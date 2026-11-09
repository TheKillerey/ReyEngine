using System.Numerics;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;

namespace ReyEngine.Formats.Meshes;

/// <summary>Decodes a .skn (Simple Skin) chunk into a <see cref="MeshAsset"/> via LeagueToolkit.</summary>
public static class SkinnedMeshDecoder
{
    public static MeshAsset Decode(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        SkinnedMesh skn = SkinnedMesh.ReadFromSimpleSkin(ms);

        IVertexBufferView view = skn.VerticesView;
        int vc = view.VertexCount;

        var positions = new float[vc * 3];
        var normals = new float[vc * 3];
        var uvs = new float[vc * 2];

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        var posArray = view.GetAccessor(ElementName.Position).AsVector3Array();
        for (int i = 0; i < vc; i++)
        {
            Vector3 p = posArray[i];
            positions[i * 3] = p.X;
            positions[i * 3 + 1] = p.Y;
            positions[i * 3 + 2] = p.Z;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        if (view.TryGetAccessor(ElementName.Normal, out var normalAccessor))
        {
            var n = normalAccessor.AsVector3Array();
            for (int i = 0; i < vc; i++)
            {
                Vector3 v = n[i];
                normals[i * 3] = v.X;
                normals[i * 3 + 1] = v.Y;
                normals[i * 3 + 2] = v.Z;
            }
        }

        if (view.TryGetAccessor(ElementName.Texcoord0, out var uvAccessor))
        {
            var t = uvAccessor.AsVector2Array();
            for (int i = 0; i < vc; i++)
            {
                Vector2 v = t[i];
                uvs[i * 2] = v.X;
                uvs[i * 2 + 1] = v.Y;
            }
        }

        int[]? blendIndices = null;
        float[]? blendWeights = null;
        if (view.TryGetAccessor(ElementName.BlendIndex, out var biAccessor) &&
            view.TryGetAccessor(ElementName.BlendWeight, out var bwAccessor))
        {
            try
            {
                var bi = biAccessor.AsXyzwU8Array();
                var bw = bwAccessor.AsVector4Array();
                blendIndices = new int[vc * 4];
                blendWeights = new float[vc * 4];
                for (int i = 0; i < vc; i++)
                {
                    var b = bi[i];
                    blendIndices[i * 4] = b.x; blendIndices[i * 4 + 1] = b.y;
                    blendIndices[i * 4 + 2] = b.z; blendIndices[i * 4 + 3] = b.w;
                    var w = bw[i];
                    blendWeights[i * 4] = w.X; blendWeights[i * 4 + 1] = w.Y;
                    blendWeights[i * 4 + 2] = w.Z; blendWeights[i * 4 + 3] = w.W;
                }
            }
            catch { blendIndices = null; blendWeights = null; }
        }

        // M646: the vertex colour. Riot's newer champion shaders read it - Locke's Onsen passes COLOR0
        // straight through as TEXCOORD0 and spends COLOR0.z as its dissolve and transition mask, so a
        // decoder that dropped it (this one did, since M1) handed every such shader a constant white and
        // with it a constant mask. Absent stays null, which the renderers turn into white.
        //
        // The four bytes go out IN FILE ORDER: x = byte 0 ... w = byte 3. LeagueToolkit labels the element
        // BGRA_Packed8888 and its accessor hands byte 0 back as "b", but the shader does not see that
        // label. Measured on locke_base.skn: byte 2 is a painted height gradient (1 at the feet, 238 at the
        // crown, correlation 1.00 with bind-pose Y on the body; 221..248 on the head) and bytes 0 and 1
        // are 0 - and the Onsen pixel shader smoothsteps COLOR0.z against VCDissolve_Value. Put the
        // labelled "r" (byte 2) into x and the shader reads z = byte 0 = 0: everything below
        // VCDissolve_Value dissolves and the body is discarded whole. File order gives z = byte 2, the
        // gradient, and the character. (The mapgeo decoder still reads the label's order; it was not
        // measured here.)
        float[]? colors = null;
        if (view.TryGetAccessor(ElementName.PrimaryColor, out var colorAccessor))
        {
            colors = new float[vc * 4];
            try
            {
                var arr = colorAccessor.AsBgraU8Array();
                for (int i = 0; i < vc; i++)
                {
                    var c = arr[i];   // (byte b, byte g, byte r, byte a) = bytes 0, 1, 2, 3 of the element
                    colors[i * 4] = c.b / 255f; colors[i * 4 + 1] = c.g / 255f;
                    colors[i * 4 + 2] = c.r / 255f; colors[i * 4 + 3] = c.a / 255f;
                }
            }
            catch
            {
                try
                {
                    var arr = colorAccessor.AsVector4Array();
                    for (int i = 0; i < vc; i++)
                    {
                        var c = arr[i];
                        colors[i * 4] = c.X; colors[i * 4 + 1] = c.Y; colors[i * 4 + 2] = c.Z; colors[i * 4 + 3] = c.W;
                    }
                }
                catch { colors = null; }
            }
        }

        IndexArray ia = skn.Indices;
        var indices = new uint[ia.Count];
        for (int i = 0; i < ia.Count; i++) indices[i] = ia[i];

        // M888: a v4 file with NORMALIZED_INDICES (flags bit 1) stores every range's indices RELATIVE to
        // that range's StartVertex - the game draws each range with it as the base vertex. LeagueToolkit
        // hands them back as stored, so every range after the first indexed the first range's vertices:
        // sheets of stretched triangles and a box round the body on Akali skin102, Kai'Sa skin83,
        // Evelynn skin65, Ahri skin97 (all flags=2), while skins without the flag (Kai'Sa skin1, flags=0,
        // absolute indices) were fine. Made absolute here, once, for every consumer of the MeshAsset.
        // SknTangentBaker reads the same flag the same way.
        if (HasNormalizedIndices(data))
            foreach (SkinnedMeshRange r in skn.Ranges)
            {
                if (r.StartVertex <= 0) continue;
                int from = Math.Clamp(r.StartIndex, 0, indices.Length);
                int to = Math.Clamp(r.StartIndex + r.IndexCount, from, indices.Length);
                for (int i = from; i < to; i++) indices[i] += (uint)r.StartVertex;
            }

        var subs = new List<SubMeshInfo>();
        foreach (SkinnedMeshRange r in skn.Ranges)
            subs.Add(new SubMeshInfo(r.Material, r.StartIndex, r.IndexCount, r.VertexCount));

        if (vc == 0) { min = Vector3.Zero; max = Vector3.Zero; }

        return new MeshAsset
        {
            Positions = positions,
            Normals = normals,
            Uvs = uvs,
            Indices = indices,
            VertexCount = vc,
            SubMeshes = subs,
            BoundsMin = min,
            BoundsMax = max,
            BlendIndices = blendIndices,
            BlendWeights = blendWeights,
            Colors = colors,   // M646
        };
    }

    /// <summary>M888: the SKN v4 header's NORMALIZED_INDICES flag (bit 1): magic, u16 major, u16 minor,
    /// u32 range count, 80-byte ranges, then the u32 flags - the layout SknTangentBaker reads. Other
    /// versions have no flags and always store absolute indices.</summary>
    public static bool HasNormalizedIndices(ReadOnlySpan<byte> skn)
    {
        const uint NormalizedIndices = 2;
        if (skn.Length < 12 || BitConverter.ToUInt16(skn[4..]) != 4) return false;
        long at = 12 + 80L * BitConverter.ToUInt32(skn[8..]);
        return at + 4 <= skn.Length && (BitConverter.ToUInt32(skn[(int)at..]) & NormalizedIndices) != 0;
    }
}
