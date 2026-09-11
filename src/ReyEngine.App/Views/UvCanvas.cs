using System;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ReyEngine.App.Views;

/// <summary>
/// M492: draws a mesh's UV layout as a wireframe over the 0..1 square.
///
/// <para>The unit square is the point of the display: a UV that leaves it wraps (or clamps, if the sampler
/// says so), and "the decals repeat" turned out to be exactly that condition. So the square is drawn as a
/// fixed reference and the UVs are framed around it, rather than the view auto-fitting to the data - an
/// auto-fit would rescale until every layout looked equally fine and hide the one thing worth seeing.</para>
///
/// <para>Segments arrive pre-flattened as pairs (a0,b0,a1,b1,...) in UV space. Building them is the view
/// model's job because it also caps how many are produced; a mapgeo mesh can carry hundreds of thousands of
/// triangles and drawing all of them would stall the UI thread for no extra information.</para>
/// </summary>
public sealed class UvCanvas : Control
{
    public static readonly StyledProperty<Vector2[]?> SegmentsProperty =
        AvaloniaProperty.Register<UvCanvas, Vector2[]?>(nameof(Segments));
    public static readonly StyledProperty<Vector2[]?> CompareSegmentsProperty =
        AvaloniaProperty.Register<UvCanvas, Vector2[]?>(nameof(CompareSegments));

    /// <summary>The channel under edit, drawn in the accent colour.</summary>
    public Vector2[]? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    /// <summary>An optional second channel drawn dimmer behind it, for comparing UV0 against UV7.</summary>
    public Vector2[]? CompareSegments { get => GetValue(CompareSegmentsProperty); set => SetValue(CompareSegmentsProperty, value); }

    private static IBrush Background => ReyEngine.App.Services.ThemeService.Brush("ReyBgBrush", "#10141B");   // M686: the palette's ground
    private static readonly IBrush InsideFill = new SolidColorBrush(Color.FromArgb(28, 90, 160, 220));
    private static readonly IPen UnitPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 120, 170, 210)), 1.4);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 130, 150, 170)), 1);
    private static readonly IPen MainPen = new Pen(new SolidColorBrush(Color.FromArgb(210, 0xE0, 0x6C, 0x75)), 1);
    private static readonly IPen ComparePen = new Pen(new SolidColorBrush(Color.FromArgb(110, 0x53, 0xC6, 0x7A)), 1);

    static UvCanvas() => AffectsRender<UvCanvas>(SegmentsProperty, CompareSegmentsProperty);

    public override void Render(DrawingContext ctx)
    {
        var bounds = Bounds;
        ctx.FillRectangle(Background, new Rect(bounds.Size));
        if (bounds.Width < 8 || bounds.Height < 8) return;

        // Frame a window slightly larger than 0..1 so UVs that just escape the square are visible rather
        // than clipped at the edge — the interesting case sits just outside.
        const float Pad = 0.35f;
        var (min, max) = Extent(Segments, CompareSegments, Pad);

        double side = Math.Min(bounds.Width, bounds.Height) - 16;
        double left = (bounds.Width - side) / 2, top = (bounds.Height - side) / 2;
        float spanX = Math.Max(1e-6f, max.X - min.X), spanY = Math.Max(1e-6f, max.Y - min.Y);
        float span = Math.Max(spanX, spanY);                       // uniform, or the layout shears

        Point Map(Vector2 uv) => new(
            left + (uv.X - min.X) / span * side,
            top + (uv.Y - min.Y) / span * side);                   // v down: texture space, as sampled

        // the unit square, and its quarter grid
        var p00 = Map(Vector2.Zero);
        var p11 = Map(Vector2.One);
        var unit = new Rect(p00, p11);
        ctx.FillRectangle(InsideFill, unit);
        for (int i = 1; i < 4; i++)
        {
            var h = Map(new Vector2(0, i / 4f));
            var v = Map(new Vector2(i / 4f, 0));
            ctx.DrawLine(GridPen, new Point(unit.Left, h.Y), new Point(unit.Right, h.Y));
            ctx.DrawLine(GridPen, new Point(v.X, unit.Top), new Point(v.X, unit.Bottom));
        }
        ctx.DrawRectangle(UnitPen, unit);

        Draw(ctx, CompareSegments, ComparePen, Map);
        Draw(ctx, Segments, MainPen, Map);
    }

    private static void Draw(DrawingContext ctx, Vector2[]? segments, IPen pen, Func<Vector2, Point> map)
    {
        if (segments is null) return;
        for (int i = 0; i + 1 < segments.Length; i += 2)
            ctx.DrawLine(pen, map(segments[i]), map(segments[i + 1]));
    }

    /// <summary>The drawn window: always contains the unit square, grown to hold the data plus a margin.</summary>
    private static (Vector2 Min, Vector2 Max) Extent(Vector2[]? a, Vector2[]? b, float pad)
    {
        var min = new Vector2(-pad, -pad);
        var max = new Vector2(1 + pad, 1 + pad);
        foreach (var set in new[] { a, b })
        {
            if (set is null) continue;
            foreach (var uv in set)
            {
                if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) continue;
                min = Vector2.Min(min, uv);
                max = Vector2.Max(max, uv);
            }
        }
        return (min, max);
    }
}
