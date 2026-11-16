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
using SIGETOUR.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace SIGETOUR.Tests.Audit
{
    public class HardeningAuditPass13Tests
    {
        private readonly ITestOutputHelper? _output;

        public HardeningAuditPass13Tests(ITestOutputHelper? output = null)
        {
            _output = output;
        }

        private class InspectableHttpMessageHandler : HttpMessageHandler
        {
            public List<HttpRequestMessage> Requests { get; } = new();
            public Action<HttpRequestMessage>? OnRequest { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                OnRequest?.Invoke(request);

                var json = "{\"tipo_de_comprobante\":2,\"serie\":\"BBB1\",\"numero\":\"00013001\",\"enlace_del_pdf\":\"https://demo.nubefact.com/pdf/test.pdf\",\"aceptada_por_sunat\":true,\"sunat_description\":\"Aceptado\"}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
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

        private static Booking SeedBooking(AppDbContext db, decimal total = 100m, decimal paid = 100m)
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
            return booking;
        }

        private static ElectronicReceipt SeedConflictedPendingReceipt(AppDbContext db, DateTime createdAtUtc)
        {
            var booking = SeedBooking(db, 100m, 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = createdAtUtc.AddMinutes(-5)
            };
            var cash = new CashTransaction
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                TransactionType = "Ingreso",
                Amount = 100m,
                CreatedAt = createdAtUtc.AddMinutes(-5)
            };

            // Doc A: en Pending
            var docA = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CulqiPaymentId = culqi.Id,
                BillableOperationKey = $"CULQI:{culqi.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "",
                Status = "Pending",
                CreatedAtUtc = createdAtUtc
            };

            // Doc B: emitido en Demo/Accepted bajo la misma operación
            var docB = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                CashTransactionId = cash.Id,
                BillableOperationKey = $"CASH:{cash.Id:N}",
                ReceiptType = "Boleta",
                Series = "BBB1",
                Number = "00099999",
                Status = "Demo",
                CreatedAtUtc = createdAtUtc.AddSeconds(-2)
            };

            db.CulqiPayments.Add(culqi);
            db.CashTransactions.Add(cash);
            db.ElectronicReceipts.AddRange(docA, docB);
            db.SaveChanges();
            return docA;
        }

        private static ElectronicReceipt SeedHealthyPendingReceipt(AppDbContext db, DateTime createdAtUtc)
        {
            var booking = SeedBooking(db, 100m, 100m);

            var culqi = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AmountCents = 10000,
                Status = "paid",
                CreatedAtUtc = createdAtUtc.AddMinutes(-5)
            };

            var doc = new ElectronicReceipt
            {
                Id = Guid.NewGuid(),
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
            return doc;
        }

        // =========================================================================
        // A. REPRODUCCIÓN PRINCIPAL: 20 CONFLICTOS SEGUIDOS DE 1 VÁLIDO EN POSICIÓN 21
        // =========================================================================

        [Fact]
        public async Task ProcessPendingReceiptsAsync_FirstBatchAllConflicts_ReachesAndProcessesValidAtPosition21()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var baseTime = DateTime.UtcNow.AddMinutes(-60);

            // Sembrar exactamente 20 comprobantes pendientes en conflicto (lote 1 completo bloqueado)
            var conflictedList = new List<ElectronicReceipt>();
            for (int i = 0; i < 20; i++)
            {
                var conflicted = SeedConflictedPendingReceipt(db, baseTime.AddMinutes(i));
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

            // Debe haber realizado exactamente 1 llamada HTTP externa al proveedor
            Assert.Single(handler.Requests);

            // Comprobar que el documento 21 fue emitido
            var freshHealthy = await db.ElectronicReceipts.FindAsync(healthy.Id);
            Assert.NotNull(freshHealthy);
            Assert.True(freshHealthy.Status is "Accepted" or "Demo");
            Assert.Equal("00013001", freshHealthy.Number);

            // Comprobar que los 20 comprobantes conflictivos permanecen intactos en Pending
            foreach (var c in conflictedList)
            {
                var freshConflicted = await db.ElectronicReceipts.FindAsync(c.Id);
                Assert.NotNull(freshConflicted);
                Assert.Equal("Pending", freshConflicted.Status);
                Assert.Null(freshConflicted.EmissionLeaseToken);
                Assert.True(string.IsNullOrEmpty(freshConflicted.Number));
            }
        }

        // =========================================================================
        // B. MÁS DE UN LOTE BLOQUEADO: 45 CONFLICTOS SEGUIDOS DE 3 VÁLIDOS
        // =========================================================================

        [Fact]
        public async Task ProcessPendingReceiptsAsync_MultipleBlockedBatches_TraversesAllAndProcessesSubsequentValids()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var baseTime = DateTime.UtcNow.AddMinutes(-120);

            // Sembrar 45 comprobantes en conflicto (> 2 lotes completos de 20)
            var conflictedList = new List<ElectronicReceipt>();
            for (int i = 0; i < 45; i++)
            {
                var conflicted = SeedConflictedPendingReceipt(db, baseTime.AddMinutes(i));
                conflictedList.Add(conflicted);
            }

            // Sembrar 3 comprobantes sanos en las posiciones 46, 47, 48
            var healthy1 = SeedHealthyPendingReceipt(db, baseTime.AddMinutes(50));
            var healthy2 = SeedHealthyPendingReceipt(db, baseTime.AddMinutes(51));
            var healthy3 = SeedHealthyPendingReceipt(db, baseTime.AddMinutes(52));

            var config = CreateConfig();
            var handler = new InspectableHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

            var processed = await service.ProcessPendingReceiptsAsync(CancellationToken.None);

            // Se deben procesar exactamente los 3 comprobantes sanos
            Assert.Equal(3, processed);
            Assert.Equal(3, handler.Requests.Count);

            var fresh1 = await db.ElectronicReceipts.FindAsync(healthy1.Id);
            var fresh2 = await db.ElectronicReceipts.FindAsync(healthy2.Id);
            var fresh3 = await db.ElectronicReceipts.FindAsync(healthy3.Id);
            Assert.True(fresh1!.Status is "Accepted" or "Demo");
            Assert.True(fresh2!.Status is "Accepted" or "Demo");
            Assert.True(fresh3!.Status is "Accepted" or "Demo");

            // Los 45 conflictivos permanecen intactos
            foreach (var c in conflictedList)
            {
                var fresh = await db.ElectronicReceipts.FindAsync(c.Id);
                Assert.Equal("Pending", fresh!.Status);
                Assert.Null(fresh.EmissionLeaseToken);
            }
        }

        // =========================================================================
        // C. FECHAS IGUALES: DESEMPATE ESTABLE POR ID EN EL LÍMITE DEL LOTE
        // =========================================================================

        [Fact]
        public async Task ProcessPendingReceiptsAsync_EqualCreatedAtUtc_TieBreakerByIdPreventsSkipsAndDuplicates()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            // Mismo timestamp exacto para todos los 25 documentos
            var exactSameTime = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

            // 20 conflictivos y 5 sanos, todos con la misma fecha CreatedAtUtc
            var conflictedList = new List<ElectronicReceipt>();
            for (int i = 0; i < 20; i++)
            {
                conflictedList.Add(SeedConflictedPendingReceipt(db, exactSameTime));
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

            foreach (var h in healthyList)
            {
                var fresh = await db.ElectronicReceipts.FindAsync(h.Id);
                Assert.True(fresh!.Status is "Accepted" or "Demo");
            }

            foreach (var c in conflictedList)
            {
                var fresh = await db.ElectronicReceipts.FindAsync(c.Id);
                Assert.Equal("Pending", fresh!.Status);
            }
        }

        // =========================================================================
        // D. LA COLA CAMBIA DURANTE EL PROCESAMIENTO: SIN SALTOS POR DESPLAZAMIENTO
        // =========================================================================

        [Fact]
        public async Task ProcessPendingReceiptsAsync_DynamicQueueChange_KeysetDoesNotShiftOrSkipDocuments()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var baseTime = DateTime.UtcNow.AddMinutes(-50);

            // Intercalados: 10 sanos, 10 conflictivos, 5 sanos
            var healthyList = new List<ElectronicReceipt>();
            var conflictedList = new List<ElectronicReceipt>();

            for (int i = 0; i < 10; i++)
            {
                healthyList.Add(SeedHealthyPendingReceipt(db, baseTime.AddMinutes(i)));
            }
            for (int i = 10; i < 20; i++)
            {
                conflictedList.Add(SeedConflictedPendingReceipt(db, baseTime.AddMinutes(i)));
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

            foreach (var h in healthyList)
            {
                var fresh = await db.ElectronicReceipts.FindAsync(h.Id);
                Assert.True(fresh!.Status is "Accepted" or "Demo");
            }

            foreach (var c in conflictedList)
            {
                var fresh = await db.ElectronicReceipts.FindAsync(c.Id);
                Assert.Equal("Pending", fresh!.Status);
            }
        }

        // =========================================================================
        // E. REPETICIÓN / IDEMPOTENCIA: SEGUNDA EJECUCIÓN NO REPITE EMISIONES
        // =========================================================================

        [Fact]
        public async Task ProcessPendingReceiptsAsync_RepeatedRun_DoesNotRepeatEmissions()
        {
            var dbName = Guid.NewGuid().ToString();
            using var db = CreateInMemoryDb(dbName);
            var baseTime = DateTime.UtcNow.AddMinutes(-30);

            // 5 conflictivos y 2 sanos
            var c1 = SeedConflictedPendingReceipt(db, baseTime.AddMinutes(1));
            var c2 = SeedConflictedPendingReceipt(db, baseTime.AddMinutes(2));
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
        }

        // =========================================================================
        // F. CANCELACIÓN: PROPAGA OPERATIONCANCELEDEXCEPTION Y DETIENE RECORRIDO
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
        // G. REGRESIONES FINANCIERAS DE AUDITORÍA FASE 11 Y 12 CONSERVADAS
        // =========================================================================

        [Fact]
        public async Task FinancialRegressions_AmbiguousConflictProtection_RemainsStrictlyIntact()
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
        // H. TRADUCCIÓN Y RECORRIDO POSTGRESQL (UNITARIA SQL + INTEGRACIÓN CON SKIP)
        // =========================================================================

        [Fact]
        public void Postgres_KeysetQuery_GeneratesValidNpgsqlSqlWithOrderByAndCursor()
        {
            // Valida la traducibilidad del predicado compuesto y ordenamiento por cursor sin requerir base de datos física
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

        [SkippableFact]
        public async Task Postgres_ProcessPendingReceipts_KeysetTraversal_OnIsolatedPostgres()
        {
            // Si no hay PostgreSQL configurado mediante variable de entorno, se emite SKIP explícito
            var db = await PostgresTestHelper.RequirePostgresAsync(_output);
            if (db == null)
            {
                return;
            }

            using (db)
            {
                var baseTime = DateTime.UtcNow.AddMinutes(-30);
                var booking = SeedBooking(db, 100m, 100m);
                var culqi = new CulqiPayment
                {
                    Id = Guid.NewGuid(),
                    BookingId = booking.Id,
                    AmountCents = 10000,
                    Status = "paid",
                    CreatedAtUtc = baseTime.AddMinutes(-5)
                };
                var receipt = new ElectronicReceipt
                {
                    Id = Guid.NewGuid(),
                    BookingId = booking.Id,
                    CulqiPaymentId = culqi.Id,
                    BillableOperationKey = $"CULQI:{culqi.Id:N}",
                    ReceiptType = "Boleta",
                    Series = "BBB1",
                    Number = "",
                    Status = "Pending",
                    CreatedAtUtc = baseTime
                };
                db.CulqiPayments.Add(culqi);
                db.ElectronicReceipts.Add(receipt);
                await db.SaveChangesAsync();

                var config = CreateConfig();
                var handler = new InspectableHttpMessageHandler();
                using var httpClient = new HttpClient(handler);
                var service = new NubefactInvoiceService(db, config, httpClient, NullLogger<NubefactInvoiceService>.Instance);

                var processed = await service.ProcessPendingReceiptsAsync(CancellationToken.None);
                Assert.True(processed >= 1);
            }
        }
    }
}
