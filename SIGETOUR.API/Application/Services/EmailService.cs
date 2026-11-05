using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SIGETOUR.API.Application.Services
{
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<EmailService> _logger;
        private readonly CheckoutAccess _checkoutAccess;
        private readonly ICompanyContactService _contactService;
        private readonly IServiceProvider _serviceProvider;

        public EmailService(
            IConfiguration config,
            ILogger<EmailService> logger,
            CheckoutAccess checkoutAccess,
            ICompanyContactService contactService,
            IServiceProvider serviceProvider)
        {
            _config = config;
            _logger = logger;
            _checkoutAccess = checkoutAccess;
            _contactService = contactService;
            _serviceProvider = serviceProvider;
        }

        public string SenderEmail
        {
            get
            {
                return Environment.GetEnvironmentVariable("SMTP_USER")
                    ?? Environment.GetEnvironmentVariable("SMTP_EMAIL")
                    ?? _config["EmailSettings:SenderEmail"]
                    ?? _config["EmailSettings:User"]
                    ?? "enriquearana1402@gmail.com";
            }
        }

        public string SenderName
        {
            get
            {
                return Environment.GetEnvironmentVariable("SMTP_FROM_NAME")
                    ?? _config["EmailSettings:SenderName"]
                    ?? "Turismo Chilón Cajamarca";
            }
        }

        private string SmtpHost => Environment.GetEnvironmentVariable("SMTP_HOST")
            ?? _config["EmailSettings:Host"]
            ?? "smtp.gmail.com";

        private int SmtpPort
        {
            get
            {
                var val = Environment.GetEnvironmentVariable("SMTP_PORT") ?? _config["EmailSettings:Port"];
                return int.TryParse(val, out var p) ? p : 587;
            }
        }

        private string SmtpPassword => Environment.GetEnvironmentVariable("SMTP_PASSWORD")
            ?? Environment.GetEnvironmentVariable("SMTP_APP_PASSWORD")
            ?? _config["EmailSettings:Password"]
            ?? "";

        private bool EnableSsl
        {
            get
            {
                var val = Environment.GetEnvironmentVariable("SMTP_ENABLE_SSL") ?? _config["EmailSettings:EnableSsl"];
                return string.IsNullOrWhiteSpace(val) || bool.TryParse(val, out var b) && b;
            }
        }

        public async Task<(bool Success, string Message)> SendEmailAsync(
            string toEmail,
            string subject,
            string htmlBody,
            string? textBody = null,
            IEnumerable<Attachment>? attachments = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(toEmail) || !toEmail.Contains("@"))
            {
                return (false, "La dirección de correo destinatario no es válida.");
            }

            var attList = attachments != null ? attachments.ToList() : new List<Attachment>();

            // Modo simulación si no hay contraseña SMTP
            if (string.IsNullOrWhiteSpace(SmtpPassword))
            {
                _logger.LogInformation(
                    "[EmailService SIMULACIÓN] Correo preparado para {ToEmail} desde {SenderEmail} ({AttCount} adjuntos). Configure SMTP_PASSWORD para entrega real vía Gmail.",
                    toEmail, SenderEmail, attList.Count);

                return (true, $"Modo de prueba: Boleto digital preparado con éxito ({attList.Count} archivo(s) PDF adjuntos) para {toEmail}. (Ingrese su Contraseña de Aplicación en SMTP_PASSWORD para entrega real a Gmail).");
            }

            try
            {
                using var client = new SmtpClient(SmtpHost, SmtpPort)
                {
                    EnableSsl = EnableSsl,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(SenderEmail, SmtpPassword),
                    Timeout = 20000
                };

                using var mail = new MailMessage
                {
                    From = new MailAddress(SenderEmail, SenderName),
                    Subject = subject,
                    Body = htmlBody,
                    IsBodyHtml = true,
                    BodyEncoding = Encoding.UTF8,
                    SubjectEncoding = Encoding.UTF8
                };

                mail.To.Add(new MailAddress(toEmail.Trim()));

                // Agregar versiones Plain Text y HTML para compatibilidad óptima con Gmail y clientes móviles
                if (!string.IsNullOrWhiteSpace(textBody))
                {
                    mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(textBody, Encoding.UTF8, "text/plain"));
                }
                mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(htmlBody, Encoding.UTF8, "text/html"));

                // Adjuntar archivos PDF
                foreach (var att in attList)
                {
                    mail.Attachments.Add(att);
                }

                await client.SendMailAsync(mail, cancellationToken);
                _logger.LogInformation("[EmailService] Correo con {Count} adjuntos enviado exitosamente a {ToEmail}", attList.Count, toEmail);
                return (true, $"Correo y {attList.Count} archivo(s) PDF (Boleto y Boleta) enviados exitosamente a {toEmail}.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmailService ERROR] Falló el envío de correo a {ToEmail}: {Error}", toEmail, ex.Message);
                return (false, $"Error al conectar con el servidor de correo: {ex.Message}");
            }
        }

        public async Task<(bool Success, string Message, int AttachedCount)> SendBookingConfirmationEmailAsync(
            Booking booking,
            string? baseUrl = null,
            bool attachVoucherPdf = true,
            bool attachReceiptPdf = true,
            CancellationToken cancellationToken = default)
        {
            if (booking == null) return (false, "Reserva inexistente.", 0);

            var customerEmail = booking.CustomerEmail?.Trim();
            if (string.IsNullOrWhiteSpace(customerEmail))
            {
                return (false, "La reserva no tiene un correo electrónico de cliente registrado.", 0);
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = _config["App:BaseUrl"] ?? "https://localhost:7103";
            }
            baseUrl = baseUrl.TrimEnd('/');

            // Obtener AppDbContext para consultar recibos e ítems si faltan
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetService<AppDbContext>();

            TourPackage? tour = null;
            if (booking.Items != null && booking.Items.Count > 0)
            {
                tour = booking.Items.FirstOrDefault()?.TourPackage;
            }

            if (tour == null && db != null)
            {
                var loadedBooking = await db.Bookings
                    .Include(b => b.Items).ThenInclude(i => i.TourPackage)
                    .Include(b => b.AssignedVehicle)
                    .FirstOrDefaultAsync(b => b.Id == booking.Id, cancellationToken);

                if (loadedBooking != null)
                {
                    booking = loadedBooking;
                    tour = loadedBooking.Items.FirstOrDefault()?.TourPackage;
                }
            }

            // Buscar comprobante electrónico oficial (NubeFact / Sunat)
            ElectronicReceipt? receipt = null;
            if (db != null)
            {
                receipt = await db.ElectronicReceipts
                    .Where(r => r.BookingId == booking.Id)
                    .OrderByDescending(r => r.CreatedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            var tourTitle = tour?.Title ?? "Tour Turístico Cajamarca";
            var tourDuration = tour?.Duration ?? "Medio Día (4 Horas)";
            var receiptLabel = receipt != null ? $"{receipt.Series}-{receipt.Number}" : null;

            // Generar PDF 1: Boleto Oficial de Viaje (Voucher)
            byte[]? voucherPdfBytes = null;
            if (attachVoucherPdf)
            {
                try
                {
                    voucherPdfBytes = VoucherPdfGenerator.GenerateVoucherPdf(booking, tour, receiptLabel);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo generar el PDF del voucher para la reserva {Code}", booking.ReferenceCode);
                }
            }

            // Obtener PDF 2: Boleta de Venta Electrónica (SUNAT / NubeFact)
            byte[]? receiptPdfBytes = null;
            string receiptFileName = receipt != null ? $"Boleta_{receipt.Series}-{receipt.Number}.pdf" : "Boleta_Venta_SUNAT.pdf";
            if (attachReceiptPdf && receipt != null && !string.IsNullOrWhiteSpace(receipt.PdfUrl))
            {
                if (receipt.PdfUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                        receiptPdfBytes = await httpClient.GetByteArrayAsync(receipt.PdfUrl, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "No se pudo descargar el PDF de la boleta desde {Url}", receipt.PdfUrl);
                    }
                }
            }

            var attachments = new List<Attachment>();
            if (voucherPdfBytes != null && voucherPdfBytes.Length > 0)
            {
                attachments.Add(new Attachment(new MemoryStream(voucherPdfBytes), $"Boleto_Oficial_{booking.ReferenceCode}.pdf", "application/pdf"));
            }
            if (receiptPdfBytes != null && receiptPdfBytes.Length > 0)
            {
                attachments.Add(new Attachment(new MemoryStream(receiptPdfBytes), receiptFileName, "application/pdf"));
            }

            // Token seguro de acceso al Voucher
            var voucherToken = _checkoutAccess.Create(booking.Id, TimeSpan.FromDays(30));
            var voucherUrl = $"{baseUrl}/Booking/Voucher/{booking.Id}?token={voucherToken}";

            // Pasajeros adicionales
            var companionList = new List<string>();
            try
            {
                if (!string.IsNullOrWhiteSpace(booking.AdditionalPassengersJson) && booking.AdditionalPassengersJson != "[]")
                {
                    using var doc = JsonDocument.Parse(booking.AdditionalPassengersJson);
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        if (elem.TryGetProperty("Name", out var nameProp))
                        {
                            var nameStr = nameProp.GetString();
                            if (!string.IsNullOrWhiteSpace(nameStr)) companionList.Add(nameStr);
                        }
                    }
                }
            }
            catch { }

            var pendingBalance = booking.TotalAmount - booking.PaidAmount;
            if (pendingBalance < 0) pendingBalance = 0;
            var isFullyPaid = booking.PaidAmount >= booking.TotalAmount && booking.TotalAmount > 0;
            var waHelpUrl = _contactService.GetWhatsAppUrl($"Hola Turismo Chilón, tengo una consulta sobre mi reserva confirmada {booking.ReferenceCode}");
            var subject = $"Confirmación de Reserva & Boleto Oficial {booking.ReferenceCode} - Turismo Chilón";

            var htmlBuilder = new StringBuilder();
            htmlBuilder.AppendLine("<!DOCTYPE html>");
            htmlBuilder.AppendLine("<html lang='es'>");
            htmlBuilder.AppendLine("<head>");
            htmlBuilder.AppendLine("<meta charset='utf-8'>");
            htmlBuilder.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1.0'>");
            htmlBuilder.AppendLine($"<title>{subject}</title>");
            htmlBuilder.AppendLine("<style>");
            htmlBuilder.AppendLine("body { margin:0; padding:0; background-color:#f0fdf9; font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif; color:#0f3d3e; }");
            htmlBuilder.AppendLine(".container { max-width:620px; margin:24px auto; background:#ffffff; border-radius:18px; overflow:hidden; border:1px solid #d4f6ee; box-shadow:0 8px 24px rgba(0,135,117,0.08); }");
            htmlBuilder.AppendLine(".header { background:linear-gradient(135deg, #008775 0%, #005e52 100%); color:#ffffff; padding:28px 24px; text-align:center; }");
            htmlBuilder.AppendLine(".badge { display:inline-block; padding:4px 12px; background:#dcfce7; color:#15803d; border-radius:999px; font-size:11px; font-weight:bold; text-transform:uppercase; letter-spacing:0.05em; margin-bottom:10px; }");
            htmlBuilder.AppendLine(".title { margin:0 0 4px; font-size:24px; font-weight:800; letter-spacing:-0.02em; }");
            htmlBuilder.AppendLine(".subtitle { margin:0; font-size:13px; color:#d4f6ee; }");
            htmlBuilder.AppendLine(".body-card { padding:24px; }");
            htmlBuilder.AppendLine(".code-box { background:#f7fefc; border:2px dashed #008775; border-radius:14px; padding:16px; text-align:center; margin-bottom:22px; }");
            htmlBuilder.AppendLine(".code-label { font-size:11px; text-transform:uppercase; color:#5e7774; font-weight:bold; letter-spacing:0.08em; display:block; margin-bottom:4px; }");
            htmlBuilder.AppendLine(".code-val { font-size:26px; font-weight:900; font-family:monospace; color:#008775; letter-spacing:0.1em; }");
            htmlBuilder.AppendLine(".section-title { font-size:13px; font-weight:800; text-transform:uppercase; color:#008775; letter-spacing:0.05em; margin:20px 0 10px; border-bottom:1px solid #e6faf5; padding-bottom:6px; }");
            htmlBuilder.AppendLine(".data-table { width:100%; border-collapse:collapse; margin-bottom:16px; }");
            htmlBuilder.AppendLine(".data-table td { padding:8px 0; font-size:13px; vertical-align:top; border-bottom:1px solid #f0fdf9; }");
            htmlBuilder.AppendLine(".data-label { color:#5e7774; font-weight:600; width:38%; }");
            htmlBuilder.AppendLine(".data-val { color:#092526; font-weight:700; width:62%; }");
            htmlBuilder.AppendLine(".btn-main { display:block; background:#008775; color:#ffffff !important; text-align:center; text-decoration:none; padding:14px 20px; border-radius:12px; font-weight:bold; font-size:14px; margin:20px 0 10px; box-shadow:0 4px 12px rgba(0,135,117,0.25); }");
            htmlBuilder.AppendLine(".btn-wa { display:block; background:#25D366; color:#ffffff !important; text-align:center; text-decoration:none; padding:12px 18px; border-radius:12px; font-weight:bold; font-size:13px; margin:8px 0; }");
            htmlBuilder.AppendLine(".box-info { background:#f0fdf9; border-left:4px solid #008775; padding:12px 16px; border-radius:6px; margin:20px 0; font-size:12px; line-height:1.5; color:#0f3d3e; }");
            htmlBuilder.AppendLine(".att-box { background:#f8fafc; border:1px solid #e2e8f0; border-radius:10px; padding:12px 16px; margin:16px 0; font-size:12px; }");
            htmlBuilder.AppendLine(".footer { background:#0f3d3e; color:#d4f6ee; padding:20px 24px; text-align:center; font-size:11px; line-height:1.6; }");
            htmlBuilder.AppendLine("</style>");
            htmlBuilder.AppendLine("</head>");
            htmlBuilder.AppendLine("<body>");
            htmlBuilder.AppendLine("<div class='container'>");
            
            // Header
            htmlBuilder.AppendLine("<div class='header'>");
            htmlBuilder.AppendLine("<div class='badge'>✓ Reserva Confirmada</div>");
            htmlBuilder.AppendLine("<h1 class='title'>TURISMO CHILÓN</h1>");
            htmlBuilder.AppendLine("<p class='subtitle'>Transporte &amp; Turismo Cajamarca • Comprobante de Viaje</p>");
            htmlBuilder.AppendLine("</div>");

            // Body
            htmlBuilder.AppendLine("<div class='body-card'>");
            htmlBuilder.AppendLine($"<p style='font-size:15px; margin-top:0;'>Estimado(a) <strong>{WebUtility.HtmlEncode(booking.CustomerName)}</strong>,</p>");
            htmlBuilder.AppendLine("<p style='font-size:13px; line-height:1.5; color:#344e4b;'>¡Tu reserva para descubrir la magia de Cajamarca ha sido confirmada con éxito! Adjunto a este correo encontrarás tus <strong>2 documentos oficiales en formato PDF</strong> (Boleto de Viaje y Boleta Electrónica).</p>");

            // Reference Code Box
            htmlBuilder.AppendLine("<div class='code-box'>");
            htmlBuilder.AppendLine("<span class='code-label'>Localizador de Viaje (Código de Reserva)</span>");
            htmlBuilder.AppendLine($"<div class='code-val'>{WebUtility.HtmlEncode(booking.ReferenceCode)}</div>");
            htmlBuilder.AppendLine($"<span style='font-size:11px; color:#5e7774;'>Emisión: {booking.BookingDate.ToString("dd/MM/yyyy HH:mm")} hrs</span>");
            htmlBuilder.AppendLine("</div>");

            // Attached Documents Box
            htmlBuilder.AppendLine("<div class='att-box'>");
            htmlBuilder.AppendLine("<strong>📎 Archivos Adjuntos a este Correo:</strong><br>");
            htmlBuilder.AppendLine($"• 📄 <strong>Boleto_Oficial_{WebUtility.HtmlEncode(booking.ReferenceCode)}.pdf</strong> (Itinerario, datos de embarque y manifiesto)<br>");
            if (receipt != null)
            {
                htmlBuilder.AppendLine($"• 🧾 <strong>{WebUtility.HtmlEncode(receiptFileName)}</strong> (Comprobante de Pago Electrónico Oficial SUNAT)");
            }
            htmlBuilder.AppendLine("</div>");

            // Tour Details
            htmlBuilder.AppendLine("<div class='section-title'>Detalles del Circuito Turístico</div>");
            htmlBuilder.AppendLine("<table class='data-table'>");
            htmlBuilder.AppendLine($"<tr><td class='data-label'>Circuito / Tour:</td><td class='data-val'>{WebUtility.HtmlEncode(tourTitle)}</td></tr>");
            htmlBuilder.AppendLine($"<tr><td class='data-label'>Fecha de Salida:</td><td class='data-val'>{booking.TravelDate.ToString("dddd, dd 'de' MMMM 'de' yyyy", new System.Globalization.CultureInfo("es-PE"))}</td></tr>");
            htmlBuilder.AppendLine($"<tr><td class='data-label'>Turno / Horario:</td><td class='data-val'>{(string.IsNullOrWhiteSpace(booking.ShiftName) ? "Turno Mañana" : WebUtility.HtmlEncode(booking.ShiftName))}</td></tr>");
            htmlBuilder.AppendLine($"<tr><td class='data-label'>Duración estimada:</td><td class='data-val'>{WebUtility.HtmlEncode(tourDuration)}</td></tr>");
            
            var pickupText = (!string.IsNullOrWhiteSpace(booking.PickupLocation) && booking.PickupLocation != "agencia")
                ? $"Recojo: {booking.PickupLocation} (Estar listo en recepción 15 min antes)"
                : "Agencia Central: Jr. 2 de Mayo 569, Cajamarca (Presentarse 15 min antes)";
            htmlBuilder.AppendLine($"<tr><td class='data-label'>Punto de Embarque:</td><td class='data-val'>{WebUtility.HtmlEncode(pickupText)}</td></tr>");
            htmlBuilder.AppendLine("</table>");

            // Passengers
            htmlBuilder.AppendLine("<div class='section-title'>Pasajeros Asignados</div>");
            htmlBuilder.AppendLine("<table class='data-table'>");
            htmlBuilder.AppendLine($"<tr><td class='data-label'>Titular del Boleto:</td><td class='data-val'>{WebUtility.HtmlEncode(booking.CustomerName)} (DNI: {(!string.IsNullOrWhiteSpace(booking.CustomerDni) ? booking.CustomerDni : "Verificar al embarque")})</td></tr>");
            htmlBuilder.AppendLine($"<tr><td class='data-label'>Total de Asientos:</td><td class='data-val'>{booking.TotalPassengers} Pasajero(s)</td></tr>");
            if (companionList.Count > 0)
            {
                htmlBuilder.AppendLine($"<tr><td class='data-label'>Acompañantes:</td><td class='data-val'>{WebUtility.HtmlEncode(string.Join(", ", companionList))}</td></tr>");
            }
            htmlBuilder.AppendLine("</table>");

            // Financial Summary
            htmlBuilder.AppendLine("<div class='section-title'>Liquidación de Pago</div>");
            htmlBuilder.AppendLine("<table class='data-table'>");
            htmlBuilder.AppendLine($"<tr><td class='data-label'>Tarifa Total:</td><td class='data-val'>S/ {booking.TotalAmount:F2}</td></tr>");
            htmlBuilder.AppendLine($"<tr><td class='data-label'>Monto Abonado:</td><td class='data-val' style='color:#15803d;'>S/ {booking.PaidAmount:F2}</td></tr>");
            if (isFullyPaid)
            {
                htmlBuilder.AppendLine("<tr><td class='data-label'>Estado de Pago:</td><td class='data-val' style='color:#15803d;'>✓ Pagado al 100%</td></tr>");
            }
            else
            {
                htmlBuilder.AppendLine($"<tr><td class='data-label'>Saldo Pendiente:</td><td class='data-val' style='color:#b91c1c;'>S/ {pendingBalance:F2} (A cancelar en Counter antes de la salida)</td></tr>");
            }
            if (receipt != null)
            {
                htmlBuilder.AppendLine($"<tr><td class='data-label'>Comprobante:</td><td class='data-val'>{WebUtility.HtmlEncode(receipt.ReceiptType)} {WebUtility.HtmlEncode(receipt.Series)}-{WebUtility.HtmlEncode(receipt.Number)}</td></tr>");
            }
            htmlBuilder.AppendLine("</table>");

            // CTA Buttons
            htmlBuilder.AppendLine("<div style='text-align:center; margin:24px 0 16px;'>");
            htmlBuilder.AppendLine($"<a href='{voucherUrl}' target='_blank' class='btn-main'>🎟️ VER Y DESCARGAR BOLETO DIGITAL EN LÍNEA</a>");
            htmlBuilder.AppendLine($"<a href='{waHelpUrl}' target='_blank' class='btn-wa'>💬 Coordinar o Consultar por WhatsApp</a>");
            htmlBuilder.AppendLine("</div>");

            // Recommendations
            htmlBuilder.AppendLine("<div class='box-info'>");
            htmlBuilder.AppendLine("<strong>Recomendaciones para el día del viaje:</strong><br>");
            htmlBuilder.AppendLine("• Presentarse 15 minutos antes de la hora acordada portando su DNI físico o documento de identidad.<br>");
            htmlBuilder.AppendLine("• Para circuitos de altura (Cumbemayo, Granja Porcón) se sugiere llevar abrigo cortaviento, calzado cómodo y protección solar.<br>");
            htmlBuilder.AppendLine("• Puede presentar los PDFs adjuntos directamente desde su celular al subir a la unidad.");
            htmlBuilder.AppendLine("</div>");

            htmlBuilder.AppendLine("</div>"); // End body-card

            // Footer
            htmlBuilder.AppendLine("<div class='footer'>");
            htmlBuilder.AppendLine("<strong>TURISMO CHILÓN • Transporte &amp; Turismo Cajamarca S.A.C.</strong><br>");
            htmlBuilder.AppendLine("RUC: 20601234567 • Inscrito en MINCETUR &amp; DIRCETUR Cert. N° 4412<br>");
            htmlBuilder.AppendLine($"Dirección: {_contactService.Address}<br>");
            htmlBuilder.AppendLine($"Central de Reservas: {_contactService.WhatsAppFormatted} / {_contactService.WhatsAppSecondaryFormatted}<br>");
            htmlBuilder.AppendLine("<p style='margin-top:10px; color:#8ef5df; font-size:10px;'>Este correo electrónico fue generado automáticamente. Por favor no responda directamente a este mensaje.</p>");
            htmlBuilder.AppendLine("</div>");

            htmlBuilder.AppendLine("</div>"); // End container
            htmlBuilder.AppendLine("</body>");
            htmlBuilder.AppendLine("</html>");

            var textBody = $"Confirmación de Reserva {booking.ReferenceCode} - Turismo Chilón\n\n" +
                           $"Estimado(a) {booking.CustomerName},\n\n" +
                           $"Tu reserva para el tour {tourTitle} el {booking.TravelDate:dd/MM/yyyy} ha sido confirmada.\n" +
                           $"Localizador: {booking.ReferenceCode}\n" +
                           $"Total: S/ {booking.TotalAmount:F2} (Abonado: S/ {booking.PaidAmount:F2})\n" +
                           $"Adjunto encontrarás tus 2 PDFs: Boleto Oficial de Viaje y Boleta Electrónica.\n" +
                           $"Ver boleto digital en línea: {voucherUrl}\n\n" +
                           $"Turismo Chilón Cajamarca - Jr. 2 de Mayo 569, Cajamarca\n";

            var result = await SendEmailAsync(customerEmail, subject, htmlBuilder.ToString(), textBody, attachments, cancellationToken);
            return (result.Success, result.Message, attachments.Count);
        }
    }
}
