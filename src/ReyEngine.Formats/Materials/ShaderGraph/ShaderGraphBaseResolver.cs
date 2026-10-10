using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Materials.ShaderGraph;

/// <summary>
/// M832: builds the <see cref="SgBase"/> of a Riot shader from the installed shader cache (and shaders.bin, for the declared
/// parameters). Read-only; the cache is never written. It loads every COLOUR pixel blob of the shader (the shadow-map passes are not
/// the graph's business) and groups them by input signature - DefaultEnv_Flat has 16.
/// </summary>
public static class ShaderGraphBaseResolver
{
    public static SgBase? Resolve(ShaderCacheReader cache, ShaderPermutationIndex? perms, string renderShader, out string error, out string note)
    {
        error = note = "";
        if (!SgBase.IsSupported(renderShader))
        {
            error = $"'{renderShader}' is not a base shader this build supports (supported: {string.Join(", ", SgBase.SupportedUvSets.Keys)}).";
            return null;
        }
        string shaderKey = renderShader.Trim().Trim('/').Replace('\\', '/');
        string full = ("assets/shaders/generated/" + shaderKey).ToLowerInvariant();
        string psPath = ShaderCacheReader.TocPathFor(full, DxbcStage.Pixel);
        var toc = cache.ReadToc(psPath);
        if (toc is null) { error = $"the shader cache has no pixel stage for {shaderKey}."; return null; }

        var described = ShaderCacheReader.DescribePermutations(toc, out bool truncated);
        if (truncated) { error = $"the define pool of {shaderKey} is too large to classify its permutations."; return null; }

        // colour blobs: a blob some key without GENERATE_SHADOW_MAP=1 uses
        var colour = new SortedDictionary<uint, List<ShaderPermutation>>();
        foreach (var p in described)
        {
            bool shadow = p.Defines is not null && p.Defines.Contains("GENERATE_SHADOW_MAP=1");
            if (shadow) continue;
            if (!colour.TryGetValue(p.BlobIndex, out var list)) colour[p.BlobIndex] = list = new List<ShaderPermutation>();
            list.Add(p);
        }
        if (colour.Count == 0) { error = $"{shaderKey} has no colour pixel permutations."; return null; }

        var groups = new Dictionary<string, (List<uint> Blobs, DxbcShader First)>(StringComparer.Ordinal);
        var shaders = new List<DxbcShader>();
        foreach (var (blob, _) in colour)
        {
            var sh = cache.LoadShader(psPath, blob, out string? why);
            if (sh is null) { error = $"pixel blob #{blob} did not load: {why}"; return null; }
            shaders.Add(sh);
            string id = SgSignature.Describe(sh.Inputs) + " => " + SgSignature.Describe(sh.Outputs);
            if (!groups.TryGetValue(id, out var g)) groups[id] = g = (new List<uint>(), sh);
            g.Blobs.Add(blob);
        }

        var signatures = new List<SgSignature>();
        int index = 0;
        foreach (var g in groups.Values.OrderBy(g => g.Blobs.Min()))
        {
            var perm = g.Blobs.SelectMany(b => colour[b]).GroupBy(p => p.Key).Select(x => x.First()).OrderBy(p => p.Key).ToList();
            signatures.Add(new SgSignature { Index = index++, Inputs = g.First.Inputs, Outputs = g.First.Outputs, Permutations = perm });
        }

        // ---- declared textures: what shaders.bin names (textures[].name, with or without a default path). The reflected
        // <Name>__TX textures the engine binds itself (BAKED_LIGHT, STATIONARY_LIGHT...) are NOT declared there and are not offered.
        var textureNames = new List<string>();
        void AddTexture(string name)
        {
            if (name.EndsWith("__TX", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Length > 0 && !textureNames.Contains(name, StringComparer.Ordinal)) textureNames.Add(name);
        }
        string defKey = DefinitionKey(perms, shaderKey);
        if (perms is not null && perms.TryGetTextureNames(defKey, out var declaredTextures) && declaredTextures.Count > 0)
        {
            var reflected = shaders.SelectMany(sh => sh.Textures).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            foreach (string t in declaredTextures)
                if (reflected.Contains(t + "__TX")) AddTexture(t);
        }
        else
        {
            note = "shaders.bin was not available, so the declared textures are the <Name>__TX textures the cooked blobs reflect.";
            foreach (var sh in shaders)
                foreach (var t in sh.Textures)
                    if (t.Name.EndsWith("__TX", StringComparison.Ordinal)) AddTexture(t.Name);
        }

        // ---- declared parameters
        var parameters = new List<SgParameterDecl>();
        IReadOnlyDictionary<string, float[]>? paramDefaults = null;
        if (perms is not null && perms.TryGetParameterDefaults(defKey, out var pd)) paramDefaults = pd;
        var reflectedTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sh in shaders)
            foreach (var cb in sh.ConstantBuffers)
                if (cb.Name == "$Globals")
                    foreach (var v in cb.Variables) reflectedTypes.TryAdd(v.Name, v.TypeName);
        if (paramDefaults is not null)
        {
            foreach (var (name, dv) in paramDefaults.OrderBy(k => k.Key, StringComparer.Ordinal))
                parameters.Add(new SgParameterDecl(name, ComponentsOf(reflectedTypes.GetValueOrDefault(name), 4), dv));
        }
        else
        {
            note = (note.Length > 0 ? note + " " : "") + "shaders.bin was not available, so the declared parameters are the float constants the cooked blobs reflect in $Globals.";
            foreach (var (name, type) in reflectedTypes.OrderBy(k => k.Key, StringComparer.Ordinal))
                if (ComponentsOf(type, 0) is > 0 and var w && type!.StartsWith("float", StringComparison.Ordinal))
                    parameters.Add(new SgParameterDecl(name, w, new float[w]));
        }

        // ---- the engine's per-frame cbuffer: one fixed layout across the cache, or none
        SgEngineBuffer? frame = null;
        var frameVars = new Dictionary<string, SgEngineVar>(StringComparer.Ordinal);
        int frameSize = 0;
        bool consistent = true;
        foreach (var sh in shaders)
            foreach (var cb in sh.ConstantBuffers)
            {
                if (cb.Name != "PerFramePixelCB") continue;
                frameSize = Math.Max(frameSize, cb.Size);
                foreach (var v in cb.Variables)
                {
                    var ev = new SgEngineVar(v.Name, v.Offset, v.Size, v.TypeName);
                    if (frameVars.TryGetValue(v.Name, out var known)) { if (known != ev) consistent = false; }
                    else frameVars[v.Name] = ev;
                }
            }
        if (frameVars.Count > 0 && consistent)
            frame = new SgEngineBuffer("PerFramePixelCB", frameSize, frameVars.Values.OrderBy(v => v.Offset).ThenBy(v => v.Name, StringComparer.Ordinal).ToList());
        else if (frameVars.Count > 0)
            note = (note.Length > 0 ? note + " " : "") + "PerFramePixelCB does not have one layout across the blobs, so Time and the sRGB encode are not offered.";

        return new SgBase
        {
            Shader = shaderKey,
            Textures = textureNames.Select(n => new SgTextureDecl(n)).ToList(),
            Parameters = parameters,
            Signatures = signatures,
            UvSet = SgBase.SupportedUvSets[SgBase.Normalize(shaderKey)],
            Frame = frame,
            ShadowPermutations = described.Where(p => p.Defines is not null && p.Defines.Contains("GENERATE_SHADOW_MAP=1")).GroupBy(p => p.Key).Select(g => g.First()).OrderBy(p => p.Key).ToList(),
            PixelTocPath = psPath,
            PixelBlobCount = toc.DeclaredBlobCount,
        };
    }

    /// <summary>The key shaders.bin definitions are indexed by: the shader's own spelling (<c>Shaders/StaticMesh/DefaultEnv_Flat</c>); the
    /// index is case-sensitive, so the spelling the material used is kept and a lowercase fallback tried.</summary>
    private static string DefinitionKey(ShaderPermutationIndex? perms, string shaderKey)
    {
        if (perms is null) return shaderKey;
        if (perms.TryGetParameterDefaults(shaderKey, out _) || perms.TryGetTextureDefaults(shaderKey, out _)) return shaderKey;
        string lower = shaderKey.ToLowerInvariant();
        return perms.TryGetParameterDefaults(lower, out _) || perms.TryGetTextureDefaults(lower, out _) ? lower : shaderKey;
    }

    private static int ComponentsOf(string? type, int fallback)
    {
        if (type is null) return fallback;
        return type switch { "float" => 1, "float2" => 2, "float3" => 3, "float4" => 4, _ => fallback };
    }
}
