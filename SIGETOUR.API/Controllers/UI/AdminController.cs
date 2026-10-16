using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Models;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Collections.Generic;

namespace SIGETOUR.API.Controllers.UI
{
    [Authorize(AuthenticationSchemes = "Identity.Application")]
    public class AdminController : Controller
    {
        private readonly AppDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IWebHostEnvironment _env;
        private readonly IManifestExportService _manifestService;

        public AdminController(AppDbContext context, UserManager<ApplicationUser> userManager, IWebHostEnvironment env, IManifestExportService manifestService)
        {
            _context = context;
            _userManager = userManager;
            _env = env;
            _manifestService = manifestService;
        }

        public async Task<IActionResult> Dashboard()
        {
            var toursCount = await _context.TourPackages.CountAsync();
            var usersCount = await _userManager.Users.CountAsync();
            var vehiclesCount = await _context.Vehicles.CountAsync();
            var bookingsCount = await _context.Bookings.CountAsync();

            var recentBookings = await _context.Bookings
                .Include(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .OrderByDescending(b => b.BookingDate)
                .Take(5)
                .ToListAsync();

            var activeTours = await _context.TourPackages
                .Include(t => t.Images)
                .Where(t => t.IsActive)
                .OrderBy(t => t.Title)
                .Take(4)
                .ToListAsync();

            ViewBag.ToursCount = toursCount;
            ViewBag.UsersCount = usersCount;
            ViewBag.VehiclesCount = vehiclesCount;
            ViewBag.BookingsCount = bookingsCount;
            ViewBag.ActiveTours = activeTours;
            ViewBag.RecentBookings = recentBookings;

            return View();
        }

        public async Task<IActionResult> Tours(string? search, Guid? categoryId, string? status)
        {
            var query = _context.TourPackages
                .Include(t => t.Images)
                .Include(t => t.Category)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                query = query.Where(t =>
                    t.Title.ToLower().Contains(term) ||
                    t.Subtitle.ToLower().Contains(term) ||
                    t.Description.ToLower().Contains(term) ||
                    t.Duration.ToLower().Contains(term) ||
                    (t.Category != null && t.Category.Name.ToLower().Contains(term)));
            }

            if (categoryId.HasValue && categoryId.Value != Guid.Empty)
            {
                query = query.Where(t => t.CategoryId == categoryId.Value);
            }

            if (!string.IsNullOrWhiteSpace(status) && status != "Todos")
            {
                bool isActive = status == "Activo";
                query = query.Where(t => t.IsActive == isActive);
            }

            var tours = await query.OrderBy(t => t.Title).ToListAsync();

            ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ToListAsync();
            ViewBag.CurrentSearch = search;
            ViewBag.CurrentCategoryId = categoryId;
            ViewBag.CurrentStatus = status;

            return View(tours);
        }

        public async Task<IActionResult> Bookings(string? search, string? status, DateTime? date, Guid? tourId)
        {
            var query = _context.Bookings
                .Include(b => b.Items)
                    .ThenInclude(i => i.TourPackage)
                        .ThenInclude(t => t.Category)
                .Include(b => b.Items)
                    .ThenInclude(i => i.TourPackage)
                        .ThenInclude(t => t.DefaultVehicle)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                query = query.Where(b =>
                    b.ReferenceCode.ToLower().Contains(term) ||
                    b.CustomerName.ToLower().Contains(term) ||
                    b.CustomerDni.ToLower().Contains(term) ||
                    b.CustomerEmail.ToLower().Contains(term) ||
                    b.CustomerPhone.ToLower().Contains(term) ||
                    b.OperationNumber.ToLower().Contains(term) ||
                    b.InvoiceNumber.ToLower().Contains(term) ||
                    b.PickupLocation.ToLower().Contains(term) ||
                    b.ShiftName.ToLower().Contains(term) ||
                    b.AdditionalPassengersJson.ToLower().Contains(term) ||
                    b.Items.Any(i => i.TourPackage.Title.ToLower().Contains(term)));
            }

            if (!string.IsNullOrWhiteSpace(status) && status != "Todos")
            {
                if (Enum.TryParse<BookingStatus>(status, true, out var stEnum))
                {
                    query = query.Where(b => b.Status == stEnum);
                }
            }

            if (date.HasValue)
            {
                var dUtc = DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Utc);
                query = query.Where(b => b.TravelDate.Date == dUtc);
            }

            if (tourId.HasValue && tourId != Guid.Empty)
            {
                query = query.Where(b => b.Items.Any(i => i.TourPackageId == tourId.Value));
            }

            var bookings = await query
                .OrderByDescending(b => b.BookingDate)
                .ToListAsync();

            ViewBag.CurrentSearch = search;
            ViewBag.CurrentStatus = status;
            ViewBag.CurrentDate = date?.ToString("yyyy-MM-dd");
            ViewBag.CurrentTourId = tourId;
            ViewBag.AvailableTours = await _context.TourPackages
                .Where(t => t.IsActive)
                .OrderBy(t => t.Title)
                .ToListAsync();
            ViewBag.AvailableVehicles = await _context.Vehicles
                .Where(v => v.IsActive)
                .OrderBy(v => v.Model)
                .ToListAsync();

            return View(bookings);
        }

        [HttpGet]
        public async Task<IActionResult> GetManifestPreview(DateTime date, Guid tourId, Guid? vehicleId, string? shift)
        {
            if (tourId == Guid.Empty) return BadRequest("Debe seleccionar un tour.");
            try
            {
                var preview = await _manifestService.GetManifestPreviewAsync(date, tourId, vehicleId, shift);
                return Json(preview);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet]
        [HttpPost]
        public async Task<IActionResult> ExportManifestExcel([FromForm] ManifestExportRequest? formRequest, [FromQuery] ManifestExportRequest? queryRequest)
        {
            var req = formRequest ?? queryRequest;
            if (req == null || req.TourPackageId == Guid.Empty)
            {
                var tourIdStr = Request.Query["tourPackageId"].ToString();
                if (Guid.TryParse(tourIdStr, out var tid))
                {
                    req = new ManifestExportRequest
                    {
                        TourPackageId = tid,
                        Date = DateTime.TryParse(Request.Query["date"].ToString(), out var d) ? d : DateTime.Today,
                        ShiftName = Request.Query["shiftName"].ToString(),
                        VehicleId = Guid.TryParse(Request.Query["vehicleId"].ToString(), out var vid) ? vid : Guid.Empty,
                        DriverName = Request.Query["driverName"].ToString(),
                        DriverLicense = Request.Query["driverLicense"].ToString(),
                        GuideName = Request.Query["guideName"].ToString(),
                        GuideCarnet = Request.Query["guideCarnet"].ToString(),
                        DeparturePoint = Request.Query["departurePoint"].ToString()
                    };
                }
            }

            if (req == null || req.TourPackageId == Guid.Empty)
            {
                return BadRequest("Debe seleccionar un tour y fecha válidos para generar el manifiesto.");
            }

            try
            {
                var fileBytes = await _manifestService.GenerateManifestExcelAsync(req);
                var tour = await _context.TourPackages.FindAsync(req.TourPackageId);
                var vehicle = await _context.Vehicles.FindAsync(req.VehicleId);

                var cleanTour = tour != null ? string.Concat(tour.Title.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_") : "Tour";
                var cleanPlate = vehicle != null ? vehicle.LicensePlate.Replace(" ", "-") : "Unidad";
                var fileName = $"Manifiesto_MTC_{cleanPlate}_{req.Date:yyyyMMdd}_{cleanTour}.xlsx";

                return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error al generar el manifiesto: {ex.Message}");
            }
        }

        [HttpGet]
        public async Task<IActionResult> EditVehicle(Guid? id)
        {
            if (id == null)
            {
                return View(new SIGETOUR.API.Models.VehicleDto());
            }
            var vehicle = await _context.Vehicles
                .Include(v => v.Images)
                .FirstOrDefaultAsync(v => v.Id == id);
            if (vehicle == null) return NotFound();
            
            var dto = new SIGETOUR.API.Models.VehicleDto
            {
                Id = vehicle.Id,
                LicensePlate = vehicle.LicensePlate,
                Model = vehicle.Model,
                Category = vehicle.Category,
                ManufactureYear = vehicle.ManufactureYear,
                ChassisNumber = vehicle.ChassisNumber,
                EngineNumber = vehicle.EngineNumber,
                SeatCapacity = vehicle.SeatCapacity,
                Color = vehicle.Color,
                FuelType = vehicle.FuelType,
                SoatNumber = vehicle.SoatNumber,
                SoatProvider = vehicle.SoatProvider,
                SoatIssueDate = vehicle.SoatIssueDate,
                SoatExpiryDate = vehicle.SoatExpiryDate,
                CitvNumber = vehicle.CitvNumber,
                CitvProvider = vehicle.CitvProvider,
                CitvExpiryDate = vehicle.CitvExpiryDate,
                TucNumber = vehicle.TucNumber,
                ResolutionNumber = vehicle.ResolutionNumber,
                EquipmentJson = vehicle.EquipmentJson,
                Status = vehicle.Status,
                ExistingImages = vehicle.Images
            };
            return View(dto);
        }

        [HttpPost]
        public async Task<IActionResult> EditVehicle(SIGETOUR.API.Models.VehicleDto dto, List<IFormFile> VehicleImages)
        {
            ModelState.Remove("ExistingImages");
            if (!ModelState.IsValid)
            {
                if (dto.Id != Guid.Empty) {
                    dto.ExistingImages = await _context.VehicleImages.Where(vi => vi.VehicleId == dto.Id).ToListAsync();
                }
                return View(dto);
            }
            if (string.IsNullOrWhiteSpace(dto.LicensePlate) || string.IsNullOrWhiteSpace(dto.Model))
            {
                ModelState.AddModelError("", "Placa y Modelo son obligatorios.");
                return View(dto);
            }

            SIGETOUR.API.Core.Entities.Vehicle vehicle;
            if (dto.Id == Guid.Empty)
            {
                vehicle = new SIGETOUR.API.Core.Entities.Vehicle
                {
                    Id = Guid.NewGuid(),
                    LicensePlate = dto.LicensePlate,
                    Model = dto.Model,
                    Category = dto.Category,
                    ManufactureYear = dto.ManufactureYear,
                    ChassisNumber = dto.ChassisNumber,
                    EngineNumber = dto.EngineNumber,
                    SeatCapacity = dto.SeatCapacity,
                    Color = dto.Color,
                    FuelType = dto.FuelType,
                    SoatNumber = dto.SoatNumber,
                    SoatProvider = dto.SoatProvider,
                    SoatIssueDate = dto.SoatIssueDate.HasValue ? DateTime.SpecifyKind(dto.SoatIssueDate.Value, DateTimeKind.Utc) : (DateTime?)null,
                    SoatExpiryDate = dto.SoatExpiryDate.HasValue ? DateTime.SpecifyKind(dto.SoatExpiryDate.Value, DateTimeKind.Utc) : (DateTime?)null,
                    CitvNumber = dto.CitvNumber,
                    CitvProvider = dto.CitvProvider,
                    CitvExpiryDate = dto.CitvExpiryDate.HasValue ? DateTime.SpecifyKind(dto.CitvExpiryDate.Value, DateTimeKind.Utc) : (DateTime?)null,
                    TucNumber = dto.TucNumber,
                    ResolutionNumber = dto.ResolutionNumber,
                    Status = dto.Status,
                    IsActive = dto.Status != "Baja Operativa",
                    EquipmentJson = dto.EquipmentJson ?? "[]"
                };
                _context.Vehicles.Add(vehicle);
            }
            else
            {
                vehicle = await _context.Vehicles.FindAsync(dto.Id) ?? throw new Exception("Vehicle not found");
                vehicle.LicensePlate = dto.LicensePlate;
                vehicle.Model = dto.Model;
                vehicle.Category = dto.Category;
                vehicle.ManufactureYear = dto.ManufactureYear;
                vehicle.ChassisNumber = dto.ChassisNumber;
                vehicle.EngineNumber = dto.EngineNumber;
                vehicle.SeatCapacity = dto.SeatCapacity;
                vehicle.Color = dto.Color;
                vehicle.FuelType = dto.FuelType;
                vehicle.SoatNumber = dto.SoatNumber;
                vehicle.SoatProvider = dto.SoatProvider;
                vehicle.SoatIssueDate = dto.SoatIssueDate.HasValue ? DateTime.SpecifyKind(dto.SoatIssueDate.Value, DateTimeKind.Utc) : (DateTime?)null;
                vehicle.SoatExpiryDate = dto.SoatExpiryDate.HasValue ? DateTime.SpecifyKind(dto.SoatExpiryDate.Value, DateTimeKind.Utc) : (DateTime?)null;
                vehicle.CitvNumber = dto.CitvNumber;
                vehicle.CitvProvider = dto.CitvProvider;
                vehicle.CitvExpiryDate = dto.CitvExpiryDate.HasValue ? DateTime.SpecifyKind(dto.CitvExpiryDate.Value, DateTimeKind.Utc) : (DateTime?)null;
                vehicle.TucNumber = dto.TucNumber;
                vehicle.ResolutionNumber = dto.ResolutionNumber;
                vehicle.Status = dto.Status;
                vehicle.IsActive = dto.Status != "Baja Operativa";
                vehicle.EquipmentJson = dto.EquipmentJson ?? "[]";
                _context.Vehicles.Update(vehicle);
            }

            await _context.SaveChangesAsync();

            // Handle image uploads
            if (VehicleImages != null && VehicleImages.Any())
            {
                var uploadsFolder = Path.Combine(_env.WebRootPath, "images", "vehicles");
                Directory.CreateDirectory(uploadsFolder);
                bool isFirst = !_context.VehicleImages.Any(vi => vi.VehicleId == vehicle.Id);

                foreach (var file in VehicleImages)
                {
                    if (file.Length > 0)
                    {
                        var ext = Path.GetExtension(file.FileName);
                        var fileName = Guid.NewGuid().ToString("N") + ext;
                        var filePath = Path.Combine(uploadsFolder, fileName);
                        using var stream = new FileStream(filePath, FileMode.Create);
                        await file.CopyToAsync(stream);

                        _context.VehicleImages.Add(new SIGETOUR.API.Core.Entities.VehicleImage
                        {
                            VehicleId = vehicle.Id,
                            ImageUrl = "/images/vehicles/" + fileName,
                            IsCover = isFirst
                        });
                        isFirst = false;
                    }
                }
                await _context.SaveChangesAsync();
            }

            return RedirectToAction("Fleet");
        }

        [HttpPost]
        public async Task<IActionResult> DeleteVehicleImage(Guid id, Guid vehicleId)
        {
            var image = await _context.VehicleImages.FindAsync(id);
            if (image != null)
            {
                // Try to delete from disk
                var path = Path.Combine(_env.WebRootPath, image.ImageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                _context.VehicleImages.Remove(image);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction("EditVehicle", new { id = vehicleId });
        }
        
        [HttpPost]
        public async Task<IActionResult> DeleteVehicle(Guid id)
        {
            var vehicle = await _context.Vehicles.FindAsync(id);
            if (vehicle != null)
            {
                vehicle.IsActive = false;
                vehicle.Status = "Baja Operativa";
                _context.Vehicles.Update(vehicle);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction("Fleet");
        }

        public async Task<IActionResult> Fleet(string? search, string? status)
        {
            var query = _context.Vehicles
                .Include(v => v.Images)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                query = query.Where(v =>
                    v.LicensePlate.ToLower().Contains(term) ||
                    v.Model.ToLower().Contains(term) ||
                    v.Category.ToLower().Contains(term) ||
                    v.Color.ToLower().Contains(term) ||
                    v.SoatNumber.ToLower().Contains(term) ||
                    v.CitvNumber.ToLower().Contains(term));
            }

            if (!string.IsNullOrWhiteSpace(status) && status != "Todos")
            {
                query = query.Where(v => v.Status == status);
            }

            var vehicles = await query
                .OrderBy(v => v.LicensePlate)
                .ToListAsync();

            ViewBag.CurrentSearch = search;
            ViewBag.CurrentStatus = status;

            return View(vehicles);
        }

        public async Task<IActionResult> Users(string? search)
        {
            var query = _userManager.Users.AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                query = query.Where(u =>
                    (u.FirstName != null && u.FirstName.ToLower().Contains(term)) ||
                    (u.LastName != null && u.LastName.ToLower().Contains(term)) ||
                    (u.Email != null && u.Email.ToLower().Contains(term)) ||
                    (u.UserName != null && u.UserName.ToLower().Contains(term)));
            }

            var users = await query.ToListAsync();
            ViewBag.CurrentSearch = search;
            return View(users);
        }

        // --- NEW CRUD METHODS FOR TOURS --- //

        [HttpGet]
        public async Task<IActionResult> CreateTour()
        {
            ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ToListAsync();
            return View(new SIGETOUR.API.Models.TourPackageDto());
        }

        [HttpPost]
        public async Task<IActionResult> CreateTour(SIGETOUR.API.Models.TourPackageDto dto, IFormFileCollection ImageFiles, string[] Stops, string[] StopTimes, string[] Inclusions, string[] ShiftNames, string[] ShiftStarts, string[] ShiftEnds)
        {
            if (!ModelState.IsValid)
            {
                ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ToListAsync();
                return View("CreateTour", dto);
            }
            var model = new SIGETOUR.API.Core.Entities.TourPackage
            {
                Id = Guid.NewGuid(),
                Title = dto.Title ?? string.Empty,
                Subtitle = dto.Subtitle ?? string.Empty,
                CategoryId = dto.CategoryId,
                Modality = dto.Modality,
                Difficulty = dto.Difficulty,
                Description = dto.Description ?? string.Empty,
                BasePrice = dto.BasePrice,
                ChildPrice = dto.ChildPrice,
                MaxCapacity = dto.MaxCapacity,
                RequiredAdvancePercentage = dto.RequiredAdvancePercentage,
                Duration = dto.Duration ?? string.Empty,
                IsActive = true
            };
            model.Slug = string.IsNullOrEmpty(dto.Slug) ? model.Title.ToLower().Replace(" ", "-") : dto.Slug;
            
            if (Stops != null) {
                for (int i = 0; i < Stops.Length; i++) {
                    if (!string.IsNullOrWhiteSpace(Stops[i]))
                        model.ItineraryStops.Add(new ItineraryStop { Id = Guid.NewGuid(), Name = Stops[i], OrderIndex = i, EstimatedTime = StopTimes?.ElementAtOrDefault(i) });
                }
            }
            
            if (Inclusions != null) {
                foreach (var inc in Inclusions) {
                    model.Inclusions.Add(new TourInclusion { Id = Guid.NewGuid(), Description = inc });
                }
            }
            
            if (ShiftNames != null) {
                for (int i = 0; i < ShiftNames.Length; i++) {
                    if (!string.IsNullOrWhiteSpace(ShiftNames[i])) {
                        model.Shifts.Add(new TourShift {
                            Id = Guid.NewGuid(),
                            ShiftName = ShiftNames[i],
                            StartTime = TimeSpan.TryParse(ShiftStarts?.ElementAtOrDefault(i), out var st) ? st : TimeSpan.Zero,
                            EndTime = TimeSpan.TryParse(ShiftEnds?.ElementAtOrDefault(i), out var et) ? et : TimeSpan.Zero,
                            IsActive = true
                        });
                    }
                }
            }

            await ProcessImages(model, ImageFiles);
            
            _context.TourPackages.Add(model);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Tours));
        }

        [HttpGet]
        public async Task<IActionResult> EditTour(Guid id)
        {
            var tour = await _context.TourPackages
                .Include(t => t.Category)
                .Include(t => t.Images)
                .Include(t => t.Inclusions)
                .Include(t => t.ItineraryStops)
                .Include(t => t.Shifts)
                .FirstOrDefaultAsync(t => t.Id == id);
                
            if (tour == null) return NotFound();
            
            ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ToListAsync();

            var dto = new SIGETOUR.API.Models.TourPackageDto
            {
                Id = tour.Id,
                Title = tour.Title,
                Subtitle = tour.Subtitle,
                Slug = tour.Slug,
                CategoryId = tour.CategoryId,
                CategoryName = tour.Category?.Name,
                Modality = tour.Modality,
                Difficulty = tour.Difficulty,
                Description = tour.Description,
                BasePrice = tour.BasePrice,
                ChildPrice = tour.ChildPrice,
                MaxCapacity = tour.MaxCapacity,
                RequiredAdvancePercentage = tour.RequiredAdvancePercentage,
                Duration = tour.Duration,
                DefaultVehicleId = tour.DefaultVehicleId,
                IsActive = tour.IsActive,
                ExistingImages = tour.Images,
                ExistingInclusions = tour.Inclusions,
                ExistingShifts = tour.Shifts,
                ExistingItineraryStops = tour.ItineraryStops
            };
            
            return View("CreateTour", dto);
        }

        [HttpPost]
        public async Task<IActionResult> EditTour(SIGETOUR.API.Models.TourPackageDto dto, IFormFileCollection ImageFiles, string[] Stops, string[] StopTimes, string[] Inclusions, string[] ShiftNames, string[] ShiftStarts, string[] ShiftEnds)
        {
            if (!ModelState.IsValid)
            {
                ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ToListAsync();
                if (dto.Id != Guid.Empty)
                {
                    var existingTour = await _context.TourPackages
                        .Include(t => t.Category)
                        .Include(t => t.Images)
                        .Include(t => t.Inclusions)
                        .Include(t => t.ItineraryStops)
                        .Include(t => t.Shifts)
                        .FirstOrDefaultAsync(t => t.Id == dto.Id);
                    if (existingTour != null)
                    {
                        dto.ExistingImages = existingTour.Images;
                        dto.ExistingInclusions = existingTour.Inclusions;
                        dto.ExistingShifts = existingTour.Shifts;
                        dto.ExistingItineraryStops = existingTour.ItineraryStops;
                    }
                }
                return View("CreateTour", dto);
            }
            var tour = await _context.TourPackages
                .Include(t => t.Images)
                .Include(t => t.ItineraryStops)
                .Include(t => t.Inclusions)
                .FirstOrDefaultAsync(t => t.Id == dto.Id);
                
            if (tour == null) return NotFound();

            tour.Title = dto.Title ?? string.Empty;
            tour.Subtitle = dto.Subtitle ?? string.Empty;
            tour.Description = dto.Description ?? string.Empty;
            tour.BasePrice = dto.BasePrice;
            tour.ChildPrice = dto.ChildPrice;
            tour.MaxCapacity = dto.MaxCapacity;
            tour.Duration = dto.Duration ?? string.Empty;
            tour.CategoryId = dto.CategoryId;
            tour.Modality = dto.Modality;
            tour.Difficulty = dto.Difficulty;
            tour.IsActive = dto.IsActive;
            tour.RequiredAdvancePercentage = dto.RequiredAdvancePercentage;
            if (!string.IsNullOrEmpty(dto.Slug)) {
                tour.Slug = dto.Slug;
            }

            // Actualizar Paradas del Itinerario
            // Eliminar directamente desde la base de datos sin tocar la coleccion en memoria
            await _context.Set<ItineraryStop>().Where(s => s.TourPackageId == tour.Id).ExecuteDeleteAsync();
            if (Stops != null) {
                for (int i = 0; i < Stops.Length; i++) {
                    if (!string.IsNullOrWhiteSpace(Stops[i]))
                        _context.Set<ItineraryStop>().Add(new ItineraryStop { Id = Guid.NewGuid(), TourPackageId = tour.Id, Name = Stops[i], OrderIndex = i, EstimatedTime = StopTimes?.ElementAtOrDefault(i) });
                }
            }

            // Actualizar Inclusiones del Tour
            await _context.Set<TourInclusion>().Where(i => i.TourPackageId == tour.Id).ExecuteDeleteAsync();
            if (Inclusions != null) {
                foreach (var inc in Inclusions) {
                    _context.Set<TourInclusion>().Add(new TourInclusion { Id = Guid.NewGuid(), TourPackageId = tour.Id, Description = inc });
                }
            }

            // Actualizar Turnos del Tour
            await _context.Set<TourShift>().Where(s => s.TourPackageId == tour.Id).ExecuteDeleteAsync();
            if (ShiftNames != null) {
                for (int i = 0; i < ShiftNames.Length; i++) {
                    if (!string.IsNullOrWhiteSpace(ShiftNames[i])) {
                        _context.Set<TourShift>().Add(new TourShift {
                            Id = Guid.NewGuid(),
                            TourPackageId = tour.Id,
                            ShiftName = ShiftNames[i],
                            StartTime = TimeSpan.TryParse(ShiftStarts?.ElementAtOrDefault(i), out var st) ? st : TimeSpan.Zero,
                            EndTime = TimeSpan.TryParse(ShiftEnds?.ElementAtOrDefault(i), out var et) ? et : TimeSpan.Zero,
                            IsActive = true
                        });
                    }
                }
            }

            await ProcessImages(tour, ImageFiles);

            // _context.TourPackages.Update(tour); // Removed to prevent state overriding
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Tours));
        }

        [HttpPost]
        public async Task<IActionResult> DeleteTour(Guid id)
        {
            var tour = await _context.TourPackages.FindAsync(id);
            if (tour != null)
            {
                _context.TourPackages.Remove(tour);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Tours));
        }

        [HttpPost]
        public async Task<IActionResult> DeleteTourImage(Guid id)
        {
            var image = await _context.Set<TourImage>().FindAsync(id);
            if (image != null)
            {
                _context.Set<TourImage>().Remove(image);
                await _context.SaveChangesAsync();
                
                try {
                    var filePath = Path.Combine(_env.WebRootPath, image.ImageUrl.TrimStart('/').Replace('/', '\\'));
                    if (System.IO.File.Exists(filePath))
                        System.IO.File.Delete(filePath);
                } catch { } // ignore file delete errors
            }
            return Ok();
        }

        private async Task ProcessImages(TourPackage tour, IFormFileCollection files)
        {
            if (files != null && files.Count > 0)
            {
                var uploadsFolder = Path.Combine(_env.WebRootPath, "images", "tours");
                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                foreach (var file in files)
                {
                    if (file.Length > 0)
                    {
                        var uniqueFileName = Guid.NewGuid().ToString() + "_" + file.FileName;
                        var filePath = Path.Combine(uploadsFolder, uniqueFileName);
                        
                        using (var fileStream = new FileStream(filePath, FileMode.Create))
                        {
                            await file.CopyToAsync(fileStream);
                        }
                        
                        tour.Images.Add(new TourImage 
                        { 
                            ImageUrl = "/images/tours/" + uniqueFileName,
                            IsMain = tour.Images.Count == 0 
                        });
                    }
                }
            }
        }

        [HttpGet]
        public async Task<IActionResult> CreateBooking()
        {
            var tours = await _context.TourPackages.Include(t => t.Shifts).Where(t => t.IsActive).OrderBy(t => t.Title).ToListAsync();
            ViewBag.Tours = tours;
            
            var firstTour = tours.FirstOrDefault();
            var shifts = firstTour?.Shifts?.Where(s => s.IsActive).Select(s => s.ShiftName).ToList() ?? new List<string>();

            var vm = new SIGETOUR.API.Models.EditBookingViewModel
            {
                ReferenceCode = "NUEVA",
                TourPackageId = firstTour?.Id ?? Guid.Empty,
                TourName = firstTour?.Title ?? string.Empty,
                TourBasePrice = firstTour?.BasePrice ?? 0,
                TravelDate = DateTime.Today,
                ShiftName = shifts.FirstOrDefault() ?? "Turno Mañana",
                TotalPassengers = 1,
                TotalAmount = firstTour?.BasePrice ?? 0,
                PaidAmount = 0,
                AvailableShifts = shifts
            };
            return View(vm);
        }

        [HttpPost]
        public async Task<IActionResult> CreateBooking(SIGETOUR.API.Models.EditBookingViewModel model)
        {
            if (model.TourPackageId == Guid.Empty)
            {
                ModelState.AddModelError("TourPackageId", "Debe seleccionar un tour.");
            }

            if (!ModelState.IsValid)
            {
                ViewBag.Tours = await _context.TourPackages.Include(t => t.Shifts).Where(t => t.IsActive).OrderBy(t => t.Title).ToListAsync();
                return View(model);
            }

            var tour = await _context.TourPackages.Include(t => t.Shifts).FirstOrDefaultAsync(t => t.Id == model.TourPackageId);
            if (tour == null) {
                ModelState.AddModelError("TourPackageId", "El tour seleccionado no existe.");
                ViewBag.Tours = await _context.TourPackages.Include(t => t.Shifts).Where(t => t.IsActive).OrderBy(t => t.Title).ToListAsync();
                return View(model);
            }
            if (string.IsNullOrWhiteSpace(model.CustomerName)) {
                ModelState.AddModelError(nameof(model.CustomerName), "El nombre del cliente es obligatorio.");
                ViewBag.Tours = await _context.TourPackages.Include(t => t.Shifts).Where(t => t.IsActive).OrderBy(t => t.Title).ToListAsync();
                return View(model);
            }

            var booking = new SIGETOUR.API.Core.Entities.Booking
            {
                ReferenceCode = "RES-" + new Random().Next(1000, 9999),
                CustomerName = model.CustomerName ?? "",
                CustomerDni = model.CustomerDni ?? "",
                CustomerPhone = model.CustomerPhone ?? "",
                CustomerEmail = "manual@agencia.com",
                TravelDate = DateTime.SpecifyKind(model.TravelDate, DateTimeKind.Utc),
                ShiftName = model.ShiftName ?? "",
                PickupLocation = model.BoardingPoint ?? "Agencia Central",
                PickupCost = 0,
                TotalPassengers = model.TotalPassengers,
                TotalAmount = model.TotalAmount,
                PaidAmount = model.PaidAmount,
                PaymentMethod = model.PaymentMethod ?? "",
                OperationNumber = model.OperationNumber ?? "",
                InvoiceType = model.InvoiceType ?? "",
                InvoiceNumber = model.InvoiceNumber ?? "",
                Status = model.Status,
                AdditionalPassengersJson = model.AdditionalPassengersJson ?? "[]"
            };

            booking.Items.Add(new SIGETOUR.API.Core.Entities.BookingItem
            {
                TourPackageId = tour.Id,
                Passengers = model.TotalPassengers,
                UnitPrice = tour.BasePrice,
                SubTotal = model.TotalAmount
            });

            _context.Bookings.Add(booking);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Reserva {booking.ReferenceCode} creada exitosamente.";
            return RedirectToAction(nameof(Bookings));
        }

        [HttpGet]
        public async Task<IActionResult> EditBooking(Guid id)
        {
            var booking = await _context.Bookings
                .Include(b => b.Items)
                    .ThenInclude(i => i.TourPackage)
                        .ThenInclude(tp => tp.Shifts)
                .FirstOrDefaultAsync(b => b.Id == id);

            if (booking == null) return NotFound();

            var tours = await _context.TourPackages.Include(t => t.Shifts).Where(t => t.IsActive).OrderBy(t => t.Title).ToListAsync();
            ViewBag.Tours = tours;

            var tour = booking.Items.FirstOrDefault()?.TourPackage;
            var currentTourShifts = tour?.Shifts?.Where(s => s.IsActive).Select(s => s.ShiftName).ToList() ?? new List<string>();

            var vm = new SIGETOUR.API.Models.EditBookingViewModel
            {
                Id = booking.Id,
                ReferenceCode = booking.ReferenceCode,
                CustomerName = booking.CustomerName,
                CustomerDni = booking.CustomerDni,
                CustomerPhone = booking.CustomerPhone,
                TravelDate = booking.TravelDate,
                ShiftName = booking.ShiftName,
                BoardingPoint = booking.PickupLocation,
                TotalPassengers = booking.TotalPassengers,
                TotalAmount = booking.TotalAmount,
                PaidAmount = booking.PaidAmount,
                PaymentMethod = booking.PaymentMethod,
                OperationNumber = booking.OperationNumber,
                InvoiceType = booking.InvoiceType,
                InvoiceNumber = booking.InvoiceNumber,
                Status = booking.Status,
                AdditionalPassengersJson = booking.AdditionalPassengersJson,
                TourPackageId = tour?.Id ?? Guid.Empty,
                TourName = tour?.Title ?? "Tour Desconocido",
                TourBasePrice = tour?.BasePrice ?? 0,
                AvailableShifts = currentTourShifts
            };

            return View(vm);
        }

        [HttpPost]
        public async Task<IActionResult> EditBooking(SIGETOUR.API.Models.EditBookingViewModel model)
        {
            if (!ModelState.IsValid)
            {
                ViewBag.Tours = await _context.TourPackages.Include(t => t.Shifts).Where(t => t.IsActive).OrderBy(t => t.Title).ToListAsync();
                var selectedTourObj = await _context.TourPackages.Include(t => t.Shifts).FirstOrDefaultAsync(t => t.Id == model.TourPackageId);
                if (selectedTourObj != null)
                {
                    model.AvailableShifts = selectedTourObj.Shifts.Where(s => s.IsActive).Select(s => s.ShiftName).ToList();
                    model.TourName = selectedTourObj.Title;
                }
                return View(model);
            }

            var booking = await _context.Bookings
                .Include(b => b.Items)
                .FirstOrDefaultAsync(b => b.Id == model.Id);

            if (booking == null) return NotFound();

            var selectedTour = await _context.TourPackages.FindAsync(model.TourPackageId);

            booking.CustomerName = model.CustomerName ?? string.Empty;
            booking.CustomerDni = model.CustomerDni ?? string.Empty;
            booking.CustomerPhone = model.CustomerPhone ?? string.Empty;
            booking.TravelDate = DateTime.SpecifyKind(model.TravelDate, DateTimeKind.Utc);
            booking.ShiftName = model.ShiftName ?? string.Empty;
            booking.PickupLocation = model.BoardingPoint ?? string.Empty;
            booking.TotalPassengers = model.TotalPassengers;
            booking.TotalAmount = model.TotalAmount;
            booking.PaidAmount = model.PaidAmount;
            booking.PaymentMethod = model.PaymentMethod ?? string.Empty;
            booking.OperationNumber = model.OperationNumber ?? string.Empty;
            booking.InvoiceType = model.InvoiceType ?? string.Empty;
            booking.InvoiceNumber = model.InvoiceNumber ?? string.Empty;
            booking.Status = model.Status;
            booking.AdditionalPassengersJson = string.IsNullOrWhiteSpace(model.AdditionalPassengersJson) ? "[]" : model.AdditionalPassengersJson;

            // Actualizar o reasignar el tour en BookingItem si cambió de tour
            if (selectedTour != null)
            {
                var item = booking.Items.FirstOrDefault();
                if (item != null)
                {
                    item.TourPackageId = selectedTour.Id;
                    item.UnitPrice = selectedTour.BasePrice;
                    item.Passengers = model.TotalPassengers;
                    item.SubTotal = model.TotalAmount;
                    _context.Entry(item).State = EntityState.Modified;
                }
                else
                {
                    booking.Items.Add(new BookingItem
                    {
                        Id = Guid.NewGuid(),
                        BookingId = booking.Id,
                        TourPackageId = selectedTour.Id,
                        Passengers = model.TotalPassengers,
                        UnitPrice = selectedTour.BasePrice,
                        SubTotal = model.TotalAmount
                    });
                }
            }

            _context.Bookings.Update(booking);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Reserva {booking.ReferenceCode} actualizada con éxito.";
            return RedirectToAction(nameof(Bookings));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelBooking(Guid id)
        {
            var booking = await _context.Bookings.FindAsync(id);
            if (booking == null) return NotFound();

            booking.Status = BookingStatus.Cancelled;
            _context.Bookings.Update(booking);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"La reserva {booking.ReferenceCode} ha sido anulada con éxito.";
            return RedirectToAction(nameof(Bookings));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReactivateBooking(Guid id)
        {
            var booking = await _context.Bookings.FindAsync(id);
            if (booking == null) return NotFound();

            booking.Status = BookingStatus.Confirmed;
            _context.Bookings.Update(booking);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"La reserva {booking.ReferenceCode} ha sido reactivada con éxito.";
            return RedirectToAction(nameof(EditBooking), new { id = booking.Id });
        }

        // --- FEATURED PACKAGES CRUD --- //

        public async Task<IActionResult> Featured()
        {
            var featured = await _context.FeaturedPackages
                .Include(f => f.TourPackage)
                .OrderBy(f => f.DisplayOrder)
                .ToListAsync();
            return View(featured);
        }

        [HttpGet]
        public async Task<IActionResult> EditFeatured(Guid? id)
        {
            ViewBag.Tours = await _context.TourPackages.Where(t => t.IsActive).OrderBy(t => t.Title).ToListAsync();
            if (id == null)
            {
                return View(new SIGETOUR.API.Models.FeaturedPackageDto { DisplayOrder = 1, SyncInventory = true });
            }

            var fp = await _context.FeaturedPackages.FindAsync(id);
            if (fp == null) return NotFound();

            var dto = new SIGETOUR.API.Models.FeaturedPackageDto
            {
                Id = fp.Id,
                TourPackageId = fp.TourPackageId,
                DisplayOrder = fp.DisplayOrder,
                CommercialTitle = fp.CommercialTitle,
                CommercialSubtitle = fp.CommercialSubtitle,
                PromoBadge = fp.PromoBadge,
                PromoPrice = fp.PromoPrice,
                SyncInventory = fp.SyncInventory
            };

            return View(dto);
        }

        [HttpPost]
        public async Task<IActionResult> EditFeatured(SIGETOUR.API.Models.FeaturedPackageDto dto)
        {
            ModelState.Remove("TourPackage");
            if (!ModelState.IsValid)
            {
                ViewBag.Tours = await _context.TourPackages.Where(t => t.IsActive).OrderBy(t => t.Title).ToListAsync();
                return View(dto);
            }

            if (dto.Id == Guid.Empty)
            {
                var fp = new FeaturedPackage
                {
                    Id = Guid.NewGuid(),
                    TourPackageId = dto.TourPackageId,
                    DisplayOrder = dto.DisplayOrder,
                    CommercialTitle = dto.CommercialTitle ?? string.Empty,
                    CommercialSubtitle = dto.CommercialSubtitle ?? string.Empty,
                    PromoBadge = dto.PromoBadge ?? string.Empty,
                    PromoPrice = dto.PromoPrice,
                    SyncInventory = dto.SyncInventory
                };
                _context.FeaturedPackages.Add(fp);
            }
            else
            {
                var fp = await _context.FeaturedPackages.FindAsync(dto.Id);
                if (fp == null) return NotFound();

                fp.TourPackageId = dto.TourPackageId;
                fp.DisplayOrder = dto.DisplayOrder;
                fp.CommercialTitle = dto.CommercialTitle ?? string.Empty;
                fp.CommercialSubtitle = dto.CommercialSubtitle ?? string.Empty;
                fp.PromoBadge = dto.PromoBadge ?? string.Empty;
                fp.PromoPrice = dto.PromoPrice;
                fp.SyncInventory = dto.SyncInventory;

                _context.FeaturedPackages.Update(fp);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction("Featured");
        }

        [HttpPost]
        public async Task<IActionResult> DeleteFeatured(Guid id)
        {
            var fp = await _context.FeaturedPackages.FindAsync(id);
            if (fp != null)
            {
                _context.FeaturedPackages.Remove(fp);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction("Featured");
        }

        // --- CRUD DE CATEGORÍAS DE TOURS --- //

        [HttpGet]
        public async Task<IActionResult> Categories(string? search)
        {
            var query = _context.Categories
                .Include(c => c.TourPackages)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                query = query.Where(c => c.Name.ToLower().Contains(term) || 
                                         (c.Description != null && c.Description.ToLower().Contains(term)));
            }

            var categories = await query
                .OrderBy(c => c.DisplayOrder)
                .ToListAsync();

            var dtos = categories.Select(c => new SIGETOUR.API.Models.CategoryDto
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                Icon = c.Icon,
                IsActive = c.IsActive,
                DisplayOrder = c.DisplayOrder,
                AssociatedToursCount = c.TourPackages.Count
            }).ToList();

            ViewBag.CurrentSearch = search;

            return View(dtos);
        }

        [HttpGet]
        public async Task<IActionResult> QuickSearch(string? q)
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Json(new { bookings = new object[] { }, tours = new object[] { }, vehicles = new object[] { } });
            }

            var term = q.Trim().ToLower();

            var rawBookings = await _context.Bookings
                .Where(b => b.ReferenceCode.ToLower().Contains(term) ||
                            b.CustomerName.ToLower().Contains(term) ||
                            b.CustomerDni.ToLower().Contains(term) ||
                            b.OperationNumber.ToLower().Contains(term))
                .OrderByDescending(b => b.BookingDate)
                .Take(5)
                .Select(b => new {
                    b.Id,
                    b.ReferenceCode,
                    b.CustomerName,
                    b.CustomerDni,
                    b.Status,
                    b.TotalAmount,
                    b.TravelDate
                })
                .ToListAsync();

            var bookings = rawBookings.Select(b => new {
                id = b.Id,
                reference = b.ReferenceCode,
                customer = b.CustomerName,
                dni = b.CustomerDni,
                status = b.Status.ToString(),
                amount = b.TotalAmount,
                date = b.TravelDate.ToString("dd/MM/yyyy"),
                url = Url.Action("EditBooking", "Admin", new { id = b.Id })
            });

            var rawTours = await _context.TourPackages
                .Include(t => t.Images)
                .Where(t => t.Title.ToLower().Contains(term) ||
                            t.Subtitle.ToLower().Contains(term))
                .OrderBy(t => t.Title)
                .Take(5)
                .Select(t => new {
                    t.Id,
                    t.Title,
                    t.BasePrice,
                    Images = t.Images.Select(i => new { i.ImageUrl, i.IsMain }).ToList()
                })
                .ToListAsync();

            var tours = rawTours.Select(t => new {
                id = t.Id,
                title = t.Title,
                price = t.BasePrice,
                image = t.Images.FirstOrDefault(i => i.IsMain)?.ImageUrl ?? t.Images.FirstOrDefault()?.ImageUrl ?? "/images/placeholder.jpg",
                url = Url.Action("EditTour", "Admin", new { id = t.Id })
            });

            var rawVehicles = await _context.Vehicles
                .Where(v => v.LicensePlate.ToLower().Contains(term) ||
                            v.Model.ToLower().Contains(term))
                .OrderBy(v => v.LicensePlate)
                .Take(5)
                .Select(v => new {
                    v.Id,
                    v.LicensePlate,
                    v.Model,
                    v.Status
                })
                .ToListAsync();

            var vehicles = rawVehicles.Select(v => new {
                id = v.Id,
                plate = v.LicensePlate,
                model = v.Model,
                status = v.Status,
                url = Url.Action("EditVehicle", "Admin", new { id = v.Id })
            });

            return Json(new { bookings, tours, vehicles });
        }

        [HttpGet]
        public IActionResult CreateCategory()
        {
            return View("EditCategory", new SIGETOUR.API.Models.CategoryDto
            {
                DisplayOrder = 1,
                IsActive = true,
                Icon = "landscape"
            });
        }

        [HttpGet]
        public async Task<IActionResult> EditCategory(Guid? id)
        {
            if (id == null || id == Guid.Empty)
            {
                return View(new SIGETOUR.API.Models.CategoryDto
                {
                    DisplayOrder = 1,
                    IsActive = true,
                    Icon = "landscape"
                });
            }

            var category = await _context.Categories
                .Include(c => c.TourPackages)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (category == null) return NotFound();

            var dto = new SIGETOUR.API.Models.CategoryDto
            {
                Id = category.Id,
                Name = category.Name,
                Description = category.Description,
                Icon = category.Icon ?? "landscape",
                IsActive = category.IsActive,
                DisplayOrder = category.DisplayOrder,
                AssociatedToursCount = category.TourPackages.Count
            };

            return View(dto);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditCategory(SIGETOUR.API.Models.CategoryDto dto)
        {
            if (!ModelState.IsValid)
            {
                return View(dto);
            }

            if (dto.Id == Guid.Empty)
            {
                var newCategory = new SIGETOUR.API.Core.Entities.Category
                {
                    Id = Guid.NewGuid(),
                    Name = dto.Name.Trim(),
                    Description = dto.Description?.Trim(),
                    Icon = string.IsNullOrWhiteSpace(dto.Icon) ? "landscape" : dto.Icon.Trim(),
                    IsActive = dto.IsActive,
                    DisplayOrder = dto.DisplayOrder,
                    CreatedAt = DateTime.UtcNow
                };
                _context.Categories.Add(newCategory);
            }
            else
            {
                var category = await _context.Categories.FindAsync(dto.Id);
                if (category == null) return NotFound();

                category.Name = dto.Name.Trim();
                category.Description = dto.Description?.Trim();
                category.Icon = string.IsNullOrWhiteSpace(dto.Icon) ? "landscape" : dto.Icon.Trim();
                category.IsActive = dto.IsActive;
                category.DisplayOrder = dto.DisplayOrder;

                _context.Categories.Update(category);
            }

            await _context.SaveChangesAsync();
            TempData["SuccessMessage"] = "Categoría guardada exitosamente.";
            return RedirectToAction(nameof(Categories));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteCategory(Guid id)
        {
            var category = await _context.Categories.FindAsync(id);
            if (category != null)
            {
                var tours = await _context.TourPackages.Where(t => t.CategoryId == id).ToListAsync();
                foreach (var t in tours)
                {
                    t.CategoryId = null;
                }

                _context.Categories.Remove(category);
                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = "Categoría eliminada con éxito.";
            }
            return RedirectToAction(nameof(Categories));
        }
    }
}
















