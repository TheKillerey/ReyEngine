using System.Numerics;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// New geometry for one existing mesh, in the mesh's own LOCAL space (the space its vertex buffer holds).
/// </summary>
/// <param name="MeshIndex">Index into <see cref="MapGeoBinary.Meshes"/>.</param>
public sealed record MeshReshape(
    int MeshIndex,
    float[] Positions,
    float[]? Normals,
    float[]? Uvs,
    uint[] Indices)
{
    public int VertexCount => Positions.Length / 3;
}

/// <summary>
/// M581: replace an existing mesh's geometry — a different vertex count, different triangles, a different
/// shape entirely — while leaving everything else about that mesh alone.
///
/// <para>Everything else is the point. A mesh carries far more than its triangles: its material binding,
/// its render region, its visibility controller, its baked-light and baked-paint channels with their
/// atlas scale/bias, its per-mesh texture overrides, its quality filter. Rebuilding the mesh by removing
/// and re-appending it would lose all of that, so this rewrites the buffers underneath a mesh record that
/// is otherwise untouched.</para>
///
/// <para><b>Streams.</b> A mapgeo mesh does not have one vertex buffer, it has one per channel group —
/// measured on shipped maps, 32%–68% of meshes split as <c>[Position+Normal]</c> plus
/// <c>[Texcoord0(+Texcoord7)]</c>. Every stream is exactly <c>stride x vertexCount</c> bytes, so ALL of
/// them have to be rewritten together; growing one alone leaves the rest describing a different number of
/// vertices, which the client reads as garbage rather than as an error.</para>
///
/// <para><b>What it refuses.</b> A mesh whose buffers are shared with another mesh, a mesh with more than
/// one submesh, and anything past the u16 index ceiling. Each of those would produce a file that loads
/// and is wrong, which is worse than a rejected edit — see the notes on each check.</para>
/// </summary>
public static class MapGeoMeshReshaper
{
    /// <summary>Mapgeo index buffers are 16-bit, so a mesh cannot address more than this many vertices.</summary>
    public const int MaxVerticesPerMesh = 65_536;

    public static byte[]? TryApply(byte[] mapgeo, IReadOnlyList<MeshReshape> reshapes, out string? error)
    {
        error = null;
        ArgumentNullException.ThrowIfNull(reshapes);
        if (reshapes.Count == 0) return mapgeo;
        if (!MapGeoBinary.TryReadEditable(mapgeo, out var map))
        { error = "the mapgeo is not byte-exact editable, so nothing was reshaped"; return null; }

        try
        {
            var sharedVertex = SharedBuffers(map, m => m.VertexBufferIds);
            var sharedIndex = SharedBuffers(map, m => new[] { m.IndexBufferId });

            foreach (var reshape in reshapes)
            {
                if (!Reshape(map, reshape, sharedVertex, sharedIndex, out error)) return null;
            }
            return map.Write();
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>Buffer ids more than one mesh points at.</summary>
    private static HashSet<int> SharedBuffers(MapGeoBinary map, Func<MapGeoBinary.Mesh, IEnumerable<int>> ids)
    {
        var seen = new HashSet<int>();
        var shared = new HashSet<int>();
        foreach (var mesh in map.Meshes)
            foreach (int id in ids(mesh))
                if (!seen.Add(id)) shared.Add(id);
        return shared;
    }

    private static bool Reshape(MapGeoBinary map, MeshReshape reshape,
        HashSet<int> sharedVertex, HashSet<int> sharedIndex, out string? error)
    {
        error = null;
        if (reshape.MeshIndex < 0 || reshape.MeshIndex >= map.Meshes.Count)
        { error = $"mesh {reshape.MeshIndex} is not in this map"; return false; }

        var mesh = map.Meshes[reshape.MeshIndex];
        string label = $"mesh {reshape.MeshIndex}";

        int vertexCount = reshape.VertexCount;
        if (vertexCount == 0 || reshape.Positions.Length % 3 != 0)
        { error = $"{label}: {reshape.Positions.Length} position floats is not a whole number of vertices"; return false; }
        if (vertexCount > MaxVerticesPerMesh)
        {
            error = $"{label}: {vertexCount:n0} vertices passes the {MaxVerticesPerMesh:n0} a 16-bit index "
                  + "buffer can address. Split the mesh in Blender and send the pieces separately.";
            return false;
        }
        if (reshape.Indices.Length % 3 != 0)
        { error = $"{label}: {reshape.Indices.Length} indices is not a whole number of triangles"; return false; }
        foreach (uint index in reshape.Indices)
            if (index >= vertexCount)
            { error = $"{label}: a triangle references vertex {index} of {vertexCount}"; return false; }

        // A shared buffer belongs to more than one mesh, and resizing it for one silently redefines the
        // other. Measured on shipped maps: 3%-12% of buffers are shared, so this is not hypothetical.
        foreach (int id in mesh.VertexBufferIds)
            if (sharedVertex.Contains(id))
            { error = $"{label}: its vertex buffer is shared with another mesh, so reshaping it would change that one too"; return false; }
        if (sharedIndex.Contains(mesh.IndexBufferId))
        { error = $"{label}: its index buffer is shared with another mesh"; return false; }

        // With one material there is no question which triangles belong to it. With several there is no
        // answer at all - Blender does not send materials, so a new triangle cannot be attributed.
        if (mesh.Submeshes.Count != 1)
        {
            error = $"{label}: it has {mesh.Submeshes.Count} submeshes (materials). Reshaping cannot tell which "
                  + "of the new triangles belongs to which, so it is refused rather than guessed.";
            return false;
        }
        if (mesh.VertexBufferIds.Count == 0) { error = $"{label}: no vertex buffer"; return false; }

        // ---- rewrite every stream ----------------------------------------------------------------
        var lostChannels = new List<string>();
        for (int stream = 0; stream < mesh.VertexBufferIds.Count; stream++)
        {
            int declarationId = mesh.VertexDeclarationBase + stream;
            if (declarationId >= map.Declarations.Count)
            { error = $"{label}: stream {stream} has no vertex declaration"; return false; }
            var declaration = map.Declarations[declarationId];
            int stride = declaration.Stride;
            if (stride <= 0) { error = $"{label}: stream {stream} has a zero stride"; return false; }

            var data = new byte[stride * vertexCount];
            int offset = 0;
            foreach (var (name, format) in declaration.Elements)
            {
                int size = MapGeoBinary.FormatSize(format);
                WriteChannel(data, stride, offset, size, name, reshape, vertexCount, lostChannels);
                offset += size;
            }
            map.VertexBuffers[mesh.VertexBufferIds[stream]].Data = data;
        }

        // ---- rewrite the index buffer --------------------------------------------------------------
        var indexData = new byte[reshape.Indices.Length * 2];
        for (int i = 0; i < reshape.Indices.Length; i++)
            BitConverter.GetBytes((ushort)reshape.Indices[i]).CopyTo(indexData, i * 2);
        map.IndexBuffers[mesh.IndexBufferId].Data = indexData;

        // ---- and the counts that describe them -----------------------------------------------------
        mesh.VertexCount = vertexCount;
        mesh.IndexCount = reshape.Indices.Length;

        var submesh = mesh.Submeshes[0];
        submesh.StartIndex = 0;
        submesh.IndexCount = reshape.Indices.Length;
        submesh.MinVertex = 0;
        submesh.MaxVertex = vertexCount - 1;

        // The bounding box drives culling. A stale one makes the mesh vanish at angles the old shape did
        // not cover, which reads as a rendering bug rather than as a forgotten field.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i + 2 < reshape.Positions.Length; i += 3)
        {
            var p = new Vector3(reshape.Positions[i], reshape.Positions[i + 1], reshape.Positions[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        mesh.BoundsMin = min;
        mesh.BoundsMax = max;
        return true;
    }

    /// <summary>
    /// Fill one vertex element across every vertex.
    /// </summary>
    /// <remarks>
    /// Channels Blender cannot supply are zeroed rather than carried over. Carrying them over is not an
    /// option — the vertex they belonged to may not exist any more — and the honest consequence is that a
    /// reshaped mesh loses its baked lightmap UVs, which a changed shape would have invalidated anyway.
    /// The caller reports what was zeroed.
    /// </remarks>
    private static void WriteChannel(byte[] data, int stride, int offset, int size, uint element,
        MeshReshape reshape, int vertexCount, List<string> lost)
    {
        switch (element)
        {
            case MapGeoBinary.ElemPosition when size >= 12:
                for (int v = 0; v < vertexCount; v++)
                    Buffer.BlockCopy(reshape.Positions, v * 12, data, v * stride + offset, 12);
                return;

            case MapGeoBinary.ElemNormal when size >= 12 && reshape.Normals is { } normals
                                              && normals.Length == reshape.Positions.Length:
                for (int v = 0; v < vertexCount; v++)
                    Buffer.BlockCopy(normals, v * 12, data, v * stride + offset, 12);
                return;

            case MapGeoBinary.ElemTexcoord0 when size >= 8 && reshape.Uvs is { } uvs
                                                 && uvs.Length == vertexCount * 2:
                for (int v = 0; v < vertexCount; v++)
                    Buffer.BlockCopy(uvs, v * 8, data, v * stride + offset, 8);
                return;
        }

        // Anything else: left at zero, and named so the user is told rather than left to discover it.
        string name = element switch
        {
            MapGeoBinary.ElemNormal => "normals",
            MapGeoBinary.ElemPrimaryColor => "vertex colour",
            MapGeoBinary.ElemTexcoord0 => "diffuse UVs",
            MapGeoBinary.ElemTexcoord5 => "grass pivot",
            MapGeoBinary.ElemTexcoord7 => "baked lightmap UVs",
            _ => $"channel {element}",
        };
        if (!lost.Contains(name)) lost.Add(name);
    }

    /// <summary>
    /// Which meshes this map can reshape, and why the rest cannot.
    /// </summary>
    /// <remarks>Used to tell the user up front, rather than one refusal at a time after they have already
    /// spent an evening in Blender.</remarks>
    public static IReadOnlyDictionary<int, string> Refusals(byte[] mapgeo)
    {
        var refusals = new Dictionary<int, string>();
        if (!MapGeoBinary.TryReadEditable(mapgeo, out var map)) return refusals;

        var sharedVertex = SharedBuffers(map, m => m.VertexBufferIds);
        var sharedIndex = SharedBuffers(map, m => new[] { m.IndexBufferId });
        for (int i = 0; i < map.Meshes.Count; i++)
        {
            var mesh = map.Meshes[i];
            if (mesh.Submeshes.Count != 1) refusals[i] = $"{mesh.Submeshes.Count} materials";
            else if (mesh.VertexBufferIds.Any(sharedVertex.Contains)) refusals[i] = "shared vertex buffer";
            else if (sharedIndex.Contains(mesh.IndexBufferId)) refusals[i] = "shared index buffer";
        }
        return refusals;
    }
}
