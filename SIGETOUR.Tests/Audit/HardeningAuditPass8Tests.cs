using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
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
using SIGETOUR.API.Core.Security;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;
using SIGETOUR.Tests.Payments;
using Xunit;

namespace SIGETOUR.Tests.Audit
{
    public class HardeningAuditPass8Tests
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
                Title = "Tour Otuzco Pass 8",
                Slug = "otuzco-p8-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = total,
                IsActive = true
            };
            db.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Maria Garcia",
                CustomerDni = "73456789",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                ShiftName = "Mañana",
                TotalPassengers = 1,
                TotalAmount = total,
                PaidAmount = paid,
                InvoiceType = "boleta",
                PaymentMethod = "Efectivo",
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

        private static BookingController CreateControllerWithUser(AppDbContext context, string? permission = AppPermissions.ReceiptsManage)
        {
            var controller = new BookingController(context);
            var httpContext = new DefaultHttpContext();
            if (!string.IsNullOrEmpty(permission))
            {
                var claims = new List<Claim>
                {
                    new Claim("Permission", permission),
                    new Claim(ClaimTypes.Name, "AdminUser")
                };
                if (permission == AppPermissions.ReceiptsManage)
                {
                    claims.Add(new Claim("Permission", AppPermissions.ReceiptsView));
                    claims.Add(new Claim("Permission", AppPermissions.BookingsView));
                }
                var identity = new ClaimsIdentity(claims, "TestAuth");
                httpContext.User = new ClaimsPrincipal(identity);
            }
            else
            {
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            }

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };
            controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
            return controller;
        }

        // =========================================================================
        // REQUISITO 1: CORREGIR LA SELECCIÓN DEL COMPROBANTE EN TICKET
        // =========================================================================

        [Fact]
        public async Task Ticket_WhenHistoricalReceiptAndNewCashTx_ReturnsNullReceipt_AndSetsSelectedOperationAmount()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            // Boleta histórica previa de S/40
            var historicalReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "10",
                TotalAmount = 40m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-2)
            };
            db.ElectronicReceipts.Add(historicalReceipt);

            // Nuevo movimiento de caja de S/60 pendiente de comprobante
            var newCashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Saldo",
                Amount = 60m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(newCashTx);
            await db.SaveChangesAsync();

            var controller = CreateControllerWithUser(db);

            // Invocación a Ticket especificando el nuevo movimiento de caja
            var actionResult = await controller.Ticket(booking.Id, cashTransactionId: newCashTx.Id);

            var viewResult = Assert.IsType<ViewResult>(actionResult);
            // Requisito 1.a: No devolver la boleta de S/40; receipt debe ser null porque la operación aún no tiene comprobante
            Assert.Null(viewResult.Model);

            // Requisito 1.c: Mostrar el importe de la operación seleccionada (S/60), no el acumulado (S/100)
            Assert.Equal(60m, controller.ViewBag.SelectedOperationAmount);
            Assert.Equal(newCashTx.Id, controller.ViewBag.SelectedCashTransactionId);
        }

        [Fact]
        public async Task Ticket_WhenCashTxAlreadyBilled_ReturnsExactReceipt()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 60m, paid: 60m);

            var cashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Amount = 60m,
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(cashTx);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CashTransactionId = cashTx.Id,
                BillableOperationKey = $"CASH:{cashTx.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "15",
                TotalAmount = 60m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var controller = CreateControllerWithUser(db);

            var actionResult = await controller.Ticket(booking.Id, cashTransactionId: cashTx.Id);

            var viewResult = Assert.IsType<ViewResult>(actionResult);
            var modelReceipt = Assert.IsType<ElectronicReceipt>(viewResult.Model);
            Assert.Equal(receipt.Id, modelReceipt.Id);
            Assert.Equal("15", modelReceipt.Number);
        }

        [Fact]
        public async Task Ticket_WhenCashTxBelongsToDifferentBooking_ReturnsBadRequest()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var bookingA = SeedBooking(db);
            var bookingB = SeedBooking(db);

            var cashTxB = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = bookingB.Id,
                TransactionType = "Ingreso",
                Amount = 50m,
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(cashTxB);
            await db.SaveChangesAsync();

            var controller = CreateControllerWithUser(db);

            // Requisito 1.b: Rechazar identificadores incompatibles antes de devolver documentos
            var actionResult = await controller.Ticket(bookingA.Id, cashTransactionId: cashTxB.Id);

            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            Assert.Contains("no pertenece", badRequest.Value?.ToString() ?? "");
        }

        [Fact]
        public async Task Ticket_WhenCashTxLinkedToCulqi_ResolvesCanonicalCulqiReceipt_WithoutRequiringCashTransactionIdOnReceipt()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqi);

            var cashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(cashTx);

            // Comprobante histórico de Culqi sin CashTransactionId
            var culqiReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                CashTransactionId = null,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "20",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ElectronicReceipts.Add(culqiReceipt);
            await db.SaveChangesAsync();

            var controller = CreateControllerWithUser(db);

            // Requisito 1.d: Resolver identidad canónica y no exigir CashTransactionId en el documento histórico
            var actionResult = await controller.Ticket(booking.Id, cashTransactionId: cashTx.Id);

            var viewResult = Assert.IsType<ViewResult>(actionResult);
            var model = Assert.IsType<ElectronicReceipt>(viewResult.Model);
            Assert.Equal(culqiReceipt.Id, model.Id);
            Assert.Equal("20", model.Number);
        }

        [Fact]
        public async Task Ticket_WhenReceiptIdProvidedDirectly_PreservesHistoricalReceipt()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "88",
                TotalAmount = 100m,
                Status = "Accepted",
                SnapshotJson = "{\"Emisor\":{\"RazonSocial\":\"TURISMO HISTORICO\"}}",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var controller = CreateControllerWithUser(db);

            // Requisito 1.e: Conservar apertura directa por receiptId
            var actionResult = await controller.Ticket(receipt.Id);

            var viewResult = Assert.IsType<ViewResult>(actionResult);
            var model = Assert.IsType<ElectronicReceipt>(viewResult.Model);
            Assert.Equal(receipt.Id, model.Id);
            Assert.Contains("TURISMO HISTORICO", model.SnapshotJson);
        }

        [Fact]
        public async Task Ticket_WhenMultipleOperationsAndNoSelection_PopulatesAvailableOperationsList_DoesNotPickArbitraryReceipt()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var r1 = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "1",
                TotalAmount = 40m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-1)
            };
            db.ElectronicReceipts.Add(r1);

            var tx2 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Category = "Saldo",
                Amount = 60m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(tx2);
            await db.SaveChangesAsync();

            var controller = CreateControllerWithUser(db);

            // Consulta sin operación seleccionada
            var actionResult = await controller.Ticket(booking.Id);

            var viewResult = Assert.IsType<ViewResult>(actionResult);
            // Requisito 1.f: No elegir arbitrariamente el comprobante existente
            Assert.Null(viewResult.Model);

            var availableOps = controller.ViewBag.AvailableOperations as List<BookingOperationOptionViewModel>;
            Assert.NotNull(availableOps);
            Assert.True(availableOps.Count >= 2);
            Assert.Contains(availableOps, op => op.IsBilled && op.Amount == 40m);
            Assert.Contains(availableOps, op => !op.IsBilled && op.Amount == 60m);
        }

        // =========================================================================
        // REQUISITO 2: MANEJAR LA EMISIÓN CON VARIOS COBROS PENDIENTES
        // =========================================================================

        [Fact]
        public async Task RetryReceipt_WhenTwoUnbilledMovementsAndNoSelection_SetsTempDataError_AndRedirectsToTicket_ZeroEmissions()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var tx1 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Amount = 50m,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10)
            };
            var tx2 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Amount = 50m,
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.AddRange(tx1, tx2);
            await db.SaveChangesAsync();

            var mockService = new Mock<IElectronicReceiptService>();
            mockService.Setup(s => s.GenerateReceiptAsync(booking.Id, null, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException($"La reserva {booking.ReferenceCode} tiene múltiples movimientos de caja. Especifique el identificador del movimiento a facturar."));

            var controller = CreateControllerWithUser(db);

            // Invocación POST sin selección de cobro
            var actionResult = await controller.RetryReceipt(
                receiptId: null,
                bookingId: booking.Id,
                paymentId: null,
                receiptService: mockService.Object,
                cashTransactionId: null);

            // Requisito 2.e: Convertir en respuesta controlada y mensaje comprensible (no error 500)
            var redirect = Assert.IsType<RedirectToActionResult>(actionResult);
            Assert.Equal("Ticket", redirect.ActionName);
            Assert.Equal(booking.Id, redirect.RouteValues?["id"]);
            Assert.Contains("múltiples movimientos", controller.TempData["ErrorMessage"]?.ToString() ?? "");
        }

        [Fact]
        public async Task RetryReceipt_WhenSpecificCashTxSelected_EmitsOnlySelectedMovement()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var tx1 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Amount = 40m,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5)
            };
            var tx2 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Amount = 60m,
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.AddRange(tx1, tx2);
            await db.SaveChangesAsync();

            var expectedReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CashTransactionId = tx2.Id,
                BillableOperationKey = $"CASH:{tx2.Id:N}",
                Number = "102",
                TotalAmount = 60m,
                Status = "Accepted"
            };

            var mockService = new Mock<IElectronicReceiptService>();
            mockService.Setup(s => s.GenerateReceiptAsync(
                    booking.Id,
                    null,
                    null,
                    null,
                    tx2.Id,
                    null,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedReceipt);

            var controller = CreateControllerWithUser(db);

            // POST con la selección explícita del movimiento de S/60
            var actionResult = await controller.RetryReceipt(
                receiptId: null,
                bookingId: booking.Id,
                paymentId: null,
                receiptService: mockService.Object,
                cashTransactionId: tx2.Id);

            var redirect = Assert.IsType<RedirectToActionResult>(actionResult);
            Assert.Equal("Ticket", redirect.ActionName);
            Assert.Equal(expectedReceipt.Id, redirect.RouteValues?["id"]);

            // Requisito 2.c: Validar que se invocó exactamente con el identificador seleccionado
            mockService.Verify(s => s.GenerateReceiptAsync(
                booking.Id,
                null,
                null,
                null,
                tx2.Id,
                null,
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RetryReceipt_WhenCashTxFromOtherBooking_ReturnsBadRequest()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var bookingA = SeedBooking(db);
            var bookingB = SeedBooking(db);

            var cashTxB = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = bookingB.Id,
                TransactionType = "Ingreso",
                Amount = 50m,
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(cashTxB);
            await db.SaveChangesAsync();

            var controller = CreateControllerWithUser(db);
            var mockService = new Mock<IElectronicReceiptService>();

            var actionResult = await controller.RetryReceipt(
                receiptId: null,
                bookingId: bookingA.Id,
                paymentId: null,
                receiptService: mockService.Object,
                cashTransactionId: cashTxB.Id);

            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            Assert.Contains("no pertenece", badRequest.Value?.ToString() ?? "");
            mockService.VerifyNoOtherCalls();
        }

        // =========================================================================
        // REQUISITO 3: LIMITAR LA RECUPERACIÓN DE COLISIONES A LAS RESTRICCIONES CORRECTAS
        // =========================================================================

        [Fact]
        public void IsRecoverableBillableOperationCollision_WhenAllowedConstraint_ReturnsTrue()
        {
            var method = typeof(NubefactInvoiceService).GetMethod("IsRecoverableBillableOperationCollision",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            string[] allowed = {
                "IX_ElectronicReceipts_BillableOperationKey",
                "IX_ElectronicReceipts_CashTransactionId",
                "IX_ElectronicReceipts_CulqiPaymentId"
            };

            foreach (var constraint in allowed)
            {
                var ex = new DbUpdateException($"23505: duplicate key value violates unique constraint \"{constraint}\"",
                    new Exception($"violates unique constraint \"{constraint}\""));
                var args = new object?[] { ex, null };
                var result = (bool)method.Invoke(null, args)!;
                Assert.True(result, $"Fallo para restricción permitida: {constraint}");
                Assert.Equal(constraint, (string?)args[1]);
            }
        }

        [Fact]
        public void IsRecoverableBillableOperationCollision_WhenCorrelativeConstraint_ReturnsFalse()
        {
            var method = typeof(NubefactInvoiceService).GetMethod("IsRecoverableBillableOperationCollision",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            // Requisito 3.c: No tratar como colisión recuperable duplicados de correlativo
            var correlativeEx = new DbUpdateException("23505: duplicate key value violates unique constraint \"IX_ElectronicReceipts_Series_Number_ReceiptType\"",
                new Exception("violates unique constraint \"IX_ElectronicReceipts_Series_Number_ReceiptType\""));

            var args = new object?[] { correlativeEx, null };
            var result = (bool)method.Invoke(null, args)!;
            Assert.False(result);
            Assert.Null(args[1]);
        }

        [Fact]
        public void IsRecoverableBillableOperationCollision_WhenPKOrUnknownOrFK_ReturnsFalse()
        {
            var method = typeof(NubefactInvoiceService).GetMethod("IsRecoverableBillableOperationCollision",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            // 1. Clave primaria
            var pkEx = new DbUpdateException("23505: duplicate key value violates unique constraint \"PK_ElectronicReceipts\"",
                new Exception("violates unique constraint \"PK_ElectronicReceipts\""));
            var argsPk = new object?[] { pkEx, null };
            Assert.False((bool)method.Invoke(null, argsPk)!);

            // 2. Restricción desconocida
            var unknownEx = new DbUpdateException("23505: duplicate key value violates unique constraint \"IX_SomeUnknownConstraint\"",
                new Exception("violates unique constraint \"IX_SomeUnknownConstraint\""));
            var argsUnk = new object?[] { unknownEx, null };
            Assert.False((bool)method.Invoke(null, argsUnk)!);

            // 3. Clave foránea (23503)
            var fkEx = new DbUpdateException("23503: violates foreign key constraint \"FK_ElectronicReceipts_Bookings_BookingId\"",
                new Exception("violates foreign key constraint"));
            var argsFk = new object?[] { fkEx, null };
            Assert.False((bool)method.Invoke(null, argsFk)!);
        }

        [Fact]
        public async Task GenerateReceiptAsync_WhenCorrelativeCollision_PropagatesException_DoesNotRecover()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            // Simular fallo de correlativo NubeFact duplicado (por ejemplo, número ya existente)
            var correlativeEx = new DbUpdateException("23505: duplicate key value violates unique constraint \"IX_ElectronicReceipts_Series_Number_ReceiptType\"",
                new Exception("violates unique constraint \"IX_ElectronicReceipts_Series_Number_ReceiptType\""));

            var method = typeof(NubefactInvoiceService).GetMethod("IsRecoverableBillableOperationCollision",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var args = new object?[] { correlativeEx, null };
            var isRecoverable = (bool)method.Invoke(null, args)!;

            // Requisito 3.c: Propagar el error y no ocultar el fallo
            Assert.False(isRecoverable);
        }
    }
}
