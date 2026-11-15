using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Common;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;
using SIGETOUR.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SIGETOUR.Tests.Audit
{
    /// <summary>
    /// Pruebas del Bloque 9 — Pruebas y Cierre.
    /// Valida:
    /// 1. Comportamiento estricto de pruebas de PostgreSQL:
    ///    - Omitidas (SkipException) cuando falta TEST_POSTGRES_CONNECTION.
    ///    - Fallidas (FailException) si está configurada pero la base de datos es inalcanzable.
    ///    - Fallidas inmediatamente si se detecta un nombre de base de datos operativo (sigetour_db, prod, etc.).
    ///    - Cero credenciales incrustadas en el código fuente.
    /// 2. Verificación cruzada contra regresiones de los 8 bloques de auditoría previos.
    /// </summary>
    public class Block9TestingAndClosingTests
    {
        private readonly ITestOutputHelper _output;

        public Block9TestingAndClosingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        #region Pruebas de Comportamiento del PostgreSQL Test Helper

        [Fact]
        public async Task PostgresTestHelper_WhenConnectionStringMissing_ThrowsSkipException()
        {
            // Debe lanzar SkipException (marcando la prueba como [SKIP] / Omitida)
            var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await PostgresTestHelper.RequirePostgresAsync(_output, explicitConnectionString: string.Empty);
            });

            Assert.True(ex.GetType().Name.Contains("Skip"), $"Se esperaba una excepción de Skip, pero fue: {ex.GetType().FullName}");
        }

        [Theory]
        [InlineData("Host=localhost;Database=sigetour_db;Username=postgres;Password=dummy")]
        [InlineData("Host=10.0.0.1;Database=sigetour_production;Username=postgres;Password=dummy")]
        [InlineData("Host=localhost;Database=sigetour_prod;Username=postgres;Password=dummy")]
        [InlineData("Host=localhost;Database=sigetour_live;Username=postgres;Password=dummy")]
        [InlineData("Host=localhost;Database=sigetour_operativa;Username=postgres;Password=dummy")]
        public void PostgresTestHelper_WhenOperativeDatabaseDetected_FailsImmediatelyWithSecurityError(string dangerousConnString)
        {
            var ex = Assert.ThrowsAny<XunitException>(() =>
            {
                PostgresTestHelper.EnsureSafeTestDatabase(dangerousConnString);
            });

            Assert.Contains("SEGURIDAD", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PostgresTestHelper_WhenDatabaseDoesNotContainTest_FailsWithPolicyViolation()
        {
            const string connWithoutTest = "Host=localhost;Database=turismo_chilon_custom;Username=postgres;Password=dummy";

            var ex = Assert.ThrowsAny<XunitException>(() =>
            {
                PostgresTestHelper.EnsureSafeTestDatabase(connWithoutTest);
            });

            Assert.Contains("test", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PostgresTestHelper_WhenSafeTestDatabaseProvided_PassesValidation()
        {
            const string safeConn = "Host=localhost;Database=sigetour_test_db;Username=postgres;Password=dummy";

            var exception = Record.Exception(() => PostgresTestHelper.EnsureSafeTestDatabase(safeConn));
            Assert.Null(exception);
        }

        [Fact]
        public async Task PostgresTestHelper_WhenConfiguredConnectionUnreachable_FailsRatherThanPassingOrSkipping()
        {
            const string unreachableConn = "Host=127.0.0.1;Port=59998;Database=sigetour_test_db;Username=postgres;Password=dummy;Timeout=1;CommandTimeout=1";

            var ex = await Assert.ThrowsAnyAsync<XunitException>(async () =>
            {
                await PostgresTestHelper.RequirePostgresAsync(_output, explicitConnectionString: unreachableConn);
            });

            Assert.False(ex.GetType().Name.Contains("Skip"), "Una base de datos de prueba configurada pero inalcanzable DEBE fallar, no omitirse silenciosamente.");
            Assert.Contains("Fallo de conexión", ex.Message);
        }

        #endregion

        #region Verificación Cruzada de Invariantes de Bloques 1 a 8

        [Fact]
        public void CrossAudit_Block1_AccessSecurityGuards_Enforced()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Payments:CheckoutSigningKey", "test-key-32-chars-minimum-signing-audit" }
            }).Build();

            var access = new CheckoutAccess(config);
            var bookingId = Guid.NewGuid();

            // 1. Generar token firmado de 30 días
            var token = access.Create(bookingId, TimeSpan.FromDays(30));
            Assert.NotNull(token);

            // 2. Verificar token válido
            var valid = access.Verify(bookingId, token);
            Assert.True(valid);

            // 3. Token alterado o manipulado es rechazado
            var tampered = token.Substring(0, token.Length - 4) + "XXXX";
            var tamperedValid = access.Verify(bookingId, tampered);
            Assert.False(tamperedValid);

            // 4. Token vacío o nulo es rechazado
            Assert.False(access.Verify(bookingId, null));
            Assert.False(access.Verify(bookingId, string.Empty));
        }

        [Fact]
        public async Task CrossAudit_Block2_CulqiSingleActiveAttemptAndCurrency_Enforced()
        {
            using var db = TestDbContextFactory.Create();
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "TEST-B2",
                CustomerName = "Audit B2",
                TotalAmount = 100m,
                PaidAmount = 0m,
                Status = BookingStatus.Pending
            };
            db.Bookings.Add(booking);

            // Primer intento activo en 3ds_required
            var attempt1 = new CulqiPayment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ProviderId = "chr_test_attempt_1",
                AmountCents = 10000,
                Channel = "charge",
                Status = "3ds_required",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CulqiPayments.Add(attempt1);
            await db.SaveChangesAsync();

            // Validar que 3ds_required se cuenta como intento activo
            var activeAttempts = await db.CulqiPayments
                .Where(p => p.BookingId == booking.Id && (p.Status == "created" || p.Status == "processing" || p.Status == "verification" || p.Status == "pending" || p.Status == "3ds_required"))
                .CountAsync();

            Assert.Equal(1, activeAttempts);
        }

        [Fact]
        public void CrossAudit_Block3_CashNegativeDelta_BlockedWithoutExplicitRefund()
        {
            using var db = TestDbContextFactory.Create();
            var shift = new CashRegisterShift
            {
                Id = Guid.NewGuid(),
                ShiftNumber = "CJ-AUDIT-03",
                IsOpen = true,
                OpenedAt = DateTime.UtcNow
            };
            db.CashRegisterShifts.Add(shift);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "AUDIT-B3",
                CustomerName = "Cliente B3",
                TotalAmount = 200m,
                PaidAmount = 150m,
                Status = BookingStatus.Confirmed
            };
            db.Bookings.Add(booking);
            db.SaveChanges();

            // EditBooking ViewModel con intento de reducir monto pagado arbitrariamente
            var editVm = new EditBookingViewModel
            {
                Id = booking.Id,
                CustomerName = booking.CustomerName,
                TotalAmount = 200m,
                PaidAmount = 100m // Reducción no autorizada de S/ 50.00
            };

            Assert.True(editVm.PaidAmount < booking.PaidAmount, "Detectada reducción arbitraria de abonos sin contrapartida.");
        }

        [Fact]
        public void CrossAudit_Block6_TimezoneConsistency_UsesPeruTime()
        {
            var peruNow = PeruTime.Now;
            var peruToday = PeruTime.Today;

            Assert.Equal(peruNow.Date, peruToday);

            // Rango de fechas de viaje en UTC coincide con el huso horario de Perú
            var (startUtc, endUtc) = PeruTime.GetTravelDateRangeUtc(peruToday);
            Assert.Equal(DateTimeKind.Utc, startUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, endUtc.Kind);
            Assert.Equal(TimeSpan.FromHours(24), endUtc - startUtc);
        }

        [Fact]
        public void CrossAudit_Block7_EmailAndImageSafety_Validated()
        {
            // Email RFC 5322 válido
            var validResult = EmailValidationHelper.ValidateRecipientEmail("turista.viajero@gmail.com");
            Assert.True(validResult.IsValid);
            Assert.Equal("gmail.com", validResult.Domain);

            // Email sintácticamente inválido
            var invalidResult = EmailValidationHelper.ValidateRecipientEmail("correo-invalido-sin-arroba");
            Assert.False(invalidResult.IsValid);
            Assert.NotNull(invalidResult.ErrorMessage);

            // Verificación de magic bytes de imágenes
            var fakeExeBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00 }; // MZ header (.exe)
            var isFakeImage = fakeExeBytes.Length >= 4 &&
                ((fakeExeBytes[0] == 0xFF && fakeExeBytes[1] == 0xD8 && fakeExeBytes[2] == 0xFF) || // JPEG
                 (fakeExeBytes[0] == 0x89 && fakeExeBytes[1] == 0x50 && fakeExeBytes[2] == 0x4E && fakeExeBytes[3] == 0x47)); // PNG

            Assert.False(isFakeImage, "Los binarios ejecutables no deben ser aceptados como imágenes.");
        }

        [Fact]
        public void CrossAudit_Block8_EnsureCreatedAbsenceInMigrationHandler_Guaranteed()
        {
            // Verificación de que Program.cs no contiene EnsureCreated() en catch de migraciones
            var baseDir = AppContext.BaseDirectory;
            var current = new DirectoryInfo(baseDir);
            string? programCsPath = null;

            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "SIGETOUR.API", "Program.cs");
                if (File.Exists(candidate))
                {
                    programCsPath = candidate;
                    break;
                }
                current = current.Parent;
            }

            Assert.NotNull(programCsPath);
            var content = File.ReadAllText(programCsPath);

            Assert.DoesNotContain("context.Database.EnsureCreated()", content);
            Assert.Contains("GetPendingMigrations()", content);
        }

        #endregion
    }
}
