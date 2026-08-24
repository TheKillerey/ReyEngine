using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Interop;

/// <summary>What a snapshot contains, plus anything about it the user needs told.</summary>
public sealed record BridgeSnapshot(IReadOnlyList<BridgeMesh> Meshes, IReadOnlyList<string> Notes);

/// <summary>
/// M580: turn the open map into meshes Blender can hold, and put Blender's placements back.
///
/// <para>Geometry only, by design — no materials, no submesh ranges, no lightmap channels. Those are
/// bound to the mapgeo's own layout and to material state Blender has no way to represent; round-tripping
/// them through an .blend would be a good way to lose them. What Blender is genuinely better at is shape
/// and placement, so that is what crosses.</para>
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
        var notes = new List<string>();
        int grouped = 0, degenerate = 0;

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

            var placement = CurrentTransform(mesh);
            meshes.Add(new BridgeMesh(mesh.Index, mesh.Name, mesh.Pivot, positions, normals,
                IndicesOf(map, mesh), placement.Location, placement.RotationDegrees, placement.Scale));
        }

        if (grouped > 0)
            notes.Add($"{grouped:n0} mesh(es) skipped: they carry a batch (multi-select) transform from this "
                      + "session, which the bridge cannot represent. Save the map, reload it, and they will send.");
        if (degenerate > 0) notes.Add($"{degenerate:n0} mesh(es) skipped: no vertices.");
        return new BridgeSnapshot(meshes, notes);
    }

    /// <summary>
    /// The triangle list for one mesh, rebased so vertex 0 is the mesh's own first vertex.
    ///
    /// <para>The decoder concatenates every mesh's vertices into one array and every mesh's indices into
    /// another, so the raw indices are absolute. Blender wants each object indexing its own vertices.</para>
    /// </summary>
    private static uint[] IndicesOf(MapGeoAsset map, MapGeoMesh mesh)
    {
        var indices = new List<uint>();
        uint first = (uint)mesh.VertexStart;
        uint last = (uint)(mesh.VertexStart + mesh.VertexCount);

        foreach (var group in map.Groups)
        {
            for (int i = group.StartIndex; i + 2 < group.StartIndex + group.IndexCount && i + 2 < map.Indices.Length; i += 3)
            {
                uint a = map.Indices[i], b = map.Indices[i + 1], c = map.Indices[i + 2];
                if (a < first || a >= last || b < first || b >= last || c < first || c >= last) continue;
                // A face collapsed to a point is how this editor deletes one; Blender would reject it.
                if (a == b || b == c || a == c) continue;
                indices.Add(a - first); indices.Add(b - first); indices.Add(c - first);
            }
        }
        return indices.ToArray();
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

            var offset = t.Location - mesh.Pivot;
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

    /// <summary>Where a mesh's Blender object should sit, and how it should be oriented, right now.</summary>
    public static BridgeTransform CurrentTransform(MapGeoMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return new BridgeTransform(mesh.Index, mesh.Pivot + mesh.Offset, mesh.RotationDegrees, mesh.Scale);
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Float equality with enough slack for a value that has been through a decimal round trip.</summary>
    private static bool Same(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;
}
