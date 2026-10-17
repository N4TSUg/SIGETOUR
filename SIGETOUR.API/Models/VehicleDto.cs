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

        [Required(ErrorMessage = "Por favor, ingresa la placa de rodaje.")]
        public string LicensePlate { get; set; } = string.Empty;

        [Required(ErrorMessage = "Por favor, ingresa la marca y modelo del vehículo.")]
        public string Model { get; set; } = string.Empty;
        
        public string? Category { get; set; } = string.Empty;
        public int ManufactureYear { get; set; } = DateTime.Now.Year;
        public string? ChassisNumber { get; set; } = string.Empty;
        public string? EngineNumber { get; set; } = string.Empty;
        
        [Required(ErrorMessage = "Por favor, especifica la capacidad de asientos.")]
        [Range(1, 120, ErrorMessage = "La capacidad debe ser de al menos 1 pasajero.")]
        public int SeatCapacity { get; set; }
        
        public string? Color { get; set; } = string.Empty;
        public string? FuelType { get; set; } = string.Empty;
        
        public string? SoatNumber { get; set; } = string.Empty;
        public string? SoatProvider { get; set; } = string.Empty;
        public DateTime? SoatIssueDate { get; set; }
        public DateTime? SoatExpiryDate { get; set; }
        
        public string? CitvNumber { get; set; } = string.Empty;
        public string? CitvProvider { get; set; } = string.Empty;
        public DateTime? CitvExpiryDate { get; set; }
        
        public string? TucNumber { get; set; } = string.Empty;
        public string? ResolutionNumber { get; set; } = string.Empty;

        public string? EquipmentJson { get; set; } = "[]";

        public string? Status { get; set; } = "Operativo en Ruta (En Servicio)";

        // Read-only property for the view to render existing images
        public ICollection<VehicleImage> ExistingImages { get; set; } = new List<VehicleImage>();
    }
}

