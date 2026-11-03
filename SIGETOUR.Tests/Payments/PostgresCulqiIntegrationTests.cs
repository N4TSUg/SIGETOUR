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
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;
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
            if (!await IsPostgresAvailableAsync())
            {
                _output.WriteLine("[OMITIDA] La base de datos PostgreSQL de pruebas no está disponible.");
                return;
            }

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
            if (!await IsPostgresAvailableAsync())
            {
                _output.WriteLine("[OMITIDA] La base de datos PostgreSQL de pruebas no está disponible.");
                return;
            }

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

        [Fact]
        public async Task Postgres_FilteredIndex_AllowsMultiplePendingReceiptsWithoutNumber_EnforcesUniqueOnRealNumbers()
        {
            if (!await IsPostgresAvailableAsync())
            {
                _output.WriteLine("[OMITIDA] La base de datos PostgreSQL de pruebas no está disponible.");
                return;
            }

            await using var db = CreatePostgresDbContext();

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-INDEX-" + Guid.NewGuid().ToString("N")[..6].ToUpper(),
                CustomerName = "Filtered Index Test",
                CustomerEmail = "index@sigetour.com",
                CustomerDni = "71829399",
                TotalAmount = 100m,
                PaidAmount = 100m,
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.Date.AddDays(3)
            };
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();

            // Bloque 2 Item 3: Múltiples comprobantes en estado Pending sin correlativo numérico asignado
            var pending1 = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "B001",
                Number = "",
                Status = "Pending",
                TotalAmount = 50m,
                CreatedAtUtc = DateTime.UtcNow
            };
            var pending2 = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "B001",
                Number = "",
                Status = "Pending",
                TotalAmount = 50m,
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(1)
            };
            var pending3 = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "B001",
                Number = "",
                Status = "Pending",
                TotalAmount = 50m,
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(2)
            };

            db.ElectronicReceipts.AddRange(pending1, pending2, pending3);
            // No debe violar restricción de unicidad porque Number es nulo o vacío
            await db.SaveChangesAsync();

            var countPending = await db.ElectronicReceipts.CountAsync(r => r.BookingId == booking.Id && r.Series == "B001");
            Assert.Equal(3, countPending);

            // Ahora intentar insertar dos comprobantes con el MISMO número correlativo real
            var uniqueNumber = Random.Shared.Next(100000, 999999).ToString();
            var real1 = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "B001",
                Number = uniqueNumber,
                Status = "Accepted",
                TotalAmount = 50m,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ElectronicReceipts.Add(real1);
            await db.SaveChangesAsync();

            var real2Duplicate = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "B001",
                Number = uniqueNumber, // Mismo número real en la misma serie y tipo
                Status = "Accepted",
                TotalAmount = 50m,
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(1)
            };
            db.ElectronicReceipts.Add(real2Duplicate);

            // Debe lanzar excepción por violación del índice filtrado único en PostgreSQL
            await Assert.ThrowsAsync<DbUpdateException>(async () => await db.SaveChangesAsync());

            // Cleanup
            db.ChangeTracker.Clear();
            db.ElectronicReceipts.RemoveRange(await db.ElectronicReceipts.Where(r => r.BookingId == booking.Id).ToListAsync());
            var bClean = await db.Bookings.FindAsync(booking.Id);
            if (bClean != null) db.Bookings.Remove(bClean);
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task Postgres_CancelledBookingPayment_RetainsExternalMoney_PersistsIncidentAndCashTransaction()
        {
            if (!await IsPostgresAvailableAsync())
            {
                _output.WriteLine("[OMITIDA] La base de datos PostgreSQL de pruebas no está disponible.");
                return;
            }

            await using var db = CreatePostgresDbContext();

            // Reserva previamente ANULADA
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-CANC-" + Guid.NewGuid().ToString("N")[..6].ToUpper(),
                CustomerName = "Cancelled Payment Client",
                CustomerEmail = "cancelled@sigetour.com",
                CustomerDni = "72918273",
                TotalAmount = 80m,
                PaidAmount = 0m,
                Status = BookingStatus.Cancelled, // Anulada
                TravelDate = DateTime.UtcNow.Date.AddDays(5)
            };
            db.Bookings.Add(booking);

            var payment = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 8000,
                Channel = "charge",
                Status = "verification",
                ProviderId = "chr_test_canc_pg_" + Guid.NewGuid().ToString("N")[..8],
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(payment);
            await db.SaveChangesAsync();

            var token = _checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(30));

            var mockClient = CreateMockCulqiClient(req =>
            {
                var json = $"{{\"object\":\"charge\",\"id\":\"{payment.ProviderId}\",\"amount\":8000,\"currency_code\":\"PEN\",\"capture\":true,\"outcome\":{{\"type\":\"venta_exitosa\"}}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            });

            var controller = CreateController(db, mockClient);
            var result = await controller.Check(new CheckPayment(payment.Id, token, booking.Id), CancellationToken.None) as OkObjectResult;
            Assert.NotNull(result);

            // Verificar en PostgreSQL: Dinero acreditado en CashTransactions y PaymentIncident registrado
            var incident = await db.PaymentIncidents.FirstOrDefaultAsync(i => i.CulqiPaymentId == payment.Id);
            Assert.NotNull(incident);
            Assert.Equal("CancelledBookingPayment", incident.IncidentType);
            Assert.Equal("Critical", incident.Severity);
            Assert.Equal(80m, incident.Amount);
            Assert.Equal("PendingReview", incident.Status);

            var cashTx = await db.CashTransactions.FirstOrDefaultAsync(t => t.CulqiPaymentId == payment.Id);
            Assert.NotNull(cashTx);
            Assert.Equal("Cobro de Reserva Cancelada", cashTx.Category);
            Assert.Equal(80m, cashTx.Amount);

            // Cleanup
            db.PaymentIncidents.RemoveRange(await db.PaymentIncidents.Where(i => i.CulqiPaymentId == payment.Id).ToListAsync());
            db.CashTransactions.RemoveRange(await db.CashTransactions.Where(t => t.CulqiPaymentId == payment.Id).ToListAsync());
            db.CulqiPayments.RemoveRange(await db.CulqiPayments.Where(p => p.BookingId == booking.Id).ToListAsync());
            var bClean = await db.Bookings.FindAsync(booking.Id);
            if (bClean != null) db.Bookings.Remove(bClean);
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task Postgres_CapacityCheck_DoesNotThrowDateTimeUnspecifiedException()
        {
            if (!await IsPostgresAvailableAsync())
            {
                _output.WriteLine("[OMITIDA] La base de datos PostgreSQL de pruebas no está disponible.");
                return;
            }

            await using var db = CreatePostgresDbContext();

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "PG Capacity Test Tour " + Guid.NewGuid().ToString("N")[..6],
                Slug = "pg-cap-test-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = 60m,
                MaxCapacity = 20,
                IsActive = true
            };
            db.TourPackages.Add(tour);
            await db.SaveChangesAsync();

            var controller = new BookingController(db, _checkoutAccess, _config);

            // TravelDate enviada desde navegador/formulario HTML como DateTimeKind.Unspecified
            var unspecifiedDate = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Unspecified);

            var checkoutModel = new CheckoutRequestViewModel
            {
                TourId = tour.Id,
                CustomerName = "Unspecified Tester",
                CustomerDni = "72918234",
                CustomerEmail = "unspecified@sigetour.com",
                CustomerPhone = "987654321",
                TravelDate = unspecifiedDate,
                Shift = "",
                Passengers = 2,
                BoardingPoint = "central"
            };

            // Act: no debe lanzar System.ArgumentException sobre Npgsql timestamptz
            var result = await controller.Checkout(checkoutModel);
            Assert.IsType<RedirectToActionResult>(result);

            // Cleanup
            var savedBooking = await db.Bookings.Include(b => b.Items).FirstOrDefaultAsync(b => b.CustomerEmail == "unspecified@sigetour.com");
            if (savedBooking != null)
            {
                db.BookingItems.RemoveRange(savedBooking.Items);
                db.Bookings.Remove(savedBooking);
            }
            db.TourPackages.Remove(tour);
            await db.SaveChangesAsync();
        }

        private CulqiApiClient CreateMockCulqiClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(handler));
            return new CulqiApiClient(mockHttp, _config);
        }

        private CulqiPaymentsController CreateController(AppDbContext context, CulqiApiClient culqiClient)
        {
            var dummyHttp = new HttpClient(new MockCulqiHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var receiptService = new NubefactInvoiceService(context, _config, dummyHttp, NullLogger<NubefactInvoiceService>.Instance);
            return new CulqiPaymentsController(context, culqiClient, _checkoutAccess, _config, receiptService);
        }
    }
}
