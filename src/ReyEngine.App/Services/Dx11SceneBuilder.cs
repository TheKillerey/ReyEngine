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
///
/// <para>M250: split into <see cref="Prepare"/> (CPU, safe on a worker thread) and <see cref="Commit"/>
/// (D3D, must be on the UI thread). The split is drawn where D3D allows it, not where it is convenient:
/// device resource creation is free-threaded but the immediate context is not, and texture upload maps
/// buffers through it. So uploads stay on the UI thread and only the decoding moves - which M224 measured
/// at 88% of a 42 s Map12 load, and is why this is worth doing at all.</para>
/// </summary>
public static class Dx11SceneBuilder
{
    public sealed record Result(int Materials, int Failed, int Textures, int Slices, string Report);

    /// <summary>One slice, resolved and ready to become a pipeline. Everything here is CPU-side.</summary>
    public sealed record PreparedSlice(
        string Name, int Start, int Count,
        DxbcShader Vs, DxbcShader Ps,
        ShaderDescription VsDesc, ShaderDescription PsDesc,
        IReadOnlyList<(string Target, string Key)> Textures);

    /// <summary>Everything a scene needs that does not touch D3D.</summary>
    public sealed class PreparedScene
    {
        public required PreviewMesh Mesh { get; init; }
        public required List<PreparedSlice> Slices { get; init; }
        public required Dictionary<string, TextureImage> Textures { get; init; }
        public int Failed { get; set; }
        public int RawGroups { get; set; }
        public double PrepareMs { get; set; }
    }

    // ---------------------------------------------------------------- CPU half

    public static PreparedScene Prepare(
        ShaderCacheReader cache,
        ShaderPermutationIndex? perms,
        MapGeoAsset map,
        IReadOnlyList<MaterialBinding> materials,
        Func<ulong, byte[]?> readAsset)
    {
        var t0 = DateTime.UtcNow;

        var mesh = PreviewGeometry.FromLeagueArrays(
            "viewport", map.Positions.Length / 3,
            map.Positions, map.Normals, map.Uvs, map.Colors, map.LightmapUvs, map.Indices,
            grassPivots: map.GrassPivots,
            // M253: authored world coordinates, NOT recentred. The editor camera is shared with the GL
            // viewport, which draws the map where the data puts it.
            recentre: false);

        var merged = MergeSlices(map);
        var byName = new Dictionary<string, MaterialBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in materials) byName[m.Name] = m;

        // Every distinct texture path the scene will need, collected first so the decode can be one
        // parallel pass rather than interleaved with shader resolution.
        var distinct = new HashSet<string>(StringComparer.Ordinal);

        var scene = new PreparedScene
        {
            Mesh = mesh,
            Slices = new List<PreparedSlice>(merged.Count),
            Textures = new Dictionary<string, TextureImage>(StringComparer.Ordinal),
            RawGroups = map.Groups.Count,
        };

        foreach (var slice in merged)
        {
            if (!byName.TryGetValue(slice.Material, out var b) || string.IsNullOrEmpty(b.RenderShader))
            { scene.Failed++; continue; }

            string full = "assets/shaders/generated/" + b.RenderShader!.Trim('/');
            IReadOnlyDictionary<string, string>? feat = null;
            IReadOnlyDictionary<string, bool>? swDef = null;
            perms?.TryGetShaderDefs(b.RenderShader!, out feat, out swDef);

            var vsToc = cache.ReadToc(ShaderCacheReader.TocPathFor(full, DxbcStage.Vertex));
            var psToc = cache.ReadToc(ShaderCacheReader.TocPathFor(full, DxbcStage.Pixel));
            if (vsToc is null || psToc is null) { scene.Failed++; continue; }

            var vp = ShaderCacheReader.ResolvePermutation(vsToc, b.Macros, b.Switches, feat, swDef, out _);
            var pp = ShaderCacheReader.ResolvePermutation(psToc, b.Macros, b.Switches, feat, swDef, out _);
            if (vp is null || pp is null) { scene.Failed++; continue; }

            var vs = cache.LoadShader(ShaderCacheReader.TocPathFor(full, DxbcStage.Vertex), vp.BlobIndex, out _);
            var ps = cache.LoadShader(ShaderCacheReader.TocPathFor(full, DxbcStage.Pixel), pp.BlobIndex, out _);
            if (vs is null || ps is null) { scene.Failed++; continue; }

            var wanted = new List<(string Target, string Key)>();
            foreach (var slot in b.Slots)
            {
                if (string.IsNullOrWhiteSpace(slot.Path)) continue;
                string? target = ResolveTextureTarget(slot.SamplerName, ps);
                if (target is not null) wanted.Add((target, slot.Path!.ToLowerInvariant()));
            }
            if (slice.Lightmap.Length > 0
                && ps.Textures.FirstOrDefault(t => t.Name.Contains("BAKED_LIGHT", StringComparison.OrdinalIgnoreCase))
                    is { } lmSlot)
                wanted.Add((lmSlot.Name, slice.Lightmap.ToLowerInvariant()));

            foreach (var (_, key) in wanted) distinct.Add(key);

            scene.Slices.Add(new PreparedSlice(b.Name, slice.Start, slice.Count, vs, ps,
                new ShaderDescription(full, DxbcStage.Vertex, vp.Key, vp.BlobIndex, b.Macros, vs),
                new ShaderDescription(full, DxbcStage.Pixel, pp.Key, pp.BlobIndex, b.Macros, ps),
                wanted));
        }

        DecodeTextures(distinct, readAsset, scene);
        scene.PrepareMs = (DateTime.UtcNow - t0).TotalMilliseconds;
        return scene;
    }

    /// <summary>
    /// <para>M251: read sequentially, decode in parallel.</para>
    ///
    /// <para>The asymmetry is not arbitrary. <c>WadArchive.Extract</c> takes a lock, so concurrent reads
    /// would queue on it and buy nothing - parallelising that half would add contention for no gain.
    /// Decoding is pure CPU per buffer with no shared state, which is the half that scales.</para>
    ///
    /// <para>Once per distinct PATH, never once per slice: bloom has 2,860 texture bindings across far
    /// fewer files, and decoding per binding would make this slower than the version it replaced.</para>
    /// </summary>
    private static void DecodeTextures(
        HashSet<string> distinct, Func<ulong, byte[]?> readAsset, PreparedScene scene)
    {
        var keys = distinct.ToArray();
        var raw = new byte[keys.Length][];

        for (int i = 0; i < keys.Length; i++)
        {
            try { raw[i] = readAsset(HashAlgorithms.WadPath(keys[i])) ?? Array.Empty<byte>(); }
            catch { raw[i] = Array.Empty<byte>(); }
        }

        var decoded = new System.Collections.Concurrent.ConcurrentDictionary<string, TextureImage>(
            StringComparer.Ordinal);

        System.Threading.Tasks.Parallel.For(0, keys.Length, i =>
        {
            var bytes = raw[i];
            if (bytes.Length == 0) return;
            // A texture that will not decode is left out and reported by the commit pass, exactly as when
            // this ran serially - a throw here would abort the whole scene over one bad file.
            try { decoded[keys[i]] = TextureDecoder.Decode(bytes); }
            catch { }
            finally { raw[i] = Array.Empty<byte>(); }   // release the compressed copy as we go
        });

        foreach (var kv in decoded) scene.Textures[kv.Key] = kv.Value;
    }

    // ---------------------------------------------------------------- D3D half

    /// <summary>Must run on the UI thread - every call in here touches the device or the immediate
    /// context.</summary>
    public static Result Commit(ShaderPreviewRenderer renderer, PreparedScene scene, string gameVersion)
    {
        var sb = new StringBuilder();
        var t0 = DateTime.UtcNow;

        renderer.GameVersion = gameVersion;
        renderer.ClearMaterials();
        renderer.SetMesh(scene.Mesh);

        int ok = 0, textures = 0, failed = scene.Failed;
        foreach (var s in scene.Slices)
        {
            var mat = renderer.BuildMaterial(s.Name, s.Vs, s.Ps, s.Start, s.Count, out var rep,
                s.VsDesc, s.PsDesc, StateDescription.Geometry);
            if (mat is null) { failed++; sb.AppendLine($"   ! {s.Name}: {rep.Error}"); continue; }

            // M245 culling + M246 sorting. Scene geometry writes depth, so the depth buffer decides what is
            // in front and grouping by pipeline cannot change the image.
            mat.Bounds = SliceBounds(scene.Mesh, s.Start, s.Count);
            mat.SortableByPipeline = true;

            foreach (var (target, key) in s.Textures)
            {
                if (renderer.TryBindCached(mat, target, key)) { textures++; continue; }
                if (!scene.Textures.TryGetValue(key, out var img)) continue;
                renderer.SetTexture(mat, target, key, img.Rgba, img.Width, img.Height);
                textures++;
            }

            renderer.AddMaterial(mat);
            ok++;
        }

        double commitMs = (DateTime.UtcNow - t0).TotalMilliseconds;
        sb.AppendLine($"{scene.Mesh.Vertices.Length:n0} vertices, {scene.Mesh.TriangleCount:n0} triangles");
        sb.AppendLine($"{scene.RawGroups} groups -> {scene.Slices.Count} slices");
        sb.AppendLine($"{ok} material(s) live, {failed} unresolved, {textures} texture binding(s)");
        sb.AppendLine($"pipelines: {renderer.PipelineCacheHits} hit, {renderer.PipelineCacheMisses} built, "
                      + $"{renderer.CachedPipelineCount} resident");
        sb.AppendLine($"timing: {scene.PrepareMs:F0} ms off-thread + {commitMs:F0} ms on the UI thread");
        return new Result(ok, failed, textures, scene.Slices.Count, sb.ToString());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>M226: coalesce runs that are already adjacent AND want the same atlas page. The lightmap
    /// has to be part of the key: it is a per-GROUP property, and keying it by material name handed 71.5%
    /// of Map12's lit groups another mesh's atlas page.</summary>
    private static List<(string Material, int Start, int Count, string Lightmap)> MergeSlices(MapGeoAsset map)
    {
        var ordered = map.Groups
            .Select(g => (g.Material, Start: g.StartIndex, Count: g.IndexCount, Lightmap: g.LightmapTexture))
            .OrderBy(x => x.Start)
            .ToList();

        var merged = new List<(string Material, int Start, int Count, string Lightmap)>();
        foreach (var s in ordered)
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
        return merged;
    }

    /// <summary>M210: a material sampler binds to the shader texture named after it plus "__TX". Anything
    /// ending _SharedTexture is engine-supplied and never material-bound.</summary>
    private static string? ResolveTextureTarget(string sampler, DxbcShader ps)
    {
        var exact = ps.Textures.FirstOrDefault(t =>
            t.Name.Equals(sampler + "__TX", StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Name;
        return ps.Textures.FirstOrDefault(t =>
            t.Name.Equals(sampler, StringComparison.OrdinalIgnoreCase))?.Name;
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
