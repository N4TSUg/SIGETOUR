using System;

namespace SIGETOUR.API.Core.Entities;

public sealed class PaymentIncident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IncidentType { get; set; } = string.Empty; // CancelledBookingPayment | Overpayment | AmbiguousChargeMatch | OrphanProviderCharge
    public string Severity { get; set; } = "Warning"; // Info | Warning | Critical
    public Guid? BookingId { get; set; }
    public Booking? Booking { get; set; }
    public Guid? CulqiPaymentId { get; set; }
    public CulqiPayment? CulqiPayment { get; set; }
    public string? ProviderId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "PEN";
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "PendingReview"; // PendingReview | Resolved | Refunded | Dismissed
    public string? ResolutionNotes { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; set; }
}
