using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SIGETOUR.API.Application.Payments;

public sealed class CulqiApiClient
{
    private readonly HttpClient _http;
    private readonly string _secret;
    public CulqiApiClient(HttpClient http, IConfiguration config)
    {
        _http = http;
        _http.BaseAddress = new Uri("https://api.culqi.com/v2/");
        _http.Timeout = TimeSpan.FromSeconds(20);
        _secret = config["Culqi:SecretKey"] ?? throw new InvalidOperationException("Falta Culqi:SecretKey");
        if (!_secret.StartsWith("sk_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("Este módulo de laboratorio solo acepta sk_test_. No activar producción aún.");
    }

    public async Task<CulqiResult> PostAsync(string route, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, route);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);
        req.Content = JsonContent.Create(body);
        using var res = await _http.SendAsync(req, ct);
        return await ParseResponse(res, ct);
    }
    public async Task<CulqiResult> GetAsync(string route, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, route);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);
        using var res = await _http.SendAsync(req, ct);
        return await ParseResponse(res, ct);
    }
    private static async Task<CulqiResult> ParseResponse(HttpResponseMessage res, CancellationToken ct)
    {
        var json = await res.Content.ReadAsStringAsync(ct);
        try
        {
            return new CulqiResult(res.IsSuccessStatusCode, (int)res.StatusCode, JsonDocument.Parse(json));
        }
        catch (JsonException)
        {
            return new CulqiResult(false, (int)res.StatusCode, null);
        }
    }
}

public sealed record CulqiResult(bool Success, int HttpStatus, JsonDocument? Body) : IDisposable
{
    public void Dispose() => Body?.Dispose();
    public string? Text(string field)
    {
        if (Body is null || !Body.RootElement.TryGetProperty(field, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
    }
    public long? Number(string field)
    {
        if (Body is null || !Body.RootElement.TryGetProperty(field, out var prop)) return null;
        return prop.TryGetInt64(out var value) ? value : null;
    }

    public string? GetDeclineOrErrorCode()
    {
        if (Body is null) return null;
        var root = Body.RootElement;
        if (root.TryGetProperty("decline_code", out var dc)) return dc.GetString();
        if (root.TryGetProperty("code", out var c)) return c.GetString();
        if (root.TryGetProperty("outcome", out var outcome) && outcome.TryGetProperty("code", out var oc)) return oc.GetString();
        if (root.TryGetProperty("error", out var err))
        {
            if (err.TryGetProperty("decline_code", out var edc)) return edc.GetString();
            if (err.TryGetProperty("code", out var ec)) return ec.GetString();
        }
        return null;
    }

    public string? GetErrorOrUserMessage(string? sourceId = null)
    {
        if (Body is null) return null;
        var root = Body.RootElement;

        // 1. Mensaje de usuario en raíz
        if (root.TryGetProperty("user_message", out var um) && !string.IsNullOrWhiteSpace(um.GetString()))
            return um.GetString();

        // 2. Anidado en outcome
        if (root.TryGetProperty("outcome", out var outcome))
        {
            if (outcome.TryGetProperty("user_message", out var oum) && !string.IsNullOrWhiteSpace(oum.GetString()))
                return oum.GetString();
            if (outcome.TryGetProperty("merchant_message", out var omm) && !string.IsNullOrWhiteSpace(omm.GetString()))
                return omm.GetString();
        }

        // 3. Anidado en error
        if (root.TryGetProperty("error", out var err))
        {
            if (err.TryGetProperty("user_message", out var eum) && !string.IsNullOrWhiteSpace(eum.GetString()))
                return eum.GetString();
            if (err.TryGetProperty("merchant_message", out var emm) && !string.IsNullOrWhiteSpace(emm.GetString()))
                return emm.GetString();
        }

        if (root.TryGetProperty("merchant_message", out var mm) && !string.IsNullOrWhiteSpace(mm.GetString()))
            return mm.GetString();

        // 4. Códigos específicos de Yape (código de aprobación inválido o expirado)
        var code = GetDeclineOrErrorCode();
        if (code is "AUT0014" or "AUT0007")
        {
            return "Código de aprobación de Yape inválido o expirado. Genera un nuevo código en tu app Yape.";
        }

        bool isYape = sourceId != null && (sourceId.StartsWith("ype_") || sourceId.Contains("yape", StringComparison.OrdinalIgnoreCase));
        return isYape
            ? "No se pudo procesar el pago con Yape. Por favor verifica tu código de aprobación o saldo en la app Yape."
            : "Tarjeta rechazada por la entidad bancaria";
    }
}
