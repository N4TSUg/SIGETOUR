using System.Collections.Generic;

namespace SIGETOUR.API.Models
{
    public class VehicleDetailsViewModel
    {
        public VehicleDto Vehicle { get; set; } = new VehicleDto();
        public List<VehicleDto> OtherVehicles { get; set; } = new List<VehicleDto>();
    }
}
