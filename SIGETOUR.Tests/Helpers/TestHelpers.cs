using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Moq;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;

namespace SIGETOUR.Tests.Helpers
{
    public static class TestHelpers
    {
        public static Mock<UserManager<ApplicationUser>> CreateMockUserManager(List<ApplicationUser>? users = null)
        {
            var userStore = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new Mock<UserManager<ApplicationUser>>(
                userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

            users ??= new List<ApplicationUser>();
            userManager.Setup(m => m.Users).Returns(users.AsAsyncQueryable());

            userManager.Setup(m => m.FindByIdAsync(It.IsAny<string>()))
                .ReturnsAsync((string id) => users.FirstOrDefault(u => u.Id == id));

            userManager.Setup(m => m.FindByNameAsync(It.IsAny<string>()))
                .ReturnsAsync((string name) => users.FirstOrDefault(u => u.UserName == name));

            userManager.Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
                .ReturnsAsync((string email) => users.FirstOrDefault(u => u.Email == email));

            userManager.Setup(m => m.GetRolesAsync(It.IsAny<ApplicationUser>()))
                .ReturnsAsync(new List<string> { "Administrator" });

            userManager.Setup(m => m.AddToRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
                .ReturnsAsync(IdentityResult.Success);

            userManager.Setup(m => m.RemoveFromRolesAsync(It.IsAny<ApplicationUser>(), It.IsAny<IEnumerable<string>>()))
                .ReturnsAsync(IdentityResult.Success);

            return userManager;
        }

        public static Mock<RoleManager<IdentityRole>> CreateMockRoleManager(List<IdentityRole>? roles = null)
        {
            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var roleManager = new Mock<RoleManager<IdentityRole>>(
                roleStore.Object, null!, null!, null!, null!);

            roles ??= new List<IdentityRole>
            {
                new IdentityRole { Id = "1", Name = "SuperAdmin", NormalizedName = "SUPERADMIN" },
                new IdentityRole { Id = "2", Name = "Administrator", NormalizedName = "ADMINISTRATOR" },
                new IdentityRole { Id = "3", Name = "Guide", NormalizedName = "GUIDE" }
            };
            roleManager.Setup(m => m.Roles).Returns(roles.AsAsyncQueryable());
            roleManager.Setup(m => m.RoleExistsAsync(It.IsAny<string>())).ReturnsAsync(true);

            return roleManager;
        }

        public static Mock<SignInManager<ApplicationUser>> CreateMockSignInManager(
            Mock<UserManager<ApplicationUser>> userManager)
        {
            var contextAccessor = new Mock<IHttpContextAccessor>();
            var claimsFactory = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();
            var signInManager = new Mock<SignInManager<ApplicationUser>>(
                userManager.Object,
                contextAccessor.Object,
                claimsFactory.Object,
                null!, null!, null!, null!);

            return signInManager;
        }

        public static Mock<IWebHostEnvironment> CreateMockEnvironment()
        {
            var mockEnv = new Mock<IWebHostEnvironment>();
            var tempPath = Path.Combine(Path.GetTempPath(), "sigetour_test_wwwroot");
            Directory.CreateDirectory(tempPath);
            mockEnv.Setup(m => m.WebRootPath).Returns(tempPath);
            return mockEnv;
        }

        public static AdminController CreateAdminController(
            AppDbContext context,
            Mock<UserManager<ApplicationUser>>? userManager = null,
            Mock<RoleManager<IdentityRole>>? roleManager = null,
            Mock<IWebHostEnvironment>? env = null,
            Mock<IManifestExportService>? manifestService = null,
            Mock<ICashReportExportService>? cashReportService = null,
            string currentUserId = "current-admin-id",
            string currentUserName = "admin@sigetour.com")
        {
            userManager ??= CreateMockUserManager();
            roleManager ??= CreateMockRoleManager();
            env ??= CreateMockEnvironment();
            manifestService ??= new Mock<IManifestExportService>();
            cashReportService ??= new Mock<ICashReportExportService>();

            var controller = new AdminController(
                context,
                userManager.Object,
                roleManager.Object,
                env.Object,
                manifestService.Object,
                cashReportService.Object);

            var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, currentUserId),
                new Claim(ClaimTypes.Name, currentUserName),
                new Claim(ClaimTypes.Role, "SuperAdmin")
            }, "TestAuth"));

            var httpContext = new DefaultHttpContext { User = user };
            var tempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());

            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            controller.TempData = tempData;

            return controller;
        }
    }
}
