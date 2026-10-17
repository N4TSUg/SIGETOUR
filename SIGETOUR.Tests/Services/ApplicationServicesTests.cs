using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using SIGETOUR.API.Application.DTOs;
using SIGETOUR.API.Application.Mappings;
using SIGETOUR.API.Application.Services;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Core.Enums;
using SIGETOUR.API.Models;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Services
{
    public class ApplicationServicesTests
    {
        private IMapper CreateMapper()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAutoMapper(cfg =>
            {
                cfg.AddProfile<TourPackageProfile>();
            });
            var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IMapper>();
        }

        #region TourPackageService Tests
        [Fact]
        public async Task TourPackageService_GetAllAsync_ReturnsMappedTourPackages()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var mapper = CreateMapper();
            var service = new TourPackageService(context, mapper);

            // Act
            var result = await service.GetAllAsync();

            // Assert
            Assert.NotNull(result);
            Assert.NotEmpty(result);
            Assert.Contains(result, t => t.Title == "Cumbemayo Místico");
        }

        [Fact]
        public async Task TourPackageService_GetByIdAsync_ExistingId_ReturnsTourPackage()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var mapper = CreateMapper();
            var service = new TourPackageService(context, mapper);
            var existingTour = context.TourPackages.First();

            // Act
            var result = await service.GetByIdAsync(existingTour.Id);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(existingTour.Id, result.Id);
            Assert.Equal(existingTour.Title, result.Title);
        }

        [Fact]
        public async Task TourPackageService_GetByIdAsync_NonExistingId_ReturnsNull()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var mapper = CreateMapper();
            var service = new TourPackageService(context, mapper);

            // Act
            var result = await service.GetByIdAsync(Guid.NewGuid());

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task TourPackageService_CreateAsync_PersistsAndReturnsCreatedDto()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var mapper = CreateMapper();
            var service = new TourPackageService(context, mapper);

            var createDto = new TourPackageCreateDto
            {
                Title = "Laguna San Nicolás y Caballitos de Totora",
                Subtitle = "Paseo tradicional en balsa de totora",
                Category = TourCategory.NaturalezaYAventura,
                Modality = TourModality.Regular,
                Difficulty = TourDifficulty.Ligero,
                BasePrice = 45.00m,
                ChildPrice = 30.00m,
                MaxCapacity = 20,
                Duration = "4 horas",
                IsActive = true
            };

            // Act
            var result = await service.CreateAsync(createDto);

            // Assert
            Assert.NotNull(result);
            Assert.NotEqual(Guid.Empty, result.Id);
            Assert.Equal(createDto.Title, result.Title);

            var inDb = context.TourPackages.Find(result.Id);
            Assert.NotNull(inDb);
            Assert.Equal("Laguna San Nicolás y Caballitos de Totora", inDb.Title);
        }

        [Fact]
        public async Task TourPackageService_UpdateAsync_ExistingId_UpdatesAndReturnsTrue()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var mapper = CreateMapper();
            var service = new TourPackageService(context, mapper);
            var existingTour = context.TourPackages.First();

            var updateDto = new TourPackageUpdateDto
            {
                Id = existingTour.Id,
                Title = "Cumbemayo Místico y Bosque de Piedras (Actualizado)",
                Subtitle = "Nueva descripción corta",
                Category = TourCategory.ArqueologiaYPetroglifos,
                Modality = TourModality.Regular,
                Difficulty = TourDifficulty.Moderado,
                BasePrice = 65.00m,
                ChildPrice = 45.00m,
                MaxCapacity = 18,
                Duration = "5 horas",
                IsActive = true
            };

            // Act
            var success = await service.UpdateAsync(existingTour.Id, updateDto);

            // Assert
            Assert.True(success);
            var updated = context.TourPackages.Find(existingTour.Id);
            Assert.NotNull(updated);
            Assert.Equal("Cumbemayo Místico y Bosque de Piedras (Actualizado)", updated.Title);
            Assert.Equal(65.00m, updated.BasePrice);
        }

        [Fact]
        public async Task TourPackageService_UpdateAsync_NonExistingId_ReturnsFalse()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var mapper = CreateMapper();
            var service = new TourPackageService(context, mapper);

            var updateDto = new TourPackageUpdateDto
            {
                Title = "Inexistente",
                BasePrice = 100
            };

            // Act
            var success = await service.UpdateAsync(Guid.NewGuid(), updateDto);

            // Assert
            Assert.False(success);
        }

        [Fact]
        public async Task TourPackageService_DeleteAsync_ExistingId_RemovesAndReturnsTrue()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var mapper = CreateMapper();
            var service = new TourPackageService(context, mapper);
            var existingTour = context.TourPackages.First();

            // Act
            var success = await service.DeleteAsync(existingTour.Id);

            // Assert
            Assert.True(success);
            var inDb = context.TourPackages.Find(existingTour.Id);
            Assert.Null(inDb);
        }

        [Fact]
        public async Task TourPackageService_DeleteAsync_NonExistingId_ReturnsFalse()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var mapper = CreateMapper();
            var service = new TourPackageService(context, mapper);

            // Act
            var success = await service.DeleteAsync(Guid.NewGuid());

            // Assert
            Assert.False(success);
        }
        #endregion

        #region ManifestExportService Tests
        [Fact]
        public async Task ManifestExportService_GetManifestPreviewAsync_ReturnsPreviewWithSeats()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var service = new ManifestExportService(context);
            var tour = context.TourPackages.First();
            var vehicle = context.Vehicles.First();

            // Act
            var preview = await service.GetManifestPreviewAsync(
                DateTime.UtcNow.Date,
                tour.Id,
                vehicle.Id,
                "Turno Mañana");

            // Assert
            Assert.NotNull(preview);
            Assert.Equal(vehicle.SeatCapacity, preview.TotalSeats);
            Assert.NotEmpty(preview.Passengers);
            Assert.Equal(vehicle.LicensePlate, preview.VehiclePlate);
        }

        [Fact]
        public async Task ManifestExportService_GenerateManifestExcelAsync_GeneratesValidExcelBytes()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var service = new ManifestExportService(context);
            var tour = context.TourPackages.First();
            var vehicle = context.Vehicles.First();

            var request = new ManifestExportRequest
            {
                Date = DateTime.UtcNow.Date,
                TourPackageId = tour.Id,
                VehicleId = vehicle.Id,
                ShiftName = "Turno Mañana",
                DriverName = "Carlos Mendoza",
                GuideName = "Ana Rodriguez"
            };

            // Act
            var excelBytes = await service.GenerateManifestExcelAsync(request);

            // Assert
            Assert.NotNull(excelBytes);
            Assert.NotEmpty(excelBytes);

            // Verify it is a valid ClosedXML workbook
            using var stream = new MemoryStream(excelBytes);
            using var workbook = new XLWorkbook(stream);
            Assert.NotEmpty(workbook.Worksheets);
        }
        #endregion

        #region CashReportExportService Tests
        [Fact]
        public async Task CashReportExportService_GenerateDailyCashReportExcelAsync_GeneratesNonEmptyExcel()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var service = new CashReportExportService(context);

            // Act
            var excelBytes = await service.GenerateDailyCashReportExcelAsync(DateTime.UtcNow.Date);

            // Assert
            Assert.NotNull(excelBytes);
            Assert.NotEmpty(excelBytes);

            using var stream = new MemoryStream(excelBytes);
            using var workbook = new XLWorkbook(stream);
            Assert.NotEmpty(workbook.Worksheets);
        }

        [Fact]
        public async Task CashReportExportService_GenerateMonthlyCashReportExcelAsync_GeneratesNonEmptyExcel()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var service = new CashReportExportService(context);

            // Act
            var excelBytes = await service.GenerateMonthlyCashReportExcelAsync(DateTime.UtcNow.Year, DateTime.UtcNow.Month);

            // Assert
            Assert.NotNull(excelBytes);
            Assert.NotEmpty(excelBytes);

            using var stream = new MemoryStream(excelBytes);
            using var workbook = new XLWorkbook(stream);
            Assert.NotEmpty(workbook.Worksheets);
        }

        [Fact]
        public async Task CashReportExportService_GenerateShiftReportExcelAsync_GeneratesNonEmptyExcel()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var service = new CashReportExportService(context);
            var shift = context.CashRegisterShifts.First();

            // Act
            var excelBytes = await service.GenerateShiftReportExcelAsync(shift.Id);

            // Assert
            Assert.NotNull(excelBytes);
            Assert.NotEmpty(excelBytes);

            using var stream = new MemoryStream(excelBytes);
            using var workbook = new XLWorkbook(stream);
            Assert.NotEmpty(workbook.Worksheets);
        }
        #endregion
    }
}
