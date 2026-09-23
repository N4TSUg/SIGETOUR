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

            string[] roleNames = { "SuperAdmin", "Admin", "User" };
            foreach (var roleName in roleNames)
            {
                if (!await roleManager.RoleExistsAsync(roleName))
                {
                    await roleManager.CreateAsync(new IdentityRole(roleName));
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
        }
    }
}
