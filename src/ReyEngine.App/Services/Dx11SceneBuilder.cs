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
    /// <summary><paramref name="Reasons"/> (M278) is the first example of each distinct failure kind. A
    /// caller that logs <paramref name="Failed"/> without one of these is reporting a number nobody can
    /// act on - which is what "0 material(s), 21 unresolved" was.</summary>
    public sealed record Result(int Materials, int Failed, int Textures, int Slices, string Report,
        IReadOnlyList<string> Reasons);

    /// <summary>One slice, resolved and ready to become a pipeline. Everything here is CPU-side.
    /// <para>M279: <paramref name="Profile"/> is the material's OWN render state, derived from its
    /// technique/pass in the .materials.bin. Commit reads exactly one field off it - see there for what is
    /// honoured and what deliberately is not.</para></summary>
    public sealed record PreparedSlice(
        string Name, int Start, int Count,
        DxbcShader Vs, DxbcShader Ps,
        ShaderDescription VsDesc, ShaderDescription PsDesc,
        IReadOnlyList<(string Target, string Key)> Textures,
        IReadOnlyList<(string Name, float[] Value)> Parameters,
        MaterialProfile Profile,
        /// <summary>M292: the mapgeo group this slice was merged from. MergeSlices keys on visibility
        /// identity, so every group in the run resolves the same and this one index speaks for all of
        /// them - which is what lets the host drive Visible from the per-group array the GL viewport
        /// already uses, instead of DX11 re-deriving the dragon/baron/region rules.</summary>
        int GroupIndex = -1);

    /// <summary>Everything a scene needs that does not touch D3D.</summary>
    public sealed class PreparedScene
    {
        public required PreviewMesh Mesh { get; init; }
        public required List<PreparedSlice> Slices { get; init; }
        public required Dictionary<string, TextureImage> Textures { get; init; }
        public int Failed { get; set; }
        public int RawGroups { get; set; }
        public double PrepareMs { get; set; }

        /// <summary>M278: why the failures failed, first example per distinct kind, in the order they were
        /// first hit. The report used to say "21 unresolved" and nothing else, so a shader cache whose
        /// entries had all been renamed underneath us read exactly like a scene bug - and was chased as one
        /// for an afternoon. A categorical failure should name itself.</summary>
        public Dictionary<string, string> FailureReasons { get; } = new();

        public void Fail(string kind, string detail)
        {
            Failed++;
            if (FailureReasons.Count < 8) FailureReasons.TryAdd(kind, detail);
        }
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
            { scene.Fail("no material binding or no renderShader", slice.Material); continue; }

            string full = "assets/shaders/generated/" + b.RenderShader!.Trim('/');
            IReadOnlyDictionary<string, string>? feat = null;
            IReadOnlyDictionary<string, bool>? swDef = null;
            perms?.TryGetShaderDefs(b.RenderShader!, out feat, out swDef);

            string vsPath = ShaderCacheReader.TocPathFor(full, DxbcStage.Vertex);
            string psPath = ShaderCacheReader.TocPathFor(full, DxbcStage.Pixel);

            var vsToc = cache.ReadToc(vsPath);
            var psToc = cache.ReadToc(psPath);
            if (vsToc is null || psToc is null)
            { scene.Fail("no TOC in the shader cache", vsToc is null ? vsPath : psPath); continue; }

            var vp = ShaderCacheReader.ResolvePermutation(vsToc, b.Macros, b.Switches, feat, swDef, out var vwhy);
            var pp = ShaderCacheReader.ResolvePermutation(psToc, b.Macros, b.Switches, feat, swDef, out var pwhy);
            if (vp is null || pp is null)
            { scene.Fail("no cooked permutation", $"{b.Name}: {(vp is null ? vwhy : pwhy)}"); continue; }

            var vs = cache.LoadShader(vsPath, vp.BlobIndex, out var vsErr);
            var ps = cache.LoadShader(psPath, pp.BlobIndex, out var psErr);
            if (vs is null || ps is null)
            { scene.Fail("bytecode would not load", (vs is null ? vsErr : psErr) ?? "(no reason given)"); continue; }

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

            // M255: the material's OWN parameters. Missing these is what made the viewport draw black
            // foliage while the shader preview - which has always written them - drew the same map
            // correctly. TintColor lives at $Globals+0 and staticmesh/vertexdeform does
            //     mul r2.xyz, diffuse, cb0[0].xyzx
            // so an unwritten TintColor is zero and everything multiplied by it is black. Any material
            // whose shader multiplies by an authored parameter has the same failure.
            var parameters = new List<(string, float[])>();
            var authored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prm in b.Parameters)
                if (prm.TryGetVector4(out var pv))
                {
                    parameters.Add((prm.Name, new[] { pv.X, pv.Y, pv.Z, pv.W }));
                    authored.Add(prm.Name);
                }

            // M257: fall back to the SHADER's declared default for anything the material leaves out.
            // shaders.bin records these on 343 of its 347 definitions, as parameters[].name + .data.
            // Without them an unauthored parameter is simply unwritten, i.e. zero - and zero is not
            // "unspecified", it is a value the shader multiplies by. Authored always wins.
            if (perms is not null && perms.TryGetParameterDefaults(b.RenderShader!, out var defs))
                foreach (var (dn, dv) in defs)
                    if (!authored.Contains(dn)) parameters.Add((dn, dv));

            scene.Slices.Add(new PreparedSlice(b.Name, slice.Start, slice.Count, vs, ps,
                new ShaderDescription(full, DxbcStage.Vertex, vp.Key, vp.BlobIndex, b.Macros, vs),
                new ShaderDescription(full, DxbcStage.Pixel, pp.Key, pp.BlobIndex, b.Macros, ps),
                wanted, parameters, b.Profile, slice.Group));
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

        int ok = 0, textures = 0, failed = scene.Failed, transparent = 0;
        var reasons = new Dictionary<string, string>(scene.FailureReasons);
        foreach (var s in scene.Slices)
        {
            var mat = renderer.BuildMaterial(s.Name, s.Vs, s.Ps, s.Start, s.Count, out var rep,
                s.VsDesc, s.PsDesc, StateDescription.Geometry);
            if (mat is null)
            {
                failed++;
                sb.AppendLine($"   ! {s.Name}: {rep.Error}");
                if (reasons.Count < 8) reasons.TryAdd("pipeline would not build", $"{s.Name}: {rep.Error}");
                continue;
            }

            mat.Bounds = SliceBounds(scene.Mesh, s.Start, s.Count);   // M245 frustum culling
            mat.MapGroupIndex = s.GroupIndex;                          // M292: lets the host filter it

            // M279: the material's authored depth-write decides BOTH of these, and the two have to agree.
            //
            // M246 set SortableByPipeline unconditionally on the reasoning that "scene geometry writes
            // depth, so the depth buffer decides what is in front and grouping by pipeline cannot change
            // the image". That is true of opaque geometry and false of every transparent pass - 83 of
            // Map453/jade_container's 426 slices and 204 of Map12/bloom's 1,389. A decal authored
            // "transparent cutout - blend - no depth-write" was still given the depth mask, so it stamped
            // depth at its own plane and then DEPTH-REJECTED the paving it was supposed to composite over -
            // and because the sort is by PipelineId, whether that happened at all depended on cache
            // assignment order, which is why it looked intermittent. Measured: base_chasm1's decal sorted
            // to draw position 395 of 426 while the ground under it drew at 407-414.
            //
            // Measured on the pixels, not inferred: one decal isolated over its own paving, mean
            // per-channel distance from the ideal composite across its partially transparent margin, 0..255.
            // base_chasm1 33.2 -> 6.2, new_stone_road 70.7 -> 0.5, grasstuft 39.1 -> 5.8. The decal's OPAQUE
            // core stayed on screen throughout (79% -> 90%), which is what rules out the other explanation,
            // that the margin "improved" because the decal stopped drawing.
            //
            // Setting WritesDepth false is what reaches the device (ShaderPreviewRenderer picks the
            // no-write depth state per material). Setting SortableByPipeline false is what puts the draw in
            // the order-preserving TAIL the sort comparator already maintains, so every transparent slice
            // lands after all the solid geometry - the same two passes ViewportMeshRenderer runs on the GL
            // side off the same predicate (AlphaMode >= 2 is exactly !MaterialProfile.DepthWrite), which is
            // why the two viewports now agree instead of only one of them being right. GL needed no change.
            //
            // The cost is real and was measured: pipeline state changes on a full Map12/bloom draw go from
            // 24 to 93, because the tail cannot be grouped by pipeline. Frame time did not move out of the
            // harness's own repeat spread.
            //
            // NOT honoured here, deliberately. Per-material back-face culling: one rasterizer state is
            // built from PreviewSettings, and StateDescription.Geometry's cull-off is an M240 decision
            // carrying its own live-game evidence, so it has to be settled on that evidence rather than
            // smuggled in behind a decal fix (it is also 95% of materials, not 17%). Blend FACTORS other
            // than SrcAlpha/InvSrcAlpha: 52 of 9,036 materials censused across four maps author anything
            // else, all of them on Map22. Back-to-front sorting inside the transparent tail: the GL path
            // does not do it either, so doing it here would break the parity this change just bought.
            // And the alpha CUTOUT needs nothing at all - it is a discard compiled into Riot's own pixel
            // shader ("lt r1.x, r0.w, cb0[2].x" then "discard_nz"), driven by the AlphaTestValue this
            // builder already puts in mat.Params, so it was never a blend-state question.
            bool depthWrite = s.Profile.DepthWrite;
            mat.WritesDepth = depthWrite;
            mat.SortableByPipeline = depthWrite;
            if (!depthWrite) transparent++;

            foreach (var (name, value) in s.Parameters) mat.Params[name] = value;

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
        // M279: worth a line of its own, because it is the number that silently used to be zero. If a map
        // that visibly has decals reports 0 transparent slices, the profile classification is what broke,
        // not the renderer.
        if (transparent > 0)
            sb.AppendLine($"{transparent} transparent slice(s): no depth write, drawn after the solid pass in authored order");
        // M278: never report a count of failures without a reason for them. The FIRST distinct reason is
        // what localises a categorical failure; the rest are usually the same one repeated.
        foreach (var (kind, detail) in reasons) sb.AppendLine($"   unresolved - {kind}: {Trim(detail)}");
        sb.AppendLine($"pipelines: {renderer.PipelineCacheHits} hit, {renderer.PipelineCacheMisses} built, "
                      + $"{renderer.CachedPipelineCount} resident");
        sb.AppendLine($"timing: {scene.PrepareMs:F0} ms off-thread + {commitMs:F0} ms on the UI thread");
        return new Result(ok, failed, textures, scene.Slices.Count, sb.ToString(),
            reasons.Select(kv => $"{kv.Key}: {Trim(kv.Value)}").ToList());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Keep a reason on one line of the status panel. ResolvePermutation's explanation lists every
    /// axis it pinned and can run to several hundred characters.</summary>
    private static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= 160 ? s : s[..157] + "...";
    }

    /// <summary>M226: coalesce runs that are already adjacent AND want the same atlas page. The lightmap
    /// has to be part of the key: it is a per-GROUP property, and keying it by material name handed 71.5%
    /// of Map12's lit groups another mesh's atlas page.</summary>
    /// <para>M292: the merge is ALSO keyed on the group's visibility identity - its effective visibility
    /// bitmask, its controller hash and its render region - and each merged run remembers the first group
    /// it came from.</para>
    ///
    /// <para>Visibility is a per-GROUP property, and one material carries a single Visible flag, so a run
    /// that spans groups with different visibility cannot express them: hiding a dragon layer would have
    /// to hide its neighbours too, or not hide anything. Keying on the identity keeps every run
    /// homogeneous, which is what makes a per-material flag sufficient. Note this method deliberately does
    /// not know the RULES - only that groups differing in these three inputs must not be fused - so the
    /// rules stay in MapVisibilityResolver, shared with the OpenGL viewport.</para>
    private static List<(string Material, int Start, int Count, string Lightmap, int Group)> MergeSlices(MapGeoAsset map)
    {
        // Sourced exactly as MainWindowViewModel.ApplyMapVisibility does, including the live per-mesh
        // edits, or the two viewports would disagree about which layer a group belongs to.
        var meshByIdx = map.Meshes.ToDictionary(m => m.Index);
        (int Flags, uint Ctrl, uint Region) Identity(MapGeoGroup g)
        {
            int flags = g.VisibilityFlags;
            uint ctrl = g.ControllerHash, region = 0;
            if (g.MeshIndex >= 0 && meshByIdx.TryGetValue(g.MeshIndex, out var src))
            { flags = src.EffectiveVisibility; ctrl = src.EffectiveController; region = src.RegionHash; }
            return (flags, ctrl, region);
        }

        var ordered = map.Groups
            .Select((g, i) => (g.Material, Start: g.StartIndex, Count: g.IndexCount,
                               Lightmap: g.LightmapTexture, Group: i, Id: Identity(g)))
            .OrderBy(x => x.Start)
            .ToList();

        var merged = new List<(string Material, int Start, int Count, string Lightmap, int Group)>();
        (int Flags, uint Ctrl, uint Region) lastId = default;
        foreach (var s in ordered)
        {
            if (merged.Count > 0)
            {
                var p = merged[^1];
                if (p.Start + p.Count == s.Start
                    && lastId == s.Id
                    && string.Equals(p.Material, s.Material, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.Lightmap, s.Lightmap, StringComparison.OrdinalIgnoreCase))
                { merged[^1] = p with { Count = p.Count + s.Count }; continue; }
            }
            merged.Add((s.Material, s.Start, s.Count, s.Lightmap, s.Group));
            lastId = s.Id;
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
