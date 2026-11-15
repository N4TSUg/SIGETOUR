using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIGETOUR.API.Application.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SIGETOUR.API.Application.Services
{
    public class CloudinaryStorageService : IImageStorageService
    {
        private readonly Cloudinary? _cloudinary;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<CloudinaryStorageService> _logger;
        private readonly bool _useCloudinary;

        public CloudinaryStorageService(
            IOptions<CloudinaryOptions> options,
            IConfiguration configuration,
            IWebHostEnvironment env,
            ILogger<CloudinaryStorageService> logger)
        {
            _env = env;
            _logger = logger;

            // 1. Intentar configurar desde variable CLOUDINARY_URL estándar
            var cloudinaryUrl = Environment.GetEnvironmentVariable("CLOUDINARY_URL")
                ?? configuration["CLOUDINARY_URL"]
                ?? configuration["Cloudinary:Url"];

            if (!string.IsNullOrWhiteSpace(cloudinaryUrl))
            {
                try
                {
                    _cloudinary = new Cloudinary(cloudinaryUrl);
                    _cloudinary.Api.Secure = true;
                    _useCloudinary = true;
                    _logger.LogInformation("Cloudinary inicializado exitosamente desde CLOUDINARY_URL.");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo al inicializar Cloudinary desde CLOUDINARY_URL. Se probarán opciones individuales.");
                }
            }

            // 2. Intentar configurar desde opciones individuales (CloudName, ApiKey, ApiSecret)
            var opt = options?.Value;
            var cloudName = opt?.CloudName ?? configuration["Cloudinary:CloudName"] ?? Environment.GetEnvironmentVariable("Cloudinary__CloudName");
            var apiKey = opt?.ApiKey ?? configuration["Cloudinary:ApiKey"] ?? Environment.GetEnvironmentVariable("Cloudinary__ApiKey");
            var apiSecret = opt?.ApiSecret ?? configuration["Cloudinary:ApiSecret"] ?? Environment.GetEnvironmentVariable("Cloudinary__ApiSecret");

            if (!string.IsNullOrWhiteSpace(cloudName) && !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
            {
                try
                {
                    var account = new Account(cloudName, apiKey, apiSecret);
                    _cloudinary = new Cloudinary(account);
                    _cloudinary.Api.Secure = true;
                    _useCloudinary = true;
                    _logger.LogInformation("Cloudinary inicializado exitosamente para CloudName: {CloudName}", cloudName);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fallo al inicializar Cloudinary con credenciales individuales.");
                }
            }

            // 3. Fallback: Almacenamiento local si no hay credenciales configuradas
            _useCloudinary = false;
            _logger.LogWarning("Cloudinary no configurado (CLOUDINARY_URL o CloudName/ApiKey/ApiSecret no provistos). Se utilizará almacenamiento local en wwwroot como fallback.");
        }

        public static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp"
        };

        public static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg", "image/pjpeg", "image/png", "image/x-png", "image/webp"
        };

        public const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

        public static bool ValidateImageFile(IFormFile file, out string? errorMessage)
        {
            if (file == null || file.Length == 0)
            {
                errorMessage = "El archivo proporcionado está vacío.";
                return false;
            }

            if (file.Length > MaxFileSizeBytes)
            {
                errorMessage = $"El archivo excede el tamaño máximo permitido de 5 MB ({file.Length / (1024.0 * 1024.0):F2} MB).";
                return false;
            }

            var extension = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
            {
                errorMessage = $"La extensión de archivo '{extension}' no está permitida. Formatos aceptados: JPG, PNG, WEBP.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(file.ContentType) && !AllowedMimeTypes.Contains(file.ContentType))
            {
                errorMessage = $"El tipo de contenido MIME '{file.ContentType}' no corresponde a una imagen permitida (image/jpeg, image/png, image/webp).";
                return false;
            }

            // Validación de magic bytes en el stream (firmas JPEG, PNG o WEBP)
            try
            {
                using var stream = file.OpenReadStream();
                if (stream.Length < 3)
                {
                    errorMessage = "El contenido del archivo es insuficiente para verificar la firma de imagen.";
                    return false;
                }

                var header = new byte[Math.Min(12, (int)stream.Length)];
                var read = stream.Read(header, 0, header.Length);

                bool isJpeg = read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
                bool isPng = read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
                             header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;
                bool isWebp = read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                              header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50;

                if (!isJpeg && !isPng && !isWebp)
                {
                    errorMessage = "El contenido del archivo no corresponde a una firma de imagen válida (JPEG, PNG o WEBP).";
                    return false;
                }
            }
            catch (Exception ex)
            {
                errorMessage = $"Error al leer el archivo para validación: {ex.Message}";
                return false;
            }

            errorMessage = null;
            return true;
        }

        public async Task<string> UploadImageAsync(IFormFile file, string folder, string? customFileName = null)
        {
            if (!ValidateImageFile(file, out var validationError))
            {
                _logger.LogWarning("[CloudinaryStorageService] Archivo rechazado por validación de seguridad: {Error}", validationError);
                throw new ArgumentException(validationError, nameof(file));
            }

            var cleanFolder = Regex.Replace(folder ?? "general", @"[^a-zA-Z0-9_\-]", "").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(cleanFolder)) cleanFolder = "general";

            string? cleanCustomName = null;
            if (!string.IsNullOrWhiteSpace(customFileName))
            {
                var withoutExt = Path.GetFileNameWithoutExtension(customFileName);
                cleanCustomName = Regex.Replace(withoutExt, @"[^a-zA-Z0-9_\-]", "");
            }

            // Si Cloudinary está activo, subir directamente a la nube
            if (_useCloudinary && _cloudinary != null)
            {
                try
                {
                    using var stream = file.OpenReadStream();

                    var uploadParams = new ImageUploadParams
                    {
                        File = new FileDescription(file.FileName, stream),
                        Folder = $"sigetour/{cleanFolder}",
                        UseFilename = false,
                        UniqueFilename = true,
                        Overwrite = false,
                        Transformation = new Transformation().Quality("auto").FetchFormat("auto")
                    };

                    if (!string.IsNullOrWhiteSpace(cleanCustomName))
                    {
                        uploadParams.PublicId = cleanCustomName;
                    }

                    var uploadResult = await _cloudinary.UploadAsync(uploadParams);

                    if (uploadResult.Error != null)
                    {
                        _logger.LogError("Error de respuesta de Cloudinary: {Error}", uploadResult.Error.Message);
                        throw new InvalidOperationException($"Cloudinary Error: {uploadResult.Error.Message}");
                    }

                    var secureUrl = uploadResult.SecureUrl?.ToString() ?? uploadResult.Url?.ToString();
                    if (!string.IsNullOrEmpty(secureUrl))
                    {
                        _logger.LogInformation("Imagen subida a Cloudinary exitosamente: {Url}", secureUrl);
                        return secureUrl;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Excepción al subir imagen a Cloudinary. Intentando fallback local controlado.");
                    // Fallback transparente a almacenamiento local ante cortes de red o credenciales inválidas
                }
            }

            // Fallback local seguro en wwwroot/images/{cleanFolder}
            var basePath = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var localUploadsDir = Path.Combine(basePath, "images", cleanFolder);
            
            if (!Directory.Exists(localUploadsDir))
            {
                Directory.CreateDirectory(localUploadsDir);
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var finalFileName = string.IsNullOrWhiteSpace(cleanCustomName)
                ? $"{Guid.NewGuid():N}{extension}"
                : $"{cleanCustomName}_{Guid.NewGuid():N}{extension}";

            var localFilePath = Path.Combine(localUploadsDir, finalFileName);
            using (var localStream = new FileStream(localFilePath, FileMode.Create))
            {
                await file.CopyToAsync(localStream);
            }

            var relativeUrl = $"/images/{cleanFolder}/{finalFileName}";
            _logger.LogInformation("Imagen guardada localmente: {Url}", relativeUrl);
            return relativeUrl;
        }

        public async Task<bool> DeleteImageAsync(string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return false;
            }

            // 1. Si la URL pertenece a Cloudinary
            if (imageUrl.Contains("cloudinary.com", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
                    (!uri.Host.EndsWith("cloudinary.com", StringComparison.OrdinalIgnoreCase) ||
                     (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
                {
                    _logger.LogWarning("[Security] URL de Cloudinary inválida o maliciosa: {Url}", imageUrl);
                    return false;
                }

                if (_useCloudinary && _cloudinary != null)
                {
                    try
                    {
                        var publicId = ExtractPublicIdFromCloudinaryUrl(imageUrl);
                        if (!string.IsNullOrEmpty(publicId))
                        {
                            var deleteParams = new DeletionParams(publicId);
                            var result = await _cloudinary.DestroyAsync(deleteParams);
                            _logger.LogInformation("Resultado de eliminación en Cloudinary para {PublicId}: {Result}", publicId, result.Result);
                            return result.Result == "ok";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error al eliminar imagen de Cloudinary: {Url}", imageUrl);
                    }
                }
                return false;
            }

            // 2. Si es una ruta local en wwwroot (/images/...)
            try
            {
                var basePath = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                var allowedImagesDir = Path.GetFullPath(Path.Combine(basePath, "images"));

                var normalizedRelative = imageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var fullLocalPath = Path.GetFullPath(Path.Combine(basePath, normalizedRelative));

                // Protección estricta contra Path Traversal: debe residir dentro de wwwroot/images
                if (!fullLocalPath.StartsWith(allowedImagesDir, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[Security] Intento de eliminación fuera del directorio permitido de imágenes: {Path}", fullLocalPath);
                    return false;
                }

                if (File.Exists(fullLocalPath))
                {
                    File.Delete(fullLocalPath);
                    _logger.LogInformation("Archivo local eliminado exitosamente: {Path}", fullLocalPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo eliminar el archivo local: {Url}", imageUrl);
            }

            return false;
        }

        /// <summary>
        /// Extrae el public_id con sus carpetas (ej. "sigetour/vehicles/abcdef123") a partir de una URL completa de Cloudinary.
        /// </summary>
        public static string? ExtractPublicIdFromCloudinaryUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                var path = uri.AbsolutePath;
                var uploadMarker = "/upload/";
                var uploadIndex = path.IndexOf(uploadMarker, StringComparison.OrdinalIgnoreCase);
                if (uploadIndex == -1) return null;

                var afterUpload = path.Substring(uploadIndex + uploadMarker.Length);
                var segments = afterUpload.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var publicIdParts = new List<string>();
                bool foundVersionOrPrefix = false;

                for (int i = 0; i < segments.Length; i++)
                {
                    var seg = segments[i];

                    // Ignorar transformaciones (ej. "q_auto,f_auto", "c_fill,w_300")
                    if (!foundVersionOrPrefix && (seg.Contains(',') || seg.StartsWith("c_") || seg.StartsWith("w_") || seg.StartsWith("h_")))
                    {
                        continue;
                    }

                    // Ignorar número de versión de Cloudinary (ej. "v1712345678")
                    if (!foundVersionOrPrefix && Regex.IsMatch(seg, @"^v\d+$"))
                    {
                        foundVersionOrPrefix = true;
                        continue;
                    }

                    // Todos los segmentos a partir de aquí forman el public_id
                    publicIdParts.Add(seg);
                }

                if (publicIdParts.Count == 0) return null;

                var rawPublicId = string.Join("/", publicIdParts);
                var lastDotIndex = rawPublicId.LastIndexOf('.');
                return lastDotIndex != -1 ? rawPublicId.Substring(0, lastDotIndex) : rawPublicId;
            }
            catch
            {
                return null;
            }
        }
    }
}
