using GpmPayWebhookLocal.Platform;

namespace GpmPayWebhookLocal.Services;

/// <summary>
/// eSpeak NG (GPLv3, https://github.com/espeak-ng/espeak-ng) — giọng máy, chạy offline.
/// <list type="bullet">
/// <item>Windows: tự tải bản phát hành chính thức và giải nén bằng "msiexec /a" (chỉ giải nén, không cài, không cần Admin).</item>
/// <item>macOS / Linux: dùng bản người dùng cài sẵn (brew install espeak-ng / apt install espeak-ng).</item>
/// </list>
/// App chỉ gọi espeak-ng như một chương trình riêng, không đóng gói kèm.
/// </summary>
public static class EspeakNg
{
    public const string Version = "1.52.0";
    private const string MsiUrl = $"https://github.com/espeak-ng/espeak-ng/releases/download/{Version}/espeak-ng.msi";

    /// <summary>Windows: thư mục giải nén trong DataDir.</summary>
    public static readonly string InstallDir = Path.Combine(AppSettings.DataDir, "espeak-ng");

    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static string? _systemExe;
    private static bool _hintLogged;

    private static string? ExePath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var exe = Path.Combine(InstallDir, "espeak-ng.exe");
                return File.Exists(exe) && File.Exists(Path.Combine(InstallDir, "espeak-ng-data", "vi_dict")) ? exe : null;
            }
            return _systemExe ??= ProcessUtil.FindExecutable("espeak-ng");
        }
    }

    public static bool IsInstalled => ExePath != null;

    public static async Task EnsureAsync(Action<string> log, CancellationToken ct = default)
    {
        if (IsInstalled) return;
        if (!OperatingSystem.IsWindows())
        {
            _systemExe = null; // người dùng có thể vừa cài xong → tìm lại
            if (IsInstalled) return;
            if (!_hintLogged)
            {
                _hintLogged = true;
                log(OperatingSystem.IsMacOS()
                    ? "Chưa cài eSpeak NG. Cài bằng: brew install espeak-ng"
                    : "Chưa cài eSpeak NG. Cài bằng: sudo apt install espeak-ng");
            }
            throw new InvalidOperationException("Chưa cài eSpeak NG.");
        }

        await Lock.WaitAsync(ct);
        try
        {
            if (IsInstalled) return;

            var work = Path.Combine(Path.GetTempPath(), "gpmpay-espeak-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            try
            {
                var msi = Path.Combine(work, "espeak-ng.msi");
                log($"Đang tải eSpeak NG {Version} (~13MB)...");
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                await using (var src = await http.GetStreamAsync(MsiUrl, ct))
                await using (var dst = File.Create(msi))
                    await src.CopyToAsync(dst, ct);

                log("Đang giải nén eSpeak NG...");
                var extract = Path.Combine(work, "x");
                // msiexec tự phân tích dòng lệnh theo kiểu riêng (PROP="giá trị") → ghép chuỗi thủ công
                var psi = ProcessUtil.StartInfo("msiexec.exe");
                psi.Arguments = $"/a \"{msi}\" /qn TARGETDIR=\"{extract}\"";
                var code = await ProcessUtil.RunAsync(psi, TimeSpan.FromMinutes(5), ct);
                if (code != 0) throw new Exception($"Giải nén eSpeak NG thất bại (msiexec mã {code}).");

                var srcDir = Directory.GetFiles(extract, "espeak-ng.exe", SearchOption.AllDirectories).FirstOrDefault()
                    is { } exe ? Path.GetDirectoryName(exe)! : throw new Exception("Không tìm thấy espeak-ng.exe trong gói cài.");

                if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true);
                CopyDir(srcDir, InstallDir);
                log("Đã cài eSpeak NG.");
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }
        finally
        {
            Lock.Release();
        }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    /// <summary>Tạo WAV từ văn bản tiếng Việt. Văn bản truyền qua file UTF-8 để không lỗi dấu.</summary>
    public static async Task<byte[]> SynthesizeAsync(string text, int wordsPerMinute, CancellationToken ct)
    {
        var exe = ExePath ?? throw new InvalidOperationException("Chưa cài eSpeak NG.");
        var tmp = Path.Combine(Path.GetTempPath(), "gpmpay-tts-" + Guid.NewGuid().ToString("N")[..8]);
        var txt = tmp + ".txt";
        var wav = tmp + ".wav";
        try
        {
            await File.WriteAllTextAsync(txt, text, new System.Text.UTF8Encoding(false), ct);
            var psi = ProcessUtil.StartInfo(exe, "-v", "vi", "-s", wordsPerMinute.ToString(), "-b", "1", "-f", txt, "-w", wav);
            // bản giải nén trên Windows không có registry → phải chỉ đường dẫn dữ liệu, nếu không espeak-ng.exe sẽ crash
            if (OperatingSystem.IsWindows()) psi.Environment["ESPEAK_DATA_PATH"] = InstallDir;

            var code = await ProcessUtil.RunAsync(psi, TimeSpan.FromSeconds(15), ct);
            if (code != 0 || !File.Exists(wav)) throw new Exception($"espeak-ng lỗi (mã {code}).");
            return await File.ReadAllBytesAsync(wav, ct);
        }
        finally
        {
            try { File.Delete(txt); } catch { }
            try { File.Delete(wav); } catch { }
        }
    }
}
