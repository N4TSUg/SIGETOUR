using System;
using System.ComponentModel.DataAnnotations;

namespace SIGETOUR.API.Models
{
    public class FeaturedPackageDto
    {
        public Guid Id { get; set; }
        
        [Required(ErrorMessage = "Debes seleccionar un tour del catálogo base.")]
        public Guid TourPackageId { get; set; }
        
        public int DisplayOrder { get; set; }
        
        public string? CommercialTitle { get; set; }
        
        public string? CommercialSubtitle { get; set; }
        
        public string? PromoBadge { get; set; }
        
        public decimal? PromoPrice { get; set; }
        
        public bool SyncInventory { get; set; }
        
        // Navigation property for display
        public TourPackageDto? TourPackage { get; set; }
    }
}
