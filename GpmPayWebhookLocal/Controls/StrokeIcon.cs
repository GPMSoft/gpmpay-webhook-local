using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace GpmPayWebhookLocal.Controls;

/// <summary>Biểu tượng nét (viewBox 24×24, nét 2px, bo tròn) — màu lấy theo Foreground kế thừa từ control chứa nó.</summary>
public sealed class StrokeIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<StrokeIcon, Geometry?>(nameof(Data));

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    static StrokeIcon()
    {
        AffectsRender<StrokeIcon>(DataProperty, TextElement.ForegroundProperty);
        WidthProperty.OverrideDefaultValue<StrokeIcon>(22);
        HeightProperty.OverrideDefaultValue<StrokeIcon>(22);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is not { } geometry || TextElement.GetForeground(this) is not { } brush) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        var dx = (Bounds.Width - 24 * scale) / 2;
        var dy = (Bounds.Height - 24 * scale) / 2;
        var pen = new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            context.DrawGeometry(null, pen, geometry);
    }
}
