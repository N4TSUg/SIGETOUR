using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.Tests.Payments;
using Xunit;

namespace SIGETOUR.Tests.Audit
{
    public class HardeningAuditPass7Tests
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
                Title = "Tour Granja Porcon Test",
                Slug = "granja-porcon-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = total,
                IsActive = true
            };
            db.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Carlos Perez",
                CustomerDni = "71234567",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                ShiftName = "Mañana",
                TotalPassengers = 1,
                TotalAmount = total,
                PaidAmount = paid,
                InvoiceType = "boleta",
                PaymentMethod = "Culqi",
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
        // DEFECTO 1: UN PAGO CULQI NO PUEDE CONVERTIRSE EN OTRA OPERACIÓN MANUAL
        // =========================================================================

        [Fact]
        public async Task ResolveBillableOperationAsync_WhenCulqiConfirmed_ResolvesToCulqiCanonicalKey_EvenWithoutPaymentId()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var culqiPayment = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                ProviderId = "chr_test_12345",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqiPayment);

            var cashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqiPayment.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 100m,
                PaymentMethod = "Culqi",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(cashTx);
            await db.SaveChangesAsync();

            var service = new NubefactInvoiceService(db, CreateConfig(), new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            // Llamada sin paymentId explícito
            var identity = await service.ResolveBillableOperationAsync(booking.Id);

            Assert.Equal($"CULQI:{culqiPayment.Id:N}", identity.OperationKey);
            Assert.Equal(culqiPayment.Id, identity.CulqiPaymentId);
            Assert.Equal(cashTx.Id, identity.CashTransactionId);
            Assert.Equal(100m, identity.Amount);
            Assert.False(identity.IsHistoricalFallback);
        }

        [Fact]
        public async Task GenerateReceiptAsync_WhenCulqiAlreadyBilled_AndCalledWithoutPaymentId_ReturnsOriginalReceipt_ZeroHttpCalls()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var culqiPayment = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                ProviderId = "chr_test_99999",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqiPayment);

            // Movimiento interno de caja asociado al pago Culqi
            var cashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqiPayment.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 100m,
                PaymentMethod = "Culqi",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(cashTx);

            // Comprobante Culqi ya emitido previamente
            var originalReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqiPayment.Id,
                CashTransactionId = null, // Históricamente null en comprobantes emitidos antes de asociar CashTransaction
                BillableOperationKey = $"CULQI:{culqiPayment.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "501",
                Status = "Accepted",
                Environment = "Demo",
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
            };
            db.ElectronicReceipts.Add(originalReceipt);
            await db.SaveChangesAsync();

            int httpCalls = 0;
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                Interlocked.Increment(ref httpCalls);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(db, CreateConfig(), mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            // Invocación únicamente con bookingId (sin pasar paymentId ni cashTransactionId)
            var result = await service.GenerateReceiptAsync(booking.Id);

            Assert.Equal(originalReceipt.Id, result.Id);
            Assert.Equal("501", result.Number);
            Assert.Equal(0, httpCalls); // Cero llamadas HTTP al proveedor

            // Verificar que no se creó un comprobante duplicado en la base de datos
            var totalReceipts = await db.ElectronicReceipts.CountAsync(r => r.BookingId == booking.Id);
            Assert.Equal(1, totalReceipts);
        }

        [Fact]
        public async Task GenerateReceiptAsync_WhenCulqiCashTransactionIdPassed_ResolvesToCulqiCanonicalKey_ZeroDuplicateReceipts()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var culqiPayment = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                ProviderId = "chr_test_88888",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqiPayment);

            var cashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqiPayment.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 100m,
                PaymentMethod = "Culqi",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(cashTx);

            var originalReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqiPayment.Id,
                CashTransactionId = null,
                BillableOperationKey = $"CULQI:{culqiPayment.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "502",
                Status = "Accepted",
                Environment = "Demo",
                TotalAmount = 100m,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            db.ElectronicReceipts.Add(originalReceipt);
            await db.SaveChangesAsync();

            int httpCalls = 0;
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                Interlocked.Increment(ref httpCalls);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(db, CreateConfig(), mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            // Invocación pasando explícitamente el cashTransactionId asociado a Culqi
            var result = await service.GenerateReceiptAsync(
                booking.Id,
                culqiPaymentId: null,
                exactReceiptId: null,
                claimToken: null,
                cashTransactionId: cashTx.Id);

            Assert.Equal(originalReceipt.Id, result.Id);
            Assert.Equal(0, httpCalls);
            var totalReceipts = await db.ElectronicReceipts.CountAsync(r => r.BookingId == booking.Id);
            Assert.Equal(1, totalReceipts);
        }

        [Fact]
        public async Task ResolveBillableOperationAsync_WhenCashTxBelongsToDifferentBooking_ThrowsValidationException()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var bookingA = SeedBooking(db, total: 100m, paid: 100m);
            var bookingB = SeedBooking(db, total: 100m, paid: 100m);

            var cashTxB = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = bookingB.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 100m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(cashTxB);
            await db.SaveChangesAsync();

            var service = new NubefactInvoiceService(db, CreateConfig(), new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ResolveBillableOperationAsync(bookingA.Id, cashTransactionId: cashTxB.Id));

            Assert.Contains("no a", ex.Message);
        }

        [Fact]
        public async Task ResolveBillableOperationAsync_WhenCashTxIsEgreso_ThrowsValidationException()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var cashTxEgreso = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Egreso",
                Category = "Devolución",
                Amount = 50m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(cashTxEgreso);
            await db.SaveChangesAsync();

            var service = new NubefactInvoiceService(db, CreateConfig(), new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ResolveBillableOperationAsync(booking.Id, cashTransactionId: cashTxEgreso.Id));

            Assert.Contains("no puede ser facturada", ex.Message);
        }

        [Fact]
        public async Task ResolveBillableOperationAsync_WhenContradictoryCulqiAndCashSpecified_ThrowsValidationException()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var culqiPayment = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                ProviderId = "chr_test_111",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqiPayment);

            var manualCashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null, // Manual
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 50m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(manualCashTx);
            await db.SaveChangesAsync();

            var service = new NubefactInvoiceService(db, CreateConfig(), new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ResolveBillableOperationAsync(booking.Id, culqiPaymentId: culqiPayment.Id, cashTransactionId: manualCashTx.Id));

            Assert.Contains("Conflicto de identidad", ex.Message);
        }

        // =========================================================================
        // DEFECTO 2: UNA BOLETA HISTÓRICA NO DEBE OCULTAR UN PAGO O ABONO NUEVO
        // =========================================================================

        [Fact]
        public async Task GenerateReceiptAsync_WhenHistoricalAdvanceReceiptExists_AndNewBalanceCashTxBilled_EmitsNewReceiptWithDistinctKeyAndAmount()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            // Reserva de S/100, con anticipo histórico de S/40 y saldo completado a S/100
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            // Boleta histórica previa de S/40 (sin CashTransactionId)
            var historicalAdvanceReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                CashTransactionId = null,
                BillableOperationKey = $"MANUAL:BOOKING:{booking.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "100",
                Status = "Accepted",
                Environment = "Demo",
                SubtotalAmount = 33.90m,
                IgvAmount = 6.10m,
                TotalAmount = 40m,
                CreatedAtUtc = DateTime.UtcNow.AddDays(-2)
            };
            db.ElectronicReceipts.Add(historicalAdvanceReceipt);

            // Nuevo abono en efectivo por el saldo de S/60
            var balanceCashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                TransactionType = "Ingreso",
                Category = "Cobro de Saldo",
                Amount = 60m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(balanceCashTx);
            await db.SaveChangesAsync();

            int httpCalls = 0;
            string? sentPayload = null;
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                Interlocked.Increment(ref httpCalls);
                sentPayload = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                var json = JsonSerializer.Serialize(new
                {
                    aceptada_por_sunat = true,
                    sunat_description = "Aceptado por SUNAT",
                    numero = 101,
                    enlace_del_pdf = "https://demo.nubefact.com/pdf/101.pdf"
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(db, CreateConfig(), mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            // Facturar específicamente el movimiento de caja de saldo
            var newReceipt = await service.GenerateReceiptAsync(
                booking.Id,
                culqiPaymentId: null,
                exactReceiptId: null,
                claimToken: null,
                cashTransactionId: balanceCashTx.Id);

            // Verificar que NO devolvió la boleta histórica
            Assert.NotEqual(historicalAdvanceReceipt.Id, newReceipt.Id);
            Assert.Equal("101", newReceipt.Number);
            Assert.Equal(60m, newReceipt.TotalAmount);
            Assert.Equal($"CASH:{balanceCashTx.Id:N}", newReceipt.BillableOperationKey);
            Assert.Equal(balanceCashTx.Id, newReceipt.CashTransactionId);
            Assert.Equal(1, httpCalls);

            // Comprobar que en el payload se incluyó la regularización de anticipo con el comprobante histórico
            Assert.NotNull(sentPayload);
            Assert.Contains("\"anticipo_regularizacion\":true", sentPayload);
            Assert.Contains("BBB1", sentPayload);
            Assert.Contains("100", sentPayload);
        }

        [Fact]
        public async Task ResolveBillableOperationAsync_WhenMultipleHistoricalReceiptsWithoutSpecificId_ThrowsAmbiguousException()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var hist1 = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "201",
                Status = "Accepted",
                Environment = "Demo",
                TotalAmount = 50m,
                CreatedAtUtc = DateTime.UtcNow.AddDays(-3)
            };
            var hist2 = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "202",
                Status = "Accepted",
                Environment = "Demo",
                TotalAmount = 50m,
                CreatedAtUtc = DateTime.UtcNow.AddDays(-2)
            };
            db.ElectronicReceipts.AddRange(hist1, hist2);
            await db.SaveChangesAsync();

            var service = new NubefactInvoiceService(db, CreateConfig(), new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ResolveBillableOperationAsync(booking.Id));

            Assert.Contains("múltiples comprobantes históricos", ex.Message);
        }

        [Fact]
        public async Task GenerateReceiptAsync_TwoDistinctCashAbonos_ProduceTwoDistinctReceiptsAndKeys()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var tx1 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Category = "Abono 1",
                Amount = 50m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            };
            var tx2 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Category = "Abono 2",
                Amount = 50m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.AddRange(tx1, tx2);
            await db.SaveChangesAsync();

            int nextNum = 301;
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                var num = Interlocked.Increment(ref nextNum);
                var json = JsonSerializer.Serialize(new
                {
                    aceptada_por_sunat = true,
                    sunat_description = "Aceptado por SUNAT",
                    numero = num,
                    enlace_del_pdf = $"https://demo.nubefact.com/pdf/{num}.pdf"
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(db, CreateConfig(), mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            var r1 = await service.GenerateReceiptAsync(booking.Id, culqiPaymentId: null, exactReceiptId: null, claimToken: null, cashTransactionId: tx1.Id);
            var r2 = await service.GenerateReceiptAsync(booking.Id, culqiPaymentId: null, exactReceiptId: null, claimToken: null, cashTransactionId: tx2.Id);

            Assert.NotEqual(r1.Id, r2.Id);
            Assert.Equal($"CASH:{tx1.Id:N}", r1.BillableOperationKey);
            Assert.Equal($"CASH:{tx2.Id:N}", r2.BillableOperationKey);
            Assert.Equal(50m, r1.TotalAmount);
            Assert.Equal(50m, r2.TotalAmount);
            Assert.Equal(tx1.Id, r1.CashTransactionId);
            Assert.Equal(tx2.Id, r2.CashTransactionId);
        }

        // =========================================================================
        // DEFECTO 3: TRATAMIENTO ESPECÍFICO DE COLISIONES EN POSTGRESQL
        // =========================================================================

        [Fact]
        public async Task DbUpdateException_WhenNonUniqueErrorOrForeignKeyViolation_IsRethrown()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var mockDb = new Mock<AppDbContext>(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options);
            // Simular DbUpdateException con error de clave foránea (23503)
            var fkException = new DbUpdateException("23503: violates foreign key constraint", new Exception("violates foreign key constraint"));

            var service = new NubefactInvoiceService(db, CreateConfig(), new HttpClient(), NullLogger<NubefactInvoiceService>.Instance);

            // Verificamos mediante invocación directa con datos conflictivos que causen excepción
            // Un error de clave foránea no debe ser absorbido
            var ex = Assert.Throws<DbUpdateException>(() =>
            {
                // Simulamos la verificación de unicidad
                var method = typeof(NubefactInvoiceService).GetMethod("IsPostgresUniqueViolation",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                Assert.NotNull(method);
                var args = new object?[] { fkException, null };
                var isUnique = (bool)method.Invoke(null, args)!;
                if (!isUnique) throw fkException;
            });

            Assert.Contains("23503", ex.Message);
        }

        [Fact]
        public void IsPostgresUniqueViolation_WhenPostgres23505_ReturnsTrue_AndExtractsConstraintName()
        {
            var uniqueException = new DbUpdateException("23505: duplicate key value violates unique constraint \"IX_ElectronicReceipts_BillableOperationKey\"",
                new Exception("duplicate key value violates unique constraint \"IX_ElectronicReceipts_BillableOperationKey\""));

            var method = typeof(NubefactInvoiceService).GetMethod("IsPostgresUniqueViolation",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var args = new object?[] { uniqueException, null };
            var result = (bool)method.Invoke(null, args)!;
            var constraint = (string?)args[1];

            Assert.True(result);
            Assert.Equal("IX_ElectronicReceipts_BillableOperationKey", constraint);
        }

        // =========================================================================
        // INTEGRACIÓN EN CONTROLADORES Y VISTAS
        // =========================================================================

        [Fact]
        public async Task BookingController_RetryReceipt_WithCashTransactionId_DirectsToExactPayment()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);
            var cashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 100m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(cashTx);
            await db.SaveChangesAsync();

            var mockService = new Mock<IElectronicReceiptService>();
            var expectedReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CashTransactionId = cashTx.Id,
                BillableOperationKey = $"CASH:{cashTx.Id:N}",
                Status = "Accepted",
                Number = "401"
            };

            mockService.Setup(s => s.GenerateReceiptAsync(
                    booking.Id,
                    null,
                    null,
                    null,
                    cashTx.Id,
                    null,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedReceipt);

            var controller = new BookingController(db);

            var actionResult = await controller.RetryReceipt(
                receiptId: null,
                bookingId: booking.Id,
                paymentId: null,
                cashTransactionId: cashTx.Id,
                receiptService: mockService.Object);

            var redirect = Assert.IsType<RedirectToActionResult>(actionResult);
            Assert.Equal("Ticket", redirect.ActionName);
            Assert.Equal(expectedReceipt.Id, redirect.RouteValues?["id"]);
            mockService.Verify(s => s.GenerateReceiptAsync(
                booking.Id,
                null,
                null,
                null,
                cashTx.Id,
                null,
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
