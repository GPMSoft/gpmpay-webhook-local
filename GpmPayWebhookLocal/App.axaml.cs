using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GpmPayWebhookLocal.Platform;
using GpmPayWebhookLocal.Services;
using GpmPayWebhookLocal.Views;

namespace GpmPayWebhookLocal;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // app sống dưới khay → chỉ thoát khi người dùng bấm "Thoát"
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var startMinimized = desktop.Args?.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)) == true;
            var window = new MainWindow(AppSettings.Load(), startMinimized);

            // Cmd+Q (macOS) / đăng xuất → dọn dẹp tunnel, webhook server trước khi thoát
            desktop.ShutdownRequested += (_, e) =>
            {
                if (window.IsExiting) return;
                e.Cancel = true;
                window.ExitApp();
            };

            // mở app lần 2 → bản đang chạy hiện cửa sổ
            SingleInstance.Listen(() => Dispatcher.UIThread.Post(window.ShowFromTray));
            Notifier.Activated += () => Dispatcher.UIThread.Post(window.ShowFromTray);

            // macOS: bấm vào biểu tượng trên Dock khi cửa sổ đang ẩn
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen) window.ShowFromTray();
                };
            }

            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                window.ReportError(e.Exception);
                e.Handled = true;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
