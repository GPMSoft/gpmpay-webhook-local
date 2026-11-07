using Avalonia;
using Avalonia.Media;
using GpmPayWebhookLocal.Platform;

namespace GpmPayWebhookLocal;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!SingleInstance.TryAcquire())
        {
            // đã có 1 bản đang chạy (có thể đang ẩn dưới khay) → bảo nó hiện cửa sổ rồi thoát
            SingleInstance.SignalExisting();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Visual Studio / Rider designer cũng dùng hàm này
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "avares://GpmPayWebhookLocal/Assets/Fonts#Be Vietnam Pro",
            })
            .LogToTrace();
}
