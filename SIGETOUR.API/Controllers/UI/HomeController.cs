using Microsoft.AspNetCore.Mvc;

namespace SIGETOUR.API.Controllers.UI
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
