using System.Diagnostics;

namespace GpmPayWebhookLocal.Platform;

/// <summary>Tiện ích chạy tiến trình ngoài (say, afplay, espeak-ng, notify-send...) — truyền tham số qua ArgumentList để không lỗi dấu cách / ký tự đặc biệt.</summary>
public static class ProcessUtil
{
    /// <summary>
    /// App GUI mở từ Finder / menu ứng dụng thường có PATH rất ngắn → tìm thêm ở các thư mục hay cài công cụ
    /// (Homebrew, /usr/local/bin...).
    /// </summary>
    private static readonly string[] ExtraDirs = OperatingSystem.IsWindows()
        ? []
        : ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/opt/local/bin", "/snap/bin"];

    public static string? FindExecutable(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(ExtraDirs)
            .Distinct();
        var names = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { name + ".exe", name }
            : [name];
        foreach (var dir in dirs)
        {
            foreach (var n in names)
            {
                try
                {
                    var full = Path.Combine(dir, n);
                    if (File.Exists(full)) return full;
                }
                catch { /* PATH có mục lỗi */ }
            }
        }
        return null;
    }

    public static ProcessStartInfo StartInfo(string exe, params IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Chạy và chờ thoát; huỷ / quá giờ thì kill. Trả về mã thoát.</summary>
    public static async Task<int> RunAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
    {
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Không chạy được {psi.FileName}.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(psi.FileName)} chạy quá {timeout.TotalSeconds:0} giây.");
        }
        return p.ExitCode;
    }

    /// <summary>Chạy và đọc stdout (dùng cho lệnh ngắn như <c>say -v ?</c>).</summary>
    public static string? ReadOutput(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        try
        {
            var psi = StartInfo(exe, args);
            psi.RedirectStandardOutput = true;
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeout)) { try { p.Kill(true); } catch { } return null; }
            return output.Result;
        }
        catch { return null; }
    }

    /// <summary>Chạy kiểu "bắn rồi quên" (thông báo hệ thống...).</summary>
    public static void Fire(string exe, params IEnumerable<string> args)
    {
        try { Process.Start(StartInfo(exe, args))?.Dispose(); } catch { }
    }
}
