using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Decoding;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M832: the Shader Graph half of the Material Graph window - opening and creating graphs, and swapping the graph's compiled pixel
/// shader into the D3D11 preview in place of Riot's pixel shader of the open material (the Riot VERTEX shader stays). Preview only: nothing
/// here writes a bin or the shader cache.
/// </summary>
public sealed partial class ShaderPreviewViewModel
{
    /// <summary>The Shader Graph editor (a graph is "open" while one is loaded; the window then shows it instead of the material graph).</summary>
    public ShaderGraphViewModel ShaderGraph { get; private set; } = null!;

    public bool ShaderGraphActive => ShaderGraph.IsOpen;
    public bool MaterialGraphActive => !ShaderGraph.IsOpen;

    /// <summary>M837: the toolbar's "N nodes, M wires" - of the Shader Graph while one is open, else of the material graph.</summary>
    public string ToolbarSummary => ShaderGraph.IsOpen ? ShaderGraph.Summary : MaterialGraph.Summary;

    private readonly Dictionary<string, SgBase> _graphBases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, SgCompiled> _goodBySignature = new();
    private readonly Dictionary<ulong, ReyEngine.Core.Decoding.TextureImage> _graphTextures = new();
    private string _graphLog = "";
    private bool _closeArmed;

    /// <summary>M835: the compiled graph of the picked material (REY_GRAPH) while it is drawn with the viewport service rather than the open editor; else null.</summary>
    private SgViewportOverride? _materialGraph;

    /// <summary>The line the window shows about what the preview is drawing with.</summary>
    [ObservableProperty] private string _graphPreviewNote = "";

    private Func<string?>? _sgProjectRoot;

    private void InitShaderGraph(Func<string?>? projectRoot)
    {
        _sgProjectRoot = projectRoot;
        ShaderGraph = new ShaderGraphViewModel(projectRoot ?? (() => null))
        {
            BaseResolver = ResolveShaderGraphBase,
            TextureExists = path => { try { return _readAsset?.Invoke(HashAlgorithms.WadPath(path.ToLowerInvariant())) is { Length: > 0 }; } catch { return true; } },
        };
        MaterialGraph.AttachShaderGraph(ShaderGraph);
        MaterialGraph.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MaterialGraphViewModel.Summary)) OnPropertyChanged(nameof(ToolbarSummary)); };
        ShaderGraph.BuildCompleted += OnShaderGraphBuilt;
        ShaderGraph.PropertyChanged += (_, e) =>
        {
            // a pending "press again to discard" is only good for the graph state it was armed in
            if (e.PropertyName is nameof(ShaderGraphViewModel.IsOpen) or nameof(ShaderGraphViewModel.IsDirty) or nameof(ShaderGraphViewModel.GraphName)) _closeArmed = false;
            if (e.PropertyName is nameof(ShaderGraphViewModel.Summary)) OnPropertyChanged(nameof(ToolbarSummary));
            if (e.PropertyName != nameof(ShaderGraphViewModel.IsOpen)) return;
            OnPropertyChanged(nameof(ToolbarSummary));
            OnPropertyChanged(nameof(ShaderGraphActive));
            OnPropertyChanged(nameof(MaterialGraphActive));
            OnPropertyChanged(nameof(CanNewShaderGraph));
        };
    }

    /// <summary>The base shader of a graph: read from the shader cache (and shaders.bin) once per shader.</summary>
    private (SgBase? Base, string Error) ResolveShaderGraphBase(string shader)
    {
        if (_cache is null) return (null, "the shader cache is not available");
        if (_graphBases.TryGetValue(shader, out var known)) return (known, "");
        var b = ShaderGraphBaseResolver.Resolve(_cache, _perms, shader, out string error, out string note);
        if (b is null) return (null, error);
        _graphBases[shader] = b;
        if (note.Length > 0) Status = note;
        return (b, "");
    }

    /// <summary>True when the picked material's shader is a base this build supports.</summary>
    public bool CanNewShaderGraph => SelectedMaterial?.Binding.RenderShader is { } s && SgBase.IsSupported(s) && _cache is not null;

    /// <summary>M835: the templates "New Shader Graph" offers.</summary>
    public IReadOnlyList<SgTemplate> ShaderGraphTemplates => SgTemplates.All;

    [RelayCommand]
    private void NewShaderGraphFromTemplate(SgTemplate? template)
    {
        if (template is null) return;
        NewShaderGraphCore(template.Id);
    }

    /// <summary>"New Shader Graph from this shader": a graph on the picked material's shader.</summary>
    [RelayCommand]
    private void NewShaderGraph() => NewShaderGraphCore(null);

    private void NewShaderGraphCore(string? templateId)
    {
        string? shader = SelectedMaterial?.Binding.RenderShader;
        if (string.IsNullOrWhiteSpace(shader)) { Status = "Pick a material whose shader is a supported base first."; return; }
        if (!SgBase.IsSupported(shader))
        {
            Status = $"'{shader}' is not a base shader a Shader Graph supports yet (supported: DefaultEnv_Flat).";
            HasError = true;
            return;
        }
        if (ShaderGraph.IsOpen && ShaderGraph.IsDirty && !_closeArmed) { _closeArmed = true; Status = "The open Shader Graph has unsaved edits. Press again to start a new graph and discard them, or save first (Ctrl+S)."; return; }
        _closeArmed = false;
        if (!ShaderGraph.NewFromTemplate(shader, templateId, out string error)) { Status = error; HasError = true; return; }
        HasError = false;
        AfterGraphOpened();
    }

    /// <summary>"Open Shader Graph": a saved graph of the project.</summary>
    [RelayCommand]
    private void OpenShaderGraph(SgFileRow? row)
    {
        if (row is null) return;
        if (ShaderGraph.IsOpen && ShaderGraph.IsDirty && !_closeArmed) { _closeArmed = true; Status = "The open Shader Graph has unsaved edits. Open again to discard them, or save first (Ctrl+S)."; return; }
        _closeArmed = false;
        if (!ShaderGraph.Open(row.Path, out string error)) { Status = error; HasError = true; return; }
        HasError = false;
        AfterGraphOpened();
    }

    [RelayCommand]
    private void RefreshShaderGraphs() => ShaderGraph.RefreshSavedGraphs();

    /// <summary>Back to the material graph.</summary>
    [RelayCommand]
    private void CloseShaderGraph()
    {
        if (!ShaderGraph.IsOpen) return;
        if (ShaderGraph.IsDirty && !_closeArmed) { _closeArmed = true; Status = "The Shader Graph has unsaved edits. Press Close again to discard them, or save first (Ctrl+S)."; return; }
        _closeArmed = false;
        ShaderGraph.Close();
        _graphTextures.Clear();
        _goodBySignature.Clear();
        _graphLog = "";
        GraphPreviewNote = "";
        AppendLog();
        // Riot's own pixel shader again
        if (IsLoaded && SelectedMaterial is { HasShader: true } && SceneSubmeshes.Count == 0) ApplyMaterial();
    }

    private void AfterGraphOpened()
    {
        _graphTextures.Clear();
        _goodBySignature.Clear();
        _graphLog = "";
        GraphPreviewNote = "Compiling the Shader Graph...";
        OnPropertyChanged(nameof(ShaderGraphActive));
        OnPropertyChanged(nameof(MaterialGraphActive));
    }

    private void OnShaderGraphBuilt(SgBuild build)
    {
        _graphLog = ShaderGraph.BuildLog();
        // per signature: a compile that worked replaces what the preview uses; an analysis error keeps the last good one
        if (!build.Analysis.HasErrors) _goodBySignature.Clear();
        foreach (var c in build.Compiled) if (c.Ok) _goodBySignature[c.Signature.Index] = c;
        AppendLog();
        if (_disposed) return;
        // what the note says when nothing re-draws the preview below (the draw itself sets it again from what it used)
        GraphPreviewNote = !ShaderGraph.PreviewEnabled ? "Preview of the Shader Graph is off: the viewport shows Riot's shader."
            : build.Analysis.HasErrors && _goodBySignature.Count == 0 ? "The Shader Graph has errors and has not compiled yet: see the diagnostics."
            : build.Analysis.HasErrors ? "The Shader Graph has errors: the preview keeps the last good compile when one is drawn."
            : !build.Ok ? "The Shader Graph does not compile for every pixel input signature (see the Log tab)."
            : IsLoaded && SelectedMaterial is { HasShader: true } && SceneSubmeshes.Count == 0 ? "Compiled: updating the preview..."
            : "Compiled. Pick a material that uses " + ShaderGraph.BaseShaderName + " to see it in the preview.";
        // re-draw the open material with the new pixel shader (never over a loaded scene, like a material edit)
        if (!IsLoaded || SelectedMaterial is not { HasShader: true } || SceneSubmeshes.Count > 0 || _cache is null) return;
        ApplyMaterial();
    }

    /// <summary>M837: the note the window shows when the previewed material is not assigned to the open Shader Graph and the preview fills in the graph's defaults.</summary>
    public const string AuthoringDefaultsNote = "preview uses the graph's defaults (material not assigned)";

    /// <summary>M837: whether the previewed material is assigned to the Shader Graph that is OPEN (its REY_GRAPH number is this graph's on this base). A material that is not is previewed with the graph's defaults for the
    /// custom parameters and textures it lacks; an assigned one keeps the game-accurate zero / no texture. False when no graph is open.</summary>
    public bool IsAssignedToOpenGraph(MaterialBinding b)
    {
        if (!ShaderGraph.IsOpen || ShaderGraph.Base is not { } gb) return false;
        if (!b.Macros.TryGetValue(SgShip.Macro, out string? value) || !SgShipValidator.TryParseNumber(value, out int n)) return false;
        try
        {
            string? root = _sgProjectRoot?.Invoke();
            if (string.IsNullOrEmpty(root) || ShaderGraph.FilePath is not { Length: > 0 } file) return false;
            var entry = ShaderGraphRegistry.FindByFile(root, ShaderGraphRegistry.StemOf(file));
            return entry is not null && entry.Number == n && SgBase.Normalize(entry.Base) == SgBase.Normalize(gb.Shader);
        }
        catch { return false; }
    }

    /// <summary>The custom textures with a default image the preview should bind when nothing else does: the open graph's, else the picked material's graph's.</summary>
    private IEnumerable<(string Target, string Path)> GraphDefaultTextures()
    {
        if (ShaderGraph.IsOpen && ShaderGraph.PreviewEnabled && ShaderGraph.Document is { } doc)
            return doc.Textures.Where(t => t.DefaultPath.Length > 0).Select(t => (t.Resource, t.DefaultPath.Replace('\\', '/').ToLowerInvariant())).ToList();
        return _materialGraph?.DefaultTextures ?? Array.Empty<(string, string)>();
    }

    /// <summary>The graph's custom texture resources (<c>Name__TX</c>) of the graph being drawn: the open editor's, else the picked material's.</summary>
    private IEnumerable<string> GraphCustomTextures()
    {
        if (ShaderGraph.IsOpen && ShaderGraph.PreviewEnabled && ShaderGraph.Document is { } doc) return doc.Textures.Select(t => t.Resource).ToList();
        return _materialGraph?.DefaultTextures.Select(t => t.Target).ToList() ?? new List<string>();
    }

    /// <summary>The graph's custom parameters (name, width) of the graph being drawn.</summary>
    private IEnumerable<(string Name, int Width)> GraphCustomParameters()
    {
        if (ShaderGraph.IsOpen && ShaderGraph.PreviewEnabled && ShaderGraph.Document is { } doc) return doc.Params.Select(p => (p.Name, Math.Clamp(p.Components, 1, 4))).ToList();
        return _materialGraph?.CustomParameters.Select(n => (n, 4)).ToList() ?? new List<(string, int)>();
    }

    private void ApplyGraphTextureDefaults()
    {
        if (_loadingMaterial) return;   // a material is the subject: what it does not carry stays unbound, as in the game
        var declared = TextureSlots.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (target, path) in GraphDefaultTextures())
            if (declared.Contains(target)) BindGraphTexture(target, path, out _);
    }

    /// <summary>Decode a graph's default image and bind it to a texture slot of the previewed shader.</summary>
    private bool BindGraphTexture(string target, string path, out string why)
    {
        why = "";
        if (_readAsset is null) { why = "no asset mounts"; return false; }
        try
        {
            ulong chunk = HashAlgorithms.WadPath(path);
            TextureImage? img = null;
            if (!(ShaderGraph.IsOpen && _graphTextures.TryGetValue(chunk, out img)))
            {
                var data = _readAsset(chunk);
                if (data is null || data.Length == 0) { why = "NOT FOUND"; return false; }
                img = TextureDecoder.Decode(data);
                if (ShaderGraph.IsOpen) { if (_graphTextures.Count > 24) _graphTextures.Clear(); _graphTextures[chunk] = img; }
            }
            _renderer.SetTexture(target, img!.Rgba, img.Width, img.Height);
            return true;
        }
        catch (Exception ex) { why = ex.Message; return false; }
    }

    /// <summary>Called by Load with the Riot pixel shader just read for <paramref name="shaderFull"/>: returns the Shader Graph's compiled
    /// pixel shader for the same input signature when one exists (and the preview is on), else Riot's own.</summary>
    private DxbcShader ApplyShaderGraphPixelOverride(string shaderFull, DxbcShader riotPixel)
    {
        _materialGraph = null;
        if (!ShaderGraph.IsOpen || ShaderGraph.Base is not { } b)
        {
            GraphPreviewNote = "";
            // M835: a material that names a graph is drawn with it, with or without the editor open - the same service the map viewport uses
            if (_loadingMaterial && SelectedMaterial?.Binding is { } mb && mb.Macros.ContainsKey(SgShip.Macro) && _cache is not null && !string.IsNullOrEmpty(mb.RenderShader))
            {
                var ov = ShaderGraphViewport.TryOverride(mb.RenderShader!, mb.Macros, riotPixel, _cache, _perms, out string why);
                if (ov is not null)
                {
                    _materialGraph = ov;
                    GraphPreviewNote = $"Preview: this material's Shader Graph '{ov.GraphName}' (#{ov.Number}) replaces Riot's pixel shader, as in the viewport.";
                    return ov.Shader;
                }
                if (why.Length > 0) GraphPreviewNote = why;
            }
            return riotPixel;
        }
        if (!ShaderGraph.PreviewEnabled) return riotPixel;

        string shaderName = ShaderPermutationIndex.DefinitionPathForCacheShader(shaderFull);
        if (!string.Equals(shaderName, SgBase.Normalize(b.Shader), StringComparison.OrdinalIgnoreCase))
        {
            GraphPreviewNote = $"The open material uses {shaderName}, not {b.Shader}: the graph is not drawn on it.";
            return riotPixel;
        }
        var sig = b.SignatureOf(riotPixel);
        if (sig is null)
        {
            GraphPreviewNote = "This permutation's pixel input signature is not one the graph was built for: Riot's shader is drawn.";
            return riotPixel;
        }
        if (!_goodBySignature.TryGetValue(sig.Index, out var good) || good.Shader is null)
        {
            GraphPreviewNote = ShaderGraph.LastBuild is null
                ? "The Shader Graph is still compiling: Riot's shader is drawn until it is ready."
                : $"The Shader Graph does not compile for pixel input signature #{sig.Index} (see the Log tab): Riot's shader is drawn.";
            return riotPixel;
        }
        ShaderGraph.SignatureIndex = sig.Index;
        bool stale = ShaderGraph.LastBuild is { } last && (last.Analysis.HasErrors || last.For(sig) is { Ok: false });
        GraphPreviewNote = stale
            ? $"Preview: the Shader Graph '{ShaderGraph.GraphName}' - LAST GOOD compile (the graph has errors now). Shipped when a material is assigned to it. The map viewport draws the SAVED graph of each assigned material."
            : $"Preview: the Shader Graph '{ShaderGraph.GraphName}' replaces Riot's pixel shader (signature #{sig.Index}). Shipped when a material is assigned to it. A material assigned to it draws it in the map viewport too (live as you edit).";
        return good.Shader;
    }
}
