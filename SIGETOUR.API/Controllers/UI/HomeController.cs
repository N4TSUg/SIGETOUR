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
                .Where(t => t.IsActive)
                .OrderBy(t => t.Title)
                .Take(6)
                .ToListAsync();
                
            var featured = await _context.FeaturedPackages
                .Include(f => f.TourPackage)
                .ThenInclude(t => t.Images)
                .OrderBy(f => f.DisplayOrder)
                .ToListAsync();
                
            ViewBag.FeaturedPackages = featured;

            return View(tours);
        }
    }
}

