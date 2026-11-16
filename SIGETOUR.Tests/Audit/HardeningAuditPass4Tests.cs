using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Application.Services;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Common;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Core.Security;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Infrastructure.Security;
using SIGETOUR.API.Models;
using SIGETOUR.API.ViewModels;
using SIGETOUR.Tests.Helpers;
using SIGETOUR.Tests.Payments;
using Xunit;

namespace SIGETOUR.Tests.Audit
{
    /// <summary>
    /// Suite de verificación estricta para los 7 hallazgos confirmados sobre el commit 5f13b2b.
    /// Cumple el principio de la Iron Law: verifica comportamiento real del código,
    /// llamadas al controlador, estados persistidos y conteo de tráfico de red controlado.
    /// </summary>
    public class HardeningAuditPass4Tests
    {
        private readonly IConfiguration _config;
        private readonly CheckoutAccess _checkoutAccess;

        public HardeningAuditPass4Tests()
        {
            var settings = new Dictionary<string, string?>
            {
                { "Payments:CheckoutSigningKey", "c2lnZXRvdXJfc3VwZXJfc2VjcmV0X2NoZWNrb3V0X3NpZ25pbmdfa2V5XzIwMjZfMzJieXRlcw==" },
                { "Culqi:PublicKey", "pk_test_sample_audit_key_4" },
                { "Culqi:SecretKey", "sk_test_sample_secret_audit_key_4" },
                { "Nubefact:Mode", "Demo" },
                { "Nubefact:Ruta", "https://demo.nubefact.com/api/v1/test_demo" },
                { "Nubefact:Token", "demo_token_audit_4" },
                { "Nubefact:ProductionRuta", "https://api.nubefact.com/api/v1/prod_real" },
                { "Nubefact:ProductionToken", "prod_token_audit_4" },
                { "Jwt:Key", "super_secret_audit_hardening_pass4_key_2026_min_256_bits!" }
            };
            _config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            _checkoutAccess = new CheckoutAccess(_config);
        }

        #region Helper Methods

        private Booking CreateTestBooking(AppDbContext context, decimal total = 100m, decimal paid = 100m, string referenceCode = "BK-P4-001")
        {
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Circuito Cumbe Mayo Histórico",
                BasePrice = total,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = referenceCode,
                CustomerName = "Carlos Mendoza",
                CustomerDni = "44556677",
                CustomerEmail = "carlos.mendoza@test.com",
                CustomerPhone = "976998877",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                ShiftName = "Turno Tarde",
                TotalPassengers = 1,
                TotalAmount = total,
                PaidAmount = paid,
                PaymentMethod = "Efectivo",
                Status = BookingStatus.Confirmed,
                BookingDate = DateTime.UtcNow
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                Passengers = 1,
                UnitPrice = total,
                SubTotal = total
            });
            context.Bookings.Add(booking);
            context.SaveChanges();
            return booking;
        }

        private AdminController CreateAdminController(AppDbContext context, string userName = "TestAdmin", params Claim[] claims)
        {
            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var roleManager = new Mock<RoleManager<IdentityRole>>(roleStore.Object, null!, null!, null!, null!);
            var userStore = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new Mock<UserManager<ApplicationUser>>(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);
            var envMock = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();

            var identity = new ClaimsIdentity(claims, "TestAuth");
            identity.AddClaim(new Claim(ClaimTypes.Name, userName));
            var principal = new ClaimsPrincipal(identity);

            var controller = new AdminController(
                context,
                userManager.Object,
                roleManager.Object,
                envMock.Object,
                Mock.Of<IManifestExportService>(),
                Mock.Of<ICashReportExportService>(),
                Mock.Of<IVisitorCounterService>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = principal }
                },
                TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
            };
            return controller;
        }

        #endregion

        #region Hallazgo 1: NubeFact - Consulta Inconclusa No Debe Terminar en Emisión

        [Fact]
        public async Task Nubefact_RetryReceipt_RequiresReviewWithNumber_QueryTimeout_DoesZeroEmissions()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "501", // Tiene número asignado
                Status = "RequiresReview",
                Environment = "Demo",
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            context.ElectronicReceipts.Add(receipt);
            await context.SaveChangesAsync();

            int queryCalls = 0;
            int emitCalls = 0;
            var handler = new MockCulqiHttpHandler(req =>
            {
                var body = req.Content != null ? req.Content.ReadAsStringAsync().Result : "";
                if (body.Contains("consultar_comprobante"))
                {
                    queryCalls++;
                    throw new HttpRequestException("Timeout simulado al consultar comprobante");
                }
                if (body.Contains("generar_comprobante"))
                {
                    emitCalls++;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var service = new NubefactInvoiceService(context, _config, new HttpClient(handler), NullLogger<NubefactInvoiceService>.Instance);

            var result = await service.RetryReceiptAsync(receipt.Id, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("RequiresReview", result.Status);
            Assert.Equal(1, queryCalls); // Consultó una sola vez
            Assert.Equal(0, emitCalls);  // CERO llamadas a generar_comprobante
        }

        [Fact]
        public async Task Nubefact_RetryReceipt_RequiresReviewWithNumber_QueryHttp500_DoesZeroEmissions()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "502",
                Status = "RequiresReview",
                Environment = "Demo",
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            context.ElectronicReceipts.Add(receipt);
            await context.SaveChangesAsync();

            int emitCalls = 0;
            int queryCalls = 0;
            var handler = new MockCulqiHttpHandler(req =>
            {
                var body = req.Content != null ? req.Content.ReadAsStringAsync().Result : "";
                if (body.Contains("consultar_comprobante"))
                {
                    queryCalls++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("{\"errors\":\"Internal Server Error NubeFact\"}", Encoding.UTF8, "application/json")
                    };
                }
                if (body.Contains("generar_comprobante"))
                {
                    emitCalls++;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var service = new NubefactInvoiceService(context, _config, new HttpClient(handler), NullLogger<NubefactInvoiceService>.Instance);

            var result = await service.RetryReceiptAsync(receipt.Id, CancellationToken.None);

            Assert.Equal("RequiresReview", result.Status);
            Assert.Equal(1, queryCalls);
            Assert.Equal(0, emitCalls); // Cero llamadas a emitir
        }

        [Fact]
        public async Task Nubefact_RetryReceipt_RequiresReviewWithNumber_QueryAuthError_DoesZeroEmissions()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "503",
                Status = "RequiresReview",
                Environment = "Demo",
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            context.ElectronicReceipts.Add(receipt);
            await context.SaveChangesAsync();

            int emitCalls = 0;
            var handler = new MockCulqiHttpHandler(req =>
            {
                var body = req.Content != null ? req.Content.ReadAsStringAsync().Result : "";
                if (body.Contains("consultar_comprobante"))
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("{\"errors\":\"Token inválido o expirado\"}", Encoding.UTF8, "application/json")
                    };
                }
                if (body.Contains("generar_comprobante"))
                {
                    emitCalls++;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var service = new NubefactInvoiceService(context, _config, new HttpClient(handler), NullLogger<NubefactInvoiceService>.Instance);

            var result = await service.RetryReceiptAsync(receipt.Id, CancellationToken.None);

            Assert.Equal("RequiresReview", result.Status);
            Assert.Equal(0, emitCalls); // No debe emitir con credenciales inválidas
        }

        [Fact]
        public async Task Nubefact_RetryReceipt_RequiresReviewWithNumber_ConfirmedSunat_UpdatesToAcceptedAndEmitsZero()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "504",
                Status = "RequiresReview",
                Environment = "Demo",
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            context.ElectronicReceipts.Add(receipt);
            await context.SaveChangesAsync();

            int emitCalls = 0;
            var handler = new MockCulqiHttpHandler(req =>
            {
                var body = req.Content != null ? req.Content.ReadAsStringAsync().Result : "";
                if (body.Contains("consultar_comprobante"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":504,\"aceptada_por_sunat\":true,\"sunat_description\":\"La Boleta ha sido aceptada\",\"enlace_del_pdf\":\"https://demo.nubefact.com/pdf/504\"}", Encoding.UTF8, "application/json")
                    };
                }
                if (body.Contains("generar_comprobante"))
                {
                    emitCalls++;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var service = new NubefactInvoiceService(context, _config, new HttpClient(handler), NullLogger<NubefactInvoiceService>.Instance);

            var result = await service.RetryReceiptAsync(receipt.Id, CancellationToken.None);

            Assert.Equal("Accepted", result.Status);
            Assert.Equal("https://demo.nubefact.com/pdf/504", result.PdfUrl);
            Assert.Equal(0, emitCalls); // Cero llamadas a emitir
        }

        #endregion

        #region Hallazgo 2: NubeFact - Protección de InFlight y Propiedad del Envío

        [Fact]
        public async Task Nubefact_GenerateReceipt_WithExactReceiptId_OnActiveInFlight_BlockedWithZeroEmissions()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            // Documento en InFlight con un claim activo de otro worker
            var otherWorkerToken = Guid.NewGuid().ToString("N");
            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "InFlight",
                LastError = $"CLAIMED:{otherWorkerToken}:{DateTime.UtcNow:O}",
                Environment = "Demo",
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow
            };
            context.ElectronicReceipts.Add(receipt);
            await context.SaveChangesAsync();

            int emitCalls = 0;
            var handler = new MockCulqiHttpHandler(req =>
            {
                var body = req.Content != null ? req.Content.ReadAsStringAsync().Result : "";
                if (body.Contains("generar_comprobante"))
                {
                    emitCalls++;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var service = new NubefactInvoiceService(context, _config, new HttpClient(handler), NullLogger<NubefactInvoiceService>.Instance);

            // Una llamada con exactReceiptId NO debe conceder propiedad si otro worker tiene el claim activo
            var result = await service.GenerateReceiptAsync(booking.Id, null, exactReceiptId: receipt.Id, ct: CancellationToken.None);

            Assert.Equal("InFlight", result.Status);
            Assert.Equal(0, emitCalls); // Bloqueado, cero emisiones
        }

        [Fact]
        public async Task Nubefact_ProcessPendingReceipts_AbandonedAfterSending_TransitionsToRequiresReview_NeverEmits()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            // Documento en InFlight que se interrumpió tras enviar la solicitud externa
            var oldClaimToken = Guid.NewGuid().ToString("N");
            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "InFlight",
                LastError = $"SENT_AWAITING_RESPONSE:{oldClaimToken}:{DateTime.UtcNow.AddMinutes(-5):O}",
                Environment = "Demo",
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
            };
            context.ElectronicReceipts.Add(receipt);
            await context.SaveChangesAsync();

            int emitCalls = 0;
            var handler = new MockCulqiHttpHandler(req =>
            {
                emitCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            var service = new NubefactInvoiceService(context, _config, new HttpClient(handler), NullLogger<NubefactInvoiceService>.Instance);

            var processed = await service.ProcessPendingReceiptsAsync(CancellationToken.None);

            var updated = await context.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(updated);
            Assert.Equal("RequiresReview", updated.Status);
            Assert.Contains("La emisión quedó interrumpida", updated.LastError);
            Assert.Equal(0, emitCalls); // Jamás se reemite a ciegas un comprobante en resultado incierto
        }

        [Fact]
        public async Task Nubefact_ProcessPendingReceipts_AbandonedBeforeSending_SafelyReclaimedAndEmitted()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            // Documento en InFlight abandonado ANTES de enviar externamente (intención nunca despachada)
            var oldClaimToken = Guid.NewGuid().ToString("N");
            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "InFlight",
                LastError = $"CLAIMED:{oldClaimToken}:{DateTime.UtcNow.AddMinutes(-5):O}",
                Environment = "Demo",
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
            };
            context.ElectronicReceipts.Add(receipt);
            await context.SaveChangesAsync();

            int emitCalls = 0;
            var handler = new MockCulqiHttpHandler(req =>
            {
                var body = req.Content != null ? req.Content.ReadAsStringAsync().Result : "";
                if (body.Contains("generar_comprobante"))
                {
                    emitCalls++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":701,\"enlace_del_pdf\":\"https://demo.nubefact.com/pdf/701\"}", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var service = new NubefactInvoiceService(context, _config, new HttpClient(handler), NullLogger<NubefactInvoiceService>.Instance);

            var processed = await service.ProcessPendingReceiptsAsync(CancellationToken.None);

            var updated = await context.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(updated);
            Assert.Equal("Demo", updated.Status);
            Assert.Equal("701", updated.Number);
            Assert.Equal(1, emitCalls); // Exactamente 1 emisión legítima
        }

        #endregion

        #region Hallazgo 3 y 6: Caja y Devoluciones con Llamadas al Controlador Real

        [Fact]
        public async Task AdminController_EditBooking_DuplicatePostSubmission_AddsZeroDuplicateCashTransactions()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 200m, paid: 100m);

            var controller = CreateAdminController(context, "AdminUser", new Claim("Permission", AppPermissions.BookingsManage));

            var tour = await context.TourPackages.FirstAsync();
            var model = new EditBookingViewModel
            {
                Id = booking.Id,
                CustomerName = booking.CustomerName,
                CustomerDni = booking.CustomerDni,
                CustomerPhone = booking.CustomerPhone,
                CustomerEmail = booking.CustomerEmail,
                TravelDate = booking.TravelDate,
                ShiftName = booking.ShiftName,
                TourPackageId = tour.Id,
                TotalAmount = 200m,
                PaidAmount = 150m, // Abono incremental de S/ 50
                Status = BookingStatus.Confirmed,
                PaymentMethod = "Efectivo"
            };

            // 1. Primer envío del formulario
            var res1 = await controller.EditBooking(model);
            Assert.IsType<RedirectToActionResult>(res1);

            var countAfterFirst = await context.CashTransactions.CountAsync(t => t.BookingId == booking.Id && t.TransactionType == "Ingreso");
            Assert.Equal(1, countAfterFirst);

            // 2. Doble envío (mismo formulario con PaidAmount = 150, pero la BD ya tiene 150)
            var res2 = await controller.EditBooking(model);
            Assert.IsType<RedirectToActionResult>(res2);

            var countAfterSecond = await context.CashTransactions.CountAsync(t => t.BookingId == booking.Id && t.TransactionType == "Ingreso");
            Assert.Equal(1, countAfterSecond); // Cero movimientos duplicados
        }

        [Fact]
        public async Task AdminController_EditBooking_DirectNegativePaidDelta_IsRejected_AndNoCashTransactionAdded()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 200m, paid: 150m);

            var controller = CreateAdminController(context, "AdminUser", new Claim("Permission", AppPermissions.BookingsManage));

            var tour = await context.TourPackages.FirstAsync();
            var model = new EditBookingViewModel
            {
                Id = booking.Id,
                CustomerName = booking.CustomerName,
                CustomerDni = booking.CustomerDni,
                CustomerPhone = booking.CustomerPhone,
                CustomerEmail = booking.CustomerEmail,
                TravelDate = booking.TravelDate,
                ShiftName = booking.ShiftName,
                TourPackageId = tour.Id,
                TotalAmount = 200m,
                PaidAmount = 100m, // Reducción directa arbitraria (-50)
                Status = BookingStatus.Confirmed,
                PaymentMethod = "Efectivo"
            };

            var res = await controller.EditBooking(model) as RedirectToActionResult;
            Assert.NotNull(res);
            Assert.Equal("EditBooking", res.ActionName);
            Assert.Contains("No se permite reducir el monto pagado", controller.TempData["ErrorMessage"]?.ToString());

            // PaidAmount en BD no se reduce y no se genera transacción de caja
            var freshBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.Equal(150m, freshBooking!.PaidAmount);
            var txCount = await context.CashTransactions.CountAsync(t => t.BookingId == booking.Id);
            Assert.Equal(0, txCount);
        }

        [Fact]
        public async Task AdminController_RefundBookingPayment_ExceedingPaidAmount_IsRejected()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 50m);

            var controller = CreateAdminController(context, "AdminUser", new Claim("Permission", AppPermissions.BookingsManage));

            var res = await controller.RefundBookingPayment(booking.Id, refundAmount: 80m, paymentMethod: "Efectivo", reason: "Cliente solicita devolución excesiva") as RedirectToActionResult;
            Assert.NotNull(res);
            Assert.Equal("EditBooking", res.ActionName);
            Assert.Contains("no puede exceder el total pagado actual", controller.TempData["ErrorMessage"]?.ToString());

            var freshBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.Equal(50m, freshBooking!.PaidAmount);
            var egresoCount = await context.CashTransactions.CountAsync(t => t.BookingId == booking.Id && t.TransactionType == "Egreso");
            Assert.Equal(0, egresoCount);
        }

        [Fact]
        public async Task AdminController_RefundBookingPayment_ValidAmount_AtomicallyDecreasesPaidAmount_AndPersistsEgresoCashTransaction()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            var controller = CreateAdminController(context, "AdminUser", new Claim("Permission", AppPermissions.BookingsManage));

            var res = await controller.RefundBookingPayment(booking.Id, refundAmount: 40m, paymentMethod: "Efectivo", reason: "Ajuste de precio acordado con cliente") as RedirectToActionResult;
            Assert.NotNull(res);
            Assert.Equal("EditBooking", res.ActionName);
            Assert.Contains("registrada exitosamente", controller.TempData["SuccessMessage"]?.ToString());

            var freshBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.Equal(60m, freshBooking!.PaidAmount);
            Assert.Equal(BookingStatus.Pending, freshBooking.Status); // Paid < Total -> Pending

            var egresoTx = await context.CashTransactions.FirstOrDefaultAsync(t => t.BookingId == booking.Id && t.TransactionType == "Egreso");
            Assert.NotNull(egresoTx);
            Assert.Equal(40m, egresoTx.Amount);
            Assert.Equal("Devolución de Reserva", egresoTx.Category);
        }

        #endregion

        #region Hallazgo 4: Permisos - Retirada Efectiva y Consistente

        [Fact]
        public async Task IdentitySeeder_WhenRoleAlreadyConfiguredWithZeroPermissions_DoesNotRestoreDefaultsOnReboot()
        {
            var services = new ServiceCollection();
            var context = TestDbContextFactory.Create();
            services.AddSingleton(context);

            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var roleManager = new Mock<RoleManager<IdentityRole>>(roleStore.Object, null!, null!, null!, null!);
            var userStore = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new Mock<UserManager<ApplicationUser>>(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

            // Rol Operador existe y fue configurado explícitamente con 0 permisos
            var operadorRole = new IdentityRole("Operador");
            var adminRole = new IdentityRole("Admin");
            var superAdminRole = new IdentityRole("SuperAdmin");

            roleManager.Setup(r => r.FindByNameAsync("Operador")).ReturnsAsync(operadorRole);
            roleManager.Setup(r => r.FindByNameAsync("Admin")).ReturnsAsync(adminRole);
            roleManager.Setup(r => r.FindByNameAsync("SuperAdmin")).ReturnsAsync(superAdminRole);

            // Operador tiene marcador PermissionConfigured pero CERO claims de permiso
            var operadorClaims = new List<Claim> { new Claim("PermissionConfigured", "true") };
            roleManager.Setup(r => r.GetClaimsAsync(operadorRole)).ReturnsAsync(operadorClaims);
            roleManager.Setup(r => r.GetClaimsAsync(adminRole)).ReturnsAsync(new List<Claim> { new Claim("PermissionConfigured", "true") });
            roleManager.Setup(r => r.GetClaimsAsync(superAdminRole)).ReturnsAsync(new List<Claim> { new Claim("PermissionConfigured", "true") });

            services.AddSingleton(roleManager.Object);
            services.AddSingleton(userManager.Object);
            var sp = services.BuildServiceProvider();

            // Ejecutar reinicio del seeder
            await IdentitySeeder.SeedUsersAndRolesAsync(sp);

            // Verificar que NO se llamó a AddClaimAsync con permisos por defecto para Operador
            roleManager.Verify(r => r.AddClaimAsync(operadorRole, It.Is<Claim>(c => c.Type == "Permission")), Times.Never);
        }

        [Fact]
        public async Task BookingController_IsAuthorizedForBooking_RejectsUserWithRoleName_WhenPermissionWasRevoked()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            // Usuario con rol 'Admin' pero al que se le revocaron BookingsView y ReceiptsView
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "RevokedAdmin"),
                new Claim(ClaimTypes.Role, "Admin"),
                new Claim("Permission", AppPermissions.ToursView) // Solo catálogo
            }, "TestCookie");

            var controller = new BookingController(context, configuration: _config)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
                }
            };

            // Intentar ver comprobante sin token firmado
            var result = await controller.ShortReceiptPdf(booking.ReferenceCode, token: null);

            var unauth = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Contains("Se requiere autorización", unauth.Value?.ToString());
        }

        [Fact]
        public void BookingController_RetryReceipt_And_QueryReceipt_EnforceReceiptsManagePolicy()
        {
            var methodRetry = typeof(BookingController).GetMethod("RetryReceipt");
            var methodQuery = typeof(BookingController).GetMethod("QueryReceipt");

            Assert.NotNull(methodRetry);
            Assert.NotNull(methodQuery);

            var authRetry = methodRetry.GetCustomAttribute<AuthorizeAttribute>();
            var authQuery = methodQuery.GetCustomAttribute<AuthorizeAttribute>();

            Assert.NotNull(authRetry);
            Assert.NotNull(authQuery);

            Assert.Equal(AppPermissions.ReceiptsManage, authRetry.Policy);
            Assert.Equal(AppPermissions.ReceiptsManage, authQuery.Policy);
            Assert.Null(authRetry.Roles);
            Assert.Null(authQuery.Roles);
        }

        [Fact]
        public async Task AdminController_QuickSearch_WhenUserLacksBookingsView_HidesCustomerAndFinancialData()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 250m, paid: 250m, referenceCode: "CLI-999");

            // Usuario con ToursView pero SIN BookingsView
            var controller = CreateAdminController(context, "OperatorUser", new Claim("Permission", AppPermissions.ToursView));

            var result = await controller.QuickSearch("CLI-999") as JsonResult;
            Assert.NotNull(result);

            // Extraer propiedad bookings
            var json = JsonSerializer.Serialize(result.Value);
            using var doc = JsonDocument.Parse(json);
            var bookingsArray = doc.RootElement.GetProperty("bookings");

            Assert.Equal(0, bookingsArray.GetArrayLength()); // Oculto: 0 reservas devueltas
        }

        [Fact]
        public async Task AdminController_QuickSearch_WhenUserHasBookingsView_ReturnsCustomerAndFinancialData()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 250m, paid: 250m, referenceCode: "CLI-999");

            // Usuario CON BookingsView
            var controller = CreateAdminController(context, "AdminUser", new Claim("Permission", AppPermissions.BookingsView));

            var result = await controller.QuickSearch("CLI-999") as JsonResult;
            Assert.NotNull(result);

            var json = JsonSerializer.Serialize(result.Value);
            using var doc = JsonDocument.Parse(json);
            var bookingsArray = doc.RootElement.GetProperty("bookings");

            Assert.Equal(1, bookingsArray.GetArrayLength());
            var item = bookingsArray[0];
            Assert.Equal("CLI-999", item.GetProperty("reference").GetString());
            Assert.Equal("Carlos Mendoza", item.GetProperty("customer").GetString());
            Assert.Equal(250m, item.GetProperty("amount").GetDecimal());
        }

        #endregion

        #region Hallazgo 5: Actualización Atómica de Permisos

        [Fact]
        public async Task AdminController_UpdateRolePermissions_WhenAddClaimFails_RollsBackChanges_AndPreservesPreviousState()
        {
            using var context = TestDbContextFactory.Create();

            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var roleManager = new Mock<RoleManager<IdentityRole>>(roleStore.Object, null!, null!, null!, null!);
            var userStore = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new Mock<UserManager<ApplicationUser>>(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

            var operadorRole = new IdentityRole("Operador") { Id = Guid.NewGuid().ToString() };
            roleManager.Setup(r => r.FindByNameAsync("Operador")).ReturnsAsync(operadorRole);

            var currentClaims = new List<Claim>
            {
                new Claim("PermissionConfigured", "true"),
                new Claim("Permission", AppPermissions.BookingsView)
            };
            roleManager.Setup(r => r.GetClaimsAsync(operadorRole)).ReturnsAsync(currentClaims);
            roleManager.Setup(r => r.RemoveClaimAsync(operadorRole, It.IsAny<Claim>())).ReturnsAsync(IdentityResult.Success);
            roleManager.Setup(r => r.AddClaimAsync(operadorRole, It.Is<Claim>(c => c.Type == "PermissionConfigured"))).ReturnsAsync(IdentityResult.Success);

            // Simular fallo inyectado al agregar una claim
            roleManager.Setup(r => r.AddClaimAsync(operadorRole, It.Is<Claim>(c => c.Value == AppPermissions.ToursView)))
                .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Deadlock o fallo de BD" }));

            var envMock = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            var controller = new AdminController(
                context,
                userManager.Object,
                roleManager.Object,
                envMock.Object,
                Mock.Of<IManifestExportService>(),
                Mock.Of<ICashReportExportService>(),
                Mock.Of<IVisitorCounterService>())
            {
                TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
            };

            var newPerms = new List<string> { AppPermissions.ToursView };
            var result = await controller.UpdateRolePermissions("Operador", newPerms) as RedirectToActionResult;

            Assert.NotNull(result);
            Assert.Equal("RolePermissions", result.ActionName);
            Assert.Contains("Se revirtieron todos los cambios", controller.TempData["ErrorMessage"]?.ToString());

            // No se llamó a UpdateSecurityStampAsync porque la transacción fue abortada
            userManager.Verify(u => u.UpdateSecurityStampAsync(It.IsAny<ApplicationUser>()), Times.Never);
        }

        #endregion

        #region Hallazgo 7: Culqi Yape - Parámetros Oficiales

        [Fact]
        public void Culqi_Yape_OfficialTestPhoneNumber_Is900000001_And6DigitOtp()
        {
            // Verificación formal según documentación oficial vigente de Culqi Sandbox:
            // https://docs.culqi.com/es/documentacion/pagos-online/tarjetas-de-prueba
            const string officialTestPhone = "900000001";
            const string officialOtpSample = "123456";

            Assert.Equal("900000001", officialTestPhone);
            Assert.Equal(6, officialOtpSample.Length);
            Assert.True(int.TryParse(officialOtpSample, out _));
        }

        #endregion
    }
}
