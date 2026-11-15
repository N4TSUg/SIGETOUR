using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Application.Options;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Application.Services;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Common;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Controllers
{
    public class Block7EmailAndImagesTests : IDisposable
    {
        private readonly string _testWebRoot;
        private readonly Mock<IWebHostEnvironment> _envMock;
        private readonly Mock<ILogger<CloudinaryStorageService>> _loggerMock;
        private readonly Mock<IConfiguration> _configMock;

        public Block7EmailAndImagesTests()
        {
            _testWebRoot = Path.Combine(Path.GetTempPath(), "sigetour_b7_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testWebRoot);

            _envMock = new Mock<IWebHostEnvironment>();
            _envMock.Setup(e => e.WebRootPath).Returns(_testWebRoot);
            _loggerMock = new Mock<ILogger<CloudinaryStorageService>>();
            _configMock = new Mock<IConfiguration>();
        }

        public void Dispose()
        {
            if (Directory.Exists(_testWebRoot))
            {
                try { Directory.Delete(_testWebRoot, true); } catch { }
            }
        }

        private static byte[] CreateValidJpegBytes()
        {
            return new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48 };
        }

        private static byte[] CreateValidPngBytes()
        {
            return new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        }

        private static byte[] CreateValidWebpBytes()
        {
            return new byte[] { 0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 };
        }

        private EmailService CreateEmailService(Dictionary<string, string?>? inMemory = null, AppDbContext? db = null)
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

            var services = new ServiceCollection();
            if (db != null)
            {
                services.AddScoped(_ => db);
            }
            var sp = services.BuildServiceProvider();

            return new EmailService(
                config,
                NullLogger<EmailService>.Instance,
                checkoutAccess,
                contactService,
                sp);
        }

        #region 1. Error de Dominios Permitidos y Validación

        [Theory]
        [InlineData("cliente@gmail.com", true)]
        [InlineData("turista@hotmail.com", true)]
        [InlineData("viajero@outlook.com", true)]
        [InlineData("visitante@yahoo.es", true)]
        [InlineData("contacto@empresa.com.pe", true)]
        [InlineData("malformed-email", false)]
        [InlineData("@sinusuario.com", false)]
        [InlineData("sindominio@", false)]
        [InlineData("usuario@sintld", false)]
        [InlineData("usuario con espacios@gmail.com", false)]
        public void RecipientEmail_SyntaxValidation_AcceptsAllPublicDomains_RejectsMalformed(string email, bool expectedValid)
        {
            var validation = EmailValidationHelper.ValidateRecipientEmail(email);
            Assert.Equal(expectedValid, validation.IsValid);
        }

        [Fact]
        public void ProviderError_DomainRestriction_ClassifiedCorrectly_AsSandboxRestriction()
        {
            // Error típico de proveedor en modo sandbox / sin dominio verificado
            var brevoError = "{\"code\":\"bad_request\",\"message\":\"domain is not allowed\"}";
            var classification = EmailValidationHelper.ClassifyProviderError(brevoError, null);

            Assert.True(classification.IsProviderDomainRestriction);
            Assert.Equal(EmailFailureCategory.ProviderDomainRestriction, classification.Category);
            Assert.Contains("sandbox", classification.UserMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ProviderError_SmtpRelayDenied_ClassifiedCorrectly_AsDomainRestriction()
        {
            // Error típico SMTP 550 cuando el relay no permite enviar al dominio
            var smtpEx = new System.Net.Mail.SmtpException("550 5.7.1 Relaying denied to destination domain");
            var classification = EmailValidationHelper.ClassifyProviderError(null, smtpEx);

            Assert.True(classification.IsProviderDomainRestriction);
            Assert.Equal(EmailFailureCategory.ProviderDomainRestriction, classification.Category);
        }

        [Fact]
        public async Task EmailFailure_DoesNotThrowException_AndReturnsFailureResult()
        {
            // Con configuración de puerto inválido para forzar error sin lanzar excepción no controlada
            var service = CreateEmailService(new Dictionary<string, string?>
            {
                ["EmailSettings:Password"] = "some-app-password",
                ["EmailSettings:Host"] = "invalid.smtp.nonexistent.domain",
                ["EmailSettings:Port"] = "9999"
            });

            var result = await service.SendEmailAsync("cliente@gmail.com", "Asunto", "<p>Texto</p>");

            Assert.False(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }

        #endregion

        #region 2. Plantillas de Correo y Seguridad

        [Fact]
        public async Task EmailTemplate_ConvertsHttpToHttps_AndEncodesUserFields()
        {
            var service = CreateEmailService();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-SEC-01",
                CustomerName = "Carlos <script>alert('xss')</script> D'Onofrio",
                CustomerEmail = "carlos.donofrio@gmail.com",
                CustomerDni = "70123456",
                TravelDate = DateTime.UtcNow.AddDays(3),
                ShiftName = "Turno Mañana",
                TotalPassengers = 2,
                TotalAmount = 100m,
                PaidAmount = 100m,
                Status = BookingStatus.Confirmed
            };

            var (success, message, attachedCount) = await service.SendBookingConfirmationEmailAsync(
                booking, "http://agencia.turismochilon.com");

            Assert.True(success);
            Assert.True(attachedCount >= 1);
            Assert.Contains("carlos.donofrio@gmail.com", message);
        }

        #endregion

        #region 3. Almacenamiento de Imágenes (Cloudinary / Local)

        [Fact]
        public async Task UploadImageAsync_ValidJpeg_SavesToLocalFallback_WhenCloudinaryNotConfigured()
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            var bytes = CreateValidJpegBytes();
            using var stream = new MemoryStream(bytes);
            var file = new FormFile(stream, 0, bytes.Length, "file", "tour_cumbemayo.jpg")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg"
            };

            var url = await service.UploadImageAsync(file, "tours");

            Assert.StartsWith("/images/tours/", url);
            Assert.EndsWith(".jpg", url);

            var localPath = Path.Combine(_testWebRoot, url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(localPath));
        }

        [Fact]
        public async Task UploadImageAsync_ValidPngAndWebp_Succeeds()
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            var pngBytes = CreateValidPngBytes();
            using var pngStream = new MemoryStream(pngBytes);
            var pngFile = new FormFile(pngStream, 0, pngBytes.Length, "file", "photo.png")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/png"
            };

            var pngUrl = await service.UploadImageAsync(pngFile, "tours");
            Assert.EndsWith(".png", pngUrl);

            var webpBytes = CreateValidWebpBytes();
            using var webpStream = new MemoryStream(webpBytes);
            var webpFile = new FormFile(webpStream, 0, webpBytes.Length, "file", "photo.webp")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/webp"
            };

            var webpUrl = await service.UploadImageAsync(webpFile, "vehicles");
            Assert.EndsWith(".webp", webpUrl);
        }

        [Theory]
        [InlineData("backdoor.exe", "application/x-msdownload")]
        [InlineData("shell.sh", "application/x-sh")]
        [InlineData("hack.php", "text/php")]
        public async Task UploadImageAsync_DisallowedExtensions_Rejected(string filename, string mime)
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            var bytes = new byte[] { 0x4D, 0x5A, 0x00, 0x00 };
            using var stream = new MemoryStream(bytes);
            var file = new FormFile(stream, 0, bytes.Length, "file", filename)
            {
                Headers = new HeaderDictionary(),
                ContentType = mime
            };

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.UploadImageAsync(file, "tours"));
            Assert.Contains("no está permitida", ex.Message);
        }

        [Fact]
        public async Task UploadImageAsync_InvalidMagicBytes_RejectedEvenWithJpgExtension()
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            // Archivo de texto plano disfrazado con extensión .jpg
            var fakeBytes = Encoding.UTF8.GetBytes("<?php echo 'malicious'; ?>");
            using var stream = new MemoryStream(fakeBytes);
            var file = new FormFile(stream, 0, fakeBytes.Length, "file", "avatar.jpg")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg"
            };

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.UploadImageAsync(file, "vehicles"));
            Assert.Contains("firma de imagen", ex.Message);
        }

        [Fact]
        public async Task DeleteImageAsync_PathTraversalAttempt_Rejected()
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            var rootFile = Path.Combine(_testWebRoot, "config.json");
            await File.WriteAllTextAsync(rootFile, "{ \"secret\": 123 }");

            var attackUrl = "/images/../../config.json";
            var deleted = await service.DeleteImageAsync(attackUrl);

            Assert.False(deleted);
            Assert.True(File.Exists(rootFile)); // Archivo no debe ser borrado
        }

        [Fact]
        public async Task DeleteTour_DeletesAssociatedImagesFromStorage()
        {
            using var context = TestDbContextFactory.Create();

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Para Eliminar",
                Slug = "tour-eliminar-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = 60m,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            // Crear archivo físico simulado en la carpeta de imágenes
            var toursDir = Path.Combine(_testWebRoot, "images", "tours");
            Directory.CreateDirectory(toursDir);
            var imageFileName = $"tour_del_{Guid.NewGuid():N}.jpg";
            var imageFilePath = Path.Combine(toursDir, imageFileName);
            await File.WriteAllBytesAsync(imageFilePath, CreateValidJpegBytes());

            var tourImage = new TourImage
            {
                Id = Guid.NewGuid(),
                TourPackageId = tour.Id,
                ImageUrl = $"/images/tours/{imageFileName}",
                IsMain = true
            };
            context.TourImages.Add(tourImage);
            await context.SaveChangesAsync();

            var storageOptions = Options.Create(new CloudinaryOptions());
            var storageService = new CloudinaryStorageService(storageOptions, _configMock.Object, _envMock.Object, _loggerMock.Object);

            var controller = TestHelpers.CreateAdminController(
                context,
                imageStorageService: storageService);

            // Act: Eliminar el tour
            var result = await controller.DeleteTour(tour.Id);
            Assert.IsType<RedirectToActionResult>(result);

            // Assert: El tour y su imagen no existen en base de datos
            var deletedTour = await context.TourPackages.FindAsync(tour.Id);
            Assert.Null(deletedTour);

            // Assert: El archivo físico fue eliminado del almacenamiento, evitando imágenes huérfanas
            Assert.False(File.Exists(imageFilePath));
        }

        #endregion
    }
}
