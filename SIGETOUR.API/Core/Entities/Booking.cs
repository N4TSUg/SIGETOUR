using System;
using System.Collections.Generic;

namespace SIGETOUR.API.Core.Entities
{
    public class Booking
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string ReferenceCode { get; set; } = string.Empty; // e.g. RES-001
        
        public string CustomerName { get; set; } = string.Empty;
        public string CustomerEmail { get; set; } = string.Empty;
        public string CustomerPhone { get; set; } = string.Empty;

        public DateTime BookingDate { get; set; } = DateTime.UtcNow;
        public DateTime TravelDate { get; set; }
        
        public int TotalPassengers { get; set; }
        public decimal TotalAmount { get; set; }
        
        public BookingStatus Status { get; set; } = BookingStatus.Pending;

        public string PickupLocation { get; set; } = string.Empty;
        public decimal PickupCost { get; set; }

        public ICollection<BookingItem> Items { get; set; } = new List<BookingItem>();
    }

    public enum BookingStatus
    {
        Pending,
        Confirmed,
        Cancelled
    }
}

