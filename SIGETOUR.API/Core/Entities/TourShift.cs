using System;

namespace SIGETOUR.API.Core.Entities
{
    public class TourShift
    {
        public Guid Id { get; set; }
        public Guid TourPackageId { get; set; }
        
        public string ShiftName { get; set; } = string.Empty; // e.g., "Mañana", "Tarde"
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        
        public bool IsActive { get; set; } = true;

        public TourPackage TourPackage { get; set; } = null!;
    }
}
