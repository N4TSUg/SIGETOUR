using System;
using SIGETOUR.API.Core.Enums;

namespace SIGETOUR.API.Application.DTOs
{
    public class TourInclusionDto
    {
        public Guid Id { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    public class TourShiftDto
    {
        public Guid Id { get; set; }
        public string ShiftName { get; set; } = string.Empty;
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public bool IsActive { get; set; }
    }

    public class BoardingPointDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class ItineraryStopDto
    {
        public Guid Id { get; set; }
        public int OrderIndex { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class VehicleDto
    {
        public Guid Id { get; set; }
        public string Model { get; set; } = string.Empty;
        public string LicensePlate { get; set; } = string.Empty;
        public int SeatCapacity { get; set; }
    }
}
