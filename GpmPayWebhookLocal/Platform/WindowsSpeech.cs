#if WINDOWS
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;
using GpmPayWebhookLocal.Services;

namespace GpmPayWebhookLocal.Platform;

/// <summary>Windows: giọng OneCore qua WinRT SpeechSynthesizer (Microsoft An cho tiếng Việt), phát bằng MediaPlayer.</summary>
public sealed class WindowsSpeech : ISystemVoice, IWavPlayer
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly MediaPlayer _player = new();
    private readonly SemaphoreSlim _playLock = new(1, 1);

    public string Label => "Windows";
    public bool IsAvailable => true;
    public bool HasVietnamese { get; }

    public WindowsSpeech()
    {
        var vi = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("vi", StringComparison.OrdinalIgnoreCase));
        if (vi != null) _synth.Voice = vi;
        HasVietnamese = vi != null;
    }

    public async Task SpeakAsync(string text, double volume, CancellationToken ct)
    {
        using var stream = await _synth.SynthesizeTextToStreamAsync(text);
        await PlayStreamAsync(stream, volume, ct);
    }

    public async Task PlayAsync(byte[] wav, double volume, CancellationToken ct)
    {
        using var stream = new MemoryStream(wav).AsRandomAccessStream();
        await PlayStreamAsync(stream, volume, ct);
    }

    private async Task PlayStreamAsync(IRandomAccessStream stream, double volume, CancellationToken ct)
    {
        await _playLock.WaitAsync(ct);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEnded(MediaPlayer s, object a) => ended.TrySetResult();
        void OnFailed(MediaPlayer s, MediaPlayerFailedEventArgs a) => ended.TrySetResult();
        _player.MediaEnded += OnEnded;
        _player.MediaFailed += OnFailed;
        try
        {
            _player.Volume = volume;
            _player.Source = MediaSource.CreateFromStream(stream, "audio/wav");
            _player.Play();
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (TimeoutException) { }
        finally
        {
            _player.MediaEnded -= OnEnded;
            _player.MediaFailed -= OnFailed;
            _player.Source = null;
            _playLock.Release();
        }
    }

    public void Dispose()
    {
        _player.Dispose();
        _synth.Dispose();
    }
}
#endif
