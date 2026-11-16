using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Core.Security;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;
using Xunit;

namespace SIGETOUR.Tests.Audit
{
    public class HardeningAuditPass11Tests
    {
        private static AppDbContext CreateInMemoryDb(string dbName)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
            return new AppDbContext(options);
        }

        private static IConfiguration CreateConfig()
        {
            var dict = new Dictionary<string, string?>
            {
                { "Payments:CheckoutSigningKey", "super_secret_test_key_phase_11_1234567890" },
                { "Nubefact:Mode", "Demo" },
                { "Nubefact:Ruta", "https://demo.nubefact.com/api/v1/test" },
                { "Nubefact:Token", "demo_token_123" },
                { "Nubefact:BoletaSeries", "BBB1" },
                { "Nubefact:FacturaSeries", "FFF1" },
                { "Company:Ruc", "20601234567" },
                { "Company:TradeName", "TURISMO CHILÓN" },
                { "Company:LegalName", "TRANSPORTE & TURISMO CAJAMARCA S.A.C." },
                { "Company:FiscalAddress", "Jr. 2 de Mayo 569, Cajamarca - Perú" },
                { "Company:Phone", "+51 901 195 392" }
            };
            return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        }

        private static IServiceProvider CreateRazorServiceProvider(IConfiguration config)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(config);
            services.AddSingleton(Mock.Of<ICompanyContactService>());

            var diagListener = new System.Diagnostics.DiagnosticListener("Microsoft.AspNetCore");
            services.AddSingleton<System.Diagnostics.DiagnosticSource>(diagListener);
            services.AddSingleton<System.Diagnostics.DiagnosticListener>(diagListener);

            services.AddControllersWithViews()
                .AddApplicationPart(typeof(BookingController).Assembly);

            var urlHelper = CreateRealUrlHelper();
            var factoryMock = new Mock<IUrlHelperFactory>();
            factoryMock.Setup(f => f.GetUrlHelper(It.IsAny<ActionContext>())).Returns(urlHelper);
            services.AddSingleton<IUrlHelperFactory>(factoryMock.Object);

            return services.BuildServiceProvider();
        }

        private static Booking SeedBooking(AppDbContext db, decimal total = 100m, decimal paid = 100m)
        {
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Cumbe Mayo Pass 11",
                Slug = "cumbe-mayo-p11-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = total,
                IsActive = true
            };
            db.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-P11-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Mariana Pasajera 11",
                CustomerDni = "76543210",
                TravelDate = DateTime.UtcNow.Date.AddDays(3),
                ShiftName = "Mañana",
                TotalPassengers = 1,
                TotalAmount = total,
                PaidAmount = paid,
                InvoiceType = "boleta",
                PaymentMethod = "Culqi",
                Status = BookingStatus.Confirmed
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                TourPackage = tour,
                Passengers = 1,
                UnitPrice = total,
                SubTotal = total
            });
            db.Bookings.Add(booking);
            db.SaveChanges();
            return booking;
        }

        private static IUrlHelper CreateRealUrlHelper()
        {
            var urlMock = new Mock<IUrlHelper>();
            urlMock.Setup(u => u.Action(It.IsAny<UrlActionContext>()))
                .Returns<UrlActionContext>(ctx =>
                {
                    var action = ctx.Action;
                    var controllerName = ctx.Controller ?? "Booking";
                    var values = new RouteValueDictionary(ctx.Values);
                    var queryList = new List<string>();
                    string idPart = "";
                    foreach (var kvp in values)
                    {
                        if (kvp.Key.Equals("id", StringComparison.OrdinalIgnoreCase))
                        {
                            idPart = $"/{kvp.Value}";
                        }
                        else if (kvp.Value != null)
                        {
                            queryList.Add($"{kvp.Key}={kvp.Value}");
                        }
                    }
                    var query = queryList.Count > 0 ? "?" + string.Join("&", queryList) : "";
                    return $"/{controllerName}/{action}{idPart}{query}";
                });
            return urlMock.Object;
        }

        private static BookingController CreateController(
            AppDbContext context,
            ClaimsPrincipal? user = null,
            CheckoutAccess? checkoutAccess = null,
            IConfiguration? config = null,
            IServiceProvider? sp = null)
        {
            var cfg = config ?? CreateConfig();
            var chk = checkoutAccess ?? new CheckoutAccess(cfg);
            var controller = new BookingController(context, chk, cfg);
            var serviceProvider = sp ?? CreateRazorServiceProvider(cfg);
            var httpContext = new DefaultHttpContext
            {
                User = user ?? new ClaimsPrincipal(new ClaimsIdentity()),
                RequestServices = serviceProvider
            };
            var routeData = new RouteData();
            routeData.Routers.Add(Mock.Of<IRouter>());
            routeData.Values["controller"] = "Booking";
            routeData.Values["action"] = "Ticket";
            var actionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor
            {
                ControllerName = "Booking",
                ActionName = "Ticket",
                RouteValues = new Dictionary<string, string?>
                {
                    ["controller"] = "Booking",
                    ["action"] = "Ticket"
                }
            };
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
                RouteData = routeData,
                ActionDescriptor = actionDescriptor
            };
            controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
            controller.Url = CreateRealUrlHelper();
            return controller;
        }

        private static async Task<string> RenderTicketViewAsync(
            ViewResult viewResult,
            BookingController controller,
            IServiceProvider sp)
        {
            var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
            var actionContext = controller.ControllerContext;
            if (actionContext.RouteData == null)
            {
                actionContext.RouteData = new RouteData();
            }
            if (!actionContext.RouteData.Values.ContainsKey("controller"))
            {
                actionContext.RouteData.Values["controller"] = "Booking";
            }
            if (!actionContext.RouteData.Values.ContainsKey("action"))
            {
                actionContext.RouteData.Values["action"] = "Ticket";
            }

            var findResult = viewEngine.FindView(actionContext, viewResult.ViewName ?? "Ticket", isMainPage: false);
            if (!findResult.Success)
            {
                throw new InvalidOperationException($"No se pudo encontrar la vista 'Ticket'. Buscado en: {string.Join(", ", findResult.SearchedLocations)}");
            }

            await using var sw = new StringWriter();
            var viewContext = new ViewContext(
                actionContext,
                findResult.View,
                viewResult.ViewData,
                viewResult.TempData,
                sw,
                new HtmlHelperOptions()
            );

            await findResult.View.RenderAsync(viewContext);
            return sw.ToString();
        }

        private static string ExtractHrefBySize(string html, string targetSize)
        {
            var match = Regex.Match(html, $"href=\"([^\"]*size={targetSize}[^\"]*)\"", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                throw new InvalidOperationException($"No se encontró un enlace para size={targetSize} en el HTML renderizado.");
            }
            return WebUtility.HtmlDecode(match.Groups[1].Value);
        }

        private static async Task<(IActionResult Result, BookingController Controller)> FollowUrlAsync(
            AppDbContext db,
            string url,
            ClaimsPrincipal? user,
            CheckoutAccess chk,
            IConfiguration cfg,
            IServiceProvider sp)
        {
            var controller = CreateController(db, user, chk, cfg, sp);
            var uri = new Uri("http://localhost" + url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

            string? pathIdStr = null;
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length >= 3 && Guid.TryParse(segments[2], out var parsedPathId))
            {
                pathIdStr = parsedPathId.ToString();
            }

            Guid effectiveId = Guid.Empty;
            if (!string.IsNullOrEmpty(pathIdStr))
            {
                effectiveId = Guid.Parse(pathIdStr);
            }
            else if (!string.IsNullOrEmpty(query["id"]))
            {
                effectiveId = Guid.Parse(query["id"]!);
            }

            string size = query["size"] ?? "80";
            Guid? paymentId = !string.IsNullOrEmpty(query["paymentId"]) ? Guid.Parse(query["paymentId"]!) : null;
            Guid? cashTransactionId = !string.IsNullOrEmpty(query["cashTransactionId"]) ? Guid.Parse(query["cashTransactionId"]!) : null;
            Guid? receiptId = !string.IsNullOrEmpty(query["receiptId"]) ? Guid.Parse(query["receiptId"]!) : null;
            string? token = query["token"];

            var result = await controller.Ticket(effectiveId, size, paymentId, token, cashTransactionId, receiptId);
            return (result, controller);
        }

        private static ClaimsPrincipal CreateStaffUser(bool hasManage = true)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, "AdminUser"),
                new Claim(ClaimTypes.Role, "SuperAdmin")
            };
            if (hasManage)
            {
                claims.Add(new Claim("Permission", AppPermissions.ReceiptsManage));
                claims.Add(new Claim("Permission", AppPermissions.ReceiptsView));
                claims.Add(new Claim("Permission", AppPermissions.BookingsManage));
                claims.Add(new Claim("Permission", AppPermissions.BookingsView));
            }
            else
            {
                claims.Add(new Claim("Permission", AppPermissions.ReceiptsView));
                claims.Add(new Claim("Permission", AppPermissions.BookingsView));
            }
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        }

        // =========================================================================
        // 1. CASOS DE RESOLUCIÓN TIPADA (SECCIÓN REQUERIDA 1)
        // =========================================================================

        [Fact]
        public async Task Resolve_SingleReceiptMatchingFkAndKey_IsExact_NoConflict()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 100m, 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = DateTime.UtcNow
            };
            var cash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow
            };
            // Documento que coincide POR AMBOS CAMPOS (FK y clave canónica)
            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                CashTransactionId = cash.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00001001",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var controller = CreateController(db, CreateStaffUser());
            var actionResult = await controller.Ticket(booking.Id, cashTransactionId: cash.Id);
            var viewResult = Assert.IsType<ViewResult>(actionResult);

            Assert.Equal(200, controller.Response.StatusCode);
            Assert.False(controller.ViewBag.IsConflict == true);
            var modelReceipt = Assert.IsType<ElectronicReceipt>(viewResult.Model);
            Assert.Equal(receipt.Id, modelReceipt.Id);
        }

        [Fact]
        public async Task Resolve_CulqiKeyAndCashKeyForSamePayment_IsConflict()
        {
            // Caso reproducido: Doc A por CULQI:{id} y Doc B por CASH:{id} para la misma operación financiera
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 100m, 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
            };
            var cash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10)
            };
            // Documento A asociado por clave Culqi
            var receiptA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00001010",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            // Documento B (diferente ID) asociado por clave de caja
            var receiptB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00001011",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            };
            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(receiptA, receiptB);
            await db.SaveChangesAsync();

            var controller = CreateController(db, CreateStaffUser());
            var actionResult = await controller.Ticket(booking.Id, cashTransactionId: cash.Id);
            var viewResult = Assert.IsType<ViewResult>(actionResult);

            // Debe responder con HTTP 409 Conflict
            Assert.Equal((int)HttpStatusCode.Conflict, controller.Response.StatusCode);
            Assert.True(controller.ViewBag.IsConflict == true);
            Assert.Null(viewResult.Model); // No elige arbitrariamente ninguno

            ReceiptResolutionResult resolution = Assert.IsType<ReceiptResolutionResult>((object)controller.ViewBag.ConflictResolution);
            Assert.True(resolution.IsConflict);
            Assert.Equal(2, resolution.Candidates.Count);
            Assert.Contains(receiptA.Id, resolution.Candidates.Select(c => c.Id));
            Assert.Contains(receiptB.Id, resolution.Candidates.Select(c => c.Id));
        }

        [Fact]
        public async Task Resolve_ManualCashWithFkAndKeyConflict_IsConflict()
        {
            // Movimiento manual con Doc A por FK CashTransactionId y Doc B por clave CASH
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 60m, 60m);

            var manualCash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                TransactionType = "Ingreso",
                Amount = 60m,
                Category = "Cobro Presencial",
                CreatedAt = DateTime.UtcNow.AddMinutes(-20)
            };
            var receiptA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CashTransactionId = manualCash.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00002001",
                TotalAmount = 60m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
            };
            var receiptB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CASH:{manualCash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00002002",
                TotalAmount = 60m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            db.CashTransactions.Add(manualCash);
            db.ElectronicReceipts.AddRange(receiptA, receiptB);
            await db.SaveChangesAsync();

            var controller = CreateController(db, CreateStaffUser());
            var actionResult = await controller.Ticket(booking.Id, cashTransactionId: manualCash.Id);
            var viewResult = Assert.IsType<ViewResult>(actionResult);

            Assert.Equal((int)HttpStatusCode.Conflict, controller.Response.StatusCode);
            Assert.True(controller.ViewBag.IsConflict == true);
            Assert.Null(viewResult.Model);

            var resolution = Assert.IsType<ReceiptResolutionResult>(controller.ViewBag.ConflictResolution);
            Assert.True(resolution.IsConflict);
            Assert.Equal(2, resolution.Candidates.Count);
        }

        [Fact]
        public async Task Resolve_TwoReceiptsWithSameDateOrAmount_DoesNotSelectArbitrarily()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 100m, 100m);

            var now = DateTime.UtcNow;
            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = now
            };
            var cash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = now
            };
            var receiptA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00003001",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = now
            };
            var receiptB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00003002",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = now
            };
            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(receiptA, receiptB);
            await db.SaveChangesAsync();

            var controller = CreateController(db, CreateStaffUser());
            var actionResult = await controller.Ticket(booking.Id, paymentId: culqi.Id);
            var viewResult = Assert.IsType<ViewResult>(actionResult);

            // A pesar de tener idéntica fecha e idéntico monto, NO elige por FirstOrDefault
            Assert.Equal((int)HttpStatusCode.Conflict, controller.Response.StatusCode);
            Assert.True(controller.ViewBag.IsConflict == true);
            Assert.Null(viewResult.Model);
        }

        [Fact]
        public async Task Resolve_ReceiptFromOtherBooking_NeverIncludedInCandidates()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var bookingA = SeedBooking(db, 100m, 100m);
            var bookingB = SeedBooking(db, 100m, 100m);

            var culqiA = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = bookingA.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = DateTime.UtcNow
            };
            var cashA = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = bookingA.Id,
                CulqiPaymentId = culqiA.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow
            };
            var receiptForB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = bookingB.Id, // Pertenece a otra reserva
                BillableOperationKey = $"CULQI:{culqiA.Id:N}", // Clave similar mal atribuida
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00004001",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqiA);
            db.CashTransactions.Add(cashA);
            db.ElectronicReceipts.Add(receiptForB);
            await db.SaveChangesAsync();

            var controller = CreateController(db, CreateStaffUser());
            var actionResult = await controller.Ticket(bookingA.Id, paymentId: culqiA.Id);
            var viewResult = Assert.IsType<ViewResult>(actionResult);

            // El comprobante de la otra reserva NO debe incorporarse a los candidatos de bookingA
            Assert.Equal(200, controller.Response.StatusCode);
            Assert.False(controller.ViewBag.IsConflict == true);
            Assert.Null(viewResult.Model); // Queda como no facturado para bookingA (provisional legítimo)
        }

        [Fact]
        public async Task Resolve_NoReceipt_ReturnsNoReceiptStatus_PreservesProvisional()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 100m, 100m);

            var manualCash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(manualCash);
            await db.SaveChangesAsync();

            var controller = CreateController(db, CreateStaffUser());
            var actionResult = await controller.Ticket(booking.Id, cashTransactionId: manualCash.Id);
            var viewResult = Assert.IsType<ViewResult>(actionResult);

            Assert.Equal(200, controller.Response.StatusCode);
            Assert.False(controller.ViewBag.IsConflict == true);
            Assert.Null(viewResult.Model);
        }

        // =========================================================================
        // 2. CASOS DE NAVEGACIÓN Y RENDERIZADO RAZOR (SECCIÓN REQUERIDA 2)
        // =========================================================================

        [Fact]
        public async Task Ticket_AmbiguousOperation_ConsistentAcrossBookingIdPaymentIdCashTx()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 100m, 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = DateTime.UtcNow
            };
            var cash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow
            };
            var receiptA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00005001",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            var receiptB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00005002",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(receiptA, receiptB);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var sp = CreateRazorServiceProvider(config);
            var staff = CreateStaffUser();

            // 1. Acceso vía bookingId directo (operación única en conflicto)
            var c1 = CreateController(db, staff, config: config, sp: sp);
            var res1 = await c1.Ticket(booking.Id);
            var vr1 = Assert.IsType<ViewResult>(res1);
            Assert.Equal((int)HttpStatusCode.Conflict, c1.Response.StatusCode);
            Assert.True(c1.ViewBag.IsConflict == true);
            var html1 = await RenderTicketViewAsync(vr1, c1, sp);
            Assert.Contains("ASOCIACIÓN AMBIGUA", html1);
            Assert.Contains("SE REQUIERE REVISIÓN ADMINISTRATIVA", html1);
            Assert.DoesNotContain("*** VOUCHER PROVISIONAL ***", html1);
            Assert.DoesNotContain("Emitir Comprobante NubeFact", html1);

            // 2. Acceso vía cashTransactionId
            var c2 = CreateController(db, staff, config: config, sp: sp);
            var res2 = await c2.Ticket(booking.Id, cashTransactionId: cash.Id);
            var vr2 = Assert.IsType<ViewResult>(res2);
            Assert.Equal((int)HttpStatusCode.Conflict, c2.Response.StatusCode);
            Assert.True(c2.ViewBag.IsConflict == true);
            var html2 = await RenderTicketViewAsync(vr2, c2, sp);
            Assert.Contains("ASOCIACIÓN AMBIGUA", html2);
            Assert.DoesNotContain("*** VOUCHER PROVISIONAL ***", html2);
            Assert.DoesNotContain("Emitir Comprobante NubeFact", html2);

            // 3. Acceso vía paymentId
            var c3 = CreateController(db, staff, config: config, sp: sp);
            var res3 = await c3.Ticket(booking.Id, paymentId: culqi.Id);
            var vr3 = Assert.IsType<ViewResult>(res3);
            Assert.Equal((int)HttpStatusCode.Conflict, c3.Response.StatusCode);
            Assert.True(c3.ViewBag.IsConflict == true);
            var html3 = await RenderTicketViewAsync(vr3, c3, sp);
            Assert.Contains("ASOCIACIÓN AMBIGUA", html3);
            Assert.DoesNotContain("*** VOUCHER PROVISIONAL ***", html3);
            Assert.DoesNotContain("Emitir Comprobante NubeFact", html3);
        }

        [Fact]
        public async Task Ticket_StaffUser_CanOpenSpecificReceiptById_Returns200AndPreservesAcrossSizes()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 100m, 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = DateTime.UtcNow
            };
            var cash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow
            };
            var receiptA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00006001",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            var receiptB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00006002",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(receiptA, receiptB);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var sp = CreateRazorServiceProvider(config);
            var chk = new CheckoutAccess(config);
            var staff = CreateStaffUser();

            // 1. Abrir conflicto y verificar que para staff se listan ambos candidatos con enlaces para abrir
            var cConflict = CreateController(db, staff, config: config, sp: sp);
            var resConflict = await cConflict.Ticket(booking.Id, cashTransactionId: cash.Id);
            var vrConflict = Assert.IsType<ViewResult>(resConflict);
            var htmlConflict = await RenderTicketViewAsync(vrConflict, cConflict, sp);

            Assert.Contains("BBB1-00006001", htmlConflict);
            Assert.Contains("BBB1-00006002", htmlConflict);
            Assert.Contains("Abrir Comprobante", htmlConflict);

            // 2. Abrir explícitamente el documento A por receiptId
            var cDirect = CreateController(db, staff, config: config, sp: sp);
            var resDirect = await cDirect.Ticket(booking.Id, size: "80", receiptId: receiptA.Id);
            var vrDirect = Assert.IsType<ViewResult>(resDirect);
            Assert.Equal(200, cDirect.Response.StatusCode);
            var htmlDirect80 = await RenderTicketViewAsync(vrDirect, cDirect, sp);

            Assert.Contains("BBB1-00006001", htmlDirect80);
            Assert.DoesNotContain("BBB1-00006002", htmlDirect80);
            Assert.DoesNotContain("ASOCIACIÓN AMBIGUA", htmlDirect80);

            // 3. Cambiar a 58 mm y comprobar preservación de identidad
            var url58 = ExtractHrefBySize(htmlDirect80, "58");
            var (res58, c58) = await FollowUrlAsync(db, url58, staff, chk, config, sp);
            var vr58 = Assert.IsType<ViewResult>(res58);
            var htmlDirect58 = await RenderTicketViewAsync(vr58, c58, sp);

            Assert.Contains("BBB1-00006001", htmlDirect58);

            // 4. Regresar a 80 mm
            var url80 = ExtractHrefBySize(htmlDirect58, "80");
            var (res80Back, c80Back) = await FollowUrlAsync(db, url80, staff, chk, config, sp);
            var vr80Back = Assert.IsType<ViewResult>(res80Back);
            var htmlDirect80Back = await RenderTicketViewAsync(vr80Back, c80Back, sp);

            Assert.Contains("BBB1-00006001", htmlDirect80Back);
        }

        [Fact]
        public async Task Ticket_TokenVisitor_WhenConflict_RendersFriendlyNoticeWithoutAdminActions()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 100m, 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = DateTime.UtcNow
            };
            var cash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow
            };
            var receiptA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00007001",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            var receiptB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00007002",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(receiptA, receiptB);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var sp = CreateRazorServiceProvider(config);
            var chk = new CheckoutAccess(config);
            string validToken = chk.Create(booking.Id);

            // Visitante sin Claims de staff
            var visitorPrincipal = new ClaimsPrincipal(new ClaimsIdentity());
            var controller = CreateController(db, visitorPrincipal, chk, config, sp);

            var actionResult = await controller.Ticket(booking.Id, token: validToken);
            var viewResult = Assert.IsType<ViewResult>(actionResult);

            Assert.Equal((int)HttpStatusCode.Conflict, controller.Response.StatusCode);
            Assert.True(controller.ViewBag.IsConflict == true);

            var html = await RenderTicketViewAsync(viewResult, controller, sp);

            // Debe mostrar el aviso sencillo
            Assert.Contains("Esta operación requiere revisión administrativa por parte del equipo de Turismo Chilón", html);
            // NO debe exponer listado de comprobantes técnicos ni enlaces de conciliación
            Assert.DoesNotContain("Abrir Comprobante", html);
            Assert.DoesNotContain("CULQI:", html);
            Assert.DoesNotContain("CASH:", html);
            Assert.DoesNotContain("Emitir Comprobante NubeFact", html);
        }

        [Fact]
        public async Task Ticket_TokenVisitor_MismatchedBookingToken_Returns401Unauthorized()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var bookingA = SeedBooking(db, 100m, 100m);
            var bookingB = SeedBooking(db, 100m, 100m);

            var config = CreateConfig();
            var chk = new CheckoutAccess(config);
            string tokenForB = chk.Create(bookingB.Id);

            var controller = CreateController(db, new ClaimsPrincipal(new ClaimsIdentity()), chk, config);
            var actionResult = await controller.Ticket(bookingA.Id, token: tokenForB);

            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(actionResult);
            Assert.Equal((int)HttpStatusCode.Unauthorized, unauthResult.StatusCode);
        }

        // =========================================================================
        // 3. CASOS DE EFECTOS: CERO ESCRITURAS Y CERO EMISIONES (SECCIÓN 3)
        // =========================================================================

        [Fact]
        public async Task Ticket_GetConflict_DoesNotMutateDatabaseOrEmitReceipts()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 100m, 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = DateTime.UtcNow
            };
            var cash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow
            };
            var receiptA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00008001",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            var receiptB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00008002",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(receiptA, receiptB);
            await db.SaveChangesAsync();

            var controller = CreateController(db, CreateStaffUser());
            var res = await controller.Ticket(booking.Id, cashTransactionId: cash.Id);

            // Verificaciones de inmutabilidad estricta
            Assert.False(db.ChangeTracker.HasChanges());
            Assert.Equal(2, await db.ElectronicReceipts.CountAsync());
            Assert.Equal("Accepted", (await db.ElectronicReceipts.FindAsync(receiptA.Id))?.Status);
            Assert.Equal("Accepted", (await db.ElectronicReceipts.FindAsync(receiptB.Id))?.Status);
        }

        [Fact]
        public async Task RetryReceipt_AmbiguousOperation_RejectedWith409AndZeroExternalCalls()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 100m, 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = DateTime.UtcNow
            };
            var cash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow
            };
            var receiptA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00009001",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            var receiptB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00009002",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(receiptA, receiptB);
            await db.SaveChangesAsync();

            // Mock estricto del servicio de facturación que NO debe ser invocado
            var receiptServiceMock = new Mock<IElectronicReceiptService>(MockBehavior.Strict);

            var controller = CreateController(db, CreateStaffUser());

            // POST de emisión para la operación ambigua vía cashTransactionId
            var postResult = await controller.RetryReceipt(
                receiptId: null,
                bookingId: booking.Id,
                paymentId: null,
                receiptService: receiptServiceMock.Object,
                cashTransactionId: cash.Id);

            var statusResult = Assert.IsType<ObjectResult>(postResult);
            Assert.Equal((int)HttpStatusCode.Conflict, statusResult.StatusCode);

            // Cero llamadas al proveedor o servicio
            receiptServiceMock.VerifyNoOtherCalls();

            // Base de datos sin cambios
            Assert.Equal(2, await db.ElectronicReceipts.CountAsync());
        }

        [Fact]
        public async Task NubefactService_GenerateReceipt_AmbiguousOperation_ThrowsInvalidOperationException()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 100m, 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = DateTime.UtcNow
            };
            var cash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow
            };
            var receiptA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00010001",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            var receiptB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00010002",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(receiptA, receiptB);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            using var httpClient = new HttpClient();
            var service = new NubefactInvoiceService(
                db,
                config,
                httpClient,
                NullLogger<NubefactInvoiceService>.Instance);

            // Intento directo de emitir la operación ambigua
            var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                service.GenerateReceiptAsync(booking.Id, culqiPaymentId: culqi.Id, exactReceiptId: null, claimToken: null, cashTransactionId: cash.Id));

            Assert.Contains("Conflicto de identidad", ex.Message);
            Assert.Contains("múltiples comprobantes asociados", ex.Message);
        }

        [Fact]
        public async Task Ticket_HistoricalReceipt40AndNewCash60_NormalizesAsTwoDistinctOperations()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 100m, 100m);

            // 1. Boleta histórica de S/ 40
            var histReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                CashTransactionId = null,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00011001",
                TotalAmount = 40m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-1)
            };
            // 2. Nuevo cobro de S/ 60 manual
            var newCash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                TransactionType = "Ingreso",
                Amount = 60m,
                Category = "Saldo Final",
                CreatedAt = DateTime.UtcNow
            };
            db.ElectronicReceipts.Add(histReceipt);
            db.CashTransactions.Add(newCash);
            await db.SaveChangesAsync();

            var controller = CreateController(db, CreateStaffUser());
            var result = await controller.Ticket(booking.Id);
            var viewResult = Assert.IsType<ViewResult>(result);

            // Se deben normalizar como exactamente 2 operaciones distintas
            List<BookingOperationOptionViewModel> ops = Assert.IsType<List<BookingOperationOptionViewModel>>((object)controller.ViewBag.AvailableOperations);
            Assert.Equal(2, ops.Count);

            var histOp = ops.FirstOrDefault(o => o.ReceiptId == histReceipt.Id);
            Assert.NotNull(histOp);
            Assert.Equal(40m, histOp.Amount);
            Assert.True(histOp.IsBilled);

            var cashOp = ops.FirstOrDefault(o => o.CashTransactionId == newCash.Id);
            Assert.NotNull(cashOp);
            Assert.Equal(60m, cashOp.Amount);
            Assert.False(cashOp.IsBilled);
        }
    }
}
