using System;

namespace SIGETOUR.API.Models
{
    public sealed class BookingOperationOptionViewModel
    {
        public string Type { get; set; } = string.Empty;
        public Guid? ReceiptId { get; set; }
        public Guid? CashTransactionId { get; set; }
        public Guid? CulqiPaymentId { get; set; }
        public string Title { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool IsBilled { get; set; }
        public DateTime Date { get; set; }
        public string PaymentMethod { get; set; } = string.Empty;
        public string? ReferenceCode { get; set; }
    }
}
