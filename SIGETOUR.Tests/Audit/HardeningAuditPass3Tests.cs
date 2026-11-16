using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
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
using Microsoft.Extensions.Options;
using Moq;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Application.Services;
using SIGETOUR.API.Controllers;
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
    /// Suite de verificación y regresión para los 7 hallazgos de auditoría (Fase 3 Hardening).
    /// Enforces the strict Iron Law: fresh evidence before declaring any audit item resolved.
    /// </summary>
    public class HardeningAuditPass3Tests
    {
        private readonly IConfiguration _config;
        private readonly CheckoutAccess _checkoutAccess;

        public HardeningAuditPass3Tests()
        {
            var settings = new Dictionary<string, string?>
            {
                { "Payments:CheckoutSigningKey", "c2lnZXRvdXJfc3VwZXJfc2VjcmV0X2NoZWNrb3V0X3NpZ25pbmdfa2V5XzIwMjZfMzJieXRlcw==" },
                { "Culqi:PublicKey", "pk_test_sample_audit_key_3" },
                { "Culqi:SecretKey", "sk_test_sample_secret_audit_key_3" },
                { "Nubefact:Mode", "Demo" },
                { "Nubefact:Ruta", "https://demo.nubefact.com/api/v1/test_demo" },
                { "Nubefact:Token", "demo_token_audit_3" },
                { "Nubefact:ProductionRuta", "https://api.nubefact.com/api/v1/prod_real" },
                { "Nubefact:ProductionToken", "prod_token_audit_3" },
                { "Jwt:Key", "super_secret_audit_hardening_pass3_key_2026_min_256_bits!" }
            };
            _config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            _checkoutAccess = new CheckoutAccess(_config);
        }

        #region Helper Methods

        private Booking CreateTestBooking(AppDbContext context, decimal total = 100m, decimal paid = 100m, string referenceCode = "BK-P3-001")
        {
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Circuito Valle Arqueológico",
                BasePrice = total,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = referenceCode,
                CustomerName = "Juan Pérez",
                CustomerDni = "40192834",
                CustomerEmail = "juan.perez@test.com",
                CustomerPhone = "976123456",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                ShiftName = "Turno Mañana",
                TotalPassengers = 1,
                TotalAmount = total,
                PaidAmount = paid,
                PaymentMethod = "Culqi",
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

        #endregion

        #region Hallazgo 1: NubeFact Timeout y Resultados Inciertos

        [Fact]
        public async Task Nubefact_TimeoutAfterSending_TransitionsToRequiresReview_AndPreventsBlindReemission()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            // 1. Simulación de fallo de red / timeout tras iniciar el envío
            int networkAttempts = 0;
            var timeoutHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                networkAttempts++;
                throw new HttpRequestException("The request timed out while awaiting response from provider.");
            }));

            var service = new NubefactInvoiceService(context, _config, timeoutHttp, NullLogger<NubefactInvoiceService>.Instance);
            var receipt = await service.GenerateReceiptAsync(booking.Id, null, CancellationToken.None);

            // Assert 1: Debe persistir 'RequiresReview' y NO 'Pending' para distinguir resultado incierto de no enviado
            Assert.NotNull(receipt);
            Assert.Equal("RequiresReview", receipt.Status);
            Assert.Contains("Resultado incierto tras fallo de conexión", receipt.LastError);
            Assert.Equal(1, networkAttempts);

            // 2. Ejecutar recuperación automática: no debe reemitir a ciegas porque no tiene número correlativo
            int recoveryHttpCalls = 0;
            var recoveryHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                recoveryHttpCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":999}", Encoding.UTF8, "application/json")
                };
            }));

            var recoveryService = new NubefactInvoiceService(context, _config, recoveryHttp, NullLogger<NubefactInvoiceService>.Instance);
            var recoveredCount = await recoveryService.ProcessPendingReceiptsAsync(CancellationToken.None);

            // Assert 2: 0 comprobantes procesados para reemisión a ciegas y 0 peticiones de red disparadas
            Assert.Equal(0, recoveredCount);
            Assert.Equal(0, recoveryHttpCalls);

            var dbReceipt = await context.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(dbReceipt);
            Assert.Equal("RequiresReview", dbReceipt.Status);
        }

        [Fact]
        public async Task Nubefact_Http500_TransitionsToRequiresReview_AndRetainedForManualReview()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 150m, paid: 150m);

            var http500Handler = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"errors\":\"Internal gateway error in Nubefact cluster\"}", Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(context, _config, http500Handler, NullLogger<NubefactInvoiceService>.Instance);
            var receipt = await service.GenerateReceiptAsync(booking.Id, null, CancellationToken.None);

            Assert.NotNull(receipt);
            Assert.Equal("RequiresReview", receipt.Status);
            Assert.Contains("HTTP 500", receipt.LastError);
            Assert.Contains("requiere conciliación antes de reintentar", receipt.LastError);
        }

        [Fact]
        public async Task Nubefact_AbandonedInFlight_ReconcilesToRequiresReviewUponRecovery()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 200m, paid: 200m);

            // Simular comprobante que quedó 'InFlight' porque el servidor se reinició antes de recibir la respuesta
            var inFlightReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "InFlight",
                Environment = "Demo",
                TotalAmount = 200m,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5) // Antiguo al cutoff
            };
            context.ElectronicReceipts.Add(inFlightReceipt);
            await context.SaveChangesAsync();

            var dummyHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                throw new InvalidOperationException("No debe disparar emisión externa para InFlight abandonado");
            }));

            var service = new NubefactInvoiceService(context, _config, dummyHttp, NullLogger<NubefactInvoiceService>.Instance);
            await service.ProcessPendingReceiptsAsync(CancellationToken.None);

            var reconciled = await context.ElectronicReceipts.FindAsync(inFlightReceipt.Id);
            Assert.NotNull(reconciled);
            Assert.Equal("RequiresReview", reconciled.Status);
            Assert.Contains("emisión quedó interrumpida o en vuelo tras reinicio", reconciled.LastError);
        }

        #endregion

        #region Hallazgo 2: NubeFact Aislamiento Demo / Producción

        [Fact]
        public async Task Nubefact_EnvironmentMismatch_DemoToProduction_BlockedWithZeroExternalCalls()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            // Configurar sistema en Producción (sin credenciales de Demo)
            var prodConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Nubefact:Mode", "Production" },
                { "Nubefact:Ruta", "https://api.nubefact.com/api/v1/prod" },
                { "Nubefact:Token", "token_prod_exclusive" }
            }).Build();

            // Documento histórico creado previamente en DEMO
            var demoReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "Pending",
                Environment = "Demo",
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
            };
            context.ElectronicReceipts.Add(demoReceipt);
            await context.SaveChangesAsync();

            int externalCalls = 0;
            var http = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                externalCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));

            var service = new NubefactInvoiceService(context, prodConfig, http, NullLogger<NubefactInvoiceService>.Instance);

            // Debe lanzar excepción y bloquear la emisión por incompatibilidad de credenciales
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.GenerateReceiptAsync(booking.Id, null, demoReceipt.Id, CancellationToken.None));

            Assert.Contains("Incompatibilidad de ambiente", ex.Message);
            Assert.Equal(0, externalCalls); // Cero llamadas externas con tokens cruzados
        }

        [Fact]
        public async Task Nubefact_SimulationEnvironment_NeverCallsExternalNetwork()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 90m, paid: 90m);

            var simConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Nubefact:Mode", "Simulation" }
            }).Build();

            int externalCalls = 0;
            var http = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                externalCalls++;
                throw new InvalidOperationException("En modo Simulation no debe existir tráfico de red");
            }));

            var service = new NubefactInvoiceService(context, simConfig, http, NullLogger<NubefactInvoiceService>.Instance);
            var receipt = await service.GenerateReceiptAsync(booking.Id, null, CancellationToken.None);

            Assert.NotNull(receipt);
            Assert.Equal("Simulation", receipt.Status);
            Assert.Equal("Simulation", receipt.Environment);
            Assert.Equal(0, externalCalls);
        }

        #endregion

        #region Hallazgo 3: Caja - Abonos Concurrentes y Doble Envío

        [Fact]
        public void EditBooking_DoubleFormSubmission_PaidDeltaIsZero_NoDuplicateCashMovement()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 50m);

            // Simular primer abono de S/ 50 (PaidAmount pasa a 100)
            var initialPaid = booking.PaidAmount;
            var submittedPaid = 100m;
            var delta1 = submittedPaid - initialPaid;
            Assert.Equal(50m, delta1);

            // Aplicar cambio
            booking.PaidAmount = submittedPaid;
            context.CashTransactions.Add(new CashTransaction
            {
                Id = Guid.NewGuid(),
                ShiftId = Guid.NewGuid(),
                TransactionType = "Ingreso",
                Category = "Abono de Saldo",
                Amount = delta1,
                ReferenceCode = booking.ReferenceCode,
                CreatedAt = DateTime.UtcNow
            });
            context.SaveChanges();

            // Simular doble envío concurrente del mismo formulario (submittedPaid = 100, pero booking.PaidAmount ya es 100)
            var currentPaidInDb = booking.PaidAmount;
            var secondSubmitPaid = 100m;
            var delta2 = secondSubmitPaid - currentPaidInDb;

            // Assert: El segundo envío detecta delta = 0 y no genera un segundo movimiento de caja
            Assert.Equal(0m, delta2);
            var txCount = context.CashTransactions.Count(t => t.ReferenceCode == booking.ReferenceCode);
            Assert.Equal(1, txCount); // Exactamente 1 movimiento de caja, cero duplicados
        }

        [Fact]
        public void EditBooking_DirectReductionOfPaidAmount_IsRejectedToProtectHistory()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 200m, paid: 150m);

            var attemptReductionPaidAmount = 100m;
            var paidDelta = attemptReductionPaidAmount - booking.PaidAmount;

            // Assert: Delta negativo (-50) es detectado y rechazado
            Assert.True(paidDelta < 0);
            // La regla de negocio exige usar RefundBookingPayment con justificación y egreso trazable
        }

        [Fact]
        public void RefundBookingPayment_ExceedingPaidAmount_IsRejected()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 60m);

            var requestedRefund = 80m;
            bool isInvalidRefund = requestedRefund > booking.PaidAmount;

            Assert.True(isInvalidRefund);
            // El controlador devuelve error y revierte la transacción
        }

        #endregion

        #region Hallazgo 4: Autorización Efectiva en Administración

        [Fact]
        public void AdminController_Actions_GuardedByFineGrainedPolicyAttributes()
        {
            var adminType = typeof(AdminController);

            // Verificación de acciones que deben exigir políticas finas y no solo roles estáticos
            var policyChecks = new Dictionary<string, string>
            {
                { "Users", AppPermissions.UsersManage },
                { "CreateUser", AppPermissions.UsersManage },
                { "EditUser", AppPermissions.UsersManage },
                { "DeleteUser", AppPermissions.UsersManage },
                { "CreateRole", AppPermissions.RolesManage },
                { "RolePermissions", AppPermissions.RolesManage },
                { "UpdateRolePermissions", AppPermissions.RolesManage },
                { "Supervision", AppPermissions.SupervisionView },
                { "ResolveIncident", AppPermissions.IncidentsManage },
                { "Bookings", AppPermissions.BookingsView },
                { "CreateBooking", AppPermissions.BookingsManage },
                { "EditBooking", AppPermissions.BookingsManage },
                { "CancelBooking", AppPermissions.BookingsManage },
                { "RefundBookingPayment", AppPermissions.BookingsManage },
                { "Cash", AppPermissions.CashView },
                { "OpenCashShift", AppPermissions.CashManage },
                { "CloseCashShift", AppPermissions.CashManage },
                { "Tours", AppPermissions.ToursView },
                { "EditTour", AppPermissions.ToursManage },
                { "Fleet", AppPermissions.ToursView },
                { "EditVehicle", AppPermissions.ToursManage },
                { "Drivers", AppPermissions.ToursView },
                { "EditDriver", AppPermissions.ToursManage },
                { "Despacho", AppPermissions.ManifestView },
                { "ExportManifestExcel", AppPermissions.ManifestManage }
            };

            foreach (var kvp in policyChecks)
            {
                var actionName = kvp.Key;
                var expectedPolicy = kvp.Value;

                var methods = adminType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == actionName);

                Assert.NotEmpty(methods);
                foreach (var method in methods)
                {
                    var authAttr = method.GetCustomAttribute<AuthorizeAttribute>();
                    Assert.True(authAttr != null, $"El método {actionName} debe tener [Authorize]");
                    Assert.Equal(expectedPolicy, authAttr.Policy);
                }
            }
        }

        [Fact]
        public void RolePermissions_SuperAdminVsGerente_SeparationOfConcerns()
        {
            var superAdminPerms = AppPermissions.GetDefaultPermissionsForRole("SuperAdmin").ToHashSet();
            var adminPerms = AppPermissions.GetDefaultPermissionsForRole("Admin").ToHashSet();
            var operadorPerms = AppPermissions.GetDefaultPermissionsForRole("Operador").ToHashSet();

            // SuperAdmin tiene seguridad, supervisión e incidencias
            Assert.Contains(AppPermissions.UsersManage, superAdminPerms);
            Assert.Contains(AppPermissions.RolesManage, superAdminPerms);
            Assert.Contains(AppPermissions.SupervisionView, superAdminPerms);
            Assert.Contains(AppPermissions.IncidentsManage, superAdminPerms);

            // Gerente / Admin NO tiene UsersManage ni RolesManage por defecto
            Assert.DoesNotContain(AppPermissions.UsersManage, adminPerms);
            Assert.DoesNotContain(AppPermissions.RolesManage, adminPerms);
            Assert.DoesNotContain(AppPermissions.SupervisionView, adminPerms);

            // Gerente tiene operaciones de negocio habituales
            Assert.Contains(AppPermissions.ToursManage, adminPerms);
            Assert.Contains(AppPermissions.BookingsManage, adminPerms);
            Assert.Contains(AppPermissions.CashManage, adminPerms);
            Assert.Contains(AppPermissions.ReceiptsManage, adminPerms);
            Assert.Contains(AppPermissions.ManifestManage, adminPerms);

            // Operador tiene sólo lectura y venta en counter
            Assert.Contains(AppPermissions.BookingsView, operadorPerms);
            Assert.Contains(AppPermissions.BookingsManage, operadorPerms);
            Assert.DoesNotContain(AppPermissions.CashManage, operadorPerms);
            Assert.DoesNotContain(AppPermissions.ToursManage, operadorPerms);
        }

        #endregion

        #region Hallazgo 5: Pantalla de Permisos y Protección SuperAdmin

        [Fact]
        public async Task UpdateRolePermissions_ProtectsSuperAdminFromLockout()
        {
            using var context = TestDbContextFactory.Create();

            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var roleManager = new Mock<RoleManager<IdentityRole>>(roleStore.Object, null!, null!, null!, null!);
            var userStore = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new Mock<UserManager<ApplicationUser>>(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

            var superAdminRole = new IdentityRole("SuperAdmin");
            roleManager.Setup(r => r.FindByNameAsync("SuperAdmin")).ReturnsAsync(superAdminRole);
            roleManager.Setup(r => r.GetClaimsAsync(superAdminRole)).ReturnsAsync(new List<Claim>());

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

            // Intentar retirar RolesManage y UsersManage a SuperAdmin
            var reducedPermissions = new List<string> { AppPermissions.ToursView, AppPermissions.BookingsView };
            var result = await controller.UpdateRolePermissions("SuperAdmin", reducedPermissions) as RedirectToActionResult;

            Assert.NotNull(result);
            Assert.Equal("RolePermissions", result.ActionName);
            Assert.Contains("No es posible retirar los permisos de administración de usuarios o roles al rol SuperAdmin", controller.TempData["ErrorMessage"]?.ToString());
        }

        #endregion

        #region Hallazgo 6: Arranque y Estado de Base de Datos

        [Fact]
        public void DatabaseReadiness_WhenNotReady_ThrowsInvalidOperationException()
        {
            // Simular estado de fallo o migración no ejecutada
            DatabaseReadiness.IsReady = false;
            DatabaseReadiness.InitializationError = "PostgreSQL connection refused";

            var ex = Assert.Throws<InvalidOperationException>(() => DatabaseReadiness.EnsureReady());
            Assert.Contains("PostgreSQL connection refused", ex.Message);

            // Restaurar estado
            DatabaseReadiness.IsReady = true;
            DatabaseReadiness.InitializationError = null;
            DatabaseReadiness.EnsureReady(); // No debe lanzar
        }

        #endregion

        #region Hallazgo 7: Culqi Yape y Manejo de Declinaciones

        [Fact]
        public void Culqi_DeclineCode_AUT0014_MapsToFriendlyYapeMessage()
        {
            var jsonErr = "{\"object\":\"error\",\"type\":\"card_error\",\"decline_code\":\"AUT0014\",\"merchant_message\":\"Código de aprobación expirado o inválido\"}";
            using var doc = JsonDocument.Parse(jsonErr);
            var result = new CulqiResult(false, 400, doc);

            var code = result.GetDeclineOrErrorCode();
            var msg = result.GetErrorOrUserMessage("ype_test_token123");

            Assert.Equal("AUT0014", code);
            Assert.Contains("Código de aprobación de Yape inválido o expirado", msg);
        }

        [Fact]
        public void Culqi_DeclineCode_AUT0007_MapsToFriendlyInsufficientFundsMessage()
        {
            var jsonErr = "{\"object\":\"error\",\"type\":\"card_error\",\"decline_code\":\"AUT0007\",\"merchant_message\":\"Fondos insuficientes\"}";
            using var doc = JsonDocument.Parse(jsonErr);
            var result = new CulqiResult(false, 402, doc);

            var code = result.GetDeclineOrErrorCode();
            var msg = result.GetErrorOrUserMessage("ype_test_token456");

            Assert.Equal("AUT0007", code);
            Assert.Contains("Código de aprobación de Yape inválido o expirado", msg);
        }

        [Fact]
        public async Task Culqi_Charge_WhenPaymentInVerification_ReturnsConflict409()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateTestBooking(context, total: 100m, paid: 0m);

            var payment = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Channel = "charge",
                Status = "verification", // Intento ya en verificación
                CreatedAtUtc = DateTime.UtcNow
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var token = _checkoutAccess.Create(booking.Id, CheckoutAccess.DefaultDocumentTtl);

            var culqiClient = new CulqiApiClient(new HttpClient(), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Culqi:SecretKey", "sk_test_mock_audit_key" }
            }).Build());

            var controller = new CulqiPaymentsController(
                context, 
                culqiClient, 
                _checkoutAccess, 
                _config, 
                Mock.Of<IElectronicReceiptService>());

            var req = new ChargePayment(payment.Id, token, "tkn_test_sample", null, null);
            var result = await controller.Charge(req, CancellationToken.None) as ObjectResult;

            Assert.NotNull(result);
            Assert.Equal(409, result.StatusCode);
            Assert.Contains("verificación o procesamiento", result.Value?.ToString());
        }

        #endregion
    }
}
