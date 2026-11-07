using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GpmPayWebhookLocal.Services;

public sealed class WebhookPayload
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("gateway")] public string? Gateway { get; set; }
    [JsonPropertyName("counterName")] public string? CounterName { get; set; }
    [JsonPropertyName("transferType")] public string? TransferType { get; set; }
    [JsonPropertyName("accountNumber")] public string? AccountNumber { get; set; }
    [JsonPropertyName("referenceCode")] public string? ReferenceCode { get; set; }
    [JsonPropertyName("transferAmount")] public decimal TransferAmount { get; set; }
    [JsonPropertyName("transactionDate")] public DateTimeOffset? TransactionDate { get; set; }
    [JsonPropertyName("test")] public bool Test { get; set; }

    [JsonIgnore] public bool IsIncoming => string.Equals(TransferType, "in", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsSimulated => string.Equals(Source, "SIMULATED", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// HTTP server nội bộ (Kestrel, chỉ lắng nghe localhost) nhận webhook từ GPM Pay qua Cloudflare tunnel.
/// Không dùng Minimal API / routing (dựa reflection) mà xử lý thẳng bằng RequestDelegate → an toàn với Native AOT.
/// </summary>
public sealed class WebhookServer(Func<string> secretProvider, Action<string> log) : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly HashSet<string> _seenIds = [];
    private readonly Queue<string> _seenOrder = new();

    public event Action<WebhookPayload>? TransactionReceived;

    public async Task StartAsync(int port, string webhookPath)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(port));

        var app = builder.Build();
        var healthPath = webhookPath + "/health";
        app.Run(async ctx =>
        {
            var path = (ctx.Request.Path.Value ?? "").TrimEnd('/');
            if (HttpMethods.IsGet(ctx.Request.Method) && path.Equals(healthPath, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync("ok");
            }
            else if (HttpMethods.IsPost(ctx.Request.Method) && path.Equals(webhookPath, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = await HandleAsync(ctx);
            }
            else
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });

        await app.StartAsync();
        _app = app;
        log($"HTTP server đang lắng nghe tại http://127.0.0.1:{port}{webhookPath}");
    }

    private async Task<int> HandleAsync(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var raw = ms.ToArray();

        if (!VerifySignature(raw, ctx.Request.Headers["X-GPMPay-Signature"].ToString(), secretProvider(), out var reason))
        {
            log($"Từ chối webhook: {reason}");
            return StatusCodes.Status401Unauthorized;
        }

        WebhookPayload? payload;
        try { payload = JsonSerializer.Deserialize(raw, AppJson.Default.WebhookPayload); }
        catch (Exception ex)
        {
            log($"Webhook JSON không hợp lệ: {ex.Message}");
            return StatusCodes.Status400BadRequest;
        }
        if (payload == null) return StatusCodes.Status400BadRequest;

        if (payload.Test)
        {
            log("Nhận gói tin xác minh từ GPM Pay (test) → trả 200.");
            return StatusCodes.Status200OK;
        }

        // GPM Pay có thể retry → chống xử lý trùng theo id
        if (!string.IsNullOrEmpty(payload.Id))
        {
            lock (_seenIds)
            {
                if (!_seenIds.Add(payload.Id)) return StatusCodes.Status200OK;
                _seenOrder.Enqueue(payload.Id);
                if (_seenOrder.Count > 1000) _seenIds.Remove(_seenOrder.Dequeue());
            }
        }

        TransactionReceived?.Invoke(payload);
        return StatusCodes.Status200OK;
    }

    /// <summary>X-GPMPay-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt; ; v1 = HMAC_SHA256(secret, "{t}.{rawBody}"), lệch thời gian ≤ 300s.</summary>
    private static bool VerifySignature(byte[] rawBody, string header, string secret, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(header)) { reason = "thiếu header X-GPMPay-Signature"; return false; }

        string? t = null, v1 = null;
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "t") t = kv[1].Trim();
            else if (kv[0].Trim() == "v1") v1 = kv[1].Trim();
        }
        if (t == null || v1 == null || !long.TryParse(t, out var ts)) { reason = "header chữ ký sai định dạng"; return false; }

        var skew = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts);
        if (skew > 300) { reason = $"chữ ký lệch thời gian {skew}s (kiểm tra đồng hồ máy tính)"; return false; }

        byte[] given;
        try { given = Convert.FromHexString(v1); } catch { reason = "v1 không phải hex"; return false; }

        var prefix = Encoding.UTF8.GetBytes(t + ".");
        var data = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(data, 0);
        rawBody.CopyTo(data, prefix.Length);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data);

        if (!CryptographicOperations.FixedTimeEquals(expected, given)) { reason = "chữ ký HMAC không khớp"; return false; }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app == null) return;
        try { await _app.StopAsync(); } catch { }
        await _app.DisposeAsync();
        _app = null;
    }
}
