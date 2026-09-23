using System;

namespace SIGETOUR.API.Core.Entities
{
    public class ItineraryStop
    {
        public Guid Id { get; set; }
        public Guid TourPackageId { get; set; }
        
        public int OrderIndex { get; set; }
        public string Name { get; set; } = string.Empty; // e.g., "Mirador Bellavista"

        public TourPackage TourPackage { get; set; } = null!;
    }
}
