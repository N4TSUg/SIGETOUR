using System;

namespace SIGETOUR.API.Infrastructure.Data
{
    /// <summary>
    /// Gestiona el estado de preparación y validez de la base de datos del sistema.
    /// Si las migraciones fallan en el arranque, previene que procesos de fondo o peticiones
    /// operativas ejecuten transacciones financieras sobre un esquema incompleto o corrupto.
    /// </summary>
    public static class DatabaseReadiness
    {
        public static bool IsReady { get; set; } = false;
        public static string? InitializationError { get; set; }

        public static void EnsureReady()
        {
            if (!IsReady)
            {
                throw new InvalidOperationException($"La base de datos del sistema no está lista para operar transacciones: {InitializationError ?? "Migraciones o conexión pendientes"}");
            }
        }
    }
}
