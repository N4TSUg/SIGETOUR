using System;
using System.ComponentModel.DataAnnotations;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Models
{
    public class EditBookingViewModel
    {
        public Guid Id { get; set; }
        
        [Required]
        public string CustomerName { get; set; } = string.Empty;
        
        [Required]
        public string CustomerDni { get; set; } = string.Empty;
        
        [Required]
        public string CustomerPhone { get; set; } = string.Empty;

        public DateTime TravelDate { get; set; }
        
        public string ShiftName { get; set; } = string.Empty;
        public string BoardingPoint { get; set; } = string.Empty; // Using PickupLocation basically
        public int TotalPassengers { get; set; }
        
        public decimal TotalAmount { get; set; }
        public decimal PaidAmount { get; set; }
        
        public string PaymentMethod { get; set; } = string.Empty;
        public string OperationNumber { get; set; } = string.Empty;
        
        public string InvoiceType { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        
        public BookingStatus Status { get; set; }
        
        public string AdditionalPassengersJson { get; set; } = "[]";
        
        public string ReferenceCode { get; set; } = string.Empty;
        public string TourName { get; set; } = string.Empty;
        public System.Collections.Generic.List<string> AvailableShifts { get; set; } = new();
    }
}

