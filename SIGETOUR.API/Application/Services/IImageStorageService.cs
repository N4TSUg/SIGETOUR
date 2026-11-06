using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

namespace SIGETOUR.API.Application.Services
{
    public interface IImageStorageService
    {
        /// <summary>
        /// Sube una imagen a Cloudinary (o almacenamiento local si Cloudinary no está configurado).
        /// Retorna la URL pública segura de acceso a la imagen.
        /// </summary>
        /// <param name="file">Archivo de imagen recibido del formulario.</param>
        /// <param name="folder">Subcarpeta lógica de destino (ej: "vehicles", "tours").</param>
        /// <param name="customFileName">Nombre personalizado opcional.</param>
        Task<string> UploadImageAsync(IFormFile file, string folder, string? customFileName = null);

        /// <summary>
        /// Elimina una imagen de Cloudinary o del almacenamiento local según el origen de la URL.
        /// </summary>
        /// <param name="imageUrl">URL completa de la imagen guardada en base de datos.</param>
        Task<bool> DeleteImageAsync(string imageUrl);
    }
}
