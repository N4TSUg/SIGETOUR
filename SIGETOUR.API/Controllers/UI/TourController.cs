using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Infrastructure.Data;
using System;
using System.Threading.Tasks;

namespace SIGETOUR.API.Controllers.UI
{
    public class TourController : Controller
    {
        private readonly AppDbContext _context;

        public TourController(AppDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Catalog()
        {
            var tours = await _context.TourPackages
                .Include(t => t.Images)
                .Include(t => t.Category)
                .Where(t => t.IsActive)
                .ToListAsync();
            return View(tours);
        }

        [Route("Tour/Details/{slug?}")]
        public async Task<IActionResult> Details(string slug)
        {
            if (string.IsNullOrEmpty(slug))
            {
                return RedirectToAction(nameof(Catalog));
            }

            var tour = await _context.TourPackages
                .Include(t => t.Images)
                .Include(t => t.Inclusions)
                .Include(t => t.ItineraryStops)
                .Include(t => t.Category)
                .FirstOrDefaultAsync(t => t.Slug == slug);

            if (tour == null)
            {
                return NotFound();
            }

            var featured = await _context.FeaturedPackages.FirstOrDefaultAsync(f => f.TourPackageId == tour.Id);
            if (featured != null && featured.PromoPrice.HasValue)
            {
                tour.BasePrice = featured.PromoPrice.Value;
            }

            return View(tour);
        }
    }
}
