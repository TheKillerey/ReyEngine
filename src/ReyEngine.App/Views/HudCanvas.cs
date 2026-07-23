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

    public IEnumerable<HudDrawItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public double RefWidth { get => GetValue(RefWidthProperty); set => SetValue(RefWidthProperty, value); }
    public double RefHeight { get => GetValue(RefHeightProperty); set => SetValue(RefHeightProperty, value); }
    public uint SelectedHash { get => GetValue(SelectedHashProperty); set => SetValue(SelectedHashProperty, value); }
    public bool ShowBounds { get => GetValue(ShowBoundsProperty); set => SetValue(ShowBoundsProperty, value); }

    /// <summary>Raised with the clicked element's path hash (0 = clicked empty space).</summary>
    public event Action<uint>? ElementPicked;

    static HudCanvas()
    {
        AffectsRender<HudCanvas>(ItemsProperty, SelectedHashProperty, RefWidthProperty, RefHeightProperty, ShowBoundsProperty);
    }

    private INotifyCollectionChanged? _observed;

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

    // fit the reference rect into the control, centred (letterbox)
    private (double scale, double ox, double oy) Fit()
    {
        double bw = Bounds.Width, bh = Bounds.Height;
        double rw = Math.Max(1, RefWidth), rh = Math.Max(1, RefHeight);
        double scale = Math.Min(bw / rw, bh / rh);
        return (scale, (bw - rw * scale) / 2, (bh - rh * scale) / 2);
    }

    public override void Render(DrawingContext ctx)
    {
        var (scale, ox, oy) = Fit();

        // backdrop (a neutral game-ish dark) + reference frame
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1d)), new Rect(Bounds.Size));
        var frame = new Rect(ox, oy, RefWidth * scale, RefHeight * scale);
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x1c, 0x22, 0x2c)), frame);

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

        // selection outline
        if (SelectedHash != 0 && Items.FirstOrDefault(i => i.Element.PathHash == SelectedHash) is { } sel)
        {
            var r = new Rect(ox + sel.X * scale, oy + sel.Y * scale, sel.W * scale, sel.H * scale);
            ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(0x35, 0xd0, 0x8a)), 2), r);
            ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), 4), r.Inflate(1));
        }
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Items is null) return;
        var (scale, ox, oy) = Fit();
        var p = e.GetPosition(this);
        double rx = (p.X - ox) / scale, ry = (p.Y - oy) / scale;

        // topmost hit = last in layer order that contains the point
        uint hit = 0;
        foreach (var it in Items)
            if (rx >= it.X && rx <= it.X + it.W && ry >= it.Y && ry <= it.Y + it.H)
                hit = it.Element.PathHash;
        ElementPicked?.Invoke(hit);
    }
}
