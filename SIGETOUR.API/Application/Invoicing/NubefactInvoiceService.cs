using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;

namespace SIGETOUR.API.Application.Invoicing
{
    public class NubefactInvoiceService : IElectronicReceiptService
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;
        private readonly HttpClient _http;
        private readonly ILogger<NubefactInvoiceService> _logger;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _receiptLocks = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _creationLocks = new();

        public NubefactInvoiceService(
            AppDbContext db,
            IConfiguration config,
            HttpClient http,
            ILogger<NubefactInvoiceService> logger)
        {
            _db = db;
            _config = config;
            _http = http;
            _logger = logger;
        }

        private (string? ruta, string? token) ResolveEnvironmentCredentials(string environment, string currentMode)
        {
            if (string.Equals(environment, currentMode, StringComparison.OrdinalIgnoreCase))
            {
                return (_config["Nubefact:Ruta"]?.Trim(), _config["Nubefact:Token"]?.Trim());
            }

            var envRuta = _config[$"Nubefact:{environment}:Ruta"]?.Trim();
            var envToken = _config[$"Nubefact:{environment}:Token"]?.Trim();
            if (!string.IsNullOrWhiteSpace(envRuta) && !string.IsNullOrWhiteSpace(envToken))
            {
                return (envRuta, envToken);
            }

            return (null, null);
        }

        public static bool IsSunatSuccessResponseCode(JsonElement prop)
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var code))
                return code == 0;
            if (prop.ValueKind == JsonValueKind.String)
            {
                var str = prop.GetString()?.Trim();
                return str == "0" || str == "0000";
            }
            return false;
        }

        public enum InFlightClassification
        {
            ActiveLeaseAnotherProcess,
            ActiveLeaseCurrentProcess,
            ReliablyUnsentExpired,
            UncertainOrSent
        }

        public static InFlightClassification ClassifyInFlightState(ElectronicReceipt receipt, string? currentClaimToken)
        {
            var nowUtc = DateTime.UtcNow;

            // 1. Si cuenta con campos estructurados de lease:
            if (!string.IsNullOrWhiteSpace(receipt.EmissionLeaseToken))
            {
                bool leaseActive = receipt.EmissionLeaseExpiresAtUtc.HasValue && receipt.EmissionLeaseExpiresAtUtc.Value > nowUtc;
                if (leaseActive)
                {
                    if (!string.IsNullOrWhiteSpace(currentClaimToken) && string.Equals(receipt.EmissionLeaseToken, currentClaimToken, StringComparison.Ordinal))
                    {
                        return InFlightClassification.ActiveLeaseCurrentProcess;
                    }
                    return InFlightClassification.ActiveLeaseAnotherProcess;
                }

                if (receipt.EmissionStartedAtUtc.HasValue)
                {
                    return InFlightClassification.UncertainOrSent;
                }

                return InFlightClassification.ReliablyUnsentExpired;
            }

            // 2. Si NO cuenta con campos estructurados (documentos históricos previos a migración):
            if (string.IsNullOrWhiteSpace(receipt.LastError))
            {
                return InFlightClassification.UncertainOrSent;
            }

            var parts = receipt.LastError.Split(':');
            if (parts.Length >= 3)
            {
                var prefix = parts[0];
                var token = parts[1];
                var dateStr = string.Join(":", parts.Skip(2));

                if (!DateTime.TryParse(dateStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var stampUtc))
                {
                    return InFlightClassification.UncertainOrSent;
                }

                var age = nowUtc - stampUtc;
                if (age < TimeSpan.Zero)
                {
                    return InFlightClassification.UncertainOrSent;
                }

                if (prefix == "SENT_AWAITING_RESPONSE")
                {
                    return InFlightClassification.UncertainOrSent;
                }

                if (prefix == "CLAIMED")
                {
                    bool isActive = age < TimeSpan.FromSeconds(60);
                    if (isActive)
                    {
                        if (!string.IsNullOrWhiteSpace(currentClaimToken) && string.Equals(token, currentClaimToken, StringComparison.Ordinal))
                        {
                            return InFlightClassification.ActiveLeaseCurrentProcess;
                        }
                        return InFlightClassification.ActiveLeaseAnotherProcess;
                    }

                    return InFlightClassification.ReliablyUnsentExpired;
                }
            }

            return InFlightClassification.UncertainOrSent;
        }

        public async Task<(bool Acquired, string LeaseToken, ElectronicReceipt Receipt)> TryAcquireEmissionAsync(
            Guid receiptId,
            string? requestedClaimToken = null,
            bool allowRejectedRetry = false,
            CancellationToken ct = default)
        {
            var nowUtc = DateTime.UtcNow;
            var leaseExpiresAtUtc = nowUtc.AddSeconds(45);
            var token = !string.IsNullOrWhiteSpace(requestedClaimToken) ? requestedClaimToken : Guid.NewGuid().ToString("N");

            if (_db.Database.IsRelational())
            {
                var rowsAffected = await _db.ElectronicReceipts
                    .Where(r => r.Id == receiptId &&
                        (r.Status == "Pending" ||
                         (!string.IsNullOrWhiteSpace(requestedClaimToken) && r.Status == "InFlight" && r.EmissionLeaseToken == requestedClaimToken && r.EmissionLeaseExpiresAtUtc != null && r.EmissionLeaseExpiresAtUtc >= nowUtc) ||
                         (r.Status == "InFlight" && r.EmissionStartedAtUtc == null && r.EmissionLeaseExpiresAtUtc != null && r.EmissionLeaseExpiresAtUtc < nowUtc) ||
                         (allowRejectedRetry && (r.Status == "Rejected" || r.Status == "Failed") && (r.Number == null || r.Number == ""))))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, "InFlight")
                        .SetProperty(r => r.EmissionLeaseToken, token)
                        .SetProperty(r => r.EmissionLeaseAcquiredAtUtc, nowUtc)
                        .SetProperty(r => r.EmissionLeaseExpiresAtUtc, leaseExpiresAtUtc)
                        .SetProperty(r => r.EmissionStartedAtUtc, (DateTime?)null)
                        .SetProperty(r => r.LastError, $"CLAIMED:{token}:{nowUtc:O}"), ct);

                var receipt = await _db.ElectronicReceipts
                    .Include(r => r.Booking)
                        .ThenInclude(b => b.Items)
                            .ThenInclude(i => i.TourPackage)
                    .FirstOrDefaultAsync(r => r.Id == receiptId, ct);

                if (receipt == null)
                    throw new InvalidOperationException($"El comprobante {receiptId} no existe.");

                if (rowsAffected == 1)
                {
                    await _db.Entry(receipt).ReloadAsync(ct);
                    return (true, token, receipt);
                }

                return (false, string.Empty, receipt);
            }
            else
            {
                var semaphore = _receiptLocks.GetOrAdd(receiptId, _ => new SemaphoreSlim(1, 1));
                await semaphore.WaitAsync(ct);
                try
                {
                    var receipt = await _db.ElectronicReceipts
                        .Include(r => r.Booking)
                            .ThenInclude(b => b.Items)
                                .ThenInclude(i => i.TourPackage)
                        .FirstOrDefaultAsync(r => r.Id == receiptId, ct);

                    if (receipt == null)
                        throw new InvalidOperationException($"El comprobante {receiptId} no existe.");

                    bool eligible = receipt.Status == "Pending" ||
                        (!string.IsNullOrWhiteSpace(requestedClaimToken) && receipt.Status == "InFlight" && receipt.EmissionLeaseToken == requestedClaimToken && receipt.EmissionLeaseExpiresAtUtc != null && receipt.EmissionLeaseExpiresAtUtc >= nowUtc) ||
                        (receipt.Status == "InFlight" && receipt.EmissionStartedAtUtc == null && receipt.EmissionLeaseExpiresAtUtc != null && receipt.EmissionLeaseExpiresAtUtc < nowUtc) ||
                        (allowRejectedRetry && (receipt.Status == "Rejected" || receipt.Status == "Failed") && string.IsNullOrWhiteSpace(receipt.Number));

                    if (eligible)
                    {
                        receipt.Status = "InFlight";
                        receipt.EmissionLeaseToken = token;
                        receipt.EmissionLeaseAcquiredAtUtc = nowUtc;
                        receipt.EmissionLeaseExpiresAtUtc = leaseExpiresAtUtc;
                        receipt.EmissionStartedAtUtc = null;
                        receipt.LastError = $"CLAIMED:{token}:{nowUtc:O}";
                        await _db.SaveChangesAsync(ct);
                        return (true, token, receipt);
                    }

                    return (false, string.Empty, receipt);
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }

        public async Task<bool> TryMarkEmissionStartedAsync(Guid receiptId, string leaseToken, CancellationToken ct = default)
        {
            var nowUtc = DateTime.UtcNow;

            if (_db.Database.IsRelational())
            {
                var rowsAffected = await _db.ElectronicReceipts
                    .Where(r => r.Id == receiptId &&
                                r.Status == "InFlight" &&
                                r.EmissionLeaseToken == leaseToken &&
                                r.EmissionStartedAtUtc == null &&
                                (r.EmissionLeaseExpiresAtUtc == null || r.EmissionLeaseExpiresAtUtc > nowUtc))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.EmissionStartedAtUtc, nowUtc)
                        .SetProperty(r => r.LastError, $"SENT_AWAITING_RESPONSE:{leaseToken}:{nowUtc:O}"), ct);

                return rowsAffected == 1;
            }
            else
            {
                var semaphore = _receiptLocks.GetOrAdd(receiptId, _ => new SemaphoreSlim(1, 1));
                await semaphore.WaitAsync(ct);
                try
                {
                    var receipt = await _db.ElectronicReceipts.FirstOrDefaultAsync(r => r.Id == receiptId, ct);
                    if (receipt != null &&
                        receipt.Status == "InFlight" &&
                        receipt.EmissionLeaseToken == leaseToken &&
                        receipt.EmissionStartedAtUtc == null &&
                        (receipt.EmissionLeaseExpiresAtUtc == null || receipt.EmissionLeaseExpiresAtUtc > nowUtc))
                    {
                        receipt.EmissionStartedAtUtc = nowUtc;
                        receipt.LastError = $"SENT_AWAITING_RESPONSE:{leaseToken}:{nowUtc:O}";
                        await _db.SaveChangesAsync(ct);
                        return true;
                    }

                    return false;
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }

        public async Task<bool> TryRecoverUnsentInFlightAsync(Guid receiptId, string? observedLeaseToken, CancellationToken ct = default)
        {
            var nowUtc = DateTime.UtcNow;
            var recoveryMsg = "Intención de emisión recuperada tras reinicio del sistema (nunca enviada al proveedor).";

            if (_db.Database.IsRelational())
            {
                var rowsAffected = await _db.ElectronicReceipts
                    .Where(r => r.Id == receiptId &&
                                r.Status == "InFlight" &&
                                r.EmissionStartedAtUtc == null &&
                                (r.EmissionLeaseExpiresAtUtc == null || r.EmissionLeaseExpiresAtUtc < nowUtc) &&
                                (observedLeaseToken == null ? r.EmissionLeaseToken == null : r.EmissionLeaseToken == observedLeaseToken))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, "Pending")
                        .SetProperty(r => r.EmissionLeaseToken, (string?)null)
                        .SetProperty(r => r.EmissionLeaseAcquiredAtUtc, (DateTime?)null)
                        .SetProperty(r => r.EmissionLeaseExpiresAtUtc, (DateTime?)null)
                        .SetProperty(r => r.EmissionStartedAtUtc, (DateTime?)null)
                        .SetProperty(r => r.LastError, recoveryMsg), ct);

                return rowsAffected == 1;
            }
            else
            {
                var semaphore = _receiptLocks.GetOrAdd(receiptId, _ => new SemaphoreSlim(1, 1));
                await semaphore.WaitAsync(ct);
                try
                {
                    var receipt = await _db.ElectronicReceipts.FirstOrDefaultAsync(r => r.Id == receiptId, ct);
                    if (receipt != null &&
                        receipt.Status == "InFlight" &&
                        receipt.EmissionStartedAtUtc == null &&
                        (receipt.EmissionLeaseExpiresAtUtc == null || receipt.EmissionLeaseExpiresAtUtc < nowUtc) &&
                        (observedLeaseToken == null ? receipt.EmissionLeaseToken == null : receipt.EmissionLeaseToken == observedLeaseToken))
                    {
                        receipt.Status = "Pending";
                        receipt.EmissionLeaseToken = null;
                        receipt.EmissionLeaseAcquiredAtUtc = null;
                        receipt.EmissionLeaseExpiresAtUtc = null;
                        receipt.EmissionStartedAtUtc = null;
                        receipt.LastError = recoveryMsg;
                        await _db.SaveChangesAsync(ct);
                        return true;
                    }
                    return false;
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }

        public async Task<bool> TryTransitionInFlightToReviewAsync(Guid receiptId, string? observedLeaseToken, string reason, CancellationToken ct = default)
        {
            if (_db.Database.IsRelational())
            {
                var rowsAffected = await _db.ElectronicReceipts
                    .Where(r => r.Id == receiptId &&
                                r.Status == "InFlight" &&
                                (observedLeaseToken == null ? r.EmissionLeaseToken == null : r.EmissionLeaseToken == observedLeaseToken))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, "RequiresReview")
                        .SetProperty(r => r.LastError, reason), ct);

                return rowsAffected == 1;
            }
            else
            {
                var semaphore = _receiptLocks.GetOrAdd(receiptId, _ => new SemaphoreSlim(1, 1));
                await semaphore.WaitAsync(ct);
                try
                {
                    var receipt = await _db.ElectronicReceipts.FirstOrDefaultAsync(r => r.Id == receiptId, ct);
                    if (receipt != null &&
                        receipt.Status == "InFlight" &&
                        (observedLeaseToken == null ? receipt.EmissionLeaseToken == null : receipt.EmissionLeaseToken == observedLeaseToken))
                    {
                        receipt.Status = "RequiresReview";
                        receipt.LastError = reason;
                        await _db.SaveChangesAsync(ct);
                        return true;
                    }
                    return false;
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }

        public async Task<bool> TryCompleteEmissionAsync(
            Guid receiptId,
            string leaseToken,
            string finalStatus,
            string? number,
            string? pdfUrl,
            string? xmlUrl,
            string? cdrUrl,
            string? qrData,
            string? digitalHash,
            string? providerDescription,
            string? providerResponseCode,
            string? lastError,
            DateTime? issuedAtUtc,
            CancellationToken ct = default)
        {
            if (_db.Database.IsRelational())
            {
                var rowsAffected = await _db.ElectronicReceipts
                    .Where(r => r.Id == receiptId &&
                                r.Status == "InFlight" &&
                                r.EmissionLeaseToken == leaseToken &&
                                r.EmissionStartedAtUtc != null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, finalStatus)
                        .SetProperty(r => r.Number, r => number ?? r.Number)
                        .SetProperty(r => r.PdfUrl, r => pdfUrl ?? r.PdfUrl)
                        .SetProperty(r => r.XmlUrl, r => xmlUrl ?? r.XmlUrl)
                        .SetProperty(r => r.CdrUrl, r => cdrUrl ?? r.CdrUrl)
                        .SetProperty(r => r.QrData, r => qrData ?? r.QrData)
                        .SetProperty(r => r.DigitalHash, r => digitalHash ?? r.DigitalHash)
                        .SetProperty(r => r.ProviderDescription, r => providerDescription ?? r.ProviderDescription)
                        .SetProperty(r => r.ProviderResponseCode, r => providerResponseCode ?? r.ProviderResponseCode)
                        .SetProperty(r => r.LastError, lastError)
                        .SetProperty(r => r.IssuedAtUtc, r => issuedAtUtc ?? r.IssuedAtUtc), ct);

                if (rowsAffected == 1)
                {
                    return true;
                }

                // rowsAffected == 0: El lease expiró, fue reclamado o completado por otro proceso
                var current = await _db.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == receiptId, ct);
                if (current == null) return false;

                // 1. Si el comprobante ya está Aceptado, Demo o Simulado en base de datos, NUNCA degradarlo ni sobreescribir con error
                if (current.Status is "Accepted" or "Demo" or "Simulation")
                {
                    _logger.LogInformation("Comprobante {ReceiptId} ya se encuentra confirmado como {Status}. Se descarta actualización tardía.", receiptId, current.Status);
                    return false;
                }

                // 2. Si la respuesta tardía fue un éxito tributario (Accepted o Demo) y el registro en DB quedó en RequiresReview o InFlight:
                if (finalStatus is "Accepted" or "Demo")
                {
                    _logger.LogInformation("Comprobante {ReceiptId} recibió confirmación tardía de emisión ({FinalStatus}). Rescatando comprobante desde estado {CurrentStatus}.", receiptId, finalStatus, current.Status);

                    var updated = await _db.ElectronicReceipts
                        .Where(r => r.Id == receiptId && (r.Status == "RequiresReview" || r.Status == "InFlight"))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(r => r.Status, finalStatus)
                            .SetProperty(r => r.Number, r => number ?? r.Number)
                            .SetProperty(r => r.PdfUrl, r => pdfUrl ?? r.PdfUrl)
                            .SetProperty(r => r.XmlUrl, r => xmlUrl ?? r.XmlUrl)
                            .SetProperty(r => r.CdrUrl, r => cdrUrl ?? r.CdrUrl)
                            .SetProperty(r => r.QrData, r => qrData ?? r.QrData)
                            .SetProperty(r => r.DigitalHash, r => digitalHash ?? r.DigitalHash)
                            .SetProperty(r => r.ProviderDescription, r => providerDescription ?? r.ProviderDescription)
                            .SetProperty(r => r.ProviderResponseCode, r => providerResponseCode ?? r.ProviderResponseCode)
                            .SetProperty(r => r.LastError, (string?)null)
                            .SetProperty(r => r.IssuedAtUtc, r => issuedAtUtc ?? r.IssuedAtUtc), ct);
                    return updated > 0;
                }

                // 3. Si la respuesta tardía fue un error (Rejected o RequiresReview) pero rowsAffected fue 0:
                // Descartar el error tardío para no sobreescribir al nuevo dueño
                _logger.LogWarning("Respuesta tardía con error ({FinalStatus}) descartada para comprobante {ReceiptId} porque el lease {LeaseToken} ya no era el dueño activo.", finalStatus, receiptId, leaseToken);
                return false;
            }
            else
            {
                var semaphore = _receiptLocks.GetOrAdd(receiptId, _ => new SemaphoreSlim(1, 1));
                await semaphore.WaitAsync(ct);
                try
                {
                    var receipt = await _db.ElectronicReceipts.FirstOrDefaultAsync(r => r.Id == receiptId, ct);
                    if (receipt == null) return false;

                    if (receipt.Status == "InFlight" &&
                        receipt.EmissionLeaseToken == leaseToken &&
                        receipt.EmissionStartedAtUtc != null)
                    {
                        receipt.Status = finalStatus;
                        receipt.Number = number ?? receipt.Number;
                        receipt.PdfUrl = pdfUrl ?? receipt.PdfUrl;
                        receipt.XmlUrl = xmlUrl ?? receipt.XmlUrl;
                        receipt.CdrUrl = cdrUrl ?? receipt.CdrUrl;
                        receipt.QrData = qrData ?? receipt.QrData;
                        receipt.DigitalHash = digitalHash ?? receipt.DigitalHash;
                        receipt.ProviderDescription = providerDescription ?? receipt.ProviderDescription;
                        receipt.ProviderResponseCode = providerResponseCode ?? receipt.ProviderResponseCode;
                        receipt.LastError = lastError;
                        receipt.IssuedAtUtc = issuedAtUtc ?? receipt.IssuedAtUtc;
                        await _db.SaveChangesAsync(ct);
                        return true;
                    }

                    // Si ya está confirmado, nunca degradar
                    if (receipt.Status is "Accepted" or "Demo" or "Simulation")
                    {
                        _logger.LogInformation("Comprobante {ReceiptId} ya se encuentra confirmado como {Status}. Se descarta actualización tardía.", receiptId, receipt.Status);
                        return false;
                    }

                    // Si la respuesta tardía es confirmación exitosa, rescatar desde RequiresReview o InFlight
                    if ((receipt.Status == "RequiresReview" || receipt.Status == "InFlight") && (finalStatus is "Accepted" or "Demo"))
                    {
                        receipt.Status = finalStatus;
                        receipt.Number = number ?? receipt.Number;
                        receipt.PdfUrl = pdfUrl ?? receipt.PdfUrl;
                        receipt.XmlUrl = xmlUrl ?? receipt.XmlUrl;
                        receipt.CdrUrl = cdrUrl ?? receipt.CdrUrl;
                        receipt.QrData = qrData ?? receipt.QrData;
                        receipt.DigitalHash = digitalHash ?? receipt.DigitalHash;
                        receipt.ProviderDescription = providerDescription ?? receipt.ProviderDescription;
                        receipt.ProviderResponseCode = providerResponseCode ?? receipt.ProviderResponseCode;
                        receipt.LastError = null;
                        receipt.IssuedAtUtc = issuedAtUtc ?? receipt.IssuedAtUtc;
                        await _db.SaveChangesAsync(ct);
                        return true;
                    }

                    // Descartar error tardío
                    _logger.LogWarning("Respuesta tardía con error ({FinalStatus}) descartada para comprobante {ReceiptId} porque el lease {LeaseToken} ya no era el dueño activo.", finalStatus, receiptId, leaseToken);
                    return false;
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }

        public static bool IsActiveClaim(string? lastError, out DateTime claimedAt)
        {
            claimedAt = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(lastError)) return false;

            var parts = lastError.Split(':');
            if (parts.Length >= 3 && (parts[0] == "CLAIMED" || parts[0] == "SENT_AWAITING_RESPONSE"))
            {
                var dateStr = string.Join(":", parts.Skip(2));
                if (DateTime.TryParse(dateStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out claimedAt))
                {
                    var age = DateTime.UtcNow - claimedAt;
                    return age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(60);
                }
            }
            return false;
        }

        public static string? ExtractClaimToken(string? lastError)
        {
            if (string.IsNullOrWhiteSpace(lastError)) return null;
            var parts = lastError.Split(':');
            if (parts.Length >= 3 && (parts[0] == "CLAIMED" || parts[0] == "SENT_AWAITING_RESPONSE"))
            {
                return parts[1];
            }
            return null;
        }

        public async Task<ElectronicReceipt?> GetReceiptByIdAsync(Guid receiptId, CancellationToken ct = default)
        {
            return await _db.ElectronicReceipts
                .Include(r => r.Booking)
                .ThenInclude(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .FirstOrDefaultAsync(r => r.Id == receiptId, ct);
        }

        public async Task<ElectronicReceipt?> GetReceiptByBookingIdAsync(Guid bookingId, CancellationToken ct = default)
        {
            return await _db.ElectronicReceipts
                .AsNoTracking()
                .Include(r => r.Booking)
                .ThenInclude(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .Where(r => r.BookingId == bookingId)
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
        }

        public Task<ElectronicReceipt> GenerateReceiptAsync(Guid bookingId, Guid? culqiPaymentId = null, CancellationToken ct = default)
        {
            return GenerateReceiptAsync(bookingId, culqiPaymentId, exactReceiptId: null, ct);
        }

        public Task<ElectronicReceipt> GenerateReceiptAsync(Guid bookingId, Guid? culqiPaymentId, Guid? exactReceiptId, CancellationToken ct = default)
        {
            return GenerateReceiptAsync(bookingId, culqiPaymentId, exactReceiptId, claimToken: null, ct);
        }

        public Task<ElectronicReceipt> GenerateReceiptAsync(Guid bookingId, Guid? culqiPaymentId, Guid? exactReceiptId, string? claimToken, CancellationToken ct = default)
        {
            return GenerateReceiptAsync(bookingId, culqiPaymentId, exactReceiptId, claimToken, cashTransactionId: null, billableOperationKey: null, ct);
        }

        private static bool IsPostgresUniqueViolation(DbUpdateException ex, out string? constraintName)
        {
            constraintName = null;

            for (Exception? curr = ex; curr != null; curr = curr.InnerException)
            {
                if (curr is Npgsql.PostgresException pgEx)
                {
                    if (pgEx.SqlState == "23505")
                    {
                        constraintName = pgEx.ConstraintName;
                        return true;
                    }
                    return false;
                }
            }

            var msg = ex.InnerException?.Message ?? ex.Message;
            if (msg.Contains("23503", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("foreign key", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("violates foreign key constraint", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (msg.Contains("23505", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                if (msg.Contains("IX_ElectronicReceipts_BillableOperationKey", StringComparison.OrdinalIgnoreCase))
                    constraintName = "IX_ElectronicReceipts_BillableOperationKey";
                else if (msg.Contains("IX_ElectronicReceipts_CashTransactionId", StringComparison.OrdinalIgnoreCase))
                    constraintName = "IX_ElectronicReceipts_CashTransactionId";
                else if (msg.Contains("IX_ElectronicReceipts_CulqiPaymentId", StringComparison.OrdinalIgnoreCase))
                    constraintName = "IX_ElectronicReceipts_CulqiPaymentId";
                else
                    constraintName = "UNIQUE";
                return true;
            }

            return false;
        }

        private async Task<ElectronicReceipt?> FindExistingReceiptAsync(
            Guid bookingId,
            BillableOperationIdentity identity,
            bool terminalOnly,
            CancellationToken ct)
        {
            IQueryable<ElectronicReceipt> query = _db.ElectronicReceipts
                .OrderByDescending(r => r.CreatedAtUtc);

            if (identity.IsHistoricalFallback)
            {
                query = query.Where(r => r.BookingId == bookingId && r.CulqiPaymentId == null && r.CashTransactionId == null);
            }
            else
            {
                var opKey = identity.OperationKey;
                var cashId = identity.CashTransactionId;
                var culqiId = identity.CulqiPaymentId;

                query = query.Where(r =>
                    (!string.IsNullOrEmpty(opKey) && r.BillableOperationKey == opKey) ||
                    (cashId != null && r.CashTransactionId == cashId) ||
                    (culqiId != null && r.CulqiPaymentId == culqiId));
            }

            if (terminalOnly)
            {
                query = query.Where(r => r.Status == "Accepted" || r.Status == "Demo" || r.Status == "Simulation");
            }

            return await query.FirstOrDefaultAsync(ct);
        }

        public async Task<BillableOperationIdentity> ResolveBillableOperationAsync(
            Guid bookingId,
            Guid? culqiPaymentId = null,
            Guid? exactReceiptId = null,
            Guid? cashTransactionId = null,
            string? billableOperationKey = null,
            CancellationToken ct = default)
        {
            var booking = await _db.Bookings
                .Include(b => b.Items)
                .FirstOrDefaultAsync(b => b.Id == bookingId, ct);

            if (booking == null)
            {
                throw new InvalidOperationException($"No se encontró la reserva {bookingId} para facturación electrónica.");
            }

            if (exactReceiptId.HasValue && exactReceiptId.Value != Guid.Empty)
            {
                var receipt = await _db.ElectronicReceipts.FirstOrDefaultAsync(r => r.Id == exactReceiptId.Value, ct);
                if (receipt == null)
                {
                    throw new InvalidOperationException($"No se encontró el comprobante {exactReceiptId.Value} solicitado.");
                }
                if (receipt.BookingId != bookingId)
                {
                    throw new InvalidOperationException($"El comprobante solicitado pertenece a la reserva {receipt.BookingId}, no a {bookingId}.");
                }
                if (culqiPaymentId.HasValue && receipt.CulqiPaymentId.HasValue && receipt.CulqiPaymentId.Value != culqiPaymentId.Value)
                {
                    throw new InvalidOperationException("El comprobante solicitado pertenece a un pago Culqi diferente.");
                }
                if (cashTransactionId.HasValue && receipt.CashTransactionId.HasValue && receipt.CashTransactionId.Value != cashTransactionId.Value)
                {
                    throw new InvalidOperationException("El comprobante solicitado pertenece a una transacción de caja diferente.");
                }

                string key;
                bool isHistFallback = false;
                if (!string.IsNullOrWhiteSpace(receipt.BillableOperationKey))
                {
                    key = receipt.BillableOperationKey;
                }
                else if (receipt.CulqiPaymentId.HasValue)
                {
                    key = $"CULQI:{receipt.CulqiPaymentId.Value:N}";
                }
                else if (receipt.CashTransactionId.HasValue)
                {
                    key = $"CASH:{receipt.CashTransactionId.Value:N}";
                }
                else
                {
                    key = $"MANUAL:BOOKING:{bookingId:N}";
                    isHistFallback = true;
                }

                return new BillableOperationIdentity(
                    OperationKey: key,
                    CulqiPaymentId: receipt.CulqiPaymentId,
                    CashTransactionId: receipt.CashTransactionId,
                    Amount: receipt.TotalAmount > 0 ? receipt.TotalAmount : booking.PaidAmount,
                    PaymentMethod: booking.PaymentMethod,
                    OperationNumber: booking.OperationNumber,
                    IsHistoricalFallback: isHistFallback);
            }

            if (cashTransactionId.HasValue && cashTransactionId.Value != Guid.Empty)
            {
                var cashTx = await _db.CashTransactions.FirstOrDefaultAsync(t => t.Id == cashTransactionId.Value, ct);
                if (cashTx == null)
                {
                    throw new InvalidOperationException($"No se encontró la transacción de caja {cashTransactionId.Value}.");
                }
                if (cashTx.BookingId != bookingId)
                {
                    throw new InvalidOperationException($"La transacción de caja {cashTransactionId.Value} pertenece a la reserva {cashTx.BookingId}, no a {bookingId}.");
                }
                if (cashTx.TransactionType != "Ingreso")
                {
                    throw new InvalidOperationException($"La transacción de caja {cashTransactionId.Value} es de tipo '{cashTx.TransactionType}' y no puede ser facturada.");
                }
                if (cashTx.Amount <= 0)
                {
                    throw new InvalidOperationException($"La transacción de caja {cashTransactionId.Value} tiene un monto inválido ({cashTx.Amount}).");
                }

                if (cashTx.CulqiPaymentId.HasValue)
                {
                    if (culqiPaymentId.HasValue && culqiPaymentId.Value != cashTx.CulqiPaymentId.Value)
                    {
                        throw new InvalidOperationException("Conflicto de identidad: la transacción de caja pertenece a un pago Culqi distinto al solicitado.");
                    }

                    var culqiId = cashTx.CulqiPaymentId.Value;
                    var culqiPayment = await _db.CulqiPayments.FirstOrDefaultAsync(p => p.Id == culqiId, ct);
                    if (culqiPayment == null || culqiPayment.BookingId != bookingId || culqiPayment.Status != "paid")
                    {
                        throw new InvalidOperationException($"El pago Culqi {culqiId} asociado a la transacción de caja no existe, no pertenece a la reserva o no se encuentra confirmado.");
                    }

                    return new BillableOperationIdentity(
                        OperationKey: $"CULQI:{culqiId:N}",
                        CulqiPaymentId: culqiId,
                        CashTransactionId: cashTx.Id,
                        Amount: cashTx.Amount,
                        PaymentMethod: cashTx.PaymentMethod ?? booking.PaymentMethod,
                        OperationNumber: cashTx.ReferenceCode ?? culqiPayment.ProviderId ?? booking.OperationNumber,
                        IsHistoricalFallback: false);
                }
                else
                {
                    if (culqiPaymentId.HasValue)
                    {
                        throw new InvalidOperationException("Conflicto de identidad: se especificó un pago Culqi junto con una transacción manual de caja.");
                    }

                    var expectedKey = $"CASH:{cashTx.Id:N}";
                    if (!string.IsNullOrWhiteSpace(billableOperationKey) && billableOperationKey != expectedKey)
                    {
                        throw new InvalidOperationException($"Conflicto de clave de operación: '{billableOperationKey}' no coincide con '{expectedKey}'.");
                    }

                    return new BillableOperationIdentity(
                        OperationKey: expectedKey,
                        CulqiPaymentId: null,
                        CashTransactionId: cashTx.Id,
                        Amount: cashTx.Amount,
                        PaymentMethod: cashTx.PaymentMethod ?? booking.PaymentMethod,
                        OperationNumber: cashTx.ReferenceCode ?? booking.OperationNumber,
                        IsHistoricalFallback: false);
                }
            }

            if (culqiPaymentId.HasValue && culqiPaymentId.Value != Guid.Empty)
            {
                var culqi = await _db.CulqiPayments.FirstOrDefaultAsync(p => p.Id == culqiPaymentId.Value, ct);
                if (culqi == null || culqi.BookingId != bookingId || culqi.Status != "paid")
                {
                    throw new InvalidOperationException($"El pago Culqi {culqiPaymentId.Value} no existe, no pertenece a la reserva o no se encuentra confirmado.");
                }

                var linkedCashTx = await _db.CashTransactions
                    .FirstOrDefaultAsync(t => t.CulqiPaymentId == culqi.Id && t.BookingId == bookingId, ct);

                var expectedCulqiKey = $"CULQI:{culqi.Id:N}";
                if (!string.IsNullOrWhiteSpace(billableOperationKey) && billableOperationKey != expectedCulqiKey)
                {
                    throw new InvalidOperationException($"Conflicto de clave de operación: '{billableOperationKey}' no coincide con '{expectedCulqiKey}'.");
                }

                return new BillableOperationIdentity(
                    OperationKey: expectedCulqiKey,
                    CulqiPaymentId: culqi.Id,
                    CashTransactionId: linkedCashTx?.Id,
                    Amount: culqi.AmountCents / 100m,
                    PaymentMethod: booking.PaymentMethod,
                    OperationNumber: culqi.ProviderId ?? booking.OperationNumber,
                    IsHistoricalFallback: false);
            }

            // Llamada sin identificadores específicos: resolución automática
            var confirmedCulqiList = await _db.CulqiPayments
                .Where(p => p.BookingId == bookingId && p.Status == "paid")
                .OrderBy(p => p.CreatedAtUtc)
                .ToListAsync(ct);

            if (confirmedCulqiList.Count == 1)
            {
                var culqi = confirmedCulqiList[0];
                var linkedCash = await _db.CashTransactions
                    .FirstOrDefaultAsync(t => t.CulqiPaymentId == culqi.Id && t.BookingId == bookingId, ct);

                return new BillableOperationIdentity(
                    OperationKey: $"CULQI:{culqi.Id:N}",
                    CulqiPaymentId: culqi.Id,
                    CashTransactionId: linkedCash?.Id,
                    Amount: culqi.AmountCents / 100m,
                    PaymentMethod: booking.PaymentMethod,
                    OperationNumber: culqi.ProviderId ?? booking.OperationNumber,
                    IsHistoricalFallback: false);
            }
            else if (confirmedCulqiList.Count > 1)
            {
                throw new InvalidOperationException($"La reserva {booking.ReferenceCode} cuenta con múltiples pagos Culqi confirmados. Especifique el identificador de pago a facturar.");
            }

            var manualCashList = await _db.CashTransactions
                .Where(t => t.BookingId == bookingId && t.TransactionType == "Ingreso" && t.CulqiPaymentId == null)
                .OrderBy(t => t.CreatedAt)
                .ToListAsync(ct);

            if (manualCashList.Count == 1)
            {
                var cashTx = manualCashList[0];
                return new BillableOperationIdentity(
                    OperationKey: $"CASH:{cashTx.Id:N}",
                    CulqiPaymentId: null,
                    CashTransactionId: cashTx.Id,
                    Amount: cashTx.Amount,
                    PaymentMethod: cashTx.PaymentMethod ?? booking.PaymentMethod,
                    OperationNumber: cashTx.ReferenceCode ?? booking.OperationNumber,
                    IsHistoricalFallback: false);
            }
            else if (manualCashList.Count > 1)
            {
                var unbilledManual = manualCashList
                    .Where(t => !_db.ElectronicReceipts.Any(r => r.CashTransactionId == t.Id || r.BillableOperationKey == $"CASH:{t.Id:N}"))
                    .ToList();

                if (unbilledManual.Count == 1)
                {
                    var cashTx = unbilledManual[0];
                    return new BillableOperationIdentity(
                        OperationKey: $"CASH:{cashTx.Id:N}",
                        CulqiPaymentId: null,
                        CashTransactionId: cashTx.Id,
                        Amount: cashTx.Amount,
                        PaymentMethod: cashTx.PaymentMethod ?? booking.PaymentMethod,
                        OperationNumber: cashTx.ReferenceCode ?? booking.OperationNumber,
                        IsHistoricalFallback: false);
                }

                throw new InvalidOperationException($"La reserva {booking.ReferenceCode} tiene múltiples movimientos de caja. Especifique el identificador del movimiento a facturar.");
            }

            var historicalReceipts = await _db.ElectronicReceipts
                .Where(r => r.BookingId == bookingId && r.CulqiPaymentId == null && r.CashTransactionId == null)
                .OrderByDescending(r => r.CreatedAtUtc)
                .ToListAsync(ct);

            if (historicalReceipts.Count == 1)
            {
                var hist = historicalReceipts[0];
                return new BillableOperationIdentity(
                    OperationKey: !string.IsNullOrWhiteSpace(hist.BillableOperationKey) ? hist.BillableOperationKey : $"MANUAL:BOOKING:{bookingId:N}",
                    CulqiPaymentId: null,
                    CashTransactionId: null,
                    Amount: hist.TotalAmount > 0 ? hist.TotalAmount : booking.PaidAmount,
                    PaymentMethod: booking.PaymentMethod,
                    OperationNumber: booking.OperationNumber,
                    IsHistoricalFallback: true);
            }
            else if (historicalReceipts.Count > 1)
            {
                throw new InvalidOperationException($"La reserva {booking.ReferenceCode} cuenta con múltiples comprobantes históricos sin asociar. Se requiere conciliación explícita.");
            }

            if (booking.PaidAmount <= 0)
            {
                throw new InvalidOperationException($"La reserva {booking.ReferenceCode} no cuenta con ningún pago confirmado para emitir comprobante.");
            }

            return new BillableOperationIdentity(
                OperationKey: $"MANUAL:BOOKING:{bookingId:N}",
                CulqiPaymentId: null,
                CashTransactionId: null,
                Amount: booking.PaidAmount,
                PaymentMethod: booking.PaymentMethod,
                OperationNumber: booking.OperationNumber,
                IsHistoricalFallback: false);
        }

        public async Task<ElectronicReceipt> GenerateReceiptAsync(
            Guid bookingId,
            Guid? culqiPaymentId,
            Guid? exactReceiptId,
            string? claimToken,
            Guid? cashTransactionId,
            string? billableOperationKey = null,
            CancellationToken ct = default)
        {
            var mode = _config["Nubefact:Mode"]?.Trim() ?? "Demo";
            ElectronicReceipt? receipt = null;
            bool isNew = false;

            var identity = await ResolveBillableOperationAsync(
                bookingId,
                culqiPaymentId,
                exactReceiptId,
                cashTransactionId,
                billableOperationKey,
                ct);

            if (exactReceiptId.HasValue && exactReceiptId.Value != Guid.Empty)
            {
                receipt = await _db.ElectronicReceipts.FirstOrDefaultAsync(r => r.Id == exactReceiptId.Value, ct);
                if (receipt == null)
                {
                    throw new InvalidOperationException($"No se encontró el comprobante {exactReceiptId.Value} solicitado.");
                }
                if (receipt.Status is "Accepted" or "Demo" or "Simulation")
                {
                    return receipt;
                }
                isNew = false;
            }
            else
            {
                // 1. Prevención estricta de duplicados / idempotencia (Reutilizar si ya fue Aceptado, Demo o Simulado)
                var existing = await FindExistingReceiptAsync(bookingId, identity, terminalOnly: true, ct);
                if (existing != null) return existing;

                // Reutilizar registro si quedó en estado fallido/rechazado/pendiente para actualizarlo en vez de duplicarlo
                receipt = await FindExistingReceiptAsync(bookingId, identity, terminalOnly: false, ct);
                isNew = receipt == null;
            }

            var booking = await _db.Bookings
                .Include(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .FirstOrDefaultAsync(b => b.Id == bookingId, ct);

            if (booking == null)
                throw new InvalidOperationException($"No se encontró la reserva {bookingId} para facturación electrónica.");

            // 2. Determinación de importes, anticipos y saldos
            decimal paymentAmount = identity.Amount;

            var isAdvance = (booking.PaidAmount < booking.TotalAmount && paymentAmount < booking.TotalAmount);
            var isBalance = (booking.PaidAmount >= booking.TotalAmount && paymentAmount < booking.TotalAmount)
                || (booking.PaidAmount >= booking.TotalAmount && (booking.PaidAmount - paymentAmount > 0));

            // Preservar ambiente histórico si ya fue establecido en el comprobante
            var effectiveEnvironment = !string.IsNullOrWhiteSpace(receipt?.Environment)
                ? receipt.Environment
                : mode;

            // Búsqueda de comprobante previo para regularización de anticipo conforme al manual NubeFact (mismo ambiente, excluye Simulación)
            ElectronicReceipt? prevAdvanceReceipt = null;
            if (isBalance)
            {
                prevAdvanceReceipt = await _db.ElectronicReceipts
                    .OrderByDescending(r => r.CreatedAtUtc)
                    .FirstOrDefaultAsync(r => r.BookingId == bookingId && r.Id != (receipt != null ? receipt.Id : Guid.Empty)
                        && (r.Status == "Accepted" || r.Status == "Demo")
                        && r.Environment == effectiveEnvironment
                        && r.Environment != "Simulation", ct);
            }
            bool isAdvanceRegularization = isBalance && prevAdvanceReceipt != null;

            // 3. Configuración tributaria (Gravada vs Exonerada)
            var operationType = _config["Nubefact:OperationType"]?.Trim().ToLowerInvariant() ?? "gravada";
            var isExonerado = operationType == "exonerada";
            var defaultIgvRate = decimal.TryParse(_config["Nubefact:DefaultIgvRate"], NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedRate)
                ? parsedRate
                : 0.18m;
            var igvRate = isExonerado ? 0.00m : defaultIgvRate;

            decimal subtotal;
            decimal igv;
            decimal total = paymentAmount;

            if (isExonerado || igvRate <= 0)
            {
                subtotal = total;
                igv = 0.00m;
            }
            else
            {
                subtotal = Math.Round(total / (1m + igvRate), 2);
                igv = total - subtotal;
            }

            // 4. Tipo de Comprobante y Documento del Cliente
            var isFactura = string.Equals(booking.InvoiceType, "factura", StringComparison.OrdinalIgnoreCase);
            var receiptType = isFactura ? "Factura" : "Boleta";
            var defaultSeries = isFactura
                ? (_config["Nubefact:FacturaSeries"] ?? "F001")
                : (_config["Nubefact:BoletaSeries"] ?? "B001");

            var dni = (booking.CustomerDni ?? "").Trim();
            string docType;
            if (isFactura)
            {
                docType = "6"; // RUC
            }
            else if (dni.Length == 8 && dni.All(char.IsDigit))
            {
                docType = "1"; // DNI
            }
            else if (dni.Length == 11 && dni.All(char.IsDigit))
            {
                docType = "6"; // RUC
            }
            else if (dni.Length is >= 9 and <= 12)
            {
                docType = "4"; // Carnet de Extranjería / Pasaporte
            }
            else
            {
                docType = "0"; // Doc Trib No Domic / Sin Documento
            }

            if (receipt == null)
            {
                receipt = new ElectronicReceipt
                {
                    BookingId = booking.Id,
                    CulqiPaymentId = identity.CulqiPaymentId,
                    CashTransactionId = identity.CashTransactionId,
                    BillableOperationKey = identity.OperationKey,
                    Environment = effectiveEnvironment,
                    CreatedAtUtc = DateTime.UtcNow
                };
            }
            else
            {
                if (receipt.CashTransactionId == null && identity.CashTransactionId != null)
                    receipt.CashTransactionId = identity.CashTransactionId;
                if (receipt.CulqiPaymentId == null && identity.CulqiPaymentId != null)
                    receipt.CulqiPaymentId = identity.CulqiPaymentId;
                if (string.IsNullOrWhiteSpace(receipt.BillableOperationKey) && identity.OperationKey != null)
                    receipt.BillableOperationKey = identity.OperationKey;
            }

            receipt.ReceiptType = receiptType;
            if (string.IsNullOrWhiteSpace(receipt.Series))
            {
                receipt.Series = defaultSeries;
            }
            // No generar correlativos aleatorios; NubeFact asigna el número oficial correlativo
            receipt.CustomerDocType = docType;
            receipt.CustomerDocNumber = !string.IsNullOrWhiteSpace(dni) ? dni : "00000000";
            receipt.CustomerName = !string.IsNullOrWhiteSpace(booking.CustomerName) ? booking.CustomerName : "Cliente General";
            receipt.CustomerEmail = booking.CustomerEmail ?? string.Empty;
            receipt.SubtotalAmount = subtotal;
            receipt.IgvAmount = igv;
            receipt.TotalAmount = total;
            if (string.IsNullOrWhiteSpace(receipt.Environment))
            {
                receipt.Environment = effectiveEnvironment;
            }

            // 5. Separación estricta de Modos: Mock, Demo y Production
            var currentConfigMode = _config["Nubefact:Mode"]?.Trim() ?? "Demo";
            mode = effectiveEnvironment;
            var isMock = string.Equals(mode, "Mock", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "Simulation", StringComparison.OrdinalIgnoreCase);
            var isCurrentMock = string.Equals(currentConfigMode, "Mock", StringComparison.OrdinalIgnoreCase) || string.Equals(currentConfigMode, "Simulation", StringComparison.OrdinalIgnoreCase);

            // Validar incompatibilidad entre simulación y ambientes externos
            if (!isNew && !string.IsNullOrWhiteSpace(receipt.Environment))
            {
                if ((isMock && !isCurrentMock) || (!isMock && isCurrentMock))
                {
                    _logger.LogWarning("Incompatibilidad de ambiente para comprobante {ReceiptId}: documento histórico en '{DocEnv}', pero configuración actual en '{CurrentMode}'. Se bloquea la operación para evitar mezclar simulación con externo.", receipt.Id, receipt.Environment, currentConfigMode);
                    throw new InvalidOperationException($"Incompatibilidad de ambiente para comprobante {receipt.Id}: el documento pertenece a '{receipt.Environment}', mientras que el sistema está en '{currentConfigMode}'. No se permite alternar entre simulación y ambientes externos.");
                }
            }

            var (ruta, token) = ResolveEnvironmentCredentials(mode, currentConfigMode);

            // No pasar silenciosamente a Mock cuando falten credenciales en Demo o Production
            if (!isMock && (string.IsNullOrWhiteSpace(ruta) || string.IsNullOrWhiteSpace(token)))
            {
                _logger.LogWarning("Incompatibilidad de ambiente para modo '{Mode}' frente a configuración actual '{CurrentMode}'. Se bloquea la emisión externa.", mode, currentConfigMode);
                throw new InvalidOperationException($"Incompatibilidad de ambiente para el modo '{mode}': el sistema está configurado en '{currentConfigMode}' y no dispone de credenciales aisladas para '{mode}'. Se bloquea la operación para evitar mezclar ambientes.");
            }

            // Modo Mock explícito (solo cuando mode == 'Mock')
            if (isMock)
            {
                _logger.LogInformation("Nubefact Modo Simulación (Mock) explícito activo para reserva {RefCode}. Registrando comprobante interno.", booking.ReferenceCode);
                receipt.Environment = "Simulation";
                receipt.Status = "Simulation";
                receipt.ProviderResponseCode = "0";
                receipt.ProviderDescription = "Comprobante generado en modo Simulación/Mock (sin impacto SUNAT)";
                if (string.IsNullOrWhiteSpace(receipt.Number))
                {
                    var lastNum = await _db.ElectronicReceipts
                        .Where(r => r.Series == receipt.Series && r.Number != null && r.Number.Length <= 8)
                        .Select(r => r.Number)
                        .ToListAsync(ct);
                    int maxCorrelativo = lastNum.Select(n => int.TryParse(n, out var val) ? val : 0).DefaultIfEmpty(0).Max();
                    receipt.Number = (maxCorrelativo + 1).ToString();
                }
                receipt.PdfUrl = $"/Booking/Ticket/{receipt.Id}";
                receipt.IssuedAtUtc = DateTime.UtcNow;

                var cpeType = isFactura ? "01" : "03";
                var cleanDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
                receipt.DigitalHash = Convert.ToBase64String(Guid.NewGuid().ToByteArray())[..16];
                var emisorRuc = _config["Company:Ruc"] ?? "20601234567";
                receipt.QrData = $"{emisorRuc}|{cpeType}|{receipt.Series}|{receipt.Number}|{receipt.IgvAmount:F2}|{receipt.TotalAmount:F2}|{cleanDate}|{receipt.CustomerDocType}|{receipt.CustomerDocNumber}|{receipt.DigitalHash}|";

                if (isNew)
                {
                    var lockKey = identity.OperationKey ?? $"MANUAL:BOOKING:{bookingId:N}";
                    var creationSemaphore = _creationLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
                    await creationSemaphore.WaitAsync(ct);
                    try
                    {
                        var doubleCheck = await FindExistingReceiptAsync(bookingId, identity, terminalOnly: false, ct);
                        if (doubleCheck != null)
                        {
                            return doubleCheck;
                        }
                        _db.ElectronicReceipts.Add(receipt);
                        await _db.SaveChangesAsync(ct);
                        isNew = false;
                    }
                    catch (DbUpdateException ex)
                    {
                        if (!IsPostgresUniqueViolation(ex, out var constraintName))
                        {
                            throw;
                        }
                        _logger.LogInformation(ex, "Colisión de unicidad ({Constraint}) al insertar ElectronicReceipt para {OpKey}. Desasociando entidad y recargando ganador.", constraintName, identity.OperationKey);
                        _db.Entry(receipt).State = EntityState.Detached;
                        var winner = await FindExistingReceiptAsync(bookingId, identity, terminalOnly: false, ct);
                        if (winner != null) return winner;
                        throw;
                    }
                    finally
                    {
                        creationSemaphore.Release();
                    }
                }
                else
                {
                    await _db.SaveChangesAsync(ct);
                }
                booking.InvoiceNumber = receipt.FullNumber;
                try { await _db.SaveChangesAsync(ct); } catch {}
                return receipt;
            }

            // 6. Conciliación previa y protección contra reemisiones a ciegas
            if (isNew)
            {
                var lockKey = identity.OperationKey ?? $"MANUAL:BOOKING:{bookingId:N}";
                var creationSemaphore = _creationLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
                await creationSemaphore.WaitAsync(ct);
                try
                {
                    var doubleCheck = await FindExistingReceiptAsync(bookingId, identity, terminalOnly: false, ct);

                    if (doubleCheck != null)
                    {
                        receipt = doubleCheck;
                        isNew = false;
                    }
                    else
                    {
                        receipt.Status = "Pending";
                        _db.ElectronicReceipts.Add(receipt);
                        try
                        {
                            await _db.SaveChangesAsync(ct);
                            isNew = false;
                        }
                        catch (DbUpdateException ex)
                        {
                            if (!IsPostgresUniqueViolation(ex, out var constraintName))
                            {
                                throw;
                            }
                            _logger.LogInformation(ex, "Colisión concurrente ({Constraint}) al insertar ElectronicReceipt para {OpKey}. Desasociando entidad y recargando ganador.", constraintName, identity.OperationKey);
                            _db.Entry(receipt).State = EntityState.Detached;

                            receipt = await FindExistingReceiptAsync(bookingId, identity, terminalOnly: false, ct);

                            if (receipt == null) throw;
                            isNew = false;
                        }
                    }
                }
                finally
                {
                    creationSemaphore.Release();
                }
            }
            else
            {
                if (_db.Database.IsRelational())
                {
                    try { await _db.Entry(receipt).ReloadAsync(ct); } catch { }
                }
            }

            if (receipt.Status is "Accepted" or "Demo" or "Simulation")
            {
                return receipt;
            }

            // Protección InFlight:
            if (receipt.Status == "InFlight")
            {
                var classification = ClassifyInFlightState(receipt, claimToken);
                if (classification == InFlightClassification.ActiveLeaseAnotherProcess)
                {
                    _logger.LogWarning("Comprobante {ReceiptId} ya cuenta con una emisión activa en vuelo en otro proceso o hilo. Se bloquea la llamada para evitar emisiones simultáneas.", receipt.Id);
                    return receipt;
                }

                if (classification == InFlightClassification.UncertainOrSent)
                {
                    if (!string.IsNullOrWhiteSpace(receipt.Number))
                    {
                        var rec = await ReconcileReceiptWithProviderAsync(receipt.Id, ct);
                        return rec.Receipt;
                    }

                    _logger.LogWarning("Comprobante {ReceiptId} en InFlight sin evidencia concluyente de no haber sido enviado al proveedor. Se retiene en RequiresReview de forma condicional.", receipt.Id);
                    var reviewReason = "Comprobante en vuelo histórico sin evidencia concluyente de no haber sido enviado al proveedor. Se retiene en revisión manual para no duplicar comprobantes en SUNAT.";
                    await TryTransitionInFlightToReviewAsync(receipt.Id, receipt.EmissionLeaseToken, reviewReason, ct);
                    if (_db.Database.IsRelational())
                    {
                        try { await _db.Entry(receipt).ReloadAsync(ct); } catch { }
                    }
                    else
                    {
                        receipt.Status = "RequiresReview";
                        receipt.LastError = reviewReason;
                    }
                    return receipt;
                }

                if (classification == InFlightClassification.ReliablyUnsentExpired)
                {
                    await TryRecoverUnsentInFlightAsync(receipt.Id, receipt.EmissionLeaseToken, ct);
                    if (_db.Database.IsRelational())
                    {
                        try { await _db.Entry(receipt).ReloadAsync(ct); } catch { }
                    }
                    else
                    {
                        receipt.Status = "Pending";
                        receipt.EmissionLeaseToken = null;
                        receipt.EmissionStartedAtUtc = null;
                    }
                }
            }

            if (receipt.Status is "RequiresReview" or "Submitted")
            {
                if (!string.IsNullOrWhiteSpace(receipt.Number))
                {
                    var reconciliation = await ReconcileReceiptWithProviderAsync(receipt.Id, ct);
                    return reconciliation.Receipt;
                }

                _logger.LogWarning("Comprobante {ReceiptId} en estado '{Status}' sin número correlativo asignado. No se reemite a ciegas para evitar duplicación tributaria en SUNAT; requiere verificación en el portal NubeFact.", receipt.Id, receipt.Status);
                return receipt;
            }

            // 6.5. Adquisición atómica del derecho de emisión (Requirement 1.A)
            string acquiredToken;
            if (receipt.Status == "InFlight" && !string.IsNullOrWhiteSpace(claimToken) &&
                ClassifyInFlightState(receipt, claimToken) == InFlightClassification.ActiveLeaseCurrentProcess)
            {
                acquiredToken = claimToken;
            }
            else
            {
                bool allowRetry = (receipt.Status is "Rejected" or "Failed") && string.IsNullOrWhiteSpace(receipt.Number);
                var (acquired, acqToken, refreshedReceipt) = await TryAcquireEmissionAsync(receipt.Id, claimToken, allowRejectedRetry: allowRetry, ct);
                if (!acquired)
                {
                    _logger.LogWarning("Adquisición atómica de emisión denegada para comprobante {ReceiptId}. Otro proceso o hilo obtuvo el derecho de emisión.", receipt.Id);
                    return refreshedReceipt;
                }
                receipt = refreshedReceipt;
                acquiredToken = acqToken;
            }

            // 7. Construcción de conceptos y desglose por cada paquete o pasajero en la reserva
            var ratio = booking.TotalAmount > 0 ? (total / booking.TotalAmount) : 1m;
            var itemConcept = isAdvance ? " (Anticipo)" : (isBalance ? " (Saldo Final)" : " (Pago Total)");

            var itemsList = new List<object>();
            var bookingItems = booking.Items.ToList();
            if (bookingItems.Count == 0)
            {
                // Fallback para reservas sin items detallados
                itemsList.Add(new
                {
                    unidad_de_medida = "ZZ",
                    codigo = "TOUR",
                    descripcion = $"Servicio Turístico: Paquete General{itemConcept}",
                    cantidad = 1,
                    valor_unitario = subtotal.ToString("F2", CultureInfo.InvariantCulture),
                    precio_unitario = total.ToString("F2", CultureInfo.InvariantCulture),
                    descuento = (string?)null,
                    subtotal = subtotal.ToString("F2", CultureInfo.InvariantCulture),
                    tipo_de_igv = isExonerado ? 8 : 1,
                    igv = igv.ToString("F2", CultureInfo.InvariantCulture),
                    total = total.ToString("F2", CultureInfo.InvariantCulture),
                    anticipo_regularizacion = isAdvanceRegularization,
                    anticipo_documento_serie = isAdvanceRegularization ? prevAdvanceReceipt!.Series : (string?)null,
                    anticipo_documento_numero = isAdvanceRegularization ? (int.TryParse(prevAdvanceReceipt!.Number, out var pn) ? (object)pn : prevAdvanceReceipt!.Number) : (object?)null
                });
            }
            else
            {
                decimal runningSubtotal = 0m;
                decimal runningIgv = 0m;
                decimal runningTotal = 0m;

                for (int i = 0; i < bookingItems.Count; i++)
                {
                    var itm = bookingItems[i];
                    var isLast = (i == bookingItems.Count - 1);

                    decimal itmTotal;
                    decimal itmSubtotal;
                    decimal itmIgv;

                    if (isLast)
                    {
                        itmTotal = total - runningTotal;
                        itmSubtotal = subtotal - runningSubtotal;
                        itmIgv = igv - runningIgv;
                    }
                    else
                    {
                        itmTotal = Math.Round(itm.SubTotal * ratio, 2);
                        itmSubtotal = isExonerado || igvRate <= 0 ? itmTotal : Math.Round(itmTotal / (1m + igvRate), 2);
                        itmIgv = itmTotal - itmSubtotal;

                        runningTotal += itmTotal;
                        runningSubtotal += itmSubtotal;
                        runningIgv += itmIgv;
                    }

                    var pkgTitle = itm.TourPackage?.Title ?? "Paquete Turístico";
                    var pkgCode = (itm.TourPackageId.ToString() ?? "TOUR")[..Math.Min(6, itm.TourPackageId.ToString()?.Length ?? 4)];
                    var qty = Math.Max(1, itm.Passengers);
                    var valUnit = Math.Round(itmSubtotal / qty, 2);
                    var priceUnit = Math.Round(itmTotal / qty, 2);

                    itemsList.Add(new
                    {
                        unidad_de_medida = "ZZ",
                        codigo = pkgCode,
                        descripcion = $"Servicio Turístico: {pkgTitle} ({qty} Pax){itemConcept}",
                        cantidad = qty,
                        valor_unitario = valUnit.ToString("F2", CultureInfo.InvariantCulture),
                        precio_unitario = priceUnit.ToString("F2", CultureInfo.InvariantCulture),
                        descuento = (string?)null,
                        subtotal = itmSubtotal.ToString("F2", CultureInfo.InvariantCulture),
                        tipo_de_igv = isExonerado ? 8 : 1,
                        igv = itmIgv.ToString("F2", CultureInfo.InvariantCulture),
                        total = itmTotal.ToString("F2", CultureInfo.InvariantCulture),
                        anticipo_regularizacion = isAdvanceRegularization,
                        anticipo_documento_serie = isAdvanceRegularization ? prevAdvanceReceipt!.Series : (string?)null,
                        anticipo_documento_numero = isAdvanceRegularization ? (int.TryParse(prevAdvanceReceipt!.Number, out var pn) ? (object)pn : prevAdvanceReceipt!.Number) : (object?)null
                    });
                }
            }

            var sendToSunat = string.Equals(mode, "Production", StringComparison.OrdinalIgnoreCase)
                && string.Equals(_config["Nubefact:SendToSunat"], "true", StringComparison.OrdinalIgnoreCase);

            var pdfFormat = _config["Nubefact:PdfFormat"] ?? "TICKET";

            // Fecha de emisión en zona horaria oficial de Perú (SUNAT exige hora local UTC-5, no fecha UTC)
            DateTime peruTime;
            try
            {
                var peruZone = TimeZoneInfo.FindSystemTimeZoneById("SA Pacific Standard Time");
                peruTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, peruZone);
            }
            catch
            {
                peruTime = DateTime.UtcNow.AddHours(-5);
            }
            var fechaEmision = peruTime.ToString("dd-MM-yyyy");

            // 7.5. Generar instantánea inmutable (Snapshot) de emisor, cliente, ítems, desglose y pago
            var snapshotItems = new List<object>();
            foreach (var itm in bookingItems)
            {
                var pkgTitle = itm.TourPackage?.Title ?? "Paquete Turístico";
                var qty = Math.Max(1, itm.Passengers);
                var itmTotal = Math.Round(itm.SubTotal * ratio, 2);
                var itmSubtotal = isExonerado || igvRate <= 0 ? itmTotal : Math.Round(itmTotal / (1m + igvRate), 2);
                var itmIgv = itmTotal - itmSubtotal;
                var effectivePriceUnit = qty > 0 ? Math.Round(itmTotal / qty, 2) : itmTotal;

                snapshotItems.Add(new
                {
                    TourTitle = pkgTitle,
                    Quantity = qty,
                    Unit = "NIU",
                    UnitPrice = effectivePriceUnit,
                    FullPackageUnitPrice = itm.UnitPrice > 0 ? itm.UnitPrice : effectivePriceUnit,
                    Subtotal = itmSubtotal,
                    Igv = itmIgv,
                    Total = itmTotal
                });
            }

            var snapshot = new
            {
                Emisor = new
                {
                    Ruc = _config["Company:Ruc"] ?? "20601234567",
                    RazonSocial = _config["Company:BusinessName"] ?? "TURISMO CHILÓN TRANSPORTE & TURISMO S.A.C.",
                    Direccion = _config["Company:Address"] ?? "Jr. 2 de Mayo 569, Cajamarca, Perú",
                    Telefono = "+51 901 195 392"
                },
                Cliente = new
                {
                    TipoDoc = receipt.CustomerDocType,
                    NumeroDoc = receipt.CustomerDocNumber,
                    Nombre = receipt.CustomerName,
                    Email = receipt.CustomerEmail
                },
                Documento = new
                {
                    Tipo = receipt.ReceiptType,
                    Serie = receipt.Series,
                    Numero = receipt.Number,
                    FechaEmision = fechaEmision,
                    Moneda = "PEN",
                    CondicionPago = "Contado",
                    MedioPago = !string.IsNullOrWhiteSpace(booking.PaymentMethod) ? booking.PaymentMethod : "Culqi",
                    OperacionNumero = booking.OperationNumber,
                    Modalidad = isAdvance ? "Adelanto / Seña" : (isBalance ? "Saldo Final" : "Total 100%"),
                    Ambiente = effectiveEnvironment
                },
                Items = snapshotItems,
                Totales = new
                {
                    Subtotal = subtotal,
                    Igv = igv,
                    Total = total,
                    TotalGravada = isExonerado ? 0m : subtotal,
                    TotalExonerada = isExonerado ? total : 0m,
                    TotalAnticipos = isAdvanceRegularization ? prevAdvanceReceipt!.TotalAmount : 0m
                }
            };

            if (string.IsNullOrWhiteSpace(receipt.Environment))
            {
                receipt.Environment = effectiveEnvironment;
            }
            receipt.SnapshotJson = JsonSerializer.Serialize(snapshot);
            await _db.SaveChangesAsync(ct);

            var payload = new Dictionary<string, object?>
            {
                ["operacion"] = "generar_comprobante",
                ["tipo_de_comprobante"] = isFactura ? 1 : 2,
                ["serie"] = receipt.Series,
                ["numero"] = null, // Auto-correlativo controlado exclusivamente por NubeFact
                ["sunat_transaction"] = 1, // 1 = Venta Interna
                ["cliente_tipo_de_documento"] = int.TryParse(receipt.CustomerDocType, out var dt) ? dt : 1,
                ["cliente_numero_de_documento"] = receipt.CustomerDocNumber,
                ["cliente_denominacion"] = receipt.CustomerName,
                ["cliente_direccion"] = "Cajamarca, Perú",
                ["cliente_email"] = receipt.CustomerEmail,
                ["fecha_de_emision"] = fechaEmision,
                ["moneda"] = 1, // Soles
                ["porcentaje_de_igv"] = isExonerado ? 0 : 18.00,
                ["total_gravada"] = isExonerado ? null : subtotal.ToString("F2", CultureInfo.InvariantCulture),
                ["total_exonerada"] = isExonerado ? total.ToString("F2", CultureInfo.InvariantCulture) : null,
                ["total_inafecta"] = null,
                ["total_gratuita"] = null,
                ["total_descuento"] = null,
                ["total_anticipos"] = isAdvanceRegularization ? prevAdvanceReceipt!.TotalAmount.ToString("F2", CultureInfo.InvariantCulture) : "0.00",
                ["total_igv"] = igv.ToString("F2", CultureInfo.InvariantCulture),
                ["total"] = total.ToString("F2", CultureInfo.InvariantCulture),
                ["enviar_automaticamente_a_la_sunat"] = sendToSunat,
                ["enviar_automaticamente_al_cliente"] = false,
                ["enviar_a_sunat"] = sendToSunat,
                ["enviar_al_cliente"] = false,
                ["codigo_unico"] = $"SIG-{receipt.Id:N}",
                ["condiciones_de_pago"] = "Contado",
                ["medio_de_pago"] = !string.IsNullOrWhiteSpace(booking.PaymentMethod) ? booking.PaymentMethod.ToLowerInvariant() : "tarjeta_de_credito",
                ["formato_de_pdf"] = pdfFormat,
                ["items"] = itemsList
            };

            // 8. Invocación a API REST de NubeFact y comprobación tributaria estricta
            using var requestMsg = new HttpRequestMessage(HttpMethod.Post, ruta);
            requestMsg.Headers.TryAddWithoutValidation("Authorization", $"Token token=\"{token}\"");
            requestMsg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                // Persistir inicio de envío de forma condicional y atómica inmediatamente antes de invocar HTTP
                bool canSend = await TryMarkEmissionStartedAsync(receipt.Id, acquiredToken, ct);
                if (!canSend)
                {
                    _logger.LogWarning("Permiso de envío revocado o expirado para comprobante {ReceiptId} con token {Token}. Abortando llamada HTTP externa.", receipt.Id, acquiredToken);
                    if (_db.Database.IsRelational())
                    {
                        try { await _db.Entry(receipt).ReloadAsync(ct); } catch { }
                    }
                    return receipt;
                }

                string finalStatus;
                string? providerDescription = null;
                string? providerResponseCode = null;
                string? lastError = null;
                string? number = receipt.Number;
                string? pdfUrl = receipt.PdfUrl;
                string? xmlUrl = receipt.XmlUrl;
                string? cdrUrl = receipt.CdrUrl;
                string? qrData = receipt.QrData;
                string? digitalHash = receipt.DigitalHash;
                DateTime? issuedAtUtc = null;

                try
                {
                    var response = await _http.SendAsync(requestMsg, ct);
                    var jsonStr = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(jsonStr);
                    var root = doc.RootElement;

                    if (response.IsSuccessStatusCode)
                    {
                        // No interpretar automáticamente HTTP 200 como aceptación tributaria
                        if (root.TryGetProperty("errors", out var errProp) && !string.IsNullOrWhiteSpace(errProp.GetString()))
                        {
                            finalStatus = "Rejected";
                            lastError = errProp.GetString();
                            providerDescription = lastError;
                            _logger.LogWarning("NubeFact devolvió errores en HTTP 200 para reserva {RefCode}: {Errors}", booking.ReferenceCode, lastError);
                        }
                        else
                        {
                            // Comprobar explícitamente aceptación tributaria SUNAT
                            bool isSunatAccepted = false;
                            if (root.TryGetProperty("aceptada_por_sunat", out var asp) && asp.ValueKind == JsonValueKind.True)
                            {
                                isSunatAccepted = true;
                            }
                            else if (root.TryGetProperty("sunat_responsecode", out var src) && IsSunatSuccessResponseCode(src))
                            {
                                isSunatAccepted = true;
                            }

                            var sunatDesc = root.TryGetProperty("sunat_description", out var descProp) ? descProp.GetString() : null;
                            providerDescription = sunatDesc;

                            if (isSunatAccepted)
                            {
                                finalStatus = "Accepted";
                                providerDescription ??= "Aceptado por SUNAT";
                            }
                            else if (string.Equals(effectiveEnvironment, "Demo", StringComparison.OrdinalIgnoreCase))
                            {
                                // En modo DEMO, la disponibilidad de un PDF no equivale a aceptación tributaria SUNAT
                                finalStatus = "Demo";
                                providerDescription ??= "Documento generado en DEMO NubeFact (sin aceptación tributaria SUNAT)";
                            }
                            else if (!sendToSunat)
                            {
                                finalStatus = "Submitted";
                                providerDescription ??= "Generado internamente (Envío a SUNAT pendiente)";
                            }
                            else
                            {
                                finalStatus = "Submitted";
                                providerDescription ??= "Enviado a SUNAT (pendiente de respuesta)";
                            }

                            if (root.TryGetProperty("numero", out var numProp))
                            {
                                number = numProp.ValueKind == JsonValueKind.Number
                                    ? numProp.GetInt64().ToString()
                                    : numProp.GetString() ?? number;
                            }

                            pdfUrl = root.TryGetProperty("enlace_del_pdf", out var pdfProp) ? pdfProp.GetString() : pdfUrl;
                            xmlUrl = root.TryGetProperty("enlace_del_xml", out var xmlProp) ? xmlProp.GetString() : xmlUrl;
                            cdrUrl = root.TryGetProperty("enlace_del_cdr", out var cdrProp) ? cdrProp.GetString() : cdrUrl;
                            digitalHash = root.TryGetProperty("codigo_hash", out var hashProp) ? hashProp.GetString() : digitalHash;

                            if (root.TryGetProperty("cadena_para_codigo_qr", out var qrProp) && !string.IsNullOrWhiteSpace(qrProp.GetString()))
                            {
                                qrData = qrProp.GetString();
                            }
                            else
                            {
                                qrData = null; // No fabricar QR ficticio
                            }

                            issuedAtUtc = DateTime.UtcNow;
                            lastError = null;
                        }
                    }
                    else
                    {
                        // HTTP != 200: diferenciar error de cliente vs error temporal de servidor
                        var httpCode = (int)response.StatusCode;
                        if (httpCode >= 500)
                        {
                            finalStatus = "RequiresReview"; // Error temporal en gateway o servidor NubeFact tras envío: resultado incierto
                            lastError = $"Error temporal del proveedor NubeFact (HTTP {httpCode}). Solicitud enviada con resultado incierto; requiere conciliación antes de reintentar para no duplicar comprobantes.";
                        }
                        else
                        {
                            finalStatus = "Rejected"; // 4xx: Parámetros inválidos o rechazo de negocio
                            lastError = jsonStr;
                            try
                            {
                                using var errDoc = JsonDocument.Parse(jsonStr);
                                if (errDoc.RootElement.TryGetProperty("errors", out var errProp))
                                    providerDescription = errProp.GetString();
                                if (errDoc.RootElement.TryGetProperty("codigo", out var codProp))
                                    providerResponseCode = codProp.ToString();
                            }
                            catch {}
                        }
                        _logger.LogWarning("NubeFact respondió HTTP {StatusCode} para {RefCode}: {Response}", response.StatusCode, booking.ReferenceCode, jsonStr);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Excepción de red o timeout invocando API NubeFact para reserva {RefCode}", booking.ReferenceCode);
                    // Timeout o fallo de red: resultado incierto tras envío; retener en RequiresReview
                    finalStatus = "RequiresReview";
                    lastError = $"Resultado incierto tras fallo de conexión o timeout con NubeFact: {ex.Message}. La solicitud fue enviada pero el resultado se desconoce; requiere verificación manual en el portal NubeFact antes de reintentar.";
                }

                await TryCompleteEmissionAsync(
                    receipt.Id,
                    acquiredToken,
                    finalStatus,
                    number,
                    pdfUrl,
                    xmlUrl,
                    cdrUrl,
                    qrData,
                    digitalHash,
                    providerDescription,
                    providerResponseCode,
                    lastError,
                    issuedAtUtc,
                    ct);

                if (_db.Database.IsRelational())
                {
                    try { await _db.Entry(receipt).ReloadAsync(ct); } catch {}
                }
                else
                {
                    var reloaded = await _db.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == receipt.Id, ct);
                    if (reloaded != null)
                    {
                        receipt.Status = reloaded.Status;
                        receipt.Number = reloaded.Number;
                        receipt.PdfUrl = reloaded.PdfUrl;
                        receipt.XmlUrl = reloaded.XmlUrl;
                        receipt.CdrUrl = reloaded.CdrUrl;
                        receipt.QrData = reloaded.QrData;
                        receipt.DigitalHash = reloaded.DigitalHash;
                        receipt.ProviderDescription = reloaded.ProviderDescription;
                        receipt.ProviderResponseCode = reloaded.ProviderResponseCode;
                        receipt.LastError = reloaded.LastError;
                        receipt.IssuedAtUtc = reloaded.IssuedAtUtc;
                    }
                }

                if (!string.IsNullOrWhiteSpace(receipt.Number))
                {
                    booking.InvoiceNumber = receipt.FullNumber;
                    try { await _db.SaveChangesAsync(ct); } catch {}
                }

                return receipt;
        }

        public async Task<ElectronicReceipt> RetryReceiptAsync(Guid receiptId, CancellationToken ct = default)
        {
            var receipt = await _db.ElectronicReceipts
                .Include(r => r.Booking)
                    .ThenInclude(b => b.Items)
                        .ThenInclude(i => i.TourPackage)
                .FirstOrDefaultAsync(r => r.Id == receiptId, ct);

            if (receipt == null)
            {
                throw new InvalidOperationException($"El comprobante con ID {receiptId} no existe.");
            }

                if (receipt.Status is "Accepted" or "Demo" or "Simulation")
                {
                    return receipt;
                }

                // Validar compatibilidad de ambiente antes de cualquier acción
                var currentMode = _config["Nubefact:Mode"]?.Trim() ?? "Demo";
                var docEnv = !string.IsNullOrWhiteSpace(receipt.Environment) ? receipt.Environment : currentMode;
                var (envRuta, envToken) = ResolveEnvironmentCredentials(docEnv, currentMode);
                if (!string.Equals(docEnv, "Simulation", StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(envRuta) || string.IsNullOrWhiteSpace(envToken)))
                {
                    _logger.LogWarning("Incompatibilidad de ambiente para comprobante {ReceiptId}: pertenece a '{DocEnv}', pero el sistema está configurado en '{CurrentMode}'. Operación bloqueada.", receipt.Id, docEnv, currentMode);
                    throw new InvalidOperationException($"Incompatibilidad de ambiente para el modo '{docEnv}': el sistema está configurado en '{currentMode}' y no dispone de credenciales aisladas para '{docEnv}'. Se bloquea la operación para evitar mezclar ambientes.");
                }

                // Documentos con número asignado: intentar conciliación concluyente
                if (!string.IsNullOrWhiteSpace(receipt.Number))
                {
                    var reconciliation = await ReconcileReceiptWithProviderAsync(receipt.Id, ct);
                    if (reconciliation.Status == NubefactReconciliationStatus.Confirmed)
                    {
                        return reconciliation.Receipt;
                    }
                    if (reconciliation.Status == NubefactReconciliationStatus.PendingResponse)
                    {
                        // Documento ya enviado a SUNAT y pendiente de respuesta
                        return reconciliation.Receipt;
                    }
                    if (reconciliation.Status == NubefactReconciliationStatus.ConfirmedRejection)
                    {
                        // Rechazo formal por SUNAT
                        return reconciliation.Receipt;
                    }
                    if (reconciliation.Status == NubefactReconciliationStatus.Unknown)
                    {
                        // Resultado incierto: terminar sin emitir y retener en revisión manual
                        _logger.LogWarning("Reintento detenido para comprobante {ReceiptId}: consulta inconclusa con el proveedor. Se retiene en revisión manual para no duplicar comprobantes en SUNAT.", receipt.Id);
                        return reconciliation.Receipt;
                    }
                    if (reconciliation.Status == NubefactReconciliationStatus.ConfirmedNonExistent)
                    {
                        _logger.LogWarning("NubeFact confirmó inexistencia previa de {FullNumber}. Se retiene para revisión operativa.", receipt.FullNumber);
                        receipt.Status = "RequiresReview";
                        receipt.LastError = "NubeFact confirmó que el correlativo no existe en su sistema. Se retiene para revisión operativa.";
                        await _db.SaveChangesAsync(ct);
                        return receipt;
                    }
                }
                else
                {
                    // Si no tiene número y está en RequiresReview o InFlight:
                    if (receipt.Status is "RequiresReview" or "InFlight")
                    {
                        if (receipt.Status == "InFlight")
                        {
                            var classification = ClassifyInFlightState(receipt, currentClaimToken: null);
                            if (classification == InFlightClassification.ActiveLeaseAnotherProcess)
                            {
                                _logger.LogWarning("Reintento bloqueado para comprobante {ReceiptId}: emisión en vuelo activa en otro proceso.", receiptId);
                                return receipt;
                            }
                            if (classification == InFlightClassification.UncertainOrSent)
                            {
                                _logger.LogWarning("Reintento detenido para comprobante {ReceiptId} en InFlight sin evidencia de no haber sido enviado.", receiptId);
                                var reason = "Comprobante en vuelo histórico sin evidencia concluyente de no haber sido enviado al proveedor. Se retiene en revisión manual para no duplicar comprobantes en SUNAT.";
                                await TryTransitionInFlightToReviewAsync(receipt.Id, receipt.EmissionLeaseToken, reason, ct);
                                if (_db.Database.IsRelational())
                                {
                                    try { await _db.Entry(receipt).ReloadAsync(ct); } catch {}
                                }
                                else
                                {
                                    receipt.Status = "RequiresReview";
                                    receipt.LastError = reason;
                                }
                                return receipt;
                            }
                            if (classification == InFlightClassification.ReliablyUnsentExpired)
                            {
                                await TryRecoverUnsentInFlightAsync(receipt.Id, receipt.EmissionLeaseToken, ct);
                                if (_db.Database.IsRelational())
                                {
                                    try { await _db.Entry(receipt).ReloadAsync(ct); } catch {}
                                }
                                else
                                {
                                    receipt.Status = "Pending";
                                    receipt.EmissionLeaseToken = null;
                                    receipt.EmissionStartedAtUtc = null;
                                }
                            }
                        }

                        if (receipt.Status == "RequiresReview")
                        {
                            _logger.LogWarning("Comprobante {ReceiptId} en estado {Status} sin correlativo asignado. No se reemite a ciegas para evitar duplicación tributaria en SUNAT; requiere verificación en el portal NubeFact.", receiptId, receipt.Status);
                            return receipt;
                        }
                    }
                }

                // Si está en Pending, Rejected o Failed (y no en revisión/in-flight), proceder a emitir
                if (receipt.Status is "Pending" or "Rejected" or "Failed")
                {
                    return await GenerateReceiptAsync(
                        receipt.BookingId,
                        receipt.CulqiPaymentId,
                        exactReceiptId: receipt.Id,
                        claimToken: null,
                        cashTransactionId: receipt.CashTransactionId,
                        billableOperationKey: receipt.BillableOperationKey,
                        ct: ct);
                }

                return receipt;
        }

        public async Task<int> ProcessPendingReceiptsAsync(CancellationToken ct = default)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-15);
            var currentMode = _config["Nubefact:Mode"]?.Trim() ?? "Demo";

            // 1. Limpieza y conciliación de comprobantes en vuelo ('InFlight') abandonados tras reinicio del servidor
            // Se leen sin seguimiento (AsNoTracking) para evitar SaveChangesAsync desactualizados
            var inFlightList = await _db.ElectronicReceipts
                .AsNoTracking()
                .Where(r => r.Status == "InFlight")
                .ToListAsync(ct);

            foreach (var inFlight in inFlightList)
            {
                var classification = ClassifyInFlightState(inFlight, currentClaimToken: null);
                if (classification == InFlightClassification.ActiveLeaseAnotherProcess)
                {
                    // Sigue activo dentro de su ventana de lease -> no tocar
                    continue;
                }

                if (classification == InFlightClassification.ReliablyUnsentExpired)
                {
                    // Intención reservada pero comprobablemente NUNCA enviada -> recuperar a Pending condicionalmente
                    await TryRecoverUnsentInFlightAsync(inFlight.Id, inFlight.EmissionLeaseToken, ct);
                }
                else // UncertainOrSent
                {
                    // Marca ausente, texto no estructurado, fecha inválida, o envío iniciado -> REQUIRES REVIEW condicionalmente
                    var reason = "La emisión quedó interrumpida o en vuelo tras reinicio del servidor. Se requiere revisión manual antes de reintentar para no duplicar comprobantes en SUNAT.";
                    await TryTransitionInFlightToReviewAsync(inFlight.Id, inFlight.EmissionLeaseToken, reason, ct);
                }
            }

            // 2. Solo recuperar comprobantes pendientes no despachados ("Pending") o enviados con número que requieran consulta
            // No procesar automáticamente comprobantes en RequiresReview o InFlight sin número para no generar duplicados en SUNAT
            var pendingReceipts = await _db.ElectronicReceipts
                .Where(r => (r.Status == "Pending" || (r.Status == "Submitted" && !string.IsNullOrWhiteSpace(r.Number))) && r.CreatedAtUtc <= cutoff)
                .OrderBy(r => r.CreatedAtUtc)
                .Take(20)
                .ToListAsync(ct);

            int processedCount = 0;
            foreach (var receipt in pendingReceipts)
            {
                // Comprobar compatibilidad de ambiente antes de cualquier acción
                var docEnv = !string.IsNullOrWhiteSpace(receipt.Environment) ? receipt.Environment : currentMode;
                var (envRuta, envToken) = ResolveEnvironmentCredentials(docEnv, currentMode);
                if (!string.Equals(docEnv, "Simulation", StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(envRuta) || string.IsNullOrWhiteSpace(envToken)))
                {
                    _logger.LogWarning("Comprobante {ReceiptId} pertenece al ambiente '{DocEnv}', incompatible con la configuración actual '{CurrentMode}'. Se omite en recuperación automática.", receipt.Id, docEnv, currentMode);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(receipt.Number))
                {
                    var queried = await ReconcileReceiptWithProviderAsync(receipt.Id, ct);
                    if (queried.Status == NubefactReconciliationStatus.Confirmed)
                    {
                        processedCount++;
                        continue;
                    }
                    if (queried.Status is NubefactReconciliationStatus.Unknown 
                        or NubefactReconciliationStatus.PendingResponse 
                        or NubefactReconciliationStatus.ConfirmedRejection 
                        or NubefactReconciliationStatus.ConfirmedNonExistent)
                    {
                        continue;
                    }
                }

                // Unificado: utilizar TryAcquireEmissionAsync para que worker y direct emission compitan bajo la misma regla atómica
                var (claimed, workerClaimToken, refreshed) = await TryAcquireEmissionAsync(receipt.Id, requestedClaimToken: null, allowRejectedRetry: false, ct);
                if (!claimed)
                {
                    // Ya fue reclamado por la emisión inicial o por otro hilo/proceso recuperador
                    continue;
                }

                try
                {
                    await GenerateReceiptAsync(refreshed.BookingId, refreshed.CulqiPaymentId, refreshed.Id, claimToken: workerClaimToken, cashTransactionId: refreshed.CashTransactionId, billableOperationKey: refreshed.BillableOperationKey, ct: ct);
                    processedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo recuperar el comprobante pendiente {ReceiptId}", receipt.Id);
                }
            }

            return processedCount;
        }

        public async Task<ElectronicReceipt?> QueryReceiptFromProviderAsync(Guid receiptId, CancellationToken ct = default)
        {
            var res = await ReconcileReceiptWithProviderAsync(receiptId, ct);
            return res.Receipt;
        }

        public async Task<NubefactReconciliationResult> ReconcileReceiptWithProviderAsync(Guid receiptId, CancellationToken ct = default)
        {
            var receipt = await _db.ElectronicReceipts
                .Include(r => r.Booking)
                .FirstOrDefaultAsync(r => r.Id == receiptId, ct);
            if (receipt == null) 
                throw new InvalidOperationException($"El comprobante con ID {receiptId} no existe.");

            var currentMode = _config["Nubefact:Mode"]?.Trim() ?? "Demo";
            var docEnv = !string.IsNullOrWhiteSpace(receipt.Environment) ? receipt.Environment : currentMode;
            if (string.Equals(docEnv, "Simulation", StringComparison.OrdinalIgnoreCase) || string.Equals(docEnv, "Mock", StringComparison.OrdinalIgnoreCase))
            {
                return new NubefactReconciliationResult(NubefactReconciliationStatus.Confirmed, receipt, "Modo Simulación");
            }

            var (ruta, token) = ResolveEnvironmentCredentials(docEnv, currentMode);
            if (string.IsNullOrWhiteSpace(ruta) || string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("El comprobante {ReceiptId} pertenece al ambiente histórico '{DocEnv}', pero el sistema está configurado en '{CurrentMode}'. Se aborta la consulta para evitar mezclar credenciales de distintos ambientes.",
                    receiptId, docEnv, currentMode);
                return new NubefactReconciliationResult(NubefactReconciliationStatus.Unknown, receipt, "Ambiente histórico sin credenciales configuradas");
            }

            if (string.IsNullOrWhiteSpace(receipt.Number) || !int.TryParse(receipt.Number, out var numInt) || numInt <= 0)
            {
                _logger.LogWarning("No es posible consultar el comprobante {ReceiptId} porque no posee un correlativo numérico válido ({Number}).", receiptId, receipt.Number);
                return new NubefactReconciliationResult(NubefactReconciliationStatus.Unknown, receipt, "Sin correlativo válido");
            }

            try
            {
                var docEnvironment = !string.IsNullOrWhiteSpace(receipt.Environment)
                    ? receipt.Environment
                    : currentMode;

                var sendToSunat = string.Equals(docEnvironment, "Production", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_config["Nubefact:SendToSunat"], "true", StringComparison.OrdinalIgnoreCase);

                var isFactura = receipt.ReceiptType == "Factura";
                var payload = new
                {
                    operacion = "consultar_comprobante",
                    tipo_de_comprobante = isFactura ? 1 : 2,
                    serie = receipt.Series,
                    numero = numInt
                };

                using var requestMsg = new HttpRequestMessage(HttpMethod.Post, ruta);
                requestMsg.Headers.TryAddWithoutValidation("Authorization", $"Token token=\"{token}\"");
                requestMsg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(requestMsg, ct);
                var httpCode = (int)response.StatusCode;

                if (httpCode == 401 || httpCode == 403)
                {
                    var authErr = "Fallo de autenticación o credenciales inválidas al consultar NubeFact.";
                    receipt.Status = "RequiresReview";
                    receipt.LastError = authErr;
                    await _db.SaveChangesAsync(ct);
                    return new NubefactReconciliationResult(NubefactReconciliationStatus.Unknown, receipt, authErr);
                }

                if (httpCode >= 500)
                {
                    var serverErr = $"Error temporal del servidor NubeFact (HTTP {httpCode}). Solicitud de consulta con resultado incierto.";
                    receipt.Status = "RequiresReview";
                    receipt.LastError = serverErr;
                    await _db.SaveChangesAsync(ct);
                    return new NubefactReconciliationResult(NubefactReconciliationStatus.Unknown, receipt, serverErr);
                }

                var jsonStr = await response.Content.ReadAsStringAsync(ct);
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(jsonStr);
                }
                catch (JsonException)
                {
                    var parseErr = "Respuesta incompleta o no interpretable devuelta por NubeFact.";
                    receipt.Status = "RequiresReview";
                    receipt.LastError = parseErr;
                    await _db.SaveChangesAsync(ct);
                    return new NubefactReconciliationResult(NubefactReconciliationStatus.Unknown, receipt, parseErr);
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (response.IsSuccessStatusCode && !root.TryGetProperty("errors", out _))
                    {
                        bool sunatAccepted = false;
                        if (root.TryGetProperty("aceptada_por_sunat", out var aProp) && aProp.ValueKind == JsonValueKind.True)
                        {
                            sunatAccepted = true;
                        }
                        else if (root.TryGetProperty("sunat_responsecode", out var rcProp) && IsSunatSuccessResponseCode(rcProp))
                        {
                            sunatAccepted = true;
                        }

                        var sunatQueryDesc = root.TryGetProperty("sunat_description", out var qDescProp) ? qDescProp.GetString() : null;
                        var sunatRespCode = root.TryGetProperty("sunat_responsecode", out var codeProp) ? codeProp.ToString() : null;

                        // Comprobar si SUNAT lo rechazó formalmente
                        bool isExplicitRejection = !sunatAccepted &&
                            ((root.TryGetProperty("sunat_responsecode", out var rCode) && !IsSunatSuccessResponseCode(rCode) && rCode.ToString() != "0")
                             || (sunatQueryDesc != null && (sunatQueryDesc.Contains("rechazad", StringComparison.OrdinalIgnoreCase) || sunatQueryDesc.Contains("excepcion", StringComparison.OrdinalIgnoreCase))));

                        if (isExplicitRejection)
                        {
                            receipt.Status = "Rejected";
                            receipt.ProviderResponseCode = sunatRespCode;
                            receipt.ProviderDescription = sunatQueryDesc ?? "Comprobante rechazado formalmente por SUNAT";
                            receipt.LastError = sunatQueryDesc;
                            await _db.SaveChangesAsync(ct);
                            return new NubefactReconciliationResult(NubefactReconciliationStatus.ConfirmedRejection, receipt, sunatQueryDesc);
                        }

                        if (string.IsNullOrWhiteSpace(receipt.Environment))
                        {
                            receipt.Environment = docEnvironment;
                        }

                        if (sunatAccepted)
                        {
                            receipt.Status = "Accepted";
                            receipt.ProviderDescription = sunatQueryDesc ?? "Aceptado por SUNAT";
                        }
                        else if (string.Equals(receipt.Environment, "Demo", StringComparison.OrdinalIgnoreCase))
                        {
                            receipt.Status = "Demo";
                            receipt.ProviderDescription = sunatQueryDesc ?? "Documento generado en DEMO NubeFact (sin aceptación tributaria SUNAT)";
                        }
                        else if (!sendToSunat)
                        {
                            receipt.Status = "Submitted";
                            receipt.ProviderDescription = sunatQueryDesc ?? "Generado internamente (Envío a SUNAT pendiente)";
                        }
                        else
                        {
                            receipt.Status = "Submitted";
                            receipt.ProviderDescription = sunatQueryDesc ?? "Enviado a SUNAT";
                        }

                        receipt.PdfUrl = root.TryGetProperty("enlace_del_pdf", out var p) ? p.GetString() : receipt.PdfUrl;
                        receipt.XmlUrl = root.TryGetProperty("enlace_del_xml", out var x) ? x.GetString() : receipt.XmlUrl;
                        receipt.CdrUrl = root.TryGetProperty("enlace_del_cdr", out var c) ? c.GetString() : receipt.CdrUrl;
                        receipt.DigitalHash = root.TryGetProperty("codigo_hash", out var h) ? h.GetString() : receipt.DigitalHash;
                        if (root.TryGetProperty("cadena_para_codigo_qr", out var q) && !string.IsNullOrWhiteSpace(q.GetString()))
                        {
                            receipt.QrData = q.GetString();
                        }
                        if (receipt.Status is "Accepted" or "Demo")
                        {
                            receipt.LastError = null;
                        }
                        await _db.SaveChangesAsync(ct);

                        var resStatus = receipt.Status == "Submitted" ? NubefactReconciliationStatus.PendingResponse : NubefactReconciliationStatus.Confirmed;
                        return new NubefactReconciliationResult(resStatus, receipt);
                    }
                    else if (root.TryGetProperty("errors", out var errProp))
                    {
                        var errStr = errProp.GetString() ?? string.Empty;
                        bool isNonExistent = errStr.Contains("no existe", StringComparison.OrdinalIgnoreCase)
                            || errStr.Contains("no encontrado", StringComparison.OrdinalIgnoreCase);

                        if (isNonExistent)
                        {
                            _logger.LogInformation("NubeFact confirmó que el comprobante {FullNumber} no existe en su sistema: {Error}", receipt.FullNumber, errStr);
                            return new NubefactReconciliationResult(NubefactReconciliationStatus.ConfirmedNonExistent, receipt, errStr);
                        }

                        // Error devuelto por NubeFact que no acredita rechazo SUNAT ni inexistencia (resultado incierto)
                        receipt.Status = "RequiresReview";
                        receipt.LastError = $"Consulta inconclusa con NubeFact: {errStr}. Requiere verificación manual en portal.";
                        await _db.SaveChangesAsync(ct);
                        return new NubefactReconciliationResult(NubefactReconciliationStatus.Unknown, receipt, errStr);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Excepción de red o timeout al consultar comprobante {FullNumber} en NubeFact", receipt.FullNumber);
                receipt.Status = "RequiresReview";
                receipt.LastError = $"Resultado incierto tras fallo de conexión o timeout al consultar NubeFact: {ex.Message}";
                await _db.SaveChangesAsync(ct);
                return new NubefactReconciliationResult(NubefactReconciliationStatus.Unknown, receipt, ex.Message);
            }

            return new NubefactReconciliationResult(NubefactReconciliationStatus.Unknown, receipt, "Resultado desconocido");
        }
    }
}
