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

        public async Task<ElectronicReceipt> GenerateReceiptAsync(Guid bookingId, Guid? culqiPaymentId, Guid? exactReceiptId, string? claimToken, CancellationToken ct = default)
        {
            var mode = _config["Nubefact:Mode"]?.Trim() ?? "Demo";
            ElectronicReceipt? receipt = null;
            bool isNew = false;

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
                if (culqiPaymentId == null && receipt.CulqiPaymentId != null)
                {
                    culqiPaymentId = receipt.CulqiPaymentId;
                }
                isNew = false;
            }
            else
            {
                // 1. Prevención estricta de duplicados / idempotencia (Reutilizar si ya fue Aceptado, Demo o Simulado)
                var existing = await _db.ElectronicReceipts
                    .OrderByDescending(r => r.CreatedAtUtc)
                    .FirstOrDefaultAsync(r => ((culqiPaymentId != null && r.CulqiPaymentId == culqiPaymentId) || (culqiPaymentId == null && r.BookingId == bookingId && r.CulqiPaymentId == null))
                        && (r.Status == "Accepted" || r.Status == "Demo" || r.Status == "Simulation"), ct);
                if (existing != null) return existing;

                // Reutilizar registro si quedó en estado fallido/rechazado para actualizarlo en vez de duplicarlo
                receipt = await _db.ElectronicReceipts
                    .OrderByDescending(r => r.CreatedAtUtc)
                    .FirstOrDefaultAsync(r => (culqiPaymentId != null && r.CulqiPaymentId == culqiPaymentId)
                        || (culqiPaymentId == null && r.BookingId == bookingId && r.CulqiPaymentId == null), ct);
                isNew = receipt == null;
            }

            var booking = await _db.Bookings
                .Include(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .FirstOrDefaultAsync(b => b.Id == bookingId, ct);

            if (booking == null)
                throw new InvalidOperationException($"No se encontró la reserva {bookingId} para facturación electrónica.");

            CulqiPayment? culqiPayment = null;
            if (culqiPaymentId.HasValue)
            {
                culqiPayment = await _db.CulqiPayments.FindAsync(new object[] { culqiPaymentId.Value }, ct);
                if (culqiPayment == null || culqiPayment.BookingId != bookingId || culqiPayment.Status != "paid")
                {
                    throw new InvalidOperationException($"El pago Culqi {culqiPaymentId} no existe, no pertenece a la reserva o no se encuentra confirmado.");
                }
            }
            else
            {
                if (booking.PaidAmount <= 0)
                {
                    throw new InvalidOperationException($"La reserva {booking.ReferenceCode} no cuenta con ningún pago confirmado para emitir comprobante.");
                }
            }

            // 2. Determinación de importes, anticipos y saldos
            var paymentAmount = culqiPayment != null
                ? culqiPayment.AmountCents / 100m
                : (receipt != null && receipt.TotalAmount > 0 ? receipt.TotalAmount : booking.PaidAmount);

            var isAdvance = (culqiPayment != null && booking.PaidAmount < booking.TotalAmount)
                || (culqiPayment == null && paymentAmount < booking.TotalAmount);
            var isBalance = (culqiPayment != null && booking.PaidAmount >= booking.TotalAmount && (booking.PaidAmount - paymentAmount > 0))
                || (culqiPayment == null && booking.PaidAmount >= booking.TotalAmount && paymentAmount < booking.TotalAmount);

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
                    CulqiPaymentId = culqiPaymentId,
                    Environment = effectiveEnvironment,
                    CreatedAtUtc = DateTime.UtcNow
                };
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
                    _db.ElectronicReceipts.Add(receipt);
                }
                booking.InvoiceNumber = receipt.FullNumber;
                await _db.SaveChangesAsync(ct);
                return receipt;
            }

            // 6. Conciliación previa y protección contra reemisiones a ciegas
            if (!isNew)
            {
                // Re-verificar estado fresco de la base de datos
                if (_db.Database.IsRelational())
                {
                    try { await _db.Entry(receipt).ReloadAsync(ct); } catch { }
                }

                if (receipt.Status is "Accepted" or "Demo" or "Simulation")
                {
                    return receipt;
                }

                // Protección InFlight: solo el titular del claimToken activo tiene derecho a emitir
                if (receipt.Status == "InFlight")
                {
                    bool active = IsActiveClaim(receipt.LastError, out _);
                    var activeToken = ExtractClaimToken(receipt.LastError);

                    if (active && (string.IsNullOrWhiteSpace(claimToken) || !string.Equals(claimToken, activeToken, StringComparison.Ordinal)))
                    {
                        _logger.LogWarning("Comprobante {ReceiptId} ya cuenta con una emisión activa en vuelo en otro proceso o hilo. Se bloquea la llamada para evitar emisiones simultáneas.", receipt.Id);
                        return receipt;
                    }

                    if (!active)
                    {
                        // Abandonado tras envío -> resultado incierto -> RequiresReview (NO reemitir a ciegas)
                        if (receipt.LastError != null && receipt.LastError.StartsWith("SENT_AWAITING_RESPONSE", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("Comprobante {ReceiptId} en vuelo fue interrumpido después de enviar solicitud externa. Resultado incierto; pasando a RequiresReview.", receipt.Id);
                            receipt.Status = "RequiresReview";
                            receipt.LastError = "La solicitud fue enviada pero el proceso se interrumpió antes de confirmar el resultado. Requiere verificación manual en portal NubeFact.";
                            await _db.SaveChangesAsync(ct);
                            return receipt;
                        }
                    }
                }

                if (receipt.Status == "RequiresReview" && string.IsNullOrWhiteSpace(receipt.Number))
                {
                    _logger.LogWarning("Comprobante {ReceiptId} en estado 'RequiresReview' sin número correlativo asignado. No se reemite a ciegas para evitar duplicación tributaria en SUNAT; requiere verificación en el portal NubeFact.", receipt.Id);
                    return receipt;
                }

                if (!string.IsNullOrWhiteSpace(receipt.Number))
                {
                    var reconciliation = await ReconcileReceiptWithProviderAsync(receipt.Id, ct);
                    if (reconciliation.Status == NubefactReconciliationStatus.Confirmed)
                    {
                        _logger.LogInformation("Comprobante {FullNumber} recuperado y confirmado tras consulta previa con el proveedor.", receipt.FullNumber);
                        return reconciliation.Receipt;
                    }

                    // Si el resultado es incierto (Unknown), pendiente o rechazado formalmente, TERMINAR sin emitir
                    if (reconciliation.Status is NubefactReconciliationStatus.Unknown 
                        or NubefactReconciliationStatus.PendingResponse 
                        or NubefactReconciliationStatus.ConfirmedRejection 
                        or NubefactReconciliationStatus.ConfirmedNonExistent)
                    {
                        _logger.LogWarning("Operación detenida para comprobante {ReceiptId}: consulta previa inconclusa o resultado no apto para emisión ({Status}).", receipt.Id, reconciliation.Status);
                        return reconciliation.Receipt;
                    }
                }
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
            // Persistir InFlight y derecho de emisión antes de la preparación y envío
            if (string.IsNullOrWhiteSpace(claimToken))
            {
                claimToken = Guid.NewGuid().ToString("N");
            }
            receipt.Status = "InFlight";
            receipt.LastError = $"CLAIMED:{claimToken}:{DateTime.UtcNow:O}";
            if (isNew)
            {
                _db.ElectronicReceipts.Add(receipt);
                isNew = false;
            }
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
            try
            {
                using var requestMsg = new HttpRequestMessage(HttpMethod.Post, ruta);
                requestMsg.Headers.TryAddWithoutValidation("Authorization", $"Token token=\"{token}\"");
                requestMsg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                // Persistir SENT_AWAITING_RESPONSE antes de la llamada de red: si el proceso se interrumpe
                // tras el envío externo, el documento se identifica inequívocamente como enviado con resultado incierto
                receipt.LastError = $"SENT_AWAITING_RESPONSE:{claimToken}:{DateTime.UtcNow:O}";
                await _db.SaveChangesAsync(ct);

                var response = await _http.SendAsync(requestMsg, ct);
                var jsonStr = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                if (response.IsSuccessStatusCode)
                {
                    // No interpretar automáticamente HTTP 200 como aceptación tributaria
                    if (root.TryGetProperty("errors", out var errProp) && !string.IsNullOrWhiteSpace(errProp.GetString()))
                    {
                        receipt.Status = "Rejected";
                        receipt.LastError = errProp.GetString();
                        _logger.LogWarning("NubeFact devolvió errores en HTTP 200 para reserva {RefCode}: {Errors}", booking.ReferenceCode, errProp.GetString());
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
                        if (string.IsNullOrWhiteSpace(receipt.Environment))
                        {
                            receipt.Environment = effectiveEnvironment;
                        }

                        if (isSunatAccepted)
                        {
                            receipt.Status = "Accepted";
                            receipt.ProviderDescription = sunatDesc ?? "Aceptado por SUNAT";
                        }
                        else if (string.Equals(receipt.Environment, "Demo", StringComparison.OrdinalIgnoreCase))
                        {
                            // En modo DEMO, la disponibilidad de un PDF no equivale a aceptación tributaria SUNAT
                            receipt.Status = "Demo";
                            receipt.ProviderDescription = sunatDesc ?? "Documento generado en DEMO NubeFact (sin aceptación tributaria SUNAT)";
                        }
                        else if (!sendToSunat)
                        {
                            receipt.Status = "Submitted";
                            receipt.ProviderDescription = sunatDesc ?? "Generado internamente (Envío a SUNAT pendiente)";
                        }
                        else
                        {
                            receipt.Status = "Submitted";
                            receipt.ProviderDescription = sunatDesc ?? "Enviado a SUNAT (pendiente de respuesta)";
                        }

                        if (root.TryGetProperty("numero", out var numProp))
                        {
                            receipt.Number = numProp.ValueKind == JsonValueKind.Number
                                ? numProp.GetInt64().ToString()
                                : numProp.GetString() ?? receipt.Number;
                        }

                        receipt.PdfUrl = root.TryGetProperty("enlace_del_pdf", out var pdfProp) ? pdfProp.GetString() : null;
                        receipt.XmlUrl = root.TryGetProperty("enlace_del_xml", out var xmlProp) ? xmlProp.GetString() : null;
                        receipt.CdrUrl = root.TryGetProperty("enlace_del_cdr", out var cdrProp) ? cdrProp.GetString() : null;
                        receipt.DigitalHash = root.TryGetProperty("codigo_hash", out var hashProp) ? hashProp.GetString() : null;

                        if (root.TryGetProperty("cadena_para_codigo_qr", out var qrProp) && !string.IsNullOrWhiteSpace(qrProp.GetString()))
                        {
                            receipt.QrData = qrProp.GetString();
                        }
                        else
                        {
                            receipt.QrData = null; // No fabricar QR ficticio
                        }

                        receipt.IssuedAtUtc = DateTime.UtcNow;
                    }
                }
                else
                {
                    // HTTP != 200: diferenciar error de cliente vs error temporal de servidor
                    var httpCode = (int)response.StatusCode;
                    if (httpCode >= 500)
                    {
                        receipt.Status = "RequiresReview"; // Error temporal en gateway o servidor NubeFact tras envío: resultado incierto
                        receipt.LastError = $"Error temporal del proveedor NubeFact (HTTP {httpCode}). Solicitud enviada con resultado incierto; requiere conciliación antes de reintentar para no duplicar comprobantes.";
                    }
                    else
                    {
                        receipt.Status = "Rejected"; // 4xx: Parámetros inválidos o rechazo de negocio
                        receipt.LastError = jsonStr;
                        try
                        {
                            using var errDoc = JsonDocument.Parse(jsonStr);
                            if (errDoc.RootElement.TryGetProperty("errors", out var errProp))
                                receipt.ProviderDescription = errProp.GetString();
                            if (errDoc.RootElement.TryGetProperty("codigo", out var codProp))
                                receipt.ProviderResponseCode = codProp.ToString();
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
                receipt.Status = "RequiresReview";
                receipt.LastError = $"Resultado incierto tras fallo de conexión o timeout con NubeFact: {ex.Message}. La solicitud fue enviada pero el resultado se desconoce; requiere verificación manual en el portal NubeFact antes de reintentar.";
            }

            if (receipt.Status == "Accepted" || receipt.Status == "Demo" || receipt.Status == "Simulation")
            {
                receipt.LastError = null;
            }
            booking.InvoiceNumber = receipt.FullNumber;
            await _db.SaveChangesAsync(ct);
            return receipt;
        }

        public async Task<ElectronicReceipt> RetryReceiptAsync(Guid receiptId, CancellationToken ct = default)
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
                        _logger.LogWarning("Comprobante {ReceiptId} en estado {Status} sin correlativo asignado. No se reemite a ciegas para evitar duplicación tributaria en SUNAT; requiere verificación en el portal NubeFact.", receiptId, receipt.Status);
                        return receipt;
                    }
                }

                // Si está en Pending, Rejected o Failed (y no en revisión/in-flight), proceder a emitir
                if (receipt.Status is "Pending" or "Rejected" or "Failed")
                {
                    return await GenerateReceiptAsync(receipt.BookingId, receipt.CulqiPaymentId, receipt.Id, ct: ct);
                }

                return receipt;
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task<int> ProcessPendingReceiptsAsync(CancellationToken ct = default)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-15);
            var currentMode = _config["Nubefact:Mode"]?.Trim() ?? "Demo";

            // 1. Limpieza y conciliación de comprobantes en vuelo ('InFlight') abandonados tras reinicio del servidor
            var inFlightList = await _db.ElectronicReceipts
                .Where(r => r.Status == "InFlight")
                .ToListAsync(ct);

            foreach (var abandoned in inFlightList)
            {
                bool isActive = IsActiveClaim(abandoned.LastError, out _);
                if (!isActive)
                {
                    if (abandoned.LastError != null && abandoned.LastError.StartsWith("CLAIMED:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Abandonado antes de enviar externamente -> intención realmente no enviada, regresar a Pending para recuperarla
                        abandoned.Status = "Pending";
                        abandoned.LastError = "Intención de emisión recuperada tras reinicio del sistema (nunca enviada al proveedor).";
                    }
                    else
                    {
                        // La solicitud fue enviada o no se puede descartar su envío: retener en RequiresReview
                        abandoned.Status = "RequiresReview";
                        abandoned.LastError = "La emisión quedó interrumpida o en vuelo tras reinicio del servidor. Se requiere revisión manual antes de reintentar para no duplicar comprobantes en SUNAT.";
                    }
                }
            }
            if (inFlightList.Count > 0)
            {
                await _db.SaveChangesAsync(ct);
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

                var workerClaimToken = Guid.NewGuid().ToString("N");
                var workerStamp = $"CLAIMED:{workerClaimToken}:{DateTime.UtcNow:O}";

                // Reclamo atómico en base de datos para impedir procesamiento simultáneo entre múltiples procesos
                if (receipt.Status == "Pending")
                {
                    int claimed = 0;
                    if (_db.Database.IsRelational())
                    {
                        claimed = await _db.ElectronicReceipts
                            .Where(r => r.Id == receipt.Id && r.Status == "Pending")
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(r => r.Status, "InFlight")
                                .SetProperty(r => r.LastError, workerStamp), ct);
                    }
                    else
                    {
                        if (receipt.Status == "Pending")
                        {
                            receipt.Status = "InFlight";
                            receipt.LastError = workerStamp;
                            await _db.SaveChangesAsync(ct);
                            claimed = 1;
                        }
                    }

                    if (claimed == 0)
                    {
                        // Ya fue reclamado por la emisión inicial o por otro hilo/proceso recuperador
                        continue;
                    }
                }

                var semaphore = _receiptLocks.GetOrAdd(receipt.Id, _ => new SemaphoreSlim(1, 1));
                if (!await semaphore.WaitAsync(TimeSpan.FromMilliseconds(100), ct))
                {
                    // Otro hilo ya está procesando este comprobante en memoria
                    continue;
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(receipt.Number))
                    {
                        var queried = await ReconcileReceiptWithProviderAsync(receipt.Id, ct);
                        if (queried.Status == NubefactReconciliationStatus.Confirmed)
                        {
                            processedCount++;
                            continue;
                        }
                    }

                    await GenerateReceiptAsync(receipt.BookingId, receipt.CulqiPaymentId, receipt.Id, claimToken: workerClaimToken, ct: ct);
                    processedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo recuperar el comprobante pendiente {ReceiptId}", receipt.Id);
                }
                finally
                {
                    semaphore.Release();
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
