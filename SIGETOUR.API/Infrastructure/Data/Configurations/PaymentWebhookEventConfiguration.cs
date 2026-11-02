using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Infrastructure.Data.Configurations;

public class PaymentWebhookEventConfiguration : IEntityTypeConfiguration<PaymentWebhookEvent>
{
    public void Configure(EntityTypeBuilder<PaymentWebhookEvent> builder)
    {
        builder.ToTable("PaymentWebhookEvents");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.ProviderId).HasMaxLength(128).IsRequired();
        builder.Property(e => e.EventType).HasMaxLength(64);
        builder.Property(e => e.OrderState).HasMaxLength(32);
        builder.Property(e => e.RawPayload).HasColumnType("text");

        builder.HasIndex(e => e.ProviderId);
        builder.HasIndex(e => e.Processed);
    }
}
