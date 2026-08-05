using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.Services;

/// <summary>
/// Builds companion shader-cache entries for the legacy SRX blend shaders. Unlike DynamicEffect, these
/// shaders have no baked-light permutation anywhere in Riot's cache, so their normal scene pass uses a
/// small compatible custom program while shadow/quality passes retain Riot's original bytecode.
/// </summary>
public static class ExperimentalSrxBlendShaderService
{
    public const string MasterShader = "Shaders/StaticMesh/SRX_Blend_Master";
    public const string ChemtechDecalShader = "Shaders/StaticMesh/SRX_Blend_Chemtech_Decal";

    private static readonly HashSet<string> SupportedShaders =
        new(StringComparer.OrdinalIgnoreCase) { MasterShader, ChemtechDecalShader };
    private static readonly HashSet<string> ForceNoBakedAbsent =
        new(StringComparer.OrdinalIgnoreCase) { MaterialBinding.MacroNoBakedLighting };
    private static readonly HashSet<string> RuntimeMainPassAxes =
        new(StringComparer.OrdinalIgnoreCase) { "ENV_TRANSITION" };

    public static bool Supports(MaterialBinding material) =>
        SupportedShaders.Contains(material.RenderShader ?? material.ShaderName ?? "");

    public static ExperimentalLightmapShaderPatch Build(
        ShaderCacheReader cache,
        ShaderPermutationIndex definitions,
        IReadOnlyList<MaterialBinding> materials)
    {
        var targets = materials.Where(Supports).ToList();
        if (targets.Count == 0)
            throw new InvalidOperationException("no SRX blend material was selected");
        if (!ExperimentalSrxBlendLightmapShader.TryCompile(out var compiled, out var compileError))
            throw new InvalidOperationException("custom SRX HLSL compilation failed: " + compileError);

        var assets = new List<ExperimentalShaderAsset>();
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int vertexKeys = 0, pixelKeys = 0, customBlobs = 0;

        foreach (var shaderGroup in targets.GroupBy(
                     m => m.RenderShader ?? m.ShaderName ?? "", StringComparer.OrdinalIgnoreCase))
        {
            string renderShader = shaderGroup.Key;
            var unique = shaderGroup.GroupBy(Signature, StringComparer.Ordinal).Select(g => g.First()).ToList();
            byte[] vertexProgram = renderShader.Equals(ChemtechDecalShader, StringComparison.OrdinalIgnoreCase)
                ? compiled!.ChemtechVertex : compiled!.Vertex;
            byte[] pixelProgram = renderShader.Equals(ChemtechDecalShader, StringComparison.OrdinalIgnoreCase)
                ? compiled!.ChemtechPixel : compiled!.MasterPixel;

            var vertex = BuildStage(cache, definitions, renderShader, unique,
                DxbcStage.Vertex, vertexProgram);
            var pixel = BuildStage(cache, definitions, renderShader, unique,
                DxbcStage.Pixel, pixelProgram);
            assets.AddRange(vertex.Assets);
            assets.AddRange(pixel.Assets);
            vertexKeys += vertex.KeysAdded;
            pixelKeys += pixel.KeysAdded;
            customBlobs += vertex.CustomBlobCount + pixel.CustomBlobCount;

            foreach (var material in shaderGroup)
                if (vertex.SupportedSignatures.Contains(Signature(material))
                    && pixel.SupportedSignatures.Contains(Signature(material)))
                    supported.Add(material.Name);
        }

        return new ExperimentalLightmapShaderPatch(
            assets, supported, vertexKeys, pixelKeys, customBlobs,
            $"{supported.Count:n0} SRX blend material(s), {vertexKeys:n0} VS + {pixelKeys:n0} PS key(s), "
            + $"{customBlobs:n0} custom DXBC blob(s)");
    }

    private sealed record StageResult(
        IReadOnlyList<ExperimentalShaderAsset> Assets,
        IReadOnlySet<string> SupportedSignatures,
        int KeysAdded,
        int CustomBlobCount);

    private static StageResult BuildStage(
        ShaderCacheReader cache,
        ShaderPermutationIndex definitions,
        string renderShader,
        IReadOnlyList<MaterialBinding> materials,
        DxbcStage stage,
        byte[] customProgram)
    {
        string generatedShader = "assets/shaders/generated/" + renderShader;
        string requestedPath = ShaderCacheReader.TocPathFor(generatedShader, stage);
        var toc = cache.ReadToc(requestedPath)
            ?? throw new InvalidOperationException($"{stage} TOC is missing: {requestedPath}");
        var originalByKey = toc.Permutations.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First());
        var additions = new Dictionary<ulong, ShaderPermutation>();
        var customBlobs = new List<byte[]>();
        uint? customIndex = null;
        var supported = new HashSet<string>(StringComparer.Ordinal);

        definitions.TryGetShaderDefs(renderShader, out var features, out var defaults);
        foreach (var material in materials)
        {
            var candidates = ShaderPermutationPlanner.EnumerateCandidates(
                toc, material.Macros, material.Switches, features, defaults,
                out var planWhy, forcedAbsent: ForceNoBakedAbsent, forcedFree: RuntimeMainPassAxes);
            if (candidates.Count == 0)
                throw new InvalidOperationException($"{material.Name}: {stage} define planning failed: {planWhy}");

            bool suppliedMainPass = false;
            foreach (var candidate in candidates)
            {
                bool usesCustomShader = candidate.InferredDefines.All(IsRuntimeMainPassDefine);
                ulong sourceKey = ShaderCacheReader.PermutationKey(
                    candidate.Defines.Append(MaterialBinding.MacroNoBakedLighting + "=1"));
                if (!originalByKey.TryGetValue(sourceKey, out var source)) continue;

                if (originalByKey.ContainsKey(candidate.Key))
                {
                    if (usesCustomShader) suppliedMainPass = true;
                    continue;
                }

                uint blobIndex;
                if (usesCustomShader)
                {
                    if (customIndex is null)
                    {
                        customIndex = toc.DeclaredBlobCount + (uint)customBlobs.Count;
                        customBlobs.Add(customProgram);
                    }
                    blobIndex = customIndex.Value;
                    suppliedMainPass = true;
                }
                else
                {
                    // Shadow-map and quality variants need a lookup key but do not sample the map atlas.
                    blobIndex = source.BlobIndex;
                }

                if (additions.TryGetValue(candidate.Key, out var prior) && prior.BlobIndex != blobIndex)
                    throw new InvalidOperationException(
                        $"{stage} key 0x{candidate.Key:x16} maps to two SRX programs");
                additions[candidate.Key] = new ShaderPermutation(candidate.Key, blobIndex)
                {
                    Defines = candidate.Defines,
                };
            }

            if (suppliedMainPass) supported.Add(Signature(material));
        }

        if (supported.Count == 0)
            throw new InvalidOperationException($"no missing main-pass {stage} permutation could be generated for {renderShader}");

        var allPermutations = toc.Permutations.Concat(additions.Values.OrderBy(p => p.Key)).ToList();
        uint declaredBlobCount = toc.DeclaredBlobCount + (uint)customBlobs.Count;
        var output = new List<ExperimentalShaderAsset>
        {
            new(toc.Path, ShaderCachePatchWriter.WriteToc(toc, allPermutations, declaredBlobCount)),
        };

        if (customBlobs.Count > 0)
        {
            uint containerBase = toc.DeclaredBlobCount / 100 * 100;
            var containerBlobs = new List<byte[]>();
            for (uint index = containerBase; index < toc.DeclaredBlobCount; index++)
            {
                var blob = cache.LoadBlob(toc.Path, index, out var blobError, out _)
                    ?? throw new InvalidOperationException($"could not rebuild {toc.Path}_{containerBase}: {blobError}");
                containerBlobs.Add(blob);
            }
            containerBlobs.AddRange(customBlobs);
            output.Add(new ExperimentalShaderAsset(
                $"{toc.Path}_{containerBase}", ShaderCachePatchWriter.WriteContainer(containerBlobs)));
        }

        var reparsed = ShaderCacheReader.ParseToc(output[0].Bytes, toc.Path)
            ?? throw new InvalidDataException($"generated {stage} TOC did not reparse");
        if (reparsed.Permutations.Count != allPermutations.Count
            || reparsed.DeclaredBlobCount != declaredBlobCount)
            throw new InvalidDataException($"generated {stage} SRX TOC changed counts during reparse");

        return new StageResult(output, supported, additions.Count, customBlobs.Count);
    }

    private static bool IsRuntimeMainPassDefine(string define)
    {
        int equals = define.IndexOf('=');
        string name = equals < 0 ? define : define[..equals];
        return RuntimeMainPassAxes.Contains(name);
    }

    private static string Signature(MaterialBinding material) =>
        string.Join(";", material.Macros.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                                     .Select(x => $"M:{x.Key}={x.Value}"))
        + "|" + string.Join(";", material.Switches.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                                                    .Select(x => $"S:{x.Key}={(x.Value ? 1 : 0)}"));
}
