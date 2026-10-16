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

        // Propiedades calculadas
        public decimal TotalSpent => Movements
            .Where(m => m.MovementType == "Egreso")
            .Sum(m => m.Amount);

        public decimal TotalReplenished => Movements
            .Where(m => m.MovementType == "Reposicion")
            .Sum(m => m.Amount);

        public decimal CurrentBalance => Math.Max(0m, TotalFundAmount - TotalSpent + TotalReplenished);

        public decimal ConsumptionPercentage => TotalFundAmount > 0 
            ? Math.Min(100m, Math.Round((TotalSpent / TotalFundAmount) * 100m, 1)) 
            : 0m;
    }
}
