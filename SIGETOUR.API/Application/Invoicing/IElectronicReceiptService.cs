using System;
using System.Threading;
using System.Threading.Tasks;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Application.Invoicing
{
    public enum NubefactReconciliationStatus
    {
        Confirmed,             // Aceptado en SUNAT o Demo
        PendingResponse,       // Enviado a SUNAT pero aún en procesamiento
        ConfirmedRejection,    // Rechazo formal emitido por SUNAT
        ConfirmedNonExistent,  // El proveedor confirma que el comprobante no existe en su sistema
        Unknown                // Fallo de red, timeout, HTTP 5xx, fallo de autenticación o respuesta inconclusa
    }

    public sealed record NubefactReconciliationResult(
        NubefactReconciliationStatus Status,
        ElectronicReceipt Receipt,
        string? Details = null);

    public interface IElectronicReceiptService
    {
        Task<ElectronicReceipt?> GetReceiptByIdAsync(Guid receiptId, CancellationToken ct = default);
        Task<ElectronicReceipt?> GetReceiptByBookingIdAsync(Guid bookingId, CancellationToken ct = default);
        Task<ElectronicReceipt> GenerateReceiptAsync(Guid bookingId, Guid? culqiPaymentId = null, CancellationToken ct = default);
        Task<ElectronicReceipt> GenerateReceiptAsync(Guid bookingId, Guid? culqiPaymentId, Guid? exactReceiptId, CancellationToken ct = default);
        Task<ElectronicReceipt> GenerateReceiptAsync(Guid bookingId, Guid? culqiPaymentId, Guid? exactReceiptId, string? claimToken, CancellationToken ct = default);
        Task<ElectronicReceipt> GenerateReceiptAsync(Guid bookingId, Guid? culqiPaymentId, Guid? exactReceiptId, string? claimToken, Guid? cashTransactionId, string? billableOperationKey = null, CancellationToken ct = default);
        Task<ElectronicReceipt> RetryReceiptAsync(Guid receiptId, CancellationToken ct = default);
        Task<ElectronicReceipt?> QueryReceiptFromProviderAsync(Guid receiptId, CancellationToken ct = default);
        Task<NubefactReconciliationResult> ReconcileReceiptWithProviderAsync(Guid receiptId, CancellationToken ct = default);
        Task<(bool Acquired, string LeaseToken, ElectronicReceipt Receipt)> TryAcquireEmissionAsync(Guid receiptId, string? requestedClaimToken = null, bool allowRejectedRetry = false, CancellationToken ct = default);
        Task<bool> TryMarkEmissionStartedAsync(Guid receiptId, string leaseToken, CancellationToken ct = default);
        Task<bool> TryRecoverUnsentInFlightAsync(Guid receiptId, string? observedLeaseToken, CancellationToken ct = default);
        Task<bool> TryTransitionInFlightToReviewAsync(Guid receiptId, string? observedLeaseToken, string reason, CancellationToken ct = default);
        Task<bool> TryCompleteEmissionAsync(
            Guid receiptId,
            string leaseToken,
            string finalStatus,
            string? number,
            string? pdfUrl,
            string? xmlUrl,
            string? cdrUrl,
            string? qrData,
            string? digitalHash,
            string? providerDescription,
            string? providerResponseCode,
            string? lastError,
            DateTime? issuedAtUtc,
            CancellationToken ct = default);
        Task<int> ProcessPendingReceiptsAsync(CancellationToken ct = default);
    }
}
