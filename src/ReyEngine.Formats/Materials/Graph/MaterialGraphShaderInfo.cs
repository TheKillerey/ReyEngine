using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Materials.Graph;

/// <summary>One texture input a compiled shader declares (a DXBC <c>Texture</c> bind point).</summary>
public sealed record ShaderTextureInput(string Name, uint BindPoint, string Dimension, string Stage)
{
    /// <summary>The name without Riot's <c>__TX</c> suffix, which is how a material and a person name it.</summary>
    public string DisplayName => Name.EndsWith("__TX", StringComparison.OrdinalIgnoreCase) ? Name[..^4] : Name;
}

/// <summary>One constant a compiled shader declares in a cbuffer.</summary>
public sealed record ShaderConstantInput(string Name, string Buffer, string TypeName, bool IsUsed, string Stage);

/// <summary>
/// M829: what the graph needs to know about the shader a material names - the texture inputs, constants and
/// switch defaults - without a GPU or a window. Built from the already-loaded <see cref="DxbcShader"/>s and the
/// shaders.bin definition, so the graph and the D3D11 preview describe the SAME permutation.
///
/// <para>A material whose shader could not be loaded still gets a graph: pass <c>null</c> to
/// <see cref="MaterialGraphBuilder.Build"/> and every wire lands on a pin named after the thing it carries,
/// with a note that no reflection was available. Absent is never turned into a guess.</para>
/// </summary>
public sealed class MaterialGraphShaderInfo
{
    public string ShaderName { get; init; } = "";
    public IReadOnlyList<ShaderTextureInput> Textures { get; init; } = Array.Empty<ShaderTextureInput>();
    public IReadOnlyList<ShaderConstantInput> Constants { get; init; } = Array.Empty<ShaderConstantInput>();

    /// <summary>shaders.bin staticSwitches: name -> onByDefault. Empty when the definition was unavailable.</summary>
    public IReadOnlyDictionary<string, bool> SwitchDefaults { get; init; } = new Dictionary<string, bool>();

    /// <summary>True when <see cref="SwitchDefaults"/> came from a real definition (so an unlisted switch is meaningful).</summary>
    public bool HasSwitchDefinition { get; init; }

    public IReadOnlyDictionary<string, string> FeatureDefines { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, float[]> ParameterDefaults { get; init; } = new Dictionary<string, float[]>();
    public IReadOnlyDictionary<string, string> TextureDefaults { get; init; } = new Dictionary<string, string>();

    /// <summary>"blob #12, key 0x..." for each stage, or empty.</summary>
    public string PixelPermutation { get; init; } = "";
    public string VertexPermutation { get; init; } = "";
    public DxbcStats? PixelStats { get; init; }
    public DxbcStats? VertexStats { get; init; }

    /// <summary>Which declared texture a material sampler feeds. Supplied by the caller so the graph uses the
    /// SAME rule the D3D11 binding uses (<c>Dx11CharacterScene.ResolveTextureTarget</c>); the default is the
    /// same rule over the names alone.</summary>
    public Func<string, string?>? ResolveTexture { get; init; }

    public string? TargetFor(string samplerName)
    {
        if (ResolveTexture is not null) return ResolveTexture(samplerName);
        string exact = samplerName + "__TX";
        foreach (var t in Textures)
            if (t.Name.Equals(exact, StringComparison.OrdinalIgnoreCase)
                || t.Name.Equals(samplerName, StringComparison.OrdinalIgnoreCase))
                return t.Name;
        if (!samplerName.Equals("texture", StringComparison.OrdinalIgnoreCase)) return null;
        var candidates = Textures
            .Where(t => t.Stage == "PS" && !t.Name.EndsWith("_SharedTexture", StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.FirstOrDefault(t => t.Name.Contains("Diffuse", StringComparison.OrdinalIgnoreCase))?.Name
               ?? candidates.FirstOrDefault()?.Name;
    }

    /// <summary>Pixel stage first, then vertex-only textures, each once by name.</summary>
    public static MaterialGraphShaderInfo FromReflection(string shaderName, DxbcShader? vs, DxbcShader? ps,
        IReadOnlyDictionary<string, bool>? switchDefaults = null,
        IReadOnlyDictionary<string, string>? featureDefines = null,
        IReadOnlyDictionary<string, float[]>? parameterDefaults = null,
        IReadOnlyDictionary<string, string>? textureDefaults = null,
        Func<string, string?>? resolveTexture = null,
        string pixelPermutation = "", string vertexPermutation = "")
    {
        var textures = new List<ShaderTextureInput>();
        var constants = new List<ShaderConstantInput>();
        foreach (var (stage, sh) in new[] { ("PS", ps), ("VS", vs) })
        {
            if (sh is null) continue;
            foreach (var t in sh.Textures.OrderBy(t => t.BindPoint))
                if (textures.All(x => !x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)))
                    textures.Add(new ShaderTextureInput(t.Name, t.BindPoint, t.DimensionName, stage));
            foreach (var cb in sh.ConstantBuffers)
                foreach (var v in cb.Variables)
                {
                    int at = constants.FindIndex(c => c.Name.Equals(v.Name, StringComparison.OrdinalIgnoreCase));
                    if (at < 0) constants.Add(new ShaderConstantInput(v.Name, cb.Name, v.TypeName, v.IsUsed, stage));
                    else if (!constants[at].IsUsed && v.IsUsed)
                        constants[at] = constants[at] with { IsUsed = true };
                }
        }

        return new MaterialGraphShaderInfo
        {
            ShaderName = shaderName,
            Textures = textures,
            Constants = constants,
            SwitchDefaults = switchDefaults ?? new Dictionary<string, bool>(),
            HasSwitchDefinition = switchDefaults is { Count: > 0 },
            FeatureDefines = featureDefines ?? new Dictionary<string, string>(),
            ParameterDefaults = parameterDefaults ?? new Dictionary<string, float[]>(),
            TextureDefaults = textureDefaults ?? new Dictionary<string, string>(),
            ResolveTexture = resolveTexture,
            PixelPermutation = pixelPermutation,
            VertexPermutation = vertexPermutation,
            PixelStats = ps is null ? null : DxbcStats.Read(ps),
            VertexStats = vs is null ? null : DxbcStats.Read(vs),
        };
    }
}
