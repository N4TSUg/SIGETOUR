using System;
using System.Text.RegularExpressions;

namespace SIGETOUR.API.Core.Common
{
    public class EmailValidationResult
    {
        public bool IsValid { get; set; }
        public string? NormalizedEmail { get; set; }
        public string? Domain { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public enum EmailFailureCategory
    {
        None,
        RecipientSyntax,
        ProviderDomainRestriction,
        AuthenticationError,
        NetworkOrTimeout,
        GeneralProviderError
    }

    public class EmailFailureClassification
    {
        public EmailFailureCategory Category { get; set; }
        public string UserMessage { get; set; } = string.Empty;
        public string DiagnosticMessage { get; set; } = string.Empty;
        public bool IsProviderDomainRestriction => Category == EmailFailureCategory.ProviderDomainRestriction;
    }

    /// <summary>
    /// Helper centralizado para validación sintáctica de correos electrónicos y clasificación
    /// de fallos en proveedores (distinguiendo validación interna vs. restricciones de dominio en sandbox/proveedores).
    /// </summary>
    public static class EmailValidationHelper
    {
        private static readonly Regex EmailRegex = new Regex(
            @"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Valida la sintaxis del correo destinatario dentro del sistema.
        /// El sistema admite libremente cualquier dominio público o privado válido (Gmail, Hotmail, Yahoo, corporativos, etc.).
        /// </summary>
        public static EmailValidationResult ValidateRecipientEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return new EmailValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "La dirección de correo electrónico es obligatoria."
                };
            }

            var trimmed = email.Trim();
            if (trimmed.Length > 254)
            {
                return new EmailValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "La dirección de correo excede la longitud máxima permitida (254 caracteres)."
                };
            }

            if (!EmailRegex.IsMatch(trimmed))
            {
                return new EmailValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "La dirección de correo electrónico no es válida."
                };
            }

            var atIndex = trimmed.LastIndexOf('@');
            var localPart = trimmed.Substring(0, atIndex);
            var domainPart = trimmed.Substring(atIndex + 1);

            if (localPart.Length > 64)
            {
                return new EmailValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "La parte local del correo electrónico excede los 64 caracteres permitidos."
                };
            }

            var dotIndex = domainPart.LastIndexOf('.');
            if (dotIndex == -1 || dotIndex == domainPart.Length - 1 || (domainPart.Length - dotIndex - 1) < 2)
            {
                return new EmailValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "El dominio del correo debe incluir una extensión válida de nivel superior (ej. .com, .pe)."
                };
            }

            return new EmailValidationResult
            {
                IsValid = true,
                NormalizedEmail = trimmed.ToLowerInvariant(),
                Domain = domainPart.ToLowerInvariant()
            };
        }

        /// <summary>
        /// Clasifica errores producidos por el proveedor de correo (Brevo, SendGrid, Resend o SMTP Relay).
        /// Permite identificar si el fallo se debe a restricciones de sandbox/dominios no verificados en el proveedor
        /// frente a fallos de autenticación, red o formato.
        /// </summary>
        public static EmailFailureClassification ClassifyProviderError(string? providerResponse, Exception? ex)
        {
            var combined = ((providerResponse ?? "") + " " + (ex?.Message ?? "") + " " + (ex?.InnerException?.Message ?? "")).ToLowerInvariant();

            // 1. Restricción de dominios del proveedor o limitaciones de Sandbox (Brevo, Resend, SendGrid, SMTP 550 Relaying Denied)
            if (combined.Contains("domain is not allowed") ||
                combined.Contains("domain not verified") ||
                combined.Contains("unverified domain") ||
                combined.Contains("sender domain not verified") ||
                combined.Contains("sender not allowed") ||
                combined.Contains("you can only send testing emails to your own email address") ||
                (combined.Contains("sandbox") && (combined.Contains("domain") || combined.Contains("recipient") || combined.Contains("verified"))) ||
                combined.Contains("relaying denied") ||
                combined.Contains("5.7.1") ||
                combined.Contains("5.7.26") ||
                combined.Contains("spf") ||
                combined.Contains("dkim") ||
                combined.Contains("not allowed to send") ||
                combined.Contains("sender identity"))
            {
                return new EmailFailureClassification
                {
                    Category = EmailFailureCategory.ProviderDomainRestriction,
                    DiagnosticMessage = $"[EMAIL_PROVIDER_DOMAIN_RESTRICTION] El proveedor de correo rechazó el mensaje debido a restricciones de dominio o modo sandbox de la cuenta: {combined.Trim()}",
                    UserMessage = "El proveedor de correo está en modo sandbox o restringido a dominios verificados. Para enviar a cualquier cliente, verifique el dominio del remitente en el panel del proveedor (Brevo/SendGrid/Gmail)."
                };
            }

            // 2. Tiempo de espera agotado o puertos bloqueados en el hosting (ej. Render Free bloquea 587/465)
            if (combined.Contains("timed out") ||
                combined.Contains("timeout") ||
                combined.Contains("connection refused") ||
                combined.Contains("no se puede establecer una conexión"))
            {
                return new EmailFailureClassification
                {
                    Category = EmailFailureCategory.NetworkOrTimeout,
                    DiagnosticMessage = $"[EMAIL_NETWORK_TIMEOUT] Tiempo de espera agotado al conectar con el servidor de correo: {combined.Trim()}",
                    UserMessage = "Tiempo de espera agotado al conectar con el servidor de correo. Si está desplegado en un plan gratuito en la nube (ej. Render Free), los puertos SMTP (587/465) pueden estar bloqueados; use la API HTTP de Brevo."
                };
            }

            // 3. Error de credenciales o autenticación
            if (combined.Contains("authentication failed") ||
                combined.Contains("bad credentials") ||
                combined.Contains("invalid api key") ||
                combined.Contains("unauthorized") ||
                combined.Contains("535") ||
                combined.Contains("401"))
            {
                return new EmailFailureClassification
                {
                    Category = EmailFailureCategory.AuthenticationError,
                    DiagnosticMessage = $"[EMAIL_AUTH_ERROR] Error de autenticación en el proveedor de correo: {combined.Trim()}",
                    UserMessage = "Error de autenticación con el servicio de correo. Verifique las credenciales configuradas (SMTP_PASSWORD o BREVO_API_KEY)."
                };
            }

            return new EmailFailureClassification
            {
                Category = EmailFailureCategory.GeneralProviderError,
                DiagnosticMessage = $"[EMAIL_GENERAL_ERROR] Error al procesar el envío de correo: {combined.Trim()}",
                UserMessage = "No se pudo completar el envío del correo de confirmación. Puede intentar el reenvío desde la administración de reservas."
            };
        }
    }
}
