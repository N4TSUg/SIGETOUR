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
        public DbSet<FeaturedPackage> FeaturedPackages { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<TourImage> TourImages { get; set; }
        public DbSet<TourInclusion> TourInclusions { get; set; }
        public DbSet<TourShift> TourShifts { get; set; }
        public DbSet<BoardingPoint> BoardingPoints { get; set; }
        public DbSet<ItineraryStop> ItineraryStops { get; set; }
        public DbSet<Vehicle> Vehicles { get; set; }
        public DbSet<VehicleImage> VehicleImages { get; set; }
        public DbSet<Booking> Bookings { get; set; }
        public DbSet<BookingItem> BookingItems { get; set; }
        public DbSet<CashRegisterShift> CashRegisterShifts { get; set; }
        public DbSet<CashTransaction> CashTransactions { get; set; }
        public DbSet<PettyCashAccount> PettyCashAccounts { get; set; }
        public DbSet<PettyCashMovement> PettyCashMovements { get; set; }
        public DbSet<Driver> Drivers { get; set; }
        public DbSet<TourReview> TourReviews { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            
            // Relaciones para Driver y Booking
            modelBuilder.Entity<Booking>()
                .HasOne(b => b.AssignedVehicle)
                .WithMany()
                .HasForeignKey(b => b.AssignedVehicleId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<Booking>()
                .HasOne(b => b.AssignedDriver)
                .WithMany()
                .HasForeignKey(b => b.AssignedDriverId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<Driver>()
                .HasOne(d => d.AssignedVehicle)
                .WithMany()
                .HasForeignKey(d => d.AssignedVehicleId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<Booking>()
                .HasMany(b => b.Items)
                .WithOne(i => i.Booking)
                .HasForeignKey(i => i.BookingId)
                .OnDelete(DeleteBehavior.Cascade);

            // Aplicar configuraciones de entidades
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        }
    }
}
