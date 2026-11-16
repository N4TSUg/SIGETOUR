using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Core.Security;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace SIGETOUR.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IConfiguration _configuration;

        public AuthController(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager, IConfiguration configuration)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _configuration = configuration;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Username) || string.IsNullOrWhiteSpace(model.Password))
            {
                return BadRequest(new { message = "Debe proporcionar usuario y contraseña." });
            }

            var cleanUser = model.Username.Trim();
            var user = await _userManager.FindByNameAsync(cleanUser)
                       ?? await _userManager.FindByEmailAsync(cleanUser);

            if (user == null || !await _userManager.CheckPasswordAsync(user, model.Password))
            {
                return Unauthorized();
            }

            // Aplicación estricta de cuenta activa: cuentas suspendidas no obtienen token
            if (!user.IsActive)
            {
                return Unauthorized(new { message = "La cuenta de usuario se encuentra desactivada. Contacte al Administrador." });
            }

            var userRoles = await _userManager.GetRolesAsync(user);

            var authClaims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.UserName ?? string.Empty),
                new Claim(ClaimTypes.Email, user.Email ?? string.Empty),
                new Claim("IsActive", user.IsActive ? "true" : "false"),
                new Claim("SecurityStamp", user.SecurityStamp ?? string.Empty),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            };

            foreach (var userRole in userRoles)
            {
                authClaims.Add(new Claim(ClaimTypes.Role, userRole));

                var identityRole = await _roleManager.FindByNameAsync(userRole);
                if (identityRole != null)
                {
                    var roleClaims = await _roleManager.GetClaimsAsync(identityRole);
                    var permClaims = roleClaims.Where(c => c.Type == "Permission").ToList();

                    // Lectura pura de permisos asignados: CERO escrituras en BD durante el login
                    foreach (var claim in permClaims)
                    {
                        if (!authClaims.Any(c => c.Type == "Permission" && c.Value == claim.Value))
                        {
                            authClaims.Add(new Claim("Permission", claim.Value));
                        }
                    }

                    if (roleClaims.Any(c => c.Type == "PermissionConfigured"))
                    {
                        if (!authClaims.Any(c => c.Type == "PermissionConfigured"))
                        {
                            authClaims.Add(new Claim("PermissionConfigured", "true"));
                        }
                    }
                }
                else
                {
                    // Fallback de solo lectura en memoria para entornos donde RoleManager no contiene la entidad (ej. mocks unitarios en pruebas)
                    var defaults = AppPermissions.GetDefaultPermissionsForRole(userRole);
                    foreach (var def in defaults)
                    {
                        if (!authClaims.Any(c => c.Type == "Permission" && c.Value == def))
                        {
                            authClaims.Add(new Claim("Permission", def));
                        }
                    }
                }
            }

            var authSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"] ?? "default_super_secret_key_1234567890_min_256_bits"));

            var token = new JwtSecurityToken(
                expires: DateTime.UtcNow.AddHours(3),
                claims: authClaims,
                signingCredentials: new SigningCredentials(authSigningKey, SecurityAlgorithms.HmacSha256)
            );

            return Ok(new
            {
                token = new JwtSecurityTokenHandler().WriteToken(token),
                expiration = token.ValidTo
            });
        }

        [HttpPost("seed")]
        public async Task<IActionResult> SeedRolesAndAdmin()
        {
            // Protección contra acceso público: Si el sistema ya fue inicializado con un SuperAdmin,
            // se deniega el acceso a llamadas no autenticadas o que no pertenezcan al rol SuperAdmin.
            var existingSuperAdmins = await _userManager.GetUsersInRoleAsync("SuperAdmin");
            bool hasInitializedSystem = existingSuperAdmins != null && existingSuperAdmins.Any();

            if (hasInitializedSystem && (!User.Identity?.IsAuthenticated == true || !User.IsInRole("SuperAdmin")))
            {
                return Unauthorized(new
                {
                    message = "El endpoint de inicialización se encuentra protegido contra acceso público. El sistema ya cuenta con administradores registrados."
                });
            }

            string[] systemRoles = { "SuperAdmin", "Admin", "Operador" };
            foreach (var r in systemRoles)
            {
                if (!await _roleManager.RoleExistsAsync(r))
                {
                    await _roleManager.CreateAsync(new IdentityRole(r));
                }
            }

            var superAdminUser = await _userManager.FindByNameAsync("superadmin");
            if (superAdminUser == null)
            {
                superAdminUser = new ApplicationUser()
                {
                    UserName = "superadmin",
                    Email = "superadmin@sigetour.com",
                    FirstName = "Super",
                    LastName = "Admin",
                    IsActive = true
                };
                var result = await _userManager.CreateAsync(superAdminUser, "SuperAdmin123!");
                if (result.Succeeded)
                {
                    await _userManager.AddToRoleAsync(superAdminUser, "SuperAdmin");
                }
            }
            
            var adminUser = await _userManager.FindByNameAsync("admin");
            if (adminUser == null)
            {
                adminUser = new ApplicationUser()
                {
                    UserName = "admin",
                    Email = "admin@sigetour.com",
                    FirstName = "Gerente",
                    LastName = "Operativo",
                    IsActive = true
                };
                var result = await _userManager.CreateAsync(adminUser, "Admin123!");
                if (result.Succeeded)
                {
                    await _userManager.AddToRoleAsync(adminUser, "Admin");
                }
            }

            var operadorUser = await _userManager.FindByNameAsync("operador");
            if (operadorUser == null)
            {
                operadorUser = new ApplicationUser()
                {
                    UserName = "operador",
                    Email = "operador@sigetour.com",
                    FirstName = "Operador",
                    LastName = "Counter",
                    IsActive = true
                };
                var result = await _userManager.CreateAsync(operadorUser, "Operador123!");
                if (result.Succeeded)
                {
                    await _userManager.AddToRoleAsync(operadorUser, "Operador");
                }
            }

            return Ok(new { message = "Roles y usuarios administrativos inicializados con éxito." });
        }
    }

    public class LoginDto
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}
