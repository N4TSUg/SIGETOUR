using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Infrastructure.Data.Configurations;

public class PaymentIncidentConfiguration : IEntityTypeConfiguration<PaymentIncident>
{
    public void Configure(EntityTypeBuilder<PaymentIncident> builder)
    {
        builder.ToTable("PaymentIncidents");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.IncidentType).HasMaxLength(64).IsRequired();
        builder.Property(i => i.Severity).HasMaxLength(32).IsRequired();
        builder.Property(i => i.Currency).HasMaxLength(8).HasDefaultValue("PEN").IsRequired();
        builder.Property(i => i.Amount).HasPrecision(12, 2);
        builder.Property(i => i.Status).HasMaxLength(32).HasDefaultValue("PendingReview").IsRequired();
        builder.Property(i => i.ProviderId).HasMaxLength(128);

        builder.HasIndex(i => i.BookingId);
        builder.HasIndex(i => i.CulqiPaymentId);
        builder.HasIndex(i => i.Status);

        builder.HasOne(i => i.Booking)
            .WithMany()
            .HasForeignKey(i => i.BookingId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(i => i.CulqiPayment)
            .WithMany()
            .HasForeignKey(i => i.CulqiPaymentId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
