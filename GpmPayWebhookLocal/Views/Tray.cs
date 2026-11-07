using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace GpmPayWebhookLocal.Views;

/// <summary>
/// Biểu tượng dưới khay hệ thống (Windows: system tray · macOS: menu bar · Linux: StatusNotifierItem / AppIndicator).
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _statusItem;
    private readonly NativeMenuItem _muteItem;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action<bool>? MuteToggled;

    public Tray()
    {
        _statusItem = new NativeMenuItem("Đã dừng") { IsEnabled = false };
        _muteItem = new NativeMenuItem("Tắt tiếng") { ToggleType = MenuItemToggleType.CheckBox };
        _muteItem.Click += (_, _) =>
        {
            _muteItem.IsChecked = !_muteItem.IsChecked;
            MuteToggled?.Invoke(_muteItem.IsChecked);
        };

        var open = new NativeMenuItem("Mở cửa sổ");
        open.Click += (_, _) => OpenRequested?.Invoke();
        var exit = new NativeMenuItem("Thoát");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new NativeMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(open);
        menu.Items.Add(_muteItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        using var iconStream = AssetLoader.Open(new Uri("avares://GpmPayWebhookLocal/Assets/app.ico"));
        _icon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "GPMPay Webhook Local",
            Menu = menu,
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => OpenRequested?.Invoke();

        TrayIcon.SetIcons(Application.Current!, [_icon]);
    }

    public void SetMuted(bool muted) => _muteItem.IsChecked = muted;

    public void SetStatus(string status)
    {
        _statusItem.Header = status.Length > 60 ? status[..60] + "…" : status;
        var text = "GPMPay Webhook Local\n" + status;
        _icon.ToolTipText = text.Length > 127 ? text[..127] : text;
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
