using LeagueToolkit.Core.Mesh;

namespace ReyEngine.Formats.Meshes;

/// <summary>Decoded static object (.scb/.sco) as a flat triangle soup for particle-mesh rendering (M47).</summary>
public sealed record StaticMeshData(float[] Positions, float[] Uvs, uint[] Indices, string Name)
{
    public int TriangleCount => Indices.Length / 3;
}

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
            int vp = 0, vu = 0;
            for (int f = 0; f < faces; f++)
            {
                var face = mesh.Faces[f];
                Span<int> vid = stackalloc int[] { face.VertexId0, face.VertexId1, face.VertexId2 };
                Span<System.Numerics.Vector2> fuv = stackalloc[] { face.UV0, face.UV1, face.UV2 };
                for (int k = 0; k < 3; k++)
                {
                    var v = mesh.Vertices[vid[k]];
                    pos[vp++] = v.X; pos[vp++] = v.Y; pos[vp++] = v.Z;
                    uv[vu++] = fuv[k].X; uv[vu++] = fuv[k].Y;
                    idx[f * 3 + k] = (uint)(f * 3 + k);
                }
            }
            return new StaticMeshData(pos, uv, idx, mesh.Name);
        }
        catch { return null; }
    }
}
