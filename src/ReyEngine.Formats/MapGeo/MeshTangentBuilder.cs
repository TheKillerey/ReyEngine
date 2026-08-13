using System.Numerics;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M433: give meshes the TANGENT channel (Texcoord6) some shaders read.
///
/// <para><b>Why Texcoord6 and not "Tangent".</b> Measured from Riot's own compiled bytecode:
/// <c>Shaders/StaticMesh/Mantis_Env_Baked_PBR</c>'s vertex shader declares five inputs — POSITION0,
/// NORMAL0, TEXCOORD0, <b>TEXCOORD6 (float4, mask xyzw, fully read)</b> and TEXCOORD7 — and its define
/// pool includes FEATURE_TANGENT. The mapgeo element literally named <c>Tangent</c> appears in 0 of
/// 40,512 shipped meshes, which is why an earlier pass wrongly concluded no shader wanted tangents: the
/// tangent frame travels as Texcoord6. Only 1 of those 40,512 meshes carries Texcoord6 at all
/// (map22/carousel_set14, on Set14_Carousel_RainPuddleFX_MAT), and it stores XYZ_Packed161616.</para>
///
/// <para><b>What is written.</b> XYZW_Float32: xyz is the normalised tangent, w is the handedness
/// (+1/-1) that lets the shader rebuild the bitangent as <c>cross(N,T)*w</c>. That matches what the
/// shader reads. The shipped example stores three packed components and lets w default to 1, which is
/// the lossier option — a mirrored UV chart needs the sign.</para>
///
/// <para><b>Honest limit.</b> Deriving a tangent basis from positions + UV0 + normals is standard and
/// well-defined, but it is DERIVED, not authored. Degenerate UVs (zero-area in UV space) have no defined
/// tangent; those vertices get an arbitrary vector orthogonal to the normal rather than NaN, and the
/// count is reported so it is never silent.</para>
/// </summary>
public static class MeshTangentBuilder
{
    /// <summary>What one add-tangents pass did.</summary>
    public sealed record Result(int MeshesChanged, int VerticesWritten, int DegenerateVertices,
                                IReadOnlyList<string> Skipped)
    {
        public string Summary => MeshesChanged == 0
            ? "No mesh changed" + (Skipped.Count > 0 ? ": " + string.Join("; ", Skipped.Take(3)) : ".")
            : $"Added Texcoord6 tangents to {MeshesChanged:n0} mesh(es), {VerticesWritten:n0} vertices"
              + (DegenerateVertices > 0 ? $", {DegenerateVertices:n0} from degenerate UVs" : "")
              + (Skipped.Count > 0 ? $"; skipped {Skipped.Count} ({string.Join("; ", Skipped.Take(3))})" : ".");
    }

    /// <summary>Add Texcoord6 to the meshes at the given ordinals; an empty selection means every mesh.</summary>
    /// <param name="extendedChannelMaterials">M444: materials that lengthen the per-mesh channel block.
    /// Tangents are exactly what a Mantis material wants, so this is the path most likely to need it.</param>
    public static byte[] AddTangents(byte[] mapGeo, IEnumerable<int> meshIndices, out Result result,
        IReadOnlySet<string>? extendedChannelMaterials = null)
    {
        ArgumentNullException.ThrowIfNull(mapGeo);
        if (!MapGeoBinary.TryReadEditable(mapGeo, out var map, extendedChannelMaterials) || map is null)
            throw new InvalidOperationException(
                "This mapgeo does not round-trip byte-exactly, so it cannot be safely rewritten.");
        return AddTangents(map, meshIndices, out result) ? map.Write() : mapGeo;
    }

    /// <summary>In-place variant for callers that already hold the editable layer.</summary>
    public static bool AddTangents(MapGeoBinary map, IEnumerable<int> meshIndices, out Result result)
    {
        ArgumentNullException.ThrowIfNull(map);
        var wanted = new HashSet<int>(meshIndices);
        var skipped = new List<string>();
        int changed = 0, vertices = 0, degenerate = 0;

        for (int index = 0; index < map.Meshes.Count; index++)
        {
            if (wanted.Count > 0 && !wanted.Contains(index)) continue;
            var mesh = map.Meshes[index];

            if (map.MeshHasElement(mesh, MapGeoBinary.ElemTexcoord6))
            { skipped.Add($"mesh {index} already has Texcoord6"); continue; }
            if (mesh.VertexCount == 0) { skipped.Add($"mesh {index} has no vertices"); continue; }

            if (!TryReadStream(map, mesh, MapGeoBinary.ElemPosition, MapGeoBinary.FmtXYZ_Float32, 3, out var pos)
                || !TryReadStream(map, mesh, MapGeoBinary.ElemNormal, MapGeoBinary.FmtXYZ_Float32, 3, out var nrm)
                || !TryReadStream(map, mesh, MapGeoBinary.ElemTexcoord0, MapGeoBinary.FmtXY_Float32, 2, out var uv))
            { skipped.Add($"mesh {index} needs float Position, Normal and Texcoord0 to derive tangents"); continue; }

            var indices = ReadIndices(map, mesh);
            if (indices.Length == 0) { skipped.Add($"mesh {index} has no readable index buffer"); continue; }

            var tangents = Compute(pos, nrm, uv, indices, mesh.VertexCount, out int deg);
            var payload = new byte[mesh.VertexCount * 16];
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                var t = tangents[v];
                BitConverter.TryWriteBytes(payload.AsSpan(v * 16 + 0), t.X);
                BitConverter.TryWriteBytes(payload.AsSpan(v * 16 + 4), t.Y);
                BitConverter.TryWriteBytes(payload.AsSpan(v * 16 + 8), t.Z);
                BitConverter.TryWriteBytes(payload.AsSpan(v * 16 + 12), t.W);
            }

            map.AddVertexChannel(mesh, MapGeoBinary.ElemTexcoord6, MapGeoBinary.FmtXYZW_Float32, payload);
            changed++;
            vertices += mesh.VertexCount;
            degenerate += deg;
        }

        if (changed > 0) map.Compact();
        result = new Result(changed, vertices, degenerate, skipped);
        return changed > 0;
    }

    /// <summary>Per-triangle tangent accumulation, then Gram-Schmidt against the normal — the standard
    /// derivation. Handedness comes from whether the accumulated bitangent agrees with cross(N,T).</summary>
    private static Vector4[] Compute(float[] pos, float[] nrm, float[] uv, int[] indices, int vertexCount,
        out int degenerate)
    {
        var tan = new Vector3[vertexCount];
        var bit = new Vector3[vertexCount];

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;

            var p0 = At3(pos, a); var p1 = At3(pos, b); var p2 = At3(pos, c);
            var w0 = At2(uv, a);  var w1 = At2(uv, b);  var w2 = At2(uv, c);

            var e1 = p1 - p0; var e2 = p2 - p0;
            float s1 = w1.X - w0.X, t1 = w1.Y - w0.Y;
            float s2 = w2.X - w0.X, t2 = w2.Y - w0.Y;

            float det = s1 * t2 - s2 * t1;
            if (MathF.Abs(det) < 1e-12f) continue;          // zero-area in UV space: contributes nothing
            float r = 1f / det;

            var sdir = (e1 * t2 - e2 * t1) * r;
            var tdir = (e2 * s1 - e1 * s2) * r;
            tan[a] += sdir; tan[b] += sdir; tan[c] += sdir;
            bit[a] += tdir; bit[b] += tdir; bit[c] += tdir;
        }

        degenerate = 0;
        var result = new Vector4[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            var n = At3(nrm, v);
            if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n); else n = Vector3.UnitY;

            var t = tan[v] - n * Vector3.Dot(n, tan[v]);    // Gram-Schmidt: orthogonalise against the normal
            if (t.LengthSquared() < 1e-12f)
            {
                degenerate++;
                // No UV gradient reached this vertex. Any unit vector orthogonal to the normal is as
                // defensible as another; pick one deterministically rather than emitting NaN.
                var axis = MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
                t = Vector3.Cross(n, axis);
            }
            t = Vector3.Normalize(t);

            float handedness = Vector3.Dot(Vector3.Cross(n, t), bit[v]) < 0f ? -1f : 1f;
            result[v] = new Vector4(t, handedness);
        }
        return result;
    }

    private static Vector3 At3(float[] a, int i) => new(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);
    private static Vector2 At2(float[] a, int i) => new(a[i * 2], a[i * 2 + 1]);

    /// <summary>Read a float stream out of whichever buffer declares it, in the expected format.</summary>
    private static bool TryReadStream(MapGeoBinary map, MapGeoBinary.Mesh mesh, uint element, uint format,
        int components, out float[] values)
    {
        values = Array.Empty<float>();
        for (int i = 0; i < mesh.VertexBufferIds.Count; i++)
        {
            var decl = map.Declarations[mesh.VertexDeclarationBase + i];
            int offset = 0;
            foreach (var (name, fmt) in decl.Elements)
            {
                if (name == element)
                {
                    if (fmt != format) return false;        // packed/other formats are not decoded here
                    var data = map.VertexBuffers[mesh.VertexBufferIds[i]].Data;
                    int stride = decl.Stride;
                    if (stride <= 0 || data.Length < stride * mesh.VertexCount) return false;
                    var result = new float[mesh.VertexCount * components];
                    for (int v = 0; v < mesh.VertexCount; v++)
                        for (int c = 0; c < components; c++)
                            result[v * components + c] = BitConverter.ToSingle(data, v * stride + offset + c * 4);
                    values = result;
                    return true;
                }
                offset += MapGeoBinary.FormatSize(fmt);
            }
        }
        return false;
    }

    /// <summary>The mesh's slice of its index buffer, as vertex ordinals.</summary>
    private static int[] ReadIndices(MapGeoBinary map, MapGeoBinary.Mesh mesh)
    {
        if (mesh.IndexBufferId < 0 || mesh.IndexBufferId >= map.IndexBuffers.Count) return Array.Empty<int>();
        var data = map.IndexBuffers[mesh.IndexBufferId].Data;
        int count = Math.Min(mesh.IndexCount, data.Length / 2);
        if (count <= 0) return Array.Empty<int>();
        var result = new int[count];
        for (int i = 0; i < count; i++) result[i] = BitConverter.ToUInt16(data, i * 2);
        return result;
    }
}
