using System;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;

namespace SIGETOUR.Tests.Helpers
{
    public static class TestDbContextFactory
    {
        public static AppDbContext Create(string? dbName = null)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: dbName ?? Guid.NewGuid().ToString())
                .Options;

            var context = new AppDbContext(options);
            context.Database.EnsureCreated();
            return context;
        }

        public static void SeedSampleData(AppDbContext context)
        {
            var cat = new Category
            {
                Id = Guid.NewGuid(),
                Name = "Aventura y Naturaleza",
                Description = "Tours de aventura",
                IsActive = true,
                DisplayOrder = 1
            };
            context.Categories.Add(cat);

            var vehicle = new Vehicle
            {
                Id = Guid.NewGuid(),
                LicensePlate = "ABC-123",
                Model = "Mercedes Sprinter",
                SeatCapacity = 15,
                IsActive = true,
                Status = "Operativo en Ruta (En Servicio)"
            };
            context.Vehicles.Add(vehicle);

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Cumbemayo Místico",
                Slug = "cumbemayo-mistico",
                BasePrice = 50,
                ChildPrice = 35,
                MaxCapacity = 15,
                Duration = "4 Horas",
                IsActive = true,
                CategoryId = cat.Id,
                DefaultVehicleId = vehicle.Id
            };
            context.TourPackages.Add(tour);

            var shift = new TourShift
            {
                Id = Guid.NewGuid(),
                TourPackageId = tour.Id,
                ShiftName = "Turno Mañana",
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(13, 0, 0),
                IsActive = true
            };
            context.TourShifts.Add(shift);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-TEST1",
                CustomerName = "Juan Perez",
                CustomerEmail = "juan@example.com",
                CustomerPhone = "999888777",
                CustomerDni = "12345678",
                TravelDate = DateTime.UtcNow.Date,
                ShiftName = "Turno Mañana",
                TotalPassengers = 2,
                TotalAmount = 100,
                PaidAmount = 100,
                Status = BookingStatus.Confirmed
            };
            var item = new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                Passengers = 2,
                UnitPrice = 50,
                SubTotal = 100,
                Booking = booking
            };
            booking.Items.Add(item);
            context.Bookings.Add(booking);
            context.BookingItems.Add(item);

            var cashShift = new CashRegisterShift
            {
                Id = Guid.NewGuid(),
                ShiftNumber = "TUR-001",
                OpenedByUserName = "cajero_test",
                OpenedAt = DateTime.UtcNow,
                InitialAmount = 200,
                IsOpen = true,
                Notes = "Turno de prueba"
            };
            context.CashRegisterShifts.Add(cashShift);

            var pettyCash = new PettyCashAccount
            {
                Id = Guid.NewGuid(),
                Name = "Caja Chica Principal",
                TotalFundAmount = 500,
                IsActive = true
            };
            context.PettyCashAccounts.Add(pettyCash);

            context.SaveChanges();
        }
    }
}
