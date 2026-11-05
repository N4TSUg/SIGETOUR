using Microsoft.Extensions.Configuration;
using SIGETOUR.API.Application.Services;
using System;
using System.Collections.Generic;
using Xunit;

namespace SIGETOUR.Tests.Services
{
    public class CompanyContactServiceTests : IDisposable
    {
        private readonly string? _originalEnv;

        public CompanyContactServiceTests()
        {
            _originalEnv = Environment.GetEnvironmentVariable("SIGETOUR_CONTACT_WHATSAPP");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("SIGETOUR_CONTACT_WHATSAPP", _originalEnv);
        }

        [Fact]
        public void WhatsAppNumber_Prioritizes_EnvironmentVariable_And_Normalizes()
        {
            // Arrange
            Environment.SetEnvironmentVariable("SIGETOUR_CONTACT_WHATSAPP", "928876259");
            var config = new ConfigurationBuilder().Build();
            var service = new CompanyContactService(config);

            // Act & Assert
            Assert.Equal("51928876259", service.WhatsAppNumber);
            Assert.Equal("+51 928 876 259", service.WhatsAppFormatted);
            Assert.StartsWith("https://wa.me/51928876259", service.GetWhatsAppUrl());
            Assert.Contains("text=Hola", service.GetWhatsAppUrl("Hola"));
        }

        [Fact]
        public void WhatsAppNumber_Uses_Configuration_If_Env_Not_Set()
        {
            // Arrange
            Environment.SetEnvironmentVariable("SIGETOUR_CONTACT_WHATSAPP", null);
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "Company:WhatsApp", "944746364" }
            };
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
            var service = new CompanyContactService(config);

            // Act & Assert
            Assert.Equal("51944746364", service.WhatsAppNumber);
            Assert.Equal("+51 944 746 364", service.WhatsAppFormatted);
        }
    }
}
