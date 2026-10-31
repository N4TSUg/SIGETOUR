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

        public async Task<ElectronicReceipt> GenerateReceiptAsync(Guid bookingId, Guid? culqiPaymentId = null, CancellationToken ct = default)
        {
            // 1. Prevención estricta de duplicados / idempotencia (Reutilizar si ya fue Aceptado o Simulado)
            var existing = await _db.ElectronicReceipts
                .FirstOrDefaultAsync(r => ((culqiPaymentId != null && r.CulqiPaymentId == culqiPaymentId) || (culqiPaymentId == null && r.BookingId == bookingId))
                    && (r.Status == "Accepted" || r.Status == "Simulation"), ct);
            if (existing != null) return existing;

            // Reutilizar registro si quedó en estado fallido/rechazado para actualizarlo en vez de duplicarlo
            var receipt = await _db.ElectronicReceipts
                .FirstOrDefaultAsync(r => (culqiPaymentId != null && r.CulqiPaymentId == culqiPaymentId)
                    || (culqiPaymentId == null && r.BookingId == bookingId), ct);
            bool isNew = receipt == null;

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

            if (receipt == null)
            {
                receipt = new ElectronicReceipt
                {
                    BookingId = booking.Id,
                    CulqiPaymentId = culqiPaymentId,
                    CreatedAtUtc = DateTime.UtcNow
                };
            }

            receipt.ReceiptType = receiptType;
            receipt.Series = defaultSeries;
            // No generar correlativos aleatorios; NubeFact asigna el número oficial correlativo
            receipt.CustomerDocType = docType;
            receipt.CustomerDocNumber = !string.IsNullOrWhiteSpace(dni) ? dni : "00000000";
            receipt.CustomerName = !string.IsNullOrWhiteSpace(booking.CustomerName) ? booking.CustomerName : "Cliente General";
            receipt.CustomerEmail = booking.CustomerEmail ?? string.Empty;
            receipt.SubtotalAmount = subtotal;
            receipt.IgvAmount = igv;
            receipt.TotalAmount = total;

            // 5. Separación estricta de Modos: Mock, Demo y Production
            var mode = _config["Nubefact:Mode"]?.Trim() ?? "Demo";
            var isMock = string.Equals(mode, "Mock", StringComparison.OrdinalIgnoreCase);
            var ruta = _config["Nubefact:Ruta"]?.Trim();
            var token = _config["Nubefact:Token"]?.Trim();

            // No pasar silenciosamente a Mock cuando falten credenciales en Demo o Production
            if (!isMock && (string.IsNullOrWhiteSpace(ruta) || string.IsNullOrWhiteSpace(token)))
            {
                throw new InvalidOperationException($"Configuración de NubeFact incompleta para el modo '{mode}'. Se requiere 'Ruta' y 'Token' válidos en la configuración.");
            }

            // Modo Mock explícito (solo cuando mode == 'Mock')
            if (isMock)
            {
                _logger.LogInformation("Nubefact Modo Simulación (Mock) explícito activo para reserva {RefCode}. Registrando comprobante interno.", booking.ReferenceCode);
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

            // 6. Conciliación previa: Si el comprobante ya existía en estado Pending o con resultado desconocido, consultar antes de reintentar emisión
            if (!isNew && receipt.Status == "Pending" && !string.IsNullOrWhiteSpace(receipt.Number))
            {
                var queried = await QueryReceiptFromProviderAsync(receipt.Id, ct);
                if (queried != null && (queried.Status == "Accepted" || queried.Status == "Submitted"))
                {
                    _logger.LogInformation("Comprobante {FullNumber} recuperado y confirmado tras consulta previa con el proveedor.", queried.FullNumber);
                    return queried;
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
                    anticipo_regularizacion = false,
                    anticipo_documento_serie = (string?)null,
                    anticipo_documento_numero = (string?)null
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
                        anticipo_regularizacion = false,
                        anticipo_documento_serie = (string?)null,
                        anticipo_documento_numero = (string?)null
                    });
                }
            }

            var sendToSunat = string.Equals(mode, "Production", StringComparison.OrdinalIgnoreCase)
                && string.Equals(_config["Nubefact:SendToSunat"], "true", StringComparison.OrdinalIgnoreCase);

            var pdfFormat = _config["Nubefact:PdfFormat"] ?? "TICKET";

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
                ["fecha_de_emision"] = DateTime.UtcNow.ToString("dd-MM-yyyy"),
                ["moneda"] = 1, // Soles
                ["porcentaje_de_igv"] = isExonerado ? 0 : 18.00,
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
                ["medio_de_pago"] = !string.IsNullOrWhiteSpace(booking.PaymentMethod) ? booking.PaymentMethod.ToLowerInvariant() : "tarjeta_de_credito",
                ["formato_de_pdf"] = pdfFormat,
                ["items"] = itemsList
            };

            // 8. Invocación a API REST de NubeFact y comprobación tributaria estricta
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
                    // No interpretar automáticamente HTTP 200 como aceptación tributaria
                    if (root.TryGetProperty("errors", out var errProp) && !string.IsNullOrWhiteSpace(errProp.GetString()))
                    {
                        receipt.Status = "Rejected";
                        receipt.LastError = errProp.GetString();
                        _logger.LogWarning("NubeFact devolvió errores en HTTP 200 para reserva {RefCode}: {Errors}", booking.ReferenceCode, errProp.GetString());
                    }
                    else
                    {
                        // Comprobar explícitamente aceptación tributaria
                        bool isSunatAccepted = false;
                        if (root.TryGetProperty("aceptada_por_sunat", out var asp) && asp.ValueKind == JsonValueKind.True)
                        {
                            isSunatAccepted = true;
                        }
                        else if (root.TryGetProperty("sunat_responsecode", out var src) && (src.GetString() == "0" || src.ToString() == "0"))
                        {
                            isSunatAccepted = true;
                        }
                        else if (!sendToSunat && root.TryGetProperty("enlace_del_pdf", out _))
                        {
                            // En modo DEMO con enviar_a_sunat = false, el documento es emitido válidamente por NubeFact DEMO
                            isSunatAccepted = true;
                        }

                        receipt.Status = isSunatAccepted ? "Accepted" : "Submitted";

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
                        receipt.ProviderDescription = root.TryGetProperty("sunat_description", out var descProp) ? descProp.GetString() : (isSunatAccepted ? "Aceptado" : "Enviado");

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
                        receipt.Status = "Pending"; // Error temporal en gateway o servidor NubeFact
                        receipt.LastError = $"Error temporal del proveedor NubeFact (HTTP {httpCode}). Requiere consulta antes de reintentar.";
                    }
                    else
                    {
                        receipt.Status = "Rejected"; // 4xx: Parámetros inválidos o rechazo de negocio
                        receipt.LastError = jsonStr;
                    }
                    _logger.LogWarning("NubeFact respondió HTTP {StatusCode} para {RefCode}: {Response}", response.StatusCode, booking.ReferenceCode, jsonStr);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Excepción de red invocando API NubeFact para reserva {RefCode}", booking.ReferenceCode);
                receipt.Status = "Pending"; // Timeout o fallo de red: no asumir rechazo ni emisión
                receipt.LastError = $"Fallo de conexión con NubeFact: {ex.Message}";
            }

            if (isNew)
            {
                _db.ElectronicReceipts.Add(receipt);
            }
            if (receipt.Status == "Accepted")
            {
                receipt.LastError = null;
            }
            booking.InvoiceNumber = receipt.FullNumber;
            await _db.SaveChangesAsync(ct);
            return receipt;
        }

        public async Task<ElectronicReceipt?> QueryReceiptFromProviderAsync(Guid receiptId, CancellationToken ct = default)
        {
            var receipt = await _db.ElectronicReceipts
                .Include(r => r.Booking)
                .FirstOrDefaultAsync(r => r.Id == receiptId, ct);
            if (receipt == null) return null;

            var ruta = _config["Nubefact:Ruta"]?.Trim();
            var token = _config["Nubefact:Token"]?.Trim();
            if (string.IsNullOrWhiteSpace(ruta) || string.IsNullOrWhiteSpace(token))
                return receipt;

            // No consultar documento número 1 como respaldo si no tiene un correlativo válido
            if (string.IsNullOrWhiteSpace(receipt.Number) || !int.TryParse(receipt.Number, out var numInt) || numInt <= 0)
            {
                _logger.LogWarning("No es posible consultar el comprobante {ReceiptId} porque no posee un correlativo numérico válido ({Number}).", receiptId, receipt.Number);
                return receipt;
            }

            try
            {
                var isFactura = receipt.ReceiptType == "Factura";
                var payload = new
                {
                    operacion = "consultar_comprobante",
                    tipo_de_comprobante = isFactura ? 1 : 2,
                    serie = receipt.Series,
                    numero = numInt // Correlativo real, nunca número 1 ficticio
                };

                using var requestMsg = new HttpRequestMessage(HttpMethod.Post, ruta);
                requestMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                requestMsg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(requestMsg, ct);
                var jsonStr = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                if (response.IsSuccessStatusCode && !root.TryGetProperty("errors", out _))
                {
                    // No asumir aceptada_por_sunat = true si el campo falta
                    bool sunatAccepted = false;
                    if (root.TryGetProperty("aceptada_por_sunat", out var aProp) && aProp.ValueKind == JsonValueKind.True)
                    {
                        sunatAccepted = true;
                    }
                    else if (root.TryGetProperty("sunat_responsecode", out var rcProp) && (rcProp.GetString() == "0" || rcProp.ToString() == "0"))
                    {
                        sunatAccepted = true;
                    }
                    else if (root.TryGetProperty("enlace_del_pdf", out _) && root.TryGetProperty("codigo_hash", out _))
                    {
                        // En demo, si tiene PDF y hash generado sin errores SUNAT
                        sunatAccepted = true;
                    }

                    receipt.Status = sunatAccepted ? "Accepted" : "Submitted";
                    receipt.PdfUrl = root.TryGetProperty("enlace_del_pdf", out var p) ? p.GetString() : receipt.PdfUrl;
                    receipt.XmlUrl = root.TryGetProperty("enlace_del_xml", out var x) ? x.GetString() : receipt.XmlUrl;
                    receipt.CdrUrl = root.TryGetProperty("enlace_del_cdr", out var c) ? c.GetString() : receipt.CdrUrl;
                    receipt.DigitalHash = root.TryGetProperty("codigo_hash", out var h) ? h.GetString() : receipt.DigitalHash;
                    receipt.ProviderDescription = root.TryGetProperty("sunat_description", out var s) ? s.GetString() : receipt.ProviderDescription;
                    if (root.TryGetProperty("cadena_para_codigo_qr", out var q) && !string.IsNullOrWhiteSpace(q.GetString()))
                    {
                        receipt.QrData = q.GetString();
                    }
                    if (receipt.Status == "Accepted")
                    {
                        receipt.LastError = null;
                    }
                    await _db.SaveChangesAsync(ct);
                }
                else if (root.TryGetProperty("errors", out var errProp))
                {
                    receipt.Status = "Rejected";
                    receipt.LastError = errProp.GetString();
                    await _db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al consultar estado en NubeFact para comprobante {FullNumber}", receipt.FullNumber);
            }

            return receipt;
        }
    }
}
