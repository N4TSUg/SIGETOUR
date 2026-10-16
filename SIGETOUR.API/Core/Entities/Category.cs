using System;
using System.Collections.Generic;

namespace SIGETOUR.API.Core.Entities
{
    public class Category
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Icon { get; set; } = "landscape";
        public bool IsActive { get; set; } = true;
        public int DisplayOrder { get; set; } = 1;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<TourPackage> TourPackages { get; set; } = new List<TourPackage>();

        public override string ToString() => Name;
    }
}
