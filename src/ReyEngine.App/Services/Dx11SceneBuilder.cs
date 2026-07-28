using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M249 (phase 6, step 2): populate a D3D11 renderer from a map the editor already has open.</para>
///
/// <para>A service rather than another copy of the logic inside a view model. The preview window grew its
/// own version of this over M213-M247, and the divergence between that and the app's real path is exactly
/// what let M235's bug hide - a probe that exercised a sibling code path reported green while the product
/// drew nothing. The viewport gets the same builder or it gets nothing.</para>
/// </summary>
public static class Dx11SceneBuilder
{
    public sealed record Result(int Materials, int Failed, int Textures, int Slices, string Report);

    /// <summary>Upload the geometry and build one pipeline per drawable slice.</summary>
    public static Result Build(
        ShaderPreviewRenderer renderer,
        ShaderCacheReader cache,
        ShaderPermutationIndex? perms,
        MapGeoAsset map,
        IReadOnlyList<MaterialBinding> materials,
        Func<ulong, byte[]?> readAsset,
        string gameVersion)
    {
        var sb = new StringBuilder();
        renderer.GameVersion = gameVersion;
        renderer.ClearMaterials();

        var mesh = PreviewGeometry.FromLeagueArrays(
            "viewport", map.Positions.Length / 3,
            map.Positions, map.Normals, map.Uvs, map.Colors, map.LightmapUvs, map.Indices,
            grassPivots: map.GrassPivots);
        renderer.SetMesh(mesh);

        // M226: the lightmap atlas is a per-GROUP property. Keying it by material name handed 71.5% of
        // Map12's lit groups another mesh's atlas page, so it travels on the slice.
        var slices = map.Groups
            .Select(g => (g.Material, Start: g.StartIndex, Count: g.IndexCount, Lightmap: g.LightmapTexture))
            .OrderBy(x => x.Start)
            .ToList();

        // M226: coalesce runs that are already adjacent AND want the same atlas page. The lightmap has to
        // be part of the key or merging re-introduces the bug above.
        var merged = new List<(string Material, int Start, int Count, string Lightmap)>();
        foreach (var s in slices)
        {
            if (merged.Count > 0)
            {
                var p = merged[^1];
                if (p.Start + p.Count == s.Start
                    && string.Equals(p.Material, s.Material, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.Lightmap, s.Lightmap, StringComparison.OrdinalIgnoreCase))
                { merged[^1] = p with { Count = p.Count + s.Count }; continue; }
            }
            merged.Add((s.Material, s.Start, s.Count, s.Lightmap));
        }

        var byName = new Dictionary<string, MaterialBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in materials) byName[m.Name] = m;

        int ok = 0, failed = 0, textures = 0;
        var texCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var slice in merged)
        {
            if (!byName.TryGetValue(slice.Material, out var b) || string.IsNullOrEmpty(b.RenderShader))
            { failed++; continue; }

            string full = "assets/shaders/generated/" + b.RenderShader!.Trim('/');
            IReadOnlyDictionary<string, string>? feat = null;
            IReadOnlyDictionary<string, bool>? swDef = null;
            perms?.TryGetShaderDefs(b.RenderShader!, out feat, out swDef);

            var vsToc = cache.ReadToc(ShaderCacheReader.TocPathFor(full, DxbcStage.Vertex));
            var psToc = cache.ReadToc(ShaderCacheReader.TocPathFor(full, DxbcStage.Pixel));
            if (vsToc is null || psToc is null) { failed++; continue; }

            var vp = ShaderCacheReader.ResolvePermutation(vsToc, b.Macros, b.Switches, feat, swDef, out _);
            var pp = ShaderCacheReader.ResolvePermutation(psToc, b.Macros, b.Switches, feat, swDef, out _);
            if (vp is null || pp is null) { failed++; continue; }

            var vs = cache.LoadShader(ShaderCacheReader.TocPathFor(full, DxbcStage.Vertex), vp.BlobIndex, out _);
            var ps = cache.LoadShader(ShaderCacheReader.TocPathFor(full, DxbcStage.Pixel), pp.BlobIndex, out _);
            if (vs is null || ps is null) { failed++; continue; }

            var vsDesc = new ShaderDescription(full, DxbcStage.Vertex, vp.Key, vp.BlobIndex, b.Macros, vs);
            var psDesc = new ShaderDescription(full, DxbcStage.Pixel, pp.Key, pp.BlobIndex, b.Macros, ps);

            var mat = renderer.BuildMaterial(b.Name, vs, ps, slice.Start, slice.Count, out var rep,
                vsDesc, psDesc, StateDescription.Geometry);
            if (mat is null) { failed++; sb.AppendLine($"   ! {b.Name}: {rep.Error}"); continue; }

            // M245 culling + M246 sorting. Scene geometry writes depth, so the depth buffer decides what is
            // in front and grouping by pipeline cannot change the image.
            mat.Bounds = SliceBounds(mesh, slice.Start, slice.Count);
            mat.SortableByPipeline = true;

            foreach (var slot in b.Slots)
            {
                if (string.IsNullOrWhiteSpace(slot.Path)) continue;
                string? target = ResolveTextureTarget(slot.SamplerName, ps);
                if (target is null) continue;
                if (BindTexture(renderer, mat, target, slot.Path!, readAsset, texCache)) textures++;
            }

            if (slice.Lightmap.Length > 0
                && ps.Textures.FirstOrDefault(t => t.Name.Contains("BAKED_LIGHT", StringComparison.OrdinalIgnoreCase))
                    is { } lmSlot
                && BindTexture(renderer, mat, lmSlot.Name, slice.Lightmap, readAsset, texCache))
                textures++;

            renderer.AddMaterial(mat);
            ok++;
        }

        sb.AppendLine($"{mesh.Vertices.Length:n0} vertices, {mesh.TriangleCount:n0} triangles");
        sb.AppendLine($"{map.Groups.Count} groups -> {merged.Count} slices");
        sb.AppendLine($"{ok} material(s) live, {failed} unresolved, {textures} texture binding(s)");
        sb.AppendLine($"pipelines: {renderer.PipelineCacheHits} hit, {renderer.PipelineCacheMisses} built, "
                      + $"{renderer.CachedPipelineCount} resident");

        return new Result(ok, failed, textures, merged.Count, sb.ToString());
    }

    /// <summary>M210's rule: a material sampler binds to the shader texture named after it plus "__TX".
    /// Anything ending _SharedTexture is engine-supplied and is never material-bound.</summary>
    private static string? ResolveTextureTarget(string sampler, DxbcShader ps)
    {
        var exact = ps.Textures.FirstOrDefault(t =>
            t.Name.Equals(sampler + "__TX", StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Name;
        return ps.Textures.FirstOrDefault(t =>
            t.Name.Equals(sampler, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    private static bool BindTexture(ShaderPreviewRenderer renderer, PreviewMaterial mat, string target,
        string path, Func<ulong, byte[]?> readAsset, Dictionary<string, bool> tried)
    {
        string key = path.ToLowerInvariant();
        if (renderer.TryBindCached(mat, target, key)) return true;
        if (tried.TryGetValue(key, out bool wasOk) && !wasOk) return false;
        try
        {
            var data = readAsset(HashAlgorithms.WadPath(key));
            if (data is null || data.Length == 0) { tried[key] = false; return false; }
            var img = TextureDecoder.Decode(data);
            renderer.SetTexture(mat, target, key, img.Rgba, img.Width, img.Height);
            tried[key] = true;
            return true;
        }
        catch { tried[key] = false; return false; }
    }

    private static (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)? SliceBounds(
        PreviewMesh mesh, int start, int count)
    {
        if (count <= 0) return null;
        var lo = new System.Numerics.Vector3(float.MaxValue);
        var hi = new System.Numerics.Vector3(float.MinValue);
        int end = Math.Min(start + count, mesh.Indices.Length);
        for (int i = start; i < end; i++)
        {
            uint vi = mesh.Indices[i];
            if (vi >= mesh.Vertices.Length) continue;
            var p = mesh.Vertices[vi].Position;
            lo = System.Numerics.Vector3.Min(lo, p);
            hi = System.Numerics.Vector3.Max(hi, p);
        }
        return lo.X > hi.X ? null : (lo, hi);
    }
}
