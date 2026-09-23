using System;

namespace SIGETOUR.API.Core.Entities
{
    public class TourImage
    {
        public Guid Id { get; set; }
        public Guid TourPackageId { get; set; }
        public string ImageUrl { get; set; } = string.Empty;
        public bool IsMain { get; set; }

        public TourPackage? TourPackage { get; set; }
    }
}
