using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
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
    public class HardeningAuditPass10Tests
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
                { "Payments:CheckoutSigningKey", "super_secret_test_key_phase_10_1234567890" },
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
                Title = "Tour Otuzco Pass 10",
                Slug = "otuzco-p10-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = total,
                IsActive = true
            };
            db.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-P10-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Carlos Pasajero 10",
                CustomerDni = "78901234",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
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
            if (match.Success)
            {
                return WebUtility.HtmlDecode(match.Groups[1].Value);
            }
            throw new InvalidOperationException($"No se encontró el enlace con size={targetSize} en el HTML renderizado.");
        }

        private static string ExtractVoucherLink(string html)
        {
            var match = Regex.Match(html, "<a\\s+[^>]*href=\"([^\"]+)\"[^>]*>[\\s\\S]*?Volver al Boleto", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return WebUtility.HtmlDecode(match.Groups[1].Value);
            }
            throw new InvalidOperationException("No se encontró el enlace 'Volver al Boleto' en el HTML renderizado.");
        }

        private static async Task<(IActionResult Result, BookingController Controller)> FollowUrlAsync(
            AppDbContext db,
            string url,
            ClaimsPrincipal? user,
            CheckoutAccess checkoutAccess,
            IConfiguration config,
            IServiceProvider sp)
        {
            var uri = new Uri("http://localhost" + url);
            var pathSegments = uri.AbsolutePath.Trim('/').Split('/');
            Guid id = Guid.Parse(pathSegments[2]);

            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            string? size = query.TryGetValue("size", out var sVal) ? sVal.ToString() : "80";
            Guid? receiptId = query.TryGetValue("receiptId", out var rVal) && Guid.TryParse(rVal, out var rGuid) ? rGuid : null;
            Guid? cashTransactionId = query.TryGetValue("cashTransactionId", out var cVal) && Guid.TryParse(cVal, out var cGuid) ? cGuid : null;
            Guid? paymentId = query.TryGetValue("paymentId", out var pVal) && Guid.TryParse(pVal, out var pGuid) ? pGuid : null;
            string? token = query.TryGetValue("token", out var tVal) ? tVal.ToString() : null;

            var controller = CreateController(db, user, checkoutAccess, config, sp);
            var actionResult = await controller.Ticket(id, size, paymentId, token, cashTransactionId, receiptId);
            return (actionResult, controller);
        }

        private static ClaimsPrincipal CreateStaffUser(string permission = AppPermissions.ReceiptsManage)
        {
            var claims = new List<Claim>
            {
                new Claim("Permission", permission),
                new Claim(ClaimTypes.Name, "StaffUser")
            };
            if (permission == AppPermissions.ReceiptsManage)
            {
                claims.Add(new Claim("Permission", AppPermissions.ReceiptsView));
                claims.Add(new Claim("Permission", AppPermissions.BookingsView));
            }
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        }

        // =========================================================================
        // CASO PRINCIPAL: COMPROBANTE ASOCIADO SOLO POR BILLABLEOPERATIONKEY
        // =========================================================================

        [Fact]
        public async Task Ticket_MainCase_ReceiptAssociatedOnlyByKey_PreservesReceiptAcross80_58_80()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                ProviderId = "chr_culqi_pass10",
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

            // Comprobante asociado ÚNICAMENTE mediante BillableOperationKey
            // CulqiPaymentId = null y CashTransactionId = null
            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                CashTransactionId = null,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00000999",
                TotalAmount = 100m,
                Status = "Accepted",
                Environment = "Demo",
                CreatedAtUtc = DateTime.UtcNow
            };

            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var sp = CreateRazorServiceProvider(config);
            var checkoutAccess = new CheckoutAccess(config);
            var staff = CreateStaffUser();

            // 1. Abrir Ticket por bookingId en 80mm
            var controller1 = CreateController(db, staff, checkoutAccess, config, sp);
            var result1 = await controller1.Ticket(booking.Id, size: "80");
            var viewResult1 = Assert.IsType<ViewResult>(result1);

            // Renderizar Razor HTML
            var html1 = await RenderTicketViewAsync(viewResult1, controller1, sp);

            // Confirmar que muestra el comprobante real y NO un voucher provisional
            Assert.Contains("BBB1-00000999", html1);
            Assert.Contains("S/ 100.00", html1);
            Assert.DoesNotContain("*** VOUCHER PROVISIONAL ***", html1);
            Assert.DoesNotContain("Emitir Comprobante NubeFact", html1);

            // 2. Extraer el enlace real de 58 mm del HTML
            var url58 = ExtractHrefBySize(html1, "58");
            Assert.Contains("size=58", url58);

            // 3. Seguir el enlace de 58 mm en un nuevo contexto MVC
            var (result2, controller2) = await FollowUrlAsync(db, url58, staff, checkoutAccess, config, sp);
            var viewResult2 = Assert.IsType<ViewResult>(result2);
            var html2 = await RenderTicketViewAsync(viewResult2, controller2, sp);

            // Confirmar que el comprobante se conserva intacto en 58 mm
            Assert.Contains("BBB1-00000999", html2);
            Assert.Contains("S/ 100.00", html2);
            Assert.DoesNotContain("*** VOUCHER PROVISIONAL ***", html2);
            Assert.DoesNotContain("Emitir Comprobante NubeFact", html2);

            // 4. Extraer el enlace real de 80 mm del HTML de 58 mm
            var url80 = ExtractHrefBySize(html2, "80");
            Assert.Contains("size=80", url80);

            // 5. Seguir el enlace de regreso a 80 mm
            var (result3, controller3) = await FollowUrlAsync(db, url80, staff, checkoutAccess, config, sp);
            var viewResult3 = Assert.IsType<ViewResult>(result3);
            var html3 = await RenderTicketViewAsync(viewResult3, controller3, sp);

            // Confirmar que el comprobante se conserva intacto al volver a 80 mm
            Assert.Contains("BBB1-00000999", html3);
            Assert.Contains("S/ 100.00", html3);
            Assert.DoesNotContain("*** VOUCHER PROVISIONAL ***", html3);
            Assert.DoesNotContain("Emitir Comprobante NubeFact", html3);
        }

        // =========================================================================
        // ACCESO DIRECTO POR CASHTRANSACTIONID AL COMPROBANTE ASOCIADO POR CLAVE CULQI
        // =========================================================================

        [Fact]
        public async Task Ticket_AccessViaCashTransactionId_WhenAssociatedByKey_ResolvesExactReceipt()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                ProviderId = "chr_caja_access",
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

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                CashTransactionId = null,
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

            var config = CreateConfig();
            var sp = CreateRazorServiceProvider(config);
            var staff = CreateStaffUser();

            // Abrir desde Caja mediante ?cashTransactionId=...
            var controller = CreateController(db, staff, null, config, sp);
            var result = await controller.Ticket(booking.Id, size: "80", cashTransactionId: cash.Id);
            var viewResult = Assert.IsType<ViewResult>(result);

            var html = await RenderTicketViewAsync(viewResult, controller, sp);

            // Confirmar que resuelve el comprobante y no muestra provisional
            Assert.Contains("BBB1-00001001", html);
            Assert.DoesNotContain("*** VOUCHER PROVISIONAL ***", html);
        }

        // =========================================================================
        // ASOCIACIONES POR CLAVE CASH ADMITIDAS POR EL SISTEMA
        // =========================================================================

        [Fact]
        public async Task Ticket_AccessViaManualCash_WhenAssociatedByCashKey_ResolvesExactReceipt()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 80m, paid: 80m);

            var manualCash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 80m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                CashTransactionId = null,
                BillableOperationKey = $"CASH:{manualCash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00001002",
                TotalAmount = 80m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };

            db.CashTransactions.Add(manualCash);
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var sp = CreateRazorServiceProvider(config);
            var staff = CreateStaffUser();

            var controller = CreateController(db, staff, null, config, sp);
            var result = await controller.Ticket(booking.Id, size: "80", cashTransactionId: manualCash.Id);
            var viewResult = Assert.IsType<ViewResult>(result);

            var html = await RenderTicketViewAsync(viewResult, controller, sp);
            Assert.Contains("BBB1-00001002", html);
            Assert.DoesNotContain("*** VOUCHER PROVISIONAL ***", html);
        }

        // =========================================================================
        // ACCESO DIRECTO POR RECEIPTID
        // =========================================================================

        [Fact]
        public async Task Ticket_DirectAccessByReceiptId_PreservesReceiptAcrossSizeSwitch()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Factura",
                Series = "FFF1",
                Number = "00000045",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var sp = CreateRazorServiceProvider(config);
            var checkoutAccess = new CheckoutAccess(config);
            var staff = CreateStaffUser();

            // Abrir por receiptId
            var controller = CreateController(db, staff, checkoutAccess, config, sp);
            var result = await controller.Ticket(receipt.Id, size: "80");
            var viewResult = Assert.IsType<ViewResult>(result);

            var html80 = await RenderTicketViewAsync(viewResult, controller, sp);
            Assert.Contains("FFF1-00000045", html80);

            var url58 = ExtractHrefBySize(html80, "58");
            var (result58, controller58) = await FollowUrlAsync(db, url58, staff, checkoutAccess, config, sp);
            var viewResult58 = Assert.IsType<ViewResult>(result58);
            var html58 = await RenderTicketViewAsync(viewResult58, controller58, sp);

            Assert.Contains("FFF1-00000045", html58);
            Assert.DoesNotContain("*** VOUCHER PROVISIONAL ***", html58);
        }

        // =========================================================================
        // MOVIMIENTO SIN COMPROBANTE: PERMANECE PROVISIONAL Y CONSERVA IDENTIDAD
        // =========================================================================

        [Fact]
        public async Task Ticket_UnbilledMovement_RemainsProvisionalAndRetainsIdentityAcrossSizes()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 60m);

            var unbilledCash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Category = "Abono Parcial",
                Amount = 60m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(unbilledCash);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var sp = CreateRazorServiceProvider(config);
            var checkoutAccess = new CheckoutAccess(config);
            var staff = CreateStaffUser();

            var controller = CreateController(db, staff, checkoutAccess, config, sp);
            var result = await controller.Ticket(booking.Id, size: "80", cashTransactionId: unbilledCash.Id);
            var viewResult = Assert.IsType<ViewResult>(result);

            var html80 = await RenderTicketViewAsync(viewResult, controller, sp);
            Assert.Contains("*** VOUCHER PROVISIONAL ***", html80);
            Assert.Contains("S/ 60.00", html80);

            var url58 = ExtractHrefBySize(html80, "58");
            Assert.Contains($"cashTransactionId={unbilledCash.Id}", url58);

            var (result58, controller58) = await FollowUrlAsync(db, url58, staff, checkoutAccess, config, sp);
            var viewResult58 = Assert.IsType<ViewResult>(result58);
            var html58 = await RenderTicketViewAsync(viewResult58, controller58, sp);

            Assert.Contains("*** VOUCHER PROVISIONAL ***", html58);
            Assert.Contains("S/ 60.00", html58);
        }

        // =========================================================================
        // VISITANTE CON TOKEN VÁLIDO: CONSERVA ACCESO Y REGRESO A VOUCHER
        // =========================================================================

        [Fact]
        public async Task Ticket_TokenVisitor_PreservesAccessAcrossSizesAndReturnToVoucher()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00000088",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var sp = CreateRazorServiceProvider(config);
            var checkoutAccess = new CheckoutAccess(config);
            var validToken = checkoutAccess.Create(booking.Id);

            // Visitante anónimo sin sesión ni cookies
            var controller = CreateController(db, user: null, checkoutAccess, config, sp);
            var result = await controller.Ticket(receipt.Id, size: "80", token: validToken);
            var viewResult = Assert.IsType<ViewResult>(result);

            var html80 = await RenderTicketViewAsync(viewResult, controller, sp);
            Assert.Contains("BBB1-00000088", html80);

            // 1. Seguir enlace a 58 mm
            var url58 = ExtractHrefBySize(html80, "58");
            Assert.Contains($"token={validToken}", url58);

            var (result58, controller58) = await FollowUrlAsync(db, url58, user: null, checkoutAccess, config, sp);
            var viewResult58 = Assert.IsType<ViewResult>(result58);
            var html58 = await RenderTicketViewAsync(viewResult58, controller58, sp);
            Assert.Contains("BBB1-00000088", html58);

            // 2. Extraer enlace a Voucher y seguirlo
            var voucherUrl = ExtractVoucherLink(html58);
            Assert.Contains($"token={validToken}", voucherUrl);

            var voucherUri = new Uri("http://localhost" + voucherUrl);
            var voucherQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(voucherUri.Query);
            var vController = CreateController(db, user: null, checkoutAccess, config, sp);
            var voucherResult = await vController.Voucher(booking.Id, token: voucherQuery["token"]);
            Assert.IsType<ViewResult>(voucherResult);

            // Confirmar que no hay formularios de emisión para el visitante
            Assert.DoesNotContain("Emitir Comprobante NubeFact", html80);
            Assert.DoesNotContain("Emitir Comprobante NubeFact", html58);
        }

        // =========================================================================
        // RECHAZO DE TOKEN DE OTRA RESERVA
        // =========================================================================

        [Fact]
        public async Task Ticket_TokenFromOtherBooking_IsRejectedWithUnauthorized()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var bookingA = SeedBooking(db);
            var bookingB = SeedBooking(db);

            var config = CreateConfig();
            var checkoutAccess = new CheckoutAccess(config);
            var tokenForB = checkoutAccess.Create(bookingB.Id);

            var controller = CreateController(db, user: null, checkoutAccess, config);
            var result = await controller.Ticket(bookingA.Id, token: tokenForB);

            Assert.IsType<UnauthorizedObjectResult>(result);
        }

        // =========================================================================
        // USUARIO SIN PERMISO DE EMISIÓN: NO APARECEN FORMULARIOS DE EMISIÓN
        // =========================================================================

        [Fact]
        public async Task Ticket_UserWithoutManagePermission_DoesNotRenderEmissionButtons()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var unbilled = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(unbilled);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var sp = CreateRazorServiceProvider(config);
            var checkoutAccess = new CheckoutAccess(config);

            // Usuario con permiso de solo lectura (ReceiptsView) pero sin ReceiptsManage
            var readOnlyUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("Permission", AppPermissions.ReceiptsView),
                new Claim("Permission", AppPermissions.BookingsView),
                new Claim(ClaimTypes.Name, "ReadOnlyStaff")
            }, "TestAuth"));

            var controller = CreateController(db, readOnlyUser, checkoutAccess, config, sp);
            var result = await controller.Ticket(booking.Id, size: "80", cashTransactionId: unbilled.Id);
            var viewResult = Assert.IsType<ViewResult>(result);

            var html = await RenderTicketViewAsync(viewResult, controller, sp);

            Assert.Contains("*** VOUCHER PROVISIONAL ***", html);
            Assert.DoesNotContain("<form asp-controller=\"Booking\" asp-action=\"RetryReceipt\"", html);
            Assert.DoesNotContain("Emitir Comprobante NubeFact", html);
        }

        // =========================================================================
        // CERO EMISIONES Y CERO ESCRITURAS FINANCIERAS DURANTE LOS GET
        // =========================================================================

        [Fact]
        public async Task Ticket_GetNavigation_PerformsZeroExternalEmissionsAndZeroDbWrites()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var receipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00000001",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var sp = CreateRazorServiceProvider(config);
            var checkoutAccess = new CheckoutAccess(config);
            var staff = CreateStaffUser();

            int initialReceiptsCount = await db.ElectronicReceipts.CountAsync();
            int initialCashCount = await db.CashTransactions.CountAsync();

            var controller = CreateController(db, staff, checkoutAccess, config, sp);
            var r1 = await controller.Ticket(receipt.Id, size: "80");
            var r2 = await controller.Ticket(receipt.Id, size: "58");
            var r3 = await controller.Ticket(booking.Id, size: "80", receiptId: receipt.Id);

            int finalReceiptsCount = await db.ElectronicReceipts.CountAsync();
            int finalCashCount = await db.CashTransactions.CountAsync();

            Assert.Equal(initialReceiptsCount, finalReceiptsCount);
            Assert.Equal(initialCashCount, finalCashCount);
        }
    }
}
