using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Undo;
using ReyEngine.Formats.Materials.Graph;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.ViewModels;

/// <summary>One editable setting of the selected Shader Graph node (a texture name, a constant, a mask, an unconnected input's value).</summary>
public sealed partial class SgPropRow : ObservableObject
{
    private readonly ShaderGraphViewModel _owner;
    private readonly string _nodeId;
    private readonly string _key;
    private string _committed;

    public SgPropRow(ShaderGraphViewModel owner, string nodeId, string key, string label, string text, string hint, IReadOnlyList<string>? choices = null)
    {
        _owner = owner; _nodeId = nodeId; _key = key; Label = label; _text = _committed = text; Hint = hint;
        Choices = choices ?? Array.Empty<string>();
    }

    public string Label { get; }
    public string Hint { get; }
    public IReadOnlyList<string> Choices { get; }
    public bool HasChoices => Choices.Count > 0;
    [ObservableProperty] private string _text;

    /// <summary>Enter, or the box losing focus. An unchanged value writes nothing.</summary>
    [RelayCommand]
    public void Commit()
    {
        if (Text == _committed) return;
        if (_owner.SetProp(_nodeId, _key, Text)) _committed = Text;
        else Text = _committed;
    }

    partial void OnTextChanged(string value)
    {
        // a choice picked from the list commits at once
        if (HasChoices && value != _committed && Choices.Contains(value)) Commit();
    }
}

/// <summary>One line of the diagnostics list: a node's error or warning (selecting it selects the node).</summary>
public sealed record SgDiagnosticRow(string NodeId, bool IsError, string Text);

/// <summary>One entry of the "Open Shader Graph" list.</summary>
public sealed record SgFileRow(string Path, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// M832: the Shader Graph editor behind the Material Graph window - a custom shader as a node graph on a base Riot shader.
///
/// <para><b>Document.</b> A <see cref="ShaderGraphDocument"/> (JSON) saved in the open project at
/// <c>.reyengine/shadergraphs/&lt;name&gt;.shadergraph.json</c>: editor data, skipped by Build Package and the exporters, referenced by no
/// bin. Preview only - the game does not see custom shaders yet.</para>
///
/// <para><b>Editing.</b> Every gesture (add, delete, connect, disconnect, edit a setting) is ONE undo step on this view model's OWN undo
/// stack (a private <see cref="UndoRedoService"/>: a Shader Graph edit never lands on the Material Editor's or the main window's stack),
/// recorded as a before/after snapshot of the document. The type pass runs on every edit (cheap, on the UI thread) and paints types and
/// errors on the canvas at once; the HLSL compile follows, debounced and OFF the UI thread (<see cref="ShaderGraphCompiler"/>: one pixel
/// shader per input signature of the base, each verified against Riot's), and raises <see cref="BuildCompleted"/> for the preview.</para>
/// </summary>
public sealed partial class ShaderGraphViewModel : ObservableObject, IDisposable
{
    public const string Banner = "Preview only: the game does not see custom shaders yet. A Shader Graph is saved in the project and shown here; it is not packed into the mod.";

    private readonly Func<string?> _projectRoot;
    private readonly UndoRedoService _undo = new();
    private readonly object _context = new();
    private ShaderGraphDocument? _doc;
    private SgBase? _base;
    private SgAnalysis? _analysis;
    private SgBuild? _build;
    private bool _moved;
    private int _editVersion;           // bumped by every change of the document, so a save can tell whether it was edited during the write
    private bool _buildStale = true;    // the last build is of an older document: its diagnostics are not shown against the current graph
    private int _compileVersion;
    private CancellationTokenSource? _debounce;
    private bool _restoring;
    private bool _disposed;

    public ShaderGraphViewModel(Func<string?> projectRoot)
    {
        _projectRoot = projectRoot;
        PropRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NoPropRows));
        DiagnosticRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NoDiagnosticRows));
        SavedGraphs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NoSavedGraphs));
    }

    // empty-list flags for the window (an Avalonia "!" does not negate a Count)
    public bool NoPropRows => PropRows.Count == 0;
    public bool NoDiagnosticRows => DiagnosticRows.Count == 0;
    public bool NoSavedGraphs => SavedGraphs.Count == 0;

    /// <summary>Resolves a base shader (renderShader spelling) to its <see cref="SgBase"/>; set by the window that owns the shader cache.</summary>
    public Func<string, (SgBase? Base, string Error)>? BaseResolver { get; set; }

    /// <summary>The debounce before a compile starts (tests set it to zero).</summary>
    public TimeSpan CompileDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Raised on the UI thread when a compile finished and its diagnostics were applied. The preview swaps its pixel shader here.</summary>
    public event Action<SgBuild>? BuildCompleted;

    /// <summary>Raised when the open graph, its dirty flag or its status changed in a way the window chrome shows.</summary>
    public event Action? StateChanged;

    // ============================================================================== state

    public bool IsOpen => _doc is not null;
    public ShaderGraphDocument? Document => _doc;
    public SgBase? Base => _base;
    public SgAnalysis? Analysis => _analysis;
    public SgBuild? LastBuild => _build;
    public string? FilePath { get; private set; }

    [ObservableProperty] private MaterialGraph? _graph;
    [ObservableProperty] private string? _selectedNodeId;
    [ObservableProperty] private string _graphName = "";
    [ObservableProperty] private string _baseShaderName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _hlsl = "";
    [ObservableProperty] private string _hlslNote = "No Shader Graph is open.";
    [ObservableProperty] private string _compileState = "";
    [ObservableProperty] private bool _compiling;
    [ObservableProperty] private bool _previewEnabled = true;

    /// <summary>The signature whose HLSL the Shader Code tab shows (the one the previewed Riot pixel shader has; 0 until a preview names it).</summary>
    [ObservableProperty] private int _signatureIndex;

    public ObservableCollection<SgPropRow> PropRows { get; } = new();
    public ObservableCollection<SgDiagnosticRow> DiagnosticRows { get; } = new();
    public ObservableCollection<SgFileRow> SavedGraphs { get; } = new();

    public string BannerText => Banner;
    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    partial void OnIsDirtyChanged(bool value) => StateChanged?.Invoke();
    partial void OnStatusChanged(string value) => StateChanged?.Invoke();
    partial void OnSelectedNodeIdChanged(string? value) => RefreshProps();
    partial void OnSignatureIndexChanged(int value) => ShowHlsl();
    partial void OnPreviewEnabledChanged(bool value) { if (_build is not null) BuildCompleted?.Invoke(_build); }

    public IReadOnlyList<SgPaletteEntry> Palette => SgCatalog.Palette(_base);

    public void Dispose()
    {
        _disposed = true;
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = null;
    }

    // ============================================================================== open / new / save

    /// <summary>Close the open graph (the window goes back to the material graph). Unsaved edits are the caller's to ask about.</summary>
    public void Close()
    {
        _debounce?.Cancel();
        Interlocked.Increment(ref _compileVersion);   // a compile still running is dropped
        Compiling = false;
        _doc = null; _base = null; _analysis = null; _build = null; FilePath = null;
        _undo.Clear(); _moved = false; IsDirty = false; _editVersion++;
        Status = "";
        Graph = null; SelectedNodeId = null; GraphName = ""; BaseShaderName = ""; Hlsl = ""; HlslNote = "No Shader Graph is open.";
        PropRows.Clear(); DiagnosticRows.Clear(); CompileState = "";
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo));
        StateChanged?.Invoke();
    }

    /// <summary>Make a new graph on <paramref name="baseShader"/> (the material's renderShader) with the starter wiring
    /// texture RGB -> Base Color, texture A -> Opacity. The name is made unique among the project's saved graphs.</summary>
    public bool NewFromBase(string baseShader, out string error, string? suggestedName = null)
    {
        error = "";
        if (BaseResolver is null) { error = "No shader cache is available to read the base shader from."; Status = error; return false; }
        var (b, why) = BaseResolver(baseShader);
        if (b is null) { error = why.Length > 0 ? why : $"'{baseShader}' is not a supported base shader."; Status = error; return false; }

        string leaf = baseShader.Trim('/').Split('/').Last();
        string name = UniqueName(suggestedName is { Length: > 0 } ? suggestedName : leaf + "_Graph");
        var doc = ShaderGraphDocument.CreateEmpty(name, b.Shader);
        var tex = b.Textures.FirstOrDefault();
        if (tex is not null)
        {
            doc.Nodes.Add(new SgNode { Id = "n2", Type = "TextureSample", X = 300, Y = 120, Props = { ["texture"] = tex.Name } });
            doc.Links.Add(new SgLink { FromNode = "n2", FromPin = "RGB", ToNode = "n1", ToPin = SgCatalog.BaseColorPin });
            doc.Links.Add(new SgLink { FromNode = "n2", FromPin = "A", ToNode = "n1", ToPin = SgCatalog.OpacityPin });
        }
        try { Load(doc, b, null); }
        catch (Exception ex) { Close(); error = "The new Shader Graph could not be set up: " + ex.Message; Status = error; return false; }
        IsDirty = true;   // not on disk yet
        Status = $"New Shader Graph '{name}' on {leaf}. Save (Ctrl+S) writes it to the project.";
        return true;
    }

    /// <summary>Open a saved graph. False with <paramref name="error"/> when the file is unreadable or its base shader cannot be read.</summary>
    public bool Open(string path, out string error)
    {
        error = "";
        ShaderGraphDocument? doc;
        try
        {
            if (new FileInfo(path).Length > ShaderGraphDocument.MaxFileBytes) { error = "the file is too large to be a Shader Graph"; doc = null; }
            else { doc = ShaderGraphJson.TryDeserialize(File.ReadAllText(path), out string? parseError); error = parseError ?? ""; }
        }
        catch (Exception ex) { error = ex.Message; doc = null; }
        if (doc is null) { Status = $"{Path.GetFileName(path)}: {error}"; error = Status; return false; }
        if (BaseResolver is null) { error = "No shader cache is available to read the base shader from."; Status = error; return false; }
        var (b, why) = BaseResolver(doc.BaseShader);
        if (b is null) { error = $"{Path.GetFileName(path)}: base shader '{doc.BaseShader}': {why}"; Status = error; return false; }
        // look at the file the way Load will BEFORE touching what is open: a file the analyser cannot take leaves the open graph alone
        try { SgGraphView.Build(doc, b, SgAnalyzer.Analyze(doc, b)); }
        catch (Exception ex) { error = $"{Path.GetFileName(path)} could not be read as a Shader Graph: {ex.Message}"; Status = error; return false; }
        try { Load(doc, b, path); }
        catch (Exception ex)
        {
            // nothing half-open: the graph that was open before is gone only if it could not be put back
            Close();
            error = $"{Path.GetFileName(path)} could not be opened: {ex.Message}";
            Status = error;
            return false;
        }
        Status = $"Opened {Path.GetFileName(path)}.";
        return true;
    }

    private void Load(ShaderGraphDocument doc, SgBase b, string? path)
    {
        _debounce?.Cancel();
        _doc = doc; _base = b; FilePath = path; _build = null;
        _undo.Clear(); _moved = false; IsDirty = false;
        GraphName = doc.Name; BaseShaderName = doc.BaseShader;
        SelectedNodeId = null;
        Hlsl = ""; HlslNote = "Compiling...";
        OnPropertyChanged(nameof(IsOpen)); OnPropertyChanged(nameof(Palette));
        OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo));
        Reanalyse();
        RequestCompile(immediate: true);
        StateChanged?.Invoke();
    }

    private string UniqueName(string wanted)
    {
        string stem = ShaderGraphDocument.SafeFileStem(wanted);
        string? root = _projectRoot();
        if (root is null) return stem;
        string name = stem;
        for (int i = 2; File.Exists(ShaderGraphDocument.PathIn(root, name)); i++) name = stem + "_" + i;
        return name;
    }

    /// <summary>The graphs saved in the open project (refreshed when the Open list is shown).</summary>
    public void RefreshSavedGraphs()
    {
        SavedGraphs.Clear();
        string? root = _projectRoot();
        if (root is null) return;
        string dir = Path.Combine(root, ".reyengine", ShaderGraphDocument.FolderName);
        if (!Directory.Exists(dir)) return;
        foreach (string f in Directory.EnumerateFiles(dir, "*" + ShaderGraphDocument.FileSuffix).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            SavedGraphs.Add(new SgFileRow(f, Path.GetFileName(f)[..^ShaderGraphDocument.FileSuffix.Length]));
    }

    /// <summary>Write the graph to the project (Ctrl+S). Atomic (a temporary file, then a move); nothing outside the project's
    /// <c>.reyengine/shadergraphs</c> folder is written.</summary>
    public async Task<bool> SaveAsync()
    {
        if (_doc is null) { Status = "No Shader Graph is open."; return false; }
        string? root = _projectRoot();
        if (root is null) { Status = "Open or create a project first: a Shader Graph is saved in the project folder."; return false; }
        string? tmp = null;
        try
        {
            string path = FilePath ?? ShaderGraphDocument.PathIn(root, _doc.Name);
            if (FilePath is null && File.Exists(path)) { Status = $"{Path.GetFileName(path)} already exists; the graph was not saved over it."; return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            int version = _editVersion;
            string text = ShaderGraphJson.Serialize(_doc);
            tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, text);
            File.Move(tmp, path, overwrite: true);
            tmp = null;
            FilePath = path;
            if (version == _editVersion)
            {
                _undo.MarkSaved();
                _moved = false;
                IsDirty = false;
                Status = $"Saved {Path.GetFileName(path)} to the project (editor data only: it is not packed into the mod).";
            }
            else Status = $"Saved {Path.GetFileName(path)}; the graph was edited while it was being written, so it is still marked as changed.";
            RefreshSavedGraphs();
            return true;
        }
        catch (Exception ex)
        {
            Status = "Save failed: " + ex.Message;
            return false;
        }
        finally { if (tmp is not null) { try { File.Delete(tmp); } catch { } } }
    }

    // ============================================================================== undo

    private sealed class SnapshotCommand : IEditorCommand
    {
        private readonly ShaderGraphViewModel _owner;
        private readonly ShaderGraphDocument _before, _after;
        public SnapshotCommand(ShaderGraphViewModel owner, string name, ShaderGraphDocument before, ShaderGraphDocument after, object context)
        { _owner = owner; Name = name; _before = before; _after = after; Context = context; }
        public string Name { get; }
        public object? Context { get; }
        public void Execute() => _owner.Restore(_after);
        public void Undo() => _owner.Restore(_before);
        public bool CanMergeWith(IEditorCommand next) => false;
        public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
    }

    /// <summary>Apply an edit as one undo step: <paramref name="edit"/> changes a COPY of the document; the step swaps the before and after snapshots.</summary>
    private bool Do(string name, Action<ShaderGraphDocument> edit)
    {
        if (_doc is null) return false;
        var before = _doc.Clone();
        var after = _doc.Clone();
        edit(after);
        if (Same(before, after)) return false;
        var cmd = new SnapshotCommand(this, name, before, after, _context);
        _undo.Do(cmd);
        AfterEdit(name);
        return true;
    }

    private static bool Same(ShaderGraphDocument a, ShaderGraphDocument b) =>
        ShaderGraphJson.Serialize(a) == ShaderGraphJson.Serialize(b);

    private void Restore(ShaderGraphDocument snapshot)
    {
        if (_doc is null) return;
        _restoring = true;
        try
        {
            var copy = snapshot.Clone();
            // topology and settings come from the snapshot; where the person put a node is layout and stays where it is now
            foreach (var n in copy.Nodes)
                if (_doc.Find(n.Id) is { } now) { n.X = now.X; n.Y = now.Y; }
            _doc.Nodes = copy.Nodes;
            _doc.Links = copy.Links;
        }
        finally { _restoring = false; }
    }

    private bool ComputeDirty() => _undo.IsDirty || _moved || FilePath is null;   // a graph never saved stays dirty whatever is undone

    private void AfterEdit(string what)
    {
        _editVersion++;
        IsDirty = true;
        OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo));
        if (SelectedNodeId is not null && _doc!.Find(SelectedNodeId) is null) SelectedNodeId = null;
        Reanalyse();
        RefreshProps();
        RequestCompile();
        Status = what;
    }

    public bool Undo()
    {
        if (!_undo.CanUndo) { Status = "Nothing to undo."; return false; }
        string name = _undo.UndoName ?? "";
        bool ok = _undo.Undo();
        if (ok) { AfterEdit("Undid: " + name); IsDirty = ComputeDirty(); }
        return ok;
    }

    public bool Redo()
    {
        if (!_undo.CanRedo) { Status = "Nothing to redo."; return false; }
        string name = _undo.RedoName ?? "";
        bool ok = _undo.Redo();
        if (ok) { AfterEdit("Redid: " + name); IsDirty = ComputeDirty(); }
        return ok;
    }

    /// <summary>Apply whatever is typed in a property box but not committed yet (a key binding can fire before the box loses focus).</summary>
    public void Commit() { foreach (var r in PropRows.ToList()) r.Commit(); }

    // ============================================================================== editing gestures

    /// <summary>Add a node from the palette at a graph position.</summary>
    public string? AddNode(SgPaletteEntry entry, double x, double y)
    {
        string? id = null;
        bool ok = Do("Add " + entry.Title, d =>
        {
            id = d.NewId();
            var n = new SgNode { Id = id, Type = entry.Type, X = Math.Round(x), Y = Math.Round(y) };
            foreach (var (k, v) in entry.Props) n.Props[k] = v;
            d.Nodes.Add(n);
        });
        if (ok) SelectedNodeId = id;
        return ok ? id : null;
    }

    public bool DeleteNode(string id)
    {
        var n = _doc?.Find(id);
        if (n is null) return false;
        if (n.Type == SgCatalog.OutputType) { Status = "The Shader Output node cannot be deleted."; return false; }
        string title = SgCatalog.Get(n.Type)?.Title ?? n.Type;
        return Do("Delete " + title, d =>
        {
            d.Nodes.RemoveAll(x => x.Id == id);
            d.Links.RemoveAll(l => l.FromNode == id || l.ToNode == id);
        });
    }

    /// <summary>What a wire from an output pin to an input pin would be: allowed, or why not. A width mismatch is allowed (it is shown as an
    /// error on the node, like Unreal); what is refused is a loop, a wire to the same node and a pin that does not exist.</summary>
    public GraphLinkCheck CheckLink(string fromNode, string fromPin, string toNode, string toPin)
    {
        if (_doc is null) return new(false, "No Shader Graph is open.");
        var a = _doc.Find(fromNode); var b = _doc.Find(toNode);
        if (a is null || b is null) return new(false, "That node no longer exists.");
        if (fromNode == toNode) return new(false, "A node cannot feed itself.");
        var adef = SgCatalog.Get(a.Type); var bdef = SgCatalog.Get(b.Type);
        if (adef is null || bdef is null) return new(false, "An unknown node type cannot be wired.");
        if (adef.OutputIndex(fromPin) < 0) return new(false, $"{adef.Title} has no output '{fromPin}'.");
        if (bdef.InputIndex(toPin) < 0) return new(false, $"{bdef.Title} has no input '{toPin}'.");
        if (DependsOn(fromNode, toNode)) return new(false, "That wire would make a loop.");
        return new(true, "");
    }

    /// <summary>True when <paramref name="node"/> (transitively) takes a value from <paramref name="upstream"/>.</summary>
    private bool DependsOn(string node, string upstream)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            string cur = stack.Pop();
            if (cur == upstream) return true;
            if (!seen.Add(cur)) continue;
            foreach (var l in _doc!.Links) if (l.ToNode == cur) stack.Push(l.FromNode);
        }
        return false;
    }

    public bool Connect(string fromNode, string fromPin, string toNode, string toPin)
    {
        var check = CheckLink(fromNode, fromPin, toNode, toPin);
        if (!check.Ok) { Status = check.Reason; return false; }
        bool changed = Do("Connect", d =>
        {
            d.Links.RemoveAll(l => l.ToNode == toNode && l.ToPin == toPin);   // an input takes one wire
            d.Links.Add(new SgLink { FromNode = fromNode, FromPin = fromPin, ToNode = toNode, ToPin = toPin });
        });
        return changed;
    }

    /// <summary>A wire picked up by its input end and dropped on another input: ONE step (the old input is freed, the new one takes it).</summary>
    public bool MoveWire(string fromNode, string fromPin, string oldTo, string oldPin, string newTo, string newPin)
    {
        var check = CheckLink(fromNode, fromPin, newTo, newPin);
        if (!check.Ok) { Status = check.Reason; return false; }
        return Do("Move wire", d =>
        {
            d.Links.RemoveAll(l => l.ToNode == oldTo && l.ToPin == oldPin);
            d.Links.RemoveAll(l => l.ToNode == newTo && l.ToPin == newPin);
            d.Links.Add(new SgLink { FromNode = fromNode, FromPin = fromPin, ToNode = newTo, ToPin = newPin });
        });
    }

    /// <summary>Every wire of a node (the node menu's "Break all wires").</summary>
    public bool BreakNodeLinks(string nodeId) =>
        Do("Break links", d => d.Links.RemoveAll(l => l.FromNode == nodeId || l.ToNode == nodeId));

    /// <summary>A line for the status label (the canvas says why a gesture did nothing).</summary>
    public void Notify(string text) => Status = text;

    public bool Disconnect(string toNode, string toPin) =>
        Do("Disconnect", d => d.Links.RemoveAll(l => l.ToNode == toNode && l.ToPin == toPin));

    /// <summary>Alt+click on a pin: every wire at it.</summary>
    public bool BreakLinks(string nodeId, string pinName, bool isInput) =>
        Do("Break links", d => d.Links.RemoveAll(l => isInput ? l.ToNode == nodeId && l.ToPin == pinName : l.FromNode == nodeId && l.FromPin == pinName));

    public bool SetProp(string nodeId, string key, string value)
    {
        var n = _doc?.Find(nodeId);
        if (n is null) return false;
        if (value.Length > 256) { Status = "That value is too long."; return false; }
        // a pin's own value (A, B, Alpha...) is a number
        if (SgCatalog.Get(n.Type) is { } d && d.InputIndex(key) >= 0
            && !(float.TryParse(value.Trim(), System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out float num) && float.IsFinite(num)))
        { Status = $"'{value}' is not a number."; return false; }
        return Do("Edit " + (SgCatalog.Get(n.Type)?.Title ?? n.Type), d => d.Find(nodeId)!.Props[key] = value.Trim());
    }

    /// <summary>The canvas dragged a node. Layout only: no undo step (like the material graph's), the document is marked changed.</summary>
    public void MoveNode(string id, double x, double y)
    {
        var n = _doc?.Find(id);
        if (n is null) return;
        n.X = Math.Round(x); n.Y = Math.Round(y);
        if (Graph?.Find(id) is { } gn) gn.SetPosition(n.X, n.Y);
        _moved = true;
        _editVersion++;
        IsDirty = true;
    }

    // ============================================================================== analysis, projection, properties

    private void Reanalyse()
    {
        if (_doc is null) return;
        _analysis = SgAnalyzer.Analyze(_doc, _base);
        _buildStale = true;
        Project();
    }

    private void Project()
    {
        if (_doc is null || _analysis is null) return;
        var known = _buildStale ? null : _build?.Diagnostics;
        Graph = SgGraphView.Build(_doc, _base, _analysis, known);
        FillDiagnostics(known ?? _analysis.Diagnostics);
    }

    private void FillDiagnostics(IReadOnlyList<SgDiagnostic> all)
    {
        DiagnosticRows.Clear();
        foreach (var d in all.OrderByDescending(d => d.IsError))
        {
            string where = d.NodeId.Length == 0 ? "" : (_doc?.Find(d.NodeId) is { } n ? (SgCatalog.Get(n.Type)?.Title ?? n.Type) + " " + d.NodeId : d.NodeId) + ": ";
            DiagnosticRows.Add(new SgDiagnosticRow(d.NodeId, d.IsError, (d.IsError ? "Error  " : "Warning  ") + where + d.Message));
        }
    }

    public void RefreshProps()
    {
        PropRows.Clear();
        if (_doc is null || SelectedNodeId is null || _doc.Find(SelectedNodeId) is not { } n) return;
        var def = SgCatalog.Get(n.Type);
        if (def is null) return;
        string id = n.Id;
        switch (n.Type)
        {
            case "TextureSample":
                PropRows.Add(new SgPropRow(this, id, "texture", "Texture", n.Prop("texture"), "A texture the base shader declares in shaders.bin.", _base?.Textures.Select(t => t.Name).ToList()));
                break;
            case "Parameter":
                PropRows.Add(new SgPropRow(this, id, "name", "Parameter", n.Prop("name"), "A parameter the base shader declares in shaders.bin.", _base?.Parameters.Select(p => p.Name).ToList()));
                break;
            case "Constant":
                PropRows.Add(new SgPropRow(this, id, "value", "Value", n.Prop("value", "0"), "1 to 4 numbers separated by spaces (float, float2, float3, float4)."));
                break;
            case "TexCoord":
            {
                var sets = SgCatalog.Palette(_base).Where(p => p.Type == "TexCoord").Select(p => p.Props["set"]).ToList();
                PropRows.Add(new SgPropRow(this, id, "set", "TEXCOORD set", n.Prop("set", (_base?.UvSet ?? 1).ToString(CultureInfo.InvariantCulture)), "The xy of TEXCOORD<set> (set 1 is UV0).", sets));
                break;
            }
            case "ComponentMask":
                PropRows.Add(new SgPropRow(this, id, "mask", "Mask", n.Prop("mask", "rgb"), "1 to 4 of r g b a (or x y z w)."));
                break;
        }
        foreach (var pin in def.Inputs)
            if (_doc.LinkInto(id, pin.Name) is null && !(n.Type == "TextureSample" && pin.Name == "UV"))
                PropRows.Add(new SgPropRow(this, id, pin.Name, pin.Name + " (not wired)", SgHlsl.Float(SgCatalog.PinDefault(n, pin)), "The value used while nothing is wired here."));
    }

    // ============================================================================== compile

    /// <summary>Ask for a compile: debounced (the last request within <see cref="CompileDelay"/> wins), run off the UI thread.</summary>
    public void RequestCompile(bool immediate = false)
    {
        if (_doc is null || _base is null || _disposed) return;
        _debounce?.Cancel();
        _debounce?.Dispose();
        var cts = _debounce = new CancellationTokenSource();
        var token = cts.Token;
        int version = Interlocked.Increment(ref _compileVersion);
        var doc = _doc.Clone();
        var b = _base;
        Compiling = true;
        CompileState = "Compiling...";
        _ = Task.Run(async () =>
        {
            try
            {
                if (!immediate && CompileDelay > TimeSpan.Zero) await Task.Delay(CompileDelay, token);
                if (token.IsCancellationRequested) return;
                var build = ShaderGraphCompiler.BuildAll(doc, b);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyBuild(version, build));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (version == _compileVersion) { Compiling = false; CompileState = "Compile failed: " + ex.Message; } });
            }
        });
    }

    /// <summary>Compile the current document now and apply the result before returning (the tests and the headless probe use this).</summary>
    public async Task<SgBuild?> CompileNowAsync()
    {
        if (_doc is null || _base is null) return null;
        _debounce?.Cancel();
        int version = Interlocked.Increment(ref _compileVersion);
        var doc = _doc.Clone();
        var b = _base;
        var build = await Task.Run(() => ShaderGraphCompiler.BuildAll(doc, b));
        ApplyBuild(version, build);
        return _build;
    }

    private void ApplyBuild(int version, SgBuild build)
    {
        if (_disposed || version != _compileVersion || _doc is null) return;   // an edit made it stale
        _build = build;
        _buildStale = false;
        Compiling = false;
        int ok = build.Compiled.Count(c => c.Ok);
        CompileState = build.Analysis.HasErrors
            ? $"{build.Analysis.Errors.Count()} error(s) in the graph - fix them to compile."
            : build.Ok ? $"Compiled: {ok} of {build.Compiled.Count} pixel input signatures OK."
            : $"{build.Compiled.Count - ok} of {build.Compiled.Count} pixel input signatures failed to compile or verify.";
        Project();   // errors from the compiler land on their nodes
        ShowHlsl();
        BuildCompleted?.Invoke(build);
        StateChanged?.Invoke();
    }

    private void ShowHlsl()
    {
        var b = _base;
        if (_build is null || b is null || _doc is null) { if (_doc is null) { Hlsl = ""; HlslNote = "No Shader Graph is open."; } return; }
        if (_build.Analysis.HasErrors)
        {
            Hlsl = "";
            HlslNote = "The graph has errors; no HLSL is generated until they are fixed (see the diagnostics).";
            return;
        }
        var c = _build.Compiled.FirstOrDefault(x => x.Signature.Index == SignatureIndex) ?? _build.Compiled.FirstOrDefault();
        if (c is null) { Hlsl = ""; HlslNote = "No pixel input signature was compiled."; return; }
        Hlsl = c.Hlsl.Source.Length > 0 ? c.Hlsl.Source : "// " + string.Join("\n// ", c.Errors.Select(e => e.Message));
        HlslNote = $"Generated HLSL for pixel input signature #{c.Signature.Index} of {b.Signatures.Count} (the one the previewed Riot shader uses; "
                   + $"{c.Signature.Permutations.Count} cooked permutation(s) share it). "
                   + (c.Ok ? "It compiled (ps_5_0) and its input and output signatures equal Riot's."
                       : c.Errors.Count > 0 ? "It did NOT compile: " + c.Errors[0].Message : "");
    }

    /// <summary>The compiler's messages for the Log tab: the verdict per signature, every error and the compiler's own warnings.</summary>
    public string BuildLog()
    {
        if (_build is null) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[Shader Graph] {GraphName} on {BaseShaderName}: {CompileState}");
        foreach (var d in _build.Analysis.Diagnostics) sb.AppendLine("  " + d);
        foreach (var c in _build.Compiled)
        {
            sb.AppendLine($"  signature #{c.Signature.Index}: {(c.Ok ? "ok" : "FAILED")}, {c.Signature.Permutations.Count} permutation(s), {(c.Bytecode?.Length ?? 0)} bytes");
            foreach (var e in c.Errors) sb.AppendLine("    " + e);
            if (c.CompilerLog.Trim().Length > 0) foreach (string line in c.CompilerLog.Trim().Split('\n').Take(12)) sb.AppendLine("    | " + line.TrimEnd());
        }
        return sb.ToString();
    }
}
