using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SIGETOUR.API.Application.Options;
using SIGETOUR.API.Application.Services;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace SIGETOUR.Tests.Services
{
    public class CloudinaryStorageServiceTests : IDisposable
    {
        private readonly string _testWebRoot;
        private readonly Mock<IWebHostEnvironment> _envMock;
        private readonly Mock<ILogger<CloudinaryStorageService>> _loggerMock;
        private readonly Mock<IConfiguration> _configMock;

        public CloudinaryStorageServiceTests()
        {
            _testWebRoot = Path.Combine(Path.GetTempPath(), "sigetour_storage_tests_" + Guid.NewGuid().ToString("N"));
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

        [Fact]
        public void CloudinaryOptions_IsConfigured_ReturnsTrue_WhenAllCredentialsPresent()
        {
            var options = new CloudinaryOptions
            {
                CloudName = "turismochilon",
                ApiKey = "123456789",
                ApiSecret = "secret_key_abc"
            };

            Assert.True(options.IsConfigured);
        }

        [Theory]
        [InlineData(null, "123", "secret")]
        [InlineData("cloud", null, "secret")]
        [InlineData("cloud", "123", null)]
        [InlineData("", "123", "secret")]
        [InlineData("cloud", "   ", "secret")]
        public void CloudinaryOptions_IsConfigured_ReturnsFalse_WhenAnyCredentialMissing(string? cloud, string? key, string? secret)
        {
            var options = new CloudinaryOptions
            {
                CloudName = cloud,
                ApiKey = key,
                ApiSecret = secret
            };

            Assert.False(options.IsConfigured);
        }

        [Theory]
        [InlineData("https://res.cloudinary.com/turismochilon/image/upload/v1730928000/sigetour/vehicles/van_hiace_1.jpg", "sigetour/vehicles/van_hiace_1")]
        [InlineData("https://res.cloudinary.com/turismochilon/image/upload/q_auto,f_auto/v1730928000/sigetour/tours/porcon_1.webp", "sigetour/tours/porcon_1")]
        [InlineData("https://res.cloudinary.com/demo/image/upload/c_fill,w_300,h_200/v12345/sigetour/general/foto.png", "sigetour/general/foto")]
        [InlineData("https://res.cloudinary.com/turismochilon/image/upload/v1/sample.jpg", "sample")]
        public void ExtractPublicIdFromCloudinaryUrl_ValidUrls_ExtractsCorrectPublicId(string url, string expectedPublicId)
        {
            var result = CloudinaryStorageService.ExtractPublicIdFromCloudinaryUrl(url);

            Assert.Equal(expectedPublicId, result);
        }

        [Theory]
        [InlineData("https://example.com/images/vehicles/van.jpg")]
        [InlineData("/images/vehicles/local.jpg")]
        [InlineData("not_a_valid_url")]
        [InlineData("")]
        public void ExtractPublicIdFromCloudinaryUrl_InvalidUrls_ReturnsNull(string invalidUrl)
        {
            var result = CloudinaryStorageService.ExtractPublicIdFromCloudinaryUrl(invalidUrl);

            Assert.Null(result);
        }

        private static byte[] CreateValidJpegBytes()
        {
            // JPEG JFIF standard header magic bytes
            return new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48 };
        }

        private static byte[] CreateValidPngBytes()
        {
            // PNG standard header magic bytes
            return new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        }

        [Fact]
        public async Task UploadImageAsync_WithoutCloudinaryCredentials_FallsBackToLocalStorage()
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            var bytes = CreateValidJpegBytes();
            using var stream = new MemoryStream(bytes);
            var formFile = new FormFile(stream, 0, bytes.Length, "VehicleImages", "test_van.jpg")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg"
            };

            var url = await service.UploadImageAsync(formFile, "vehicles");

            Assert.StartsWith("/images/vehicles/", url);
            Assert.EndsWith(".jpg", url);

            var relativePath = url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var savedFile = Path.Combine(_testWebRoot, relativePath);
            Assert.True(File.Exists(savedFile));
        }

        [Theory]
        [InlineData("malicious.exe", "application/x-msdownload")]
        [InlineData("script.sh", "application/x-sh")]
        [InlineData("exploit.php", "text/php")]
        [InlineData("document.pdf", "application/pdf")]
        public async Task UploadImageAsync_DisallowedExtension_ThrowsArgumentException(string fileName, string contentType)
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            var bytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00 }; // DOS MZ header
            using var stream = new MemoryStream(bytes);
            var formFile = new FormFile(stream, 0, bytes.Length, "file", fileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = contentType
            };

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.UploadImageAsync(formFile, "tours"));
            Assert.Contains("no está permitida", ex.Message);
        }

        [Fact]
        public async Task UploadImageAsync_InvalidMagicBytes_ThrowsArgumentException()
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            var fakeBytes = Encoding.UTF8.GetBytes("not-a-real-jpeg-just-plain-text");
            using var stream = new MemoryStream(fakeBytes);
            var formFile = new FormFile(stream, 0, fakeBytes.Length, "file", "fake.jpg")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg"
            };

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.UploadImageAsync(formFile, "tours"));
            Assert.Contains("firma de imagen", ex.Message);
        }

        [Fact]
        public async Task UploadImageAsync_Exceeding5MB_ThrowsArgumentException()
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            var header = CreateValidJpegBytes();
            var oversizedStream = new MemoryStream();
            oversizedStream.Write(header, 0, header.Length);
            oversizedStream.SetLength(6 * 1024 * 1024); // 6 MB
            oversizedStream.Position = 0;

            var formFile = new FormFile(oversizedStream, 0, oversizedStream.Length, "file", "large.jpg")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg"
            };

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.UploadImageAsync(formFile, "tours"));
            Assert.Contains("excede el tamaño máximo permitido", ex.Message);
        }

        [Fact]
        public async Task DeleteImageAsync_WithLocalFile_DeletesFileSuccessfully()
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            var vehiclesDir = Path.Combine(_testWebRoot, "images", "vehicles");
            Directory.CreateDirectory(vehiclesDir);
            var filePath = Path.Combine(vehiclesDir, "temp_to_delete.jpg");
            await File.WriteAllTextAsync(filePath, "dummy");

            var localUrl = "/images/vehicles/temp_to_delete.jpg";
            var result = await service.DeleteImageAsync(localUrl);

            Assert.True(result);
            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public async Task DeleteImageAsync_PathTraversal_RejectedAndReturnsFalse()
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            // Intentar borrar un archivo fuera de /images
            var secretFile = Path.Combine(_testWebRoot, "secret.txt");
            await File.WriteAllTextAsync(secretFile, "sensitive data");

            var traversalUrl = "/images/../../secret.txt";
            var result = await service.DeleteImageAsync(traversalUrl);

            Assert.False(result);
            Assert.True(File.Exists(secretFile)); // Archivo protegido
        }

        [Fact]
        public async Task DeleteImageAsync_WithInvalidHost_ReturnsFalse()
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            // Intentar engañar al eliminador con un host ajeno a Cloudinary
            var maliciousUrl = "https://evil-attacker.com/image/upload/sample.jpg";
            var result = await service.DeleteImageAsync(maliciousUrl);

            Assert.False(result);
        }

        [Fact]
        public async Task DeleteImageAsync_WithEmptyUrl_ReturnsFalse()
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            var result = await service.DeleteImageAsync("");

            Assert.False(result);
        }
    }
}
