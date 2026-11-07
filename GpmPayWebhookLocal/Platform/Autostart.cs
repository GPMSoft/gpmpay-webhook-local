using System.Security;

namespace GpmPayWebhookLocal.Platform;

/// <summary>
/// Khởi động cùng hệ thống, chạy ẩn dưới khay (tham số --minimized).
/// <list type="bullet">
/// <item>Windows: HKCU\Software\Microsoft\Windows\CurrentVersion\Run</item>
/// <item>macOS: ~/Library/LaunchAgents/com.gpmsoftwares.gpmpay-webhook-local.plist</item>
/// <item>Linux: ~/.config/autostart/gpmpay-webhook-local.desktop (chuẩn XDG)</item>
/// </list>
/// </summary>
public static class Autostart
{
    private const string AppName = "GpmPayWebhookLocal";
    private const string MacLabel = "com.gpmsoftwares.gpmpay-webhook-local";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string MacPlist => Path.Combine(Home, "Library", "LaunchAgents", MacLabel + ".plist");

    private static string LinuxDesktopFile =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config"),
            "autostart", "gpmpay-webhook-local.desktop");

    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Không xác định được đường dẫn app.");

    /// <summary>Tên hệ điều hành để hiển thị trên giao diện ("Khởi động cùng ...").</summary>
    public static string OsName =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "hệ thống";

    public static bool IsEnabled
    {
        get
        {
            try
            {
#if WINDOWS
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                return key?.GetValue(AppName) is string;
#else
                return File.Exists(OperatingSystem.IsMacOS() ? MacPlist : LinuxDesktopFile);
#endif
            }
            catch { return false; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
#if WINDOWS
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue(AppName, $"\"{ExePath}\" --minimized");
        else key.DeleteValue(AppName, false);
#else
        var file = OperatingSystem.IsMacOS() ? MacPlist : LinuxDesktopFile;
        if (!enabled)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, OperatingSystem.IsMacOS() ? MacPlistContent() : LinuxDesktopContent());
#endif
    }

    private static string MacPlistContent() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{MacLabel}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{SecurityElement.Escape(ExePath)}</string>
                <string>--minimized</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>ProcessType</key>
            <string>Interactive</string>
        </dict>
        </plist>
        """;

    private static string LinuxDesktopContent() => $"""
        [Desktop Entry]
        Type=Application
        Name=GPMPay Webhook Local
        Comment=Nhận webhook GPM Pay và đọc số tiền qua loa
        Exec="{ExePath.Replace("\"", "\\\"")}" --minimized
        Terminal=false
        X-GNOME-Autostart-enabled=true
        """;
}
