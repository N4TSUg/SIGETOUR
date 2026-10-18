using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.ViewModels;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Controllers
{
    public class AccountControllerTests
    {
        [Fact]
        public void Login_Get_UnauthenticatedUser_ReturnsView()
        {
            // Arrange
            var mockUserManager = TestHelpers.CreateMockUserManager();
            var mockSignInManager = TestHelpers.CreateMockSignInManager(mockUserManager);
            mockSignInManager.Setup(s => s.IsSignedIn(It.IsAny<ClaimsPrincipal>())).Returns(false);

            var controller = new AccountController(mockSignInManager.Object, mockUserManager.Object);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            // Act
            var result = controller.Login();

            // Assert
            Assert.IsType<ViewResult>(result);
        }

        [Fact]
        public void Login_Get_AlreadySignedIn_RedirectsToDashboard()
        {
            // Arrange
            var mockUserManager = TestHelpers.CreateMockUserManager();
            var mockSignInManager = TestHelpers.CreateMockSignInManager(mockUserManager);
            mockSignInManager.Setup(s => s.IsSignedIn(It.IsAny<ClaimsPrincipal>())).Returns(true);

            var controller = new AccountController(mockSignInManager.Object, mockUserManager.Object);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            // Act
            var result = controller.Login();

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Dashboard", redirectResult.ActionName);
            Assert.Equal("Admin", redirectResult.ControllerName);
        }

        [Fact]
        public void Login_Get_SetsNoCacheHeaders()
        {
            // Arrange
            var mockUserManager = TestHelpers.CreateMockUserManager();
            var mockSignInManager = TestHelpers.CreateMockSignInManager(mockUserManager);
            mockSignInManager.Setup(s => s.IsSignedIn(It.IsAny<ClaimsPrincipal>())).Returns(false);

            var controller = new AccountController(mockSignInManager.Object, mockUserManager.Object);
            var httpContext = new DefaultHttpContext();
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };

            // Act
            controller.Login();

            // Assert
            Assert.Equal("no-cache, no-store, must-revalidate", httpContext.Response.Headers["Cache-Control"].ToString());
            Assert.Equal("no-cache", httpContext.Response.Headers["Pragma"].ToString());
            Assert.Equal("0", httpContext.Response.Headers["Expires"].ToString());
        }

        [Fact]
        public async Task Login_Post_ValidCredentials_SignsInAndRedirectsToDashboard()
        {
            // Arrange
            var user = new ApplicationUser
            {
                Id = "admin-id",
                UserName = "admin@sigetour.com",
                Email = "admin@sigetour.com"
            };

            var mockUserManager = TestHelpers.CreateMockUserManager(new List<ApplicationUser> { user });
            var mockSignInManager = TestHelpers.CreateMockSignInManager(mockUserManager);

            mockSignInManager.Setup(s => s.PasswordSignInAsync("admin@sigetour.com", "Password123!", false, false))
                .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Success);

            var controller = new AccountController(mockSignInManager.Object, mockUserManager.Object);

            var model = new LoginViewModel
            {
                EmailOrUsername = "admin@sigetour.com",
                Password = "Password123!",
                RememberMe = false
            };

            // Act
            var result = await controller.Login(model);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Dashboard", redirectResult.ActionName);
            Assert.Equal("Admin", redirectResult.ControllerName);
        }

        [Fact]
        public async Task Login_Post_InvalidCredentials_ReturnsViewWithModelError()
        {
            // Arrange
            var user = new ApplicationUser
            {
                Id = "admin-id",
                UserName = "admin@sigetour.com",
                Email = "admin@sigetour.com"
            };

            var mockUserManager = TestHelpers.CreateMockUserManager(new List<ApplicationUser> { user });
            var mockSignInManager = TestHelpers.CreateMockSignInManager(mockUserManager);

            mockSignInManager.Setup(s => s.PasswordSignInAsync("admin@sigetour.com", "WrongPassword", false, false))
                .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Failed);

            var controller = new AccountController(mockSignInManager.Object, mockUserManager.Object);

            var model = new LoginViewModel
            {
                EmailOrUsername = "admin@sigetour.com",
                Password = "WrongPassword",
                RememberMe = false
            };

            // Act
            var result = await controller.Login(model);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Equal(model, viewResult.Model);
            Assert.False(controller.ModelState.IsValid);
        }

        [Fact]
        public async Task Logout_SignsOutAndRedirectsToHome()
        {
            // Arrange
            var mockUserManager = TestHelpers.CreateMockUserManager();
            var mockSignInManager = TestHelpers.CreateMockSignInManager(mockUserManager);
            mockSignInManager.Setup(s => s.SignOutAsync()).Returns(Task.CompletedTask);

            var controller = new AccountController(mockSignInManager.Object, mockUserManager.Object);

            // Act
            var result = await controller.Logout();

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Index", redirectResult.ActionName);
            Assert.Equal("Home", redirectResult.ControllerName);
            mockSignInManager.Verify(s => s.SignOutAsync(), Times.Once);
        }
    }
}
