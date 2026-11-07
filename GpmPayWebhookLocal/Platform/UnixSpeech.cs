using System.Globalization;
using System.Text.RegularExpressions;
using GpmPayWebhookLocal.Services;

namespace GpmPayWebhookLocal.Platform;

public static class PlatformSpeech
{
    public static (ISystemVoice Voice, IWavPlayer Player) Create()
    {
#if WINDOWS
        var win = new WindowsSpeech();
        return (win, win);
#else
        if (OperatingSystem.IsMacOS()) return (new MacSystemVoice(), new MacWavPlayer());
        return (new LinuxSystemVoice(), new LinuxWavPlayer());
#endif
    }

    internal static async Task<string> WriteTempAsync(string ext, Func<string, Task> write)
    {
        var path = Path.Combine(Path.GetTempPath(), "gpmpay-tts-" + Guid.NewGuid().ToString("N")[..8] + ext);
        await write(path);
        return path;
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}

/// <summary>macOS: lệnh <c>say</c> (giọng Linh cho tiếng Việt) xuất ra AIFF rồi phát bằng <c>afplay -v</c> để chỉnh được âm lượng.</summary>
public sealed partial class MacSystemVoice : ISystemVoice
{
    private readonly string? _voice;

    public string Label => "macOS";
    public bool IsAvailable => true;
    public bool HasVietnamese => _voice != null;

    // "Linh                vi_VN    # Xin chào, tôi tên là Linh."
    [GeneratedRegex(@"^(?<name>.+?)\s+(?<lang>[a-z]{2,3}[_-][A-Za-z]{2,4})\s+#", RegexOptions.Multiline)]
    private static partial Regex VoiceLine();

    public MacSystemVoice()
    {
        var list = ProcessUtil.ReadOutput("/usr/bin/say", ["-v", "?"], TimeSpan.FromSeconds(5)) ?? "";
        _voice = VoiceLine().Matches(list)
            .Where(m => m.Groups["lang"].Value.StartsWith("vi", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups["name"].Value.Trim())
            .FirstOrDefault();
    }

    public async Task SpeakAsync(string text, double volume, CancellationToken ct)
    {
        var txt = await PlatformSpeech.WriteTempAsync(".txt", p => File.WriteAllTextAsync(p, text, ct));
        var aiff = Path.ChangeExtension(txt, ".aiff");
        try
        {
            var args = new List<string>();
            if (_voice != null) args.AddRange(["-v", _voice]);
            args.AddRange(["-f", txt, "-o", aiff]);
            var code = await ProcessUtil.RunAsync(ProcessUtil.StartInfo("/usr/bin/say", args), TimeSpan.FromSeconds(20), ct);
            if (code != 0 || !File.Exists(aiff)) throw new Exception($"say lỗi (mã {code}).");
            await MacWavPlayer.PlayFileAsync(aiff, volume, ct);
        }
        finally
        {
            PlatformSpeech.TryDelete(txt);
            PlatformSpeech.TryDelete(aiff);
        }
    }

    public void Dispose() { }
}

public sealed class MacWavPlayer : IWavPlayer
{
    public async Task PlayAsync(byte[] wav, double volume, CancellationToken ct)
    {
        var file = await PlatformSpeech.WriteTempAsync(".wav", p => File.WriteAllBytesAsync(p, wav, ct));
        try { await PlayFileAsync(file, volume, ct); }
        finally { PlatformSpeech.TryDelete(file); }
    }

    internal static Task PlayFileAsync(string file, double volume, CancellationToken ct) =>
        ProcessUtil.RunAsync(
            ProcessUtil.StartInfo("/usr/bin/afplay", "-v", volume.ToString("0.##", CultureInfo.InvariantCulture), file),
            TimeSpan.FromSeconds(30), ct);
}

/// <summary>
/// Linux: giọng hệ thống = Speech Dispatcher (<c>spd-say</c>). Không xác định chắc được có giọng Việt tự nhiên hay không
/// nên chế độ Tự động dùng giọng "Có sẵn".
/// </summary>
public sealed class LinuxSystemVoice : ISystemVoice
{
    private readonly string? _spdSay = ProcessUtil.FindExecutable("spd-say");

    public string Label => "Speech Dispatcher";
    public bool IsAvailable => _spdSay != null;
    public bool HasVietnamese => false;

    public async Task SpeakAsync(string text, double volume, CancellationToken ct)
    {
        if (_spdSay == null) throw new InvalidOperationException("Chưa cài Speech Dispatcher (spd-say).");
        // -w: chờ đọc xong · -i: âm lượng -100..100
        var vol = (int)Math.Round(volume * 200 - 100);
        await ProcessUtil.RunAsync(
            ProcessUtil.StartInfo(_spdSay, "-w", "-l", "vi", "-i", vol.ToString(CultureInfo.InvariantCulture), "--", text),
            TimeSpan.FromSeconds(30), ct);
    }

    public void Dispose() { }
}

/// <summary>Linux: phát WAV bằng paplay (PulseAudio / PipeWire) có chỉnh âm lượng; không có thì pw-play / aplay.</summary>
public sealed class LinuxWavPlayer : IWavPlayer
{
    public async Task PlayAsync(byte[] wav, double volume, CancellationToken ct)
    {
        var file = await PlatformSpeech.WriteTempAsync(".wav", p => File.WriteAllBytesAsync(p, wav, ct));
        try
        {
            var inv = CultureInfo.InvariantCulture;
            System.Diagnostics.ProcessStartInfo psi;
            if (ProcessUtil.FindExecutable("paplay") is { } paplay)
                psi = ProcessUtil.StartInfo(paplay, $"--volume={(int)(volume * 65536)}", file);
            else if (ProcessUtil.FindExecutable("pw-play") is { } pwPlay)
                psi = ProcessUtil.StartInfo(pwPlay, $"--volume={volume.ToString("0.##", inv)}", file);
            else if (ProcessUtil.FindExecutable("aplay") is { } aplay)
                psi = ProcessUtil.StartInfo(aplay, "-q", file);
            else
                throw new InvalidOperationException("Không tìm thấy trình phát âm thanh (paplay / pw-play / aplay).");

            await ProcessUtil.RunAsync(psi, TimeSpan.FromSeconds(30), ct);
        }
        finally { PlatformSpeech.TryDelete(file); }
    }
}
