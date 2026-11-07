using System.Diagnostics;
using Avalonia.Threading;

namespace GpmPayWebhookLocal.Controls;

/// <summary>Hiệu ứng chuyển động đơn giản theo khung hình — không dùng reflection, an toàn với Native AOT.</summary>
public static class Tween
{
    /// <summary>Chạy <paramref name="apply"/>(0→1 đã qua easing) trong <paramref name="duration"/>. Dispose để dừng giữa chừng.</summary>
    public static IDisposable Run(TimeSpan duration, Func<double, double> ease, Action<double> apply)
    {
        var sw = Stopwatch.StartNew();
        apply(ease(0));
        DispatcherTimer? timer = null;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            var t = Math.Min(1, sw.Elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            apply(ease(t));
            if (t >= 1) timer!.Stop();
        });
        timer.Start();
        return new Stopper(timer);
    }

    /// <summary>Lặp vô hạn, <paramref name="apply"/> nhận số ms đã trôi qua.</summary>
    public static IDisposable Loop(TimeSpan interval, Action<double> apply)
    {
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer(interval, DispatcherPriority.Render, (_, _) => apply(sw.Elapsed.TotalMilliseconds));
        timer.Start();
        return new Stopper(timer);
    }

    /// <summary>Tương đương ExponentialEase EaseOut của WPF.</summary>
    public static Func<double, double> ExpOut(double exponent) =>
        t => t >= 1 ? 1 : 1 - (Math.Exp(exponent * (1 - t)) - 1) / (Math.Exp(exponent) - 1);

    private sealed class Stopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
