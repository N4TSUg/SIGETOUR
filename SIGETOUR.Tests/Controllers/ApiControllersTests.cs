using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using SIGETOUR.API.Application.DTOs;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Controllers;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Controllers
{
    public class ApiControllersTests
    {
        #region AuthController Tests
        [Fact]
        public async Task AuthController_Login_ValidCredentials_ReturnsOkWithToken()
        {
            // Arrange
            var user = new ApplicationUser
            {
                Id = "test-user-id",
                UserName = "admin",
                Email = "admin@sigetour.com"
            };

            var mockUserManager = TestHelpers.CreateMockUserManager(new List<ApplicationUser> { user });
            mockUserManager.Setup(m => m.CheckPasswordAsync(user, "Password123!"))
                .ReturnsAsync(true);
            mockUserManager.Setup(m => m.GetRolesAsync(user))
                .ReturnsAsync(new List<string> { "Administrator" });

            var mockRoleManager = TestHelpers.CreateMockRoleManager();

            var inMemoryConfig = new Dictionary<string, string?>
            {
                { "Jwt:Key", "A_Very_Long_And_Secure_Secret_Key_For_Jwt_Signing_Must_Be_At_Least_256_Bits_Long!" }
            };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemoryConfig)
                .Build();

            var controller = new AuthController(mockUserManager.Object, mockRoleManager.Object, configuration);

            var loginDto = new LoginDto
            {
                Username = "admin",
                Password = "Password123!"
            };

            // Act
            var result = await controller.Login(loginDto);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(okResult.Value);
        }

        [Fact]
        public async Task AuthController_Login_InvalidPassword_ReturnsUnauthorized()
        {
            // Arrange
            var user = new ApplicationUser
            {
                Id = "test-user-id",
                UserName = "admin",
                Email = "admin@sigetour.com"
            };

            var mockUserManager = TestHelpers.CreateMockUserManager(new List<ApplicationUser> { user });
            mockUserManager.Setup(m => m.CheckPasswordAsync(user, "WrongPassword"))
                .ReturnsAsync(false);

            var mockRoleManager = TestHelpers.CreateMockRoleManager();
            var configuration = new ConfigurationBuilder().Build();

            var controller = new AuthController(mockUserManager.Object, mockRoleManager.Object, configuration);

            var loginDto = new LoginDto
            {
                Username = "admin",
                Password = "WrongPassword"
            };

            // Act
            var result = await controller.Login(loginDto);

            // Assert
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task AuthController_SeedRolesAndAdmin_CreatesRequiredRolesAndUsers()
        {
            // Arrange
            var users = new List<ApplicationUser>();
            var mockUserManager = TestHelpers.CreateMockUserManager(users);
            mockUserManager.Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
                .ReturnsAsync(IdentityResult.Success);

            var mockRoleManager = TestHelpers.CreateMockRoleManager();
            mockRoleManager.Setup(r => r.RoleExistsAsync(It.IsAny<string>())).ReturnsAsync(false);
            mockRoleManager.Setup(r => r.CreateAsync(It.IsAny<IdentityRole>())).ReturnsAsync(IdentityResult.Success);

            var configuration = new ConfigurationBuilder().Build();

            var controller = new AuthController(mockUserManager.Object, mockRoleManager.Object, configuration);

            // Act
            var result = await controller.SeedRolesAndAdmin();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(okResult.Value);
            mockRoleManager.Verify(r => r.CreateAsync(It.Is<IdentityRole>(role => role.Name == "SuperAdmin")), Times.Once);
            mockRoleManager.Verify(r => r.CreateAsync(It.Is<IdentityRole>(role => role.Name == "Admin")), Times.Once);
            mockUserManager.Verify(u => u.CreateAsync(It.Is<ApplicationUser>(user => user.UserName == "superadmin"), "SuperAdmin123!"), Times.Once);
            mockUserManager.Verify(u => u.CreateAsync(It.Is<ApplicationUser>(user => user.UserName == "admin"), "Admin123!"), Times.Once);
        }
        #endregion

        #region TourPackagesController Tests
        [Fact]
        public async Task TourPackagesController_GetAll_ReturnsOkWithList()
        {
            // Arrange
            var mockService = new Mock<ITourPackageService>();
            var expectedList = new List<TourPackageReadDto>
            {
                new TourPackageReadDto { Id = Guid.NewGuid(), Title = "Tour 1", BasePrice = 50 },
                new TourPackageReadDto { Id = Guid.NewGuid(), Title = "Tour 2", BasePrice = 75 }
            };
            mockService.Setup(s => s.GetAllAsync()).ReturnsAsync(expectedList);

            var controller = new TourPackagesController(mockService.Object);

            // Act
            var result = await controller.GetAll();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            var returnedList = Assert.IsAssignableFrom<IEnumerable<TourPackageReadDto>>(okResult.Value);
            Assert.Equal(2, returnedList.Count());
        }

        [Fact]
        public async Task TourPackagesController_GetById_ExistingId_ReturnsOkWithDto()
        {
            // Arrange
            var tourId = Guid.NewGuid();
            var mockService = new Mock<ITourPackageService>();
            var expectedDto = new TourPackageReadDto { Id = tourId, Title = "Tour Específico", BasePrice = 60 };
            mockService.Setup(s => s.GetByIdAsync(tourId)).ReturnsAsync(expectedDto);

            var controller = new TourPackagesController(mockService.Object);

            // Act
            var result = await controller.GetById(tourId);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            var returnedDto = Assert.IsType<TourPackageReadDto>(okResult.Value);
            Assert.Equal(tourId, returnedDto.Id);
        }

        [Fact]
        public async Task TourPackagesController_GetById_NonExistingId_ReturnsNotFound()
        {
            // Arrange
            var tourId = Guid.NewGuid();
            var mockService = new Mock<ITourPackageService>();
            mockService.Setup(s => s.GetByIdAsync(tourId)).ReturnsAsync((TourPackageReadDto?)null);

            var controller = new TourPackagesController(mockService.Object);

            // Act
            var result = await controller.GetById(tourId);

            // Assert
            Assert.IsType<NotFoundObjectResult>(result.Result);
        }

        [Fact]
        public async Task TourPackagesController_Create_ValidDto_ReturnsCreatedAtAction()
        {
            // Arrange
            var mockService = new Mock<ITourPackageService>();
            var createDto = new TourPackageCreateDto { Title = "Nuevo Tour", BasePrice = 100 };
            var readDto = new TourPackageReadDto { Id = Guid.NewGuid(), Title = "Nuevo Tour", BasePrice = 100 };

            mockService.Setup(s => s.CreateAsync(createDto)).ReturnsAsync(readDto);

            var controller = new TourPackagesController(mockService.Object);

            // Act
            var result = await controller.Create(createDto);

            // Assert
            var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
            Assert.Equal(nameof(controller.GetById), createdResult.ActionName);
            var returnedDto = Assert.IsType<TourPackageReadDto>(createdResult.Value);
            Assert.Equal(readDto.Id, returnedDto.Id);
        }

        [Fact]
        public async Task TourPackagesController_Update_ExistingId_ReturnsNoContent()
        {
            // Arrange
            var tourId = Guid.NewGuid();
            var updateDto = new TourPackageUpdateDto { Title = "Tour Modificado", BasePrice = 120 };
            var mockService = new Mock<ITourPackageService>();
            mockService.Setup(s => s.UpdateAsync(tourId, updateDto)).ReturnsAsync(true);

            var controller = new TourPackagesController(mockService.Object);

            // Act
            var result = await controller.Update(tourId, updateDto);

            // Assert
            Assert.IsType<NoContentResult>(result);
        }

        [Fact]
        public async Task TourPackagesController_Update_NonExistingId_ReturnsNotFound()
        {
            // Arrange
            var tourId = Guid.NewGuid();
            var updateDto = new TourPackageUpdateDto { Title = "Tour Modificado", BasePrice = 120 };
            var mockService = new Mock<ITourPackageService>();
            mockService.Setup(s => s.UpdateAsync(tourId, updateDto)).ReturnsAsync(false);

            var controller = new TourPackagesController(mockService.Object);

            // Act
            var result = await controller.Update(tourId, updateDto);

            // Assert
            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task TourPackagesController_Delete_ExistingId_ReturnsNoContent()
        {
            // Arrange
            var tourId = Guid.NewGuid();
            var mockService = new Mock<ITourPackageService>();
            mockService.Setup(s => s.DeleteAsync(tourId)).ReturnsAsync(true);

            var controller = new TourPackagesController(mockService.Object);

            // Act
            var result = await controller.Delete(tourId);

            // Assert
            Assert.IsType<NoContentResult>(result);
        }

        [Fact]
        public async Task TourPackagesController_Delete_NonExistingId_ReturnsNotFound()
        {
            // Arrange
            var tourId = Guid.NewGuid();
            var mockService = new Mock<ITourPackageService>();
            mockService.Setup(s => s.DeleteAsync(tourId)).ReturnsAsync(false);

            var controller = new TourPackagesController(mockService.Object);

            // Act
            var result = await controller.Delete(tourId);

            // Assert
            Assert.IsType<NotFoundObjectResult>(result);
        }
        #endregion
    }
}
