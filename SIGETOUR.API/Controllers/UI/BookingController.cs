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
        public async Task<IActionResult> Ticket(Guid id, string size = "80", [FromServices] IElectronicReceiptService? receiptService = null)
        {
            ElectronicReceipt? receipt = await _context.ElectronicReceipts
                .Include(r => r.Booking)
                .ThenInclude(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .FirstOrDefaultAsync(r => r.Id == id || r.BookingId == id);

            Booking? booking = receipt?.Booking;
            if (booking == null)
            {
                booking = await _context.Bookings
                    .Include(b => b.Items)
                    .ThenInclude(i => i.TourPackage)
                    .FirstOrDefaultAsync(b => b.Id == id);
            }

            if (booking == null) return NotFound("Comprobante o reserva no encontrada");

            // Si la reserva está confirmada y el comprobante no existe o quedó rechazado, intentar emitir/actualizar con NubeFact
            if (receiptService != null && booking.Status == BookingStatus.Confirmed && (receipt == null || receipt.Status == "Rejected" || receipt.Status == "Pending"))
            {
                try
                {
                    receipt = await receiptService.GenerateReceiptAsync(booking.Id);
                }
                catch { }
            }

            ViewBag.Booking = booking;
            ViewBag.TicketSize = (size == "58") ? "58" : "80";
            return View("Ticket", receipt);
        }

        [HttpPost]
        public async Task<IActionResult> RetryReceipt(Guid bookingId, [FromServices] IElectronicReceiptService receiptService)
        {
            await receiptService.GenerateReceiptAsync(bookingId);
            return RedirectToAction("Ticket", new { id = bookingId });
        }
    }
}
