using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Core.Security;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;
using Xunit;

namespace SIGETOUR.Tests.Audit
{
    public class HardeningAuditPass9Tests
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
                { "Payments:CheckoutSigningKey", "super_secret_test_key_phase_9_1234567890" },
                { "Nubefact:Mode", "Demo" },
                { "Nubefact:Ruta", "https://demo.nubefact.com/api/v1/test" },
                { "Nubefact:Token", "demo_token_123" },
                { "Nubefact:BoletaSeries", "BBB1" },
                { "Nubefact:FacturaSeries", "FFF1" },
                { "Company:Ruc", "20601234567" }
            };
            return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        }

        private static Booking SeedBooking(AppDbContext db, decimal total = 100m, decimal paid = 100m)
        {
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Otuzco Pass 9",
                Slug = "otuzco-p9-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = total,
                IsActive = true
            };
            db.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                CustomerName = "Pedro Pasajero",
                CustomerDni = "71234567",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                ShiftName = "Mañana",
                TotalPassengers = 1,
                TotalAmount = total,
                PaidAmount = paid,
                InvoiceType = "boleta",
                PaymentMethod = "Efectivo",
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

        private static (BookingController controller, IUrlHelper urlHelper) CreateControllerWithUser(
            AppDbContext context,
            string? permission = AppPermissions.ReceiptsManage,
            CheckoutAccess? checkoutAccess = null,
            IConfiguration? config = null)
        {
            var cfg = config ?? CreateConfig();
            var chk = checkoutAccess ?? new CheckoutAccess(cfg);
            var controller = new BookingController(context, chk, cfg);
            var httpContext = new DefaultHttpContext();

            if (!string.IsNullOrEmpty(permission))
            {
                var claims = new List<Claim>
                {
                    new Claim("Permission", permission),
                    new Claim(ClaimTypes.Name, "AdminUser")
                };
                if (permission == AppPermissions.ReceiptsManage)
                {
                    claims.Add(new Claim("Permission", AppPermissions.ReceiptsView));
                    claims.Add(new Claim("Permission", AppPermissions.BookingsView));
                }
                var identity = new ClaimsIdentity(claims, "TestAuth");
                httpContext.User = new ClaimsPrincipal(identity);
            }
            else
            {
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            }

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };
            controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());

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

            controller.Url = urlMock.Object;
            return (controller, urlMock.Object);
        }

        // =========================================================================
        // REQUISITO 1: CONSERVAR LA OPERACIÓN AL CAMBIAR EL ANCHO DEL TICKET
        // =========================================================================

        [Fact]
        public async Task Ticket_ChangeWidth_PreservesSelectedOperationAndAmount_80to58to80()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            // Boleta histórica de S/40
            var historicalReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "40",
                TotalAmount = 40m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-2)
            };
            db.ElectronicReceipts.Add(historicalReceipt);

            // Nuevo cobro de S/60 sin comprobante
            var newCashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Saldo",
                Amount = 60m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(newCashTx);
            await db.SaveChangesAsync();

            var (controller, urlHelper) = CreateControllerWithUser(db);

            // 1. Abrir ticket para el nuevo cobro de S/60 en 80mm
            var result80 = await controller.Ticket(booking.Id, size: "80", cashTransactionId: newCashTx.Id);
            var viewResult80 = Assert.IsType<ViewResult>(result80);
            Assert.Null(viewResult80.Model); // Estado provisional
            Assert.Equal(60m, viewResult80.ViewData["SelectedOperationAmount"]);

            // Obtener contexto de navegación
            var nav = viewResult80.ViewData["NavigationContext"] as TicketNavigationContext;
            Assert.NotNull(nav);

            // 2. Generar URL para cambiar a 58mm y verificar que conserva cashTransactionId
            var url58 = nav.GetUrlForSize(urlHelper, "58");
            Assert.Contains("size=58", url58);
            Assert.Contains($"cashTransactionId={newCashTx.Id}", url58);
            Assert.DoesNotContain("size=80", url58);

            // 3. Simular navegación hacia el enlace real de 58mm
            var result58 = await controller.Ticket(booking.Id, size: "58", cashTransactionId: newCashTx.Id);
            var viewResult58 = Assert.IsType<ViewResult>(result58);
            Assert.Null(viewResult58.Model); // Debe seguir siendo el ticket provisional de S/60, no la boleta histórica de S/40
            Assert.Equal(60m, viewResult58.ViewData["SelectedOperationAmount"]);
            Assert.Equal("58", viewResult58.ViewData["TicketSize"]);

            // 4. Generar URL para regresar a 80mm desde 58mm
            var nav58 = viewResult58.ViewData["NavigationContext"] as TicketNavigationContext;
            Assert.NotNull(nav58);
            var url80Back = nav58.GetUrlForSize(urlHelper, "80");
            Assert.Contains("size=80", url80Back);
            Assert.Contains($"cashTransactionId={newCashTx.Id}", url80Back);

            // 5. Navegar de vuelta a 80mm
            var result80Back = await controller.Ticket(booking.Id, size: "80", cashTransactionId: newCashTx.Id);
            var viewResult80Back = Assert.IsType<ViewResult>(result80Back);
            Assert.Null(viewResult80Back.Model);
            Assert.Equal(60m, viewResult80Back.ViewData["SelectedOperationAmount"]);
        }

        [Fact]
        public async Task Ticket_ChangeWidth_WhenAutomaticallySelectedSingleOperation_PreservesOperationInNextRequest()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 60m, paid: 60m);

            // Una única operación de caja pendiente (S/60)
            var singleCashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Category = "Cobro Completo",
                Amount = 60m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(singleCashTx);
            await db.SaveChangesAsync();

            var (controller, urlHelper) = CreateControllerWithUser(db);

            // Acceso sin especificar cashTransactionId en query
            var result = await controller.Ticket(booking.Id, size: "80", cashTransactionId: null);
            var viewResult = Assert.IsType<ViewResult>(result);

            // Debe haberse seleccionado automáticamente
            Assert.Equal(singleCashTx.Id, viewResult.ViewData["SelectedCashTransactionId"]);

            var nav = viewResult.ViewData["NavigationContext"] as TicketNavigationContext;
            Assert.NotNull(nav);
            Assert.Equal(singleCashTx.Id, nav.CashTransactionId);

            // El enlace de cambio a 58mm debe preservar el ID seleccionado automáticamente
            var url58 = nav.GetUrlForSize(urlHelper, "58");
            Assert.Contains($"cashTransactionId={singleCashTx.Id}", url58);
        }

        // =========================================================================
        // REQUISITO 2: CONSERVAR EL ACCESO AUTORIZADO EN LOS ENLACES INTERNOS
        // =========================================================================

        [Fact]
        public async Task Ticket_TokenVisitor_WithoutStaffSession_PreservesTokenInInternalLinks()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            var config = CreateConfig();
            var checkoutAccess = new CheckoutAccess(config);
            var validToken = checkoutAccess.Create(booking.Id);

            // Dos operaciones para forzar la aparición del selector
            var tx1 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Category = "Abono 1",
                Amount = 50m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10)
            };
            var tx2 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Category = "Abono 2",
                Amount = 50m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.AddRange(tx1, tx2);
            await db.SaveChangesAsync();

            // Usuario anónimo (sin sesión staff, sin cookies)
            var (controller, urlHelper) = CreateControllerWithUser(db, permission: null, checkoutAccess: checkoutAccess, config: config);

            // 1. Abrir selector con token válido
            var result = await controller.Ticket(booking.Id, size: "80", token: validToken);
            var viewResult = Assert.IsType<ViewResult>(result);
            var ops = Assert.IsType<List<BookingOperationOptionViewModel>>(viewResult.ViewData["AvailableOperations"]);
            Assert.Equal(2, ops.Count);

            var nav = viewResult.ViewData["NavigationContext"] as TicketNavigationContext;
            Assert.NotNull(nav);
            Assert.True(nav.IsTokenAuthorized);

            // 2. Comprobar que el enlace "Volver al Boleto" incluye el token
            var voucherUrl = nav.GetUrlForVoucher(urlHelper);
            Assert.Contains($"token={validToken}", voucherUrl);

            // 3. Comprobar que los enlaces de cambio de ancho incluyen el token
            var size58Url = nav.GetUrlForSize(urlHelper, "58");
            Assert.Contains($"token={validToken}", size58Url);

            // 4. Comprobar que cada enlace de operación ("Ver Ticket") incluye el token
            foreach (var op in ops)
            {
                var opUrl = nav.GetUrlForOperation(urlHelper, op);
                Assert.Contains($"token={validToken}", opUrl);
                Assert.Contains($"cashTransactionId={op.CashTransactionId}", opUrl);

                // 5. Simular la navegación real del visitante al enlace generado
                var opResult = await controller.Ticket(booking.Id, size: "80", cashTransactionId: op.CashTransactionId, token: validToken);
                var opViewResult = Assert.IsType<ViewResult>(opResult);
                Assert.Equal(50m, opViewResult.ViewData["SelectedOperationAmount"]);
            }
        }

        [Fact]
        public async Task Ticket_TokenVisitor_InvalidOrDifferentBookingToken_ReturnsUnauthorized()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var bookingA = SeedBooking(db, total: 100m, paid: 100m);
            var bookingB = SeedBooking(db, total: 100m, paid: 100m);

            var config = CreateConfig();
            var checkoutAccess = new CheckoutAccess(config);

            // Token firmado para bookingB
            var tokenForB = checkoutAccess.Create(bookingB.Id);

            var (controller, _) = CreateControllerWithUser(db, permission: null, checkoutAccess: checkoutAccess, config: config);

            // Intentar abrir bookingA con token de bookingB -> debe rechazar
            var result = await controller.Ticket(bookingA.Id, token: tokenForB);
            Assert.IsType<UnauthorizedObjectResult>(result);

            // Token ausente -> rechazar
            var resultNoToken = await controller.Ticket(bookingA.Id, token: null);
            Assert.IsType<UnauthorizedObjectResult>(resultNoToken);

            // Token inválido/corrupto -> rechazar
            var resultBadToken = await controller.Ticket(bookingA.Id, token: "token_invalido_corrupto_123");
            Assert.IsType<UnauthorizedObjectResult>(resultBadToken);
        }

        // =========================================================================
        // REQUISITO 3: MOSTRAR UNA SOLA OPERACIÓN POR PAGO CULQI
        // =========================================================================

        [Fact]
        public async Task Ticket_NormalizedOperations_SingleCulqiPaymentWithCashTx_ShowsSingleOperationDirectly()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            // 1 pago Culqi pagado de S/100
            var culqiPayment = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                ProviderId = "chr_test_100",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqiPayment);

            // Movimiento de caja automático asociado a ese mismo CulqiPaymentId
            var cashTx = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqiPayment.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 100m,
                PaymentMethod = "Tarjeta Web",
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.Add(cashTx);
            await db.SaveChangesAsync();

            var (controller, _) = CreateControllerWithUser(db);

            // Act: Abrir ticket por bookingId sin seleccionar operación
            var result = await controller.Ticket(booking.Id);
            var viewResult = Assert.IsType<ViewResult>(result);

            // Requisito 3: No debe mostrar el selector porque es un único cobro canónico
            Assert.Null(viewResult.ViewData["AvailableOperations"]);
            Assert.Equal(100m, viewResult.ViewData["SelectedOperationAmount"]);
            Assert.Equal(culqiPayment.Id, viewResult.ViewData["PaymentId"]);
            Assert.Equal("Culqi", viewResult.ViewData["SelectedPaymentMethod"]);
        }

        [Fact]
        public async Task Ticket_NormalizedOperations_TwoDifferentCulqiPayments_ShowsExactlyTwoOptions()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            // Pago Culqi 1: S/50
            var culqi1 = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 5000,
                Status = "paid",
                ProviderId = "chr_culqi_1",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            var cash1 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi1.Id,
                TransactionType = "Ingreso",
                Amount = 50m,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5)
            };

            // Pago Culqi 2: S/50 (mismo monto, diferente cobro)
            var culqi2 = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 5000,
                Status = "paid",
                ProviderId = "chr_culqi_2",
                CreatedAtUtc = DateTime.UtcNow
            };
            var cash2 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi2.Id,
                TransactionType = "Ingreso",
                Amount = 50m,
                CreatedAt = DateTime.UtcNow
            };

            db.CulqiPayments.AddRange(culqi1, culqi2);
            db.CashTransactions.AddRange(cash1, cash2);
            await db.SaveChangesAsync();

            var (controller, _) = CreateControllerWithUser(db);

            var result = await controller.Ticket(booking.Id);
            var viewResult = Assert.IsType<ViewResult>(result);

            // Requisito 3: No deduplicar por importe. Deben ser exactamente 2 opciones (no 4)
            var ops = Assert.IsType<List<BookingOperationOptionViewModel>>(viewResult.ViewData["AvailableOperations"]);
            Assert.Equal(2, ops.Count);
            Assert.Contains(ops, o => o.CulqiPaymentId == culqi1.Id);
            Assert.Contains(ops, o => o.CulqiPaymentId == culqi2.Id);
        }

        [Fact]
        public async Task Ticket_NormalizedOperations_OneCulqiPlusOneIndependentManualCash_ShowsExactlyTwoOptions()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            // 1 Culqi de S/40
            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 4000,
                Status = "paid",
                ProviderId = "chr_culqi_40",
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1)
            };
            var culqiCash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Amount = 40m,
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            };

            // 1 Cobro manual independiente en efectivo de S/60 (CulqiPaymentId == null)
            var manualCash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                TransactionType = "Ingreso",
                Category = "Cobro en Mostrador",
                Amount = 60m,
                PaymentMethod = "Efectivo",
                CreatedAt = DateTime.UtcNow
            };

            db.CulqiPayments.Add(culqi);
            db.CashTransactions.AddRange(culqiCash, manualCash);
            await db.SaveChangesAsync();

            var (controller, _) = CreateControllerWithUser(db);

            var result = await controller.Ticket(booking.Id);
            var viewResult = Assert.IsType<ViewResult>(result);

            // Deben ser exactamente 2 operaciones: Culqi (S/40) y Manual (S/60)
            var ops = Assert.IsType<List<BookingOperationOptionViewModel>>(viewResult.ViewData["AvailableOperations"]);
            Assert.Equal(2, ops.Count);
            Assert.Contains(ops, o => o.CulqiPaymentId == culqi.Id && o.Amount == 40m);
            Assert.Contains(ops, o => o.CashTransactionId == manualCash.Id && o.Amount == 60m);
        }

        [Fact]
        public async Task Ticket_NormalizedOperations_BilledCulqiPayment_ReusesReceiptWithoutDuplicatePending()
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
                ProviderId = "chr_culqi_billed",
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
                CulqiPaymentId = culqi.Id,
                CashTransactionId = cash.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "500",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };

            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var (controller, _) = CreateControllerWithUser(db);

            var result = await controller.Ticket(booking.Id);
            var viewResult = Assert.IsType<ViewResult>(result);

            // El comprobante ya emitido se reutiliza directamente, sin entrada pendiente duplicada
            Assert.NotNull(viewResult.Model);
            var model = Assert.IsType<ElectronicReceipt>(viewResult.Model);
            Assert.Equal(receipt.Id, model.Id);
            Assert.Null(viewResult.ViewData["AvailableOperations"]);
        }

        [Fact]
        public async Task Ticket_NormalizedOperations_HistoricalReceiptPlusNewManualCash_KeepsBothDistinct()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db, total: 100m, paid: 100m);

            // Boleta histórica antigua sin CulqiPaymentId ni CashTransactionId
            var historicalReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "12",
                TotalAmount = 40m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-3)
            };

            // Nuevo cobro manual de S/60
            var manualCash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                TransactionType = "Ingreso",
                Amount = 60m,
                CreatedAt = DateTime.UtcNow
            };

            db.ElectronicReceipts.Add(historicalReceipt);
            db.CashTransactions.Add(manualCash);
            await db.SaveChangesAsync();

            var (controller, _) = CreateControllerWithUser(db);

            var result = await controller.Ticket(booking.Id);
            var viewResult = Assert.IsType<ViewResult>(result);

            // Requisito 3: Conservar ambos sin confundirlos en el selector
            var ops = Assert.IsType<List<BookingOperationOptionViewModel>>(viewResult.ViewData["AvailableOperations"]);
            Assert.Equal(2, ops.Count);

            var billed = Assert.Single(ops, o => o.IsBilled);
            Assert.Equal(historicalReceipt.Id, billed.ReceiptId);
            Assert.Equal(40m, billed.Amount);

            var unbilled = Assert.Single(ops, o => !o.IsBilled);
            Assert.Equal(manualCash.Id, unbilled.CashTransactionId);
            Assert.Equal(60m, unbilled.Amount);
        }

        // =========================================================================
        // REQUISITO D: PRUEBAS DE REGRESIÓN Y FLUJO COMPLETO
        // =========================================================================

        [Fact]
        public async Task Ticket_ChangeWidth_WithExistingReceipt_PreservesReceiptContext()
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
                Number = "777",
                TotalAmount = 100m,
                Status = "Accepted",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ElectronicReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var (controller, urlHelper) = CreateControllerWithUser(db);

            // 1. Abrir ticket del comprobante por su ID directo en 80mm
            var result = await controller.Ticket(receipt.Id, size: "80");
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ElectronicReceipt>(viewResult.Model);
            Assert.Equal(receipt.Id, model.Id);

            var nav = Assert.IsType<TicketNavigationContext>(viewResult.ViewData["NavigationContext"]);
            Assert.Equal(receipt.Id, nav.RouteId);
            Assert.Equal(receipt.Id, nav.ReceiptId);

            // 2. Obtener URL para 58mm
            var url58 = nav.GetUrlForSize(urlHelper, "58");
            Assert.Contains($"/{receipt.Id}", url58);
            Assert.Contains("size=58", url58);

            // 3. Navegar a 58mm y verificar que conserva el mismo comprobante
            var result58 = await controller.Ticket(receipt.Id, size: "58");
            var viewResult58 = Assert.IsType<ViewResult>(result58);
            var model58 = Assert.IsType<ElectronicReceipt>(viewResult58.Model);
            Assert.Equal(receipt.Id, model58.Id);
            Assert.Equal("58", viewResult58.ViewData["TicketSize"]);
        }

        [Fact]
        public async Task Ticket_ChangeWidth_WithSelectedCulqiPayment_PreservesPaymentContext()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 8000,
                Status = "paid",
                ProviderId = "chr_culqi_nav_80",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(culqi);
            await db.SaveChangesAsync();

            var (controller, urlHelper) = CreateControllerWithUser(db);

            var result = await controller.Ticket(booking.Id, size: "80", paymentId: culqi.Id);
            var viewResult = Assert.IsType<ViewResult>(result);

            var nav = Assert.IsType<TicketNavigationContext>(viewResult.ViewData["NavigationContext"]);
            Assert.Equal(culqi.Id, nav.PaymentId);

            var url58 = nav.GetUrlForSize(urlHelper, "58");
            Assert.Contains("size=58", url58);
            Assert.Contains($"paymentId={culqi.Id}", url58);

            // Navegar a 58mm
            var result58 = await controller.Ticket(booking.Id, size: "58", paymentId: culqi.Id);
            var viewResult58 = Assert.IsType<ViewResult>(result58);
            Assert.Equal(80m, viewResult58.ViewData["SelectedOperationAmount"]);
            Assert.Equal(culqi.Id, viewResult58.ViewData["PaymentId"]);
        }

        [Fact]
        public async Task Ticket_TokenVisitor_ChangeWidthAndReturnToVoucher_PreservesAccess()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var config = CreateConfig();
            var checkoutAccess = new CheckoutAccess(config);
            var validToken = checkoutAccess.Create(booking.Id);

            var (controller, urlHelper) = CreateControllerWithUser(db, permission: null, checkoutAccess: checkoutAccess, config: config);

            // 1. Abrir Ticket con token válido
            var result = await controller.Ticket(booking.Id, size: "80", token: validToken);
            var viewResult = Assert.IsType<ViewResult>(result);
            var nav = Assert.IsType<TicketNavigationContext>(viewResult.ViewData["NavigationContext"]);

            // 2. Comprobar URL a Voucher y seguirla
            var voucherUrl = nav.GetUrlForVoucher(urlHelper);
            Assert.Contains($"token={validToken}", voucherUrl);

            var voucherResult = await controller.Voucher(booking.Id, token: validToken);
            var voucherView = Assert.IsType<ViewResult>(voucherResult);
            var voucherBooking = Assert.IsType<Booking>(voucherView.Model);
            Assert.Equal(booking.Id, voucherBooking.Id);
        }

        [Fact]
        public void RetryReceipt_HasAuthorizeAndAntiforgeryAttributes()
        {
            var method = typeof(BookingController).GetMethod("RetryReceipt");
            Assert.NotNull(method);

            var authAttr = method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .FirstOrDefault();
            Assert.NotNull(authAttr);
            Assert.Equal(AppPermissions.ReceiptsManage, authAttr.Policy);

            var afAttr = method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute), false)
                .FirstOrDefault();
            Assert.NotNull(afAttr);
        }

        [Fact]
        public async Task RetryReceipt_AmbiguousEmission_ReturnsControlledRedirectWithTempData()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var booking = SeedBooking(db);

            var tx1 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Amount = 50m,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10)
            };
            var tx2 = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TransactionType = "Ingreso",
                Amount = 50m,
                CreatedAt = DateTime.UtcNow
            };
            db.CashTransactions.AddRange(tx1, tx2);
            await db.SaveChangesAsync();

            var mockService = new Mock<SIGETOUR.API.Application.Invoicing.IElectronicReceiptService>();
            mockService.Setup(s => s.GenerateReceiptAsync(booking.Id, null, It.IsAny<System.Threading.CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException($"La reserva {booking.ReferenceCode} tiene múltiples movimientos. Seleccione el movimiento a facturar."));

            var (controller, _) = CreateControllerWithUser(db);

            var actionResult = await controller.RetryReceipt(
                receiptId: null,
                bookingId: booking.Id,
                paymentId: null,
                receiptService: mockService.Object,
                cashTransactionId: null);

            var redirect = Assert.IsType<RedirectToActionResult>(actionResult);
            Assert.Equal("Ticket", redirect.ActionName);
            Assert.Equal(booking.Id, redirect.RouteValues?["id"]);
            Assert.Contains("múltiples movimientos", controller.TempData["ErrorMessage"]?.ToString() ?? "");
        }
    }
}
