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
    }
}
