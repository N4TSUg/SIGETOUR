using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
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

        public AdminController(AppDbContext context, UserManager<ApplicationUser> userManager, IWebHostEnvironment env)
        {
            _context = context;
            _userManager = userManager;
            _env = env;
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

        public async Task<IActionResult> Tours()
        {
            var tours = await _context.TourPackages
                .Include(t => t.Images)
                .OrderBy(t => t.Title)
                .ToListAsync();
            return View(tours);
        }

        public async Task<IActionResult> Bookings()
        {
            var bookings = await _context.Bookings
                .Include(b => b.Items)
                .ThenInclude(i => i.TourPackage)
                .OrderByDescending(b => b.BookingDate)
                .ToListAsync();
            return View(bookings);
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
                    IsActive = dto.Status == "Operativo en Ruta (En Servicio)",
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
                vehicle.IsActive = dto.Status == "Operativo en Ruta (En Servicio)";
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

        public async Task<IActionResult> Fleet()
        {
            var vehicles = await _context.Vehicles
                .Include(v => v.Images)
                .ToListAsync();
            return View(vehicles);
        }

        public async Task<IActionResult> Users()
        {
            var users = await _userManager.Users.ToListAsync();
            return View(users);
        }

        // --- NEW CRUD METHODS FOR TOURS --- //

        [HttpGet]
        public IActionResult CreateTour()
        {
            return View(new TourPackage());
        }

        [HttpPost]
        public async Task<IActionResult> CreateTour(TourPackage model, IFormFileCollection ImageFiles, string[] Stops, string[] StopTimes, string[] Inclusions, string[] ShiftNames, string[] ShiftStarts, string[] ShiftEnds)
        {
            model.Id = Guid.NewGuid();
            model.Subtitle ??= string.Empty;
            model.Description ??= string.Empty;
            if (string.IsNullOrEmpty(model.Slug)) {
                model.Slug = model.Title.ToLower().Replace(" ", "-");
            }
            
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
                .Include(t => t.Images)
                .Include(t => t.Inclusions)
                .Include(t => t.ItineraryStops)
                .Include(t => t.Shifts)
                .FirstOrDefaultAsync(t => t.Id == id);
                
            if (tour == null) return NotFound();
            
            return View("CreateTour", tour);
        }

        [HttpPost]
        public async Task<IActionResult> EditTour(TourPackage model, IFormFileCollection ImageFiles, string[] Stops, string[] StopTimes, string[] Inclusions, string[] ShiftNames, string[] ShiftStarts, string[] ShiftEnds)
        {
            var tour = await _context.TourPackages
                .Include(t => t.Images)
                .Include(t => t.ItineraryStops)
                .Include(t => t.Images)
                .Include(t => t.Inclusions)
                .FirstOrDefaultAsync(t => t.Id == model.Id);
                
            if (tour == null) return NotFound();

            tour.Title = model.Title ?? string.Empty;
            tour.Subtitle = model.Subtitle ?? string.Empty;
            tour.Description = model.Description ?? string.Empty;
            tour.BasePrice = model.BasePrice;
            tour.ChildPrice = model.ChildPrice;
            tour.MaxCapacity = model.MaxCapacity;
            tour.Duration = model.Duration ?? string.Empty;
            tour.Category = model.Category;
            tour.Modality = model.Modality;
            tour.Difficulty = model.Difficulty;
            tour.IsActive = model.IsActive;
            tour.RequiredAdvancePercentage = model.RequiredAdvancePercentage;
            if (!string.IsNullOrEmpty(model.Slug)) {
                tour.Slug = model.Slug;
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
            var tours = await _context.TourPackages.Where(t => t.IsActive).ToListAsync();
            ViewBag.Tours = tours;
            
            var vm = new SIGETOUR.API.Models.EditBookingViewModel
            {
                ReferenceCode = "NUEVA",
                TravelDate = DateTime.Today,
                TotalPassengers = 1,
                TotalAmount = 0,
                PaidAmount = 0
            };
            return View(vm);
        }

        [HttpPost]
        public async Task<IActionResult> CreateBooking(SIGETOUR.API.Models.EditBookingViewModel model, Guid TourId)
        {
            var tour = await _context.TourPackages.FindAsync(TourId);
            if (tour == null) {
                ModelState.AddModelError("", "Tour no seleccionado.");
                ViewBag.Tours = await _context.TourPackages.Where(t => t.IsActive).ToListAsync();
                return View(model);
            }
            if (string.IsNullOrWhiteSpace(model.CustomerName)) {
                ModelState.AddModelError("", "El nombre del cliente es obligatorio.");
                ViewBag.Tours = await _context.TourPackages.Where(t => t.IsActive).ToListAsync();
                return View(model);
            }
            if (tour == null) return NotFound();

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

            return RedirectToAction("Dashboard");
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

            var tour = booking.Items.FirstOrDefault()?.TourPackage;

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
                TourName = tour?.Title ?? "Tour Desconocido",
                AvailableShifts = tour?.Shifts?.Select(s => s.ShiftName).ToList() ?? new List<string>()
            };

            return View(vm);
        }

        [HttpPost]
        public async Task<IActionResult> EditBooking(SIGETOUR.API.Models.EditBookingViewModel model)
        {
            var booking = await _context.Bookings.FindAsync(model.Id);
            if (booking == null) return NotFound();

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

            _context.Bookings.Update(booking);
            await _context.SaveChangesAsync();

            // Redirect back to Dashboard or the Bookings list
            return RedirectToAction("Dashboard");
        }
    }
}









