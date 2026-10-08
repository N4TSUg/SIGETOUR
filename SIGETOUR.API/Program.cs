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
    try { db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""FeaturedPackages"" (""Id"" uuid NOT NULL PRIMARY KEY, ""TourPackageId"" uuid NOT NULL REFERENCES ""TourPackages""(""Id"") ON DELETE CASCADE, ""DisplayOrder"" integer NOT NULL DEFAULT 1, ""CommercialTitle"" text NOT NULL DEFAULT '', ""CommercialSubtitle"" text NOT NULL DEFAULT '', ""PromoBadge"" text NOT NULL DEFAULT '', ""PromoPrice"" numeric, ""SyncInventory"" boolean NOT NULL DEFAULT true);"); } catch { }
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






