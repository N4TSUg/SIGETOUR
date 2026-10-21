using System;

namespace SIGETOUR.API.Core.Entities
{
    public class TourReview
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        
        public Guid TourPackageId { get; set; }
        public TourPackage? TourPackage { get; set; }

        public string AuthorName { get; set; } = string.Empty;
        public string AuthorCity { get; set; } = string.Empty; // Ej. "Turista de Lima • Viaje Familiar"
        public string Comment { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsApproved { get; set; } = false;
        public bool IsVisibleOnHome { get; set; } = false;
    }
}
