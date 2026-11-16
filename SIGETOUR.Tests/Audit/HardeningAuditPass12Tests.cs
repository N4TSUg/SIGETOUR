using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    public class HardeningAuditPass12Tests
    {
        private class InspectableHttpMessageHandler : HttpMessageHandler
        {
            public List<HttpRequestMessage> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":\"00012999\",\"enlace_del_pdf\":\"https://test.pdf\"}")
                });
            }
        }

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
                { "Payments:CheckoutSigningKey", "super_secret_test_key_phase_12_1234567890" },
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
                Title = "Tour Otuzco Pass 12",
                Slug = "otuzco-p12-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = total,
                IsActive = true
            };
            db.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "TC-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                Status = BookingStatus.Confirmed,
                TotalAmount = total,
                PaidAmount = paid,
                CustomerName = "Juan Pérez",
                CustomerDni = "45678912",
                TravelDate = DateTime.UtcNow.Date.AddDays(3),
                ShiftName = "Mañana",
                TotalPassengers = 1,
                InvoiceType = "boleta",
                PaymentMethod = "Culqi"
            };

            var item = new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                TourPackage = tour,
                Passengers = 1,
                UnitPrice = total,
                SubTotal = total
            };

            booking.Items.Add(item);
            db.Bookings.Add(booking);
            db.SaveChanges();
            return booking;
        }

        private static BookingController CreateController(
            AppDbContext db,
            ClaimsPrincipal? user = null,
            CheckoutAccess? checkoutAccess = null,
            IConfiguration? config = null,
            IServiceProvider? sp = null)
        {
            var cfg = config ?? CreateConfig();
            var chk = checkoutAccess ?? new CheckoutAccess(cfg);
            var controller = new BookingController(db, chk, cfg);

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

        private static ClaimsPrincipal CreateStaffUser()
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, "AdminUser"),
                new Claim(ClaimTypes.Role, "SuperAdmin"),
                new Claim("Permission", AppPermissions.ReceiptsManage),
                new Claim("Permission", AppPermissions.ReceiptsView),
                new Claim("Permission", AppPermissions.BookingsManage),
                new Claim("Permission", AppPermissions.BookingsView)
            };
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
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

        // =========================================================================
        // 1. CASO PRINCIPAL DE AUDITORÍA: BYPASS EN RETRY POR RECEIPTID (409 Y CERO PETICIONES)
        // =========================================================================

        [Fact]
        public async Task RetryReceipt_ByReceiptId_WhenOperationHasConflictingDocument_Returns409AndMakesZeroCalls()
        {
            // Reproducción exacta del hallazgo prioritario:
            // 1. Un pago Culqi confirmado tiene su movimiento de caja vinculado.
            // 2. Doc A: asociado al pago Culqi, rechazado, sin número.
            // 3. Doc B: asociado por CASH:{cashId}, ya emitido (Demo/Accepted).
            // 4. RetryReceipt solicitado con receiptId de Doc A debe devolver 409 Conflict y 0 llamadas al proveedor.
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

            var receiptA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "",
                TotalAmount = 100m,
                Status = "Rejected",
                LastError = "Error de validación tributaria simulado",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-8)
            };

            var receiptB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CashTransactionId = cash.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00012001",
                TotalAmount = 100m,
                Status = "Demo",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };

            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(receiptA, receiptB);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(
                db,
                config,
                httpClient,
                NullLogger<NubefactInvoiceService>.Instance);

            var controller = CreateController(db, CreateStaffUser(), config: config);

            // POST de reintento apuntando al receiptId del Documento A
            var actionResult = await controller.RetryReceipt(
                receiptId: receiptA.Id,
                bookingId: booking.Id,
                paymentId: culqi.Id,
                receiptService: service,
                cashTransactionId: cash.Id);

            var statusResult = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal((int)HttpStatusCode.Conflict, statusResult.StatusCode);

            // Cero peticiones HTTP externas al proveedor NubeFact
            Assert.Empty(handler.Requests);

            // Comprobar que el Documento A NO fue modificado ni promovido a Demo/Accepted
            var freshDocA = await db.ElectronicReceipts.FindAsync(receiptA.Id);
            Assert.NotNull(freshDocA);
            Assert.Equal("Rejected", freshDocA.Status);
            Assert.True(string.IsNullOrEmpty(freshDocA.Number));
            Assert.Null(freshDocA.EmissionLeaseToken);

            // Comprobar que el Documento B permanece intacto
            var freshDocB = await db.ElectronicReceipts.FindAsync(receiptB.Id);
            Assert.NotNull(freshDocB);
            Assert.Equal("Demo", freshDocB.Status);
            Assert.Equal("00012001", freshDocB.Number);
        }

        // =========================================================================
        // 2. DEFENSA DIRECTA A NIVEL DE SERVICIO (RETRYRECEIPTASYNC Y GENERATERECEIPTASYNC)
        // =========================================================================

        [Fact]
        public async Task RetryReceiptAsync_DirectCall_WhenOperationHasConflictingDocument_ThrowsAmbiguousReceiptConflictException()
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

            var docA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Status = "Rejected",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            var docB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Number = "00012002",
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            };

            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(docA, docB);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

            var ex = await Assert.ThrowsAsync<AmbiguousReceiptConflictException>(() =>
                service.RetryReceiptAsync(docA.Id));

            Assert.Equal(booking.Id, ex.BookingId);
            Assert.Contains(docA.Id, ex.CandidateReceiptIds);
            Assert.Contains(docB.Id, ex.CandidateReceiptIds);
            Assert.Empty(handler.Requests);

            var reloaded = await db.ElectronicReceipts.FindAsync(docA.Id);
            Assert.NotNull(reloaded);
            Assert.Equal("Rejected", reloaded.Status);
            Assert.Null(reloaded.EmissionLeaseToken);
        }

        [Fact]
        public async Task GenerateReceiptAsync_WithExactReceiptId_WhenOperationHasConflictingDocument_ThrowsAmbiguousReceiptConflictException()
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

            var docA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Status = "Pending",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            var docB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CashTransactionId = cash.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Number = "00012003",
                Status = "Demo",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            };

            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(docA, docB);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

            var ex = await Assert.ThrowsAsync<AmbiguousReceiptConflictException>(() =>
                service.GenerateReceiptAsync(
                    booking.Id,
                    culqiPaymentId: culqi.Id,
                    exactReceiptId: docA.Id,
                    claimToken: null,
                    cashTransactionId: cash.Id,
                    billableOperationKey: docA.BillableOperationKey));

            Assert.Equal(booking.Id, ex.BookingId);
            Assert.Contains(docA.Id, ex.CandidateReceiptIds);
            Assert.Contains(docB.Id, ex.CandidateReceiptIds);
            Assert.Empty(handler.Requests);

            var reloaded = await db.ElectronicReceipts.FindAsync(docA.Id);
            Assert.NotNull(reloaded);
            Assert.Equal("Pending", reloaded.Status);
            Assert.Null(reloaded.EmissionLeaseToken);
        }

        // =========================================================================
        // 3. VARIANTES DE ESTADO Y ASOCIACIONES CRUZADAS
        // =========================================================================

        [Theory]
        [InlineData("Pending")]
        [InlineData("Rejected")]
        [InlineData("Failed")]
        public async Task RetryReceipt_VariousNonTerminalStatuses_AllBlockedByConflict(string status)
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

            var docA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Status = status,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            var docB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Number = "00012010",
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            };

            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(docA, docB);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);
            var controller = CreateController(db, CreateStaffUser(), config: config);

            var actionResult = await controller.RetryReceipt(
                receiptId: docA.Id,
                bookingId: booking.Id,
                paymentId: culqi.Id,
                receiptService: service,
                cashTransactionId: cash.Id);

            var statusResult = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal((int)HttpStatusCode.Conflict, statusResult.StatusCode);
            Assert.Empty(handler.Requests);

            var reloaded = await db.ElectronicReceipts.FindAsync(docA.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(status, reloaded.Status);
        }

        [Fact]
        public async Task RetryReceipt_CrossKeyAssociation_OnlyKeysPresent_BlocksConflict()
        {
            // Escenario: Los documentos NO tienen FKs asignadas, solo BillableOperationKey
            // Doc A: CULQI:{id} (Rejected)
            // Doc B: CASH:{id} (Accepted)
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

            var docA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                CashTransactionId = null,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Status = "Rejected",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            var docB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                CashTransactionId = null,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Number = "00012020",
                Status = "Demo",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            };

            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(docA, docB);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);
            var controller = CreateController(db, CreateStaffUser(), config: config);

            var actionResult = await controller.RetryReceipt(
                receiptId: docA.Id,
                bookingId: booking.Id,
                paymentId: null,
                receiptService: service,
                cashTransactionId: null);

            var statusResult = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal((int)HttpStatusCode.Conflict, statusResult.StatusCode);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task RetryReceipt_SingleReceiptMatchingMultipleFields_NoFalseConflict_Proceeds()
        {
            // Un comprobante único que coincide por varios campos no debe disparar falso conflicto
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

            var singleDoc = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                CashTransactionId = cash.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Status = "Rejected",
                CreatedAtUtc = DateTime.UtcNow
            };

            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.Add(singleDoc);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);
            var controller = CreateController(db, CreateStaffUser(), config: config);

            var actionResult = await controller.RetryReceipt(
                receiptId: singleDoc.Id,
                bookingId: booking.Id,
                paymentId: culqi.Id,
                receiptService: service,
                cashTransactionId: cash.Id);

            // Debe proceder al reintento (redirección a Ticket), sin 409
            var redirectResult = Assert.IsType<RedirectToActionResult>(actionResult);
            Assert.Equal("Ticket", redirectResult.ActionName);
            // El handler recibió la petición de emisión de reintento
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task RetryReceipt_DocumentFromAnotherBooking_DoesNotCauseConflict()
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
            var docA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = bookingA.Id,
                CulqiPaymentId = culqiA.Id,
                BillableOperationKey = $"CULQI:{culqiA.Id:N}",
                ReceiptType = "Boleta",
                Status = "Rejected",
                CreatedAtUtc = DateTime.UtcNow
            };

            var docOtherBooking = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = bookingB.Id,
                ReceiptType = "Boleta",
                Number = "00012099",
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };

            db.CulqiPayments.Add(culqiA);
            db.ElectronicReceipts.AddRange(docA, docOtherBooking);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);
            var controller = CreateController(db, CreateStaffUser(), config: config);

            var actionResult = await controller.RetryReceipt(
                receiptId: docA.Id,
                bookingId: bookingA.Id,
                paymentId: culqiA.Id,
                receiptService: service);

            var redirectResult = Assert.IsType<RedirectToActionResult>(actionResult);
            Assert.Equal("Ticket", redirectResult.ActionName);
            Assert.Single(handler.Requests);
        }

        // =========================================================================
        // 4. PROCESAMIENTO EN SEGUNDO PLANO: PROCESSPENDINGRECEIPTSASYNC
        // =========================================================================

        [Fact]
        public async Task ProcessPendingReceiptsAsync_SkipsConflictedReceipts_DoesNotAcquireLeaseAndContinuesHealthy()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);

            // Reserva 1: En conflicto (Doc A pendiente, Doc B ya emitido)
            var booking1 = SeedBooking(db, 100m, 100m);
            var culqi1 = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking1.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30)
            };
            var cash1 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking1.Id,
                CulqiPaymentId = culqi1.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow.AddMinutes(-30)
            };
            var conflictedPending = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking1.Id,
                CulqiPaymentId = culqi1.Id,
                BillableOperationKey = $"CULQI:{culqi1.Id:N}",
                ReceiptType = "Boleta",
                Status = "Pending",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20)
            };
            var alreadyIssued = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking1.Id,
                CashTransactionId = cash1.Id,
                BillableOperationKey = $"CASH:{cash1.Id:N}",
                ReceiptType = "Boleta",
                Number = "00012050",
                Status = "Demo",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
            };

            // Reserva 2: Comprobante pendiente sano sin conflicto
            var booking2 = SeedBooking(db, 80m, 80m);
            var culqi2 = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking2.Id,
                AmountCents = 8000,
                Status = "paid",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30)
            };
            var healthyPending = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking2.Id,
                CulqiPaymentId = culqi2.Id,
                BillableOperationKey = $"CULQI:{culqi2.Id:N}",
                ReceiptType = "Boleta",
                Status = "Pending",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20)
            };

            db.CulqiPayments.AddRange(culqi1, culqi2);
            db.CashTransactions.Add(cash1);
            db.ElectronicReceipts.AddRange(conflictedPending, alreadyIssued, healthyPending);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

            int processed = await service.ProcessPendingReceiptsAsync();

            // Solo 1 comprobante debió ser procesado (el sano de booking2)
            Assert.Equal(1, processed);

            // El comprobante en conflicto NO fue modificado ni adquirió lease
            var freshConflicted = await db.ElectronicReceipts.FindAsync(conflictedPending.Id);
            Assert.NotNull(freshConflicted);
            Assert.Equal("Pending", freshConflicted.Status);
            Assert.Null(freshConflicted.EmissionLeaseToken);
            Assert.True(string.IsNullOrEmpty(freshConflicted.Number));

            // El comprobante sano de booking2 fue emitido exitosamente
            var freshHealthy = await db.ElectronicReceipts.FindAsync(healthyPending.Id);
            Assert.NotNull(freshHealthy);
            Assert.Equal("Demo", freshHealthy.Status);
            Assert.Equal("00012999", freshHealthy.Number);

            // Solo 1 petición HTTP fue realizada al proveedor
            Assert.Single(handler.Requests);
        }

        // =========================================================================
        // 5. CONSERVAR CONSULTA, REIMPRESIÓN Y SUPRESIÓN DEL BOTÓN EN TICKET.CSHTML
        // =========================================================================

        [Fact]
        public async Task Ticket_ViewingRejectedReceiptWithConflict_Returns200_SuppressesRetryButton_AndPreservesSizeChange()
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

            var receiptA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "",
                TotalAmount = 100m,
                Status = "Rejected",
                LastError = "Error de validación RUC",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-8)
            };

            var receiptB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CashTransactionId = cash.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00012080",
                TotalAmount = 100m,
                Status = "Demo",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };

            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(receiptA, receiptB);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var sp = CreateRazorServiceProvider(config);
            var chk = new CheckoutAccess(config);
            var staff = CreateStaffUser();

            // 1. Abrir directamente el Documento A (Rejected) por su receiptId
            var controller80 = CreateController(db, staff, chk, config, sp);
            var result80 = await controller80.Ticket(receiptA.Id, size: "80");
            var vr80 = Assert.IsType<ViewResult>(result80);

            Assert.Equal(200, controller80.Response.StatusCode);
            Assert.True(controller80.ViewBag.DocumentHasConflict == true);

            var html80 = await RenderTicketViewAsync(vr80, controller80, sp);

            // Debe mostrarse como comprobante rechazado
            Assert.Contains("*** COMPROBANTE RECHAZADO ***", html80);
            // Debe mostrar el aviso de reintento bloqueado
            Assert.Contains("Reintento bloqueado", html80);
            Assert.Contains("La operación asociada a este comprobante presenta conflicto de identidad", html80);
            // NO debe contener el botón de reintento
            Assert.DoesNotContain("↻ Reintentar Emisión NubeFact", html80);

            // 2. Cambiar a 58 mm
            var url58 = ExtractHrefBySize(html80, "58");
            var (result58, controller58) = await FollowUrlAsync(db, url58, staff, chk, config, sp);
            var vr58 = Assert.IsType<ViewResult>(result58);

            Assert.Equal(200, controller58.Response.StatusCode);
            Assert.True(controller58.ViewBag.DocumentHasConflict == true);
            var html58 = await RenderTicketViewAsync(vr58, controller58, sp);

            Assert.Contains("*** COMPROBANTE RECHAZADO ***", html58);
            Assert.Contains("Reintento bloqueado", html58);
            Assert.DoesNotContain("↻ Reintentar Emisión NubeFact", html58);

            // 3. Regresar a 80 mm
            var url80Back = ExtractHrefBySize(html58, "80");
            var (result80Back, controller80Back) = await FollowUrlAsync(db, url80Back, staff, chk, config, sp);
            var vr80Back = Assert.IsType<ViewResult>(result80Back);

            Assert.Equal(200, controller80Back.Response.StatusCode);
            Assert.True(controller80Back.ViewBag.DocumentHasConflict == true);
            var html80Back = await RenderTicketViewAsync(vr80Back, controller80Back, sp);

            Assert.Contains("*** COMPROBANTE RECHAZADO ***", html80Back);
            Assert.Contains("Reintento bloqueado", html80Back);
            Assert.DoesNotContain("↻ Reintentar Emisión NubeFact", html80Back);
        }

        [Fact]
        public async Task Ticket_ViewingIssuedReceipt_Returns200_ReprintWorksAcrossSizes()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 100m, 100m);

            var receiptIssued = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00012090",
                TotalAmount = 100m,
                Status = "Demo",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ElectronicReceipts.Add(receiptIssued);
            await db.SaveChangesAsync();

            var config = CreateConfig();
            var sp = CreateRazorServiceProvider(config);
            var chk = new CheckoutAccess(config);
            var staff = CreateStaffUser();

            var controller = CreateController(db, staff, chk, config, sp);
            var result = await controller.Ticket(receiptIssued.Id, size: "80");
            var vr = Assert.IsType<ViewResult>(result);

            Assert.Equal(200, controller.Response.StatusCode);
            var html80 = await RenderTicketViewAsync(vr, controller, sp);

            Assert.Contains("BBB1-00012090", html80);
            Assert.Contains("*** COMPROBANTE DEMO NUBEFACT ***", html80);

            var url58 = ExtractHrefBySize(html80, "58");
            var (result58, controller58) = await FollowUrlAsync(db, url58, staff, chk, config, sp);
            var vr58 = Assert.IsType<ViewResult>(result58);
            var html58 = await RenderTicketViewAsync(vr58, controller58, sp);

            Assert.Contains("BBB1-00012090", html58);
        }

        [Fact]
        public async Task Ticket_Historical40AndNewCash60_IndependentOperations_NoConflictOnRetry()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, 100m, 100m);

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

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);
            var controller = CreateController(db, CreateStaffUser(), config: config);

            // Emitir comprobante para el cobro manual de S/60
            var actionResult = await controller.RetryReceipt(
                receiptId: null,
                bookingId: booking.Id,
                paymentId: null,
                receiptService: service,
                cashTransactionId: newCash.Id);

            var redirectResult = Assert.IsType<RedirectToActionResult>(actionResult);
            Assert.Equal("Ticket", redirectResult.ActionName);
            Assert.Single(handler.Requests);

            // Se crearon 2 comprobantes en total (histórico de S/40 y nuevo de S/60)
            Assert.Equal(2, await db.ElectronicReceipts.CountAsync());
        }
    }
}
