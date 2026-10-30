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
using Microsoft.Extensions.Logging.Abstractions;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Controllers;
using SIGETOUR.API.Controllers.UI;
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

        private CulqiPaymentsController CreateController(AppDbContext context, CulqiApiClient? client = null, IElectronicReceiptService? receiptService = null)
        {
            var culqiClient = client ?? CreateMockCulqiClient();
            var dummyHttp = new HttpClient(new MockCulqiHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var rService = receiptService ?? new NubefactInvoiceService(context, _config, dummyHttp, NullLogger<NubefactInvoiceService>.Instance);
            return new CulqiPaymentsController(context, culqiClient, _checkoutAccess, _config, rService);
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

            var controller = CreateController(context);
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
            var controller = CreateController(context);
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
            var controller = CreateController(context);
            var request = new PreparePayment(booking.Id, token, "deposit", "charge");

            var result = await controller.Prepare(request, CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal(8000L, doc.RootElement.GetProperty("amountCents").GetInt64());
        }

        [Fact]
        public async Task Charge_SuccessfulCardCharge_VerifiesAndConfirmsBooking()
        {
            using var context = TestDbContextFactory.Create();
            var shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, OpenedAt = DateTime.UtcNow };
            context.CashRegisterShifts.Add(shift);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-CHARGE-1",
                CustomerName = "Juan Perez",
                CustomerEmail = "juan@test.com",
                TotalAmount = 100m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending
            };
            var item = new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                UnitPrice = 100m,
                SubTotal = 100m,
                Passengers = 1
            };
            booking.Items.Add(item);
            context.Bookings.Add(booking);

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 10000,
                Channel = "charge",
                Status = "created",
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var mockClient = CreateMockCulqiClient(req =>
            {
                if (req.Method == HttpMethod.Post && req.RequestUri!.ToString().Contains("charges"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_card_1\",\"amount\":10000,\"capture\":true,\"outcome\":{\"type\":\"venta_exitosa\"}}", Encoding.UTF8, "application/json")
                    };
                }
                if (req.Method == HttpMethod.Get && req.RequestUri!.ToString().Contains("charges/chr_test_card_1"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_card_1\",\"amount\":10000,\"capture\":true,\"outcome\":{\"type\":\"venta_exitosa\"}}", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = CreateController(context, mockClient);
            var request = new ChargePayment(payment.Id, token, "tkn_test_valid_card");

            var result = await controller.Charge(request, CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal("paid", doc.RootElement.GetProperty("status").GetString());

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(BookingStatus.Confirmed, updatedBooking.Status);
            Assert.Equal(100m, updatedBooking.PaidAmount);

            var cashTx = await context.CashTransactions.FirstOrDefaultAsync(t => t.BookingId == booking.Id);
            Assert.NotNull(cashTx);
            Assert.Equal(100m, cashTx.Amount);
        }

        [Fact]
        public async Task Charge_DefinitiveRejection_MarksStatusFailed_AllowsImmediateRetry()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-DECLINED",
                CustomerName = "Carlos Decline",
                CustomerEmail = "carlos@test.com",
                TotalAmount = 100m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending
            };
            var item = new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 100m, SubTotal = 100m, Passengers = 1 };
            booking.Items.Add(item);
            context.Bookings.Add(booking);

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 10000,
                Channel = "charge",
                Status = "created",
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            // Simular respuesta de Culqi 402 Fondos Insuficientes
            var mockClient = CreateMockCulqiClient(req => new HttpResponseMessage(HttpStatusCode.PaymentRequired)
            {
                Content = new StringContent("{\"object\":\"error\",\"type\":\"card_error\",\"merchant_message\":\"Tarjeta rechazada\",\"user_message\":\"Fondos insuficientes en la tarjeta\"}", Encoding.UTF8, "application/json")
            });

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = CreateController(context, mockClient);
            var request = new ChargePayment(payment.Id, token, "tkn_test_no_funds");

            var result = await controller.Charge(request, CancellationToken.None) as BadRequestObjectResult;
            Assert.NotNull(result);

            // Verificar que el intento quedó en failed (NO en verification)
            var updatedPayment = await context.CulqiPayments.FindAsync(payment.Id);
            Assert.NotNull(updatedPayment);
            Assert.Equal("failed", updatedPayment.Status);

            // Comprobar que el usuario PUEDE preparar inmediatamente un nuevo intento sin Conflict
            var prepareReq = new PreparePayment(booking.Id, token, "full", "charge");
            var prepareResult = await controller.Prepare(prepareReq, CancellationToken.None) as OkObjectResult;
            Assert.NotNull(prepareResult);
        }

        [Fact]
        public async Task Charge_3DSChallengeRequired_Returns3DSRequiredStatus()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-3DS-1",
                CustomerName = "Pedro 3DS",
                CustomerEmail = "pedro@test.com",
                TotalAmount = 250m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 250m, SubTotal = 250m, Passengers = 1 });
            context.Bookings.Add(booking);

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 25000,
                Channel = "charge",
                Status = "created",
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            // Simular respuesta 3DS REVIEW de Culqi
            var mockClient = CreateMockCulqiClient(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_3ds\",\"action_code\":\"REVIEW\",\"user_message\":\"Requiere autenticacion 3DS\"}", Encoding.UTF8, "application/json")
            });

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = CreateController(context, mockClient);
            var request = new ChargePayment(payment.Id, token, "tkn_test_3ds_challenge");

            var result = await controller.Charge(request, CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal("3ds_required", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("tkn_test_3ds_challenge", doc.RootElement.GetProperty("tokenId").GetString());
        }

        [Fact]
        public async Task Charge_NetworkTimeout_SetsStatusVerification()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-TIMEOUT",
                CustomerName = "Timeout User",
                CustomerEmail = "timeout@test.com",
                TotalAmount = 100m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 100m, SubTotal = 100m, Passengers = 1 });
            context.Bookings.Add(booking);

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 10000,
                Channel = "charge",
                Status = "created",
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var mockClient = CreateMockCulqiClient(_ => throw new HttpRequestException("Network failure"));
            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = CreateController(context, mockClient);
            var request = new ChargePayment(payment.Id, token, "tkn_test_net_err");

            var result = await controller.Charge(request, CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(202, result.StatusCode);

            var updatedPayment = await context.CulqiPayments.FindAsync(payment.Id);
            Assert.NotNull(updatedPayment);
            Assert.Equal("verification", updatedPayment.Status);
        }

        [Fact]
        public async Task ConfirmPayment_ManualOffline_DoesNotConfirmBooking_DoesNotInflateCash()
        {
            using var context = TestDbContextFactory.Create();
            var shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, OpenedAt = DateTime.UtcNow };
            context.CashRegisterShifts.Add(shift);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-MANUAL-1",
                CustomerName = "Ana Counter",
                TotalAmount = 120m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending
            };
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var configMock = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var bookingController = new BookingController(context, _checkoutAccess, configMock);

            var result = await bookingController.ConfirmPayment(booking.Id, "Efectivo en Counter");
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Voucher", redirect.ActionName);

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            // Debe permanecer en Pending y con PaidAmount en 0
            Assert.Equal(BookingStatus.Pending, updatedBooking.Status);
            Assert.Equal(0m, updatedBooking.PaidAmount);
            Assert.Equal("Efectivo en Counter", updatedBooking.PaymentMethod);

            // No debe haber transacciones de caja automáticas
            Assert.False(await context.CashTransactions.AnyAsync(t => t.BookingId == booking.Id));
        }

        [Fact]
        public async Task VerifyAndApply_PreventsDuplicateCashTransactions()
        {
            using var context = TestDbContextFactory.Create();
            var shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, OpenedAt = DateTime.UtcNow };
            context.CashRegisterShifts.Add(shift);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-DUP-1",
                CustomerName = "Duplicado Test",
                TotalAmount = 80m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 80m, SubTotal = 80m, Passengers = 1 });
            context.Bookings.Add(booking);

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 8000,
                Channel = "charge",
                Status = "created",
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var mockClient = CreateMockCulqiClient(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_dup_1\",\"amount\":8000,\"capture\":true,\"outcome\":{\"type\":\"venta_exitosa\"}}", Encoding.UTF8, "application/json")
            });

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = CreateController(context, mockClient);

            // Primer cobro
            var res1 = await controller.Charge(new ChargePayment(payment.Id, token, "tkn_test_card_1"), CancellationToken.None);
            Assert.IsType<OkObjectResult>(res1);

            // Verificación posterior (por ejemplo por webhook o polling)
            var res2 = await controller.Check(new CheckPayment(payment.Id, token), CancellationToken.None);
            Assert.IsType<OkObjectResult>(res2);

            // Verificar que SOLO existe 1 CashTransaction
            var txCount = await context.CashTransactions.CountAsync(t => t.BookingId == booking.Id);
            Assert.Equal(1, txCount);
        }

        [Fact]
        public async Task Webhook_RequiresBasicAuth_ProcessesEventSuccessfully()
        {
            using var context = TestDbContextFactory.Create();
            var shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, OpenedAt = DateTime.UtcNow };
            context.CashRegisterShifts.Add(shift);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-WEBHOOK-1",
                CustomerName = "Webhook User",
                TotalAmount = 60m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 60m, SubTotal = 60m, Passengers = 1 });
            context.Bookings.Add(booking);

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 6000,
                Channel = "charge",
                Status = "verification",
                ProviderId = "chr_test_wh_1",
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var mockClient = CreateMockCulqiClient(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_wh_1\",\"amount\":6000,\"capture\":true,\"outcome\":{\"type\":\"venta_exitosa\"}}", Encoding.UTF8, "application/json")
            });

            var controller = CreateController(context, mockClient);
            var httpContext = new DefaultHttpContext();
            var validCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("webhook_user_sigetour:webhook_pass_secret_test"));
            httpContext.Request.Headers.Authorization = "Basic " + validCredentials;

            var webhookPayload = "{\"data\":{\"object\":{\"id\":\"chr_test_wh_1\",\"object\":\"charge\"}}}";
            httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(webhookPayload));
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var result = await controller.Webhook(CancellationToken.None);
            Assert.IsType<OkResult>(result);

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(BookingStatus.Confirmed, updatedBooking.Status);
            Assert.Equal(60m, updatedBooking.PaidAmount);
        }

        [Fact]
        public async Task Prepare_WhenFirstPaymentWasAdvance_AllowsSecondPaymentForRemainingBalance()
        {
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Completo Cajamarca",
                RequiredAdvancePercentage = 50,
                BasePrice = 100m,
                Duration = "6h"
            };
            context.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-BAL-1",
                CustomerName = "Elena Test",
                CustomerEmail = "elena@test.com",
                TotalAmount = 200m,
                PaidAmount = 100m, // Ya pagó la seña del 50%
                Status = BookingStatus.Confirmed // Ya confirmada por la seña
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                TourPackage = tour,
                UnitPrice = 100m,
                SubTotal = 200m,
                Passengers = 2
            });
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = CreateController(context);

            // Intentar pedir adelanto de nuevo debe fallar porque ya está cubierto
            var advanceReq = new PreparePayment(booking.Id, token, "advance", "charge");
            var conflictResult = await controller.Prepare(advanceReq, CancellationToken.None) as ConflictObjectResult;
            Assert.NotNull(conflictResult);

            // Preparar el saldo restante (full) debe cobrar exactamente los S/ 100 restantes
            var balanceReq = new PreparePayment(booking.Id, token, "full", "charge");
            var okResult = await controller.Prepare(balanceReq, CancellationToken.None) as OkObjectResult;
            Assert.NotNull(okResult);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(okResult.Value));
            Assert.Equal(10000L, doc.RootElement.GetProperty("amountCents").GetInt64());
        }

        [Fact]
        public async Task Prepare_ExistingVerificationAttempt_WithExpiredOrder_MarksFailed_AllowsNewAttempt()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-EXPIRED-ORDER",
                CustomerName = "Orden Expirada",
                TotalAmount = 100m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 100m, SubTotal = 100m, Passengers = 1 });
            context.Bookings.Add(booking);

            var oldAttempt = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 10000,
                Channel = "wallet_qr",
                Status = "verification",
                ProviderId = "ord_test_expired_1",
                Booking = booking,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            context.CulqiPayments.Add(oldAttempt);
            await context.SaveChangesAsync();

            // Culqi responde que la orden vieja expiró y para la nueva orden genera el nuevo id
            var mockClient = CreateMockCulqiClient(req =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"object\":\"order\",\"id\":\"ord_test_expired_1\",\"state\":\"expired\"}", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"object\":\"order\",\"id\":\"ord_test_new_2\",\"amount\":10000}", Encoding.UTF8, "application/json")
                };
            });

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = CreateController(context, mockClient);

            var req = new PreparePayment(booking.Id, token, "full", "wallet_qr");
            var result = await controller.Prepare(req, CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);

            // El intento anterior debe haberse marcado como failed
            var updatedOld = await context.CulqiPayments.FindAsync(oldAttempt.Id);
            Assert.NotNull(updatedOld);
            Assert.Equal("failed", updatedOld.Status);
        }

        [Fact]
        public async Task Charge_OrderQR_PaymentSuccess_VerifiesOrderAndConfirmsBooking()
        {
            using var context = TestDbContextFactory.Create();
            var shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, OpenedAt = DateTime.UtcNow };
            context.CashRegisterShifts.Add(shift);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-QR-PAID",
                CustomerName = "Yape User",
                TotalAmount = 50m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 50m, SubTotal = 50m, Passengers = 1 });
            context.Bookings.Add(booking);

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 5000,
                Channel = "wallet_qr",
                Status = "verification",
                ProviderId = "ord_test_paid_qr_1",
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            // Simular respuesta de Culqi orden pagada
            var mockClient = CreateMockCulqiClient(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"object\":\"order\",\"id\":\"ord_test_paid_qr_1\",\"amount\":5000,\"state\":\"paid\"}", Encoding.UTF8, "application/json")
            });

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = CreateController(context, mockClient);

            // Al verificar (check) el intento QR
            var checkResult = await controller.Check(new CheckPayment(payment.Id, token), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(checkResult);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(checkResult.Value));
            Assert.Equal("paid", doc.RootElement.GetProperty("status").GetString());

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(BookingStatus.Confirmed, updatedBooking.Status);
            Assert.Equal(50m, updatedBooking.PaidAmount);
            Assert.Equal("Culqi QR", updatedBooking.PaymentMethod);
        }

        [Fact]
        public async Task Webhook_InvalidAuth_ReturnsUnauthorized()
        {
            using var context = TestDbContextFactory.Create();
            var controller = CreateController(context);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers.Authorization = "Basic invalid_base64_or_credentials";
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var result = await controller.Webhook(CancellationToken.None);
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task Charge_TwoPaymentsForSameBooking_AdvanceThenBalance_CreatesTwoCashTransactionsWithDistinctPaymentIds()
        {
            using var context = TestDbContextFactory.Create();
            var shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, OpenedAt = DateTime.UtcNow };
            context.CashRegisterShifts.Add(shift);

            var tour = new TourPackage { Id = Guid.NewGuid(), Title = "Tour Cumbemayo", BasePrice = 100m, RequiredAdvancePercentage = 50, IsActive = true };
            context.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-SPLIT-1",
                CustomerName = "Carlos Split",
                TotalAmount = 100m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 100m, SubTotal = 100m, Passengers = 1, TourPackage = tour, TourPackageId = tour.Id });
            context.Bookings.Add(booking);

            // Abono 1: Adelanto de S/ 50.00
            var payment1 = new CulqiPayment { BookingId = booking.Id, AmountCents = 5000, Channel = "charge", Status = "created", Booking = booking };
            context.CulqiPayments.Add(payment1);
            await context.SaveChangesAsync();

            var mockClient = CreateMockCulqiClient(req =>
            {
                var id = req.RequestUri?.ToString().Contains("chr_test_split_2") == true ? "chr_test_split_2" : "chr_test_split_1";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"object\":\"charge\",\"id\":\"{id}\",\"amount\":5000,\"capture\":true,\"outcome\":{{\"type\":\"venta_exitosa\"}}}}", Encoding.UTF8, "application/json")
                };
            });

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = CreateController(context, mockClient);

            // Pago 1: Adelanto
            var res1 = await controller.Charge(new ChargePayment(payment1.Id, token, "tkn_test_card_p1"), CancellationToken.None);
            Assert.IsType<OkObjectResult>(res1);

            var afterP1 = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(afterP1);
            Assert.Equal(50m, afterP1.PaidAmount);
            Assert.Equal(BookingStatus.Confirmed, afterP1.Status); // Confirmada porque completó el 50% de adelanto requerido

            // Abono 2: Saldo restante de S/ 50.00
            var payment2 = new CulqiPayment { BookingId = booking.Id, AmountCents = 5000, Channel = "charge", Status = "created", Booking = booking };
            context.CulqiPayments.Add(payment2);
            await context.SaveChangesAsync();

            var res2 = await controller.Charge(new ChargePayment(payment2.Id, token, "tkn_test_card_p2"), CancellationToken.None);
            Assert.IsType<OkObjectResult>(res2);

            var afterP2 = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(afterP2);
            Assert.Equal(100m, afterP2.PaidAmount);

            // Verificar que existen EXACTAMENTE 2 CashTransactions, cada una ligada a su CulqiPaymentId
            var txs = await context.CashTransactions.Where(t => t.BookingId == booking.Id).ToListAsync();
            Assert.Equal(2, txs.Count);
            Assert.Contains(txs, t => t.CulqiPaymentId == payment1.Id && t.Amount == 50m);
            Assert.Contains(txs, t => t.CulqiPaymentId == payment2.Id && t.Amount == 50m);
        }

        [Fact]
        public async Task Prepare_AttemptInVerification_CulqiReturns500OrTimeout_DoesNotMarkFailedAndReturnsConflict()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking { Id = Guid.NewGuid(), ReferenceCode = "ST-TIMEOUT", CustomerName = "Timeout Test", TotalAmount = 80m, Status = BookingStatus.Pending };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 80m, SubTotal = 80m, Passengers = 1 });
            context.Bookings.Add(booking);

            var attempt = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 8000,
                Channel = "charge",
                Status = "verification",
                ProviderId = "chr_test_pending_timeout",
                Booking = booking
            };
            context.CulqiPayments.Add(attempt);
            await context.SaveChangesAsync();

            // Simular fallo HTTP 500 en Culqi
            var mockClient = CreateMockCulqiClient(req => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"message\":\"Internal Server Error in Culqi\"}", Encoding.UTF8, "application/json")
            });

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = CreateController(context, mockClient);

            var prepResult = await controller.Prepare(new PreparePayment(booking.Id, token, "full", "charge"), CancellationToken.None);
            var conflict = Assert.IsType<ConflictObjectResult>(prepResult);
            Assert.Equal(409, conflict.StatusCode);

            // Validar que el intento NO se marcó como fallido (resultado ambiguo / timeout protegido)
            var preservedAttempt = await context.CulqiPayments.FindAsync(attempt.Id);
            Assert.NotNull(preservedAttempt);
            Assert.Equal("verification", preservedAttempt.Status);
        }

        [Fact]
        public async Task Prepare_AttemptInVerification_OlderThan25Min_ReconcilesWithCulqiInsteadOfBlindFailure()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking { Id = Guid.NewGuid(), ReferenceCode = "ST-OLD-RECONCILE", CustomerName = "Old Reconcile", TotalAmount = 90m, Status = BookingStatus.Pending };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 90m, SubTotal = 90m, Passengers = 1 });
            context.Bookings.Add(booking);

            // Intento con 35 minutos de antigüedad
            var attempt = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 9000,
                Channel = "charge",
                Status = "verification",
                ProviderId = "chr_test_paid_old",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-35),
                Booking = booking
            };
            context.CulqiPayments.Add(attempt);
            await context.SaveChangesAsync();

            // Culqi responde que la operación fue pagada exitosamente
            var mockClient = CreateMockCulqiClient(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_paid_old\",\"amount\":9000,\"capture\":true,\"outcome\":{\"type\":\"venta_exitosa\"}}", Encoding.UTF8, "application/json")
            });

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = CreateController(context, mockClient);

            var result = await controller.Prepare(new PreparePayment(booking.Id, token, "full", "charge"), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal("paid", doc.RootElement.GetProperty("status").GetString());

            var updatedAttempt = await context.CulqiPayments.FindAsync(attempt.Id);
            Assert.NotNull(updatedAttempt);
            Assert.Equal("paid", updatedAttempt.Status);
        }

        [Fact]
        public async Task Webhook_RepeatedCall_IsIdempotent_DoesNotDuplicateCashTransactions()
        {
            using var context = TestDbContextFactory.Create();
            var shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, OpenedAt = DateTime.UtcNow };
            context.CashRegisterShifts.Add(shift);

            var booking = new Booking { Id = Guid.NewGuid(), ReferenceCode = "ST-WH-REPEAT", CustomerName = "Webhook Repeat", TotalAmount = 70m, Status = BookingStatus.Pending };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 70m, SubTotal = 70m, Passengers = 1 });
            context.Bookings.Add(booking);

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 7000,
                Channel = "charge",
                Status = "verification",
                ProviderId = "chr_test_wh_repeat",
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var mockClient = CreateMockCulqiClient(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_wh_repeat\",\"amount\":7000,\"capture\":true,\"outcome\":{\"type\":\"venta_exitosa\"}}", Encoding.UTF8, "application/json")
            });

            var controller = CreateController(context, mockClient);
            var payload = "{\"data\":{\"id\":\"chr_test_wh_repeat\"}}";
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes("webhook_user_sigetour:webhook_pass_secret_test"));

            // Primer Webhook
            var httpContext1 = new DefaultHttpContext();
            httpContext1.Request.Headers.Authorization = $"Basic {creds}";
            httpContext1.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext1 };

            var res1 = await controller.Webhook(CancellationToken.None);
            Assert.IsType<OkResult>(res1);

            // Segundo Webhook (Repetido)
            var httpContext2 = new DefaultHttpContext();
            httpContext2.Request.Headers.Authorization = $"Basic {creds}";
            httpContext2.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext2 };

            var res2 = await controller.Webhook(CancellationToken.None);
            Assert.IsType<OkResult>(res2);

            // Verificar que no se duplicaron transacciones de caja ni el monto abonado
            var txCount = await context.CashTransactions.CountAsync(t => t.BookingId == booking.Id);
            Assert.Equal(1, txCount);

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(70m, updatedBooking.PaidAmount);
        }

        [Fact]
        public async Task Charge_3DSFlow_ReviewTriggers3DSRequired_FollowupWith3DSCompletesPayment()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking { Id = Guid.NewGuid(), ReferenceCode = "ST-3DS-TEST", CustomerName = "User 3DS", TotalAmount = 120m, Status = BookingStatus.Pending };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 120m, SubTotal = 120m, Passengers = 1 });
            context.Bookings.Add(booking);

            var payment = new CulqiPayment { BookingId = booking.Id, AmountCents = 12000, Channel = "charge", Status = "created", Booking = booking };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var step = 1;
            var mockClient = CreateMockCulqiClient(req =>
            {
                if (step == 1)
                {
                    // Primer intento: el banco emisor requiere autenticación 3DS (REVIEW)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"action_code\":\"REVIEW\",\"user_message\":\"Validación 3DS requerida\"}", Encoding.UTF8, "application/json")
                    };
                }
                else
                {
                    // Segundo intento tras reto 3DS: venta exitosa
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_3ds_success\",\"amount\":12000,\"capture\":true,\"outcome\":{\"type\":\"venta_exitosa\"}}", Encoding.UTF8, "application/json")
                    };
                }
            });

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var controller = CreateController(context, mockClient);

            // 1. Primer llamado sin parámetros 3DS
            var res1 = await controller.Charge(new ChargePayment(payment.Id, token, "tkn_test_card_3ds"), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(res1);

            using var doc1 = JsonDocument.Parse(JsonSerializer.Serialize(res1.Value));
            Assert.Equal("3ds_required", doc1.RootElement.GetProperty("status").GetString());

            // El intento vuelve a "created" para permitir el reintento autenticado
            var attemptAfterStep1 = await context.CulqiPayments.FindAsync(payment.Id);
            Assert.NotNull(attemptAfterStep1);
            Assert.Equal("created", attemptAfterStep1.Status);

            // 2. Segundo llamado con Parameters3DS emitidos por el iframe bancario
            step = 2;
            using var params3dsDoc = JsonDocument.Parse("{\"xid\":\"12345\",\"eci\":\"05\",\"cavv\":\"test_cavv\"}");
            var res2 = await controller.Charge(new ChargePayment(payment.Id, token, "tkn_test_card_3ds", params3dsDoc.RootElement), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(res2);

            using var doc2 = JsonDocument.Parse(JsonSerializer.Serialize(res2.Value));
            Assert.Equal("paid", doc2.RootElement.GetProperty("status").GetString());

            var attemptAfterStep2 = await context.CulqiPayments.FindAsync(payment.Id);
            Assert.NotNull(attemptAfterStep2);
            Assert.Equal("paid", attemptAfterStep2.Status);
            Assert.Equal("chr_test_3ds_success", attemptAfterStep2.ProviderId);
        }

        [Fact]
        public async Task Nubefact_DemoMode_EmitsReceipt_ParsesOfficialResponse()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-NUBE-DEMO",
                CustomerName = "Empresa Turística",
                CustomerDni = "20601234567",
                InvoiceType = "factura",
                TotalAmount = 150m,
                PaidAmount = 150m,
                Status = BookingStatus.Confirmed
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 150m, SubTotal = 150m, Passengers = 1 });
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var demoSettings = new Dictionary<string, string?>
            {
                { "Nubefact:Mode", "Demo" },
                { "Nubefact:Ruta", "https://demo.nubefact.com/api/v1/test" },
                { "Nubefact:Token", "demo_token_12345" },
                { "Nubefact:FacturaSeries", "FFF1" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(demoSettings).Build();

            var fakeNubefactResponse = "{\"tipo_de_comprobante\":1,\"serie\":\"FFF1\",\"numero\":104,\"enlace_del_pdf\":\"https://demo.nubefact.com/pdf/FFF1-104.pdf\",\"enlace_del_xml\":\"https://demo.nubefact.com/xml/FFF1-104.xml\",\"enlace_del_cdr\":\"https://demo.nubefact.com/cdr/R-FFF1-104.zip\",\"codigo_hash\":\"abc123hash\",\"sunat_description\":\"La Factura FFF1-104 ha sido aceptada\"}";
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                Assert.Equal("demo_token_12345", req.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(fakeNubefactResponse, Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(context, config, mockHttp, NullLogger<NubefactInvoiceService>.Instance);
            var receipt = await service.GenerateReceiptAsync(booking.Id, null, CancellationToken.None);

            Assert.NotNull(receipt);
            Assert.Equal("Accepted", receipt.Status);
            Assert.Equal("FFF1", receipt.Series);
            Assert.Equal("104", receipt.Number);
            Assert.Equal("https://demo.nubefact.com/pdf/FFF1-104.pdf", receipt.PdfUrl);
            Assert.Equal("https://demo.nubefact.com/xml/FFF1-104.xml", receipt.XmlUrl);
            Assert.Equal("abc123hash", receipt.DigitalHash);
        }

        [Fact]
        public async Task Nubefact_ReceiptRejection_MarksStatusRejectedAndStoresError()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-NUBE-REJECT",
                CustomerName = "Cliente Inválido",
                CustomerDni = "00000000",
                InvoiceType = "factura",
                TotalAmount = 50m,
                PaidAmount = 50m,
                Status = BookingStatus.Confirmed
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, UnitPrice = 50m, SubTotal = 50m, Passengers = 1 });
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var demoSettings = new Dictionary<string, string?>
            {
                { "Nubefact:Mode", "Demo" },
                { "Nubefact:Ruta", "https://demo.nubefact.com/api/v1/test" },
                { "Nubefact:Token", "demo_token_12345" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(demoSettings).Build();

            var rejectionJson = "{\"errors\":\"El RUC no existe en el padrón de SUNAT\"}";
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(rejectionJson, Encoding.UTF8, "application/json")
            }));

            var service = new NubefactInvoiceService(context, config, mockHttp, NullLogger<NubefactInvoiceService>.Instance);
            var receipt = await service.GenerateReceiptAsync(booking.Id, null, CancellationToken.None);

            Assert.NotNull(receipt);
            Assert.Equal("Rejected", receipt.Status);
            Assert.Contains("El RUC no existe", receipt.LastError);
        }

        [Fact]
        public async Task Nubefact_ReprintReceipt_IsIdempotent_DoesNotCallExternalApi()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-REPRINT-TEST",
                CustomerName = "Carlos Reimpresión",
                CustomerDni = "44556677",
                TotalAmount = 80m,
                PaidAmount = 80m,
                Status = BookingStatus.Confirmed
            };
            context.Bookings.Add(booking);

            var existingReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "B001",
                Number = "00000042",
                CustomerName = booking.CustomerName,
                CustomerDocNumber = booking.CustomerDni,
                CustomerDocType = "1",
                TotalAmount = 80m,
                SubtotalAmount = 67.80m,
                IgvAmount = 12.20m,
                Status = "Accepted",
                PdfUrl = "https://demo.nubefact.com/pdf/B001-42.pdf"
            };
            context.ElectronicReceipts.Add(existingReceipt);
            await context.SaveChangesAsync();

            var demoSettings = new Dictionary<string, string?>
            {
                { "Nubefact:Mode", "Demo" },
                { "Nubefact:Ruta", "https://demo.nubefact.com/api/v1/test" },
                { "Nubefact:Token", "demo_token_12345" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(demoSettings).Build();

            int externalApiCalls = 0;
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                Interlocked.Increment(ref externalApiCalls);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(context, config, mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            // Act 1: GetReceiptByIdAsync
            var retrievedReceipt = await service.GetReceiptByIdAsync(existingReceipt.Id, CancellationToken.None);

            // Act 2: Attempt GenerateReceiptAsync on already emitted booking
            var secondGenerateReceipt = await service.GenerateReceiptAsync(booking.Id, null, CancellationToken.None);

            // Assert: no HTTP calls should be made to NubeFact when reprinting/fetching existing receipt
            Assert.Equal(0, externalApiCalls);
            Assert.NotNull(retrievedReceipt);
            Assert.Equal("B001-00000042", retrievedReceipt.FullNumber);
            Assert.NotNull(secondGenerateReceipt);
            Assert.Equal(existingReceipt.Id, secondGenerateReceipt.Id);
        }

        [Fact]
        public async Task Nubefact_QueryReceiptFromProviderAsync_UpdatesDetailsFromNubeFact()
        {
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-QUERY-TEST",
                CustomerName = "Consulta NubeFact",
                CustomerDni = "88990011",
                TotalAmount = 100m,
                PaidAmount = 100m,
                Status = BookingStatus.Confirmed
            };
            context.Bookings.Add(booking);

            var existingReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "B001",
                Number = "55",
                Status = "Submitted"
            };
            context.ElectronicReceipts.Add(existingReceipt);
            await context.SaveChangesAsync();

            var demoSettings = new Dictionary<string, string?>
            {
                { "Nubefact:Mode", "Demo" },
                { "Nubefact:Ruta", "https://demo.nubefact.com/api/v1/test" },
                { "Nubefact:Token", "demo_token_12345" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(demoSettings).Build();

            var queryResponseJson = "{\"tipo_de_comprobante\":2,\"serie\":\"B001\",\"numero\":55,\"enlace_del_pdf\":\"https://demo.nubefact.com/pdf/B001-55.pdf\",\"enlace_del_xml\":\"https://demo.nubefact.com/xml/B001-55.xml\",\"codigo_hash\":\"queryhash123\",\"sunat_description\":\"Aceptado por SUNAT\"}";
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(queryResponseJson, Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(context, config, mockHttp, NullLogger<NubefactInvoiceService>.Instance);
            var updated = await service.QueryReceiptFromProviderAsync(existingReceipt.Id, CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal("Accepted", updated.Status);
            Assert.Equal("https://demo.nubefact.com/pdf/B001-55.pdf", updated.PdfUrl);
            Assert.Equal("queryhash123", updated.DigitalHash);
        }
    }
}
