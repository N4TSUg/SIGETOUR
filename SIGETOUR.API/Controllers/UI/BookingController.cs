using Microsoft.AspNetCore.Mvc;

namespace SIGETOUR.API.Controllers.UI
{
    public class BookingController : Controller
    {
        public IActionResult Checkout()
        {
            return View();
        }

        public IActionResult Voucher()
        {
            return View();
        }
    }
}
