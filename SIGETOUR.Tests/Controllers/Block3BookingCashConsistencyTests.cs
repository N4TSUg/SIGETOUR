using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Moq;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Controllers
{
    public class Block3BookingCashConsistencyTests
    {
        private static AdminController CreateControllerWithTempData(AppDbContext context)
        {
            var controller = TestHelpers.CreateAdminController(context);
            var httpContext = new DefaultHttpContext();
            var tempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
            controller.TempData = tempData;
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        private static (TourPackage Tour, CashRegisterShift Shift) SetupBaseData(AppDbContext context)
        {
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Cajamarca Mística",
                Slug = "tour-cajamarca-mistica-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = 50m,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var shift = new CashRegisterShift
            {
                Id = Guid.NewGuid(),
                ShiftNumber = "CJ-TEST-B3",
                IsOpen = true,
                OpenedAt = DateTime.UtcNow,
                OpenedByUserName = "cajero_test"
            };
            context.CashRegisterShifts.Add(shift);
            context.SaveChanges();

            return (tour, shift);
        }

        [Fact]
        public async Task CreateBooking_WithPaidAmount_SavesBookingAndCashTransactionTogether()
        {
            using var context = TestDbContextFactory.Create();
            var (tour, shift) = SetupBaseData(context);
            var controller = CreateControllerWithTempData(context);

            var vm = new EditBookingViewModel
            {
                TourPackageId = tour.Id,
                CustomerName = "Juan Pérez",
                CustomerDni = "72918273",
                CustomerPhone = "987654321",
                CustomerEmail = "juan@example.com",
                TravelDate = DateTime.UtcNow.AddDays(2),
                TotalPassengers = 2,
                TotalAmount = 100m,
                PaidAmount = 60m, // Abono inicial
                PaymentMethod = "Efectivo",
                Status = BookingStatus.Confirmed
            };

            var result = await controller.CreateBooking(vm);
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(AdminController.Bookings), redirect.ActionName);

            // Verificar que la reserva se guardó con PaidAmount = 60m
            var booking = await context.Bookings.FirstOrDefaultAsync(b => b.CustomerDni == "72918273");
            Assert.NotNull(booking);
            Assert.Equal(60m, booking.PaidAmount);

            // Verificar que se registró exactamente un movimiento de caja por S/ 60.00
            var cashTx = await context.CashTransactions.FirstOrDefaultAsync(t => t.BookingId == booking.Id);
            Assert.NotNull(cashTx);
            Assert.Equal(60m, cashTx.Amount);
            Assert.Equal("Ingreso", cashTx.TransactionType);
            Assert.Equal("Cobro de Reserva", cashTx.Category);
            Assert.Equal(shift.Id, cashTx.ShiftId);
        }

        [Fact]
        public async Task EditBooking_IncrementalPayment_RecordsExactlyTheDeltaDifference()
        {
            using var context = TestDbContextFactory.Create();
            var (tour, shift) = SetupBaseData(context);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-B3-DELTA",
                CustomerName = "Carlos Incremento",
                CustomerDni = "71829384",
                TotalAmount = 100m,
                PaidAmount = 40m, // Pagó S/ 40.00 inicialmente
                Status = BookingStatus.Pending,
                TravelDate = DateTime.UtcNow.AddDays(3)
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, TourPackageId = tour.Id, Passengers = 2, UnitPrice = 50m, SubTotal = 100m });
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var controller = CreateControllerWithTempData(context);

            var editModel = new EditBookingViewModel
            {
                Id = booking.Id,
                TourPackageId = tour.Id,
                CustomerName = "Carlos Incremento",
                CustomerDni = "71829384",
                TotalAmount = 100m,
                PaidAmount = 70m, // Nuevo monto: S/ 70.00 (Delta = +S/ 30.00)
                PaymentMethod = "Efectivo",
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.AddDays(3)
            };

            var result = await controller.EditBooking(editModel);
            Assert.IsType<RedirectToActionResult>(result);

            // Verificar que la reserva se actualizó a S/ 70.00
            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(70m, updatedBooking.PaidAmount);

            // Verificar que se registró una CashTransaction para el delta exacto (+S/ 30.00)
            var deltaTx = await context.CashTransactions.FirstOrDefaultAsync(t => t.BookingId == booking.Id && t.Amount == 30m);
            Assert.NotNull(deltaTx);
            Assert.Equal("Abono de Saldo", deltaTx.Category);
            Assert.Equal(30m, deltaTx.Amount);
            Assert.Equal("Ingreso", deltaTx.TransactionType);
        }

        [Fact]
        public async Task EditBooking_ReductionOfPayment_RejectedWithErrorMessage_DoesNotChangePaidAmount()
        {
            using var context = TestDbContextFactory.Create();
            var (tour, shift) = SetupBaseData(context);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-B3-REDUCE",
                CustomerName = "Reducción Tester",
                CustomerDni = "71829385",
                TotalAmount = 120m,
                PaidAmount = 80m, // Pagó S/ 80.00
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.AddDays(4)
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, TourPackageId = tour.Id, Passengers = 2, UnitPrice = 60m, SubTotal = 120m });
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var controller = CreateControllerWithTempData(context);

            var editModel = new EditBookingViewModel
            {
                Id = booking.Id,
                TourPackageId = tour.Id,
                CustomerName = "Reducción Tester",
                CustomerDni = "71829385",
                TotalAmount = 120m,
                PaidAmount = 50m, // Intento de reducción arbitraria: -S/ 30.00
                PaymentMethod = "Efectivo",
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.AddDays(4)
            };

            var result = await controller.EditBooking(editModel);
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(AdminController.EditBooking), redirect.ActionName);
            Assert.NotNull(controller.TempData["ErrorMessage"]);
            Assert.Contains("No se permite reducir el monto pagado directamente", controller.TempData["ErrorMessage"]!.ToString());

            // Verificar que en base de datos el monto pagado NO cambió
            var unmodifiedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(unmodifiedBooking);
            Assert.Equal(80m, unmodifiedBooking.PaidAmount);

            // Verificar que no se creó ningún movimiento espurio
            var countTxs = await context.CashTransactions.CountAsync(t => t.BookingId == booking.Id);
            Assert.Equal(0, countTxs);
        }

        [Fact]
        public async Task EditBooking_ReductionBelowCulqiConfirmedPayments_StrictlyRejected()
        {
            using var context = TestDbContextFactory.Create();
            var (tour, shift) = SetupBaseData(context);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-B3-CULQI-PROT",
                CustomerName = "Culqi Protection Client",
                CustomerDni = "71829386",
                TotalAmount = 100m,
                PaidAmount = 80m,
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.AddDays(3)
            };
            context.Bookings.Add(booking);

            // Pago confirmado por Culqi por S/ 60.00 (6000 céntimos)
            var culqiPayment = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 6000,
                Channel = "charge",
                Status = "paid",
                ProviderId = "chr_test_prot_123"
            };
            context.CulqiPayments.Add(culqiPayment);
            await context.SaveChangesAsync();

            var controller = CreateControllerWithTempData(context);

            // Intento de fijar el monto pagado en S/ 40.00 (por debajo de los S/ 60.00 confirmados por Culqi)
            var editModel = new EditBookingViewModel
            {
                Id = booking.Id,
                TourPackageId = tour.Id,
                CustomerName = "Culqi Protection Client",
                CustomerDni = "71829386",
                TotalAmount = 100m,
                PaidAmount = 40m,
                PaymentMethod = "Efectivo",
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.AddDays(3)
            };

            var result = await controller.EditBooking(editModel);
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(AdminController.EditBooking), redirect.ActionName);
            Assert.NotNull(controller.TempData["ErrorMessage"]);
            Assert.Contains("Culqi", controller.TempData["ErrorMessage"]!.ToString());

            var dbBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(dbBooking);
            Assert.Equal(80m, dbBooking.PaidAmount); // Intacto
        }

        [Fact]
        public async Task EditBooking_RepeatedSubmission_WithSamePaidAmount_DoesNotDuplicateCash()
        {
            using var context = TestDbContextFactory.Create();
            var (tour, shift) = SetupBaseData(context);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-B3-REPEATED",
                CustomerName = "Repeat Client",
                CustomerDni = "71829387",
                TotalAmount = 100m,
                PaidAmount = 70m,
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.AddDays(2)
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, TourPackageId = tour.Id, Passengers = 2, UnitPrice = 50m, SubTotal = 100m });
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var controller = CreateControllerWithTempData(context);

            var editModel = new EditBookingViewModel
            {
                Id = booking.Id,
                TourPackageId = tour.Id,
                CustomerName = "Repeat Client Editado",
                CustomerDni = "71829387",
                TotalAmount = 100m,
                PaidAmount = 70m, // Mismo importe pagado (paidDelta = 0)
                PaymentMethod = "Efectivo",
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.AddDays(2)
            };

            // Disparar dos veces el submit
            await controller.EditBooking(editModel);
            await controller.EditBooking(editModel);

            // No se debe haber creado ningún movimiento de caja adicional
            var countTxs = await context.CashTransactions.CountAsync(t => t.BookingId == booking.Id);
            Assert.Equal(0, countTxs);
        }

        [Fact]
        public async Task RefundBookingPayment_ExplicitApprovedFlow_RecordsEgresoAndReducesPaidAmount()
        {
            using var context = TestDbContextFactory.Create();
            var (tour, shift) = SetupBaseData(context);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-B3-REFUND",
                CustomerName = "Refund Client",
                CustomerDni = "71829388",
                TotalAmount = 100m,
                PaidAmount = 100m,
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.AddDays(5)
            };
            context.Bookings.Add(booking);
            await context.SaveChangesAsync();

            var controller = CreateControllerWithTempData(context);

            // Devolución explícita autorizada de S/ 40.00 por cambio de planes del pasajero
            var result = await controller.RefundBookingPayment(booking.Id, 40m, "Cancelación parcial de 1 cupo solicitada por el titular", "Efectivo");
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(AdminController.EditBooking), redirect.ActionName);
            Assert.NotNull(controller.TempData["SuccessMessage"]);

            // Verificar que PaidAmount se redujo exactamente a S/ 60.00
            var updatedBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(updatedBooking);
            Assert.Equal(60m, updatedBooking.PaidAmount);
            Assert.Equal(BookingStatus.Pending, updatedBooking.Status); // Vuelve a pendiente por saldo restante

            // Verificar que se registró una contrapartida de Egreso en caja
            var refundTx = await context.CashTransactions.FirstOrDefaultAsync(t => t.BookingId == booking.Id && t.TransactionType == "Egreso");
            Assert.NotNull(refundTx);
            Assert.Equal(40m, refundTx.Amount);
            Assert.Equal("Devolución de Reserva", refundTx.Category);
            Assert.Contains("Cancelación parcial", refundTx.Description);
        }

        [Fact]
        public async Task RefundBookingPayment_WhenAttemptingToRefundCulqiPayment_Rejected()
        {
            using var context = TestDbContextFactory.Create();
            var (tour, shift) = SetupBaseData(context);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-B3-CULQI-REF",
                CustomerName = "Culqi Refund Tester",
                CustomerDni = "71829389",
                TotalAmount = 100m,
                PaidAmount = 100m,
                Status = BookingStatus.Confirmed,
                TravelDate = DateTime.UtcNow.AddDays(4)
            };
            context.Bookings.Add(booking);

            // Pago por Culqi de S/ 70.00
            context.CulqiPayments.Add(new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 7000,
                Channel = "charge",
                Status = "paid",
                ProviderId = "chr_test_ref_70"
            });
            await context.SaveChangesAsync();

            var controller = CreateControllerWithTempData(context);

            // Intentar devolver S/ 50.00 (lo cual dejaría pagado S/ 50.00, violando los S/ 70.00 de Culqi)
            var result = await controller.RefundBookingPayment(booking.Id, 50m, "Devolución que afectaría pasarela", "Efectivo");
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(AdminController.EditBooking), redirect.ActionName);
            Assert.NotNull(controller.TempData["ErrorMessage"]);
            Assert.Contains("Culqi", controller.TempData["ErrorMessage"]!.ToString());

            var dbBooking = await context.Bookings.FindAsync(booking.Id);
            Assert.NotNull(dbBooking);
            Assert.Equal(100m, dbBooking.PaidAmount); // Intacto
        }

        [Fact]
        public async Task UpdateCashTransaction_WhenLinkedToBooking_RejectsAmountAlteration()
        {
            using var context = TestDbContextFactory.Create();
            var (tour, shift) = SetupBaseData(context);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-B3-TX-UPD",
                CustomerName = "Tx Upd Client",
                TotalAmount = 80m,
                PaidAmount = 80m
            };
            context.Bookings.Add(booking);

            var tx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 80m,
                PaymentMethod = "Efectivo",
                BookingId = booking.Id,
                ReferenceCode = booking.ReferenceCode,
                Description = "Cobro de reserva"
            };
            context.CashTransactions.Add(tx);
            await context.SaveChangesAsync();

            var controller = CreateControllerWithTempData(context);

            // Intento de modificar el importe del movimiento a S/ 50.00 directamente desde la pantalla de caja
            var result = await controller.UpdateCashTransaction(tx.Id, "Ingreso", "Cobro de Reserva", 50m, "Efectivo", booking.ReferenceCode, "Alterando monto");
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(AdminController.Cash), redirect.ActionName);
            Assert.NotNull(controller.TempData["ErrorMessage"]);
            Assert.Contains("No se puede alterar el importe de un movimiento vinculado a una reserva", controller.TempData["ErrorMessage"]!.ToString());

            var dbTx = await context.CashTransactions.FindAsync(tx.Id);
            Assert.NotNull(dbTx);
            Assert.Equal(80m, dbTx.Amount); // Monto original preservado
        }

        [Fact]
        public async Task DeleteCashTransaction_WhenLinkedToBookingOrCulqi_RejectsDeletion()
        {
            using var context = TestDbContextFactory.Create();
            var (tour, shift) = SetupBaseData(context);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-B3-DEL-TX",
                CustomerName = "Delete Tx Client",
                TotalAmount = 50m,
                PaidAmount = 50m
            };
            context.Bookings.Add(booking);

            var txBooking = new CashTransaction
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 50m,
                PaymentMethod = "Efectivo",
                BookingId = booking.Id,
                Description = "Cobro manual"
            };
            var txCulqi = new CashTransaction
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                TransactionType = "Ingreso",
                Category = "Cobro en Línea",
                Amount = 50m,
                PaymentMethod = "Culqi",
                CulqiPaymentId = Guid.NewGuid(),
                Description = "Cobro Culqi"
            };
            context.CashTransactions.AddRange(txBooking, txCulqi);
            await context.SaveChangesAsync();

            var controller = CreateControllerWithTempData(context);

            // 1. Intentar borrar movimiento de reserva
            var res1 = await controller.DeleteCashTransaction(txBooking.Id);
            Assert.IsType<RedirectToActionResult>(res1);
            Assert.NotNull(controller.TempData["ErrorMessage"]);
            Assert.Contains("No se puede eliminar un movimiento vinculado a una reserva", controller.TempData["ErrorMessage"]!.ToString());

            // 2. Intentar borrar movimiento de Culqi
            var res2 = await controller.DeleteCashTransaction(txCulqi.Id);
            Assert.IsType<RedirectToActionResult>(res2);
            Assert.NotNull(controller.TempData["ErrorMessage"]);
            Assert.Contains("Culqi", controller.TempData["ErrorMessage"]!.ToString());

            // Ambos movimientos deben seguir existiendo en la base de datos
            Assert.NotNull(await context.CashTransactions.FindAsync(txBooking.Id));
            Assert.NotNull(await context.CashTransactions.FindAsync(txCulqi.Id));
        }
    }
}
