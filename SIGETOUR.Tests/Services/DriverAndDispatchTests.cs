using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using SIGETOUR.API.Application.Services;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Models;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Services
{
    public class DriverAndDispatchTests
    {
        [Fact]
        public void Booking_CreatedWithoutAssignedVehicleOrDriver_InitialStateIsNull()
        {
            // Arrange & Act: Un cliente reserva o se crea una reserva
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                CustomerName = "Mariana Gutierrez",
                CustomerDni = "71829304",
                TotalPassengers = 2,
                TotalAmount = 70.00m,
                TravelDate = DateTime.UtcNow.Date,
                ShiftName = "Turno Mañana"
            };

            // Assert: La reserva NO debe tener vehículo ni chofer enlazado al crearse
            Assert.Null(booking.AssignedVehicleId);
            Assert.Null(booking.AssignedVehicle);
            Assert.Null(booking.AssignedDriverId);
            Assert.Null(booking.AssignedDriver);
        }

        [Fact]
        public void Driver_CanBeCreated_AndLinkedToVehicle()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var vehicle = new Vehicle
            {
                Id = Guid.NewGuid(),
                LicensePlate = "T7C-841",
                Model = "Mercedes Sprinter",
                SeatCapacity = 19,
                IsActive = true
            };
            context.Vehicles.Add(vehicle);
            context.SaveChanges();

            // Act: Crear conductor y vincular unidad habitual
            var driver = new Driver
            {
                Id = Guid.NewGuid(),
                FullName = "Carlos Mendoza Silva",
                Dni = "42910834",
                LicenseNumber = "Q-42910834",
                LicenseCategory = "A-IIIa (Especial Transporte Turístico)",
                Phone = "976123456",
                IsActive = true,
                AssignedVehicleId = vehicle.Id
            };
            context.Drivers.Add(driver);
            context.SaveChanges();

            // Assert
            var saved = context.Drivers.Find(driver.Id);
            Assert.NotNull(saved);
            Assert.Equal("Carlos Mendoza Silva", saved.FullName);
            Assert.Equal(vehicle.Id, saved.AssignedVehicleId);
        }

        [Fact]
        public async Task ManifestPreview_WhenUnderUtilized_SuggestsSmallerVehicle_ToAvoidLosses()
        {
            // Arrange: 2 vehículos disponibles (14 plazas y 20 plazas). Hay 8 pasajeros.
            using var context = TestDbContextFactory.Create();
            var smallVan = new Vehicle
            {
                Id = Guid.NewGuid(),
                LicensePlate = "T7B-952",
                Model = "Toyota HiAce Van",
                SeatCapacity = 14,
                IsActive = true
            };
            var largeBus = new Vehicle
            {
                Id = Guid.NewGuid(),
                LicensePlate = "T9A-102",
                Model = "Bus Panorámico",
                SeatCapacity = 28,
                IsActive = true
            };
            context.Vehicles.AddRange(smallVan, largeBus);

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Cumbemayo Místico",
                IsActive = true,
                DefaultVehicleId = largeBus.Id
            };
            context.TourPackages.Add(tour);

            var bookingDate = DateTime.UtcNow.Date;
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                CustomerName = "Grupo Turistas",
                TotalPassengers = 8,
                TotalAmount = 280,
                TravelDate = bookingDate,
                ShiftName = "Turno Mañana",
                Status = BookingStatus.Confirmed
            };
            booking.Items.Add(new BookingItem
            {
                TourPackageId = tour.Id,
                Passengers = 8,
                UnitPrice = 35,
                SubTotal = 280,
                Booking = booking
            });
            context.Bookings.Add(booking);
            context.SaveChanges();

            var service = new ManifestExportService(context);

            // Act: Vista previa para el bus grande de 28 plazas teniendo solo 8 pasajeros
            var preview = await service.GetManifestPreviewAsync(bookingDate, tour.Id, largeBus.Id, "Turno Mañana");

            // Assert: Debe advertir subutilización y sugerir la van de 14 plazas para evitar pérdidas operativas
            Assert.NotNull(preview);
            Assert.True(preview.IsUnderUtilized);
            Assert.False(preview.IsOverCapacity);
            Assert.Contains("Optimización de costos", preview.OptimizationAdvice);
            Assert.Equal(smallVan.Id, preview.SuggestedVehicleId);
        }

        [Fact]
        public async Task ManifestPreview_WhenOverCapacity_WarnsAndSuggestsLargerVehicle()
        {
            // Arrange: 2 vehículos (14 y 20 plazas). Hay 16 pasajeros.
            using var context = TestDbContextFactory.Create();
            var smallVan = new Vehicle
            {
                Id = Guid.NewGuid(),
                LicensePlate = "T7B-952",
                Model = "Toyota HiAce Van",
                SeatCapacity = 14,
                IsActive = true
            };
            var mediumVan = new Vehicle
            {
                Id = Guid.NewGuid(),
                LicensePlate = "T7C-841",
                Model = "Mercedes Sprinter",
                SeatCapacity = 19,
                IsActive = true
            };
            context.Vehicles.AddRange(smallVan, mediumVan);

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Baños del Inca VIP",
                IsActive = true,
                DefaultVehicleId = smallVan.Id
            };
            context.TourPackages.Add(tour);

            var bookingDate = DateTime.UtcNow.Date;
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                CustomerName = "Delegación Trujillo",
                TotalPassengers = 16,
                TotalAmount = 560,
                TravelDate = bookingDate,
                ShiftName = "Turno Mañana",
                Status = BookingStatus.Confirmed
            };
            booking.Items.Add(new BookingItem
            {
                TourPackageId = tour.Id,
                Passengers = 16,
                UnitPrice = 35,
                SubTotal = 560,
                Booking = booking
            });
            context.Bookings.Add(booking);
            context.SaveChanges();

            var service = new ManifestExportService(context);

            // Act: Vista previa con la unidad pequeña de 14 para 16 pasajeros
            var preview = await service.GetManifestPreviewAsync(bookingDate, tour.Id, smallVan.Id, "Turno Mañana");

            // Assert: Debe marcar sobrecapacidad y sugerir la unidad mayor
            Assert.NotNull(preview);
            Assert.True(preview.IsOverCapacity);
            Assert.Contains("Capacidad insuficiente", preview.OptimizationAdvice);
            Assert.Equal(mediumVan.Id, preview.SuggestedVehicleId);
        }

        [Fact]
        public async Task ManifestExportService_WithDriverId_ResolvesDriverOfficialInfo()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);

            var tour = context.TourPackages.First();
            var vehicle = context.Vehicles.First();

            var driver = new Driver
            {
                Id = Guid.NewGuid(),
                FullName = "Jorge Llanos Bazan",
                Dni = "41829301",
                LicenseNumber = "Q-41829301",
                LicenseCategory = "A-IIIa",
                IsActive = true
            };
            context.Drivers.Add(driver);
            context.SaveChanges();

            var service = new ManifestExportService(context);

            var request = new ManifestExportRequest
            {
                Date = DateTime.UtcNow.Date,
                TourPackageId = tour.Id,
                VehicleId = vehicle.Id,
                DriverId = driver.Id,
                ShiftName = "Turno Mañana"
            };

            // Act
            var excelBytes = await service.GenerateManifestExcelAsync(request);

            // Assert
            Assert.NotNull(excelBytes);
            Assert.NotEmpty(excelBytes);
            Assert.Equal("Jorge Llanos Bazan", request.DriverName);
            Assert.Equal("Q-41829301", request.DriverLicense);

            using var stream = new MemoryStream(excelBytes);
            using var workbook = new XLWorkbook(stream);
            var ws = workbook.Worksheets.First();
            // Verificar que el nombre del conductor figure en la hoja
            var cellValue = ws.Cell("C9").GetString();
            Assert.Contains("Jorge Llanos", cellValue);
        }
    }
}
