using AutoMapper;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Application.DTOs;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SIGETOUR.API.Application.Services
{
    public class TourPackageService : ITourPackageService
    {
        private readonly AppDbContext _context;
        private readonly IMapper _mapper;

        public TourPackageService(AppDbContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        public async Task<IEnumerable<TourPackageReadDto>> GetAllAsync()
        {
            var packages = await _context.TourPackages
                .Include(p => p.Images)
                .Include(p => p.Inclusions)
                .Include(p => p.Shifts)
                .Include(p => p.BoardingPoints)
                .Include(p => p.ItineraryStops)
                .Include(p => p.DefaultVehicle)
                .AsNoTracking()
                .ToListAsync();

            return _mapper.Map<IEnumerable<TourPackageReadDto>>(packages);
        }

        public async Task<TourPackageReadDto?> GetByIdAsync(Guid id)
        {
            var package = await _context.TourPackages
                .Include(p => p.Images)
                .Include(p => p.Inclusions)
                .Include(p => p.Shifts)
                .Include(p => p.BoardingPoints)
                .Include(p => p.ItineraryStops)
                .Include(p => p.DefaultVehicle)
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id);

            if (package == null) return null;

            return _mapper.Map<TourPackageReadDto>(package);
        }

        public async Task<TourPackageReadDto> CreateAsync(TourPackageCreateDto dto)
        {
            var package = _mapper.Map<TourPackage>(dto);
            package.Id = Guid.NewGuid();
            
            if (dto.ImageUrls != null && dto.ImageUrls.Any())
            {
                var isFirst = true;
                foreach (var url in dto.ImageUrls)
                {
                    package.Images.Add(new TourImage 
                    { 
                        Id = Guid.NewGuid(),
                        ImageUrl = url, 
                        IsMain = isFirst 
                    });
                    isFirst = false;
                }
            }

            // Inclusions already mapped partly by AutoMapper string->Description, but we can assign IDs
            if (package.Inclusions.Any())
            {
                foreach(var inc in package.Inclusions) inc.Id = Guid.NewGuid();
            }
            if (package.Shifts.Any())
            {
                foreach(var s in package.Shifts) s.Id = Guid.NewGuid();
            }
            if (package.BoardingPoints.Any())
            {
                foreach(var b in package.BoardingPoints) b.Id = Guid.NewGuid();
            }
            if (package.ItineraryStops.Any())
            {
                foreach(var i in package.ItineraryStops) i.Id = Guid.NewGuid();
            }

            _context.TourPackages.Add(package);
            await _context.SaveChangesAsync();

            return _mapper.Map<TourPackageReadDto>(package);
        }

        public async Task<bool> UpdateAsync(Guid id, TourPackageUpdateDto dto)
        {
            var package = await _context.TourPackages
                .Include(p => p.Images)
                .Include(p => p.Inclusions)
                .Include(p => p.Shifts)
                .Include(p => p.BoardingPoints)
                .Include(p => p.ItineraryStops)
                .FirstOrDefaultAsync(p => p.Id == id);

            if (package == null) return false;

            // Clear old collections
            _context.TourInclusions.RemoveRange(package.Inclusions);
            _context.TourShifts.RemoveRange(package.Shifts);
            _context.BoardingPoints.RemoveRange(package.BoardingPoints);
            _context.ItineraryStops.RemoveRange(package.ItineraryStops);

            _mapper.Map(dto, package); 
            
            // Generate IDs for new items mapped by AutoMapper
            if (package.Inclusions.Any())
                foreach(var inc in package.Inclusions) inc.Id = Guid.NewGuid();
            if (package.Shifts.Any())
                foreach(var s in package.Shifts) s.Id = Guid.NewGuid();
            if (package.BoardingPoints.Any())
                foreach(var b in package.BoardingPoints) b.Id = Guid.NewGuid();
            if (package.ItineraryStops.Any())
                foreach(var i in package.ItineraryStops) i.Id = Guid.NewGuid();

            // Images are handled via ImageUrls list instead of Dtos in the CreateDto, wait... UpdateDto doesn't have Images Dtos, it has ImageUrls
            if (dto.ImageUrls != null)
            {
                _context.TourImages.RemoveRange(package.Images);
                var isFirst = true;
                package.Images = dto.ImageUrls.Select(url => {
                    var img = new TourImage
                    {
                        Id = Guid.NewGuid(),
                        ImageUrl = url,
                        IsMain = isFirst,
                        TourPackageId = package.Id
                    };
                    isFirst = false;
                    return img;
                }).ToList();
            }

            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> DeleteAsync(Guid id)
        {
            var package = await _context.TourPackages.FindAsync(id);
            if (package == null) return false;

            _context.TourPackages.Remove(package);
            await _context.SaveChangesAsync();
            return true;
        }
    }
}
