using AutoMapper;
using SIGETOUR.API.Application.DTOs;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Application.Mappings
{
    public class TourPackageProfile : Profile
    {
        public TourPackageProfile()
        {
            CreateMap<TourImage, TourImageDto>().ReverseMap();
            CreateMap<TourInclusion, TourInclusionDto>().ReverseMap();
            CreateMap<TourShift, TourShiftDto>().ReverseMap();
            CreateMap<BoardingPoint, BoardingPointDto>().ReverseMap();
            CreateMap<ItineraryStop, ItineraryStopDto>().ReverseMap();
            CreateMap<Vehicle, VehicleDto>().ReverseMap();

            // Custom mapping for string to TourInclusion for Creation/Update
            CreateMap<string, TourInclusion>()
                .ForMember(d => d.Description, opt => opt.MapFrom(src => src));

            CreateMap<TourShiftCreateDto, TourShift>();
            CreateMap<BoardingPointCreateDto, BoardingPoint>();
            CreateMap<ItineraryStopCreateDto, ItineraryStop>();

            CreateMap<TourPackage, TourPackageReadDto>();
            
            CreateMap<TourPackageCreateDto, TourPackage>()
                .ForMember(d => d.Images, opt => opt.Ignore()); // Images should be handled separately

            CreateMap<TourPackageUpdateDto, TourPackage>()
                .ForMember(d => d.Images, opt => opt.Ignore());
        }
    }
}
