using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Models
{
    public class VehicleDto
    {
        public Guid Id { get; set; }

        [Required(ErrorMessage = "La placa es obligatoria")]
        public string LicensePlate { get; set; } = string.Empty;

        [Required(ErrorMessage = "El modelo es obligatorio")]
        public string Model { get; set; } = string.Empty;
        
        public string Category { get; set; } = string.Empty;
        public int ManufactureYear { get; set; } = DateTime.Now.Year;
        public string ChassisNumber { get; set; } = string.Empty;
        public string EngineNumber { get; set; } = string.Empty;
        
        [Required]
        [Range(1, 100, ErrorMessage = "La capacidad debe ser mayor a 0")]
        public int SeatCapacity { get; set; }
        
        public string Color { get; set; } = string.Empty;
        public string FuelType { get; set; } = string.Empty;
        
        public string SoatNumber { get; set; } = string.Empty;
        public string SoatProvider { get; set; } = string.Empty;
        public DateTime? SoatIssueDate { get; set; }
        public DateTime? SoatExpiryDate { get; set; }
        
        public string CitvNumber { get; set; } = string.Empty;
        public string CitvProvider { get; set; } = string.Empty;
        public DateTime? CitvExpiryDate { get; set; }
        
        public string TucNumber { get; set; } = string.Empty;
        public string ResolutionNumber { get; set; } = string.Empty;

        public string EquipmentJson { get; set; } = "[]";

        public string Status { get; set; } = "Operativo en Ruta (En Servicio)";

        // Read-only property for the view to render existing images
        public ICollection<VehicleImage> ExistingImages { get; set; } = new List<VehicleImage>();
    }
}
