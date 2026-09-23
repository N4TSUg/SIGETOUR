using Microsoft.AspNetCore.Mvc;
using SIGETOUR.API.Application.DTOs;
using SIGETOUR.API.Application.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SIGETOUR.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TourPackagesController : ControllerBase
    {
        private readonly ITourPackageService _tourPackageService;

        public TourPackagesController(ITourPackageService tourPackageService)
        {
            _tourPackageService = tourPackageService;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<TourPackageReadDto>>> GetAll()
        {
            var packages = await _tourPackageService.GetAllAsync();
            return Ok(packages);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<TourPackageReadDto>> GetById(Guid id)
        {
            var package = await _tourPackageService.GetByIdAsync(id);
            if (package == null) return NotFound(new { Message = $"TourPackage con ID {id} no encontrado." });
            
            return Ok(package);
        }

        [HttpPost]
        public async Task<ActionResult<TourPackageReadDto>> Create(TourPackageCreateDto dto)
        {
            var createdPackage = await _tourPackageService.CreateAsync(dto);
            return CreatedAtAction(nameof(GetById), new { id = createdPackage.Id }, createdPackage);
        }

        [HttpPut("{id}")]
        public async Task<ActionResult> Update(Guid id, TourPackageUpdateDto dto)
        {
            var success = await _tourPackageService.UpdateAsync(id, dto);
            if (!success) return NotFound(new { Message = $"TourPackage con ID {id} no encontrado." });

            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<ActionResult> Delete(Guid id)
        {
            var success = await _tourPackageService.DeleteAsync(id);
            if (!success) return NotFound(new { Message = $"TourPackage con ID {id} no encontrado." });

            return NoContent();
        }
    }
}
