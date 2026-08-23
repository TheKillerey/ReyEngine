using System.Numerics;

namespace ReyEngine.Formats.MapGeo;

/// <summary>Extrude pushes the face out along its normal; inset shrinks it toward its own centre.</summary>
public enum FaceGrowOp { Extrude, Inset }

/// <param name="Triangle">Index into the decoded asset's combined triangle list.</param>
/// <param name="Amount">Extrude: world units along the face normal. Inset: 0..1 of the way to the centre.</param>
public readonly record struct FaceGrow(int Triangle, FaceGrowOp Op, float Amount);

/// <summary>
/// M570: extrude and inset — the first face operations that CREATE geometry.
///
/// <para>Everything in <see cref="MapGeoFaceWriter"/> preserves the file's length, which is why deleting a
/// face degenerates it rather than removing it. These cannot: each face gains three vertices and turns
/// one triangle into seven. So this writer has to do the thing the others were built to avoid — grow the
/// buffers and repair every offset that moves as a result.</para>
///
/// <para>Both operations share a shape. Three new vertices are made from the originals (pushed along the
/// normal, or pulled toward the centroid), the original triangle is DEGENERATED, and seven triangles are
/// appended: one cap on the new vertices, and two per edge bridging old to new. Only where the new
/// vertices go differs.</para>
///
/// <para><b>The u16 ceiling is real.</b> Mapgeo index buffers are 16-bit, so a mesh cannot exceed 65,536
/// vertices. A mesh near that ceiling refuses rather than silently wrapping its indices, which would
/// scramble the geometry in a way that looks like corruption rather than a rejected edit.</para>
/// </summary>
public static class MapGeoFaceGrower
{
    private const int MaxVerticesPerMesh = 65_536;

    /// <summary>Seven triangles replace one: a cap, plus two per edge for the sides.</summary>
    private const int TrianglesPerFace = 7;

    public static byte[]? TryApply(byte[] mapgeo, MapGeoAsset asset, IReadOnlyList<FaceGrow> grows,
        out string? error)
    {
        error = null;
        if (grows.Count == 0) return mapgeo;
        if (!MapGeoBinary.TryReadEditable(mapgeo, out var map))
        { error = "the mapgeo is not byte-exact editable, so the edit was not applied"; return null; }

        try
        {
            // Group by mesh: every mesh's buffers are grown once, and its submesh offsets repaired once.
            var byMesh = new Dictionary<int, List<(FaceGrow Grow, int SubmeshOrdinal, int IndexAt)>>();
            foreach (var grow in grows)
            {
                if (!TryLocate(asset, map, grow.Triangle, out int meshIndex, out int ordinal, out int indexAt))
                { error = $"triangle {grow.Triangle} is not inside any submesh of this map"; return null; }
                if (!byMesh.TryGetValue(meshIndex, out var list)) byMesh[meshIndex] = list = new();
                list.Add((grow, ordinal, indexAt));
            }

            foreach (var (meshIndex, edits) in byMesh)
                if (!TryGrowMesh(map, meshIndex, edits, out error)) return null;

            return map.Write();
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    private static bool TryGrowMesh(MapGeoBinary map, int meshIndex,
        List<(FaceGrow Grow, int SubmeshOrdinal, int IndexAt)> edits, out string? error)
    {
        error = null;
        var mesh = map.Meshes[meshIndex];
        var declaration = map.Declarations[mesh.VertexDeclarationBase];
        if (declaration.Elements.Count == 0 || declaration.Elements[0].Name != MapGeoBinary.ElemPosition)
        { error = "this mesh does not lead with a POSITION element"; return false; }
        if (mesh.VertexBufferIds.Count == 0) { error = "this mesh has no vertex buffer"; return false; }

        int newVertices = edits.Count * 3;
        if (mesh.VertexCount + newVertices > MaxVerticesPerMesh)
        {
            error = $"this mesh already holds {mesh.VertexCount:n0} vertices; {newVertices} more would pass "
                + $"the {MaxVerticesPerMesh:n0} that a 16-bit index buffer can address";
            return false;
        }

        var vertexBuffer = map.VertexBuffers[mesh.VertexBufferIds[0]];
        int stride = declaration.Stride;
        var indexBuffer = map.IndexBuffers[mesh.IndexBufferId];

        // --- build the new vertices and the triangles that use them ------------------------------------
        var appendedVertices = new List<byte[]>(newVertices);
        // per submesh ordinal: the triangles to insert at the end of that submesh's range
        var appendedIndices = new Dictionary<int, List<ushort>>();
        var degenerate = new List<(int At, ushort To)>();

        foreach (var (grow, ordinal, indexAt) in edits)
        {
            int at = indexAt * 2;
            if (at + 6 > indexBuffer.Data.Length) { error = "a triangle runs past its index buffer"; return false; }
            ushort i0 = BitConverter.ToUInt16(indexBuffer.Data, at);
            ushort i1 = BitConverter.ToUInt16(indexBuffer.Data, at + 2);
            ushort i2 = BitConverter.ToUInt16(indexBuffer.Data, at + 4);

            Vector3 P(ushort v)
            {
                int o = v * stride;
                return new Vector3(BitConverter.ToSingle(vertexBuffer.Data, o),
                    BitConverter.ToSingle(vertexBuffer.Data, o + 4),
                    BitConverter.ToSingle(vertexBuffer.Data, o + 8));
            }
            Vector3 p0 = P(i0), p1 = P(i1), p2 = P(i2);

            Vector3[] moved;
            if (grow.Op == FaceGrowOp.Extrude)
            {
                var normal = Vector3.Cross(p1 - p0, p2 - p0);
                if (normal.LengthSquared() <= 1e-12f) { error = "a degenerate triangle has no normal to extrude along"; return false; }
                var push = Vector3.Normalize(normal) * grow.Amount;
                moved = new[] { p0 + push, p1 + push, p2 + push };
            }
            else
            {
                // Toward the centroid. Clamped below 1 so the face never collapses to a point, which would
                // leave three coincident vertices and seven zero-area triangles.
                float t = Math.Clamp(grow.Amount, 0.01f, 0.95f);
                var centre = (p0 + p1 + p2) / 3f;
                moved = new[]
                {
                    Vector3.Lerp(p0, centre, t), Vector3.Lerp(p1, centre, t), Vector3.Lerp(p2, centre, t),
                };
            }

            // The new vertices copy the originals WHOLE - normal, uv, colour, every channel the
            // declaration carries - and only the position is rewritten. Building one from scratch would
            // mean knowing every element's meaning; copying means the new geometry shades like its parent.
            var newIds = new ushort[3];
            ushort[] source = { i0, i1, i2 };
            for (int k = 0; k < 3; k++)
            {
                var record = new byte[stride];
                Array.Copy(vertexBuffer.Data, source[k] * stride, record, 0, stride);
                BitConverter.TryWriteBytes(record.AsSpan(0), moved[k].X);
                BitConverter.TryWriteBytes(record.AsSpan(4), moved[k].Y);
                BitConverter.TryWriteBytes(record.AsSpan(8), moved[k].Z);
                newIds[k] = (ushort)(mesh.VertexCount + appendedVertices.Count);
                appendedVertices.Add(record);
            }

            if (!appendedIndices.TryGetValue(ordinal, out var into)) appendedIndices[ordinal] = into = new();
            // the cap, wound like the original
            into.Add(newIds[0]); into.Add(newIds[1]); into.Add(newIds[2]);
            // and the sides: each original edge bridged to its new counterpart
            void Side(ushort a, ushort b, ushort na, ushort nb)
            {
                into.Add(a); into.Add(b); into.Add(nb);
                into.Add(a); into.Add(nb); into.Add(na);
            }
            Side(i0, i1, newIds[0], newIds[1]);
            Side(i1, i2, newIds[1], newIds[2]);
            Side(i2, i0, newIds[2], newIds[0]);

            // The original becomes the hole the new cap sits over.
            degenerate.Add((at, i0));
        }

        // --- splice ------------------------------------------------------------------------------------
        foreach (var (at, to) in degenerate)
        {
            BitConverter.TryWriteBytes(indexBuffer.Data.AsSpan(at + 2), to);
            BitConverter.TryWriteBytes(indexBuffer.Data.AsSpan(at + 4), to);
        }

        var grown = new List<byte>(vertexBuffer.Data.Length + appendedVertices.Count * stride);
        grown.AddRange(vertexBuffer.Data);
        foreach (var record in appendedVertices) grown.AddRange(record);
        vertexBuffer.Data = grown.ToArray();
        mesh.VertexCount += appendedVertices.Count;

        // Insert each submesh's new triangles at the END of its own range, then push every later submesh
        // along by what was inserted before it. THIS is the part the length-preserving operations exist to
        // avoid: a mapgeo has no offset table, so an index inserted here moves everything after it.
        var rebuilt = new List<byte>(indexBuffer.Data.Length + appendedIndices.Values.Sum(x => x.Count) * 2);
        var ordered = appendedIndices.Keys.OrderBy(o => mesh.Submeshes[o].StartIndex).ToList();
        int copiedUpTo = 0;
        int shiftSoFar = 0;
        var shifts = new List<(int AfterIndex, int By)>();

        foreach (int ordinal in ordered)
        {
            var submesh = mesh.Submeshes[ordinal];
            int insertAt = submesh.StartIndex + submesh.IndexCount;
            rebuilt.AddRange(indexBuffer.Data.AsSpan(copiedUpTo * 2, (insertAt - copiedUpTo) * 2).ToArray());
            foreach (ushort v in appendedIndices[ordinal]) rebuilt.AddRange(BitConverter.GetBytes(v));
            copiedUpTo = insertAt;

            int added = appendedIndices[ordinal].Count;
            submesh.StartIndex += shiftSoFar;
            submesh.IndexCount += added;
            shiftSoFar += added;
            shifts.Add((insertAt, shiftSoFar));
        }
        rebuilt.AddRange(indexBuffer.Data.AsSpan(copiedUpTo * 2).ToArray());
        indexBuffer.Data = rebuilt.ToArray();
        mesh.IndexCount += shiftSoFar;

        // Every OTHER submesh in the same buffer - including ones belonging to other meshes, since a
        // buffer can be shared - moves by however much was inserted before it.
        foreach (var other in map.Meshes)
        {
            if (other.IndexBufferId != mesh.IndexBufferId) continue;
            foreach (var sub in other.Submeshes)
            {
                if (ReferenceEquals(other, mesh) && ordered.Any(o => ReferenceEquals(mesh.Submeshes[o], sub))) continue;
                int by = 0;
                foreach (var (afterIndex, total) in shifts) if (sub.StartIndex >= afterIndex) by = total;
                sub.StartIndex += by;
            }
            if (!ReferenceEquals(other, mesh) && shiftSoFar > 0) other.IndexCount = other.Submeshes.Sum(x => x.IndexCount);
        }
        return true;
    }

    /// <summary>Same mapping as <see cref="MapGeoFaceWriter"/>: combined triangle -> mesh and submesh.</summary>
    private static bool TryLocate(MapGeoAsset asset, MapGeoBinary map, int triangle,
        out int meshIndex, out int ordinal, out int indexAt)
    {
        meshIndex = -1; ordinal = 0; indexAt = 0;
        int firstIndex = triangle * 3;
        if (triangle < 0 || firstIndex + 2 >= asset.Indices.Length) return false;

        foreach (var group in asset.Groups)
        {
            if (firstIndex < group.StartIndex || firstIndex >= group.StartIndex + group.IndexCount) continue;
            if (group.MeshIndex < 0 || group.MeshIndex >= map.Meshes.Count) return false;
            meshIndex = group.MeshIndex;

            int n = 0;
            foreach (var g in asset.Groups)
            {
                if (ReferenceEquals(g, group)) break;
                if (g.MeshIndex == group.MeshIndex) n++;
            }
            if (n >= map.Meshes[meshIndex].Submeshes.Count) return false;
            ordinal = n;
            indexAt = map.Meshes[meshIndex].Submeshes[n].StartIndex + (firstIndex - group.StartIndex);
            return true;
        }
        return false;
    }
}
