using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Infrastructure.Data
{
    public static class IdentitySeeder
    {
        public static async Task SeedUsersAndRolesAsync(IServiceProvider serviceProvider)
        {
            using var scope = serviceProvider.CreateScope();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            string[] roleNames = { "SuperAdmin", "Admin", "Operador" };
            foreach (var roleName in roleNames)
            {
                var role = await roleManager.FindByNameAsync(roleName);
                if (role == null)
                {
                    // A. Creación por primera vez de un rol del sistema: sembrar defaults y marcar como configurado
                    role = new IdentityRole(roleName);
                    var createResult = await roleManager.CreateAsync(role);
                    if (createResult.Succeeded)
                    {
                        var defaults = SIGETOUR.API.Core.Security.AppPermissions.GetDefaultPermissionsForRole(roleName);
                        foreach (var perm in defaults)
                        {
                            await roleManager.AddClaimAsync(role, new System.Security.Claims.Claim("Permission", perm));
                        }
                        await roleManager.AddClaimAsync(role, new System.Security.Claims.Claim("PermissionConfigured", "true"));
                    }
                }
                else
                {
                    // B. Conservar asignación histórica en actualización / reinicio:
                    // Si el rol ya existía, conservar sus permisos excepto si un rol administrativo crítico no tiene ninguno
                    var existingClaims = await roleManager.GetClaimsAsync(role);
                    bool hasConfigured = existingClaims.Any(c => c.Type == "PermissionConfigured");
                    bool hasAnyPermission = existingClaims.Any(c => c.Type == "Permission");

                    // Si es un rol administrativo crítico (SuperAdmin o Admin) y no tiene ningún permiso en base de datos,
                    // sembrar sus permisos por defecto para garantizar la operatividad administrativa del sistema.
                    if (!hasAnyPermission && (roleName == "SuperAdmin" || roleName == "Admin"))
                    {
                        var defaults = SIGETOUR.API.Core.Security.AppPermissions.GetDefaultPermissionsForRole(roleName);
                        foreach (var perm in defaults)
                        {
                            await roleManager.AddClaimAsync(role, new System.Security.Claims.Claim("Permission", perm));
                        }
                        if (!hasConfigured)
                        {
                            await roleManager.AddClaimAsync(role, new System.Security.Claims.Claim("PermissionConfigured", "true"));
                        }
                    }
                    else if (!hasConfigured)
                    {
                        // Estampar el marcador de inicialización sin alterar los permisos existentes (sean muchos, pocos o ninguno)
                        await roleManager.AddClaimAsync(role, new System.Security.Claims.Claim("PermissionConfigured", "true"));
                    }
                }
            }

            var superadmin = await userManager.FindByNameAsync("superadmin");
            if (superadmin == null)
            {
                superadmin = new ApplicationUser
                {
                    UserName = "superadmin",
                    Email = "superadmin@sigetour.com",
                    FirstName = "Super",
                    LastName = "Admin",
                    EmailConfirmed = true,
                    IsActive = true
                };
                await userManager.CreateAsync(superadmin, "SuperAdmin123!");
                await userManager.AddToRoleAsync(superadmin, "SuperAdmin");
            }
            else
            {
                // Asegurar cuenta activa, desbloqueada y contraseña oficial sincronizada
                superadmin.IsActive = true;
                superadmin.EmailConfirmed = true;
                try
                {
                    if (await userManager.IsLockedOutAsync(superadmin))
                    {
                        await userManager.SetLockoutEndDateAsync(superadmin, null);
                    }
                    await userManager.ResetAccessFailedCountAsync(superadmin);

                    if (!await userManager.CheckPasswordAsync(superadmin, "SuperAdmin123!"))
                    {
                        var resetToken = await userManager.GeneratePasswordResetTokenAsync(superadmin);
                        await userManager.ResetPasswordAsync(superadmin, resetToken, "SuperAdmin123!");
                    }

                    if (!await userManager.IsInRoleAsync(superadmin, "SuperAdmin"))
                    {
                        await userManager.AddToRoleAsync(superadmin, "SuperAdmin");
                    }
                }
                catch { }
            }

            var admin = await userManager.FindByNameAsync("admin");
            if (admin == null)
            {
                admin = new ApplicationUser
                {
                    UserName = "admin",
                    Email = "admin@sigetour.com",
                    FirstName = "Admin",
                    LastName = "System",
                    EmailConfirmed = true,
                    IsActive = true
                };
                await userManager.CreateAsync(admin, "Admin123!");
                await userManager.AddToRoleAsync(admin, "Admin");
            }
            else
            {
                // Asegurar cuenta activa, desbloqueada y contraseña oficial sincronizada
                admin.IsActive = true;
                admin.EmailConfirmed = true;
                try
                {
                    if (await userManager.IsLockedOutAsync(admin))
                    {
                        await userManager.SetLockoutEndDateAsync(admin, null);
                    }
                    await userManager.ResetAccessFailedCountAsync(admin);

                    if (!await userManager.CheckPasswordAsync(admin, "Admin123!"))
                    {
                        var resetToken = await userManager.GeneratePasswordResetTokenAsync(admin);
                        await userManager.ResetPasswordAsync(admin, resetToken, "Admin123!");
                    }

                    if (!await userManager.IsInRoleAsync(admin, "Admin"))
                    {
                        await userManager.AddToRoleAsync(admin, "Admin");
                    }
                }
                catch { }
            }
        }
    }
}
