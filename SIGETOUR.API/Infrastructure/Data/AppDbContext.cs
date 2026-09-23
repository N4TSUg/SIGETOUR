using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Infrastructure.Data
{
    public class AppDbContext : IdentityDbContext<ApplicationUser>
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<TourPackage> TourPackages { get; set; }
        public DbSet<TourImage> TourImages { get; set; }
        public DbSet<TourInclusion> TourInclusions { get; set; }
        public DbSet<TourShift> TourShifts { get; set; }
        public DbSet<BoardingPoint> BoardingPoints { get; set; }
        public DbSet<ItineraryStop> ItineraryStops { get; set; }
        public DbSet<Vehicle> Vehicles { get; set; }
        public DbSet<Booking> Bookings { get; set; }
        public DbSet<BookingItem> BookingItems { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            
            // Aplicar configuraciones de entidades
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        }
    }
}

