using System;

namespace SIGETOUR.API.Core.Entities;

// Historial: una reserva puede tener varios pagos (adelanto y saldo).
public sealed class CulqiPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookingId { get; set; }
    public Booking Booking { get; set; } = null!;
    public long AmountCents { get; set; }
    public string Channel { get; set; } = ""; // charge | wallet_qr
    public string Status { get; set; } = "created"; // created | processing | verification | paid | failed
    public string? ProviderId { get; set; } // chr_test_... | ord_test_...
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ConfirmedAtUtc { get; set; }
}
