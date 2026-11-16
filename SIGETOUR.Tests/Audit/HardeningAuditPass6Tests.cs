using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.Tests.Payments;
using Xunit;

namespace SIGETOUR.Tests.Audit
{
    public class HardeningAuditPass6Tests
    {
        private static AppDbContext CreateInMemoryDb(string dbName)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
            return new AppDbContext(options);
        }

        private static IConfiguration CreateConfig(string mode = "Demo")
        {
            var dict = new Dictionary<string, string?>
            {
                { "Nubefact:Mode", mode },
                { "Nubefact:Ruta", "https://demo.nubefact.com/api/v1/test" },
                { "Nubefact:Token", "demo_token_123" },
                { "Nubefact:BoletaSeries", "BBB1" },
                { "Nubefact:FacturaSeries", "FFF1" },
                { "Company:Ruc", "20601234567" }
            };
            return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        }

        private static Booking SeedBooking(AppDbContext db, decimal total = 100m, decimal paid = 100m)
        {
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Cumbemayo Test",
                Slug = "tour-cumbemayo-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = total,
                IsActive = true
            };
            db.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Juan Perez",
                CustomerDni = "72345678",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                ShiftName = "Mañana",
                TotalPassengers = 1,
                TotalAmount = total,
                PaidAmount = paid,
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
                UnitPrice = total,
                SubTotal = total
            });
            db.Bookings.Add(booking);
            db.SaveChanges();
            return booking;
        }

        // =========================================================================
        // DEFECTO 1: RECUPERACIÓN SIN SOBRESCRIBIR PROPIEDAD DE OTRO EMISOR
        // =========================================================================

        [Fact]
        public async Task TryRecoverUnsentInFlightAsync_WhenAnotherContextAcquiredLease_ReturnsFalse_DoesNotOverwrite()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var oldToken = Guid.NewGuid().ToString("N");
            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "InFlight",
                EmissionLeaseToken = oldToken,
                EmissionLeaseAcquiredAtUtc = DateTime.UtcNow.AddMinutes(-2),
                EmissionLeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1), // Vencido
                EmissionStartedAtUtc = null,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            };
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var service = new NubefactInvoiceService(db, CreateConfig(), new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            // Simular que otro contexto adquiere un nuevo lease y marca inicio
            var newToken = Guid.NewGuid().ToString("N");
            var (acquired, token, _) = await service.TryAcquireEmissionAsync(receipt.Id, requestedClaimToken: newToken);
            Assert.True(acquired);
            var started = await service.TryMarkEmissionStartedAsync(receipt.Id, newToken);
            Assert.True(started);

            // Ahora el recuperador intenta recuperar con el token viejo que había leído
            var recovered = await service.TryRecoverUnsentInFlightAsync(receipt.Id, oldToken);
            Assert.False(recovered);

            // Verificar que el comprobante en DB sigue con el nuevo dueño y NO volvió a Pending
            var current = await db.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(current);
            Assert.Equal("InFlight", current.Status);
            Assert.Equal(newToken, current.EmissionLeaseToken);
            Assert.NotNull(current.EmissionStartedAtUtc);
        }

        [Fact]
        public async Task TryRecoverUnsentInFlightAsync_WhenUnsentAndExpired_ReturnsTrue_TransitionsToPending()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var leaseToken = Guid.NewGuid().ToString("N");
            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "InFlight",
                EmissionLeaseToken = leaseToken,
                EmissionLeaseAcquiredAtUtc = DateTime.UtcNow.AddMinutes(-2),
                EmissionLeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1), // Vencido
                EmissionStartedAtUtc = null, // Comprobablemente nunca enviado
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            };
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var service = new NubefactInvoiceService(db, CreateConfig(), new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            var recovered = await service.TryRecoverUnsentInFlightAsync(receipt.Id, leaseToken);
            Assert.True(recovered);

            var current = await db.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(current);
            Assert.Equal("Pending", current.Status);
            Assert.Null(current.EmissionLeaseToken);
            Assert.Null(current.EmissionLeaseExpiresAtUtc);
            Assert.Null(current.EmissionStartedAtUtc);
            Assert.Contains("recuperada", current.LastError ?? "");
        }

        [Fact]
        public async Task TryTransitionInFlightToReviewAsync_WhenTokenMatches_TransitionsToRequiresReview()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var leaseToken = Guid.NewGuid().ToString("N");
            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "InFlight",
                EmissionLeaseToken = leaseToken,
                EmissionStartedAtUtc = DateTime.UtcNow.AddSeconds(-30),
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
            };
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var service = new NubefactInvoiceService(db, CreateConfig(), new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            var transitioned = await service.TryTransitionInFlightToReviewAsync(receipt.Id, leaseToken, "Resultado incierto");
            Assert.True(transitioned);

            var current = await db.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(current);
            Assert.Equal("RequiresReview", current.Status);
            Assert.Equal("Resultado incierto", current.LastError);
        }

        [Fact]
        public async Task ProcessPendingReceiptsAsync_RacingWithDirectEmission_DoesNotOverwriteNewEmitter()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

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
                EmissionLeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-4), // Vencido
                EmissionStartedAtUtc = null,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            int httpCalls = 0;
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                Interlocked.Increment(ref httpCalls);
                var json = JsonSerializer.Serialize(new
                {
                    aceptada_por_sunat = true,
                    sunat_description = "Aceptado por SUNAT",
                    numero = 1001,
                    enlace_del_pdf = "https://demo.nubefact.com/pdf/1001.pdf"
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(db, CreateConfig(), mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            // Simular emisión directa concurrente antes de que ProcessPendingReceiptsAsync procese el comprobante
            var emitTask = service.GenerateReceiptAsync(booking.Id, culqiPaymentId: null, exactReceiptId: receipt.Id);
            var processTask = service.ProcessPendingReceiptsAsync();

            await Task.WhenAll(emitTask, processTask);

            // Verificar que el comprobante fue emitido con éxito y no quedó revertido a Pending
            var finalReceipt = await db.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(finalReceipt);
            Assert.Equal("Accepted", finalReceipt.Status);
            Assert.Equal("1001", finalReceipt.Number);
            Assert.Equal(1, httpCalls); // Exactamente 1 llamada HTTP externa
        }

        [Fact]
        public async Task TryCompleteEmissionAsync_ActiveOwnerCompletesSuccessfully_UpdatesState()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var leaseToken = Guid.NewGuid().ToString("N");
            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "InFlight",
                EmissionLeaseToken = leaseToken,
                EmissionStartedAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var service = new NubefactInvoiceService(db, CreateConfig(), new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            var completed = await service.TryCompleteEmissionAsync(
                receipt.Id,
                leaseToken,
                "Accepted",
                "5001",
                "https://pdf.example.com",
                null, null, null, null,
                "Aceptado", "0", null,
                DateTime.UtcNow);

            Assert.True(completed);
            var current = await db.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(current);
            Assert.Equal("Accepted", current.Status);
            Assert.Equal("5001", current.Number);
            Assert.Equal("https://pdf.example.com", current.PdfUrl);
        }

        [Fact]
        public async Task TryCompleteEmissionAsync_LateSuccessfulResponse_WhenStatusIsRequiresReview_RescuesConfirmedReceipt()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var leaseToken = Guid.NewGuid().ToString("N");
            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "RequiresReview", // El sistema lo pasó a revisión por timeout
                EmissionLeaseToken = leaseToken,
                EmissionStartedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            };
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var service = new NubefactInvoiceService(db, CreateConfig(), new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            // Llega la respuesta tardía confirmando que SUNAT sí lo aceptó
            var completed = await service.TryCompleteEmissionAsync(
                receipt.Id,
                leaseToken,
                "Accepted",
                "5002",
                "https://pdf.example.com/5002",
                null, null, null, null,
                "Aceptado tardío", "0", null,
                DateTime.UtcNow);

            Assert.True(completed);
            var current = await db.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(current);
            Assert.Equal("Accepted", current.Status);
            Assert.Equal("5002", current.Number);
        }

        [Fact]
        public async Task TryCompleteEmissionAsync_LateErrorResponse_WhenDocumentAlreadyConfirmed_NeverDegradesReceipt()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var leaseToken = Guid.NewGuid().ToString("N");
            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "5003",
                Status = "Accepted", // Ya confirmado por reconciliación o hilo ganador
                EmissionLeaseToken = leaseToken,
                EmissionStartedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            };
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var service = new NubefactInvoiceService(db, CreateConfig(), new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            // Una respuesta tardía con error o excepción intenta completarse
            var completed = await service.TryCompleteEmissionAsync(
                receipt.Id,
                leaseToken,
                "RequiresReview",
                null, null, null, null, null, null,
                null, null, "Error tardío de red",
                null);

            Assert.False(completed);
            var current = await db.ElectronicReceipts.FindAsync(receipt.Id);
            Assert.NotNull(current);
            Assert.Equal("Accepted", current.Status); // NO se degradó
            Assert.Equal("5003", current.Number);
            Assert.Null(current.LastError);
        }

        // =========================================================================
        // DEFECTO 2: CREACIÓN ATÓMICA DE INTENCIÓN PARA PAGOS MANUALES
        // =========================================================================

        [Fact]
        public async Task GenerateReceiptAsync_ManualPayment_DuplicateConcurrentRequests_CreatesSingleReceiptAndEmitsOnce()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            int httpCalls = 0;
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                Interlocked.Increment(ref httpCalls);
                var json = JsonSerializer.Serialize(new
                {
                    aceptada_por_sunat = true,
                    sunat_description = "Aceptado",
                    numero = 7001,
                    enlace_del_pdf = "https://demo.nubefact.com/pdf/7001.pdf"
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(db, CreateConfig(), mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            // Lanzar 2 llamadas concurrentes exactamente con los mismos parámetros de pago manual
            var task1 = service.GenerateReceiptAsync(booking.Id, culqiPaymentId: null);
            var task2 = service.GenerateReceiptAsync(booking.Id, culqiPaymentId: null);

            var results = await Task.WhenAll(task1, task2);

            Assert.Equal(results[0].Id, results[1].Id);
            Assert.Equal(1, await db.ElectronicReceipts.CountAsync(r => r.BookingId == booking.Id));
            Assert.Equal(1, httpCalls);
            Assert.Equal($"MANUAL:BOOKING:{booking.Id:N}", results[0].BillableOperationKey);
        }

        [Fact]
        public async Task GenerateReceiptAsync_ManualPayment_WithCashTransactions_AdvanceAndBalance_CreatesTwoDistinctReceipts()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var shift = new CashRegisterShift
            {
                Id = Guid.NewGuid(),
                ShiftNumber = "TURNO-001",
                OpenedByUserName = "Admin",
                OpenedAt = DateTime.UtcNow,
                IsOpen = true
            };
            db.CashRegisterShifts.Add(shift);

            var txAdvance = new CashTransaction
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 40m,
                PaymentMethod = "Efectivo",
                ReferenceCode = booking.ReferenceCode,
                BookingId = booking.Id,
                Description = "Anticipo",
                RegisteredBy = "Admin",
                CreatedAt = DateTime.UtcNow.AddHours(-2)
            };
            var txBalance = new CashTransaction
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                TransactionType = "Ingreso",
                Category = "Abono de Saldo",
                Amount = 60m,
                PaymentMethod = "Efectivo",
                ReferenceCode = booking.ReferenceCode,
                BookingId = booking.Id,
                Description = "Saldo Final",
                RegisteredBy = "Admin",
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            };
            db.CashTransactions.AddRange(txAdvance, txBalance);
            await db.SaveChangesAsync();

            int httpCalls = 0;
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                var count = Interlocked.Increment(ref httpCalls);
                var json = JsonSerializer.Serialize(new
                {
                    aceptada_por_sunat = true,
                    sunat_description = "Aceptado",
                    numero = 8000 + count,
                    enlace_del_pdf = $"https://demo.nubefact.com/pdf/{8000 + count}.pdf"
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(db, CreateConfig(), mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            // Emisión 1: Para el anticipo (detecta o recibe txAdvance)
            var receipt1 = await service.GenerateReceiptAsync(booking.Id, culqiPaymentId: null, exactReceiptId: null, claimToken: null, cashTransactionId: txAdvance.Id);

            // Emisión 2: Para el saldo final (detecta o recibe txBalance)
            var receipt2 = await service.GenerateReceiptAsync(booking.Id, culqiPaymentId: null, exactReceiptId: null, claimToken: null, cashTransactionId: txBalance.Id);

            Assert.NotEqual(receipt1.Id, receipt2.Id);
            Assert.Equal(txAdvance.Id, receipt1.CashTransactionId);
            Assert.Equal(txBalance.Id, receipt2.CashTransactionId);
            Assert.Equal($"CASH:{txAdvance.Id:N}", receipt1.BillableOperationKey);
            Assert.Equal($"CASH:{txBalance.Id:N}", receipt2.BillableOperationKey);
            Assert.Equal(40m, receipt1.TotalAmount);
            Assert.Equal(60m, receipt2.TotalAmount);
            Assert.Equal(2, httpCalls);
        }

        [Fact]
        public async Task GenerateReceiptAsync_HistoricalReceiptWithoutBillableKey_ReusesExistingReceipt()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var existing = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00000099",
                Status = "Accepted",
                BillableOperationKey = null, // Documento histórico sin clave
                CashTransactionId = null,    // Sin cash tx vinculado
                CulqiPaymentId = null,
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow.AddDays(-10)
            };
            db.ElectronicReceipts.Add(existing);
            await db.SaveChangesAsync();

            int httpCalls = 0;
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                Interlocked.Increment(ref httpCalls);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));

            var service = new NubefactInvoiceService(db, CreateConfig(), mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            var result = await service.GenerateReceiptAsync(booking.Id, culqiPaymentId: null);

            Assert.Equal(existing.Id, result.Id);
            Assert.Equal(0, httpCalls); // Cero llamadas externas
        }
    }
}
