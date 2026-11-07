using System.Collections.Concurrent;
using System.Text;

namespace GpmPayWebhookLocal.Services;

/// <summary>
/// Giọng "Có sẵn": ghép các cụm âm thanh nhúng trong app (Assets/Voice) thành câu "Đã nhận … đồng".
/// Không cần giọng của hệ điều hành hay eSpeak NG — chạy giống nhau trên mọi OS.
///
/// Mỗi cụm (cả số 1–99, "X trăm", "linh Y", "nghìn"…) được cắt từ một câu nói liền nên giữ ngữ điệu tự nhiên;
/// câu được tách theo cụm dài nhất có thể để ít chỗ nối nhất. Danh sách cụm nằm trong Assets/Voice/voice.txt.
/// Tạo lại bộ âm thanh: dotnet run tools/generate-voice.cs
/// </summary>
public static class BuiltinVoice
{
    private const int Rate = 16000;

    /// <summary>Nghỉ ngắn sau các cụm này (khi còn đọc tiếp số) để câu có nhịp: "một triệu · hai trăm năm mươi nghìn".</summary>
    private static readonly HashSet<string> PauseAfter = ["đã nhận", "tỷ", "triệu", "nghìn"];

    private static readonly Lazy<(Dictionary<string, string> Units, int MaxWords)> Manifest = new(LoadManifest);
    private static readonly ConcurrentDictionary<string, short[]> Cache = new();

    /// <summary>Ghép câu thành WAV (16 kHz mono 16-bit). Trả về null nếu câu có từ không nằm trong bộ âm thanh.</summary>
    public static byte[]? TrySynthesize(string text)
    {
        var units = Split(text);
        if (units == null || units.Count == 0) return null;

        var parts = new List<short[]>(units.Count * 2);
        for (int i = 0; i < units.Count; i++)
        {
            if (Load(Manifest.Value.Units[units[i]]) is not { } clip) return null;
            parts.Add(clip);
            if (i == units.Count - 1) break;
            var next = units[i + 1];
            var gapMs = next == "đồng" ? 0 : units[i] == "đã nhận" ? 50 : PauseAfter.Contains(units[i]) ? 70 : 0;
            if (gapMs > 0) parts.Add(new short[Rate * gapMs / 1000]);
        }
        return ToWav(parts);
    }

    /// <summary>Tách câu thành các cụm có trong bộ âm thanh, ưu tiên cụm dài nhất; null nếu gặp từ lạ.</summary>
    private static List<string>? Split(string text)
    {
        var (units, maxWords) = Manifest.Value;
        var words = text.ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        for (int i = 0; i < words.Length;)
        {
            int n = Math.Min(maxWords, words.Length - i);
            for (; n > 0; n--)
            {
                var candidate = string.Join(' ', words, i, n);
                if (units.ContainsKey(candidate)) { result.Add(candidate); break; }
            }
            if (n == 0) return null;
            i += n;
        }
        return result;
    }

    /// <summary>voice.txt: mỗi dòng "cụm từ=tên file".</summary>
    private static (Dictionary<string, string>, int) LoadManifest()
    {
        var units = new Dictionary<string, string>();
        using var s = typeof(BuiltinVoice).Assembly.GetManifestResourceStream("Voice.voice.txt");
        if (s == null) return (units, 1);
        using var reader = new StreamReader(s, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var eq = line.IndexOf('=');
            if (eq > 0) units[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return (units, units.Count == 0 ? 1 : units.Keys.Max(k => k.Split(' ').Length));
    }

    private static short[]? Load(string name) => Cache.GetOrAdd(name, static n =>
    {
        using var s = typeof(BuiltinVoice).Assembly.GetManifestResourceStream($"Voice.{n}.wav");
        if (s == null) return [];
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ReadPcm(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }) is { Length: > 0 } clip ? clip : null;

    /// <summary>Đọc phần data của WAV PCM 16-bit mono (định dạng do tools/generate-voice.cs tạo).</summary>
    private static short[] ReadPcm(ReadOnlySpan<byte> b)
    {
        int pos = 12;
        while (pos + 8 <= b.Length)
        {
            var id = Encoding.ASCII.GetString(b.Slice(pos, 4));
            var size = BitConverter.ToInt32(b.Slice(pos + 4, 4));
            if (id == "data")
            {
                size = Math.Min(size, b.Length - pos - 8);
                var samples = new short[size / 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(b.Slice(pos + 8 + i * 2, 2));
                return samples;
            }
            pos += 8 + size + (size & 1);
        }
        return [];
    }

    private static byte[] ToWav(List<short[]> parts)
    {
        var count = parts.Sum(p => p.Length);
        var ms = new MemoryStream(44 + count * 2);
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(36 + count * 2); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(count * 2);
            foreach (var p in parts)
                foreach (var v in p) w.Write(v);
        }
        return ms.ToArray();
    }
}
