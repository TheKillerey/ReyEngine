using System.Numerics;

namespace ReyEngine.Formats.MapGeo;

/// <summary>What to do to one triangle of the open map.</summary>
public enum FaceOp
{
    /// <summary>Collapse it to zero area. See <see cref="MapGeoFaceWriter"/> for why not removal.</summary>
    Delete,
    /// <summary>Reverse its winding, so it faces the other way.</summary>
    Flip,
    /// <summary>Move the vertices it uses by <see cref="FaceEdit.Offset"/>.</summary>
    Move,
}

/// <param name="Triangle">Index into the decoded asset's combined triangle list, as
/// <c>MeshRayHit.Triangle</c> reports it.</param>
public readonly record struct FaceEdit(int Triangle, FaceOp Op, Vector3 Offset = default);

/// <summary>
/// M566: face editing, written back into the mapgeo.
///
/// <para><b>Every operation preserves the file's length</b>, which is the whole reason the set is what it
/// is. A mapgeo is a run of count-prefixed arrays with no offset table: submesh ranges are offsets into a
/// shared index buffer, so physically removing three indices would shift every later submesh in the same
/// buffer and every mesh that shares it. Deleting a face therefore DEGENERATES it - all three indices set
/// to the same vertex - which is zero area, discarded by the rasteriser before it costs a fragment, and
/// leaves every offset in the file untouched. Flip swaps two indices in place, and Move rewrites vertex
/// positions in place.</para>
///
/// <para>Move shifts VERTICES, so a vertex shared with a neighbouring face drags that face's corner too.
/// That is what Blender does without an explicit split, and splitting would change the vertex count and
/// with it the file's length.</para>
/// </summary>
public static class MapGeoFaceWriter
{
    /// <summary>
    /// Applies <paramref name="edits"/> to <paramref name="mapgeo"/>. Returns null with a reason rather
    /// than throwing, because this runs behind an interactive tool.
    /// </summary>
    public static byte[]? TryApply(byte[] mapgeo, MapGeoAsset asset, IReadOnlyList<FaceEdit> edits,
        out string? error)
    {
        error = null;
        if (edits.Count == 0) return mapgeo;
        if (!MapGeoBinary.TryReadEditable(mapgeo, out var map))
        { error = "the mapgeo is not byte-exact editable, so face edits were not applied"; return null; }

        try
        {
            // Which submesh owns each edited triangle, and where its indices sit in that mesh's buffer.
            var moved = new Dictionary<(int Buffer, int Vertex), Vector3>();
            int applied = 0;

            foreach (var edit in edits)
            {
                if (!TryLocate(asset, map, edit.Triangle, out var mesh, out int indexAt, out int meshIndex))
                { error = $"triangle {edit.Triangle} is not inside any submesh of this map"; return null; }

                var buffer = map.IndexBuffers[mesh.IndexBufferId];
                int at = indexAt * 2;                        // u16 indices
                if (at + 6 > buffer.Data.Length)
                { error = $"triangle {edit.Triangle} runs past its index buffer"; return null; }

                ushort i0 = BitConverter.ToUInt16(buffer.Data, at);
                ushort i1 = BitConverter.ToUInt16(buffer.Data, at + 2);
                ushort i2 = BitConverter.ToUInt16(buffer.Data, at + 4);

                switch (edit.Op)
                {
                    case FaceOp.Delete:
                        // All three the same: zero area. The file keeps its length and every later offset.
                        BitConverter.TryWriteBytes(buffer.Data.AsSpan(at + 2), i0);
                        BitConverter.TryWriteBytes(buffer.Data.AsSpan(at + 4), i0);
                        applied++;
                        break;

                    case FaceOp.Flip:
                        BitConverter.TryWriteBytes(buffer.Data.AsSpan(at + 2), i2);
                        BitConverter.TryWriteBytes(buffer.Data.AsSpan(at + 4), i1);
                        applied++;
                        break;

                    case FaceOp.Move:
                        // Collected rather than applied per triangle: two selected faces sharing a vertex
                        // must move it ONCE, or the shared corner travels twice as far.
                        foreach (ushort v in new[] { i0, i1, i2 })
                            moved[(meshIndex, v)] = edit.Offset;
                        applied++;
                        break;
                }
            }

            foreach (var ((meshIndex, vertex), offset) in moved)
                if (!TryOffsetVertex(map, meshIndex, vertex, offset, out error)) return null;

            if (applied == 0) { error = "no face edit could be applied"; return null; }
            return map.Write();
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>
    /// Maps a combined-asset triangle onto the mesh and index-buffer position that hold it.
    ///
    /// <para>The decoder concatenates every submesh's indices into one array, so a group's
    /// <c>StartIndex</c> locates it there while the binary's submesh carries its own offset into a shared
    /// buffer. Going between the two is the only fiddly part of this file.</para>
    /// </summary>
    private static bool TryLocate(MapGeoAsset asset, MapGeoBinary map, int triangle,
        out MapGeoBinary.Mesh mesh, out int indexAt, out int meshIndex)
    {
        mesh = null!; indexAt = 0; meshIndex = -1;
        int firstIndex = triangle * 3;
        if (triangle < 0 || firstIndex + 2 >= asset.Indices.Length) return false;

        foreach (var group in asset.Groups)
        {
            if (firstIndex < group.StartIndex || firstIndex >= group.StartIndex + group.IndexCount) continue;
            if (group.MeshIndex < 0 || group.MeshIndex >= map.Meshes.Count) return false;

            meshIndex = group.MeshIndex;
            mesh = map.Meshes[meshIndex];

            // Which submesh of that mesh is this group? The decoder walks a mesh's submeshes in order, so
            // the n-th group belonging to a mesh is its n-th submesh.
            int ordinal = 0;
            foreach (var g in asset.Groups)
            {
                if (ReferenceEquals(g, group)) break;
                if (g.MeshIndex == group.MeshIndex) ordinal++;
            }
            if (ordinal >= mesh.Submeshes.Count) return false;

            indexAt = mesh.Submeshes[ordinal].StartIndex + (firstIndex - group.StartIndex);
            return true;
        }
        return false;
    }

    /// <summary>Adds <paramref name="offset"/> to one vertex's POSITION, in place.</summary>
    private static bool TryOffsetVertex(MapGeoBinary map, int meshIndex, int vertex, Vector3 offset,
        out string? error)
    {
        error = null;
        var mesh = map.Meshes[meshIndex];
        // Position is the first element of the first buffer on every declaration this app writes and every
        // one Riot ships (M476 censused the orders: Position always leads).
        if (mesh.VertexBufferIds.Count == 0) { error = "a mesh with no vertex buffer cannot be moved"; return false; }
        var declaration = map.Declarations[mesh.VertexDeclarationBase];
        if (declaration.Elements.Count == 0 || declaration.Elements[0].Name != MapGeoBinary.ElemPosition)
        { error = "this mesh does not lead with a POSITION element, so face moves would write the wrong field"; return false; }

        var buffer = map.VertexBuffers[mesh.VertexBufferIds[0]];
        int stride = declaration.Stride;
        int at = vertex * stride;
        if (at + 12 > buffer.Data.Length) { error = "vertex runs past its buffer"; return false; }

        float x = BitConverter.ToSingle(buffer.Data, at) + offset.X;
        float y = BitConverter.ToSingle(buffer.Data, at + 4) + offset.Y;
        float z = BitConverter.ToSingle(buffer.Data, at + 8) + offset.Z;
        BitConverter.TryWriteBytes(buffer.Data.AsSpan(at), x);
        BitConverter.TryWriteBytes(buffer.Data.AsSpan(at + 4), y);
        BitConverter.TryWriteBytes(buffer.Data.AsSpan(at + 8), z);
        return true;
    }
}
