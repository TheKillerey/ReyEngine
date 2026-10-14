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

    /// <summary>M842: raw TEXCOORD7 (the lightmap channel before its atlas scale/bias), 2 floats per vertex.</summary>
    public float[]? LightmapUvs { get; init; }

    /// <summary>M842: PRIMARY_COLOR as RGBA 0..1, 4 floats per vertex.</summary>
    public float[]? Colors { get; init; }

    /// <summary>M842: TEXCOORD5 (the grass clump pivot of a VertexDeform bush), mesh-LOCAL, 3 floats per vertex.</summary>
    public float[]? GrassPivots { get; init; }

    /// <summary>
    /// M842: the submesh ranges of <see cref="Indices"/>, one per material, in order. Null = the whole index
    /// list is the mesh's one existing submesh (and a mesh with several is refused, as before).
    /// </summary>
    public IReadOnlyList<ReshapeSubmesh>? Submeshes { get; init; }
}

/// <summary>M842: one material's range of a reshaped mesh's index list.</summary>
public sealed record ReshapeSubmesh(string Material, int StartIndex, int IndexCount);

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
/// <para><b>Shared buffers (M842).</b> Riot instances geometry: several meshes point at one vertex and/or
/// index buffer and place it with their own transforms (3%-12% of buffers on shipped maps). A reshaped
/// mesh that shares gets its OWN new buffers, appended; the other sharers keep the original ones byte for
/// byte, so every buffer stays referenced (LTK refuses an unreferenced vertex buffer, M768). The vertex
/// declaration - and so the stride and channel layout - stays the mesh's own.</para>
///
/// <para><b>Channels (M842).</b> Position, normal, UV0, the raw lightmap channel, vertex colour and the
/// grass pivot are written from what Blender sent, in the declaration's own formats (float and the packed
/// 8888 colours). A channel nothing was sent for, or one in a packed format this does not write, is zeroed
/// and named in the notes - except the grass pivot, which falls back to each vertex's own position as the
/// decoder does. Bounds are written in WORLD space (the mesh transform applied), as Riot's files hold them.</para>
///
/// <para><b>Materials (M842).</b> With a submesh table, one submesh per material is written (an existing
/// submesh of the same material keeps its hash, a new one gets 0 like an appended mesh). Without one, a mesh with several materials is refused:
/// there is no telling which new triangle is which.</para>
/// </summary>
public static class MapGeoMeshReshaper
{
    /// <summary>Mapgeo index buffers are 16-bit, so a mesh cannot address more than this many vertices.</summary>
    public const int MaxVerticesPerMesh = 65_536;

    public static byte[]? TryApply(byte[] mapgeo, IReadOnlyList<MeshReshape> reshapes, out string? error)
        => TryApply(mapgeo, reshapes, out error, out _);

    public static byte[]? TryApply(byte[] mapgeo, IReadOnlyList<MeshReshape> reshapes, out string? error,
        out IReadOnlyList<string> notes) => TryApply(mapgeo, reshapes, null, out error, out notes);

    /// <param name="notes">What was worth saying: buffers that were un-shared, channels that were zeroed.</param>
    /// <param name="extendedChannelMaterials">The map's Mantis (extended channel) materials - see
    /// <see cref="ExtendedChannelRule"/>. Needed to READ a map that uses one (the per-mesh channel block is
    /// 40 or 48 bytes by material), and applied after the reshape, because a submesh table can move a mesh
    /// onto or off such a material.</param>
    public static byte[]? TryApply(byte[] mapgeo, IReadOnlyList<MeshReshape> reshapes,
        IReadOnlySet<string>? extendedChannelMaterials, out string? error, out IReadOnlyList<string> notes)
    {
        error = null;
        var said = new List<string>();
        notes = said;
        ArgumentNullException.ThrowIfNull(reshapes);
        if (reshapes.Count == 0) return mapgeo;
        if (!MapGeoBinary.TryReadEditable(mapgeo, out var map, extendedChannelMaterials))
        { error = "the mapgeo is not byte-exact editable, so nothing was reshaped"; return null; }

        try
        {
            foreach (var reshape in reshapes)
            {
                // re-counted per reshape: un-sharing one mesh can leave a buffer with a single user
                var sharedVertex = SharedBuffers(map, m => m.VertexBufferIds);
                var sharedIndex = SharedBuffers(map, m => new[] { m.IndexBufferId });
                if (!Reshape(map, reshape, sharedVertex, sharedIndex, said, out error)) return null;
            }
            if (extendedChannelMaterials is not null)
            {
                int changed = ExtendedChannelRule.Apply(map, extendedChannelMaterials);
                if (changed > 0) said.Add($"{changed} mesh(es) moved onto or off an extended-channel (Mantis) material: channel block updated");
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
            foreach (int id in ids(mesh).Distinct())
                if (!seen.Add(id)) shared.Add(id);
        return shared;
    }

    private static bool Reshape(MapGeoBinary map, MeshReshape reshape,
        HashSet<int> sharedVertex, HashSet<int> sharedIndex, List<string> notes, out string? error)
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

        // ---- which triangles belong to which material ---------------------------------------------
        IReadOnlyList<ReshapeSubmesh> ranges;
        if (reshape.Submeshes is { Count: > 0 } sent)
        {
            int next = 0;
            foreach (var s in sent)
            {
                if (string.IsNullOrEmpty(s.Material)) { error = $"{label}: a submesh names no material"; return false; }
                if (s.StartIndex != next || s.IndexCount < 0 || s.IndexCount % 3 != 0)
                { error = $"{label}: the submesh ranges do not tile the index list in whole triangles"; return false; }
                next += s.IndexCount;
            }
            if (next != reshape.Indices.Length)
            { error = $"{label}: the submesh ranges cover {next} of {reshape.Indices.Length} indices"; return false; }
            ranges = sent;
        }
        else
        {
            // With one material there is no question which triangles belong to it. With several and no
            // table there is no answer at all.
            if (mesh.Submeshes.Count != 1)
            {
                error = $"{label}: it has {mesh.Submeshes.Count} submeshes (materials) and the reshape says nothing "
                      + "about which new triangle is which, so it is refused rather than guessed.";
                return false;
            }
            ranges = new[] { new ReshapeSubmesh(mesh.Submeshes[0].Material, 0, reshape.Indices.Length) };
        }
        if (mesh.VertexBufferIds.Count == 0) { error = $"{label}: no vertex buffer"; return false; }

        // ---- rewrite every stream (into the mesh's OWN buffer) -------------------------------------
        var lostChannels = new List<string>();
        int unsharedStreams = 0;
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
                WriteChannel(data, stride, offset, format, name, reshape, vertexCount, lostChannels);
                offset += size;
            }

            int id = mesh.VertexBufferIds[stream];
            if (sharedVertex.Contains(id))
            {
                var original = map.VertexBuffers[id];
                map.VertexBuffers.Add(new MapGeoBinary.VertexBuffer
                {
                    Visibility = original.Visibility, HasVisibility = original.HasVisibility, Data = data,
                });
                mesh.VertexBufferIds[stream] = map.VertexBuffers.Count - 1;
                unsharedStreams++;
            }
            else map.VertexBuffers[id].Data = data;
        }

        // ---- rewrite the index buffer --------------------------------------------------------------
        var indexData = new byte[reshape.Indices.Length * 2];
        for (int i = 0; i < reshape.Indices.Length; i++)
            BitConverter.GetBytes((ushort)reshape.Indices[i]).CopyTo(indexData, i * 2);
        bool unsharedIndex = sharedIndex.Contains(mesh.IndexBufferId);
        if (unsharedIndex)
        {
            var original = map.IndexBuffers[mesh.IndexBufferId];
            map.IndexBuffers.Add(new MapGeoBinary.IndexBuffer
            {
                Visibility = original.Visibility, HasVisibility = original.HasVisibility, Data = indexData,
            });
            mesh.IndexBufferId = map.IndexBuffers.Count - 1;
        }
        else map.IndexBuffers[mesh.IndexBufferId].Data = indexData;
        if (unsharedStreams > 0 || unsharedIndex)
            notes.Add($"{label}: shared {(unsharedStreams > 0 ? $"{unsharedStreams} vertex stream(s)" : "")}"
                    + $"{(unsharedStreams > 0 && unsharedIndex ? " and " : "")}{(unsharedIndex ? "its index buffer" : "")} "
                    + "with other meshes - it now has its own copy, and the other meshes keep theirs unchanged");
        if (lostChannels.Count > 0)
            notes.Add($"{label}: {string.Join(", ", lostChannels)} not sent (or in a format not written) - zeroed");

        // ---- and the counts that describe them -----------------------------------------------------
        mesh.VertexCount = vertexCount;
        mesh.IndexCount = reshape.Indices.Length;

        var submeshes = new List<MapGeoBinary.Submesh>(ranges.Count);
        foreach (var range in ranges)
        {
            var existing = mesh.Submeshes.FirstOrDefault(s => string.Equals(s.Material, range.Material, StringComparison.Ordinal));
            int lo = int.MaxValue, hi = 0;
            for (int i = range.StartIndex; i < range.StartIndex + range.IndexCount; i++)
            {
                int v = (int)reshape.Indices[i];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            if (range.IndexCount == 0) { lo = 0; hi = 0; }
            submeshes.Add(new MapGeoBinary.Submesh
            {
                Hash = existing?.Hash ?? 0u,   // a new material: 0, as MapGeoMeshAppender writes
                Material = range.Material,
                StartIndex = range.StartIndex,
                IndexCount = range.IndexCount,
                MinVertex = lo,
                MaxVertex = hi,
            });
        }
        mesh.Submeshes = submeshes;

        // The bounding box drives culling. A stale one makes the mesh vanish at angles the old shape did
        // not cover, which reads as a rendering bug rather than as a forgotten field. It is WORLD space (the
        // decoder's, MapGeoWriter's and MapGeoFaceWriter's convention, and Riot's): the local vertices through
        // the mesh's own transform. M842 review: a local box was written before, wrong for every instanced mesh.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i + 2 < reshape.Positions.Length; i += 3)
        {
            var p = Vector3.Transform(new Vector3(reshape.Positions[i], reshape.Positions[i + 1], reshape.Positions[i + 2]), mesh.Transform);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        mesh.BoundsMin = min;
        mesh.BoundsMax = max;
        return true;
    }

    /// <summary>Write <paramref name="components"/>-wide float data into a float-format element. False for a
    /// packed format (nothing is written then).</summary>
    private static bool WriteFloats(byte[] data, int stride, int offset, uint format, float[] source, int components,
        int vertexCount, float pad = 0f)
    {
        int width = format switch
        {
            MapGeoBinary.FmtX_Float32 => 1, MapGeoBinary.FmtXY_Float32 => 2,
            MapGeoBinary.FmtXYZ_Float32 => 3, MapGeoBinary.FmtXYZW_Float32 => 4, _ => 0,
        };
        if (width == 0) return false;
        for (int v = 0; v < vertexCount; v++)
            for (int c = 0; c < width; c++)
            {
                float value = c < components ? source[v * components + c] : (c == 3 ? 1f : pad);
                BitConverter.TryWriteBytes(data.AsSpan(v * stride + offset + c * 4, 4), value);
            }
        return true;
    }

    private static byte Unit(float f) => (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);

    /// <summary>
    /// Fill one vertex element across every vertex, from what the reshape carries.
    /// </summary>
    /// <remarks>
    /// Channels nothing was sent for are zeroed rather than carried over - the vertex they belonged to may
    /// not exist any more - and named in <paramref name="lost"/> so the user is told.
    /// </remarks>
    private static void WriteChannel(byte[] data, int stride, int offset, uint format, uint element,
        MeshReshape reshape, int vertexCount, List<string> lost)
    {
        bool written = element switch
        {
            MapGeoBinary.ElemPosition => WriteFloats(data, stride, offset, format, reshape.Positions, 3, vertexCount),
            MapGeoBinary.ElemNormal => reshape.Normals is { } n && n.Length == vertexCount * 3
                && WriteFloats(data, stride, offset, format, n, 3, vertexCount),
            MapGeoBinary.ElemTexcoord0 => reshape.Uvs is { } uv && uv.Length == vertexCount * 2
                && WriteFloats(data, stride, offset, format, uv, 2, vertexCount),
            MapGeoBinary.ElemTexcoord7 => reshape.LightmapUvs is { } lm && lm.Length == vertexCount * 2
                && WriteFloats(data, stride, offset, format, lm, 2, vertexCount),
            // M842 review: no pivot sent -> each vertex is its own pivot, the decoder's fallback (a zero pivot
            // would bend every blade around the mesh origin).
            MapGeoBinary.ElemTexcoord5 => WriteFloats(data, stride, offset, format,
                reshape.GrassPivots is { } gp && gp.Length == vertexCount * 3 ? gp : reshape.Positions, 3, vertexCount),
            MapGeoBinary.ElemPrimaryColor => reshape.Colors is { } col && col.Length == vertexCount * 4
                && WriteColor(data, stride, offset, format, col, vertexCount),
            _ => false,
        };
        if (written) return;

        string name = element switch
        {
            MapGeoBinary.ElemPosition => "positions",
            MapGeoBinary.ElemNormal => "normals",
            MapGeoBinary.ElemPrimaryColor => "vertex colour",
            MapGeoBinary.ElemTexcoord0 => "diffuse UVs",
            MapGeoBinary.ElemTexcoord5 => "grass pivot",
            MapGeoBinary.ElemTexcoord6 => "tangents",
            MapGeoBinary.ElemTexcoord7 => "baked lightmap UVs",
            _ => $"channel {element}",
        };
        if (!lost.Contains(name)) lost.Add(name);
    }

    private static bool WriteColor(byte[] data, int stride, int offset, uint format, float[] rgba, int vertexCount)
    {
        if (format is MapGeoBinary.FmtXYZW_Float32) return WriteFloats(data, stride, offset, format, rgba, 4, vertexCount);
        if (format is not (MapGeoBinary.FmtBGRA_Packed8888 or MapGeoBinary.FmtRGBA_Packed8888)) return false;
        bool bgra = format == MapGeoBinary.FmtBGRA_Packed8888;
        for (int v = 0; v < vertexCount; v++)
        {
            int at = v * stride + offset;
            byte r = Unit(rgba[v * 4]), g = Unit(rgba[v * 4 + 1]), b = Unit(rgba[v * 4 + 2]), a = Unit(rgba[v * 4 + 3]);
            data[at] = bgra ? b : r; data[at + 1] = g; data[at + 2] = bgra ? r : b; data[at + 3] = a;
        }
        return true;
    }

    /// <summary>
    /// Which meshes cannot be reshaped without a submesh table, and why. Shared buffers are no longer a
    /// refusal (M842: the reshaped mesh is given its own).
    /// </summary>
    public static IReadOnlyDictionary<int, string> Refusals(byte[] mapgeo, IReadOnlySet<string>? extendedChannelMaterials = null)
    {
        var refusals = new Dictionary<int, string>();
        if (!MapGeoBinary.TryReadEditable(mapgeo, out var map, extendedChannelMaterials)) return refusals;
        for (int i = 0; i < map.Meshes.Count; i++)
            if (map.Meshes[i].Submeshes.Count != 1)
                refusals[i] = $"{map.Meshes[i].Submeshes.Count} materials";
        return refusals;
    }

    /// <summary>M842: which meshes share a vertex or index buffer with another mesh (Riot instancing).</summary>
    public static IReadOnlySet<int> SharingMeshes(byte[] mapgeo, IReadOnlySet<string>? extendedChannelMaterials = null)
    {
        var result = new HashSet<int>();
        if (!MapGeoBinary.TryReadEditable(mapgeo, out var map, extendedChannelMaterials)) return result;
        var sharedVertex = SharedBuffers(map, m => m.VertexBufferIds);
        var sharedIndex = SharedBuffers(map, m => new[] { m.IndexBufferId });
        for (int i = 0; i < map.Meshes.Count; i++)
            if (map.Meshes[i].VertexBufferIds.Any(sharedVertex.Contains) || sharedIndex.Contains(map.Meshes[i].IndexBufferId))
                result.Add(i);
        return result;
    }
}
