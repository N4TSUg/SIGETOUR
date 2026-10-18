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
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequiredLength = 4;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// 3. Cookie Configuration for Identity
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SIGETOUR.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/Login";
});

// 4. Authentication & JWT Configuration
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
    options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
})
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
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
builder.Services.AddScoped<ICashReportExportService, CashReportExportService>();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SIGETOUR.API.Infrastructure.Data.AppDbContext>();
    try
    {
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DB Migration Notice] {ex.Message}");
        try { db.Database.EnsureCreated(); } catch { }
    }
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
    try { db.Database.ExecuteSqlRaw(@"ALTER TABLE ""TourPackages"" DROP COLUMN IF EXISTS ""Category"";"); } catch { }
    try { db.Database.ExecuteSqlRaw(@"ALTER TABLE ""TourPackages"" ADD COLUMN IF NOT EXISTS ""CategoryId"" uuid REFERENCES ""Categories""(""Id"") ON DELETE SET NULL;"); } catch { }
    try { db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""Drivers"" (
        ""Id"" uuid NOT NULL PRIMARY KEY,
        ""FullName"" text NOT NULL DEFAULT '',
        ""Dni"" text NOT NULL DEFAULT '',
        ""LicenseNumber"" text NOT NULL DEFAULT '',
        ""LicenseCategory"" text NOT NULL DEFAULT 'A-IIIa (Especial Transporte Turístico)',
        ""LicenseExpiryDate"" timestamp with time zone NULL,
        ""Phone"" text NOT NULL DEFAULT '',
        ""IsActive"" boolean NOT NULL DEFAULT true,
        ""AssignedVehicleId"" uuid REFERENCES ""Vehicles""(""Id"") ON DELETE SET NULL,
        ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
    );"); } catch { }
    try { db.Database.ExecuteSqlRaw(@"ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""AssignedVehicleId"" uuid REFERENCES ""Vehicles""(""Id"") ON DELETE SET NULL;"); } catch { }
    try { db.Database.ExecuteSqlRaw(@"ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""AssignedDriverId"" uuid REFERENCES ""Drivers""(""Id"") ON DELETE SET NULL;"); } catch { }
    try
    {
        if (!db.Drivers.Any())
        {
            var v1 = db.Vehicles.FirstOrDefault(v => v.LicensePlate == "T7C-841") ?? db.Vehicles.FirstOrDefault();
            var v2 = db.Vehicles.Skip(1).FirstOrDefault();
            var v3 = db.Vehicles.Skip(2).FirstOrDefault();

            var d1 = new SIGETOUR.API.Core.Entities.Driver
            {
                Id = Guid.NewGuid(),
                FullName = "Carlos Mendoza Silva",
                Dni = "42910834",
                LicenseNumber = "Q-42910834",
                LicenseCategory = "A-IIIa (Especial Transporte Turístico)",
                LicenseExpiryDate = DateTime.UtcNow.AddYears(2),
                Phone = "976 123 456",
                IsActive = true,
                AssignedVehicleId = v1?.Id
            };
            var d2 = new SIGETOUR.API.Core.Entities.Driver
            {
                Id = Guid.NewGuid(),
                FullName = "Jorge Llanos Bazán",
                Dni = "41829301",
                LicenseNumber = "Q-41829301",
                LicenseCategory = "A-IIIa (Especial Transporte Turístico)",
                LicenseExpiryDate = DateTime.UtcNow.AddYears(1).AddMonths(6),
                Phone = "976 234 567",
                IsActive = true,
                AssignedVehicleId = v2?.Id
            };
            var d3 = new SIGETOUR.API.Core.Entities.Driver
            {
                Id = Guid.NewGuid(),
                FullName = "Manuel Tejada Quispe",
                Dni = "45019283",
                LicenseNumber = "Q-45019283",
                LicenseCategory = "A-IIIc (Profesional Superior)",
                LicenseExpiryDate = DateTime.UtcNow.AddYears(3),
                Phone = "976 345 678",
                IsActive = true,
                AssignedVehicleId = v3?.Id
            };
            var d4 = new SIGETOUR.API.Core.Entities.Driver
            {
                Id = Guid.NewGuid(),
                FullName = "Hernán Villanueva Cabrera",
                Dni = "40918273",
                LicenseNumber = "Q-40918273",
                LicenseCategory = "A-IIIa (Especial Transporte Turístico)",
                LicenseExpiryDate = DateTime.UtcNow.AddMonths(9),
                Phone = "976 456 789",
                IsActive = true
            };
            db.Drivers.AddRange(d1, d2, d3, d4);
            db.SaveChanges();
        }
    }
    catch { }
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

    try
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""CashRegisterShifts"" (
                ""Id"" uuid NOT NULL PRIMARY KEY,
                ""ShiftNumber"" text NOT NULL DEFAULT '',
                ""OpenedByUserName"" text NOT NULL DEFAULT '',
                ""OpenedAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                ""InitialAmount"" numeric NOT NULL DEFAULT 0,
                ""ClosedByUserName"" text,
                ""ClosedAt"" timestamp with time zone,
                ""IsOpen"" boolean NOT NULL DEFAULT true,
                ""ActualCash"" numeric,
                ""Notes"" text
            );

            CREATE TABLE IF NOT EXISTS ""CashTransactions"" (
                ""Id"" uuid NOT NULL PRIMARY KEY,
                ""ShiftId"" uuid NOT NULL REFERENCES ""CashRegisterShifts""(""Id"") ON DELETE CASCADE,
                ""TransactionType"" text NOT NULL DEFAULT 'Ingreso',
                ""Category"" text NOT NULL DEFAULT 'Cobro de Reserva',
                ""Amount"" numeric NOT NULL DEFAULT 0,
                ""PaymentMethod"" text NOT NULL DEFAULT 'Efectivo',
                ""ReferenceCode"" text,
                ""BookingId"" uuid REFERENCES ""Bookings""(""Id"") ON DELETE SET NULL,
                ""Description"" text NOT NULL DEFAULT '',
                ""RegisteredBy"" text NOT NULL DEFAULT '',
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS ""PettyCashAccounts"" (
                ""Id"" uuid NOT NULL PRIMARY KEY,
                ""Name"" text NOT NULL DEFAULT 'Caja Chica Principal - Oficina Cajamarca',
                ""TotalFundAmount"" numeric NOT NULL DEFAULT 4220.00,
                ""ResponsiblePerson"" text NOT NULL DEFAULT 'Administrador Central',
                ""IsActive"" boolean NOT NULL DEFAULT true,
                ""LastReplenishedAt"" timestamp with time zone
            );

            CREATE TABLE IF NOT EXISTS ""PettyCashMovements"" (
                ""Id"" uuid NOT NULL PRIMARY KEY,
                ""AccountId"" uuid NOT NULL REFERENCES ""PettyCashAccounts""(""Id"") ON DELETE CASCADE,
                ""MovementType"" text NOT NULL DEFAULT 'Egreso',
                ""ExpenseCategory"" text NOT NULL DEFAULT 'Combustible',
                ""VoucherType"" text NOT NULL DEFAULT 'Boleta',
                ""VoucherNumber"" text,
                ""Supplier"" text,
                ""Amount"" numeric NOT NULL DEFAULT 0,
                ""ResponsiblePerson"" text NOT NULL DEFAULT '',
                ""Concept"" text NOT NULL DEFAULT '',
                ""RegisteredAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
        ");
    }
    catch { }

    try
    {
        if (!db.PettyCashAccounts.Any())
        {
            var petty = new SIGETOUR.API.Core.Entities.PettyCashAccount
            {
                Id = Guid.NewGuid(),
                Name = "Caja Chica Principal - Oficina Cajamarca",
                TotalFundAmount = 4220.00m,
                ResponsiblePerson = "Administrador Central",
                IsActive = true,
                LastReplenishedAt = DateTime.UtcNow.AddDays(-2)
            };

            petty.Movements.Add(new SIGETOUR.API.Core.Entities.PettyCashMovement
            {
                Id = Guid.NewGuid(),
                AccountId = petty.Id,
                MovementType = "Egreso",
                ExpenseCategory = "Combustible",
                VoucherType = "Factura",
                VoucherNumber = "F001-002891",
                Supplier = "Repsol Cajamarca Norte",
                Amount = 140.00m,
                ResponsiblePerson = "Juan Carlos Rojas (Conductor)",
                Concept = "Abastecimiento Diésel B5 para Van Mercedes T7C-841 ruta Cumbemayo",
                RegisteredAt = DateTime.UtcNow.AddHours(-18)
            });

            petty.Movements.Add(new SIGETOUR.API.Core.Entities.PettyCashMovement
            {
                Id = Guid.NewGuid(),
                AccountId = petty.Id,
                MovementType = "Egreso",
                ExpenseCategory = "Peajes",
                VoucherType = "Boleta",
                VoucherNumber = "B012-009182",
                Supplier = "Concesionaria Vial Sierra Norte",
                Amount = 24.50m,
                ResponsiblePerson = "Juan Carlos Rojas",
                Concept = "Pago Peaje ida y vuelta Circuito Porcón",
                RegisteredAt = DateTime.UtcNow.AddHours(-14)
            });

            petty.Movements.Add(new SIGETOUR.API.Core.Entities.PettyCashMovement
            {
                Id = Guid.NewGuid(),
                AccountId = petty.Id,
                MovementType = "Egreso",
                ExpenseCategory = "Alimentación / Refrigerios",
                VoucherType = "Boleta",
                VoucherNumber = "B002-004120",
                Supplier = "Panadería San Antonio Cajamarca",
                Amount = 65.00m,
                ResponsiblePerson = "Guía Oficial Chilón",
                Concept = "Refrigerios y aguas minerales para pasajeros de salida especial",
                RegisteredAt = DateTime.UtcNow.AddHours(-6)
            });

            db.PettyCashAccounts.Add(petty);
            db.SaveChanges();
        }

        if (!db.CashRegisterShifts.Any())
        {
            var todayStr = DateTime.UtcNow.ToString("yyyyMMdd");
            var shift = new SIGETOUR.API.Core.Entities.CashRegisterShift
            {
                Id = Guid.NewGuid(),
                ShiftNumber = $"CJ-{todayStr}-01",
                OpenedByUserName = "Admin Central",
                OpenedAt = DateTime.UtcNow.Date.AddHours(7),
                InitialAmount = 300.00m,
                IsOpen = true,
                Notes = "Apertura de turno matutino para cobro en counter y pasarela online"
            };

            var confirmedBookings = db.Bookings.Where(b => b.Status == SIGETOUR.API.Core.Entities.BookingStatus.Confirmed).Take(5).ToList();
            foreach (var b in confirmedBookings)
            {
                string pMethod = !string.IsNullOrWhiteSpace(b.PaymentMethod) ? b.PaymentMethod : "Efectivo";
                shift.Transactions.Add(new SIGETOUR.API.Core.Entities.CashTransaction
                {
                    Id = Guid.NewGuid(),
                    ShiftId = shift.Id,
                    TransactionType = "Ingreso",
                    Category = "Cobro de Reserva",
                    Amount = b.TotalAmount,
                    PaymentMethod = pMethod,
                    ReferenceCode = b.ReferenceCode,
                    BookingId = b.Id,
                    Description = $"Cobro por reserva de {b.CustomerName} - Tour con {b.TotalPassengers} pasajero(s)",
                    RegisteredBy = "Sistema / Counter",
                    CreatedAt = b.BookingDate
                });
            }

            db.CashRegisterShifts.Add(shift);
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






