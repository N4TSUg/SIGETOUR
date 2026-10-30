using System;
using System.Threading;
using System.Threading.Tasks;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Application.Invoicing
{
    public interface IElectronicReceiptService
    {
        Task<ElectronicReceipt?> GetReceiptByBookingIdAsync(Guid bookingId, CancellationToken ct = default);
        Task<ElectronicReceipt> GenerateReceiptAsync(Guid bookingId, Guid? culqiPaymentId = null, CancellationToken ct = default);
    }
}
