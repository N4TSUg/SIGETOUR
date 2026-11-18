using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.ViewModels;
using System.Threading.Tasks;

namespace SIGETOUR.API.Controllers.UI
{
    public class AccountController : Controller
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;

        public AccountController(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager)
        {
            _signInManager = signInManager;
            _userManager = userManager;
        }

        [HttpGet]
        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Login(string? returnUrl = null)
        {
            // Prevenir cache en el navegador para que al presionar 'Atrás' no muestre el formulario si ya está logeado
            Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";

            if (_signInManager.IsSignedIn(User) || User.Identity?.IsAuthenticated == true)
            {
                if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
                    && !returnUrl.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase)
                    && !returnUrl.Contains("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase))
                {
                    return Redirect(returnUrl);
                }
                return RedirectToAction("Dashboard", "Admin");
            }
            ViewData["ReturnUrl"] = returnUrl;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
        {
            if (ModelState.IsValid)
            {
                var cleanInput = model.EmailOrUsername?.Trim() ?? string.Empty;
                var user = await _userManager.FindByEmailAsync(cleanInput) ??
                           await _userManager.FindByNameAsync(cleanInput);

                if (user != null)
                {
                    if (!user.IsActive)
                    {
                        ModelState.AddModelError(string.Empty, "La cuenta de usuario se encuentra desactivada. Contacte al Administrador.");
                        ViewData["ReturnUrl"] = returnUrl;
                        return View(model);
                    }

                    var result = await _signInManager.PasswordSignInAsync(user.UserName!, model.Password, model.RememberMe, lockoutOnFailure: false);

                    if (result.Succeeded)
                    {
                        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
                            && !returnUrl.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase)
                            && !returnUrl.Contains("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase))
                        {
                            return Redirect(returnUrl);
                        }
                        return RedirectToAction("Dashboard", "Admin");
                    }

                    if (result.IsLockedOut)
                    {
                        ModelState.AddModelError(string.Empty, "La cuenta se encuentra bloqueada temporalmente por intentos fallidos. Espere unos minutos e intente nuevamente.");
                        ViewData["ReturnUrl"] = returnUrl;
                        return View(model);
                    }
                }

                ModelState.AddModelError(string.Empty, "Intento de inicio de sesión no válido.");
            }

            ViewData["ReturnUrl"] = returnUrl;
            return View(model);
        }

        [HttpGet]
        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult AccessDenied(string? returnUrl = null)
        {
            // Prevenir cache en el navegador para información sensible
            Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";

            ViewData["ReturnUrl"] = returnUrl;
            return View();
        }

        [HttpPost]
        [HttpGet]
        public async Task<IActionResult> Logout(string? returnUrl = null)
        {
            await _signInManager.SignOutAsync();

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
                && !returnUrl.Contains("/Account/Logout", StringComparison.OrdinalIgnoreCase))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction("Index", "Home");
        }
    }
}

