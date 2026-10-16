using System;

namespace SIGETOUR.API.Core.Entities
{
    public class PettyCashMovement
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid AccountId { get; set; }
        public PettyCashAccount? Account { get; set; }

        public string MovementType { get; set; } = "Egreso"; // "Egreso" (Gasto) o "Reposicion" (Reembolso)
        public string ExpenseCategory { get; set; } = "Combustible"; // "Combustible", "Peajes", "Alimentación / Refrigerios", "Mantenimiento Menor", "Útiles y Limpieza", "Otros Gastos Menores", "Reposición de Fondo"
        public string VoucherType { get; set; } = "Boleta"; // "Boleta", "Factura", "Recibo por Honorarios", "Vale Provisional", "Declaración Jurada", "Recibo de Ingreso"
        public string? VoucherNumber { get; set; }
        public string? Supplier { get; set; }
        public decimal Amount { get; set; } = 0m;
        public string ResponsiblePerson { get; set; } = string.Empty;
        public string Concept { get; set; } = string.Empty;
        public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    }
}
