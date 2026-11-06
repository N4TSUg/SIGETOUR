using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Infrastructure.Data;

namespace SIGETOUR.API.Controllers.UI
{
    public class HomeController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IEmailService? _emailService;
        private readonly ICompanyContactService? _contactService;
        private readonly ILogger<HomeController>? _logger;

        public HomeController(
            AppDbContext context,
            IEmailService? emailService = null,
            ICompanyContactService? contactService = null,
            ILogger<HomeController>? logger = null)
        {
            _context = context;
            _emailService = emailService;
            _contactService = contactService;
            _logger = logger;
        }

        public async Task<IActionResult> Index()
        {
            var tours = await _context.TourPackages
                .Include(t => t.Images)
                .Include(t => t.Category)
                .Where(t => t.IsActive)
                .OrderBy(t => t.Title)
                .Take(4)
                .ToListAsync();
                
            var featured = await _context.FeaturedPackages
                .Where(f => f.IsActive)
                .Include(f => f.TourPackage)
                .ThenInclude(t => t!.Category)
                .Include(f => f.TourPackage)
                .ThenInclude(t => t!.Images)
                .OrderBy(f => f.DisplayOrder)
                .ToListAsync();
                
            ViewBag.FeaturedPackages = featured;

            var vehicles = await _context.Vehicles
                .Include(v => v.Images)
                .Where(v => v.Status != "Baja Operativa")
                .OrderByDescending(v => v.SeatCapacity)
                .Take(4)
                .ToListAsync();

            ViewBag.Vehicles = vehicles;

            var allTours = await _context.TourPackages
                .Where(t => t.IsActive)
                .OrderBy(t => t.Title)
                .Select(t => new { t.Id, t.Title, t.Slug, t.CategoryId })
                .ToListAsync();
            ViewBag.AllTours = allTours;

            var categories = await _context.Categories
                .Where(c => c.IsActive)
                .OrderBy(c => c.DisplayOrder)
                .ToListAsync();
            ViewBag.Categories = categories;

            // Destino Insignia de Portada (configurable desde Admin)
            var flagshipTour = await _context.TourPackages
                .Include(t => t.Images)
                .Include(t => t.Category)
                .Include(t => t.Shifts)
                .OrderBy(t => t.Title)
                .FirstOrDefaultAsync(t => t.IsFlagship && t.IsActive)
                ?? await _context.TourPackages
                    .Include(t => t.Images)
                    .Include(t => t.Category)
                    .Include(t => t.Shifts)
                    .OrderBy(t => t.Title)
                    .FirstOrDefaultAsync(t => t.IsActive);
            ViewBag.FlagshipTour = flagshipTour;

            // Testimonios y Comentarios de Experiencia (aprobados y seleccionados para portada)
            var homeReviews = await _context.TourReviews
                .Include(r => r.TourPackage)
                .Where(r => r.IsApproved && r.IsVisibleOnHome)
                .OrderByDescending(r => r.CreatedAt)
                .Take(6)
                .ToListAsync();
            ViewBag.HomeReviews = homeReviews;

            return View(tours);
        }

        [HttpGet]
        public async Task<IActionResult> Nosotros()
        {
            ViewBag.ActiveToursCount = await _context.TourPackages.CountAsync(t => t.IsActive);
            ViewBag.ActiveVehiclesCount = await _context.Vehicles.CountAsync(v => v.IsActive);
            
            var reviews = await _context.TourReviews
                .Include(r => r.TourPackage)
                .Where(r => r.IsApproved && r.IsVisibleOnHome)
                .OrderByDescending(r => r.CreatedAt)
                .Take(3)
                .ToListAsync();
            ViewBag.Reviews = reviews;

            var vehicles = await _context.Vehicles
                .Include(v => v.Images)
                .Where(v => v.IsActive)
                .OrderByDescending(v => v.SeatCapacity)
                .Take(2)
                .ToListAsync();
            ViewBag.Vehicles = vehicles;

            return View();
        }

        [HttpGet]
        public IActionResult Contacto()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Contacto(string nombre, string? apellidos, string email, string whatsapp, string? servicio, string? mensaje)
        {
            if (string.IsNullOrWhiteSpace(nombre) || string.IsNullOrWhiteSpace(whatsapp))
            {
                ViewBag.ErrorMessage = "Por favor completa tu nombre y número de WhatsApp para poder contactarte.";
                return View();
            }

            var fullName = string.IsNullOrWhiteSpace(apellidos) ? nombre.Trim() : $"{nombre.Trim()} {apellidos.Trim()}";
            var serviceSelected = string.IsNullOrWhiteSpace(servicio) ? "Consulta General" : servicio.Trim();
            var customerMsg = string.IsNullOrWhiteSpace(mensaje) ? "Sin mensaje adicional" : mensaje.Trim();
            var cleanPhone = new string(whatsapp.Where(char.IsDigit).ToArray());

            if (_emailService != null)
            {
                try
                {
                    // 1. Notificación al correo oficial de la agencia (Turismo Chilón)
                    var adminEmail = _contactService?.Email ?? "enriquearana1402@gmail.com";
                    var adminSubject = $"[Nuevo Contacto Web] {fullName} - {serviceSelected}";
                    var adminHtml = $@"
<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e2e8f0; border-radius: 12px; overflow: hidden;"">
    <div style=""background-color: #0b5345; color: white; padding: 20px; text-align: center;"">
        <h2 style=""margin: 0; font-size: 20px;"">Nuevo Mensaje de Contacto en Línea</h2>
        <p style=""margin: 5px 0 0; font-size: 13px; opacity: 0.9;"">Plataforma Web Turismo Chilón - SIGETOUR</p>
    </div>
    <div style=""padding: 24px; background-color: #ffffff; color: #1e293b; font-size: 14px; line-height: 1.6;"">
        <p style=""margin-top: 0;"">Un cliente potencial ha enviado un mensaje desde el formulario web de Turismo Chilón:</p>
        <table style=""width: 100%; border-collapse: collapse; margin: 16px 0;"">
            <tr>
                <td style=""padding: 8px 12px; background: #f8fafc; font-weight: bold; width: 35%; border-bottom: 1px solid #e2e8f0;"">Cliente:</td>
                <td style=""padding: 8px 12px; border-bottom: 1px solid #e2e8f0;"">{fullName}</td>
            </tr>
            <tr>
                <td style=""padding: 8px 12px; background: #f8fafc; font-weight: bold; border-bottom: 1px solid #e2e8f0;"">WhatsApp:</td>
                <td style=""padding: 8px 12px; border-bottom: 1px solid #e2e8f0;"">
                    <a href=""https://wa.me/51{cleanPhone}"" style=""color: #0b5345; font-weight: bold; text-decoration: underline;"">+{cleanPhone} (Abrir en WhatsApp)</a>
                </td>
            </tr>
            <tr>
                <td style=""padding: 8px 12px; background: #f8fafc; font-weight: bold; border-bottom: 1px solid #e2e8f0;"">Correo Electrónico:</td>
                <td style=""padding: 8px 12px; border-bottom: 1px solid #e2e8f0;"">
                    <a href=""mailto:{email}"" style=""color: #0b5345; text-decoration: underline;"">{email}</a>
                </td>
            </tr>
            <tr>
                <td style=""padding: 8px 12px; background: #f8fafc; font-weight: bold; border-bottom: 1px solid #e2e8f0;"">Servicio de Interés:</td>
                <td style=""padding: 8px 12px; border-bottom: 1px solid #e2e8f0;""><span style=""background: #ecfdf5; color: #065f46; padding: 2px 8px; border-radius: 4px; font-weight: bold;"">{serviceSelected}</span></td>
            </tr>
            <tr>
                <td style=""padding: 8px 12px; background: #f8fafc; font-weight: bold; border-bottom: 1px solid #e2e8f0;"">Fecha y Hora:</td>
                <td style=""padding: 8px 12px; border-bottom: 1px solid #e2e8f0;"">{DateTime.UtcNow.AddHours(-5):dd/MM/yyyy HH:mm} (Hora Perú)</td>
            </tr>
        </table>
        <div style=""background: #f1f5f9; padding: 14px; border-radius: 8px; margin-top: 16px;"">
            <strong style=""display: block; margin-bottom: 6px; color: #334155;"">Consulta o Mensaje:</strong>
            <p style=""margin: 0; white-space: pre-wrap; color: #475569;"">{customerMsg}</p>
        </div>
    </div>
    <div style=""background-color: #f8fafc; padding: 12px 24px; text-align: center; font-size: 12px; color: #64748b; border-top: 1px solid #e2e8f0;"">
        Turismo Chilón • Jr. 2 de Mayo 569, Cajamarca • WhatsApp: {_contactService?.WhatsAppFormatted ?? "+51 928 876 259"}
    </div>
</div>";

                    await _emailService.SendEmailAsync(adminEmail, adminSubject, adminHtml);

                    // 2. Acuse de recibo automático al correo del cliente (si es válido)
                    if (!string.IsNullOrWhiteSpace(email) && email.Contains("@"))
                    {
                        var clientSubject = "Hemos recibido tu consulta - Turismo Chilón Cajamarca";
                        var clientHtml = $@"
<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e2e8f0; border-radius: 12px; overflow: hidden;"">
    <div style=""background-color: #0b5345; color: white; padding: 20px; text-align: center;"">
        <h2 style=""margin: 0; font-size: 20px;"">¡Gracias por escribirnos!</h2>
        <p style=""margin: 5px 0 0; font-size: 13px; opacity: 0.9;"">Turismo Chilón Cajamarca</p>
    </div>
    <div style=""padding: 24px; background-color: #ffffff; color: #1e293b; font-size: 14px; line-height: 1.6;"">
        <p>Hola <strong>{fullName}</strong>,</p>
        <p>Hemos recibido tu consulta sobre <strong>{serviceSelected}</strong>. Nuestro equipo de atención turística en Cajamarca revisará tus requerimientos y se comunicará contigo al WhatsApp <strong>+{cleanPhone}</strong> o respondiendo a este correo a la brevedad.</p>
        <div style=""background: #f8fafc; border-left: 4px solid #0b5345; padding: 12px 16px; margin: 16px 0;"">
            <p style=""margin: 0; font-size: 13px; color: #475569;""><em>""{customerMsg}""</em></p>
        </div>
        <p style=""font-size: 13px; color: #64748b;"">Si necesitas atención inmediata para tu viaje o reserva de hoy/mañana, escríbenos directamente a nuestro WhatsApp oficial: <a href=""{_contactService?.GetWhatsAppUrl() ?? "https://wa.me/51928876259"}"" style=""color: #0b5345; font-weight: bold;"">{_contactService?.WhatsAppFormatted ?? "+51 928 876 259"}</a>.</p>
    </div>
    <div style=""background-color: #f8fafc; padding: 12px 24px; text-align: center; font-size: 12px; color: #64748b; border-top: 1px solid #e2e8f0;"">
        Turismo Chilón • Jr. 2 de Mayo 569, Cajamarca • Central Telefónica: {_contactService?.PhoneCallFormatted ?? "+51 944 746 364"}
    </div>
</div>";
                        await _emailService.SendEmailAsync(email, clientSubject, clientHtml);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error al enviar correo de contacto para {FullName}", fullName);
                }
            }

            TempData["SuccessMessage"] = $"¡Muchas gracias {nombre}! Tu mensaje ha sido recibido exitosamente. Un asesor de Turismo Chilón te responderá a la brevedad.";
            return RedirectToAction(nameof(Contacto));
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View();
        }
    }
}
