using System;

namespace SIGETOUR.API.Models
{
    public class FeaturedPackageDto
    {
        public Guid Id { get; set; }
        public Guid TourPackageId { get; set; }
        
        public int DisplayOrder { get; set; }
        public string CommercialTitle { get; set; } = string.Empty;
        public string CommercialSubtitle { get; set; } = string.Empty;
        public string PromoBadge { get; set; } = string.Empty;
        public decimal? PromoPrice { get; set; }
        public bool SyncInventory { get; set; }
        
        // Navigation property for display
        public TourPackageDto? TourPackage { get; set; }
    }
}
