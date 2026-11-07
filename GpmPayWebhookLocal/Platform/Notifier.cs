#if WINDOWS
using Windows.UI.Notifications;
#endif
using GpmPayWebhookLocal.Services;

namespace GpmPayWebhookLocal.Platform;

/// <summary>
/// Thông báo hệ thống khi cửa sổ đang ẩn dưới khay.
/// Windows: toast (đăng ký AppUserModelID trong HKCU, không cần shortcut / quyền Admin) ·
/// macOS: osascript "display notification" · Linux: notify-send (libnotify).
/// </summary>
public static class Notifier
{
#pragma warning disable CS0067 // macOS / Linux: lệnh thông báo không báo lại khi người dùng bấm
    /// <summary>Bấm vào thông báo (hiện chỉ hỗ trợ trên Windows).</summary>
    public static event Action? Activated;
#pragma warning restore CS0067

#if WINDOWS
    private const string Aumid = "GPMSoftwares.GpmPayWebhookLocal";
    private static ToastNotifier? _toast;
    private static bool _initTried;

    private static ToastNotifier? Toast()
    {
        if (_initTried) return _toast;
        _initTried = true;
        try
        {
            var iconPath = Path.Combine(AppSettings.DataDir, "app.png");
            if (!File.Exists(iconPath))
            {
                using var src = Avalonia.Platform.AssetLoader.Open(new Uri("avares://GpmPayWebhookLocal/Assets/app.png"));
                using var dst = File.Create(iconPath);
                src.CopyTo(dst);
            }
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}"))
            {
                key.SetValue("DisplayName", "GPMPay Webhook Local");
                key.SetValue("IconUri", iconPath);
            }
            _toast = ToastNotificationManager.CreateToastNotifier(Aumid);
        }
        catch { _toast = null; }
        return _toast;
    }
#endif

    public static void Show(string title, string body)
    {
        try
        {
#if WINDOWS
            if (Toast() is not { } notifier) return;
            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var texts = xml.GetElementsByTagName("text");
            texts[0].AppendChild(xml.CreateTextNode(title));
            texts[1].AppendChild(xml.CreateTextNode(body));
            var toast = new ToastNotification(xml) { ExpirationTime = DateTimeOffset.Now.AddMinutes(5) };
            toast.Activated += (_, _) => Activated?.Invoke();
            notifier.Show(toast);
#else
            if (OperatingSystem.IsMacOS())
            {
                // truyền chữ qua argv của AppleScript → không phải escape, không bị chèn lệnh
                ProcessUtil.Fire("/usr/bin/osascript",
                    "-e", "on run argv",
                    "-e", "display notification (item 2 of argv) with title (item 1 of argv)",
                    "-e", "end run",
                    title, body);
            }
            else if (ProcessUtil.FindExecutable("notify-send") is { } notifySend)
            {
                ProcessUtil.Fire(notifySend, "-a", "GPMPay Webhook Local", title, body);
            }
#endif
        }
        catch { /* thông báo là phụ, lỗi thì bỏ qua */ }
    }
}
