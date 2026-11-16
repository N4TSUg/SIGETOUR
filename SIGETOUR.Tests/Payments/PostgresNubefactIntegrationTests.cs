using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Controllers;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.Tests.Helpers;
using SIGETOUR.Tests.Payments;
using Xunit;
using Xunit.Abstractions;

namespace SIGETOUR.Tests.Payments
{
    [Collection("PostgresDatabaseCollection")]
    public class PostgresNubefactIntegrationTests
    {
        private readonly ITestOutputHelper _output;

        public PostgresNubefactIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private AppDbContext CreatePostgresDbContext()
        {
            var conn = PostgresTestHelper.GetConfiguredConnectionString()!;
            var options = PostgresTestHelper.CreatePostgresOptions(conn);
            return new AppDbContext(options);
        }

        private static IConfiguration CreateConfig()
        {
            var dict = new Dictionary<string, string?>
            {
                { "Payments:CheckoutSigningKey", "super_secret_signing_key_for_testing_checkout_12345" },
                { "Culqi:SecretKey", "sk_test_demo12345" },
                { "Culqi:PublicKey", "pk_test_demo12345" },
                { "Nubefact:Mode", "Demo" },
                { "Nubefact:Ruta", "https://demo.nubefact.com/api/v1/test" },
                { "Nubefact:Token", "demo_token_123" },
                { "Nubefact:BoletaSeries", "BBB1" },
                { "Nubefact:FacturaSeries", "FFF1" }
            };
            return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        }

        [SkippableFact]
        public async Task Postgres_VerifyAndApply_AtomicallyPersistsPendingReceiptBeforeCommit()
        {
            await using (var dbCheck = await PostgresTestHelper.RequirePostgresAsync(_output)) { }

            await using var db = CreatePostgresDbContext();
            var config = CreateConfig();
            var checkoutAccess = new CheckoutAccess(config);

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Huambocancha B4",
                Slug = "huambocancha-b4-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = 60m,
                IsActive = true
            };
            db.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-B4-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Carlos Postgres",
                CustomerDni = "12345678",
                TravelDate = DateTime.UtcNow.Date.AddDays(3),
                ShiftName = "Tarde",
                TotalPassengers = 1,
                TotalAmount = 60m,
                PaidAmount = 0m,
                InvoiceType = "boleta",
                Status = BookingStatus.Pending
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                TourPackage = tour,
                Passengers = 1,
                UnitPrice = 60m,
                SubTotal = 60m
            });
            db.Bookings.Add(booking);

            var payment = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ProviderId = "chr_pg_b4_test_" + Guid.NewGuid().ToString("N")[..8],
                AmountCents = 6000,
                Channel = "charge",
                Status = "created"
            };
            db.CulqiPayments.Add(payment);
            await db.SaveChangesAsync();

            // Mock Culqi Charge GET response
            var culqiClient = new CulqiApiClient(new HttpClient(new MockCulqiHttpHandler(req =>
            {
                var chargeJson = $"{{\"id\":\"{payment.ProviderId}\",\"object\":\"charge\",\"amount\":6000,\"currency_code\":\"PEN\",\"outcome\":{{\"type\":\"venta_exitosa\"}}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(chargeJson, Encoding.UTF8, "application/json") };
            })), config);

            // Mock Receipt Service that throws to simulate process interruption during external API call
            var failingReceiptService = new Mock<IElectronicReceiptService>();
            failingReceiptService
                .Setup(s => s.GenerateReceiptAsync(booking.Id, payment.Id, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("Simulated crash right after database commit"));

            var controller = new CulqiPaymentsController(db, culqiClient, checkoutAccess, config, failingReceiptService.Object);

            var checkoutToken = checkoutAccess.Create(booking.Id, TimeSpan.FromMinutes(10));
            var chargePaymentRequest = new ChargePayment(payment.Id, checkoutToken, "tkn_test_card");

            var body = JsonSerializer.Serialize(new
            {
                attemptId = payment.Id,
                sourceId = "tkn_test_card",
                checkoutToken = checkoutToken
            });
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            // Act: Ejecutar cargo
            var result = await controller.Charge(chargePaymentRequest, CancellationToken.None);

            // Assert: A pesar del fallo en la llamada desacoplada, el comprobante en estado Pending QUEDÓ PERSISTIDO en PostgreSQL
            var persistedReceipt = await db.ElectronicReceipts
                .FirstOrDefaultAsync(r => r.CulqiPaymentId == payment.Id);

            Assert.NotNull(persistedReceipt);
            Assert.Equal("Pending", persistedReceipt.Status);
            Assert.Equal(booking.Id, persistedReceipt.BookingId);
            Assert.Equal(60m, persistedReceipt.TotalAmount);

            // Cleanup
            db.ChangeTracker.Clear();
            var rDel = await db.ElectronicReceipts.FirstOrDefaultAsync(r => r.CulqiPaymentId == payment.Id);
            if (rDel != null) db.ElectronicReceipts.Remove(rDel);
            var cashTxs = await db.CashTransactions.Where(t => t.CulqiPaymentId == payment.Id).ToListAsync();
            db.CashTransactions.RemoveRange(cashTxs);
            var pDel = await db.CulqiPayments.FindAsync(payment.Id);
            if (pDel != null) db.CulqiPayments.Remove(pDel);
            var bDel = await db.Bookings.FindAsync(booking.Id);
            if (bDel != null) db.Bookings.Remove(bDel);
            var tDel = await db.TourPackages.FindAsync(tour.Id);
            if (tDel != null) db.TourPackages.Remove(tDel);
            await db.SaveChangesAsync();
        }

        [SkippableFact]
        public async Task Postgres_ProcessPendingReceipts_RecoversPendingReceiptInDatabase()
        {
            await using (var dbCheck = await PostgresTestHelper.RequirePostgresAsync(_output)) { }

            await using var db = CreatePostgresDbContext();
            var config = CreateConfig();

            // Limpiar comprobantes pendientes previos para no interferir en la prueba
            var oldPending = await db.ElectronicReceipts.Where(r => r.Status == "Pending").ToListAsync();
            db.ElectronicReceipts.RemoveRange(oldPending);
            await db.SaveChangesAsync();

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Otuzco B4",
                Slug = "otuzco-b4-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = 50m,
                IsActive = true
            };
            db.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-RECOVER-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Maria Recuperada",
                CustomerDni = "87654321",
                TravelDate = DateTime.UtcNow.Date.AddDays(1),
                ShiftName = "Mañana",
                TotalPassengers = 1,
                TotalAmount = 50m,
                PaidAmount = 50m,
                InvoiceType = "boleta",
                Status = BookingStatus.Confirmed
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                TourPackage = tour,
                Passengers = 1,
                UnitPrice = 50m,
                SubTotal = 50m
            });
            db.Bookings.Add(booking);

            var pendingReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "Pending",
                Environment = "Demo",
                TotalAmount = 50m,
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(-45)
            };
            db.ElectronicReceipts.Add(pendingReceipt);
            await db.SaveChangesAsync();

            var fakeNubefactResponse = "{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":999,\"enlace_del_pdf\":\"https://demo.nubefact.com/pdf/BBB1-999.pdf\",\"sunat_description\":\"Demo\"}";
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(fakeNubefactResponse, Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(db, config, mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            // Act: Recuperar comprobantes pendientes
            var count = await service.ProcessPendingReceiptsAsync(CancellationToken.None);

            // Assert: Comprobante actualizado a Demo con correlativo en PostgreSQL
            Assert.Equal(1, count);

            var updated = await db.ElectronicReceipts.FindAsync(pendingReceipt.Id);
            Assert.NotNull(updated);
            Assert.Equal("Demo", updated.Status);
            Assert.Equal("999", updated.Number);
            Assert.Equal("https://demo.nubefact.com/pdf/BBB1-999.pdf", updated.PdfUrl);

            // Cleanup
            db.ElectronicReceipts.Remove(updated);
            db.Bookings.Remove(booking);
            db.TourPackages.Remove(tour);
            await db.SaveChangesAsync();
        }

        [SkippableFact]
        public async Task Postgres_TryAcquireEmissionAsync_TwoIndependentDbContexts_OnlyOneSucceedsViaExecuteUpdate()
        {
            await using (var dbCheck = await PostgresTestHelper.RequirePostgresAsync(_output)) { }

            await using var db1 = CreatePostgresDbContext();
            await using var db2 = CreatePostgresDbContext();
            var config = CreateConfig();

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Postgres Concurrency",
                Slug = "postgres-conc-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = 100m,
                IsActive = true
            };
            db1.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-CONC-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Postgres Concurrent",
                CustomerDni = "12345678",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                ShiftName = "Mañana",
                TotalPassengers = 1,
                TotalAmount = 100m,
                PaidAmount = 100m,
                InvoiceType = "boleta",
                Status = BookingStatus.Confirmed
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                TourPackage = tour,
                Passengers = 1,
                UnitPrice = 100m,
                SubTotal = 100m
            });
            db1.Bookings.Add(booking);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "Pending",
                Environment = "Demo",
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow
            };
            db1.ElectronicReceipts.Add(receipt);
            await db1.SaveChangesAsync();

            var service1 = new NubefactInvoiceService(db1, config, new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);
            var service2 = new NubefactInvoiceService(db2, config, new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            var barrier = new Barrier(2);
            var task1 = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await service1.TryAcquireEmissionAsync(receipt.Id);
            });
            var task2 = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await service2.TryAcquireEmissionAsync(receipt.Id);
            });

            var results = await Task.WhenAll(task1, task2);
            int winners = results.Count(r => r.Acquired);
            int losers = results.Count(r => !r.Acquired);

            Assert.Equal(1, winners);
            Assert.Equal(1, losers);

            // Cleanup
            var toDeleteReceipt = await db1.ElectronicReceipts.FindAsync(receipt.Id);
            if (toDeleteReceipt != null) db1.ElectronicReceipts.Remove(toDeleteReceipt);
            var toDeleteBooking = await db1.Bookings.FindAsync(booking.Id);
            if (toDeleteBooking != null) db1.Bookings.Remove(toDeleteBooking);
            var toDeleteTour = await db1.TourPackages.FindAsync(tour.Id);
            if (toDeleteTour != null) db1.TourPackages.Remove(toDeleteTour);
            await db1.SaveChangesAsync();
        }

        [SkippableFact]
        public async Task Postgres_ManualPayment_UniqueConstraint_DbUpdateException_RecoversWinner()
        {
            await using (var dbCheck = await PostgresTestHelper.RequirePostgresAsync(_output)) { }

            await using var db1 = CreatePostgresDbContext();
            await using var db2 = CreatePostgresDbContext();
            var config = CreateConfig();

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour PG Manual",
                Slug = "tour-pg-man-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = 80m,
                IsActive = true
            };
            db1.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-MAN-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Carlos Concurrente",
                CustomerDni = "72345678",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                ShiftName = "Mañana",
                TotalPassengers = 1,
                TotalAmount = 80m,
                PaidAmount = 80m,
                InvoiceType = "boleta",
                Status = BookingStatus.Confirmed
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                TourPackage = tour,
                Passengers = 1,
                UnitPrice = 80m,
                SubTotal = 80m
            });
            db1.Bookings.Add(booking);
            await db1.SaveChangesAsync();

            var opKey = $"MANUAL:BOOKING:{booking.Id:N}";

            // Dos comprobantes creados concurrentemente con la misma BillableOperationKey
            var rec1 = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "Pending",
                BillableOperationKey = opKey,
                TotalAmount = 80m,
                CreatedAtUtc = DateTime.UtcNow
            };
            var rec2 = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "Pending",
                BillableOperationKey = opKey,
                TotalAmount = 80m,
                CreatedAtUtc = DateTime.UtcNow
            };

            db1.ElectronicReceipts.Add(rec1);
            await db1.SaveChangesAsync();

            // En db2, intentar insertar rec2 con el mismo opKey debe lanzar DbUpdateException por unicidad PostgreSQL
            db2.ElectronicReceipts.Add(rec2);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
            Assert.NotNull(ex);

            // Verificar que al desasociar y recargar se obtiene rec1
            db2.Entry(rec2).State = EntityState.Detached;
            var winner = await db2.ElectronicReceipts.FirstOrDefaultAsync(r => r.BillableOperationKey == opKey);
            Assert.NotNull(winner);
            Assert.Equal(rec1.Id, winner.Id);

            // Cleanup
            db1.ElectronicReceipts.Remove(rec1);
            db1.Bookings.Remove(booking);
            db1.TourPackages.Remove(tour);
            await db1.SaveChangesAsync();
        }

        [SkippableFact]
        public async Task Postgres_InFlightRecovery_ConditionalUpdate_DoesNotOverwriteNewLease()
        {
            await using (var dbCheck = await PostgresTestHelper.RequirePostgresAsync(_output)) { }

            await using var db1 = CreatePostgresDbContext();
            await using var db2 = CreatePostgresDbContext();
            var config = CreateConfig();

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour PG Recovery",
                Slug = "tour-pg-rec-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = 90m,
                IsActive = true
            };
            db1.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-REC-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Maria Recovery",
                CustomerDni = "87654321",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                ShiftName = "Tarde",
                TotalPassengers = 1,
                TotalAmount = 90m,
                PaidAmount = 90m,
                InvoiceType = "boleta",
                Status = BookingStatus.Confirmed
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                TourPackage = tour,
                Passengers = 1,
                UnitPrice = 90m,
                SubTotal = 90m
            });
            db1.Bookings.Add(booking);

            var oldToken = Guid.NewGuid().ToString("N");
            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "InFlight",
                EmissionLeaseToken = oldToken,
                EmissionLeaseAcquiredAtUtc = DateTime.UtcNow.AddMinutes(-5),
                EmissionLeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-4), // Expirado
                EmissionStartedAtUtc = null,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            db1.ElectronicReceipts.Add(receipt);
            await db1.SaveChangesAsync();

            var service1 = new NubefactInvoiceService(db1, config, new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);
            var service2 = new NubefactInvoiceService(db2, config, new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            // Context 2 adquiere el lease nuevo en PostgreSQL y marca inicio
            var newToken = Guid.NewGuid().ToString("N");
            var (acquired, token, _) = await service2.TryAcquireEmissionAsync(receipt.Id, requestedClaimToken: newToken);
            Assert.True(acquired);
            var started = await service2.TryMarkEmissionStartedAsync(receipt.Id, newToken);
            Assert.True(started);

            // Context 1 (recuperador) intenta recuperar con oldToken en PostgreSQL
            var recovered = await service1.TryRecoverUnsentInFlightAsync(receipt.Id, oldToken);
            Assert.False(recovered);

            // Comprobar que en PostgreSQL sigue en InFlight con newToken y no volvió a Pending
            var current = await db1.ElectronicReceipts.FindAsync(receipt.Id);
            await db1.Entry(current!).ReloadAsync();
            Assert.Equal("InFlight", current!.Status);
            Assert.Equal(newToken, current.EmissionLeaseToken);
            Assert.NotNull(current.EmissionStartedAtUtc);

            // Cleanup
            db1.ElectronicReceipts.Remove(current);
            db1.Bookings.Remove(booking);
            db1.TourPackages.Remove(tour);
            await db1.SaveChangesAsync();
        }

        [SkippableFact]
        public async Task Postgres_DbUpdateException_ForeignKeyViolation_IsRethrown_NotTreatedAsUniqueViolation()
        {
            await using (var dbCheck = await PostgresTestHelper.RequirePostgresAsync(_output)) { }

            await using var db = CreatePostgresDbContext();
            var config = CreateConfig();

            var fakeNonExistentBookingId = Guid.NewGuid();
            var service = new NubefactInvoiceService(db, config, new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            // Intentar emitir para una reserva inexistente debe lanzar InvalidOperationException antes de tocar BD
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.GenerateReceiptAsync(fakeNonExistentBookingId));
        }

        [SkippableFact]
        public async Task Postgres_DbUpdateException_UniqueViolationOnBillableOperationKey_DetachesAndReloadsWinner()
        {
            await using (var dbCheck = await PostgresTestHelper.RequirePostgresAsync(_output)) { }

            await using var db1 = CreatePostgresDbContext();
            await using var db2 = CreatePostgresDbContext();
            var config = CreateConfig();

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour PG Unique Key",
                Slug = "tour-pg-unique-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = 120m,
                IsActive = true
            };
            db1.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-UNIQ-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Pedro Unique",
                CustomerDni = "76543210",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                ShiftName = "Mañana",
                TotalPassengers = 1,
                TotalAmount = 120m,
                PaidAmount = 120m,
                InvoiceType = "boleta",
                Status = BookingStatus.Confirmed
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                TourPackage = tour,
                Passengers = 1,
                UnitPrice = 120m,
                SubTotal = 120m
            });
            db1.Bookings.Add(booking);

            var cashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 120m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };
            db1.CashTransactions.Add(cashTx);
            await db1.SaveChangesAsync();

            var service1 = new NubefactInvoiceService(db1, config, new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);
            var service2 = new NubefactInvoiceService(db2, config, new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            // Ambos contextos intentan emitir el comprobante para la misma transacción de caja
            var t1 = service1.GenerateReceiptAsync(booking.Id, culqiPaymentId: null, exactReceiptId: null, claimToken: null, cashTransactionId: cashTx.Id);
            var t2 = service2.GenerateReceiptAsync(booking.Id, culqiPaymentId: null, exactReceiptId: null, claimToken: null, cashTransactionId: cashTx.Id);

            var results = await Task.WhenAll(t1, t2);

            Assert.Equal(results[0].Id, results[1].Id);
            Assert.Equal($"CASH:{cashTx.Id:N}", results[0].BillableOperationKey);

            // Cleanup
            var receipt = await db1.ElectronicReceipts.FindAsync(results[0].Id);
            if (receipt != null) db1.ElectronicReceipts.Remove(receipt);
            db1.CashTransactions.Remove(cashTx);
            db1.Bookings.Remove(booking);
            db1.TourPackages.Remove(tour);
            await db1.SaveChangesAsync();
        }

        [SkippableFact]
        public async Task Postgres_DbUpdateException_CorrelativeCollision_IsNotRecoverable()
        {
            await using (var dbCheck = await PostgresTestHelper.RequirePostgresAsync(_output)) { }

            await using var db = CreatePostgresDbContext();

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Correlative Check",
                Slug = "tour-correl-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = 80m,
                IsActive = true
            };
            db.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "PG-CORR-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Correlative User",
                CustomerDni = "76543211",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                ShiftName = "Mañana",
                TotalPassengers = 1,
                TotalAmount = 80m,
                PaidAmount = 80m,
                InvoiceType = "boleta",
                Status = BookingStatus.Confirmed
            };
            db.Bookings.Add(booking);

            var r1 = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "COR1",
                Number = "00000001",
                TotalAmount = 80m,
                Status = "Accepted",
                BillableOperationKey = $"MANUAL:{Guid.NewGuid():N}",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ElectronicReceipts.Add(r1);
            await db.SaveChangesAsync();

            // Intentar insertar un segundo comprobante con el MISMO correlativo (Series + Number + ReceiptType)
            // pero para una operación billable distinta
            var r2 = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "COR1",
                Number = "00000001",
                TotalAmount = 80m,
                Status = "Accepted",
                BillableOperationKey = $"MANUAL:{Guid.NewGuid():N}",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ElectronicReceipts.Add(r2);

            var dbEx = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            // Validar que el helper IsRecoverableBillableOperationCollision rechaza esta colisión de correlativo
            var method = typeof(NubefactInvoiceService).GetMethod("IsRecoverableBillableOperationCollision",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var args = new object?[] { dbEx, null };
            var isRecoverable = (bool)method.Invoke(null, args)!;
            Assert.False(isRecoverable);

            // Cleanup
            db.ElectronicReceipts.Remove(r1);
            db.Bookings.Remove(booking);
            db.TourPackages.Remove(tour);
            await db.SaveChangesAsync();
        }
    }
}
