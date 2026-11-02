using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Controllers;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;
using SIGETOUR.Tests.Helpers;
using SIGETOUR.Tests.Payments;
using Xunit;

namespace SIGETOUR.Tests.Audit
{
    public class HardeningAuditTests
    {
        private readonly IConfiguration _config;
        private readonly CheckoutAccess _checkoutAccess;

        public HardeningAuditTests()
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "Payments:CheckoutSigningKey", "c2lnZXRvdXJfc3VwZXJfc2VjcmV0X2NoZWNrb3V0X3NpZ25pbmdfa2V5XzIwMjZfMzJieXRlcw==" },
                { "Culqi:PublicKey", "pk_test_sample_audit_key" },
                { "Culqi:SecretKey", "sk_test_sample_secret_audit_key" },
                { "Culqi:WebhookUser", "webhook_user_sigetour" },
                { "Culqi:WebhookPassword", "webhook_pass_secret_test" },
                { "Nubefact:Mode", "Demo" },
                { "Nubefact:Ruta", "https://demo.nubefact.com/api/v1/test" },
                { "Nubefact:Token", "demo_token_audit" }
            };
            _config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
            _checkoutAccess = new CheckoutAccess(_config);
        }

        // ==========================================
        // BLOQUE 1: SEGURIDAD DEL COBRO CULQI
        // ==========================================

        [Fact]
        public async Task Bloque1_Charge_WhenStatusIsVerificationOrProcessing_Returns409Conflict_ZeroExternalCalls()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-B1-VERIF",
                TotalAmount = 100m,
                PaidAmount = 0m,
                CustomerEmail = "verif@sigetour.com",
                Status = BookingStatus.Pending
            };
            context.Bookings.Add(booking);

            var attempt = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                Booking = booking,
                AmountCents = 10000,
                Channel = "charge",
                Status = "verification", // Estado ambiguo tras timeout previo
                ProviderId = null,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            };
            context.Set<CulqiPayment>().Add(attempt);
            await context.SaveChangesAsync();

            int externalCallCount = 0;
            var mockClient = new CulqiApiClient(new HttpClient(new MockCulqiHttpHandler(req =>
            {
                externalCallCount++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            })), _config);

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = new CulqiPaymentsController(context, mockClient, _checkoutAccess, _config, Mock.Of<IElectronicReceiptService>());

            // Act: El cliente reenvía Charge con un nuevo source_id
            var result = await controller.Charge(new ChargePayment(attempt.Id, token, "tkn_test_card_new_token"), CancellationToken.None);

            // Assert: Debe bloquearse inmediatamente con 409 Conflict sin realizar llamadas al proveedor externo
            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Equal(0, externalCallCount); // Cero llamadas externas = No doble cobro
        }

        [Fact]
        public async Task Bloque1_Webhook_ReceivedBeforeProviderId_IsPersistedInWebhookEventsTable()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-B1-WEBHOOK",
                TotalAmount = 50m,
                CustomerEmail = "webhook@sigetour.com",
                Status = BookingStatus.Pending
            };
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var mockClient = new CulqiApiClient(new HttpClient(new MockCulqiHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), _config);
            var controller = new CulqiPaymentsController(context, mockClient, _checkoutAccess, _config, Mock.Of<IElectronicReceiptService>());

            var rawPayload = "{\"id\":\"evt_test_ahead\",\"type\":\"order.status.changed\",\"data\":{\"id\":\"ord_test_unmapped_123\",\"state\":\"paid\"}}";
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes("webhook_user_sigetour:webhook_pass_secret_test"));
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers.Authorization = $"Basic {creds}";
            httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(rawPayload));
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            // Act: Simular webhook recibido antes de que el frontend guarde ProviderId
            var result = await controller.Webhook(CancellationToken.None);
            Assert.IsType<OkResult>(result);

            // Assert: El evento se persistió en PaymentWebhookEvents como pendiente
            var savedEvent = await context.PaymentWebhookEvents.FirstOrDefaultAsync(e => e.ProviderId == "ord_test_unmapped_123");
            Assert.NotNull(savedEvent);
            Assert.False(savedEvent.Processed);
            Assert.Equal("order.status.changed", savedEvent.EventType);
        }

        // ==========================================
        // BLOQUE 2: INTEGRIDAD Y RECUPERACIÓN NUBEFACT
        // ==========================================

        [Theory]
        [InlineData("0")]
        [InlineData("0000")]
        public void Bloque2_Nubefact_SafeParsingOfSunatResponseCode_HandlesStringAndNumber(string codeVal)
        {
            var jsonString = $"{{\"sunat_responsecode\": \"{codeVal}\"}}";
            using var doc = JsonDocument.Parse(jsonString);
            var prop = doc.RootElement.GetProperty("sunat_responsecode");

            Assert.True(NubefactInvoiceService.IsSunatSuccessResponseCode(prop));
        }

        [Fact]
        public void Bloque2_Nubefact_SafeParsingOfSunatResponseCode_NumericZero()
        {
            var jsonString = "{\"sunat_responsecode\": 0}";
            using var doc = JsonDocument.Parse(jsonString);
            var prop = doc.RootElement.GetProperty("sunat_responsecode");

            Assert.True(NubefactInvoiceService.IsSunatSuccessResponseCode(prop));
        }

        [Fact]
        public async Task Bloque2_RetryReceiptAsync_NonExistentReceiptId_ThrowsInvalidOperationException()
        {
            using var context = TestDbContextFactory.Create();
            var service = new NubefactInvoiceService(context, _config, new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            var randomId = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetryReceiptAsync(randomId));
        }

        // ==========================================
        // BLOQUE 3: GERENTE, SUPERADMIN Y ACCESO A COMPRADORES
        // ==========================================

        [Fact]
        public async Task Bloque3_DeactivatedUser_CannotLogin()
        {
            var user = new ApplicationUser
            {
                Id = "deactivated-user-id",
                UserName = "deactivated",
                Email = "deactivated@sigetour.com",
                IsActive = false // Desactivado
            };

            var mockUserManager = TestHelpers.CreateMockUserManager(new List<ApplicationUser> { user });
            var mockSignInManager = TestHelpers.CreateMockSignInManager(mockUserManager);

            var controller = new AccountController(mockSignInManager.Object, mockUserManager.Object);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var loginModel = new SIGETOUR.API.ViewModels.LoginViewModel
            {
                EmailOrUsername = "deactivated",
                Password = "AnyPassword123"
            };

            var result = await controller.Login(loginModel);

            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.False(controller.ModelState.IsValid);
            Assert.Contains(controller.ModelState[string.Empty]!.Errors, e => e.ErrorMessage.Contains("desactivada"));
        }

        [Fact]
        public async Task Bloque3_AnonymousUser_WithInvalidToken_RejectedAtPayment()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-B3-TOKEN",
                TotalAmount = 50m,
                CustomerEmail = "token@sigetour.com"
            };
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var controller = new BookingController(context, _checkoutAccess, _config);
            var httpContext = new DefaultHttpContext();
            // Anónimo (no staff)
            httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            // Act: Usuario anónimo intenta entrar con token inválido o inventado
            var result = await controller.Payment(booking.Id, "invalid_or_fake_token");

            // Assert: Debe ser rechazado con Unauthorized
            Assert.IsType<UnauthorizedObjectResult>(result);
        }

        // ==========================================
        // BLOQUE 4: PRECIO, RESERVAS Y CAJA MANUAL
        // ==========================================

        [Fact]
        public async Task Bloque4_PromoCalculation_DoesNotMutateTrackedTourBasePrice()
        {
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Laguna Azul",
                Slug = "tour-laguna-azul",
                BasePrice = 100m, // Precio maestro original: S/ 100
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var featured = new FeaturedPackage
            {
                Id = Guid.NewGuid(),
                TourPackageId = tour.Id,
                PromoPrice = 70m // Precio promocional: S/ 70
            };
            context.FeaturedPackages.Add(featured);
            await context.SaveChangesAsync();

            var controller = new BookingController(context, _checkoutAccess, _config);

            var checkoutModel = new CheckoutRequestViewModel
            {
                TourId = tour.Id,
                CustomerName = "Promo Tester",
                CustomerDni = "71829304",
                CustomerEmail = "promo@sigetour.com",
                CustomerPhone = "987654321",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                Shift = "",
                Passengers = 2,
                BoardingPoint = "central"
            };

            var result = await controller.Checkout(checkoutModel);
            Assert.IsType<RedirectToActionResult>(result);

            // Verificar en la base de datos que el precio maestro de TourPackage NO fue sobreescrito
            var reloadedTour = await context.TourPackages.FindAsync(tour.Id);
            Assert.NotNull(reloadedTour);
            Assert.Equal(100m, reloadedTour.BasePrice); // Permanece inmutable en S/ 100

            // Y que el precio contratado en el ítem de la reserva sí usó la tarifa promocional efectiva
            var savedBooking = await context.Bookings.Include(b => b.Items).FirstOrDefaultAsync(b => b.CustomerEmail == "promo@sigetour.com");
            Assert.NotNull(savedBooking);
            Assert.Equal(140m, savedBooking.TotalAmount); // 2 * 70 = 140
            Assert.Equal(70m, savedBooking.Items.First().UnitPrice);
        }

        [Fact]
        public async Task Bloque4_CapacityCheck_PreventsOverselling()
        {
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Con Capacidad",
                BasePrice = 50m,
                MaxCapacity = 10, // Capacidad máxima: 10 pasajeros
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var travelDate = DateTime.UtcNow.Date.AddDays(3);

            // Reserva existente ocupando 8 asientos
            var existingBooking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-EXIST-8",
                TravelDate = travelDate,
                ShiftName = "Tarde",
                TotalPassengers = 8,
                Status = BookingStatus.Confirmed
            };
            existingBooking.Items.Add(new BookingItem { Id = Guid.NewGuid(), TourPackageId = tour.Id, Passengers = 8, SubTotal = 400m });
            context.Bookings.Add(existingBooking);
            await context.SaveChangesAsync();

            var controller = new BookingController(context, _checkoutAccess, _config);

            var checkoutModel = new CheckoutRequestViewModel
            {
                TourId = tour.Id,
                CustomerName = "Oversell Tester",
                CustomerDni = "72918293",
                CustomerEmail = "oversell@sigetour.com",
                CustomerPhone = "987654321",
                TravelDate = travelDate,
                Shift = "Tarde",
                Passengers = 4, // 8 + 4 = 12 > 10 (Sobreventa)
                BoardingPoint = "central"
            };

            var result = await controller.Checkout(checkoutModel);

            // Debe rechazar y devolver la vista con error de modelo
            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.False(controller.ModelState.IsValid);
        }

        [Fact]
        public async Task Bloque4_PastTravelDate_IsRejected()
        {
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage { Id = Guid.NewGuid(), Title = "Tour Test", BasePrice = 50m, IsActive = true };
            context.TourPackages.Add(tour);
            await context.SaveChangesAsync();

            var controller = new BookingController(context, _checkoutAccess, _config);

            var checkoutModel = new CheckoutRequestViewModel
            {
                TourId = tour.Id,
                CustomerName = "Past Date Tester",
                CustomerDni = "72918293",
                CustomerEmail = "past@sigetour.com",
                CustomerPhone = "987654321",
                TravelDate = DateTime.UtcNow.Date.AddDays(-2), // Fecha en el pasado
                Shift = "",
                Passengers = 1,
                BoardingPoint = "central"
            };

            var result = await controller.Checkout(checkoutModel);

            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.False(controller.ModelState.IsValid);
            Assert.True(controller.ModelState.ContainsKey("TravelDate"));
        }

        [Fact]
        public async Task Bloque4_Admin_EditBooking_IncrementalPayment_RecordsCashTransactionForDelta()
        {
            using var context = TestDbContextFactory.Create();
            var shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, ShiftNumber = "CJ-ADMIN-INC", OpenedAt = DateTime.UtcNow };
            context.CashRegisterShifts.Add(shift);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-DELTA-1",
                CustomerName = "Delta Tester",
                TotalAmount = 100m,
                PaidAmount = 40m, // Pagó inicialmente S/ 40
                Status = BookingStatus.Confirmed
            };
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var controller = TestHelpers.CreateAdminController(context);

            var editModel = new EditBookingViewModel
            {
                Id = booking.Id,
                CustomerName = "Delta Tester",
                TotalAmount = 100m,
                PaidAmount = 70m, // Nuevo monto: S/ 70 (Delta = +S/ 30)
                PaymentMethod = "Efectivo",
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.AddDays(2)
            };

            var result = await controller.EditBooking(editModel);
            Assert.IsType<RedirectToActionResult>(result);

            // Verificar que se registró una CashTransaction para el delta (+S/ 30.00)
            var deltaTx = await context.CashTransactions.FirstOrDefaultAsync(t => t.BookingId == booking.Id && t.Amount == 30m);
            Assert.NotNull(deltaTx);
            Assert.Equal("Abono de Saldo", deltaTx.Category);
            Assert.Equal(30m, deltaTx.Amount);
        }
    }
}
