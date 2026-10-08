using System;
using System.ComponentModel.DataAnnotations;
using SIGETOUR.API.Core.Enums;
using SIGETOUR.API.Core.Entities;
using System.Collections.Generic;

namespace SIGETOUR.API.Models
{
    public class TourPackageDto
    {
        public Guid Id { get; set; }
        
        [Required]
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        
        public TourCategory Category { get; set; }
        public TourModality Modality { get; set; }
        public TourDifficulty Difficulty { get; set; }
        
        [Required]
        public string Description { get; set; } = string.Empty;
        
        public decimal BasePrice { get; set; }
        public decimal ChildPrice { get; set; }
        
        public int MaxCapacity { get; set; }
        public int RequiredAdvancePercentage { get; set; }
        
        public string Duration { get; set; } = string.Empty;
        
        public Guid? DefaultVehicleId { get; set; }

        public bool IsActive { get; set; } = true;
        
        // For the views
        public ICollection<TourImage> ExistingImages { get; set; } = new List<TourImage>();
        public ICollection<TourInclusion> ExistingInclusions { get; set; } = new List<TourInclusion>();
        public ICollection<TourShift> ExistingShifts { get; set; } = new List<TourShift>();
        public ICollection<ItineraryStop> ExistingItineraryStops { get; set; } = new List<ItineraryStop>();
    }
}
