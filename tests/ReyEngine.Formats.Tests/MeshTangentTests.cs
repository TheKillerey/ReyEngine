using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>M433: deriving the Texcoord6 tangent channel. Mantis_Env_Baked_PBR's compiled vertex shader
/// reads TEXCOORD6 as a fully-used float4; a vertex shader input with no matching vertex element is an
/// input-layout failure at load.</summary>
public class MeshTangentTests
{
    /// <summary>One triangle in the XY plane with UVs running along +X and +Y, so the correct tangent is
    /// exactly +X and the normal +Z. A case whose answer is known by inspection.</summary>
    private static MapGeoBinary Triangle(Vector2 uv0, Vector2 uv1, Vector2 uv2)
    {
        Vector3[] pos = { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0) };
        Vector3[] nrm = { new(0, 0, 1), new(0, 0, 1), new(0, 0, 1) };
        Vector2[] uv = { uv0, uv1, uv2 };

        var data = new byte[3 * 32];                       // pos(12) + nrm(12) + uv(8)
        for (int i = 0; i < 3; i++)
        {
            int at = i * 32;
            BitConverter.TryWriteBytes(data.AsSpan(at + 0), pos[i].X);
            BitConverter.TryWriteBytes(data.AsSpan(at + 4), pos[i].Y);
            BitConverter.TryWriteBytes(data.AsSpan(at + 8), pos[i].Z);
            BitConverter.TryWriteBytes(data.AsSpan(at + 12), nrm[i].X);
            BitConverter.TryWriteBytes(data.AsSpan(at + 16), nrm[i].Y);
            BitConverter.TryWriteBytes(data.AsSpan(at + 20), nrm[i].Z);
            BitConverter.TryWriteBytes(data.AsSpan(at + 24), uv[i].X);
            BitConverter.TryWriteBytes(data.AsSpan(at + 28), uv[i].Y);
        }

        var indices = new byte[6];
        for (int i = 0; i < 3; i++) BitConverter.TryWriteBytes(indices.AsSpan(i * 2), (ushort)i);

        var map = new MapGeoBinary { Version = 18 };
        map.Declarations.Add(new MapGeoBinary.VertexDeclaration
        {
            Elements = { (MapGeoBinary.ElemPosition, MapGeoBinary.FmtXYZ_Float32),
                         (MapGeoBinary.ElemNormal, MapGeoBinary.FmtXYZ_Float32),
                         (MapGeoBinary.ElemTexcoord0, MapGeoBinary.FmtXY_Float32) },
            Padding = new byte[8 * 12],
        });
        map.VertexBuffers.Add(new MapGeoBinary.VertexBuffer { Data = data, HasVisibility = true, Visibility = 0xFF });
        map.IndexBuffers.Add(new MapGeoBinary.IndexBuffer { Data = indices, HasVisibility = true, Visibility = 0xFF });
        map.Meshes.Add(new MapGeoBinary.Mesh
        {
            VertexCount = 3, VertexDeclarationBase = 0, VertexBufferIds = { 0 },
            IndexCount = 3, IndexBufferId = 0, Transform = Matrix4x4.Identity,
            HasVisibility = true, Visibility = 0xFF,
            HasRegionHash = true, HasVcHash = true, HasDisableBackface = true,
            HasLayerTransition = true, RenderFlagsIsUshort = true,
        });
        map.Tail = new byte[8];
        return map;
    }

    private static Vector4[] ReadTangents(MapGeoBinary map, MapGeoBinary.Mesh mesh)
    {
        for (int i = 0; i < mesh.VertexBufferIds.Count; i++)
        {
            if (!map.Declarations[mesh.VertexDeclarationBase + i].Has(MapGeoBinary.ElemTexcoord6)) continue;
            var data = map.VertexBuffers[mesh.VertexBufferIds[i]].Data;
            var result = new Vector4[mesh.VertexCount];
            for (int v = 0; v < mesh.VertexCount; v++)
                result[v] = new Vector4(
                    BitConverter.ToSingle(data, v * 16), BitConverter.ToSingle(data, v * 16 + 4),
                    BitConverter.ToSingle(data, v * 16 + 8), BitConverter.ToSingle(data, v * 16 + 12));
            return result;
        }
        return Array.Empty<Vector4>();
    }

    [Fact]
    public void Uv_running_along_x_gives_a_tangent_along_x()
    {
        var map = Triangle(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1));

        Assert.True(MeshTangentBuilder.AddTangents(map, new[] { 0 }, out var result));

        Assert.Equal(1, result.MeshesChanged);
        Assert.Equal(3, result.VerticesWritten);
        Assert.Equal(0, result.DegenerateVertices);
        foreach (var t in ReadTangents(map, map.Meshes[0]))
        {
            Assert.Equal(1f, t.X, 5);
            Assert.Equal(0f, t.Y, 5);
            Assert.Equal(0f, t.Z, 5);
        }
    }

    /// <summary>Swapping the UV axes mirrors the chart, which is exactly the case w exists to record.</summary>
    [Fact]
    public void A_mirrored_uv_chart_flips_the_handedness()
    {
        var normal = Triangle(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1));
        var mirrored = Triangle(new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 0));

        MeshTangentBuilder.AddTangents(normal, new[] { 0 }, out _);
        MeshTangentBuilder.AddTangents(mirrored, new[] { 0 }, out _);

        float a = ReadTangents(normal, normal.Meshes[0])[0].W;
        float b = ReadTangents(mirrored, mirrored.Meshes[0])[0].W;
        Assert.Equal(1f, Math.Abs(a));
        Assert.Equal(1f, Math.Abs(b));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Tangents_are_unit_length_and_orthogonal_to_the_normal()
    {
        var map = Triangle(new Vector2(0.3f, 0.1f), new Vector2(0.9f, 0.4f), new Vector2(0.2f, 0.8f));

        MeshTangentBuilder.AddTangents(map, new[] { 0 }, out _);

        foreach (var t in ReadTangents(map, map.Meshes[0]))
        {
            var xyz = new Vector3(t.X, t.Y, t.Z);
            Assert.Equal(1f, xyz.Length(), 4);
            Assert.Equal(0f, Vector3.Dot(xyz, new Vector3(0, 0, 1)), 4);
        }
    }

    /// <summary>A zero-area UV chart has no defined tangent. It must still produce a usable orthogonal
    /// vector rather than NaN, and it must be COUNTED — a silent fallback would hide bad source data.</summary>
    [Fact]
    public void Degenerate_uvs_report_rather_than_emit_nan()
    {
        var map = Triangle(Vector2.Zero, Vector2.Zero, Vector2.Zero);

        Assert.True(MeshTangentBuilder.AddTangents(map, new[] { 0 }, out var result));

        Assert.Equal(3, result.DegenerateVertices);
        Assert.Contains("degenerate", result.Summary);
        foreach (var t in ReadTangents(map, map.Meshes[0]))
        {
            var xyz = new Vector3(t.X, t.Y, t.Z);
            Assert.False(float.IsNaN(xyz.X) || float.IsNaN(xyz.Y) || float.IsNaN(xyz.Z));
            Assert.Equal(1f, xyz.Length(), 4);
            Assert.Equal(0f, Vector3.Dot(xyz, new Vector3(0, 0, 1)), 4);
        }
    }

    [Fact]
    public void Running_it_twice_does_not_add_a_second_channel()
    {
        var map = Triangle(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1));
        MeshTangentBuilder.AddTangents(map, new[] { 0 }, out _);

        Assert.False(MeshTangentBuilder.AddTangents(map, new[] { 0 }, out var second));

        Assert.Equal(0, second.MeshesChanged);
        Assert.Contains(second.Skipped, s => s.Contains("already has Texcoord6"));
        Assert.Equal(2, map.Meshes[0].VertexBufferIds.Count);
    }

    [Fact]
    public void A_mesh_without_normals_is_reported_not_faked()
    {
        var map = Triangle(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1));
        map.Declarations[0].Elements.RemoveAll(e => e.Name == MapGeoBinary.ElemNormal);

        Assert.False(MeshTangentBuilder.AddTangents(map, new[] { 0 }, out var result));

        Assert.Equal(0, result.MeshesChanged);
        Assert.Contains(result.Skipped, s => s.Contains("Position, Normal and Texcoord0"));
    }

    [Fact]
    public void The_rewritten_mapgeo_reopens_with_the_channel_intact()
    {
        byte[] original = Triangle(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)).Write();

        byte[] rewritten = MeshTangentBuilder.AddTangents(original, new[] { 0 }, out var result);

        Assert.Equal(1, result.MeshesChanged);
        var reopened = MapGeoBinary.Read(rewritten);
        Assert.True(reopened.MeshHasElement(reopened.Meshes[0], MapGeoBinary.ElemTexcoord6));
        Assert.Equal(1f, ReadTangents(reopened, reopened.Meshes[0])[0].X, 5);
    }

    /// <summary>Texcoord7 and Texcoord6 have to coexist — Mantis needs BOTH, so adding one must not
    /// disturb the other's declaration run.</summary>
    [Fact]
    public void Tangents_and_texcoord7_coexist_on_the_same_mesh()
    {
        var map = Triangle(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1));

        MeshUvChannelBuilder.AddTexcoord7(map, new[] { 0 }, out var uvResult);
        MeshTangentBuilder.AddTangents(map, new[] { 0 }, out var tanResult);

        Assert.Equal(1, uvResult.MeshesChanged);
        Assert.Equal(1, tanResult.MeshesChanged);
        var mesh = map.Meshes[0];
        Assert.Equal(3, mesh.VertexBufferIds.Count);                  // original + uv7 + tangents
        Assert.True(map.MeshHasLightmapUv(mesh));
        Assert.True(map.MeshHasElement(mesh, MapGeoBinary.ElemTexcoord6));
        Assert.True(map.MeshHasElement(mesh, MapGeoBinary.ElemPosition));
        Assert.Equal(1f, ReadTangents(map, mesh)[0].X, 5);
    }
}
