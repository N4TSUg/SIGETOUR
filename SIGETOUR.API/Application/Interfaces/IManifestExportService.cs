using System;
using System.Threading.Tasks;
using SIGETOUR.API.Models;

namespace SIGETOUR.API.Application.Interfaces
{
    public interface IManifestExportService
    {
        Task<byte[]> GenerateManifestExcelAsync(ManifestExportRequest request);
        Task<ManifestPreviewDto> GetManifestPreviewAsync(DateTime date, Guid tourId, Guid? vehicleId, string? shift);
    }
}
