using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>M432: adding Texcoord7 to a selected mesh without baking a lightmap. The channel is a vertex
/// contract some shaders (Mantis_Env_Baked_PBR, FEATURE_BAKED_PAINT) require independently of any
/// atlas.</summary>
public class MeshUvChannelTests
{
    /// <summary>One mesh, one buffer: Position(XYZ) + Texcoord0(XY), three vertices.</summary>
    private static MapGeoBinary OneMeshMap(params Vector2[] uv0)
    {
        var data = new byte[uv0.Length * 20];
        for (int i = 0; i < uv0.Length; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * 20 + 0), (float)i);      // position x
            BitConverter.TryWriteBytes(data.AsSpan(i * 20 + 12), uv0[i].X);
            BitConverter.TryWriteBytes(data.AsSpan(i * 20 + 16), uv0[i].Y);
        }

        var map = new MapGeoBinary { Version = 18 };
        map.Declarations.Add(new MapGeoBinary.VertexDeclaration
        {
            Elements = { (MapGeoBinary.ElemPosition, MapGeoBinary.FmtXYZ_Float32),
                         (MapGeoBinary.ElemTexcoord0, MapGeoBinary.FmtXY_Float32) },
            Padding = new byte[8 * 13],
        });
        map.VertexBuffers.Add(new MapGeoBinary.VertexBuffer { Data = data, HasVisibility = true, Visibility = 0xFF });
        map.IndexBuffers.Add(new MapGeoBinary.IndexBuffer
        { Data = new byte[uv0.Length * 2], HasVisibility = true, Visibility = 0xFF });
        map.Meshes.Add(V18Mesh(uv0.Length));
        // v18 tail = scene graphs + planar reflectors; empty counts are the smallest valid form.
        map.Tail = new byte[8];
        return map;
    }

    /// <summary>The writer emits optional fields from the Has* flags, so a v18 fixture has to carry the
    /// ones a v18 reader will look for — otherwise it writes a stream that no longer reparses.</summary>
    private static MapGeoBinary.Mesh V18Mesh(int vertexCount) => new()
    {
        VertexCount = vertexCount, VertexDeclarationBase = 0, VertexBufferIds = { 0 },
        IndexCount = vertexCount, IndexBufferId = 0, Transform = Matrix4x4.Identity,
        HasVisibility = true, Visibility = 0xFF,
        HasRegionHash = true, HasVcHash = true, HasDisableBackface = true,
        HasLayerTransition = true, RenderFlagsIsUshort = true,
    };

    /// <summary>Read back the Texcoord7 values the pass wrote.</summary>
    private static Vector2[] ReadUv7(MapGeoBinary map, MapGeoBinary.Mesh mesh)
    {
        for (int i = 0; i < mesh.VertexBufferIds.Count; i++)
        {
            if (!map.Declarations[mesh.VertexDeclarationBase + i].Has(MapGeoBinary.LightmapUvElement)) continue;
            var data = map.VertexBuffers[mesh.VertexBufferIds[i]].Data;
            var result = new Vector2[mesh.VertexCount];
            for (int v = 0; v < mesh.VertexCount; v++)
                result[v] = new Vector2(BitConverter.ToSingle(data, v * 8), BitConverter.ToSingle(data, v * 8 + 4));
            return result;
        }
        return Array.Empty<Vector2>();
    }

    [Fact]
    public void Selected_mesh_gains_texcoord7_copied_from_texcoord0()
    {
        var uv0 = new[] { new Vector2(0.25f, 0.5f), new Vector2(1f, 0f), new Vector2(0.75f, 0.125f) };
        var map = OneMeshMap(uv0);

        Assert.True(MeshUvChannelBuilder.AddTexcoord7(map, new[] { 0 }, out var result));

        Assert.Equal(1, result.MeshesChanged);
        Assert.Equal(3, result.VerticesWritten);
        Assert.Empty(result.Skipped);
        Assert.True(map.MeshHasLightmapUv(map.Meshes[0]));
        Assert.Equal(uv0, ReadUv7(map, map.Meshes[0]));
    }

    [Fact]
    public void The_original_streams_are_left_alone()
    {
        var map = OneMeshMap(new Vector2(0.25f, 0.5f), new Vector2(1f, 0f), new Vector2(0.75f, 0.125f));
        var before = (byte[])map.VertexBuffers[0].Data.Clone();

        MeshUvChannelBuilder.AddTexcoord7(map, new[] { 0 }, out _);

        var mesh = map.Meshes[0];
        Assert.Equal(2, mesh.VertexBufferIds.Count);                     // original + the new uv7 buffer
        Assert.Equal(before, map.VertexBuffers[mesh.VertexBufferIds[0]].Data);
        Assert.True(map.Declarations[mesh.VertexDeclarationBase].Has(MapGeoBinary.ElemPosition));
        Assert.True(map.Declarations[mesh.VertexDeclarationBase].Has(MapGeoBinary.ElemTexcoord0));
    }

    /// <summary>The whole point of the UV-only variant: no lightmap texture is invented. Pointing
    /// BakedLight at an atlas that doesn't exist would trade one missing resource for another.</summary>
    [Fact]
    public void No_lightmap_texture_reference_is_written()
    {
        var map = OneMeshMap(new Vector2(0.25f, 0.5f), Vector2.Zero, Vector2.One);

        MeshUvChannelBuilder.AddTexcoord7(map, new[] { 0 }, out _);

        Assert.Equal("", map.Meshes[0].BakedLight.Texture);
        Assert.Equal(Vector2.Zero, map.Meshes[0].BakedLight.Scale);
        Assert.Equal(Vector2.Zero, map.Meshes[0].BakedLight.Bias);
    }

    [Fact]
    public void Unselected_meshes_are_untouched()
    {
        var map = OneMeshMap(new Vector2(0.25f, 0.5f), Vector2.Zero, Vector2.One);
        map.Meshes.Add(V18Mesh(3));

        MeshUvChannelBuilder.AddTexcoord7(map, new[] { 1 }, out var result);

        Assert.Equal(1, result.MeshesChanged);
        Assert.False(map.MeshHasLightmapUv(map.Meshes[0]));
        Assert.True(map.MeshHasLightmapUv(map.Meshes[1]));
    }

    [Fact]
    public void An_empty_selection_means_every_mesh()
    {
        var map = OneMeshMap(new Vector2(0.25f, 0.5f), Vector2.Zero, Vector2.One);
        map.Meshes.Add(V18Mesh(3));

        MeshUvChannelBuilder.AddTexcoord7(map, Array.Empty<int>(), out var result);

        Assert.Equal(2, result.MeshesChanged);
        Assert.All(map.Meshes, m => Assert.True(map.MeshHasLightmapUv(m)));
    }

    /// <summary>Running it twice must not append a second channel — the underlying primitive throws on a
    /// mesh that already has one, so the builder has to filter before it calls.</summary>
    [Fact]
    public void A_mesh_that_already_has_the_channel_is_skipped_not_duplicated()
    {
        var map = OneMeshMap(new Vector2(0.25f, 0.5f), Vector2.Zero, Vector2.One);
        MeshUvChannelBuilder.AddTexcoord7(map, new[] { 0 }, out _);

        Assert.False(MeshUvChannelBuilder.AddTexcoord7(map, new[] { 0 }, out var second));

        Assert.Equal(0, second.MeshesChanged);
        Assert.Equal(2, map.Meshes[0].VertexBufferIds.Count);
        Assert.Contains(second.Skipped, s => s.Contains("already has Texcoord7"));
    }

    [Fact]
    public void A_mesh_without_texcoord0_is_reported_not_faked()
    {
        var map = OneMeshMap(Vector2.Zero, Vector2.Zero, Vector2.Zero);
        map.Declarations[0].Elements.RemoveAll(e => e.Name == MapGeoBinary.ElemTexcoord0);

        Assert.False(MeshUvChannelBuilder.AddTexcoord7(map, new[] { 0 }, out var result));

        Assert.Equal(0, result.MeshesChanged);
        Assert.False(map.MeshHasLightmapUv(map.Meshes[0]));
        Assert.Contains(result.Skipped, s => s.Contains("no readable Texcoord0"));
    }

    [Fact]
    public void The_rewritten_mapgeo_reopens_with_the_channel_intact()
    {
        var uv0 = new[] { new Vector2(0.25f, 0.5f), new Vector2(1f, 0f), new Vector2(0.75f, 0.125f) };
        byte[] original = OneMeshMap(uv0).Write();

        byte[] rewritten = MeshUvChannelBuilder.AddTexcoord7(original, new[] { 0 }, out var result);

        Assert.Equal(1, result.MeshesChanged);
        Assert.NotEqual(original.Length, rewritten.Length);
        var reopened = MapGeoBinary.Read(rewritten);
        Assert.True(reopened.MeshHasLightmapUv(reopened.Meshes[0]));
        Assert.Equal(uv0, ReadUv7(reopened, reopened.Meshes[0]));
    }

    /// <summary>The decoder is what the viewport and the bake-readiness checks consult, so the added
    /// channel has to be visible THERE, not just in the byte layer.</summary>
    [Fact]
    public void The_decoder_reports_the_new_channel()
    {
        byte[] original = OneMeshMap(new Vector2(0.25f, 0.5f), Vector2.Zero, Vector2.One).Write();
        Assert.False(MapGeoDecoder.Decode(original).Meshes[0].HasLightmapUv);

        byte[] rewritten = MeshUvChannelBuilder.AddTexcoord7(original, new[] { 0 }, out _);

        Assert.True(MapGeoDecoder.Decode(rewritten).Meshes[0].HasLightmapUv);
    }

    [Fact]
    public void Nothing_to_do_returns_the_input_unchanged()
    {
        byte[] original = OneMeshMap(Vector2.Zero, Vector2.Zero, Vector2.Zero).Write();

        byte[] same = MeshUvChannelBuilder.AddTexcoord7(original, new[] { 99 }, out var result);

        Assert.Same(original, same);
        Assert.Equal(0, result.MeshesChanged);
    }

    /// <summary>M441: formats 8-12 fell through to 0, silently corrupting VertexDeclaration.Stride for
    /// 454 of 959 declarations across the shipped corpus. Values are LeagueToolkit's own
    /// VertexElement.GetFormatSize, printed for all 13 formats rather than derived from the names -
    /// XYZ_Packed161616 is 8 bytes, not the 6 the name suggests, because it is padded.</summary>
    [Theory]
    [InlineData(0u, 4)]   [InlineData(1u, 8)]   [InlineData(2u, 12)]  [InlineData(3u, 16)]
    [InlineData(4u, 4)]   [InlineData(5u, 4)]   [InlineData(6u, 4)]   [InlineData(7u, 4)]
    [InlineData(8u, 8)]   [InlineData(9u, 8)]   [InlineData(10u, 2)]  [InlineData(11u, 3)]
    [InlineData(12u, 4)]
    public void Every_element_format_has_the_size_leaguetoolkit_reports(uint format, int expected)
    {
        Assert.Equal(expected, MapGeoBinary.FormatSize(format));
    }

    [Fact]
    public void No_known_element_format_reports_zero_size()
    {
        for (uint f = 0; f <= 12; f++)
            Assert.True(MapGeoBinary.FormatSize(f) > 0, $"format {f} reports 0 bytes, which corrupts stride");
    }

    /// <summary>A packed declaration's stride must be the sum of real sizes. Riot's most common packed
    /// layout is Position XYZ_Float32 + Normal XYZ_Packed161616 + Texcoord0 XY_Packed1616.</summary>
    [Fact]
    public void A_packed_declaration_computes_a_real_stride()
    {
        var decl = new MapGeoBinary.VertexDeclaration
        {
            Elements =
            {
                (MapGeoBinary.ElemPosition, MapGeoBinary.FmtXYZ_Float32),        // 12
                (MapGeoBinary.ElemNormal, MapGeoBinary.FmtXYZ_Packed161616),     //  8
                (MapGeoBinary.ElemTexcoord0, MapGeoBinary.FmtXY_Packed1616),     //  4
            },
        };

        Assert.Equal(24, decl.Stride);
    }
}
