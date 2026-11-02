using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Infrastructure.Data.Configurations;

public class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.HasIndex(b => b.ReferenceCode)
            .IsUnique();
    }
}
