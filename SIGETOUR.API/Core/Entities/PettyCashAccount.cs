using System;
using System.Collections.Generic;
using System.Linq;

namespace SIGETOUR.API.Core.Entities
{
    public class PettyCashAccount
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Caja Chica Principal - Oficina Cajamarca";
        public decimal TotalFundAmount { get; set; } = 4220.00m;
        public string ResponsiblePerson { get; set; } = "Administrador Central";
        public bool IsActive { get; set; } = true;
        public DateTime? LastReplenishedAt { get; set; }

        public ICollection<PettyCashMovement> Movements { get; set; } = new List<PettyCashMovement>();

        // Total histórico de egresos
        public decimal TotalSpentAllTime => Movements
            .Where(m => m.MovementType == "Egreso")
            .Sum(m => m.Amount);

        // Total histórico de reposiciones e ingresos
        public decimal TotalReplenishedAllTime => Movements
            .Where(m => m.MovementType == "Reposicion" || m.MovementType == "Ingreso")
            .Sum(m => m.Amount);

        // Gasto pendiente de reposición en el fondo actual (neto no reembolsado)
        public decimal PendingSpent => Math.Max(0m, TotalSpentAllTime - TotalReplenishedAllTime);

        // Lo que se muestra como "Gastado" en rojo en la interfaz es el gasto pendiente de reposición
        public decimal TotalSpent => PendingSpent;

        public decimal TotalReplenished => TotalReplenishedAllTime;

        // Saldo real en efectivo disponible en caja chica
        public decimal CurrentBalance => Math.Max(0m, TotalFundAmount - TotalSpentAllTime + TotalReplenishedAllTime);

        // Consumo del fondo asignado (% del fondo que está gastado y pendiente de reposición)
        public decimal ConsumptionPercentage => TotalFundAmount > 0 
            ? Math.Min(100m, Math.Round((PendingSpent / TotalFundAmount) * 100m, 1)) 
            : 0m;
    }
}
