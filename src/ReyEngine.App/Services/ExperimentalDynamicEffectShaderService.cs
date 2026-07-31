using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.Services;

public sealed record ExperimentalShaderAsset(string Path, byte[] Bytes);

public sealed record ExperimentalDynamicEffectPatch(
    IReadOnlyList<ExperimentalShaderAsset> Assets,
    IReadOnlySet<string> SupportedMaterials,
    int VertexKeysAdded,
    int PixelKeysAdded,
    int CustomBlobsAdded,
    string Detail);

/// <summary>
/// M312: constructs a companion patch for the missing baked-light SRX_DynamicEffect permutations. Riot's
/// source cache remains read-only; the returned TOCs and last blob containers are staged into a separate
/// <c>ShaderCache.dx11.wad.client</c> project folder by the caller.
/// </summary>
public static class ExperimentalDynamicEffectShaderService
{
    public const string RenderShader = "Shaders/StaticMesh/SRX_DynamicEffect";
    private const string GeneratedShader = "assets/shaders/generated/" + RenderShader;
    private static readonly HashSet<string> ForceNoBakedAbsent =
        new(StringComparer.OrdinalIgnoreCase) { MaterialBinding.MacroNoBakedLighting };

    private enum CustomKind { Vertex, Pixel, FlowRipplePixel }

    public static ExperimentalDynamicEffectPatch Build(
        ShaderCacheReader cache,
        ShaderPermutationIndex definitions,
        IReadOnlyList<MaterialBinding> materials)
    {
        var targets = materials
            .Where(IsTarget)
            .GroupBy(Signature, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        if (targets.Count == 0)
            throw new InvalidOperationException("no SRX_DynamicEffect material with NO_BAKED_LIGHTING was selected");

        if (!ExperimentalDynamicEffectLightmapShader.TryCompile(out var compiled, out var compileError))
            throw new InvalidOperationException("custom HLSL compilation failed: " + compileError);

        var assets = new List<ExperimentalShaderAsset>();
        var stageResults = new Dictionary<DxbcStage, StageResult>();
        foreach (var stage in new[] { DxbcStage.Vertex, DxbcStage.Pixel })
        {
            var result = BuildStage(cache, definitions, targets, stage, compiled!);
            stageResults[stage] = result;
            assets.AddRange(result.Assets);
        }

        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in materials.Where(IsTarget))
            if (stageResults[DxbcStage.Vertex].SupportedSignatures.Contains(Signature(material))
                && stageResults[DxbcStage.Pixel].SupportedSignatures.Contains(Signature(material)))
                supported.Add(material.Name);

        var vertex = stageResults[DxbcStage.Vertex];
        var pixel = stageResults[DxbcStage.Pixel];
        return new ExperimentalDynamicEffectPatch(
            assets,
            supported,
            vertex.KeysAdded,
            pixel.KeysAdded,
            vertex.CustomBlobCount + pixel.CustomBlobCount,
            $"{supported.Count:n0} material(s), {vertex.KeysAdded:n0} VS + {pixel.KeysAdded:n0} PS key(s), "
            + $"{vertex.CustomBlobCount + pixel.CustomBlobCount} custom DXBC blob(s)");
    }

    private sealed record StageResult(
        IReadOnlyList<ExperimentalShaderAsset> Assets,
        IReadOnlySet<string> SupportedSignatures,
        int KeysAdded,
        int CustomBlobCount);

    private static StageResult BuildStage(
        ShaderCacheReader cache,
        ShaderPermutationIndex definitions,
        IReadOnlyList<MaterialBinding> materials,
        DxbcStage stage,
        ExperimentalDynamicEffectLightmapShader.Compiled compiled)
    {
        string requestedPath = ShaderCacheReader.TocPathFor(GeneratedShader, stage);
        var toc = cache.ReadToc(requestedPath)
            ?? throw new InvalidOperationException($"{stage} TOC is missing: {requestedPath}");
        var originalByKey = toc.Permutations.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First());
        var additions = new Dictionary<ulong, ShaderPermutation>();
        var customIndex = new Dictionary<CustomKind, uint>();
        var customBlobs = new List<byte[]>();
        var supported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var material in materials)
        {
            definitions.TryGetShaderDefs(RenderShader, out var features, out var defaults);
            var candidates = ShaderPermutationPlanner.EnumerateCandidates(
                toc, material.Macros, material.Switches, features, defaults,
                out var planWhy, forcedAbsent: ForceNoBakedAbsent);
            if (candidates.Count == 0)
                throw new InvalidOperationException($"{material.Name}: {stage} define planning failed: {planWhy}");

            bool suppliedMainPass = false;
            foreach (var candidate in candidates)
            {
                var sourceDefines = candidate.Defines.Append(MaterialBinding.MacroNoBakedLighting + "=1")
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList();
                ulong sourceKey = ShaderCacheReader.PermutationKey(sourceDefines);
                if (!originalByKey.TryGetValue(sourceKey, out var source)) continue;

                ulong targetKey = candidate.Key;
                if (originalByKey.ContainsKey(targetKey))
                {
                    if (candidate.InferredDefines.Count == 0) suppliedMainPass = true;
                    continue;
                }

                uint blobIndex;
                if (candidate.InferredDefines.Count == 0)
                {
                    CustomKind kind = stage == DxbcStage.Vertex ? CustomKind.Vertex
                        : SwitchOn(material, "FLOW_RIPPLE_ON") ? CustomKind.FlowRipplePixel
                        : CustomKind.Pixel;
                    if (!customIndex.TryGetValue(kind, out blobIndex))
                    {
                        blobIndex = toc.DeclaredBlobCount + (uint)customBlobs.Count;
                        customIndex[kind] = blobIndex;
                        customBlobs.Add(kind switch
                        {
                            CustomKind.Vertex => compiled.Vertex,
                            CustomKind.FlowRipplePixel => compiled.FlowRipplePixel,
                            _ => compiled.Pixel,
                        });
                    }
                    suppliedMainPass = true;
                }
                else
                {
                    // Quality, shadow-map and other runtime-injected variants retain Riot's original
                    // unlit bytecode. Adding their missing key prevents a lookup crash; only the normal
                    // scene pass needs the custom lightmap implementation.
                    blobIndex = source.BlobIndex;
                }

                if (additions.TryGetValue(targetKey, out var prior) && prior.BlobIndex != blobIndex)
                    throw new InvalidOperationException($"{stage} key 0x{targetKey:x16} maps to two custom programs");
                additions[targetKey] = new ShaderPermutation(targetKey, blobIndex)
                {
                    Defines = candidate.Defines,
                };
            }

            if (suppliedMainPass) supported.Add(Signature(material));
        }

        if (supported.Count == 0)
            throw new InvalidOperationException($"no missing main-pass {stage} permutation could be generated");

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

        // Reparse the bytes now, before a caller writes anything into a project.
        var reparsed = ShaderCacheReader.ParseToc(output[0].Bytes, toc.Path)
            ?? throw new InvalidDataException($"generated {stage} TOC did not reparse");
        if (reparsed.Permutations.Count != allPermutations.Count || reparsed.DeclaredBlobCount != declaredBlobCount)
            throw new InvalidDataException($"generated {stage} TOC changed counts during reparse");

        return new StageResult(output, supported, additions.Count, customBlobs.Count);
    }

    private static bool IsTarget(MaterialBinding material) =>
        string.Equals(material.RenderShader ?? material.ShaderName, RenderShader, StringComparison.OrdinalIgnoreCase)
        && material.MacroOn(MaterialBinding.MacroNoBakedLighting);

    private static bool SwitchOn(MaterialBinding material, string name) =>
        material.Switches.TryGetValue(name, out bool enabled) && enabled;

    private static string Signature(MaterialBinding material) =>
        string.Join(";", material.Macros.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                                     .Select(x => $"M:{x.Key}={x.Value}"))
        + "|" + string.Join(";", material.Switches.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                                                    .Select(x => $"S:{x.Key}={(x.Value ? 1 : 0)}"));
}
