using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Moq;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;
using SIGETOUR.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace SIGETOUR.Tests.Payments
{
    [Collection("PostgresDatabaseCollection")]
    public class PostgresBookingCashConsistencyTests
    {
        private static readonly string ConnectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")
            ?? "Host=localhost;Database=sigetour_test_db;Username=postgres;Password=3nriqueA1402#";

        private readonly ITestOutputHelper _output;

        public PostgresBookingCashConsistencyTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private AppDbContext CreatePostgresDbContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(ConnectionString, npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .Options;

            return new AppDbContext(options);
        }

        private async Task<bool> IsPostgresAvailableAsync()
        {
            try
            {
                await using var db = CreatePostgresDbContext();
                return await db.Database.CanConnectAsync();
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[OMITIDA] Error conectando a PostgreSQL de pruebas: {ex.Message}");
                return false;
            }
        }

        private static AdminController CreateControllerWithTempData(AppDbContext context)
        {
            var controller = TestHelpers.CreateAdminController(context);
            var httpContext = new DefaultHttpContext();
            var tempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
            controller.TempData = tempData;
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        [Fact]
        public async Task Postgres_CreateBooking_AtomicPersistenceWithCashTransaction()
        {
            if (!await IsPostgresAvailableAsync())
            {
                _output.WriteLine("[OMITIDA] PostgreSQL de pruebas no disponible.");
                return;
            }

            await using var db = CreatePostgresDbContext();

            var shift = await db.CashRegisterShifts.FirstOrDefaultAsync(s => s.IsOpen);
            if (shift == null)
            {
                shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, OpenedAt = DateTime.UtcNow, ShiftNumber = "CJ-PG-B3-01" };
                db.CashRegisterShifts.Add(shift);
                await db.SaveChangesAsync();
            }

            var tour = await db.TourPackages.FirstOrDefaultAsync();
            if (tour == null)
            {
                tour = new TourPackage { Id = Guid.NewGuid(), Title = "Tour PG B3", Slug = "tour-pg-b3-" + Guid.NewGuid().ToString("N")[..6], BasePrice = 80m, IsActive = true };
                db.TourPackages.Add(tour);
                await db.SaveChangesAsync();
            }

            var controller = CreateControllerWithTempData(db);
            var dni = "73" + Random.Shared.Next(100000, 999999);

            var vm = new EditBookingViewModel
            {
                TourPackageId = tour.Id,
                CustomerName = "Postgres Client B3",
                CustomerDni = dni,
                CustomerPhone = "987654321",
                CustomerEmail = "pg_b3@sigetour.com",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                TotalPassengers = 1,
                TotalAmount = 80m,
                PaidAmount = 50m,
                PaymentMethod = "Efectivo",
                Status = BookingStatus.Confirmed
            };

            var res = await controller.CreateBooking(vm);
            Assert.IsType<RedirectToActionResult>(res);

            var savedBooking = await db.Bookings.FirstOrDefaultAsync(b => b.CustomerDni == dni);
            Assert.NotNull(savedBooking);
            Assert.Equal(50m, savedBooking.PaidAmount);

            var cashTx = await db.CashTransactions.FirstOrDefaultAsync(t => t.BookingId == savedBooking.Id);
            Assert.NotNull(cashTx);
            Assert.Equal(50m, cashTx.Amount);
            Assert.Equal("Ingreso", cashTx.TransactionType);

            // Cleanup
            db.CashTransactions.Remove(cashTx);
            db.BookingItems.RemoveRange(await db.BookingItems.Where(i => i.BookingId == savedBooking.Id).ToListAsync());
            db.Bookings.Remove(savedBooking);
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task Postgres_EditBooking_IncrementalPayment_CommittedTogetherInPostgres()
        {
            if (!await IsPostgresAvailableAsync())
            {
                _output.WriteLine("[OMITIDA] PostgreSQL de pruebas no disponible.");
                return;
            }

            await using var db = CreatePostgresDbContext();

            var shift = await db.CashRegisterShifts.FirstOrDefaultAsync(s => s.IsOpen);
            if (shift == null)
            {
                shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, OpenedAt = DateTime.UtcNow, ShiftNumber = "CJ-PG-B3-02" };
                db.CashRegisterShifts.Add(shift);
                await db.SaveChangesAsync();
            }

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour PG B3 Delta " + Guid.NewGuid().ToString("N")[..6],
                Slug = "tour-pg-b3-d-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = 100m,
                MaxCapacity = 50,
                IsActive = true
            };
            db.TourPackages.Add(tour);
            await db.SaveChangesAsync();

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-B3-" + Guid.NewGuid().ToString("N")[..6].ToUpper(),
                CustomerName = "PG Delta Client",
                CustomerDni = "74" + Random.Shared.Next(100000, 999999),
                CustomerEmail = "pg_delta@sigetour.com",
                TotalAmount = 100m,
                PaidAmount = 40m,
                Status = BookingStatus.Pending,
                TravelDate = DateTime.UtcNow.Date.AddDays(3)
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, TourPackageId = tour.Id, Passengers = 1, UnitPrice = 100m, SubTotal = 100m });
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();

            var controller = CreateControllerWithTempData(db);

            var editModel = new EditBookingViewModel
            {
                Id = booking.Id,
                TourPackageId = tour.Id,
                CustomerName = "PG Delta Client",
                CustomerDni = booking.CustomerDni,
                CustomerEmail = booking.CustomerEmail,
                TotalPassengers = 1,
                TotalAmount = 100m,
                PaidAmount = 90m, // Delta: +S/ 50.00
                PaymentMethod = "Efectivo",
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.Date.AddDays(3)
            };

            var res = await controller.EditBooking(editModel);
            Assert.IsType<RedirectToActionResult>(res);

            var updatedBooking = await db.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(90m, updatedBooking.PaidAmount);

            var deltaTx = await db.CashTransactions.FirstOrDefaultAsync(t => t.BookingId == booking.Id && t.Amount == 50m);
            Assert.NotNull(deltaTx);
            Assert.Equal("Abono de Saldo", deltaTx.Category);
            Assert.Equal("Ingreso", deltaTx.TransactionType);

            // Cleanup
            db.CashTransactions.Remove(deltaTx);
            db.BookingItems.RemoveRange(await db.BookingItems.Where(i => i.BookingId == booking.Id).ToListAsync());
            db.Bookings.Remove(updatedBooking);
            db.TourPackages.Remove(tour);
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task Postgres_RefundBookingPayment_PersistsEgresoAndAdjustsBookingAtomicallyInPostgres()
        {
            if (!await IsPostgresAvailableAsync())
            {
                _output.WriteLine("[OMITIDA] PostgreSQL de pruebas no disponible.");
                return;
            }

            await using var db = CreatePostgresDbContext();

            var shift = await db.CashRegisterShifts.FirstOrDefaultAsync(s => s.IsOpen);
            if (shift == null)
            {
                shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, OpenedAt = DateTime.UtcNow, ShiftNumber = "CJ-PG-B3-03" };
                db.CashRegisterShifts.Add(shift);
                await db.SaveChangesAsync();
            }

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-REF-" + Guid.NewGuid().ToString("N")[..6].ToUpper(),
                CustomerName = "PG Refund Client",
                CustomerDni = "75" + Random.Shared.Next(100000, 999999),
                CustomerEmail = "pg_refund@sigetour.com",
                TotalAmount = 150m,
                PaidAmount = 150m,
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.Date.AddDays(4)
            };
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();

            var controller = CreateControllerWithTempData(db);

            // Devolución explícita en PostgreSQL de S/ 50.00
            var res = await controller.RefundBookingPayment(booking.Id, 50m, "Cancelación parcial de viaje autorizada por gerencia", "Efectivo");
            Assert.IsType<RedirectToActionResult>(res);

            var updatedBooking = await db.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(100m, updatedBooking.PaidAmount);

            var egresoTx = await db.CashTransactions.FirstOrDefaultAsync(t => t.BookingId == booking.Id && t.TransactionType == "Egreso");
            Assert.NotNull(egresoTx);
            Assert.Equal(50m, egresoTx.Amount);
            Assert.Equal("Devolución de Reserva", egresoTx.Category);

            // Cleanup
            db.CashTransactions.Remove(egresoTx);
            db.Bookings.Remove(updatedBooking);
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task Postgres_DeleteCashTransaction_RejectedWhenLinkedToBooking_PreservesHistory()
        {
            if (!await IsPostgresAvailableAsync())
            {
                _output.WriteLine("[OMITIDA] PostgreSQL de pruebas no disponible.");
                return;
            }

            await using var db = CreatePostgresDbContext();

            var shift = await db.CashRegisterShifts.FirstOrDefaultAsync(s => s.IsOpen);
            if (shift == null)
            {
                shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, OpenedAt = DateTime.UtcNow, ShiftNumber = "CJ-PG-B3-04" };
                db.CashRegisterShifts.Add(shift);
                await db.SaveChangesAsync();
            }

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-DEL-" + Guid.NewGuid().ToString("N")[..6].ToUpper(),
                CustomerName = "PG Del Tx Client",
                CustomerDni = "76" + Random.Shared.Next(100000, 999999),
                TotalAmount = 90m,
                PaidAmount = 90m,
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.Date.AddDays(2)
            };
            db.Bookings.Add(booking);

            var tx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 90m,
                PaymentMethod = "Efectivo",
                BookingId = booking.Id,
                ReferenceCode = booking.ReferenceCode,
                Description = "Cobro de reserva"
            };
            db.CashTransactions.Add(tx);
            await db.SaveChangesAsync();

            var controller = CreateControllerWithTempData(db);

            // Intento de borrar el movimiento de caja en PostgreSQL
            var res = await controller.DeleteCashTransaction(tx.Id);
            Assert.IsType<RedirectToActionResult>(res);
            Assert.NotNull(controller.TempData["ErrorMessage"]);

            // El movimiento debe permanecer intacto en la base de datos PostgreSQL
            var persistedTx = await db.CashTransactions.FindAsync(tx.Id);
            Assert.NotNull(persistedTx);

            // Cleanup
            db.CashTransactions.Remove(persistedTx);
            db.Bookings.Remove(booking);
            await db.SaveChangesAsync();
        }
    }
}
