using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Application.Services;
using SIGETOUR.API.Core.Common;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Core.Security;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;

namespace SIGETOUR.API.Controllers.UI
{
    public class BookingController : Controller
    {
        private readonly AppDbContext _context;
        private readonly CheckoutAccess _checkoutAccess;
        private readonly IConfiguration _configuration;
        private readonly ICapacityValidationService _capacityService;
        private readonly ILogger<BookingController>? _logger;

        public BookingController(
            AppDbContext context,
            CheckoutAccess? checkoutAccess = null,
            IConfiguration? configuration = null,
            ICapacityValidationService? capacityService = null,
            ILogger<BookingController>? logger = null)
        {
            _context = context;
            _configuration = configuration ?? new ConfigurationBuilder().Build();
            _checkoutAccess = checkoutAccess ?? CreateDefaultCheckoutAccess(_configuration);
            _capacityService = capacityService ?? new CapacityValidationService(_context);
            _logger = logger;
        }

        private static CheckoutAccess CreateDefaultCheckoutAccess(IConfiguration config)
        {
            if (config["Payments:CheckoutSigningKey"] != null)
            {
                return new CheckoutAccess(config);
            }
            var inMemoryConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Payments:CheckoutSigningKey"] = "c2lnZXRvdXJfc3VwZXJfc2VjcmV0X2NoZWNrb3V0X3NpZ25pbmdfa2V5XzIwMjZfMzJieXRlcw=="
                })
                .Build();
            return new CheckoutAccess(inMemoryConfig);
        }

        private bool IsAuthorizedForBooking(Booking booking, string? token)
        {
            if (User?.Identity?.IsAuthenticated == true)
            {
                // El acceso de personal administrativo exige estrictamente permisos efectivos de consulta
                if (User.HasClaim("Permission", AppPermissions.BookingsView) ||
                    User.HasClaim("Permission", AppPermissions.ReceiptsView))
                {
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(token) && _checkoutAccess.Verify(booking.Id, token))
            {
                return true;
            }

            if (Request?.Cookies != null)
            {
                try
                {
                    if (Request.Cookies.TryGetValue("sigetour_last_booking", out var cookieToken) && !string.IsNullOrWhiteSpace(cookieToken))
                    {
                        if (_checkoutAccess.Verify(booking.Id, cookieToken))
                        {
                            return true;
                        }
                    }
                }
                catch { }
            }

            try
            {
                var sessionBookingId = HttpContext?.Session?.GetString("LastBookingId");
                if (!string.IsNullOrWhiteSpace(sessionBookingId) && sessionBookingId == booking.Id.ToString())
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        [HttpGet]
        public async Task<IActionResult> Checkout(Guid tourId)
        {
            var tour = await _context.TourPackages
                .Include(t => t.Shifts)
                .FirstOrDefaultAsync(t => t.Id == tourId);
            if (tour == null || !tour.IsActive) return RedirectToAction("Catalog", "Tour");

            var featured = await _context.FeaturedPackages
                .OrderBy(f => f.DisplayOrder)
                .FirstOrDefaultAsync(f => f.TourPackageId == tourId && f.IsActive);
            var effectivePrice = (featured != null && featured.PromoPrice.HasValue) ? featured.PromoPrice.Value : tour.BasePrice;
            ViewBag.EffectiveUnitPrice = effectivePrice;

            return View(tour);
        }

        [HttpPost]
        public async Task<IActionResult> Checkout(CheckoutRequestViewModel model)
        {
            var tour = await _context.TourPackages.Include(t => t.Shifts).FirstOrDefaultAsync(t => t.Id == model.TourId);
            if (tour == null) return RedirectToAction("Catalog", "Tour");

            var featured = await _context.FeaturedPackages
                .OrderBy(f => f.DisplayOrder)
                .FirstOrDefaultAsync(f => f.TourPackageId == model.TourId && f.IsActive);
            var effectiveUnitPrice = (featured != null && featured.PromoPrice.HasValue) ? featured.PromoPrice.Value : tour.BasePrice;
            ViewBag.EffectiveUnitPrice = effectiveUnitPrice;

            // Validaciones server-side: tour activo, fecha no pasada, turno activo y capacidad máxima
            if (!tour.IsActive)
            {
                ModelState.AddModelError("", "El circuito turístico seleccionado no se encuentra activo para reservas.");
                return View(tour);
            }

            var peruToday = PeruTime.Today;
            if (model.TravelDate.Date < peruToday)
            {
                ModelState.AddModelError("TravelDate", "La fecha del viaje no puede ser una fecha en el pasado.");
                return View(tour);
            }

            if (tour.Shifts.Any() && !tour.Shifts.Any(s => s.ShiftName == model.Shift && s.IsActive))
            {
                ModelState.AddModelError("Shift", "El turno seleccionado no se encuentra activo.");
                return View(tour);
            }

            var capacityCheck = await _capacityService.ValidateCapacityAsync(
                tour.Id, model.TravelDate, model.Shift, model.Passengers, null);

            if (!capacityCheck.IsAvailable)
            {
                ModelState.AddModelError("", capacityCheck.ErrorMessage ?? "No hay cupos suficientes para la fecha y turno seleccionados.");
                return View(tour);
            }

            if (!ModelState.IsValid)
            {
                return View(tour);
            }

            decimal pickupCost = 0;
            string pickupLocation = "Agencia Central";

            if (model.BoardingPoint == "hotel")
            {
                pickupLocation = model.HotelName + " - " + model.HotelAddress;
                // Cost calculation: Free if Historic Center OR Passengers > 6. Otherwise S/ 15.
                if (model.HotelZone != "centro" && model.Passengers <= 6)
                {
                    pickupCost = 15.00m;
                }
            }

            var extraPassengers = new List<object>();
            if (model.PassengerNames != null && model.PassengerNames.Any())
            {
                for (int i = 0; i < model.PassengerNames.Count; i++)
                {
                    var pName = model.PassengerNames[i]?.Trim() ?? "";
                    var pDni = (model.PassengerDnis != null && i < model.PassengerDnis.Count) ? model.PassengerDnis[i]?.Trim() ?? "" : "";
                    var pPhone = (model.PassengerPhones != null && i < model.PassengerPhones.Count) ? model.PassengerPhones[i]?.Trim() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(pName) || !string.IsNullOrWhiteSpace(pDni))
                    {
                        extraPassengers.Add(new { Name = pName, Dni = pDni, Phone = pPhone });
                    }
                }
            }

            string refCode;
            do
            {
                refCode = $"RES-{Random.Shared.Next(1000, 9999)}";
            } while (await _context.Bookings.AnyAsync(b => b.ReferenceCode == refCode));

            var booking = new Booking
            {
                ReferenceCode = refCode,
                CustomerName = model.CustomerName,
                CustomerDni = model.CustomerDni,
                CustomerEmail = model.CustomerEmail,
                CustomerPhone = model.CustomerPhone,
                TravelDate = DateTime.SpecifyKind(model.TravelDate.Date, DateTimeKind.Utc),
                ShiftName = model.Shift,
                TotalPassengers = model.Passengers,
                TotalAmount = (effectiveUnitPrice * model.Passengers) + pickupCost,
                PickupLocation = pickupLocation,
                PickupCost = pickupCost,
                Status = BookingStatus.Pending,
                AdditionalPassengersJson = JsonSerializer.Serialize(extraPassengers)
            };

            booking.Items.Add(new BookingItem
            {
                TourPackageId = tour.Id,
                Passengers = model.Passengers,
                UnitPrice = effectiveUnitPrice, // Precio unitario contratado inmutable en el ítem
                SubTotal = effectiveUnitPrice * model.Passengers
            });

            if (_context.Database.IsRelational())
            {
                await using var dbTx = await _context.Database.BeginTransactionAsync();
                try
                {
                    var finalCapacity = await _capacityService.ValidateCapacityAsync(
                        tour.Id, model.TravelDate, model.Shift, model.Passengers, null);

                    if (!finalCapacity.IsAvailable)
                    {
                        ModelState.AddModelError("", finalCapacity.ErrorMessage ?? "No hay cupos suficientes para la fecha y turno seleccionados.");
                        return View(tour);
                    }

                    _context.Bookings.Add(booking);
                    await _context.SaveChangesAsync();
                    await dbTx.CommitAsync();
                }
                catch
                {
                    await dbTx.RollbackAsync();
                    throw;
                }
            }
            else
            {
                _context.Bookings.Add(booking);
                await _context.SaveChangesAsync();
            }

            // Guardar en sesión y cookie de forma segura para permitir acceso inmediato a su voucher
            var checkoutToken = _checkoutAccess.Create(booking.Id, CheckoutAccess.DefaultCheckoutTtl);
            try { HttpContext?.Session?.SetString("LastBookingId", booking.Id.ToString()); } catch { }
            try
            {
                Response?.Cookies?.Append("sigetour_last_booking", checkoutToken, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.Add(CheckoutAccess.DefaultCheckoutTtl)
                });
            }
            catch { }

            return RedirectToAction("Payment", new { id = booking.Id, token = checkoutToken });
        }

        [HttpGet]
        public async Task<IActionResult> Payment(Guid id, string? token = null)
        {
            var booking = await _context.Bookings
                .Include(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .FirstOrDefaultAsync(b => b.Id == id);
            if (booking == null) return RedirectToAction("Catalog", "Tour");

            if (!IsAuthorizedForBooking(booking, token))
            {
                // Bloque 1: Rechazar anónimos o manipulaciones sin autorización
                return Unauthorized("Acceso no autorizado al proceso de pago. Inicie su reserva desde el catálogo.");
            }

            if (string.IsNullOrWhiteSpace(token) || !_checkoutAccess.Verify(booking.Id, token))
            {
                token = _checkoutAccess.Create(booking.Id, CheckoutAccess.DefaultCheckoutTtl);
            }

            ViewBag.CheckoutToken = token;
            ViewBag.CulqiPublicKey = _configuration["Culqi:PublicKey"] ?? "";

            var advancePercentage = booking.Items.FirstOrDefault()?.TourPackage?.RequiredAdvancePercentage > 0
                ? booking.Items.First().TourPackage!.RequiredAdvancePercentage
                : 50;

            var advanceRequired = booking.Items.Sum(i => i.SubTotal * (i.TourPackage?.RequiredAdvancePercentage > 0 ? i.TourPackage.RequiredAdvancePercentage : advancePercentage) / 100m);
            var balance = Math.Max(0m, booking.TotalAmount - booking.PaidAmount);
            var advanceAmount = Math.Min(balance, Math.Max(0m, advanceRequired - booking.PaidAmount));

            ViewBag.AdvancePercentage = advancePercentage;
            ViewBag.AdvanceAmount = advanceAmount > 0 ? advanceAmount : decimal.Round(booking.TotalAmount / 2m, 2);
            ViewBag.BalanceAmount = balance;

            return View(booking);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmPayment(Guid bookingId, string? manualPaymentMethod = null, string? token = null, [FromServices] SIGETOUR.API.Application.Interfaces.IEmailService? emailService = null)
        {
            var booking = await _context.Bookings
                .Include(b => b.Items)
                    .ThenInclude(i => i.TourPackage)
                .Include(b => b.AssignedVehicle)
                .FirstOrDefaultAsync(b => b.Id == bookingId);
            if (booking == null) return NotFound();

            if (!IsAuthorizedForBooking(booking, token))
            {
                return Unauthorized("Se requiere autorización o token válido para confirmar esta reserva.");
            }

            // Medida de seguridad: los pagos manuales (Counter / Transferencia bancaria)
            // NO confirman el pago ni inflan la caja automáticamente.
            // La reserva queda en estado Pendiente y con PaidAmount = 0 (o el monto real previamente acreditado).
            // La conciliación e ingreso a caja se realizan exclusivamente cuando el cajero/administrador
            // valida el comprobante o recibe el dinero físico en AdminController.
            if (booking.Status == BookingStatus.Pending && booking.PaidAmount == 0)
            {
                booking.PaymentMethod = string.IsNullOrWhiteSpace(manualPaymentMethod)
                    ? "Pago en Counter / Agencia"
                    : manualPaymentMethod;
                await _context.SaveChangesAsync();
            }

            if (emailService != null && !string.IsNullOrWhiteSpace(booking.CustomerEmail) && !booking.CustomerEmail.EndsWith("@agencia.com", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var baseUrl = (Request != null && Request.Host.HasValue)
                ? $"{Request.Scheme}://{Request.Host}"
                : "https://localhost:7103";
                    await emailService.SendBookingConfirmationEmailAsync(booking, baseUrl);
                }
                catch { }
            }

            var voucherToken = _checkoutAccess.Create(booking.Id, CheckoutAccess.DefaultDocumentTtl);
            try { HttpContext?.Session?.SetString("LastBookingId", booking.Id.ToString()); } catch { }
            try
            {
                Response?.Cookies?.Append("sigetour_last_booking", voucherToken, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.Add(CheckoutAccess.DefaultDocumentTtl)
                });
            }
            catch { }

            return RedirectToAction("Voucher", new { id = bookingId, token = voucherToken });
        }

        public async Task<IActionResult> Voucher(Guid id, string? token = null)
        {
            var booking = await _context.Bookings
                .Include(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .ThenInclude(t => t.Category)
                .Include(b => b.AssignedVehicle)
                .FirstOrDefaultAsync(b => b.Id == id);
                
            if (booking == null) return RedirectToAction("Catalog", "Tour");
            
            if (!IsAuthorizedForBooking(booking, token))
            {
                return Unauthorized("Se requiere autorización o token válido para acceder al boleto de viaje.");
            }

            var receipt = await _context.ElectronicReceipts
                .Where(r => r.BookingId == id)
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefaultAsync();

            ViewBag.Receipt = receipt;
            return View(booking);
        }

        [HttpPost("Booking/ResendConfirmationEmail/{id}")]
        public async Task<IActionResult> ResendConfirmationEmail(Guid id, [FromServices] SIGETOUR.API.Application.Interfaces.IEmailService emailService, string? token = null)
        {
            var booking = await _context.Bookings
                .Include(b => b.Items).ThenInclude(i => i.TourPackage)
                .Include(b => b.AssignedVehicle)
                .FirstOrDefaultAsync(b => b.Id == id);

            if (booking == null) return NotFound(new { success = false, message = "Reserva no encontrada." });

            if (!IsAuthorizedForBooking(booking, token))
            {
                return Unauthorized(new { success = false, message = "No autorizado." });
            }

            if (string.IsNullOrWhiteSpace(booking.CustomerEmail))
            {
                return BadRequest(new { success = false, message = "La reserva no tiene un correo electrónico registrado." });
            }

            var baseUrl = (Request != null && Request.Host.HasValue)
                ? $"{Request.Scheme}://{Request.Host}"
                : "https://localhost:7103";
            var result = await emailService.SendBookingConfirmationEmailAsync(booking, baseUrl);

            return Json(new { success = result.Success, message = result.Message, email = booking.CustomerEmail });
        }

        [HttpGet]
        public async Task<IActionResult> DownloadVoucherPdf(Guid id, string? token = null)
        {
            var booking = await _context.Bookings
                .Include(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .ThenInclude(t => t.Category)
                .Include(b => b.AssignedVehicle)
                .FirstOrDefaultAsync(b => b.Id == id);

            if (booking == null) return NotFound("Reserva no encontrada.");

            if (!IsAuthorizedForBooking(booking, token))
            {
                return Unauthorized("Se requiere autorización o token válido para acceder al boleto de viaje.");
            }

            var receipt = await _context.ElectronicReceipts
                .Where(r => r.BookingId == id)
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefaultAsync();

            var tour = booking.Items?.FirstOrDefault()?.TourPackage;
            var receiptLabel = receipt != null ? $"{receipt.Series}-{receipt.Number}" : null;

            var pdfBytes = VoucherPdfGenerator.GenerateVoucherPdf(booking, tour, receiptLabel);
            return File(pdfBytes, "application/pdf", $"Boleto_Oficial_{booking.ReferenceCode}.pdf");
        }

        [HttpGet]
        public async Task<IActionResult> DownloadReceiptPdf(Guid id, string? token = null)
        {
            var booking = await _context.Bookings.FirstOrDefaultAsync(b => b.Id == id);
            if (booking == null) return NotFound("Reserva no encontrada.");

            if (!IsAuthorizedForBooking(booking, token))
            {
                return Unauthorized("Se requiere autorización o token válido para acceder al comprobante.");
            }

            var receipt = await _context.ElectronicReceipts
                .Where(r => r.BookingId == id)
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefaultAsync();

            if (receipt == null || string.IsNullOrWhiteSpace(receipt.PdfUrl))
            {
                return NotFound("El comprobante electrónico aún no cuenta con archivo PDF disponible.");
            }

            if (receipt.PdfUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var bytes = await httpClient.GetByteArrayAsync(receipt.PdfUrl);
                    return File(bytes, "application/pdf", $"Boleta_{receipt.Series}-{receipt.Number}.pdf");
                }
                catch
                {
                    return Redirect(receipt.PdfUrl);
                }
            }

            return Redirect(receipt.PdfUrl);
        }

        [HttpGet("b/{code}")]
        public async Task<IActionResult> ShortVoucherPdf(string code, string? token = null)
        {
            if (string.IsNullOrWhiteSpace(code)) return NotFound("Código de reserva no válido.");

            var cleanCode = code.Trim().ToUpperInvariant();
            var booking = await _context.Bookings
                .Include(b => b.Items).ThenInclude(i => i.TourPackage).ThenInclude(t => t.Category)
                .Include(b => b.AssignedVehicle)
                .FirstOrDefaultAsync(b => b.ReferenceCode == cleanCode || b.ReferenceCode == $"RES-{cleanCode}");

            if (booking == null) return NotFound($"No se encontró la reserva con código {code}.");

            if (!IsAuthorizedForBooking(booking, token))
            {
                return Unauthorized("Se requiere autorización de personal o un enlace firmado válido para acceder al boleto.");
            }

            var receipt = await _context.ElectronicReceipts
                .Where(r => r.BookingId == booking.Id)
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefaultAsync();

            var tour = booking.Items?.FirstOrDefault()?.TourPackage;
            var receiptLabel = receipt != null ? $"{receipt.Series}-{receipt.Number}" : null;

            var pdfBytes = VoucherPdfGenerator.GenerateVoucherPdf(booking, tour, receiptLabel);
            Response.Headers["Content-Disposition"] = $"inline; filename=Boleto_Oficial_{booking.ReferenceCode}.pdf";
            return File(pdfBytes, "application/pdf");
        }

        [HttpGet("b/{code}/boleta")]
        public async Task<IActionResult> ShortReceiptPdf(string code, string? token = null)
        {
            if (string.IsNullOrWhiteSpace(code)) return NotFound("Código de reserva no válido.");

            var cleanCode = code.Trim().ToUpperInvariant();
            var booking = await _context.Bookings
                .FirstOrDefaultAsync(b => b.ReferenceCode == cleanCode || b.ReferenceCode == $"RES-{cleanCode}");

            if (booking == null) return NotFound($"No se encontró la reserva con código {code}.");

            if (!IsAuthorizedForBooking(booking, token))
            {
                return Unauthorized("Se requiere autorización de personal o un enlace firmado válido para acceder al comprobante.");
            }

            var receipt = await _context.ElectronicReceipts
                .Where(r => r.BookingId == booking.Id)
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefaultAsync();

            if (receipt == null) return NotFound("Esta reserva aún no tiene un comprobante electrónico emitido.");

            if (!string.IsNullOrWhiteSpace(receipt.PdfUrl) && receipt.PdfUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var bytes = await httpClient.GetByteArrayAsync(receipt.PdfUrl);
                    Response.Headers["Content-Disposition"] = $"inline; filename=Boleta_{receipt.Series}-{receipt.Number}.pdf";
                    return File(bytes, "application/pdf");
                }
                catch
                {
                    return Redirect(receipt.PdfUrl);
                }
            }

            if (!string.IsNullOrWhiteSpace(receipt.PdfUrl))
            {
                return Redirect(receipt.PdfUrl);
            }

            return NotFound("El comprobante electrónico aún no cuenta con archivo PDF disponible.");
        }

        [HttpGet("b/{code}/ver")]
        public async Task<IActionResult> ShortVoucherHtml(string code, string? token = null)
        {
            if (string.IsNullOrWhiteSpace(code)) return NotFound("Código de reserva no válido.");

            var cleanCode = code.Trim().ToUpperInvariant();
            var booking = await _context.Bookings
                .Include(b => b.Items).ThenInclude(i => i.TourPackage).ThenInclude(t => t.Category)
                .Include(b => b.AssignedVehicle)
                .FirstOrDefaultAsync(b => b.ReferenceCode == cleanCode || b.ReferenceCode == $"RES-{cleanCode}");

            if (booking == null) return NotFound($"No se encontró la reserva con código {code}.");

            if (!IsAuthorizedForBooking(booking, token))
            {
                return Unauthorized("Se requiere autorización de personal o un enlace firmado válido para acceder al boleto.");
            }

            var redirectToken = (!string.IsNullOrWhiteSpace(token) && _checkoutAccess.Verify(booking.Id, token))
                ? token
                : _checkoutAccess.Create(booking.Id, CheckoutAccess.DefaultDocumentTtl);

            return RedirectToAction("Voucher", new { id = booking.Id, token = redirectToken });
        }

        private static ReceiptResolutionResult EvaluateCandidates(
            IEnumerable<ElectronicReceipt> candidates,
            string conflictMessage)
        {
            // Regla: Deduplicar candidatos por ElectronicReceipt.Id
            // Un mismo documento que coincide por varios campos representa un solo candidato
            var distinctCandidates = candidates
                .GroupBy(r => r.Id)
                .Select(g => g.First())
                .OrderByDescending(r => r.CreatedAtUtc)
                .ToList();

            if (distinctCandidates.Count == 0)
            {
                return ReceiptResolutionResult.None();
            }

            if (distinctCandidates.Count == 1)
            {
                return ReceiptResolutionResult.Exact(distinctCandidates[0]);
            }

            // Dos documentos con IDs diferentes no deben resolverse mediante prioridad, fecha, estado o FirstOrDefault
            return ReceiptResolutionResult.Conflicted(distinctCandidates, conflictMessage);
        }

        private async Task<ReceiptResolutionResult> ResolveReceiptForCulqiOperationAsync(
            Guid bookingId,
            Guid culqiPaymentId,
            Guid? cashTransactionId = null)
        {
            var culqiKey = $"CULQI:{culqiPaymentId:N}";
            var cashKey = cashTransactionId.HasValue ? $"CASH:{cashTransactionId.Value:N}" : null;

            var candidates = await _context.ElectronicReceipts
                .Include(r => r.Booking)
                .ThenInclude(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .Where(r => r.BookingId == bookingId &&
                    (r.CulqiPaymentId == culqiPaymentId ||
                     r.BillableOperationKey == culqiKey ||
                     (cashTransactionId.HasValue && r.CashTransactionId == cashTransactionId.Value) ||
                     (cashKey != null && r.BillableOperationKey == cashKey)))
                .ToListAsync();

            return EvaluateCandidates(candidates, "Se encontraron múltiples comprobantes fiscales asociados a esta operación de pago Culqi.");
        }

        private async Task<ReceiptResolutionResult> ResolveReceiptForManualCashOperationAsync(
            Guid bookingId,
            Guid cashTransactionId)
        {
            var cashKey = $"CASH:{cashTransactionId:N}";

            var candidates = await _context.ElectronicReceipts
                .Include(r => r.Booking)
                .ThenInclude(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .Where(r => r.BookingId == bookingId &&
                    (r.CashTransactionId == cashTransactionId ||
                     r.BillableOperationKey == cashKey))
                .ToListAsync();

            return EvaluateCandidates(candidates, "Se encontraron múltiples comprobantes fiscales asociados a este movimiento de caja.");
        }

        [HttpGet]
        public async Task<IActionResult> Ticket(Guid id, string size = "80", Guid? paymentId = null, string? token = null, Guid? cashTransactionId = null, Guid? receiptId = null)
        {
            // Solo consultar y mostrar datos existentes (operación GET estrictamente idempotente y de solo lectura)
            
            // 1. Identificar comprobante exacto si 'receiptId' fue provisto o si 'id' coincide con un comprobante existente
            ElectronicReceipt? receipt = null;
            Guid? effectiveReceiptId = receiptId.HasValue && receiptId.Value != Guid.Empty ? receiptId.Value : null;

            if (effectiveReceiptId.HasValue)
            {
                receipt = await _context.ElectronicReceipts
                    .Include(r => r.Booking)
                    .ThenInclude(b => b.Items)
                    .ThenInclude(i => i.TourPackage)
                    .FirstOrDefaultAsync(r => r.Id == effectiveReceiptId.Value);

                if (receipt == null) return NotFound("El comprobante especificado no existe.");
            }
            else
            {
                // Comprobar si 'id' es un ReceiptId directo
                receipt = await _context.ElectronicReceipts
                    .Include(r => r.Booking)
                    .ThenInclude(b => b.Items)
                    .ThenInclude(i => i.TourPackage)
                    .FirstOrDefaultAsync(r => r.Id == id);
            }

            Booking? booking = receipt?.Booking;
            if (booking == null)
            {
                booking = await _context.Bookings
                    .Include(b => b.Items)
                    .ThenInclude(i => i.TourPackage)
                    .FirstOrDefaultAsync(b => b.Id == id);
            }

            if (booking == null) return NotFound("Comprobante o reserva no encontrada");

            // Si se especificó receiptId explícito y la ruta id era un bookingId, validar pertenencia
            if (effectiveReceiptId.HasValue && id != effectiveReceiptId.Value && receipt != null && receipt.BookingId != id)
            {
                return BadRequest("El comprobante especificado no pertenece a la reserva solicitada.");
            }

            bool isTokenAuthorized = !string.IsNullOrWhiteSpace(token) && _checkoutAccess.Verify(booking.Id, token);

            if (!IsAuthorizedForBooking(booking, token))
            {
                return Unauthorized("Se requiere autorización o token válido para acceder al comprobante.");
            }

            // Si se abrió directamente un comprobante existente, asociar sus identificadores de operación
            if (receipt != null)
            {
                if (receipt.CashTransactionId.HasValue)
                {
                    ViewBag.SelectedCashTransactionId = receipt.CashTransactionId.Value;
                }
                if (receipt.CulqiPaymentId.HasValue)
                {
                    ViewBag.PaymentId = receipt.CulqiPaymentId.Value;
                }
            }

            // 2. Si se solicitó un cashTransactionId específico:
            if (cashTransactionId.HasValue && cashTransactionId.Value != Guid.Empty)
            {
                var cashTx = await _context.CashTransactions.FirstOrDefaultAsync(t => t.Id == cashTransactionId.Value);
                if (cashTx == null)
                {
                    return NotFound("El movimiento de caja especificado no existe.");
                }
                if (cashTx.BookingId != booking.Id)
                {
                    return BadRequest("El movimiento de caja no pertenece a la reserva especificada.");
                }

                // Requisito 1.d: Si el movimiento está vinculado a Culqi, resolver su comprobante canónico por CulqiPaymentId, CashTransactionId o BillableOperationKey
                ReceiptResolutionResult resolution;
                if (cashTx.CulqiPaymentId.HasValue)
                {
                    ViewBag.PaymentId = cashTx.CulqiPaymentId.Value;
                    resolution = await ResolveReceiptForCulqiOperationAsync(booking.Id, cashTx.CulqiPaymentId.Value, cashTx.Id);
                }
                else
                {
                    resolution = await ResolveReceiptForManualCashOperationAsync(booking.Id, cashTx.Id);
                }

                if (resolution.IsConflict)
                {
                    receipt = null;
                    Response.StatusCode = StatusCodes.Status409Conflict;
                    ViewBag.IsConflict = true;
                    ViewBag.ConflictResolution = resolution;
                    ViewBag.ConflictMessage = resolution.ConflictMessage;
                }
                else if (resolution.IsExact)
                {
                    receipt = resolution.Receipt;
                }
                else
                {
                    receipt = null;
                }

                // Si no existe comprobante para este movimiento, receipt queda null (no sustituir por el último de la reserva)
                ViewBag.SelectedCashTransactionId = cashTx.Id;
                ViewBag.SelectedOperationAmount = cashTx.Amount;
                ViewBag.SelectedPaymentMethod = cashTx.PaymentMethod ?? booking.PaymentMethod;
                ViewBag.SelectedOperationNumber = cashTx.ReferenceCode ?? booking.OperationNumber;
                ViewBag.SelectedOperationDescription = $"{cashTx.Category ?? "Cobro"} - S/ {cashTx.Amount:N2}";
                ViewBag.SelectedOperationDate = cashTx.CreatedAt;
            }
            // 3. Si se solicitó un paymentId (Culqi) específico:
            else if (paymentId.HasValue && paymentId.Value != Guid.Empty)
            {
                var culqi = await _context.CulqiPayments.FirstOrDefaultAsync(p => p.Id == paymentId.Value);
                if (culqi == null)
                {
                    return NotFound("El pago Culqi especificado no existe.");
                }
                if (culqi.BookingId != booking.Id)
                {
                    return BadRequest("El pago Culqi no pertenece a la reserva especificada.");
                }

                var linkedCash = await _context.CashTransactions.FirstOrDefaultAsync(t => t.CulqiPaymentId == culqi.Id);
                if (linkedCash != null)
                {
                    ViewBag.SelectedCashTransactionId = linkedCash.Id;
                }

                var resolution = await ResolveReceiptForCulqiOperationAsync(booking.Id, culqi.Id, linkedCash?.Id);
                if (resolution.IsConflict)
                {
                    receipt = null;
                    Response.StatusCode = StatusCodes.Status409Conflict;
                    ViewBag.IsConflict = true;
                    ViewBag.ConflictResolution = resolution;
                    ViewBag.ConflictMessage = resolution.ConflictMessage;
                }
                else if (resolution.IsExact)
                {
                    receipt = resolution.Receipt;
                }
                else
                {
                    receipt = null;
                }

                ViewBag.PaymentId = culqi.Id;
                ViewBag.SelectedOperationAmount = culqi.AmountCents / 100m;
                ViewBag.SelectedPaymentMethod = "Culqi";
                ViewBag.SelectedOperationNumber = culqi.ProviderId ?? booking.OperationNumber;
                ViewBag.SelectedOperationDescription = $"Pago Culqi - S/ {(culqi.AmountCents / 100m):N2}";
                ViewBag.SelectedOperationDate = culqi.CreatedAtUtc;
            }
            // 4. Si se accedió por reserva sin operación seleccionada (y 'id' no era un receiptId):
            else if (receipt == null)
            {
                var bookingReceipts = await _context.ElectronicReceipts
                    .Include(r => r.Booking)
                    .ThenInclude(b => b.Items)
                    .ThenInclude(i => i.TourPackage)
                    .Where(r => r.BookingId == booking.Id)
                    .OrderByDescending(r => r.CreatedAtUtc)
                    .ToListAsync();

                var allIncomeCash = await _context.CashTransactions
                    .Where(t => t.BookingId == booking.Id && t.TransactionType == "Ingreso")
                    .OrderBy(t => t.CreatedAt)
                    .ToListAsync();

                var allCulqi = await _context.CulqiPayments
                    .Where(p => p.BookingId == booking.Id && p.Status == "paid")
                    .OrderBy(p => p.CreatedAtUtc)
                    .ToListAsync();

                // Normalización de operaciones (Hallazgo 3: Un pago Culqi y su movimiento representan la misma operación)
                var associatedReceiptIds = new HashSet<Guid>();
                var normalizedOps = new List<BookingOperationOptionViewModel>();

                // A. Resolver pagos Culqi canónicos (reuniendo IDs de allCulqi y de movimientos de caja vinculados)
                var culqiIds = new HashSet<Guid>(allCulqi.Select(p => p.Id));
                foreach (var txWithCulqi in allIncomeCash.Where(t => t.CulqiPaymentId.HasValue))
                {
                    culqiIds.Add(txWithCulqi.CulqiPaymentId!.Value);
                }

                foreach (var cId in culqiIds)
                {
                    var culqiPayment = allCulqi.FirstOrDefault(p => p.Id == cId);
                    var linkedCashTx = allIncomeCash.FirstOrDefault(t => t.CulqiPaymentId == cId);

                    var culqiKey = $"CULQI:{cId:N}";
                    var cashKey = linkedCashTx != null ? $"CASH:{linkedCashTx.Id:N}" : null;

                    var candidates = bookingReceipts.Where(r =>
                        r.CulqiPaymentId == cId ||
                        r.BillableOperationKey == culqiKey ||
                        (linkedCashTx != null && (r.CashTransactionId == linkedCashTx.Id || r.BillableOperationKey == cashKey)));

                    var resolution = EvaluateCandidates(candidates, "Se encontraron múltiples comprobantes fiscales asociados a esta operación de pago Culqi.");

                    if (resolution.IsExact)
                    {
                        associatedReceiptIds.Add(resolution.Receipt!.Id);
                    }
                    else if (resolution.IsConflict)
                    {
                        foreach (var c in resolution.Candidates)
                        {
                            associatedReceiptIds.Add(c.Id);
                        }
                    }

                    decimal amount = culqiPayment != null
                        ? culqiPayment.AmountCents / 100m
                        : (linkedCashTx?.Amount ?? 0m);

                    DateTime date = culqiPayment?.CreatedAtUtc ?? linkedCashTx?.CreatedAt ?? DateTime.UtcNow;
                    string refCode = culqiPayment?.ProviderId ?? linkedCashTx?.ReferenceCode ?? "---";

                    normalizedOps.Add(new BookingOperationOptionViewModel
                    {
                        Type = resolution.IsExact ? "Receipt" : (resolution.IsConflict ? "Conflict" : "CulqiPayment"),
                        ReceiptId = resolution.Receipt?.Id,
                        CulqiPaymentId = cId,
                        CashTransactionId = linkedCashTx?.Id,
                        Title = resolution.IsExact
                            ? $"{resolution.Receipt!.ReceiptType} {resolution.Receipt.FullNumber}"
                            : (resolution.IsConflict ? "Pago Culqi (Conflicto de Comprobantes)" : "Pago Culqi (Confirmado)"),
                        Amount = resolution.Receipt?.TotalAmount ?? amount,
                        Status = resolution.IsExact
                            ? resolution.Receipt!.Status
                            : (resolution.IsConflict ? "En Conflicto (Requiere Revisión)" : "Sin Comprobante"),
                        IsBilled = resolution.IsExact,
                        IsConflict = resolution.IsConflict,
                        ConflictCandidateCount = resolution.Candidates.Count,
                        Date = resolution.Receipt?.IssuedAtUtc ?? date,
                        PaymentMethod = resolution.IsExact
                            ? (resolution.Receipt!.CustomerDocType == "6" ? "Factura" : "Boleta")
                            : "Culqi",
                        ReferenceCode = resolution.Receipt?.FullNumber ?? refCode
                    });
                }

                // B. Resolver movimientos de caja manuales independientes (CulqiPaymentId == null)
                foreach (var manualTx in allIncomeCash.Where(t => !t.CulqiPaymentId.HasValue))
                {
                    var cashKey = $"CASH:{manualTx.Id:N}";
                    var candidates = bookingReceipts.Where(r =>
                        r.CashTransactionId == manualTx.Id ||
                        r.BillableOperationKey == cashKey);

                    var resolution = EvaluateCandidates(candidates, "Se encontraron múltiples comprobantes fiscales asociados a este movimiento de caja.");

                    if (resolution.IsExact)
                    {
                        associatedReceiptIds.Add(resolution.Receipt!.Id);
                    }
                    else if (resolution.IsConflict)
                    {
                        foreach (var c in resolution.Candidates)
                        {
                            associatedReceiptIds.Add(c.Id);
                        }
                    }

                    normalizedOps.Add(new BookingOperationOptionViewModel
                    {
                        Type = resolution.IsExact ? "Receipt" : (resolution.IsConflict ? "Conflict" : "CashTransaction"),
                        ReceiptId = resolution.Receipt?.Id,
                        CashTransactionId = manualTx.Id,
                        CulqiPaymentId = null,
                        Title = resolution.IsExact
                            ? $"{resolution.Receipt!.ReceiptType} {resolution.Receipt.FullNumber}"
                            : (resolution.IsConflict ? $"Movimiento de Caja ({manualTx.Category ?? "Cobro"}) [Conflicto]" : $"Movimiento de Caja ({manualTx.Category ?? "Cobro"})"),
                        Amount = resolution.Receipt?.TotalAmount ?? manualTx.Amount,
                        Status = resolution.IsExact
                            ? resolution.Receipt!.Status
                            : (resolution.IsConflict ? "En Conflicto (Requiere Revisión)" : "Sin Comprobante"),
                        IsBilled = resolution.IsExact,
                        IsConflict = resolution.IsConflict,
                        ConflictCandidateCount = resolution.Candidates.Count,
                        Date = resolution.Receipt?.IssuedAtUtc ?? manualTx.CreatedAt,
                        PaymentMethod = resolution.IsExact
                            ? (resolution.Receipt!.CustomerDocType == "6" ? "Factura" : "Boleta")
                            : (manualTx.PaymentMethod ?? "Efectivo"),
                        ReferenceCode = resolution.Receipt?.FullNumber ?? manualTx.ReferenceCode
                    });
                }

                // C. Conservar comprobantes históricos no asociados como documentos consultables
                foreach (var unlinkedReceipt in bookingReceipts.Where(r => !associatedReceiptIds.Contains(r.Id)))
                {
                    normalizedOps.Add(new BookingOperationOptionViewModel
                    {
                        Type = "Receipt",
                        ReceiptId = unlinkedReceipt.Id,
                        CulqiPaymentId = unlinkedReceipt.CulqiPaymentId,
                        CashTransactionId = unlinkedReceipt.CashTransactionId,
                        Title = $"{unlinkedReceipt.ReceiptType} {unlinkedReceipt.FullNumber}",
                        Amount = unlinkedReceipt.TotalAmount,
                        Status = unlinkedReceipt.Status,
                        IsBilled = true,
                        Date = unlinkedReceipt.IssuedAtUtc ?? unlinkedReceipt.CreatedAtUtc,
                        PaymentMethod = unlinkedReceipt.CustomerDocType == "6" ? "Factura" : "Boleta",
                        ReferenceCode = unlinkedReceipt.FullNumber
                    });
                }

                int totalOps = normalizedOps.Count;

                if (totalOps <= 1)
                {
                    if (totalOps == 1)
                    {
                        var singleOp = normalizedOps[0];
                        if (singleOp.IsConflict)
                        {
                            receipt = null;
                            Response.StatusCode = StatusCodes.Status409Conflict;
                            ViewBag.IsConflict = true;
                            ViewBag.SelectedCashTransactionId = singleOp.CashTransactionId;
                            ViewBag.PaymentId = singleOp.CulqiPaymentId;
                            ViewBag.SelectedOperationAmount = singleOp.Amount;
                            ViewBag.SelectedPaymentMethod = singleOp.PaymentMethod;
                            ViewBag.SelectedOperationNumber = singleOp.ReferenceCode;
                            ViewBag.SelectedOperationDescription = singleOp.Title + $" - S/ {singleOp.Amount:N2}";
                            ViewBag.SelectedOperationDate = singleOp.Date;

                            if (singleOp.CulqiPaymentId.HasValue)
                            {
                                ViewBag.ConflictResolution = await ResolveReceiptForCulqiOperationAsync(booking.Id, singleOp.CulqiPaymentId.Value, singleOp.CashTransactionId);
                            }
                            else if (singleOp.CashTransactionId.HasValue)
                            {
                                ViewBag.ConflictResolution = await ResolveReceiptForManualCashOperationAsync(booking.Id, singleOp.CashTransactionId.Value);
                            }
                            ViewBag.ConflictMessage = (ViewBag.ConflictResolution as ReceiptResolutionResult)?.ConflictMessage ?? "Se requiere revisión administrativa: se detectaron múltiples comprobantes asociados a esta operación.";
                        }
                        else if (singleOp.IsBilled && singleOp.ReceiptId.HasValue)
                        {
                            receipt = bookingReceipts.FirstOrDefault(r => r.Id == singleOp.ReceiptId.Value);
                            ViewBag.SelectedCashTransactionId = singleOp.CashTransactionId;
                            ViewBag.PaymentId = singleOp.CulqiPaymentId;
                        }
                        else
                        {
                            receipt = null;
                            ViewBag.SelectedCashTransactionId = singleOp.CashTransactionId;
                            ViewBag.PaymentId = singleOp.CulqiPaymentId;
                            ViewBag.SelectedOperationAmount = singleOp.Amount;
                            ViewBag.SelectedPaymentMethod = singleOp.PaymentMethod;
                            ViewBag.SelectedOperationNumber = singleOp.ReferenceCode;
                            ViewBag.SelectedOperationDescription = singleOp.Title + $" - S/ {singleOp.Amount:N2}";
                            ViewBag.SelectedOperationDate = singleOp.Date;
                        }
                    }
                }
                else
                {
                    // Requisito 1.f: No elegir arbitrariamente; presentar opciones de selección normalizadas
                    receipt = null;
                    ViewBag.AvailableOperations = normalizedOps;
                }
            }

            var navContext = new TicketNavigationContext
            {
                RouteId = id,
                BookingId = booking.Id,
                ReceiptId = receipt?.Id ?? effectiveReceiptId,
                CashTransactionId = ViewBag.SelectedCashTransactionId as Guid?,
                PaymentId = ViewBag.PaymentId as Guid?,
                Size = (size == "58") ? "58" : "80",
                Token = token,
                IsTokenAuthorized = isTokenAuthorized
            };

            ViewBag.NavigationContext = navContext;
            ViewBag.AccessToken = isTokenAuthorized ? token : null;
            ViewBag.Booking = booking;
            ViewBag.TicketSize = (size == "58") ? "58" : "80";
            return View("Ticket", receipt);
        }

        [HttpPost]
        [Authorize(Policy = AppPermissions.ReceiptsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RetryReceipt(
            Guid? receiptId,
            Guid? bookingId,
            Guid? paymentId,
            [FromServices] IElectronicReceiptService receiptService,
            Guid? cashTransactionId = null)
        {
            try
            {
                // Bloque 2 Item 9: Trabajar con el identificador exacto del comprobante y rechazar identificadores incompatibles
                if (receiptId.HasValue && receiptId.Value != Guid.Empty)
                {
                    var existingReceipt = await _context.ElectronicReceipts.FindAsync(receiptId.Value);
                    if (existingReceipt == null)
                    {
                        return NotFound("El comprobante especificado no existe.");
                    }

                    if (bookingId.HasValue && bookingId.Value != existingReceipt.BookingId)
                    {
                        return BadRequest("El identificador de reserva no coincide con el comprobante.");
                    }
                    if (paymentId.HasValue && existingReceipt.CulqiPaymentId.HasValue && paymentId.Value != existingReceipt.CulqiPaymentId.Value)
                    {
                        return BadRequest("El identificador de pago no coincide con el comprobante.");
                    }
                    if (cashTransactionId.HasValue && existingReceipt.CashTransactionId.HasValue && cashTransactionId.Value != existingReceipt.CashTransactionId.Value)
                    {
                        return BadRequest("El identificador de transacción de caja no coincide con el comprobante.");
                    }

                    var retried = await receiptService.RetryReceiptAsync(receiptId.Value)
                        ?? (existingReceipt.CashTransactionId.HasValue
                            ? await receiptService.GenerateReceiptAsync(
                                existingReceipt.BookingId,
                                existingReceipt.CulqiPaymentId,
                                exactReceiptId: existingReceipt.Id,
                                claimToken: null,
                                cashTransactionId: existingReceipt.CashTransactionId,
                                billableOperationKey: existingReceipt.BillableOperationKey)
                            : await receiptService.GenerateReceiptAsync(existingReceipt.BookingId, existingReceipt.CulqiPaymentId));
                    return RedirectToAction("Ticket", new { id = retried?.Id ?? receiptId.Value });
                }

                if (!bookingId.HasValue || bookingId.Value == Guid.Empty)
                {
                    return BadRequest("Se requiere el identificador exacto del comprobante o de la reserva.");
                }

                var booking = await _context.Bookings.FindAsync(bookingId.Value);
                if (booking == null)
                {
                    return NotFound("La reserva especificada no existe.");
                }

                // Validación estricta de cashTransactionId antes de llamar al servicio
                if (cashTransactionId.HasValue && cashTransactionId.Value != Guid.Empty)
                {
                    var cashTx = await _context.CashTransactions.FindAsync(cashTransactionId.Value);
                    if (cashTx == null)
                    {
                        return NotFound("El movimiento de caja especificado no existe.");
                    }
                    if (cashTx.BookingId != bookingId.Value)
                    {
                        return BadRequest("El movimiento de caja no pertenece a la reserva especificada.");
                    }
                }

                // Validación estricta de paymentId antes de llamar al servicio
                if (paymentId.HasValue && paymentId.Value != Guid.Empty)
                {
                    var culqi = await _context.CulqiPayments.FindAsync(paymentId.Value);
                    if (culqi == null)
                    {
                        return NotFound("El pago Culqi especificado no existe.");
                    }
                    if (culqi.BookingId != bookingId.Value)
                    {
                        return BadRequest("El pago Culqi no pertenece a la reserva especificada.");
                    }
                }

                // Validación contra asociación ambigua / conflicto de identidad antes de llamadas externas
                ReceiptResolutionResult? conflictCheck = null;
                if (cashTransactionId.HasValue && cashTransactionId.Value != Guid.Empty)
                {
                    var cashTx = await _context.CashTransactions.FindAsync(cashTransactionId.Value);
                    if (cashTx != null)
                    {
                        if (cashTx.CulqiPaymentId.HasValue)
                        {
                            conflictCheck = await ResolveReceiptForCulqiOperationAsync(bookingId.Value, cashTx.CulqiPaymentId.Value, cashTx.Id);
                        }
                        else
                        {
                            conflictCheck = await ResolveReceiptForManualCashOperationAsync(bookingId.Value, cashTx.Id);
                        }
                    }
                }
                else if (paymentId.HasValue && paymentId.Value != Guid.Empty)
                {
                    var linkedCash = await _context.CashTransactions.FirstOrDefaultAsync(t => t.CulqiPaymentId == paymentId.Value && t.BookingId == bookingId.Value);
                    conflictCheck = await ResolveReceiptForCulqiOperationAsync(bookingId.Value, paymentId.Value, linkedCash?.Id);
                }
                else
                {
                    var allCulqi = await _context.CulqiPayments
                        .Where(p => p.BookingId == bookingId.Value && p.Status == "paid")
                        .ToListAsync();
                    var allManualCash = await _context.CashTransactions
                        .Where(t => t.BookingId == bookingId.Value && t.TransactionType == "Ingreso" && t.CulqiPaymentId == null)
                        .ToListAsync();

                    if (allCulqi.Count == 1 && allManualCash.Count == 0)
                    {
                        var culqi = allCulqi[0];
                        var linkedCash = await _context.CashTransactions.FirstOrDefaultAsync(t => t.CulqiPaymentId == culqi.Id && t.BookingId == bookingId.Value);
                        conflictCheck = await ResolveReceiptForCulqiOperationAsync(bookingId.Value, culqi.Id, linkedCash?.Id);
                    }
                    else if (allManualCash.Count == 1 && allCulqi.Count == 0)
                    {
                        conflictCheck = await ResolveReceiptForManualCashOperationAsync(bookingId.Value, allManualCash[0].Id);
                    }
                }

                if (conflictCheck != null && conflictCheck.IsConflict)
                {
                    return StatusCode(StatusCodes.Status409Conflict, "Conflicto: la operación seleccionada presenta múltiples comprobantes asociados (conflicto de identidad) y requiere revisión administrativa antes de cualquier emisión.");
                }

                ElectronicReceipt result;
                if (cashTransactionId.HasValue)
                {
                    result = await receiptService.GenerateReceiptAsync(
                        bookingId.Value,
                        culqiPaymentId: paymentId,
                        exactReceiptId: null,
                        claimToken: null,
                        cashTransactionId: cashTransactionId);
                }
                else
                {
                    result = await receiptService.GenerateReceiptAsync(bookingId.Value, paymentId);
                }

                return RedirectToAction("Ticket", new { id = result.Id });
            }
            catch (InvalidOperationException ex)
            {
                // Error esperado de validación de negocio (ej. reserva con múltiples cobros sin seleccionar o conflicto)
                _logger?.LogWarning(ex, "Validación de facturación rechazada para reserva {BookingId}: {Message}", bookingId, ex.Message);
                if (ex.Message.Contains("Conflicto de identidad") || ex.Message.Contains("múltiples comprobantes asociados"))
                {
                    return StatusCode(StatusCodes.Status409Conflict, ex.Message);
                }
                if (TempData != null)
                {
                    TempData["ErrorMessage"] = ex.Message;
                }
                return RedirectToAction("Ticket", new { id = bookingId, cashTransactionId = cashTransactionId, paymentId = paymentId });
            }
        }

        [HttpPost]
        [Authorize(Policy = AppPermissions.ReceiptsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QueryReceipt(Guid receiptId, [FromServices] IElectronicReceiptService receiptService)
        {
            // Acción explícita para consultar y conciliar el estado tributario directamente con NubeFact
            await receiptService.QueryReceiptFromProviderAsync(receiptId);
            return RedirectToAction("Ticket", new { id = receiptId });
        }
    }
}
