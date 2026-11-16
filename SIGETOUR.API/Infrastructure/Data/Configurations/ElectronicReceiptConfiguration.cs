using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Infrastructure.Data.Configurations
{
    public class ElectronicReceiptConfiguration : IEntityTypeConfiguration<ElectronicReceipt>
    {
        public void Configure(EntityTypeBuilder<ElectronicReceipt> builder)
        {
            builder.ToTable("ElectronicReceipts");
            builder.HasKey(r => r.Id);

            builder.Property(r => r.ReceiptType).HasMaxLength(20).IsRequired();
            builder.Property(r => r.Series).HasMaxLength(10).IsRequired();
            builder.Property(r => r.Number).HasMaxLength(20).IsRequired();
            builder.Property(r => r.CustomerDocType).HasMaxLength(10).IsRequired();
            builder.Property(r => r.CustomerDocNumber).HasMaxLength(20).IsRequired();
            builder.Property(r => r.CustomerName).HasMaxLength(200).IsRequired();
            builder.Property(r => r.CustomerEmail).HasMaxLength(150);
            builder.Property(r => r.Status).HasMaxLength(30).IsRequired();
            builder.Property(r => r.Environment).HasMaxLength(30).HasDefaultValue("Demo").IsRequired();
            builder.Property(r => r.SnapshotJson).HasColumnType("text");
            builder.Property(r => r.EmissionLeaseToken).HasMaxLength(64);
            builder.Property(r => r.EmissionLeaseAcquiredAtUtc);
            builder.Property(r => r.EmissionLeaseExpiresAtUtc);
            builder.Property(r => r.EmissionStartedAtUtc);
            builder.Property(r => r.BillableOperationKey).HasMaxLength(128);

            builder.Property(r => r.SubtotalAmount).HasPrecision(12, 2);
            builder.Property(r => r.IgvAmount).HasPrecision(12, 2);
            builder.Property(r => r.TotalAmount).HasPrecision(12, 2);

            builder.HasIndex(r => new { r.Series, r.Number, r.ReceiptType })
                .IsUnique()
                .HasFilter("\"Number\" IS NOT NULL AND \"Number\" <> ''");
            builder.HasIndex(r => r.BookingId);
            builder.HasIndex(r => r.CulqiPaymentId)
                .IsUnique()
                .HasFilter("\"CulqiPaymentId\" IS NOT NULL");
            builder.HasIndex(r => r.CashTransactionId)
                .IsUnique()
                .HasFilter("\"CashTransactionId\" IS NOT NULL");
            builder.HasIndex(r => r.BillableOperationKey)
                .IsUnique()
                .HasFilter("\"BillableOperationKey\" IS NOT NULL");

            builder.HasOne(r => r.Booking)
                .WithMany()
                .HasForeignKey(r => r.BookingId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(r => r.CulqiPayment)
                .WithMany()
                .HasForeignKey(r => r.CulqiPaymentId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.HasOne(r => r.CashTransaction)
                .WithMany()
                .HasForeignKey(r => r.CashTransactionId)
                .OnDelete(DeleteBehavior.SetNull);
        }
    }
}
