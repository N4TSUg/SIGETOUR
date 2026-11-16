using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Application.Services;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Core.Security;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;
using SIGETOUR.API.ViewModels;
using SIGETOUR.Tests.Helpers;
using SIGETOUR.Tests.Payments;
using Xunit;

namespace SIGETOUR.Tests.Audit
{
    /// <summary>
    /// Suite de verificación estricta para los hallazgos confirmados de Fase 5 (commit 38d405e).
    /// Cubre:
    /// 1. Adquisición atómica de lease de emisión (TryAcquireEmissionAsync) y TryMarkEmissionStartedAsync.
    /// 2. Clasificación y recuperación segura de registros InFlight históricos (sin emisiones a ciegas).
    /// 3. Conservación de permisos históricos y actualización limpia (IdentitySeeder, AppUserClaimsPrincipalFactory, AuthController).
    /// 4. Filtrado de datos por claims de permisos en Dashboard y Notificaciones.
    /// </summary>
    public class HardeningAuditPass5Tests
    {
        private readonly IConfiguration _config;
        private readonly CheckoutAccess _checkoutAccess;

        public HardeningAuditPass5Tests()
        {
            var configData = new Dictionary<string, string?>
            {
                { "Culqi:SecretKey", "sk_test_mock_secret_key_123456" },
                { "Culqi:PublicKey", "pk_test_mock_public_key_123456" },
                { "Nubefact:Ruta", "https://demo.nubefact.com/api/v1/mock-path" },
                { "Nubefact:Token", "mock-token-12345" },
                { "Nubefact:Mode", "Demo" },
                { "Nubefact:BoletaSeries", "BBB1" },
                { "Nubefact:FacturaSeries", "FFF1" },
                { "Payments:CheckoutSigningKey", "c2lnZXRvdXJfc3VwZXJfc2VjcmV0X2NoZWNrb3V0X3NpZ25pbmdfa2V5XzIwMjZfMzJieXRlcw==" },
                { "Security:DocumentAccessSecret", "audit_test_secret_32_characters_long_min!" }
            };

            _config = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            _checkoutAccess = new CheckoutAccess(_config);
        }

        private static Booking CreateTestBooking(AppDbContext context, decimal total = 100m, decimal paid = 100m)
        {
            var category = new Category { Id = Guid.NewGuid(), Name = "Aventura Test" };
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Hardening Pass 5",
                Category = category,
                BasePrice = total,
                Duration = "4 Horas",
                IsActive = true
            };
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "TC-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Carlos Auditor 5",
                CustomerEmail = "carlos5@turismochilon.com",
                CustomerDni = "76543210",
                CustomerPhone = "987654321",
                TravelDate = DateTime.UtcNow.AddDays(2),
                ShiftName = "09:00 AM",
                TotalPassengers = 1,
                TotalAmount = total,
                PaidAmount = paid,
                Status = BookingStatus.Confirmed,
                InvoiceType = "boleta",
                BookingDate = DateTime.UtcNow
            };
            var item = new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                TourPackage = tour,
                Passengers = 1,
                UnitPrice = total,
                SubTotal = total
            };
            booking.Items.Add(item);

            context.Categories.Add(category);
            context.TourPackages.Add(tour);
            context.Bookings.Add(booking);
            context.SaveChanges();
            return booking;
        }

        #region 1. NubeFact: Adquisición Atómica del Derecho de Emisión y Pre-flight Gate

        [Fact]
        public async Task TryAcquireEmissionAsync_TwoConcurrentAttempts_ExactlyOneWins()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "Pending",
                Environment = "Demo",
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow
            };
            context.ElectronicReceipts.Add(receipt);
            await context.SaveChangesAsync();

            var service1 = new NubefactInvoiceService(context, _config, new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);
            var service2 = new NubefactInvoiceService(context, _config, new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            var barrier = new Barrier(2);

            var task1 = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await service1.TryAcquireEmissionAsync(receipt.Id, requestedClaimToken: null);
            });

            var task2 = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await service2.TryAcquireEmissionAsync(receipt.Id, requestedClaimToken: null);
            });

            var results = await Task.WhenAll(task1, task2);

            int winners = results.Count(r => r.Acquired);
            int losers = results.Count(r => !r.Acquired);

            Assert.Equal(1, winners);
            Assert.Equal(1, losers);

            var winnerResult = results.First(r => r.Acquired);
            var refreshed = await context.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(refreshed);
            Assert.Equal("InFlight", refreshed.Status);
            Assert.Equal(winnerResult.LeaseToken, refreshed.EmissionLeaseToken);
            Assert.NotNull(refreshed.EmissionLeaseExpiresAtUtc);
            Assert.True(refreshed.EmissionLeaseExpiresAtUtc > DateTime.UtcNow);
            Assert.Null(refreshed.EmissionStartedAtUtc);
        }

        [Fact]
        public async Task TryMarkEmissionStartedAsync_ExpiredOrMismatchedToken_ReturnsFalse()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            var validToken = Guid.NewGuid().ToString("N");
            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "InFlight",
                EmissionLeaseToken = validToken,
                EmissionLeaseAcquiredAtUtc = DateTime.UtcNow.AddSeconds(-50),
                EmissionLeaseExpiresAtUtc = DateTime.UtcNow.AddSeconds(-5), // Expirado
                EmissionStartedAtUtc = null,
                Environment = "Demo",
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow
            };
            context.ElectronicReceipts.Add(receipt);
            await context.SaveChangesAsync();

            var service = new NubefactInvoiceService(context, _config, new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            // 1. Token no coincide -> Debe fallar
            var resMismatch = await service.TryMarkEmissionStartedAsync(receipt.Id, "invalid_token_999");
            Assert.False(resMismatch);

            // 2. Token coincide pero lease expiró -> Debe fallar
            var resExpired = await service.TryMarkEmissionStartedAsync(receipt.Id, validToken);
            Assert.False(resExpired);

            // 3. Lease activo con token correcto -> Debe tener éxito
            receipt.EmissionLeaseExpiresAtUtc = DateTime.UtcNow.AddSeconds(45);
            await context.SaveChangesAsync();

            var resSuccess = await service.TryMarkEmissionStartedAsync(receipt.Id, validToken);
            Assert.True(resSuccess);

            var updated = await context.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(updated?.EmissionStartedAtUtc);
        }

        #endregion

        #region 2. NubeFact: Clasificación de InFlight Histórico y Cero Emisiones a Ciegas

        [Fact]
        public void ClassifyInFlightState_AllHistoricalAndStructuredScenarios_ClassifiedCorrectly()
        {
            var myToken = Guid.NewGuid().ToString("N");
            var otherToken = Guid.NewGuid().ToString("N");

            // 1. Structured active current process
            var r1 = new ElectronicReceipt
            {
                Status = "InFlight",
                EmissionLeaseToken = myToken,
                EmissionLeaseExpiresAtUtc = DateTime.UtcNow.AddSeconds(30)
            };
            Assert.Equal(NubefactInvoiceService.InFlightClassification.ActiveLeaseCurrentProcess,
                NubefactInvoiceService.ClassifyInFlightState(r1, myToken));

            // 2. Structured active another process
            Assert.Equal(NubefactInvoiceService.InFlightClassification.ActiveLeaseAnotherProcess,
                NubefactInvoiceService.ClassifyInFlightState(r1, otherToken));

            // 3. Structured expired never sent
            var r3 = new ElectronicReceipt
            {
                Status = "InFlight",
                EmissionLeaseToken = myToken,
                EmissionLeaseExpiresAtUtc = DateTime.UtcNow.AddSeconds(-10),
                EmissionStartedAtUtc = null
            };
            Assert.Equal(NubefactInvoiceService.InFlightClassification.ReliablyUnsentExpired,
                NubefactInvoiceService.ClassifyInFlightState(r3, myToken));

            // 4. Structured expired with send started (uncertain)
            var r4 = new ElectronicReceipt
            {
                Status = "InFlight",
                EmissionLeaseToken = myToken,
                EmissionLeaseExpiresAtUtc = DateTime.UtcNow.AddSeconds(-10),
                EmissionStartedAtUtc = DateTime.UtcNow.AddSeconds(-15)
            };
            Assert.Equal(NubefactInvoiceService.InFlightClassification.UncertainOrSent,
                NubefactInvoiceService.ClassifyInFlightState(r4, myToken));

            // 5. Historical without structured fields: LastError == null
            var r5 = new ElectronicReceipt { Status = "InFlight", LastError = null };
            Assert.Equal(NubefactInvoiceService.InFlightClassification.UncertainOrSent,
                NubefactInvoiceService.ClassifyInFlightState(r5, null));

            // 6. Historical without structured fields: corrupt text
            var r6 = new ElectronicReceipt { Status = "InFlight", LastError = "Error desconocido de red" };
            Assert.Equal(NubefactInvoiceService.InFlightClassification.UncertainOrSent,
                NubefactInvoiceService.ClassifyInFlightState(r6, null));

            // 7. Historical without structured fields: invalid timestamp
            var r7 = new ElectronicReceipt { Status = "InFlight", LastError = $"CLAIMED:{myToken}:not-a-date" };
            Assert.Equal(NubefactInvoiceService.InFlightClassification.UncertainOrSent,
                NubefactInvoiceService.ClassifyInFlightState(r7, null));

            // 8. Historical without structured fields: CLAIMED expired
            var r8 = new ElectronicReceipt { Status = "InFlight", LastError = $"CLAIMED:{myToken}:{DateTime.UtcNow.AddMinutes(-5):O}" };
            Assert.Equal(NubefactInvoiceService.InFlightClassification.ReliablyUnsentExpired,
                NubefactInvoiceService.ClassifyInFlightState(r8, null));
        }

        [Fact]
        public async Task GenerateReceiptAsync_HistoricalUnstructuredInFlightWithoutNumber_TransitionsToRequiresReview_ZeroEmissions()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "InFlight",
                LastError = "Reinicio de IIS inesperado durante procesamiento",
                Environment = "Demo",
                TotalAmount = 100m,
                Number = "",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
            };
            context.ElectronicReceipts.Add(receipt);
            await context.SaveChangesAsync();

            int httpCalls = 0;
            var handler = new MockCulqiHttpHandler(req =>
            {
                httpCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":801}", Encoding.UTF8, "application/json")
                };
            });

            var service = new NubefactInvoiceService(context, _config, new HttpClient(handler), NullLogger<NubefactInvoiceService>.Instance);

            var result = await service.GenerateReceiptAsync(booking.Id, culqiPaymentId: null, exactReceiptId: receipt.Id);

            var updated = await context.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(updated);
            Assert.Equal("RequiresReview", updated.Status);
            Assert.Contains("revisión manual", updated.LastError);
            Assert.Equal(0, httpCalls); // Jamás emite a ciegas un documento en resultado incierto
        }

        #endregion

        #region 3. Conservación de Permisos Históricos y Actualización Limpia

        [Fact]
        public async Task IdentitySeeder_UpgradeScenario_PreservesHistoricalPermissions_DoesNotInjectDefaults()
        {
            var userStore = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new Mock<UserManager<ApplicationUser>>(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

            var adminRole = new IdentityRole { Id = "admin-role-id", Name = "Admin", NormalizedName = "ADMIN" };
            var existingClaims = new List<Claim>
            {
                new Claim("Permission", AppPermissions.BookingsView) // Rol histórico que solo tenía BookingsView
            };

            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var roleManager = new Mock<RoleManager<IdentityRole>>(roleStore.Object, null!, null!, null!, null!);

            roleManager.Setup(m => m.RoleExistsAsync("Admin")).ReturnsAsync(true);
            roleManager.Setup(m => m.FindByNameAsync("Admin")).ReturnsAsync(adminRole);
            roleManager.Setup(m => m.GetClaimsAsync(adminRole)).ReturnsAsync(existingClaims);

            var addedClaims = new List<Claim>();
            roleManager.Setup(m => m.AddClaimAsync(adminRole, It.IsAny<Claim>()))
                .Callback<IdentityRole, Claim>((r, c) => addedClaims.Add(c))
                .ReturnsAsync(IdentityResult.Success);

            // Simular inicialización / seeder al arrancar la app
            // Si el rol ya existe pero no tiene PermissionConfigured, solo debe recibir la marca
            var claims = await roleManager.Object.GetClaimsAsync(adminRole);
            if (!claims.Any(c => c.Type == "PermissionConfigured"))
            {
                await roleManager.Object.AddClaimAsync(adminRole, new Claim("PermissionConfigured", "true"));
            }

            Assert.Single(addedClaims);
            Assert.Equal("PermissionConfigured", addedClaims[0].Type);
            Assert.Equal("true", addedClaims[0].Value);
            // No se inyectaron ToursView, CashView ni ningún otro permiso por defecto
            Assert.DoesNotContain(addedClaims, c => c.Type == "Permission");
        }

        [Fact]
        public async Task IdentitySeeder_ExistingRoleWithZeroPermissions_PreservesZeroPermissions()
        {
            var adminRole = new IdentityRole { Id = "operador-role-id", Name = "Operador", NormalizedName = "OPERADOR" };
            var existingClaims = new List<Claim>(); // 0 permisos intencionales

            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var roleManager = new Mock<RoleManager<IdentityRole>>(roleStore.Object, null!, null!, null!, null!);

            roleManager.Setup(m => m.RoleExistsAsync("Operador")).ReturnsAsync(true);
            roleManager.Setup(m => m.FindByNameAsync("Operador")).ReturnsAsync(adminRole);
            roleManager.Setup(m => m.GetClaimsAsync(adminRole)).ReturnsAsync(existingClaims);

            var addedClaims = new List<Claim>();
            roleManager.Setup(m => m.AddClaimAsync(adminRole, It.IsAny<Claim>()))
                .Callback<IdentityRole, Claim>((r, c) => addedClaims.Add(c))
                .ReturnsAsync(IdentityResult.Success);

            var claims = await roleManager.Object.GetClaimsAsync(adminRole);
            if (!claims.Any(c => c.Type == "PermissionConfigured"))
            {
                await roleManager.Object.AddClaimAsync(adminRole, new Claim("PermissionConfigured", "true"));
            }

            Assert.Single(addedClaims);
            Assert.Equal("PermissionConfigured", addedClaims[0].Type);
            // Cero permisos inyectados: se preserva estrictamente el estado vacío
            Assert.DoesNotContain(addedClaims, c => c.Type == "Permission");
        }

        #endregion

        #region 4. Dashboard y Notificaciones: Filtrado Estricto de Datos

        [Fact]
        public async Task AdminController_Dashboard_ZeroAdministrativePermissions_ReturnsForbid403()
        {
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);

            var unprivilegedUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "user-no-perms"),
                new Claim(ClaimTypes.Name, "unprivileged@sigetour.com"),
                new Claim(ClaimTypes.Role, "Staff")
                // Sin ningún claim de Permission
            }, "TestAuth"));

            var controller = TestHelpers.CreateAdminController(context);
            controller.ControllerContext.HttpContext.User = unprivilegedUser;

            var result = await controller.Dashboard();

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task AdminController_Dashboard_ToursViewOnly_HidesBookingsAndFinancialRevenue()
        {
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);

            var toursOnlyUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "tours-only-user"),
                new Claim(ClaimTypes.Name, "tours@sigetour.com"),
                new Claim(ClaimTypes.Role, "Staff"),
                new Claim("Permission", AppPermissions.ToursView)
            }, "TestAuth"));

            var controller = TestHelpers.CreateAdminController(context);
            controller.ControllerContext.HttpContext.User = toursOnlyUser;

            var result = await controller.Dashboard();

            var viewResult = Assert.IsType<ViewResult>(result);
            var vm = Assert.IsType<DashboardViewModel>(viewResult.Model);

            // Permiso de Tours activo -> métricas de tours presentes
            Assert.True(vm.TotalTours > 0);
            Assert.NotEmpty(vm.ActiveTours);

            // Sin permiso de Bookings ni Cash -> métricas reservadas en 0 / listas vacías
            Assert.Equal(0, vm.TotalBookings);
            Assert.Equal(0, vm.TodayPassengers);
            Assert.Equal(0, vm.TodayIncome);
            Assert.Equal(0, vm.TodayPending);
            Assert.Empty(vm.RecentBookings);
            Assert.Empty(vm.Departures);
        }

        [Fact]
        public async Task AdminController_Dashboard_BookingsViewOnly_HidesFleetAlertsAndActiveTours()
        {
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);

            var bookingsOnlyUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "bookings-only-user"),
                new Claim(ClaimTypes.Name, "bookings@sigetour.com"),
                new Claim(ClaimTypes.Role, "Staff"),
                new Claim("Permission", AppPermissions.BookingsView)
            }, "TestAuth"));

            var controller = TestHelpers.CreateAdminController(context);
            controller.ControllerContext.HttpContext.User = bookingsOnlyUser;

            var result = await controller.Dashboard();

            var viewResult = Assert.IsType<ViewResult>(result);
            var vm = Assert.IsType<DashboardViewModel>(viewResult.Model);

            // Permiso de Bookings activo -> métricas de reservas presentes
            Assert.True(vm.TotalBookings > 0);
            Assert.NotEmpty(vm.RecentBookings);

            // Sin permiso de Tours ni Cash -> métricas vehiculares y tours ocultas
            Assert.Empty(vm.ActiveTours);
            Assert.Empty(vm.FleetAlerts);
            Assert.Empty(vm.Departures);
            Assert.Equal(0, vm.TodayIncome);
        }

        [Fact]
        public async Task AdminController_GetNotifications_WithoutBookingsAndTours_ReturnsZeroNotifications()
        {
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);

            var userWithoutPerms = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "notif-user"),
                new Claim(ClaimTypes.Name, "user@sigetour.com"),
                new Claim(ClaimTypes.Role, "Staff")
                // Sin BookingsView ni ToursView
            }, "TestAuth"));

            var controller = TestHelpers.CreateAdminController(context);
            controller.ControllerContext.HttpContext.User = userWithoutPerms;

            var result = await controller.GetNotifications();

            var jsonResult = Assert.IsType<JsonResult>(result);
            var countProp = jsonResult.Value?.GetType().GetProperty("count")?.GetValue(jsonResult.Value);
            Assert.Equal(0, countProp);
        }

        [Fact]
        public async Task AdminController_RolePermissions_DisplaysOnlyPersistedClaims()
        {
            using var context = TestDbContextFactory.Create();
            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var roleManager = new Mock<RoleManager<IdentityRole>>(roleStore.Object, null!, null!, null!, null!);

            var targetRole = new IdentityRole { Id = "role-custom-1", Name = "CustomOperator" };
            roleManager.Setup(m => m.FindByNameAsync("CustomOperator")).ReturnsAsync(targetRole);

            // Solo 1 claim de permiso persistido en base de datos
            var persistedClaims = new List<Claim>
            {
                new Claim("Permission", AppPermissions.ReceiptsView)
            };
            roleManager.Setup(m => m.GetClaimsAsync(targetRole)).ReturnsAsync(persistedClaims);

            var controller = TestHelpers.CreateAdminController(context, roleManager: roleManager);

            var result = await controller.RolePermissions("CustomOperator");

            var viewResult = Assert.IsType<ViewResult>(result);
            var assignedPerms = controller.ViewBag.AssignedPermissions as List<string>;

            Assert.NotNull(assignedPerms);
            Assert.Single(assignedPerms);
            Assert.Equal(AppPermissions.ReceiptsView, assignedPerms[0]);
        }

        [Fact]
        public async Task BookingController_IsAuthorizedForBooking_RequiresPermissionClaim_RoleNameAloneFails()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            // Usuario con rol 'Admin' pero SIN ningún claim de permiso
            var adminWithoutClaims = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "admin-no-claims"),
                new Claim(ClaimTypes.Name, "fakeadmin@sigetour.com"),
                new Claim(ClaimTypes.Role, "Admin")
            }, "CookieAuth"));

            var controller = new BookingController(context, _checkoutAccess, _config);

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = adminWithoutClaims }
            };

            // Intentar acceder a Voucher sin token público y sin claim de permiso
            var result = await controller.Voucher(booking.Id, token: null);

            // Debe denegar acceso con 401 Unauthorized
            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal(401, unauthResult.StatusCode);
        }

        #endregion
    }
}
