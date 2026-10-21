using System;
using System.Collections.Generic;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.ViewModels
{
    public class DashboardViewModel
    {
        // KPIs Generales
        public int TotalTours { get; set; }
        public int TotalUsers { get; set; }
        public int TotalVehicles { get; set; }
        public int ActiveVehicles { get; set; }
        public int TotalBookings { get; set; }

        // KPIs de Operación Hoy
        public int TodayPassengers { get; set; }
        public int TotalDailyCapacity { get; set; }
        public int OccupancyPercentage { get; set; }
        public int TodayDeparturesCount { get; set; }
        public decimal TodayIncome { get; set; }
        public decimal TodayPending { get; set; }

        // Tablero de Despacho en Vivo de Hoy
        public List<DashboardDepartureItem> Departures { get; set; } = new();

        // Demanda Turística por Circuito (Barras de Progreso)
        public List<DashboardCircuitDemandItem> CircuitDemands { get; set; } = new();

        // Alertas Regulatorias y de Flota (SOAT/CITV)
        public List<FleetAlertItem> FleetAlerts { get; set; } = new();

        // Listados Complementarios
        public List<Booking> RecentBookings { get; set; } = new();
        public List<TourPackage> ActiveTours { get; set; } = new();
    }

    public class DashboardDepartureItem
    {
        public DateTime DepartureDate { get; set; } = DateTime.Today;
        public string FormattedDate { get; set; } = string.Empty;
        public string Time { get; set; } = "09:00 AM";
        public string TourTitle { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string VehicleModel { get; set; } = string.Empty;
        public string LicensePlate { get; set; } = string.Empty;
        public string DriverName { get; set; } = string.Empty;
        public string GuideName { get; set; } = string.Empty;
        public int BookedSeats { get; set; }
        public int TotalCapacity { get; set; }
        public string Status { get; set; } = "Programado";
        public string StatusColor { get; set; } = "slate"; // "emerald", "amber", "sky", "slate"
        public Guid? TourId { get; set; }
        public Guid? BookingId { get; set; }
    }

    public class DashboardCircuitDemandItem
    {
        public string TourTitle { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public int PassengerCount { get; set; }
        public int Percentage { get; set; }
        public string ColorClass { get; set; } = "bg-primary";
    }

    public class FleetAlertItem
    {
        public string LicensePlate { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string AlertType { get; set; } = string.Empty; // "SOAT", "CITV (Rev. Técnica)"
        public DateTime ExpiryDate { get; set; }
        public int DaysRemaining { get; set; }
        public bool IsExpired { get; set; }
    }
}
