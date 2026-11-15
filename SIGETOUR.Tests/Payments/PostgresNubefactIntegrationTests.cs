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
    public class PostgresNubefactIntegrationTests
    {
        private static readonly string ConnectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")
            ?? "Host=localhost;Database=sigetour_test_db;Username=postgres;Password=3nriqueA1402#";

        private readonly ITestOutputHelper _output;

        public PostgresNubefactIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private AppDbContext CreatePostgresDbContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(ConnectionString, npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .Options;

            return new AppDbContext(options);
        }

        private async Task<bool> IsPostgresAvailableAsync()
        {
            try
            {
                await using var db = CreatePostgresDbContext();
                return await db.Database.CanConnectAsync();
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[OMITIDA] Error conectando a PostgreSQL de pruebas: {ex.Message}");
                return false;
            }
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

        [Fact]
        public async Task Postgres_VerifyAndApply_AtomicallyPersistsPendingReceiptBeforeCommit()
        {
            if (!await IsPostgresAvailableAsync())
            {
                _output.WriteLine("[OMITIDA] La base de datos PostgreSQL de pruebas no está disponible.");
                return;
            }

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
            db.ElectronicReceipts.Remove(persistedReceipt);
            var cashTxs = await db.CashTransactions.Where(t => t.CulqiPaymentId == payment.Id).ToListAsync();
            db.CashTransactions.RemoveRange(cashTxs);
            db.CulqiPayments.Remove(payment);
            db.Bookings.Remove(booking);
            db.TourPackages.Remove(tour);
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task Postgres_ProcessPendingReceipts_RecoversPendingReceiptInDatabase()
        {
            if (!await IsPostgresAvailableAsync())
            {
                _output.WriteLine("[OMITIDA] La base de datos PostgreSQL de pruebas no está disponible.");
                return;
            }

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
    }
}
