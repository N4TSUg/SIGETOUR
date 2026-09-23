using System;
using System.ComponentModel.DataAnnotations;

namespace SIGETOUR.API.Models
{
    public class CheckoutRequestViewModel
    {
        [Required]
        public Guid TourId { get; set; }
        
        [Required]
        public DateTime TravelDate { get; set; }
        
        [Required]
        public string Shift { get; set; } = string.Empty;
        
        [Required]
        public string BoardingPoint { get; set; } = string.Empty;
        
        public string? HotelZone { get; set; }
        public string? HotelName { get; set; }
        public string? HotelAddress { get; set; }
        
        [Required]
        public string CustomerName { get; set; } = string.Empty;
        
        [Required]
        public string CustomerPhone { get; set; } = string.Empty;
        
        [Required]
        [EmailAddress]
        public string CustomerEmail { get; set; } = string.Empty;
        
        [Required]
        [Range(1, 100)]
        public int Passengers { get; set; }
    }
}
