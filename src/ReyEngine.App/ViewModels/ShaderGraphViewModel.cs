using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
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

    private readonly Func<string, bool>? _commit;
    private bool _syncing;

    public SgPropRow(ShaderGraphViewModel owner, string nodeId, string key, string label, string text, string hint, IReadOnlyList<string>? choices = null,
        Func<string, bool>? commit = null, bool multiline = false, int colorComponents = 0, string? actionLabel = null, Action? action = null, bool overridden = false)
    {
        IsOverridden = overridden;
        ActionLabel = actionLabel ?? ""; _action = action;
        _owner = owner; _nodeId = nodeId; _key = key; Label = label; _text = _committed = text; Hint = hint;
        Choices = choices ?? Array.Empty<string>();
        _commit = commit; IsMultiline = multiline; ColorComponents = colorComponents;
        if (colorComponents > 0) { _syncing = true; _colorValue = SgColors.FromText(text); _syncing = false; }
    }

    private readonly Action? _action;

    /// <summary>M836: a wired pin's stored constant: shown greyed, the wire decides.</summary>
    public bool IsOverridden { get; }
    public double RowOpacity => IsOverridden ? 0.5 : 1.0;

    /// <summary>M836: a button beside the row (the Texture row's "New texture"). Empty label = no button.</summary>
    public string ActionLabel { get; }
    public bool HasAction => _action is not null && ActionLabel.Length > 0;
    [RelayCommand] private void RunAction() => _action?.Invoke();

    /// <summary>M834: a multi-line box (the Custom node's HLSL). Enter is a new line; leaving the box, or Ctrl+Enter, applies it.</summary>
    public bool HasAlpha => ColorComponents == 4;
    public bool IsMultiline { get; }
    public bool IsSingleLine => !IsMultiline;
    public bool ShowsTextBox => !HasChoices && !IsMultiline;
    public bool ShowsMultiline => IsMultiline;

    /// <summary>M834: 3 or 4 when this row edits a colour (a picker next to the numbers), else 0.</summary>
    public int ColorComponents { get; }
    public bool IsColor => ColorComponents > 0;
    [ObservableProperty] private Avalonia.Media.Color _colorValue;

    partial void OnColorValueChanged(Avalonia.Media.Color value)
    {
        if (_syncing || !IsColor) return;
        Text = SgColors.ToText(value, ColorComponents);
        Commit();
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
        bool ok = _commit is not null ? _commit(Text) : _owner.SetProp(_nodeId, _key, Text);
        if (ok) _committed = Text;
        else
        {
            Text = _committed;
            if (IsColor) { _syncing = true; ColorValue = SgColors.FromText(_committed); _syncing = false; }
        }
    }

    partial void OnTextChanged(string value)
    {
        // a choice picked from the list commits at once
        if (HasChoices && value != _committed && Choices.Contains(value)) Commit();
    }
}

/// <summary>M834: the colour text of a Shader Graph value (floats 0..1, space separated) and the picker's colour, both ways.</summary>
public static class SgColors
{
    public static Avalonia.Media.Color FromText(string text)
    {
        SgCatalog.TryParseConstant(text, out var v);
        byte B(int i) => (byte)Math.Clamp((int)Math.Round((i < v.Length ? v[i] : (i == 3 ? 1f : 0f)) * 255f), 0, 255);
        return Avalonia.Media.Color.FromArgb(B(3), B(0), B(1), B(2));
    }

    public static string ToText(Avalonia.Media.Color c, int components)
    {
        var all = new[] { c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f };
        return SgCatalog.FormatConstant(all.Take(Math.Clamp(components, 3, 4)));
    }
}

/// <summary>M834: one custom parameter of the graph in the Details panel: rename, type, colour flag, default.</summary>
public sealed partial class SgParamRow : ObservableObject
{
    private readonly ShaderGraphViewModel _owner;
    private readonly string _name;
    private bool _syncing;

    public static IReadOnlyList<string> KindChoices { get; } = new[] { "float", "float2", "float3", "float4" };

    public SgParamRow(ShaderGraphViewModel owner, SgCustomParam p, int usedBy)
    {
        _owner = owner; _name = p.Name;
        _syncing = true;
        _nameText = p.Name;
        _kind = SgCatalog.TypeName(p.Components);
        _isColor = p.IsColor;
        _defaultText = p.Default;
        _colorValue = SgColors.FromText(p.Default);
        _syncing = false;
        Components = p.Components;
        UsedText = usedBy == 0 ? "not used by any node" : usedBy == 1 ? "used by 1 node" : $"used by {usedBy} nodes";
    }

    public string Name => _name;
    public int Components { get; }
    public string UsedText { get; }
    public bool CanBeColor => Components >= 3;
    public bool HasAlpha => Components == 4;
    public bool ShowsColor => IsColor && CanBeColor;
    public bool ShowsNumbers => !ShowsColor;

    [ObservableProperty] private string _nameText;
    [ObservableProperty] private string _kind;
    [ObservableProperty] private bool _isColor;
    [ObservableProperty] private string _defaultText;
    [ObservableProperty] private Avalonia.Media.Color _colorValue;

    /// <summary>Enter, or the box losing focus: rename (every node that reads it follows).</summary>
    [RelayCommand]
    public void CommitName()
    {
        if (_syncing || NameText == _name) return;
        if (!_owner.RenameParameter(_name, NameText)) NameText = _name;
    }

    [RelayCommand]
    public void CommitDefault()
    {
        if (_syncing) return;
        if (!_owner.SetParameterDefault(_name, DefaultText)) DefaultText = _owner.Document?.FindParam(_name)?.Default ?? DefaultText;
    }

    [RelayCommand]
    public void Delete() => _owner.DeleteParameter(_name);

    partial void OnKindChanged(string value)
    {
        if (_syncing || value is null) return;
        int w = SgCustom.OutWidth(value);
        if (w > 0 && w != Components) _owner.SetParameterKind(_name, w);
    }

    partial void OnIsColorChanged(bool value)
    {
        if (_syncing) return;
        _owner.SetParameterColor(_name, value);
    }

    partial void OnColorValueChanged(Avalonia.Media.Color value)
    {
        if (_syncing || !IsColor) return;
        DefaultText = SgColors.ToText(value, Components);
        CommitDefault();
    }
}

/// <summary>M835: one custom texture of the graph in the Details panel: rename, default image, address modes, delete.</summary>
public sealed partial class SgTextureRow : ObservableObject
{
    private readonly ShaderGraphViewModel _owner;
    private readonly string _name;
    private bool _syncing;

    public static IReadOnlyList<string> AddressChoices { get; } = SgCustomTexture.AddressModes;

    public SgTextureRow(ShaderGraphViewModel owner, SgCustomTexture t, int usedBy)
    {
        _owner = owner; _name = t.Name;
        _syncing = true;
        _nameText = t.Name; _pathText = t.DefaultPath; _addressU = t.AddressU; _addressV = t.AddressV;
        _syncing = false;
        UsedText = usedBy == 0 ? "not used by any node" : usedBy == 1 ? "used by 1 node" : $"used by {usedBy} nodes";
    }

    public string Name => _name;
    public string UsedText { get; }
    [ObservableProperty] private string _nameText;
    [ObservableProperty] private string _pathText;
    [ObservableProperty] private string _addressU;
    [ObservableProperty] private string _addressV;

    [RelayCommand] public void CommitName() { if (!_syncing && NameText != _name && !_owner.RenameTexture(_name, NameText)) NameText = _name; }
    [RelayCommand] public void CommitPath() { if (!_syncing && !_owner.SetTextureDefault(_name, PathText)) PathText = _owner.Document?.FindTexture(_name)?.DefaultPath ?? PathText; }
    [RelayCommand] public void Delete() => _owner.DeleteTexture(_name);
    partial void OnAddressUChanged(string value) { if (!_syncing && value is not null) _owner.SetTextureAddress(_name, value, null); }
    partial void OnAddressVChanged(string value) { if (!_syncing && value is not null) _owner.SetTextureAddress(_name, null, value); }
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
/// bin. M833: a graph ships when a material is assigned to it (REY_GRAPH); the export rebuilds the shader cache patch from the installed game.</para>
///
/// <para><b>Editing.</b> Every gesture (add, delete, connect, disconnect, edit a setting) is ONE undo step on this view model's OWN undo
/// stack (a private <see cref="UndoRedoService"/>: a Shader Graph edit never lands on the Material Editor's or the main window's stack),
/// recorded as a before/after snapshot of the document. The type pass runs on every edit (cheap, on the UI thread) and paints types and
/// errors on the canvas at once; the HLSL compile follows, debounced and OFF the UI thread (<see cref="ShaderGraphCompiler"/>: one pixel
/// shader per input signature of the base, each verified against Riot's), and raises <see cref="BuildCompleted"/> for the preview.</para>
/// </summary>
public sealed partial class ShaderGraphViewModel : ObservableObject, IDisposable
{
    public const string Banner = "Graphs ship when assigned: they are rebuilt from the installed game on every export, so re-export after each patch.";

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
        ParamRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NoParamRows));
        TextureRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NoTextureRows));
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
    public ObservableCollection<SgParamRow> ParamRows { get; } = new();
    public ObservableCollection<SgTextureRow> TextureRows { get; } = new();
    public bool NoTextureRows => TextureRows.Count == 0;

    /// <summary>M835: the host renames a texture in the materials of the graph (their samplerValues entries). Arguments: graph file path, old name, new name.</summary>
    public Func<string?, string, string, (int Renamed, string Note)>? RenameTextureInMaterials { get; set; }

    /// <summary>M836: the host adds (or, on undo, removes) a new custom texture in the materials assigned to this graph. Arguments: graph file path, the texture, add?</summary>
    public Func<string?, SgCustomTexture, bool, (int Changed, string Note)>? AddTextureToMaterials { get; set; }

    /// <summary>M835: checks that a default texture path names an asset the project can read (the host knows the mounts). Null = unchecked.</summary>
    public Func<string, bool>? TextureExists { get; set; }
    public bool NoParamRows => ParamRows.Count == 0;

    /// <summary>M834: the host renames a parameter in the materials of the graph (the Material Editor's own commands, one undo step there).
    /// Arguments: this graph's file path (null while unsaved), the old name, the new name. Returns how many material parameters were renamed.</summary>
    public Func<string?, string, string, (int Renamed, string Note)>? RenameParameterInMaterials { get; set; }

    /// <summary>M834: the names of the materials (of the document the Material Editor holds) that are assigned to this graph and still carry a parameter of this name.</summary>
    public Func<string?, string, IReadOnlyList<string>>? MaterialsCarryingParameter { get; set; }
    public ObservableCollection<SgDiagnosticRow> DiagnosticRows { get; } = new();
    public ObservableCollection<SgFileRow> SavedGraphs { get; } = new();

    public string BannerText => Banner;
    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    partial void OnIsDirtyChanged(bool value) => StateChanged?.Invoke();
    partial void OnStatusChanged(string value) => StateChanged?.Invoke();
    /// <summary>M834: the selected comment box (a node and a comment are never both selected).</summary>
    [ObservableProperty] private string? _selectedCommentId;

    partial void OnSelectedNodeIdChanged(string? value)
    {
        if (value is not null && SelectedCommentId is not null) { SelectedCommentId = null; return; }
        RefreshProps();
    }

    partial void OnSelectedCommentIdChanged(string? value)
    {
        if (value is not null && SelectedNodeId is not null) { SelectedNodeId = null; return; }
        RefreshProps();
    }
    partial void OnSignatureIndexChanged(int value) => ShowHlsl();
    partial void OnPreviewEnabledChanged(bool value) { if (_build is not null) BuildCompleted?.Invoke(_build); }

    public IReadOnlyList<SgPaletteEntry> Palette => SgCatalog.Palette(_base, _doc);

    public void Dispose()
    {
        ReleaseLive(FilePath);
        _disposed = true;
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = null;
    }

    // ============================================================================== open / new / save

    /// <summary>Close the open graph (the window goes back to the material graph). Unsaved edits are the caller's to ask about.</summary>
    public void Close()
    {
        string? closedPath = FilePath;
        _debounce?.Cancel();
        Interlocked.Increment(ref _compileVersion);   // a compile still running is dropped
        Compiling = false;
        _doc = null; _base = null; _analysis = null; _build = null; FilePath = null;
        _undo.Clear(); _moved = false; IsDirty = false; _editVersion++;
        Status = "";
        Graph = null; SelectedNodeId = null; SelectedCommentId = null; GraphName = ""; BaseShaderName = ""; Hlsl = ""; HlslNote = "No Shader Graph is open.";
        PropRows.Clear(); ParamRows.Clear(); TextureRows.Clear(); DiagnosticRows.Clear(); CompileState = "";
        ReleaseLive(closedPath);
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo));
        StateChanged?.Invoke();
    }

    /// <summary>Make a new graph on <paramref name="baseShader"/> (the material's renderShader) with the starter wiring
    /// texture RGB -> Base Color, texture A -> Opacity. The name is made unique among the project's saved graphs.</summary>
    public bool NewFromBase(string baseShader, out string error, string? suggestedName = null) => NewFromTemplate(baseShader, null, out error, suggestedName);

    /// <summary>M835: a new graph from one of the <see cref="SgTemplates"/> (null = the original starter: texture RGB to Base Color, texture A to Opacity).</summary>
    public bool NewFromTemplate(string baseShader, string? templateId, out string error, string? suggestedName = null)
    {
        error = "";
        if (BaseResolver is null) { error = "No shader cache is available to read the base shader from."; Status = error; return false; }
        var (b, why) = BaseResolver(baseShader);
        if (b is null) { error = why.Length > 0 ? why : $"'{baseShader}' is not a supported base shader."; Status = error; return false; }

        string leaf = baseShader.Trim('/').Split('/').Last();
        string name = UniqueName(suggestedName is { Length: > 0 } ? suggestedName : leaf + "_Graph");
        ShaderGraphDocument doc;
        if (templateId is not null)
        {
            if (SgTemplates.Problem(templateId, b) is { } why2) { error = $"The '{templateId}' template cannot be built on {leaf}: {why2}."; Status = error; return false; }
            try { doc = SgTemplates.Build(templateId, name, b); }
            catch (Exception ex) { error = ex.Message; Status = error; return false; }
        }
        else doc = ShaderGraphDocument.CreateEmpty(name, b.Shader);
        var tex = templateId is null ? b.Textures.FirstOrDefault() : null;
        if (tex is not null)
        {
            doc.Nodes.Add(new SgNode { Id = "n2", Type = "TextureSample", X = 300, Y = 120, Props = { ["texture"] = tex.Name } });
            doc.Links.Add(new SgLink { FromNode = "n2", FromPin = "RGB", ToNode = "n1", ToPin = SgCatalog.BaseColorPin });
            doc.Links.Add(new SgLink { FromNode = "n2", FromPin = "A", ToNode = "n1", ToPin = SgCatalog.OpacityPin });
        }
        try { Load(doc, b, null); }
        catch (Exception ex) { Close(); error = "The new Shader Graph could not be set up: " + ex.Message; Status = error; return false; }
        IsDirty = true;   // not on disk yet
        Status = $"New Shader Graph '{name}' on {leaf}{(templateId is null ? "" : " from the '" + (SgTemplates.All.FirstOrDefault(t => t.Id == templateId)?.Title ?? templateId) + "' template")}. Save (Ctrl+S) writes it to the project.";
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

    /// <summary>M835: the viewport service must stop drawing this editor's document for <paramref name="path"/> (the graph was closed, replaced by another, or the editor went away):
    /// back to the saved file, and the main window is told so it can refresh.</summary>
    private static void ReleaseLive(string? path)
    {
        if (path is null) return;
        ShaderGraphViewport.SetLive(path, null);
        ShaderGraphViewport.NotifyGraphChanged(path);
    }

    private void Load(ShaderGraphDocument doc, SgBase b, string? path)
    {
        if (FilePath is not null && !string.Equals(FilePath, path, StringComparison.OrdinalIgnoreCase)) ReleaseLive(FilePath);   // Open B / a new graph while A is open
        _debounce?.Cancel();
        _doc = doc; _base = b; FilePath = path; _build = null;
        _undo.Clear(); _moved = false; IsDirty = false;
        GraphName = doc.Name; BaseShaderName = doc.BaseShader;
        SelectedNodeId = null; SelectedCommentId = null;
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
                Status = $"Saved {Path.GetFileName(path)} to the project (editor data: the graph itself is not packed; a material it is assigned to ships its compiled shader).";
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
        private readonly (string From, string To)? _rename;
        private readonly bool _textures;
        private readonly SgCustomTexture? _added;
        public SnapshotCommand(ShaderGraphViewModel owner, string name, ShaderGraphDocument before, ShaderGraphDocument after, object context, (string From, string To)? rename, bool textures, SgCustomTexture? added = null)
        { _owner = owner; Name = name; _before = before; _after = after; Context = context; _rename = rename; _textures = textures; _added = added; }
        public string Name { get; }
        public object? Context { get; }
        public void Execute() { _owner.Restore(_after); if (_rename is { } r) _owner.FollowRename(r.From, r.To, _textures); if (_added is not null) _owner.FollowTextureAdd(_added, true); }
        public void Undo() { _owner.Restore(_before); if (_rename is { } r) _owner.FollowRename(r.To, r.From, _textures); if (_added is not null) _owner.FollowTextureAdd(_added, false); }
        public bool CanMergeWith(IEditorCommand next) => false;
        public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
    }

    /// <summary>Apply an edit as one undo step: <paramref name="edit"/> changes a COPY of the document; the step swaps the before and after snapshots.</summary>
    private bool Do(string name, Action<ShaderGraphDocument> edit, (string From, string To)? rename = null, bool textures = false, string? addedTexture = null)
    {
        if (_doc is null) return false;
        var before = _doc.Clone();
        var after = _doc.Clone();
        edit(after);
        if (Same(before, after)) return false;
        var added = addedTexture is null ? null : after.FindTexture(addedTexture)?.Clone();
        var cmd = new SnapshotCommand(this, name, before, after, _context, rename, textures, added);
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
            foreach (var c in copy.Comments)
                if (_doc.FindComment(c.Id) is { } now) { c.X = now.X; c.Y = now.Y; c.Width = now.Width; c.Height = now.Height; }
            _doc.Nodes = copy.Nodes;
            _doc.Links = copy.Links;
            _doc.Params = copy.Params;
            _doc.Textures = copy.Textures;
            _doc.Comments = copy.Comments;
        }
        finally { _restoring = false; }
    }

    private string _renameNote = "";

    /// <summary>M836: a texture created in the graph (or its undo) reaches the materials that name this graph, through the host.</summary>
    internal void FollowTextureAdd(SgCustomTexture texture, bool add)
    {
        (int Changed, string Note) r = (0, "");
        try { r = AddTextureToMaterials?.Invoke(FilePath, texture, add) ?? (0, ""); }
        catch (Exception ex) { _renameNote = $" (the materials could NOT follow: {ex.Message})"; return; }
        _renameNote = r.Changed > 0 ? $" ({r.Changed} material{(r.Changed == 1 ? "" : "s")} {(add ? "got" : "lost")} the '{texture.Name}' sampler in the Material Editor; undo it there too)" : "";
    }

    /// <summary>A rename (or its undo) reaches the materials that name this graph, through the host.</summary>
    internal void FollowRename(string from, string to, bool texture = false)
    {
        (int Renamed, string Note) r = (0, "");
        try { r = (texture ? RenameTextureInMaterials : RenameParameterInMaterials)?.Invoke(FilePath, from, to) ?? (0, ""); }
        catch (Exception ex) { _renameNote = $" (the materials could NOT follow: {ex.Message}. A material that still carries '{from}' blocks the export: rename or remove it in the Material Editor)"; return; }
        _renameNote = r.Renamed > 0 ? $" ({r.Renamed} material parameter{(r.Renamed == 1 ? "" : "s")} renamed in the Material Editor; undo it there too)" : "";
        if (r.Note.Length > 0) _renameNote += " " + r.Note;
    }

    private bool ComputeDirty() => _undo.IsDirty || _moved || FilePath is null;   // a graph never saved stays dirty whatever is undone

    private void AfterEdit(string what)
    {
        _editVersion++;
        IsDirty = true;
        OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo));
        if (SelectedNodeId is not null && _doc!.Find(SelectedNodeId) is null) SelectedNodeId = null;
        if (SelectedCommentId is not null && _doc!.FindComment(SelectedCommentId) is null) SelectedCommentId = null;
        Reanalyse();
        RefreshProps();
        RequestCompile();
        Status = what + _renameNote;
        _renameNote = "";
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
        bool ok = Do("Add " + entry.Title, d => id = ApplyEntry(d, entry, x, y));
        if (ok) SelectedNodeId = id;
        return ok ? id : null;
    }

    /// <summary>Create the node of a palette line in <paramref name="d"/> (and the custom parameter or texture a "new" line asks for); returns its id.</summary>
    private string ApplyEntry(ShaderGraphDocument d, SgPaletteEntry entry, double x, double y)
    {
        string id = d.NewId();
        var n = new SgNode { Id = id, Type = entry.Type, X = Math.Round(x), Y = Math.Round(y) };
        foreach (var (k, v) in entry.Props) if (k != SgCatalog.NewParamKey && k != SgCatalog.NewTextureKey) n.Props[k] = v;
        if (entry.Props.TryGetValue(SgCatalog.NewParamKey, out var kind) && SgCustomParams.ParseKind(kind) is { } pk)
        {
            // "new parameter": the parameter and the node that reads it are ONE undo step
            string pn = SgNames.UniqueParameterName(pk.IsColor ? "ColorParam" : pk.Components == 1 ? "ScalarParam" : "VectorParam", d, _base);
            d.Params.Add(new SgCustomParam { Name = pn, Components = pk.Components, IsColor = pk.IsColor, Default = SgCustomParams.NewDefault(pk.Components, pk.IsColor) });
            n.Props["name"] = pn;
        }
        if (entry.Props.ContainsKey(SgCatalog.NewTextureKey))
        {
            string tn = SgNames.UniqueTextureName("CustomTexture", d, _base);
            d.Textures.Add(new SgCustomTexture { Name = tn });
            n.Props["texture"] = tn;
        }
        d.Nodes.Add(n);
        return id;
    }

    /// <summary>
    /// M835 (Unreal's drag-a-wire-into-empty-space): create the node of <paramref name="entry"/> at a point and wire it to the pin the wire was dragged from, as ONE undo step.
    /// <paramref name="fromIsOutput"/>: the wire started at an output pin (<paramref name="fromNode"/>.<paramref name="fromPin"/>) and ends at the new node's input <paramref name="newPin"/>;
    /// otherwise it started at an unwired input and the new node's output <paramref name="newPin"/> feeds it.
    /// </summary>
    public string? AddNodeAndConnect(SgPaletteEntry entry, double x, double y, string fromNode, string fromPin, bool fromIsOutput, string newPin)
    {
        if (_doc is null || _doc.Find(fromNode) is not { } from) return null;
        // the pins are checked on a throw-away node BEFORE anything is recorded: a refusal changes nothing and costs no undo step
        var probe = new SgNode { Id = "probe", Type = entry.Type };
        foreach (var (k, v) in entry.Props) probe.Props[k] = v;
        var newDef = SgCatalog.DefFor(probe);
        var fromDef = SgCatalog.DefFor(from);
        bool pinsOk = newDef is not null && fromDef is not null
            && (fromIsOutput ? fromDef.OutputIndex(fromPin) >= 0 && newDef.InputIndex(newPin) >= 0 : newDef.OutputIndex(newPin) >= 0 && fromDef.InputIndex(fromPin) >= 0);
        if (!pinsOk) { Status = "That node has no such pin."; return null; }
        string? id = null;
        bool ok = Do("Add " + entry.Title + " and connect", d =>
        {
            id = ApplyEntry(d, entry, x, y);
            string srcNode = fromIsOutput ? fromNode : id, srcPin = fromIsOutput ? fromPin : newPin;
            string dstNode = fromIsOutput ? id : fromNode, dstPin = fromIsOutput ? newPin : fromPin;
            d.Links.RemoveAll(l => l.ToNode == dstNode && l.ToPin == dstPin);
            d.Links.Add(new SgLink { FromNode = srcNode, FromPin = srcPin, ToNode = dstNode, ToPin = dstPin });
        });
        if (ok) SelectedNodeId = id;
        return ok ? id : null;
    }

    /// <summary>The width a pin of a node takes (its fixed width, or 1 when it takes any: the pin's current default is a scalar).</summary>
    private int PinWidth(SgNode n, string pin) => SgCatalog.DefFor(n)?.Inputs.FirstOrDefault(p => p.Name == pin) is { } pd ? (pd.Fixed > 0 ? pd.Fixed : 1) : 1;

    /// <summary>Why "Promote to Parameter" / "Add Constant" cannot be used on this pin, or null.</summary>
    public string? CanPromote(string nodeId, string pin)
    {
        var n = _doc?.Find(nodeId);
        if (n is null) return "That node no longer exists.";
        var def = SgCatalog.DefFor(n);
        if (def is null || def.InputIndex(pin) < 0) return "That pin does not exist.";
        if (_doc!.LinkInto(nodeId, pin) is not null) return "That pin is already wired.";
        return null;
    }

    /// <summary>The current default of an unwired pin, as the text a parameter or constant of its width holds.</summary>
    private string PinValueText(SgNode n, SgPinDef pd, int width)
    {
        float v = SgCatalog.IsUvPin(n, pd) || SgCatalog.IsTimePin(n, pd) ? 0f : SgCatalog.PinDefault(n, pd);
        return SgCatalog.FormatConstant(Enumerable.Repeat(v, width));
    }

    /// <summary>M835: right-click an unwired input &gt; Promote to Parameter: a custom parameter of the pin's type, holding the pin's current value, wired into it. One undo step.
    /// A Base Color pin becomes a colour. A UV or Time pin has no number as its default (the engine supplies it): the parameter starts at zero.</summary>
    public string? PromoteToParameter(string nodeId, string pin)
    {
        if (CanPromote(nodeId, pin) is { } why) { Status = why; return null; }
        if (_doc!.Params.Count >= ShaderGraphDocument.MaxCustomParams) { Status = $"A graph has at most {ShaderGraphDocument.MaxCustomParams} custom parameters."; return null; }
        var n = _doc.Find(nodeId)!;
        var pd = SgCatalog.DefFor(n)!.Inputs.First(p => p.Name == pin);
        int w = PinWidth(n, pin);
        string? created = null;
        bool ok = Do("Promote " + pin + " to parameter", d =>
        {
            string name = SgNames.ParameterNameFrom(pin, d, _base);
            created = name;
            d.Params.Add(new SgCustomParam { Name = name, Components = w, IsColor = pin == SgCatalog.BaseColorPin && w >= 3, Default = PinValueText(n, pd, w) });
            string id = d.NewId();
            var target = d.Find(nodeId)!;
            d.Nodes.Add(new SgNode { Id = id, Type = "Parameter", X = Math.Round(target.X - 260), Y = Math.Round(target.Y + 40 + 24 * (SgCatalog.DefFor(target)!.InputIndex(pin))), Props = { ["name"] = name } });
            d.Links.Add(new SgLink { FromNode = id, FromPin = "Value", ToNode = nodeId, ToPin = pin });
        });
        if (ok) Status = $"Promoted {pin} to the custom parameter '{created}' ({SgCatalog.TypeName(w)}). A material can now set it.";
        return ok ? created : null;
    }

    /// <summary>M835: right-click an unwired input &gt; Add Constant: a Constant node of the pin's width holding the pin's current value, wired into it. One undo step.</summary>
    public string? AddConstantTo(string nodeId, string pin)
    {
        if (CanPromote(nodeId, pin) is { } why) { Status = why; return null; }
        var n = _doc!.Find(nodeId)!;
        var pd = SgCatalog.DefFor(n)!.Inputs.First(p => p.Name == pin);
        int w = PinWidth(n, pin);
        string? id = null;
        bool ok = Do("Add constant to " + pin, d =>
        {
            id = d.NewId();
            var target = d.Find(nodeId)!;
            d.Nodes.Add(new SgNode { Id = id, Type = "Constant", X = Math.Round(target.X - 260), Y = Math.Round(target.Y + 40 + 24 * (SgCatalog.DefFor(target)!.InputIndex(pin))), Props = { ["value"] = PinValueText(n, pd, w) } });
            d.Links.Add(new SgLink { FromNode = id, FromPin = "Value", ToNode = nodeId, ToPin = pin });
        });
        if (ok) SelectedNodeId = id;
        return ok ? id : null;
    }

    // ============================================================================== custom textures (M835)

    public string? TextureProblem(string name, string? renaming = null) => _doc is null ? "No Shader Graph is open." : SgNames.TextureProblem(name, _doc, _base, renaming);

    /// <summary>Create a custom texture without a node (the Details panel's button).</summary>
    public string? AddTexture(string? name = null)
    {
        if (_doc is null) return null;
        string tn = name ?? SgNames.UniqueTextureName("CustomTexture", _doc, _base);
        if (TextureProblem(tn) is { } why) { Status = why; return null; }
        if (_doc.Textures.Count >= ShaderGraphDocument.MaxCustomTextures) { Status = $"A graph has at most {ShaderGraphDocument.MaxCustomTextures} custom textures."; return null; }
        return Do("Add texture " + tn, d => d.Textures.Add(new SgCustomTexture { Name = tn })) ? tn : null;
    }

    [RelayCommand] private void AddTextureParameter() => AddTexture();

    /// <summary>The choice at the end of a Texture Sample's Texture list that makes a new custom texture.</summary>
    public const string NewTextureChoice = "+ New texture...";

    /// <summary>A valid, unused custom texture name from a node's label ("Glow texture" becomes GlowTexture); "CustomTexture" when the label gives nothing usable.</summary>
    public string TextureNameFromLabel(string label, ShaderGraphDocument d)
    {
        var sb = new System.Text.StringBuilder();
        bool up = true;
        foreach (char c in label ?? "")
        {
            if (char.IsAsciiLetterOrDigit(c)) { sb.Append(up ? char.ToUpperInvariant(c) : c); up = false; }
            else up = true;
        }
        while (sb.Length > 0 && char.IsAsciiDigit(sb[0])) sb.Remove(0, 1);
        if (sb.Length > 0) sb[0] = char.ToUpperInvariant(sb[0]);
        if (sb.Length > SgNames.MaxParamName - 4) sb.Length = SgNames.MaxParamName - 4;
        string name = sb.Length == 0 ? "CustomTexture" : sb.ToString();
        string unique = SgNames.UniqueTextureName(name, d, _base);
        return SgNames.TextureProblem(unique, d, _base) is null ? unique : SgNames.UniqueTextureName("CustomTexture", d, _base);
    }

    /// <summary>
    /// M836: "+ New texture..." on a Texture Sample node: a custom texture named after the node's label (made valid and unique), selected on the node, and added to every
    /// material assigned to this graph. ONE undo step on the graph's stack, and ONE on the Material Editor's (undoing this step removes the samplers again).
    /// </summary>
    public string? NewTextureForNode(string nodeId)
    {
        if (_doc is null || _doc.Find(nodeId) is not { Type: "TextureSample" } n) return null;
        if (_doc.Textures.Count >= ShaderGraphDocument.MaxCustomTextures) { Status = $"A graph has at most {ShaderGraphDocument.MaxCustomTextures} custom textures."; return null; }
        string tn = TextureNameFromLabel(n.Label, _doc);
        bool ok = Do("New texture " + tn, d =>
        {
            d.Textures.Add(new SgCustomTexture { Name = tn });
            d.Find(nodeId)!.Props["texture"] = tn;
        }, addedTexture: tn);
        return ok ? tn : null;
    }

    /// <summary>Rename a custom texture: every Texture Sample that reads it follows, and the materials assigned to this graph follow through the host (their samplerValues entry).</summary>
    public bool RenameTexture(string oldName, string newName)
    {
        if (_doc is null) return false;
        newName = (newName ?? "").Trim();
        if (_doc.FindTexture(oldName) is null) { Status = $"'{oldName}' is not a custom texture of this graph."; return false; }
        if (newName == oldName) return false;
        if (TextureProblem(newName, oldName) is { } why) { Status = why; return false; }
        return Do($"Rename texture {oldName} to {newName}", d =>
        {
            d.FindTexture(oldName)!.Name = newName;
            foreach (var n in d.Nodes)
                if (n.Type == "TextureSample" && n.Prop("texture") == oldName) n.Props["texture"] = newName;
        }, (oldName, newName), textures: true);
    }

    public bool SetTextureDefault(string name, string path)
    {
        var t = _doc?.FindTexture(name);
        if (t is null) return false;
        path = (path ?? "").Trim().Replace('\\', '/');
        if (SgNames.TexturePathProblem(path) is { } pathWhy) { Status = pathWhy; return false; }
        if (path.Length > 0 && TextureExists is { } exists && !exists(path)) Status = $"Note: '{path}' is not an asset the project can read; the preview shows no default image.";
        return Do("Edit texture " + name, d => d.FindTexture(name)!.DefaultPath = path);
    }

    public bool SetTextureAddress(string name, string? u, string? v)
    {
        var t = _doc?.FindTexture(name);
        if (t is null) return false;
        if (u is not null && !SgCustomTexture.AddressModes.Contains(u) || v is not null && !SgCustomTexture.AddressModes.Contains(v)) return false;
        return Do("Edit texture " + name, d => { var q = d.FindTexture(name)!; if (u is not null) q.AddressU = u; if (v is not null) q.AddressV = v; });
    }

    public bool DeleteTexture(string name)
    {
        if (_doc is null || _doc.FindTexture(name) is null) return false;
        int uses = _doc.Nodes.Count(n => n.Type == "TextureSample" && n.Prop("texture") == name);
        if (uses > 0) { Status = $"'{name}' is still read by {uses} node(s): delete them first."; return false; }
        return Do("Delete texture " + name, d => d.Textures.RemoveAll(q => q.Name == name));
    }

    public bool DeleteNode(string id)
    {
        var n = _doc?.Find(id);
        if (n is null) return false;
        if (n.Type == SgCatalog.OutputType) { Status = "The Shader Output node cannot be deleted."; return false; }
        string title = n.Label.Length > 0 ? n.Label : SgCatalog.Get(n.Type)?.Title ?? n.Type;
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
        var adef = SgCatalog.DefFor(a); var bdef = SgCatalog.DefFor(b);
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
        bool isCode = n.Type == SgCustom.Type && key == "code";
        if (value.Length > (isCode ? ShaderGraphDocument.MaxCodeLength : 256)) { Status = "That value is too long."; return false; }
        // a pin's own value (A, B, Alpha...) is a number
        if (SgCatalog.DefFor(n) is { } d && d.InputIndex(key) >= 0
            && !(float.TryParse(value.Trim(), System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out float num) && float.IsFinite(num)))
        { Status = $"'{value}' is not a number."; return false; }
        string stored = isCode ? value.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd() : value.Trim();
        return Do("Edit " + (n.Label.Length > 0 ? n.Label : SgCatalog.Get(n.Type)?.Title ?? n.Type), doc =>
        {
            var node = doc.Find(nodeId)!;
            node.Props[key] = stored;
            if (node.Type == SgCustom.Type && key == "inputs")
            {
                // a wire into an input that no longer exists goes with it
                var pins = SgCatalog.DefFor(node)!.Inputs.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                doc.Links.RemoveAll(l => l.ToNode == nodeId && !pins.Contains(l.ToPin));
            }
        });
    }

    // ============================================================================== labels, custom parameters, comments (M834)

    private const int MaxLabel = ShaderGraphDocument.MaxLabelLength;

    /// <summary>Name a node (Unreal's node comment): shown in its title bar. Empty puts the type's own title back. One undo step.</summary>
    public bool SetLabel(string nodeId, string text)
    {
        var n = _doc?.Find(nodeId);
        if (n is null) return false;
        var clean = new string((text ?? "").Where(c => c >= ' ' && c != '\u007f').ToArray()).Trim();
        if (clean.Length > MaxLabel) { Status = $"A label has at most {MaxLabel} characters."; return false; }
        return Do(clean.Length == 0 ? "Clear node label" : "Name node " + clean, d => d.Find(nodeId)!.Label = clean);
    }

    public string? ParameterProblem(string name, string? renaming = null) => _doc is null ? "No Shader Graph is open." : SgNames.ParameterProblem(name, _doc, _base, renaming);

    /// <summary>Create a custom parameter without a node (the Details panel's buttons). <paramref name="kind"/>: float, float2, float3, float4, color3, color4.</summary>
    public string? AddParameter(string kind, string? name = null)
    {
        if (_doc is null || SgCustomParams.ParseKind(kind) is not { } pk) return null;
        string pn = name ?? SgNames.UniqueParameterName(pk.IsColor ? "ColorParam" : pk.Components == 1 ? "ScalarParam" : "VectorParam", _doc, _base);
        if (ParameterProblem(pn) is { } why) { Status = why; return null; }
        if (_doc.Params.Count >= ShaderGraphDocument.MaxCustomParams) { Status = $"A graph has at most {ShaderGraphDocument.MaxCustomParams} custom parameters."; return null; }
        bool ok = Do("Add parameter " + pn, d => d.Params.Add(new SgCustomParam { Name = pn, Components = pk.Components, IsColor = pk.IsColor, Default = SgCustomParams.NewDefault(pk.Components, pk.IsColor) }));
        return ok ? pn : null;
    }

    [RelayCommand] private void AddScalarParameter() => AddParameter("float");
    [RelayCommand] private void AddVectorParameter() => AddParameter("float4");
    [RelayCommand] private void AddColourParameter() => AddParameter("color4");

    /// <summary>Rename a custom parameter: every node that reads it follows, and the materials assigned to this graph follow through the host (the Material
    /// Editor's own rename command, one undo step there).</summary>
    public bool RenameParameter(string oldName, string newName)
    {
        if (_doc is null) return false;
        newName = (newName ?? "").Trim();
        if (_doc.FindParam(oldName) is null) { Status = $"'{oldName}' is not a custom parameter of this graph."; return false; }
        if (newName == oldName) return false;
        if (ParameterProblem(newName, oldName) is { } why) { Status = why; return false; }
        return Do($"Rename parameter {oldName} to {newName}", d =>
        {
            d.FindParam(oldName)!.Name = newName;
            foreach (var n in d.Nodes)
                if (n.Type == "Parameter" && n.Prop("name") == oldName) n.Props["name"] = newName;
        }, (oldName, newName));
    }

    public bool SetParameterDefault(string name, string text)
    {
        var p = _doc?.FindParam(name);
        if (p is null) return false;
        if (!SgCatalog.TryParseConstant(text, out var v) || v.Length != p.Components)
        { Status = $"'{text}' is not {p.Components} number(s) (a {SgCatalog.TypeName(p.Components)})."; return false; }
        string canon = SgCatalog.FormatConstant(v);
        return Do("Edit parameter " + name, d => d.FindParam(name)!.Default = canon);
    }

    public bool SetParameterKind(string name, int components)
    {
        var p = _doc?.FindParam(name);
        if (p is null || components is < 1 or > 4 || components == p.Components) return false;
        return Do($"Change parameter {name} to {SgCatalog.TypeName(components)}", d =>
        {
            var q = d.FindParam(name)!;
            q.Components = components;
            q.Default = SgCustomParams.ResizeDefault(q.Default, components);
            if (components < 3) q.IsColor = false;
        });
    }

    public bool SetParameterColor(string name, bool isColor)
    {
        var p = _doc?.FindParam(name);
        if (p is null || p.IsColor == isColor) return false;
        if (isColor && p.Components < 3) { Status = "Only a float3 or float4 can be a colour."; return false; }
        return Do((isColor ? "Make parameter a colour: " : "Make parameter plain numbers: ") + name, d => d.FindParam(name)!.IsColor = isColor);
    }

    public bool DeleteParameter(string name)
    {
        if (_doc is null || _doc.FindParam(name) is null) return false;
        int uses = _doc.Nodes.Count(n => n.Type == "Parameter" && n.Prop("name") == name);
        if (uses > 0) { Status = $"'{name}' is still read by {uses} node(s): delete them first."; return false; }
        bool deleted = Do("Delete parameter " + name, d => d.Params.RemoveAll(q => q.Name == name));
        if (deleted)
        {
            var carrying = MaterialsCarryingParameter?.Invoke(FilePath, name) ?? Array.Empty<string>();
            if (carrying.Count > 0)
                Status = $"Deleted '{name}', but material(s) {string.Join(", ", carrying.Take(6))}{(carrying.Count > 6 ? "..." : "")} still carry it: no shaders.bin declares it any more, so the export is blocked until you remove it there.";
        }
        return deleted;
    }

    public string? AddComment(double x, double y, double w = 360, double h = 220, string title = "Comment")
    {
        if (_doc is null) return null;
        if (_doc.Comments.Count >= ShaderGraphDocument.MaxComments) { Status = "Too many comment boxes."; return null; }
        string? id = null;
        bool ok = Do("Add comment", d =>
        {
            id = d.NewCommentId();
            d.Comments.Add(new SgComment { Id = id, Title = title, X = Math.Round(x), Y = Math.Round(y), Width = Math.Round(w), Height = Math.Round(h) });
        });
        return ok ? id : null;
    }

    /// <summary>A comment box around the given nodes (the selection), with room for its title.</summary>
    public string? AddCommentAround(IEnumerable<string> nodeIds)
    {
        var g = Graph;
        var ns = nodeIds.Select(i => g?.Find(i)).Where(n => n is not null).Select(n => n!).ToList();
        if (ns.Count == 0) { Status = "Select a node (Ctrl+click adds more) to put a comment around."; return null; }
        const double pad = 28, titleH = 44;
        double x0 = ns.Min(n => n.X) - pad, y0 = ns.Min(n => n.Y) - pad - titleH, x1 = ns.Max(n => n.Right) + pad, y1 = ns.Max(n => n.Bottom) + pad;
        return AddComment(x0, y0, x1 - x0, y1 - y0);
    }

    public bool SetCommentTitle(string id, string title)
    {
        if (_doc?.FindComment(id) is null) return false;
        title = new string((title ?? "").Where(c => c >= ' ' && c != '\u007f').ToArray()).Trim();
        if (title.Length > 128) { Status = "A comment title has at most 128 characters."; return false; }
        return Do("Edit comment title", d => d.FindComment(id)!.Title = title);
    }

    public bool SetCommentColor(string id, string key)
    {
        if (_doc?.FindComment(id) is null || !SgComment.ColorKeys.Contains(key)) return false;
        return Do("Colour comment", d => d.FindComment(id)!.Color = key);
    }

    public bool DeleteComment(string id) => _doc?.FindComment(id) is not null && Do("Delete comment", d => d.Comments.RemoveAll(c => c.Id == id));

    /// <summary>The nodes whose boxes lie fully inside a comment: what moves with it.</summary>
    public IReadOnlyList<string> NodesInside(string commentId)
    {
        var c = _doc?.FindComment(commentId);
        var g = Graph;
        if (c is null || g is null) return Array.Empty<string>();
        return g.Nodes.Where(n => n.X >= c.X && n.Y >= c.Y && n.Right <= c.X + c.Width && n.Bottom <= c.Y + c.Height).Select(n => n.Id).ToList();
    }

    /// <summary>Layout only (no undo step, like moving a node): the comment and the nodes in it go to new positions.</summary>
    public void MoveComment(string id, double x, double y, IReadOnlyDictionary<string, (double X, double Y)>? nodes = null)
    {
        var c = _doc?.FindComment(id);
        if (c is null) return;
        c.X = Math.Round(x); c.Y = Math.Round(y);
        if (Graph?.Comments.FirstOrDefault(k => k.Id == id) is { } gc) { gc.X = c.X; gc.Y = c.Y; }
        if (nodes is not null)
            foreach (var (nid, pos) in nodes)
                if (_doc!.Find(nid) is { } n)
                {
                    n.X = Math.Round(pos.X); n.Y = Math.Round(pos.Y);
                    if (Graph?.Find(nid) is { } gn) gn.SetPosition(n.X, n.Y);
                }
        _moved = true; _editVersion++; IsDirty = true;
    }

    public void ResizeComment(string id, double w, double h)
    {
        var c = _doc?.FindComment(id);
        if (c is null) return;
        c.Width = Math.Round(Math.Clamp(w, SgComment.MinSize, SgComment.MaxSize)); c.Height = Math.Round(Math.Clamp(h, SgComment.MinSize, SgComment.MaxSize));
        if (Graph?.Comments.FirstOrDefault(k => k.Id == id) is { } gc) { gc.Width = c.Width; gc.Height = c.Height; }
        _moved = true; _editVersion++; IsDirty = true;
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
        RefreshParams();
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
            string where = d.NodeId.Length == 0 ? "" : (_doc?.Find(d.NodeId) is { } n ? (n.Label.Length > 0 ? n.Label : SgCatalog.Get(n.Type)?.Title ?? n.Type) + " " + d.NodeId : d.NodeId) + ": ";
            DiagnosticRows.Add(new SgDiagnosticRow(d.NodeId, d.IsError, (d.IsError ? "Error  " : "Warning  ") + where + d.Message));
        }
    }

    private void RefreshParams()
    {
        ParamRows.Clear();
        TextureRows.Clear();
        if (_doc is null) return;
        foreach (var t in _doc.Textures)
            TextureRows.Add(new SgTextureRow(this, t, _doc.Nodes.Count(n => n.Type == "TextureSample" && n.Prop("texture") == t.Name)));
        foreach (var p in _doc.Params)
            ParamRows.Add(new SgParamRow(this, p, _doc.Nodes.Count(n => n.Type == "Parameter" && n.Prop("name") == p.Name)));
    }

    public void RefreshProps()
    {
        PropRows.Clear();
        if (_doc is not null && SelectedCommentId is { } cid && _doc.FindComment(cid) is { } cm)
        {
            PropRows.Add(new SgPropRow(this, cid, "title", "Comment title", cm.Title, "Shown in the box's title bar (F2 or double-click the title on the canvas).", null, t => SetCommentTitle(cid, t)));
            PropRows.Add(new SgPropRow(this, cid, "color", "Colour", cm.Color, "A palette colour: it follows the theme. Default is the neutral grey.", SgComment.ColorKeys, t => SetCommentColor(cid, t)));
            return;
        }
        if (_doc is null || SelectedNodeId is null || _doc.Find(SelectedNodeId) is not { } n) return;
        var def = SgCatalog.DefFor(n);
        if (def is null) return;
        string id = n.Id;
        PropRows.Add(new SgPropRow(this, id, "label", "Label", n.Label, "A name for this node (F2 or double-click it on the canvas). Empty = the type's own title.", null, t => SetLabel(id, t)));
        switch (n.Type)
        {
            case "TextureSample":
            {
                string sampled = n.Prop("texture");
                string textureHint = "A texture the base shader declares in shaders.bin, or one of this graph's custom textures. \"" + NewTextureChoice + "\" makes a new custom texture named after this node's label.";
                // a label is only a name: a node called GlowTexture that samples DiffuseTexture is the usual mix-up
                if (n.Label.Length > 0 && !string.Equals(n.Label, sampled, StringComparison.OrdinalIgnoreCase))
                    textureHint = $"This node samples {sampled}, not \"{n.Label}\": a label is only a name. " + textureHint;
                PropRows.Add(new SgPropRow(this, id, "texture", "Texture", sampled, textureHint,
                    (_base?.Textures.Select(t => t.Name) ?? Enumerable.Empty<string>()).Concat(_doc.Textures.Select(t => t.Name)).Append(NewTextureChoice).ToList(),
                    t => t == NewTextureChoice ? NewTextureForNode(id) is not null : SetProp(id, "texture", t),
                    actionLabel: "New texture", action: () => NewTextureForNode(id)));
            }
                if (_doc.FindTexture(n.Prop("texture")) is { } ct)
                {
                    string cname = ct.Name;
                    PropRows.Add(new SgPropRow(this, id, "renameTexture", "Rename texture", ct.Name, "Renames the custom texture everywhere in the graph and in the materials assigned to it.", null, t => RenameTexture(cname, t)));
                    PropRows.Add(new SgPropRow(this, id, "defaultImage", "Default image", ct.DefaultPath, "Asset path of the image used until a material sets this texture, for example assets/maps/.../x.tex. The preview and the viewport show it.", null, t => SetTextureDefault(cname, t)));
                    PropRows.Add(new SgPropRow(this, id, "addressU", "Address U", ct.AddressU, "How the preview samples outside 0..1 along U (the game takes the address mode from the material).", SgCustomTexture.AddressModes, t => SetTextureAddress(cname, t, null)));
                    PropRows.Add(new SgPropRow(this, id, "addressV", "Address V", ct.AddressV, "How the preview samples outside 0..1 along V.", SgCustomTexture.AddressModes, t => SetTextureAddress(cname, null, t)));
                }
                break;
            case "Parameter":
            {
                var names = (_base?.Parameters.Select(p => p.Name) ?? Enumerable.Empty<string>()).Concat(_doc.Params.Select(p => p.Name)).ToList();
                PropRows.Add(new SgPropRow(this, id, "name", "Parameter", n.Prop("name"), "A parameter the base shader declares in shaders.bin, or one of this graph's custom parameters.", names));
                if (_doc.FindParam(n.Prop("name")) is { } cp)
                {
                    string cname = cp.Name;
                    PropRows.Add(new SgPropRow(this, id, "rename", "Rename parameter", cp.Name, "Renames the custom parameter everywhere in the graph and in the materials assigned to it.", null, t => RenameParameter(cname, t)));
                    PropRows.Add(new SgPropRow(this, id, "default", "Default value", cp.Default, "Used by the preview until a material sets it.", null, t => SetParameterDefault(cname, t),
                        colorComponents: cp.IsColor && cp.Components >= 3 ? cp.Components : 0));
                }
                break;
            }
            case "ConstantColor":
                PropRows.Add(new SgPropRow(this, id, "value", "Colour", n.Prop("value", "1 1 1 1"), "r g b a, 0 to 1 and beyond. The picker sets the four numbers; they are used as they are (no sRGB conversion).", colorComponents: 4));
                break;
            case "Custom":
                PropRows.Add(new SgPropRow(this, id, "inputs", "Inputs", n.Prop("inputs", SgCustom.DefaultInputs), "name:type separated by ; (float, float2, float3 or float4). Up to 8. The names are the variables your code reads."));
                PropRows.Add(new SgPropRow(this, id, "out", "Output type", n.Prop("out", SgCustom.DefaultOut), "What the code returns.", new[] { "float", "float2", "float3", "float4" }));
                PropRows.Add(new SgPropRow(this, id, "code", "HLSL", n.Prop("code", SgCustom.DefaultCode), "An expression (In * 2.0), or a body that returns a value. ASCII only; no #, cbuffer, Texture2D, SamplerState or register. It runs in its own function.", multiline: true));
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
        {
            if (_doc.LinkInto(id, pin.Name) is null && !SgCatalog.IsUvPin(n, pin) && !SgCatalog.IsTimePin(n, pin))
                PropRows.Add(new SgPropRow(this, id, pin.Name, pin.Name + " (not wired)", SgHlsl.Float(SgCatalog.PinDefault(n, pin)), "The value used while nothing is wired here."));
            else if (_doc.LinkInto(id, pin.Name) is not null && n.Props.ContainsKey(pin.Name))
            {
                // M836: the wire wins; the constant the pin still stores does nothing (the generated shader reads the wire)
                string stored = n.Props[pin.Name].Trim();
                PropRows.Add(new SgPropRow(this, id, pin.Name, pin.Name + " (wired)", stored, $"linked: constant {stored} ignored. Unwire the pin to use the constant again.", overridden: true));
            }
        }
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
        PublishToViewport(build);
        BuildCompleted?.Invoke(build);
        StateChanged?.Invoke();
    }

    /// <summary>M835: a saved graph that materials are assigned to shows in the viewports as it is edited: the open document replaces the file's for the viewport service, its compiled
    /// signatures are handed over (so the viewport does not compile them again), and the main window is told to refresh, debounced.</summary>
    private void PublishToViewport(SgBuild build)
    {
        if (FilePath is not { } path || _doc is null || _base is null) return;
        try
        {
            ShaderGraphViewport.SetLive(path, build.Document);
            if (!build.Analysis.HasErrors) ShaderGraphViewport.Prime(build.Document, _base, build);
            string stem = ShaderGraphRegistry.StemOf(path);
            ShaderGraphViewport.Report(path, build.Ok,
                build.Analysis.HasErrors ? ShaderGraphViewport.ErrorsText(stem, build.Analysis.Errors.First().Message) :
                build.Ok ? ShaderGraphViewport.DrawnText(stem) :
                $"Shader Graph '{stem}' does not compile for every pixel input signature: Riot's shader is drawn where it fails.");
            ShaderGraphViewport.NotifyGraphChanged(path);
        }
        catch (Exception ex) { _ = ex; /* the viewport hook must never break the editor */ }
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
