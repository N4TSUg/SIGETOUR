using System;

namespace SIGETOUR.API.Core.Entities;

public sealed class PaymentWebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderId { get; set; } = string.Empty;
    public string? EventType { get; set; }
    public string? OrderState { get; set; }
    public string RawPayload { get; set; } = string.Empty;
    public bool Processed { get; set; } = false;
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
}
