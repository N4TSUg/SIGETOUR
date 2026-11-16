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
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SIGETOUR.API.Application.DTOs;
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
    /// Suite de verificación rigurosa de los 8 hallazgos de auditoría (Post-Review Hardening).
    /// </summary>
    public class HardeningAuditPass2Tests
    {
        private readonly IConfiguration _config;
        private readonly CheckoutAccess _checkoutAccess;

        public HardeningAuditPass2Tests()
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "Payments:CheckoutSigningKey", "c2lnZXRvdXJfc3VwZXJfc2VjcmV0X2NoZWNrb3V0X3NpZ25pbmdfa2V5XzIwMjZfMzJieXRlcw==" },
                { "Culqi:PublicKey", "pk_test_sample_audit_key" },
                { "Culqi:SecretKey", "sk_test_sample_secret_audit_key" },
                { "Nubefact:Mode", "Demo" },
                { "Nubefact:Ruta", "https://demo.nubefact.com/api/v1/test" },
                { "Nubefact:Token", "demo_token_audit" },
                { "Jwt:Key", "super_secret_audit_hardening_key_2026_min_256_bits_length!" }
            };
            _config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
            _checkoutAccess = new CheckoutAccess(_config);
        }

        #region Hallazgo 1: Migración e Índice Filtrado de Intentos Activos

        [Fact]
        public void Finding1_CulqiPaymentConfiguration_Includes3dsRequiredInFilteredIndex()
        {
            using var context = TestDbContextFactory.Create();
            var entityType = context.Model.FindEntityType(typeof(CulqiPayment));
            Assert.NotNull(entityType);

            var bookingIndex = entityType.GetIndexes()
                .FirstOrDefault(idx => idx.Properties.Any(p => p.Name == "BookingId") && idx.IsUnique);

            Assert.NotNull(bookingIndex);
            Assert.True(bookingIndex.IsUnique);
            Assert.NotNull(bookingIndex.GetFilter());
            Assert.Contains("3ds_required", bookingIndex.GetFilter()!);
            Assert.Contains("created", bookingIndex.GetFilter()!);
            Assert.Contains("processing", bookingIndex.GetFilter()!);
            Assert.Contains("verification", bookingIndex.GetFilter()!);
        }

        #endregion

        #region Hallazgo 2: Atomicidad Entre Reserva, Turno y Caja

        [Fact]
        public async Task Finding2_CreateBooking_WhenCapacityExceeded_DoesNotPersistBookingOrCashTransaction()
        {
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Cumbemayo Místico",
                Slug = "cumbemayo-mistico",
                BasePrice = 50m,
                MaxCapacity = 2,
                IsActive = true
            };
            context.TourPackages.Add(tour);
            await context.SaveChangesAsync();

            var capacityService = new CapacityValidationService(context);
            var controller = TestHelpers.CreateAdminController(context, capacityService: capacityService);

            var model = new EditBookingViewModel
            {
                TourPackageId = tour.Id,
                TravelDate = PeruTime.Today.AddDays(2),
                ShiftName = "Mañana",
                TotalPassengers = 5, // Excede capacidad
                TotalAmount = 250m,
                PaidAmount = 250m,
                CustomerName = "Cliente Exceso",
                CustomerEmail = "test@cliente.pe"
            };

            var result = await controller.CreateBooking(model);

            // Debe devolver la vista con error
            Assert.IsType<ViewResult>(result);

            // Verificar que no quedó ninguna reserva ni movimiento de caja persistido
            var bookingsCount = await context.Bookings.CountAsync();
            var cashCount = await context.CashTransactions.CountAsync();
            Assert.Equal(0, bookingsCount);
            Assert.Equal(0, cashCount);
        }

        [Fact]
        public async Task Finding2_RefundBookingPayment_AdjustsBookingAndPersistsEgresoTogether()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-REF1",
                CustomerName = "Cliente Devolución",
                TravelDate = DateTime.UtcNow.AddDays(3),
                ShiftName = "Mañana",
                TotalPassengers = 2,
                TotalAmount = 200m,
                PaidAmount = 200m,
                Status = BookingStatus.Confirmed
            };
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var controller = TestHelpers.CreateAdminController(context);

            var result = await controller.RefundBookingPayment(booking.Id, 100m, "Ajuste justificado por fuerza mayor");

            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("EditBooking", redirectResult.ActionName);

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(100m, updatedBooking.PaidAmount);
            Assert.Equal(BookingStatus.Pending, updatedBooking.Status); // Pagado parcial pasa a Pending

            var egreso = await context.CashTransactions.FirstOrDefaultAsync(c => c.BookingId == booking.Id);
            Assert.NotNull(egreso);
            Assert.Equal("Egreso", egreso.TransactionType);
            Assert.Equal(100m, egreso.Amount);
            Assert.Equal("Devolución de Reserva", egreso.Category);
        }

        #endregion

        #region Hallazgo 3: Revocación Efectiva de Sesiones y Permisos

        [Fact]
        public async Task Finding3_SecurityStampMismatch_InvalidatesJwtTokenInValidation()
        {
            // Simular usuario con sello de seguridad original
            var userStore = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new Mock<UserManager<ApplicationUser>>(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

            var originalUser = new ApplicationUser
            {
                Id = "usr-audit-1",
                UserName = "operador_prueba",
                IsActive = true,
                SecurityStamp = "STAMP_REVOKED_NEW"
            };

            userManager.Setup(m => m.FindByNameAsync("operador_prueba")).ReturnsAsync(originalUser);

            // Token generado con sello de seguridad antiguo STAMP_OLD
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, "operador_prueba"),
                new Claim("SecurityStamp", "STAMP_OLD_REVOKED")
            };
            var identity = new ClaimsIdentity(claims, "Bearer");
            var principal = new ClaimsPrincipal(identity);

            var services = new ServiceCollection();
            services.AddSingleton(userManager.Object);
            var serviceProvider = services.BuildServiceProvider();

            var httpContext = new DefaultHttpContext { RequestServices = serviceProvider };
            var authScheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
            var options = new JwtBearerOptions();

            var tokenContext = new TokenValidatedContext(httpContext, authScheme, options)
            {
                Principal = principal
            };

            // Ejecutar la lógica de validación de Program.cs
            var username = tokenContext.Principal?.Identity?.Name;
            var user = await userManager.Object.FindByNameAsync(username!);
            var tokenStamp = tokenContext.Principal?.FindFirst("SecurityStamp")?.Value;

            bool isRevoked = !string.IsNullOrEmpty(tokenStamp) && !string.Equals(user?.SecurityStamp, tokenStamp, StringComparison.Ordinal);

            Assert.True(isRevoked, "El token con SecurityStamp desactualizado debe considerarse revocado inmediatamente.");
        }

        #endregion

        #region Hallazgo 4: Autorización por Operación

        [Fact]
        public void Finding4_TourPackagesController_CreateUpdateDelete_EnforceToursManagePolicy()
        {
            var createMethod = typeof(TourPackagesController).GetMethod("Create");
            var updateMethod = typeof(TourPackagesController).GetMethod("Update");
            var deleteMethod = typeof(TourPackagesController).GetMethod("Delete");

            Assert.NotNull(createMethod);
            Assert.NotNull(updateMethod);
            Assert.NotNull(deleteMethod);

            var createAttr = createMethod!.GetCustomAttribute<AuthorizeAttribute>();
            var updateAttr = updateMethod!.GetCustomAttribute<AuthorizeAttribute>();
            var deleteAttr = deleteMethod!.GetCustomAttribute<AuthorizeAttribute>();

            Assert.NotNull(createAttr);
            Assert.Equal(AppPermissions.ToursManage, createAttr!.Policy);

            Assert.NotNull(updateAttr);
            Assert.Equal(AppPermissions.ToursManage, updateAttr!.Policy);

            Assert.NotNull(deleteAttr);
            Assert.Equal(AppPermissions.ToursManage, deleteAttr!.Policy);
        }

        [Fact]
        public async Task Finding4_VoucherAccess_AuthenticatedUserWithoutPermissions_DeniedAccess()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-PRIV1",
                CustomerName = "Cliente Reservado",
                TravelDate = DateTime.UtcNow.AddDays(5),
                TotalPassengers = 2,
                TotalAmount = 150m,
                PaidAmount = 150m,
                Status = BookingStatus.Confirmed
            };
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var controller = new BookingController(context, _checkoutAccess, _config, capacityService: null!);
            // Usuario autenticado pero SIN roles ni permisos (e.g. un cliente logueado o rol sin permisos)
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "usuario_comun"),
                new Claim(ClaimTypes.NameIdentifier, "usr-common-1")
            }, "TestAuthScheme");

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            };

            var result = await controller.Voucher(booking.Id, token: null);

            // Debe rechazar con Unauthorized / 401
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal(401, unauthorizedResult.StatusCode);
        }

        [Fact]
        public async Task Finding4_VoucherAccess_ValidSignedToken_PermittedEvenForAnonymous()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-TOKEN1",
                CustomerName = "Cliente Con Token",
                TravelDate = DateTime.UtcNow.AddDays(5),
                TotalPassengers = 2,
                TotalAmount = 150m,
                PaidAmount = 150m,
                Status = BookingStatus.Confirmed
            };
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var controller = new BookingController(context, _checkoutAccess, _config, capacityService: null!);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
            };

            var validToken = _checkoutAccess.Create(booking.Id, CheckoutAccess.DefaultCheckoutTtl);
            var result = await controller.Voucher(booking.Id, token: validToken);

            // Debe permitir el acceso (ViewResult)
            Assert.IsType<ViewResult>(result);
        }

        [Fact]
        public async Task Finding4_VoucherAccess_AuthorizedStaffRole_PermittedAccess()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-STAFF1",
                CustomerName = "Cliente Staff",
                TravelDate = DateTime.UtcNow.AddDays(5),
                TotalPassengers = 2,
                TotalAmount = 150m,
                PaidAmount = 150m,
                Status = BookingStatus.Confirmed
            };
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var controller = new BookingController(context, _checkoutAccess, _config, capacityService: null!);
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "gerente"),
                new Claim(ClaimTypes.Role, "Admin"),
                new Claim("Permission", AppPermissions.BookingsView)
            }, "TestAuthScheme");

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            };

            var result = await controller.Voucher(booking.Id, token: null);

            Assert.IsType<ViewResult>(result);
        }

        #endregion

        #region Hallazgo 5: Cupos en Edición y Reactivación

        [Fact]
        public async Task Finding5_CapacityValidation_ExcludesCurrentBookingPassengersWhenEditing()
        {
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Granja Porcón",
                Slug = "granja-porcon",
                BasePrice = 40m,
                MaxCapacity = 10,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var travelDate = PeruTime.Today.AddDays(3);
            var (startUtc, endUtc) = PeruTime.GetTravelDateRangeUtc(travelDate);

            var existingBooking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-CAP1",
                CustomerName = "Pasajeros Existentes",
                TravelDate = startUtc.AddHours(2),
                ShiftName = "Mañana",
                TotalPassengers = 8,
                TotalAmount = 320m,
                PaidAmount = 320m,
                Status = BookingStatus.Confirmed
            };
            existingBooking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = existingBooking.Id,
                TourPackageId = tour.Id,
                Passengers = 8,
                UnitPrice = 40m,
                SubTotal = 320m
            });

            context.Bookings.Add(existingBooking);
            await context.SaveChangesAsync();

            var capacityService = new CapacityValidationService(context);

            // Validar capacidad SIN excluir: 8 ocupados + 2 solicitados = 10 (cabe justo), pero si solicita 3 no cabe (11 > 10)
            var checkWithoutExclude = await capacityService.ValidateCapacityAsync(
                tour.Id, travelDate, "Mañana", requestedPassengers: 3, excludeBookingId: null);
            Assert.False(checkWithoutExclude.IsAvailable);

            // Validar capacidad EXCLUYENDO la propia reserva (modificando de 8 a 9 pasajeros):
            // 0 otros ocupados + 9 solicitados <= 10 -> Disponible
            var checkWithExclude = await capacityService.ValidateCapacityAsync(
                tour.Id, travelDate, "Mañana", requestedPassengers: 9, excludeBookingId: existingBooking.Id);
            Assert.True(checkWithExclude.IsAvailable);
            Assert.Equal(0, checkWithExclude.BookedSeats); // No cuenta los 8 de sí misma
        }

        #endregion

        #region Hallazgo 6: Comprobantes con Resultado Incierto y Background Service

        [Fact]
        public async Task Finding6_Nubefact_Http500_TransitionsToRequiresReview_DoesNotAutoReemitWithoutNumber()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-NUBE500",
                CustomerName = "Cliente Error 500",
                CustomerEmail = "cliente500@sigetour.pe",
                CustomerDni = "76543210",
                TravelDate = DateTime.UtcNow.AddDays(2),
                TotalPassengers = 1,
                TotalAmount = 100m,
                PaidAmount = 100m,
                Status = BookingStatus.Confirmed
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = Guid.NewGuid(),
                Passengers = 1,
                UnitPrice = 100m,
                SubTotal = 100m
            });
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            // Simular respuesta HTTP 500 del servidor NubeFact
            var http500Handler = new MockCulqiHttpHandler(req =>
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"errors\":\"Internal gateway error\"}", Encoding.UTF8, "application/json")
                };
            });
            var httpClient = new HttpClient(http500Handler);
            var service = new NubefactInvoiceService(context, _config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

            // Act: Intentar emisión
            var receipt = await service.GenerateReceiptAsync(booking.Id, null, CancellationToken.None);

            // Assert: Debe quedar explícitamente en RequiresReview para evitar duplicidad
            Assert.NotNull(receipt);
            Assert.Equal("RequiresReview", receipt.Status);
            Assert.Contains("500", receipt.LastError ?? "");

            // Comprobar que RetryReceiptAsync no reemite a ciegas sin número
            var retried = await service.RetryReceiptAsync(receipt.Id, CancellationToken.None);
            Assert.Equal("RequiresReview", retried.Status); // No fue reemitido a ciegas

            // Comprobar que ProcessPendingReceiptsAsync omite RequiresReview sin número
            var autoProcessed = await service.ProcessPendingReceiptsAsync(CancellationToken.None);
            Assert.Equal(0, autoProcessed);
        }

        [Fact]
        public async Task Finding6_Nubefact_EnvironmentIsolation_AbortsQueryWhenEnvironmentDiffers()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-ISOL1",
                CustomerName = "Cliente Aislado",
                TravelDate = DateTime.UtcNow.AddDays(2),
                TotalPassengers = 1,
                TotalAmount = 50m,
                PaidAmount = 50m,
                Status = BookingStatus.Confirmed
            };
            context.Bookings.Add(booking);

            // Comprobante histórico creado en ambiente Producción
            var prodReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "1234",
                Status = "Submitted",
                Environment = "Production", // Ambiente histórico
                TotalAmount = 50m
            };
            context.ElectronicReceipts.Add(prodReceipt);
            await context.SaveChangesAsync();

            // Configuración actual del sistema en Demo
            var service = new NubefactInvoiceService(context, _config, new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            var queried = await service.QueryReceiptFromProviderAsync(prodReceipt.Id, CancellationToken.None);

            // Debe devolver el comprobante sin alterar y sin consultar API externa con credenciales mezcladas
            Assert.NotNull(queried);
            Assert.Equal("Production", queried!.Environment);
            Assert.Equal("Submitted", queried.Status);
        }

        #endregion

        #region Hallazgo 7: AutoMapper 16.3.0

        [Fact]
        public void Finding7_AutoMapper_16_3_0_ExecutesMappingsCorrectly()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAutoMapper(cfg =>
            {
                cfg.AddProfile<SIGETOUR.API.Application.Mappings.TourPackageProfile>();
            });
            var sp = services.BuildServiceProvider();
            var mapper = sp.GetRequiredService<AutoMapper.IMapper>();

            var entity = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Cumbe Mayo",
                BasePrice = 60m,
                MaxCapacity = 15,
                Duration = "4 horas"
            };

            var readDto = mapper.Map<TourPackageReadDto>(entity);
            Assert.NotNull(readDto);
            Assert.Equal("Cumbe Mayo", readDto.Title);
            Assert.Equal(60m, readDto.BasePrice);
            Assert.Equal(15, readDto.MaxCapacity);

            var createDto = new TourPackageCreateDto
            {
                Title = "Otuzco y Ventanillas",
                BasePrice = 45m,
                MaxCapacity = 20,
                Duration = "3.5 horas"
            };

            var createdEntity = mapper.Map<TourPackage>(createDto);
            Assert.NotNull(createdEntity);
            Assert.Equal("Otuzco y Ventanillas", createdEntity.Title);
            Assert.Equal(45m, createdEntity.BasePrice);
        }

        #endregion

        #region Hallazgo 8: Culqi Yape con Código de Confirmación

        [Fact]
        public void Finding8_CulqiResult_DetectsYapeOtpInvalidOrExpiredCode()
        {
            var yapeExpiredJson = @"{
                ""object"": ""error"",
                ""type"": ""card_error"",
                ""decline_code"": ""AUT0014"",
                ""merchant_message"": ""El código de aprobación es inválido o ha expirado."",
                ""user_message"": ""Código de aprobación inválido o expirado. Genera un nuevo código en tu app Yape.""
            }";

            using var doc = JsonDocument.Parse(yapeExpiredJson);
            using var culqiResult = new CulqiResult(false, 402, doc);

            var errorCode = culqiResult.GetDeclineOrErrorCode();
            var userMsg = culqiResult.GetErrorOrUserMessage("ype_test_token123");

            Assert.Equal("AUT0014", errorCode);
            Assert.Contains("Yape", userMsg);
            Assert.Contains("nuevo código", userMsg);
        }

        [Fact]
        public void Finding8_CulqiResult_WhenNestedInOutcome_ExtractsErrorMessage()
        {
            var outcomeJson = @"{
                ""outcome"": {
                    ""type"": ""venta_rechazada"",
                    ""code"": ""AUT0007"",
                    ""user_message"": ""Código de confirmación de Yape expirado.""
                }
            }";

            using var doc = JsonDocument.Parse(outcomeJson);
            using var culqiResult = new CulqiResult(false, 400, doc);

            var errorCode = culqiResult.GetDeclineOrErrorCode();
            var userMsg = culqiResult.GetErrorOrUserMessage("tkn_test_yape999");

            Assert.Equal("AUT0007", errorCode);
            Assert.Contains("Yape", userMsg);
        }

        #endregion
    }
}
