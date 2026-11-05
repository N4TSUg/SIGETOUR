using SIGETOUR.API.Core.Entities;
using System;
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
            CancellationToken cancellationToken = default);

        Task<(bool Success, string Message)> SendBookingConfirmationEmailAsync(
            Booking booking,
            string? baseUrl = null,
            CancellationToken cancellationToken = default);
    }
}
