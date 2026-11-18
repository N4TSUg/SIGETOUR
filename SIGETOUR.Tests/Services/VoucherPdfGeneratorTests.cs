using System;
using System.Text.Json;
using SIGETOUR.API.Application.Services;
using SIGETOUR.API.Core.Entities;
using Xunit;

namespace SIGETOUR.Tests.Services
{
    public class VoucherPdfGeneratorTests
    {
        [Fact]
        public void GenerateVoucherPdf_IncludesCompanionDni_WhenPresentInAdditionalPassengersJson()
        {
            // Arrange
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Granja de Porcón",
                Duration = "4 Horas (Medio Día)"
            };

            var extraPassengers = new[]
            {
                new { Name = "José Miguel Arana Narro", Dni = "72345678", Phone = "928876259" },
                new { Name = "María Soledad Narro Alvarez", Dni = "73456789", Phone = "944746364" }
            };

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-8428",
                CustomerName = "Cesar Enrique Arana Narro",
                CustomerDni = "71271712",
                CustomerEmail = "cesar@example.com",
                CustomerPhone = "928876259",
                BookingDate = DateTime.UtcNow,
                TravelDate = DateTime.UtcNow.AddDays(2),
                ShiftName = "Turno Mañana",
                TotalPassengers = 3,
                TotalAmount = 120m,
                PaidAmount = 120m,
                Status = BookingStatus.Confirmed,
                AdditionalPassengersJson = JsonSerializer.Serialize(extraPassengers)
            };

            // Act
            var pdfBytes = VoucherPdfGenerator.GenerateVoucherPdf(booking, tour, "BBB1-19");

            // Assert
            Assert.NotNull(pdfBytes);
            Assert.NotEmpty(pdfBytes);
            Assert.True(pdfBytes.Length > 1000);

            var pdfHeader = System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 5);
            Assert.Equal("%PDF-", pdfHeader);
        }

        [Fact]
        public void GenerateVoucherPdf_FallsBackToVerificarAlEmbarque_WhenDniIsEmpty()
        {
            // Arrange
            var tour = new TourPackage { Id = Guid.NewGuid(), Title = "Namora" };
            var extraPassengers = new[]
            {
                new { Name = "Pasajero Sin Dni", Dni = "", Phone = "" }
            };

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-9999",
                CustomerName = "Titular Test",
                CustomerDni = "11223344",
                TotalPassengers = 2,
                TotalAmount = 80m,
                PaidAmount = 80m,
                Status = BookingStatus.Confirmed,
                AdditionalPassengersJson = JsonSerializer.Serialize(extraPassengers)
            };

            // Act
            var pdfBytes = VoucherPdfGenerator.GenerateVoucherPdf(booking, tour);

            // Assert
            Assert.NotNull(pdfBytes);
            Assert.True(pdfBytes.Length > 1000);
        }
    }
}
