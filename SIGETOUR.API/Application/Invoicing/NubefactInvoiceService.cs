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

        public async Task<ElectronicReceipt?> GetReceiptByBookingIdAsync(Guid bookingId, CancellationToken ct = default)
        {
            return await _db.ElectronicReceipts
                .AsNoTracking()
                .Where(r => r.BookingId == bookingId)
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<ElectronicReceipt> GenerateReceiptAsync(Guid bookingId, Guid? culqiPaymentId = null, CancellationToken ct = default)
        {
            // 1. Prevención estricta de duplicados / idempotencia
            var existing = await _db.ElectronicReceipts
                .FirstOrDefaultAsync(r => (culqiPaymentId != null && r.CulqiPaymentId == culqiPaymentId)
                    || (culqiPaymentId == null && r.BookingId == bookingId && (r.Status == "Accepted" || r.Status == "Simulation")), ct);
            if (existing != null) return existing;

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
            }

            // 2. Determinación de importes, anticipos y saldos
            var paymentAmount = culqiPayment != null
                ? culqiPayment.AmountCents / 100m
                : (booking.PaidAmount > 0 ? booking.PaidAmount : booking.TotalAmount);

            if (paymentAmount <= 0)
            {
                paymentAmount = booking.TotalAmount;
            }

            var isAdvance = culqiPayment != null && booking.PaidAmount < booking.TotalAmount;
            var isBalance = culqiPayment != null && booking.PaidAmount >= booking.TotalAmount && (booking.PaidAmount - paymentAmount > 0);

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

            var receipt = new ElectronicReceipt
            {
                BookingId = booking.Id,
                CulqiPaymentId = culqiPaymentId,
                ReceiptType = receiptType,
                Series = defaultSeries,
                Number = $"{DateTime.UtcNow:yyMMdd}{new Random().Next(1000, 9999)}",
                CustomerDocType = docType,
                CustomerDocNumber = !string.IsNullOrWhiteSpace(dni) ? dni : "00000000",
                CustomerName = !string.IsNullOrWhiteSpace(booking.CustomerName) ? booking.CustomerName : "Cliente General",
                CustomerEmail = booking.CustomerEmail ?? string.Empty,
                SubtotalAmount = subtotal,
                IgvAmount = igv,
                TotalAmount = total,
                CreatedAtUtc = DateTime.UtcNow
            };

            // 5. Separación de Modos: Mock, Demo y Production
            var mode = _config["Nubefact:Mode"]?.Trim() ?? "Demo";
            var isMock = string.Equals(mode, "Mock", StringComparison.OrdinalIgnoreCase);
            var ruta = _config["Nubefact:Ruta"]?.Trim();
            var token = _config["Nubefact:Token"]?.Trim();

            // En modo Mock o si no se proveyeron credenciales HTTP en Demo
            if (isMock || string.IsNullOrWhiteSpace(ruta) || string.IsNullOrWhiteSpace(token))
            {
                _logger.LogInformation("Nubefact Modo Simulación (Mock) activo para reserva {RefCode}. Registrando comprobante interno.", booking.ReferenceCode);
                receipt.Status = "Simulation";
                receipt.ProviderResponseCode = "0";
                receipt.ProviderDescription = "Comprobante generado en modo Simulación/Mock (sin impacto SUNAT)";
                receipt.PdfUrl = $"/Booking/Voucher/{booking.Id}";
                receipt.IssuedAtUtc = DateTime.UtcNow;

                _db.ElectronicReceipts.Add(receipt);
                booking.InvoiceNumber = receipt.FullNumber;
                await _db.SaveChangesAsync(ct);
                return receipt;
            }

            // 6. Construcción del Payload JSON Oficial NubeFact
            var tourTitle = booking.Items.FirstOrDefault()?.TourPackage?.Title ?? "Paquete Turístico";
            var itemConcept = isAdvance ? " (Anticipo)" : (isBalance ? " (Saldo Final)" : " (Pago Total)");

            var itemPayload = new
            {
                unidad_de_medida = "ZZ", // Servicio
                codigo = (booking.Items.FirstOrDefault()?.TourPackageId.ToString() ?? "TOUR")[..Math.Min(6, booking.Items.FirstOrDefault()?.TourPackageId.ToString()?.Length ?? 4)],
                descripcion = $"Servicio Turístico: {tourTitle}{itemConcept}",
                cantidad = 1,
                valor_unitario = subtotal.ToString("F2", CultureInfo.InvariantCulture),
                precio_unitario = total.ToString("F2", CultureInfo.InvariantCulture),
                descuento = (string?)null,
                subtotal = subtotal.ToString("F2", CultureInfo.InvariantCulture),
                tipo_de_igv = isExonerado ? "8" : "1",
                igv = igv.ToString("F2", CultureInfo.InvariantCulture),
                total = total.ToString("F2", CultureInfo.InvariantCulture),
                anticipo_regularizacion = false,
                anticipo_documento_serie = (string?)null,
                anticipo_documento_numero = (string?)null
            };

            var sendToSunat = string.Equals(mode, "Production", StringComparison.OrdinalIgnoreCase)
                && string.Equals(_config["Nubefact:SendToSunat"], "true", StringComparison.OrdinalIgnoreCase);

            var payload = new Dictionary<string, object?>
            {
                ["operacion"] = "generar_comprobante",
                ["tipo_de_comprobante"] = isFactura ? "1" : "2",
                ["serie"] = receipt.Series,
                ["numero"] = null,
                ["sunat_transaction"] = isAdvance ? "2" : "1",
                ["cliente_tipo_de_documento"] = receipt.CustomerDocType,
                ["cliente_numero_de_documento"] = receipt.CustomerDocNumber,
                ["cliente_denominacion"] = receipt.CustomerName,
                ["cliente_direccion"] = "Cajamarca, Perú",
                ["cliente_email"] = receipt.CustomerEmail,
                ["fecha_de_emision"] = DateTime.UtcNow.ToString("dd-MM-yyyy"),
                ["moneda"] = "1",
                ["porcentaje_de_igv"] = isExonerado ? "0.00" : (igvRate * 100m).ToString("F2", CultureInfo.InvariantCulture),
                ["total_gravada"] = isExonerado ? null : subtotal.ToString("F2", CultureInfo.InvariantCulture),
                ["total_exonerada"] = isExonerado ? total.ToString("F2", CultureInfo.InvariantCulture) : null,
                ["total_inafecta"] = null,
                ["total_gratuita"] = null,
                ["total_descuento"] = null,
                ["total_igv"] = igv.ToString("F2", CultureInfo.InvariantCulture),
                ["total"] = total.ToString("F2", CultureInfo.InvariantCulture),
                ["enviar_a_sunat"] = sendToSunat,
                ["enviar_al_cliente"] = false,
                ["codigo_unico"] = $"SIG-{receipt.Id:N}",
                ["condiciones_de_pago"] = "Contado",
                ["medio_de_pago"] = "tarjeta_de_credito",
                ["formato_de_pdf"] = "A4",
                ["items"] = new[] { itemPayload }
            };

            // 7. Invocación a API REST de NubeFact
            try
            {
                using var requestMsg = new HttpRequestMessage(HttpMethod.Post, ruta);
                requestMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                requestMsg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(requestMsg, ct);
                var jsonStr = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                if (response.IsSuccessStatusCode)
                {
                    receipt.Status = "Accepted";
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
                    receipt.ProviderDescription = root.TryGetProperty("sunat_description", out var descProp) ? descProp.GetString() : "Aceptado";
                    receipt.IssuedAtUtc = DateTime.UtcNow;
                }
                else
                {
                    receipt.Status = "Rejected";
                    receipt.LastError = jsonStr;
                    _logger.LogWarning("NubeFact rechazó la emisión del comprobante para {RefCode}: {Response}", booking.ReferenceCode, jsonStr);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Excepción invocando API NubeFact para reserva {RefCode}", booking.ReferenceCode);
                receipt.Status = "Pending";
                receipt.LastError = ex.Message;
            }

            _db.ElectronicReceipts.Add(receipt);
            booking.InvoiceNumber = receipt.FullNumber;
            await _db.SaveChangesAsync(ct);
            return receipt;
        }
    }
}
