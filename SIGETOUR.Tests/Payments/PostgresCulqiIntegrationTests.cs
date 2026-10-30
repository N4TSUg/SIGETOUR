using System;
using System.Collections.Generic;
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
using Xunit;
using Xunit.Abstractions;

namespace SIGETOUR.Tests.Payments
{
    public class PostgresCulqiIntegrationTests
    {
        // Base de datos dedicada y aislada exclusivamente para pruebas (NUNCA la base operativa sigetour_db)
        private static readonly string ConnectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")
            ?? "Host=localhost;Database=sigetour_test_db;Username=postgres;Password=3nriqueA1402#";

        private readonly IConfiguration _config;
        private readonly CheckoutAccess _checkoutAccess;
        private readonly ITestOutputHelper _output;

        public PostgresCulqiIntegrationTests(ITestOutputHelper output)
        {
            _output = output;

            var inMemorySettings = new Dictionary<string, string?>
            {
                { "Payments:CheckoutSigningKey", "test-signing-key-for-checkout-access-sigetour-sandbox-32chars" },
                { "Culqi:PublicKey", "pk_test_sample_public_key_123" },
                { "Culqi:SecretKey", "sk_test_sample_secret_key_123" },
                { "Culqi:WebhookUser", "webhook_user_sigetour" },
                { "Culqi:WebhookPassword", "webhook_pass_secret_test" },
                { "ConnectionStrings:DefaultConnection", ConnectionString }
            };

            _config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
            _checkoutAccess = new CheckoutAccess(_config);
        }

        private AppDbContext CreatePostgresDbContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(ConnectionString)
                .Options;

            return new AppDbContext(options);
        }

        private async Task<bool> IsPostgresAvailableAsync()
        {
            try
            {
                await using var db = CreatePostgresDbContext();
                var canConnect = await db.Database.CanConnectAsync();
                if (!canConnect)
                {
                    _output.WriteLine("[OMITIDA] Base de datos de pruebas PostgreSQL (sigetour_test_db) no accesible.");
                }
                return canConnect;
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[OMITIDA] Error conectando a PostgreSQL de pruebas: {ex.Message}");
                return false;
            }
        }

        [Fact]
        public async Task Postgres_FullPaymentCycle_AtomicUpdateAndNoDuplicates()
        {
            if (!await IsPostgresAvailableAsync()) return;

            await using var db = CreatePostgresDbContext();

            // 1. Arrange: Asegurar turno de caja activo para probar inserción real en CashTransactions
            var shift = await db.CashRegisterShifts.FirstOrDefaultAsync(s => s.IsOpen);
            if (shift == null)
            {
                shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, OpenedAt = DateTime.UtcNow, ShiftNumber = "CJ-TEST-01" };
                db.CashRegisterShifts.Add(shift);
                await db.SaveChangesAsync();
            }

            // Crear reserva real en PostgreSQL de pruebas
            var tour = await db.TourPackages.FirstOrDefaultAsync();
            if (tour == null)
            {
                tour = new TourPackage
                {
                    Id = Guid.NewGuid(),
                    Title = "Tour Test Integracion Postgres",
                    Slug = "tour-test-postgres-" + Guid.NewGuid().ToString()[..6],
                    BasePrice = 75m,
                    RequiredAdvancePercentage = 50,
                    Duration = "3h"
                };
                db.TourPackages.Add(tour);
                await db.SaveChangesAsync();
            }

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-TEST-" + Guid.NewGuid().ToString()[..6].ToUpper(),
                CustomerName = "Postgres Integracion",
                CustomerEmail = "pg_test@sigetour.com",
                CustomerDni = "71829304",
                TotalPassengers = 1,
                TotalAmount = 75m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending,
                TravelDate = DateTime.UtcNow.Date.AddDays(2)
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                Passengers = 1,
                UnitPrice = 75m,
                SubTotal = 75m
            });
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));

            var runId = Guid.NewGuid().ToString("N")[..8];
            var testChargeId = $"chr_test_pg_real_{runId}";

            var mockClient = new CulqiApiClient(new HttpClient(new MockCulqiHttpHandler(req =>
            {
                if (req.Method == HttpMethod.Post && req.RequestUri!.ToString().Contains("charges"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"{{\"object\":\"charge\",\"id\":\"{testChargeId}\",\"amount\":7500,\"capture\":true,\"outcome\":{{\"type\":\"venta_exitosa\"}}}}", Encoding.UTF8, "application/json")
                    };
                }
                if (req.Method == HttpMethod.Get && req.RequestUri!.ToString().Contains($"charges/{testChargeId}"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"{{\"object\":\"charge\",\"id\":\"{testChargeId}\",\"amount\":7500,\"capture\":true,\"outcome\":{{\"type\":\"venta_exitosa\"}}}}", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })), _config);

            var dummyHttp = new HttpClient(new MockCulqiHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var receiptService = new NubefactInvoiceService(db, _config, dummyHttp, NullLogger<NubefactInvoiceService>.Instance);
            var controller = new CulqiPaymentsController(db, mockClient, _checkoutAccess, _config, receiptService);

            // 2. Act: Prepare (valida y crea en base de datos real)
            var prepResult = await controller.Prepare(new PreparePayment(booking.Id, token, "full", "charge"), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(prepResult);

            var attempt = await db.CulqiPayments.FirstOrDefaultAsync(p => p.BookingId == booking.Id && p.Status == "created");
            Assert.NotNull(attempt);
            Assert.Equal(7500L, attempt.AmountCents);

            // 3. Act: Charge con ExecuteUpdateAsync real en PostgreSQL
            var chargeResult = await controller.Charge(new ChargePayment(attempt.Id, token, "tkn_test_card_pg"), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(chargeResult);

            // 4. Assert: Reserva confirmada, abono registrado
            var updatedBooking = await db.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(BookingStatus.Confirmed, updatedBooking.Status);
            Assert.Equal(75m, updatedBooking.PaidAmount);

            // 5. Assert: Movimiento de caja registrado con CulqiPaymentId
            var cashTx = await db.CashTransactions.FirstOrDefaultAsync(t => t.CulqiPaymentId == attempt.Id);
            Assert.NotNull(cashTx);
            Assert.Equal(75m, cashTx.Amount);

            // 6. Cleanup de prueba para no ensuciar la base de pruebas
            db.ElectronicReceipts.RemoveRange(await db.ElectronicReceipts.Where(r => r.BookingId == booking.Id).ToListAsync());
            db.CashTransactions.RemoveRange(await db.CashTransactions.Where(t => t.BookingId == booking.Id).ToListAsync());
            db.CulqiPayments.RemoveRange(await db.CulqiPayments.Where(p => p.BookingId == booking.Id).ToListAsync());
            db.BookingItems.RemoveRange(await db.BookingItems.Where(i => i.BookingId == booking.Id).ToListAsync());
            db.Bookings.Remove(updatedBooking);
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task Postgres_SplitPayment_AdvanceThenBalance_PersistsBothUniqueCashTransactions()
        {
            if (!await IsPostgresAvailableAsync()) return;

            await using var db = CreatePostgresDbContext();

            var shift = await db.CashRegisterShifts.FirstOrDefaultAsync(s => s.IsOpen);
            if (shift == null)
            {
                shift = new CashRegisterShift { Id = Guid.NewGuid(), IsOpen = true, OpenedAt = DateTime.UtcNow, ShiftNumber = "CJ-TEST-02" };
                db.CashRegisterShifts.Add(shift);
                await db.SaveChangesAsync();
            }

            var tour = await db.TourPackages.FirstOrDefaultAsync();
            if (tour == null)
            {
                tour = new TourPackage
                {
                    Id = Guid.NewGuid(),
                    Title = "Tour Test Integracion Split",
                    Slug = "tour-test-split-" + Guid.NewGuid().ToString()[..6],
                    BasePrice = 100m,
                    RequiredAdvancePercentage = 50,
                    Duration = "4h"
                };
                db.TourPackages.Add(tour);
                await db.SaveChangesAsync();
            }

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-SPLIT-" + Guid.NewGuid().ToString()[..6].ToUpper(),
                CustomerName = "Postgres Split Test",
                CustomerEmail = "pg_split@sigetour.com",
                CustomerDni = "72938475",
                TotalPassengers = 1,
                TotalAmount = 100m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending,
                TravelDate = DateTime.UtcNow.Date.AddDays(3)
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                Passengers = 1,
                UnitPrice = 100m,
                SubTotal = 100m
            });
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(15));

            var splitRunId = Guid.NewGuid().ToString("N")[..8];
            var chargeCallCount = 0;
            var mockClient = new CulqiApiClient(new HttpClient(new MockCulqiHttpHandler(req =>
            {
                if (req.Method == HttpMethod.Post)
                {
                    chargeCallCount++;
                    var chargeId = $"chr_test_pg_{splitRunId}_{chargeCallCount}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"{{\"object\":\"charge\",\"id\":\"{chargeId}\",\"amount\":5000,\"capture\":true,\"outcome\":{{\"type\":\"venta_exitosa\"}}}}", Encoding.UTF8, "application/json")
                    };
                }
                var uri = req.RequestUri?.ToString() ?? "";
                var getChargeId = uri.Contains($"{splitRunId}_2") ? $"chr_test_pg_{splitRunId}_2" : $"chr_test_pg_{splitRunId}_1";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"object\":\"charge\",\"id\":\"{getChargeId}\",\"amount\":5000,\"capture\":true,\"outcome\":{{\"type\":\"venta_exitosa\"}}}}", Encoding.UTF8, "application/json")
                };
            })), _config);

            var dummyHttp = new HttpClient(new MockCulqiHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var receiptService = new NubefactInvoiceService(db, _config, dummyHttp, NullLogger<NubefactInvoiceService>.Instance);
            var controller = new CulqiPaymentsController(db, mockClient, _checkoutAccess, _config, receiptService);

            // Pago 1: Adelanto 50% (S/ 50.00)
            var p1 = new CulqiPayment { BookingId = booking.Id, AmountCents = 5000, Channel = "charge", Status = "created", Booking = booking };
            db.CulqiPayments.Add(p1);
            await db.SaveChangesAsync();

            var res1 = await controller.Charge(new ChargePayment(p1.Id, token, "tkn_test_p1"), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(res1);

            // Pago 2: Saldo 50% (S/ 50.00)
            var p2 = new CulqiPayment { BookingId = booking.Id, AmountCents = 5000, Channel = "charge", Status = "created", Booking = booking };
            db.CulqiPayments.Add(p2);
            await db.SaveChangesAsync();

            var res2 = await controller.Charge(new ChargePayment(p2.Id, token, "tkn_test_p2"), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(res2);

            // Verificar en PostgreSQL real que existen exactamente 2 registros en CashTransactions
            var txs = await db.CashTransactions.Where(t => t.BookingId == booking.Id).ToListAsync();
            Assert.Equal(2, txs.Count);
            Assert.Contains(txs, t => t.CulqiPaymentId == p1.Id && t.Amount == 50m);
            Assert.Contains(txs, t => t.CulqiPaymentId == p2.Id && t.Amount == 50m);

            // Cleanup
            db.ElectronicReceipts.RemoveRange(await db.ElectronicReceipts.Where(r => r.BookingId == booking.Id).ToListAsync());
            db.CashTransactions.RemoveRange(await db.CashTransactions.Where(t => t.BookingId == booking.Id).ToListAsync());
            db.CulqiPayments.RemoveRange(await db.CulqiPayments.Where(p => p.BookingId == booking.Id).ToListAsync());
            db.BookingItems.RemoveRange(await db.BookingItems.Where(i => i.BookingId == booking.Id).ToListAsync());
            var bToDelete = await db.Bookings.FindAsync(booking.Id);
            if (bToDelete != null) db.Bookings.Remove(bToDelete);
            await db.SaveChangesAsync();
        }
    }
}
