using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Controllers;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Payments
{
    public class Block2CulqiTests
    {
        private readonly IConfiguration _config;
        private readonly CheckoutAccess _checkoutAccess;

        public Block2CulqiTests()
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "Payments:CheckoutSigningKey", "test-signing-key-for-checkout-access-sigetour-sandbox-32chars" },
                { "Culqi:PublicKey", "pk_test_sample_public_key_123" },
                { "Culqi:SecretKey", "sk_test_sample_secret_key_123" },
                { "Culqi:WebhookUser", "webhook_user_sigetour" },
                { "Culqi:WebhookPassword", "webhook_pass_secret_test" }
            };

            _config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
            _checkoutAccess = new CheckoutAccess(_config);
        }

        private CulqiApiClient CreateMockCulqiClient(Func<HttpRequestMessage, HttpResponseMessage>? handler = null)
        {
            var defaultHandler = handler ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_123\",\"amount\":10000,\"currency_code\":\"PEN\",\"capture\":true,\"outcome\":{\"type\":\"venta_exitosa\"}}", Encoding.UTF8, "application/json")
            });

            var httpClient = new HttpClient(new MockCulqiHttpHandler(defaultHandler));
            return new CulqiApiClient(httpClient, _config);
        }

        private CulqiPaymentsController CreateController(AppDbContext context, CulqiApiClient? client = null)
        {
            var culqiClient = client ?? CreateMockCulqiClient();
            var dummyHttp = new HttpClient(new MockCulqiHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var rService = new NubefactInvoiceService(context, _config, dummyHttp, NullLogger<NubefactInvoiceService>.Instance);
            return new CulqiPaymentsController(context, culqiClient, _checkoutAccess, _config, rService);
        }

        private static Booking CreateSampleBooking(AppDbContext context, decimal totalAmount = 100m, BookingStatus status = BookingStatus.Pending)
        {
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-B2-" + Guid.NewGuid().ToString("N")[..6].ToUpper(),
                CustomerName = "Carlos Pasajero",
                CustomerEmail = "pasajero@sigetour.com",
                CustomerDni = "71829304",
                CustomerPhone = "987654321",
                TotalPassengers = 2,
                TotalAmount = totalAmount,
                PaidAmount = 0m,
                Status = status,
                TravelDate = DateTime.UtcNow.Date.AddDays(3)
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                UnitPrice = totalAmount,
                SubTotal = totalAmount,
                Passengers = 2
            });
            context.Bookings.Add(booking);
            context.SaveChanges();
            return booking;
        }

        // =========================================================================
        // 1. Doble clic y peticiones simultáneas
        // =========================================================================
        [Fact]
        public async Task DoubleClick_Charge_WhenAlreadyProcessingOrPaid_ReturnsConflictOrIdempotentOk()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 100m);
            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 10000,
                Channel = "charge",
                Status = "processing", // Ya tomado por la primera petición
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var controller = CreateController(context);
            var result = await controller.Charge(new ChargePayment(payment.Id, token, "tkn_test_card_123"), CancellationToken.None);

            var conflictResult = Assert.IsAssignableFrom<ConflictObjectResult>(result);
            Assert.NotNull(conflictResult);
        }

        // =========================================================================
        // 2. 3DS: Pendiente (3ds_required), Completado y Fallido
        // =========================================================================
        [Fact]
        public async Task ThreeDS_Pending_Returns3dsRequiredStatus_DoesNotConfirmBooking()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 120m);
            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 12000,
                Channel = "charge",
                Status = "created",
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            // Culqi responde que el banco requiere autenticación 3DS (REVIEW)
            var mockClient = CreateMockCulqiClient(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"action_code\":\"REVIEW\",\"user_message\":\"Se requiere validación 3DS\"}", Encoding.UTF8, "application/json")
            });

            var controller = CreateController(context, mockClient);
            var result = await controller.Charge(new ChargePayment(payment.Id, token, "tkn_test_3ds_challenge"), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal("3ds_required", doc.RootElement.GetProperty("status").GetString());

            var updatedPayment = await context.CulqiPayments.FindAsync(payment.Id);
            Assert.NotNull(updatedPayment);
            Assert.Equal("3ds_required", updatedPayment.Status);

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(BookingStatus.Pending, updatedBooking.Status); // No confirmada aún
            Assert.Equal(0m, updatedBooking.PaidAmount);
        }

        [Fact]
        public async Task ThreeDS_Completion_WithParameters3DS_CompletesPaymentAndConfirmsBooking()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 120m);
            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 12000,
                Channel = "charge",
                Status = "3ds_required",
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var chargeId = "chr_test_3ds_done_" + Guid.NewGuid().ToString("N")[..6];
            var mockClient = CreateMockCulqiClient(req =>
            {
                var json = $"{{\"object\":\"charge\",\"id\":\"{chargeId}\",\"amount\":12000,\"currency_code\":\"PEN\",\"capture\":true,\"outcome\":{{\"type\":\"venta_exitosa\"}}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            });

            var controller = CreateController(context, mockClient);
            using var params3dsDoc = JsonDocument.Parse("{\"xid\":\"12345\",\"eci\":\"05\",\"cavv\":\"test_cavv\"}");
            var result = await controller.Charge(new ChargePayment(payment.Id, token, "tkn_test_card_123", params3dsDoc.RootElement), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal("paid", doc.RootElement.GetProperty("status").GetString());

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(BookingStatus.Confirmed, updatedBooking.Status);
            Assert.Equal(120m, updatedBooking.PaidAmount);
        }

        [Fact]
        public async Task ThreeDS_Failure_WhenBankRejectsCard_TransitionsToFailed_AllowsRetry()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 120m);
            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 12000,
                Channel = "charge",
                Status = "3ds_required",
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            // Banco rechaza el reto 3DS (HTTP 402 Card Declined)
            var mockClient = CreateMockCulqiClient(req => new HttpResponseMessage(HttpStatusCode.PaymentRequired)
            {
                Content = new StringContent("{\"object\":\"error\",\"type\":\"card_error\",\"merchant_message\":\"Autenticación 3DS fallida por banco emisor\",\"user_message\":\"Tarjeta rechazada durante la autenticación\"}", Encoding.UTF8, "application/json")
            });

            var controller = CreateController(context, mockClient);
            using var params3dsDoc = JsonDocument.Parse("{\"xid\":\"bad_xid\",\"eci\":\"07\"}");
            var result = await controller.Charge(new ChargePayment(payment.Id, token, "tkn_test_card_123", params3dsDoc.RootElement), CancellationToken.None) as BadRequestObjectResult;
            Assert.NotNull(result);

            var updatedPayment = await context.CulqiPayments.FindAsync(payment.Id);
            Assert.NotNull(updatedPayment);
            Assert.Equal("failed", updatedPayment.Status); // Marca failed para permitir reintentar con otra tarjeta
        }

        [Fact]
        public async Task Prepare_WhenAttemptIn3DSRequired_ReusesActiveAttempt_PreventsSecondConcurrentAttempt()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 100m);
            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));

            var active3ds = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 10000,
                Channel = "charge",
                Status = "3ds_required",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-3),
                Booking = booking
            };
            context.CulqiPayments.Add(active3ds);
            await context.SaveChangesAsync();

            var controller = CreateController(context);
            var result = await controller.Prepare(new PreparePayment(booking.Id, token, "full", "charge"), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.True(doc.RootElement.GetProperty("reused").GetBoolean());
            Assert.Equal(active3ds.Id.ToString(), doc.RootElement.GetProperty("attemptId").GetString());
            Assert.Equal("3ds_required", doc.RootElement.GetProperty("status").GetString());

            var count = await context.CulqiPayments.CountAsync(p => p.BookingId == booking.Id);
            Assert.Equal(1, count); // Exactamente 1 intento
        }

        // =========================================================================
        // 3. Timeout después de enviar el cargo
        // =========================================================================
        [Fact]
        public async Task Charge_NetworkTimeoutAfterSending_TransitionsToVerification_DoesNotConfirmBlindly()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 80m);
            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));

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

            // Simula fallo de timeout / HTTP 504 Gateway Timeout
            var mockClient = CreateMockCulqiClient(req => new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                Content = new StringContent("{\"message\":\"Gateway timeout from provider\"}", Encoding.UTF8, "application/json")
            });

            var controller = CreateController(context, mockClient);
            var result = await controller.Charge(new ChargePayment(payment.Id, token, "tkn_test_card_timeout"), CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(202, result.StatusCode);

            var updatedPayment = await context.CulqiPayments.FindAsync(payment.Id);
            Assert.NotNull(updatedPayment);
            Assert.Equal("verification", updatedPayment.Status); // Pasa a verificación, no se marca failed ni paid a ciegas

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(BookingStatus.Pending, updatedBooking.Status);
            Assert.Equal(0m, updatedBooking.PaidAmount);
        }

        // =========================================================================
        // 4. Webhook repetido o adelantado
        // =========================================================================
        [Fact]
        public async Task Webhook_EarlyArrivalBeforeCheck_VerifiesPaymentAndConfirmsBooking()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 100m);
            var chargeId = "chr_test_early_" + Guid.NewGuid().ToString("N")[..6];

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 10000,
                Channel = "charge",
                Status = "verification",
                ProviderId = chargeId,
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var mockClient = CreateMockCulqiClient(req =>
            {
                var json = $"{{\"object\":\"charge\",\"id\":\"{chargeId}\",\"amount\":10000,\"currency_code\":\"PEN\",\"capture\":true,\"outcome\":{{\"type\":\"venta_exitosa\"}}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            });

            var controller = CreateController(context, mockClient);
            var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes("webhook_user_sigetour:webhook_pass_secret_test"));
            httpContext.Request.Headers.Authorization = $"Basic {authHeader}";
            var webhookPayload = $"{{\"type\":\"charge.creation.succeeded\",\"data\":{{\"id\":\"{chargeId}\",\"amount\":10000,\"currency_code\":\"PEN\"}}}}";
            httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(webhookPayload));
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var result = await controller.Webhook(CancellationToken.None);
            Assert.IsType<OkResult>(result);

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(BookingStatus.Confirmed, updatedBooking.Status);
            Assert.Equal(100m, updatedBooking.PaidAmount);

            var txs = await context.CashTransactions.Where(t => t.CulqiPaymentId == payment.Id).ToListAsync();
            Assert.Single(txs);
        }

        [Fact]
        public async Task Webhook_RepeatedCalls_AreStrictlyIdempotent_DoesNotDuplicateCash()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 100m);
            var chargeId = "chr_test_rep_" + Guid.NewGuid().ToString("N")[..6];

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 10000,
                Channel = "charge",
                Status = "verification",
                ProviderId = chargeId,
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var mockClient = CreateMockCulqiClient(req =>
            {
                var json = $"{{\"object\":\"charge\",\"id\":\"{chargeId}\",\"amount\":10000,\"currency_code\":\"PEN\",\"capture\":true,\"outcome\":{{\"type\":\"venta_exitosa\"}}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            });

            var controller = CreateController(context, mockClient);
            var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes("webhook_user_sigetour:webhook_pass_secret_test"));
            var webhookPayload = $"{{\"type\":\"charge.creation.succeeded\",\"data\":{{\"id\":\"{chargeId}\",\"amount\":10000,\"currency_code\":\"PEN\"}}}}";

            // Primera llamada del webhook
            var httpContext1 = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            httpContext1.Request.Headers.Authorization = $"Basic {authHeader}";
            httpContext1.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(webhookPayload));
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext1 };
            var res1 = await controller.Webhook(CancellationToken.None);
            Assert.IsType<OkResult>(res1);

            // Segunda llamada del webhook (repetido)
            var httpContext2 = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            httpContext2.Request.Headers.Authorization = $"Basic {authHeader}";
            httpContext2.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(webhookPayload));
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext2 };
            var res2 = await controller.Webhook(CancellationToken.None);
            Assert.IsType<OkResult>(res2);

            // Debe existir exactamente 1 movimiento de caja
            var txs = await context.CashTransactions.Where(t => t.CulqiPaymentId == payment.Id).ToListAsync();
            Assert.Single(txs);
        }

        // =========================================================================
        // 5. Importe o moneda incorrectos
        // =========================================================================
        [Fact]
        public async Task Charge_InvalidCurrencyUSD_RejectedWithBadRequest_MarkedFailed()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 60m);
            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 6000,
                Channel = "charge",
                Status = "created",
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            // Culqi devuelve moneda USD
            var mockClient = CreateMockCulqiClient(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_usd_123\",\"amount\":6000,\"currency_code\":\"USD\",\"capture\":true,\"outcome\":{\"type\":\"venta_exitosa\"}}", Encoding.UTF8, "application/json")
            });

            var controller = CreateController(context, mockClient);
            var result = await controller.Charge(new ChargePayment(payment.Id, token, "tkn_test_card_usd"), CancellationToken.None) as BadRequestObjectResult;
            Assert.NotNull(result);

            var updatedPayment = await context.CulqiPayments.FindAsync(payment.Id);
            Assert.NotNull(updatedPayment);
            Assert.Equal("failed", updatedPayment.Status);
        }

        [Fact]
        public async Task Charge_MismatchedAmount_DoesNotVerifyAsPaid()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 100m);
            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));

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

            // El importe devuelto por Culqi es 5000 (S/ 50.00), no los 10000 esperados
            var mockClient = CreateMockCulqiClient(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_wrong_amt\",\"amount\":5000,\"currency_code\":\"PEN\",\"capture\":true,\"outcome\":{\"type\":\"venta_exitosa\"}}", Encoding.UTF8, "application/json")
            });

            var controller = CreateController(context, mockClient);
            var result = await controller.Charge(new ChargePayment(payment.Id, token, "tkn_test_card_amt"), CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(202, result.StatusCode);

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(BookingStatus.Pending, updatedBooking.Status);
            Assert.Equal(0m, updatedBooking.PaidAmount);
        }

        // =========================================================================
        // 6. Pago sobre reserva cancelada y exceso de pago (paid_excess)
        // =========================================================================
        [Fact]
        public async Task Check_CancelledBookingPayment_MarksPaidExcess_SingleIncidentAndCashTx()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 80m, BookingStatus.Cancelled);
            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));
            var chargeId = "chr_test_canc_" + Guid.NewGuid().ToString("N")[..6];

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 8000,
                Channel = "charge",
                Status = "verification",
                ProviderId = chargeId,
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var mockClient = CreateMockCulqiClient(req =>
            {
                var json = $"{{\"object\":\"charge\",\"id\":\"{chargeId}\",\"amount\":8000,\"currency_code\":\"PEN\",\"capture\":true,\"outcome\":{{\"type\":\"venta_exitosa\"}}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            });

            var controller = CreateController(context, mockClient);
            var result = await controller.Check(new CheckPayment(payment.Id, token, booking.Id), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);

            var incident = await context.PaymentIncidents.FirstOrDefaultAsync(i => i.CulqiPaymentId == payment.Id);
            Assert.NotNull(incident);
            Assert.Equal("CancelledBookingPayment", incident.IncidentType);
            Assert.Equal(80m, incident.Amount);

            var cashTx = await context.CashTransactions.FirstOrDefaultAsync(t => t.CulqiPaymentId == payment.Id);
            Assert.NotNull(cashTx);
            Assert.Equal(80m, cashTx.Amount);

            // Re-check no duplica incidente ni movimiento
            await controller.Check(new CheckPayment(payment.Id, token, booking.Id), CancellationToken.None);
            var countIncidents = await context.PaymentIncidents.CountAsync(i => i.CulqiPaymentId == payment.Id);
            var countCashTxs = await context.CashTransactions.CountAsync(t => t.CulqiPaymentId == payment.Id);
            Assert.Equal(1, countIncidents);
            Assert.Equal(1, countCashTxs);
        }

        [Fact]
        public async Task Check_Overpayment_MarksPaidExcess_IsTerminal_NoDuplicateIncidents()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 50m);
            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));
            var chargeId = "chr_test_over_" + Guid.NewGuid().ToString("N")[..6];

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 7000, // 70 > 50
                Channel = "charge",
                Status = "verification",
                ProviderId = chargeId,
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var mockClient = CreateMockCulqiClient(req =>
            {
                var json = $"{{\"object\":\"charge\",\"id\":\"{chargeId}\",\"amount\":7000,\"currency_code\":\"PEN\",\"capture\":true,\"outcome\":{{\"type\":\"venta_exitosa\"}}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            });

            var controller = CreateController(context, mockClient);
            var res1 = await controller.Check(new CheckPayment(payment.Id, token, booking.Id), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(res1);

            var updatedPayment = await context.CulqiPayments.FindAsync(payment.Id);
            Assert.NotNull(updatedPayment);
            Assert.Equal("paid_excess", updatedPayment.Status);

            var incidents = await context.PaymentIncidents.Where(i => i.CulqiPaymentId == payment.Id).ToListAsync();
            Assert.Single(incidents);
            Assert.Equal("Overpayment", incidents[0].IncidentType);

            // 2do Check repetido: no duplica
            var res2 = await controller.Check(new CheckPayment(payment.Id, token, booking.Id), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(res2);

            var incidentsAfter = await context.PaymentIncidents.Where(i => i.CulqiPaymentId == payment.Id).ToListAsync();
            Assert.Single(incidentsAfter);
        }

        // =========================================================================
        // 7. Yape de prueba según el flujo documentado
        // =========================================================================
        [Fact]
        public async Task Charge_YapeTokenFromModal_TransitionsWalletQrToCharge_ConfirmsPayment()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 45m);
            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));
            var chargeId = "chr_test_yape_tok_" + Guid.NewGuid().ToString("N")[..6];

            // Intento preparado inicialmente como wallet_qr
            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 4500,
                Channel = "wallet_qr",
                Status = "created",
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var mockClient = CreateMockCulqiClient(req =>
            {
                var json = $"{{\"object\":\"charge\",\"id\":\"{chargeId}\",\"amount\":4500,\"currency_code\":\"PEN\",\"capture\":true,\"outcome\":{{\"type\":\"venta_exitosa\"}}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            });

            var controller = CreateController(context, mockClient);
            // El cliente completó en el modal usando token Yape (ype_test_...)
            var result = await controller.Charge(new ChargePayment(payment.Id, token, "ype_test_valid_sandbox_token"), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal("paid", doc.RootElement.GetProperty("status").GetString());

            var updatedPayment = await context.CulqiPayments.FindAsync(payment.Id);
            Assert.NotNull(updatedPayment);
            Assert.Equal("charge", updatedPayment.Channel); // Transición de canal exitosa
            Assert.Equal("paid", updatedPayment.Status);

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(BookingStatus.Confirmed, updatedBooking.Status);
            Assert.Equal(45m, updatedBooking.PaidAmount);
        }

        [Fact]
        public async Task Check_WalletQr_CulqiOrderPaid_ConfirmsBookingAndCreditsCash()
        {
            using var context = TestDbContextFactory.Create();
            var booking = CreateSampleBooking(context, 50m);
            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));
            var orderId = "ord_test_yape_qr_" + Guid.NewGuid().ToString("N")[..6];

            var payment = new CulqiPayment
            {
                BookingId = booking.Id,
                AmountCents = 5000,
                Channel = "wallet_qr",
                Status = "created",
                ProviderId = orderId,
                Booking = booking
            };
            context.CulqiPayments.Add(payment);
            await context.SaveChangesAsync();

            var mockClient = CreateMockCulqiClient(req =>
            {
                var path = req.RequestUri!.PathAndQuery;
                if (path.Contains($"orders/{orderId}"))
                {
                    var json = $"{{\"object\":\"order\",\"id\":\"{orderId}\",\"amount\":5000,\"currency_code\":\"PEN\",\"state\":\"paid\"}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                }
                if (path.Contains("events"))
                {
                    var json = $"{{\"data\":[{{\"type\":\"order.status.changed\",\"data\":{{\"id\":\"{orderId}\",\"state\":\"paid\",\"amount\":5000,\"currency_code\":\"PEN\"}}}}]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var controller = CreateController(context, mockClient);
            var result = await controller.Check(new CheckPayment(payment.Id, token, booking.Id), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal("paid", doc.RootElement.GetProperty("status").GetString());

            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(BookingStatus.Confirmed, updatedBooking.Status);
            Assert.Equal(50m, updatedBooking.PaidAmount);

            var cashTx = await context.CashTransactions.FirstOrDefaultAsync(t => t.CulqiPaymentId == payment.Id);
            Assert.NotNull(cashTx);
            Assert.Equal(50m, cashTx.Amount);
        }
    }
}
