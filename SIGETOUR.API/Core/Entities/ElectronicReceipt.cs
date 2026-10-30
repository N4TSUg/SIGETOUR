using System;

namespace SIGETOUR.API.Core.Entities
{
    public class ElectronicReceipt
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid BookingId { get; set; }
        public Booking Booking { get; set; } = null!;
        public Guid? CulqiPaymentId { get; set; }
        public CulqiPayment? CulqiPayment { get; set; }

        public string ReceiptType { get; set; } = "Boleta"; // "Boleta", "Factura", "NotaCredito"
        public string Series { get; set; } = string.Empty; // e.g. "BBB1", "FFF1"
        public string Number { get; set; } = string.Empty; // e.g. "00000001"
        public string FullNumber => $"{Series}-{Number}";

        public string CustomerDocType { get; set; } = "1"; // "1" = DNI, "6" = RUC, "4" = CE, "0" = Sin doc
        public string CustomerDocNumber { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public string CustomerEmail { get; set; } = string.Empty;

        public decimal SubtotalAmount { get; set; }
        public decimal IgvAmount { get; set; }
        public decimal TotalAmount { get; set; }

        public string Status { get; set; } = "Pending"; // "Pending", "Submitted", "Accepted", "Rejected", "Cancelled"
        public string? ProviderResponseCode { get; set; }
        public string? ProviderDescription { get; set; }
        public string? PdfUrl { get; set; }
        public string? XmlUrl { get; set; }
        public string? CdrUrl { get; set; }
        public string? QrData { get; set; }
        public string? DigitalHash { get; set; }

        public string? LastError { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? IssuedAtUtc { get; set; }
    }
}
