using System;
using System.Threading.Tasks;

namespace SIGETOUR.API.Application.Interfaces
{
    public interface ICashReportExportService
    {
        /// <summary>
        /// Genera el reporte consolidado diario de movimientos de caja, arqueo y caja chica en formato Excel (.xlsx).
        /// </summary>
        Task<byte[]> GenerateDailyCashReportExcelAsync(DateTime date);

        /// <summary>
        /// Genera el reporte consolidado mensual de movimientos de caja, flujo día a día y balance en formato Excel (.xlsx).
        /// </summary>
        Task<byte[]> GenerateMonthlyCashReportExcelAsync(int year, int month);

        /// <summary>
        /// Genera el reporte detallado y acta de arqueo de un turno específico en formato Excel (.xlsx).
        /// </summary>
        Task<byte[]> GenerateShiftReportExcelAsync(Guid shiftId);
    }
}
