using System.Collections;
using System.Numerics;
using System.Reflection;
using ReyEngine.App.Services;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Tests;

/// <summary>M319: Map21/IoniaBase stores BakedTerrain colour outside the material: the material points at
/// black.tex while each mapgeo mesh overrides BAKED_DIFFUSE_TEXTURE and supplies its own atlas transform.</summary>
public class BakedTerrainBindingTests
{
    [Fact]
    public void Dx11_uses_the_reflected_baked_diffuse_target_and_xyzw_uv_layout()
    {
        var ps = new DxbcShader
        {
            Bytecode = Array.Empty<byte>(),
            Resources = new[]
            {
                new DxbcResource("BAKED_LIGHT__TX", DxbcResourceKind.Texture, 1, 1, 4, 0),
                new DxbcResource("BAKED_DIFFUSE_TEXTURE__TX", DxbcResourceKind.Texture, 2, 1, 4, 0),
            },
        };

        Assert.Equal("BAKED_DIFFUSE_TEXTURE__TX", Dx11SceneBuilder.BakedPaintTextureTarget(ps));
        Assert.Equal(new[] { 0.5f, 0.75f, 0.125f, 0.25f },
            Dx11SceneBuilder.BakedPaintUvScaleBias(new Vector2(0.5f, 0.75f), new Vector2(0.125f, 0.25f)));
    }

    [Fact]
    public void Dx11_does_not_merge_adjacent_meshes_with_different_baked_atlases()
    {
        static MapGeoGroup Group(int start, string texture) => new("mat", start, 3)
        {
            BakedPaintTexture = texture,
            BakedPaintScale = new Vector2(0.5f),
            BakedPaintBias = new Vector2(0.001f),
        };

        var map = new MapGeoAsset
        {
            Positions = Array.Empty<float>(), Normals = Array.Empty<float>(), Uvs = Array.Empty<float>(),
            Indices = new uint[6], Groups = new[] { Group(0, "atlas/a.tex"), Group(3, "atlas/b.tex") },
        };

        var merge = typeof(Dx11SceneBuilder).GetMethod("MergeSlices", BindingFlags.NonPublic | BindingFlags.Static);
        var result = Assert.IsAssignableFrom<ICollection>(merge!.Invoke(null, new object[] { map }));
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Dx11_ignores_unused_baked_uv_values_when_no_texture_override_exists()
    {
        var map = new MapGeoAsset
        {
            Positions = Array.Empty<float>(), Normals = Array.Empty<float>(), Uvs = Array.Empty<float>(),
            Indices = new uint[6],
            Groups = new[]
            {
                new MapGeoGroup("mat", 0, 3) { BakedPaintScale = new Vector2(0.25f) },
                new MapGeoGroup("mat", 3, 3) { BakedPaintScale = new Vector2(0.75f) },
            },
        };

        var merge = typeof(Dx11SceneBuilder).GetMethod("MergeSlices", BindingFlags.NonPublic | BindingFlags.Static);
        var result = Assert.IsAssignableFrom<ICollection>(merge!.Invoke(null, new object[] { map }));
        Assert.Single(result.Cast<object>());
    }

    [Fact]
    public void Dx11_does_not_merge_adjacent_meshes_with_different_lightmap_transforms()
    {
        static MapGeoGroup Group(int start, Vector2 scale) => new("mat", start, 3)
        {
            LightmapTexture = "atlas/light.tex",
            LightmapScale = scale,
            LightmapBias = new Vector2(0.001f),
        };

        var map = new MapGeoAsset
        {
            Positions = Array.Empty<float>(), Normals = Array.Empty<float>(), Uvs = Array.Empty<float>(),
            Indices = new uint[6], Groups = new[] { Group(0, new Vector2(0.5f)), Group(3, new Vector2(0.25f)) },
        };

        var merge = typeof(Dx11SceneBuilder).GetMethod("MergeSlices", BindingFlags.NonPublic | BindingFlags.Static);
        var result = Assert.IsAssignableFrom<ICollection>(merge!.Invoke(null, new object[] { map }));
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Dx11_ignores_unused_lightmap_transform_when_no_lightmap_exists()
    {
        var map = new MapGeoAsset
        {
            Positions = Array.Empty<float>(), Normals = Array.Empty<float>(), Uvs = Array.Empty<float>(),
            Indices = new uint[6],
            Groups = new[]
            {
                new MapGeoGroup("mat", 0, 3) { LightmapScale = new Vector2(0.25f) },
                new MapGeoGroup("mat", 3, 3) { LightmapScale = new Vector2(0.75f) },
            },
        };

        var merge = typeof(Dx11SceneBuilder).GetMethod("MergeSlices", BindingFlags.NonPublic | BindingFlags.Static);
        var result = Assert.IsAssignableFrom<ICollection>(merge!.Invoke(null, new object[] { map }));
        Assert.Single(result.Cast<object>());
    }
}
