using Microsoft.Extensions.Configuration;
using SIGETOUR.API.Application.Interfaces;
using System;
using System.Text.RegularExpressions;

namespace SIGETOUR.API.Application.Services
{
    public class CompanyContactService : ICompanyContactService
    {
        private readonly IConfiguration _config;

        public CompanyContactService(IConfiguration config)
        {
            _config = config;
        }

        public string WhatsAppNumber
        {
            get
            {
                var val = Environment.GetEnvironmentVariable("SIGETOUR_CONTACT_WHATSAPP")
                       ?? Environment.GetEnvironmentVariable("CONTACT_WHATSAPP")
                       ?? _config["SIGETOUR_CONTACT_WHATSAPP"]
                       ?? _config["CONTACT_WHATSAPP"]
                       ?? Environment.GetEnvironmentVariable("Company:WhatsApp")
                       ?? Environment.GetEnvironmentVariable("Company__WhatsApp")
                       ?? _config["Company:WhatsApp"]
                       ?? _config["Company__WhatsApp"]
                       ?? "928876259";
                return NormalizeDigits(val);
            }
        }

        public string WhatsAppSecondaryNumber
        {
            get
            {
                var val = Environment.GetEnvironmentVariable("SIGETOUR_CONTACT_WHATSAPP_SECONDARY")
                       ?? Environment.GetEnvironmentVariable("CONTACT_WHATSAPP_SECONDARY")
                       ?? _config["SIGETOUR_CONTACT_WHATSAPP_SECONDARY"]
                       ?? _config["CONTACT_WHATSAPP_SECONDARY"]
                       ?? Environment.GetEnvironmentVariable("Company:WhatsAppSecondary")
                       ?? Environment.GetEnvironmentVariable("Company__WhatsAppSecondary")
                       ?? _config["Company:WhatsAppSecondary"]
                       ?? _config["Company__WhatsAppSecondary"]
                       ?? "944746364";
                return NormalizeDigits(val);
            }
        }

        public string WhatsAppFormatted => FormatPeruvianPhone(WhatsAppNumber);

        public string WhatsAppSecondaryFormatted => FormatPeruvianPhone(WhatsAppSecondaryNumber);

        public string PhoneCall
        {
            get
            {
                var val = Environment.GetEnvironmentVariable("SIGETOUR_CONTACT_PHONE")
                       ?? Environment.GetEnvironmentVariable("CONTACT_PHONE")
                       ?? _config["SIGETOUR_CONTACT_PHONE"]
                       ?? _config["CONTACT_PHONE"]
                       ?? Environment.GetEnvironmentVariable("Company:PhoneCall")
                       ?? Environment.GetEnvironmentVariable("Company__PhoneCall")
                       ?? _config["Company:PhoneCall"]
                       ?? _config["Company__PhoneCall"]
                       ?? WhatsAppSecondaryNumber;
                return "+" + NormalizeDigits(val);
            }
        }

        public string PhoneFormatted => FormatPeruvianPhone(PhoneCall);

        public string PhoneCallFormatted => PhoneFormatted;

        public string Email
        {
            get
            {
                return Environment.GetEnvironmentVariable("SIGETOUR_CONTACT_EMAIL")
                    ?? Environment.GetEnvironmentVariable("CONTACT_EMAIL")
                    ?? _config["SIGETOUR_CONTACT_EMAIL"]
                    ?? _config["CONTACT_EMAIL"]
                    ?? Environment.GetEnvironmentVariable("Company:Email")
                    ?? Environment.GetEnvironmentVariable("Company__Email")
                    ?? _config["Company:Email"]
                    ?? _config["Company__Email"]
                    ?? "informes@turismochilon.com";
            }
        }

        public string EmailSecondary
        {
            get
            {
                return Environment.GetEnvironmentVariable("SIGETOUR_CONTACT_EMAIL_SECONDARY")
                    ?? Environment.GetEnvironmentVariable("CONTACT_EMAIL_SECONDARY")
                    ?? _config["SIGETOUR_CONTACT_EMAIL_SECONDARY"]
                    ?? _config["CONTACT_EMAIL_SECONDARY"]
                    ?? Environment.GetEnvironmentVariable("Company:EmailSecondary")
                    ?? Environment.GetEnvironmentVariable("Company__EmailSecondary")
                    ?? _config["Company:EmailSecondary"]
                    ?? _config["Company__EmailSecondary"]
                    ?? "informes.chilon@gmail.com";
            }
        }

        public string Address
        {
            get
            {
                return Environment.GetEnvironmentVariable("SIGETOUR_CONTACT_ADDRESS")
                    ?? Environment.GetEnvironmentVariable("CONTACT_ADDRESS")
                    ?? _config["SIGETOUR_CONTACT_ADDRESS"]
                    ?? _config["CONTACT_ADDRESS"]
                    ?? Environment.GetEnvironmentVariable("Company:FiscalAddress")
                    ?? Environment.GetEnvironmentVariable("Company__FiscalAddress")
                    ?? _config["Company:FiscalAddress"]
                    ?? _config["Company__FiscalAddress"]
                    ?? "Jr. 2 de Mayo 569, Cajamarca - Perú";
            }
        }

        public string GetWhatsAppUrl(string? prefilledText = null, bool useSecondary = false)
        {
            var number = useSecondary ? WhatsAppSecondaryNumber : WhatsAppNumber;
            if (string.IsNullOrWhiteSpace(prefilledText))
            {
                return $"https://wa.me/{number}";
            }
            return $"https://wa.me/{number}?text={Uri.EscapeDataString(prefilledText)}";
        }

        public static string NormalizeDigits(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "51928876259";
            var digits = Regex.Replace(raw, @"\D", "");
            if (digits.Length == 9 && digits.StartsWith("9"))
            {
                digits = "51" + digits;
            }
            return digits;
        }

        public static string FormatPeruvianPhone(string raw)
        {
            var digits = NormalizeDigits(raw);
            if (digits.StartsWith("51") && digits.Length == 11)
            {
                return $"+51 {digits.Substring(2, 3)} {digits.Substring(5, 3)} {digits.Substring(8, 3)}";
            }
            if (digits.Length == 9)
            {
                return $"+51 {digits.Substring(0, 3)} {digits.Substring(3, 3)} {digits.Substring(6, 3)}";
            }
            return "+" + digits;
        }
    }
}
