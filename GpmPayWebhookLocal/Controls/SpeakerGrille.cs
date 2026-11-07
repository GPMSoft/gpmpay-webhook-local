using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace GpmPayWebhookLocal.Controls;

/// <summary>
/// Lưới loa 15×6 lỗ. Khi đang đọc: sóng màu lan từ tâm ra ngoài (mỗi lỗ trễ theo khoảng cách tới tâm),
/// tự vẽ trong 1 control thay vì 90 Ellipse + 90 animation.
/// </summary>
public sealed class SpeakerGrille : Control
{
    private const int Cols = 15, Rows = 6;
    private const double Dot = 15, RowHeight = 22, CycleMs = 1100;

    private static readonly Color Hole = Color.Parse("#062F27"), Mint = Color.Parse("#8CF0CF"), Mid = Color.Parse("#1C7A64");
    private static readonly IImmutableSolidColorBrush HoleBrush = new ImmutableSolidColorBrush(Hole);

    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;
    private bool _speaking;

    public SpeakerGrille()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => InvalidateVisual());
    }

    public bool IsSpeaking
    {
        get => _speaking;
        set
        {
            if (_speaking == value) return;
            _speaking = value;
            if (value) { _clock.Restart(); _timer.Start(); }
            else { _timer.Stop(); _clock.Reset(); }
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? Cols * 20 : availableSize.Width, Rows * RowHeight);

    public override void Render(DrawingContext context)
    {
        var cellW = Bounds.Width / Cols;
        var elapsed = _clock.Elapsed.TotalMilliseconds;
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                IBrush brush = HoleBrush;
                if (_speaking)
                {
                    double dx = c - (Cols - 1) / 2.0, dy = (r - (Rows - 1) / 2.0) * 1.15;
                    var local = elapsed - Math.Sqrt(dx * dx + dy * dy) * 60;
                    if (local > 0) brush = new ImmutableSolidColorBrush(ColorAt(local % CycleMs / CycleMs));
                }
                var center = new Point(cellW * (c + 0.5), RowHeight * (r + 0.5));
                context.DrawEllipse(brush, null, center, Dot / 2, Dot / 2);
            }
        }
    }

    /// <summary>Khung hình: 0% tối → 22% bạc hà → 70% xanh vừa → 100% tối, mỗi đoạn ease-out mũ 4.</summary>
    private static Color ColorAt(double p) => p switch
    {
        < 0.22 => Lerp(Hole, Mint, ExpOut(p / 0.22)),
        < 0.70 => Lerp(Mint, Mid, ExpOut((p - 0.22) / 0.48)),
        _ => Lerp(Mid, Hole, ExpOut((p - 0.70) / 0.30)),
    };

    internal static double ExpOut(double t, double k = 4) => 1 - (Math.Exp(k * (1 - t)) - 1) / (Math.Exp(k) - 1);

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
