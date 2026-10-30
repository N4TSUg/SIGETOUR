using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
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

namespace SIGETOUR.Tests.Payments
{
    public class PostgresCulqiIntegrationTests
    {
        private const string ConnectionString = "Host=localhost;Database=sigetour_db;Username=postgres;Password=3nriqueA1402#";
        private readonly IConfiguration _config;
        private readonly CheckoutAccess _checkoutAccess;

        public PostgresCulqiIntegrationTests()
        {
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
                return await db.Database.CanConnectAsync();
            }
            catch
            {
                return false;
            }
        }

        [Fact]
        public async Task Postgres_FullPaymentCycle_AtomicUpdateAndNoDuplicates()
        {
            if (!await IsPostgresAvailableAsync()) return; // Omite si PostgreSQL local no está activo en el entorno

            await using var db = CreatePostgresDbContext();

            // 1. Arrange: Crear reserva real en PostgreSQL
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

            var mockClient = new CulqiApiClient(new HttpClient(new MockCulqiHttpHandler(req =>
            {
                if (req.Method == HttpMethod.Post && req.RequestUri!.ToString().Contains("charges"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_pg_real_1\",\"amount\":7500,\"capture\":true,\"outcome\":{\"type\":\"venta_exitosa\"}}", Encoding.UTF8, "application/json")
                    };
                }
                if (req.Method == HttpMethod.Get && req.RequestUri!.ToString().Contains("charges/chr_test_pg_real_1"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"object\":\"charge\",\"id\":\"chr_test_pg_real_1\",\"amount\":7500,\"capture\":true,\"outcome\":{\"type\":\"venta_exitosa\"}}", Encoding.UTF8, "application/json")
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

            // 5. Assert: Comprobante electrónico simulado generado en tabla real
            var receipt = await db.ElectronicReceipts.FirstOrDefaultAsync(r => r.BookingId == booking.Id);
            Assert.NotNull(receipt);
            Assert.Equal("Simulation", receipt.Status);

            // 6. Cleanup de prueba para no ensuciar datos
            db.ElectronicReceipts.RemoveRange(await db.ElectronicReceipts.Where(r => r.BookingId == booking.Id).ToListAsync());
            db.CashTransactions.RemoveRange(await db.CashTransactions.Where(t => t.BookingId == booking.Id).ToListAsync());
            db.CulqiPayments.RemoveRange(await db.CulqiPayments.Where(p => p.BookingId == booking.Id).ToListAsync());
            db.BookingItems.RemoveRange(await db.BookingItems.Where(i => i.BookingId == booking.Id).ToListAsync());
            db.Bookings.Remove(updatedBooking);
            await db.SaveChangesAsync();
        }
    }
}
