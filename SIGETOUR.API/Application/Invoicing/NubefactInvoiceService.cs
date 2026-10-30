using System;
using System.Collections.Generic;
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
            // Idempotencia: si ya existe un comprobante emitido o aceptado para esta reserva, devolverlo
            var existing = await _db.ElectronicReceipts
                .FirstOrDefaultAsync(r => r.BookingId == bookingId && (r.Status == "Accepted" || r.Status == "Simulation"), ct);
            if (existing != null) return existing;

            var booking = await _db.Bookings
                .Include(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .FirstOrDefaultAsync(b => b.Id == bookingId, ct);

            if (booking == null)
                throw new InvalidOperationException($"No se encontró la reserva {bookingId} para facturación.");

            var isFactura = string.Equals(booking.InvoiceType, "factura", StringComparison.OrdinalIgnoreCase);
            var receiptType = isFactura ? "Factura" : "Boleta";
            var defaultSeries = isFactura ? (_config["Nubefact:FacturaSeries"] ?? "FFF1") : (_config["Nubefact:BoletaSeries"] ?? "BBB1");

            var totalAmount = booking.PaidAmount > 0 ? booking.PaidAmount : booking.TotalAmount;
            var subtotal = Math.Round(totalAmount / 1.18m, 2);
            var igv = totalAmount - subtotal;

            var docType = isFactura ? "6" : (!string.IsNullOrWhiteSpace(booking.CustomerDni) && booking.CustomerDni.Length == 8 ? "1" : "0");

            var receipt = new ElectronicReceipt
            {
                BookingId = booking.Id,
                CulqiPaymentId = culqiPaymentId,
                ReceiptType = receiptType,
                Series = defaultSeries,
                Number = $"{DateTime.UtcNow:yyMMdd}{new Random().Next(1000, 9999)}",
                CustomerDocType = docType,
                CustomerDocNumber = !string.IsNullOrWhiteSpace(booking.CustomerDni) ? booking.CustomerDni : "00000000",
                CustomerName = !string.IsNullOrWhiteSpace(booking.CustomerName) ? booking.CustomerName : "Cliente General",
                CustomerEmail = booking.CustomerEmail ?? string.Empty,
                SubtotalAmount = subtotal,
                IgvAmount = igv,
                TotalAmount = totalAmount,
                CreatedAtUtc = DateTime.UtcNow
            };

            var isLive = string.Equals(_config["Nubefact:IsLive"], "true", StringComparison.OrdinalIgnoreCase);
            var ruta = _config["Nubefact:Ruta"];
            var token = _config["Nubefact:Token"];

            // REGLA DE SEGURIDAD: En entorno Sandbox o sin credenciales activas, NO emitir ante SUNAT real.
            if (!isLive || string.IsNullOrWhiteSpace(ruta) || string.IsNullOrWhiteSpace(token))
            {
                _logger.LogInformation("Nubefact Sandbox/Modo Simulación activo para reserva {RefCode}. Registrando comprobante interno.", booking.ReferenceCode);
                receipt.Status = "Simulation";
                receipt.ProviderResponseCode = "0";
                receipt.ProviderDescription = "Comprobante generado en modo Simulación Sandbox (sin impacto SUNAT)";
                receipt.PdfUrl = $"/Booking/Voucher/{booking.Id}";
                receipt.IssuedAtUtc = DateTime.UtcNow;

                _db.ElectronicReceipts.Add(receipt);
                booking.InvoiceNumber = receipt.FullNumber;
                await _db.SaveChangesAsync(ct);
                return receipt;
            }

            // Flujo oficial con API REST de Nubefact (cuando se activen las credenciales reales)
            try
            {
                var payload = new
                {
                    operacion = "generar_comprobante",
                    tipo_de_comprobante = isFactura ? "1" : "2",
                    serie = receipt.Series,
                    sunat_transaction = "1",
                    cliente_tipo_de_documento = receipt.CustomerDocType,
                    cliente_numero_de_documento = receipt.CustomerDocNumber,
                    cliente_denominacion = receipt.CustomerName,
                    cliente_direccion = "Cajamarca, Perú",
                    cliente_email = receipt.CustomerEmail,
                    fecha_de_emision = DateTime.UtcNow.ToString("dd-MM-yyyy"),
                    moneda = "1",
                    porcentaje_de_igv = "18.00",
                    total_gravada = receipt.SubtotalAmount.ToString("F2"),
                    total_igv = receipt.IgvAmount.ToString("F2"),
                    total = receipt.TotalAmount.ToString("F2"),
                    enviar_a_sunat = true,
                    formato_de_pdf = "A4",
                    items = booking.Items.Select(item => new
                    {
                        unidad_de_medida = "ZZ",
                        codigo = item.TourPackageId.ToString()[..6],
                        descripcion = item.TourPackage?.Title ?? "Servicio Turístico",
                        cantidad = item.Passengers,
                        valor_unitario = Math.Round(item.UnitPrice / 1.18m, 2).ToString("F2"),
                        precio_unitario = item.UnitPrice.ToString("F2"),
                        subtotal = Math.Round(item.SubTotal / 1.18m, 2).ToString("F2"),
                        tipo_de_igv = "1",
                        igv = (item.SubTotal - Math.Round(item.SubTotal / 1.18m, 2)).ToString("F2"),
                        total = item.SubTotal.ToString("F2")
                    }).ToList()
                };

                using var requestMsg = new HttpRequestMessage(HttpMethod.Post, ruta);
                requestMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                requestMsg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(requestMsg, ct);
                var jsonStr = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(jsonStr);

                if (response.IsSuccessStatusCode)
                {
                    receipt.Status = "Accepted";
                    receipt.Number = doc.RootElement.TryGetProperty("numero", out var numProp) ? numProp.ToString() : receipt.Number;
                    receipt.PdfUrl = doc.RootElement.TryGetProperty("enlace_del_pdf", out var pdfProp) ? pdfProp.GetString() : null;
                    receipt.XmlUrl = doc.RootElement.TryGetProperty("enlace_del_xml", out var xmlProp) ? xmlProp.GetString() : null;
                    receipt.CdrUrl = doc.RootElement.TryGetProperty("enlace_del_cdr", out var cdrProp) ? cdrProp.GetString() : null;
                    receipt.DigitalHash = doc.RootElement.TryGetProperty("codigo_hash", out var hashProp) ? hashProp.GetString() : null;
                    receipt.ProviderDescription = doc.RootElement.TryGetProperty("sunat_description", out var descProp) ? descProp.GetString() : "Aceptado";
                    receipt.IssuedAtUtc = DateTime.UtcNow;
                }
                else
                {
                    receipt.Status = "Rejected";
                    receipt.LastError = jsonStr;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error invocando API NubeFact para reserva {RefCode}", booking.ReferenceCode);
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
