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

        [Fact]
        public async Task UploadImageAsync_WithoutCloudinaryCredentials_FallsBackToLocalStorage()
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            var content = "dummy-image-bytes";
            var bytes = Encoding.UTF8.GetBytes(content);
            using var stream = new MemoryStream(bytes);
            var formFile = new FormFile(stream, 0, bytes.Length, "VehicleImages", "test_van.jpg");

            var url = await service.UploadImageAsync(formFile, "vehicles");

            Assert.StartsWith("/images/vehicles/", url);
            Assert.EndsWith(".jpg", url);

            var relativePath = url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var savedFile = Path.Combine(_testWebRoot, relativePath);
            Assert.True(File.Exists(savedFile));
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
        public async Task DeleteImageAsync_WithEmptyUrl_ReturnsFalse()
        {
            var options = Options.Create(new CloudinaryOptions());
            var service = new CloudinaryStorageService(options, _configMock.Object, _envMock.Object, _loggerMock.Object);

            var result = await service.DeleteImageAsync("");

            Assert.False(result);
        }
    }
}
