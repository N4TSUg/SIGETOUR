using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Application.Services;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using System.Text;
using System;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddControllersWithViews();

// 1. Configuración de PostgreSQL
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2. Identity Configuration
builder.Services.AddIdentity<ApplicationUser, IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// 3. Authentication & JWT
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"] ?? "default_super_secret_key_1234567890_min_256_bits"))
    };
});

// 4. Configuración de AutoMapper
builder.Services.AddAutoMapper(config => {
    config.AddProfile<SIGETOUR.API.Application.Mappings.TourPackageProfile>();
});

// 5. Inyección de Dependencias
builder.Services.AddScoped<ITourPackageService, TourPackageService>();
builder.Services.AddScoped<IManifestExportService, ManifestExportService>();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SIGETOUR.API.Infrastructure.Data.AppDbContext>();
    db.Database.EnsureCreated();
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"ItineraryStops\" ADD COLUMN IF NOT EXISTS \"EstimatedTime\" text;"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Bookings\" ADD COLUMN IF NOT EXISTS \"ShiftName\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Bookings\" ADD COLUMN IF NOT EXISTS \"PaidAmount\" numeric NOT NULL DEFAULT 0;"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Bookings\" ADD COLUMN IF NOT EXISTS \"PaymentMethod\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Bookings\" ADD COLUMN IF NOT EXISTS \"OperationNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Bookings\" ADD COLUMN IF NOT EXISTS \"InvoiceType\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Bookings\" ADD COLUMN IF NOT EXISTS \"InvoiceNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"Category\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"ManufactureYear\" integer NOT NULL DEFAULT 2020;"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"ChassisNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"EngineNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"Color\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"FuelType\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"SoatNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"SoatProvider\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"SoatIssueDate\" timestamp with time zone NULL;"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"SoatExpiryDate\" timestamp with time zone NULL;"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"CitvNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"CitvProvider\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"CitvExpiryDate\" timestamp with time zone NULL;"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"TucNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"ResolutionNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"EquipmentJson\" text NOT NULL DEFAULT '[]';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN IF NOT EXISTS \"Status\" text NOT NULL DEFAULT 'Operativo en Ruta (En Servicio)';"); } catch { }
    try { db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"VehicleImages\" (\"Id\" uuid NOT NULL PRIMARY KEY, \"VehicleId\" uuid NOT NULL REFERENCES \"Vehicles\"(\"Id\") ON DELETE CASCADE, \"ImageUrl\" text NOT NULL DEFAULT '', \"IsCover\" boolean NOT NULL DEFAULT false);"); } catch { }
    try { db.Database.ExecuteSqlRaw("UPDATE \"Vehicles\" SET \"IsActive\" = true WHERE \"Status\" != 'Baja Operativa';"); } catch { }
    try { db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""FeaturedPackages"" (""Id"" uuid NOT NULL PRIMARY KEY, ""TourPackageId"" uuid NOT NULL REFERENCES ""TourPackages""(""Id"") ON DELETE CASCADE, ""DisplayOrder"" integer NOT NULL DEFAULT 1, ""CommercialTitle"" text NOT NULL DEFAULT '', ""CommercialSubtitle"" text NOT NULL DEFAULT '', ""PromoBadge"" text NOT NULL DEFAULT '', ""PromoPrice"" numeric, ""SyncInventory"" boolean NOT NULL DEFAULT true);"); } catch { }
    try { db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""Categories"" (""Id"" uuid NOT NULL PRIMARY KEY, ""Name"" text NOT NULL, ""Description"" text, ""Icon"" text, ""IsActive"" boolean NOT NULL DEFAULT true, ""DisplayOrder"" integer NOT NULL DEFAULT 1, ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP);"); } catch { }
    try { db.Database.ExecuteSqlRaw(@"ALTER TABLE ""TourPackages"" ADD COLUMN IF NOT EXISTS ""CategoryId"" uuid REFERENCES ""Categories""(""Id"") ON DELETE SET NULL;"); } catch { }
    try
    {
        if (!db.Categories.Any())
        {
            var cat1 = new SIGETOUR.API.Core.Entities.Category { Id = Guid.NewGuid(), Name = "Arqueología y Petroglifos", Description = "Rutas ancestrales, ventanillas y petroglifos preincas", Icon = "history_edu", DisplayOrder = 1, IsActive = true };
            var cat2 = new SIGETOUR.API.Core.Entities.Category { Id = Guid.NewGuid(), Name = "Termalismo y Spa", Description = "Aguas termales medicinales y baños relajantes", Icon = "hot_tub", DisplayOrder = 2, IsActive = true };
            var cat3 = new SIGETOUR.API.Core.Entities.Category { Id = Guid.NewGuid(), Name = "Naturaleza y Aventura", Description = "Bosques de piedras, lagunas, cascadas y trekking", Icon = "landscape", DisplayOrder = 3, IsActive = true };
            var cat4 = new SIGETOUR.API.Core.Entities.Category { Id = Guid.NewGuid(), Name = "Tradición y Agroturismo", Description = "Granjas lecheras, talleres artesanales y gastronomía típica", Icon = "agriculture", DisplayOrder = 4, IsActive = true };
            var cat5 = new SIGETOUR.API.Core.Entities.Category { Id = Guid.NewGuid(), Name = "City Tour y Tradición", Description = "Centro histórico, iglesias coloniales y Cuarto del Rescate", Icon = "location_city", DisplayOrder = 5, IsActive = true };
            db.Categories.AddRange(cat1, cat2, cat3, cat4, cat5);
            db.SaveChanges();

            // Link existing tours
            var existingTours = db.TourPackages.Where(t => t.CategoryId == null).ToList();
            foreach (var t in existingTours)
            {
                var lower = t.Title.ToLower();
                if (lower.Contains("cumbemayo")) t.CategoryId = cat3.Id;
                else if (lower.Contains("baños") || lower.Contains("inca")) t.CategoryId = cat2.Id;
                else if (lower.Contains("otuzco")) t.CategoryId = cat1.Id;
                else t.CategoryId = cat5.Id;
            }
            db.SaveChanges();
        }
    }
    catch { }
    try
    {
        if (!db.Vehicles.Any())
        {
            var v1 = new SIGETOUR.API.Core.Entities.Vehicle
            {
                Id = Guid.NewGuid(),
                LicensePlate = "T7C-841",
                Model = "Mercedes-Benz Sprinter 515 VIP",
                Category = "Van Turística VIP (14 - 19 Pas.)",
                ManufactureYear = 2024,
                SeatCapacity = 19,
                Color = "Blanco Glaciar",
                FuelType = "Diésel B5 S-50 (Ultra Bajo Azufre)",
                SoatNumber = "748920192-RIMAC",
                SoatProvider = "Rímac Seguros",
                SoatExpiryDate = DateTime.UtcNow.AddMonths(10),
                CitvNumber = "CITV-2024-84912",
                CitvProvider = "Revitec Cajamarca",
                CitvExpiryDate = DateTime.UtcNow.AddMonths(8),
                TucNumber = "TUC-DRTC-004812",
                ResolutionNumber = "RD-0452-2024-MTC/DRTC",
                Status = "Operativo en Ruta (En Servicio)",
                IsActive = true
            };
            v1.Images.Add(new SIGETOUR.API.Core.Entities.VehicleImage
            {
                Id = Guid.NewGuid(),
                VehicleId = v1.Id,
                ImageUrl = "https://lh3.googleusercontent.com/aida-public/AB6AXuBC7BURpMqQ-hvVEt2bVvyXxDCGvmpK4jG1ehi1_9kro4AkrvV1FfAxmEdjKo07xbC5DhKpbe7O5wgme9RYw1M0BebEH-HTct2kvj4xkod1WccZqovwLmRq7W6JIBPN7wAeQjiYYyJp5aesWP1H0LW13piJexwt2P_flgPZYtgDB4MaGvCoapl_VT0ChWDjYnbINExV4l_60pgGfuL5aaMwVfh31dswF5ICihupM-VaqXK5M0mzyfErNw",
                IsCover = true
            });

            var v2 = new SIGETOUR.API.Core.Entities.Vehicle
            {
                Id = Guid.NewGuid(),
                LicensePlate = "M4B-912",
                Model = "Toyota HiAce Gran Commuter",
                Category = "Van Turística VIP (14 - 19 Pas.)",
                ManufactureYear = 2023,
                SeatCapacity = 14,
                Color = "Plata Metálico",
                FuelType = "Diésel B5 S-50 (Ultra Bajo Azufre)",
                SoatNumber = "849201844-PACIFICO",
                SoatProvider = "Pacífico Seguros",
                SoatExpiryDate = DateTime.UtcNow.AddMonths(9),
                CitvNumber = "CITV-2024-91023",
                CitvProvider = "Revitec Cajamarca",
                CitvExpiryDate = DateTime.UtcNow.AddMonths(7),
                TucNumber = "TUC-DRTC-005119",
                ResolutionNumber = "RD-0511-2023-MTC/DRTC",
                Status = "Operativo en Ruta (En Servicio)",
                IsActive = true
            };
            v2.Images.Add(new SIGETOUR.API.Core.Entities.VehicleImage
            {
                Id = Guid.NewGuid(),
                VehicleId = v2.Id,
                ImageUrl = "https://lh3.googleusercontent.com/aida-public/AB6AXuB75Is5JazVkkNYTGVxNNn99nKpbZ2C9nhdKkd3T6PZpiyXToJgnzeXFivDxt3XRm1L0r9lpY-tSZlskG6V4jR8_R2xUJJL9isZg-jqdPqBDX3cQMI25p0VT1t3jTwkqm9ogwYmYPXdI_80JioeTNp59QwEJLr6sbIvf_xKs7GiLj8Ay-ZnL8fw8YERwb-cDSHHGM9vPDnbdhfpcIQLFu_Im0l9V4dX9SgfntQLQqwNOlsvjz50yZDiJA",
                IsCover = true
            });

            db.Vehicles.AddRange(v1, v2);
            db.SaveChanges();
        }
    }
    catch { }

    SIGETOUR.API.Infrastructure.Data.IdentitySeeder.SeedUsersAndRolesAsync(scope.ServiceProvider).Wait();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers(); // For API
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();






