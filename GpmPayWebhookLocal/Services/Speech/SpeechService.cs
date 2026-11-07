using System.Threading.Channels;
using GpmPayWebhookLocal.Platform;

namespace GpmPayWebhookLocal.Services;

public enum VoiceEngine
{
    /// <summary>Giọng tiếng Việt của hệ điều hành nếu có, không thì giọng "Có sẵn".</summary>
    Auto,
    /// <summary>Giọng hệ điều hành (Windows: Microsoft An · macOS: Linh · Linux: Speech Dispatcher).</summary>
    System,
    /// <summary>Ghép các đoạn âm thanh nhúng trong app — không phụ thuộc hệ điều hành.</summary>
    Builtin,
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

/// <summary>Phát dữ liệu WAV (dùng cho giọng "Có sẵn").</summary>
public interface IWavPlayer
{
    Task PlayAsync(byte[] wav, double volume, CancellationToken ct);
}

public sealed record VoiceWarning(string Title, string Body);

/// <summary>Đọc văn bản qua loa bằng giọng "Có sẵn" hoặc giọng hệ điều hành, có hàng đợi, âm lượng và tắt tiếng.</summary>
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

    /// <summary>
    /// Engine thực sự dùng. Tự động: giọng tiếng Việt của hệ điều hành (tự nhiên nhất) nếu có, không thì giọng "Có sẵn".
    /// </summary>
    public VoiceEngine EffectiveEngine => Engine switch
    {
        VoiceEngine.Auto => _system.HasVietnamese ? VoiceEngine.System : VoiceEngine.Builtin,
        _ => Engine,
    };

    /// <summary>Mô tả ngắn engine đang dùng, hiện cạnh "Giọng đọc:".</summary>
    public string Describe() => Engine switch
    {
        VoiceEngine.Auto => $"Tự động ({(EffectiveEngine == VoiceEngine.System ? _system.Label : "Có sẵn")})",
        VoiceEngine.System => _system.Label,
        _ => "Có sẵn",
    };

    /// <summary>Cảnh báo hiện bằng biểu tượng ⚠ khi giọng hệ điều hành được chọn nhưng không đọc được tiếng Việt; null nếu ổn.</summary>
    public VoiceWarning? Warning
    {
        get
        {
            if (Engine != VoiceEngine.System || _system.HasVietnamese) return null;

            if (OperatingSystem.IsWindows())
                return new("Windows chưa có giọng tiếng Việt.",
                    "Chọn giọng \"Có sẵn\", hoặc cài Microsoft An: Settings → Time & language → Speech → Add voices → Tiếng Việt.");
            if (OperatingSystem.IsMacOS())
                return new("macOS chưa có giọng tiếng Việt.",
                    "Chọn giọng \"Có sẵn\", hoặc tải giọng Linh: System Settings → Accessibility → Spoken Content → System voice → Manage Voices… → Tiếng Việt.");
            return _system.IsAvailable ? null
                : new("Chưa có Speech Dispatcher.", "Chọn giọng \"Có sẵn\", hoặc cài: sudo apt install speech-dispatcher.");
        }
    }

    public void Speak(string text, bool force = false)
    {
        if (Muted && !force) return;
        _queue.Writer.TryWrite(text);
    }

    private async Task LoopAsync()
    {
        try
        {
            await foreach (var text in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    var volume = Math.Clamp(Volume, 0, 1);
                    // "Có sẵn" chỉ ghép được câu báo tiền; câu khác (nếu có) thì nhờ giọng hệ điều hành
                    if (EffectiveEngine == VoiceEngine.Builtin && BuiltinVoice.TrySynthesize(text) is { } clip)
                    {
                        SpeakingChanged?.Invoke(true);
                        await _player.PlayAsync(clip, volume, _cts.Token);
                    }
                    else if (_system.IsAvailable)
                    {
                        SpeakingChanged?.Invoke(true);
                        await _system.SpeakAsync(text, volume, _cts.Token);
                    }
                    else _log("Không đọc được câu này: " + text);
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
