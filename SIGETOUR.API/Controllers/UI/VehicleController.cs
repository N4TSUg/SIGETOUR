using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;

namespace SIGETOUR.API.Controllers.UI
{
    public class VehicleController : Controller
    {
        private readonly AppDbContext _context;

        public VehicleController(AppDbContext context)
        {
            _context = context;
        }

        // GET: /Vehicle/Catalog o /Flota
        [HttpGet]
        [Route("Vehicle")]
        [Route("Vehicle/Catalog")]
        [Route("Flota")]
        public async Task<IActionResult> Catalog(string? category)
        {
            var query = _context.Vehicles
                .Include(v => v.Images)
                .Where(v => v.IsActive);

            if (!string.IsNullOrWhiteSpace(category) && category != "Todos")
            {
                query = query.Where(v => v.Category == category);
            }

            var vehicles = await query
                .OrderByDescending(v => v.SeatCapacity)
                .ToListAsync();

            var categories = await _context.Vehicles
                .Where(v => v.IsActive && !string.IsNullOrEmpty(v.Category))
                .Select(v => v.Category!)
                .Distinct()
                .ToListAsync();

            var viewModel = new VehicleCatalogViewModel
            {
                Vehicles = vehicles.Select(MapToDto).ToList(),
                SelectedCategory = string.IsNullOrWhiteSpace(category) ? "Todos" : category,
                AvailableCategories = categories,
                TotalUnits = await _context.Vehicles.CountAsync(),
                OperationalUnits = await _context.Vehicles.CountAsync(v => v.IsActive)
            };

            return View(viewModel);
        }

        // GET: /Vehicle/Details/{id} o /Flota/Detalle/{id}
        [HttpGet]
        [Route("Vehicle/Details/{id}")]
        [Route("Flota/Detalle/{id}")]
        public async Task<IActionResult> Details(Guid id)
        {
            if (id == Guid.Empty)
            {
                return RedirectToAction(nameof(Catalog));
            }

            var vehicle = await _context.Vehicles
                .Include(v => v.Images)
                .FirstOrDefaultAsync(v => v.Id == id && v.IsActive);

            if (vehicle == null)
            {
                return NotFound();
            }

            var otherVehicles = await _context.Vehicles
                .Include(v => v.Images)
                .Where(v => v.IsActive && v.Id != id)
                .Take(3)
                .ToListAsync();

            var viewModel = new VehicleDetailsViewModel
            {
                Vehicle = MapToDto(vehicle),
                OtherVehicles = otherVehicles.Select(MapToDto).ToList()
            };

            return View(viewModel);
        }

        private static VehicleDto MapToDto(Core.Entities.Vehicle v)
        {
            return new VehicleDto
            {
                Id = v.Id,
                LicensePlate = v.LicensePlate,
                Model = v.Model,
                Category = v.Category,
                ManufactureYear = v.ManufactureYear,
                ChassisNumber = v.ChassisNumber,
                EngineNumber = v.EngineNumber,
                SeatCapacity = v.SeatCapacity,
                Color = v.Color,
                FuelType = v.FuelType,
                SoatNumber = v.SoatNumber,
                SoatProvider = v.SoatProvider,
                SoatIssueDate = v.SoatIssueDate,
                SoatExpiryDate = v.SoatExpiryDate,
                CitvNumber = v.CitvNumber,
                CitvProvider = v.CitvProvider,
                CitvExpiryDate = v.CitvExpiryDate,
                TucNumber = v.TucNumber,
                ResolutionNumber = v.ResolutionNumber,
                EquipmentJson = v.EquipmentJson,
                Status = v.Status,
                ExistingImages = v.Images?.ToList() ?? new List<Core.Entities.VehicleImage>()
            };
        }
    }
}
