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
        // M835 (Unreal): a wire dropped on nothing opens the node search, filtered to what fits, and the picked node is connected
        if (srcNode is not null && dstNode is null) { SgShowAutoPalette(cursor, srcNode, srcPin!, fromIsOutput: true); return; }
        if (dstNode is not null && srcNode is null) { SgShowAutoPalette(cursor, dstNode, dstPin!, fromIsOutput: false); return; }
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
        DrawLiveStroke(ctx, pal, geo, color, bad, _sgHover is not null && _sgCheck.Ok);
        if (_sgHover is { } hh && g.Find(hh.Node) is { } hnode && hnode.Pins(hh.IsInput).FirstOrDefault(p => p.Name == hh.Pin) is { } hpin)
        {
            var (hx, hy) = hnode.PinPosition(hpin);
            if (bad) ctx.DrawEllipse(null, new Pen(WithOpacity(color, 0.30), 7), new Point(hx, hy), 8, 8);
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
        items.Add(Item("Rename node (F2)", () => { SelectedNodeId = id; SgRenameSelected(screen); }));
        items.Add(Item("Comment around this node", () => se.AddCommentAround(_multi.Contains(id) ? _multi.ToList() : new List<string> { id })));
        items.Add(new Separator());
        items.Add(Item("Add node...", () => SgShowPalette(screen)));
        return items;
    }

    /// <summary>The menu for a right click on empty canvas (public for the probe): add a node, add a comment, comment the selection.</summary>
    public IReadOnlyList<Control> BuildCanvasMenu(Point screen)
    {
        var items = new List<Control>();
        var se = ShaderEditor;
        if (se is null) return items;
        MenuItem Item(string header, Action act, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => act();
            return mi;
        }
        var at = ToGraph(screen);
        items.Add(Item("Add node...", () => SgShowPalette(screen)));
        items.Add(new Separator());
        items.Add(Item("Add comment here", () => se.AddComment(at.X, at.Y)));
        var sel = SelectionIds();
        items.Add(Item("Comment around selection", () => se.AddCommentAround(sel), sel.Count > 0));
        return items;
    }

    /// <summary>The menu for a right click on a comment box (public for the probe).</summary>
    public IReadOnlyList<Control> BuildCommentMenu(Point screen)
    {
        var items = new List<Control>();
        var se = ShaderEditor;
        if (se is null || CommentAt(screen) is not { } c) return items;
        string id = c.Id;
        var rename = new MenuItem { Header = "Rename comment (F2)" };
        rename.Click += (_, _) => { se.SelectedCommentId = id; SgRenameSelected(screen); };
        items.Add(rename);
        var colour = new MenuItem { Header = "Colour" };
        foreach (string key in SgComment.ColorKeys)
        {
            string k = key;
            var mi = new MenuItem { Header = k == "Comment" ? "Default" : k };
            mi.Click += (_, _) => se.SetCommentColor(id, k);
            colour.Items.Add(mi);
        }
        items.Add(colour);
        var del = new MenuItem { Header = "Delete comment" };
        del.Click += (_, _) => { se.DeleteComment(id); se.SelectedCommentId = null; };
        items.Add(del);
        items.Add(new Separator());
        var add = new MenuItem { Header = "Add node..." };
        add.Click += (_, _) => SgShowPalette(screen);
        items.Add(add);
        return items;
    }

    /// <summary>M835: the menu for a right click on an UNWIRED input pin (public for the probe): promote it to a custom parameter, or feed it a constant.</summary>
    public IReadOnlyList<Control> BuildPinMenu(Point screen)
    {
        var items = new List<Control>();
        var se = ShaderEditor;
        if (se is null || PinAt(screen) is not { } hit || !hit.Pin.IsInput || hit.Pin.Linked) return items;
        string node = hit.Node.Id, pin = hit.Pin.Name;
        string? why = se.CanPromote(node, pin);
        MenuItem Item(string header, Action act, string tip)
        {
            var mi = new MenuItem { Header = header, IsEnabled = why is null };
            ToolTip.SetTip(mi, why ?? tip);
            mi.Click += (_, _) => act();
            return mi;
        }
        items.Add(Item("Promote to Parameter", () => se.PromoteToParameter(node, pin),
            "Creates a custom parameter of this pin's type, holding its current value, and wires it in. A material can then set it."));
        items.Add(Item("Add Constant", () => se.AddConstantTo(node, pin), "Creates a Constant node holding this pin's current value and wires it in."));
        items.Add(new Separator());
        var search = new MenuItem { Header = "Connect a node..." };
        ToolTip.SetTip(search, "Opens the node search, filtered to the nodes that can feed this pin.");
        search.Click += (_, _) => SgShowAutoPalette(screen, node, pin, fromIsOutput: false);
        items.Add(search);
        return items;
    }

    private void SgShowMenu(Point screen)
    {
        if (ShaderEditor is null || Graph is null) return;
        IReadOnlyList<Control> items = PinAt(screen) is { Pin.IsInput: true, Pin.Linked: false } ? BuildPinMenu(screen)
            : NodeAt(screen) is not null ? BuildNodeMenu(screen)
            : CommentAt(screen) is not null ? BuildCommentMenu(screen)
            : BuildCanvasMenu(screen);
        if (items.Count == 0) return;
        new ContextMenu { ItemsSource = items }.Open(this);
    }

    // =============================================================================== M834: comment boxes, multi-select, renaming

    private readonly HashSet<string> _multi = new(StringComparer.Ordinal);
    private string? _cmId;
    private Point _cmDown;
    private (double X, double Y) _cmOrigin;
    private (double W, double H) _cmSize;
    private readonly Dictionary<string, (double X, double Y)> _cmNodes = new(StringComparer.Ordinal);

    public const double CommentTitleHeight = 34;

    /// <summary>A new projection arrived (every edit and every compile makes one): ids of nodes that are gone leave the selection, and a comment drag whose comment is gone ends.</summary>
    private void SgGraphChanged(MaterialGraph? g)
    {
        if (g is null) { _multi.Clear(); _cmNodes.Clear(); _cmId = null; return; }
        _multi.RemoveWhere(id => g.Find(id) is null);
        foreach (string id in _cmNodes.Keys.Where(id => g.Find(id) is null).ToList()) _cmNodes.Remove(id);
        if (_cmId is not null && g.Comments.All(c => c.Id != _cmId))
        {
            _cmId = null; _cmNodes.Clear();
            if (_gesture is Gesture.CommentMove or Gesture.CommentResize) { _gesture = Gesture.None; Cursor = null; }
        }
    }

    /// <summary>The nodes a comment would go around: the Ctrl+click selection plus the selected node.</summary>
    public IReadOnlyList<string> SelectionIds()
    {
        var ids = new List<string>(_multi);
        if (SelectedNodeId is { } s && !ids.Contains(s)) ids.Add(s);
        return ids;
    }

    private GraphComment? CommentAt(Point screen)
    {
        var g = Graph;
        if (g is null) return null;
        var p = ToGraph(screen);
        for (int i = g.Comments.Count - 1; i >= 0; i--)
            if (g.Comments[i].Contains(p.X, p.Y)) return g.Comments[i];
        return null;
    }

    /// <summary>A left press on a node or on empty canvas, Shader Graph mode. True when it was handled here (Ctrl+click selection, or a comment gesture).</summary>
    private bool SgNodeOrCommentPressed(PointerPressedEventArgs e, PointerPoint pt, GraphNode? node)
    {
        var se = ShaderEditor!;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (node is not null)
        {
            if (ctrl)
            {
                if (SelectedNodeId is { } prim) _multi.Add(prim);
                if (!_multi.Remove(node.Id)) _multi.Add(node.Id);
                SelectedNodeId = node.Id;
                InvalidateVisual();
                return true;
            }
            _multi.Clear();
            se.SelectedCommentId = null;
            return false;   // the ordinary node drag
        }
        if (!ctrl) _multi.Clear();
        var c = CommentAt(pt.Position);
        if (c is null) { se.SelectedCommentId = null; return false; }

        SelectedNodeId = null;
        se.SelectedCommentId = c.Id;
        var gp = ToGraph(pt.Position);
        double grip = 20 / Math.Max(_zoom, 0.3);
        bool resize = gp.X >= c.Right - grip && gp.Y >= c.Bottom - grip;
        bool title = gp.Y <= c.Y + CommentTitleHeight;
        InvalidateVisual();
        if (!resize && !title) return false;   // inside the box: an empty-canvas press (pans)

        _cmId = c.Id; _cmDown = pt.Position; _cmOrigin = (c.X, c.Y); _cmSize = (c.Width, c.Height);
        _cmNodes.Clear();
        if (!resize)
            foreach (string id in se.NodesInside(c.Id))
                if (Graph!.Find(id) is { } n) _cmNodes[id] = (n.X, n.Y);
        _gesture = resize ? Gesture.CommentResize : Gesture.CommentMove;
        Cursor = new Cursor(resize ? StandardCursorType.BottomRightCorner : StandardCursorType.SizeAll);
        e.Pointer.Capture(this);
        return true;
    }

    private void SgUpdateComment(Point pos)
    {
        var se = ShaderEditor;
        if (se is null || _cmId is null) return;
        double dx = (pos.X - _cmDown.X) / _zoom, dy = (pos.Y - _cmDown.Y) / _zoom;
        if (_gesture == Gesture.CommentResize) se.ResizeComment(_cmId, _cmSize.W + dx, _cmSize.H + dy);
        else se.MoveComment(_cmId, _cmOrigin.X + dx, _cmOrigin.Y + dy, _cmNodes.ToDictionary(k => k.Key, k => (k.Value.X + dx, k.Value.Y + dy)));
        InvalidateVisual();
    }

    private IBrush CommentKeyBrush(Palette pal, string key) => key switch
    {
        "Texture" => pal.Texture, "Param" => pal.Param, "Switch" => pal.Switch, "Macro" => pal.Macro, "Shader" => pal.Shader, "Output" => pal.Output,
        _ => pal.CommentBorder,
    };

    private void DrawComment(DrawingContext ctx, Palette pal, GraphComment c, bool titles)
    {
        var rect = new Rect(c.X, c.Y, c.Width, c.Height);
        bool selected = ShaderEditor?.SelectedCommentId == c.Id;
        var key = CommentKeyBrush(pal, c.ColorKey);
        bool plain = c.ColorKey == "Comment";
        ctx.DrawRectangle(plain ? pal.Comment : WithOpacity(key, 0.16), new Pen(selected ? pal.Accent : plain ? pal.CommentBorder : WithOpacity(key, 0.85), selected ? 2.4 : 1.4), rect, 8, 8);
        using (ctx.PushClip(new Rect(c.X, c.Y, c.Width, CommentTitleHeight)))
            ctx.DrawRectangle(WithOpacity(key, plain ? 0.35 : 0.30), null, new Rect(c.X, c.Y, c.Width, CommentTitleHeight + 10), 8, 8);
        if (titles)
            ctx.DrawText(Text(c.Title.Length > 0 ? c.Title : "Comment", 15, pal.TextBright, bold: true, maxWidth: Math.Max(20, c.Width - 28)), new Point(c.X + 12, c.Y + 6));
        // the resize grip, bottom right
        var grip = new Pen(WithOpacity(pal.TextDim, 0.8), 1.5);
        ctx.DrawLine(grip, new Point(c.Right - 6, c.Bottom - 16), new Point(c.Right - 16, c.Bottom - 6));
        ctx.DrawLine(grip, new Point(c.Right - 6, c.Bottom - 10), new Point(c.Right - 10, c.Bottom - 6));
    }

    /// <summary>F2 / double-click: rename the selected comment, else the selected node (a label, shown in its title bar).</summary>
    private void SgRenameSelected(Point? at = null)
    {
        var se = ShaderEditor;
        if (se is null || Graph is null) return;
        var where = at ?? new Point(Bounds.Width / 2, Bounds.Height / 3);
        if (se.SelectedCommentId is { } cid && Graph.Comments.FirstOrDefault(k => k.Id == cid) is { } c)
            SgShowTextEditor(where, "Comment title", c.Title, t => se.SetCommentTitle(cid, t));
        else if (SelectedNodeId is { } nid && se.Document?.Find(nid) is { } n)
            SgShowTextEditor(where, "Node name (empty = the type's own title)", n.Label, t => se.SetLabel(nid, t));
    }

    private void SgShowTextEditor(Point screen, string header, string initial, Func<string, bool> commit)
    {
        var box = new TextBox { Text = initial, MinWidth = 260, MaxLength = 128 };
        var panel = new StackPanel { Spacing = 4, MinWidth = 260 };
        panel.Children.Add(new TextBlock { Text = header + " - Enter applies, Esc cancels", FontSize = 11 });
        panel.Children.Add(box);
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Pointer };
        box.KeyDown += (_, k) =>
        {
            if (k.Key == Key.Enter) { commit(box.Text ?? ""); flyout.Hide(); k.Handled = true; }
            else if (k.Key == Key.Escape) { flyout.Hide(); k.Handled = true; }
        };
        flyout.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        flyout.ShowAt(this);
    }

    private void SgOnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ShaderEditor is null || Graph is null) return;
        var pos = e.GetPosition(this);
        if (PinAt(pos) is not null) return;
        if (NodeAt(pos) is { } n) { SelectedNodeId = n.Id; ShaderEditor.SelectedCommentId = null; SgRenameSelected(pos); e.Handled = true; }
        else if (CommentAt(pos) is { } c && ToGraph(pos).Y <= c.Y + CommentTitleHeight) { ShaderEditor.SelectedCommentId = c.Id; SgRenameSelected(pos); e.Handled = true; }
    }

    /// <summary>One line of a palette list: the node to create and, for a wire dragged into empty space, the pin of the new node it ends at.</summary>
    public sealed record SgPaletteRow(SgPaletteEntry Entry, string? Pin)
    {
        public string Text => Pin is null ? Entry.Display : Entry.Display + "   (" + Pin + ")";
    }

    private void SgShowPalette(Point screen)
    {
        var se = ShaderEditor;
        if (se is null) return;
        SgShowPaletteCore(screen, "Search nodes: texture, parameter, lerp, multiply...", f => PaletteEntries(f).Select(e => new SgPaletteRow(e, null)).ToList(),
            row => AddFromPalette(row.Entry, screen));
    }

    /// <summary>The compatible lines for a wire dragged from a pin into empty space (public for the probe and the tests).</summary>
    public IReadOnlyList<SgPaletteRow> AutoConnectRows(string fromNode, string fromPin, bool fromIsOutput, string? filter)
    {
        var se = ShaderEditor;
        var g = Graph;
        if (se?.Document is not { } doc || g?.Find(fromNode) is not { } node) return Array.Empty<SgPaletteRow>();
        var pins = fromIsOutput ? node.Outputs : node.Inputs;
        var pin = pins.FirstOrDefault(p => p.Name == fromPin);
        if (pin is null) return Array.Empty<SgPaletteRow>();
        var matches = fromIsOutput
            ? SgAutoConnect.ForOutput(se.Palette, doc, se.Base, Math.Max(1, pin.Components))
            : SgAutoConnect.ForInput(se.Palette, doc, se.Base, pin.Components);
        return SgAutoConnect.Filter(matches, filter).Select(m => new SgPaletteRow(m.Entry, m.Pin)).ToList();
    }

    /// <summary>Add the picked line at the drop point and wire it to the pin the wire came from (ONE undo step). Public for the probe.</summary>
    public string? AddAutoConnected(SgPaletteRow row, Point screen, string fromNode, string fromPin, bool fromIsOutput)
    {
        var p = ToGraph(screen);
        // the new node sits to the right of an output it is fed by, to the left of an input it feeds
        double x = fromIsOutput ? p.X : p.X - SgGraphView.NodeWidth;
        return ShaderEditor?.AddNodeAndConnect(row.Entry, x, p.Y, fromNode, fromPin, fromIsOutput, row.Pin ?? "");
    }

    private void SgShowAutoPalette(Point screen, string fromNode, string fromPin, bool fromIsOutput)
    {
        if (ShaderEditor is null) return;
        SgShowPaletteCore(screen, fromIsOutput ? "Nodes that take this wire - type to filter" : "Nodes that can feed this input - type to filter",
            f => AutoConnectRows(fromNode, fromPin, fromIsOutput, f), row => AddAutoConnected(row, screen, fromNode, fromPin, fromIsOutput));
    }

    private void SgShowPaletteCore(Point screen, string watermark, Func<string?, IReadOnlyList<SgPaletteRow>> source, Action<SgPaletteRow> pick)
    {
        var box = new TextBox { Watermark = watermark, MinWidth = 300 };
        var list = new ListBox
        {
            MaxHeight = 320, MinWidth = 300,
            ItemsSource = source(null),
            ItemTemplate = new FuncDataTemplate<SgPaletteRow>((row, _) =>
            {
                var tb = new TextBlock { Text = row?.Text ?? "", FontSize = 11.5 };
                if (row is not null && row.Entry.Tooltip.Length > 0) ToolTip.SetTip(tb, row.Entry.Tooltip);
                return tb;
            }),
        };
        var panel = new StackPanel { Spacing = 4, MinWidth = 300 };
        panel.Children.Add(box);
        panel.Children.Add(list);
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Pointer };
        void Pick(SgPaletteRow? row)
        {
            if (row is null) return;
            pick(row);
            flyout.Hide();
        }
        box.TextChanged += (_, _) => { list.ItemsSource = source(box.Text); if (list.ItemCount > 0) list.SelectedIndex = 0; };
        box.KeyDown += (_, k) =>
        {
            if (k.Key == Key.Enter) { Pick(list.SelectedItem as SgPaletteRow ?? source(box.Text).FirstOrDefault()); k.Handled = true; }
            else if (k.Key == Key.Down && list.ItemCount > 0) { list.SelectedIndex = Math.Min(list.ItemCount - 1, list.SelectedIndex + 1); k.Handled = true; }
            else if (k.Key == Key.Up && list.ItemCount > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); k.Handled = true; }
            else if (k.Key == Key.Escape) { flyout.Hide(); k.Handled = true; }
        };
        list.DoubleTapped += (_, _) => Pick(list.SelectedItem as SgPaletteRow);
        list.KeyDown += (_, k) => { if (k.Key == Key.Enter) { Pick(list.SelectedItem as SgPaletteRow); k.Handled = true; } };
        flyout.Opened += (_, _) => box.Focus();
        flyout.ShowAt(this);
    }
}

internal static class SgGraphNodeExtensions
{
    public static IReadOnlyList<GraphPin> Pins(this GraphNode n, bool inputs) => inputs ? n.Inputs : n.Outputs;
}
