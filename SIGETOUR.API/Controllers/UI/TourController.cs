using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Core.Enums;
using SIGETOUR.API.Infrastructure.Data;

namespace SIGETOUR.API.Controllers.UI
{
    public class TourController : Controller
    {
        private readonly AppDbContext _context;

        public TourController(AppDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Catalog(
            string? q = null,
            Guid? categoryId = null,
            string? date = null,
            string? shift = null,
            string? modality = null,
            string? difficulty = null,
            decimal? maxPrice = null,
            string? sortBy = null,
            string? duration = null)
        {
            var query = _context.TourPackages
                .Include(t => t.Images)
                .Include(t => t.Category)
                .Include(t => t.Shifts)
                .Where(t => t.IsActive);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                query = query.Where(t => 
                    t.Title.ToLower().Contains(term) || 
                    t.Subtitle.ToLower().Contains(term) || 
                    t.Description.ToLower().Contains(term) ||
                    (t.Category != null && t.Category.Name.ToLower().Contains(term)));
            }

            if (categoryId.HasValue && categoryId.Value != Guid.Empty)
            {
                query = query.Where(t => t.CategoryId == categoryId.Value);
            }

            if (!string.IsNullOrWhiteSpace(modality) && modality != "Todos")
            {
                if (Enum.TryParse<TourModality>(modality, true, out var modEnum))
                {
                    query = query.Where(t => t.Modality == modEnum);
                }
            }

            if (!string.IsNullOrWhiteSpace(difficulty) && difficulty != "Todos")
            {
                if (Enum.TryParse<TourDifficulty>(difficulty, true, out var diffEnum))
                {
                    query = query.Where(t => t.Difficulty == diffEnum);
                }
            }

            if (!string.IsNullOrWhiteSpace(shift) && shift != "Todos" && shift != "Cualquier Turno")
            {
                query = query.Where(t => t.Shifts.Any(s => s.ShiftName.ToLower().Contains(shift.ToLower())));
            }

            if (!string.IsNullOrWhiteSpace(duration) && duration != "Todos")
            {
                var durLower = duration.Trim().ToLower();
                if (durLower.Contains("medio") || durLower.Contains("half"))
                {
                    query = query.Where(t => t.Duration != null && (t.Duration.ToLower().Contains("medio") || t.Duration.ToLower().Contains("4 hora") || t.Duration.ToLower().Contains("4h") || t.Duration.ToLower().Contains("3")));
                }
                else if (durLower.Contains("completo") || durLower.Contains("full"))
                {
                    query = query.Where(t => t.Duration != null && (t.Duration.ToLower().Contains("completo") || t.Duration.ToLower().Contains("6") || t.Duration.ToLower().Contains("8") || t.Duration.ToLower().Contains("9") || t.Duration.ToLower().Contains("jornada")));
                }
                else
                {
                    query = query.Where(t => t.Duration != null && t.Duration.ToLower().Contains(durLower));
                }
            }

            var activeFeatured = await _context.FeaturedPackages
                .Where(f => f.IsActive)
                .ToDictionaryAsync(f => f.TourPackageId, f => f);
            ViewBag.ActiveFeatured = activeFeatured;

            var tours = await query.ToListAsync();

            if (maxPrice.HasValue && maxPrice.Value > 0)
            {
                tours = tours.Where(t =>
                    (activeFeatured.TryGetValue(t.Id, out var f) && f.PromoPrice.HasValue ? f.PromoPrice.Value : t.BasePrice) <= maxPrice.Value
                ).ToList();
            }

            // Sorting taking into account promo price
            tours = sortBy switch
            {
                "price-asc" => tours.OrderBy(t => activeFeatured.TryGetValue(t.Id, out var f) && f.PromoPrice.HasValue ? f.PromoPrice.Value : t.BasePrice).ToList(),
                "price-desc" => tours.OrderByDescending(t => activeFeatured.TryGetValue(t.Id, out var f) && f.PromoPrice.HasValue ? f.PromoPrice.Value : t.BasePrice).ToList(),
                "title" => tours.OrderBy(t => t.Title).ToList(),
                _ => tours.OrderBy(t => t.Title).ToList()
            };

            // Load categories with count of active tours
            var categories = await _context.Categories
                .Where(c => c.IsActive)
                .Include(c => c.TourPackages)
                .OrderBy(c => c.DisplayOrder)
                .ToListAsync();

            ViewBag.Categories = categories;
            ViewBag.CurrentQ = q;
            ViewBag.CurrentCategoryId = categoryId;
            ViewBag.CurrentDate = date;
            ViewBag.CurrentShift = shift;
            ViewBag.CurrentModality = modality;
            ViewBag.CurrentDifficulty = difficulty;
            ViewBag.CurrentMaxPrice = maxPrice;
            ViewBag.CurrentDuration = duration;
            ViewBag.CurrentSortBy = sortBy;

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

            var featured = await _context.FeaturedPackages
                .OrderBy(f => f.DisplayOrder)
                .FirstOrDefaultAsync(f => f.TourPackageId == tour.Id && f.IsActive);
            if (featured != null && featured.PromoPrice.HasValue)
            {
                ViewBag.PromoPrice = featured.PromoPrice.Value;
                ViewBag.PromoBadge = string.IsNullOrEmpty(featured.PromoBadge) || featured.PromoBadge == "none" ? "¡Oferta Especial!" : featured.PromoBadge;
            }

            var reviews = await _context.TourReviews
                .Where(r => r.TourPackageId == tour.Id && r.IsApproved)
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();
            ViewBag.Reviews = reviews;

            return View(tour);
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitReview(Guid tourId, string authorName, string comment, string? authorCity = null)
        {
            var tour = await _context.TourPackages.FindAsync(tourId);
            if (tour == null) return NotFound();

            if (string.IsNullOrWhiteSpace(authorName) || string.IsNullOrWhiteSpace(comment))
            {
                TempData["ErrorMessage"] = "Por favor ingresa tu nombre y la descripción de tu experiencia.";
                return RedirectToAction(nameof(Details), new { slug = tour.Slug });
            }

            var review = new SIGETOUR.API.Core.Entities.TourReview
            {
                Id = Guid.NewGuid(),
                TourPackageId = tour.Id,
                AuthorName = authorName.Trim(),
                AuthorCity = string.IsNullOrWhiteSpace(authorCity) ? "Viajero" : authorCity.Trim(),
                Comment = comment.Trim(),
                CreatedAt = DateTime.UtcNow,
                IsApproved = false,
                IsVisibleOnHome = false
            };

            _context.TourReviews.Add(review);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "¡Muchas gracias por compartir tu experiencia! Tu reseña ha sido registrada y será publicada tras su breve moderación.";
            return RedirectToAction(nameof(Details), new { slug = tour.Slug });
        }
    }
}
