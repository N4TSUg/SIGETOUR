using System;

namespace SIGETOUR.API.Core.Entities
{
    public class ItineraryStop
    {
        public Guid Id { get; set; }
        public Guid TourPackageId { get; set; }
        
        public int OrderIndex { get; set; }
        public string Name { get; set; } = string.Empty; // e.g., "Mirador Bellavista"
        public string? EstimatedTime { get; set; } // e.g., "09:30 AM"

        public TourPackage TourPackage { get; set; } = null!;
    }
}
