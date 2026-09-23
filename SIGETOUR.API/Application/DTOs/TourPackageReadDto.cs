using System;
using System.Collections.Generic;
using SIGETOUR.API.Core.Enums;

namespace SIGETOUR.API.Application.DTOs
{
    public class TourPackageReadDto
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        
        public TourCategory Category { get; set; }
        public string CategoryName => Category.ToString();
        public TourModality Modality { get; set; }
        public string ModalityName => Modality.ToString();
        public TourDifficulty Difficulty { get; set; }
        public string DifficultyName => Difficulty.ToString();

        public string Description { get; set; } = string.Empty;
        
        public decimal BasePrice { get; set; }
        public decimal ChildPrice { get; set; }
        
        public int MaxCapacity { get; set; }
        public int RequiredAdvancePercentage { get; set; }
        public string Duration { get; set; } = string.Empty;

        public Guid? DefaultVehicleId { get; set; }
        public VehicleDto? DefaultVehicle { get; set; }

        public bool IsActive { get; set; }

        public ICollection<TourImageDto> Images { get; set; } = new List<TourImageDto>();
        public ICollection<TourInclusionDto> Inclusions { get; set; } = new List<TourInclusionDto>();
        public ICollection<TourShiftDto> Shifts { get; set; } = new List<TourShiftDto>();
        public ICollection<BoardingPointDto> BoardingPoints { get; set; } = new List<BoardingPointDto>();
        public ICollection<ItineraryStopDto> ItineraryStops { get; set; } = new List<ItineraryStopDto>();
    }
}
