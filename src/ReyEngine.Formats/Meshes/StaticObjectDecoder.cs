using LeagueToolkit.Core.Mesh;

namespace ReyEngine.Formats.Meshes;

/// <summary>Decoded static object (.scb/.sco) as a flat triangle soup for particle-mesh rendering (M47).
/// Skinned primitives (.skn) attach an <see cref="Animation"/> payload so the viewport can CPU-skin the
/// wing-flap idle per frame (M48); their Positions/Uvs/Indices are the indexed bind-pose mesh.</summary>
public sealed record StaticMeshData(float[] Positions, float[] Uvs, uint[] Indices, string Name)
{
    public int TriangleCount => Indices.Length / 3;
    public VfxMeshAnimation? Animation { get; init; }
    /// <summary>M776: per soup vertex, the .scb/.sco vertex colour in FILE order (b, g, r, a) - Riot's
    /// particlesystem/mesh_vs USE_VERTEX_COLORS permutation reads COLOR0 as v2.zyxw, so the file's byte
    /// order lines up with that swizzle without any reordering here. A mesh emitter's alpha feather (e.g.
    /// Crepe_Brush_Piltover_1_SmokeMesh.scb, whose rim vertices author alpha 0) rides in this channel - the
    /// base permutation (COLOR0 unbound) draws every vertex opaque instead. Null when the mesh has none.</summary>
    public float[]? Colors { get; init; }
}

/// <summary>M48: skinning payload for an animated mesh primitive (butterflies/dragonflies).</summary>
public sealed record VfxMeshAnimation(
    MeshAsset Mesh,
    ReyEngine.Formats.Skeletons.SkeletonAsset Skeleton,
    ReyEngine.Formats.Animation.AnimationClip Clip);

/// <summary>M47: decodes League .scb (binary) / .sco (ascii) static objects via LeagueToolkit's
/// <see cref="StaticMesh"/> reader. Faces carry their own UVs, so vertices are un-shared into a
/// triangle soup (3 verts per face). Never throws — null on failure.</summary>
public static class StaticObjectDecoder
{
    public static StaticMeshData? Decode(byte[] data, string path)
    {
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            var mesh = path.EndsWith(".sco", StringComparison.OrdinalIgnoreCase)
                ? StaticMesh.ReadAscii(ms)
                : StaticMesh.ReadBinary(ms);

            int faces = mesh.Faces.Count;
            if (faces == 0) return null;
            var pos = new float[faces * 3 * 3];
            var uv = new float[faces * 3 * 2];
            var idx = new uint[faces * 3];
            var vcs = mesh.VertexColors;
            var col = vcs is { Count: > 0 } ? new float[faces * 3 * 4] : null;
            int vp = 0, vu = 0, vc = 0;
            Span<int> vid = stackalloc int[3];
            Span<System.Numerics.Vector2> fuv = stackalloc System.Numerics.Vector2[3];
            for (int f = 0; f < faces; f++)
            {
                var face = mesh.Faces[f];
                vid[0] = face.VertexId0; vid[1] = face.VertexId1; vid[2] = face.VertexId2;
                fuv[0] = face.UV0; fuv[1] = face.UV1; fuv[2] = face.UV2;
                for (int k = 0; k < 3; k++)
                {
                    var v = mesh.Vertices[vid[k]];
                    pos[vp++] = v.X; pos[vp++] = v.Y; pos[vp++] = v.Z;
                    uv[vu++] = fuv[k].X; uv[vu++] = fuv[k].Y;
                    idx[f * 3 + k] = (uint)(f * 3 + k);
                    if (col is not null) { var c = vcs![vid[k]]; col[vc++] = c.B; col[vc++] = c.G; col[vc++] = c.R; col[vc++] = c.A; }
                }
            }
            return new StaticMeshData(pos, uv, idx, mesh.Name) { Colors = col };
        }
        catch { return null; }
    }
}
