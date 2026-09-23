using System;
using System.Collections.Generic;
using SIGETOUR.API.Core.Enums;

namespace SIGETOUR.API.Core.Entities
{
    public class TourPackage
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        
        public TourCategory Category { get; set; }
        public TourModality Modality { get; set; }
        public TourDifficulty Difficulty { get; set; }
        
        public string Description { get; set; } = string.Empty;
        
        public decimal BasePrice { get; set; } // Adult Price
        public decimal ChildPrice { get; set; }
        
        public int MaxCapacity { get; set; }
        public int RequiredAdvancePercentage { get; set; } // e.g. 50 or 100
        
        public string Duration { get; set; } = string.Empty; // e.g. "4 Horas"
        
        public Guid? DefaultVehicleId { get; set; }
        public Vehicle? DefaultVehicle { get; set; }

        public bool IsActive { get; set; } = true;

        // Collections
        public ICollection<TourImage> Images { get; set; } = new List<TourImage>();
        public ICollection<TourInclusion> Inclusions { get; set; } = new List<TourInclusion>();
        public ICollection<TourShift> Shifts { get; set; } = new List<TourShift>();
        public ICollection<BoardingPoint> BoardingPoints { get; set; } = new List<BoardingPoint>();
        public ICollection<ItineraryStop> ItineraryStops { get; set; } = new List<ItineraryStop>();
    }
}

