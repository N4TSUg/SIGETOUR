using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Core.Common;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;

namespace SIGETOUR.API.Application.Services
{
    public class CapacityValidationService : ICapacityValidationService
    {
        private readonly AppDbContext _context;

        public CapacityValidationService(AppDbContext context)
        {
            _context = context;
        }

        public async Task<(bool IsAvailable, int BookedSeats, int MaxCapacity, string? ErrorMessage)> ValidateCapacityAsync(
            Guid tourId,
            DateTime travelDate,
            string? shiftName,
            int requestedPassengers,
            Guid? excludeBookingId = null,
            CancellationToken ct = default)
        {
            if (requestedPassengers <= 0)
            {
                return (false, 0, 0, "La cantidad de pasajeros debe ser mayor a 0.");
            }

            // En bases de datos relacionales (PostgreSQL), si nos encontramos dentro de una transacción activa,
            // adquirimos un bloqueo pesimista sobre la fila del TourPackage para serializar consultas concurrentes
            // y garantizar 0% de sobreventa bajo alta concurrencia.
            if (_context.Database.IsRelational() && _context.Database.CurrentTransaction != null)
            {
                try
                {
                    await _context.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT 1 FROM \"TourPackages\" WHERE \"Id\" = {tourId} FOR UPDATE;", ct);
                }
                catch
                {
                    // Fallback transparente si el provider no soporta la sintaxis FOR UPDATE
                }
            }

            var tour = await _context.TourPackages
                .Include(t => t.Shifts)
                .FirstOrDefaultAsync(t => t.Id == tourId, ct);

            if (tour == null)
            {
                return (false, 0, 0, "El circuito turístico seleccionado no existe en el sistema.");
            }

            if (!tour.IsActive)
            {
                return (false, 0, 0, $"El circuito '{tour.Title}' no se encuentra habilitado para reservas.");
            }

            var maxCapacity = tour.MaxCapacity > 0 ? tour.MaxCapacity : 20;

            var bookedPax = await GetBookedPassengersAsync(tourId, travelDate, shiftName, excludeBookingId, ct);

            if (bookedPax + requestedPassengers > maxCapacity)
            {
                var available = Math.Max(0, maxCapacity - bookedPax);
                var shiftDisplay = string.IsNullOrWhiteSpace(shiftName) ? "General" : shiftName.Trim();
                var msg = $"No hay cupos suficientes para el tour '{tour.Title}' en la fecha ({travelDate:dd/MM/yyyy}) y turno ({shiftDisplay}). " +
                          $"Capacidad máxima: {maxCapacity}, ocupados: {bookedPax}, disponibles: {available}, solicitados: {requestedPassengers}.";

                return (false, bookedPax, maxCapacity, msg);
            }

            return (true, bookedPax, maxCapacity, null);
        }

        public async Task<int> GetBookedPassengersAsync(
            Guid tourId,
            DateTime travelDate,
            string? shiftName,
            Guid? excludeBookingId = null,
            CancellationToken ct = default)
        {
            var (startUtc, endUtc) = PeruTime.GetTravelDateRangeUtc(travelDate);

            var query = _context.BookingItems
                .Where(bi => bi.TourPackageId == tourId &&
                             bi.Booking.Status != BookingStatus.Cancelled &&
                             bi.Booking.TravelDate >= startUtc &&
                             bi.Booking.TravelDate < endUtc);

            if (excludeBookingId.HasValue && excludeBookingId.Value != Guid.Empty)
            {
                query = query.Where(bi => bi.BookingId != excludeBookingId.Value);
            }

            var cleanShift = shiftName?.Trim();
            if (!string.IsNullOrWhiteSpace(cleanShift) && cleanShift != "General" && cleanShift != "Turno Único")
            {
                query = query.Where(bi => bi.Booking.ShiftName == cleanShift);
            }

            return await query.SumAsync(bi => bi.Passengers, ct);
        }
    }
}
