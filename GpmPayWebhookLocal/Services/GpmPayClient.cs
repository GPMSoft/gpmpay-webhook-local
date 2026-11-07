using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GpmPayWebhookLocal.Services;

public sealed class GpmPayException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public bool IsAuthError => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

public sealed record WebhookSetting(string Id, string? Url);

public sealed record CreateWebhookRequest(
    string Scope, string Driver, string Url, string AuthorizationType, string AuthorizationSecret, bool FireOnSimulated);

public sealed record UpdateWebhookRequest(string Url, string AuthorizationType, string AuthorizationSecret);

/// <summary>Gọi REST API GPM Pay (https://api.gpmpay.com/api/v1), response bọc dạng { statusCode, message, data }.</summary>
public sealed class GpmPayClient : IDisposable
{
    private readonly HttpClient _http;

    public GpmPayClient(string apiToken)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.gpmpay.com/api/v1/"),
            Timeout = TimeSpan.FromSeconds(30), // tạo webhook phải chờ GPM Pay ping thử URL
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
    }

    public async Task<List<WebhookSetting>> ListWebhooksAsync(CancellationToken ct)
    {
        var result = new List<WebhookSetting>();
        for (int page = 1; page <= 20; page++)
        {
            var data = await SendAsync(HttpMethod.Get, $"webhook-settings?page={page}&limit=50", null, ct);
            JsonElement items;
            int totalPages = 1;
            if (data.ValueKind == JsonValueKind.Array) items = data;
            else if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Array)
            {
                items = inner;
                if (data.TryGetProperty("meta", out var meta) && meta.TryGetProperty("totalPages", out var tp) && tp.TryGetInt32(out var n))
                    totalPages = n;
            }
            else break;

            foreach (var it in items.EnumerateArray())
            {
                var id = GetString(it, "id");
                if (id != null) result.Add(new WebhookSetting(id, GetString(it, "url")));
            }
            if (page >= totalPages) break;
        }
        return result;
    }

    public async Task<string> CreateWebhookAsync(string url, string secret, CancellationToken ct)
    {
        var body = JsonContent.Create(
            new CreateWebhookRequest("ALL", "HTTP", url, "HMAC", secret, FireOnSimulated: true),
            ApiJson.Default.CreateWebhookRequest);
        var data = await SendAsync(HttpMethod.Post, "webhook-settings", body, ct);
        return GetString(data, "id") ?? throw new GpmPayException(HttpStatusCode.OK, "GPM Pay không trả về id webhook.");
    }

    public Task UpdateWebhookUrlAsync(string id, string url, string secret, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"webhook-settings/{Uri.EscapeDataString(id)}",
            JsonContent.Create(new UpdateWebhookRequest(url, "HMAC", secret), ApiJson.Default.UpdateWebhookRequest), ct);

    public Task DeleteWebhookAsync(string id, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"webhook-settings/{Uri.EscapeDataString(id)}", null, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, HttpContent? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Content = body;
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);

        JsonElement root = default;
        try { if (!string.IsNullOrWhiteSpace(text)) root = JsonDocument.Parse(text).RootElement; } catch { }

        if (!res.IsSuccessStatusCode)
        {
            var msg = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var m)
                ? (m.ValueKind == JsonValueKind.Array ? string.Join("; ", m.EnumerateArray().Select(x => x.ToString())) : m.ToString())
                : text;
            throw new GpmPayException(res.StatusCode, $"GPM Pay {(int)res.StatusCode}: {msg}");
        }

        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) ? d : root;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    public void Dispose() => _http.Dispose();
}
