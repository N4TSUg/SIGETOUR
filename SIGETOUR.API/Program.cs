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
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"ItineraryStops\" ADD COLUMN \"EstimatedTime\" text;"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Bookings\" ADD COLUMN \"ShiftName\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Bookings\" ADD COLUMN \"PaidAmount\" numeric NOT NULL DEFAULT 0;"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Bookings\" ADD COLUMN \"PaymentMethod\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Bookings\" ADD COLUMN \"OperationNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Bookings\" ADD COLUMN \"InvoiceType\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Bookings\" ADD COLUMN \"InvoiceNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"Category\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"ManufactureYear\" integer NOT NULL DEFAULT 2020;"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"ChassisNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"EngineNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"Color\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"FuelType\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"SoatNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"SoatProvider\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"SoatIssueDate\" timestamp with time zone NULL;"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"SoatExpiryDate\" timestamp with time zone NULL;"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"CitvNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"CitvProvider\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"CitvExpiryDate\" timestamp with time zone NULL;"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"TucNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"ResolutionNumber\" text NOT NULL DEFAULT '';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"EquipmentJson\" text NOT NULL DEFAULT '[]';"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Vehicles\" ADD COLUMN \"Status\" text NOT NULL DEFAULT 'Operativo en Ruta (En Servicio)';"); } catch { }
    try { db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"VehicleImages\" (\"Id\" uuid NOT NULL PRIMARY KEY, \"VehicleId\" uuid NOT NULL REFERENCES \"Vehicles\"(\"Id\") ON DELETE CASCADE, \"ImageUrl\" text NOT NULL DEFAULT '', \"IsCover\" boolean NOT NULL DEFAULT false);"); } catch { }
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
