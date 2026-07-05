using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ReyEngine.App.Views;

/// <summary>
/// M46: read-only curve display for the Particle Editor — time 0..1 across, value range auto-fit,
/// one polyline per component channel (X/R red, Y/G green, Z/B blue, W/A grey) with key dots.
/// </summary>
public sealed class CurvePreview : Control
{
    public static readonly StyledProperty<float[]?> TimesProperty =
        AvaloniaProperty.Register<CurvePreview, float[]?>(nameof(Times));
    public static readonly StyledProperty<float[][]?> ChannelsProperty =
        AvaloniaProperty.Register<CurvePreview, float[][]?>(nameof(Channels));

    public float[]? Times { get => GetValue(TimesProperty); set => SetValue(TimesProperty, value); }
    public float[][]? Channels { get => GetValue(ChannelsProperty); set => SetValue(ChannelsProperty, value); }

    private static readonly IBrush[] ChannelBrushes =
    {
        new SolidColorBrush(Color.FromRgb(0xE5, 0x5B, 0x66)),   // X / R
        new SolidColorBrush(Color.FromRgb(0x53, 0xC6, 0x7A)),   // Y / G
        new SolidColorBrush(Color.FromRgb(0x4C, 0x9F, 0xE8)),   // Z / B
        new SolidColorBrush(Color.FromRgb(0xB9, 0xC2, 0xCC)),   // W / A
    };
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 130, 150, 170)), 1);

    static CurvePreview()
    {
        AffectsRender<CurvePreview>(TimesProperty, ChannelsProperty);
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        // frame + quarter grid
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1B)), new Rect(b.Size));
        for (int i = 1; i < 4; i++)
        {
            double x = b.Width * i / 4.0, y = b.Height * i / 4.0;
            ctx.DrawLine(GridPen, new Point(x, 0), new Point(x, b.Height));
            ctx.DrawLine(GridPen, new Point(0, y), new Point(b.Width, y));
        }

        var times = Times; var channels = Channels;
        if (times is null || channels is null || times.Length == 0) return;

        // value range across all channels (padded; degenerate range -> ±0.5)
        float min = float.MaxValue, max = float.MinValue;
        foreach (var ch in channels)
            foreach (var v in ch) { if (v < min) min = v; if (v > max) max = v; }
        if (min > max) return;
        if (max - min < 1e-6f) { min -= 0.5f; max += 0.5f; }
        float pad = (max - min) * 0.08f; min -= pad; max += pad;

        float t0 = times[0], t1 = times[^1];
        if (t1 - t0 < 1e-6f) t1 = t0 + 1f;

        for (int c = 0; c < channels.Length && c < 4; c++)
        {
            var pen = new Pen(ChannelBrushes[c], 1.6);
            var ch = channels[c];
            int n = Math.Min(times.Length, ch.Length);
            Point Map(int i) => new(
                (times[i] - t0) / (t1 - t0) * b.Width,
                b.Height - (ch[i] - min) / (max - min) * b.Height);
            for (int i = 1; i < n; i++) ctx.DrawLine(pen, Map(i - 1), Map(i));
            for (int i = 0; i < n; i++)
            {
                var p = Map(i);
                ctx.DrawEllipse(ChannelBrushes[c], null, p, 2.4, 2.4);
            }
        }

        // min/max labels
        var tf = new Typeface("Consolas");
        var fmtMax = new FormattedText($"{max:0.###}", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, tf, 10, ChannelBrushes[3]);
        var fmtMin = new FormattedText($"{min:0.###}", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, tf, 10, ChannelBrushes[3]);
        ctx.DrawText(fmtMax, new Point(4, 2));
        ctx.DrawText(fmtMin, new Point(4, b.Height - 14));
    }
}
