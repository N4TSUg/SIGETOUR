using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Infrastructure.Data.Configurations;

public sealed class CulqiPaymentConfiguration : IEntityTypeConfiguration<CulqiPayment>
{
    public void Configure(EntityTypeBuilder<CulqiPayment> entity)
    {
        entity.HasKey(p => p.Id);
        entity.Property(p => p.Status).HasMaxLength(32).IsRequired();
        entity.Property(p => p.Channel).HasMaxLength(32).IsRequired();
        entity.Property(p => p.ProviderId).HasMaxLength(100);
        entity.HasIndex(p => p.ProviderId).IsUnique();
        // Solo un intento no resuelto por reserva. Impide cobro simultáneo en varios nodos.
        entity.HasIndex(p => p.BookingId).IsUnique()
            .HasFilter("\"Status\" IN ('created', 'processing', 'verification')");
        entity.HasIndex(p => new { p.BookingId, p.CreatedAtUtc });
        entity.HasOne(p => p.Booking).WithMany().HasForeignKey(p => p.BookingId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.ToTable(t => t.HasCheckConstraint("CK_CulqiPayment_Positive", "\"AmountCents\" > 0"));
    }
}
