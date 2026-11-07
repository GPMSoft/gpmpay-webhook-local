using System.Threading.Channels;
using GpmPayWebhookLocal.Platform;

namespace GpmPayWebhookLocal.Services;

public enum VoiceEngine
{
    /// <summary>Giọng hệ điều hành nếu có tiếng Việt, không thì eSpeak NG.</summary>
    Auto,
    /// <summary>Giọng hệ điều hành (Windows: Microsoft An · macOS: Linh · Linux: Speech Dispatcher).</summary>
    System,
    Espeak,
}

/// <summary>Giọng đọc có sẵn của hệ điều hành.</summary>
public interface ISystemVoice : IDisposable
{
    /// <summary>Tên hiển thị: "Windows", "macOS", "Speech Dispatcher".</summary>
    string Label { get; }
    bool IsAvailable { get; }
    bool HasVietnamese { get; }
    /// <summary>Tổng hợp + phát xong mới trả về.</summary>
    Task SpeakAsync(string text, double volume, CancellationToken ct);
}

/// <summary>Phát dữ liệu WAV (dùng cho eSpeak NG).</summary>
public interface IWavPlayer
{
    Task PlayAsync(byte[] wav, double volume, CancellationToken ct);
}

public sealed record VoiceWarning(string Title, string Body);

/// <summary>Đọc văn bản qua loa bằng giọng hệ điều hành hoặc eSpeak NG, có hàng đợi, âm lượng và tắt tiếng.</summary>
public sealed class SpeechService : IDisposable
{
    private readonly ISystemVoice _system;
    private readonly IWavPlayer _player;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _log;

    public double Volume { get; set; } = 0.8;  // 0..1
    public bool Muted { get; set; }
    public VoiceEngine Engine { get; set; } = VoiceEngine.Auto;
    public int EspeakSpeed { get; set; } = 150; // từ/phút

    /// <summary>true khi bắt đầu đọc, false khi đã đọc hết hàng đợi.</summary>
    public event Action<bool>? SpeakingChanged;

    public bool HasVietnameseVoice => _system.HasVietnamese;
    public string SystemLabel => _system.Label;

    public SpeechService(Action<string> log)
    {
        _log = log;
        (_system, _player) = PlatformSpeech.Create();
        _ = Task.Run(LoopAsync);
    }

    /// <summary>Engine thực sự dùng sau khi xét chế độ Tự động.</summary>
    public VoiceEngine EffectiveEngine => Engine switch
    {
        VoiceEngine.Auto when _system.HasVietnamese => VoiceEngine.System,
        // Windows tự tải được eSpeak NG; macOS/Linux chỉ dùng khi đã cài, không thì đành giọng hệ thống
        VoiceEngine.Auto => OperatingSystem.IsWindows() || EspeakNg.IsInstalled || !_system.IsAvailable
            ? VoiceEngine.Espeak : VoiceEngine.System,
        _ => Engine,
    };

    /// <summary>Mô tả ngắn engine đang dùng, hiện cạnh "Giọng đọc:".</summary>
    public string Describe() => Engine switch
    {
        VoiceEngine.Auto => $"Tự động ({(EffectiveEngine == VoiceEngine.System ? _system.Label : "eSpeak NG")})",
        VoiceEngine.System => _system.Label,
        _ => EspeakNg.IsInstalled ? "eSpeak NG" : OperatingSystem.IsWindows() ? "eSpeak NG (đang tải…)" : "eSpeak NG (chưa cài)",
    };

    /// <summary>Cảnh báo hiện bằng biểu tượng ⚠ khi giọng đọc chưa tối ưu; null nếu ổn.</summary>
    public VoiceWarning? Warning
    {
        get
        {
            if (EffectiveEngine == VoiceEngine.Espeak && !EspeakNg.IsInstalled && !OperatingSystem.IsWindows())
                return new("Chưa cài eSpeak NG.", OperatingSystem.IsMacOS()
                    ? "Cài bằng Homebrew: brew install espeak-ng — hoặc dùng giọng macOS (Linh)."
                    : "Cài bằng: sudo apt install espeak-ng (Fedora: dnf, Arch: pacman).");

            if (Engine != VoiceEngine.Auto || _system.HasVietnamese || OperatingSystem.IsLinux()) return null;

            return OperatingSystem.IsWindows()
                ? new("Windows chưa có giọng tiếng Việt.",
                    "Đang dùng eSpeak NG (giọng máy). Để có giọng tự nhiên hơn, cài Microsoft An: Settings → Time & language → Speech → Add voices → Tiếng Việt.")
                : new("macOS chưa có giọng tiếng Việt.",
                    "Để có giọng tự nhiên, tải giọng Linh: System Settings → Accessibility → Spoken Content → System voice → Manage Voices… → Tiếng Việt.");
        }
    }

    public void Speak(string text, bool force = false)
    {
        if (Muted && !force) return;
        _queue.Writer.TryWrite(text);
    }

    /// <summary>Chuẩn bị sẵn eSpeak NG nếu engine hiện tại cần đến (Windows: tự tải về).</summary>
    public Task PrepareAsync() =>
        EffectiveEngine == VoiceEngine.Espeak ? EspeakNg.EnsureAsync(_log, _cts.Token) : Task.CompletedTask;

    private async Task LoopAsync()
    {
        try
        {
            await foreach (var text in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    if (EffectiveEngine == VoiceEngine.Espeak)
                    {
                        await EspeakNg.EnsureAsync(_log, _cts.Token);
                        var wav = await EspeakNg.SynthesizeAsync(text, EspeakSpeed, _cts.Token);
                        SpeakingChanged?.Invoke(true);
                        await _player.PlayAsync(wav, Math.Clamp(Volume, 0, 1), _cts.Token);
                    }
                    else
                    {
                        SpeakingChanged?.Invoke(true);
                        await _system.SpeakAsync(text, Math.Clamp(Volume, 0, 1), _cts.Token);
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
                catch (Exception ex) { _log("Lỗi đọc giọng nói: " + ex.Message); }
                finally
                {
                    if (_queue.Reader.Count == 0) SpeakingChanged?.Invoke(false);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        try { _system.Dispose(); } catch { }
        // Windows: cùng một object vừa là giọng vừa là trình phát → không dispose 2 lần
        if (!ReferenceEquals(_player, _system))
            try { (_player as IDisposable)?.Dispose(); } catch { }
    }
}
