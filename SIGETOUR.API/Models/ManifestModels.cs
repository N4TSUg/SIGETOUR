using System;
using System.Collections.Generic;

namespace SIGETOUR.API.Models
{
    public class ManifestExportRequest
    {
        public DateTime Date { get; set; } = DateTime.Today;
        public Guid TourPackageId { get; set; }
        public string? ShiftName { get; set; }
        public Guid VehicleId { get; set; }
        public string? DriverName { get; set; } = "Conductor Asignado";
        public string? DriverLicense { get; set; } = "Q-42910834";
        public string? GuideName { get; set; } = "Guía Oficial de Turismo";
        public string? GuideCarnet { get; set; } = "DIRCETUR-CAJ";
        public string? DeparturePoint { get; set; } = "Agencia Principal - Jr. Dos de Mayo 534, Cajamarca";
    }

    public class ManifestPassengerItem
    {
        public int SeatNumber { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string DocumentType { get; set; } = "DNI";
        public string DocumentNumber { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string PickupLocation { get; set; } = string.Empty;
        public string ReferenceCode { get; set; } = string.Empty;
        public string PassengerType { get; set; } = "Titular"; // Titular, Acompañante, Menor, Libre
        public bool IsOccupied { get; set; } = true;
    }

    public class ManifestPreviewDto
    {
        public string TourTitle { get; set; } = string.Empty;
        public string TravelDate { get; set; } = string.Empty;
        public string ShiftName { get; set; } = string.Empty;
        public string VehicleModel { get; set; } = string.Empty;
        public string VehiclePlate { get; set; } = string.Empty;
        public int TotalSeats { get; set; }
        public int OccupiedSeats { get; set; }
        public int AvailableSeats { get; set; }
        public double OccupancyPercentage { get; set; }
        public List<ManifestPassengerItem> Passengers { get; set; } = new();
    }
}
