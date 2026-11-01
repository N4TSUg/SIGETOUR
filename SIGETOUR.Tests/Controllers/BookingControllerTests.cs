using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Models;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Controllers
{
    public class BookingControllerTests
    {
        [Fact]
        public async Task Checkout_Get_ExistingTour_ReturnsViewWithTour()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new BookingController(context);
            var tour = context.TourPackages.First();

            // Act
            var result = await controller.Checkout(tour.Id);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<TourPackage>(viewResult.Model);
            Assert.Equal(tour.Id, model.Id);
            Assert.Equal(tour.Title, model.Title);
        }

        [Fact]
        public async Task Checkout_Get_NonExistingTour_RedirectsToCatalog()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = new BookingController(context);

            // Act
            var result = await controller.Checkout(Guid.NewGuid());

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Catalog", redirectResult.ActionName);
            Assert.Equal("Tour", redirectResult.ControllerName);
        }

        [Fact]
        public async Task Checkout_Post_ValidData_CreatesBookingAndRedirectsToPayment()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new BookingController(context);
            var tour = context.TourPackages.First();

            var model = new CheckoutRequestViewModel
            {
                TourId = tour.Id,
                CustomerName = "Maria Quispe",
                CustomerDni = "76543210",
                CustomerEmail = "maria@example.com",
                CustomerPhone = "987654321",
                TravelDate = DateTime.UtcNow.AddDays(2),
                Shift = "Turno Mañana",
                Passengers = 2,
                BoardingPoint = "agency",
                PassengerNames = new List<string> { "Carlos Quispe" },
                PassengerDnis = new List<string> { "76543211" },
                PassengerPhones = new List<string> { "987654322" }
            };

            // Act
            var result = await controller.Checkout(model);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Payment", redirectResult.ActionName);
            Assert.NotNull(redirectResult.RouteValues?["id"]);

            var bookingId = (Guid)redirectResult.RouteValues["id"]!;
            var savedBooking = context.Bookings.Find(bookingId);
            Assert.NotNull(savedBooking);
            Assert.Equal("Maria Quispe", savedBooking.CustomerName);
            Assert.Equal(2, savedBooking.TotalPassengers);
            Assert.Equal(tour.BasePrice * 2, savedBooking.TotalAmount);
            Assert.Equal(BookingStatus.Pending, savedBooking.Status);
            Assert.Single(savedBooking.Items);
        }

        [Fact]
        public async Task Checkout_Post_InvalidModelState_ReturnsViewWithTour()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new BookingController(context);
            var tour = context.TourPackages.First();

            controller.ModelState.AddModelError("CustomerName", "El nombre es obligatorio");
            var model = new CheckoutRequestViewModel
            {
                TourId = tour.Id,
                CustomerName = ""
            };

            // Act
            var result = await controller.Checkout(model);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var returnedTour = Assert.IsType<TourPackage>(viewResult.Model);
            Assert.Equal(tour.Id, returnedTour.Id);
        }

        [Fact]
        public async Task Payment_Get_ExistingBooking_ReturnsView()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new BookingController(context);
            var booking = context.Bookings.First();

            // Act
            var result = await controller.Payment(booking.Id);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<Booking>(viewResult.Model);
            Assert.Equal(booking.Id, model.Id);
        }

        [Fact]
        public async Task Payment_Get_NonExistingBooking_RedirectsToCatalog()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = new BookingController(context);

            // Act
            var result = await controller.Payment(Guid.NewGuid());

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Catalog", redirectResult.ActionName);
            Assert.Equal("Tour", redirectResult.ControllerName);
        }

        [Fact]
        public async Task ConfirmPayment_Post_RegistersManualProvisionalBooking_LeavesStatusPending_NoCashTransaction()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new BookingController(context);
            var booking = context.Bookings.First();
            booking.Status = BookingStatus.Pending;
            booking.PaidAmount = 0m;
            await context.SaveChangesAsync();

            // Act
            var result = await controller.ConfirmPayment(booking.Id, "Efectivo en Counter");

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Voucher", redirectResult.ActionName);
            Assert.Equal(booking.Id, redirectResult.RouteValues?["id"]);

            var updatedBooking = context.Bookings.Find(booking.Id);
            Assert.NotNull(updatedBooking);
            // Regla de seguridad: debe permanecer en Pending y PaidAmount en 0 para pagos manuales
            Assert.Equal(BookingStatus.Pending, updatedBooking.Status);
            Assert.Equal(0m, updatedBooking.PaidAmount);
            Assert.Equal("Efectivo en Counter", updatedBooking.PaymentMethod);

            // No debe inflar la caja de forma automática
            var transaction = context.CashTransactions.FirstOrDefault(t => t.BookingId == booking.Id);
            Assert.Null(transaction);
        }

        [Fact]
        public async Task Voucher_Get_ExistingBooking_ReturnsViewWithBooking()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new BookingController(context);
            var booking = context.Bookings.First();

            // Act
            var result = await controller.Voucher(booking.Id);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<Booking>(viewResult.Model);
            Assert.Equal(booking.Id, model.Id);
        }

        [Fact]
        public async Task Voucher_Get_NonExistingBooking_RedirectsToCatalog()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = new BookingController(context);

            // Act
            var result = await controller.Voucher(Guid.NewGuid());

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Catalog", redirectResult.ActionName);
            Assert.Equal("Tour", redirectResult.ControllerName);
        }

        [Fact]
        public async Task Ticket_Get_ExistingBooking_ReturnsTicketView_Default80mm()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new BookingController(context);
            var booking = context.Bookings.First();

            // Act
            var result = await controller.Ticket(booking.Id, "80");

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Equal("Ticket", viewResult.ViewName);
            Assert.Equal("80", controller.ViewBag.TicketSize);
            Assert.Equal(booking.Id, ((Booking)controller.ViewBag.Booking).Id);
        }

        [Fact]
        public async Task Ticket_Get_58mm_SetsViewBagTicketSize58()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            TestDbContextFactory.SeedSampleData(context);
            var controller = new BookingController(context);
            var booking = context.Bookings.First();

            // Act
            var result = await controller.Ticket(booking.Id, "58");

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Equal("Ticket", viewResult.ViewName);
            Assert.Equal("58", controller.ViewBag.TicketSize);
        }

        [Fact]
        public async Task Ticket_Get_NonExistingBookingOrReceipt_ReturnsNotFound()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var controller = new BookingController(context);

            // Act
            var result = await controller.Ticket(Guid.NewGuid());

            // Assert
            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task Audit7_Ticket_TotalsAndTaxBreakdown_StrictlyMatchReceipt()
        {
            // 7. Verifica que el detalle, importes unitarios, impuestos y totales del ticket de 58/80 mm coincidan con el comprobante electrónico.
            using var context = TestDbContextFactory.Create();
            var tour1 = new TourPackage { Id = Guid.NewGuid(), Title = "Ventanillas de Otuzco", BasePrice = 40m, IsActive = true };
            var tour2 = new TourPackage { Id = Guid.NewGuid(), Title = "Cumbemayo", BasePrice = 60m, IsActive = true };
            context.TourPackages.AddRange(tour1, tour2);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-TICKET-MATCH",
                CustomerName = "Amilcar Arana",
                CustomerDni = "26625235",
                TotalAmount = 100m,
                PaidAmount = 100m,
                Status = BookingStatus.Confirmed
            };
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, TourPackage = tour1, TourPackageId = tour1.Id, Passengers = 1, UnitPrice = 40m, SubTotal = 40m });
            booking.Items.Add(new BookingItem { Id = Guid.NewGuid(), BookingId = booking.Id, TourPackage = tour2, TourPackageId = tour2.Id, Passengers = 2, UnitPrice = 30m, SubTotal = 60m });
            context.Bookings.Add(booking);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "777",
                TotalAmount = 100m,
                SubtotalAmount = 84.75m,
                IgvAmount = 15.25m,
                Status = "Accepted",
                QrData = "20601234567|03|BBB1|777|15.25|100.00|2026-11-01|1|26625235|hash|",
                DigitalHash = "hash123",
                CreatedAtUtc = DateTime.UtcNow
            };
            context.ElectronicReceipts.Add(receipt);
            await context.SaveChangesAsync();

            var controller = new BookingController(context);
            var result = await controller.Ticket(receipt.Id, "80");

            var vr = Assert.IsType<ViewResult>(result);
            Assert.Equal("Ticket", vr.ViewName);
            var modelReceipt = Assert.IsType<ElectronicReceipt>(vr.Model);

            // Totales e impuestos coinciden exactamente
            Assert.Equal(100m, modelReceipt.TotalAmount);
            Assert.Equal(84.75m, modelReceipt.SubtotalAmount);
            Assert.Equal(15.25m, modelReceipt.IgvAmount);
            Assert.Equal("777", modelReceipt.Number);
            Assert.Equal("BBB1", modelReceipt.Series);

            // Booking items asociados coinciden con el comprobante
            var viewBooking = Assert.IsType<Booking>(controller.ViewBag.Booking);
            Assert.Equal(2, viewBooking.Items.Count);
            decimal totalItemsSum = 0m;
            foreach (var bi in viewBooking.Items) totalItemsSum += bi.SubTotal;
            Assert.Equal(100m, totalItemsSum);
        }

        [Fact]
        public async Task Audit5_RetryReceipt_UsesExactIdentifiers_AvoidsDuplicates()
        {
            // 5. Corrige BookingController.Ticket() y RetryReceipt() para trabajar con el identificador exacto del comprobante o pago y evitar emisiones duplicadas.
            using var context = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "ST-RETRY-EXACT",
                CustomerName = "Retry Test",
                CustomerDni = "71234567",
                TotalAmount = 40m,
                PaidAmount = 40m,
                Status = BookingStatus.Confirmed
            };
            context.Bookings.Add(booking);

            var culqiPayment = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 4000,
                Channel = "wallet_qr",
                Status = "paid"
            };
            context.Set<CulqiPayment>().Add(culqiPayment);

            var failedReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqiPayment.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "Rejected",
                TotalAmount = 40m,
                CreatedAtUtc = DateTime.UtcNow
            };
            context.ElectronicReceipts.Add(failedReceipt);
            await context.SaveChangesAsync();

            var mockReceiptService = new Moq.Mock<SIGETOUR.API.Application.Invoicing.IElectronicReceiptService>();
            mockReceiptService.Setup(s => s.GenerateReceiptAsync(booking.Id, culqiPayment.Id, System.Threading.CancellationToken.None))
                .Returns(Task.FromResult(failedReceipt));

            var controller = new BookingController(context);
            var result = await controller.RetryReceipt(failedReceipt.Id, null, null, mockReceiptService.Object);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Ticket", redirect.ActionName);
            Assert.Equal(failedReceipt.Id, redirect.RouteValues?["id"]);

            // Verifica que se invocó con los identificadores exactos de la reserva y del pago para no duplicar
            mockReceiptService.Verify(s => s.GenerateReceiptAsync(booking.Id, culqiPayment.Id, System.Threading.CancellationToken.None), Moq.Times.Once);
        }
    }
}
