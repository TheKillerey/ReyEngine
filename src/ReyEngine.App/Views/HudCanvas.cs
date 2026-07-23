using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>
/// M140: draws a parsed HUD to scale — each element's atlas crop at its reference-resolution rectangle,
/// in layer order, letterboxed to fit. Click hit-tests the topmost element and reports it; the selected
/// element gets an outline. Pure viewer (no editing yet); everything scales from the reference size.
/// </summary>
public sealed class HudCanvas : Control
{
    public static readonly StyledProperty<IEnumerable<HudDrawItem>?> ItemsProperty =
        AvaloniaProperty.Register<HudCanvas, IEnumerable<HudDrawItem>?>(nameof(Items));
    public static readonly StyledProperty<double> RefWidthProperty =
        AvaloniaProperty.Register<HudCanvas, double>(nameof(RefWidth), 1600);
    public static readonly StyledProperty<double> RefHeightProperty =
        AvaloniaProperty.Register<HudCanvas, double>(nameof(RefHeight), 1200);
    public static readonly StyledProperty<uint> SelectedHashProperty =
        AvaloniaProperty.Register<HudCanvas, uint>(nameof(SelectedHash));
    public static readonly StyledProperty<bool> ShowBoundsProperty =
        AvaloniaProperty.Register<HudCanvas, bool>(nameof(ShowBounds));
    public static readonly StyledProperty<bool> ShowSafeAreaProperty =
        AvaloniaProperty.Register<HudCanvas, bool>(nameof(ShowSafeArea), true);

    public IEnumerable<HudDrawItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public double RefWidth { get => GetValue(RefWidthProperty); set => SetValue(RefWidthProperty, value); }
    public double RefHeight { get => GetValue(RefHeightProperty); set => SetValue(RefHeightProperty, value); }
    public uint SelectedHash { get => GetValue(SelectedHashProperty); set => SetValue(SelectedHashProperty, value); }
    public bool ShowBounds { get => GetValue(ShowBoundsProperty); set => SetValue(ShowBoundsProperty, value); }
    /// <summary>Draw the 16:9 screen guide inside the 4:3 design space (League runs 16:9).</summary>
    public bool ShowSafeArea { get => GetValue(ShowSafeAreaProperty); set => SetValue(ShowSafeAreaProperty, value); }

    /// <summary>Raised with the clicked element's path hash (0 = clicked empty space).</summary>
    public event Action<uint>? ElementPicked;

    static HudCanvas()
    {
        AffectsRender<HudCanvas>(ItemsProperty, SelectedHashProperty, RefWidthProperty, RefHeightProperty,
            ShowBoundsProperty, ShowSafeAreaProperty);
    }

    private INotifyCollectionChanged? _observed;

    // user view transform (ref-space → control px): px = ref * _scale + _offset. Zero scale = "fit".
    private double _scale;
    private Point _offset;
    private bool _panning;
    private Point _panLast;
    private double _lastRefW, _lastRefH, _lastBoundsW, _lastBoundsH;

    // M140.4: composited element layer — the elements are drawn ONCE into this bitmap (in ref space)
    // and blitted each frame, so panning/zooming a 700-element HUD is one DrawImage, not hundreds.
    private RenderTargetBitmap? _layer;
    private double _layerScale = 1;
    private bool _layerDirty = true;
    private bool _rebuildQueued;

    // reused draw resources (no per-frame allocation)
    private static readonly IBrush FrameBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x1c, 0x22, 0x2c));
    private static readonly IPen FramePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x55, 0x55, 0x66, 0x77)), 1);
    private static readonly IBrush PlaceholderFill = new ImmutableSolidColorBrush(Color.FromArgb(0x22, 0x8a, 0xd0, 0xff));
    private static readonly IPen PlaceholderPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x55, 0x8a, 0xd0, 0xff)), 1);
    private static readonly ImmutableSolidColorBrush GuideBrush = new(Color.FromArgb(0xAA, 0xFF, 0xB4, 0x54));
    private static readonly IPen GuidePen = new ImmutablePen(GuideBrush, 1.5, new ImmutableDashStyle(new double[] { 4, 4 }, 0));
    private static readonly IPen SelPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x35, 0xd0, 0x8a)), 2);
    private static readonly IPen SelShadowPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), 4);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ItemsProperty)
        {
            if (_observed is not null) _observed.CollectionChanged -= OnItemsChanged;
            _observed = Items as INotifyCollectionChanged;
            if (_observed is not null) _observed.CollectionChanged += OnItemsChanged;
            MarkLayerDirty();
        }
        // these change the CACHED element layer (content), not just the view
        else if (e.Property == ShowBoundsProperty || e.Property == RefWidthProperty || e.Property == RefHeightProperty)
            MarkLayerDirty();
    }

    private void OnItemsChanged(object? s, NotifyCollectionChangedEventArgs e) => MarkLayerDirty();

    /// <summary>Schedule a layer rebuild off the render pass, coalescing a burst of collection adds
    /// (Load populates the ObservableCollection one item at a time) into a single rebuild.</summary>
    private void MarkLayerDirty()
    {
        _layerDirty = true;
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _rebuildQueued = false;
            if (_layerDirty) { RebuildLayer(); InvalidateVisual(); }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Composite every element into <see cref="_layer"/> in ref-space, once. Off the render
    /// pass, so CreateDrawingContext is safe.</summary>
    private void RebuildLayer()
    {
        _layerDirty = false;
        if (Items is null) { _layer?.Dispose(); _layer = null; return; }

        double rw = Math.Max(1, RefWidth), rh = Math.Max(1, RefHeight);
        const double maxDim = 2600;   // cap the cache so an exotic reference size can't blow memory
        _layerScale = Math.Min(1.0, maxDim / Math.Max(rw, rh));
        int lw = Math.Max(1, (int)Math.Ceiling(rw * _layerScale)), lh = Math.Max(1, (int)Math.Ceiling(rh * _layerScale));

        try
        {
            _layer?.Dispose();
            _layer = new RenderTargetBitmap(new PixelSize(lw, lh));
            using var dc = _layer.CreateDrawingContext();
            var lb = new Rect(0, 0, lw, lh);
            double ls = _layerScale;
            foreach (var it in Items)
            {
                var dest = new Rect(it.X * ls, it.Y * ls, it.W * ls, it.H * ls);
                if (!RectFinite(dest) || !dest.Intersects(lb)) continue;   // cull parked/off-screen
                if (it.Atlas is { } bmp && it.SrcW > 0 && it.SrcH > 0)
                {
                    var ps = bmp.PixelSize;
                    double sx = Math.Clamp(it.SrcX, 0, ps.Width), sy = Math.Clamp(it.SrcY, 0, ps.Height);
                    double sw = Math.Clamp(it.SrcW, 0, ps.Width - sx), sh = Math.Clamp(it.SrcH, 0, ps.Height - sy);
                    if (sw < 1 || sh < 1) continue;
                    var src = new Rect(sx, sy, sw, sh);
                    try
                    {
                        if (it.Tint is { } tint && tint != Colors.White)
                            using (dc.PushOpacity(tint.A / 255.0)) dc.DrawImage(bmp, src, dest);
                        else dc.DrawImage(bmp, src, dest);
                    }
                    catch { /* a bad crop must not abort the whole layer */ }
                }
                else if (ShowBounds || it.Atlas is null)
                {
                    dc.FillRectangle(PlaceholderFill, dest);
                    dc.DrawRectangle(PlaceholderPen, dest);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_reported) { _reported = true; RenderFailed?.Invoke(ex.ToString()); }
            _layer?.Dispose(); _layer = null;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _layer?.Dispose();
        _layer = null;
    }

    /// <summary>Reset the view to fit the whole reference frame, centred with a small margin.
    /// Public entry (Fit button) — invalidates so the change repaints.</summary>
    public void FitView()
    {
        ComputeFit();
        InvalidateVisual();
    }

    /// <summary>Set the fit transform WITHOUT invalidating — safe to call during a render pass
    /// (Avalonia throws "Visual was invalidated during the render pass" if you invalidate mid-render).</summary>
    private void ComputeFit()
    {
        double bw = Bounds.Width, bh = Bounds.Height;
        double rw = Math.Max(1, RefWidth), rh = Math.Max(1, RefHeight);
        _scale = Math.Min(bw / rw, bh / rh) * 0.92;
        _offset = new Point((bw - rw * _scale) / 2, (bh - rh * _scale) / 2);
    }

    private void EnsureView()
    {
        // auto-fit only when never fitted yet, or the reference/document changed (not on every resize,
        // so the user's zoom/pan survives). A first fit also needs real Bounds. Runs INSIDE Render, so
        // it must not invalidate — ComputeFit only.
        bool refChanged = RefWidth != _lastRefW || RefHeight != _lastRefH;
        if ((_scale <= 0 || refChanged) && Bounds.Width > 0 && Bounds.Height > 0)
        {
            _lastRefW = RefWidth; _lastRefH = RefHeight;
            ComputeFit();
        }
    }

    /// <summary>Fires once with the exception text when a draw throws — so the window can log it
    /// instead of the app dying (there is no UI-thread exception net).</summary>
    public event Action<string>? RenderFailed;
    private bool _reported;

    public override void Render(DrawingContext ctx)
    {
        // backdrop first (always safe), then guard the rest — a draw exception must dim the canvas,
        // not crash the process.
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1d)), new Rect(Bounds.Size));
        try { RenderCore(ctx); }
        catch (Exception ex)
        {
            if (!_reported)
            {
                _reported = true;
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reyengine_hud_render_error.txt"), ex.ToString()); } catch { }
                RenderFailed?.Invoke(ex.ToString());
            }
            try
            {
                var txt = new FormattedText("HUD render error — see the console.",
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    Typeface.Default, 12, new SolidColorBrush(Color.FromRgb(0xFF, 0x7B, 0x72)));
                ctx.DrawText(txt, new Point(12, 12));
            }
            catch { /* even the error text failed — nothing more we can safely do */ }
        }
    }

    private void RenderCore(DrawingContext ctx)
    {
        EnsureView();
        double scale = _scale, ox = _offset.X, oy = _offset.Y;
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) { ComputeFit(); scale = _scale; ox = _offset.X; oy = _offset.Y; }

        // reference frame
        var frame = new Rect(ox, oy, RefWidth * scale, RefHeight * scale);
        ctx.FillRectangle(FrameBrush, frame);
        ctx.DrawRectangle(FramePen, frame);

        using var clip = ctx.PushClip(new Rect(Bounds.Size));
        if (Items is null) return;
        var visible = new Rect(Bounds.Size);

        // blit the cached element layer (composited once) scaled to the frame — one draw, not hundreds
        if (_layer is { } layer)
            ctx.DrawImage(layer, new Rect(0, 0, layer.PixelSize.Width, layer.PixelSize.Height), frame);
        else if (_layerDirty)
            MarkLayerDirty();   // first frame before the layer exists — schedule it

        // 16:9 screen guide inside the 4:3 design space (League composites the HUD onto a 16:9 screen)
        if (ShowSafeArea)
        {
            double sw = RefWidth, sh = RefWidth * 9.0 / 16.0;
            if (sh > RefHeight) { sh = RefHeight; sw = RefHeight * 16.0 / 9.0; }
            double sx = (RefWidth - sw) / 2, sy = (RefHeight - sh) / 2;
            var guide = new Rect(ox + sx * scale, oy + sy * scale, sw * scale, sh * scale);
            ctx.DrawRectangle(GuidePen, guide);
            var txt = new FormattedText("16:9 screen", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 11, GuideBrush);
            ctx.DrawText(txt, new Point(guide.X + 4, guide.Y + 2));
        }

        // selection outline (live — changing selection doesn't rebuild the layer)
        if (SelectedHash != 0 && Items.FirstOrDefault(i => i.Element.PathHash == SelectedHash) is { } sel)
        {
            var r = new Rect(ox + sel.X * scale, oy + sel.Y * scale, sel.W * scale, sel.H * scale);
            if (RectFinite(r) && r.Intersects(visible))
            {
                ctx.DrawRectangle(SelShadowPen, r.Inflate(1));
                ctx.DrawRectangle(SelPen, r);
            }
        }
    }

    private static bool RectFinite(Rect r) =>
        double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height)
        && r.Width >= 0 && r.Height >= 0;

    protected override void OnPointerWheelChanged(Avalonia.Input.PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        double factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        double newScale = Math.Clamp(_scale * factor, 0.05, 40);
        // keep the point under the cursor fixed
        _offset = new Point(p.X - (p.X - _offset.X) * (newScale / _scale),
                            p.Y - (p.Y - _offset.Y) * (newScale / _scale));
        _scale = newScale;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);

        // right / middle button (or space held) → pan
        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            _panning = true;
            _panLast = point.Position;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (Items is null || _scale <= 0) return;
        var p = point.Position;
        double rx = (p.X - _offset.X) / _scale, ry = (p.Y - _offset.Y) / _scale;

        // topmost hit = last in layer order that contains the point
        uint hit = 0;
        foreach (var it in Items)
            if (rx >= it.X && rx <= it.X + it.W && ry >= it.Y && ry <= it.Y + it.H)
                hit = it.Element.PathHash;
        ElementPicked?.Invoke(hit);
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_panning) return;
        var p = e.GetPosition(this);
        _offset += p - _panLast;
        _panLast = p;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panning) { _panning = false; e.Pointer.Capture(null); }
    }
}
