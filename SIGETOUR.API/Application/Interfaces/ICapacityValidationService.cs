using System;
using System.Threading;
using System.Threading.Tasks;

namespace SIGETOUR.API.Application.Interfaces
{
    public interface ICapacityValidationService
    {
        /// <summary>
        /// Valida la disponibilidad de cupos para un tour en una fecha y turno determinados,
        /// aplicando bloqueo pesimista en PostgreSQL cuando se ejecuta dentro de una transacción activa
        /// para prevenir sobreventa ante peticiones concurrentes.
        /// </summary>
        Task<(bool IsAvailable, int BookedSeats, int MaxCapacity, string? ErrorMessage)> ValidateCapacityAsync(
            Guid tourId,
            DateTime travelDate,
            string? shiftName,
            int requestedPassengers,
            Guid? excludeBookingId = null,
            CancellationToken ct = default);

        /// <summary>
        /// Obtiene el total de pasajeros confirmados/reservados para un circuito, fecha y turno.
        /// Excluye reservas canceladas.
        /// </summary>
        Task<int> GetBookedPassengersAsync(
            Guid tourId,
            DateTime travelDate,
            string? shiftName,
            Guid? excludeBookingId = null,
            CancellationToken ct = default);
    }
}
