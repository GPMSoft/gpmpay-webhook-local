using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace GpmPayWebhookLocal.Services;

/// <summary>
/// Kiểm tra URL tunnel có truy cập được từ Internet không.
/// Phân giải tên miền qua DNS-over-HTTPS (1.1.1.1) để tránh cache "không tìm thấy" của Windows
/// với hostname *.trycloudflare.com vừa tạo (cache này có thể làm chậm tới cả phút).
/// Tên miền chưa phân giải được → trả false ngay, không ném exception.
/// </summary>
public sealed class HealthChecker : IDisposable
{
    private readonly HttpClient _doh = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, IPAddress[]> _resolved = new(StringComparer.OrdinalIgnoreCase);

    public HealthChecker()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = ConnectAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        })
        { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<bool> CheckAsync(string url, CancellationToken ct)
    {
        var host = new Uri(url).Host;
        var addrs = await ResolveAsync(host, ct);
        if (addrs.Length == 0) return false; // tunnel mới tạo, DNS chưa có → chưa sẵn sàng
        _resolved[host] = addrs;

        try
        {
            using var res = await _http.GetAsync(url, ct);
            return res.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var (dohOk, addrs) = await TryResolveDohAsync(host, ct);
        if (dohOk) return addrs; // DoH trả lời (kể cả rỗng) → tin DoH, không hỏi DNS Windows

        // DoH bị chặn / lỗi mạng → dùng DNS hệ thống
        try { return await Dns.GetHostAddressesAsync(host, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return []; }
    }

    private async Task<(bool ok, IPAddress[] addrs)> TryResolveDohAsync(string host, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(host)}&type=A");
            req.Headers.Accept.ParseAdd("application/dns-json");
            using var res = await _doh.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return (false, []);

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("Answer", out var answers)) return (true, []);
            var list = answers.EnumerateArray()
                .Where(a => a.TryGetProperty("type", out var t) && t.GetInt32() == 1)
                .Select(a => IPAddress.TryParse(a.GetProperty("data").GetString(), out var ip) ? ip : null)
                .OfType<IPAddress>()
                .ToArray();
            return (true, list);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (false, []); }
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        if (!_resolved.TryGetValue(ctx.DnsEndPoint.Host, out var addrs) || addrs.Length == 0)
            addrs = await ResolveAsync(ctx.DnsEndPoint.Host, ct);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addrs, ctx.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _doh.Dispose();
    }
}
