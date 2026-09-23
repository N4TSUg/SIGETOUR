using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SIGETOUR.API.Controllers.UI
{
    [Authorize]
    public class AdminController : Controller
    {
        public IActionResult Dashboard()
        {
            return View();
        }

        public IActionResult Tours()
        {
            return View();
        }

        public IActionResult Bookings()
        {
            return View();
        }

        public IActionResult Fleet()
        {
            return View();
        }

        public IActionResult Users()
        {
            return View();
        }
    }
}
