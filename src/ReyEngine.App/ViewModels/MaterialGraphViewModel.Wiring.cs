using System.Globalization;
using ReyEngine.Core.Undo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.Graph;

namespace ReyEngine.App.ViewModels;

/// <summary>The verdict on a link between a node's output and a shader input: allowed, or why not (shown as a red wire and a label).</summary>
public readonly record struct GraphLinkCheck(bool Ok, string Reason);

/// <summary>One input of the shader that the material does not author (its dashed pin): what the right-click list offers.</summary>
public sealed class GraphUnconnectedInput
{
    public required int PinIndex { get; init; }
    public required string Name { get; init; }
    public required bool IsTexture { get; init; }
    /// <summary>"texture, shader default x.tex" / "float4, default 1 1 1 1".</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// M831: the wiring half of the Material Graph view model - connect, move, disconnect, create and delete, on the material's own
/// entries, through the Material Editor that opened the window.
///
/// <para><b>What a wire is.</b> Riot binds by NAME: a <c>samplerValues</c> entry whose name matches a shader texture input feeds
/// it, a <c>paramValues</c> entry whose name matches a shader constant sets it. So the graph's nodes ARE the entries and a wire
/// is only the name match. Writing a wire writes a name:</para>
/// <list type="bullet">
/// <item><b>Drag from an output pin</b> to an input pin: one output feeds many inputs, as in Unreal. If the node already feeds an
/// input, the entry is COPIED under the new input's name (same path or value, same address modes); a node that feeds nothing (a
/// new sample, one the shader does not declare) is RENAMED to the input instead.</item>
/// <item><b>Drag a wire's end</b> from one input to another: the link MOVES - the entry is renamed, the old input falls back to the
/// shader default (it is dashed again).</item>
/// <item>An input takes ONE link: connecting to a pin that already has one removes the entries that fed it (every entry whose name
/// resolves to that input, repeats included).</item>
/// <item><b>Disconnect</b> (drag the end off, Alt+click the pin, Delete the node) removes the entry; the shader default applies.</item>
/// <item><b>Create here</b> authors a new entry for an unconnected input: a sampler gets the shader's own default texture path (or an
/// empty path when the shader names none), a parameter the shader default value.</item>
/// </list>
///
/// <para>Switches and defines stay toggles. A texture feeds texture inputs of its own shape (2D / cube) only and a parameter feeds a
/// constant with the same component count - a mismatch is refused with the reason, never truncated. Every gesture is ONE undo step
/// (a <see cref="CompositeCommand"/> of the editor's commands) on the editor's own undo stack.</para>
/// </summary>
public sealed partial class MaterialGraphViewModel
{
    // ============================================================================== state / helpers

    /// <summary>True when wires can be made now: an editable material and a resolved shader to read the inputs from.</summary>
    public bool CanWire => CanEdit && _info is not null && Graph?.ShaderNode is not null;

    /// <summary>Why wiring is not available (empty when it is).</summary>
    public string WireNote =>
        !CanEdit ? EditNote
        : _info is null || Graph?.ShaderNode is null ? "Wiring needs the shader's inputs, and none could be read for this material (see the Stats tab)."
        : "";

    private bool Refuse(string reason) { EditStatus = reason; return false; }

    /// <summary>A line for the status label (the canvas says why a gesture did nothing).</summary>
    public void Notify(string text) => EditStatus = text;

    private static string DimensionText(string dim) => dim switch
    {
        "tex2d" => "2D texture", "texcube" => "cube texture", "tex3d" => "3D texture", "" => "texture", _ => dim,
    };

    private static string TypeNameFor(int components) => components switch { 1 => "F32", 2 => "Vector2", 3 => "Vector3", _ => "Vector4" };

    private static string ZeroText(int components) => string.Join(", ", Enumerable.Repeat("0", Math.Max(1, components)));

    private bool IsWired(string nodeId) => Graph!.Wires.Any(w => w.FromNode == nodeId && w.ToNode == "shader");

    private TextureSlot? SlotOf(GraphNode n)
    {
        int i = IndexOf(n.Id, "tex:");
        return i >= 0 && _binding is not null && i < _binding.Slots.Count ? _binding.Slots[i] : null;
    }

    private MaterialParameter? ParameterOf(GraphNode n)
    {
        int i = IndexOf(n.Id, "param:");
        return i >= 0 && _binding is not null && i < _binding.Parameters.Count ? _binding.Parameters[i] : null;
    }

    /// <summary>The texture input (shader texture name) a sampler name feeds, or null.</summary>
    private string? TextureTargetOf(string samplerName) => _info?.TargetFor(samplerName);

    /// <summary>The model object a node stands for (a sampler, a parameter, a switch, a macro), or null for the shader, the output and
    /// the shader-default nodes. This is the node's IDENTITY: positional ids shift when an entry is added or removed, the object does not.</summary>
    private object? ModelOf(MaterialBinding b, string? id)
    {
        if (id is null) return null;
        int i;
        if ((i = IndexOf(id, "tex:")) >= 0) return i < b.Slots.Count ? b.Slots[i] : null;
        if ((i = IndexOf(id, "param:")) >= 0) return i < b.Parameters.Count ? b.Parameters[i] : null;
        if ((i = IndexOf(id, "sw:")) >= 0) { var all = (b.CanEditSwitches ? b.AllSwitches : b.SwitchEntries).ToList(); return i < all.Count ? all[i] : null; }
        if ((i = IndexOf(id, "mac:")) >= 0) { var all = (b.CanEditMacros ? b.AllMacros : b.MacroEntries).ToList(); return i < all.Count ? all[i] : null; }
        return null;
    }

    private static string? IdOf(MaterialBinding b, object model)
    {
        static int At<T>(IEnumerable<T> list, object m) { int k = 0; foreach (var x in list) { if (ReferenceEquals(x, m)) return k; k++; } return -1; }
        int i;
        if (model is TextureSlot && (i = At(b.Slots, model)) >= 0) return "tex:" + i;
        if (model is MaterialParameter && (i = At(b.Parameters, model)) >= 0) return "param:" + i;
        if (model is MaterialSwitch && (i = At(b.CanEditSwitches ? b.AllSwitches : b.SwitchEntries, model)) >= 0) return "sw:" + i;
        if (model is MaterialMacro && (i = At(b.CanEditMacros ? b.AllMacros : b.MacroEntries, model)) >= 0) return "mac:" + i;
        return null;
    }

    private object? _selectedModel;

    private void SelectModel(object? model)
    {
        if (model is null || _binding is null) return;
        if (IdOf(_binding, model) is { } id && Graph?.Find(id) is not null) SelectedNodeId = id;
    }

    // ============================================================================== node positions (window lifetime, never in the bin)

    private readonly Dictionary<string, Dictionary<string, (double X, double Y)>> _userLayout = new(StringComparer.Ordinal);

    private static string LayoutKey(GraphNode n) => n.Group + "|" + n.Source;

    /// <summary>Put every node the person has moved back where they left it (called after each rebuild). Positions are kept per
    /// material for the life of the window only; they are layout, and layout is never written into a material.</summary>
    private void ApplyUserLayout(MaterialGraph graph)
    {
        if (!_userLayout.TryGetValue(graph.MaterialName, out var moved) || moved.Count == 0) return;
        foreach (var n in graph.Nodes)
            if (moved.TryGetValue(LayoutKey(n), out var p)) n.SetPosition(p.X, p.Y);
        graph.UpdateBounds();
    }

    /// <summary>The person dragged a node. Layout only: no document, undo step or dirty flag is involved.</summary>
    public void MoveNode(string nodeId, double x, double y)
    {
        var g = Graph;
        var n = g?.Find(nodeId);
        if (g is null || n is null) return;
        n.SetPosition(x, y);
        if (!_userLayout.TryGetValue(g.MaterialName, out var moved)) _userLayout[g.MaterialName] = moved = new Dictionary<string, (double, double)>();
        moved[LayoutKey(n)] = (x, y);
    }

    /// <summary>Drop the moved positions of this material (Home with Shift, or after a layout reset).</summary>
    public void ResetNodePositions()
    {
        if (Graph is null) return;
        _userLayout.Remove(Graph.MaterialName);
        if (_binding is not null) { _shaderSig = ""; Refresh(); }
    }

    // ============================================================================== validation

    /// <summary>
    /// Can the output of <paramref name="srcId"/> feed input <paramref name="dstPin"/> of the shader node? The same check the canvas
    /// runs while a wire is dragged (red wire and a reason) and the gesture runs before it writes anything.
    /// </summary>
    public GraphLinkCheck CheckLink(string srcId, string dstId, int dstPin)
    {
        var g = Graph;
        if (g is null) return new(false, "There is no graph.");
        if (!CanEdit) return new(false, EditNote);
        if (!CanWire) return new(false, WireNote);
        var src = g.Find(srcId);
        var dst = g.Find(dstId);
        if (src is null || dst is null) return new(false, "That node is gone.");
        if (dst.Kind != GraphNodeKind.Shader || dstPin < 0 || dstPin >= dst.Inputs.Count)
            return new(false, "Only the shader node's inputs take a link.");
        var pin = dst.Inputs[dstPin];

        if (src.Kind is GraphNodeKind.Switch or GraphNodeKind.Macro || pin.Kind == GraphPinKind.Bool)
            return new(false, "Switches and defines are toggles, not links: flip them in the Details panel.");
        if (src.Kind is not (GraphNodeKind.Texture or GraphNodeKind.Scalar or GraphNodeKind.Vector or GraphNodeKind.Color))
            return new(false, "Only a texture sample or a parameter can be wired to a shader input.");
        if (pin.Kind is not (GraphPinKind.Texture or GraphPinKind.Scalar or GraphPinKind.Vector or GraphPinKind.Color))
            return new(false, $"{pin.Name} is not an input that takes a link.");
        if (pin.Target.Length == 0)
            return new(false, pin.Name.EndsWith("_SharedTexture", StringComparison.OrdinalIgnoreCase)
                ? $"{pin.Name} is supplied by the engine (a shared texture); a material does not set it."
                : $"{pin.Name} has no name a material entry could bind it by.");

        bool srcTexture = src.Kind == GraphNodeKind.Texture;
        bool pinTexture = pin.Kind == GraphPinKind.Texture;
        if (srcTexture && !pinTexture)
            return new(false, $"{pin.Name} is a {pin.Components}-component constant input; a texture sample feeds texture inputs only.");
        if (!srcTexture && pinTexture)
            return new(false, $"{pin.Name} is a {DimensionText(pin.Dimension)} input; a parameter cannot feed it.");

        // already exactly this link?
        if (src.Source.Equals(pin.Target, StringComparison.OrdinalIgnoreCase) && g.Wires.Any(w => w.FromNode == src.Id && w.ToNode == dst.Id && w.ToPin == dstPin)
            && src.State == GraphNodeState.Authored)
            return new(false, $"{src.Title} already feeds {pin.Name}.");

        if (srcTexture)
        {
            string srcDim = src.Outputs[0].Dimension.Length == 0 ? "tex2d" : src.Outputs[0].Dimension;
            if (pin.Dimension.Length > 0 && !pin.Dimension.Equals(srcDim, StringComparison.OrdinalIgnoreCase))
                return new(false, $"{pin.Name} is a {DimensionText(pin.Dimension)} input; {src.Title} is a {DimensionText(srcDim)}.");
            string shaderTexture = pin.Key.StartsWith("tx:", StringComparison.Ordinal) ? pin.Key[3..] : "";
            if (!string.Equals(TextureTargetOf(pin.Target), shaderTexture, StringComparison.OrdinalIgnoreCase))
                return new(false, $"No sampler name binds {pin.Name}: the game's name rule maps '{pin.Target}' elsewhere.");
            if (src.State != GraphNodeState.ShaderDefault && SlotOf(src) is not { IsRemovable: true })
                return new(false, $"{src.Title} is the skin's built-in texture field, not a sampler entry, so it cannot be moved or copied.");
            if (BlockingSlots(pin, SlotOf(src)) is { Count: > 0 } blocked && blocked.Any(s => !s.IsRemovable))
                return new(false, $"{pin.Name} is fed by the skin's built-in texture field, which this graph cannot replace.");
        }
        else
        {
            int have = src.Outputs[0].Components;
            if (have == 0)
                return new(false, $"{src.Title} ({ParameterOf(src)?.TypeName}) is not a numeric vector, so it cannot feed a constant.");
            if (pin.Components == 0)
                return new(false, $"{pin.Name} is not a float / float2 / float3 / float4 constant, so a parameter cannot feed it.");
            if (have != pin.Components)
                return new(false, $"{src.Title} has {have} component{(have == 1 ? "" : "s")} ({ParameterOf(src)?.TypeName}); {pin.Name} is a "
                                  + $"{pin.Components}-component input (float{(pin.Components == 1 ? "" : pin.Components.ToString(CultureInfo.InvariantCulture))}). "
                                  + "Nothing is truncated or padded: edit the value's type on the Material tab first.");
            if (ParameterOf(src) is not { IsRemovable: true })
                return new(false, $"{src.Title} is not a parameter entry that can be moved or copied.");
            if (BlockingParams(pin, ParameterOf(src)).Any(p => !p.IsRemovable))
                return new(false, $"{pin.Name} is fed by an entry that cannot be removed.");
        }
        return new(true, "");
    }

    /// <summary>Every sampler entry that feeds this texture input now (its name resolves to it): what a new link replaces.</summary>
    private List<TextureSlot> BlockingSlots(GraphPin pin, TextureSlot? except)
    {
        string shaderTexture = pin.Key.Length > 3 ? pin.Key[3..] : "";
        return _binding!.Slots.Where(s => !ReferenceEquals(s, except)
                                          && string.Equals(TextureTargetOf(s.SamplerName), shaderTexture, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private List<MaterialParameter> BlockingParams(GraphPin pin, MaterialParameter? except) =>
        _binding!.Parameters.Where(p => !ReferenceEquals(p, except) && p.Name.Equals(pin.Target, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Entries named like <paramref name="slot"/> that come after it: when the first of them leaves its input (moved, deleted) the
    /// game keeps the FIRST by name, so a repeat would take the input over. They go with it, in the same undo step.</summary>
    private List<TextureSlot> RepeatsAfter(TextureSlot slot)
    {
        var all = _binding!.Slots.ToList();
        int at = all.IndexOf(slot);
        return at < 0 ? new() : all.Skip(at + 1).Where(s => s.IsRemovable && s.SamplerName.Equals(slot.SamplerName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private List<MaterialParameter> RepeatsAfter(MaterialParameter p)
    {
        var all = _binding!.Parameters.ToList();
        int at = all.IndexOf(p);
        return at < 0 ? new() : all.Skip(at + 1).Where(q => q.IsRemovable && q.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // ============================================================================== the transaction: one gesture = one undo step

    /// <summary>What a wiring command does after it changed the model (also on undo and redo): the rows, the dirty flag, the shader panels
    /// and the preview. Bound to the editor and binding view model the gesture was made on - NOT to this view model's current ones, which
    /// are gone once the window closes or another material is shown, while the undo stack still holds the command.</summary>
    private static void SyncRows(MaterialEditorViewModel editor, MaterialBindingViewModel binding)
    {
        binding.ResyncRows();
        binding.RaiseDirty();
        editor.RefreshShaderDefs();
        editor.NotifyChanged();
    }

    private sealed class WireTx
    {
        private readonly MaterialGraphViewModel _vm;
        private readonly List<IEditorCommand> _done = new();
        private readonly MaterialEditorViewModel _editor;
        private readonly MaterialBindingViewModel _rows;
        private readonly MaterialBinding _model;
        private readonly object? _ctx;
        public WireTx(MaterialGraphViewModel vm)
        {
            _vm = vm;
            _editor = vm._editor!;
            _rows = vm._editBinding!;
            _model = vm._binding!;
            _ctx = _editor.DocContext;
        }
        private MaterialBinding B => _model;
        private object? Ctx => _ctx;
        private void Sync() => SyncRows(_editor, _rows);

        public bool RemoveSlot(TextureSlot s)
        {
            if (!B.RemoveSampler(s)) return false;
            _done.Add(new SamplerAddRemoveCommand(Ctx, B, s, isAdd: false, (_, _) => Sync()));
            return true;
        }

        public bool RenameSlot(TextureSlot s, string name)
        {
            string old = s.SamplerName;
            if (!B.RenameSampler(s, name)) return false;
            _done.Add(new SamplerRenameCommand(Ctx, B, s, old, name, Sync));
            return true;
        }

        public TextureSlot? DuplicateSlot(TextureSlot s, string name)
        {
            var n = B.DuplicateSampler(s, name);
            if (n is null) return null;
            _done.Add(new SamplerAddRemoveCommand(Ctx, B, n, isAdd: true, (_, _) => Sync()));
            return n;
        }

        public TextureSlot? AddSlot(string name, string path)
        {
            var n = B.AddSampler(name, path);
            if (n is null) return null;
            _done.Add(new SamplerAddRemoveCommand(Ctx, B, n, isAdd: true, (_, _) => Sync()));
            return n;
        }

        public bool RemoveParameter(MaterialParameter p)
        {
            if (!B.RemoveParameter(p)) return false;
            _done.Add(new ParameterAddRemoveCommand(Ctx, B, p, isAdd: false, Sync));
            return true;
        }

        public bool RenameParameter(MaterialParameter p, string name)
        {
            string old = p.Name;
            if (!B.RenameParameter(p, name)) return false;
            _done.Add(new ParameterRenameCommand(Ctx, B, p, old, name, Sync));
            return true;
        }

        public MaterialParameter? DuplicateParameter(MaterialParameter p, string name)
        {
            var n = B.DuplicateParameter(p, name);
            if (n is null) return null;
            _done.Add(new ParameterAddRemoveCommand(Ctx, B, n, isAdd: true, Sync));
            return n;
        }

        public MaterialParameter? AddParameter(string name, int components, string valueText)
        {
            var p = B.AddParameter(name, TypeNameFor(components));
            if (p is null) return null;
            try { p.Apply(valueText); }
            catch { B.RemoveParameter(p); return null; }
            _done.Add(new ParameterAddRemoveCommand(Ctx, B, p, isAdd: true, Sync));
            return p;
        }

        public void Rollback()
        {
            for (int i = _done.Count - 1; i >= 0; i--)
                try { _done[i].Undo(); } catch { /* the model call that failed left nothing to take back */ }
            _done.Clear();
            Sync();
        }

        /// <summary>Show the change everywhere and record it as ONE undo step.</summary>
        public bool Commit(string name)
        {
            if (_done.Count == 0) return false;
            var commands = _done.ToList();
            var us = _editor.UndoService;
            us?.PushApplied(commands.Count == 1 ? commands[0] : new CompositeCommand(name, commands, Ctx));
            Sync();
            return true;
        }
    }

    // ============================================================================== gestures

    /// <summary>Connect the output of <paramref name="srcId"/> to input <paramref name="dstPin"/> of the shader node.
    /// <paramref name="move"/> = the person picked up an existing link by its input end: the entry is renamed (the old input falls back
    /// to the shader default). Otherwise a node that already feeds an input is copied, and one that feeds none is renamed.</summary>
    public bool Connect(string srcId, string dstId, int dstPin, bool move)
    {
        var check = CheckLink(srcId, dstId, dstPin);
        if (!check.Ok) return Refuse(check.Reason);
        var g = Graph!;
        var src = g.Find(srcId)!;
        var pin = g.Find(dstId)!.Inputs[dstPin];
        CommitPending();

        bool rename = move || (src.State != GraphNodeState.ShaderDefault && !IsWired(src.Id));
        var tx = new WireTx(this);
        object? result;
        string verb;
        if (pin.Kind == GraphPinKind.Texture)
        {
            var slot = src.State == GraphNodeState.ShaderDefault ? null : SlotOf(src);
            var toRemove = BlockingSlots(pin, slot);
            if (rename && slot is not null)
                foreach (var r in RepeatsAfter(slot)) if (!toRemove.Any(x => ReferenceEquals(x, r))) toRemove.Add(r);
            foreach (var s in toRemove)
                if (!tx.RemoveSlot(s)) { tx.Rollback(); return Refuse($"Could not replace '{s.SamplerName}'."); }
            TextureSlot? now;
            if (src.State == GraphNodeState.ShaderDefault)
                now = tx.AddSlot(pin.Target, src.TexturePath);
            else if (rename)
                now = slot is not null && tx.RenameSlot(slot, pin.Target) ? slot : null;
            else now = tx.DuplicateSlot(slot!, pin.Target);
            if (now is null) { tx.Rollback(); return Refuse($"The sampler could not be written as '{pin.Target}'."); }
            result = now;
            verb = src.State == GraphNodeState.ShaderDefault ? "Created" : rename ? "Moved" : "Copied";
        }
        else
        {
            var param = ParameterOf(src)!;
            var toRemove = BlockingParams(pin, param);
            if (rename)
                foreach (var r in RepeatsAfter(param)) if (!toRemove.Any(x => ReferenceEquals(x, r))) toRemove.Add(r);
            foreach (var p in toRemove)
                if (!tx.RemoveParameter(p)) { tx.Rollback(); return Refuse($"Could not replace '{p.Name}'."); }
            MaterialParameter? now = rename
                ? (tx.RenameParameter(param, pin.Target) ? param : null)
                : tx.DuplicateParameter(param, pin.Target);
            if (now is null) { tx.Rollback(); return Refuse($"The parameter could not be written as '{pin.Target}'."); }
            result = now;
            verb = rename ? "Moved" : "Copied";
        }

        tx.Commit($"Connect {src.Title} to {pin.Name}");
        SelectModel(result);
        EditStatus = $"{verb} {src.Title} -> {pin.Name}" + (rename ? "" : " (the node also keeps feeding its first input)");
        if (rename && pin.Kind != GraphPinKind.Texture && _binding!.DynamicParameters.FirstOrDefault(d => d.Enabled && d.Name.Equals(src.Source, StringComparison.OrdinalIgnoreCase)) is { } driven)
            EditStatus += $". Note: {src.Source} is driven at runtime by {driven.Driver}; the driver still targets that name.";
        return true;
    }

    /// <summary>Remove what feeds shader input <paramref name="dstPin"/>: the entries whose name resolves to it. The shader default applies.</summary>
    public bool Disconnect(int dstPin)
    {
        var g = Graph;
        var dst = g?.ShaderNode;
        if (g is null || dst is null || dstPin < 0 || dstPin >= dst.Inputs.Count) return false;
        if (!CanEdit) return Refuse(EditNote);
        if (!CanWire) return Refuse(WireNote);
        var pin = dst.Inputs[dstPin];
        if (pin.Kind == GraphPinKind.Bool) return Refuse("Switches and defines are toggles: there is no link to break.");
        CommitPending();

        var tx = new WireTx(this);
        int removed = 0;
        string what;
        if (pin.Kind == GraphPinKind.Texture)
        {
            var feeding = BlockingSlots(pin, null);
            if (feeding.Count == 0) return Refuse($"{pin.Name} is not connected: the shader default already applies.");
            if (feeding.Any(s => !s.IsRemovable)) return Refuse($"{pin.Name} is fed by the skin's built-in texture field, which cannot be removed here.");
            foreach (var s in feeding) { if (!tx.RemoveSlot(s)) { tx.Rollback(); return Refuse($"Could not remove '{s.SamplerName}'."); } removed++; }
            what = "texture sample";
        }
        else
        {
            var feeding = BlockingParams(pin, null);
            if (feeding.Count == 0) return Refuse($"{pin.Name} is not connected: the shader default already applies.");
            if (feeding.Any(p => !p.IsRemovable)) return Refuse($"{pin.Name} is fed by an entry that cannot be removed.");
            foreach (var p in feeding) { if (!tx.RemoveParameter(p)) { tx.Rollback(); return Refuse($"Could not remove '{p.Name}'."); } removed++; }
            what = "parameter";
        }
        tx.Commit($"Disconnect {pin.Name}");
        EditStatus = $"Disconnected {pin.Name}: removed {removed} {what} entr{(removed == 1 ? "y" : "ies")}; the shader default applies.";
        return true;
    }

    /// <summary>Delete a node (the Delete key, Alt+click on its output): its entry is removed from the material.</summary>
    public bool DeleteNode(string nodeId)
    {
        var g = Graph;
        var node = g?.Find(nodeId);
        if (g is null || node is null) return false;
        if (!CanEdit) return Refuse(EditNote);
        switch (node.Kind)
        {
            case GraphNodeKind.Switch or GraphNodeKind.Macro:
                return Refuse("Switches and defines are toggles, not nodes you delete: flip them in the Details panel (a switch can be removed on the Material tab).");
            case GraphNodeKind.Shader or GraphNodeKind.Output:
                return Refuse("The shader and the material output are not deleted.");
        }
        if (node.State == GraphNodeState.ShaderDefault)
            return Refuse($"{node.Title} is not authored by this material - it is the shader's default - so there is nothing to delete.");
        CommitPending();

        var tx = new WireTx(this);
        if (node.Kind == GraphNodeKind.Texture)
        {
            var slot = SlotOf(node);
            if (slot is null || !slot.IsRemovable) return Refuse($"{node.Title} is the skin's built-in texture field, not a sampler entry, so it cannot be deleted.");
            foreach (var s in new[] { slot }.Concat(RepeatsAfter(slot)))
                if (!tx.RemoveSlot(s)) { tx.Rollback(); return Refuse($"Could not remove '{node.Title}'."); }
        }
        else
        {
            var p = ParameterOf(node);
            if (p is null || !p.IsRemovable) return Refuse($"{node.Title} cannot be deleted from here.");
            foreach (var q in new[] { p }.Concat(RepeatsAfter(p)))
                if (!tx.RemoveParameter(q)) { tx.Rollback(); return Refuse($"Could not remove '{node.Title}'."); }
        }
        string title = node.Title;
        tx.Commit($"Delete {title}");
        EditStatus = $"Deleted {title}.";
        return true;
    }

    /// <summary>Alt+click on a pin: break its links. An input pin loses what feeds it; an output pin's node loses its entry.</summary>
    public bool BreakLinks(string nodeId, int pinIndex, bool isInput) =>
        isInput ? Disconnect(pinIndex) : DeleteNode(nodeId);

    /// <summary>The shader inputs the material does not author, textures and constants: the right-click list.</summary>
    public IReadOnlyList<GraphUnconnectedInput> UnconnectedInputs()
    {
        var list = new List<GraphUnconnectedInput>();
        var g = Graph;
        var shader = g?.ShaderNode;
        if (g is null || shader is null) return list;
        for (int i = 0; i < shader.Inputs.Count; i++)
        {
            var pin = shader.Inputs[i];
            if (pin.Kind is GraphPinKind.Bool or GraphPinKind.Attribute or GraphPinKind.Shader || pin.Key.Length == 0 || pin.Target.Length == 0) continue;
            var wire = g.Wires.FirstOrDefault(w => w.ToNode == shader.Id && w.ToPin == i);
            var from = wire is null ? null : g.Find(wire.FromNode);
            if (from is not null && from.State != GraphNodeState.ShaderDefault) continue;
            bool tex = pin.Kind == GraphPinKind.Texture;
            list.Add(new GraphUnconnectedInput
            {
                PinIndex = i, Name = pin.Name, IsTexture = tex,
                Detail = tex ? (from is { TexturePath.Length: > 0 } ? "shader default " + from.Subtitle : pin.Detail) : pin.Detail,
            });
        }
        return list;
    }

    /// <summary>"Create texture sample / parameter here": author an entry for an unconnected input, with the shader's own default.</summary>
    public bool CreateAt(int dstPin)
    {
        var g = Graph;
        var dst = g?.ShaderNode;
        if (g is null || dst is null || dstPin < 0 || dstPin >= dst.Inputs.Count) return false;
        if (!CanEdit) return Refuse(EditNote);
        if (!CanWire) return Refuse(WireNote);
        var pin = dst.Inputs[dstPin];
        if (pin.Kind is GraphPinKind.Bool or GraphPinKind.Attribute or GraphPinKind.Shader || pin.Target.Length == 0)
            return Refuse(pin.Name.EndsWith("_SharedTexture", StringComparison.OrdinalIgnoreCase)
                ? $"{pin.Name} is supplied by the engine (a shared texture); a material does not set it."
                : $"{pin.Name} is not an input that a texture sample or parameter can be created for.");
        CommitPending();
        var tx = new WireTx(this);
        object? created;
        if (pin.Kind == GraphPinKind.Texture)
        {
            if (BlockingSlots(pin, null).Count > 0) return Refuse($"{pin.Name} already has a texture sample.");
            string shaderTexture = pin.Key[3..];
            string name = pin.Target, path = "";
            if (_info!.TextureDefaults.FirstOrDefault(kv => string.Equals(TextureTargetOf(kv.Key), shaderTexture, StringComparison.OrdinalIgnoreCase)) is { Key: { } dn, Value: { } dp })
            { name = dn; path = dp; }
            if (path.Length == 0)
                return Refuse($"{pin.Name} has no shader default texture, and a sampler is not authored with an empty path. Copy an existing texture sample onto it "
                              + "(drag its output pin here), or add the sampler on the Material tab and choose its texture.");
            if (!string.Equals(TextureTargetOf(name), shaderTexture, StringComparison.OrdinalIgnoreCase))
                return Refuse($"No sampler name binds {pin.Name}: the game's name rule maps '{name}' elsewhere.");
            created = tx.AddSlot(name, path);
            if (created is null) { tx.Rollback(); return Refuse("The material has no sampler list to add to."); }
            tx.Commit($"Create texture sample for {pin.Name}");
            EditStatus = $"Created a texture sample for {pin.Name} with the shader's default texture.";
        }
        else
        {
            if (BlockingParams(pin, null).Count > 0) return Refuse($"{pin.Name} already has a parameter.");
            int comps = pin.Components;
            if (comps == 0) return Refuse($"{pin.Name} is not a float / float2 / float3 / float4 constant.");
            string text = _info!.ParameterDefaults.TryGetValue(pin.Target, out var d) && d.Length == comps
                ? string.Join(", ", d.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) : ZeroText(comps);
            created = tx.AddParameter(pin.Target, comps, text);
            if (created is null) { tx.Rollback(); return Refuse("The parameter could not be added (the material has no parameter to copy the entry shape from)."); }
            tx.Commit($"Create parameter for {pin.Name}");
            EditStatus = $"Created {pin.Target} = {text}.";
        }
        SelectModel(created);
        return true;
    }

    /// <summary>Right-click, "Add Texture Sample". A sampler is never authored with an empty path, and this graph has no texture picker, so a
    /// new sample starts as a COPY of the selected texture sample (a second entry with the same texture, wired or renamed afterwards); with
    /// no texture sample selected there is nothing honest to start from and it says so.</summary>
    public bool AddTextureSample()
    {
        if (Graph is null) return false;
        if (!CanEdit) return Refuse(EditNote);
        var sel = SelectedNodeId is null ? null : Graph.Find(SelectedNodeId);
        var from = sel is { Kind: GraphNodeKind.Texture } && sel.State != GraphNodeState.ShaderDefault ? SlotOf(sel) : null;
        if (from is null || !from.IsRemovable || from.Path.Length == 0)
            return Refuse("Select a texture sample first: a new sample starts as a copy of it (a sampler is never written with an empty path). "
                          + "To use another texture, Replace Texture in the Details panel afterwards.");
        CommitPending();
        string name = UniqueName(from.SamplerName + "_Copy", _binding!.Slots.Select(s => s.SamplerName));
        var tx = new WireTx(this);
        var slot = tx.DuplicateSlot(from, name);
        if (slot is null) { tx.Rollback(); return Refuse("The sampler could not be copied."); }
        tx.Commit($"Add texture sample {name}");
        SelectModel(slot);
        EditStatus = $"Added {name}, a copy of {from.SamplerName}. It feeds nothing until you drag its output to a texture input.";
        return true;
    }

    /// <summary>Right-click, "Add Parameter": a new zero-valued parameter entry (a Vector4 unless the material authors none) that feeds
    /// nothing until it is wired to a constant input.</summary>
    public bool AddParameterNode()
    {
        if (Graph is null) return false;
        if (!CanEdit) return Refuse(EditNote);
        CommitPending();
        string name = UniqueName("New_Parameter", _binding!.Parameters.Select(p => p.Name));
        var tx = new WireTx(this);
        var p = tx.AddParameter(name, 4, ZeroText(4));
        if (p is null) { tx.Rollback(); return Refuse("The parameter could not be added (the material has no parameter to copy the entry shape from)."); }
        tx.Commit($"Add parameter {name}");
        SelectModel(p);
        EditStatus = $"Added {name} (0, 0, 0, 0). It feeds nothing until you drag its output to a constant input.";
        return true;
    }

    private static string UniqueName(string stem, IEnumerable<string> existing)
    {
        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!have.Contains(stem)) return stem;
        for (int i = 2; ; i++) if (!have.Contains($"{stem}_{i}")) return $"{stem}_{i}";
    }
}
