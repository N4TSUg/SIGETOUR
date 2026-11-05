using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Application.Services;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Xunit;

namespace SIGETOUR.Tests.Services
{
    public class EmailServiceTests
    {
        private EmailService CreateService(Dictionary<string, string?>? inMemory = null)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemory ?? new Dictionary<string, string?>())
                .Build();

            var checkoutAccess = new CheckoutAccess(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Payments:CheckoutSigningKey"] = "c2lnZXRvdXJfc3VwZXJfc2VjcmV0X2NoZWNrb3V0X3NpZ25pbmdfa2V5XzIwMjZfMzJieXRlcw=="
                }).Build());

            var contactService = new CompanyContactService(config);

            return new EmailService(
                config,
                NullLogger<EmailService>.Instance,
                checkoutAccess,
                contactService);
        }

        [Fact]
        public void SenderEmail_Defaults_To_EnriqueArana_Email()
        {
            var service = CreateService();
            Assert.Equal("enriquearana1402@gmail.com", service.SenderEmail);
            Assert.Equal("Turismo Chilón Cajamarca", service.SenderName);
        }

        [Fact]
        public async Task SendEmailAsync_Fails_When_Email_Is_Invalid()
        {
            var service = CreateService();
            var result = await service.SendEmailAsync("not-an-email", "Test", "<p>Hello</p>");
            Assert.False(result.Success);
            Assert.Contains("no es válida", result.Message);
        }

        [Fact]
        public async Task SendBookingConfirmationEmailAsync_Succeeds_In_Simulation_When_No_SmtpPassword()
        {
            var service = CreateService();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-9999",
                CustomerName = "Carlos Mendoza",
                CustomerEmail = "carlos.mendoza@gmail.com",
                CustomerPhone = "987654321",
                CustomerDni = "71234567",
                TravelDate = DateTime.UtcNow.AddDays(2),
                ShiftName = "Turno Mañana (09:00 AM)",
                TotalPassengers = 2,
                TotalAmount = 80.00m,
                PaidAmount = 80.00m,
                Status = BookingStatus.Confirmed
            };

            var (success, message) = await service.SendBookingConfirmationEmailAsync(booking, "https://turismochilon.com");

            Assert.True(success);
            Assert.Contains("carlos.mendoza@gmail.com", message);
        }

        [Fact]
        public async Task ResendBookingEmail_In_AdminController_Returns_Json_Success()
        {
            // Arrange InMemory DB
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            using var db = new AppDbContext(options);
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-8888",
                CustomerName = "Maria Quispe",
                CustomerEmail = "maria.quispe@gmail.com",
                CustomerPhone = "912345678",
                TravelDate = DateTime.UtcNow.AddDays(1),
                TotalPassengers = 1,
                TotalAmount = 45.00m,
                PaidAmount = 45.00m,
                Status = BookingStatus.Confirmed
            };
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();

            var service = CreateService();

            var controller = new AdminController(
                db,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim(ClaimTypes.Name, "admin"),
                            new Claim(ClaimTypes.Role, "Admin")
                        }, "TestAuth"))
                    }
                }
            };
            controller.ControllerContext.HttpContext.Request.Scheme = "https";
            controller.ControllerContext.HttpContext.Request.Host = new HostString("localhost:7103");

            // Act
            var actionResult = await controller.ResendBookingEmail(booking.Id, null, service);

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(actionResult);
            Assert.NotNull(jsonResult.Value);
            var property = jsonResult.Value.GetType().GetProperty("success");
            Assert.NotNull(property);
            var successVal = (bool)property.GetValue(jsonResult.Value)!;
            Assert.True(successVal);
        }
    }
}
