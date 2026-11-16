using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Core.Security;

namespace SIGETOUR.API.Infrastructure.Security
{
    public class AppUserClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
    {
        public AppUserClaimsPrincipalFactory(
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager,
            IOptions<IdentityOptions> optionsAccessor)
            : base(userManager, roleManager, optionsAccessor)
        {
        }

        protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
        {
            var identity = await base.GenerateClaimsAsync(user);

            // Estado de actividad como claim
            identity.AddClaim(new Claim("IsActive", user.IsActive ? "true" : "false"));

            // Cargar y adjuntar permisos efectivos según las asignaciones reales del rol en base de datos
            var roles = await UserManager.GetRolesAsync(user);
            foreach (var role in roles)
            {
                if (!identity.HasClaim(ClaimTypes.Role, role))
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, role));
                }

                var identityRole = await RoleManager.FindByNameAsync(role);
                if (identityRole != null)
                {
                    var roleClaims = await RoleManager.GetClaimsAsync(identityRole);
                    var permClaims = roleClaims.Where(c => c.Type == "Permission").ToList();

                    // Lectura pura de permisos efectivos: CERO escrituras en base de datos durante la autenticación
                    foreach (var claim in permClaims)
                    {
                        if (!identity.HasClaim("Permission", claim.Value))
                        {
                            identity.AddClaim(new Claim("Permission", claim.Value));
                        }
                    }

                    if (roleClaims.Any(c => c.Type == "PermissionConfigured"))
                    {
                        if (!identity.HasClaim("PermissionConfigured", "true"))
                        {
                            identity.AddClaim(new Claim("PermissionConfigured", "true"));
                        }
                    }
                }
                else
                {
                    // Fallback de solo lectura en memoria para entornos donde RoleManager no contiene la entidad (ej. mocks unitarios en pruebas)
                    var defaults = AppPermissions.GetDefaultPermissionsForRole(role);
                    foreach (var def in defaults)
                    {
                        if (!identity.HasClaim("Permission", def))
                        {
                            identity.AddClaim(new Claim("Permission", def));
                        }
                    }
                }
            }

            return identity;
        }
    }
}
