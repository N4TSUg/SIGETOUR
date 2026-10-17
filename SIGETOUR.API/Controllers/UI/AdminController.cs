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
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IWebHostEnvironment _env;
        private readonly IManifestExportService _manifestService;
        private readonly ICashReportExportService _cashReportService;

        public AdminController(
            AppDbContext context, 
            UserManager<ApplicationUser> userManager, 
            RoleManager<IdentityRole> roleManager,
            IWebHostEnvironment env, 
            IManifestExportService manifestService,
            ICashReportExportService cashReportService)
        {
            _context = context;
            _userManager = userManager;
            _roleManager = roleManager;
            _env = env;
            _manifestService = manifestService;
            _cashReportService = cashReportService;
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

        public async Task<IActionResult> Tours(string? search, Guid? categoryId, string? status, int page = 1)
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

            int totalItems = await query.CountAsync();
            int pageSize = 12;
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalItems / pageSize));
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            var tours = await query.OrderBy(t => t.Title)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ToListAsync();
            ViewBag.CurrentSearch = search;
            ViewBag.CurrentCategoryId = categoryId;
            ViewBag.CurrentStatus = status;

            var routeParams = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(search)) routeParams["search"] = search;
            if (categoryId.HasValue && categoryId.Value != Guid.Empty) routeParams["categoryId"] = categoryId.Value.ToString();
            if (!string.IsNullOrWhiteSpace(status)) routeParams["status"] = status;

            ViewBag.Pagination = new PaginationViewModel
            {
                Action = "Tours",
                Controller = "Admin",
                CurrentPage = page,
                TotalPages = totalPages,
                TotalItems = totalItems,
                PageSize = pageSize,
                RouteParams = routeParams
            };

            return View(tours);
        }

        public async Task<IActionResult> Bookings(string? search, string? status, DateTime? date, Guid? tourId, int page = 1)
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
                var targetDate = date.Value.Date;
                var minUtc = DateTime.SpecifyKind(targetDate.AddDays(-1), DateTimeKind.Utc);
                var maxUtc = DateTime.SpecifyKind(targetDate.AddDays(2), DateTimeKind.Utc);
                query = query.Where(b => b.TravelDate >= minUtc && b.TravelDate <= maxUtc);
            }

            if (tourId.HasValue && tourId != Guid.Empty)
            {
                query = query.Where(b => b.Items.Any(i => i.TourPackageId == tourId.Value));
            }

            var bookings = await query
                .OrderByDescending(b => b.BookingDate)
                .ToListAsync();

            if (date.HasValue)
            {
                var targetDateStr = date.Value.ToString("yyyy-MM-dd");
                bookings = bookings.Where(b =>
                    b.TravelDate.ToString("yyyy-MM-dd") == targetDateStr ||
                    b.TravelDate.ToUniversalTime().ToString("yyyy-MM-dd") == targetDateStr ||
                    b.TravelDate.AddHours(-5).ToString("yyyy-MM-dd") == targetDateStr ||
                    b.TravelDate.Date == date.Value.Date
                ).ToList();
            }

            int totalItems = bookings.Count;
            int pageSize = 12;
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalItems / pageSize));
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            var pagedBookings = bookings.Skip((page - 1) * pageSize).Take(pageSize).ToList();

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

            var routeParams = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(search)) routeParams["search"] = search;
            if (!string.IsNullOrWhiteSpace(status)) routeParams["status"] = status;
            if (date.HasValue) routeParams["date"] = date.Value.ToString("yyyy-MM-dd");
            if (tourId.HasValue && tourId.Value != Guid.Empty) routeParams["tourId"] = tourId.Value.ToString();

            ViewBag.Pagination = new PaginationViewModel
            {
                Action = "Bookings",
                Controller = "Admin",
                CurrentPage = page,
                TotalPages = totalPages,
                TotalItems = totalItems,
                PageSize = pageSize,
                RouteParams = routeParams
            };

            return View(pagedBookings);
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

        public async Task<IActionResult> Fleet(string? search, string? status, int page = 1)
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

            int totalItems = await query.CountAsync();
            int pageSize = 12;
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalItems / pageSize));
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            var vehicles = await query
                .OrderBy(v => v.LicensePlate)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentSearch = search;
            ViewBag.CurrentStatus = status;

            var routeParams = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(search)) routeParams["search"] = search;
            if (!string.IsNullOrWhiteSpace(status)) routeParams["status"] = status;

            ViewBag.Pagination = new PaginationViewModel
            {
                Action = "Fleet",
                Controller = "Admin",
                CurrentPage = page,
                TotalPages = totalPages,
                TotalItems = totalItems,
                PageSize = pageSize,
                RouteParams = routeParams
            };

            return View(vehicles);
        }

        public async Task<IActionResult> Users(string? search, int page = 1)
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

            int totalItems = await query.CountAsync();
            int pageSize = 12;
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalItems / pageSize));
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            var users = await query
                .OrderBy(u => u.UserName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Cargar roles de cada usuario
            var userRoles = new Dictionary<string, string>();
            foreach (var u in users)
            {
                var roles = await _userManager.GetRolesAsync(u);
                userRoles[u.Id] = roles.FirstOrDefault() ?? "Operador";
            }

            ViewBag.UserRoles = userRoles;

            // Catálogo oficial de roles: SuperAdmin, Admin, Operador
            string[] allowedRoles = { "SuperAdmin", "Admin", "Operador" };
            foreach (var r in allowedRoles)
            {
                if (!await _roleManager.RoleExistsAsync(r))
                {
                    await _roleManager.CreateAsync(new IdentityRole(r));
                }
            }
            ViewBag.AvailableRoles = allowedRoles.ToList();

            ViewBag.CurrentUserId = _userManager.GetUserId(User);
            ViewBag.CurrentUserName = User.Identity?.Name ?? "";
            ViewBag.CurrentSearch = search;

            var routeParams = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(search)) routeParams["search"] = search;

            ViewBag.Pagination = new PaginationViewModel
            {
                Action = "Users",
                Controller = "Admin",
                CurrentPage = page,
                TotalPages = totalPages,
                TotalItems = totalItems,
                PageSize = pageSize,
                RouteParams = routeParams
            };

            return View(users);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateUser(CreateUserViewModel model)
        {
            if (!ModelState.IsValid)
            {
                var firstError = ModelState.Values.SelectMany(v => v.Errors).FirstOrDefault()?.ErrorMessage ?? "Datos del formulario inválidos.";
                TempData["ErrorMessage"] = firstError;
                return RedirectToAction(nameof(Users));
            }

            var cleanUserName = model.UserName.Trim();
            var cleanEmail = model.Email.Trim();

            if (string.Equals(cleanUserName, "superadmin", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "El nombre de usuario 'superadmin' es exclusivo del sistema y no puede ser registrado.";
                return RedirectToAction(nameof(Users));
            }

            var existingByName = await _userManager.FindByNameAsync(cleanUserName);
            if (existingByName != null)
            {
                TempData["ErrorMessage"] = $"El nombre de usuario '{cleanUserName}' ya se encuentra registrado.";
                return RedirectToAction(nameof(Users));
            }

            var existingByEmail = await _userManager.FindByEmailAsync(cleanEmail);
            if (existingByEmail != null)
            {
                TempData["ErrorMessage"] = $"El correo '{cleanEmail}' ya se encuentra registrado.";
                return RedirectToAction(nameof(Users));
            }

            var user = new ApplicationUser
            {
                UserName = cleanUserName,
                Email = cleanEmail,
                FirstName = model.FirstName.Trim(),
                LastName = model.LastName.Trim(),
                PhoneNumber = model.PhoneNumber?.Trim(),
                EmailConfirmed = true,
                IsActive = model.IsActive
            };

            var result = await _userManager.CreateAsync(user, model.Password);
            if (!result.Succeeded)
            {
                var errorMsg = string.Join("; ", result.Errors.Select(e => e.Description));
                TempData["ErrorMessage"] = $"Error al registrar usuario: {errorMsg}";
                return RedirectToAction(nameof(Users));
            }

            var roleName = model.Role?.Trim();
            if (roleName != "SuperAdmin" && roleName != "Admin" && roleName != "Operador")
            {
                roleName = "Operador";
            }

            var currentUserName = User.Identity?.Name ?? "";
            bool isLoggedAsSuperAdmin = string.Equals(currentUserName, "superadmin", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(roleName, "SuperAdmin", StringComparison.OrdinalIgnoreCase) && !isLoggedAsSuperAdmin)
            {
                TempData["ErrorMessage"] = "Solo el SuperAdmin tiene autorización para asignar el rol SuperAdmin a otro usuario.";
                return RedirectToAction(nameof(Users));
            }

            if (!await _roleManager.RoleExistsAsync(roleName))
            {
                await _roleManager.CreateAsync(new IdentityRole(roleName));
            }
            await _userManager.AddToRoleAsync(user, roleName);

            TempData["SuccessMessage"] = $"Usuario '{user.FirstName} {user.LastName}' (@{user.UserName}) registrado con éxito con rol {roleName}.";
            return RedirectToAction(nameof(Users));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditUser(EditUserViewModel model)
        {
            if (!ModelState.IsValid)
            {
                var firstError = ModelState.Values.SelectMany(v => v.Errors).FirstOrDefault()?.ErrorMessage ?? "Datos del formulario inválidos.";
                TempData["ErrorMessage"] = firstError;
                return RedirectToAction(nameof(Users));
            }

            var user = await _userManager.FindByIdAsync(model.Id);
            if (user == null)
            {
                TempData["ErrorMessage"] = "El usuario seleccionado no existe.";
                return RedirectToAction(nameof(Users));
            }

            var currentUserName = User.Identity?.Name ?? "";
            var isTargetSuperAdmin = string.Equals(user.UserName, "superadmin", StringComparison.OrdinalIgnoreCase);

            // Regla de seguridad estricta: Cualquier tipo de edición del superadmin
            // que no sea realizada por el propio superadmin no es permisible para nadie.
            if (isTargetSuperAdmin && !string.Equals(currentUserName, "superadmin", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "Acción no permisible: La cuenta 'superadmin' únicamente puede ser modificada por el propio SuperAdmin.";
                return RedirectToAction(nameof(Users));
            }

            var cleanUserName = model.UserName.Trim();
            var cleanEmail = model.Email.Trim();

            if (!string.Equals(user.UserName, cleanUserName, StringComparison.OrdinalIgnoreCase))
            {
                if (isTargetSuperAdmin)
                {
                    TempData["ErrorMessage"] = "El nombre de usuario de la cuenta raíz 'superadmin' es inmutable y no puede ser cambiado.";
                    return RedirectToAction(nameof(Users));
                }

                if (string.Equals(cleanUserName, "superadmin", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["ErrorMessage"] = "El nombre de usuario 'superadmin' está reservado y protegido por el sistema.";
                    return RedirectToAction(nameof(Users));
                }

                var existingByName = await _userManager.FindByNameAsync(cleanUserName);
                if (existingByName != null && existingByName.Id != user.Id)
                {
                    TempData["ErrorMessage"] = $"El nombre de usuario '{cleanUserName}' ya está en uso.";
                    return RedirectToAction(nameof(Users));
                }
                user.UserName = cleanUserName;
            }

            if (!string.Equals(user.Email, cleanEmail, StringComparison.OrdinalIgnoreCase))
            {
                var existingByEmail = await _userManager.FindByEmailAsync(cleanEmail);
                if (existingByEmail != null && existingByEmail.Id != user.Id)
                {
                    TempData["ErrorMessage"] = $"El correo '{cleanEmail}' ya está en uso.";
                    return RedirectToAction(nameof(Users));
                }
                user.Email = cleanEmail;
            }

            user.FirstName = model.FirstName.Trim();
            user.LastName = model.LastName.Trim();
            user.PhoneNumber = model.PhoneNumber?.Trim();
            
            // La cuenta raíz 'superadmin' siempre debe permanecer activa
            if (isTargetSuperAdmin)
            {
                user.IsActive = true;
            }
            else
            {
                user.IsActive = model.IsActive;
            }

            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                var errorMsg = string.Join("; ", updateResult.Errors.Select(e => e.Description));
                TempData["ErrorMessage"] = $"Error al actualizar datos: {errorMsg}";
                return RedirectToAction(nameof(Users));
            }

            // Cambiar contraseña si se especificó una nueva
            if (!string.IsNullOrWhiteSpace(model.NewPassword))
            {
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var passResult = await _userManager.ResetPasswordAsync(user, token, model.NewPassword);
                if (!passResult.Succeeded)
                {
                    var passError = string.Join("; ", passResult.Errors.Select(e => e.Description));
                    TempData["ErrorMessage"] = $"Datos actualizados, pero no se pudo cambiar la contraseña: {passError}";
                    return RedirectToAction(nameof(Users));
                }
            }

            // REGLAS ESTRICTAS DE SEGURIDAD PARA CAMBIO DE ROLES:
            var currentUserId = _userManager.GetUserId(User);
            var isSelf = (user.Id == currentUserId || string.Equals(user.UserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase));

            var currentRoles = await _userManager.GetRolesAsync(user);
            string roleName = model.Role?.Trim() ?? "Operador";
            if (roleName != "SuperAdmin" && roleName != "Admin" && roleName != "Operador")
            {
                roleName = "Operador";
            }

            // 1. Proteger usuario raíz 'superadmin': Su rol es INMUTABLE
            if (isTargetSuperAdmin)
            {
                roleName = "SuperAdmin";
            }
            // 2. Proteger la propia cuenta en sesión: No puede cambiar o degradar su propio rol
            else if (isSelf)
            {
                roleName = currentRoles.FirstOrDefault() ?? "Admin";
            }
            // 3. Proteger elevación a SuperAdmin: Solo el propio SuperAdmin puede asignar SuperAdmin a otros
            else if (string.Equals(roleName, "SuperAdmin", StringComparison.OrdinalIgnoreCase) && !string.Equals(currentUserName, "superadmin", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "Solo el SuperAdmin tiene autorización para asignar el rol SuperAdmin a otro usuario.";
                return RedirectToAction(nameof(Users));
            }

            // Aplicar el cambio de rol seguro si difiere del actual
            if (!currentRoles.Contains(roleName))
            {
                if (!await _roleManager.RoleExistsAsync(roleName))
                {
                    await _roleManager.CreateAsync(new IdentityRole(roleName));
                }

                if (currentRoles.Any())
                {
                    await _userManager.RemoveFromRolesAsync(user, currentRoles);
                }
                await _userManager.AddToRoleAsync(user, roleName);
            }

            TempData["SuccessMessage"] = $"Usuario '{user.FirstName} {user.LastName}' actualizado exitosamente con rol {roleName}.";
            return RedirectToAction(nameof(Users));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUser(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                TempData["ErrorMessage"] = "El usuario seleccionado no existe.";
                return RedirectToAction(nameof(Users));
            }

            var currentUserId = _userManager.GetUserId(User);
            if (user.Id == currentUserId || string.Equals(user.UserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "No puedes eliminar tu propia cuenta de usuario mientras tengas la sesión activa.";
                return RedirectToAction(nameof(Users));
            }

            if (string.Equals(user.UserName, "superadmin", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "La cuenta principal 'superadmin' está protegida por el sistema y no puede ser eliminada.";
                return RedirectToAction(nameof(Users));
            }

            var result = await _userManager.DeleteAsync(user);
            if (!result.Succeeded)
            {
                var errorMsg = string.Join("; ", result.Errors.Select(e => e.Description));
                TempData["ErrorMessage"] = $"Error al eliminar usuario: {errorMsg}";
                return RedirectToAction(nameof(Users));
            }

            TempData["SuccessMessage"] = $"Usuario '{user.FirstName} {user.LastName}' (@{user.UserName}) eliminado correctamente.";
            return RedirectToAction(nameof(Users));
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

            // Validar Categoría para evitar violación de Foreign Key si el ID no existe en la BD
            Guid? validCategoryId = null;
            if (dto.CategoryId.HasValue && dto.CategoryId.Value != Guid.Empty)
            {
                var categoryExists = await _context.Categories.AnyAsync(c => c.Id == dto.CategoryId.Value);
                if (categoryExists)
                {
                    validCategoryId = dto.CategoryId.Value;
                }
                else
                {
                    var fallbackCat = await _context.Categories.FirstOrDefaultAsync(c => c.IsActive);
                    validCategoryId = fallbackCat?.Id;
                }
            }

            // Validar Vehículo por defecto si se especificó
            Guid? validVehicleId = null;
            if (dto.DefaultVehicleId.HasValue && dto.DefaultVehicleId.Value != Guid.Empty)
            {
                var vehicleExists = await _context.Vehicles.AnyAsync(v => v.Id == dto.DefaultVehicleId.Value);
                if (vehicleExists) validVehicleId = dto.DefaultVehicleId.Value;
            }

            var model = new SIGETOUR.API.Core.Entities.TourPackage
            {
                Id = Guid.NewGuid(),
                Title = dto.Title?.Trim() ?? string.Empty,
                Subtitle = dto.Subtitle?.Trim() ?? string.Empty,
                CategoryId = validCategoryId,
                Modality = dto.Modality,
                Difficulty = dto.Difficulty,
                Description = dto.Description?.Trim() ?? string.Empty,
                BasePrice = dto.BasePrice,
                ChildPrice = dto.ChildPrice,
                MaxCapacity = dto.MaxCapacity,
                RequiredAdvancePercentage = dto.RequiredAdvancePercentage,
                Duration = dto.Duration?.Trim() ?? string.Empty,
                DefaultVehicleId = validVehicleId,
                IsActive = true
            };

            var baseSlug = !string.IsNullOrWhiteSpace(dto.Slug) ? dto.Slug.Trim() : model.Title;
            var cleanSlug = System.Text.RegularExpressions.Regex.Replace(baseSlug.ToLower(), @"[^a-z0-9\-]", "-").Trim('-');
            model.Slug = string.IsNullOrWhiteSpace(cleanSlug) ? Guid.NewGuid().ToString("N").Substring(0, 8) : cleanSlug;
            
            if (Stops != null) {
                for (int i = 0; i < Stops.Length; i++) {
                    if (!string.IsNullOrWhiteSpace(Stops[i]))
                        model.ItineraryStops.Add(new ItineraryStop { 
                            Id = Guid.NewGuid(), 
                            TourPackageId = model.Id,
                            Name = Stops[i].Trim(), 
                            OrderIndex = i, 
                            EstimatedTime = StopTimes?.ElementAtOrDefault(i)?.Trim() 
                        });
                }
            }
            
            if (Inclusions != null) {
                foreach (var inc in Inclusions) {
                    if (!string.IsNullOrWhiteSpace(inc))
                        model.Inclusions.Add(new TourInclusion { 
                            Id = Guid.NewGuid(), 
                            TourPackageId = model.Id,
                            Description = inc.Trim() 
                        });
                }
            }
            
            if (ShiftNames != null) {
                for (int i = 0; i < ShiftNames.Length; i++) {
                    if (!string.IsNullOrWhiteSpace(ShiftNames[i])) {
                        model.Shifts.Add(new TourShift {
                            Id = Guid.NewGuid(),
                            TourPackageId = model.Id,
                            ShiftName = ShiftNames[i].Trim(),
                            StartTime = TimeSpan.TryParse(ShiftStarts?.ElementAtOrDefault(i), out var st) ? st : TimeSpan.Zero,
                            EndTime = TimeSpan.TryParse(ShiftEnds?.ElementAtOrDefault(i), out var et) ? et : TimeSpan.Zero,
                            IsActive = true
                        });
                    }
                }
            }

            await ProcessImages(model, ImageFiles);
            
            try
            {
                _context.TourPackages.Add(model);
                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = $"Circuito '{model.Title}' registrado y publicado exitosamente.";
                return RedirectToAction(nameof(Tours));
            }
            catch (Exception ex)
            {
                var detailedError = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                ModelState.AddModelError("", $"No se pudo guardar el circuito en la base de datos: {detailedError}");
                TempData["ErrorMessage"] = $"Error al registrar circuito: {detailedError}";
                ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ToListAsync();
                return View("CreateTour", dto);
            }
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
                .Include(t => t.Shifts)
                .FirstOrDefaultAsync(t => t.Id == dto.Id);
                
            if (tour == null) return NotFound();

            // Validar Categoría para evitar violación de Foreign Key si el ID no existe en la BD
            Guid? validCategoryId = null;
            if (dto.CategoryId.HasValue && dto.CategoryId.Value != Guid.Empty)
            {
                var categoryExists = await _context.Categories.AnyAsync(c => c.Id == dto.CategoryId.Value);
                if (categoryExists)
                {
                    validCategoryId = dto.CategoryId.Value;
                }
                else
                {
                    var fallbackCat = await _context.Categories.FirstOrDefaultAsync(c => c.IsActive);
                    validCategoryId = fallbackCat?.Id;
                }
            }

            // Validar Vehículo por defecto si se especificó
            Guid? validVehicleId = null;
            if (dto.DefaultVehicleId.HasValue && dto.DefaultVehicleId.Value != Guid.Empty)
            {
                var vehicleExists = await _context.Vehicles.AnyAsync(v => v.Id == dto.DefaultVehicleId.Value);
                if (vehicleExists) validVehicleId = dto.DefaultVehicleId.Value;
            }

            tour.Title = dto.Title?.Trim() ?? string.Empty;
            tour.Subtitle = dto.Subtitle?.Trim() ?? string.Empty;
            tour.Description = dto.Description?.Trim() ?? string.Empty;
            tour.BasePrice = dto.BasePrice;
            tour.ChildPrice = dto.ChildPrice;
            tour.MaxCapacity = dto.MaxCapacity;
            tour.Duration = dto.Duration?.Trim() ?? string.Empty;
            tour.CategoryId = validCategoryId;
            tour.DefaultVehicleId = validVehicleId;
            tour.Modality = dto.Modality;
            tour.Difficulty = dto.Difficulty;
            tour.IsActive = dto.IsActive;
            tour.RequiredAdvancePercentage = dto.RequiredAdvancePercentage;
            if (!string.IsNullOrEmpty(dto.Slug)) {
                var cleanSlug = System.Text.RegularExpressions.Regex.Replace(dto.Slug.Trim().ToLower(), @"[^a-z0-9\-]", "-").Trim('-');
                tour.Slug = string.IsNullOrWhiteSpace(cleanSlug) ? tour.Slug : cleanSlug;
            }

            // Actualizar Paradas del Itinerario sincronizando ChangeTracker
            _context.ItineraryStops.RemoveRange(tour.ItineraryStops);
            tour.ItineraryStops.Clear();
            if (Stops != null) {
                for (int i = 0; i < Stops.Length; i++) {
                    if (!string.IsNullOrWhiteSpace(Stops[i]))
                    {
                        var newStop = new ItineraryStop { 
                            Id = Guid.NewGuid(), 
                            TourPackageId = tour.Id, 
                            Name = Stops[i].Trim(), 
                            OrderIndex = i, 
                            EstimatedTime = StopTimes?.ElementAtOrDefault(i)?.Trim() 
                        };
                        _context.ItineraryStops.Add(newStop);
                        tour.ItineraryStops.Add(newStop);
                    }
                }
            }

            // Actualizar Inclusiones del Tour sincronizando ChangeTracker
            _context.TourInclusions.RemoveRange(tour.Inclusions);
            tour.Inclusions.Clear();
            if (Inclusions != null) {
                foreach (var inc in Inclusions) {
                    if (!string.IsNullOrWhiteSpace(inc))
                    {
                        var newInc = new TourInclusion { 
                            Id = Guid.NewGuid(), 
                            TourPackageId = tour.Id, 
                            Description = inc.Trim() 
                        };
                        _context.TourInclusions.Add(newInc);
                        tour.Inclusions.Add(newInc);
                    }
                }
            }

            // Actualizar Turnos del Tour sincronizando ChangeTracker
            _context.TourShifts.RemoveRange(tour.Shifts);
            tour.Shifts.Clear();
            if (ShiftNames != null) {
                for (int i = 0; i < ShiftNames.Length; i++) {
                    if (!string.IsNullOrWhiteSpace(ShiftNames[i])) {
                        var newShift = new TourShift {
                            Id = Guid.NewGuid(),
                            TourPackageId = tour.Id,
                            ShiftName = ShiftNames[i].Trim(),
                            StartTime = TimeSpan.TryParse(ShiftStarts?.ElementAtOrDefault(i), out var st) ? st : TimeSpan.Zero,
                            EndTime = TimeSpan.TryParse(ShiftEnds?.ElementAtOrDefault(i), out var et) ? et : TimeSpan.Zero,
                            IsActive = true
                        };
                        _context.TourShifts.Add(newShift);
                        tour.Shifts.Add(newShift);
                    }
                }
            }

            await ProcessImages(tour, ImageFiles);

            try
            {
                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = $"Circuito '{tour.Title}' actualizado exitosamente.";
                return RedirectToAction(nameof(Tours));
            }
            catch (Exception ex)
            {
                var detailedError = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                ModelState.AddModelError("", $"No se pudo actualizar el circuito en la base de datos: {detailedError}");
                TempData["ErrorMessage"] = $"Error al actualizar circuito: {detailedError}";
                ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ToListAsync();
                return View("CreateTour", dto);
            }
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
                        
                        var newImg = new TourImage 
                        { 
                            Id = Guid.NewGuid(),
                            TourPackageId = tour.Id,
                            ImageUrl = "/images/tours/" + uniqueFileName,
                            IsMain = tour.Images.Count == 0 
                        };
                        _context.TourImages.Add(newImg);
                        tour.Images.Add(newImg);
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

            if (booking.PaidAmount > 0)
            {
                var activeShift = await _context.CashRegisterShifts.FirstOrDefaultAsync(s => s.IsOpen);
                if (activeShift != null)
                {
                    _context.CashTransactions.Add(new SIGETOUR.API.Core.Entities.CashTransaction
                    {
                        Id = Guid.NewGuid(),
                        ShiftId = activeShift.Id,
                        TransactionType = "Ingreso",
                        Category = "Cobro de Reserva",
                        Amount = booking.PaidAmount,
                        PaymentMethod = !string.IsNullOrWhiteSpace(booking.PaymentMethod) ? booking.PaymentMethod : "Efectivo",
                        ReferenceCode = booking.ReferenceCode,
                        BookingId = booking.Id,
                        Description = $"Cobro reserva {booking.ReferenceCode} - {booking.CustomerName}",
                        RegisteredBy = User.Identity?.Name ?? "Counter / Admin",
                        CreatedAt = DateTime.UtcNow
                    });
                    await _context.SaveChangesAsync();
                }
            }

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

            if (booking.PaidAmount > 0 && !_context.CashTransactions.Any(t => t.BookingId == booking.Id))
            {
                var activeShift = await _context.CashRegisterShifts.FirstOrDefaultAsync(s => s.IsOpen);
                if (activeShift != null)
                {
                    _context.CashTransactions.Add(new SIGETOUR.API.Core.Entities.CashTransaction
                    {
                        Id = Guid.NewGuid(),
                        ShiftId = activeShift.Id,
                        TransactionType = "Ingreso",
                        Category = "Cobro de Reserva",
                        Amount = booking.PaidAmount,
                        PaymentMethod = !string.IsNullOrWhiteSpace(booking.PaymentMethod) ? booking.PaymentMethod : "Efectivo",
                        ReferenceCode = booking.ReferenceCode,
                        BookingId = booking.Id,
                        Description = $"Cobro registrado en edición de reserva {booking.ReferenceCode} - {booking.CustomerName}",
                        RegisteredBy = User.Identity?.Name ?? "Counter / Admin",
                        CreatedAt = DateTime.UtcNow
                    });
                    await _context.SaveChangesAsync();
                }
            }

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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteBooking(Guid id)
        {
            var booking = await _context.Bookings
                .Include(b => b.Items)
                .FirstOrDefaultAsync(b => b.Id == id);

            if (booking == null)
            {
                TempData["ErrorMessage"] = "La reserva seleccionada no existe o ya fue eliminada.";
                return RedirectToAction(nameof(Bookings));
            }

            if (booking.Items != null && booking.Items.Any())
            {
                _context.BookingItems.RemoveRange(booking.Items);
            }

            _context.Bookings.Remove(booking);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"La reserva {booking.ReferenceCode} a nombre de {booking.CustomerName} ha sido eliminada permanentemente.";
            return RedirectToAction(nameof(Bookings));
        }

        // --- FEATURED PACKAGES CRUD --- //

        public async Task<IActionResult> Featured(int page = 1)
        {
            var query = _context.FeaturedPackages
                .Include(f => f.TourPackage)
                .AsQueryable();

            int totalItems = await query.CountAsync();
            int pageSize = 12;
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalItems / pageSize));
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            var featured = await query
                .OrderBy(f => f.DisplayOrder)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.Pagination = new PaginationViewModel
            {
                Action = "Featured",
                Controller = "Admin",
                CurrentPage = page,
                TotalPages = totalPages,
                TotalItems = totalItems,
                PageSize = pageSize,
                RouteParams = new Dictionary<string, string>()
            };

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
        public async Task<IActionResult> Categories(string? search, int page = 1)
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

            int totalItems = await query.CountAsync();
            int pageSize = 12;
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalItems / pageSize));
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            var categories = await query
                .OrderBy(c => c.DisplayOrder)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
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

            var routeParams = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(search)) routeParams["search"] = search;

            ViewBag.Pagination = new PaginationViewModel
            {
                Action = "Categories",
                Controller = "Admin",
                CurrentPage = page,
                TotalPages = totalPages,
                TotalItems = totalItems,
                PageSize = pageSize,
                RouteParams = routeParams
            };

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

        // --- MÓDULOS DE CAJA Y CAJA CHICA --- //

        [HttpGet]
        public async Task<IActionResult> Cash(string? tab = "general", string? search = null, string? filterType = null, int page = 1)
        {
            tab = string.IsNullOrWhiteSpace(tab) ? "general" : tab.ToLower();

            // 1. Obtener Turno Activo de Caja General
            var activeShift = await _context.CashRegisterShifts
                .Include(s => s.Transactions)
                    .ThenInclude(t => t.Booking)
                .OrderByDescending(s => s.OpenedAt)
                .FirstOrDefaultAsync(s => s.IsOpen);

            // 2. Historial de Turnos Recientes
            var recentShifts = await _context.CashRegisterShifts
                .Include(s => s.Transactions)
                .OrderByDescending(s => s.OpenedAt)
                .Take(6)
                .ToListAsync();

            // 3. Obtener Cuenta de Caja Chica
            var pettyAccount = await _context.PettyCashAccounts
                .Include(a => a.Movements)
                .FirstOrDefaultAsync(a => a.IsActive);

            if (pettyAccount == null)
            {
                pettyAccount = new PettyCashAccount
                {
                    Id = Guid.NewGuid(),
                    Name = "Caja Chica Principal - Oficina Cajamarca",
                    TotalFundAmount = 4220.00m,
                    ResponsiblePerson = "Administrador Central",
                    IsActive = true
                };
                _context.PettyCashAccounts.Add(pettyAccount);
                await _context.SaveChangesAsync();
            }

            int pageSize = 12;
            if (page < 1) page = 1;

            var vm = new SIGETOUR.API.Models.CashViewModel
            {
                ActiveTab = tab,
                ActiveShift = activeShift,
                RecentShifts = recentShifts,
                PettyAccount = pettyAccount,
                SearchTerm = search,
                FilterType = filterType,
                PettyBalance = pettyAccount.CurrentBalance,
                PettySpent = pettyAccount.TotalSpent,
                PettyFund = pettyAccount.TotalFundAmount
            };

            if (activeShift != null)
            {
                vm.TotalGeneralIncome = activeShift.TotalIncome;
                vm.TotalGeneralExpense = activeShift.TotalExpense;
                vm.GeneralEffectiveBalance = activeShift.ExpectedCashBalance;
            }

            var routeParams = new Dictionary<string, string> { { "tab", tab } };
            if (!string.IsNullOrWhiteSpace(search)) routeParams["search"] = search;
            if (!string.IsNullOrWhiteSpace(filterType)) routeParams["filterType"] = filterType;

            if (tab == "petty")
            {
                var pettyQuery = _context.PettyCashMovements
                    .Where(m => m.AccountId == pettyAccount.Id)
                    .AsQueryable();

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var term = search.Trim().ToLower();
                    pettyQuery = pettyQuery.Where(m =>
                        m.Concept.ToLower().Contains(term) ||
                        m.ExpenseCategory.ToLower().Contains(term) ||
                        (m.Supplier != null && m.Supplier.ToLower().Contains(term)) ||
                        (m.VoucherNumber != null && m.VoucherNumber.ToLower().Contains(term)) ||
                        m.ResponsiblePerson.ToLower().Contains(term));
                }

                if (!string.IsNullOrWhiteSpace(filterType) && filterType != "Todos")
                {
                    pettyQuery = pettyQuery.Where(m => m.MovementType == filterType || m.ExpenseCategory == filterType);
                }

                int totalItems = await pettyQuery.CountAsync();
                int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalItems / pageSize));
                if (page > totalPages) page = totalPages;

                vm.PagedMovements = await pettyQuery
                    .OrderByDescending(m => m.RegisteredAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                vm.Pagination = new SIGETOUR.API.Models.PaginationViewModel
                {
                    Action = "Cash",
                    Controller = "Admin",
                    CurrentPage = page,
                    TotalPages = totalPages,
                    TotalItems = totalItems,
                    PageSize = pageSize,
                    RouteParams = routeParams
                };
            }
            else
            {
                var txQuery = _context.CashTransactions
                    .Include(t => t.Booking)
                    .AsQueryable();

                if (activeShift != null)
                {
                    txQuery = txQuery.Where(t => t.ShiftId == activeShift.Id);
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var term = search.Trim().ToLower();
                    txQuery = txQuery.Where(t =>
                        t.Description.ToLower().Contains(term) ||
                        t.Category.ToLower().Contains(term) ||
                        (t.ReferenceCode != null && t.ReferenceCode.ToLower().Contains(term)) ||
                        t.PaymentMethod.ToLower().Contains(term) ||
                        t.RegisteredBy.ToLower().Contains(term));
                }

                if (!string.IsNullOrWhiteSpace(filterType) && filterType != "Todos")
                {
                    txQuery = txQuery.Where(t => t.TransactionType == filterType || t.PaymentMethod == filterType);
                }

                int totalItems = await txQuery.CountAsync();
                int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalItems / pageSize));
                if (page > totalPages) page = totalPages;

                vm.PagedTransactions = await txQuery
                    .OrderByDescending(t => t.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                vm.Pagination = new SIGETOUR.API.Models.PaginationViewModel
                {
                    Action = "Cash",
                    Controller = "Admin",
                    CurrentPage = page,
                    TotalPages = totalPages,
                    TotalItems = totalItems,
                    PageSize = pageSize,
                    RouteParams = routeParams
                };
            }

            ViewBag.Pagination = vm.Pagination;
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OpenCashShift(decimal initialAmount, string? notes)
        {
            var openShift = await _context.CashRegisterShifts.FirstOrDefaultAsync(s => s.IsOpen);
            if (openShift != null)
            {
                TempData["ErrorMessage"] = "Ya existe un turno de caja abierto actualmente.";
                return RedirectToAction(nameof(Cash), new { tab = "general" });
            }

            var todayStr = DateTime.UtcNow.ToString("yyyyMMdd");
            var shiftCountToday = await _context.CashRegisterShifts
                .CountAsync(s => s.OpenedAt.Date == DateTime.UtcNow.Date);

            var newShift = new SIGETOUR.API.Core.Entities.CashRegisterShift
            {
                Id = Guid.NewGuid(),
                ShiftNumber = $"CJ-{todayStr}-{(shiftCountToday + 1):D2}",
                OpenedByUserName = User.Identity?.Name ?? "Admin Central",
                OpenedAt = DateTime.UtcNow,
                InitialAmount = Math.Max(0m, initialAmount),
                IsOpen = true,
                Notes = notes
            };

            _context.CashRegisterShifts.Add(newShift);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Turno {newShift.ShiftNumber} aperturado exitosamente con S/ {newShift.InitialAmount:N2} en efectivo.";
            return RedirectToAction(nameof(Cash), new { tab = "general" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CloseCashShift(Guid shiftId, decimal actualCash, string? notes)
        {
            var shift = await _context.CashRegisterShifts
                .Include(s => s.Transactions)
                .FirstOrDefaultAsync(s => s.Id == shiftId);

            if (shift == null)
            {
                TempData["ErrorMessage"] = "El turno de caja especificado no existe.";
                return RedirectToAction(nameof(Cash), new { tab = "general" });
            }

            shift.IsOpen = false;
            shift.ClosedAt = DateTime.UtcNow;
            shift.ClosedByUserName = User.Identity?.Name ?? "Admin Central";
            shift.ActualCash = actualCash;
            if (!string.IsNullOrWhiteSpace(notes))
            {
                shift.Notes = string.IsNullOrWhiteSpace(shift.Notes) ? notes : $"{shift.Notes} | Cierre: {notes}";
            }

            _context.CashRegisterShifts.Update(shift);
            await _context.SaveChangesAsync();

            var diff = shift.CashDifference;
            string statusMsg = diff == 0 
                ? "¡Caja Cuadrada Perfectamente!" 
                : (diff > 0 ? $"Sobrante de S/ {diff:N2}" : $"Faltante de S/ {Math.Abs(diff):N2}");

            TempData["SuccessMessage"] = $"Turno {shift.ShiftNumber} cerrado. Arqueo: {statusMsg}.";
            return RedirectToAction(nameof(Cash), new { tab = "general" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegisterCashTransaction(Guid shiftId, string type, string category, decimal amount, string paymentMethod, string? referenceCode, string description)
        {
            if (amount <= 0)
            {
                TempData["ErrorMessage"] = "El monto debe ser mayor a S/ 0.00.";
                return RedirectToAction(nameof(Cash), new { tab = "general" });
            }

            var shift = await _context.CashRegisterShifts.FindAsync(shiftId);
            if (shift == null || !shift.IsOpen)
            {
                TempData["ErrorMessage"] = "No hay un turno de caja abierto para registrar movimientos.";
                return RedirectToAction(nameof(Cash), new { tab = "general" });
            }

            var tx = new SIGETOUR.API.Core.Entities.CashTransaction
            {
                Id = Guid.NewGuid(),
                ShiftId = shiftId,
                TransactionType = type == "Egreso" ? "Egreso" : "Ingreso",
                Category = string.IsNullOrWhiteSpace(category) ? "Ingreso Extraordinario" : category,
                Amount = amount,
                PaymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? "Efectivo" : paymentMethod,
                ReferenceCode = referenceCode,
                Description = description,
                RegisteredBy = User.Identity?.Name ?? "Admin Central",
                CreatedAt = DateTime.UtcNow
            };

            _context.CashTransactions.Add(tx);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Movimiento de {tx.TransactionType} por S/ {tx.Amount:N2} registrado en el turno activo.";
            return RedirectToAction(nameof(Cash), new { tab = "general" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegisterPettyCashExpense(Guid accountId, string expenseCategory, string voucherType, string? voucherNumber, string? supplier, decimal amount, string responsiblePerson, string concept)
        {
            if (amount <= 0)
            {
                TempData["ErrorMessage"] = "El monto del gasto debe ser mayor a S/ 0.00.";
                return RedirectToAction(nameof(Cash), new { tab = "petty" });
            }

            var account = await _context.PettyCashAccounts
                .Include(a => a.Movements)
                .FirstOrDefaultAsync(a => a.Id == accountId);

            if (account == null)
            {
                TempData["ErrorMessage"] = "La cuenta de caja chica no existe.";
                return RedirectToAction(nameof(Cash), new { tab = "petty" });
            }

            if (account.CurrentBalance < amount)
            {
                TempData["ErrorMessage"] = $"Saldo insuficiente en Caja Chica. Saldo disponible: S/ {account.CurrentBalance:N2}.";
                return RedirectToAction(nameof(Cash), new { tab = "petty" });
            }

            var movement = new SIGETOUR.API.Core.Entities.PettyCashMovement
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                MovementType = "Egreso",
                ExpenseCategory = expenseCategory,
                VoucherType = voucherType,
                VoucherNumber = voucherNumber,
                Supplier = supplier,
                Amount = amount,
                ResponsiblePerson = responsiblePerson,
                Concept = concept,
                RegisteredAt = DateTime.UtcNow
            };

            _context.PettyCashMovements.Add(movement);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Gasto de Caja Chica por S/ {amount:N2} registrado correctamente.";
            return RedirectToAction(nameof(Cash), new { tab = "petty" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReplenishPettyCash(Guid accountId, decimal amount, string concept)
        {
            if (amount <= 0)
            {
                TempData["ErrorMessage"] = "El monto de reposición debe ser mayor a S/ 0.00.";
                return RedirectToAction(nameof(Cash), new { tab = "petty" });
            }

            var account = await _context.PettyCashAccounts.FindAsync(accountId);
            if (account == null)
            {
                TempData["ErrorMessage"] = "La cuenta de caja chica no existe.";
                return RedirectToAction(nameof(Cash), new { tab = "petty" });
            }

            var movement = new SIGETOUR.API.Core.Entities.PettyCashMovement
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                MovementType = "Reposicion",
                ExpenseCategory = "Reposición de Fondo",
                VoucherType = "Recibo de Ingreso",
                VoucherNumber = $"REP-{DateTime.UtcNow:yyyyMMdd-HHmm}",
                Supplier = "Tesorería Central Turismo Chilón",
                Amount = amount,
                ResponsiblePerson = User.Identity?.Name ?? "Administrador Central",
                Concept = string.IsNullOrWhiteSpace(concept) ? "Reposición de fondo fijo de Caja Chica" : concept,
                RegisteredAt = DateTime.UtcNow
            };

            account.LastReplenishedAt = DateTime.UtcNow;
            _context.PettyCashAccounts.Update(account);
            _context.PettyCashMovements.Add(movement);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Reposición de fondo por S/ {amount:N2} realizada exitosamente.";
            return RedirectToAction(nameof(Cash), new { tab = "petty" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePettyCashFund(Guid accountId, decimal newFundAmount)
        {
            if (newFundAmount <= 0)
            {
                TempData["ErrorMessage"] = "El fondo fijo debe ser mayor a S/ 0.00.";
                return RedirectToAction(nameof(Cash), new { tab = "petty" });
            }

            var account = await _context.PettyCashAccounts.FindAsync(accountId);
            if (account == null) return NotFound();

            account.TotalFundAmount = newFundAmount;
            _context.PettyCashAccounts.Update(account);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Límite del fondo fijo actualizado a S/ {newFundAmount:N2}.";
            return RedirectToAction(nameof(Cash), new { tab = "petty" });
        }

        // --- EDICIÓN Y ANULACIÓN DE REGISTROS DE CAJA Y CAJA CHICA --- //

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateCashTransaction(Guid id, string type, string category, decimal amount, string paymentMethod, string? referenceCode, string description)
        {
            if (amount <= 0)
            {
                TempData["ErrorMessage"] = "El monto debe ser mayor a S/ 0.00.";
                return RedirectToAction(nameof(Cash), new { tab = "general" });
            }

            var tx = await _context.CashTransactions.FindAsync(id);
            if (tx == null)
            {
                TempData["ErrorMessage"] = "El movimiento de caja no existe.";
                return RedirectToAction(nameof(Cash), new { tab = "general" });
            }

            tx.TransactionType = type == "Egreso" ? "Egreso" : "Ingreso";
            tx.Category = string.IsNullOrWhiteSpace(category) ? tx.Category : category;
            tx.Amount = amount;
            tx.PaymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? tx.PaymentMethod : paymentMethod;
            tx.ReferenceCode = referenceCode;
            tx.Description = description;

            _context.CashTransactions.Update(tx);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Movimiento de caja modificado exitosamente (S/ {amount:N2}).";
            return RedirectToAction(nameof(Cash), new { tab = "general" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteCashTransaction(Guid id)
        {
            var tx = await _context.CashTransactions.FindAsync(id);
            if (tx == null)
            {
                TempData["ErrorMessage"] = "El movimiento de caja no existe.";
                return RedirectToAction(nameof(Cash), new { tab = "general" });
            }

            _context.CashTransactions.Remove(tx);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Movimiento de caja eliminado / anulado exitosamente.";
            return RedirectToAction(nameof(Cash), new { tab = "general" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePettyCashMovement(Guid id, string movementType, string expenseCategory, string voucherType, string? voucherNumber, string? supplier, decimal amount, string responsiblePerson, string concept)
        {
            if (amount <= 0)
            {
                TempData["ErrorMessage"] = "El importe debe ser mayor a S/ 0.00.";
                return RedirectToAction(nameof(Cash), new { tab = "petty" });
            }

            var m = await _context.PettyCashMovements.FindAsync(id);
            if (m == null)
            {
                TempData["ErrorMessage"] = "El registro de caja chica no existe.";
                return RedirectToAction(nameof(Cash), new { tab = "petty" });
            }

            m.MovementType = movementType == "Reposicion" ? "Reposicion" : "Egreso";
            m.ExpenseCategory = string.IsNullOrWhiteSpace(expenseCategory) ? m.ExpenseCategory : expenseCategory;
            m.VoucherType = voucherType;
            m.VoucherNumber = voucherNumber;
            m.Supplier = supplier;
            m.Amount = amount;
            m.ResponsiblePerson = string.IsNullOrWhiteSpace(responsiblePerson) ? m.ResponsiblePerson : responsiblePerson;
            m.Concept = concept;

            _context.PettyCashMovements.Update(m);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Registro de Caja Chica actualizado exitosamente.";
            return RedirectToAction(nameof(Cash), new { tab = "petty" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePettyCashMovement(Guid id)
        {
            var m = await _context.PettyCashMovements.FindAsync(id);
            if (m == null)
            {
                TempData["ErrorMessage"] = "El registro de caja chica no existe.";
                return RedirectToAction(nameof(Cash), new { tab = "petty" });
            }

            _context.PettyCashMovements.Remove(m);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Registro de Caja Chica eliminado / anulado exitosamente.";
            return RedirectToAction(nameof(Cash), new { tab = "petty" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateCashShift(Guid shiftId, decimal initialAmount, decimal? actualCash, string? notes)
        {
            var shift = await _context.CashRegisterShifts.FindAsync(shiftId);
            if (shift == null)
            {
                TempData["ErrorMessage"] = "El turno de caja especificado no existe.";
                return RedirectToAction(nameof(Cash), new { tab = "general" });
            }

            shift.InitialAmount = Math.Max(0m, initialAmount);
            if (actualCash.HasValue) shift.ActualCash = Math.Max(0m, actualCash.Value);
            if (!string.IsNullOrWhiteSpace(notes)) shift.Notes = notes;

            _context.CashRegisterShifts.Update(shift);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Datos del turno {shift.ShiftNumber} actualizados exitosamente.";
            return RedirectToAction(nameof(Cash), new { tab = "general" });
        }

        // --- EXPORTACIÓN DE REPORTES EN HOJAS DE CÁLCULO (EXCEL .XLSX) --- //

        [HttpGet]
        public async Task<IActionResult> ExportDailyCashReport(DateTime? date)
        {
            var targetDate = date ?? DateTime.Today;
            try
            {
                var fileBytes = await _cashReportService.GenerateDailyCashReportExcelAsync(targetDate);
                var fileName = $"Reporte_Caja_Diario_{targetDate:yyyyMMdd}.xlsx";
                return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Error al generar el reporte diario de caja: {ex.Message}";
                return RedirectToAction(nameof(Cash));
            }
        }

        [HttpGet]
        public async Task<IActionResult> ExportMonthlyCashReport(int? year, int? month)
        {
            int y = year ?? DateTime.Today.Year;
            int m = month ?? DateTime.Today.Month;
            try
            {
                var fileBytes = await _cashReportService.GenerateMonthlyCashReportExcelAsync(y, m);
                var fileName = $"Reporte_Caja_Mensual_{y}_{m:D2}.xlsx";
                return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Error al generar el reporte mensual de caja: {ex.Message}";
                return RedirectToAction(nameof(Cash));
            }
        }

        [HttpGet]
        public async Task<IActionResult> ExportShiftReport(Guid shiftId)
        {
            try
            {
                var shift = await _context.CashRegisterShifts.FindAsync(shiftId);
                if (shift == null) return NotFound("Turno no encontrado.");

                var fileBytes = await _cashReportService.GenerateShiftReportExcelAsync(shiftId);
                var cleanNumber = shift.ShiftNumber.Replace(" ", "_").Replace("-", "_");
                var fileName = $"Acta_Turno_{cleanNumber}_{shift.OpenedAt:yyyyMMdd}.xlsx";
                return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Error al generar el acta de turno: {ex.Message}";
                return RedirectToAction(nameof(Cash));
            }
        }
    }
}
















