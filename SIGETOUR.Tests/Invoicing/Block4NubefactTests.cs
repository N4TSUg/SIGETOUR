using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Controllers;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.Tests.Helpers;
using SIGETOUR.Tests.Payments;
using Xunit;

namespace SIGETOUR.Tests.Invoicing
{
    public class Block4NubefactTests
    {
        private static IConfiguration CreateConfig(string mode = "Demo", string ruta = "https://demo.nubefact.com/api/v1/test", string token = "token_b4_test")
        {
            var dict = new Dictionary<string, string?>
            {
                { "Nubefact:Mode", mode },
                { "Nubefact:Ruta", ruta },
                { "Nubefact:Token", token },
                { "Nubefact:BoletaSeries", "BBB1" },
                { "Nubefact:FacturaSeries", "FFF1" },
                { "Nubefact:DefaultIgvRate", "0.18" },
                { "Company:Ruc", "20601234567" },
                { "Company:BusinessName", "TURISMO CHILÓN S.A.C." }
            };
            return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        }

        private static Booking CreateTestBooking(AppDbContext context, decimal total = 100m, decimal paid = 100m, string invoiceType = "boleta")
        {
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Granja Porcón",
                Slug = "granja-porcon-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = total / 2m,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "B4-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                CustomerName = "Juan Pérez",
                CustomerDni = "71234567",
                CustomerEmail = "juan.perez@example.com",
                TravelDate = DateTime.UtcNow.Date.AddDays(2),
                ShiftName = "Mañana",
                TotalPassengers = 2,
                TotalAmount = total,
                PaidAmount = paid,
                InvoiceType = invoiceType,
                Status = BookingStatus.Confirmed,
                BookingDate = DateTime.UtcNow
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackageId = tour.Id,
                TourPackage = tour,
                Passengers = 2,
                UnitPrice = total / 2m,
                SubTotal = total
            });
            context.Bookings.Add(booking);
            context.SaveChanges();
            return booking;
        }

        [Fact]
        public async Task RestartAfterPaymentBeforeEmit_PersistsPendingReceiptAndRecoversIt()
        {
            using var context = TestDbContextFactory.Create();
            var config = CreateConfig();
            var booking = CreateTestBooking(context, total: 120m, paid: 120m);

            var culqiPayment = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ProviderId = "chr_test_pending_recover",
                AmountCents = 12000,
                Channel = "charge",
                Status = "paid",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
            };
            context.CulqiPayments.Add(culqiPayment);

            // Simulación de tarea persistente de emisión creada antes de la caída/reinicio
            var pendingReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqiPayment.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "Pending",
                Environment = "Demo",
                TotalAmount = 120m,
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(-30) // Más antigua que el cutoff
            };
            context.ElectronicReceipts.Add(pendingReceipt);
            await context.SaveChangesAsync();

            var fakeNubefactResponse = "{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":777,\"enlace_del_pdf\":\"https://demo.nubefact.com/pdf/BBB1-777.pdf\",\"sunat_description\":\"Comprobante emitido en DEMO\"}";
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(fakeNubefactResponse, Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(context, config, mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            // Act: Ejecutar recuperación de emisiones pendientes tras reinicio
            var processed = await service.ProcessPendingReceiptsAsync(CancellationToken.None);

            // Assert: Se procesó exactamente 1 comprobante pendiente
            Assert.Equal(1, processed);

            var recovered = await context.ElectronicReceipts.FindAsync(pendingReceipt.Id);
            Assert.NotNull(recovered);
            Assert.Equal("Demo", recovered.Status);
            Assert.Equal("777", recovered.Number);
            Assert.Equal("BBB1-777", recovered.FullNumber);
            Assert.Equal("https://demo.nubefact.com/pdf/BBB1-777.pdf", recovered.PdfUrl);

            // No se duplicaron comprobantes para la reserva
            var totalReceipts = await context.ElectronicReceipts.CountAsync(r => r.BookingId == booking.Id);
            Assert.Equal(1, totalReceipts);
        }

        [Fact]
        public async Task TimeoutAfterSendingEmission_TransitionsToRequiresReviewAndPreventsBlindReemission()
        {
            using var context = TestDbContextFactory.Create();
            var config = CreateConfig();
            var booking = CreateTestBooking(context, total: 80m, paid: 80m);

            // 1. Simulación de timeout o fallo de conexión en la emisión inicial tras iniciar el envío
            var timeoutHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                throw new HttpRequestException("The operation was canceled due to timeout.");
            }));

            var serviceTimeout = new NubefactInvoiceService(context, config, timeoutHttp, NullLogger<NubefactInvoiceService>.Instance);
            var reviewReceipt = await serviceTimeout.GenerateReceiptAsync(booking.Id, null, CancellationToken.None);

            // Assert 1: Queda en RequiresReview (resultado incierto) con detalle del fallo, no en Pending
            Assert.NotNull(reviewReceipt);
            Assert.Equal("RequiresReview", reviewReceipt.Status);
            Assert.Contains("fallo de conexión", reviewReceipt.LastError, StringComparison.OrdinalIgnoreCase);

            // 2. Intento de reintento o recuperación sin número correlativo: NO debe reemitir a ciegas hacia el proveedor
            string capturedPayload = "";
            var successHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                capturedPayload = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":888,\"enlace_del_pdf\":\"https://demo.nubefact.com/pdf/BBB1-888.pdf\",\"sunat_description\":\"Demo\"}", Encoding.UTF8, "application/json")
                };
            }));

            var serviceSuccess = new NubefactInvoiceService(context, config, successHttp, NullLogger<NubefactInvoiceService>.Instance);
            var retried = await serviceSuccess.RetryReceiptAsync(reviewReceipt.Id, CancellationToken.None);

            // Assert 2: Se retiene en RequiresReview sin reemisión automática externa (0 llamadas a NubeFact)
            Assert.NotNull(retried);
            Assert.Equal(reviewReceipt.Id, retried.Id);
            Assert.Equal("RequiresReview", retried.Status);
            Assert.True(string.IsNullOrEmpty(capturedPayload), "No debe dispararse llamada de emisión externa a ciegas para un comprobante en RequiresReview sin número correlativo.");
        }

        [Fact]
        public async Task RetryOneOfMultipleManualReceipts_OperatesStrictlyOnExactReceipt()
        {
            using var context = TestDbContextFactory.Create();
            var config = CreateConfig();
            var booking = CreateTestBooking(context, total: 100m, paid: 100m);

            // Comprobante 1: Manual previo (Adelanto), quedó en Rejected
            var receipt1 = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Status = "Rejected",
                Environment = "Demo",
                TotalAmount = 50m,
                LastError = "Error inicial",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
            };

            // Comprobante 2: Manual posterior (Saldo), ya fue Aceptado
            var receipt2 = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = null,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "555",
                Status = "Accepted",
                Environment = "Demo",
                TotalAmount = 50m,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };

            context.ElectronicReceipts.AddRange(receipt1, receipt2);
            await context.SaveChangesAsync();

            var fakeNubefactResponse = "{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":556,\"enlace_del_pdf\":\"https://demo.nubefact.com/pdf/BBB1-556.pdf\",\"aceptada_por_sunat\":true,\"sunat_description\":\"Aceptado\"}";
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(fakeNubefactResponse, Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(context, config, mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            // Act: Reintentar explícitamente el Comprobante 1
            var retried = await service.RetryReceiptAsync(receipt1.Id, CancellationToken.None);

            // Assert: Se procesó el Comprobante 1 exactamente
            Assert.Equal(receipt1.Id, retried.Id);
            Assert.Equal("Accepted", retried.Status);
            Assert.Equal("556", retried.Number);
            Assert.Equal(50m, retried.TotalAmount); // Conservó su importe de adelanto

            // Comprobante 2 permanece intacto
            var check2 = await context.ElectronicReceipts.FindAsync(receipt2.Id);
            Assert.NotNull(check2);
            Assert.Equal("555", check2.Number);
            Assert.Equal("Accepted", check2.Status);
        }

        [Fact]
        public async Task AdvanceAndBalance_SnapshotUnitPriceMatchesEffectiveBilledPrice()
        {
            using var context = TestDbContextFactory.Create();
            var config = CreateConfig();

            // Reserva de 2 pasajeros con precio unitario original de S/ 100 c/u = Total S/ 200
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Cumbemayo",
                Slug = "cumbemayo-" + Guid.NewGuid().ToString("N")[..6],
                BasePrice = 100m,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "B4-ADVANCE-SNAPSHOT",
                CustomerName = "Carlos Mendoza",
                CustomerDni = "45678901",
                TotalPassengers = 2,
                TotalAmount = 200m,
                PaidAmount = 100m, // Adelanto del 50%
                InvoiceType = "boleta",
                Status = BookingStatus.Confirmed
            };
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TourPackage = tour,
                TourPackageId = tour.Id,
                Passengers = 2,
                UnitPrice = 100m,
                SubTotal = 200m
            });
            context.Bookings.Add(booking);

            var culqiPaymentAdvance = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ProviderId = "chr_advance_test",
                AmountCents = 10000, // S/ 100
                Channel = "charge",
                Status = "paid"
            };
            context.CulqiPayments.Add(culqiPaymentAdvance);
            await context.SaveChangesAsync();

            var fakeNubefactResponse = "{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":101,\"enlace_del_pdf\":\"https://demo.nubefact.com/pdf/BBB1-101.pdf\",\"sunat_description\":\"Demo\"}";
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(fakeNubefactResponse, Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(context, config, mockHttp, NullLogger<NubefactInvoiceService>.Instance);
            var receipt = await service.GenerateReceiptAsync(booking.Id, culqiPaymentAdvance.Id, CancellationToken.None);

            // Assert: Verificar el Snapshot inmutable
            Assert.NotNull(receipt.SnapshotJson);
            using var doc = JsonDocument.Parse(receipt.SnapshotJson);
            var items = doc.RootElement.GetProperty("Items");
            Assert.Single(items.EnumerateArray());

            var firstItem = items[0];
            var qty = firstItem.GetProperty("Quantity").GetInt32();
            var unitPrice = firstItem.GetProperty("UnitPrice").GetDecimal();
            var itemTotal = firstItem.GetProperty("Total").GetDecimal();
            var fullPkgPrice = firstItem.GetProperty("FullPackageUnitPrice").GetDecimal();

            // Coherencia tributaria: Quantity * UnitPrice == Total en el comprobante del adelanto
            Assert.Equal(2, qty);
            Assert.Equal(50.00m, unitPrice); // S/ 50 por pasajero facturado en este comprobante de adelanto
            Assert.Equal(100.00m, itemTotal);
            Assert.Equal(100.00m, Math.Round(qty * unitPrice, 2));
            Assert.Equal(100.00m, fullPkgPrice); // Conserva el precio del paquete completo
        }

        [Fact]
        public async Task HistoricalEnvironmentPreserved_WhenConfigSwitchesDemoToProduction()
        {
            using var context = TestDbContextFactory.Create();

            // Configuración en PRODUCCIÓN
            var prodConfig = CreateConfig(mode: "Production", ruta: "https://api.nubefact.com/api/v1/prod", token: "token_prod");

            var booking = CreateTestBooking(context, total: 90m, paid: 90m);

            // Comprobante emitido históricamente en "Demo"
            var historicalReceipt = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "202",
                Status = "Demo",
                Environment = "Demo", // Ambiente histórico
                TotalAmount = 90m,
                CreatedAtUtc = DateTime.UtcNow.AddDays(-10)
            };
            context.ElectronicReceipts.Add(historicalReceipt);
            await context.SaveChangesAsync();

            var queryResponse = "{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":202,\"enlace_del_pdf\":\"https://demo.nubefact.com/pdf/BBB1-202.pdf\",\"sunat_description\":\"Consultado\"}";
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(queryResponse, Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(context, prodConfig, mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            // Act: Consultar estado en NubeFact bajo configuración actual de producción
            var queried = await service.QueryReceiptFromProviderAsync(historicalReceipt.Id, CancellationToken.None);

            // Assert: El Environment histórico "Demo" NO debe ser sobreescrito con "Production"
            Assert.NotNull(queried);
            Assert.Equal("Demo", queried.Environment);
            Assert.Equal("Demo", queried.Status);
        }

        [Fact]
        public async Task ProcessPendingReceipts_WithNumber_QueriesProviderFirst()
        {
            using var context = TestDbContextFactory.Create();
            var config = CreateConfig();
            var booking = CreateTestBooking(context, total: 70m, paid: 70m);

            // Comprobante en Pending pero que ya cuenta con correlativo asignado
            var pendingWithNumber = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "303",
                Status = "Pending",
                Environment = "Demo",
                TotalAmount = 70m,
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(-30)
            };
            context.ElectronicReceipts.Add(pendingWithNumber);
            await context.SaveChangesAsync();

            string calledOperation = "";
            var mockHttp = new HttpClient(new MockCulqiHttpHandler(req =>
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var bodyDoc = JsonDocument.Parse(body);
                calledOperation = bodyDoc.RootElement.GetProperty("operacion").GetString() ?? "";

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":303,\"aceptada_por_sunat\":true,\"sunat_description\":\"Aceptado\"}", Encoding.UTF8, "application/json")
                };
            }));

            var service = new NubefactInvoiceService(context, config, mockHttp, NullLogger<NubefactInvoiceService>.Instance);

            var count = await service.ProcessPendingReceiptsAsync(CancellationToken.None);

            Assert.Equal(1, count);
            Assert.Equal("consultar_comprobante", calledOperation); // Consultó antes de reemitir a ciegas

            var updated = await context.ElectronicReceipts.FindAsync(pendingWithNumber.Id);
            Assert.NotNull(updated);
            Assert.Equal("Accepted", updated.Status);
        }
    }
}
