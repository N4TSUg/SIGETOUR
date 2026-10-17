using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Models;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Controllers
{
    public class PublicControllersTests
    {
        #region HomeController Tests
        [Fact]
        public async Task HomeController_Index_ReturnsViewWithActiveTours()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new HomeController(context);

            // Act
            var result = await controller.Index();

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<TourPackage>>(viewResult.Model);
            Assert.NotEmpty(model);
            Assert.NotNull(controller.ViewBag.FeaturedPackages);
            Assert.NotNull(controller.ViewBag.Vehicles);
            Assert.NotNull(controller.ViewBag.Categories);
        }
        #endregion

        #region TourController Tests
        [Fact]
        public async Task TourController_Catalog_ReturnsViewWithActiveTours()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new TourController(context);

            // Act
            var result = await controller.Catalog(null, null, null, null, null, null, null, null);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<TourPackage>>(viewResult.Model);
            Assert.NotEmpty(model);
        }

        [Fact]
        public async Task TourController_Catalog_FiltersBySearchQuery()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new TourController(context);

            // Act
            var result = await controller.Catalog("Cumbemayo", null, null, null, null, null, null, null);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<TourPackage>>(viewResult.Model);
            Assert.All(model, t => Assert.Contains("Cumbemayo", t.Title, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task TourController_Catalog_FiltersByMaxPrice()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new TourController(context);

            // Act: All seeded tours have BasePrice >= 30, filter max 20 should return 0 results
            var result = await controller.Catalog(null, null, null, null, null, null, 20m, null);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<TourPackage>>(viewResult.Model);
            Assert.Empty(model);
        }

        [Fact]
        public async Task TourController_Details_ExistingSlug_ReturnsViewWithTour()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new TourController(context);

            // Act
            var result = await controller.Details("cumbemayo-mistico");

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<TourPackage>(viewResult.Model);
            Assert.Equal("cumbemayo-mistico", model.Slug);
        }

        [Fact]
        public async Task TourController_Details_EmptySlug_RedirectsToCatalog()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = new TourController(context);

            // Act
            var result = await controller.Details(string.Empty);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Catalog", redirectResult.ActionName);
        }

        [Fact]
        public async Task TourController_Details_NonExistingSlug_ReturnsNotFound()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = new TourController(context);

            // Act
            var result = await controller.Details("tour-inexistente");

            // Assert
            Assert.IsType<NotFoundResult>(result);
        }
        #endregion

        #region VehicleController Tests
        [Fact]
        public async Task VehicleController_Catalog_ReturnsViewWithVehicleCatalogViewModel()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new VehicleController(context);

            // Act
            var result = await controller.Catalog(null);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<VehicleCatalogViewModel>(viewResult.Model);
            Assert.NotEmpty(model.Vehicles);
            Assert.Equal("Todos", model.SelectedCategory);
            Assert.True(model.TotalUnits > 0);
        }

        [Fact]
        public async Task VehicleController_Details_ExistingVehicle_ReturnsViewWithDetailsViewModel()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new VehicleController(context);
            var vehicle = context.Vehicles.First();

            // Act
            var result = await controller.Details(vehicle.Id);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<VehicleDetailsViewModel>(viewResult.Model);
            Assert.Equal(vehicle.Id, model.Vehicle.Id);
            Assert.Equal(vehicle.LicensePlate, model.Vehicle.LicensePlate);
        }

        [Fact]
        public async Task VehicleController_Details_EmptyGuid_RedirectsToCatalog()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = new VehicleController(context);

            // Act
            var result = await controller.Details(Guid.Empty);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Catalog", redirectResult.ActionName);
        }

        [Fact]
        public async Task VehicleController_Details_NonExistingVehicle_ReturnsNotFound()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = new VehicleController(context);

            // Act
            var result = await controller.Details(Guid.NewGuid());

            // Assert
            Assert.IsType<NotFoundResult>(result);
        }
        #endregion
    }
}
