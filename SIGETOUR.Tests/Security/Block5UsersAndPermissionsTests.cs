using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using SIGETOUR.API.Controllers;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Core.Security;
using SIGETOUR.API.Infrastructure.Security;
using SIGETOUR.API.Models;
using SIGETOUR.API.ViewModels;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Security
{
    public class Block5UsersAndPermissionsTests
    {
        private readonly IConfiguration _configuration;

        public Block5UsersAndPermissionsTests()
        {
            var inMemorySettings = new Dictionary<string, string>
            {
                { "Jwt:Key", "this_is_a_very_long_test_signing_key_for_block5_security_verification_256bits!" },
                { "Jwt:Issuer", "SIGETOUR_TEST" },
                { "Jwt:Audience", "SIGETOUR_CLIENT" }
            };

            _configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings!)
                .Build();
        }

        [Fact]
        public async Task AuthController_Seed_AnonymousWhenSuperAdminExists_Returns401Unauthorized()
        {
            // Arrange
            var userStore = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new Mock<UserManager<ApplicationUser>>(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);
            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var roleManager = new Mock<RoleManager<IdentityRole>>(roleStore.Object, null!, null!, null!, null!);

            // Simula que ya existe al menos un SuperAdmin en la base de datos
            userManager.Setup(m => m.GetUsersInRoleAsync("SuperAdmin"))
                .ReturnsAsync(new List<ApplicationUser>
                {
                    new ApplicationUser { UserName = "superadmin", Email = "superadmin@sigetour.com", IsActive = true }
                });

            var controller = new AuthController(userManager.Object, roleManager.Object, _configuration);
            // Usuario anónimo (no autenticado)
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
            };

            // Act
            var result = await controller.SeedRolesAndAdmin();

            // Assert
            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal(401, unauthResult.StatusCode);
            Assert.Contains("protegido contra acceso público", unauthResult.Value?.ToString() ?? "");
        }

        [Fact]
        public async Task AuthController_Seed_AuthenticatedSuperAdmin_ExecutesSeedSuccessfully()
        {
            // Arrange
            var userStore = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new Mock<UserManager<ApplicationUser>>(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);
            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var roleManager = new Mock<RoleManager<IdentityRole>>(roleStore.Object, null!, null!, null!, null!);

            userManager.Setup(m => m.GetUsersInRoleAsync("SuperAdmin"))
                .ReturnsAsync(new List<ApplicationUser>
                {
                    new ApplicationUser { UserName = "superadmin", Email = "superadmin@sigetour.com", IsActive = true }
                });

            roleManager.Setup(m => m.RoleExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
            userManager.Setup(m => m.FindByNameAsync(It.IsAny<string>()))
                .ReturnsAsync(new ApplicationUser { UserName = "existing" });

            var controller = new AuthController(userManager.Object, roleManager.Object, _configuration);
            // Usuario autenticado como SuperAdmin
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "superadmin"),
                new Claim(ClaimTypes.Role, "SuperAdmin")
            }, "TestAuth");

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            };

            // Act
            var result = await controller.SeedRolesAndAdmin();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, okResult.StatusCode);
        }

        [Fact]
        public async Task AuthController_Login_InactiveUser_Returns401Unauthorized()
        {
            // Arrange
            var userStore = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new Mock<UserManager<ApplicationUser>>(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);
            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var roleManager = new Mock<RoleManager<IdentityRole>>(roleStore.Object, null!, null!, null!, null!);

            var suspendedUser = new ApplicationUser
            {
                Id = "suspended-1",
                UserName = "operador_suspendido",
                Email = "suspendido@sigetour.com",
                IsActive = false
            };

            userManager.Setup(m => m.FindByNameAsync("operador_suspendido")).ReturnsAsync(suspendedUser);
            userManager.Setup(m => m.CheckPasswordAsync(suspendedUser, "Password123!")).ReturnsAsync(true);

            var controller = new AuthController(userManager.Object, roleManager.Object, _configuration);

            // Act
            var result = await controller.Login(new LoginDto
            {
                Username = "operador_suspendido",
                Password = "Password123!"
            });

            // Assert
            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal(401, unauthResult.StatusCode);
            Assert.Contains("desactivada", unauthResult.Value?.ToString() ?? "");
        }

        [Fact]
        public async Task AuthController_Login_ActiveAdmin_GeneratesJwtWithRoleAndPermissions()
        {
            // Arrange
            var userStore = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new Mock<UserManager<ApplicationUser>>(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);
            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var roleManager = new Mock<RoleManager<IdentityRole>>(roleStore.Object, null!, null!, null!, null!);

            var activeAdmin = new ApplicationUser
            {
                Id = "gerente-1",
                UserName = "gerente",
                Email = "gerente@sigetour.com",
                IsActive = true
            };

            userManager.Setup(m => m.FindByNameAsync("gerente")).ReturnsAsync(activeAdmin);
            userManager.Setup(m => m.CheckPasswordAsync(activeAdmin, "Gerente2026!")).ReturnsAsync(true);
            userManager.Setup(m => m.GetRolesAsync(activeAdmin)).ReturnsAsync(new List<string> { "Admin" });

            var controller = new AuthController(userManager.Object, roleManager.Object, _configuration);

            // Act
            var result = await controller.Login(new LoginDto
            {
                Username = "gerente",
                Password = "Gerente2026!"
            });

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, okResult.StatusCode);

            // Parse JWT token
            var tokenString = (string?)okResult.Value?.GetType().GetProperty("token")?.GetValue(okResult.Value);
            Assert.NotNull(tokenString);

            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(tokenString);

            // Validar claims generados
            Assert.Contains(jwt.Claims, c => c.Type == "IsActive" && c.Value == "true");
            Assert.Contains(jwt.Claims, c => c.Type == ClaimTypes.Role && c.Value == "Admin");
            // Admin operativo debe tener permisos de Bookings y Cash, pero NO de Users ni Supervision
            Assert.Contains(jwt.Claims, c => c.Type == "Permission" && c.Value == AppPermissions.BookingsManage);
            Assert.Contains(jwt.Claims, c => c.Type == "Permission" && c.Value == AppPermissions.CashManage);
            Assert.DoesNotContain(jwt.Claims, c => c.Type == "Permission" && c.Value == AppPermissions.UsersManage);
            Assert.DoesNotContain(jwt.Claims, c => c.Type == "Permission" && c.Value == AppPermissions.SupervisionManage);
        }

        [Fact]
        public async Task AppUserClaimsPrincipalFactory_GeneratesClaims_ForRoleAndActiveStatus()
        {
            // Arrange
            var userStore = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new Mock<UserManager<ApplicationUser>>(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);
            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var roleManager = new Mock<RoleManager<IdentityRole>>(roleStore.Object, null!, null!, null!, null!);

            var optionsAccessor = Options.Create(new IdentityOptions());

            var operatorUser = new ApplicationUser
            {
                Id = "op-1",
                UserName = "counter",
                Email = "counter@sigetour.com",
                IsActive = true
            };

            userManager.Setup(m => m.GetUserIdAsync(operatorUser)).ReturnsAsync(operatorUser.Id);
            userManager.Setup(m => m.GetUserNameAsync(operatorUser)).ReturnsAsync(operatorUser.UserName);
            userManager.Setup(m => m.GetRolesAsync(operatorUser)).ReturnsAsync(new List<string> { "Operador" });
            userManager.Setup(m => m.GetClaimsAsync(operatorUser)).ReturnsAsync(new List<Claim>());

            var factory = new AppUserClaimsPrincipalFactory(userManager.Object, roleManager.Object, optionsAccessor);

            // Act
            var principal = await factory.CreateAsync(operatorUser);

            // Assert
            Assert.NotNull(principal);
            Assert.Contains(principal.Claims, c => c.Type == "IsActive" && c.Value == "true");
            Assert.Contains(principal.Claims, c => c.Type == ClaimTypes.Role && c.Value == "Operador");
            // Operador tiene Bookings.View y Bookings.Manage, pero NO Cash.Manage ni Users.Manage
            Assert.Contains(principal.Claims, c => c.Type == "Permission" && c.Value == AppPermissions.BookingsView);
            Assert.Contains(principal.Claims, c => c.Type == "Permission" && c.Value == AppPermissions.BookingsManage);
            Assert.DoesNotContain(principal.Claims, c => c.Type == "Permission" && c.Value == AppPermissions.CashManage);
            Assert.DoesNotContain(principal.Claims, c => c.Type == "Permission" && c.Value == AppPermissions.UsersManage);
        }

        [Fact]
        public async Task AdminController_Users_LoadsDynamicRolesCatalog()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var rolesList = new List<IdentityRole>
            {
                new IdentityRole { Id = "1", Name = "SuperAdmin", NormalizedName = "SUPERADMIN" },
                new IdentityRole { Id = "2", Name = "Admin", NormalizedName = "ADMIN" },
                new IdentityRole { Id = "3", Name = "Operador", NormalizedName = "OPERADOR" },
                new IdentityRole { Id = "4", Name = "GuiaTuristico", NormalizedName = "GUIATURISTICO" },
                new IdentityRole { Id = "5", Name = "AuditorFinanciero", NormalizedName = "AUDITORFINANCIERO" }
            };

            var roleManagerMock = new Mock<RoleManager<IdentityRole>>(
                new Mock<IRoleStore<IdentityRole>>().Object, null!, null!, null!, null!);
            roleManagerMock.Setup(m => m.Roles).Returns(rolesList.AsAsyncQueryable());
            roleManagerMock.Setup(m => m.RoleExistsAsync(It.IsAny<string>())).ReturnsAsync(true);

            var userManagerMock = TestHelpers.CreateMockUserManager();
            var controller = TestHelpers.CreateAdminController(context, userManager: userManagerMock, roleManager: roleManagerMock);

            // Act
            var result = await controller.Users(search: null, page: 1);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var availableRoles = Assert.IsAssignableFrom<List<string>>(controller.ViewBag.AvailableRoles);
            Assert.Contains("GuiaTuristico", availableRoles);
            Assert.Contains("AuditorFinanciero", availableRoles);
            Assert.Contains("SuperAdmin", availableRoles);
            Assert.Contains("Admin", availableRoles);
        }

        [Fact]
        public async Task AdminController_CreateRole_SuperAdminRegistersNewCustomRole()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var rolesList = new List<IdentityRole>
            {
                new IdentityRole { Id = "1", Name = "SuperAdmin", NormalizedName = "SUPERADMIN" },
                new IdentityRole { Id = "2", Name = "Admin", NormalizedName = "ADMIN" },
                new IdentityRole { Id = "3", Name = "Operador", NormalizedName = "OPERADOR" }
            };

            var roleManagerMock = new Mock<RoleManager<IdentityRole>>(
                new Mock<IRoleStore<IdentityRole>>().Object, null!, null!, null!, null!);
            roleManagerMock.Setup(m => m.RoleExistsAsync("SupervisorFlota")).ReturnsAsync(false);
            roleManagerMock.Setup(m => m.CreateAsync(It.IsAny<IdentityRole>()))
                .Callback<IdentityRole>(r => rolesList.Add(r))
                .ReturnsAsync(IdentityResult.Success);

            var controller = TestHelpers.CreateAdminController(context, roleManager: roleManagerMock);

            // Act
            var result = await controller.CreateRole("SupervisorFlota");

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Users", redirect.ActionName);
            Assert.Contains("SupervisorFlota", controller.TempData["SuccessMessage"]?.ToString() ?? "");
            roleManagerMock.Verify(m => m.CreateAsync(It.Is<IdentityRole>(r => r.Name == "SupervisorFlota")), Times.Once);
        }

        [Fact]
        public async Task AdminController_CreateUser_AllowsCustomRoleCreationAndAssignment()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var userManagerMock = TestHelpers.CreateMockUserManager();
            var roleManagerMock = TestHelpers.CreateMockRoleManager();

            roleManagerMock.Setup(m => m.RoleExistsAsync("GuiaOficial")).ReturnsAsync(false);
            roleManagerMock.Setup(m => m.CreateAsync(It.IsAny<IdentityRole>())).ReturnsAsync(IdentityResult.Success);

            userManagerMock.Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
                .ReturnsAsync(IdentityResult.Success);
            userManagerMock.Setup(m => m.AddToRoleAsync(It.IsAny<ApplicationUser>(), "GuiaOficial"))
                .ReturnsAsync(IdentityResult.Success);

            var controller = TestHelpers.CreateAdminController(context, userManager: userManagerMock, roleManager: roleManagerMock);

            var model = new CreateUserViewModel
            {
                FirstName = "Carlos",
                LastName = "Guía",
                UserName = "carlos_guia",
                Email = "carlos.guia@sigetour.com",
                Role = "GuiaOficial",
                Password = "Password2026!",
                IsActive = true
            };

            // Act
            var result = await controller.CreateUser(model);

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Users", redirect.ActionName);
            userManagerMock.Verify(m => m.AddToRoleAsync(It.IsAny<ApplicationUser>(), "GuiaOficial"), Times.Once);
        }

        [Fact]
        public async Task AdminController_EditUser_DeactivatingLastActiveSuperAdmin_IsDenied()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var lastSuperAdmin = new ApplicationUser
            {
                Id = "sa-1",
                UserName = "superadmin",
                Email = "superadmin@sigetour.com",
                IsActive = true,
                FirstName = "Super",
                LastName = "Admin"
            };

            var userManagerMock = TestHelpers.CreateMockUserManager(new List<ApplicationUser> { lastSuperAdmin });
            userManagerMock.Setup(m => m.FindByIdAsync("sa-1")).ReturnsAsync(lastSuperAdmin);
            userManagerMock.Setup(m => m.IsInRoleAsync(lastSuperAdmin, "SuperAdmin")).ReturnsAsync(true);
            userManagerMock.Setup(m => m.GetUsersInRoleAsync("SuperAdmin"))
                .ReturnsAsync(new List<ApplicationUser> { lastSuperAdmin });

            var controller = TestHelpers.CreateAdminController(context, userManager: userManagerMock, currentUserId: "sa-1", currentUserName: "superadmin");

            var editModel = new EditUserViewModel
            {
                Id = "sa-1",
                FirstName = "Super",
                LastName = "Admin",
                UserName = "superadmin",
                Email = "superadmin@sigetour.com",
                Role = "SuperAdmin",
                IsActive = false // Intento de desactivar al único SuperAdmin activo
            };

            // Act
            var result = await controller.EditUser(editModel);

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Users", redirect.ActionName);
            Assert.Contains("No es posible desactivar al último SuperAdmin activo", controller.TempData["ErrorMessage"]?.ToString() ?? "");
            // No debe haberse actualizado a inactivo en la base de datos
            userManagerMock.Verify(m => m.UpdateAsync(It.Is<ApplicationUser>(u => !u.IsActive)), Times.Never);
        }

        [Fact]
        public async Task AdminController_EditUser_DemotingLastActiveSuperAdmin_IsDenied()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var lastSuperAdmin = new ApplicationUser
            {
                Id = "sa-1",
                UserName = "superadmin_last",
                Email = "superadmin_last@sigetour.com",
                IsActive = true,
                FirstName = "Super",
                LastName = "Admin"
            };

            var userManagerMock = TestHelpers.CreateMockUserManager(new List<ApplicationUser> { lastSuperAdmin });
            userManagerMock.Setup(m => m.FindByIdAsync("sa-1")).ReturnsAsync(lastSuperAdmin);
            userManagerMock.Setup(m => m.IsInRoleAsync(lastSuperAdmin, "SuperAdmin")).ReturnsAsync(true);
            userManagerMock.Setup(m => m.GetRolesAsync(lastSuperAdmin)).ReturnsAsync(new List<string> { "SuperAdmin" });
            userManagerMock.Setup(m => m.GetUsersInRoleAsync("SuperAdmin"))
                .ReturnsAsync(new List<ApplicationUser> { lastSuperAdmin });
            userManagerMock.Setup(m => m.UpdateAsync(It.IsAny<ApplicationUser>()))
                .ReturnsAsync(IdentityResult.Success);

            var controller = TestHelpers.CreateAdminController(context, userManager: userManagerMock, currentUserId: "sa-root", currentUserName: "superadmin");

            var editModel = new EditUserViewModel
            {
                Id = "sa-1",
                FirstName = "Super",
                LastName = "Admin",
                UserName = "superadmin_last",
                Email = "superadmin_last@sigetour.com",
                Role = "Operador", // Intento de degradar rol del último SuperAdmin
                IsActive = true
            };

            // Act
            var result = await controller.EditUser(editModel);

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Users", redirect.ActionName);
            Assert.Contains("No es posible degradar el rol del último SuperAdmin activo", controller.TempData["ErrorMessage"]?.ToString() ?? "");
            // No debe llamarse RemoveFromRolesAsync
            userManagerMock.Verify(m => m.RemoveFromRolesAsync(It.IsAny<ApplicationUser>(), It.IsAny<IEnumerable<string>>()), Times.Never);
        }

        [Fact]
        public async Task AdminController_EditUser_Deactivation_UpdatesSecurityStamp_ForImmediateSessionInvalidation()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var targetUser = new ApplicationUser
            {
                Id = "user-to-deactivate",
                UserName = "counter1",
                Email = "counter1@sigetour.com",
                IsActive = true,
                FirstName = "Counter",
                LastName = "Uno"
            };

            var userManagerMock = TestHelpers.CreateMockUserManager(new List<ApplicationUser> { targetUser });
            userManagerMock.Setup(m => m.FindByIdAsync("user-to-deactivate")).ReturnsAsync(targetUser);
            userManagerMock.Setup(m => m.IsInRoleAsync(targetUser, "SuperAdmin")).ReturnsAsync(false);
            userManagerMock.Setup(m => m.GetRolesAsync(targetUser)).ReturnsAsync(new List<string> { "Operador" });
            userManagerMock.Setup(m => m.UpdateAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(IdentityResult.Success);
            userManagerMock.Setup(m => m.UpdateSecurityStampAsync(targetUser)).ReturnsAsync(IdentityResult.Success);

            var controller = TestHelpers.CreateAdminController(context, userManager: userManagerMock);

            var editModel = new EditUserViewModel
            {
                Id = "user-to-deactivate",
                FirstName = "Counter",
                LastName = "Uno",
                UserName = "counter1",
                Email = "counter1@sigetour.com",
                Role = "Operador",
                IsActive = false // Desactivación
            };

            // Act
            var result = await controller.EditUser(editModel);

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Users", redirect.ActionName);
            // Verifica que UpdateSecurityStampAsync haya sido invocado para invalidar inmediatamente cookies y tokens activos
            userManagerMock.Verify(m => m.UpdateSecurityStampAsync(targetUser), Times.Once);
        }

        [Fact]
        public async Task AdminController_DeleteUser_DeletingLastActiveSuperAdmin_IsDenied()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var lastSuperAdmin = new ApplicationUser
            {
                Id = "sa-unique",
                UserName = "superadmin_unique",
                IsActive = true
            };

            var userManagerMock = TestHelpers.CreateMockUserManager(new List<ApplicationUser> { lastSuperAdmin });
            userManagerMock.Setup(m => m.FindByIdAsync("sa-unique")).ReturnsAsync(lastSuperAdmin);
            userManagerMock.Setup(m => m.IsInRoleAsync(lastSuperAdmin, "SuperAdmin")).ReturnsAsync(true);
            userManagerMock.Setup(m => m.GetUsersInRoleAsync("SuperAdmin"))
                .ReturnsAsync(new List<ApplicationUser> { lastSuperAdmin });

            var controller = TestHelpers.CreateAdminController(context, userManager: userManagerMock, currentUserId: "different-caller-id", currentUserName: "auditor");

            // Act
            var result = await controller.DeleteUser("sa-unique");

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Users", redirect.ActionName);
            Assert.Contains("No es posible eliminar al último SuperAdmin activo", controller.TempData["ErrorMessage"]?.ToString() ?? "");
            userManagerMock.Verify(m => m.DeleteAsync(It.IsAny<ApplicationUser>()), Times.Never);
        }

        [Fact]
        public async Task AdminController_DeleteUser_DeletingSelf_IsDenied()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var loggedUser = new ApplicationUser
            {
                Id = "logged-in-user-id",
                UserName = "admin_actual",
                IsActive = true
            };

            var userManagerMock = TestHelpers.CreateMockUserManager(new List<ApplicationUser> { loggedUser });
            userManagerMock.Setup(m => m.FindByIdAsync("logged-in-user-id")).ReturnsAsync(loggedUser);

            var controller = TestHelpers.CreateAdminController(context, userManager: userManagerMock, currentUserId: "logged-in-user-id", currentUserName: "admin_actual");

            // Act
            var result = await controller.DeleteUser("logged-in-user-id");

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Users", redirect.ActionName);
            Assert.Contains("No puedes eliminar tu propia cuenta", controller.TempData["ErrorMessage"]?.ToString() ?? "");
            userManagerMock.Verify(m => m.DeleteAsync(It.IsAny<ApplicationUser>()), Times.Never);
        }

        [Fact]
        public void ControllerActions_HaveServerSideAuthorizationAttributes_NotRelyingOnUi()
        {
            // Arrange & Act
            var adminType = typeof(AdminController);

            // 1. Acciones exclusivas de SuperAdmin / Gestión de Usuarios y Roles
            var superAdminActions = new[] { "Users", "CreateUser", "EditUser", "DeleteUser", "CreateRole", "Supervision", "ResolveIncident" };
            foreach (var actionName in superAdminActions)
            {
                var methods = adminType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == actionName);

                Assert.NotEmpty(methods);
                foreach (var method in methods)
                {
                    var authorizeAttr = method.GetCustomAttribute<AuthorizeAttribute>();
                    Assert.True(authorizeAttr != null, $"El método {actionName} en AdminController debe contar con [Authorize]");
                    Assert.True(
                        authorizeAttr.Roles == "SuperAdmin" ||
                        authorizeAttr.Policy == AppPermissions.UsersManage ||
                        authorizeAttr.Policy == AppPermissions.RolesManage ||
                        authorizeAttr.Policy == AppPermissions.SupervisionView ||
                        authorizeAttr.Policy == AppPermissions.IncidentsManage,
                        $"El método {actionName} debe requerir rol SuperAdmin o una política efectiva de seguridad/supervisión.");
                }
            }

            // 2. Acciones operativas y financieras protegidas mediante permisos y políticas
            var operationalActions = new[]
            {
                "RefundBookingPayment",
                "DeleteBooking",
                "UpdateCashTransaction",
                "DeleteCashTransaction",
                "UpdatePettyCashMovement",
                "DeletePettyCashMovement",
                "UpdateCashShift",
                "RecoverPendingReceipts"
            };

            foreach (var actionName in operationalActions)
            {
                var methods = adminType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == actionName);

                Assert.NotEmpty(methods);
                foreach (var method in methods)
                {
                    var authorizeAttr = method.GetCustomAttribute<AuthorizeAttribute>();
                    Assert.True(authorizeAttr != null, $"El método operativo sensible {actionName} debe contar con [Authorize]");
                    Assert.True(
                        authorizeAttr.Roles == "SuperAdmin,Admin" ||
                        authorizeAttr.Policy == AppPermissions.BookingsManage ||
                        authorizeAttr.Policy == AppPermissions.CashManage ||
                        authorizeAttr.Policy == AppPermissions.ReceiptsManage,
                        $"El método {actionName} debe requerir roles autorizados o políticas efectivas de gestión.");
                }
            }
        }
    }
}
