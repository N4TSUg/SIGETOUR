using System;

namespace SIGETOUR.API.Core.Entities
{
    public class Driver
    {
        public Guid Id { get; set; }
        
        public string FullName { get; set; } = string.Empty;
        public string Dni { get; set; } = string.Empty;
        
        /// <summary>
        /// Número oficial de Brevete / Licencia de Conducir MTC (Ej. Q-42910834)
        /// </summary>
        public string LicenseNumber { get; set; } = string.Empty;
        
        /// <summary>
        /// Categoría de Licencia (Ej. A-IIIa Especial Turismo, A-IIIc, A-IIb)
        /// </summary>
        public string LicenseCategory { get; set; } = "A-IIIa (Especial Transporte Turístico)";
        
        public DateTime? LicenseExpiryDate { get; set; }
        public string Phone { get; set; } = string.Empty;
        
        public bool IsActive { get; set; } = true;
        
        /// <summary>
        /// Vehículo habitual asignado en la flota (opcional)
        /// </summary>
        public Guid? AssignedVehicleId { get; set; }
        public Vehicle? AssignedVehicle { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
