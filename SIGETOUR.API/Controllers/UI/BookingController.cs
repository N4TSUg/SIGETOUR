using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;

namespace SIGETOUR.API.Controllers.UI
{
    public class BookingController : Controller
    {
        private readonly AppDbContext _context;

        public BookingController(AppDbContext context)
        {
            _context = context;
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

            return RedirectToAction("Payment", new { id = booking.Id });
        }

        [HttpGet]
        public async Task<IActionResult> Payment(Guid id)
        {
            var booking = await _context.Bookings
                .Include(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .FirstOrDefaultAsync(b => b.Id == id);
            if (booking == null) return RedirectToAction("Catalog", "Tour");
            return View(booking);
        }

        [HttpPost]
        public async Task<IActionResult> ConfirmPayment(Guid bookingId)
        {
            var booking = await _context.Bookings.FindAsync(bookingId);
            if (booking != null)
            {
                booking.Status = BookingStatus.Confirmed;
                booking.PaidAmount = booking.TotalAmount;
                if (string.IsNullOrWhiteSpace(booking.PaymentMethod))
                {
                    booking.PaymentMethod = "Tarjeta / Pasarela Online";
                }

                var activeShift = await _context.CashRegisterShifts.FirstOrDefaultAsync(s => s.IsOpen);
                if (activeShift != null && !_context.CashTransactions.Any(t => t.BookingId == booking.Id))
                {
                    _context.CashTransactions.Add(new CashTransaction
                    {
                        Id = Guid.NewGuid(),
                        ShiftId = activeShift.Id,
                        TransactionType = "Ingreso",
                        Category = "Cobro de Reserva",
                        Amount = booking.TotalAmount,
                        PaymentMethod = booking.PaymentMethod,
                        ReferenceCode = booking.ReferenceCode,
                        BookingId = booking.Id,
                        Description = $"Pago en línea confirmado para reserva {booking.ReferenceCode} - {booking.CustomerName}",
                        RegisteredBy = "Pasarela Online / Checkout",
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await _context.SaveChangesAsync();
            }

            return RedirectToAction("Voucher", new { id = bookingId });
        }

        public async Task<IActionResult> Voucher(Guid id)
        {
            var booking = await _context.Bookings
                .Include(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .FirstOrDefaultAsync(b => b.Id == id);
                
            if (booking == null) return RedirectToAction("Catalog", "Tour");
            
            return View(booking);
        }
    }
}
