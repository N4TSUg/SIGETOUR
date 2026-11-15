using System;

namespace SIGETOUR.API.Core.Common
{
    /// <summary>
    /// Manejador unificado de la zona horaria oficial del Perú (UTC-5 / America/Lima)
    /// para Turismo Chilón S.A.C., garantizando coherencia horaria en dashboard,
    /// itinerarios, límites de abordaje y consultas de fecha de viaje.
    /// </summary>
    public static class PeruTime
    {
        private static readonly TimeZoneInfo _peruTimeZone = ResolvePeruTimeZone();

        private static TimeZoneInfo ResolvePeruTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("America/Lima");
            }
            catch
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById("SA Pacific Standard Time");
                }
                catch
                {
                    return TimeZoneInfo.CreateCustomTimeZone("Peru Standard Time", TimeSpan.FromHours(-5), "Peru Standard Time", "Peru Standard Time");
                }
            }
        }

        public static TimeZoneInfo TimeZone => _peruTimeZone;

        /// <summary>
        /// Obtiene la fecha y hora actual en la zona horaria de Perú (Cajamarca).
        /// </summary>
        public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _peruTimeZone);

        /// <summary>
        /// Obtiene la fecha actual (solo fecha a las 00:00:00) en la zona horaria de Perú.
        /// </summary>
        public static DateTime Today => Now.Date;

        /// <summary>
        /// Convierte un DateTime UTC o local a la hora de Perú.
        /// </summary>
        public static DateTime ToPeruTime(DateTime dateTime)
        {
            var utc = dateTime.Kind switch
            {
                DateTimeKind.Utc => dateTime,
                DateTimeKind.Local => dateTime.ToUniversalTime(),
                _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            };
            return TimeZoneInfo.ConvertTimeFromUtc(utc, _peruTimeZone);
        }

        /// <summary>
        /// Obtiene el rango UTC correspondiente a un día de viaje calendario en Perú (inicio inclusivo y fin exclusivo).
        /// </summary>
        public static (DateTime UtcStart, DateTime UtcEnd) GetTravelDateRangeUtc(DateTime travelDate)
        {
            var dateOnly = travelDate.Date;
            var startUtc = DateTime.SpecifyKind(dateOnly, DateTimeKind.Utc);
            var endUtc = startUtc.AddDays(1);
            return (startUtc, endUtc);
        }
    }
}
