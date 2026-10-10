using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Materials.Graph;
using ReyEngine.Formats.Materials.ShaderGraph;

namespace ReyEngine.App.Views;

/// <summary>
/// M832: the Shader Graph half of the canvas. The same drawing, pan, zoom, pins and wires as the Material Graph, but the gestures are
/// the node editor's: drag from any output pin to any input pin (one wire per input; a new one replaces it), pick a wire up by its input
/// end to move it or drop it off a pin to disconnect, Alt+click a pin to break its wires, Delete removes the node, and a right click on
/// empty canvas opens a search palette of the nodes the base shader allows. The canvas only ASKS: every change is made (and undone) by
/// <see cref="ShaderGraphViewModel"/>. Gesture state holds node ids, never node objects, because the graph is projected anew after edits.
/// </summary>
public sealed partial class MaterialGraphCanvas
{
    // ---- wire drag
    private string? _sgSrcNode, _sgSrcPin;            // the output end (null while starting from an unwired input)
    private string? _sgDstNode, _sgDstPin;            // the input end (null while starting from an output)
    private bool _sgCarry;                            // the wire was picked up from its input end: dropping elsewhere moves it
    private Point _sgCursor, _sgDown;
    private bool _sgMoved;
    private (string Node, string Pin, bool IsInput)? _sgHover;
    private GraphLinkCheck _sgCheck;

    /// <summary>True while the wire <paramref name="w"/> is the one being carried (it is drawn as the live wire instead).</summary>
    private bool SgIsCarried(GraphWire w, MaterialGraph g) =>
        _sgCarry && _sgDstNode is not null && w.ToNode == _sgDstNode && g.Find(w.ToNode) is { } to && w.ToPin < to.Inputs.Count && to.Inputs[w.ToPin].Name == _sgDstPin;

    private bool SgPointerPressed(PointerPressedEventArgs e, PointerPoint pt)
    {
        var se = ShaderEditor!;
        if (!pt.Properties.IsLeftButtonPressed) return false;
        if (PinAt(pt.Position) is not { } hit) return false;

        SelectedNodeId = hit.Node.Id;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { se.BreakLinks(hit.Node.Id, hit.Pin.Name, hit.Pin.IsInput); return true; }

        _sgSrcNode = _sgSrcPin = _sgDstNode = _sgDstPin = null;
        _sgCarry = false;
        if (!hit.Pin.IsInput) { _sgSrcNode = hit.Node.Id; _sgSrcPin = hit.Pin.Name; }
        else
        {
            _sgDstNode = hit.Node.Id; _sgDstPin = hit.Pin.Name;
            if (se.Document?.LinkInto(hit.Node.Id, hit.Pin.Name) is { } link)
            {
                // an authored wire is picked up by its input end: it MOVES (or is taken off)
                _sgSrcNode = link.FromNode; _sgSrcPin = link.FromPin;
                _sgCarry = true;
            }
        }
        _gesture = Gesture.SgWire;
        _sgCursor = _sgDown = pt.Position;
        _sgMoved = false;
        _sgHover = null;
        _sgCheck = default;
        Cursor = new Cursor(StandardCursorType.Cross);
        e.Pointer.Capture(this);
        InvalidateVisual();
        return true;
    }

    private void SgUpdateWire(Point pos)
    {
        var se = ShaderEditor;
        _sgCursor = pos;
        if (Math.Abs(pos.X - _sgDown.X) > 3 || Math.Abs(pos.Y - _sgDown.Y) > 3) _sgMoved = true;
        _sgHover = null;
        _sgCheck = default;
        if (se is not null && PinAt(pos) is { } h)
        {
            if (_sgSrcNode is not null && h.Pin.IsInput)
            {
                // an output (or a carried wire) over an input
                _sgHover = (h.Node.Id, h.Pin.Name, true);
                bool same = _sgCarry && h.Node.Id == _sgDstNode && h.Pin.Name == _sgDstPin;
                _sgCheck = same ? new GraphLinkCheck(false, "") : se.CheckLink(_sgSrcNode, _sgSrcPin!, h.Node.Id, h.Pin.Name);
            }
            else if (_sgSrcNode is null && _sgDstNode is not null && !h.Pin.IsInput)
            {
                // an unwired input pulled to an output
                _sgHover = (h.Node.Id, h.Pin.Name, false);
                _sgCheck = se.CheckLink(h.Node.Id, h.Pin.Name, _sgDstNode, _sgDstPin!);
            }
        }
        InvalidateVisual();
    }

    private void SgFinishWire()
    {
        var se = ShaderEditor;
        var hover = _sgHover;
        var check = _sgCheck;
        string? srcNode = _sgSrcNode, srcPin = _sgSrcPin, dstNode = _sgDstNode, dstPin = _sgDstPin;
        bool carry = _sgCarry, moved = _sgMoved;
        var cursor = _sgCursor;
        var g = Graph;
        // how far the pointer is from the pin the wire was picked up at: a wire is only taken off once it was pulled clearly away
        double away = double.MaxValue;
        if (dstNode is not null && g?.Find(dstNode) is { } dn && dn.Inputs.FirstOrDefault(p => p.Name == dstPin) is { } dp)
        {
            var o = PinScreen(dn, dp);
            away = Math.Sqrt((o.X - cursor.X) * (o.X - cursor.X) + (o.Y - cursor.Y) * (o.Y - cursor.Y));
        }
        CancelGesture();
        _sgSrcNode = _sgSrcPin = _sgDstNode = _sgDstPin = null;
        _sgHover = null; _sgCarry = false;
        InvalidateVisual();
        if (se is null || !moved) return;   // a click on a pin only selected the node

        if (hover is { } h)
        {
            if (!check.Ok) { if (check.Reason.Length > 0) se.Notify(check.Reason); return; }
            if (carry) se.MoveWire(srcNode!, srcPin!, dstNode!, dstPin!, h.Node, h.Pin);
            else if (!h.IsInput) se.Connect(h.Node, h.Pin, dstNode!, dstPin!);
            else se.Connect(srcNode!, srcPin!, h.Node, h.Pin);
            return;
        }
        if (carry) { if (away > 18) se.Disconnect(dstNode!, dstPin!); return; }
        se.Notify("No wire made: drop it on " + (srcNode is null ? "a node's output pin." : "a node's input pin."));
    }

    private void SgDrawLiveWire(DrawingContext ctx, Palette pal, MaterialGraph g)
    {
        var cursor = ToGraph(_sgCursor);
        (double X, double Y)? outEnd = null, inEnd = null;
        var brush = pal.Param;
        if (_sgSrcNode is not null && g.Find(_sgSrcNode) is { } sn && sn.Outputs.FirstOrDefault(p => p.Name == _sgSrcPin) is { } sp)
        {
            outEnd = sn.PinPosition(sp);
            brush = KindBrush(pal, sn.Kind);
        }
        if (_sgDstNode is not null && g.Find(_sgDstNode) is { } dn && dn.Inputs.FirstOrDefault(p => p.Name == _sgDstPin) is { } dp && !_sgCarry)
            inEnd = dn.PinPosition(dp);
        if (_sgHover is { } h && g.Find(h.Node) is { } hn && hn.Pins(h.IsInput).FirstOrDefault(p => p.Name == h.Pin) is { } hp)
        {
            if (h.IsInput) inEnd = hn.PinPosition(hp); else { outEnd = hn.PinPosition(hp); brush = KindBrush(pal, hn.Kind); }
        }

        bool bad = _sgHover is not null && !_sgCheck.Ok && _sgCheck.Reason.Length > 0;
        var color = bad ? Res("ReyErrorBrush", Brushes.Red) : brush;
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
        if (_sgHover is { } hh && g.Find(hh.Node) is { } hnode && hnode.Pins(hh.IsInput).FirstOrDefault(p => p.Name == hh.Pin) is { } hpin)
        {
            var (hx, hy) = hnode.PinPosition(hpin);
            ctx.DrawEllipse(null, new Pen(bad ? color : pal.Accent, 2.2), new Point(hx, hy), 8, 8);
        }
    }

    private void SgDrawWireLabel(DrawingContext ctx, Palette pal)
    {
        bool bad = _sgHover is not null && !_sgCheck.Ok && _sgCheck.Reason.Length > 0;
        string label = bad ? _sgCheck.Reason : _sgHover is not null && _sgCheck.Ok ? "Release to connect" : "";
        if (label.Length == 0) return;
        var color = bad ? Res("ReyErrorBrush", Brushes.Red) : pal.TextBright;
        var ft = Text(label, 12, color, bold: true, maxWidth: Math.Min(420, Math.Max(120, Bounds.Width - 40)), lines: 4);
        var at = new Point(Math.Clamp(_sgCursor.X + 18, 8, Math.Max(8, Bounds.Width - ft.Width - 20)), Math.Clamp(_sgCursor.Y + 16, 8, Math.Max(8, Bounds.Height - ft.Height - 16)));
        ctx.DrawRectangle(WithOpacity(pal.Card, 0.97), new Pen(bad ? color : pal.Accent, 1.2), new Rect(at.X - 7, at.Y - 4, ft.Width + 14, ft.Height + 8), 4, 4);
        ctx.DrawText(ft, at);
    }

    // =============================================================================== the right-click palette

    /// <summary>The palette lines for a filter (public so a probe can read what the user would see).</summary>
    public IReadOnlyList<SgPaletteEntry> PaletteEntries(string? filter) =>
        ShaderEditor is null ? Array.Empty<SgPaletteEntry>() : SgCatalog.Search(ShaderEditor.Palette, filter);

    /// <summary>Add the palette line at a canvas point (what picking it does).</summary>
    public string? AddFromPalette(SgPaletteEntry entry, Point screen)
    {
        var p = ToGraph(screen);
        return ShaderEditor?.AddNode(entry, p.X, p.Y);
    }

    /// <summary>The menu items for a right click on a node (public for the probe): delete, break wires, add.</summary>
    public IReadOnlyList<Control> BuildNodeMenu(Point screen)
    {
        var items = new List<Control>();
        var se = ShaderEditor;
        var node = NodeAt(screen);
        if (se is null || node is null) return items;
        string id = node.Id;
        MenuItem Item(string header, Action act, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => act();
            return mi;
        }
        items.Add(Item($"Delete {node.Title}", () => se.DeleteNode(id), node.Kind != GraphNodeKind.Output));
        items.Add(Item("Break all wires of this node", () => se.BreakNodeLinks(id)));
        items.Add(new Separator());
        items.Add(Item("Add node...", () => SgShowPalette(screen)));
        return items;
    }

    private void SgShowMenu(Point screen)
    {
        if (ShaderEditor is null || Graph is null) return;
        if (NodeAt(screen) is not null)
        {
            var menu = new ContextMenu { ItemsSource = BuildNodeMenu(screen) };
            menu.Open(this);
            return;
        }
        SgShowPalette(screen);
    }

    private void SgShowPalette(Point screen)
    {
        var se = ShaderEditor;
        if (se is null) return;
        var box = new TextBox { Watermark = "Search nodes: texture, parameter, lerp, multiply...", MinWidth = 300 };
        var list = new ListBox
        {
            MaxHeight = 320, MinWidth = 300,
            ItemsSource = PaletteEntries(null),
            ItemTemplate = new FuncDataTemplate<SgPaletteEntry>((entry, _) => new TextBlock { Text = entry?.Display ?? "", FontSize = 11.5 }),
        };
        var panel = new StackPanel { Spacing = 4, MinWidth = 300 };
        panel.Children.Add(box);
        panel.Children.Add(list);
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Pointer };
        void Pick(SgPaletteEntry? entry)
        {
            if (entry is null) return;
            AddFromPalette(entry, screen);
            flyout.Hide();
        }
        box.TextChanged += (_, _) => { list.ItemsSource = PaletteEntries(box.Text); if (list.ItemCount > 0) list.SelectedIndex = 0; };
        box.KeyDown += (_, k) =>
        {
            if (k.Key == Key.Enter) { Pick(list.SelectedItem as SgPaletteEntry ?? PaletteEntries(box.Text).FirstOrDefault()); k.Handled = true; }
            else if (k.Key == Key.Down && list.ItemCount > 0) { list.SelectedIndex = Math.Min(list.ItemCount - 1, list.SelectedIndex + 1); k.Handled = true; }
            else if (k.Key == Key.Up && list.ItemCount > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); k.Handled = true; }
        };
        list.DoubleTapped += (_, _) => Pick(list.SelectedItem as SgPaletteEntry);
        list.KeyDown += (_, k) => { if (k.Key == Key.Enter) { Pick(list.SelectedItem as SgPaletteEntry); k.Handled = true; } };
        flyout.Opened += (_, _) => box.Focus();
        flyout.ShowAt(this);
    }
}

internal static class SgGraphNodeExtensions
{
    public static IReadOnlyList<GraphPin> Pins(this GraphNode n, bool inputs) => inputs ? n.Inputs : n.Outputs;
}
