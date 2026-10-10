using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;

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

    private readonly Dictionary<string, SgBase> _graphBases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, SgCompiled> _goodBySignature = new();
    private readonly Dictionary<ulong, ReyEngine.Core.Decoding.TextureImage> _graphTextures = new();
    private string _graphLog = "";
    private bool _closeArmed;

    /// <summary>The line the window shows about what the preview is drawing with.</summary>
    [ObservableProperty] private string _graphPreviewNote = "";

    private void InitShaderGraph(Func<string?>? projectRoot)
    {
        ShaderGraph = new ShaderGraphViewModel(projectRoot ?? (() => null)) { BaseResolver = ResolveShaderGraphBase };
        MaterialGraph.AttachShaderGraph(ShaderGraph);
        ShaderGraph.BuildCompleted += OnShaderGraphBuilt;
        ShaderGraph.PropertyChanged += (_, e) =>
        {
            // a pending "press again to discard" is only good for the graph state it was armed in
            if (e.PropertyName is nameof(ShaderGraphViewModel.IsOpen) or nameof(ShaderGraphViewModel.IsDirty) or nameof(ShaderGraphViewModel.GraphName)) _closeArmed = false;
            if (e.PropertyName != nameof(ShaderGraphViewModel.IsOpen)) return;
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

    /// <summary>"New Shader Graph from this shader": a graph on the picked material's shader.</summary>
    [RelayCommand]
    private void NewShaderGraph()
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
        if (!ShaderGraph.NewFromBase(shader, out string error)) { Status = error; HasError = true; return; }
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

    /// <summary>Called by Load with the Riot pixel shader just read for <paramref name="shaderFull"/>: returns the Shader Graph's compiled
    /// pixel shader for the same input signature when one exists (and the preview is on), else Riot's own.</summary>
    private DxbcShader ApplyShaderGraphPixelOverride(string shaderFull, DxbcShader riotPixel)
    {
        if (!ShaderGraph.IsOpen || ShaderGraph.Base is not { } b) { GraphPreviewNote = ""; return riotPixel; }
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
            ? $"Preview: the Shader Graph '{ShaderGraph.GraphName}' - LAST GOOD compile (the graph has errors now). Shipped when a material is assigned to it. The map viewport still draws Riot's shader."
            : $"Preview: the Shader Graph '{ShaderGraph.GraphName}' replaces Riot's pixel shader (signature #{sig.Index}). Shipped when a material is assigned to it. The map viewport still draws Riot's shader.";
        return good.Shader;
    }
}
