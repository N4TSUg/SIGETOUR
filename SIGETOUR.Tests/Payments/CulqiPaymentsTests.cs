using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Controllers;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Payments
{
    public class MockCulqiHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockCulqiHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    public class CulqiPaymentsTests
    {
        private readonly IConfiguration _config;
        private readonly CheckoutAccess _checkoutAccess;

        public CulqiPaymentsTests()
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "Payments:CheckoutSigningKey", "test-signing-key-for-checkout-access-sigetour-sandbox-32chars" },
                { "Culqi:PublicKey", "pk_test_sample_public_key_123" },
                { "Culqi:SecretKey", "sk_test_sample_secret_key_123" },
                { "Culqi:WebhookUser", "webhook_user_sigetour" },
                { "Culqi:WebhookPassword", "webhook_pass_secret_test" }
            };

            _config = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            _checkoutAccess = new CheckoutAccess(_config);
        }

        private CulqiApiClient CreateMockCulqiClient(Func<HttpRequestMessage, HttpResponseMessage>? handler = null)
        {
            var defaultHandler = handler ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_123\",\"amount\":10000}", Encoding.UTF8, "application/json")
            });

            var httpClient = new HttpClient(new MockCulqiHttpHandler(defaultHandler));
            return new CulqiApiClient(httpClient, _config);
        }

        [Fact]
        public void CheckoutAccess_GenerateAndVerify_ReturnsTrue()
        {
            var bookingId = Guid.NewGuid();
            var token = _checkoutAccess.Create(bookingId, TimeSpan.FromMinutes(10));

            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.True(_checkoutAccess.Verify(bookingId, token));
        }

        [Fact]
        public void CheckoutAccess_Verify_TamperedToken_ReturnsFalse()
        {
            var bookingId = Guid.NewGuid();
            var token = _checkoutAccess.Create(bookingId, TimeSpan.FromMinutes(10));
            var tamperedToken = token + "tampered";

            Assert.False(_checkoutAccess.Verify(bookingId, tamperedToken));
        }

        [Fact]
        public void CheckoutAccess_Verify_ExpiredToken_ReturnsFalse()
        {
            var bookingId = Guid.NewGuid();
            var token = _checkoutAccess.Create(bookingId, TimeSpan.FromSeconds(-5));

            Assert.False(_checkoutAccess.Verify(bookingId, token));
        }

        [Fact]
        public void CheckoutAccess_Verify_DifferentBookingId_ReturnsFalse()
        {
            var bookingId1 = Guid.NewGuid();
            var bookingId2 = Guid.NewGuid();
            var token = _checkoutAccess.Create(bookingId1, TimeSpan.FromMinutes(10));

            Assert.False(_checkoutAccess.Verify(bookingId2, token));
        }

        [Fact]
        public async Task Prepare_Unauthorized_WhenTokenIsInvalid()
        {
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var booking = await context.Bookings.FirstAsync();

            var controller = new CulqiPaymentsController(context, CreateMockCulqiClient(), _checkoutAccess, _config);
            var request = new PreparePayment(booking.Id, "invalid_token", "full", "charge");

            var result = await controller.Prepare(request, CancellationToken.None);
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task Prepare_CalculatesCorrectCents_ForFullPayment()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-CULQI1",
                CustomerName = "Carlos Test",
                CustomerEmail = "carlos@test.com",
                TotalAmount = 150m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending
            };
            var item = new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                UnitPrice = 150m,
                SubTotal = 150m,
                Passengers = 1
            };
            booking.Items.Add(item);
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = new CulqiPaymentsController(context, CreateMockCulqiClient(), _checkoutAccess, _config);
            var request = new PreparePayment(booking.Id, token, "full", "charge");

            var result = await controller.Prepare(request, CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal(15000L, doc.RootElement.GetProperty("amountCents").GetInt64());
            Assert.Equal("PEN", doc.RootElement.GetProperty("currency").GetString());
        }

        [Fact]
        public async Task Prepare_CalculatesCorrectCents_ForAdvanceDeposit()
        {
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Huambocancha",
                RequiredAdvancePercentage = 40,
                BasePrice = 100m,
                Duration = "4h"
            };
            context.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-CULQI2",
                CustomerName = "Maria Test",
                CustomerEmail = "maria@test.com",
                TotalAmount = 200m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending
            };
            var item = new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                TourPackage = tour,
                UnitPrice = 100m,
                SubTotal = 200m,
                Passengers = 2
            };
            booking.Items.Add(item);
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = new CulqiPaymentsController(context, CreateMockCulqiClient(), _checkoutAccess, _config);
            var request = new PreparePayment(booking.Id, token, "advance", "charge");

            var result = await controller.Prepare(request, CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            // 40% of 200 = 80 -> 8000 cents
            Assert.Equal(8000L, doc.RootElement.GetProperty("amountCents").GetInt64());
        }

        [Fact]
        public async Task Prepare_ReturnsConflict_WhenDebtIsZero()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-PAID",
                CustomerName = "Ana Test",
                TotalAmount = 100m,
                PaidAmount = 100m,
                Status = BookingStatus.Confirmed
            };
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = new CulqiPaymentsController(context, CreateMockCulqiClient(), _checkoutAccess, _config);
            var request = new PreparePayment(booking.Id, token, "full", "charge");

            var result = await controller.Prepare(request, CancellationToken.None);
            Assert.IsType<ConflictObjectResult>(result);
        }

        [Fact]
        public async Task Charge_RejectsNonSandboxSourceTokens()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-TOKEN-TEST",
                CustomerName = "Luis Test",
                CustomerEmail = "luis@test.com",
                TotalAmount = 80m,
                PaidAmount = 0m
            };
            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 8000,
                Channel = "charge",
                Status = "created",
                Booking = booking
            };
            context.Bookings.Add(booking);
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = new CulqiPaymentsController(context, CreateMockCulqiClient(), _checkoutAccess, _config);
            var request = new ChargePayment(payment.Id, token, "tkn_live_invalid_sandbox");

            var result = await controller.Charge(request, CancellationToken.None);
            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task Webhook_RejectsInvalidBasicAuth()
        {
            using var context = TestDbContextFactory.Create();
            var controller = new CulqiPaymentsController(context, CreateMockCulqiClient(), _checkoutAccess, _config);

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("wrong:creds"));
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var result = await controller.Webhook(CancellationToken.None);
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task Webhook_AcceptsValidBasicAuth()
        {
            using var context = TestDbContextFactory.Create();
            var controller = new CulqiPaymentsController(context, CreateMockCulqiClient(), _checkoutAccess, _config);

            var httpContext = new DefaultHttpContext();
            var validAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes("webhook_user_sigetour:webhook_pass_secret_test"));
            httpContext.Request.Headers["Authorization"] = "Basic " + validAuth;
            httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"data\":{\"id\":\"chr_test_none\"}}"));
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var result = await controller.Webhook(CancellationToken.None);
            Assert.IsType<OkResult>(result);
        }

        [Fact]
        public async Task Prepare_ReusesExistingCreatedAttempt_Idempotent()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-IDEMP",
                CustomerName = "David Test",
                CustomerEmail = "david@test.com",
                TotalAmount = 120m,
                PaidAmount = 0m
            };
            var item = new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                UnitPrice = 120m,
                SubTotal = 120m,
                Passengers = 1
            };
            booking.Items.Add(item);
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = new CulqiPaymentsController(context, CreateMockCulqiClient(), _checkoutAccess, _config);
            var request = new PreparePayment(booking.Id, token, "full", "charge");

            var firstResult = await controller.Prepare(request, CancellationToken.None) as OkObjectResult;
            Assert.NotNull(firstResult);
            using var firstDoc = JsonDocument.Parse(JsonSerializer.Serialize(firstResult.Value));
            var firstAttemptId = firstDoc.RootElement.GetProperty("attemptId").GetGuid();

            // Llamada repetida (mismo monto y canal)
            var secondResult = await controller.Prepare(request, CancellationToken.None) as OkObjectResult;
            Assert.NotNull(secondResult);
            using var secondDoc = JsonDocument.Parse(JsonSerializer.Serialize(secondResult.Value));
            var secondAttemptId = secondDoc.RootElement.GetProperty("attemptId").GetGuid();

            Assert.Equal(firstAttemptId, secondAttemptId);
        }

        [Fact]
        public async Task Check_ReturnsStatusAndBookingId()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-CHECK-1",
                CustomerName = "Sara Test",
                CustomerEmail = "sara@test.com",
                TotalAmount = 100m,
                PaidAmount = 0m
            };
            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 10000,
                Channel = "charge",
                Status = "created",
                Booking = booking
            };
            context.Bookings.Add(booking);
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = new CulqiPaymentsController(context, CreateMockCulqiClient(), _checkoutAccess, _config);
            var request = new CheckPayment(payment.Id, token);

            var result = await controller.Check(request, CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal(payment.Id, doc.RootElement.GetProperty("attemptId").GetGuid());
            Assert.Equal("created", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(booking.Id, doc.RootElement.GetProperty("bookingId").GetGuid());
        }

        [Fact]
        public async Task Check_Unauthorized_WhenTokenIsInvalid()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-CHECK-2",
                CustomerName = "Raul Test",
                TotalAmount = 50m
            };
            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 5000,
                Channel = "charge",
                Status = "created",
                Booking = booking
            };
            context.Bookings.Add(booking);
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var controller = new CulqiPaymentsController(context, CreateMockCulqiClient(), _checkoutAccess, _config);
            var request = new CheckPayment(payment.Id, "invalid-token");

            var result = await controller.Check(request, CancellationToken.None);
            Assert.IsType<UnauthorizedResult>(result);
        }
    }
}