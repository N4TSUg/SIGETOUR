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

        public async Task<string> UploadImageAsync(IFormFile file, string folder, string? customFileName = null)
        {
            if (file == null || file.Length == 0)
            {
                throw new ArgumentException("El archivo proporcionado está vacío.", nameof(file));
            }

            // Si Cloudinary está activo, subir directamente a la nube
            if (_useCloudinary && _cloudinary != null)
            {
                try
                {
                    using var stream = file.OpenReadStream();
                    var cleanFolder = string.IsNullOrWhiteSpace(folder) ? "general" : folder.Trim('/').ToLowerInvariant();

                    var uploadParams = new ImageUploadParams
                    {
                        File = new FileDescription(file.FileName, stream),
                        Folder = $"sigetour/{cleanFolder}",
                        UseFilename = false,
                        UniqueFilename = true,
                        Overwrite = false,
                        Transformation = new Transformation().Quality("auto").FetchFormat("auto")
                    };

                    if (!string.IsNullOrWhiteSpace(customFileName))
                    {
                        uploadParams.PublicId = Path.GetFileNameWithoutExtension(customFileName);
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
                    _logger.LogError(ex, "Excepción al subir imagen a Cloudinary. Intentando fallback local.");
                    // Si falla la subida a Cloudinary (ej. caída de red), intentar fallback local para no bloquear la operación
                }
            }

            // Fallback: Guardar en el disco local de wwwroot
            var cleanLocalFolder = string.IsNullOrWhiteSpace(folder) ? "general" : folder.Trim('/');
            var localUploadsDir = Path.Combine(_env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "images", cleanLocalFolder);
            
            if (!Directory.Exists(localUploadsDir))
            {
                Directory.CreateDirectory(localUploadsDir);
            }

            var extension = Path.GetExtension(file.FileName);
            var finalFileName = string.IsNullOrWhiteSpace(customFileName)
                ? $"{Guid.NewGuid():N}{extension}"
                : (customFileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? customFileName : $"{customFileName}{extension}");

            var localFilePath = Path.Combine(localUploadsDir, finalFileName);
            using (var localStream = new FileStream(localFilePath, FileMode.Create))
            {
                await file.CopyToAsync(localStream);
            }

            var relativeUrl = $"/images/{cleanLocalFolder}/{finalFileName}";
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
            if (imageUrl.Contains("res.cloudinary.com", StringComparison.OrdinalIgnoreCase))
            {
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
                var relative = imageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var basePath = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                var fullLocalPath = Path.Combine(basePath, relative);

                if (File.Exists(fullLocalPath))
                {
                    File.Delete(fullLocalPath);
                    _logger.LogInformation("Archivo local eliminado: {Path}", fullLocalPath);
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
