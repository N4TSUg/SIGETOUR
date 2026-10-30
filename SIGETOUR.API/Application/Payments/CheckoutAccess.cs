using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace SIGETOUR.API.Application.Payments;

// El token se crea DEL LADO DEL SERVIDOR al crear la reserva; NO se genera en JavaScript.
public sealed class CheckoutAccess
{
    private readonly byte[] _key;
    public CheckoutAccess(IConfiguration config)
    {
        var value = config["Payments:CheckoutSigningKey"]
            ?? throw new InvalidOperationException("Falta Payments:CheckoutSigningKey");
        if (Encoding.UTF8.GetByteCount(value) < 32)
            throw new InvalidOperationException("CheckoutSigningKey debe tener >= 32 bytes aleatorios");
        _key = Encoding.UTF8.GetBytes(value);
    }

    public string Create(Guid bookingId, TimeSpan? ttl = null)
    {
        var expiry = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(30)).ToUnixTimeSeconds();
        var payload = $"{bookingId:N}.{expiry}";
        var signature = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload));
        return payload + "." + Convert.ToHexString(signature);
    }

    public bool Verify(Guid bookingId, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != bookingId.ToString("N")) return false;
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expiry)) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (expiry <= now || expiry > now + 7 * 24 * 60 * 60) return false;
        byte[] actual;
        try { actual = Convert.FromHexString(parts[2]); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]));
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
