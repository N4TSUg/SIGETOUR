using System;

namespace SIGETOUR.API.Core.Entities
{
    public class FeaturedPackage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        
        // Link to the actual tour
        public Guid TourPackageId { get; set; }
        public TourPackage? TourPackage { get; set; }
        
        public int DisplayOrder { get; set; } = 1;
        
        public string CommercialTitle { get; set; } = string.Empty;
        public string CommercialSubtitle { get; set; } = string.Empty;
        
        public string PromoBadge { get; set; } = string.Empty; // e.g. "popular", "oferta", "2x1", "familias", "nuevo", "none"
        
        public decimal? PromoPrice { get; set; }
        
        // Sincronizar automticamente horarios y cupos con catlogo general
        public bool SyncInventory { get; set; } = true;
    }
}
