namespace GpmPayWebhookLocal.Services;

public enum ConnState { Stopped, Connecting, Running, Error }

/// <summary>
/// Điều phối: HTTP server → Cloudflare tunnel → đăng ký webhook GPM Pay, và giám sát tunnel.
/// Tunnel chết / không truy cập được → tạo URL mới và cập nhật (hoặc tạo lại) webhook, xoá webhook cũ.
/// </summary>
public sealed class AppController(AppSettings settings) : IAsyncDisposable
{
    private readonly SemaphoreSlim _wake = new(0);
    private readonly HealthChecker _health = new();
    private WebhookServer? _server;
    private CloudflareTunnel? _tunnel;
    private GpmPayClient? _api;
    private CancellationTokenSource? _cts;
    private Task? _monitor;
    private string? _cloudflaredExe;
    private volatile bool _needRecreate;

    public event Action<string>? Log;
    public event Action<string, ConnState>? StatusChanged;
    public event Action<DateTime, bool>? HealthChecked;
    public event Action<string?, string?>? WebhookChanged; // (url, webhookId)
    public event Action<bool>? RunningChanged;
    public event Action<WebhookPayload>? TransactionReceived;

    public bool IsRunning { get; private set; }
    public string? WebhookUrl { get; private set; }
    public DateTime? StartedAt { get; private set; }

    /// <summary>Đường dẫn đánh dấu — webhook nào có URL chứa chuỗi này là của app trên máy này.</summary>
    public string WebhookPath => $"/gpmpay-local/{settings.InstanceId}";

    public async Task StartAsync()
    {
        if (IsRunning) return;
        var token = settings.ApiToken;
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Chưa nhập API Token.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            SetStatus("Đang chuẩn bị cloudflared...", ConnState.Connecting);
            CloudflaredInstaller.KillOrphans();
            _cloudflaredExe = await CloudflaredInstaller.EnsureAsync(WriteLog, ct);

            _api = new GpmPayClient(token);
            _server = new WebhookServer(() => settings.WebhookSecret, WriteLog);
            _server.TransactionReceived += p => TransactionReceived?.Invoke(p);
            await _server.StartAsync(settings.Port, WebhookPath);

            IsRunning = true;
            StartedAt = DateTime.Now;
            RunningChanged?.Invoke(true);
            _needRecreate = true;
            _monitor = Task.Run(() => MonitorLoopAsync(ct));
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_monitor != null) { try { await _monitor; } catch { } }
        _monitor = null;
        _tunnel?.Dispose(); _tunnel = null;
        if (_server != null) { await _server.DisposeAsync(); _server = null; }
        _api?.Dispose(); _api = null;
        _cts?.Dispose(); _cts = null;
        WebhookUrl = null;
        WebhookChanged?.Invoke(null, null);
        if (IsRunning)
        {
            IsRunning = false;
            StartedAt = null;
            RunningChanged?.Invoke(false);
            WriteLog("Đã dừng.");
        }
        SetStatus("Đã dừng", ConnState.Stopped);
    }

    /// <summary>Yêu cầu tạo lại URL tunnel + cập nhật webhook ngay.</summary>
    public void RequestRecreate()
    {
        if (!IsRunning) return;
        _needRecreate = true;
        _wake.Release();
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        int healthFails = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_needRecreate || _tunnel == null || _tunnel.HasExited)
                {
                    _needRecreate = false;
                    await RecreateAsync(ct);
                    healthFails = 0;
                }
                else if (await CheckHealthAsync(ct))
                {
                    if (healthFails > 0) WriteLog("Tunnel hoạt động bình thường trở lại.");
                    healthFails = 0;
                }
                else
                {
                    healthFails++;
                    WriteLog($"Không truy cập được URL tunnel (lần {healthFails}/3).");
                    if (healthFails >= 3)
                    {
                        WriteLog("Tunnel lỗi → tạo lại URL và webhook mới.");
                        _needRecreate = true;
                        continue;
                    }
                }
                await _wake.WaitAsync(TimeSpan.FromSeconds(healthFails > 0 ? 10 : 30), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (GpmPayException ex) when (ex.IsAuthError)
            {
                WriteLog($"Lỗi xác thực API: {ex.Message}");
                _ = Task.Run(async () =>
                {
                    await StopAsync();
                    SetStatus("API Token không hợp lệ hoặc thiếu quyền webhooks:manage", ConnState.Error);
                });
                break;
            }
            catch (Exception ex)
            {
                WriteLog($"Lỗi: {ex.Message}");
                SetStatus("Lỗi — sẽ thử lại sau 20 giây", ConnState.Error);
                _needRecreate = true;
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RecreateAsync(CancellationToken ct)
    {
        _tunnel?.Dispose();
        _tunnel = null;
        WebhookUrl = null;
        WebhookChanged?.Invoke(null, null);

        SetStatus("Đang tạo Cloudflare tunnel...", ConnState.Connecting);
        var tunnel = new CloudflareTunnel(_cloudflaredExe!, WriteLog);
        tunnel.Exited += () => _wake.Release();
        _tunnel = tunnel;
        var baseUrl = await tunnel.StartAsync(settings.Port, ct);
        var url = baseUrl + WebhookPath;
        WriteLog($"Tunnel URL: {baseUrl}");

        SetStatus("Đang chờ tunnel sẵn sàng...", ConnState.Connecting);
        WebhookUrl = url;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!await IsReachableAsync(ct))
        {
            if (DateTime.UtcNow > deadline) { WriteLog("Chưa kiểm tra được URL tunnel, vẫn thử đăng ký webhook."); break; }
            await Task.Delay(3000, ct);
        }

        if (DateTime.UtcNow <= deadline) HealthChecked?.Invoke(DateTime.Now, true);
        SetStatus("Đang đăng ký webhook trên GPM Pay...", ConnState.Connecting);
        var id = await SyncWebhookAsync(url, ct);
        WebhookChanged?.Invoke(url, id);
        SetStatus("Đang chạy — chờ giao dịch", ConnState.Running);
    }

    /// <summary>Cập nhật webhook cũ (đánh dấu bằng WebhookPath) sang URL mới; không có thì tạo mới. Xoá các webhook cũ thừa.</summary>
    private async Task<string> SyncWebhookAsync(string url, CancellationToken ct)
    {
        var api = _api!;
        List<WebhookSetting> mine;
        try
        {
            var all = await api.ListWebhooksAsync(ct);
            mine = all.Where(w => w.Url?.Contains(WebhookPath, StringComparison.OrdinalIgnoreCase) == true).ToList();
            WriteLog($"Tìm thấy {mine.Count} webhook cũ của máy này (tổng {all.Count} webhook).");
        }
        catch (GpmPayException ex) when (!ex.IsAuthError)
        {
            WriteLog($"Không lấy được danh sách webhook: {ex.Message}");
            mine = settings.LastWebhookId != null ? [new WebhookSetting(settings.LastWebhookId, null)] : [];
        }

        string? id = null;
        // ưu tiên webhook cũ nhất hiện có / webhook đã lưu lần trước
        var candidate = mine.FirstOrDefault(w => w.Id == settings.LastWebhookId) ?? mine.FirstOrDefault();
        if (candidate != null)
        {
            if (string.Equals(candidate.Url, url, StringComparison.OrdinalIgnoreCase))
            {
                id = candidate.Id;
            }
            else
            {
                try
                {
                    await api.UpdateWebhookUrlAsync(candidate.Id, url, settings.WebhookSecret, ct);
                    id = candidate.Id;
                    WriteLog($"Đã cập nhật webhook {id} sang URL mới.");
                }
                catch (GpmPayException ex) when (!ex.IsAuthError)
                {
                    WriteLog($"Cập nhật webhook cũ thất bại ({ex.Message}) → tạo webhook mới.");
                }
            }
        }

        if (id == null)
        {
            id = await api.CreateWebhookAsync(url, settings.WebhookSecret, ct);
            WriteLog($"Đã tạo webhook mới: {id}");
        }

        foreach (var w in mine.Where(w => w.Id != id))
        {
            try
            {
                await api.DeleteWebhookAsync(w.Id, ct);
                WriteLog($"Đã xoá webhook cũ {w.Id}.");
            }
            catch (GpmPayException ex) when (!ex.IsAuthError)
            {
                WriteLog($"Không xoá được webhook {w.Id}: {ex.Message}");
            }
        }

        settings.LastWebhookId = id;
        settings.Save();
        return id;
    }

    private async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        var url = WebhookUrl;
        if (url == null) return false;
        return await _health.CheckAsync(url + "/health", ct);
    }

    private void WriteLog(string msg) => Log?.Invoke(msg);
    private void SetStatus(string s, ConnState state) => StatusChanged?.Invoke(s, state);

    private async Task<bool> CheckHealthAsync(CancellationToken ct)
    {
        var ok = await IsReachableAsync(ct);
        HealthChecked?.Invoke(DateTime.Now, ok);
        return ok;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _health.Dispose();
    }
}
