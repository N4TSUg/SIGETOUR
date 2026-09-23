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

        public async Task<IActionResult> Fleet()
        {
            var vehicles = await _context.Vehicles.ToListAsync();
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
        public async Task<IActionResult> CreateTour(TourPackage model, IFormFileCollection ImageFiles, string[] Stops, string[] Inclusions)
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
                        model.ItineraryStops.Add(new ItineraryStop { Id = Guid.NewGuid(), Name = Stops[i], OrderIndex = i });
                }
            }
            
            if (Inclusions != null) {
                foreach (var inc in Inclusions) {
                    model.Inclusions.Add(new TourInclusion { Id = Guid.NewGuid(), Description = inc });
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
                .FirstOrDefaultAsync(t => t.Id == id);
                
            if (tour == null) return NotFound();
            
            return View("CreateTour", tour);
        }

        [HttpPost]
        public async Task<IActionResult> EditTour(TourPackage model, IFormFileCollection ImageFiles, string[] Stops, string[] Inclusions)
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

                        // Update Stops
            var oldStops = tour.ItineraryStops.ToList();
            _context.Set<ItineraryStop>().RemoveRange(oldStops);
            tour.ItineraryStops.Clear();
            
            if (Stops != null) {
                for (int i = 0; i < Stops.Length; i++) {
                    if (!string.IsNullOrWhiteSpace(Stops[i]))
                        tour.ItineraryStops.Add(new ItineraryStop { Id = Guid.NewGuid(), Name = Stops[i], OrderIndex = i });
                }
            }

            // Update Inclusions
            var oldInclusions = tour.Inclusions.ToList();
            _context.Set<TourInclusion>().RemoveRange(oldInclusions);
            tour.Inclusions.Clear();
            
            if (Inclusions != null) {
                foreach (var inc in Inclusions) {
                    tour.Inclusions.Add(new TourInclusion { Id = Guid.NewGuid(), Description = inc });
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
    }
}








