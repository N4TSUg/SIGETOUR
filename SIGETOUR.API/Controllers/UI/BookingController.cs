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
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Application.Services;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;

namespace SIGETOUR.API.Controllers.UI
{
    public class BookingController : Controller
    {
        private readonly AppDbContext _context;
        private readonly CheckoutAccess _checkoutAccess;
        private readonly IConfiguration _configuration;

        public BookingController(
            AppDbContext context,
            CheckoutAccess? checkoutAccess = null,
            IConfiguration? configuration = null)
        {
            _context = context;
            _configuration = configuration ?? new ConfigurationBuilder().Build();
            _checkoutAccess = checkoutAccess ?? CreateDefaultCheckoutAccess(_configuration);
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

            var peruToday = DateTime.UtcNow.AddHours(-5).Date;
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

            // Validación de sobreventa / capacidad máxima (compatible con PostgreSQL timestamptz UTC)
            var travelDateUtc = DateTime.SpecifyKind(model.TravelDate.Date, DateTimeKind.Utc);
            var nextDayUtc = travelDateUtc.AddDays(1);

            var bookedPaxQuery = _context.BookingItems
                .Where(bi => bi.TourPackageId == tour.Id &&
                             bi.Booking.TravelDate >= travelDateUtc &&
                             bi.Booking.TravelDate < nextDayUtc &&
                             bi.Booking.Status != BookingStatus.Cancelled);

            if (!string.IsNullOrWhiteSpace(model.Shift))
            {
                bookedPaxQuery = bookedPaxQuery.Where(bi => bi.Booking.ShiftName == model.Shift);
            }

            var bookedPax = await bookedPaxQuery.SumAsync(bi => bi.Passengers);

            if (tour.MaxCapacity > 0 && bookedPax + model.Passengers > tour.MaxCapacity)
            {
                ModelState.AddModelError("", $"No hay cupos suficientes para la fecha y turno seleccionados. Capacidad máxima: {tour.MaxCapacity}, ocupados: {bookedPax}, solicitados: {model.Passengers}.");
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

            _context.Bookings.Add(booking);
            await _context.SaveChangesAsync();

            // Guardar en sesión y cookie de forma segura para permitir acceso inmediato a su voucher
            try { HttpContext?.Session?.SetString("LastBookingId", booking.Id.ToString()); } catch { }
            try
            {
                Response?.Cookies?.Append("sigetour_last_booking", booking.Id.ToString(), new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.AddHours(4)
                });
            }
            catch { }

            var checkoutToken = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(30));
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

            bool hasContext = HttpContext != null && User != null;
            bool isStaff = User?.Identity?.IsAuthenticated == true;
            bool isValidToken = !string.IsNullOrWhiteSpace(token) && _checkoutAccess.Verify(booking.Id, token);

            if (hasContext && !isStaff && !isValidToken)
            {
                // Bloque 3 Item 5: Rechazar tokens inválidos y no acuñar nuevos para anónimos no autorizados
                return Unauthorized("Acceso no autorizado al proceso de pago. Inicie su reserva desde el catálogo.");
            }

            if (string.IsNullOrWhiteSpace(token) || !_checkoutAccess.Verify(booking.Id, token))
            {
                token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(30));
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
        public async Task<IActionResult> ConfirmPayment(Guid bookingId, string? manualPaymentMethod = null, [FromServices] SIGETOUR.API.Application.Interfaces.IEmailService? emailService = null)
        {
            var booking = await _context.Bookings
                .Include(b => b.Items)
                    .ThenInclude(i => i.TourPackage)
                .Include(b => b.AssignedVehicle)
                .FirstOrDefaultAsync(b => b.Id == bookingId);
            if (booking == null) return NotFound();

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

            var voucherToken = _checkoutAccess.Create(booking.Id, TimeSpan.FromHours(2));
            try { HttpContext?.Session?.SetString("LastBookingId", booking.Id.ToString()); } catch { }
            try
            {
                Response?.Cookies?.Append("sigetour_last_booking", booking.Id.ToString(), new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.AddHours(4)
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
            
            bool hasContext = HttpContext != null && User != null;
            bool isStaff = User?.Identity?.IsAuthenticated == true;
            bool isValidToken = !string.IsNullOrWhiteSpace(token) && _checkoutAccess.Verify(booking.Id, token);

            string? sessionBookingId = null;
            try { sessionBookingId = HttpContext?.Session?.GetString("LastBookingId"); } catch { }

            string? cookieBookingId = null;
            try { Request?.Cookies.TryGetValue("sigetour_last_booking", out cookieBookingId); } catch { }

            bool isSessionOwner = !string.IsNullOrWhiteSpace(sessionBookingId) && sessionBookingId == booking.Id.ToString();
            bool isCookieOwner = !string.IsNullOrWhiteSpace(cookieBookingId) && cookieBookingId == booking.Id.ToString();

            if (hasContext && !isStaff && !isValidToken && !isSessionOwner && !isCookieOwner)
            {
                // Bloque 3 Item 6: Proteger Voucher con sesión o token válido
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

            bool isStaff = User?.Identity?.IsAuthenticated == true;
            bool isValidToken = !string.IsNullOrWhiteSpace(token) && _checkoutAccess.Verify(booking.Id, token);
            string? sessionBookingId = null;
            try { sessionBookingId = HttpContext?.Session?.GetString("LastBookingId"); } catch { }
            string? cookieBookingId = null;
            try { Request?.Cookies.TryGetValue("sigetour_last_booking", out cookieBookingId); } catch { }

            bool isOwner = isStaff || isValidToken || (sessionBookingId == booking.Id.ToString()) || (cookieBookingId == booking.Id.ToString());
            if (!isOwner) return Unauthorized(new { success = false, message = "No autorizado." });

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

            bool hasContext = HttpContext != null && User != null;
            bool isStaff = User?.Identity?.IsAuthenticated == true;
            bool isValidToken = !string.IsNullOrWhiteSpace(token) && _checkoutAccess.Verify(booking.Id, token);

            string? sessionBookingId = null;
            try { sessionBookingId = HttpContext?.Session?.GetString("LastBookingId"); } catch { }

            string? cookieBookingId = null;
            try { Request?.Cookies.TryGetValue("sigetour_last_booking", out cookieBookingId); } catch { }

            bool isSessionOwner = !string.IsNullOrWhiteSpace(sessionBookingId) && sessionBookingId == booking.Id.ToString();
            bool isCookieOwner = !string.IsNullOrWhiteSpace(cookieBookingId) && cookieBookingId == booking.Id.ToString();

            if (hasContext && !isStaff && !isValidToken && !isSessionOwner && !isCookieOwner)
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

            bool hasContext = HttpContext != null && User != null;
            bool isStaff = User?.Identity?.IsAuthenticated == true;
            bool isValidToken = !string.IsNullOrWhiteSpace(token) && _checkoutAccess.Verify(booking.Id, token);

            string? sessionBookingId = null;
            try { sessionBookingId = HttpContext?.Session?.GetString("LastBookingId"); } catch { }

            string? cookieBookingId = null;
            try { Request?.Cookies.TryGetValue("sigetour_last_booking", out cookieBookingId); } catch { }

            bool isSessionOwner = !string.IsNullOrWhiteSpace(sessionBookingId) && sessionBookingId == booking.Id.ToString();
            bool isCookieOwner = !string.IsNullOrWhiteSpace(cookieBookingId) && cookieBookingId == booking.Id.ToString();

            if (hasContext && !isStaff && !isValidToken && !isSessionOwner && !isCookieOwner)
            {
                return Unauthorized("Se requiere autorización o token válido.");
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
        public async Task<IActionResult> ShortVoucherPdf(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return NotFound("Código de reserva no válido.");

            var cleanCode = code.Trim().ToUpperInvariant();
            var booking = await _context.Bookings
                .Include(b => b.Items).ThenInclude(i => i.TourPackage).ThenInclude(t => t.Category)
                .Include(b => b.AssignedVehicle)
                .FirstOrDefaultAsync(b => b.ReferenceCode == cleanCode || b.ReferenceCode == $"RES-{cleanCode}");

            if (booking == null) return NotFound($"No se encontró la reserva con código {code}.");

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
        public async Task<IActionResult> ShortReceiptPdf(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return NotFound("Código de reserva no válido.");

            var cleanCode = code.Trim().ToUpperInvariant();
            var booking = await _context.Bookings
                .FirstOrDefaultAsync(b => b.ReferenceCode == cleanCode || b.ReferenceCode == $"RES-{cleanCode}");

            if (booking == null) return NotFound($"No se encontró la reserva con código {code}.");

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
        public async Task<IActionResult> ShortVoucherHtml(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return NotFound("Código de reserva no válido.");

            var cleanCode = code.Trim().ToUpperInvariant();
            var booking = await _context.Bookings
                .Include(b => b.Items).ThenInclude(i => i.TourPackage).ThenInclude(t => t.Category)
                .Include(b => b.AssignedVehicle)
                .FirstOrDefaultAsync(b => b.ReferenceCode == cleanCode || b.ReferenceCode == $"RES-{cleanCode}");

            if (booking == null) return NotFound($"No se encontró la reserva con código {code}.");

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromDays(30));
            return RedirectToAction("Voucher", new { id = booking.Id, token = token });
        }

        [HttpGet]
        public async Task<IActionResult> Ticket(Guid id, string size = "80", Guid? paymentId = null, string? token = null)
        {
            // Solo consultar y mostrar datos existentes (operación GET estrictamente idempotente y de solo lectura)
            // 1. Búsqueda por identificador exacto de comprobante (receiptId)
            ElectronicReceipt? receipt = await _context.ElectronicReceipts
                .Include(r => r.Booking)
                .ThenInclude(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .FirstOrDefaultAsync(r => r.Id == id);

            // 2. Búsqueda por identificador exacto de pago (paymentId o id como paymentId)
            if (receipt == null && paymentId.HasValue)
            {
                receipt = await _context.ElectronicReceipts
                    .Include(r => r.Booking)
                    .ThenInclude(b => b.Items)
                    .ThenInclude(i => i.TourPackage)
                    .FirstOrDefaultAsync(r => r.CulqiPaymentId == paymentId.Value);
            }

            if (receipt == null)
            {
                receipt = await _context.ElectronicReceipts
                    .Include(r => r.Booking)
                    .ThenInclude(b => b.Items)
                    .ThenInclude(i => i.TourPackage)
                    .FirstOrDefaultAsync(r => r.CulqiPaymentId == id);
            }

            // 3. Respaldo por BookingId si id coincide con una reserva (priorizando el comprobante más reciente)
            if (receipt == null)
            {
                receipt = await _context.ElectronicReceipts
                    .Include(r => r.Booking)
                    .ThenInclude(b => b.Items)
                    .ThenInclude(i => i.TourPackage)
                    .Where(r => r.BookingId == id)
                    .OrderByDescending(r => r.CreatedAtUtc)
                    .FirstOrDefaultAsync();
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

            bool hasContext = HttpContext != null && User != null;
            bool isStaff = User?.Identity?.IsAuthenticated == true;
            bool isValidToken = !string.IsNullOrWhiteSpace(token) && _checkoutAccess.Verify(booking.Id, token);

            string? sessionBookingId = null;
            try { sessionBookingId = HttpContext?.Session?.GetString("LastBookingId"); } catch { }

            string? cookieBookingId = null;
            try { Request?.Cookies.TryGetValue("sigetour_last_booking", out cookieBookingId); } catch { }

            bool isSessionOwner = !string.IsNullOrWhiteSpace(sessionBookingId) && sessionBookingId == booking.Id.ToString();
            bool isCookieOwner = !string.IsNullOrWhiteSpace(cookieBookingId) && cookieBookingId == booking.Id.ToString();

            if (hasContext && !isStaff && !isValidToken && !isSessionOwner && !isCookieOwner)
            {
                // Bloque 3 Item 6: Proteger Ticket con sesión autorizada o token
                return Unauthorized("Se requiere autorización o token válido para acceder al comprobante.");
            }

            ViewBag.Booking = booking;
            ViewBag.TicketSize = (size == "58") ? "58" : "80";
            return View("Ticket", receipt);
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin,Admin,Operador")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RetryReceipt(Guid? receiptId, Guid? bookingId, Guid? paymentId, [FromServices] IElectronicReceiptService receiptService)
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

                var retried = await receiptService.RetryReceiptAsync(receiptId.Value)
                    ?? await receiptService.GenerateReceiptAsync(existingReceipt.BookingId, existingReceipt.CulqiPaymentId);
                return RedirectToAction("Ticket", new { id = retried?.Id ?? receiptId.Value });
            }

            if (!bookingId.HasValue || bookingId.Value == Guid.Empty)
            {
                return BadRequest("Se requiere el identificador exacto del comprobante o de la reserva.");
            }

            var result = await receiptService.GenerateReceiptAsync(bookingId.Value, paymentId);
            return RedirectToAction("Ticket", new { id = result.Id });
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin,Admin,Operador")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QueryReceipt(Guid receiptId, [FromServices] IElectronicReceiptService receiptService)
        {
            // Acción explícita para consultar y conciliar el estado tributario directamente con NubeFact
            await receiptService.QueryReceiptFromProviderAsync(receiptId);
            return RedirectToAction("Ticket", new { id = receiptId });
        }
    }
}
