using System;

namespace SIGETOUR.API.Core.Entities
{
    public class Vehicle
    {
        public Guid Id { get; set; }
        public string LicensePlate { get; set; } = string.Empty; // Ej. T7C-841
        public string Model { get; set; } = string.Empty; // Ej. Mercedes-Benz Sprinter 515
        
        public string Category { get; set; } = string.Empty; // Ej. Van Turística VIP (14 - 19 Pas.)
        public int ManufactureYear { get; set; }
        public string ChassisNumber { get; set; } = string.Empty;
        public string EngineNumber { get; set; } = string.Empty;
        public int SeatCapacity { get; set; }
        public string Color { get; set; } = string.Empty;
        public string FuelType { get; set; } = string.Empty;
        
        // Polizas / Certificados
        public string SoatNumber { get; set; } = string.Empty;
        public string SoatProvider { get; set; } = string.Empty;
        public DateTime? SoatIssueDate { get; set; }
        public DateTime? SoatExpiryDate { get; set; }
        
        public string CitvNumber { get; set; } = string.Empty;
        public string CitvProvider { get; set; } = string.Empty;
        public DateTime? CitvExpiryDate { get; set; }
        
        public string TucNumber { get; set; } = string.Empty;
        public string ResolutionNumber { get; set; } = string.Empty;

        // Equipamiento (JSON)
        public string EquipmentJson { get; set; } = "[]";

        // Estado
        public bool IsActive { get; set; } = true; // Activo Operativamente
        public string Status { get; set; } = "Operativo en Ruta (En Servicio)";
    }
}
