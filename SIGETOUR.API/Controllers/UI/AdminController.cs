using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace SIGETOUR.API.Controllers.UI
{
    [Authorize(AuthenticationSchemes = "Identity.Application")]
    public class AdminController : Controller
    {
        private readonly AppDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public AdminController(AppDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        // ... existing Dashboard, Tours, Bookings, Fleet, Users methods ...

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
        public async Task<IActionResult> CreateTour(TourPackage model)
        {
            model.Id = Guid.NewGuid();
            if (string.IsNullOrEmpty(model.Slug)) {
                model.Slug = model.Title.ToLower().Replace(" ", "-");
            }
            
            _context.TourPackages.Add(model);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Tours));
        }

        [HttpGet]
        public async Task<IActionResult> EditTour(Guid id)
        {
            var tour = await _context.TourPackages.FindAsync(id);
            if (tour == null) return NotFound();
            
            // Re-using CreateTour view for editing, but passing the model
            return View("CreateTour", tour);
        }

        [HttpPost]
        public async Task<IActionResult> EditTour(TourPackage model)
        {
            var tour = await _context.TourPackages.FindAsync(model.Id);
            if (tour == null) return NotFound();

            tour.Title = model.Title;
            tour.Subtitle = model.Subtitle;
            tour.Description = model.Description;
            tour.BasePrice = model.BasePrice;
            tour.ChildPrice = model.ChildPrice;
            tour.MaxCapacity = model.MaxCapacity;
            tour.Duration = model.Duration;
            tour.Category = model.Category;
            tour.Modality = model.Modality;
            tour.Difficulty = model.Difficulty;
            tour.IsActive = model.IsActive;
            if (!string.IsNullOrEmpty(model.Slug)) {
                tour.Slug = model.Slug;
            }

            _context.TourPackages.Update(tour);
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
    }
}
