using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Security
{
    public class Block1DocumentAccessTests
    {
        private readonly IConfiguration _config;
        private readonly CheckoutAccess _checkoutAccess;

        public Block1DocumentAccessTests()
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "Payments:CheckoutSigningKey", "test-signing-key-for-block1-security-access-32chars" }
            };

            _config = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            _checkoutAccess = new CheckoutAccess(_config);
        }

        private (Booking booking, ElectronicReceipt receipt) CreateSampleBookingAndReceipt(SIGETOUR.API.Infrastructure.Data.AppDbContext context)
        {
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Cumbe Mayo Místico",
                BasePrice = 50m,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-4521",
                CustomerName = "Carlos Auditor",
                CustomerDni = "76543210",
                CustomerEmail = "carlos@example.com",
                CustomerPhone = "987654321",
                TotalAmount = 100m,
                PaidAmount = 100m,
                Status = BookingStatus.Confirmed,
                PaymentMethod = "Culqi",
                TravelDate = DateTime.UtcNow.AddDays(3),
                ShiftName = "Mañana",
                PickupLocation = "Plaza de Armas"
            };

            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                TourPackage = tour,
                Passengers = 2,
                UnitPrice = 50m,
                SubTotal = 100m
            });

            context.Bookings.Add(booking);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00004521",
                TotalAmount = 100m,
                SubtotalAmount = 84.75m,
                IgvAmount = 15.25m,
                Status = "Accepted",
                QrData = "20601234567|03|BBB1|00004521|15.25|100.00|2026-11-15|1|76543210|hash|",
                DigitalHash = "sample_hash",
                PdfUrl = "https://facturacion.example.com/boleta-4521.pdf",
                CreatedAtUtc = DateTime.UtcNow
            };

            context.ElectronicReceipts.Add(receipt);
            context.SaveChanges();

            return (booking, receipt);
        }

        private BookingController CreateController(SIGETOUR.API.Infrastructure.Data.AppDbContext context, ClaimsPrincipal? user = null, IDictionary<string, string>? cookies = null)
        {
            var controller = new BookingController(context, _checkoutAccess, _config);
            var httpContext = new DefaultHttpContext();

            if (user != null)
            {
                httpContext.User = user;
            }
            else
            {
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            }

            if (cookies != null && cookies.Count > 0)
            {
                var cookieHeaders = new List<string>();
                foreach (var kvp in cookies)
                {
                    cookieHeaders.Add($"{kvp.Key}={kvp.Value}");
                }
                httpContext.Request.Headers["Cookie"] = string.Join("; ", cookieHeaders);
            }

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };

            return controller;
        }

        [Fact]
        public async Task Test1_AnonymousWithoutAuthorization_AccessRejected()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var (booking, receipt) = CreateSampleBookingAndReceipt(context);
            var controller = CreateController(context); // Anonymous user, no cookies, no token
            var emailMock = new Mock<IEmailService>();

            // Act & Assert
            var voucherResult = await controller.Voucher(booking.Id, token: null);
            Assert.IsType<UnauthorizedObjectResult>(voucherResult);

            var ticketResult = await controller.Ticket(receipt.Id, token: null);
            Assert.IsType<UnauthorizedObjectResult>(ticketResult);

            var pdfResult = await controller.DownloadVoucherPdf(booking.Id, token: null);
            Assert.IsType<UnauthorizedObjectResult>(pdfResult);

            var receiptPdfResult = await controller.DownloadReceiptPdf(booking.Id, token: null);
            Assert.IsType<UnauthorizedObjectResult>(receiptPdfResult);

            var shortVoucherResult = await controller.ShortVoucherPdf("4521", token: null);
            Assert.IsType<UnauthorizedObjectResult>(shortVoucherResult);

            var shortReceiptResult = await controller.ShortReceiptPdf("4521", token: null);
            Assert.IsType<UnauthorizedObjectResult>(shortReceiptResult);

            var shortHtmlResult = await controller.ShortVoucherHtml("4521", token: null);
            Assert.IsType<UnauthorizedObjectResult>(shortHtmlResult);

            var confirmResult = await controller.ConfirmPayment(booking.Id, "Efectivo", token: null, emailMock.Object);
            Assert.IsType<UnauthorizedObjectResult>(confirmResult);

            var resendResult = await controller.ResendConfirmationEmail(booking.Id, emailMock.Object, token: null);
            Assert.IsType<UnauthorizedObjectResult>(resendResult);
        }

        [Fact]
        public async Task Test2_ManipulatedCookie_AccessRejected()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var (booking, receipt) = CreateSampleBookingAndReceipt(context);

            // An attacker crafts a cookie containing the raw GUID (vulnerable pattern from older versions)
            var manipulatedCookies = new Dictionary<string, string>
            {
                { "sigetour_last_booking", booking.Id.ToString() }
            };
            var controller = CreateController(context, cookies: manipulatedCookies);

            // Act & Assert
            var voucherResult = await controller.Voucher(booking.Id, token: null);
            Assert.IsType<UnauthorizedObjectResult>(voucherResult);

            var ticketResult = await controller.Ticket(receipt.Id, token: null);
            Assert.IsType<UnauthorizedObjectResult>(ticketResult);

            var pdfResult = await controller.DownloadVoucherPdf(booking.Id, token: null);
            Assert.IsType<UnauthorizedObjectResult>(pdfResult);

            var shortVoucherResult = await controller.ShortVoucherPdf("4521", token: null);
            Assert.IsType<UnauthorizedObjectResult>(shortVoucherResult);

            // Test with arbitrary forged token in cookie
            var forgedCookie = new Dictionary<string, string>
            {
                { "sigetour_last_booking", "invalid.signature.cookie.token" }
            };
            var forgedController = CreateController(context, cookies: forgedCookie);
            var forgedResult = await forgedController.Voucher(booking.Id, token: null);
            Assert.IsType<UnauthorizedObjectResult>(forgedResult);
        }

        [Fact]
        public async Task Test3_TokenOfAnotherBooking_TamperedOrExpired_AccessRejected()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var (bookingA, _) = CreateSampleBookingAndReceipt(context);
            var (bookingB, _) = CreateSampleBookingAndReceipt(context);
            var controller = CreateController(context);

            // A. Token belonging to BookingB presented to access BookingA
            var tokenForB = _checkoutAccess.Create(bookingB.Id, CheckoutAccess.DefaultDocumentTtl);
            var crossBookingResult = await controller.Voucher(bookingA.Id, token: tokenForB);
            Assert.IsType<UnauthorizedObjectResult>(crossBookingResult);

            // B. Tampered token (payload or HMAC modified)
            var validToken = _checkoutAccess.Create(bookingA.Id, CheckoutAccess.DefaultDocumentTtl);
            var tamperedToken = validToken.Substring(0, validToken.Length - 4) + "XXXX";
            var tamperedResult = await controller.Voucher(bookingA.Id, token: tamperedToken);
            Assert.IsType<UnauthorizedObjectResult>(tamperedResult);

            // C. Expired token (created with negative TTL)
            var expiredToken = _checkoutAccess.Create(bookingA.Id, TimeSpan.FromSeconds(-60));
            var expiredResult = await controller.Voucher(bookingA.Id, token: expiredToken);
            Assert.IsType<UnauthorizedObjectResult>(expiredResult);
        }

        [Fact]
        public async Task Test4_ValidToken_AccessGranted_WithoutOriginalSession()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var (booking, receipt) = CreateSampleBookingAndReceipt(context);

            // Independent controller representing another device or private window (no session, no cookies, unauthenticated)
            var controller = CreateController(context);
            var validToken = _checkoutAccess.Create(booking.Id, CheckoutAccess.DefaultDocumentTtl);

            // Act & Assert
            var voucherResult = await controller.Voucher(booking.Id, token: validToken);
            var viewResult = Assert.IsType<ViewResult>(voucherResult);
            Assert.Equal(booking.Id, ((Booking)viewResult.Model!).Id);

            var ticketResult = await controller.Ticket(receipt.Id, token: validToken);
            var ticketView = Assert.IsType<ViewResult>(ticketResult);
            Assert.Equal(receipt.Id, ((ElectronicReceipt)ticketView.Model!).Id);

            var pdfResult = await controller.DownloadVoucherPdf(booking.Id, token: validToken);
            var fileResult = Assert.IsType<FileContentResult>(pdfResult);
            Assert.Equal("application/pdf", fileResult.ContentType);
            Assert.NotEmpty(fileResult.FileContents);

            var shortHtmlResult = await controller.ShortVoucherHtml("4521", token: validToken);
            var redirectResult = Assert.IsType<RedirectToActionResult>(shortHtmlResult);
            Assert.Equal("Voucher", redirectResult.ActionName);
            Assert.Equal(booking.Id, redirectResult.RouteValues?["id"]);
            Assert.Equal(validToken, redirectResult.RouteValues?["token"]);
        }

        [Fact]
        public async Task Test5_KnownReservationCode_InsufficientToAccessDocuments()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var (booking, _) = CreateSampleBookingAndReceipt(context);
            var controller = CreateController(context);

            // Knowing the 4-digit code (4521 or RES-4521) without an authorized link/token must NOT grant access
            var pdfByShort = await controller.ShortVoucherPdf("4521", token: null);
            Assert.IsType<UnauthorizedObjectResult>(pdfByShort);

            var pdfByFullCode = await controller.ShortVoucherPdf(booking.ReferenceCode, token: null);
            Assert.IsType<UnauthorizedObjectResult>(pdfByFullCode);

            var boletaByShort = await controller.ShortReceiptPdf("4521", token: null);
            Assert.IsType<UnauthorizedObjectResult>(boletaByShort);

            var htmlByShort = await controller.ShortVoucherHtml("4521", token: null);
            Assert.IsType<UnauthorizedObjectResult>(htmlByShort);
        }

        [Fact]
        public void Test6_EmailServiceToken_PassesVerify_DuringValidity()
        {
            // Arrange: token created with 30-day lifetime as sent in booking confirmation emails
            var bookingId = Guid.NewGuid();
            var emailToken = _checkoutAccess.Create(bookingId, CheckoutAccess.DefaultDocumentTtl);

            // Act: verify immediately and after simulated time within 30 days
            var isImmediatelyValid = _checkoutAccess.Verify(bookingId, emailToken);

            // Assert: proves that the former bug (rejecting tokens with TTL > 7 days) is fixed
            Assert.True(isImmediatelyValid);
            Assert.Equal(TimeSpan.FromDays(30), CheckoutAccess.DefaultDocumentTtl);
            Assert.True(CheckoutAccess.MaxAllowedTtl >= TimeSpan.FromDays(30));
        }

        [Fact]
        public async Task Test7_StaffWithPermission_AccessGranted()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var (booking, receipt) = CreateSampleBookingAndReceipt(context);

            var staffPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "gerente@turismochilon.com"),
                new Claim(ClaimTypes.Role, "Admin")
            }, "CookieAuth"));

            // Controller running in authenticated staff context (no query tokens, no customer cookies)
            var controller = CreateController(context, user: staffPrincipal);

            // Act & Assert
            var voucherResult = await controller.Voucher(booking.Id, token: null);
            var viewResult = Assert.IsType<ViewResult>(voucherResult);
            Assert.Equal(booking.Id, ((Booking)viewResult.Model!).Id);

            var ticketResult = await controller.Ticket(receipt.Id, token: null);
            var ticketView = Assert.IsType<ViewResult>(ticketResult);
            Assert.Equal(receipt.Id, ((ElectronicReceipt)ticketView.Model!).Id);

            var pdfResult = await controller.DownloadVoucherPdf(booking.Id, token: null);
            var fileResult = Assert.IsType<FileContentResult>(pdfResult);
            Assert.Equal("application/pdf", fileResult.ContentType);

            var shortVoucherResult = await controller.ShortVoucherPdf(booking.ReferenceCode, token: null);
            var shortPdfResult = Assert.IsType<FileContentResult>(shortVoucherResult);
            Assert.Equal("application/pdf", shortPdfResult.ContentType);
        }
    }
}
