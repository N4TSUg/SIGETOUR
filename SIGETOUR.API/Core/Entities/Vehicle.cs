using System;

namespace SIGETOUR.API.Core.Entities
{
    public class Vehicle
    {
        public Guid Id { get; set; }
        public string Model { get; set; } = string.Empty; // e.g., "Mercedes-Benz Sprinter 515"
        public string LicensePlate { get; set; } = string.Empty; // e.g., "M3A-950"
        public int SeatCapacity { get; set; } // e.g., 19
        public bool IsActive { get; set; } = true;
    }
}
