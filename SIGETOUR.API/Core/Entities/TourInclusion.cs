using System;

namespace SIGETOUR.API.Core.Entities
{
    public class TourInclusion
    {
        public Guid Id { get; set; }
        public Guid TourPackageId { get; set; }
        public string Description { get; set; } = string.Empty;

        // Navigation property
        public TourPackage TourPackage { get; set; } = null!;
    }
}
