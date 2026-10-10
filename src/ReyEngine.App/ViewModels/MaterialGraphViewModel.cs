using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.Graph;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.ViewModels;

/// <summary>One category block of the Details panel.</summary>
public sealed class DetailGroup
{
    public required string Category { get; init; }
    public required IReadOnlyList<GraphDetail> Items { get; init; }
}

public sealed class MaterialStatRow
{
    public required string Label { get; init; }
    public required string Value { get; init; }
}

/// <summary>
/// M829: the state behind the Material Graph - the graph itself, its selection, search, the Details panel, the
/// Stats tab and the Shader Code tab. View-only: nothing here writes to a material.
///
/// <para>It is fed by <see cref="ShaderPreviewViewModel"/>: picking a material builds the graph at once (no
/// D3D11 device needed - the shader is only reflected), and loading a raw shader builds the shader-only graph.
/// The canvas, the Details panel and the tabs bind straight to this object.</para>
/// </summary>
public sealed partial class MaterialGraphViewModel : ObservableObject, IDisposable
{
    public const string ViewOnlyTip = "Editing nodes is not available yet: this version of the Material Graph is view-only.";

    public MaterialGraphViewModel(Func<ulong, byte[]?>? readAsset)
    {
        Thumbnails = new GraphThumbnails(readAsset);
    }

    public GraphThumbnails Thumbnails { get; }

    public void Dispose()
    {
        Thumbnails.Dispose();
        Interlocked.Increment(ref _codeVersion);   // an in-flight disassembly is dropped
    }

    /// <summary>A line about what the graph shows (for instance that the editor has unsaved edits the saved
    /// material in the graph does not carry). Empty = nothing to say.</summary>
    [ObservableProperty] private string _sourceNote = "";

    /// <summary>Show a reason instead of a graph (a bin or material that could not be found).</summary>
    public void ShowMessage(string text)
    {
        SelectedNodeId = null;
        Graph = null;
        Breadcrumb = "";
        EmptyText = text;
        SourceNote = "";
        OnPropertyChanged(nameof(Summary));
        RefreshDetails();
        ShaderStats.Clear();
        MaterialStats.Clear();
        StatsNote = text;
        _vs = _ps = null;
        PixelCode = VertexCode = "";
        CodeNote = text;
    }

    [ObservableProperty] private MaterialGraph? _graph;
    [ObservableProperty] private string _breadcrumb = "";
    [ObservableProperty] private string? _selectedNodeId;
    [ObservableProperty] private bool _hideUnrelated;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _searchStatus = "";
    private const string DefaultEmptyText = "Pick a material on the Material tab (left), or load a shader on the Shader tab, to see its graph.";
    [ObservableProperty] private string _emptyText = DefaultEmptyText;

    // ---- details panel ----
    [ObservableProperty] private string _detailTitle = "Material Graph";
    [ObservableProperty] private string _detailSubtitle = "";
    [ObservableProperty] private string _detailNotes = "";
    public ObservableCollection<DetailGroup> DetailGroups { get; } = new();

    // ---- stats tab ----
    public ObservableCollection<GraphStatRow> ShaderStats { get; } = new();
    public ObservableCollection<MaterialStatRow> MaterialStats { get; } = new();
    [ObservableProperty] private string _statsNote = "";

    // ---- shader code tab ----
    [ObservableProperty] private string _pixelCode = "";
    [ObservableProperty] private string _vertexCode = "";
    [ObservableProperty] private string _codeNote = "No shader is resolved yet.";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShaderCodeText))] private int _codeStage;
    public string ShaderCodeText => CodeStage == 0 ? PixelCode : VertexCode;

    partial void OnPixelCodeChanged(string value) => OnPropertyChanged(nameof(ShaderCodeText));
    partial void OnVertexCodeChanged(string value) => OnPropertyChanged(nameof(ShaderCodeText));

    /// <summary>Raised when the view should frame the whole graph (Home).</summary>
    public event Action? FitRequested;
    /// <summary>Raised when the view should centre a node (Search).</summary>
    public event Action<string>? FocusRequested;

    public string Summary => Graph is null ? "" : $"{Graph.Nodes.Count} nodes, {Graph.Wires.Count} wires";

    // ============================================================================== feeding

    /// <summary>Show a material: resolve its shader, build the graph, fill Details / Stats / Code.</summary>
    public void ShowMaterial(MaterialBinding binding, ShaderCacheReader? cache, ShaderPermutationIndex? perms)
    {
        MaterialGraphShaderInfo? info = null;
        DxbcShader? vs = null, ps = null;
        string note = cache is null ? "the shader cache is not available" : "";
        if (cache is not null)
        {
            try { info = MaterialGraphShaderResolver.Resolve(cache, perms, binding, out note, out vs, out ps, ShaderPreviewViewModel.ResolveTextureTarget); }
            catch (Exception ex) { note = "shader resolution failed: " + ex.Message; info = null; }
        }
        Apply(MaterialGraphBuilder.Build(binding, info), binding, vs, ps, note);
    }

    /// <summary>Show a loaded shader on its own (no material), reflecting the permutation that is loaded.</summary>
    public void ShowShader(string shaderPath, DxbcShader? vs, DxbcShader? ps, ShaderPermutationIndex? perms,
        string pixelPermutation, string vertexPermutation)
    {
        IReadOnlyDictionary<string, string>? feat = null;
        IReadOnlyDictionary<string, bool>? sw = null;
        IReadOnlyDictionary<string, float[]>? pd = null;
        IReadOnlyDictionary<string, string>? td = null;
        string def = ShaderPermutationIndex.DefinitionPathForCacheShader(shaderPath);
        if (perms is not null)
        {
            if (perms.TryGetShaderDefs(def, out var f, out var s)) { feat = f; sw = s; }
            if (perms.TryGetParameterDefaults(def, out var p)) pd = p;
            if (perms.TryGetTextureDefaults(def, out var t)) td = t;
        }
        Func<string, string?>? rule = vs is null || ps is null ? null : sampler => ShaderPreviewViewModel.ResolveTextureTarget(sampler, ps, vs);
        var info = MaterialGraphShaderInfo.FromReflection(def, vs, ps, sw, feat, pd, td, rule,
            pixelPermutation: pixelPermutation, vertexPermutation: vertexPermutation);
        Apply(MaterialGraphBuilder.BuildForShader(def, info), null, vs, ps, "");
    }

    private void Apply(MaterialGraph graph, MaterialBinding? binding, DxbcShader? vs, DxbcShader? ps, string note)
    {
        SelectedNodeId = null;
        SearchStatus = "";
        SourceNote = "";
        EmptyText = DefaultEmptyText;
        Graph = graph;
        Breadcrumb = $"{LeafOf(graph.MaterialName)} > Material Graph";
        OnPropertyChanged(nameof(Summary));
        RefreshDetails();

        ShaderStats.Clear();
        foreach (var r in MaterialGraphStats.Shader(vs, ps)) ShaderStats.Add(r);
        MaterialStats.Clear();
        foreach (var (label, value) in MaterialGraphStats.Material(graph))
            MaterialStats.Add(new MaterialStatRow { Label = label, Value = value });
        StatsNote = vs is null && ps is null
            ? "No compiled permutation is resolved, so no shader figures can be shown" + (note.Length > 0 ? ": " + note : ".")
            : "Figures come from the permutation this material resolves to (the same one the preview loads).";

        FillCode(vs, ps, note);
    }

    // ---- Shader Code tab: disassembled only when the tab is shown, on a worker thread ----
    private DxbcShader? _vs, _ps;
    private int _codeVersion;
    private bool _codeStale = true;

    /// <summary>True while the Shader Code tab is the selected one (bound two-way to the TabItem).</summary>
    [ObservableProperty] private bool _codeTabActive;

    partial void OnCodeTabActiveChanged(bool value) { if (value) EnsureCode(); }

    private void FillCode(DxbcShader? vs, DxbcShader? ps, string note)
    {
        _vs = vs; _ps = ps;
        Interlocked.Increment(ref _codeVersion);
        _codeStale = true;
        PixelCode = VertexCode = "";
        if (vs is null && ps is null)
        {
            CodeNote = "No shader code to show" + (note.Length > 0 ? ": " + note + "." : ".");
            _codeStale = false;
            return;
        }
        CodeNote = "The listing is produced when this tab is shown.";
        if (CodeTabActive) EnsureCode();
    }

    private void EnsureCode()
    {
        if (!_codeStale || (_vs is null && _ps is null)) return;
        _codeStale = false;
        var vs = _vs; var ps = _ps;
        int version = Interlocked.Increment(ref _codeVersion);
        CodeNote = "Disassembling...";
        _ = Task.Run(() =>
        {
            var notes = new List<string>
            {
                "Riot ships the DX11 shaders as compiled DXBC bytecode only - there is no HLSL source for them in the game files, "
                + "so this is the disassembly (D3DDisassemble) of the permutation this material resolves to.",
            };
            string p = Disassemble(ps, "pixel", notes);
            string v = Disassemble(vs, "vertex", notes);
            return (p, v, string.Join(" ", notes));
        }).ContinueWith(t =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (version != Volatile.Read(ref _codeVersion) || t.IsFaulted) return;
                PixelCode = t.Result.p;
                VertexCode = t.Result.v;
                CodeNote = t.Result.Item3;
            });
        }, TaskScheduler.Default);
    }

    private static string Disassemble(DxbcShader? sh, string stage, List<string> notes)
    {
        if (sh is null) { notes.Add($"No {stage} stage resolved."); return $"// no {stage} stage"; }
        if (!DxbcDisassembler.TryDisassemble(sh.Bytecode, out var text, out var err))
        {
            notes.Add($"The {stage} stage could not be disassembled ({err}).");
            return $"// {err}";
        }
        const int MaxChars = 150_000;
        if (text.Length > MaxChars)
        {
            notes.Add($"The {stage} listing is {text.Length:n0} characters; the first {MaxChars:n0} are shown.");
            text = text[..MaxChars];
        }
        return text;
    }

    private static string LeafOf(string name)
    {
        int cut = name.LastIndexOfAny(new[] { '/', '\\' });
        return cut >= 0 && cut < name.Length - 1 ? name[(cut + 1)..] : name;
    }

    // ============================================================================== selection / details

    partial void OnSelectedNodeIdChanged(string? value) => RefreshDetails();

    private void RefreshDetails()
    {
        DetailGroups.Clear();
        var g = Graph;
        if (g is null)
        {
            DetailTitle = "Material Graph";
            DetailSubtitle = "";
            DetailNotes = "";
            return;
        }

        var node = SelectedNodeId is null ? null : g.Find(SelectedNodeId);
        IReadOnlyList<GraphDetail> items;
        if (node is null)
        {
            // nothing selected: the material itself, like Unreal's Details panel for the material
            var output = g.OutputNode;
            DetailTitle = LeafOf(g.MaterialName);
            DetailSubtitle = g.ShaderName.Length > 0 ? g.ShaderName : "(no renderShader)";
            items = output?.Details ?? Array.Empty<GraphDetail>();
            DetailNotes = string.Join("\n", g.Notes);
        }
        else
        {
            DetailTitle = node.Title;
            DetailSubtitle = node.Kind + (node.State == GraphNodeState.Unused ? " - not used by this permutation"
                : node.State == GraphNodeState.ShaderDefault ? " - shader default" : "");
            items = node.Details;
            DetailNotes = "";
        }

        foreach (var grp in items.GroupBy(i => i.Category))
            DetailGroups.Add(new DetailGroup { Category = grp.Key, Items = grp.ToList() });
    }

    // ============================================================================== commands

    [RelayCommand]
    private void Home() => FitRequested?.Invoke();

    private int _searchCursor = -1;
    private string _searchLast = "";

    partial void OnSearchTextChanged(string value)
    {
        _searchCursor = -1;
        UpdateSearchStatus();
    }

    private void UpdateSearchStatus()
    {
        if (Graph is null || string.IsNullOrWhiteSpace(SearchText)) { SearchStatus = ""; return; }
        int n = Graph.Search(SearchText).Count();
        SearchStatus = n == 0 ? "no match" : n == 1 ? "1 match" : $"{n} matches";
    }

    /// <summary>Enter in the search box: focus the first match, then the next on each press.</summary>
    [RelayCommand]
    private void SearchNext()
    {
        if (Graph is null || string.IsNullOrWhiteSpace(SearchText)) return;
        var hits = Graph.Search(SearchText).ToList();
        UpdateSearchStatus();
        if (hits.Count == 0) return;
        _searchCursor = (_searchCursor + 1) % hits.Count;
        _searchLast = SearchText;
        SelectedNodeId = hits[_searchCursor].Id;
        FocusRequested?.Invoke(hits[_searchCursor].Id);
        SearchStatus = $"{_searchCursor + 1} of {hits.Count}";
    }
}
