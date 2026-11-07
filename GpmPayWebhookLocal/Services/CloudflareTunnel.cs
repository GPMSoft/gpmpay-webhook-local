using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using GpmPayWebhookLocal.Platform;

namespace GpmPayWebhookLocal.Services;

/// <summary>Tải cloudflared đúng hệ điều hành / kiến trúc từ GitHub releases nếu chưa có.</summary>
public static class CloudflaredInstaller
{
    public static readonly string ExePath =
        Path.Combine(AppSettings.DataDir, OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared");

    /// <summary>Tên file trên GitHub releases, theo thứ tự ưu tiên (macOS arm64 lỗi thì lùi về amd64 chạy qua Rosetta).</summary>
    private static string[] AssetCandidates()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows())
            return [arch == Architecture.X86 ? "cloudflared-windows-386.exe" : "cloudflared-windows-amd64.exe"];
        if (OperatingSystem.IsMacOS())
            return arch == Architecture.Arm64
                ? ["cloudflared-darwin-arm64.tgz", "cloudflared-darwin-amd64.tgz"]
                : ["cloudflared-darwin-amd64.tgz"];
        return arch switch
        {
            Architecture.Arm64 => ["cloudflared-linux-arm64"],
            Architecture.Arm or Architecture.Armv6 => ["cloudflared-linux-arm"],
            Architecture.X86 => ["cloudflared-linux-386"],
            _ => ["cloudflared-linux-amd64"],
        };
    }

    public static async Task<string> EnsureAsync(Action<string> log, CancellationToken ct)
    {
        if (File.Exists(ExePath)) return ExePath;

        // cloudflared có sẵn trên máy (brew / apt) → dùng luôn, khỏi tải
        if (!OperatingSystem.IsWindows() && ProcessUtil.FindExecutable("cloudflared") is { } system)
        {
            log($"Dùng cloudflared có sẵn trên máy: {system}");
            return system;
        }

        Directory.CreateDirectory(AppSettings.DataDir);
        Exception? last = null;
        foreach (var asset in AssetCandidates())
        {
            try
            {
                await DownloadAsync(asset, log, ct);
                return ExePath;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                log($"Tải {asset} thất bại: {ex.Message}");
            }
        }
        throw new Exception("Không tải được cloudflared: " + last?.Message, last);
    }

    private static async Task DownloadAsync(string asset, Action<string> log, CancellationToken ct)
    {
        var url = $"https://github.com/cloudflare/cloudflared/releases/latest/download/{asset}";
        log($"Chưa có cloudflared, đang tải {url} ...");

        var tmp = ExePath + ".download";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buffer = new byte[81920];
            long read = 0; int lastPct = -1; int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0)
                {
                    var pct = (int)(read * 100 / total.Value) / 20 * 20;
                    if (pct != lastPct) { lastPct = pct; log($"Đang tải cloudflared: {pct}%"); }
                }
            }
        }

        if (asset.EndsWith(".tgz", StringComparison.Ordinal))
        {
            await ExtractFromTgzAsync(tmp, "cloudflared", ExePath + ".extract", ct);
            File.Delete(tmp);
            tmp = ExePath + ".extract";
        }

        File.Move(tmp, ExePath, true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(ExePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        log("Đã tải xong cloudflared.");
    }

    private static async Task ExtractFromTgzAsync(string tgz, string entryName, string dest, CancellationToken ct)
    {
        await using var file = File.OpenRead(tgz);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile &&
                Path.GetFileName(entry.Name) == entryName)
            {
                await entry.ExtractToFileAsync(dest, overwrite: true, ct);
                return;
            }
        }
        throw new Exception($"Không tìm thấy {entryName} trong gói tải về.");
    }

    /// <summary>Tắt các tiến trình cloudflared do app chạy từ lần trước (nếu app bị tắt đột ngột).</summary>
    public static void KillOrphans()
    {
        foreach (var p in Process.GetProcessesByName("cloudflared"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, ExePath,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    p.Kill(true);
            }
            catch { }
            finally { p.Dispose(); }
        }
    }
}

/// <summary>Chạy Cloudflare Quick Tunnel (cloudflared tunnel --url) và lấy URL *.trycloudflare.com.</summary>
public sealed partial class CloudflareTunnel(string exePath, Action<string> log) : IDisposable
{
    private Process? _proc;
    private bool _disposed;

    public event Action? Exited;
    public bool HasExited => _proc == null || _proc.HasExited;

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public async Task<string> StartAsync(int port, CancellationToken ct)
    {
        var urlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var psi = ProcessUtil.StartInfo(exePath, "tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{port}");
        psi.RedirectStandardError = true;
        psi.RedirectStandardOutput = true;
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var m = UrlRegex().Match(line);
            if (m.Success && !m.Value.StartsWith("https://api.", StringComparison.OrdinalIgnoreCase))
                urlTcs.TrySetResult(m.Value.ToLowerInvariant());
            if (line.Contains("Registered tunnel connection", StringComparison.OrdinalIgnoreCase))
                connectedTcs.TrySetResult();
            if (line.Contains(" ERR ", StringComparison.Ordinal) || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                log("cloudflared: " + Shorten(line));
        }

        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.Exited += (_, _) =>
        {
            urlTcs.TrySetException(new Exception($"cloudflared đã thoát (mã {SafeExitCode(proc)})."));
            connectedTcs.TrySetResult();
            if (!_disposed)
            {
                log("cloudflared đã dừng bất ngờ.");
                Exited?.Invoke();
            }
        };

        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        _proc = proc;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        string url;
        try { url = await urlTcs.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Quá 60 giây vẫn chưa lấy được URL từ cloudflared.");
        }

        // chờ tunnel kết nối tới edge của Cloudflare (tối đa 30s)
        try { await connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); } catch (TimeoutException) { }
        return url;
    }

    private static string Shorten(string s) => s.Length > 300 ? s[..300] + "…" : s;
    private static string SafeExitCode(Process p) { try { return p.ExitCode.ToString(); } catch { return "?"; } }

    public void Dispose()
    {
        _disposed = true;
        if (_proc == null) return;
        try { if (!_proc.HasExited) _proc.Kill(true); } catch { }
        _proc.Dispose();
        _proc = null;
    }
}
