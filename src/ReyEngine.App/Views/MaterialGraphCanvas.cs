using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Materials.Graph;

namespace ReyEngine.App.Views;

/// <summary>
/// M829: the Material Graph canvas - a node graph drawn the way Unreal's Material Editor draws one:
/// a dark grid, coloured title bars by node type, named pins with dots, bezier wires, grey comment frames, a
/// faint "MATERIAL" watermark and a zoom readout.
///
/// <para>Everything is painted by hand (no child controls per node), so a Mantis material with 25 nodes and a
/// 2,000-unit-tall graph costs one <see cref="Render"/> pass. Nodes and pins outside the viewport are skipped
/// and text is dropped below a zoom where it could not be read anyway.</para>
///
/// <para>The canvas itself never changes a material: pan (middle or right drag, or left drag on empty canvas), wheel
/// zoom about the pointer, click to select, Home to fit. M830: values are edited in the Details panel through the
/// Material Editor's own row view models, and the view model hands the canvas a rebuilt graph after each edit; the canvas
/// keeps its pan and zoom for a graph of the same material. Colours come from the theme (ReyGraph* keys, plus the usual
/// Rey* surface brushes), so every palette restyles it.</para>
///
/// <para>M831: gestures. Drag a node by its body (layout only). Drag from an output pin to a shader input pin, or pick up a
/// wire by its input end and drop it on another pin (move) or off any pin (disconnect); a live wire follows the pointer and
/// turns red, with the reason beside it, over a pin it may not feed. Alt+click on a pin breaks its links, Delete removes the
/// selected node, a right click (without moving) opens the add / create menu. The canvas only ASKS: every change is made by
/// <see cref="MaterialGraphViewModel"/> (<see cref="Wiring"/>) through the Material Editor's entries and undo stack.</para>
/// </summary>
public sealed partial class MaterialGraphCanvas : Control
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

    /// <summary>M830: the corner label ("EDITABLE" / "READ ONLY").</summary>
    public static readonly StyledProperty<string> ModeTextProperty =
        AvaloniaProperty.Register<MaterialGraphCanvas, string>(nameof(ModeText), "");

    /// <summary>M831: the view model that wires, moves, creates and deletes (null = the canvas only views).</summary>
    public static readonly StyledProperty<MaterialGraphViewModel?> WiringProperty =
        AvaloniaProperty.Register<MaterialGraphCanvas, MaterialGraphViewModel?>(nameof(Wiring));

    /// <summary>M832: the Shader Graph editor. When set the canvas edits a Shader Graph (any output to any input, a search palette on right-click)
    /// instead of wiring a material.</summary>
    public static readonly StyledProperty<ShaderGraphViewModel?> ShaderEditorProperty =
        AvaloniaProperty.Register<MaterialGraphCanvas, ShaderGraphViewModel?>(nameof(ShaderEditor));

    public ShaderGraphViewModel? ShaderEditor { get => GetValue(ShaderEditorProperty); set => SetValue(ShaderEditorProperty, value); }

    static MaterialGraphCanvas()
    {
        AffectsRender<MaterialGraphCanvas>(GraphProperty, SelectedNodeIdProperty, HideUnrelatedProperty,
            BreadcrumbProperty, EmptyTextProperty, ModeTextProperty);
        FocusableProperty.OverrideDefaultValue<MaterialGraphCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<MaterialGraphCanvas>(true);
    }

    public MaterialGraph? Graph { get => GetValue(GraphProperty); set => SetValue(GraphProperty, value); }
    public string? SelectedNodeId { get => GetValue(SelectedNodeIdProperty); set => SetValue(SelectedNodeIdProperty, value); }
    public bool HideUnrelated { get => GetValue(HideUnrelatedProperty); set => SetValue(HideUnrelatedProperty, value); }
    public GraphThumbnails? Thumbnails { get => GetValue(ThumbnailsProperty); set => SetValue(ThumbnailsProperty, value); }
    public string Breadcrumb { get => GetValue(BreadcrumbProperty); set => SetValue(BreadcrumbProperty, value); }
    public string EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    public string ModeText { get => GetValue(ModeTextProperty); set => SetValue(ModeTextProperty, value); }
    public MaterialGraphViewModel? Wiring { get => GetValue(WiringProperty); set => SetValue(WiringProperty, value); }

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
            // M830: an edit hands over a rebuilt graph of the SAME material - keep the view where the person put it
            bool sameMaterial = change.OldValue is MaterialGraph before && change.NewValue is MaterialGraph after
                                && before.MaterialName == after.MaterialName && !_needFit;
            if (!sameMaterial) _needFit = true;
            _hoverId = null;
            // M832: a Shader Graph is re-projected when its compile finishes; that must not drop a wire the person is dragging
            if (!(sameMaterial && ShaderEditor is not null)) CancelGesture();
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

    private enum Gesture { None, Pan, Node, Wire, SgWire }

    private Gesture _gesture;
    private bool _rightPan;
    private Point _rightDown;
    private bool _rightMoved;

    // ---- node drag
    private GraphNode? _dragNode;
    private Point _dragStart;
    private (double X, double Y) _dragOrigin;
    private bool _dragMoved;

    // ---- wire drag. The OUTPUT end is the node the value comes from, the INPUT end a pin of the shader node; either may be the one
    // the person picked up. _wireSrc is null while the person started at an unlinked input and has not reached an output yet.
    private GraphNode? _wireSrc;
    private int _wireInputIndex = -1;
    private bool _wireMove;
    private bool _wireFromInput;
    private Point _wireCursor;
    private Point _wireDown;
    private bool _wireMoved;
    private (GraphNode Node, GraphPin Pin, int Index)? _wireHover;
    private GraphLinkCheck _wireCheck;

    /// <summary>The pin under a screen point (inputs and outputs of every node), or null. Pins are only live where they are drawn.</summary>
    public (GraphNode Node, GraphPin Pin, int Index)? PinAt(Point screen)
    {
        var g = Graph;
        if (g is null || _zoom < 0.28) return null;
        var p = ToGraph(screen);
        double r = Math.Min(16, 10 / _zoom);
        for (int i = g.Nodes.Count - 1; i >= 0; i--)
        {
            var n = g.Nodes[i];
            if (p.X < n.X - r || p.X > n.Right + r || p.Y < n.Y || p.Y > n.Bottom) continue;
            for (int k = 0; k < n.Inputs.Count; k++)
            {
                var (px, py) = n.PinPosition(n.Inputs[k]);
                if (Math.Abs(p.X - px) <= r && Math.Abs(p.Y - py) <= GraphMetrics.PinRowHeight / 2) return (n, n.Inputs[k], k);
            }
            for (int k = 0; k < n.Outputs.Count; k++)
            {
                var (px, py) = n.PinPosition(n.Outputs[k]);
                if (Math.Abs(p.X - px) <= r && Math.Abs(p.Y - py) <= GraphMetrics.PinRowHeight / 2) return (n, n.Outputs[k], k);
            }
        }
        return null;
    }

    /// <summary>Where a graph point is on the canvas, in pixels (for a probe that drives the pointer).</summary>
    public Point ToScreen(double graphX, double graphY) => new(graphX * _zoom + _panX, graphY * _zoom + _panY);

    /// <summary>The centre of a pin on the canvas, in pixels.</summary>
    public Point PinScreen(GraphNode node, GraphPin pin)
    {
        var (x, y) = node.PinPosition(pin);
        return ToScreen(x, y);
    }

    private void CancelGesture()
    {
        _gesture = Gesture.None;
        _wireSrc = null; _wireHover = null; _wireInputIndex = -1; _dragNode = null;
        _panning = false;
        _rightPan = false;
        Cursor = null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        e.Handled = true;
        if (Graph is null) return;

        if (ShaderEditor is not null && SgPointerPressed(e, pt)) return;
        if (pt.Properties.IsLeftButtonPressed)
        {
            if (PinAt(pt.Position) is { } hit)
            {
                SelectedNodeId = hit.Node.Id;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { Wiring?.BreakLinks(hit.Node.Id, hit.Index, hit.Pin.IsInput); return; }
                if (BeginWire(hit.Node, hit.Pin, hit.Index, pt.Position)) e.Pointer.Capture(this);
                return;
            }
            var node = NodeAt(pt.Position);
            SelectedNodeId = node?.Id;
            if (node is null) { BeginPan(pt.Position, e); return; }
            _gesture = Gesture.Node;
            _dragNode = node;
            _dragStart = pt.Position;
            _dragOrigin = (node.X, node.Y);
            _dragMoved = false;
            e.Pointer.Capture(this);
            return;
        }
        if (pt.Properties.IsMiddleButtonPressed) { BeginPan(pt.Position, e); return; }
        if (pt.Properties.IsRightButtonPressed)
        {
            _rightPan = true;
            _rightDown = pt.Position;
            _rightMoved = false;
            BeginPan(pt.Position, e);
        }
    }

    private void BeginPan(Point at, PointerPressedEventArgs e)
    {
        _gesture = Gesture.Pan;
        _panning = true;
        _panFrom = at;
        _panStart = new Point(_panX, _panY);
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    /// <summary>Start a wire at a pin. False (and a line in the status) when that pin cannot start one.</summary>
    private bool BeginWire(GraphNode node, GraphPin pin, int index, Point at)
    {
        var w = Wiring;
        if (w is null) return false;
        if (!pin.IsInput)
        {
            if (node.Kind is GraphNodeKind.Switch or GraphNodeKind.Macro) { w.Notify("Switches and defines are toggles, not links: flip them in the Details panel."); return false; }
            if (node.Kind is not (GraphNodeKind.Texture or GraphNodeKind.Scalar or GraphNodeKind.Vector or GraphNodeKind.Color)) return false;
            if (!w.CanWire) { w.Notify(w.WireNote); return false; }
            _wireSrc = node; _wireInputIndex = -1; _wireMove = false; _wireFromInput = false;
        }
        else
        {
            if (node.Kind != GraphNodeKind.Shader) return false;
            if (pin.Kind == GraphPinKind.Bool) { w.Notify("Switches and defines are toggles, not links: flip them in the Details panel."); return false; }
            if (pin.Kind is not (GraphPinKind.Texture or GraphPinKind.Scalar or GraphPinKind.Vector or GraphPinKind.Color)) return false;
            if (!w.CanWire) { w.Notify(w.WireNote); return false; }
            var linked = Graph!.Wires.FirstOrDefault(x => x.ToNode == node.Id && x.ToPin == index);
            var from = linked is null ? null : Graph.Find(linked.FromNode);
            // an authored link is picked up by its input end (it MOVES); a shader-default input has nothing to pick up, so the wire
            // starts empty and is made from the input towards an output
            _wireSrc = from is not null && from.State != GraphNodeState.ShaderDefault ? from : null;
            _wireMove = _wireSrc is not null;
            _wireFromInput = true;
            _wireInputIndex = index;
        }
        _gesture = Gesture.Wire;
        _wireCursor = at; _wireDown = at; _wireMoved = false;
        _wireHover = null; _wireCheck = default;
        Cursor = new Cursor(StandardCursorType.Cross);
        InvalidateVisual();
        return true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        switch (_gesture)
        {
            case Gesture.Pan:
                if (_rightPan && (Math.Abs(pos.X - _rightDown.X) > 4 || Math.Abs(pos.Y - _rightDown.Y) > 4)) _rightMoved = true;
                _panX = _panStart.X + (pos.X - _panFrom.X);
                _panY = _panStart.Y + (pos.Y - _panFrom.Y);
                _needFit = false;
                InvalidateVisual();
                return;
            case Gesture.Node:
                if (_dragNode is null) return;
                if (!_dragMoved && Math.Abs(pos.X - _dragStart.X) < 4 && Math.Abs(pos.Y - _dragStart.Y) < 4) return;
                _dragMoved = true;
                double nx = _dragOrigin.X + (pos.X - _dragStart.X) / _zoom, ny = _dragOrigin.Y + (pos.Y - _dragStart.Y) / _zoom;
                if (ShaderEditor is { } se) se.MoveNode(_dragNode.Id, nx, ny);
                else if (Wiring is { } wv) wv.MoveNode(_dragNode.Id, nx, ny); else _dragNode.SetPosition(nx, ny);
                InvalidateVisual();
                return;
            case Gesture.Wire:
                UpdateWire(pos);
                return;
            case Gesture.SgWire:
                SgUpdateWire(pos);
                return;
        }

        var hit = NodeAt(pos);
        var pin = PinAt(pos);
        string? key = pin is { } ph ? ph.Node.Id + "#" + (ph.Pin.IsInput ? "i" : "o") + ph.Index : hit?.Id;
        if (key != _hoverId)
        {
            _hoverId = key;
            ToolTip.SetTip(this, pin is { } p2 ? TipForPin(p2.Node, p2.Pin) : hit is null ? null : TipFor(hit));
            InvalidateVisual();
        }
    }

    private void UpdateWire(Point pos)
    {
        _wireCursor = pos;
        if (Math.Abs(pos.X - _wireDown.X) > 3 || Math.Abs(pos.Y - _wireDown.Y) > 3) _wireMoved = true;
        _wireHover = null;
        _wireCheck = default;
        var pin = PinAt(pos);
        var w = Wiring;
        var shader = Graph?.ShaderNode;
        if (pin is { } h && w is not null && shader is not null)
        {
            if (!_wireFromInput && h.Pin.IsInput && h.Node.Kind == GraphNodeKind.Shader && _wireSrc is not null)
            {
                _wireHover = h;
                _wireCheck = w.CheckLink(_wireSrc.Id, shader.Id, h.Index);
            }
            else if (_wireFromInput && !h.Pin.IsInput && _wireInputIndex >= 0)
            {
                // an input pulled towards an output
                _wireHover = h;
                _wireCheck = w.CheckLink(h.Node.Id, shader.Id, _wireInputIndex);
            }
            else if (_wireFromInput && _wireSrc is not null && h.Pin.IsInput && h.Node.Kind == GraphNodeKind.Shader)
            {
                // a picked-up link put on another input of the shader: it moves there
                _wireHover = h;
                _wireCheck = h.Index == _wireInputIndex ? new GraphLinkCheck(false, "") : w.CheckLink(_wireSrc.Id, shader.Id, h.Index);
            }
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pos = e.GetPosition(this);
        var gesture = _gesture;
        Cursor = null;

        if (gesture == Gesture.Wire) { FinishWire(); ReleaseCapture(e); return; }
        if (gesture == Gesture.SgWire) { SgFinishWire(); ReleaseCapture(e); return; }
        ReleaseCapture(e);
        if (gesture == Gesture.Node)
        {
            if (_dragMoved) Graph?.UpdateBounds();
            _dragNode = null;
        }
        bool menu = gesture == Gesture.Pan && _rightPan && !_rightMoved && e.InitialPressMouseButton == MouseButton.Right;
        _gesture = Gesture.None;
        _panning = false;
        _rightPan = false;
        if (menu) ShowMenu(pos);
    }

    private bool _releasing;

    private void ReleaseCapture(PointerReleasedEventArgs e)
    {
        _releasing = true;
        try { e.Pointer.Capture(null); }
        finally { _releasing = false; }
    }

    /// <summary>Capture taken away (focus change, window deactivated): an unfinished drag is abandoned, nothing is written.</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_releasing || _gesture == Gesture.None) return;
        CancelGesture();
        InvalidateVisual();
    }

    private void FinishWire()
    {
        var w = Wiring;
        var hover = _wireHover;
        var check = _wireCheck;
        var src = _wireSrc;
        int inputIndex = _wireInputIndex;
        bool move = _wireMove, fromInput = _wireFromInput, moved = _wireMoved;
        var shader = Graph?.ShaderNode;
        var cursor = _wireCursor;
        // how far the pointer is from the pin the wire was picked up at: a link is taken off only once it was pulled clearly away
        double away = double.MaxValue;
        if (shader is not null && inputIndex >= 0 && inputIndex < shader.Inputs.Count)
        {
            var origin = PinScreen(shader, shader.Inputs[inputIndex]);
            away = Math.Sqrt((origin.X - cursor.X) * (origin.X - cursor.X) + (origin.Y - cursor.Y) * (origin.Y - cursor.Y));
        }
        CancelGesture();
        InvalidateVisual();
        if (w is null || shader is null) return;
        if (!moved) return;   // a click on a pin: it only selected the node

        if (hover is { } h)
        {
            if (check.Ok)
            {
                if (!fromInput) w.Connect(src!.Id, shader.Id, h.Index, move: false);
                else if (!h.Pin.IsInput) w.Connect(h.Node.Id, shader.Id, inputIndex, move: false);   // an input pulled to an output: a new link
                else w.Connect(src!.Id, shader.Id, h.Index, move: true);                              // a picked-up link, put on another input
            }
            else if (check.Reason.Length > 0) w.Notify(check.Reason);
            return;
        }
        // dropped on nothing: a link picked up by its input end is taken off (the entry is removed, the shader default applies)
        if (fromInput && move && inputIndex >= 0)
        {
            if (away > 18) w.Disconnect(inputIndex);   // pin hit radius (10 px) plus a margin; a small slip off the pin does nothing
        }
        else w.Notify("No link made: drop the wire on " + (fromInput ? "a node's output pin." : "one of the shader's input pins."));
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
        else if (e.Key == Key.Escape && _gesture == Gesture.Wire) { CancelGesture(); InvalidateVisual(); e.Handled = true; }
        else if (e.Key == Key.Escape && _gesture == Gesture.SgWire) { CancelGesture(); InvalidateVisual(); e.Handled = true; }
        else if (e.Key == Key.Delete && SelectedNodeId is { } sdel && ShaderEditor is { } sed) { sed.DeleteNode(sdel); e.Handled = true; }
        else if (e.Key == Key.Delete && SelectedNodeId is { } del && Wiring is { } w) { w.DeleteNode(del); e.Handled = true; }
    }

    // =============================================================================== the right-click menu

    /// <summary>The menu for a right click at <paramref name="screen"/>: what is under the pointer first, then the unconnected shader
    /// inputs (the main way to add), then the generic adds. Built fresh each time; public so a probe can read and click it.</summary>
    public IReadOnlyList<Control> BuildMenu(Point screen)
    {
        var items = new List<Control>();
        var w = Wiring;
        MenuItem Item(string header, Action act, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => act();
            return mi;
        }
        if (w is null || Graph is null) return items;
        if (!w.CanEdit)
        {
            items.Add(Item(w.EditNote, () => { }, enabled: false));
            return items;
        }

        var pin = PinAt(screen);
        var node = pin?.Node ?? NodeAt(screen);
        if (pin is { } ph && ph.Pin.IsInput && ph.Node.Kind == GraphNodeKind.Shader && ph.Pin.Key.Length > 0 && ph.Pin.Kind != GraphPinKind.Bool)
        {
            int idx = ph.Index;
            var open = w.UnconnectedInputs().FirstOrDefault(u => u.PinIndex == idx);
            if (open is not null)
                items.Add(Item(open.IsTexture ? $"Create texture sample here ({open.Name})" : $"Create parameter here ({open.Name})", () => w.CreateAt(idx)));
            else items.Add(Item($"Disconnect {ph.Pin.Name}", () => w.Disconnect(idx)));
        }
        else if (node is not null)
        {
            if (node.State == GraphNodeState.ShaderDefault && node.Kind == GraphNodeKind.Texture
                && Graph.Wires.FirstOrDefault(x => x.FromNode == node.Id) is { } wire)
                items.Add(Item($"Create texture sample here ({node.Title})", () => w.CreateAt(wire.ToPin)));
            else if (node.Kind is GraphNodeKind.Texture or GraphNodeKind.Scalar or GraphNodeKind.Vector or GraphNodeKind.Color)
            {
                string id = node.Id;
                items.Add(Item($"Delete {node.Title}", () => w.DeleteNode(id)));
            }
        }
        if (items.Count > 0) items.Add(new Separator());

        var unconnected = w.UnconnectedInputs();
        var tex = unconnected.Where(u => u.IsTexture).ToList();
        var con = unconnected.Where(u => !u.IsTexture).ToList();
        MenuItem Sub(string header, List<GraphUnconnectedInput> list, string verb)
        {
            var sub = new MenuItem { Header = header, IsEnabled = list.Count > 0 };
            foreach (var u in list)
            {
                int pi = u.PinIndex;
                var mi = new MenuItem { Header = $"{verb} {u.Name}" + (u.Detail.Length > 0 ? $"   ({u.Detail})" : "") };
                mi.Click += (_, _) => w.CreateAt(pi);
                sub.Items.Add(mi);
            }
            return sub;
        }
        items.Add(Sub($"Add for unconnected texture input ({tex.Count})", tex, "Texture sample for"));
        items.Add(Sub($"Add for unconnected constant input ({con.Count})", con, "Parameter for"));
        items.Add(new Separator());
        items.Add(Item("Add Texture Sample", () => w.AddTextureSample()));
        items.Add(Item("Add Parameter", () => w.AddParameterNode()));
        return items;
    }

    private void ShowMenu(Point screen)
    {
        if (ShaderEditor is not null) { SgShowMenu(screen); return; }
        var items = BuildMenu(screen);
        if (items.Count == 0) return;
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(this);
    }

    private static string TipForPin(GraphNode n, GraphPin p)
    {
        var lines = new List<string> { $"{n.Title}: {p.Name}" };
        if (p.Detail.Length > 0) lines.Add(p.Detail);
        if (n.Kind == GraphNodeKind.Shader && p.IsInput && p.Key.Length > 0 && p.Kind != GraphPinKind.Bool)
            lines.Add(p.Linked ? "Drag the end to move it, off a pin to disconnect. Alt+click breaks it." : "Not connected: the shader default applies. Right-click to create one here.");
        else if (!p.IsInput && n.Kind is GraphNodeKind.Texture or GraphNodeKind.Scalar or GraphNodeKind.Vector or GraphNodeKind.Color)
            lines.Add("Drag to a shader input to connect.");
        return string.Join("\n", lines);
    }

    private static string TipFor(GraphNode n)
    {
        var lines = new List<string> { n.Title };
        if (n.Subtitle.Length > 0 && n.Subtitle != n.Title) lines.Add(n.Subtitle);
        if (n.TexturePath.Length > 0) lines.Add(n.TexturePath);
        if (n.Message.Length > 0) lines.Add(n.Message);
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
        GraphNodeKind.Texture or GraphNodeKind.Sample => p.Texture,
        GraphNodeKind.Scalar or GraphNodeKind.Vector or GraphNodeKind.Color or GraphNodeKind.Input => p.Param,
        GraphNodeKind.Math => p.Macro,
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
        var wm = Text(ShaderEditor is null ? "MATERIAL" : "SHADER GRAPH", 76, WithOpacity(pal.TextDim, 0.16), bold: true);
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
                if (_gesture == Gesture.Wire && _wireMove && w.ToPin == _wireInputIndex && _wireSrc is not null && w.FromNode == _wireSrc.Id && b.Kind == GraphNodeKind.Shader)
                    continue;   // the link being carried is drawn as the live wire
                var (x0, y0) = a.PinPosition(a.Outputs[w.FromPin]);
                var (x1, y1) = b.PinPosition(b.Inputs[w.ToPin]);
                if (!new Rect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0)).Intersects(view)) continue;

                bool lit = SelectedNodeId is { } s && (w.FromNode == s || w.ToNode == s);
                bool dimmed = related is not null && !(related.Contains(w.FromNode) && related.Contains(w.ToNode));
                if (_gesture == Gesture.SgWire && SgIsCarried(w, g)) continue;   // the wire being moved is drawn as the live wire
                var color = w.Error ? Res("ReyErrorBrush", Brushes.Red) : KindBrush(pal, w.SourceKind);
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

            if (_gesture == Gesture.Wire) DrawLiveWire(ctx, pal, g);
            if (_gesture == Gesture.SgWire) SgDrawLiveWire(ctx, pal, g);
        }

        // ---- overlay, in screen space ----
        if (Breadcrumb.Length > 0)
            ctx.DrawText(Text(Breadcrumb, 15, WithOpacity(pal.TextBright, 0.9), bold: true, maxWidth: Math.Max(80, Bounds.Width - 220)),
                new Point(16, 12));
        ctx.DrawText(Text(ZoomText, 11, pal.TextDim), new Point(16, Bounds.Height - 24));
        if (_gesture == Gesture.Wire) DrawWireLabel(ctx, pal);
        if (_gesture == Gesture.SgWire) SgDrawWireLabel(ctx, pal);
        if (ModeText.Length > 0)
        {
            var mode = Text(ModeText, 10, ModeText == "EDITABLE" ? pal.Accent : pal.TextDim, bold: true);
            ctx.DrawText(mode, new Point(Bounds.Width - mode.Width - 16, 14));
        }
    }

    /// <summary>M831: the wire being dragged, from its output end to its input end, one of them at the pointer (or snapped to the pin
    /// it is over). Red with the reason beside it over a pin it may not feed; the kind colour otherwise.</summary>
    private void DrawLiveWire(DrawingContext ctx, Palette pal, MaterialGraph g)
    {
        var cursor = ToGraph(_wireCursor);
        var shader = g.ShaderNode;
        (double X, double Y)? outEnd = null, inEnd = null;
        var pinBrush = pal.Param;
        if (_wireSrc is not null) { outEnd = _wireSrc.PinPosition(_wireSrc.Outputs[0]); pinBrush = KindBrush(pal, _wireSrc.Kind); }
        if (shader is not null && _wireInputIndex >= 0 && _wireInputIndex < shader.Inputs.Count && _wireFromInput) inEnd = shader.PinPosition(shader.Inputs[_wireInputIndex]);
        if (!_wireFromInput && _wireSrc is not null && _wireHover is { } hi && hi.Pin.IsInput) inEnd = hi.Node.PinPosition(hi.Pin);
        if (_wireFromInput && _wireHover is { } ho && !ho.Pin.IsInput) { outEnd = ho.Node.PinPosition(ho.Pin); pinBrush = KindBrush(pal, ho.Node.Kind); }
        if (_wireFromInput && _wireSrc is not null && _wireHover is { } hm && hm.Pin.IsInput) inEnd = hm.Node.PinPosition(hm.Pin);
        if (_wireFromInput && inEnd is null && shader is not null && _wireInputIndex >= 0) inEnd = shader.PinPosition(shader.Inputs[_wireInputIndex]);

        bool bad = _wireHover is not null && !_wireCheck.Ok && _wireCheck.Reason.Length > 0;
        var color = bad ? Res("ReyErrorBrush", Brushes.Red) : pinBrush;
        var (x0, y0) = outEnd ?? (cursor.X, cursor.Y);
        var (x1, y1) = inEnd ?? (cursor.X, cursor.Y);
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            double dx = Math.Max(48, Math.Abs(x1 - x0) * 0.5);
            gc.BeginFigure(new Point(x0, y0), false);
            gc.CubicBezierTo(new Point(x0 + dx, y0), new Point(x1 - dx, y1), new Point(x1, y1));
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(color, Math.Max(2.8, 3.0 / _zoom), lineCap: PenLineCap.Round), geo);

        if (_wireHover is { } h)
        {
            var (hx, hy) = h.Node.PinPosition(h.Pin);
            ctx.DrawEllipse(null, new Pen(bad ? color : pal.Accent, 2.2), new Point(hx, hy), 8, 8);
        }

    }

    /// <summary>The line beside the pointer while a wire is dragged: why it may not land here (red) or that it will connect. Drawn in screen
    /// space so it reads at any zoom.</summary>
    private void DrawWireLabel(DrawingContext ctx, Palette pal)
    {
        bool bad = _wireHover is not null && !_wireCheck.Ok && _wireCheck.Reason.Length > 0;
        string label = bad ? _wireCheck.Reason : _wireHover is not null && _wireCheck.Ok ? "Release to connect" : "";
        if (label.Length == 0) return;
        var color = bad ? Res("ReyErrorBrush", Brushes.Red) : pal.TextBright;
        var ft = Text(label, 12, color, bold: true, maxWidth: Math.Min(420, Math.Max(120, Bounds.Width - 40)), lines: 4);
        var at = new Point(Math.Clamp(_wireCursor.X + 18, 8, Math.Max(8, Bounds.Width - ft.Width - 20)), Math.Clamp(_wireCursor.Y + 16, 8, Math.Max(8, Bounds.Height - ft.Height - 16)));
        ctx.DrawRectangle(WithOpacity(pal.Card, 0.97), new Pen(bad ? color : pal.Accent, 1.2), new Rect(at.X - 7, at.Y - 4, ft.Width + 14, ft.Height + 8), 4, 4);
        ctx.DrawText(ft, at);
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
            : n.State == GraphNodeState.Error ? new Pen(Res("ReyErrorBrush", Brushes.Red), 2.0)
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
            GraphNodeKind.Shader => "SHADER", GraphNodeKind.Sample => "TEXTURE", GraphNodeKind.Math => "MATH",
            GraphNodeKind.Input => "INPUT", GraphNodeKind.Output when ShaderEditor is not null => "OUTPUT", _ => "MATERIAL",
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
