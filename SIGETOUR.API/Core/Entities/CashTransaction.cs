using System;

namespace SIGETOUR.API.Core.Entities
{
    public class CashTransaction
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ShiftId { get; set; }
        public CashRegisterShift? Shift { get; set; }

        public string TransactionType { get; set; } = "Ingreso"; // "Ingreso" o "Egreso"
        public string Category { get; set; } = "Cobro de Reserva"; // "Cobro de Reserva", "Venta en Mostrador", "Ingreso Extraordinario", "Pago a Proveedor", "Retiro de Efectivo", "Devolución"
        public decimal Amount { get; set; } = 0m;
        public string PaymentMethod { get; set; } = "Efectivo"; // "Efectivo", "Yape", "Plin", "Transferencia BCP", "Transferencia BBVA", "Tarjeta POS", "Tarjeta Web"
        public string? ReferenceCode { get; set; }
        
        public Guid? BookingId { get; set; }
        public Booking? Booking { get; set; }

        public string Description { get; set; } = string.Empty;
        public string RegisteredBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
