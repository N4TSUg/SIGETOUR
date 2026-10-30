using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Infrastructure.Data.Configurations
{
    public sealed class CashTransactionConfiguration : IEntityTypeConfiguration<CashTransaction>
    {
        public void Configure(EntityTypeBuilder<CashTransaction> builder)
        {
            builder.HasOne(t => t.CulqiPayment)
                .WithMany()
                .HasForeignKey(t => t.CulqiPaymentId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.HasIndex(t => t.CulqiPaymentId)
                .IsUnique()
                .HasFilter("\"CulqiPaymentId\" IS NOT NULL");
        }
    }
}
