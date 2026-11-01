using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
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
            if (tour == null) return RedirectToAction("Catalog", "Tour");

            var featured = await _context.FeaturedPackages.FirstOrDefaultAsync(f => f.TourPackageId == tourId);
            if (featured != null && featured.PromoPrice.HasValue)
            {
                tour.BasePrice = featured.PromoPrice.Value;
            }

            return View(tour);
        }

        [HttpPost]
        public async Task<IActionResult> Checkout(CheckoutRequestViewModel model)
        {
            var tour = await _context.TourPackages.Include(t => t.Shifts).FirstOrDefaultAsync(t => t.Id == model.TourId);
            if (tour == null) return RedirectToAction("Catalog", "Tour");

            var featured = await _context.FeaturedPackages.FirstOrDefaultAsync(f => f.TourPackageId == model.TourId);
            if (featured != null && featured.PromoPrice.HasValue)
            {
                tour.BasePrice = featured.PromoPrice.Value;
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

            var booking = new Booking
            {
                ReferenceCode = "RES-" + new Random().Next(1000, 9999),
                CustomerName = model.CustomerName,
                CustomerDni = model.CustomerDni,
                CustomerEmail = model.CustomerEmail,
                CustomerPhone = model.CustomerPhone,
                TravelDate = DateTime.SpecifyKind(model.TravelDate, DateTimeKind.Utc),
                ShiftName = model.Shift,
                TotalPassengers = model.Passengers,
                TotalAmount = (tour.BasePrice * model.Passengers) + pickupCost,
                PickupLocation = pickupLocation,
                PickupCost = pickupCost,
                Status = BookingStatus.Pending,
                AdditionalPassengersJson = JsonSerializer.Serialize(extraPassengers)
            };

            booking.Items.Add(new BookingItem
            {
                TourPackageId = tour.Id,
                Passengers = model.Passengers,
                UnitPrice = tour.BasePrice,
                SubTotal = tour.BasePrice * model.Passengers
            });

            _context.Bookings.Add(booking);
            await _context.SaveChangesAsync();

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
        public async Task<IActionResult> ConfirmPayment(Guid bookingId, string? manualPaymentMethod = null)
        {
            var booking = await _context.Bookings.FindAsync(bookingId);
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

            return RedirectToAction("Voucher", new { id = bookingId });
        }

        public async Task<IActionResult> Voucher(Guid id)
        {
            var booking = await _context.Bookings
                .Include(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .ThenInclude(t => t.Category)
                .Include(b => b.AssignedVehicle)
                .FirstOrDefaultAsync(b => b.Id == id);
                
            if (booking == null) return RedirectToAction("Catalog", "Tour");
            
            var receipt = await _context.ElectronicReceipts
                .Where(r => r.BookingId == id)
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefaultAsync();

            ViewBag.Receipt = receipt;
            return View(booking);
        }

        [HttpGet]
        public async Task<IActionResult> Ticket(Guid id, string size = "80", Guid? paymentId = null)
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

            ViewBag.Booking = booking;
            ViewBag.TicketSize = (size == "58") ? "58" : "80";
            return View("Ticket", receipt);
        }

        [HttpPost]
        public async Task<IActionResult> RetryReceipt(Guid? receiptId, Guid? bookingId, Guid? paymentId, [FromServices] IElectronicReceiptService receiptService)
        {
            // Acción explícita para reintentar emisión ante fallo o rechazo trabajando con el identificador exacto
            ElectronicReceipt? existingReceipt = null;
            if (receiptId.HasValue && receiptId.Value != Guid.Empty)
            {
                existingReceipt = await _context.ElectronicReceipts.FindAsync(receiptId.Value);
            }

            var targetBookingId = existingReceipt?.BookingId ?? bookingId ?? Guid.Empty;
            var targetPaymentId = existingReceipt?.CulqiPaymentId ?? paymentId;

            if (targetBookingId == Guid.Empty)
            {
                return BadRequest("Se requiere el identificador de la reserva o del comprobante.");
            }

            var result = await receiptService.GenerateReceiptAsync(targetBookingId, targetPaymentId);
            return RedirectToAction("Ticket", new { id = result.Id });
        }

        [HttpPost]
        public async Task<IActionResult> QueryReceipt(Guid receiptId, [FromServices] IElectronicReceiptService receiptService)
        {
            // Acción explícita para consultar y conciliar el estado tributario directamente con NubeFact
            await receiptService.QueryReceiptFromProviderAsync(receiptId);
            return RedirectToAction("Ticket", new { id = receiptId });
        }
    }
}
