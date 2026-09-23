using System;

namespace SIGETOUR.API.Core.Entities
{
    public class BookingItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid BookingId { get; set; }
        public Guid TourPackageId { get; set; }
        
        public int Passengers { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal SubTotal { get; set; }

        public Booking Booking { get; set; } = null!;
        public TourPackage TourPackage { get; set; } = null!;
    }
}
