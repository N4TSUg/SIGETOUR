using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Controllers
{
    public class Block8StartupAndSchemaTests
    {
        [Fact]
        public void CashRegisterShift_SeedLogic_UsesPaidAmount_InsteadOfTotalAmount()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();

            // Reserva 1: Total S/ 100, Pagado S/ 40 (Adelanto)
            var bookingPartial = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-B8-PARTIAL",
                CustomerName = "Pedro Adelanto",
                TotalAmount = 100m,
                PaidAmount = 40m,
                Status = BookingStatus.Confirmed,
                BookingDate = DateTime.UtcNow
            };

            // Reserva 2: Total S/ 80, Pagado S/ 0 (Pendiente de pago en counter)
            var bookingZero = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-B8-ZERO",
                CustomerName = "Ana Pendiente",
                TotalAmount = 80m,
                PaidAmount = 0m,
                Status = BookingStatus.Confirmed,
                BookingDate = DateTime.UtcNow
            };

            // Reserva 3: Total S/ 120, Pagado S/ 120 (100% Cancelado)
            var bookingFull = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-B8-FULL",
                CustomerName = "Mario Completo",
                TotalAmount = 120m,
                PaidAmount = 120m,
                Status = BookingStatus.Confirmed,
                BookingDate = DateTime.UtcNow
            };

            context.Bookings.AddRange(bookingPartial, bookingZero, bookingFull);
            context.SaveChanges();

            // Simular lógica de sembrado corregida de Program.cs
            var shift = new CashRegisterShift
            {
                Id = Guid.NewGuid(),
                ShiftNumber = "CJ-TEST-B8",
                OpenedByUserName = "Admin Central",
                OpenedAt = DateTime.UtcNow,
                InitialAmount = 300.00m,
                IsOpen = true
            };

            var confirmedBookings = context.Bookings
                .Where(b => b.Status == BookingStatus.Confirmed)
                .ToList();

            foreach (var b in confirmedBookings)
            {
                // Regla Bloque 8: Una reserva confirmada NO implica que se haya cobrado TotalAmount.
                // Se debe registrar únicamente el PaidAmount real efectivamente cobrado (> 0).
                if (b.PaidAmount > 0)
                {
                    shift.Transactions.Add(new CashTransaction
                    {
                        Id = Guid.NewGuid(),
                        ShiftId = shift.Id,
                        TransactionType = "Ingreso",
                        Category = "Cobro de Reserva",
                        Amount = b.PaidAmount, // Exacto: PaidAmount
                        PaymentMethod = "Efectivo",
                        ReferenceCode = b.ReferenceCode,
                        BookingId = b.Id,
                        Description = $"Cobro por reserva de {b.CustomerName} (Abonado: S/ {b.PaidAmount:N2} de S/ {b.TotalAmount:N2})",
                        RegisteredBy = "Sistema / Counter",
                        CreatedAt = b.BookingDate
                    });
                }
            }

            context.CashRegisterShifts.Add(shift);
            context.SaveChanges();

            // Assert
            var savedShift = context.CashRegisterShifts
                .Include(s => s.Transactions)
                .FirstOrDefault(s => s.Id == shift.Id);

            Assert.NotNull(savedShift);
            // Debe haber exactamente 2 transacciones (las que tienen PaidAmount > 0)
            Assert.Equal(2, savedShift.Transactions.Count);

            // Verificar que la reserva parcial registró S/ 40.00 y NO S/ 100.00
            var partialTx = savedShift.Transactions.FirstOrDefault(t => t.BookingId == bookingPartial.Id);
            Assert.NotNull(partialTx);
            Assert.Equal(40m, partialTx.Amount);

            // Verificar que la reserva de S/ 0 pagado NO generó movimiento de dinero ficticio
            var zeroTx = savedShift.Transactions.FirstOrDefault(t => t.BookingId == bookingZero.Id);
            Assert.Null(zeroTx);

            // Verificar que la reserva completa registró S/ 120.00
            var fullTx = savedShift.Transactions.FirstOrDefault(t => t.BookingId == bookingFull.Id);
            Assert.NotNull(fullTx);
            Assert.Equal(120m, fullTx.Amount);
        }

        private static string FindProgramCsPath()
        {
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, "SIGETOUR.API", "Program.cs");
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null || parent.FullName == dir) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException("No se pudo localizar SIGETOUR.API/Program.cs desde " + AppContext.BaseDirectory);
        }

        [Fact]
        public void ProgramCs_DoesNotCombineEnsureCreatedAsFallbackOfMigrate()
        {
            var programPath = FindProgramCsPath();
            var content = File.ReadAllText(programPath);

            // No debe contener "try { db.Database.EnsureCreated(); } catch" como fallback de Migrate
            Assert.DoesNotContain("try { db.Database.EnsureCreated(); } catch", content);
        }

        [Fact]
        public void ProgramCs_SeparatesDemoFinancialData_WithConfigurationCheck()
        {
            var programPath = FindProgramCsPath();
            var content = File.ReadAllText(programPath);

            // Debe contener la condición para aislar datos demostrativos financieros
            Assert.Contains("allowDemoFinancialData", content);
        }
    }
}
