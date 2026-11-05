namespace SIGETOUR.API.Application.Interfaces
{
    public interface ICompanyContactService
    {
        string WhatsAppNumber { get; }
        string WhatsAppSecondaryNumber { get; }
        string WhatsAppFormatted { get; }
        string WhatsAppSecondaryFormatted { get; }
        
        string PhoneCall { get; }
        string PhoneFormatted { get; }
        string PhoneCallFormatted { get; }
        
        string Email { get; }
        string EmailSecondary { get; }
        
        string Address { get; }
        
        string GetWhatsAppUrl(string? prefilledText = null, bool useSecondary = false);
    }
}
