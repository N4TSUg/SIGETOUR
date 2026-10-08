using System;
namespace SIGETOUR.API.Core.Entities
{
    public class VehicleImage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid VehicleId { get; set; }
        public string ImageUrl { get; set; } = string.Empty;
        public bool IsCover { get; set; } = false;
        public Vehicle? Vehicle { get; set; }
    }
}
