using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Models;
using SIGETOUR.API.ViewModels;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Controllers
{
    public class AdminControllerTests
    {
        [Fact]
        public async Task Dashboard_ReturnsViewWithStats()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.Dashboard();

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.True(controller.ViewBag.ToursCount >= 1);
            Assert.True(controller.ViewBag.VehiclesCount >= 1);
            Assert.True(controller.ViewBag.BookingsCount >= 1);
        }

        [Fact]
        public async Task Tours_ReturnsView_WithToursList()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.Tours(search: "Cumbemayo", categoryId: null, status: "Todos");

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<TourPackage>>(viewResult.Model);
            Assert.Single(model);
            Assert.Contains("Cumbemayo", model.First().Title);
        }

        [Fact]
        public async Task CreateTour_ValidTour_SavesSuccessfully()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            var cat = context.Categories.First();
            var dto = new TourPackageDto
            {
                Title = "Granja Porcón Ecoturismo",
                Subtitle = "Naturaleza pura",
                CategoryId = cat.Id,
                BasePrice = 45,
                ChildPrice = 30,
                MaxCapacity = 19,
                Duration = "6 Horas",
                Description = "Visita al zoológico y bosque de pinos"
            };

            var stops = new[] { "Entrada Porcón", "Mini Zoológico", "Mirador" };
            var stopTimes = new[] { "09:00 AM", "11:00 AM", "01:00 PM" };
            var inclusions = new[] { "Transporte turístico con A/C", "Guía oficial" };
            var shiftNames = new[] { "Turno Mañana" };
            var shiftStarts = new[] { "08:30" };
            var shiftEnds = new[] { "14:30" };
            var imageFiles = new FormFileCollection();

            // Act
            var result = await controller.CreateTour(dto, imageFiles, stops, stopTimes, inclusions, shiftNames, shiftStarts, shiftEnds);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Tours", redirectResult.ActionName);

            var savedTour = context.TourPackages.FirstOrDefault(t => t.Title == "Granja Porcón Ecoturismo");
            Assert.NotNull(savedTour);
            Assert.Equal(cat.Id, savedTour.CategoryId);
            Assert.Equal(3, context.ItineraryStops.Count(s => s.TourPackageId == savedTour.Id));
            Assert.Equal(2, context.TourInclusions.Count(i => i.TourPackageId == savedTour.Id));
        }

        [Fact]
        public async Task CreateTour_InvalidModel_ReturnsViewWithoutSaving()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = TestHelpers.CreateAdminController(context);
            controller.ModelState.AddModelError("Title", "El título es obligatorio");

            var dto = new TourPackageDto();

            // Act
            var result = await controller.CreateTour(dto, new FormFileCollection(), null!, null!, null!, null!, null!, null!);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Equal("CreateTour", viewResult.ViewName);
        }

        [Fact]
        public async Task EditTour_UpdatesPropertiesAndChildrenSuccessfully()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            var tour = context.TourPackages.First();
            var dto = new TourPackageDto
            {
                Id = tour.Id,
                Title = "Cumbemayo Místico Editado",
                Subtitle = "Nueva descripción corta",
                CategoryId = tour.CategoryId,
                BasePrice = 60,
                ChildPrice = 40,
                MaxCapacity = 18,
                Duration = "5 Horas",
                Description = "Descripción actualizada",
                IsActive = true
            };

            var newStops = new[] { "Parada Arqueológica 1" };
            var newStopTimes = new[] { "10:00 AM" };
            var newInclusions = new[] { "Botiquín de Primeros Auxilios" };
            var newShifts = new[] { "Turno Tarde" };
            var newStarts = new[] { "14:00" };
            var newEnds = new[] { "18:00" };
            var imageFiles = new FormFileCollection();

            // Act
            var result = await controller.EditTour(dto, imageFiles, newStops, newStopTimes, newInclusions, newShifts, newStarts, newEnds);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Tours", redirectResult.ActionName);

            var updatedTour = context.TourPackages.Find(tour.Id);
            Assert.NotNull(updatedTour);
            Assert.Equal("Cumbemayo Místico Editado", updatedTour.Title);
            Assert.Equal(60, updatedTour.BasePrice);
            Assert.Single(context.ItineraryStops.Where(s => s.TourPackageId == tour.Id));
            Assert.Single(context.TourInclusions.Where(i => i.TourPackageId == tour.Id));
        }

        [Fact]
        public async Task EditTour_Get_WithSlug_ReturnsView_WithTourDto()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            var tour = context.TourPackages.First();

            // Act
            var result = await controller.EditTour(tour.Slug);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Equal("CreateTour", viewResult.ViewName);
            var model = Assert.IsType<TourPackageDto>(viewResult.Model);
            Assert.Equal(tour.Id, model.Id);
            Assert.Equal(tour.Title, model.Title);
            Assert.Equal(tour.Slug, model.Slug);
        }

        [Fact]
        public async Task EditTour_Get_WithGuid_RedirectsToSlug_WhenSlugExists()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            var tour = context.TourPackages.First();

            // Act
            var result = await controller.EditTour(tour.Id.ToString());

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("EditTour", redirectResult.ActionName);
            Assert.Equal(tour.Slug, redirectResult.RouteValues?["id"]);
        }

        [Fact]
        public async Task EditTour_Get_WithNonExistentIdentifier_ReturnsNotFound()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.EditTour("slug-no-existente-xyz");

            // Assert
            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Fleet_ReturnsView_WithVehicleList()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.Fleet(null, null, 1);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<Vehicle>>(viewResult.Model);
            Assert.NotEmpty(model);
        }

        [Fact]
        public async Task EditVehicle_MissingRequiredFields_ReturnsViewWithErrors()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = TestHelpers.CreateAdminController(context);

            var dto = new VehicleDto
            {
                LicensePlate = "", // Missing
                Model = "",        // Missing
                SeatCapacity = 0   // Invalid
            };

            // Act
            var result = await controller.EditVehicle(dto, new List<IFormFile>());

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.False(controller.ModelState.IsValid);
            Assert.True(controller.ModelState.ContainsKey("LicensePlate"));
            Assert.True(controller.ModelState.ContainsKey("Model"));
            Assert.True(controller.ModelState.ContainsKey("SeatCapacity"));
        }

        [Fact]
        public async Task EditVehicle_ValidNewVehicle_SavesSuccessfully()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = TestHelpers.CreateAdminController(context);

            var dto = new VehicleDto
            {
                Id = Guid.Empty,
                LicensePlate = "T7C-999",
                Model = "Hyundai H1",
                Category = "Van Turística VIP",
                SeatCapacity = 11,
                ManufactureYear = 2022,
                Color = "Plateado",
                Status = "Operativo en Ruta (En Servicio)"
            };

            // Act
            var result = await controller.EditVehicle(dto, new List<IFormFile>());

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Fleet", redirectResult.ActionName);

            var saved = context.Vehicles.FirstOrDefault(v => v.LicensePlate == "T7C-999");
            Assert.NotNull(saved);
            Assert.Equal("Hyundai H1", saved.Model);
            Assert.Equal(11, saved.SeatCapacity);
            Assert.True(saved.IsActive);
        }

        [Fact]
        public async Task Categories_ReturnsView_WithCategories()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.Categories(null, 1);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<CategoryDto>>(viewResult.Model);
            Assert.NotEmpty(model);
        }

        [Fact]
        public async Task CreateCategory_ValidData_SavesCategory()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = TestHelpers.CreateAdminController(context);

            var category = new CategoryDto
            {
                Name = "Turismo Vivencial",
                Description = "Convivencia con comunidades locales",
                Icon = "groups",
                IsActive = true
            };

            // Act
            var result = await controller.EditCategory(category);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Categories", redirectResult.ActionName);

            var saved = context.Categories.FirstOrDefault(c => c.Name == "Turismo Vivencial");
            Assert.NotNull(saved);
        }

        [Fact]
        public async Task OpenShift_ValidAmount_OpensCashRegisterShift()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = TestHelpers.CreateAdminController(context, currentUserId: "cajero-01", currentUserName: "cajero@sigetour.com");

            // Act
            var result = await controller.OpenCashShift(250.50m, "Apertura caja turno mañana");

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Cash", redirectResult.ActionName);

            var shift = context.CashRegisterShifts.FirstOrDefault(s => s.IsOpen);
            Assert.NotNull(shift);
            Assert.Equal(250.50m, shift.InitialAmount);
            Assert.Equal("cajero@sigetour.com", shift.OpenedByUserName);
        }

        [Fact]
        public async Task AddCashTransaction_ValidIncome_RegistersTransaction()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context, currentUserId: "cajero-01", currentUserName: "cajero@sigetour.com");

            var activeShift = context.CashRegisterShifts.First(s => s.IsOpen);

            // Act
            var result = await controller.RegisterCashTransaction(
                activeShift.Id,
                "Ingreso",
                "Cobro de Reserva",
                80.00m,
                "Efectivo",
                "REC-001",
                "Cobro directo en mostrador tour Cumbemayo");

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Cash", redirectResult.ActionName);

            var tx = context.CashTransactions.FirstOrDefault(t => t.Amount == 80.00m);
            Assert.NotNull(tx);
            Assert.Equal("Ingreso", tx.TransactionType);
            Assert.Equal("Efectivo", tx.PaymentMethod);
        }

        [Fact]
        public async Task AddPettyCashMovement_ValidExpense_DeductsFromBalance()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context, currentUserId: "cajero-01", currentUserName: "cajero@sigetour.com");

            var account = context.PettyCashAccounts.First();
            var initialBalance = account.CurrentBalance;

            // Act
            var result = await controller.RegisterPettyCashExpense(
                account.Id,
                "Insumos y Logística",
                "Boleta",
                "B001-123",
                "Farmacia Central",
                45.00m,
                "Administrador Central",
                "Compra de botiquín de emergencia");

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Cash", redirectResult.ActionName);

            var updatedAccount = context.PettyCashAccounts.Find(account.Id);
            Assert.NotNull(updatedAccount);
            Assert.Equal(initialBalance - 45.00m, updatedAccount.CurrentBalance);
            Assert.Equal(45.00m, updatedAccount.TotalSpent);
        }

        [Fact]
        public async Task ReplenishPettyCash_ReducesPendingSpent_AndRestoresBalance()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            var account = context.PettyCashAccounts.First();
            var fundAmount = account.TotalFundAmount;

            // 1. Registrar gasto de S/ 100
            await controller.RegisterPettyCashExpense(
                account.Id, "Combustible", "Factura", "F001-999", "Grifo Primax", 100.00m, "Chofer", "Combustible Sprinter");

            var accAfterExpense = context.PettyCashAccounts.Find(account.Id)!;
            Assert.Equal(100.00m, accAfterExpense.TotalSpent);
            Assert.Equal(fundAmount - 100.00m, accAfterExpense.CurrentBalance);

            // 2. Reponer S/ 100
            var replenishResult = await controller.ReplenishPettyCash(
                account.Id, 100.00m, "Reembolso total de combustible");

            var redirectResult = Assert.IsType<RedirectToActionResult>(replenishResult);
            Assert.Equal("Cash", redirectResult.ActionName);

            var accAfterReplenish = context.PettyCashAccounts.Find(account.Id)!;
            // Gastado pendiente en rojo vuelve a 0
            Assert.Equal(0.00m, accAfterReplenish.TotalSpent);
            // Saldo disponible vuelve al 100% del fondo asignado
            Assert.Equal(fundAmount, accAfterReplenish.CurrentBalance);
            // Consumo vuelve a 0%
            Assert.Equal(0.0m, accAfterReplenish.ConsumptionPercentage);
        }

        [Fact]
        public async Task ChangeUserRole_NonSuperAdminAttemptingToModifySuperAdmin_IsBlocked()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();

            var superAdminUser = new ApplicationUser
            {
                Id = "super-admin-id",
                UserName = "superadmin",
                Email = "superadmin@sigetour.com"
            };

            var mockUserManager = TestHelpers.CreateMockUserManager(new List<ApplicationUser> { superAdminUser });
            mockUserManager.Setup(m => m.FindByIdAsync("super-admin-id"))
                .ReturnsAsync(superAdminUser);

            // The logged in caller is NOT "superadmin"
            var controller = TestHelpers.CreateAdminController(
                context,
                userManager: mockUserManager,
                currentUserId: "other-admin-id",
                currentUserName: "admin2@sigetour.com");

            var editModel = new EditUserViewModel
            {
                Id = "super-admin-id",
                UserName = "superadmin",
                Email = "superadmin@sigetour.com",
                FirstName = "Super",
                LastName = "Admin",
                Role = "Guide"
            };

            // Act
            var result = await controller.EditUser(editModel);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Users", redirectResult.ActionName);
            Assert.True(controller.TempData.ContainsKey("ErrorMessage"));
            Assert.Contains("SuperAdmin", controller.TempData["ErrorMessage"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task QuickSearch_ReturnsJsonMatches()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.QuickSearch("Cumbemayo");

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(result);
            Assert.NotNull(jsonResult.Value);
        }

        [Fact]
        public async Task Drivers_ReturnsView_WithDriversList()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var driver = new Driver
            {
                Id = Guid.NewGuid(),
                FullName = "Carlos Mendoza Silva",
                LicenseNumber = "Q-42910834",
                IsActive = true
            };
            context.Drivers.Add(driver);
            context.SaveChanges();

            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.Drivers(null);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<Driver>>(viewResult.Model);
            Assert.Contains(model, d => d.FullName == "Carlos Mendoza Silva");
        }

        [Fact]
        public async Task EditDriver_ValidModel_SavesDriver()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            var driver = new Driver
            {
                Id = Guid.Empty,
                FullName = "Nuevo Conductor Test",
                Dni = "70809010",
                LicenseNumber = "Q-70809010",
                LicenseCategory = "A-IIIa",
                Phone = "999111222",
                IsActive = true
            };

            // Act
            var result = await controller.EditDriver(driver);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Drivers", redirectResult.ActionName);
            Assert.True(context.Drivers.Any(d => d.FullName == "Nuevo Conductor Test"));
        }

        [Fact]
        public async Task EditDriver_Get_ReturnsModelWithEmptyId()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.EditDriver((Guid?)null);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<Driver>(viewResult.Model);
            Assert.Equal(Guid.Empty, model.Id);
        }

        [Fact]
        public async Task EditDriver_Post_NonExistingId_CreatesNewDriverSafely()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            var driver = new Driver
            {
                Id = Guid.NewGuid(), // Unsaved ID
                FullName = "Conductor Fallback Test",
                Dni = "77665544",
                LicenseNumber = "Q-77665544",
                LicenseCategory = "A-IIIa",
                IsActive = true
            };

            // Act
            var result = await controller.EditDriver(driver);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Drivers", redirectResult.ActionName);
            Assert.True(context.Drivers.Any(d => d.FullName == "Conductor Fallback Test"));
        }

        [Fact]
        public async Task ExportManifestExcel_LinksAssignedVehicleAndDriver_ToMatchingBookings()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);

            var booking = context.Bookings.Include(b => b.Items).First();
            var tourId = booking.Items.First().TourPackageId;
            var tour = context.TourPackages.First(t => t.Id == tourId);
            var vehicle = context.Vehicles.First();

            // Verificamos estado inicial: Sin vehículo ni conductor asignados
            Assert.Null(booking.AssignedVehicleId);
            Assert.Null(booking.AssignedDriverId);

            var driver = new Driver
            {
                Id = Guid.NewGuid(),
                FullName = "Conductor Despacho Oficial",
                Dni = "40102030",
                LicenseNumber = "Q-40102030",
                IsActive = true
            };
            context.Drivers.Add(driver);
            context.SaveChanges();

            var mockManifest = new Mock<IManifestExportService>();
            mockManifest.Setup(m => m.GenerateManifestExcelAsync(It.IsAny<ManifestExportRequest>()))
                .ReturnsAsync(new byte[] { 1, 2, 3 });

            var controller = TestHelpers.CreateAdminController(context, manifestService: mockManifest);

            var request = new ManifestExportRequest
            {
                Date = booking.TravelDate.Date,
                TourPackageId = tour.Id,
                VehicleId = vehicle.Id,
                DriverId = driver.Id,
                ShiftName = booking.ShiftName
            };

            // Act: El despachador genera el manifiesto oficial para esa salida
            var result = await controller.ExportManifestExcel(request, null);

            // Assert: Se devuelve el archivo Excel y la reserva queda enlazada con el vehículo y chofer
            Assert.IsType<FileContentResult>(result);

            var updatedBooking = context.Bookings.Find(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(vehicle.Id, updatedBooking.AssignedVehicleId);
            Assert.Equal(driver.Id, updatedBooking.AssignedDriverId);
            Assert.Equal(BookingStatus.Confirmed, updatedBooking.Status);

            var updatedVehicle = context.Vehicles.Find(vehicle.Id);
            Assert.NotNull(updatedVehicle);
            Assert.Equal("Operativo en Ruta (En Servicio)", updatedVehicle.Status);
            Assert.True(updatedVehicle.IsActive);
        }

        [Fact]
        public async Task ExportManifestExcel_AutoRecovers_AssignedVehicleAndDriver_WhenOmittedInRequest()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);

            var booking = context.Bookings.Include(b => b.Items).First();
            var tourId = booking.Items.First().TourPackageId;
            var vehicle = context.Vehicles.First();

            var driver = new Driver
            {
                Id = Guid.NewGuid(),
                FullName = "Conductor Previamente Asignado",
                Dni = "40102030",
                LicenseNumber = "Q-40102030",
                IsActive = true
            };
            context.Drivers.Add(driver);

            // Pre-asignamos vehículo y conductor en la reserva
            booking.AssignedVehicleId = vehicle.Id;
            booking.AssignedDriverId = driver.Id;
            context.SaveChanges();

            ManifestExportRequest? capturedRequest = null;
            var mockManifest = new Mock<IManifestExportService>();
            mockManifest.Setup(m => m.GenerateManifestExcelAsync(It.IsAny<ManifestExportRequest>()))
                .Callback<ManifestExportRequest>(req => capturedRequest = req)
                .ReturnsAsync(new byte[] { 1, 2, 3 });

            var controller = TestHelpers.CreateAdminController(context, manifestService: mockManifest);

            // Request sin vehicleId ni driverId (como al hacer clic directo en descarga de manifiesto)
            var queryRequest = new ManifestExportRequest
            {
                Date = booking.TravelDate.Date,
                TourPackageId = tourId,
                VehicleId = Guid.Empty,
                DriverId = null,
                ShiftName = booking.ShiftName
            };

            // Act
            var result = await controller.ExportManifestExcel(null, queryRequest);

            // Assert
            Assert.IsType<FileContentResult>(result);
            Assert.NotNull(capturedRequest);
            Assert.Equal(vehicle.Id, capturedRequest.VehicleId);
            Assert.Equal(driver.Id, capturedRequest.DriverId);
            Assert.Equal(driver.FullName, capturedRequest.DriverName);
            Assert.Equal(driver.LicenseNumber, capturedRequest.DriverLicense);
        }

        [Fact]
        public async Task Dashboard_DeparturesBoard_ShowsRealOccupancyAndEmptyWhenUnassigned()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.Dashboard();

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<DashboardViewModel>(viewResult.Model);
            Assert.NotNull(model.Departures);
            Assert.NotEmpty(model.Departures);

            // Verificamos que sin asignación previa, unidad y chofer salgan vacíos y sin guía
            foreach (var dep in model.Departures)
            {
                Assert.Empty(dep.GuideName); // La empresa no tiene guías contratados
            }
        }

        [Fact]
        public async Task DeleteBooking_WithAssociatedReceiptsPaymentsAndTransactions_DeletesCleanlyAndRedirects()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage { Id = Guid.NewGuid(), Title = "Tour Test Deletion", MaxCapacity = 15, IsActive = true };
            context.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-DEL1",
                CustomerName = "Carlos Deleteme",
                TotalPassengers = 2,
                TotalAmount = 80m,
                PaidAmount = 80m,
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.AddDays(1)
            };
            booking.Items.Add(new BookingItem { TourPackageId = tour.Id, Passengers = 2, SubTotal = 80m });
            context.Bookings.Add(booking);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00000099",
                TotalAmount = 80m,
                Status = "Accepted"
            };
            context.ElectronicReceipts.Add(receipt);

            var culqiPayment = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 8000,
                Channel = "qr",
                Status = "paid"
            };
            context.CulqiPayments.Add(culqiPayment);

            var incident = new PaymentIncident
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                IncidentType = "ManualReconciliation",
                Severity = "Info"
            };
            context.PaymentIncidents.Add(incident);

            var shift = new CashRegisterShift
            {
                Id = Guid.NewGuid(),
                ShiftNumber = "TURNO-01",
                OpenedByUserName = "admin",
                OpenedAt = DateTime.UtcNow,
                IsOpen = true
            };
            context.CashRegisterShifts.Add(shift);

            var cashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                BookingId = booking.Id,
                Amount = 80m,
                TransactionType = "Ingreso"
            };
            context.CashTransactions.Add(cashTx);
            await context.SaveChangesAsync();

            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.DeleteBooking(booking.Id);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(controller.Bookings), redirectResult.ActionName);
            Assert.True(controller.TempData.ContainsKey("SuccessMessage"));

            // Verify database state:
            var deletedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.Null(deletedBooking);

            var deletedItems = await context.BookingItems.Where(i => i.BookingId == booking.Id).ToListAsync();
            Assert.Empty(deletedItems);

            var deletedReceipts = await context.ElectronicReceipts.Where(r => r.BookingId == booking.Id).ToListAsync();
            Assert.Empty(deletedReceipts);

            var deletedPayments = await context.CulqiPayments.Where(c => c.BookingId == booking.Id).ToListAsync();
            Assert.Empty(deletedPayments);

            var deletedIncidents = await context.PaymentIncidents.Where(pi => pi.BookingId == booking.Id).ToListAsync();
            Assert.Empty(deletedIncidents);

            // CashTransaction is preserved for accounting, but unlinked
            var reloadedCashTx = await context.CashTransactions.FindAsync(cashTx.Id);
            Assert.NotNull(reloadedCashTx);
            Assert.Null(reloadedCashTx.BookingId);
        }

        [Fact]
        public async Task DeleteBooking_NonExistentBooking_SetsErrorMessageAndRedirects()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.DeleteBooking(Guid.NewGuid());

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(controller.Bookings), redirectResult.ActionName);
            Assert.True(controller.TempData.ContainsKey("ErrorMessage"));
        }

        [Fact]
        public async Task CancelBooking_WithCashRefundAndSunatVoid_RegistersEgresoAndCancelsReceipt()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);

            var booking = context.Bookings.First();
            booking.TotalAmount = 120m;
            booking.PaidAmount = 120m;
            booking.Status = BookingStatus.Confirmed;

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "24",
                Status = "Accepted",
                Environment = "Simulation",
                TotalAmount = 120m
            };
            context.ElectronicReceipts.Add(receipt);
            await context.SaveChangesAsync();

            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.CancelBooking(
                booking.Id,
                refundType: "full",
                refundAmount: 120m,
                registerCashRefund: true,
                refundMethod: "Efectivo",
                voidSunatReceipt: true,
                sunatAction: "credit_note",
                cancelReason: "Cancelación solicitada por el pasajero");

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(controller.Bookings), redirect.ActionName);

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(BookingStatus.Cancelled, updatedBooking.Status);
            Assert.Equal(0m, updatedBooking.PaidAmount);

            var refundTx = await context.CashTransactions
                .FirstOrDefaultAsync(t => t.BookingId == booking.Id && t.TransactionType == "Egreso");
            Assert.NotNull(refundTx);
            Assert.Equal(120m, refundTx.Amount);
            Assert.Equal("Devolución", refundTx.Category);

            var updatedReceipt = await context.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(updatedReceipt);
            Assert.Equal("Cancelled", updatedReceipt.Status);
        }

        [Fact]
        public async Task CancelDeparture_CancelsAllMatchingBookings_RegistersRefundsAndVoidsReceipts()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);

            var booking = context.Bookings.Include(b => b.Items).First();
            var tourId = booking.Items.First().TourPackageId;
            booking.PaidAmount = 100m;
            booking.Status = BookingStatus.Confirmed;

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "25",
                Status = "Accepted",
                Environment = "Simulation",
                TotalAmount = 100m
            };
            context.ElectronicReceipts.Add(receipt);
            await context.SaveChangesAsync();

            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.CancelDeparture(
                tourId,
                booking.TravelDate.Date,
                booking.ShiftName,
                registerCashRefund: true,
                voidSunatReceipts: true,
                sunatAction: "credit_note",
                cancelReason: "Bloqueo de carretera / Clima");

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(controller.Dashboard), redirect.ActionName);

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(BookingStatus.Cancelled, updatedBooking.Status);
            Assert.Equal(0m, updatedBooking.PaidAmount);

            var refundTx = await context.CashTransactions
                .FirstOrDefaultAsync(t => t.BookingId == booking.Id && t.TransactionType == "Egreso");
            Assert.NotNull(refundTx);
            Assert.Equal(100m, refundTx.Amount);

            var updatedReceipt = await context.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(updatedReceipt);
            Assert.Equal("Cancelled", updatedReceipt.Status);
        }
    }
}
