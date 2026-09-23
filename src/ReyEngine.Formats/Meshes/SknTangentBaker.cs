using System.Buffers.Binary;
using System.Text;

namespace ReyEngine.Formats.Meshes;

/// <summary>
/// M758: bake MikkTSpace tangents into a skin's <c>.skn</c>, for PBR skins that sample a normal map.
///
/// <para><b>LTK Manager's bake, byte for byte.</b> This mirrors <c>ltk_mesh::SkinnedMesh::bake_tangents</c>
/// (league-toolkit; what LTK Manager 1.21's "Bake tangents" runs) and is checked against it on real skins
/// by writing the same bytes: tangents from position, normal and UV0, per submesh range, into the
/// <c>Tangent</c> vertex layout's float4 at +56 (+88 on the 104-byte Ext layout). The stored w is the
/// NEGATIVE of MikkTSpace's sign - equivalent to reversing V - so the shader's
/// <c>B = w * cross(N, T)</c> matches a MikkTSpace normal-map bake with League's UVs.</para>
///
/// <para><b>What changes.</b> A Basic (52 B) or Color (56 B) vertex becomes a Tangent (72 B) one, opaque
/// white colour added where there was none; Tangent and Ext keep their layout and have their tangents
/// replaced. A vertex whose face corners need different tangents is split - never merged - so a range can
/// grow; ranges are repacked in order, vertices outside every range go last, and NORMALIZED_INDICES is set
/// when the mesh passes 65,536 vertices. Positions, normals, UVs, weights, bounds, flags, the direct blend
/// index block and the end tab are kept. The file is always written as version 4.1, as ltk_mesh writes it;
/// a pre-4 file gets the bounds ltk_mesh computes for it. A material name keeps its text up to the first
/// NUL, as the reader does.</para>
///
/// <para><b>What it does not do.</b> Bake a normal map, or rebuild anything that indexes vertices
/// externally. A normal map must be baked in the same tangent basis (MikkTSpace) and triangulation.</para>
/// </summary>
public static class SknTangentBaker
{
    public const uint Magic = 0x00112233;
    public const int MaxVertexCount = 0x10000;
    private const uint DirectBlendIndices = 1, NormalizedIndices = 2;

    public enum VertexType { Basic = 0, Color = 1, Tangent = 2, Ext = 3 }

    public static int SizeOf(VertexType t) => t switch { VertexType.Basic => 52, VertexType.Color => 56, VertexType.Tangent => 72, _ => 104 };

    public sealed class Range
    {
        public required string Material;
        public int StartVertex, VertexCount, StartIndex, IndexCount;
    }

    /// <summary>A .skn as ltk_mesh holds it: indices range-relative whatever the file stored.</summary>
    public sealed class Skn
    {
        public List<Range> Ranges = new();
        public uint Flags;
        public VertexType Type;
        public byte[] Vertices = Array.Empty<byte>();
        public ushort[] Indices = Array.Empty<ushort>();
        public float[] Bounds = new float[10];   // aabb min xyz, max xyz, sphere origin xyz, radius
        public byte[]? DirectBlendIndexBlock;
        public byte[] EndTab = new byte[12];
        public int Stride => SizeOf(Type);
        public int VertexCount => Vertices.Length / Stride;
    }

    /// <summary>What a bake did, for the log.</summary>
    public sealed record Result(VertexType From, VertexType To, int VerticesBefore, int VerticesAfter, int Ranges);

    public sealed class BakeException(string message) : Exception(message);

    // ------------------------------------------------------------------ read / write

    public static Skn Read(byte[] data)
    {
        var r = new Reader(data);
        if (r.U32() != Magic) throw new BakeException("not a .skn (bad magic)");
        int major = r.U16(), minor = r.U16();
        if (major is not (0 or 1 or 2 or 4) || minor != 1) throw new BakeException($".skn version {major}.{minor} is not one the game loads");
        var skn = new Skn { Type = VertexType.Basic };
        int indexCount, vertexCount;
        bool haveBounds = false;
        if (major == 0)
        {
            indexCount = (int)r.U32();
            vertexCount = (int)r.U32();
            skn.Ranges.Add(new Range { Material = "", StartVertex = 0, VertexCount = vertexCount, StartIndex = 0, IndexCount = indexCount });
        }
        else
        {
            uint rangeCount = r.U32();
            for (int i = 0; i < rangeCount; i++)
            {
                var name = r.Bytes(64);
                int nul = Array.IndexOf(name, (byte)0);
                skn.Ranges.Add(new Range
                {
                    Material = Encoding.UTF8.GetString(name, 0, nul < 0 ? 64 : nul),
                    StartVertex = r.I32(), VertexCount = r.I32(), StartIndex = r.I32(), IndexCount = r.I32(),
                });
            }
            if (major == 4)
            {
                skn.Flags = r.U32();
                indexCount = (int)r.U32();
                vertexCount = (int)r.U32();
                uint size = r.U32();
                uint type = r.U32();
                if (type > 3) throw new BakeException($"vertex type {type} is not one the format defines");
                skn.Type = (VertexType)type;
                if (size != SizeOf(skn.Type)) throw new BakeException($"vertex type {skn.Type} with size {size}");
                for (int i = 0; i < 10; i++) skn.Bounds[i] = r.F32();
                haveBounds = true;
            }
            else
            {
                indexCount = (int)r.U32();
                vertexCount = (int)r.U32();
            }
        }
        if ((uint)vertexCount > MaxVertexCount && (skn.Flags & NormalizedIndices) == 0)
            throw new BakeException($"{vertexCount} vertices without NORMALIZED_INDICES");
        if ((skn.Flags & DirectBlendIndices) != 0)
            skn.DirectBlendIndexBlock = r.Bytes(r.U16());
        skn.Indices = new ushort[indexCount];
        for (int i = 0; i < indexCount; i++) skn.Indices[i] = r.U16();
        if ((skn.Flags & NormalizedIndices) == 0) Rebase(skn.Indices, skn.Ranges, subtract: true);
        skn.Vertices = r.Bytes(vertexCount * skn.Stride);
        if (major >= 2) skn.EndTab = r.Bytes(12);
        if (!haveBounds) ComputeBounds(skn);
        return skn;
    }

    public static byte[] Write(Skn skn)
    {
        int count = skn.VertexCount;
        bool normalized = (skn.Flags & NormalizedIndices) != 0;
        if (count > MaxVertexCount && !normalized) throw new BakeException($"{count} vertices need NORMALIZED_INDICES");
        var indices = (ushort[])skn.Indices.Clone();
        if (!normalized) Rebase(indices, skn.Ranges, subtract: false);
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write((ushort)4); w.Write((ushort)1);
        w.Write((uint)skn.Ranges.Count);
        foreach (var r in skn.Ranges)
        {
            var name = new byte[64];
            var text = Encoding.UTF8.GetBytes(r.Material);
            Array.Copy(text, name, Math.Min(64, text.Length));
            w.Write(name);
            w.Write(r.StartVertex); w.Write(r.VertexCount); w.Write(r.StartIndex); w.Write(r.IndexCount);
        }
        w.Write(skn.Flags);
        w.Write((uint)indices.Length);
        w.Write((uint)count);
        w.Write((uint)skn.Stride);
        w.Write((uint)skn.Type);
        foreach (float f in skn.Bounds) w.Write(f);
        if (skn.DirectBlendIndexBlock is { } block)
        {
            w.Write((ushort)block.Length);
            w.Write(block);
        }
        foreach (var i in indices) w.Write(i);
        w.Write(skn.Vertices);
        w.Write(skn.EndTab);
        return ms.ToArray();
    }

    private static void Rebase(ushort[] indices, List<Range> ranges, bool subtract)
    {
        foreach (var r in ranges)
        {
            ushort start = unchecked((ushort)r.StartVertex);
            if (start == 0) continue;
            int from = Math.Clamp(r.StartIndex, 0, indices.Length);
            int to = Math.Clamp(r.StartIndex + r.IndexCount, from, indices.Length);
            for (int i = from; i < to; i++)
                indices[i] = unchecked(subtract ? (ushort)(indices[i] - start) : (ushort)(indices[i] + start));
        }
    }

    /// <summary>ltk_primitives: AABB of the positions, and a sphere from its centre to its max corner.</summary>
    private static void ComputeBounds(Skn skn)
    {
        float[] min = { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        float[] max = { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        for (int v = 0; v < skn.VertexCount; v++)
            for (int i = 0; i < 3; i++)
            {
                float p = BitConverter.ToSingle(skn.Vertices, v * skn.Stride + i * 4);
                if (p < min[i]) min[i] = p;
                if (p > max[i]) max[i] = p;
            }
        float cx = 0.5f * (min[0] + max[0]), cy = 0.5f * (min[1] + max[1]), cz = 0.5f * (min[2] + max[2]);
        // Rust's (a - b).powf(2.0): LLVM folds pow(x, 2.0) into x * x, which MathF.Pow (the C runtime's
        // powf) does not always round the same - measured on Syndra's v2.1 sphere, the one place it showed
        float dx = cx - max[0], dy = cy - max[1], dz = cz - max[2];
        float radius = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        skn.Bounds = new[] { min[0], min[1], min[2], max[0], max[1], max[2], cx, cy, cz, radius };
    }

    private sealed class Reader(byte[] data)
    {
        private int _at;
        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _at + n > data.Length) throw new BakeException("the file ends early");
            var s = data.AsSpan(_at, n);
            _at += n;
            return s;
        }
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public byte[] Bytes(int n) => Take(n).ToArray();
    }

    // ------------------------------------------------------------------ bake

    /// <summary>Bake into a .skn file's bytes; returns the new file. Throws <see cref="BakeException"/>
    /// with the reason ltk_mesh would give, and then the input is untouched.</summary>
    public static byte[] Bake(byte[] skn, out Result result)
    {
        var mesh = Read(skn);
        result = Bake(mesh);
        return Write(mesh);
    }

    public static Result Bake(Skn mesh)
    {
        var source = mesh.Type;
        var output = source == VertexType.Ext ? VertexType.Ext : VertexType.Tangent;
        int inStride = mesh.Stride, outStride = SizeOf(output);
        int vc = mesh.VertexCount;
        const int NormalAt = 32, UvAt = 44;

        var positions = new (float X, float Y, float Z)[vc];
        var normals = new (float X, float Y, float Z)[vc];
        var uvs = new (float U, float V)[vc];
        for (int v = 0; v < vc; v++)
        {
            int o = v * inStride;
            float F(int at) => BitConverter.ToSingle(mesh.Vertices, o + at);
            positions[v] = (F(0), F(4), F(8));
            var n = TryNormalize((F(NormalAt), F(NormalAt + 4), F(NormalAt + 8)));
            uvs[v] = (F(UvAt), F(UvAt + 4));
            bool finite = float.IsFinite(positions[v].X) && float.IsFinite(positions[v].Y) && float.IsFinite(positions[v].Z)
                          && float.IsFinite(uvs[v].U) && float.IsFinite(uvs[v].V);
            if (!finite || n is null) throw new BakeException($"vertex {v} has invalid position, normal or UV");
            normals[v] = n.Value;
        }

        // validate before any generation
        var owners = new bool[mesh.Indices.Length];
        var spans = new List<(int VStart, int VLen, int IStart, int ILen)>();
        for (int r = 0; r < mesh.Ranges.Count; r++)
        {
            var range = mesh.Ranges[r];
            var (vs, vl) = CheckedSpan(range.StartVertex, range.VertexCount, vc);
            var (@is, il) = CheckedSpan(range.StartIndex, range.IndexCount, mesh.Indices.Length);
            if (il % 3 != 0) throw new BakeException($"range {r} is not a triangle list");
            if (vl > MaxVertexCount) throw new BakeException($"range {r} has more than {MaxVertexCount} vertices");
            for (int i = @is; i < @is + il; i++)
            {
                if (owners[i]) throw new BakeException($"range {r} overlaps another index range");
                owners[i] = true;
                if (mesh.Indices[i] >= vl) throw new BakeException($"index {i} is outside range {r}");
            }
            spans.Add((vs, vl, @is, il));
        }

        var bytes = new List<byte>(vc * outStride + vc / 4 * outStride);
        var indices = (ushort[])mesh.Indices.Clone();
        var ranges = mesh.Ranges.Select(x => new Range { Material = x.Material, StartVertex = x.StartVertex, VertexCount = x.VertexCount, StartIndex = x.StartIndex, IndexCount = x.IndexCount }).ToList();
        var used = new bool[vc];
        for (int r = 0; r < spans.Count; r++)
        {
            var (vs, vl, @is, il) = spans[r];
            var corners = new int[il];
            for (int k = 0; k < il; k++) corners[k] = vs + mesh.Indices[@is + k];
            MikkTSpace.Tangent?[] spaces = il > 0 ? MikkTSpace.Generate(corners, positions, normals, uvs) : Array.Empty<MikkTSpace.Tangent?>();

            int @baseVertex = bytes.Count / outStride;
            ranges[r].StartVertex = @baseVertex;
            for (int v = vs; v < vs + vl; v++)
            {
                used[v] = true;
                AppendVertex(bytes, mesh.Vertices, v, inStride, source, Fallback(normals[v]));
            }
            var assigned = new bool[vl];
            var variants = new Dictionary<(int Local, uint X, uint Y, uint Z, uint W), int>();
            int count = vl;
            for (int corner = 0; corner < il; corner++)
            {
                int v = corners[corner];
                int local = v - vs;
                var t = Encode(spaces[corner], normals[v]) ?? Fallback(normals[v]);
                var key = (local, Bits(t.X), Bits(t.Y), Bits(t.Z), Bits(t.W));
                if (!variants.TryGetValue(key, out int outIndex))
                {
                    if (!assigned[local])
                    {
                        assigned[local] = true;
                        WriteTangent(bytes, (@baseVertex + local + 1) * outStride - 16, t);
                        outIndex = local;
                    }
                    else
                    {
                        if (count == MaxVertexCount) throw new BakeException($"range {r} has more than {MaxVertexCount} vertices after splitting");
                        AppendVertex(bytes, mesh.Vertices, v, inStride, source, t);
                        outIndex = count;
                        count++;
                    }
                    variants[key] = outIndex;
                }
                indices[@is + corner] = (ushort)outIndex;
            }
            ranges[r].VertexCount = count;
        }
        for (int v = 0; v < vc; v++)
            if (!used[v]) AppendVertex(bytes, mesh.Vertices, v, inStride, source, Fallback(normals[v]));

        int total = bytes.Count / outStride;
        mesh.Vertices = bytes.ToArray();
        mesh.Type = output;
        mesh.Indices = indices;
        mesh.Ranges = ranges;
        if (total > MaxVertexCount) mesh.Flags |= NormalizedIndices;
        return new Result(source, output, vc, total, ranges.Count);
    }

    private static (int Start, int Length) CheckedSpan(int start, int count, int limit)
    {
        if (start < 0) throw new BakeException("negative range start");
        if (count < 0) throw new BakeException("negative range count");
        if ((long)start + count > limit) throw new BakeException("range exceeds buffer");
        return (start, count);
    }

    /// <summary>Signed zeros share one key, so -0 and 0 tangents deduplicate (as ltk_mesh does).</summary>
    private static uint Bits(float f) => f == 0f ? 0u : BitConverter.SingleToUInt32Bits(f);

    private readonly record struct V4(float X, float Y, float Z, float W);

    /// <summary>glam 0.27 Vec3::try_normalize: multiply by 1/length when that is finite and positive.</summary>
    private static (float X, float Y, float Z)? TryNormalize((float X, float Y, float Z) v)
    {
        float len = MathF.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));
        float rcp = 1f / len;
        if (float.IsFinite(rcp) && rcp > 0f) return (v.X * rcp, v.Y * rcp, v.Z * rcp);
        return null;
    }

    /// <summary>ltk_mesh's set_tangent: project out the normal, normalise, and store the negated sign.</summary>
    private static V4? Encode(MikkTSpace.Tangent? space, (float X, float Y, float Z) n)
    {
        if (space is not { } s) return null;
        float d = (n.X * s.X) + (n.Y * s.Y) + (n.Z * s.Z);
        var projected = TryNormalize((s.X - n.X * d, s.Y - n.Y * d, s.Z - n.Z * d));
        if (projected is not { } p) return null;
        float sign = s.OrientationPreserving ? 1f : -1f;
        return new V4(p.X, p.Y, p.Z, -sign);
    }

    /// <summary>glam's any_orthonormal_vector (Pixar's ONB), w = -1: a deterministic perpendicular for
    /// the corners MikkTSpace leaves without a tangent.</summary>
    private static V4 Fallback((float X, float Y, float Z) n)
    {
        float sign = float.IsNaN(n.Z) ? float.NaN : MathF.CopySign(1f, n.Z);
        float a = -1f / (sign + n.Z);
        float b = n.X * n.Y * a;
        return new V4(b, sign + n.Y * n.Y * a, -n.Y, -1f);
    }

    private static void AppendVertex(List<byte> bytes, byte[] src, int v, int stride, VertexType kind, V4 tangent)
    {
        var s = src.AsSpan(v * stride, stride);
        switch (kind)
        {
            case VertexType.Basic:
                foreach (var x in s) bytes.Add(x);
                bytes.Add(255); bytes.Add(255); bytes.Add(255); bytes.Add(255);
                break;
            case VertexType.Color:
                foreach (var x in s) bytes.Add(x);
                break;
            default:
                foreach (var x in s[..^16]) bytes.Add(x);
                break;
        }
        AddFloat(bytes, tangent.X); AddFloat(bytes, tangent.Y); AddFloat(bytes, tangent.Z); AddFloat(bytes, tangent.W);
    }

    private static void AddFloat(List<byte> bytes, float f)
    {
        uint u = BitConverter.SingleToUInt32Bits(f);
        bytes.Add((byte)u); bytes.Add((byte)(u >> 8)); bytes.Add((byte)(u >> 16)); bytes.Add((byte)(u >> 24));
    }

    private static void WriteTangent(List<byte> bytes, int at, V4 t)
    {
        void Put(int o, float f)
        {
            uint u = BitConverter.SingleToUInt32Bits(f);
            bytes[o] = (byte)u; bytes[o + 1] = (byte)(u >> 8); bytes[o + 2] = (byte)(u >> 16); bytes[o + 3] = (byte)(u >> 24);
        }
        Put(at, t.X); Put(at + 4, t.Y); Put(at + 8, t.Z); Put(at + 12, t.W);
    }
}
