using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Interop;

/// <summary>What a snapshot contains, plus anything about it the user needs told.</summary>
public sealed record BridgeSnapshot(IReadOnlyList<BridgeMesh> Meshes, IReadOnlyList<string> Notes)
{
    /// <summary>M841: per-mesh metadata for the pull header (<see cref="MapGeoBlenderBridge.Describe"/>), in
    /// the order of <see cref="Meshes"/>. Empty when the caller has none.</summary>
    public IReadOnlyList<Dictionary<string, object?>> MeshInfo { get; init; } = Array.Empty<Dictionary<string, object?>>();

    /// <summary>M841: map-wide values for the pull header (visibility layer names, lightmap colour scale).</summary>
    public Dictionary<string, object?> MapInfo { get; init; } = new();
}

/// <summary>
/// M580: turn the open map into meshes Blender can hold, and put Blender's placements back.
///
/// <para>M841 (protocol 3, MapgeoAddon): the snapshot also carries each submesh range with its material,
/// the raw lightmap channel and the vertex colour, so Blender can show the map as MapgeoAddon's own
/// importer would. Materials still never round-trip through a file: Blender sends EDITS (an assignment,
/// a parameter) and the editor applies them through its own commands - ReyEngine stays the only writer
/// of the .mapgeo and the .materials.bin.</para>
///
/// <para>Each mesh is sent about its PIVOT with its transform carried separately, which is what makes the
/// round trip exact rather than approximate — see <see cref="BridgeMesh"/>.</para>
/// </summary>
public static class MapGeoBlenderBridge
{
    /// <summary>
    /// Snapshot every mesh of <paramref name="map"/>.
    /// </summary>
    /// <param name="include">Optional filter — used to send only the selection.</param>
    public static BridgeSnapshot Snapshot(MapGeoAsset map, Func<MapGeoMesh, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        var meshes = new List<BridgeMesh>();
        var info = new List<Dictionary<string, object?>>();
        var notes = new List<string>();
        int grouped = 0, degenerate = 0;

        // M841: groups by their mesh, once. The old per-mesh walk over EVERY group's indices was
        // O(meshes x indices) - billions of comparisons on a full Summoner's Rift.
        var groupsByMesh = new Dictionary<int, List<MapGeoGroup>>();
        foreach (var g in map.Groups)
        {
            if (g.MeshIndex < 0) continue;
            if (!groupsByMesh.TryGetValue(g.MeshIndex, out var list)) groupsByMesh[g.MeshIndex] = list = new List<MapGeoGroup>();
            list.Add(g);
        }

        foreach (var mesh in map.Meshes)
        {
            if (include is not null && !include(mesh)) continue;
            if (mesh.VertexCount <= 0) { degenerate++; continue; }

            // A group matrix is a batch transform made THIS session; it is never read from the file. The
            // bridge models a mesh's own offset/rotation/scale and has nowhere to put a second matrix that
            // applies after it, so rather than send geometry that would snap back on the first push, these
            // are reported and left out.
            if (!mesh.GroupMatrix.IsIdentity) { grouped++; continue; }

            var pristine = map.OriginalPositionsOf(mesh);
            var positions = new float[pristine.Length];
            for (int i = 0; i + 2 < pristine.Length; i += 3)
            {
                positions[i] = pristine[i] - mesh.Pivot.X;
                positions[i + 1] = pristine[i + 1] - mesh.Pivot.Y;
                positions[i + 2] = pristine[i + 2] - mesh.Pivot.Z;
            }

            var pristineNormals = map.OriginalNormalsOf(mesh);
            var normals = pristineNormals.Length == pristine.Length ? pristineNormals.ToArray() : null;

            // UV0 travels because a reshaped mesh without it would come back untextured. The lightmap
            // channel deliberately does not: a changed shape invalidates a bake anyway.
            float[]? uvs = null;
            if (map.Uvs is { Length: > 0 } source && (mesh.VertexStart + mesh.VertexCount) * 2 <= source.Length)
                uvs = source.AsSpan(mesh.VertexStart * 2, mesh.VertexCount * 2).ToArray();

            // M841: the lightmap channel as the FILE holds it (raw TEXCOORD7, before the BakedLight
            // scale/bias), so MapgeoAddon can show it the way its own importer does and keep it exact.
            float[]? lightmap = null;
            if (mesh.HasLightmapUv && map.RawLightmapUvs is { Length: > 0 } raw
                && (mesh.VertexStart + mesh.VertexCount) * 2 <= raw.Length)
                lightmap = raw.AsSpan(mesh.VertexStart * 2, mesh.VertexCount * 2).ToArray();
            float[]? colors = null;
            if (mesh.HasVertexColor && map.Colors is { Length: > 0 } col
                && (mesh.VertexStart + mesh.VertexCount) * 4 <= col.Length)
                colors = col.AsSpan(mesh.VertexStart * 4, mesh.VertexCount * 4).ToArray();
            // M842: TEXCOORD5, the grass clump pivot of a VertexDeform bush - a POSITION, so it travels in the
            // same pivot-relative world space as the vertices and comes back through the same transform.
            float[]? grass = null;
            if (Array.IndexOf(mesh.Attributes, "Texcoord5") >= 0 && map.GrassPivots is { Length: > 0 } gp
                && (mesh.VertexStart + mesh.VertexCount) * 3 <= gp.Length)
            {
                grass = gp.AsSpan(mesh.VertexStart * 3, mesh.VertexCount * 3).ToArray();
                for (int i = 0; i + 2 < grass.Length; i += 3)
                { grass[i] -= mesh.Pivot.X; grass[i + 1] -= mesh.Pivot.Y; grass[i + 2] -= mesh.Pivot.Z; }
            }

            var (indices, submeshes) = IndicesOf(map, mesh,
                groupsByMesh.TryGetValue(mesh.Index, out var own) ? own : (IReadOnlyList<MapGeoGroup>)Array.Empty<MapGeoGroup>());
            var placement = CurrentTransform(mesh);
            meshes.Add(new BridgeMesh(mesh.Index, mesh.Name, mesh.Pivot, positions, normals, uvs,
                indices, placement.Location, placement.RotationDegrees, placement.Scale)
            {
                LightmapUvs = lightmap,
                Colors = colors,
                GrassPivots = grass,
                Submeshes = submeshes,
            });
            info.Add(Describe(mesh));
        }

        if (grouped > 0)
            notes.Add($"{grouped:n0} mesh(es) skipped: they carry a batch (multi-select) transform from this "
                      + "session, which the bridge cannot represent. Save the map, reload it, and they will send.");
        if (degenerate > 0) notes.Add($"{degenerate:n0} mesh(es) skipped: no vertices.");
        return new BridgeSnapshot(meshes, notes) { MeshInfo = info };
    }

    /// <summary>
    /// The triangle list for one mesh, rebased so vertex 0 is the mesh's own first vertex.
    ///
    /// <para>The decoder concatenates every mesh's vertices into one array and every mesh's indices into
    /// another, so the raw indices are absolute. Blender wants each object indexing its own vertices.</para>
    /// </summary>
    /// <remarks>M841: per submesh, in submesh order, each with the material it will be drawn with once the
    /// pending edits are written (<see cref="MapGeoMesh.EffectiveSubmeshMaterial"/>), so Blender shows what
    /// the editor shows and an assignment pushed back names the submesh by the same number.</remarks>
    private static (uint[] Indices, IReadOnlyList<BridgeSubmesh> Submeshes) IndicesOf(
        MapGeoAsset map, MapGeoMesh mesh, IReadOnlyList<MapGeoGroup> groups)
    {
        var indices = new List<uint>();
        var submeshes = new List<BridgeSubmesh>(groups.Count);
        uint first = (uint)mesh.VertexStart;
        uint last = (uint)(mesh.VertexStart + mesh.VertexCount);

        for (int k = 0; k < groups.Count; k++)
        {
            var group = groups[k];
            int start = indices.Count;
            for (int i = group.StartIndex; i + 2 < group.StartIndex + group.IndexCount && i + 2 < map.Indices.Length; i += 3)
            {
                uint a = map.Indices[i], b = map.Indices[i + 1], c = map.Indices[i + 2];
                if (a < first || a >= last || b < first || b >= last || c < first || c >= last) continue;
                // A face collapsed to a point is how this editor deletes one; Blender would reject it.
                if (a == b || b == c || a == c) continue;
                indices.Add(a - first); indices.Add(b - first); indices.Add(c - first);
            }
            string material = mesh.Materials.Count > 0 ? mesh.EffectiveSubmeshMaterial(k) : group.Material;
            submeshes.Add(new BridgeSubmesh(material, start, indices.Count - start));
        }
        return (indices.ToArray(), submeshes);
    }

    /// <summary>
    /// M841: what MapgeoAddon's importer records on an object beside its geometry - visibility, the
    /// controller, the lightmap and baked-paint channels, texture overrides - as JSON-ready values. Sent in
    /// the pull header next to the binary block.
    /// </summary>
    public static Dictionary<string, object?> Describe(MapGeoMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var d = new Dictionary<string, object?>
        {
            ["i"] = mesh.Index,
            ["name"] = mesh.Name,
            ["visibility"] = mesh.EffectiveVisibility,
            ["controller"] = mesh.EffectiveController,
            ["render_region"] = mesh.RegionHash,
            ["render_flags"] = mesh.RenderFlags,
            ["disable_backface_culling"] = mesh.EffectiveDisableBackface,
            ["file_materials"] = mesh.Materials,
            ["has_lightmap_uv"] = mesh.HasLightmapUv,
            ["has_vertex_color"] = mesh.HasVertexColor,
        };
        if (mesh.BakedLightTexture is { Length: > 0 } lm)
            d["lightmap"] = new Dictionary<string, object?>
            {
                ["texture"] = lm,
                ["scale"] = new[] { mesh.BakedLightScale.X, mesh.BakedLightScale.Y },
                ["bias"] = new[] { mesh.BakedLightBias.X, mesh.BakedLightBias.Y },
            };
        if (mesh.BakedPaintTexture is { Length: > 0 } || mesh.BakedPaintScale != Vector2.One || mesh.BakedPaintBias != Vector2.Zero)
            d["baked_paint"] = new Dictionary<string, object?>
            {
                ["texture"] = mesh.BakedPaintTexture ?? "",
                ["scale"] = new[] { mesh.BakedPaintScale.X, mesh.BakedPaintScale.Y },
                ["bias"] = new[] { mesh.BakedPaintBias.X, mesh.BakedPaintBias.Y },
            };
        if (mesh.StationaryLightTexture is { Length: > 0 } sl) d["stationary_light"] = sl;
        if (mesh.TextureOverrides.Count > 0) d["texture_overrides"] = mesh.TextureOverrides;
        return d;
    }

    /// <summary>
    /// Apply placements from Blender. Returns the meshes that actually moved, for the undo entry.
    /// </summary>
    /// <remarks>
    /// Absolute, not incremental: ReyEngine stores a mesh's offset, rotation and scale relative to its
    /// ORIGINAL state, and Blender's object transform is relative to the same original because that is the
    /// geometry it was handed. So the two describe the same thing and no delta arithmetic is involved -
    /// which also means pushing the same transform twice is a no-op rather than a double move.
    /// </remarks>
    public static IReadOnlyList<int> ApplyTransforms(
        MapGeoAsset map, IReadOnlyList<BridgeTransform> transforms, out IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(transforms);
        var changed = new List<int>();
        var problems = new List<string>();
        var byIndex = map.Meshes.ToDictionary(m => m.Index);

        foreach (var t in transforms)
        {
            if (!byIndex.TryGetValue(t.Index, out var mesh))
            { problems.Add($"mesh {t.Index} is not in the open map"); continue; }
            if (!Finite(t.Location) || !Finite(t.RotationDegrees) || !Finite(t.Scale))
            { problems.Add($"'{mesh.Name}' sent a non-finite transform"); continue; }
            if (t.Scale.X == 0f || t.Scale.Y == 0f || t.Scale.Z == 0f)
            { problems.Add($"'{mesh.Name}' sent a zero scale, which would collapse it"); continue; }

            // Against the pivot BLENDER had, not the one the mesh has now. A face edit moves the bbox
            // centre, and measuring from the new one turns an untouched object into a jump of exactly
            // the pivot's own displacement - down after one edit, back up after the next.
            var anchor = t.Anchor ?? mesh.Pivot;
            var offset = t.Location - anchor;
            if (Same(mesh.Offset, offset) && Same(mesh.RotationDegrees, t.RotationDegrees) && Same(mesh.Scale, t.Scale))
                continue;

            mesh.Offset = offset;
            mesh.RotationDegrees = t.RotationDegrees;
            mesh.Scale = t.Scale;
            map.ApplyMeshTransform(mesh);
            changed.Add(mesh.Index);
        }

        notes = problems;
        return changed;
    }

    /// <summary>
    /// Turn geometry that came back from Blender into something the reshaper can write.
    /// </summary>
    /// <remarks>
    /// The spaces differ and it matters. Blender holds the mesh PIVOT-RELATIVE in world space, because
    /// that is what makes its object transform mean the same thing as ReyEngine's. A vertex buffer holds
    /// mesh-LOCAL coordinates, which the file's own per-mesh transform then places. So the round trip is
    /// pivot-relative -> world -> local, and skipping the last step writes geometry that is correct only
    /// for the meshes whose transform happens to be identity.
    /// </remarks>
    public static MeshReshape? ToReshape(MapGeoAsset map, BridgeMesh sent, out string? error)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(sent);
        error = null;

        var mesh = map.Meshes.FirstOrDefault(m => m.Index == sent.Index);
        if (mesh is null) { error = $"mesh {sent.Index} is not in the open map"; return null; }
        if (sent.Positions.Length == 0 || sent.Positions.Length % 3 != 0)
        { error = $"'{mesh.Name}' sent {sent.Positions.Length} position floats"; return null; }
        if (!Matrix4x4.Invert(mesh.Transform, out var toLocal))
        { error = $"'{mesh.Name}' has a transform that cannot be inverted, so its geometry has no local form"; return null; }

        int vertices = sent.Positions.Length / 3;
        var positions = new float[sent.Positions.Length];
        for (int i = 0; i < vertices; i++)
        {
            var world = new Vector3(sent.Positions[i * 3], sent.Positions[i * 3 + 1], sent.Positions[i * 3 + 2])
                        + mesh.Pivot;
            var local = Vector3.Transform(world, toLocal);
            positions[i * 3] = local.X; positions[i * 3 + 1] = local.Y; positions[i * 3 + 2] = local.Z;
        }

        float[]? normals = null;
        if (sent.Normals is { } sentNormals && sentNormals.Length == sent.Positions.Length)
        {
            // Normals do not transform like positions: the inverse TRANSPOSE is what survives a non-uniform
            // scale, and a mesh scaled unevenly would otherwise light as though it were not.
            var normalMatrix = Matrix4x4.Transpose(toLocal);
            normals = new float[sentNormals.Length];
            for (int i = 0; i < vertices; i++)
            {
                var n = Vector3.TransformNormal(
                    new Vector3(sentNormals[i * 3], sentNormals[i * 3 + 1], sentNormals[i * 3 + 2]), normalMatrix);
                if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n);
                normals[i * 3] = n.X; normals[i * 3 + 1] = n.Y; normals[i * 3 + 2] = n.Z;
            }
        }

        float[]? uvs = sent.Uvs is { } sentUvs && sentUvs.Length == vertices * 2 ? sentUvs : null;

        // M842: the grass pivot is a position: pivot-relative world -> local, like the vertices
        float[]? grass = null;
        if (sent.GrassPivots is { } gp && gp.Length == vertices * 3)
        {
            grass = new float[gp.Length];
            for (int i = 0; i < vertices; i++)
            {
                var local = Vector3.Transform(new Vector3(gp[i * 3], gp[i * 3 + 1], gp[i * 3 + 2]) + mesh.Pivot, toLocal);
                grass[i * 3] = local.X; grass[i * 3 + 1] = local.Y; grass[i * 3 + 2] = local.Z;
            }
        }
        var remap = PendingMaterialRemap(mesh);
        return new MeshReshape(mesh.Index, positions, normals, uvs, sent.Indices)
        {
            LightmapUvs = sent.LightmapUvs is { } lm && lm.Length == vertices * 2 ? lm : null,
            Colors = sent.Colors is { } col && col.Length == vertices * 4 ? col : null,
            GrassPivots = grass,
            Submeshes = sent.Submeshes.Count > 0
                ? sent.Submeshes.Select(x => new ReshapeSubmesh(
                    remap.TryGetValue(x.Material, out var to) ? to : x.Material, x.StartIndex, x.IndexCount)).ToList()
                : null,
        };
    }

    /// <summary>
    /// M842 review: a submesh table names materials by what Blender PULLED. Pulled before a material edit that
    /// is still pending in ReyEngine, it names the file's old material - and the save writes the material edit
    /// first and the reshape after it, so the reshape would silently put the old material back. Old -> edited,
    /// for every file material the pending edits replace unambiguously (one old name edited to two different
    /// materials on two submeshes is left alone: there is no telling which one a Blender range meant).
    /// </summary>
    public static IReadOnlyDictionary<string, string> PendingMaterialRemap(MapGeoMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        // every material each file name becomes (an unedited submesh of the same name keeps it: ambiguous too)
        var becomes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        for (int s = 0; s < mesh.Materials.Count; s++)
        {
            string old = mesh.Materials[s], now = mesh.EffectiveSubmeshMaterial(s);
            if (now.Length == 0) now = old;
            if (!becomes.TryGetValue(old, out var set)) becomes[old] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(now);
        }
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (old, set) in becomes)
            if (set.Count == 1 && set.First() is var now && !string.Equals(old, now, StringComparison.Ordinal))
                remap[old] = now;
        return remap;
    }

    /// <summary>Where a mesh's Blender object should sit, and how it should be oriented, right now.</summary>
    public static BridgeTransform CurrentTransform(MapGeoMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return new BridgeTransform(mesh.Index, mesh.Pivot + mesh.Offset, mesh.RotationDegrees, mesh.Scale,
            mesh.Pivot);
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Float equality with enough slack for a value that has been through a decimal round trip.</summary>
    private static bool Same(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;
}
