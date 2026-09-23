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
                .Where(t => t.IsActive)
                .ToListAsync();
            return View(tours);
        }

        public async Task<IActionResult> Details(Guid id)
        {
            if (id == Guid.Empty)
            {
                // Si no mandan ID, por ejemplo al entrar a /Tour/Details desde el header quemado, 
                // podemos redirigir al catálogo o mostrar el primero. Redirigimos por seguridad.
                return RedirectToAction(nameof(Catalog));
            }

            var tour = await _context.TourPackages
                .Include(t => t.Images)
                .Include(t => t.Inclusions)
                .Include(t => t.ItineraryStops)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (tour == null)
            {
                return NotFound();
            }

            return View(tour);
        }
    }
}
