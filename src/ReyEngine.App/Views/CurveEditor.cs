using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ReyEngine.App.Services;

namespace ReyEngine.App.Views;

/// <summary>
/// M718: the particle editor's curve pane, editable - what M46's read-only CurvePreview became once M716
/// gave the pane the width to edit in. Pointer handling follows <see cref="HudCanvas"/>: the wheel zooms
/// about the cursor, a right or middle drag pans, a left press hit-tests into a selection.
///
/// <para>A drag moves a COPY of the keys (<see cref="CurveKeyDrag"/>) and repaints; <see cref="KeyMoved"/>
/// fires once, on release. The host commits through ParticleEditorViewModel.EditCurve, which re-serializes
/// the bin, re-extracts every system and rebuilds the preview - right once per edit, and seconds per
/// pointer move on a map bin.</para>
///
/// <para>When <see cref="IsEditable"/> is false the keys are drawn as rings and cannot be picked up. A
/// read-only bin whose keys dragged and then snapped back would be an editor kinder than the file.</para>
/// </summary>
public sealed class CurveEditor : Control
{
    public static readonly StyledProperty<float[]?> TimesProperty =
        AvaloniaProperty.Register<CurveEditor, float[]?>(nameof(Times));
    public static readonly StyledProperty<float[][]?> ChannelsProperty =
        AvaloniaProperty.Register<CurveEditor, float[][]?>(nameof(Channels));
    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<CurveEditor, bool>(nameof(IsEditable));

    public float[]? Times { get => GetValue(TimesProperty); set => SetValue(TimesProperty, value); }
    public float[][]? Channels { get => GetValue(ChannelsProperty); set => SetValue(ChannelsProperty, value); }
    /// <summary>Bound to the row's CanEditCurve: false for a read-only bin, and for a curve the document
    /// cannot write back.</summary>
    public bool IsEditable { get => GetValue(IsEditableProperty); set => SetValue(IsEditableProperty, value); }

    /// <summary>A drag ended with the key moved: (index, time, every component). Once per gesture.</summary>
    public event Action<int, float, float[]>? KeyMoved;
    /// <summary>A double-click on the graph away from any key: (time, every component).</summary>
    public event Action<float, float[]>? KeyAdded;
    /// <summary>Delete or Backspace with a key selected.</summary>
    public event Action<int>? KeyRemoved;

    private static readonly Color[] ChannelColors =
    {
        Color.FromRgb(0xE5, 0x5B, 0x66),   // X / R
        Color.FromRgb(0x53, 0xC6, 0x7A),   // Y / G
        Color.FromRgb(0x4C, 0x9F, 0xE8),   // Z / B
        Color.FromRgb(0xB9, 0xC2, 0xCC),   // W / A
    };
    private static readonly IBrush[] ChannelBrushes = ChannelColors.Select(c => (IBrush)new ImmutableSolidColorBrush(c)).ToArray();
    private static readonly IPen[] LinePens = ChannelColors.Select(c => (IPen)new ImmutablePen(new ImmutableSolidColorBrush(c), 1.6)).ToArray();
    private static readonly IPen[] RingPens = ChannelColors.Select(c => (IPen)new ImmutablePen(new ImmutableSolidColorBrush(c), 1.4)).ToArray();
    // a hidden channel stays on the graph as a faint line - context, not something to grab
    private static readonly IPen[] GhostPens = ChannelColors.Select(c => (IPen)new ImmutablePen(new ImmutableSolidColorBrush(c, 0.22), 1.2)).ToArray();
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(60, 130, 150, 170)), 1);
    private static readonly IPen LifetimePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(130, 130, 150, 170)), 1);
    private static readonly Typeface Mono = new("Consolas");
    private static Cursor? _hand;

    // the view in curve space; Width/Height are refreshed from Bounds on every use
    private CurveTransform? _view;
    private bool _refit = true;
    private bool _keepView;   // set while our own commit comes back as new arrays
    private readonly bool[] _hidden = new bool[4];
    private int _channelCount;

    private (int Index, int Channel)? _selected, _hover;
    private CurveKeyDrag? _drag;
    private float _grabT, _grabV;   // key minus pointer at the press, so the key does not jump to the cursor
    private Point _pressPos;
    private bool _panning;
    private Point _panLast;

    static CurveEditor()
    {
        AffectsRender<CurveEditor>(TimesProperty, ChannelsProperty, IsEditableProperty);
    }

    public CurveEditor()
    {
        Focusable = true;   // Delete, Escape and F need the keyboard
        ClipToBounds = true;
    }

    /// <summary>Fit the view to the channels shown (the Fit button, and F).</summary>
    public void FitView()
    {
        _refit = true;
        InvalidateVisual();
    }

    private bool Visible(int c) => c < 4 && !_hidden[c];

    private static string Label(int c, int count) => (count == 4 ? "RGBA" : "XYZW")[c].ToString();

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static FormattedText Text(string s, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 10, brush);

    /// <summary>The channel chips, top right. One function for painting and for clicking, as with keys.</summary>
    private Rect ChipRect(int c, int count) => new(Bounds.Width - 6 - (count - c) * 22, 4, 18, 14);

    /// <summary>The current view, fitted first when the keys changed from outside. Null when there is
    /// nothing to show or no room to show it in, which is also when input is ignored.</summary>
    private CurveTransform? Xf()
    {
        if (Times is not { Length: > 0 } times || Channels is not { Length: > 0 } channels
            || Bounds.Width < 8 || Bounds.Height < 8)
            return null;
        if (_refit || _view is null)
        {
            _view = CurveTransform.Fit(times, channels, Bounds.Size, Visible) ?? CurveTransform.Fit(times, channels, Bounds.Size);
            if (_view is null) return null;
            _refit = false;
        }
        return _view.Value with { Width = Bounds.Width, Height = Bounds.Height };
    }

    /// <summary>What is on screen: the dragged copy mid-gesture, otherwise the row's keys.</summary>
    private (float[] Times, float[][] Channels) Shown() =>
        _drag is { } d ? (d.Times, d.Channels) : (Times!, Channels!);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TimesProperty || e.Property == ChannelsProperty)
        {
            _hover = null;
            if (_keepView)
            {
                // our own commit coming back: keep the view the user was editing in
                if (_selected is { } s && (Times is null || s.Index >= Times.Length)) _selected = null;
                return;
            }
            // another row, or a typed edit from the key list: a copy being dragged no longer matches
            _drag = null;
            _selected = null;
            _refit = true;
            int count = Channels?.Length ?? 0;
            if (count != _channelCount) { _channelCount = count; Array.Clear(_hidden); }
        }
        else if (e.Property == IsEditableProperty && !IsEditable)
            _drag = null;
    }

    /// <summary>Raise one commit event with the view held: the row answers with new arrays, and those
    /// must not refit the graph out from under the pointer.</summary>
    private void Commit(Action raise)
    {
        _keepView = true;
        try { raise(); }
        finally { _keepView = false; }
    }

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        var ground = ThemeService.Brush("ReyBgBrush", "#10141B");   // M686: the palette's ground
        ctx.FillRectangle(ground, b);
        for (int i = 1; i < 4; i++)
        {
            double y = b.Height * i / 4.0;
            ctx.DrawLine(GridPen, new Point(0, y), new Point(b.Width, y));
        }
        if (Xf() is not { } xf) return;
        var (times, channels) = Shown();
        using var clip = ctx.PushClip(b);

        // the time grid is in CURVE space - the lifetime's quarters, its start and end drawn stronger -
        // because once the view is zoomed a screen grid says nothing about where the particle's life is
        for (int q = 0; q <= 4; q++)
        {
            double x = xf.ToScreen(q / 4f, 0f).X;
            if (x >= 0 && x <= b.Width)
                ctx.DrawLine(q % 4 == 0 ? LifetimePen : GridPen, new Point(x, 0), new Point(x, b.Height));
        }

        bool editable = IsEditable;
        var selPen = new Pen(ThemeService.Brush("ReyAccentBrush", "#4C9FE8"), 1.6);
        int count = Math.Min(channels.Length, 4);
        for (int c = 0; c < count; c++)
        {
            var ch = channels[c];
            int n = Math.Min(times.Length, ch.Length);
            bool shown = Visible(c);
            var pen = shown ? LinePens[c] : GhostPens[c];
            for (int i = 1; i < n; i++) ctx.DrawLine(pen, xf.ToScreen(times[i - 1], ch[i - 1]), xf.ToScreen(times[i], ch[i]));
            if (!shown) continue;
            for (int i = 0; i < n; i++)
            {
                var p = xf.ToScreen(times[i], ch[i]);
                bool sel = _selected == (i, c);
                double r = sel ? 4.4 : _hover == (i, c) ? 3.8 : 2.8;
                if (editable) ctx.DrawEllipse(ChannelBrushes[c], sel ? selPen : null, p, r, r);
                else ctx.DrawEllipse(ground, sel ? selPen : RingPens[c], p, r, r);   // rings: nothing to pick up
            }
        }

        var dim = ChannelBrushes[3];
        ctx.DrawText(Text(F(xf.Max), dim), new Point(4, 2));
        ctx.DrawText(Text(F(xf.Min), dim), new Point(4, b.Height - 14));

        if (count > 1)
            for (int c = 0; c < count; c++)
            {
                var r = ChipRect(c, count);
                bool shown = Visible(c);
                ctx.DrawRectangle(shown ? ChannelBrushes[c] : null, shown ? null : RingPens[c], r, 3, 3);
                var label = Text(Label(c, count), shown ? ground : ChannelBrushes[c]);
                ctx.DrawText(label, new Point(r.X + (r.Width - label.Width) / 2, r.Y + (r.Height - label.Height) / 2));
            }

        // the key in hand, in numbers - the list beside the graph only catches up on release
        var focus = _drag is { } d ? (d.Index, d.Channel) : _selected;
        if (focus is { } f && f.Index < times.Length && f.Channel < count)
        {
            float t = times[f.Index], v = channels[f.Channel][f.Index];
            var txt = Text($"t {F(t)}  {(count > 1 ? Label(f.Channel, count) + " " : "")}{F(v)}", dim);
            var p = xf.ToScreen(t, v);
            double x = Math.Clamp(p.X + 9, 2, Math.Max(2, b.Width - txt.Width - 4));
            double y = Math.Clamp(p.Y - 20, 2, Math.Max(2, b.Height - txt.Height - 2));
            ctx.DrawRectangle(ThemeService.Brush("ReyPanelAltBrush", "#1A2029"), null,
                new Rect(x - 3, y - 1, txt.Width + 6, txt.Height + 2), 2, 2);
            ctx.DrawText(txt, new Point(x, y));
        }

        if (!editable)
        {
            var tag = Text("READ-ONLY", ThemeService.Brush("ReyWarningBrush", "#FFB454"));
            ctx.DrawText(tag, new Point(b.Width - tag.Width - 6, b.Height - tag.Height - 3));
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Xf() is not { } xf) return;
        double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;   // Shift+wheel can arrive as horizontal
        if (delta == 0) return;
        double f = delta > 0 ? 1 / 1.2 : 1.2;
        // Ctrl zooms time alone, Shift the value alone, the plain wheel both
        double ft = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : f;
        double fv = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 1 : f;
        _view = xf.Zoom(e.GetPosition(this), ft, fv);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_drag is not null || _panning) return;   // one gesture at a time
        Focus();
        var point = e.GetCurrentPoint(this);
        var p = point.Position;

        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            _panning = true;
            _panLast = p;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed || Xf() is not { } xf) return;
        var times = Times!;
        var channels = Channels!;
        e.Handled = true;

        int count = Math.Min(channels.Length, 4);
        if (count > 1)
            for (int c = 0; c < count; c++)
                if (ChipRect(c, count).Contains(p))
                {
                    ToggleChannel(c, solo: e.KeyModifiers.HasFlag(KeyModifiers.Control));
                    return;
                }

        if (CurveEditing.HitTest(xf, times, channels, Visible, p) is { } hit)
        {
            _selected = hit;
            if (IsEditable)
            {
                _drag = new CurveKeyDrag(times, channels, hit.Index, hit.Channel);
                var (t, v) = xf.ToCurve(p);
                _grabT = _drag.StartTime - t;
                _grabV = _drag.StartValue - v;
                _pressPos = p;
                e.Pointer.Capture(this);
            }
        }
        else if (e.ClickCount == 2 && IsEditable)
            AddKeyAt(xf, p, times, channels);
        else
            _selected = null;
        InvalidateVisual();
    }

    private void AddKeyAt(CurveTransform xf, Point p, float[] times, float[][] channels)
    {
        int c = CurveEditing.NearestChannel(xf, times, channels, Visible, p);
        if (c < 0) return;
        var (t, v) = xf.ToCurve(p);
        t = Math.Min(Math.Max(t, Math.Min(0f, times[0])), Math.Max(1f, times[^1]));
        var comps = CurveEditing.NewKey(times, channels, t, c, v);
        int at = CurveEditing.InsertionIndex(times, t);
        Commit(() => KeyAdded?.Invoke(t, comps));
        // select it only if it landed - a refused commit leaves the keys as they were
        if (Times is { } now && now.Length == times.Length + 1) _selected = (at, c);
    }

    private void ToggleChannel(int c, bool solo)
    {
        int count = Math.Min(Channels?.Length ?? 0, 4);
        if (solo)
            for (int i = 0; i < _hidden.Length; i++) _hidden[i] = i != c;
        else
        {
            // the last channel shown stays: with none there is nothing left to edit or to fit
            if (!_hidden[c] && Enumerable.Range(0, count).Count(Visible) == 1) return;
            _hidden[c] = !_hidden[c];
        }
        if (_selected is { } s && !Visible(s.Channel)) _selected = null;
        _refit = true;   // fit what is shown: an alpha and an HDR red can be an order of magnitude apart
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_panning)
        {
            if (Xf() is { } pan) _view = pan.Pan(p - _panLast);
            _panLast = p;
            InvalidateVisual();
            return;
        }
        if (Xf() is not { } xf) return;

        if (_drag is { } d)
        {
            var (t, v) = xf.ToCurve(p);
            t += _grabT;
            v += _grabV;
            // Shift holds the key to one axis, whichever the pointer has travelled further along
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                if (Math.Abs(p.X - _pressPos.X) >= Math.Abs(p.Y - _pressPos.Y)) v = d.StartValue;
                else t = d.StartTime;
            }
            d.MoveTo(t, v);   // the copy only - nothing reaches the document until the button comes up
            InvalidateVisual();
            return;
        }

        var hover = CurveEditing.HitTest(xf, Times!, Channels!, Visible, p);
        if (hover == _hover) return;
        _hover = hover;
        Cursor = hover is not null && IsEditable ? _hand ??= new Cursor(StandardCursorType.Hand) : null;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var drag = _drag;
        _drag = null;
        _panning = false;
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
        if (drag is null) return;

        // THE commit of the gesture: one EditCurve, however many moves came before it
        if (drag.Moved) Commit(() => KeyMoved?.Invoke(drag.Index, drag.Time, drag.Components()));
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // the pointer was taken away mid-gesture: nothing was released, so nothing commits
        _panning = false;
        if (_drag is null) return;
        _drag = null;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is null) return;
        _hover = null;
        Cursor = null;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _drag is not null)
        {
            _drag = null;   // back to the row's keys; the release that follows commits nothing
            e.Handled = true;
            InvalidateVisual();
        }
        else if (e.Key is Key.Delete or Key.Back && _drag is null && IsEditable && _selected is { } s)
        {
            Commit(() => KeyRemoved?.Invoke(s.Index));
            _selected = null;
            e.Handled = true;
            InvalidateVisual();
        }
        else if (e.Key == Key.F)
        {
            FitView();
            e.Handled = true;
        }
    }
}
