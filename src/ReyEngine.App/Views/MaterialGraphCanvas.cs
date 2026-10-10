using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using ReyEngine.App.Services;
using ReyEngine.Formats.Materials.Graph;

namespace ReyEngine.App.Views;

/// <summary>
/// M829: the Material Graph canvas - a read-only node graph drawn the way Unreal's Material Editor draws one:
/// a dark grid, coloured title bars by node type, named pins with dots, bezier wires, grey comment frames, a
/// faint "MATERIAL" watermark and a zoom readout.
///
/// <para>Everything is painted by hand (no child controls per node), so a Mantis material with 25 nodes and a
/// 2,000-unit-tall graph costs one <see cref="Render"/> pass. Nodes and pins outside the viewport are skipped
/// and text is dropped below a zoom where it could not be read anyway.</para>
///
/// <para>View-only by design: pan (middle or right drag, or left drag on empty canvas), wheel zoom about the
/// pointer, click to select, Home to fit. There is no node dragging, no wire dragging and nothing that writes
/// back to the material. Colours come from the theme (ReyGraph* keys, plus the usual Rey* surface brushes), so
/// every palette restyles it.</para>
/// </summary>
public sealed class MaterialGraphCanvas : Control
{
    public static readonly StyledProperty<MaterialGraph?> GraphProperty =
        AvaloniaProperty.Register<MaterialGraphCanvas, MaterialGraph?>(nameof(Graph));

    public static readonly StyledProperty<string?> SelectedNodeIdProperty =
        AvaloniaProperty.Register<MaterialGraphCanvas, string?>(nameof(SelectedNodeId), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> HideUnrelatedProperty =
        AvaloniaProperty.Register<MaterialGraphCanvas, bool>(nameof(HideUnrelated));

    public static readonly StyledProperty<GraphThumbnails?> ThumbnailsProperty =
        AvaloniaProperty.Register<MaterialGraphCanvas, GraphThumbnails?>(nameof(Thumbnails));

    public static readonly StyledProperty<string> BreadcrumbProperty =
        AvaloniaProperty.Register<MaterialGraphCanvas, string>(nameof(Breadcrumb), "");

    public static readonly StyledProperty<string> EmptyTextProperty =
        AvaloniaProperty.Register<MaterialGraphCanvas, string>(nameof(EmptyText), "");

    static MaterialGraphCanvas()
    {
        AffectsRender<MaterialGraphCanvas>(GraphProperty, SelectedNodeIdProperty, HideUnrelatedProperty,
            BreadcrumbProperty, EmptyTextProperty);
        FocusableProperty.OverrideDefaultValue<MaterialGraphCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<MaterialGraphCanvas>(true);
    }

    public MaterialGraph? Graph { get => GetValue(GraphProperty); set => SetValue(GraphProperty, value); }
    public string? SelectedNodeId { get => GetValue(SelectedNodeIdProperty); set => SetValue(SelectedNodeIdProperty, value); }
    public bool HideUnrelated { get => GetValue(HideUnrelatedProperty); set => SetValue(HideUnrelatedProperty, value); }
    public GraphThumbnails? Thumbnails { get => GetValue(ThumbnailsProperty); set => SetValue(ThumbnailsProperty, value); }
    public string Breadcrumb { get => GetValue(BreadcrumbProperty); set => SetValue(BreadcrumbProperty, value); }
    public string EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    // ---- view transform: screen = graph * zoom + pan ----
    private double _zoom = 1, _panX, _panY;
    private bool _needFit = true;
    private bool _panning;
    private Point _panFrom;
    private Point _panStart;
    private string? _hoverId;

    public const double MinZoom = 0.08, MaxZoom = 2.5;

    public double Zoom => _zoom;

    /// <summary>Where graph (0, 0) is drawn, in canvas pixels.</summary>
    public Point Pan => new(_panX, _panY);

    /// <summary>The zoom the canvas is showing, as the label under the graph reads it.</summary>
    public string ZoomText => $"Zoom {_zoom * 100:0}%";

    public event Action? ViewChanged;

    // =============================================================================== lifecycle

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GraphProperty)
        {
            _needFit = true;
            _hoverId = null;
            InvalidateVisual();
            // framed after layout, never from inside a render pass (a visual cannot invalidate itself there)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_needFit) FitToGraph(); }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        else if (change.Property == ThumbnailsProperty)
        {
            if (change.OldValue is GraphThumbnails old) old.Updated -= OnThumbnailsUpdated;
            if (change.NewValue is GraphThumbnails now) now.Updated += OnThumbnailsUpdated;
        }
    }

    private void OnThumbnailsUpdated() => InvalidateVisual();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_needFit) FitToGraph();
    }

    // =============================================================================== view control

    /// <summary>Frame the whole graph (Unreal's Home).</summary>
    public void FitToGraph()
    {
        var g = Graph;
        if (g is null || Bounds.Width < 40 || Bounds.Height < 40) return;
        _needFit = false;
        var (bx, by, bw, bh) = g.Bounds;
        if (bw <= 0 || bh <= 0) return;
        const double margin = 36;
        double z = Math.Min((Bounds.Width - 2 * margin) / bw, (Bounds.Height - 2 * margin) / bh);
        _zoom = Math.Clamp(z, MinZoom, 1.0);
        _panX = (Bounds.Width - bw * _zoom) / 2 - bx * _zoom;
        _panY = (Bounds.Height - bh * _zoom) / 2 - by * _zoom;
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Centre a node and make it readable (Search / "go to").</summary>
    public void FocusNode(string id)
    {
        var n = Graph?.Find(id);
        if (n is null || Bounds.Width < 40) return;
        _needFit = false;
        _zoom = Math.Clamp(Math.Max(_zoom, 0.85), MinZoom, MaxZoom);
        _panX = Bounds.Width / 2 - (n.X + n.Width / 2) * _zoom;
        _panY = Bounds.Height / 2 - (n.Y + n.Height / 2) * _zoom;
        SelectedNodeId = id;
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    private Point ToGraph(Point screen) => new((screen.X - _panX) / _zoom, (screen.Y - _panY) / _zoom);

    private GraphNode? NodeAt(Point screen)
    {
        var g = Graph;
        if (g is null) return null;
        var p = ToGraph(screen);
        for (int i = g.Nodes.Count - 1; i >= 0; i--)
            if (g.Nodes[i].Contains(p.X, p.Y)) return g.Nodes[i];
        return null;
    }

    // =============================================================================== input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        bool pan = pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed;
        if (pt.Properties.IsLeftButtonPressed)
        {
            var hit = NodeAt(pt.Position);
            SelectedNodeId = hit?.Id;
            pan = hit is null;   // a left drag on empty canvas pans too; on a node it just selects
        }
        if (pan)
        {
            _panning = true;
            _panFrom = pt.Position;
            _panStart = new Point(_panX, _panY);
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_panning)
        {
            _panX = _panStart.X + (pos.X - _panFrom.X);
            _panY = _panStart.Y + (pos.Y - _panFrom.Y);
            _needFit = false;
            InvalidateVisual();
            return;
        }

        var hit = NodeAt(pos);
        if (hit?.Id != _hoverId)
        {
            _hoverId = hit?.Id;
            ToolTip.SetTip(this, hit is null ? null : TipFor(hit));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_panning) return;
        _panning = false;
        e.Pointer.Capture(null);
        Cursor = null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverId is not null) { _hoverId = null; ToolTip.SetTip(this, null); InvalidateVisual(); }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var pos = e.GetPosition(this);
        double z = Math.Clamp(_zoom * Math.Pow(1.15, e.Delta.Y), MinZoom, MaxZoom);
        if (Math.Abs(z - _zoom) < 1e-9) { e.Handled = true; return; }
        // keep the graph point under the pointer where it is
        var gp = ToGraph(pos);
        _zoom = z;
        _panX = pos.X - gp.X * z;
        _panY = pos.Y - gp.Y * z;
        _needFit = false;
        ViewChanged?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Home) { FitToGraph(); e.Handled = true; }
        else if (e.Key == Key.F && SelectedNodeId is { } id) { FocusNode(id); e.Handled = true; }
    }

    private static string TipFor(GraphNode n)
    {
        var lines = new List<string> { n.Title };
        if (n.Subtitle.Length > 0 && n.Subtitle != n.Title) lines.Add(n.Subtitle);
        if (n.TexturePath.Length > 0) lines.Add(n.TexturePath);
        if (n.State == GraphNodeState.Unused) lines.Add("Not used by the resolved shader permutation");
        if (n.State == GraphNodeState.ShaderDefault) lines.Add("Shader default - the material does not set this");
        return string.Join("\n", lines);
    }

    // =============================================================================== theme

    private IBrush Res(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    private sealed class Palette
    {
        public IBrush Bg = Brushes.Black, Card = Brushes.Black, Border = Brushes.Gray, BorderSoft = Brushes.Gray,
            Text = Brushes.White, TextBright = Brushes.White, TextDim = Brushes.Gray, Accent = Brushes.Red,
            Panel = Brushes.Black,
            Texture = Brushes.Blue, Param = Brushes.Green, Switch = Brushes.Orange, Macro = Brushes.Purple,
            Shader = Brushes.Teal, Output = Brushes.Red, Comment = Brushes.Transparent, CommentBorder = Brushes.Gray,
            Warning = Brushes.Orange;
    }

    private Palette LoadPalette() => new()
    {
        Bg = Res("ReyBgBrush", Brushes.Black),
        Card = Res("ReyCardBrush", Brushes.Black),
        Panel = Res("ReyPanelAltBrush", Brushes.Black),
        Border = Res("ReyBorderBrush", Brushes.Gray),
        BorderSoft = Res("ReyBorderSoftBrush", Brushes.Gray),
        Text = Res("ReyTextBrush", Brushes.White),
        TextBright = Res("ReyTextBrightBrush", Brushes.White),
        TextDim = Res("ReyTextDimBrush", Brushes.Gray),
        Accent = Res("ReyAccentBrush", Brushes.Red),
        Warning = Res("ReyWarningBrush", Brushes.Orange),
        Texture = Res("ReyGraphTextureBrush", Brushes.SteelBlue),
        Param = Res("ReyGraphParamBrush", Brushes.SeaGreen),
        Switch = Res("ReyGraphSwitchBrush", Brushes.DarkGoldenrod),
        Macro = Res("ReyGraphMacroBrush", Brushes.MediumPurple),
        Shader = Res("ReyGraphShaderBrush", Brushes.Teal),
        Output = Res("ReyGraphOutputBrush", Brushes.Firebrick),
        Comment = Res("ReyGraphCommentBrush", Brushes.Transparent),
        CommentBorder = Res("ReyGraphCommentBorderBrush", Brushes.Gray),
    };

    // translucent copies of theme brushes, made once per (brush, opacity) rather than per wire per frame;
    // cleared when the palette changes, so a swapped theme never draws with the old colours
    private readonly Dictionary<(IBrush, double), IBrush> _faded = new();

    private IBrush WithOpacity(IBrush brush, double opacity)
    {
        if (brush is not ISolidColorBrush s) return brush;
        if (_faded.TryGetValue((brush, opacity), out var hit)) return hit;
        if (_faded.Count > 400) _faded.Clear();
        return _faded[(brush, opacity)] = new ImmutableSolidColorBrush(s.Color, opacity * s.Opacity);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Application.Current is { } app) app.ResourcesChanged += OnAppResourcesChanged;
        ActualThemeVariantChanged += OnResourcesChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Application.Current is { } app) app.ResourcesChanged -= OnAppResourcesChanged;
        ActualThemeVariantChanged -= OnResourcesChanged;
    }

    /// <summary>The palette (Application resources) or the theme variant changed: drop every cached brush and text.</summary>
    private void OnAppResourcesChanged(object? sender, ResourcesChangedEventArgs e) => OnResourcesChanged(sender, EventArgs.Empty);

    private void OnResourcesChanged(object? sender, EventArgs e)
    {
        _faded.Clear();
        _text.Clear();
        InvalidateVisual();
    }

    private IBrush KindBrush(Palette p, GraphNodeKind k) => k switch
    {
        GraphNodeKind.Texture => p.Texture,
        GraphNodeKind.Scalar or GraphNodeKind.Vector or GraphNodeKind.Color => p.Param,
        GraphNodeKind.Switch => p.Switch,
        GraphNodeKind.Macro => p.Macro,
        GraphNodeKind.Shader => p.Shader,
        _ => p.Output,
    };

    private static IBrush PinBrush(Palette p, GraphPinKind k) => k switch
    {
        GraphPinKind.Texture => p.Texture,
        GraphPinKind.Scalar or GraphPinKind.Vector or GraphPinKind.Color => p.Param,
        GraphPinKind.Bool => p.Switch,
        GraphPinKind.Shader => p.Shader,
        _ => p.TextDim,
    };

    // =============================================================================== text

    private readonly Dictionary<(string, double, bool, double, IBrush, int), FormattedText> _text = new();
    private Typeface _face = Typeface.Default;
    private Typeface _faceBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private FormattedText Text(string s, double size, IBrush brush, bool bold = false, double maxWidth = 0, int lines = 1)
    {
        if (_text.Count > 3000) _text.Clear();
        var key = (s, size, bold, Math.Round(maxWidth), brush, lines);
        if (_text.TryGetValue(key, out var ft)) return ft;
        ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? _faceBold : _face, size, brush);
        if (maxWidth > 0)
        {
            ft.MaxTextWidth = Math.Max(4, maxWidth);
            ft.MaxTextHeight = size * 1.5 * lines;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        _text[key] = ft;
        return ft;
    }

    // =============================================================================== render

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var pal = LoadPalette();
        var area = new Rect(Bounds.Size);
        ctx.DrawRectangle(pal.Bg, null, area);

        var g = Graph;
        if (g is null)
        {
            DrawGrid(ctx, pal, area);
            if (EmptyText.Length > 0)
            {
                var ft = Text(EmptyText, 13, pal.TextDim, maxWidth: Math.Min(420, Bounds.Width - 40), lines: 4);
                ctx.DrawText(ft, new Point((Bounds.Width - ft.Width) / 2, (Bounds.Height - ft.Height) / 2));
            }
            return;
        }

        DrawGrid(ctx, pal, area);

        // watermark, under everything (Unreal's big faint "MATERIAL")
        var wm = Text("MATERIAL", 76, WithOpacity(pal.TextDim, 0.16), bold: true);
        ctx.DrawText(wm, new Point(Bounds.Width - wm.Width - 24, Bounds.Height - wm.Height - 22));

        var related = HideUnrelated && SelectedNodeId is { } sel ? g.RelatedTo(sel) : null;
        var view = new Rect(-_panX / _zoom - 20, -_panY / _zoom - 20, Bounds.Width / _zoom + 40, Bounds.Height / _zoom + 40);

        using (ctx.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_panX, _panY)))
        {
            bool detail = _zoom >= 0.28;
            bool titles = _zoom >= 0.14;

            foreach (var f in g.Frames)
            {
                var r = new Rect(f.X, f.Y, f.Width, f.Height);
                if (!r.Intersects(view)) continue;
                ctx.DrawRectangle(pal.Comment, new Pen(pal.CommentBorder, 1.2), r, 8, 8);
                if (titles)
                    ctx.DrawText(Text(f.Title, 15, pal.TextBright, bold: true, maxWidth: f.Width - 24),
                        new Point(f.X + 14, f.Y + 6));
            }

            // wires first, nodes over them
            foreach (var w in g.Wires)
            {
                var a = g.Find(w.FromNode);
                var b = g.Find(w.ToNode);
                if (a is null || b is null) continue;
                var (x0, y0) = a.PinPosition(a.Outputs[w.FromPin]);
                var (x1, y1) = b.PinPosition(b.Inputs[w.ToPin]);
                if (!new Rect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0)).Intersects(view)) continue;

                bool lit = SelectedNodeId is { } s && (w.FromNode == s || w.ToNode == s);
                bool dimmed = related is not null && !(related.Contains(w.FromNode) && related.Contains(w.ToNode));
                var color = KindBrush(pal, w.SourceKind);
                var brush = WithOpacity(color, dimmed ? 0.12 : lit ? 1.0 : 0.72);
                double thick = Math.Max(lit ? 2.6 : 1.7, 1.2 / _zoom);
                var geo = new StreamGeometry();
                using (var gc = geo.Open())
                {
                    double dx = Math.Max(48, Math.Abs(x1 - x0) * 0.5);
                    gc.BeginFigure(new Point(x0, y0), false);
                    gc.CubicBezierTo(new Point(x0 + dx, y0), new Point(x1 - dx, y1), new Point(x1, y1));
                    gc.EndFigure(false);
                }
                ctx.DrawGeometry(null, new Pen(brush, thick), geo);
            }

            foreach (var n in g.Nodes)
            {
                if (!new Rect(n.X, n.Y, n.Width, n.Height).Intersects(view)) continue;
                bool dim = related is not null && !related.Contains(n.Id);
                double alpha = dim ? 0.18 : n.State switch { GraphNodeState.Unused => 0.55, GraphNodeState.ShaderDefault => 0.72, _ => 1.0 };
                using (ctx.PushOpacity(alpha))
                    DrawNode(ctx, pal, n, n.Id == SelectedNodeId, n.Id == _hoverId, detail, titles);
            }
        }

        // ---- overlay, in screen space ----
        if (Breadcrumb.Length > 0)
            ctx.DrawText(Text(Breadcrumb, 15, WithOpacity(pal.TextBright, 0.9), bold: true, maxWidth: Math.Max(80, Bounds.Width - 220)),
                new Point(16, 12));
        ctx.DrawText(Text(ZoomText, 11, pal.TextDim), new Point(16, Bounds.Height - 24));
        var view1 = Text("VIEW ONLY", 10, pal.TextDim, bold: true);
        ctx.DrawText(view1, new Point(Bounds.Width - view1.Width - 16, 14));
    }

    private void DrawGrid(DrawingContext ctx, Palette pal, Rect area)
    {
        double minor = 16 * _zoom, major = 128 * _zoom;
        if (minor < 5) { minor = major; }
        if (minor < 5) return;
        var thin = new Pen(WithOpacity(pal.BorderSoft, 0.55), 1);
        var thick = new Pen(WithOpacity(pal.Border, 0.9), 1);

        void Lines(double step, Pen pen, double skipEvery)
        {
            double ox = ((_panX % step) + step) % step;
            double oy = ((_panY % step) + step) % step;
            for (double x = ox; x < area.Width; x += step)
            {
                if (skipEvery > 0 && IsMultiple(x - _panX, skipEvery)) continue;
                ctx.DrawLine(pen, new Point(Math.Round(x) + 0.5, 0), new Point(Math.Round(x) + 0.5, area.Height));
            }
            for (double y = oy; y < area.Height; y += step)
            {
                if (skipEvery > 0 && IsMultiple(y - _panY, skipEvery)) continue;
                ctx.DrawLine(pen, new Point(0, Math.Round(y) + 0.5), new Point(area.Width, Math.Round(y) + 0.5));
            }
        }

        if (16 * _zoom >= 5) Lines(minor, thin, major);
        Lines(major, thick, 0);
    }

    private static bool IsMultiple(double v, double step)
    {
        double r = Math.Abs(v % step);
        return r < 0.75 || step - r < 0.75;
    }

    private void DrawNode(DrawingContext ctx, Palette pal, GraphNode n, bool selected, bool hover, bool detail, bool titles)
    {
        var rect = new Rect(n.X, n.Y, n.Width, n.Height);
        var kind = KindBrush(pal, n.Kind);
        var edge = selected ? new Pen(pal.Accent, 2.4)
            : n.State == GraphNodeState.ShaderDefault ? new Pen(pal.TextDim, 1.2, new DashStyle(new double[] { 3, 3 }, 0))
            : new Pen(hover ? pal.TextDim : pal.Border, 1.2);

        ctx.DrawRectangle(WithOpacity(pal.Card, 0.97), edge, rect, 6, 6);

        using (ctx.PushClip(new Rect(n.X, n.Y, n.Width, GraphMetrics.HeaderHeight)))
            ctx.DrawRectangle(kind, null, new Rect(n.X, n.Y, n.Width, GraphMetrics.HeaderHeight + 12), 6, 6);

        if (!titles) return;
        var tag = n.Kind switch
        {
            GraphNodeKind.Texture => "TEXTURE SAMPLE", GraphNodeKind.Scalar => "SCALAR", GraphNodeKind.Vector => "VECTOR",
            GraphNodeKind.Color => "COLOUR", GraphNodeKind.Switch => "SWITCH", GraphNodeKind.Macro => "DEFINE",
            GraphNodeKind.Shader => "SHADER", _ => "MATERIAL",
        };
        double tagW = 0;
        if (detail)
        {
            var tf = Text(tag, 8.5, WithOpacity(pal.TextBright, 0.7), bold: true);
            tagW = tf.Width + 14;
            if (tagW < n.Width * 0.5)
                ctx.DrawText(tf, new Point(n.Right - tf.Width - 8, n.Y + 7));
            else tagW = 0;
        }
        ctx.DrawText(Text(n.Title, 11.5, pal.TextBright, bold: true, maxWidth: n.Width - 16 - tagW), new Point(n.X + 8, n.Y + 4.5));

        if (!detail) return;

        // ---- pins ----
        foreach (var pin in n.Inputs)
        {
            var (px, py) = n.PinPosition(pin);
            bool attr = pin.Kind == GraphPinKind.Attribute;
            DrawPin(ctx, pal, new Point(px, py), PinBrush(pal, pin.Kind), attr ? pin.Shaded : pin.Linked);
            var nameBrush = attr && !pin.Shaded ? pal.TextDim : pal.Text;
            var detailText = pin.Detail;
            double detailW = 0;
            if (detailText.Length > 0)
            {
                var df = Text(detailText, 9.5, attr && pin.Shaded ? pal.TextBright : pal.TextDim, maxWidth: n.Width * 0.5);
                detailW = df.Width;
                ctx.DrawText(df, new Point(n.Right - df.Width - 8, py - df.Height / 2));
            }
            ctx.DrawText(Text(pin.Name, 10, nameBrush, maxWidth: n.Width - 24 - detailW), new Point(n.X + 10, py - 7));
        }

        if (n.Kind == GraphNodeKind.Shader && n.Subtitle.Length > 0)
            ctx.DrawText(Text(n.Subtitle, 9, pal.TextDim, maxWidth: n.Width - 80), new Point(n.X + 10, n.Y + GraphMetrics.PinRowCentre(0) - 6));

        bool valueNode = n.Inputs.Count == 0 && n.Outputs.Count == 1 && n.Kind != GraphNodeKind.Texture;
        foreach (var pin in n.Outputs)
        {
            var (px, py) = n.PinPosition(pin);
            DrawPin(ctx, pal, new Point(px, py), PinBrush(pal, pin.Kind), pin.Linked);
            if (valueNode)
            {
                double right = px - 10;
                var vf = Text(pin.Detail, 10, pal.TextBright, maxWidth: n.Width - 24);
                if (n.Swatch is { } sw)
                {
                    var sr = new Rect(right - vf.Width - 20, py - 6, 14, 12);
                    ctx.DrawRectangle(new ImmutableSolidColorBrush(ToColor(sw)), new Pen(pal.Border, 1), sr, 2, 2);
                }
                ctx.DrawText(vf, new Point(right - vf.Width, py - vf.Height / 2));
                ctx.DrawText(Text(n.Subtitle, 9, pal.TextDim, maxWidth: Math.Max(30, n.Width - vf.Width - (n.Swatch is null ? 36 : 56))),
                    new Point(n.X + 10, py - 6));
            }
            else
            {
                var lf = Text(pin.Name, 10, pal.Text);
                ctx.DrawText(lf, new Point(px - 10 - lf.Width, py - 7));
            }
        }

        if (n.Kind == GraphNodeKind.Texture) DrawTextureBody(ctx, pal, n);
    }

    private static Color ToColor(System.Numerics.Vector4 v)
    {
        static byte B(float f) => (byte)Math.Clamp((int)Math.Round((float.IsNaN(f) ? 0 : f) * 255f), 0, 255);
        // the colour is shown opaque: a zero alpha is a value to read, not a reason to paint nothing
        return Color.FromRgb(B(v.X), B(v.Y), B(v.Z));
    }

    private static void DrawPin(DrawingContext ctx, Palette pal, Point c, IBrush brush, bool filled) =>
        ctx.DrawEllipse(filled ? brush : pal.Card, new Pen(brush, 1.6), c, 4.5, 4.5);

    private void DrawTextureBody(DrawingContext ctx, Palette pal, GraphNode n)
    {
        double top = n.Y + GraphMetrics.BodyTop(1) - 2;
        var thumb = new Rect(n.X + 8, top, GraphMetrics.ThumbSize, GraphMetrics.ThumbSize);
        ctx.DrawRectangle(pal.Panel, new Pen(pal.Border, 1), thumb, 3, 3);

        var entry = Thumbnails?.Get(n.TextureChunk);
        string status = "";
        if (entry is null) status = "no texture";
        else if (entry.Bitmap is { } bmp)
        {
            // fit inside the square, keeping the texture's aspect
            double s = Math.Min(thumb.Width / bmp.Size.Width, thumb.Height / bmp.Size.Height);
            var dest = new Rect(thumb.X + (thumb.Width - bmp.Size.Width * s) / 2, thumb.Y + (thumb.Height - bmp.Size.Height * s) / 2,
                bmp.Size.Width * s, bmp.Size.Height * s);
            using (ctx.PushClip(thumb))
                ctx.DrawImage(bmp, new Rect(bmp.Size), dest);
            status = $"{entry.Width} x {entry.Height}";
        }
        else if (entry.Pending) status = "loading...";
        else status = "not available";

        double tx = thumb.Right + 8, tw = n.Right - tx - 8;
        ctx.DrawText(Text(status, 10, pal.Text, maxWidth: tw), new Point(tx, top + 2));
        if (entry is { Error: { } err } && entry.Bitmap is null && !entry.Pending)
            ctx.DrawText(Text(err, 9, pal.TextDim, maxWidth: tw), new Point(tx, top + 18));
        ctx.DrawText(Text(n.Subtitle, 9.5, pal.TextDim, maxWidth: n.Width - 16), new Point(n.X + 8, thumb.Bottom + 4));
    }
}
