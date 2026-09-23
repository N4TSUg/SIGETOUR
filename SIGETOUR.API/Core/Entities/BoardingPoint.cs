using System;

namespace SIGETOUR.API.Core.Entities
{
    public class BoardingPoint
    {
        public Guid Id { get; set; }
        public Guid TourPackageId { get; set; }
        
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty; // e.g., "Agencia Central Turismo Chilón"

        public TourPackage TourPackage { get; set; } = null!;
    }
}
