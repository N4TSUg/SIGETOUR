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
}
