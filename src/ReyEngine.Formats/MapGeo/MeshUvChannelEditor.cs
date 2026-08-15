using System.Numerics;

namespace ReyEngine.Formats.MapGeo;

/// <summary>How the second UV set should be rewritten.</summary>
public enum UvEditMode
{
    /// <summary>Project world XZ onto the canvas rect, normalised into 0..1. This is what the legacy second
    /// UV set actually IS — see <see cref="MeshUvChannelEditor"/>.</summary>
    WorldPlanarXz,
    /// <summary>Copy Texcoord0, the same choice <see cref="MeshUvChannelBuilder"/> makes when it creates the
    /// channel. Charts overlap between meshes, so this is a valid channel and not a lightmap unwrap.</summary>
    CopyTexcoord0,
    /// <summary>Keep the existing UVs and apply scale then offset — the tuning pass.</summary>
    ScaleExisting,
}

/// <summary>What one second-UV edit did.</summary>
public sealed record UvEditResult(
    int MeshesChanged, int VerticesWritten, IReadOnlyList<string> Skipped,
    Vector2 UvMin, Vector2 UvMax)
{
    public string Summary => MeshesChanged == 0
        ? "No mesh changed" + (Skipped.Count > 0 ? ": " + string.Join("; ", Skipped.Take(3)) : ".")
        : $"Rewrote Texcoord7 on {MeshesChanged:n0} mesh(es), {VerticesWritten:n0} vertices; "
          + $"u {UvMin.X:0.###}..{UvMax.X:0.###}, v {UvMin.Y:0.###}..{UvMax.Y:0.###}"
          + (Skipped.Count > 0 ? $"; skipped {Skipped.Count} ({string.Join("; ", Skipped.Take(3))})" : ".");
}

/// <summary>
/// M492: edit a mesh's SECOND UV set (Texcoord7) in place, and read any UV set back out for display.
///
/// <para><b>Why this is separate from <see cref="MeshUvChannelBuilder"/>.</b> That one ADDS the channel to a
/// mesh that lacks it, which changes the vertex declaration, the stride and the buffer length. This one only
/// overwrites floats already in the buffer: no declaration touched, no stride changed, no buffer resized.
/// That makes it the safe operation of the two — the class of defect that cost M476 (element ORDER is the
/// byte layout) and M416 (wire form) cannot arise when the layout is not touched at all.</para>
///
/// <para><b>What the second UV set is.</b> Measured in M479 over room.nvr's 342 second-UV meshes: it is a
/// world-planar canvas, not a lightmap atlas. Fitting u against world X gives R^2 = 0.9997 and v against
/// world Z gives 0.9999, while the opposite pairings give 0.0002 and 0.0003. An atlas would score near zero
/// on all four. That is why <see cref="UvEditMode.WorldPlanarXz"/> is the mode that reconstructs it, and why
/// the default rect is the map's own XZ bounds — the canvas spans the map.</para>
///
/// <para><b>It is also the lightmap UV.</b> The modern shaders read Texcoord7 through the BakedLight
/// scale/bias to sample BAKED_LIGHT__TX (4textureblend_uvbased_basemat.ps blob 8, lines 126-127). So a map
/// that carries a real baked lightmap has an atlas unwrap here and rewriting it as a planar canvas will
/// break the bake. Callers are expected to say that out loud rather than have this silently refuse: the
/// operation is legitimate on ported legacy geometry and wrong on baked geometry, and only the caller knows
/// which it is holding.</para>
/// </summary>
public static class MeshUvChannelEditor
{
    /// <summary>Read one UV set (Texcoord0 or Texcoord7) out of whichever of the mesh's buffers declares it.
    /// Used both by the editor below and by the previewer, so the display and the edit cannot disagree about
    /// where the data lives.</summary>
    public static bool TryReadUvSet(MapGeoBinary map, MapGeoBinary.Mesh mesh, uint element, out Vector2[] uv)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(mesh);
        uv = Array.Empty<Vector2>();
        if (!TryLocate(map, mesh, element, MapGeoBinary.FmtXY_Float32, out var data, out int stride, out int offset))
            return false;

        var result = new Vector2[mesh.VertexCount];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            int at = v * stride + offset;
            result[v] = new Vector2(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4));
        }
        uv = result;
        return true;
    }

    /// <summary>Read the mesh's positions in WORLD space (its own transform applied), for the planar
    /// projection and for the previewer's world-space readout.</summary>
    public static bool TryReadWorldPositions(MapGeoBinary map, MapGeoBinary.Mesh mesh, out Vector3[] positions)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(mesh);
        positions = Array.Empty<Vector3>();
        if (!TryLocate(map, mesh, MapGeoBinary.ElemPosition, MapGeoBinary.FmtXYZ_Float32,
                out var data, out int stride, out int offset)) return false;

        var result = new Vector3[mesh.VertexCount];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            int at = v * stride + offset;
            var local = new Vector3(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4),
                                    BitConverter.ToSingle(data, at + 8));
            result[v] = Vector3.Transform(local, mesh.Transform);
        }
        positions = result;
        return true;
    }

    /// <summary>The map's XZ extent, which is the default canvas rect for
    /// <see cref="UvEditMode.WorldPlanarXz"/>. Taken from the meshes' own bounds rather than by walking
    /// every vertex.</summary>
    public static (Vector2 Min, Vector2 Max) WorldXzBounds(MapGeoBinary map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var m in map.Meshes)
        {
            if (m.VertexCount == 0) continue;
            min = Vector2.Min(min, new Vector2(m.BoundsMin.X, m.BoundsMin.Z));
            max = Vector2.Max(max, new Vector2(m.BoundsMax.X, m.BoundsMax.Z));
        }
        return min.X > max.X ? (Vector2.Zero, Vector2.One) : (min, max);
    }

    /// <summary>
    /// Rewrite Texcoord7 on the meshes at the given ordinals. An empty selection means every mesh.
    /// Meshes WITHOUT the channel are skipped and reported — adding it is
    /// <see cref="MeshUvChannelBuilder.AddTexcoord7(MapGeoBinary, IEnumerable{int}, out UvChannelResult)"/>,
    /// a different operation with different risks.
    /// </summary>
    /// <param name="rect">Canvas rect for <see cref="UvEditMode.WorldPlanarXz"/>; null uses the map's own
    /// XZ bounds. Ignored by the other modes.</param>
    public static bool SetTexcoord7(MapGeoBinary map, IEnumerable<int> meshIndices, UvEditMode mode,
        Vector2 scale, Vector2 offset, out UvEditResult result, (Vector2 Min, Vector2 Max)? rect = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        var wanted = new HashSet<int>(meshIndices);
        var skipped = new List<string>();
        int changed = 0, vertices = 0;
        var seenMin = new Vector2(float.MaxValue, float.MaxValue);
        var seenMax = new Vector2(float.MinValue, float.MinValue);

        var canvas = rect ?? WorldXzBounds(map);
        var span = canvas.Max - canvas.Min;
        // A degenerate axis would divide by zero and write NaN into the buffer, which reads back as a mesh
        // that renders nothing. Fall back to 1 so the projection collapses to a constant instead.
        if (Math.Abs(span.X) < 1e-6f) span.X = 1f;
        if (Math.Abs(span.Y) < 1e-6f) span.Y = 1f;

        for (int index = 0; index < map.Meshes.Count; index++)
        {
            if (wanted.Count > 0 && !wanted.Contains(index)) continue;
            var mesh = map.Meshes[index];

            if (mesh.VertexCount == 0) { skipped.Add($"mesh {index} has no vertices"); continue; }
            if (!map.MeshHasLightmapUv(mesh))
            { skipped.Add($"mesh {index} has no Texcoord7 to edit — add the channel first"); continue; }
            if (!TryLocate(map, mesh, MapGeoBinary.ElemTexcoord7, MapGeoBinary.FmtXY_Float32,
                    out var data, out int stride, out int at0))
            { skipped.Add($"mesh {index} declares Texcoord7 in a format this cannot write"); continue; }

            Vector2[]? source = null;
            switch (mode)
            {
                case UvEditMode.WorldPlanarXz:
                    if (!TryReadWorldPositions(map, mesh, out var world))
                    { skipped.Add($"mesh {index} has no readable Position"); continue; }
                    source = new Vector2[mesh.VertexCount];
                    for (int v = 0; v < mesh.VertexCount; v++)
                        source[v] = new Vector2((world[v].X - canvas.Min.X) / span.X,
                                                (world[v].Z - canvas.Min.Y) / span.Y);
                    break;

                case UvEditMode.CopyTexcoord0:
                    if (!TryReadUvSet(map, mesh, MapGeoBinary.ElemTexcoord0, out var uv0))
                    { skipped.Add($"mesh {index} has no readable Texcoord0 to copy"); continue; }
                    source = uv0;
                    break;

                case UvEditMode.ScaleExisting:
                    if (!TryReadUvSet(map, mesh, MapGeoBinary.ElemTexcoord7, out var uv7))
                    { skipped.Add($"mesh {index} has no readable Texcoord7"); continue; }
                    source = uv7;
                    break;
            }
            if (source is null) continue;

            for (int v = 0; v < mesh.VertexCount; v++)
            {
                var value = source[v] * scale + offset;
                // Never write NaN/Infinity: it survives every check our own reader makes and shows up only
                // as geometry the game drops.
                if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
                { value = Vector2.Zero; }
                int at = v * stride + at0;
                BitConverter.TryWriteBytes(data.AsSpan(at, 4), value.X);
                BitConverter.TryWriteBytes(data.AsSpan(at + 4, 4), value.Y);
                seenMin = Vector2.Min(seenMin, value);
                seenMax = Vector2.Max(seenMax, value);
            }
            changed++;
            vertices += mesh.VertexCount;
        }

        result = new UvEditResult(changed, vertices, skipped,
            changed == 0 ? Vector2.Zero : seenMin, changed == 0 ? Vector2.Zero : seenMax);
        return changed > 0;
    }

    /// <summary>Byte-array variant, matching MeshUvChannelBuilder's shape.</summary>
    public static byte[] SetTexcoord7(byte[] mapGeo, IEnumerable<int> meshIndices, UvEditMode mode,
        Vector2 scale, Vector2 offset, out UvEditResult result,
        IReadOnlySet<string>? extendedChannelMaterials = null, (Vector2 Min, Vector2 Max)? rect = null)
    {
        ArgumentNullException.ThrowIfNull(mapGeo);
        if (!MapGeoBinary.TryReadEditable(mapGeo, out var map, extendedChannelMaterials) || map is null)
            throw new InvalidOperationException(
                "This mapgeo does not round-trip byte-exactly, so it cannot be safely rewritten.");
        return SetTexcoord7(map, meshIndices, mode, scale, offset, out result, rect) ? map.Write() : mapGeo;
    }

    /// <summary>Find an element inside the mesh's buffers, returning the buffer it lives in plus the stride
    /// and byte offset to reach it. The returned array is the LIVE buffer, so writing through it edits the
    /// map — which is what the in-place rewrite above depends on.</summary>
    private static bool TryLocate(MapGeoBinary map, MapGeoBinary.Mesh mesh, uint element, uint format,
        out byte[] data, out int stride, out int offset)
    {
        for (int i = 0; i < mesh.VertexBufferIds.Count; i++)
        {
            var decl = map.Declarations[mesh.VertexDeclarationBase + i];
            int at = 0;
            foreach (var (name, fmt) in decl.Elements)
            {
                if (name == element && fmt == format)
                {
                    var buffer = map.VertexBuffers[mesh.VertexBufferIds[i]].Data;
                    int s = decl.Stride;
                    if (s > 0 && buffer.Length >= s * mesh.VertexCount)
                    { data = buffer; stride = s; offset = at; return true; }
                    data = Array.Empty<byte>(); stride = 0; offset = 0;
                    return false;
                }
                at += MapGeoBinary.FormatSize(fmt);
            }
        }
        data = Array.Empty<byte>(); stride = 0; offset = 0;
        return false;
    }
}
