using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Core.Enums;
using System;

namespace SIGETOUR.API.Infrastructure.Data.Configurations
{
    public class TourPackageConfiguration : IEntityTypeConfiguration<TourPackage>
    {
        public void Configure(EntityTypeBuilder<TourPackage> builder)
        {
            builder.HasKey(t => t.Id);
            builder.Property(t => t.Title).IsRequired().HasMaxLength(150);
            builder.Property(t => t.Subtitle).HasMaxLength(150);
            builder.Property(t => t.Description).IsRequired();
            builder.Property(t => t.Duration).HasMaxLength(100);
            builder.Property(t => t.BasePrice).HasColumnType("decimal(18,2)").HasDefaultValue(0m);
            builder.Property(t => t.ChildPrice).HasColumnType("decimal(18,2)").HasDefaultValue(0m);
            builder.Property(t => t.IsActive).HasDefaultValue(true);

            builder.HasMany(t => t.Images)
                   .WithOne(i => i.TourPackage)
                   .HasForeignKey(i => i.TourPackageId)
                   .OnDelete(DeleteBehavior.Cascade);

            builder.HasMany(t => t.Inclusions)
                   .WithOne(i => i.TourPackage)
                   .HasForeignKey(i => i.TourPackageId)
                   .OnDelete(DeleteBehavior.Cascade);

            builder.HasMany(t => t.Shifts)
                   .WithOne(s => s.TourPackage)
                   .HasForeignKey(s => s.TourPackageId)
                   .OnDelete(DeleteBehavior.Cascade);

            builder.HasMany(t => t.BoardingPoints)
                   .WithOne(b => b.TourPackage)
                   .HasForeignKey(b => b.TourPackageId)
                   .OnDelete(DeleteBehavior.Cascade);

            builder.HasMany(t => t.ItineraryStops)
                   .WithOne(i => i.TourPackage)
                   .HasForeignKey(i => i.TourPackageId)
                   .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(t => t.DefaultVehicle)
                   .WithMany()
                   .HasForeignKey(t => t.DefaultVehicleId)
                   .OnDelete(DeleteBehavior.SetNull);

            var pkg1Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var pkg2Id = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var pkg3Id = Guid.Parse("33333333-3333-3333-3333-333333333333");

            builder.HasData(
                new TourPackage
                {
                    Id = pkg1Id,
                    Title = "Ventanillas de Otuzco", Slug = "ventanillas-de-otuzco",
                    Subtitle = "Cementerio pre-inca",
                    Category = TourCategory.ArqueologiaYPetroglifos,
                    Modality = TourModality.Regular,
                    Difficulty = TourDifficulty.Facil,
                    Description = "Ventanillas de Otuzco (Cementerio de la Cultura Cajamarca con más de 3 mil años de antigüedad), Jardín de las Hortensias, Artesanía de Cajamarca, Fundo los Aples o 'Tres Molinos', fábrica artesanal de quesos, mantequilla, manjar blanco, Rosquitas de manteca etc.",
                    BasePrice = 30m,
                    ChildPrice = 20m,
                    MaxCapacity = 19,
                    RequiredAdvancePercentage = 50,
                    Duration = "4 Horas",
                    IsActive = true
                },
                new TourPackage
                {
                    Id = pkg2Id,
                    Title = "Collpa", Slug = "collpa",
                    Subtitle = "Llamado de Vacas por su nombre",
                    Category = TourCategory.TradicionYAgroturismo,
                    Modality = TourModality.Regular,
                    Difficulty = TourDifficulty.Facil,
                    Description = "Ex - hacienda La Collpa, Laguna Artificial, Casa Hacienda, Capilla de la Virgen del Carmen, Establo Central, Llamado de Vacas por su nombre, degustación de Lácteos; visita a las Cascadas de Llacanora según temporada o talleres de cerámica utilitaria y decorativa.",
                    BasePrice = 35m,
                    ChildPrice = 25m,
                    MaxCapacity = 19,
                    RequiredAdvancePercentage = 50,
                    Duration = "4 Horas",
                    IsActive = true
                },
                new TourPackage
                {
                    Id = pkg3Id,
                    Title = "Castillo de Yanamarca", Slug = "castillo-de-yanamarca",
                    Subtitle = "Estilo medieval",
                    Category = TourCategory.CityTour,
                    Modality = TourModality.Regular,
                    Difficulty = TourDifficulty.Facil,
                    Description = "El Castillo de Yanamarca es un moderno atractivo turístico con arquitectura estilo medieval en donde se puede apreciar, pinturas, esculturas de piedra y madera.",
                    BasePrice = 40m,
                    ChildPrice = 30m,
                    MaxCapacity = 15,
                    RequiredAdvancePercentage = 50,
                    Duration = "4 Horas",
                    IsActive = true
                }
            );
        }
    }
}



