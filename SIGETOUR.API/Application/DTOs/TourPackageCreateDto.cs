using System;
using System.Collections.Generic;
using SIGETOUR.API.Core.Enums;

namespace SIGETOUR.API.Application.DTOs
{
    public class TourPackageCreateDto
    {
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        
        public TourCategory Category { get; set; }
        public TourModality Modality { get; set; }
        public TourDifficulty Difficulty { get; set; }
        
        public string Description { get; set; } = string.Empty;
        
        public decimal BasePrice { get; set; }
        public decimal ChildPrice { get; set; }
        
        public int MaxCapacity { get; set; }
        public int RequiredAdvancePercentage { get; set; }
        public string Duration { get; set; } = string.Empty;
        
        public Guid? DefaultVehicleId { get; set; }

        public bool IsActive { get; set; } = true;
        
        public List<string> ImageUrls { get; set; } = new List<string>();
        
        public List<string> Inclusions { get; set; } = new List<string>();
        public List<TourShiftCreateDto> Shifts { get; set; } = new List<TourShiftCreateDto>();
        public List<BoardingPointCreateDto> BoardingPoints { get; set; } = new List<BoardingPointCreateDto>();
        public List<ItineraryStopCreateDto> ItineraryStops { get; set; } = new List<ItineraryStopCreateDto>();
    }

    public class TourShiftCreateDto
    {
        public string ShiftName { get; set; } = string.Empty;
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
    }

    public class BoardingPointCreateDto
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class ItineraryStopCreateDto
    {
        public int OrderIndex { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
