using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SIGETOUR.API.Application.DTOs;

namespace SIGETOUR.API.Application.Interfaces
{
    public interface ITourPackageService
    {
        Task<IEnumerable<TourPackageReadDto>> GetAllAsync();
        Task<TourPackageReadDto?> GetByIdAsync(Guid id);
        Task<TourPackageReadDto> CreateAsync(TourPackageCreateDto dto);
        Task<bool> UpdateAsync(Guid id, TourPackageUpdateDto dto);
        Task<bool> DeleteAsync(Guid id);
    }
}
