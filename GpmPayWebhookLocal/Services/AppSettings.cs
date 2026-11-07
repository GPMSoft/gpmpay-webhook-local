using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GpmPayWebhookLocal.Platform;

namespace GpmPayWebhookLocal.Services;

/// <summary>
/// Cấu hình lưu tại &lt;DataDir&gt;/settings.json. API token được mã hoá
/// (Windows: DPAPI theo user; macOS/Linux: AES-GCM với khoá riêng chmod 600).
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Windows: %LocalAppData%\GpmPayWebhookLocal · macOS: ~/Library/Application Support/GpmPayWebhookLocal ·
    /// Linux: ~/.local/share/GpmPayWebhookLocal. Đặt biến môi trường GPMPAY_LOCAL_DATA_DIR để dùng thư mục khác (dev/test).
    /// </summary>
    public static readonly string DataDir =
        Environment.GetEnvironmentVariable("GPMPAY_LOCAL_DATA_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create), "GpmPayWebhookLocal");

    private static readonly string FilePath = Path.Combine(DataDir, "settings.json");

    public string? ApiTokenProtected { get; set; }

    /// <summary>Mã định danh máy — dùng làm dấu trong URL webhook để nhận ra webhook cũ của app.</summary>
    public string InstanceId { get; set; } = RandomHex(6);

    /// <summary>Secret HMAC dùng khi đăng ký webhook trên GPM Pay.</summary>
    public string WebhookSecret { get; set; } = RandomHex(32);

    public int Port { get; set; } = 47123;
    public double Volume { get; set; } = 0.8;
    public bool Muted { get; set; }

    /// <summary>Lưu dạng chuỗi để tương thích file cấu hình của bản WPF cũ ("Auto" / "Windows" / "Espeak").</summary>
    [JsonPropertyName("VoiceEngine")]
    public string VoiceEngineName { get; set; } = nameof(Services.VoiceEngine.Auto);

    public bool ReadSimulated { get; set; } = true;
    public bool AutoRun { get; set; } = true;
    public string? LastWebhookId { get; set; }

    /// <summary>Đã áp dụng mặc định "Khởi động cùng hệ thống" (bật) lần đầu chưa.</summary>
    public bool StartupDefaultApplied { get; set; }

    [JsonIgnore]
    public VoiceEngine VoiceEngine
    {
        get => VoiceEngineName switch
        {
            "System" or "Windows" => VoiceEngine.System,
            "Builtin" or "Espeak" => VoiceEngine.Builtin, // eSpeak NG đã bỏ → dùng giọng "Có sẵn"
            _ => VoiceEngine.Auto,
        };
        set => VoiceEngineName = value.ToString();
    }

    [JsonIgnore]
    public string ApiToken
    {
        get => SecretProtector.Unprotect(ApiTokenProtected);
        set => ApiTokenProtected = string.IsNullOrWhiteSpace(value) ? null : SecretProtector.Protect(value.Trim());
    }

    [JsonIgnore]
    public bool RunAtStartup
    {
        get => Autostart.IsEnabled;
        set => Autostart.SetEnabled(value);
    }

    public static AppSettings Load()
    {
        AppSettings? s = null;
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJson.Default.AppSettings);
        }
        catch { /* file hỏng → dùng mặc định */ }
        s ??= new AppSettings();

        // mặc định bật "Khởi động cùng hệ thống" (chỉ áp dụng 1 lần, sau đó tôn trọng lựa chọn của người dùng).
        // Bản Debug không tự đăng ký để khỏi trỏ startup vào thư mục bin của máy dev.
#if !DEBUG
        if (!s.StartupDefaultApplied)
        {
            try { s.RunAtStartup = true; } catch { }
            s.StartupDefaultApplied = true;
        }
#endif
        s.Save();
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppJson.Default.AppSettings));
        FileSecurity.RestrictToOwner(FilePath);
    }

    private static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
}
