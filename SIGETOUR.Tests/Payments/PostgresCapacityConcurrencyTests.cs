using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Application.Services;
using SIGETOUR.API.Core.Common;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace SIGETOUR.Tests.Payments
{
    [Collection("PostgresDatabaseCollection")]
    public class PostgresCapacityConcurrencyTests
    {
        private readonly ITestOutputHelper _output;

        public PostgresCapacityConcurrencyTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static AppDbContext CreatePostgresDbContext()
        {
            var conn = PostgresTestHelper.GetConfiguredConnectionString()!;
            var options = PostgresTestHelper.CreatePostgresOptions(conn);
            return new AppDbContext(options);
        }

        [SkippableFact]
        public async Task ConcurrentBookings_OnPostgreSQL_NeverExceedMaxCapacity()
        {
            await using (var dbCheck = await PostgresTestHelper.RequirePostgresAsync(_output)) { }

            var testTourId = Guid.NewGuid();
            var travelDate = PeruTime.Today.AddDays(7);
            var (startUtc, endUtc) = PeruTime.GetTravelDateRangeUtc(travelDate);
            const int maxCapacity = 5;
            const int totalConcurrentRequests = 10;

            // 1. Setup en PostgreSQL: Crear el circuito con capacidad máxima estricta = 5
            await using (var dbSetup = CreatePostgresDbContext())
            {
                var tour = new TourPackage
                {
                    Id = testTourId,
                    Title = "Tour Concurrencia PostgreSQL",
                    MaxCapacity = maxCapacity,
                    BasePrice = 60,
                    IsActive = true
                };
                dbSetup.TourPackages.Add(tour);
                await dbSetup.SaveChangesAsync();
            }

            try
            {
                var successCount = 0;
                var rejectedCount = 0;
                var lockObj = new object();

                // 2. Ejecutar 10 peticiones concurrentes simultáneas intentando reservar 1 asiento cada una
                var tasks = Enumerable.Range(1, totalConcurrentRequests).Select(async i =>
                {
                    await using var db = CreatePostgresDbContext();
                    var capacityService = new CapacityValidationService(db);

                    await using var tx = await db.Database.BeginTransactionAsync();
                    try
                    {
                        var validation = await capacityService.ValidateCapacityAsync(
                            testTourId, travelDate, "Turno Mañana - 09:30 AM", requestedPassengers: 1);

                        if (!validation.IsAvailable)
                        {
                            lock (lockObj) { rejectedCount++; }
                            await tx.RollbackAsync();
                            return;
                        }

                        var booking = new Booking
                        {
                            Id = Guid.NewGuid(),
                            ReferenceCode = $"CONC-{i:D4}",
                            CustomerName = $"Cliente Concurrente {i}",
                            TravelDate = startUtc,
                            ShiftName = "Turno Mañana - 09:30 AM",
                            TotalPassengers = 1,
                            Status = BookingStatus.Confirmed
                        };
                        booking.Items.Add(new BookingItem
                        {
                            TourPackageId = testTourId,
                            Passengers = 1,
                            SubTotal = 60
                        });

                        db.Bookings.Add(booking);
                        await db.SaveChangesAsync();
                        await tx.CommitAsync();

                        lock (lockObj) { successCount++; }
                    }
                    catch
                    {
                        await tx.RollbackAsync();
                        lock (lockObj) { rejectedCount++; }
                    }
                });

                await Task.WhenAll(tasks);

                _output.WriteLine($"Resultados de concurrencia: {successCount} exitosas, {rejectedCount} rechazadas.");

                // 3. Verificación en la base de datos PostgreSQL real
                await using (var dbVerify = CreatePostgresDbContext())
                {
                    var actualBookedPax = await dbVerify.BookingItems
                        .Where(bi => bi.TourPackageId == testTourId &&
                                     bi.Booking.TravelDate >= startUtc &&
                                     bi.Booking.TravelDate < endUtc &&
                                     bi.Booking.Status != BookingStatus.Cancelled)
                        .SumAsync(bi => bi.Passengers);

                    // La suma de pasajeros en BD NUNCA debe exceder maxCapacity (5)
                    Assert.Equal(maxCapacity, actualBookedPax);
                    Assert.Equal(maxCapacity, successCount);
                    Assert.Equal(totalConcurrentRequests - maxCapacity, rejectedCount);
                }
            }
            finally
            {
                // Limpieza en PostgreSQL
                await using var dbClean = CreatePostgresDbContext();
                var bookingsToDelete = await dbClean.Bookings
                    .Where(b => b.Items.Any(i => i.TourPackageId == testTourId))
                    .ToListAsync();
                dbClean.Bookings.RemoveRange(bookingsToDelete);
                var tourToDelete = await dbClean.TourPackages.FindAsync(testTourId);
                if (tourToDelete != null) dbClean.TourPackages.Remove(tourToDelete);
                await dbClean.SaveChangesAsync();
            }
        }
    }
}
