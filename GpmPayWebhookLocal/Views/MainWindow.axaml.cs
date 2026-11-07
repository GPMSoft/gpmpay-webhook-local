using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GpmPayWebhookLocal.Controls;
using GpmPayWebhookLocal.Platform;
using GpmPayWebhookLocal.Services;

namespace GpmPayWebhookLocal.Views;

public sealed record TxRow(string Content, string Amount, IBrush AmountBrush, string Meta, string Source);
public sealed record LogLine(string Time, string Text, IBrush Brush);

public partial class MainWindow : Window
{
    /// <summary>Định dạng tiền kiểu Việt Nam (1.250.000) mà không cần dữ liệu ICU (app chạy InvariantGlobalization).</summary>
    private static readonly NumberFormatInfo Vi = new() { NumberGroupSeparator = ".", NumberDecimalSeparator = "," };

    private static readonly Color Body = Rgb(0x0F6B57), BodyDeep = Rgb(0x0A4D3F), OnBody2 = Rgb(0xCDEBE2),
        Amber = Rgb(0xF5A524), LedRun = Rgb(0x43E09B), LedErr = Rgb(0xFF6B5E), Out = Rgb(0xB4530A), Chip = Rgb(0xE3F4EE);

    private static readonly IBrush InkBrush = B(0x0B1F1A), BodyBrush = B(0x0F6B57), OutBrush = B(0xB4530A);

    private readonly AppSettings _settings;
    private readonly SpeechService _speech;
    private readonly AppController _controller;
    private readonly TransactionStore _store = TransactionStore.Load();
    private readonly Tray _tray = new();
    private readonly ObservableCollection<TxRow> _txRows = [];
    private readonly ObservableCollection<LogLine> _logs = [];
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly ScaleTransform _haloScale = new();

    private IDisposable? _ledPulse;
    private IDisposable? _amountAnim;
    private bool _loading = true;
    private bool _trayHintShown;
    private bool _busy;
    private string? _openSheet;

    public bool IsExiting { get; private set; }

    /// <summary>Chỉ cho designer của IDE.</summary>
    public MainWindow() : this(new AppSettings(), true) { }

    public MainWindow(AppSettings settings, bool startMinimized)
    {
        InitializeComponent();
        _settings = settings;

        _speech = new SpeechService(msg => Ui(() => AddLog(msg)));
        _controller = new AppController(settings);

        LedHalo.RenderTransform = _haloScale;
        TxList.ItemsSource = _txRows;
        LogList.ItemsSource = _logs;

        // ── nhãn theo hệ điều hành
        EngineSystemItem.Content = _speech.SystemLabel;
        StartupLabel.Text = $"Khởi động cùng {Autostart.OsName}, chạy ẩn dưới khay";

        // ── nạp cấu hình
        TokenBox.Text = settings.ApiToken;
        PortBox.Text = settings.Port.ToString();
        ChkSimulated.IsChecked = settings.ReadSimulated;
        ChkAutoRun.IsChecked = settings.AutoRun;
        ChkStartup.IsChecked = settings.RunAtStartup;
        EngineBox.SelectedItem = EngineBox.Items.OfType<ComboBoxItem>().First(i => (string?)i.Tag == settings.VoiceEngine.ToString());
        VolumeSlider.Value = Math.Round(settings.Volume * 100);
        _speech.Volume = settings.Volume;
        _speech.Muted = settings.Muted;
        _speech.Engine = settings.VoiceEngine;
        _tray.SetMuted(settings.Muted);
        foreach (var tx in _store.Items) _txRows.Add(ToRow(tx));
        _loading = false;

        UpdateMuteUi();
        UpdateVolumeText();
        UpdateVoiceUi();
        UpdateDisplay(animate: false);
        UpdateStatusUi("Đã dừng", ConnState.Stopped);
        UpdateRunningUi();
        PortText.Text = settings.Port.ToString();

        // ── sự kiện
        VolumeSlider.ValueChanged += Volume_Changed;
        EngineBox.SelectionChanged += Engine_Changed;
        RootBody.PointerPressed += Body_PointerPressed;
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);

        _controller.Log += msg => Ui(() => AddLog(msg));
        _controller.StatusChanged += (text, state) => Ui(() => UpdateStatusUi(text, state));
        _controller.WebhookChanged += (url, id) => Ui(() =>
        {
            UrlText.Text = url ?? "—";
            ToolTip.SetTip(UrlText, url);
            WebhookIdText.Text = ShortId(id);
        });
        _controller.RunningChanged += _ => Ui(UpdateRunningUi);
        _controller.HealthChecked += (time, ok) => Ui(() => HealthText.Text = $"{time:HH:mm:ss} · {(ok ? "tốt" : "lỗi")}");
        _controller.TransactionReceived += p => Ui(() => OnTransaction(p));
        _speech.SpeakingChanged += speaking => Ui(() => SetSpeaking(speaking));

        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += ExitApp;
        _tray.MuteToggled += muted => { if (_speech.Muted != muted) ToggleMute(); };

        _clock.Tick += (_, _) => { UpdateUptime(); UpdateDisplay(animate: false); };
        _clock.Start();

        Closing += (_, e) =>
        {
            if (IsExiting) return;
            e.Cancel = true;
            HideToTray();
        };

        if (!startMinimized) Show();
        // khởi động cùng máy (chạy ẩn) → không hiện trên Dock. Post để chạy sau khi Avalonia khởi tạo NSApplication
        // xong (lúc đó Avalonia tự đặt chế độ Dock mặc định, gọi sớm hơn sẽ bị ghi đè).
        else Dispatcher.UIThread.Post(() => { if (!IsVisible) MacDock.SetVisible(false); }, DispatcherPriority.Background);
        AddLog("Ứng dụng đã khởi động.");

        if (settings.AutoRun && !string.IsNullOrWhiteSpace(settings.ApiToken))
            Dispatcher.UIThread.Post(() => _ = StartAsync());
        else if (string.IsNullOrWhiteSpace(settings.ApiToken))
            Dispatcher.UIThread.Post(() => ShowSheet("config"));
    }

    // ═════════ Lưới loa ═════════

    private void SetSpeaking(bool speaking) => Grille.IsSpeaking = speaking;

    // ═════════ Đèn trạng thái ═════════

    private void UpdateStatusUi(string text, ConnState state)
    {
        var (led, top) = state switch
        {
            ConnState.Running => (LedRun, "Đang chạy"),
            ConnState.Connecting => (Amber, "Đang kết nối…"),
            ConnState.Error => (LedErr, "Lỗi"),
            _ => (Amber, "Đã dừng"),
        };
        var ledBrush = new ImmutableSolidColorBrush(led);
        Led.Fill = ledBrush;
        LedHalo.Fill = ledBrush;
        PillLed.Fill = ledBrush;
        TopStatus.Text = top;
        StatusFull.Text = text;
        _tray.SetStatus(text);

        var (pillBg, pillFg) = state switch
        {
            ConnState.Running => (Chip, BodyDeep),
            ConnState.Error => (Rgb(0xFDECEA), Rgb(0x8A1C12)),
            _ => (Rgb(0xFFF4DD), Rgb(0x6B4500)),
        };
        StatusPill.Background = new ImmutableSolidColorBrush(pillBg);
        StatusFull.Foreground = new ImmutableSolidColorBrush(pillFg);

        // quầng sáng quanh đèn "thở" khi đang chạy / đang kết nối (1 ↔ 1.45, chu kỳ 2.4s)
        if (state is ConnState.Running or ConnState.Connecting)
        {
            _ledPulse ??= Tween.Loop(TimeSpan.FromMilliseconds(33), ms =>
            {
                var s = 1 + 0.45 * (1 - Math.Cos(ms / 1200 * Math.PI)) / 2;
                _haloScale.ScaleX = _haloScale.ScaleY = s;
            });
        }
        else
        {
            _ledPulse?.Dispose();
            _ledPulse = null;
            _haloScale.ScaleX = _haloScale.ScaleY = 1;
        }
    }

    private void UpdateRunningUi()
    {
        var running = _controller.IsRunning;
        BtnPower.Background = new ImmutableSolidColorBrush(running ? Colors.White : BodyDeep);
        BtnPower.Foreground = new ImmutableSolidColorBrush(running ? Body : OnBody2);
        BtnPower.Classes.Set("raised", running);
        var label = running ? "Dừng nhận webhook" : "Bắt đầu nhận webhook";
        ToolTip.SetTip(BtnPower, label);
        AutomationProperties.SetName(BtnPower, label);
        BtnRecreate.IsEnabled = running;
        if (!running) HealthText.Text = "—";
        UpdateUptime();
    }

    private void UpdateUptime()
    {
        var start = _controller.StartedAt;
        if (start == null) { UptimeText.Text = "—"; return; }
        var d = DateTime.Now - start.Value;
        UptimeText.Text = d.TotalHours >= 1 ? $"{(int)d.TotalHours} giờ {d.Minutes} phút" : $"{Math.Max(d.Minutes, 0)} phút";
    }

    // ═════════ Màn hình số tiền ═════════

    private void UpdateDisplay(bool animate)
    {
        var last = _store.Items.FirstOrDefault();
        if (last == null)
        {
            DispChip.IsVisible = false;
            DispKind.Text = "Chưa có giao dịch";
            DispTime.Text = "";
            DispAmount.Text = "0";
            DispAmount.Foreground = InkBrush;
            DispCurrency.Foreground = BodyBrush;
            DispContent.Text = "Chờ tiền vào…";
        }
        else
        {
            DispChip.IsVisible = !string.IsNullOrEmpty(last.Gateway);
            DispGateway.Text = last.Gateway;
            DispKind.Text = (last.Incoming ? "Tiền vào" : "Tiền ra") + (last.Simulated ? " · giả lập" : "");
            DispTime.Text = FormatTime(last.Time);
            DispAmount.Text = (last.Incoming ? "+" : "−") + Money(last.Amount);
            DispAmount.Foreground = last.Incoming ? InkBrush : OutBrush;
            DispCurrency.Foreground = last.Incoming ? BodyBrush : OutBrush;
            DispContent.Text = string.IsNullOrWhiteSpace(last.Content) ? "(không có nội dung)" : last.Content;
            ToolTip.SetTip(DispContent, string.IsNullOrWhiteSpace(last.Content) ? null : last.Content);
        }

        var (count, sum) = _store.TodayIncoming();
        DispTodayCount.Text = $"Hôm nay · {count} giao dịch";
        DispTodaySum.Text = Money(sum) + " ₫";
        TxTodayCount.Text = $"Hôm nay · {count} giao dịch vào";
        TxTodaySum.Text = Money(sum) + " ₫";
        TxEmpty.IsVisible = _txRows.Count == 0;

        if (animate)
        {
            var slide = (TranslateTransform)DispAmountPanel.RenderTransform!;
            _amountAnim?.Dispose();
            _amountAnim = Tween.Run(TimeSpan.FromMilliseconds(600), Tween.ExpOut(5), t =>
            {
                slide.Y = 8 * (1 - t);
                DispAmountPanel.Opacity = 0.2 + 0.8 * t;
            });
        }
    }

    private void OnTransaction(WebhookPayload p)
    {
        var tx = _store.Add(p);
        _txRows.Insert(0, ToRow(tx));
        while (_txRows.Count > 500) _txRows.RemoveAt(_txRows.Count - 1);
        UpdateDisplay(animate: true);

        var amountText = Money(p.TransferAmount) + " đ";
        AddLog($"Giao dịch {(p.IsIncoming ? "vào" : "ra")} {amountText} ({p.Gateway}, {p.Source}) — {p.Content}");

        if (!p.IsIncoming) return;
        if (p.IsSimulated && !_settings.ReadSimulated) return;
        _speech.Speak($"Đã nhận {VietnameseNumber.ToWords((long)Math.Round(p.TransferAmount))} đồng");
        if (!IsVisible) Notifier.Show($"+{amountText}", $"{p.Gateway} • {p.Content}");
    }

    private static TxRow ToRow(TxRecord t) => new(
        string.IsNullOrWhiteSpace(t.Content) ? "(không có nội dung)" : t.Content,
        (t.Incoming ? "+" : "−") + Money(t.Amount) + " ₫",
        t.Incoming ? BodyBrush : OutBrush,
        $"{FormatTime(t.Time)} · {t.Gateway}",
        t.Simulated ? "Giả lập" : "Thật");

    private static string FormatTime(DateTimeOffset time) =>
        time.LocalDateTime.Date == DateTime.Today
            ? time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : time.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);

    // ═════════ Bảng trượt ═════════

    private void Tab_Click(object? sender, RoutedEventArgs e)
    {
        var key = (string)((Control)sender!).Tag!;
        ShowSheet(_openSheet == key ? null : key);
    }

    private void CloseSheet_Click(object? sender, RoutedEventArgs e) => ShowSheet(null);

    private void ShowSheet(string? key)
    {
        _openSheet = key;
        var sheets = new (string Key, Border Sheet, ToggleButton Tab)[]
        {
            ("config", SheetConfig, TabConfig), ("status", SheetStatus, TabStatus), ("tx", SheetTx, TabTx), ("log", SheetLog, TabLog),
        };
        foreach (var (k, sheet, tab) in sheets)
        {
            var open = k == key;
            tab.IsChecked = open;
            if (!open) { sheet.IsVisible = false; continue; }
            if (sheet.IsVisible) continue;

            sheet.IsVisible = true;
            var slide = sheet.RenderTransform as TranslateTransform ?? new TranslateTransform();
            sheet.RenderTransform = slide;
            Tween.Run(TimeSpan.FromMilliseconds(320), Tween.ExpOut(5), t =>
            {
                slide.Y = 24 * (1 - t);
                sheet.Opacity = t;
            });
        }
        if (key == "config")
        {
            TokenBox.Text = _settings.ApiToken;
            PortBox.Text = _settings.Port.ToString();
            ConfigError.IsVisible = false;
        }
        if (key == "log" && _logs.Count > 0) Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _openSheet != null)
        {
            ShowSheet(null);
            e.Handled = true;
        }
    }

    // ═════════ Khay hệ thống ═════════

    public void ShowFromTray()
    {
        if (IsExiting) return;
        MacDock.SetVisible(true);
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void HideToTray()
    {
        Hide();
        MacDock.SetVisible(false); // macOS: bỏ icon khỏi Dock, chỉ còn trên menu bar (như khay của Windows / Ubuntu)
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            Notifier.Show("GPMPay Webhook Local",
                OperatingSystem.IsMacOS()
                    ? "Ứng dụng vẫn chạy nền trên thanh menu. Bấm vào biểu tượng để mở lại."
                    : "Ứng dụng vẫn chạy nền dưới khay. Bấm vào biểu tượng để mở lại.");
        }
    }

    public async void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        // lỗi dọn dẹp nào cũng không được chặn việc thoát
        try { _tray.SetStatus("Đang thoát..."); } catch { }
        try { await _controller.DisposeAsync(); } catch { }
        try { _speech.Dispose(); } catch { }
        try { _tray.Dispose(); } catch { }
        try { Close(); } catch { }
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }

    private void Hide_Click(object? sender, RoutedEventArgs e) => HideToTray();

    private void Body_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    /// <summary>
    /// Click vào control tương tác thì không kéo cửa sổ. Một số control (ComboBox mở popup khi nhả chuột,
    /// ScrollBar, Slider…) không đánh dấu PointerPressed là đã xử lý, nên BeginMoveDrag sẽ "nuốt" mất cú click.
    /// </summary>
    private bool IsInteractive(Visual? v)
    {
        for (; v != null && v != RootBody; v = v.GetVisualParent())
            if (v is ComboBox or TextBox or Button or Slider or ScrollBar or ListBoxItem or SelectableTextBlock)
                return true;
        return false;
    }

    // ═════════ Chạy / dừng ═════════

    private async void Power_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_controller.IsRunning)
        {
            _busy = true;
            try { await _controller.StopAsync(); } finally { _busy = false; }
        }
        else if (string.IsNullOrWhiteSpace(_settings.ApiToken))
        {
            ShowSheet("config");
            TokenBox.Focus();
        }
        else await StartAsync();
    }

    private async Task StartAsync()
    {
        if (_busy || _controller.IsRunning) return;
        _busy = true;
        try
        {
            await _controller.StartAsync();
        }
        catch (Exception ex)
        {
            AddLog("Không khởi động được: " + ex.Message);
            UpdateStatusUi("Không khởi động được: " + ex.Message, ConnState.Error);
            if (!IsVisible) Notifier.Show("Không khởi động được", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Recreate_Click(object? sender, RoutedEventArgs e)
    {
        AddLog("Yêu cầu tạo lại URL + webhook.");
        _controller.RequestRecreate();
    }

    private async void CopyUrl_Click(object? sender, RoutedEventArgs e)
    {
        if (_controller.WebhookUrl is { } url && await CopyAsync(url)) ToolTip.SetTip(BtnCopyUrl, "Đã copy!");
    }

    // ═════════ Cấu hình ═════════

    private void ToggleToken_Click(object? sender, RoutedEventArgs e)
    {
        TokenBox.RevealPassword = !TokenBox.RevealPassword;
        var label = TokenBox.RevealPassword ? "Ẩn token" : "Hiện token";
        ToolTip.SetTip(BtnEye, label);
        AutomationProperties.SetName(BtnEye, label);
    }

    private void ShowConfigError(string message, Control focus)
    {
        ConfigError.Text = message;
        ConfigError.IsVisible = true;
        focus.Focus();
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var token = (TokenBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowConfigError("Vui lòng nhập API Token (GPM Pay → Tích hợp → API Tokens, cần quyền webhooks:manage).", TokenBox);
            return;
        }
        if (!int.TryParse((PortBox.Text ?? "").Trim(), out var port) || port is < 1024 or > 65535)
        {
            ShowConfigError("Port phải là số từ 1024 đến 65535.", PortBox);
            return;
        }

        var changed = token != _settings.ApiToken || port != _settings.Port;
        _settings.ApiToken = token;
        _settings.Port = port;
        _settings.Save();
        PortText.Text = port.ToString();
        ShowSheet(null);

        if (_busy) return;
        if (_controller.IsRunning && changed)
        {
            AddLog("Cấu hình thay đổi → khởi động lại.");
            _busy = true;
            try { await _controller.StopAsync(); } finally { _busy = false; }
            await StartAsync();
        }
        else if (!_controller.IsRunning)
        {
            await StartAsync();
        }
    }

    private void Option_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ReadSimulated = ChkSimulated.IsChecked == true;
        _settings.AutoRun = ChkAutoRun.IsChecked == true;
        try { _settings.RunAtStartup = ChkStartup.IsChecked == true; }
        catch (Exception ex) { AddLog($"Không cài được khởi động cùng {Autostart.OsName}: " + ex.Message); }
        _settings.Save();
    }

    private void Engine_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || EngineBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var engine = Enum.Parse<VoiceEngine>(tag);
        _speech.Engine = engine;
        _settings.VoiceEngine = engine;
        _settings.Save();
        UpdateVoiceUi();
    }

    // ═════════ Âm thanh ═════════

    private void Mute_Click(object? sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        _speech.Muted = !_speech.Muted;
        _settings.Muted = _speech.Muted;
        _settings.Save();
        _tray.SetMuted(_speech.Muted);
        if (_speech.Muted) SetSpeaking(false);
        UpdateMuteUi();
        UpdateVolumeText();
    }

    private void UpdateMuteUi()
    {
        var muted = _speech.Muted;
        BtnMute.Background = new ImmutableSolidColorBrush(muted ? Amber : BodyDeep);
        BtnMute.Foreground = new ImmutableSolidColorBrush(muted ? Rgb(0x3A2600) : Colors.White);
        BtnMute.Classes.Set("raised", muted);
        MuteIcon.Data = this.FindResource(muted ? "IcMuted" : "IcVolume") as Geometry;
        var label = muted ? "Bật tiếng" : "Tắt tiếng";
        ToolTip.SetTip(BtnMute, label);
        AutomationProperties.SetName(BtnMute, label);
    }

    private void Volume_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _speech.Volume = e.NewValue / 100;
        UpdateVolumeText();
        if (_loading) return;
        _settings.Volume = e.NewValue / 100;
        _settings.Save();
    }

    private void UpdateVolumeText() => VolumeText.Text = _speech.Muted ? "Tắt" : $"{VolumeSlider.Value:0}%";

    private void TestSpeak_Click(object? sender, RoutedEventArgs e) =>
        _speech.Speak($"Đã nhận {VietnameseNumber.ToWords(1_250_000)} đồng", force: true);

    private void UpdateVoiceUi()
    {
        VoiceName.Text = _speech.Describe();
        var warning = _speech.Warning;
        VoiceWarn.IsVisible = warning != null;
        VoiceWarnTitle.Text = warning?.Title;
        VoiceWarnBody.Text = warning?.Body;
    }

    // ═════════ Nhật ký ═════════

    private void AddLog(string msg)
    {
        var brush = msg.StartsWith("Giao dịch vào") ? BodyBrush
            : msg.StartsWith("Lỗi") || msg.StartsWith("Không") || msg.StartsWith("Từ chối") || msg.StartsWith("Chưa cài")
              || msg.Contains("lỗi") || msg.StartsWith("cloudflared") ? OutBrush
            : InkBrush;
        _logs.Add(new LogLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), msg, brush));
        while (_logs.Count > 1000) _logs.RemoveAt(0);
        if (_openSheet == "log") Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private async void CopyLog_Click(object? sender, RoutedEventArgs e)
    {
        if (_logs.Count > 0 && await CopyAsync(string.Join(Environment.NewLine, _logs.Select(l => $"[{l.Time}] {l.Text}"))))
            ToolTip.SetTip(BtnCopyLog, "Đã copy!");
    }

    /// <summary>Gọi khi có exception chưa xử lý trên luồng UI — ghi nhật ký thay vì làm sập app.</summary>
    public void ReportError(Exception ex) => AddLog("Lỗi: " + ex.Message);

    // ═════════ Tiện ích ═════════

    private async Task<bool> CopyAsync(string text)
    {
        try
        {
            if (Clipboard is not { } clipboard) return false;
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            AddLog("Không copy được: " + ex.Message);
            return false;
        }
    }

    private static string Money(decimal v) => v.ToString("#,##0", Vi);

    private static string ShortId(string? id) =>
        string.IsNullOrEmpty(id) ? "—" : id.Length > 14 ? id[..8] + "…" + id[^4..] : id;

    private static Color Rgb(int hex) => Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
    private static IBrush B(int hex) => new ImmutableSolidColorBrush(Rgb(hex));

    private static void Ui(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }
}
