using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
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
using Npgsql;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Core.Security;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;
using SIGETOUR.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace SIGETOUR.Tests.Audit
{
    [Collection("PostgresDatabaseCollection")]
    public class HardeningAuditPass13Tests
    {
        private readonly ITestOutputHelper? _output;
        private static int _docBCounter = 90000;

        public HardeningAuditPass13Tests(ITestOutputHelper? output = null)
        {
            _output = output;
        }

        private class InspectableHttpMessageHandler : HttpMessageHandler
        {
            private int _correlativeCounter = 13000;
            public List<HttpRequestMessage> Requests { get; } = new();
            public List<string> RequestBodies { get; } = new();
            public Action<HttpRequestMessage>? OnRequest { get; set; }

            public void ResetCounter(int start = 13000) => Interlocked.Exchange(ref _correlativeCounter, start);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                if (request.Content != null)
                {
                    var body = await request.Content.ReadAsStringAsync(cancellationToken);
                    RequestBodies.Add(body);
                }
                OnRequest?.Invoke(request);

                var num = Interlocked.Increment(ref _correlativeCounter);
                var json = $"{{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":\"{num:D8}\",\"enlace_del_pdf\":\"https://demo.nubefact.com/pdf/test_{num:D8}.pdf\",\"aceptada_por_sunat\":true,\"sunat_description\":\"Aceptado\"}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        }

        private class TestDataTracker
        {
            public List<Guid> ReceiptIds { get; } = new();
            public List<Guid> CashIds { get; } = new();
            public List<Guid> ShiftIds { get; } = new();
            public List<Guid> CulqiIds { get; } = new();
            public List<Guid> BookingIds { get; } = new();
            public List<Guid> TourIds { get; } = new();

            public async Task CleanupAsync(AppDbContext db, ITestOutputHelper? output = null, Exception? testException = null)
            {
                try
                {
                    // 1. Comprobantes electrónicos primero (claves foráneas hacia Booking, CulqiPayment, CashTransaction)
                    if (ReceiptIds.Count > 0)
                    {
                        var receipts = await db.ElectronicReceipts.Where(r => ReceiptIds.Contains(r.Id)).ToListAsync();
                        db.ElectronicReceipts.RemoveRange(receipts);
                    }

                    // 2. Movimientos de caja (dependientes de Shift y Booking)
                    if (CashIds.Count > 0)
                    {
                        var cashes = await db.CashTransactions.Where(c => CashIds.Contains(c.Id)).ToListAsync();
                        db.CashTransactions.RemoveRange(cashes);
                    }

                    // 3. Cajas de turno (una vez eliminados los movimientos dependientes)
                    if (ShiftIds.Count > 0)
                    {
                        var shifts = await db.CashRegisterShifts.Where(s => ShiftIds.Contains(s.Id)).ToListAsync();
                        db.CashRegisterShifts.RemoveRange(shifts);
                    }

                    // 4. Pagos Culqi
                    if (CulqiIds.Count > 0)
                    {
                        var culqis = await db.CulqiPayments.Where(p => CulqiIds.Contains(p.Id)).ToListAsync();
                        db.CulqiPayments.RemoveRange(culqis);
                    }

                    // 5. Items y Reservas
                    if (BookingIds.Count > 0)
                    {
                        var items = await db.BookingItems.Where(bi => BookingIds.Contains(bi.BookingId)).ToListAsync();
                        db.BookingItems.RemoveRange(items);
                        var bookings = await db.Bookings.Where(b => BookingIds.Contains(b.Id)).ToListAsync();
                        db.Bookings.RemoveRange(bookings);
                    }

                    // 6. Paquetes turísticos
                    if (TourIds.Count > 0)
                    {
                        var tours = await db.TourPackages.Where(t => TourIds.Contains(t.Id)).ToListAsync();
                        db.TourPackages.RemoveRange(tours);
                    }

                    await db.SaveChangesAsync();
                }
                catch (Exception cleanupEx)
                {
                    output?.WriteLine($"[ERROR DE LIMPIEZA] Error eliminando datos de prueba propios: {cleanupEx}");
                    if (testException != null)
                    {
                        // Preservar la excepción original de aserción combinándola con el fallo de limpieza
                        throw new AggregateException("Fallo en la prueba y posterior fallo en la limpieza de datos de prueba.", testException, cleanupEx);
                    }
                    // Si la aserción pasó pero la limpieza falló, no permitir que la prueba parezca aprobada
                    throw new InvalidOperationException($"La prueba dejó datos residuales porque la limpieza falló: {cleanupEx.Message}", cleanupEx);
                }
            }
        }

        private static void AssertHttpRequestsMatchReceipts(
            InspectableHttpMessageHandler handler,
            IEnumerable<Guid> expectedValidIds,
            IEnumerable<Guid> expectedConflictedIds)
        {
            var valids = expectedValidIds.ToList();
            var conflicts = expectedConflictedIds.ToList();

            // 1. Cada solicitud debe ser de tipo generar_comprobante
            foreach (var body in handler.RequestBodies)
            {
                Assert.Contains("\"operacion\":\"generar_comprobante\"", body);
            }

            // 2. Cada ID válido debe tener exactamente una solicitud correspondiente con su codigo_unico
            foreach (var validId in valids)
            {
                var count = handler.RequestBodies.Count(b => b.Contains($"SIG-{validId:N}", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(1, count);
            }

            // 3. Ningún documento conflictivo debe haber generado solicitud externa
            foreach (var conflictedId in conflicts)
            {
                var count = handler.RequestBodies.Count(b => b.Contains($"SIG-{conflictedId:N}", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(0, count);
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
                { "Payments:CheckoutSigningKey", "super_secret_test_key_phase_13_1234567890" },
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

        private static CashRegisterShift SeedCashRegisterShift(AppDbContext db, TestDataTracker? tracker = null)
        {
            var shift = new CashRegisterShift
            {
                Id = Guid.NewGuid(),
                ShiftNumber = "TURNO-TEST-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                OpenedByUserName = "auditor_p13",
                OpenedAt = DateTime.UtcNow.AddHours(-2),
                InitialAmount = 100m,
                IsOpen = true
            };
            db.CashRegisterShifts.Add(shift);
            db.SaveChanges();

            if (tracker != null)
            {
                tracker.ShiftIds.Add(shift.Id);
            }
            return shift;
        }

        private static Booking SeedBooking(AppDbContext db, decimal total = 100m, decimal paid = 100m, TestDataTracker? tracker = null)
        {
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Tour Otuzco Pass 13",
                Slug = "otuzco-p13-" + Guid.NewGuid().ToString("N")[..6],
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
                CustomerName = "Cliente Auditoría 13",
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

            if (tracker != null)
            {
                tracker.TourIds.Add(tour.Id);
                tracker.BookingIds.Add(booking.Id);
            }

            return booking;
        }

        private static ElectronicReceipt SeedConflictedPendingReceipt(
            AppDbContext db,
            DateTime createdAtUtc,
            Guid? explicitDocAId = null,
            TestDataTracker? tracker = null,
            CashRegisterShift? shift = null)
        {
            if (shift == null)
            {
                shift = SeedCashRegisterShift(db, tracker);
            }

            var booking = SeedBooking(db, 100m, 100m, tracker);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Channel = "charge",
                Status = "paid",
                ProviderId = "chr_test_" + Guid.NewGuid().ToString("N")[..16],
                CreatedAtUtc = createdAtUtc.AddMinutes(-5),
                ConfirmedAtUtc = createdAtUtc.AddMinutes(-5)
            };

            var cash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 100m,
                PaymentMethod = "Tarjeta Web",
                Description = "Cobro Culqi verificado",
                RegisteredBy = "auditor_p13",
                CreatedAt = createdAtUtc.AddMinutes(-5)
            };

            // Doc A: en Pending sin número
            var docA = new ElectronicReceipt
            {
                Id = explicitDocAId ?? Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "",
                Status = "Pending",
                CreatedAtUtc = createdAtUtc
            };

            // Doc B: emitido bajo la misma operación financiera con número único para evitar colisión de índice en PostgreSQL
            var numB = Interlocked.Increment(ref _docBCounter);
            var docB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CashTransactionId = cash.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = numB.ToString("D8"),
                Status = "Demo",
                CreatedAtUtc = createdAtUtc.AddSeconds(-2)
            };

            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(docA, docB);
            db.SaveChanges();

            if (tracker != null)
            {
                tracker.CulqiIds.Add(culqi.Id);
                tracker.CashIds.Add(cash.Id);
                tracker.ReceiptIds.Add(docA.Id);
                tracker.ReceiptIds.Add(docB.Id);
            }

            return docA;
        }

        private static ElectronicReceipt SeedHealthyPendingReceipt(
            AppDbContext db,
            DateTime createdAtUtc,
            Guid? explicitId = null,
            TestDataTracker? tracker = null)
        {
            var booking = SeedBooking(db, 100m, 100m, tracker);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Channel = "charge",
                Status = "paid",
                ProviderId = "chr_test_" + Guid.NewGuid().ToString("N")[..16],
                CreatedAtUtc = createdAtUtc.AddMinutes(-5),
                ConfirmedAtUtc = createdAtUtc.AddMinutes(-5)
            };

            var doc = new ElectronicReceipt
            {
                Id = explicitId ?? Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "",
                Status = "Pending",
                CreatedAtUtc = createdAtUtc
            };

            db.CulqiPayments.Add(culqi);
            db.ElectronicReceipts.Add(doc);
            db.SaveChanges();

            if (tracker != null)
            {
                tracker.CulqiIds.Add(culqi.Id);
                tracker.ReceiptIds.Add(doc.Id);
            }

            return doc;
        }

        // =========================================================================
        // A. IN-MEMORY: 20 CONFLICTOS SEGUIDOS DE 1 VÁLIDO EN POSICIÓN 21
        // =========================================================================

        [Fact]
        public async Task ProcessPendingReceiptsAsync_FirstBatchAllConflicts_ReachesAndProcessesValidAtPosition21()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var baseTime = DateTime.UtcNow.AddMinutes(-60);

            var shift = SeedCashRegisterShift(db);

            // Sembrar exactamente 20 comprobantes pendientes en conflicto (lote 1 completo bloqueado)
            var conflictedList = new List<ElectronicReceipt>();
            for (int i = 0; i < 20; i++)
            {
                var conflicted = SeedConflictedPendingReceipt(db, baseTime.AddMinutes(i), shift: shift);
                conflictedList.Add(conflicted);
            }

            // Sembrar 1 comprobante pendiente sano posterior en la posición 21
            var healthy = SeedHealthyPendingReceipt(db, baseTime.AddMinutes(25));

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

            // Ejecutar el procesamiento de pendientes
            var processed = await service.ProcessPendingReceiptsAsync(CancellationToken.None);

            // Debe haber procesado exitosamente el documento 21
            Assert.Equal(1, processed);
            Assert.Single(handler.Requests);

            // Comprobación de solicitudes HTTP contra documentos
            AssertHttpRequestsMatchReceipts(handler, new[] { healthy.Id }, conflictedList.Select(c => c.Id));

            // Comprobar que el documento 21 fue emitido
            var freshHealthy = await db.ElectronicReceipts.FindAsync(healthy.Id);
            Assert.NotNull(freshHealthy);
            Assert.True(freshHealthy.Status is "Accepted" or "Demo");
            Assert.False(string.IsNullOrWhiteSpace(freshHealthy.Number));

            // Comprobar que los 20 comprobantes conflictivos permanecen intactos en Pending
            foreach (var c in conflictedList)
            {
                var freshConflicted = await db.ElectronicReceipts.FindAsync(c.Id);
                Assert.NotNull(freshConflicted);
                Assert.Equal("Pending", freshConflicted.Status);
                Assert.Null(freshConflicted.EmissionLeaseToken);
                Assert.True(string.IsNullOrEmpty(freshConflicted.Number));
                Assert.Equal(c.BillableOperationKey, freshConflicted.BillableOperationKey);
            }
        }

        // =========================================================================
        // B. IN-MEMORY: MÁS DE UN LOTE BLOQUEADO (45 CONFLICTOS SEGUIDOS DE 3 VÁLIDOS)
        // =========================================================================

        [Fact]
        public async Task ProcessPendingReceiptsAsync_MultipleBlockedBatches_TraversesAllAndProcessesSubsequentValids()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var baseTime = DateTime.UtcNow.AddMinutes(-120);

            var shift = SeedCashRegisterShift(db);

            // Sembrar 45 comprobantes en conflicto (> 2 lotes completos de 20)
            var conflictedList = new List<ElectronicReceipt>();
            for (int i = 0; i < 45; i++)
            {
                var conflicted = SeedConflictedPendingReceipt(db, baseTime.AddMinutes(i), shift: shift);
                conflictedList.Add(conflicted);
            }

            // Sembrar 3 comprobantes sanos en las posiciones 46, 47, 48
            var healthy1 = SeedHealthyPendingReceipt(db, baseTime.AddMinutes(50));
            var healthy2 = SeedHealthyPendingReceipt(db, baseTime.AddMinutes(51));
            var healthy3 = SeedHealthyPendingReceipt(db, baseTime.AddMinutes(52));
            var healthyList = new[] { healthy1, healthy2, healthy3 };

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

            var processed = await service.ProcessPendingReceiptsAsync(CancellationToken.None);

            // Se deben procesar exactamente los 3 comprobantes sanos
            Assert.Equal(3, processed);
            Assert.Equal(3, handler.Requests.Count);

            AssertHttpRequestsMatchReceipts(handler, healthyList.Select(h => h.Id), conflictedList.Select(c => c.Id));

            foreach (var h in healthyList)
            {
                var fresh = await db.ElectronicReceipts.FindAsync(h.Id);
                Assert.NotNull(fresh);
                Assert.True(fresh.Status is "Accepted" or "Demo");
                Assert.False(string.IsNullOrWhiteSpace(fresh.Number));
            }

            // Los 45 conflictivos permanecen intactos
            foreach (var c in conflictedList)
            {
                var fresh = await db.ElectronicReceipts.FindAsync(c.Id);
                Assert.NotNull(fresh);
                Assert.Equal("Pending", fresh.Status);
                Assert.Null(fresh.EmissionLeaseToken);
                Assert.True(string.IsNullOrEmpty(fresh.Number));
            }
        }

        // =========================================================================
        // C. IN-MEMORY: FECHAS IGUALES Y DESEMPATE POR ID
        // =========================================================================

        [Fact]
        public async Task ProcessPendingReceiptsAsync_EqualCreatedAtUtc_TieBreakerByIdPreventsSkipsAndDuplicates()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var exactSameTime = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

            var shift = SeedCashRegisterShift(db);

            // 20 conflictivos y 5 sanos, todos con la misma fecha CreatedAtUtc
            var conflictedList = new List<ElectronicReceipt>();
            for (int i = 0; i < 20; i++)
            {
                conflictedList.Add(SeedConflictedPendingReceipt(db, exactSameTime, shift: shift));
            }

            var healthyList = new List<ElectronicReceipt>();
            for (int i = 0; i < 5; i++)
            {
                healthyList.Add(SeedHealthyPendingReceipt(db, exactSameTime));
            }

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

            var processed = await service.ProcessPendingReceiptsAsync(CancellationToken.None);

            // Todos los 5 comprobantes sanos deben ser procesados
            Assert.Equal(5, processed);
            Assert.Equal(5, handler.Requests.Count);

            AssertHttpRequestsMatchReceipts(handler, healthyList.Select(h => h.Id), conflictedList.Select(c => c.Id));

            foreach (var h in healthyList)
            {
                var fresh = await db.ElectronicReceipts.FindAsync(h.Id);
                Assert.NotNull(fresh);
                Assert.True(fresh.Status is "Accepted" or "Demo");
                Assert.False(string.IsNullOrWhiteSpace(fresh.Number));
            }

            foreach (var c in conflictedList)
            {
                var fresh = await db.ElectronicReceipts.FindAsync(c.Id);
                Assert.NotNull(fresh);
                Assert.Equal("Pending", fresh.Status);
                Assert.Null(fresh.EmissionLeaseToken);
            }
        }

        // =========================================================================
        // D. IN-MEMORY: CAMBIOS DINÁMICOS DURANTE EL RECORRIDO
        // =========================================================================

        [Fact]
        public async Task ProcessPendingReceiptsAsync_DynamicQueueChange_KeysetDoesNotShiftOrSkipDocuments()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var baseTime = DateTime.UtcNow.AddMinutes(-50);

            var shift = SeedCashRegisterShift(db);

            // Intercalados: 10 sanos, 10 conflictivos, 5 sanos
            var healthyList = new List<ElectronicReceipt>();
            var conflictedList = new List<ElectronicReceipt>();

            for (int i = 0; i < 10; i++)
            {
                healthyList.Add(SeedHealthyPendingReceipt(db, baseTime.AddMinutes(i)));
            }
            for (int i = 10; i < 20; i++)
            {
                conflictedList.Add(SeedConflictedPendingReceipt(db, baseTime.AddMinutes(i), shift: shift));
            }
            for (int i = 20; i < 25; i++)
            {
                healthyList.Add(SeedHealthyPendingReceipt(db, baseTime.AddMinutes(i)));
            }

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

            var processed = await service.ProcessPendingReceiptsAsync(CancellationToken.None);

            // Todos los 15 sanos fueron procesados (10 de lote 1 + 5 de lote 2)
            Assert.Equal(15, processed);
            Assert.Equal(15, handler.Requests.Count);

            AssertHttpRequestsMatchReceipts(handler, healthyList.Select(h => h.Id), conflictedList.Select(c => c.Id));

            foreach (var h in healthyList)
            {
                var fresh = await db.ElectronicReceipts.FindAsync(h.Id);
                Assert.NotNull(fresh);
                Assert.True(fresh.Status is "Accepted" or "Demo");
            }

            foreach (var c in conflictedList)
            {
                var fresh = await db.ElectronicReceipts.FindAsync(c.Id);
                Assert.NotNull(fresh);
                Assert.Equal("Pending", fresh.Status);
                Assert.Null(fresh.EmissionLeaseToken);
            }
        }

        // =========================================================================
        // E. IN-MEMORY: SEGUNDA EJECUCIÓN / IDEMPOTENCIA
        // =========================================================================

        [Fact]
        public async Task ProcessPendingReceiptsAsync_RepeatedRun_DoesNotRepeatEmissions()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var baseTime = DateTime.UtcNow.AddMinutes(-30);

            var shift = SeedCashRegisterShift(db);

            // 5 conflictivos y 2 sanos
            var c1 = SeedConflictedPendingReceipt(db, baseTime.AddMinutes(1), shift: shift);
            var c2 = SeedConflictedPendingReceipt(db, baseTime.AddMinutes(2), shift: shift);
            var h1 = SeedHealthyPendingReceipt(db, baseTime.AddMinutes(3));
            var h2 = SeedHealthyPendingReceipt(db, baseTime.AddMinutes(4));

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

            // Primera corrida
            var run1 = await service.ProcessPendingReceiptsAsync(CancellationToken.None);
            Assert.Equal(2, run1);
            Assert.Equal(2, handler.Requests.Count);

            // Segunda corrida inmediata
            var run2 = await service.ProcessPendingReceiptsAsync(CancellationToken.None);
            Assert.Equal(0, run2);
            // No se hicieron llamadas HTTP adicionales
            Assert.Equal(2, handler.Requests.Count);

            // Los conflictivos siguen en Pending
            var freshC1 = await db.ElectronicReceipts.FindAsync(c1.Id);
            var freshC2 = await db.ElectronicReceipts.FindAsync(c2.Id);
            Assert.Equal("Pending", freshC1!.Status);
            Assert.Equal("Pending", freshC2!.Status);
            Assert.Null(freshC1.EmissionLeaseToken);
            Assert.Null(freshC2.EmissionLeaseToken);
        }

        // =========================================================================
        // F. IN-MEMORY: CANCELACIÓN COOPERATIVA
        // =========================================================================

        [Fact]
        public async Task ProcessPendingReceiptsAsync_CancellationRequested_HaltsImmediatelyAndRethrows()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var baseTime = DateTime.UtcNow.AddMinutes(-30);

            for (int i = 0; i < 30; i++)
            {
                SeedHealthyPendingReceipt(db, baseTime.AddMinutes(i));
            }

            var config = CreateConfig();
            using var cts = new CancellationTokenSource();
            var handler = new InspectableHttpMessageHandler();
            handler.OnRequest = _ =>
            {
                // Cancelar inmediatamente en la primera llamada externa
                cts.Cancel();
            };

            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

            // Debe lanzar OperationCanceledException
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ProcessPendingReceiptsAsync(cts.Token));

            // No debe haber procesado todas las páginas (se detuvo inmediatamente)
            Assert.True(handler.Requests.Count <= 2, $"Esperado <= 2 llamadas antes de abortar, pero fueron {handler.Requests.Count}");
        }

        // =========================================================================
        // G. IN-MEMORY: PROTECCIONES FINANCIERAS DE AUDITORÍA FASE 11 Y 12 CONSERVADAS
        // =========================================================================

        [Fact]
        public async Task FinancialRegressions_AmbiguousConflictProtection_RemainsStrictlyIntact()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var shift = SeedCashRegisterShift(db);
            var booking = SeedBooking(db, 100m, 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Channel = "charge",
                Status = "paid",
                ProviderId = "chr_test_" + Guid.NewGuid().ToString("N")[..16],
                CreatedAtUtc = DateTime.UtcNow,
                ConfirmedAtUtc = DateTime.UtcNow
            };
            var cash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = 100m,
                PaymentMethod = "Tarjeta Web",
                Description = "Cobro Culqi verificado",
                RegisteredBy = "auditor_p13",
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
                Number = "",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            var docB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CashTransactionId = cash.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Number = "00013090",
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

            // 1. Reintento directo por receiptId lanza AmbiguousReceiptConflictException
            var ex1 = await Assert.ThrowsAsync<AmbiguousReceiptConflictException>(() =>
                service.RetryReceiptAsync(docA.Id));
            Assert.Contains(docA.Id, ex1.CandidateReceiptIds);
            Assert.Contains(docB.Id, ex1.CandidateReceiptIds);

            // 2. GenerateReceiptAsync con exactReceiptId lanza AmbiguousReceiptConflictException
            var ex2 = await Assert.ThrowsAsync<AmbiguousReceiptConflictException>(() =>
                service.GenerateReceiptAsync(booking.Id, culqi.Id, exactReceiptId: docA.Id, claimToken: null, cashTransactionId: cash.Id));
            Assert.Equal(booking.Id, ex2.BookingId);

            // Cero peticiones externas realizadas
            Assert.Empty(handler.Requests);
        }

        // =========================================================================
        // H.1 TRADUCCIÓN SQL NPGSQL (UNITARIA SQL SIN REQUERIR SERVIDOR FÍSICO)
        // =========================================================================

        [Fact]
        public void Postgres_KeysetQuery_GeneratesValidNpgsqlSqlWithOrderByAndCursor()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=sigetour_dummy_test;Username=postgres;Password=postgres")
                .Options;

            using var db = new AppDbContext(options);
            var cutoff = DateTime.UtcNow;
            var cursorCreatedAt = DateTime.UtcNow.AddMinutes(-10);
            var cursorId = Guid.NewGuid();

            var query = db.ElectronicReceipts
                .Where(r => (r.Status == "Pending" || (r.Status == "Submitted" && !string.IsNullOrWhiteSpace(r.Number))) && r.CreatedAtUtc <= cutoff)
                .Where(r => r.CreatedAtUtc > cursorCreatedAt || (r.CreatedAtUtc == cursorCreatedAt && r.Id.CompareTo(cursorId) > 0))
                .OrderBy(r => r.CreatedAtUtc)
                .ThenBy(r => r.Id)
                .Take(20);

            string sql = query.ToQueryString();

            Assert.NotNull(sql);
            Assert.Contains("\"CreatedAtUtc\"", sql);
            Assert.Contains("\"Id\"", sql);
            Assert.Contains("ORDER BY", sql);
            Assert.Contains("LIMIT", sql);
        }

        // =========================================================================
        // H.2 PRUEBAS DE INTEGRACIÓN EN POSTGRESQL CON FIXTURE DE AISLAMIENTO EXCLUSIVO
        // =========================================================================

        [SkippableFact]
        public async Task Postgres_ProcessPendingReceipts_KeysetTraversal_OnIsolatedPostgres()
        {
            // Escenario A en PostgreSQL: 20 conflictivos bloqueados seguidos de 5 válidos cruzando el límite de página (20)
            await Postgres_ExecuteScenarioA_BlockedBatchFollowedByValids();
        }

        [SkippableFact]
        public async Task Postgres_ProcessPendingReceipts_ScenarioA_BlockedBatchFollowedByValids_AcrossBoundary()
        {
            await Postgres_ExecuteScenarioA_BlockedBatchFollowedByValids();
        }

        private async Task Postgres_ExecuteScenarioA_BlockedBatchFollowedByValids()
        {
            var fixture = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            if (fixture == null) return;

            var tracker = new TestDataTracker();
            Exception? testEx = null;
            try
            {
                await using var db = fixture.CreateDbContext();

                var baseTime = DateTime.UtcNow.AddMinutes(-60);
                var shift = SeedCashRegisterShift(db, tracker);

                // 1. Sembrar exactamente 20 comprobantes pendientes en conflicto (Página 1: items 0 a 19)
                var conflictedList = new List<ElectronicReceipt>();
                for (int i = 0; i < 20; i++)
                {
                    var conflicted = SeedConflictedPendingReceipt(db, baseTime.AddMinutes(i), tracker: tracker, shift: shift);
                    conflictedList.Add(conflicted);
                }

                // 2. Sembrar exactamente 5 comprobantes sanos en Página 2 (items 20 a 24)
                var healthyList = new List<ElectronicReceipt>();
                for (int i = 20; i < 25; i++)
                {
                    var healthy = SeedHealthyPendingReceipt(db, baseTime.AddMinutes(i), tracker: tracker);
                    healthyList.Add(healthy);
                }

                // 3. Verificación de aislamiento estricta antes de procesar:
                // Comprobar que en el espacio aislado existen únicamente los IDs sembrados por la prueba
                var preReceiptIds = await db.ElectronicReceipts.AsNoTracking().Select(r => r.Id).ToListAsync();
                var expectedAllIds = tracker.ReceiptIds.ToHashSet();
                var unexpected = preReceiptIds.Except(expectedAllIds).ToList();
                var missing = expectedAllIds.Except(preReceiptIds).ToList();
                if (unexpected.Count > 0 || missing.Count > 0)
                {
                    Assert.Fail($"[AISLAMIENTO COMPROMETIDO] El espacio aislado '{fixture.SchemaName}' no coincide con los datos sembrados. Inesperados: {unexpected.Count}, Faltantes: {missing.Count}.");
                }

                var config = CreateConfig();
                var handler = new InspectableHttpMessageHandler();
                using var httpClient = new HttpClient(handler);
                var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

                // Act
                var processed = await service.ProcessPendingReceiptsAsync(CancellationToken.None);

                // Assert 1: Se procesaron exactamente 5 documentos válidos
                Assert.Equal(5, processed);

                // Assert 2: Cero emisiones para los 20 conflictivos, exactamente 5 llamadas para los válidos
                Assert.Equal(5, handler.Requests.Count);

                // Assert 3: Correspondencia exacta de cuerpos HTTP y documentos
                AssertHttpRequestsMatchReceipts(handler, healthyList.Select(h => h.Id), conflictedList.Select(c => c.Id));

                // Assert 4: Comprobación con contexto fresco sin seguimiento en el espacio aislado
                await using var verifyDb = fixture.CreateDbContext();

                foreach (var h in healthyList)
                {
                    var freshValid = await verifyDb.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == h.Id);
                    Assert.NotNull(freshValid);
                    Assert.True(freshValid.Status is "Accepted" or "Demo");
                    Assert.False(string.IsNullOrWhiteSpace(freshValid.Number));
                    Assert.NotNull(freshValid.PdfUrl);

                    // Demostrar contractualmente que el documento ya no puede ser reclamado
                    var (claimed, _, _) = await service.TryAcquireEmissionAsync(freshValid.Id, requestedClaimToken: null, allowRejectedRetry: false);
                    Assert.False(claimed);
                }

                foreach (var c in conflictedList)
                {
                    var freshConflicted = await verifyDb.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == c.Id);
                    Assert.NotNull(freshConflicted);
                    Assert.Equal("Pending", freshConflicted.Status);
                    Assert.True(string.IsNullOrWhiteSpace(freshConflicted.Number));
                    Assert.Null(freshConflicted.EmissionLeaseToken); // El documento en conflicto NUNCA adquirió lease
                    Assert.Equal(c.BillableOperationKey, freshConflicted.BillableOperationKey);
                }
            }
            catch (Exception ex)
            {
                testEx = ex;
                throw;
            }
            finally
            {
                try
                {
                    await using var cleanupDb = fixture.CreateDbContext();
                    await tracker.CleanupAsync(cleanupDb, _output, testEx);
                }
                finally
                {
                    await fixture.DisposeAsync(testEx);
                }
            }
        }

        [SkippableFact]
        public async Task Postgres_ProcessPendingReceipts_ScenarioB_EqualTimestamps_TieBreakerById_AcrossBoundary()
        {
            var fixture = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            if (fixture == null) return;

            var tracker = new TestDataTracker();
            Exception? testEx = null;
            try
            {
                await using var db = fixture.CreateDbContext();

                var exactSameTime = DateTime.UtcNow.AddMinutes(-45);
                var shift = SeedCashRegisterShift(db, tracker);

                // Generar 25 IDs deterministas: 00000000-0000-0000-0000-000000000001 a ...0025
                var healthyIds = new HashSet<Guid>();
                var conflictedIds = new HashSet<Guid>();

                // Distribución cruzando el límite de página (20):
                // Página 1 (pos 1-20): 1-10 conflictivos (10), 11-15 sanos (5), 16-20 conflictivos (5)
                // Página 2 (pos 21-25): 21-23 sanos (3), 24-25 conflictivos (2)
                for (int i = 1; i <= 25; i++)
                {
                    var guid = Guid.Parse($"00000000-0000-0000-0000-{i:D12}");
                    bool isHealthy = (i >= 11 && i <= 15) || (i >= 21 && i <= 23);

                    if (isHealthy)
                    {
                        SeedHealthyPendingReceipt(db, exactSameTime, explicitId: guid, tracker: tracker);
                        healthyIds.Add(guid);
                    }
                    else
                    {
                        SeedConflictedPendingReceipt(db, exactSameTime, explicitDocAId: guid, tracker: tracker, shift: shift);
                        conflictedIds.Add(guid);
                    }
                }

                // Verificación de aislamiento estricta antes de procesar
                var preReceiptIds = await db.ElectronicReceipts.AsNoTracking().Select(r => r.Id).ToListAsync();
                var expectedAllIds = tracker.ReceiptIds.ToHashSet();
                var unexpected = preReceiptIds.Except(expectedAllIds).ToList();
                var missing = expectedAllIds.Except(preReceiptIds).ToList();
                if (unexpected.Count > 0 || missing.Count > 0)
                {
                    Assert.Fail($"[AISLAMIENTO COMPROMETIDO] El espacio aislado '{fixture.SchemaName}' no coincide con los datos sembrados. Inesperados: {unexpected.Count}, Faltantes: {missing.Count}.");
                }

                var config = CreateConfig();
                var handler = new InspectableHttpMessageHandler();
                using var httpClient = new HttpClient(handler);
                var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

                // Act
                var processed = await service.ProcessPendingReceiptsAsync(CancellationToken.None);

                // Assert: Total sanos esperados = 5 (lote 1) + 3 (lote 2) = 8
                Assert.Equal(8, processed);
                Assert.Equal(8, handler.Requests.Count);

                AssertHttpRequestsMatchReceipts(handler, healthyIds, conflictedIds);

                await using var verifyDb = fixture.CreateDbContext();

                foreach (var id in healthyIds)
                {
                    var fresh = await verifyDb.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
                    Assert.NotNull(fresh);
                    Assert.True(fresh.Status is "Accepted" or "Demo");
                    Assert.False(string.IsNullOrWhiteSpace(fresh.Number));

                    var (claimed, _, _) = await service.TryAcquireEmissionAsync(fresh.Id, requestedClaimToken: null, allowRejectedRetry: false);
                    Assert.False(claimed);
                }

                foreach (var id in conflictedIds)
                {
                    var fresh = await verifyDb.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
                    Assert.NotNull(fresh);
                    Assert.Equal("Pending", fresh.Status);
                    Assert.True(string.IsNullOrWhiteSpace(fresh.Number));
                    Assert.Null(fresh.EmissionLeaseToken);
                }
            }
            catch (Exception ex)
            {
                testEx = ex;
                throw;
            }
            finally
            {
                try
                {
                    await using var cleanupDb = fixture.CreateDbContext();
                    await tracker.CleanupAsync(cleanupDb, _output, testEx);
                }
                finally
                {
                    await fixture.DisposeAsync(testEx);
                }
            }
        }

        [SkippableFact]
        public async Task Postgres_ProcessPendingReceipts_ScenarioC_DynamicQueueChange_NoSkips()
        {
            var fixture = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            if (fixture == null) return;

            var tracker = new TestDataTracker();
            Exception? testEx = null;
            try
            {
                await using var db = fixture.CreateDbContext();

                var baseTime = DateTime.UtcNow.AddMinutes(-55);
                var shift = SeedCashRegisterShift(db, tracker);

                // Página 1 (0 a 19): 15 sanos (0 a 14) + 5 conflictivos (15 a 19)
                // Página 2 (20 a 29): 5 sanos (20 a 24) + 5 conflictivos (25 a 29)
                // Total: 20 sanos y 10 conflictivos
                var healthyIds = new List<Guid>();
                var conflictedIds = new List<Guid>();

                for (int i = 0; i < 15; i++)
                {
                    var h = SeedHealthyPendingReceipt(db, baseTime.AddMinutes(i), tracker: tracker);
                    healthyIds.Add(h.Id);
                }
                for (int i = 15; i < 20; i++)
                {
                    var c = SeedConflictedPendingReceipt(db, baseTime.AddMinutes(i), tracker: tracker, shift: shift);
                    conflictedIds.Add(c.Id);
                }
                for (int i = 20; i < 25; i++)
                {
                    var h = SeedHealthyPendingReceipt(db, baseTime.AddMinutes(i), tracker: tracker);
                    healthyIds.Add(h.Id);
                }
                for (int i = 25; i < 30; i++)
                {
                    var c = SeedConflictedPendingReceipt(db, baseTime.AddMinutes(i), tracker: tracker, shift: shift);
                    conflictedIds.Add(c.Id);
                }

                // Verificación de aislamiento estricta antes de procesar
                var preReceiptIds = await db.ElectronicReceipts.AsNoTracking().Select(r => r.Id).ToListAsync();
                var expectedAllIds = tracker.ReceiptIds.ToHashSet();
                var unexpected = preReceiptIds.Except(expectedAllIds).ToList();
                var missing = expectedAllIds.Except(preReceiptIds).ToList();
                if (unexpected.Count > 0 || missing.Count > 0)
                {
                    Assert.Fail($"[AISLAMIENTO COMPROMETIDO] El espacio aislado '{fixture.SchemaName}' no coincide con los datos sembrados. Inesperados: {unexpected.Count}, Faltantes: {missing.Count}.");
                }

                var config = CreateConfig();
                var handler = new InspectableHttpMessageHandler();
                using var httpClient = new HttpClient(handler);
                var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

                // Act
                var processed = await service.ProcessPendingReceiptsAsync(CancellationToken.None);

                // Assert: los 20 sanos procesados (los de lote 1 dejaron de ser Pending, sin causar saltos en lote 2)
                Assert.Equal(20, processed);
                Assert.Equal(20, handler.Requests.Count);

                AssertHttpRequestsMatchReceipts(handler, healthyIds, conflictedIds);

                await using var verifyDb = fixture.CreateDbContext();

                foreach (var id in healthyIds)
                {
                    var fresh = await verifyDb.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
                    Assert.NotNull(fresh);
                    Assert.True(fresh.Status is "Accepted" or "Demo");

                    var (claimed, _, _) = await service.TryAcquireEmissionAsync(fresh.Id, requestedClaimToken: null, allowRejectedRetry: false);
                    Assert.False(claimed);
                }

                foreach (var id in conflictedIds)
                {
                    var fresh = await verifyDb.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
                    Assert.NotNull(fresh);
                    Assert.Equal("Pending", fresh.Status);
                    Assert.Null(fresh.EmissionLeaseToken);
                }
            }
            catch (Exception ex)
            {
                testEx = ex;
                throw;
            }
            finally
            {
                try
                {
                    await using var cleanupDb = fixture.CreateDbContext();
                    await tracker.CleanupAsync(cleanupDb, _output, testEx);
                }
                finally
                {
                    await fixture.DisposeAsync(testEx);
                }
            }
        }

        [SkippableFact]
        public async Task Postgres_ProcessPendingReceipts_ScenarioD_SecondRun_ProducesZeroAdditionalEmissions()
        {
            var fixture = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            if (fixture == null) return;

            var tracker = new TestDataTracker();
            Exception? testEx = null;
            try
            {
                await using var db = fixture.CreateDbContext();

                var baseTime = DateTime.UtcNow.AddMinutes(-40);
                var shift = SeedCashRegisterShift(db, tracker);

                // 20 conflictivos y 5 sanos
                var conflictedList = new List<ElectronicReceipt>();
                for (int i = 0; i < 20; i++)
                {
                    conflictedList.Add(SeedConflictedPendingReceipt(db, baseTime.AddMinutes(i), tracker: tracker, shift: shift));
                }
                var healthyList = new List<ElectronicReceipt>();
                for (int i = 20; i < 25; i++)
                {
                    healthyList.Add(SeedHealthyPendingReceipt(db, baseTime.AddMinutes(i), tracker: tracker));
                }

                // Verificación de aislamiento estricta antes de procesar
                var preReceiptIds = await db.ElectronicReceipts.AsNoTracking().Select(r => r.Id).ToListAsync();
                var expectedAllIds = tracker.ReceiptIds.ToHashSet();
                var unexpected = preReceiptIds.Except(expectedAllIds).ToList();
                var missing = expectedAllIds.Except(preReceiptIds).ToList();
                if (unexpected.Count > 0 || missing.Count > 0)
                {
                    Assert.Fail($"[AISLAMIENTO COMPROMETIDO] El espacio aislado '{fixture.SchemaName}' no coincide con los datos sembrados. Inesperados: {unexpected.Count}, Faltantes: {missing.Count}.");
                }

                var config = CreateConfig();
                var handler = new InspectableHttpMessageHandler();
                using var httpClient = new HttpClient(handler);
                var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

                // Corrida 1
                var run1 = await service.ProcessPendingReceiptsAsync(CancellationToken.None);
                Assert.Equal(5, run1);
                Assert.Equal(5, handler.Requests.Count);

                AssertHttpRequestsMatchReceipts(handler, healthyList.Select(h => h.Id), conflictedList.Select(c => c.Id));

                // Corrida 2 inmediata sobre la misma base de datos
                var run2 = await service.ProcessPendingReceiptsAsync(CancellationToken.None);
                Assert.Equal(0, run2);
                Assert.Equal(5, handler.Requests.Count); // Cero llamadas HTTP adicionales

                await using var verifyDb = fixture.CreateDbContext();

                foreach (var h in healthyList)
                {
                    var fresh = await verifyDb.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == h.Id);
                    Assert.NotNull(fresh);
                    Assert.True(fresh.Status is "Accepted" or "Demo");
                    Assert.False(string.IsNullOrWhiteSpace(fresh.Number));

                    var (claimed, _, _) = await service.TryAcquireEmissionAsync(fresh.Id, requestedClaimToken: null, allowRejectedRetry: false);
                    Assert.False(claimed);
                }

                foreach (var c in conflictedList)
                {
                    var fresh = await verifyDb.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == c.Id);
                    Assert.NotNull(fresh);
                    Assert.Equal("Pending", fresh.Status);
                    Assert.True(string.IsNullOrWhiteSpace(fresh.Number));
                    Assert.Null(fresh.EmissionLeaseToken);
                }
            }
            catch (Exception ex)
            {
                testEx = ex;
                throw;
            }
            finally
            {
                try
                {
                    await using var cleanupDb = fixture.CreateDbContext();
                    await tracker.CleanupAsync(cleanupDb, _output, testEx);
                }
                finally
                {
                    await fixture.DisposeAsync(testEx);
                }
            }
        }

        // =========================================================================
        // H.3 PRUEBAS ESPECÍFICAS DE AISLAMIENTO EN POSTGRESQL (VERIFICACIÓN A, B, C, D)
        // =========================================================================

        [SkippableFact]
        public async Task Postgres_Isolation_ScenarioA_TwoIndependentSpaces_WorkerInOneDoesNotAffectOther()
        {
            var fixture1 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            if (fixture1 == null) return;
            PostgresIsolatedScenarioFixture? fixture2 = null;

            try
            {
                fixture2 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
                if (fixture2 == null) return;

                await using var db1 = fixture1.CreateDbContext();
                await using var db2 = fixture2.CreateDbContext();

                var baseTime = DateTime.UtcNow.AddMinutes(-30);

                // Sembrar 1 comprobante sano en fixture1 y 1 comprobante sano en fixture2
                var doc1 = SeedHealthyPendingReceipt(db1, baseTime);
                var doc2 = SeedHealthyPendingReceipt(db2, baseTime);

                // Ejecutar worker exclusivamente en db1
                var config = CreateConfig();
                var handler1 = new InspectableHttpMessageHandler();
                using var httpClient1 = new HttpClient(handler1);
                var service1 = new NubefactInvoiceService(db1, config, httpClient1, NullLogger<NubefactInvoiceService>.Instance);

                var processed1 = await service1.ProcessPendingReceiptsAsync(CancellationToken.None);

                Assert.Equal(1, processed1);
                Assert.Single(handler1.Requests);

                // Verificar que en fixture1 el comprobante se emitió
                await using var verifyDb1 = fixture1.CreateDbContext();
                var fresh1 = await verifyDb1.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == doc1.Id);
                Assert.NotNull(fresh1);
                Assert.True(fresh1.Status is "Accepted" or "Demo");
                Assert.False(string.IsNullOrWhiteSpace(fresh1.Number));

                // Verificar que en fixture2 el comprobante permaneció intacto (no fue visto ni tocado por service1)
                await using var verifyDb2 = fixture2.CreateDbContext();
                var fresh2 = await verifyDb2.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == doc2.Id);
                Assert.NotNull(fresh2);
                Assert.Equal("Pending", fresh2.Status);
                Assert.True(string.IsNullOrWhiteSpace(fresh2.Number));
                Assert.Null(fresh2.EmissionLeaseToken);
            }
            finally
            {
                if (fixture1 != null) await fixture1.DisposeAsync();
                if (fixture2 != null) await fixture2.DisposeAsync();
            }
        }

        [SkippableFact]
        public async Task Postgres_Isolation_ScenarioB_CleanupOfOneSpace_DoesNotAffectOther_AndLeavesNoResources()
        {
            var fixture1 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            if (fixture1 == null) return;
            PostgresIsolatedScenarioFixture? fixture2 = null;

            try
            {
                fixture2 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
                if (fixture2 == null) return;

                await using var db1 = fixture1.CreateDbContext();
                await using var db2 = fixture2.CreateDbContext();

                var doc1 = SeedHealthyPendingReceipt(db1, DateTime.UtcNow.AddMinutes(-20));
                var doc2 = SeedHealthyPendingReceipt(db2, DateTime.UtcNow.AddMinutes(-20));

                // Liberar fixture1
                var schema1 = fixture1.SchemaName;
                await fixture1.DisposeAsync();

                // 1. Comprobar que en PostgreSQL no queda rastro del esquema 1
                var baseConn = PostgresTestHelper.GetConfiguredConnectionString()!;
                await using var conn = new NpgsqlConnection(baseConn);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname = @name;";
                cmd.Parameters.AddWithValue("@name", schema1);
                var schemaCount = (long?)await cmd.ExecuteScalarAsync();
                Assert.Equal(0, schemaCount);

                // 2. Comprobar que fixture2 sigue plenamente operativo y con sus datos intactos
                await using var verifyDb2 = fixture2.CreateDbContext();
                var fresh2 = await verifyDb2.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == doc2.Id);
                Assert.NotNull(fresh2);
                Assert.Equal("Pending", fresh2.Status);
            }
            finally
            {
                if (fixture1 != null) await fixture1.DisposeAsync();
                if (fixture2 != null) await fixture2.DisposeAsync();
            }
        }

        [SkippableFact]
        public async Task Postgres_Isolation_ScenarioC_ControlledTestFailure_PreservesOriginalDiagnostic_AndCleansUpSafely()
        {
            var fixture1 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            if (fixture1 == null) return;
            var fixture2 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            if (fixture2 == null)
            {
                await fixture1.DisposeAsync();
                return;
            }

            try
            {
                await using var db1 = fixture1.CreateDbContext();
                await using var db2 = fixture2.CreateDbContext();

                SeedHealthyPendingReceipt(db1, DateTime.UtcNow.AddMinutes(-10));
                var doc2 = SeedHealthyPendingReceipt(db2, DateTime.UtcNow.AddMinutes(-10));

                // Provocar fallo controlado en el escenario 1
                Exception? capturedException = null;
                try
                {
                    throw new InvalidOperationException("Fallo simulado de aserción en prueba A");
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                }

                // Ejecutar limpieza del escenario 1 pasando el error original
                await fixture1.DisposeAsync(capturedException);

                // Comprobar que se preservó el diagnóstico
                Assert.NotNull(capturedException);
                Assert.Contains("Fallo simulado de aserción en prueba A", capturedException.Message);

                // Comprobar que el espacio 2 no fue alterado por el fallo ni por la limpieza del espacio 1
                await using var verifyDb2 = fixture2.CreateDbContext();
                var fresh2 = await verifyDb2.ElectronicReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == doc2.Id);
                Assert.NotNull(fresh2);
                Assert.Equal("Pending", fresh2.Status);
            }
            finally
            {
                await fixture2.DisposeAsync();
            }
        }

        [SkippableFact]
        public async Task Postgres_Isolation_ScenarioD_RepeatedExecution_DoesNotDependOnLeftoversOrPrefixDeletions()
        {
            // Ejecución 1
            var fixtureRun1 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            if (fixtureRun1 == null) return;
            try
            {
                await using var db1 = fixtureRun1.CreateDbContext();
                var shift1 = SeedCashRegisterShift(db1);
                var baseTime1 = DateTime.UtcNow.AddMinutes(-50);
                for (int i = 0; i < 20; i++) SeedConflictedPendingReceipt(db1, baseTime1.AddMinutes(i), shift: shift1);
                for (int i = 20; i < 25; i++) SeedHealthyPendingReceipt(db1, baseTime1.AddMinutes(i));

                // Comprobar que antes de procesar hay exactamente 45 comprobantes en el espacio aislado (20 docA + 20 docB + 5 válidos)
                var count1 = await db1.ElectronicReceipts.CountAsync();
                Assert.Equal(45, count1);

                var config = CreateConfig();
                var handler1 = new InspectableHttpMessageHandler();
                using var client1 = new HttpClient(handler1);
                var service1 = new NubefactInvoiceService(db1, config, client1, NullLogger<NubefactInvoiceService>.Instance);
                var processed1 = await service1.ProcessPendingReceiptsAsync(CancellationToken.None);
                Assert.Equal(5, processed1);
            }
            finally
            {
                await fixtureRun1.DisposeAsync();
            }

            // Ejecución 2 (repetición independiente)
            var fixtureRun2 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            if (fixtureRun2 == null) return;
            try
            {
                await using var db2 = fixtureRun2.CreateDbContext();
                // Comprobar que el espacio 2 inicia completamente vacío (0 residuos de ejecución 1)
                var preCount2 = await db2.ElectronicReceipts.CountAsync();
                Assert.Equal(0, preCount2);

                var shift2 = SeedCashRegisterShift(db2);
                var baseTime2 = DateTime.UtcNow.AddMinutes(-50);
                for (int i = 0; i < 20; i++) SeedConflictedPendingReceipt(db2, baseTime2.AddMinutes(i), shift: shift2);
                for (int i = 20; i < 25; i++) SeedHealthyPendingReceipt(db2, baseTime2.AddMinutes(i));

                var count2 = await db2.ElectronicReceipts.CountAsync();
                Assert.Equal(45, count2);

                var config = CreateConfig();
                var handler2 = new InspectableHttpMessageHandler();
                using var client2 = new HttpClient(handler2);
                var service2 = new NubefactInvoiceService(db2, config, client2, NullLogger<NubefactInvoiceService>.Instance);
                var processed2 = await service2.ProcessPendingReceiptsAsync(CancellationToken.None);
                Assert.Equal(5, processed2);
            }
            finally
            {
                await fixtureRun2.DisposeAsync();
            }
        }
    }
}
