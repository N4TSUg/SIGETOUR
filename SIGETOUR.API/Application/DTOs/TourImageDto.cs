using System;

namespace SIGETOUR.API.Application.DTOs
{
    public class TourImageDto
    {
        public Guid Id { get; set; }
        public string ImageUrl { get; set; } = string.Empty;
        public bool IsMain { get; set; }
    }
}
