using System;
using System.Collections.Generic;
using System.Linq;

namespace SIGETOUR.API.Core.Entities
{
    public class CashRegisterShift
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string ShiftNumber { get; set; } = string.Empty;
        public string OpenedByUserName { get; set; } = string.Empty;
        public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
        public decimal InitialAmount { get; set; } = 0m;
        
        public string? ClosedByUserName { get; set; }
        public DateTime? ClosedAt { get; set; }
        public bool IsOpen { get; set; } = true;
        public decimal? ActualCash { get; set; }
        public string? Notes { get; set; }

        public ICollection<CashTransaction> Transactions { get; set; } = new List<CashTransaction>();

        // Propiedades calculadas
        public decimal TotalCashIncome => Transactions
            .Where(t => t.TransactionType == "Ingreso" && t.PaymentMethod == "Efectivo")
            .Sum(t => t.Amount);

        public decimal TotalCashExpense => Transactions
            .Where(t => t.TransactionType == "Egreso" && t.PaymentMethod == "Efectivo")
            .Sum(t => t.Amount);

        public decimal TotalDigitalIncome => Transactions
            .Where(t => t.TransactionType == "Ingreso" && t.PaymentMethod != "Efectivo")
            .Sum(t => t.Amount);

        public decimal TotalDigitalExpense => Transactions
            .Where(t => t.TransactionType == "Egreso" && t.PaymentMethod != "Efectivo")
            .Sum(t => t.Amount);

        public decimal ExpectedCashBalance => InitialAmount + TotalCashIncome - TotalCashExpense;
        public decimal TotalIncome => TotalCashIncome + TotalDigitalIncome;
        public decimal TotalExpense => TotalCashExpense + TotalDigitalExpense;
        public decimal SystemBalance => InitialAmount + TotalIncome - TotalExpense;

        public decimal CashDifference => ActualCash.HasValue ? (ActualCash.Value - ExpectedCashBalance) : 0m;
    }
}
