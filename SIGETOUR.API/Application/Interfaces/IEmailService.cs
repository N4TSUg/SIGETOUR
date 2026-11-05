using SIGETOUR.API.Core.Entities;
using System;
using System.Collections.Generic;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace SIGETOUR.API.Application.Interfaces
{
    public interface IEmailService
    {
        string SenderEmail { get; }
        string SenderName { get; }

        Task<(bool Success, string Message)> SendEmailAsync(
            string toEmail,
            string subject,
            string htmlBody,
            string? textBody = null,
            IEnumerable<Attachment>? attachments = null,
            CancellationToken cancellationToken = default);

        Task<(bool Success, string Message, int AttachedCount)> SendBookingConfirmationEmailAsync(
            Booking booking,
            string? baseUrl = null,
            bool attachVoucherPdf = true,
            bool attachReceiptPdf = true,
            CancellationToken cancellationToken = default);
    }
}
