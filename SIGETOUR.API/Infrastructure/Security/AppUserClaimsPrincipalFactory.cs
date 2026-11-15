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

            // Cargar y adjuntar permisos efectivos según los roles asignados
            var roles = await UserManager.GetRolesAsync(user);
            foreach (var role in roles)
            {
                if (!identity.HasClaim(ClaimTypes.Role, role))
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, role));
                }

                var permissions = AppPermissions.GetDefaultPermissionsForRole(role);
                foreach (var perm in permissions)
                {
                    if (!identity.HasClaim("Permission", perm))
                    {
                        identity.AddClaim(new Claim("Permission", perm));
                    }
                }

                var identityRole = await RoleManager.FindByNameAsync(role);
                if (identityRole != null)
                {
                    var roleClaims = await RoleManager.GetClaimsAsync(identityRole);
                    foreach (var claim in roleClaims.Where(c => c.Type == "Permission"))
                    {
                        if (!identity.HasClaim("Permission", claim.Value))
                        {
                            identity.AddClaim(new Claim("Permission", claim.Value));
                        }
                    }
                }
            }

            return identity;
        }
    }
}
