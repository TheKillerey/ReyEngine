using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Materials.Graph;

/// <summary>
/// M829: resolves the shader a material names to the cooked permutation the engine would pick (the SAME
/// resolution the D3D11 preview's "Load this material" does - ShaderCacheReader.ResolvePermutation over the
/// material's switches and macros plus the shader's shaders.bin defaults) and reflects it for the graph.
/// Headless: it reads the shader cache and nothing else, so tests and the UiProbe can run it on real data.
/// </summary>
public static class MaterialGraphShaderResolver
{
    /// <param name="cache">The shader cache (ShaderCache.dx11.wad.client).</param>
    /// <param name="perms">shaders.bin definitions; null leaves the define set to what the material authors.</param>
    /// <param name="note">What happened, in one line, when the result is null or partial.</param>
    public static MaterialGraphShaderInfo? Resolve(ShaderCacheReader cache, ShaderPermutationIndex? perms,
        MaterialBinding b, out string note, out DxbcShader? vertex, out DxbcShader? pixel,
        Func<string, DxbcShader, DxbcShader, string?>? textureRule = null)
    {
        vertex = pixel = null;
        string? shader = b.RenderShader;
        if (string.IsNullOrWhiteSpace(shader)) { note = "the material names no renderShader"; return null; }

        string full = "assets/shaders/generated/" + shader.Trim('/');
        string vsPath = ShaderCacheReader.TocPathFor(full, DxbcStage.Vertex);
        string psPath = ShaderCacheReader.TocPathFor(full, DxbcStage.Pixel);
        var vsToc = cache.ReadToc(vsPath);
        var psToc = cache.ReadToc(psPath);

        IReadOnlyDictionary<string, string>? feat = null;
        IReadOnlyDictionary<string, bool>? swDef = null;
        IReadOnlyDictionary<string, float[]>? paramDef = null;
        IReadOnlyDictionary<string, string>? texDef = null;
        if (perms is not null)
        {
            if (perms.TryGetShaderDefs(shader, out var f, out var s)) { feat = f; swDef = s; }
            if (perms.TryGetParameterDefaults(shader, out var pd)) paramDef = pd;
            if (perms.TryGetTextureDefaults(shader, out var td)) texDef = td;
        }

        if (vsToc is null || psToc is null)
        {
            note = $"'{shader}' is not in the shader cache (or ships only one stage)";
            return new MaterialGraphShaderInfo
            {
                ShaderName = shader, SwitchDefaults = swDef ?? new Dictionary<string, bool>(), HasSwitchDefinition = swDef is { Count: > 0 },
                FeatureDefines = feat ?? new Dictionary<string, string>(),
                ParameterDefaults = paramDef ?? new Dictionary<string, float[]>(),
                TextureDefaults = texDef ?? new Dictionary<string, string>(),
            };
        }

        var vsPerm = ShaderCacheReader.ResolvePermutation(vsToc, b.Macros, b.Switches, feat, swDef, out var vsWhy);
        var psPerm = ShaderCacheReader.ResolvePermutation(psToc, b.Macros, b.Switches, feat, swDef, out var psWhy);
        if (vsPerm is null || psPerm is null)
        {
            note = "no cooked permutation matches this material's define set: " + (psPerm is null ? psWhy : vsWhy);
            return new MaterialGraphShaderInfo
            {
                ShaderName = shader, SwitchDefaults = swDef ?? new Dictionary<string, bool>(), HasSwitchDefinition = swDef is { Count: > 0 },
                FeatureDefines = feat ?? new Dictionary<string, string>(),
                ParameterDefaults = paramDef ?? new Dictionary<string, float[]>(),
                TextureDefaults = texDef ?? new Dictionary<string, string>(),
            };
        }

        vertex = cache.LoadShader(vsPath, vsPerm.BlobIndex, out var e1);
        pixel = cache.LoadShader(psPath, psPerm.BlobIndex, out var e2);
        note = vertex is null || pixel is null ? $"bytecode did not load: {e1 ?? e2}" : "";

        // the caller's rule (the preview's own sampler -> texture rule) so graph wires match what the preview binds
        DxbcShader? vsLocal = vertex, psLocal = pixel;
        Func<string, string?>? rule = textureRule is null || vsLocal is null || psLocal is null
            ? null : sampler => textureRule(sampler, psLocal, vsLocal);
        return MaterialGraphShaderInfo.FromReflection(shader, vertex, pixel, swDef, feat, paramDef, texDef, rule,
            pixelPermutation: $"blob #{psPerm.BlobIndex}, key 0x{psPerm.Key:x16}",
            vertexPermutation: $"blob #{vsPerm.BlobIndex}, key 0x{vsPerm.Key:x16}");
    }
}
