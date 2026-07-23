using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ItemsProperty)
        {
            if (_observed is not null) _observed.CollectionChanged -= OnItemsChanged;
            _observed = Items as INotifyCollectionChanged;
            if (_observed is not null) _observed.CollectionChanged += OnItemsChanged;
            InvalidateVisual();
        }
    }

    private void OnItemsChanged(object? s, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    /// <summary>Reset the view to fit the whole reference frame, centred with a small margin.</summary>
    public void FitView()
    {
        double bw = Bounds.Width, bh = Bounds.Height;
        double rw = Math.Max(1, RefWidth), rh = Math.Max(1, RefHeight);
        _scale = Math.Min(bw / rw, bh / rh) * 0.92;
        _offset = new Point((bw - rw * _scale) / 2, (bh - rh * _scale) / 2);
        InvalidateVisual();
    }

    private void EnsureView()
    {
        // auto-fit only when never fitted yet, or the reference/document changed (not on every resize,
        // so the user's zoom/pan survives). A first fit also needs real Bounds.
        bool refChanged = RefWidth != _lastRefW || RefHeight != _lastRefH;
        if ((_scale <= 0 || refChanged) && Bounds.Width > 0 && Bounds.Height > 0)
        {
            _lastRefW = RefWidth; _lastRefH = RefHeight;
            FitView();
        }
    }

    public override void Render(DrawingContext ctx)
    {
        EnsureView();
        double scale = _scale, ox = _offset.X, oy = _offset.Y;

        // backdrop (a neutral game-ish dark) + reference frame
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1d)), new Rect(Bounds.Size));
        var frame = new Rect(ox, oy, RefWidth * scale, RefHeight * scale);
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x1c, 0x22, 0x2c)), frame);
        ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x55, 0x66, 0x77)), 1), frame);

        // clip drawing to the control so parked/off-screen elements can't paint over the panels
        using var clip = ctx.PushClip(new Rect(Bounds.Size));

        if (Items is null) return;
        foreach (var it in Items)
        {
            var dest = new Rect(ox + it.X * scale, oy + it.Y * scale, it.W * scale, it.H * scale);
            if (it.Atlas is { } bmp && it.SrcW > 0 && it.SrcH > 0)
            {
                var src = new Rect(it.SrcX, it.SrcY, it.SrcW, it.SrcH);
                try
                {
                    if (it.Tint is { } tint && tint != Colors.White)
                    {
                        using (ctx.PushOpacity(tint.A / 255.0))
                            ctx.DrawImage(bmp, src, dest);
                    }
                    else ctx.DrawImage(bmp, src, dest);
                }
                catch { /* a bad crop rect must not kill the whole render */ }
            }
            else if (ShowBounds || it.Atlas is null)
            {
                // no texture (or atlas missing): a faint placeholder box so the element is still visible
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(0x22, 0x8a, 0xd0, 0xff)), dest);
                ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x8a, 0xd0, 0xff)), 1), dest);
            }
        }

        // 16:9 screen guide inside the 4:3 design space (League composites the HUD onto a 16:9 screen)
        if (ShowSafeArea)
        {
            double sw = RefWidth, sh = RefWidth * 9.0 / 16.0;
            if (sh > RefHeight) { sh = RefHeight; sw = RefHeight * 16.0 / 9.0; }
            double sx = (RefWidth - sw) / 2, sy = (RefHeight - sh) / 2;
            var guide = new Rect(ox + sx * scale, oy + sy * scale, sw * scale, sh * scale);
            ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xB4, 0x54)), 1.5,
                dashStyle: new DashStyle(new double[] { 4, 4 }, 0)), guide);
            var txt = new FormattedText("16:9 screen", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 11, new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xB4, 0x54)));
            ctx.DrawText(txt, new Point(guide.X + 4, guide.Y + 2));
        }

        // selection outline
        if (SelectedHash != 0 && Items.FirstOrDefault(i => i.Element.PathHash == SelectedHash) is { } sel)
        {
            var r = new Rect(ox + sel.X * scale, oy + sel.Y * scale, sel.W * scale, sel.H * scale);
            ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), 4), r.Inflate(1));
            ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(0x35, 0xd0, 0x8a)), 2), r);
        }
    }

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
