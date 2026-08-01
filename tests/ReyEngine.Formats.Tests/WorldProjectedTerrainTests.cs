using System.Numerics;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

/// <summary>M321: 4TextureBlend_WorldProjected receives its blend map from the engine, so its material has
/// four colour layers and deliberately has no Mask_Texture sampler.</summary>
public class WorldProjectedTerrainTests
{
    private static TextureSlot Slot(string name) => new(name, new BinTreeString(0, $"assets/{name}.tex"));
    private static MaterialParameter Param(string name, float x, float y = 0f) =>
        new(name, new BinTreeVector4(0, new Vector4(x, y, 0f, 0f)));

    private static MaterialBinding Binding(string shader = "Shaders/StaticMesh/4TextureBlend_WorldProjected") =>
        new("Ground_Blend", shader, Array.Empty<string>(), false,
            new List<TextureSlot>
            {
                Slot("Bottom_Texture"), Slot("Middle_Texture"), Slot("Top_Texture"), Slot("Extras_Texture"),
            },
            new MaterialParameter[]
            {
                Param("WS_Multiplier", 0.01f), Param("Bottom_Tiling", 0.1f, 0.1f),
                Param("Mid_Tiling", 0.08f, 0.08f), Param("Top_Tiling", 0.2f, 0.2f),
                Param("Red_Blend_Power", 4f), Param("Green_Blend_Power", 4f),
                Param("Blue_Blend_Power", 4f), Param("OV_Low", 0.23f), Param("OV_High", 0.565f),
            })
        {
            Switches = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["USE_TOP"] = true,
                ["USE_EXTRAS"] = false,
                ["USE_A_AS_OVERLAY"] = true,
            },
        };

    [Fact]
    public void Four_layer_world_projected_shader_uses_the_engine_owned_terrain_mask()
    {
        var profile = MaterialProfiles.Classify(Binding(), MaterialSourceKind.MapMaterials);

        Assert.True(profile.IsTerrainBlend);
        Assert.True(profile.TerrainWorldProjectedMask);
        Assert.Null(profile.TerrainMaskPath);
        Assert.Equal(new Vector3(4f), profile.TerrainBlendPowers);
        Assert.True(profile.TerrainUseTop);
        Assert.False(profile.TerrainUseExtras);
        Assert.True(profile.TerrainUseAlphaOverlay);
        Assert.Equal(new Vector2(0.23f, 0.565f), profile.TerrainOverlayRange);
        Assert.Equal(MaterialRenderMode.Opaque, profile.RenderMode);
    }

    [Fact]
    public void Four_arbitrary_layers_without_a_mask_are_not_misclassified_as_terrain()
    {
        var profile = MaterialProfiles.Classify(Binding("Shaders/StaticMesh/Unrelated"), MaterialSourceKind.MapMaterials);
        Assert.False(profile.IsTerrainBlend);
    }

    [Fact]
    public void Terrain_paint_path_is_derived_from_the_active_mapgeo()
    {
        Assert.Equal("assets/maps/terrainpaint/maps/mapgeometry/map21/base_array_1_of_1.tex",
            MapGeoMaterialResolver.TerrainBlendTexturePathFor("data/maps/mapgeometry/map21/base.mapgeo"));
    }
}
