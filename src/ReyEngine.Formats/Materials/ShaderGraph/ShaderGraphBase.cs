using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Materials.ShaderGraph;

/// <summary>A texture input the base shader declares. The client binds <c>&lt;Name&gt;__TX</c> and its sampler <c>&lt;Name&gt;__SMP</c> BY NAME.</summary>
public sealed record SgTextureDecl(string Name)
{
    public string Resource => Name + "__TX";
    public string Sampler => Name + "__SMP";
}

/// <summary>A parameter the base shader declares in shaders.bin (<see cref="Components"/> from the cooked reflection, 4 when no blob names it).</summary>
public sealed record SgParameterDecl(string Name, int Components, float[] Default);

/// <summary>One variable of an engine cbuffer, at the offset every cooked blob has it.</summary>
public sealed record SgEngineVar(string Name, int Offset, int Size, string TypeName);

/// <summary>An engine constant buffer (<c>PerFramePixelCB</c>): one fixed layout across the cache, so a generated shader can redeclare it exactly.</summary>
public sealed record SgEngineBuffer(string Name, int Size, IReadOnlyList<SgEngineVar> Variables)
{
    public SgEngineVar? Var(string name) => Variables.FirstOrDefault(v => v.Name == name);
}

/// <summary>
/// One pixel-stage input/output signature of the base shader. The paired Riot vertex shader writes exactly this, so a generated pixel
/// shader must declare exactly this (D3D links by semantic, register and mask). <see cref="Permutations"/> are the cooked colour
/// permutations that use it - what a shipping step must add twin keys for.
/// </summary>
public sealed class SgSignature
{
    public required int Index { get; init; }
    public required IReadOnlyList<DxbcSignatureElement> Inputs { get; init; }
    public required IReadOnlyList<DxbcSignatureElement> Outputs { get; init; }
    public required IReadOnlyList<ShaderPermutation> Permutations { get; init; }

    /// <summary>"SV_Position0:xyzw@r0 TEXCOORD0:xyzw@r1 ..." - the identity two signatures are grouped by.</summary>
    public string Text => Describe(Inputs) + " => " + Describe(Outputs);

    public static string Describe(IEnumerable<DxbcSignatureElement> els) =>
        string.Join(" ", els.Select(e => $"{e.FullSemantic}:{e.MaskString}@r{e.Register}/sv{e.SystemValueType}/t{e.ComponentType}"));

    /// <summary>Everything D3D linkage compares (semantic, index, system value, type, register, mask). The read-write mask is usage, not linkage.</summary>
    public static bool SameElement(DxbcSignatureElement a, DxbcSignatureElement b) =>
        a.Semantic == b.Semantic && a.Index == b.Index && a.SystemValueType == b.SystemValueType
        && a.ComponentType == b.ComponentType && a.Register == b.Register && a.Mask == b.Mask;

    public static bool SameList(IReadOnlyList<DxbcSignatureElement> a, IReadOnlyList<DxbcSignatureElement> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!SameElement(a[i], b[i])) return false;
        return true;
    }

    public bool MatchesInputs(IReadOnlyList<DxbcSignatureElement> inputs) => SameList(Inputs, inputs);
    public bool Matches(DxbcShader ps) => SameList(Inputs, ps.Inputs) && SameList(Outputs, ps.Outputs);

    public DxbcSignatureElement? Input(string semantic, int index) =>
        Inputs.FirstOrDefault(e => e.Semantic.Equals(semantic, StringComparison.OrdinalIgnoreCase) && e.Index == index);
}

/// <summary>
/// M832: everything a Shader Graph needs to know about its base Riot shader: the declared texture and parameter names (graph inputs are
/// limited to these - the preview must not be kinder than the game), the pixel input signatures, the UV0 interpolant, and the engine
/// cbuffer layout. Built from the shader cache and shaders.bin by <see cref="ShaderGraphBaseResolver"/>; tests build it by hand.
/// </summary>
public sealed class SgBase
{
    /// <summary>shaders.bin spelling: <c>Shaders/StaticMesh/DefaultEnv_Flat</c>.</summary>
    public required string Shader { get; init; }
    public IReadOnlyList<SgTextureDecl> Textures { get; init; } = Array.Empty<SgTextureDecl>();
    public IReadOnlyList<SgParameterDecl> Parameters { get; init; } = Array.Empty<SgParameterDecl>();
    public IReadOnlyList<SgSignature> Signatures { get; init; } = Array.Empty<SgSignature>();

    /// <summary>The TEXCOORD index that carries UV0 (verified per base: for DefaultEnv_Flat it is TEXCOORD1, xy, in all 16 colour signatures).</summary>
    public int UvSet { get; init; } = 1;

    /// <summary>The shadow-map pass permutations (GENERATE_SHADOW_MAP=1): Riot's own blobs, never a graph's. A shipping step that adds twin keys
    /// for the colour permutations needs these too (a twin of a shadow key points at the Riot blob its source key does).</summary>
    public IReadOnlyList<ShaderPermutation> ShadowPermutations { get; init; } = Array.Empty<ShaderPermutation>();

    /// <summary>The pixel TOC this base was read from and how many blobs it declares - what a patch of that TOC starts from.</summary>
    public string PixelTocPath { get; init; } = "";
    public uint PixelBlobCount { get; init; }

    /// <summary>The per-frame pixel cbuffer, when the blobs declare one (the source of Time and the output encode flag).</summary>
    public SgEngineBuffer? Frame { get; init; }

    public bool HasVertexColor => Signatures.Any(s => s.Inputs.Any(e => e.Semantic.Equals("COLOR", StringComparison.OrdinalIgnoreCase)));
    public bool HasTime => Frame?.Var("TIME") is not null;

    public SgTextureDecl? Texture(string name) => Textures.FirstOrDefault(t => t.Name.Equals(name, StringComparison.Ordinal));
    public SgParameterDecl? Parameter(string name) => Parameters.FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));

    /// <summary>The signature whose pixel inputs equal those of a loaded Riot pixel shader, or null.</summary>
    public SgSignature? SignatureOf(DxbcShader riotPixel) => Signatures.FirstOrDefault(s => s.Matches(riotPixel));

    /// <summary>The bases this build has verified. Others are refused rather than guessed at.</summary>
    public static bool IsSupported(string? renderShader) =>
        !string.IsNullOrWhiteSpace(renderShader) && SupportedUvSets.ContainsKey(Normalize(renderShader));

    public static string Normalize(string shader) => shader.Trim().Trim('/').Replace('\\', '/').ToLowerInvariant();

    internal static readonly Dictionary<string, int> SupportedUvSets = new(StringComparer.Ordinal)
    {
        // TEXCOORD1.xy = UV0 in all 16 colour signatures (the M832 probe, ISGN census of the 16.20 cache)
        ["shaders/staticmesh/defaultenv_flat"] = 1,
    };
}
