// Tạo bộ âm thanh cho giọng "Có sẵn" (GpmPayWebhookLocal/Assets/Voice/*.wav) bằng giọng tiếng Việt của Windows.
//
// Yêu cầu: Windows 10/11 đã cài giọng tiếng Việt (Settings → Time & language → Speech → Add voices → Tiếng Việt).
// Chạy:    dotnet run tools/generate-voice.cs
//
// Để nghe tự nhiên, mỗi "cụm" được cắt ra từ một câu hoàn chỉnh (giữ ngữ điệu khi nói liền),
// và dùng cụm lớn (cả số 1–99, "X trăm", "linh Y") để câu ghép có ít chỗ nối nhất:
//   "Đã nhận | một triệu | hai trăm | năm mươi | nghìn | đồng"
// Điểm cắt lấy từ mốc thời gian từng từ (word boundary), rồi dịch tới chỗ năng lượng thấp nhất ngay trước đó
// để không cắt vào giữa âm. (Thẻ SSML <mark> của giọng OneCore bị lệch nên không dùng.)
#:property TargetFramework=net10.0-windows10.0.19041.0
#:property PublishAot=false

using System.Text;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

const int Rate = 16000; // tần số gốc của giọng OneCore

string[] digits = ["không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín"];

// cụm → (tên file, câu chứa cụm; [[ ]] đánh dấu đoạn cần cắt). Phải khớp quy tắc của VietnameseNumber.
var units = new List<(string File, string Text, string Carrier)>
{
    ("da-nhan", "đã nhận", "[[Đã nhận]] năm mươi nghìn đồng"),
    ("dong",    "đồng",    "Đã nhận năm mươi nghìn [[đồng]]"),
    ("am",      "âm",      "Đã nhận [[âm]] năm mươi nghìn đồng"),
    ("khong",   "không",   "Đã nhận [[không]] nghìn đồng"),
    ("nghin",   "nghìn",   "Đã nhận hai mươi [[nghìn]] đồng"),
    ("trieu",   "triệu",   "Đã nhận năm [[triệu]] đồng"),
    ("ty",      "tỷ",      "Đã nhận hai [[tỷ]] đồng"),
};
for (int n = 1; n <= 99; n++)
{
    var text = TwoDigits(n);
    units.Add(($"n{n:00}", text, $"Đã nhận [[{text}]] nghìn đồng"));
}
for (int h = 0; h <= 9; h++)
    units.Add(($"tram{h}", $"{digits[h]} trăm", $"Đã nhận một triệu [[{digits[h]} trăm]] năm mươi nghìn đồng"));
for (int u = 1; u <= 9; u++)
    units.Add(($"linh{u}", $"linh {digits[u]}", $"Đã nhận một trăm [[linh {digits[u]}]] nghìn đồng"));

var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Path.Combine(Directory.GetCurrentDirectory(), "tools"), ".."));
var outDir = Path.Combine(root, "GpmPayWebhookLocal", "Assets", "Voice");
if (Directory.Exists(outDir)) foreach (var f in Directory.GetFiles(outDir, "*.wav")) File.Delete(f);
Directory.CreateDirectory(outDir);

using var synth = new SpeechSynthesizer();
synth.Options.IncludeWordBoundaryMetadata = true;
var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("vi", StringComparison.OrdinalIgnoreCase))
    ?? throw new InvalidOperationException("Windows chưa có giọng tiếng Việt.");
synth.Voice = voice;
Console.WriteLine($"Giọng: {voice.DisplayName} → {outDir}");

long total = 0;
var manifest = new StringBuilder();
foreach (var (file, text, carrier) in units)
{
    // vị trí (theo từ) của cụm trong câu
    var words = carrier.Replace("[[", "").Replace("]]", "").Split(' ');
    int first = carrier[..carrier.IndexOf("[[")].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    int after = first + text.Split(' ').Length;

    using var stream = await synth.SynthesizeTextToStreamAsync(string.Join(' ', words));
    var cues = stream.TimedMetadataTracks.First(t => t.Id == "SpeechWord").Cues.Select(c => c.StartTime.TotalSeconds).ToList();
    if (cues.Count != words.Length) throw new InvalidDataException($"{carrier}: {cues.Count} mốc / {words.Length} từ");

    var bytes = new byte[stream.Size];
    using (var reader = new DataReader(stream.GetInputStreamAt(0)))
    {
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
    }
    var (rate, all) = ReadWav(bytes);
    all = Resample(all, rate, Rate);

    // mốc từ thường rơi vào đầu nguyên âm → điểm cắt thật nằm ngay trước đó
    int Cut(int word) => BoundaryBefore(all, (int)(cues[word] * Rate), (int)(cues[word - 1] * Rate) + Rate * 80 / 1000);
    int a = first == 0 ? 0 : Cut(first);
    int b = after >= words.Length ? all.Length : Cut(after);
    var clip = Trim(all[a..b]);

    WriteWav(Path.Combine(outDir, file + ".wav"), clip);
    total += 44 + clip.Length * 2;
    manifest.AppendLine($"{text}={file}");
    Console.WriteLine($"  {file,-8} {text,-18} {clip.Length * 1000 / Rate,5} ms");
}
File.WriteAllText(Path.Combine(outDir, "voice.txt"), manifest.ToString(), new UTF8Encoding(false));
Console.WriteLine($"{units.Count} cụm, tổng {total / 1024} KB");

string TwoDigits(int n)
{
    int d = n / 10, u = n % 10;
    if (d == 0) return digits[u];
    if (d == 1) return "mười" + (u == 0 ? "" : u == 5 ? " lăm" : " " + digits[u]);
    return digits[d] + " mươi" + (u == 0 ? "" : u == 1 ? " mốt" : u == 5 ? " lăm" : " " + digits[u]);
}

// ── Xử lý âm thanh ─────────────────────────────────────────────

/// <summary>
/// Trong khoảng [-90 ms, +20 ms] quanh mốc từ, tìm khung 10 ms có năng lượng thấp nhất → điểm cắt.
/// Không lùi quá <paramref name="notBefore"/> (80 ms sau mốc từ trước) để khỏi rơi vào khoảng lặng
/// của phụ âm tắc (t, đ, b…) ở đầu chính từ trước đó.
/// </summary>
static int BoundaryBefore(short[] s, int at, int notBefore)
{
    int win = Rate / 100;
    int best = at; double bestE = double.MaxValue;
    for (int c = Math.Max(Math.Max(win, notBefore), at - Rate * 90 / 1000); c <= Math.Min(s.Length - win, at + Rate * 20 / 1000); c += Rate / 1000)
    {
        double e = 0;
        for (int i = c - win / 2; i < c + win / 2; i++) e += (double)s[i] * s[i];
        if (e < bestE) { bestE = e; best = c; }
    }
    return best;
}

/// <summary>Bỏ lặng thừa ở hai đầu (giữ 10 ms), fade 4 ms để chỗ nối không có tiếng "tách".</summary>
static short[] Trim(short[] s)
{
    int peak = s.Length == 0 ? 0 : s.Max(v => Math.Abs((int)v));
    int threshold = Math.Max(peak / 150, 1);
    int start = Array.FindIndex(s, v => Math.Abs((int)v) > threshold);
    int end = Array.FindLastIndex(s, v => Math.Abs((int)v) > threshold);
    if (start < 0) return [];
    int pad = Rate / 100;
    var r = s[Math.Max(0, start - pad)..(Math.Min(s.Length - 1, end + pad) + 1)];
    int fade = Math.Min(Rate * 4 / 1000, r.Length / 2);
    for (int i = 0; i < fade; i++)
    {
        var k = (double)i / fade;
        r[i] = (short)(r[i] * k);
        r[^(i + 1)] = (short)(r[^(i + 1)] * k);
    }
    return r;
}

static (int Rate, short[] Samples) ReadWav(byte[] b)
{
    int pos = 12, rate = 0, channels = 1, bits = 16;
    while (pos + 8 <= b.Length)
    {
        var id = Encoding.ASCII.GetString(b, pos, 4);
        var size = BitConverter.ToInt32(b, pos + 4);
        if (id == "fmt ")
        {
            channels = BitConverter.ToInt16(b, pos + 10);
            rate = BitConverter.ToInt32(b, pos + 12);
            bits = BitConverter.ToInt16(b, pos + 22);
        }
        else if (id == "data")
        {
            if (bits != 16) throw new NotSupportedException($"WAV {bits}-bit");
            size = Math.Min(size, b.Length - pos - 8);
            var n = size / 2 / channels;
            var s = new short[n];
            for (int i = 0; i < n; i++)
            {
                int sum = 0;
                for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(b, pos + 8 + (i * channels + c) * 2);
                s[i] = (short)(sum / channels);
            }
            return (rate, s);
        }
        pos += 8 + size + (size & 1);
    }
    throw new InvalidDataException("WAV không có data");
}

static short[] Resample(short[] s, int from, int to)
{
    if (from == to) return s;
    var n = (int)((long)s.Length * to / from);
    var r = new short[n];
    for (int i = 0; i < n; i++)
    {
        var x = (double)i * from / to;
        int i0 = (int)x, i1 = Math.Min(i0 + 1, s.Length - 1);
        r[i] = (short)Math.Round(s[i0] + (s[i1] - s[i0]) * (x - i0));
    }
    return r;
}

static void WriteWav(string path, short[] s)
{
    using var w = new BinaryWriter(File.Create(path));
    w.Write("RIFF"u8); w.Write(36 + s.Length * 2); w.Write("WAVE"u8);
    w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
    w.Write("data"u8); w.Write(s.Length * 2);
    foreach (var v in s) w.Write(v);
}
