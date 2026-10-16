using System.Collections.Generic;

namespace SIGETOUR.API.Models
{
    public class VehicleCatalogViewModel
    {
        public List<VehicleDto> Vehicles { get; set; } = new List<VehicleDto>();
        public string SelectedCategory { get; set; } = "Todos";
        public List<string> AvailableCategories { get; set; } = new List<string>();
        public int TotalUnits { get; set; }
        public int OperationalUnits { get; set; }
    }
}
