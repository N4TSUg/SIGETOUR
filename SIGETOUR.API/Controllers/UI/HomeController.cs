using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Infrastructure.Data;
using System.Linq;
using System.Threading.Tasks;

namespace SIGETOUR.API.Controllers.UI
{
    public class HomeController : Controller
    {
        private readonly AppDbContext _context;

        public HomeController(AppDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            var tours = await _context.TourPackages
                .Include(t => t.Images)
                .Include(t => t.Category)
                .Where(t => t.IsActive)
                .OrderBy(t => t.Title)
                .Take(6)
                .ToListAsync();
                
            var featured = await _context.FeaturedPackages
                .Include(f => f.TourPackage)
                .ThenInclude(t => t!.Category)
                .Include(f => f.TourPackage)
                .ThenInclude(t => t!.Images)
                .OrderBy(f => f.DisplayOrder)
                .ToListAsync();
                
            ViewBag.FeaturedPackages = featured;

            var vehicles = await _context.Vehicles
                .Include(v => v.Images)
                .Where(v => v.Status != "Baja Operativa")
                .OrderByDescending(v => v.SeatCapacity)
                .Take(4)
                .ToListAsync();

            ViewBag.Vehicles = vehicles;

            var allTours = await _context.TourPackages
                .Where(t => t.IsActive)
                .OrderBy(t => t.Title)
                .Select(t => new { t.Id, t.Title, t.Slug, t.CategoryId })
                .ToListAsync();
            ViewBag.AllTours = allTours;

            var categories = await _context.Categories
                .Where(c => c.IsActive)
                .OrderBy(c => c.DisplayOrder)
                .ToListAsync();
            ViewBag.Categories = categories;

            // Destino Insignia de Portada (configurable desde Admin)
            var flagshipTour = await _context.TourPackages
                .Include(t => t.Images)
                .Include(t => t.Category)
                .Include(t => t.Shifts)
                .FirstOrDefaultAsync(t => t.IsFlagship && t.IsActive)
                ?? await _context.TourPackages
                    .Include(t => t.Images)
                    .Include(t => t.Category)
                    .Include(t => t.Shifts)
                    .FirstOrDefaultAsync(t => t.IsActive);
            ViewBag.FlagshipTour = flagshipTour;

            // Testimonios y Comentarios de Experiencia (aprobados y seleccionados para portada)
            var homeReviews = await _context.TourReviews
                .Include(r => r.TourPackage)
                .Where(r => r.IsApproved && r.IsVisibleOnHome)
                .OrderByDescending(r => r.CreatedAt)
                .Take(6)
                .ToListAsync();
            ViewBag.HomeReviews = homeReviews;

            return View(tours);
        }
    }
}
