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

        IndexArray ia = skn.Indices;
        var indices = new uint[ia.Count];
        for (int i = 0; i < ia.Count; i++) indices[i] = ia[i];

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
        };
    }
}
