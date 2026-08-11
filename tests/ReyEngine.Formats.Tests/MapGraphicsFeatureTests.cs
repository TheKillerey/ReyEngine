using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>M434: MapGraphicsFeature components in MapContainer.components[]. Measured context: 179 of 206
/// shipped map materials.bin declare MapLightingV2; the 27 that do not are the map11 family plus
/// map21/base. DefaultEnv_Flat_BakedTerrain occurs in 2 bins and both declare it.</summary>
public class MapGraphicsFeatureTests
{
    private static readonly uint ContainerCls = HashAlgorithms.Fnv1a("MapContainer");
    private static readonly uint ComponentsField = HashAlgorithms.Fnv1a("components");
    private static readonly uint SunCls = HashAlgorithms.Fnv1a("MapSunProperties");

    /// <summary>A minimal map bin: one MapContainer whose components[] holds MapSunProperties. Container
    /// elements carry no name hash on the wire, which is why these are built with NameHash 0.</summary>
    private static byte[] MapBin(params BinTreeProperty[] extraComponents)
    {
        var components = new List<BinTreeProperty>
        {
            new BinTreeStruct(0, SunCls, new BinTreeProperty[]
            {
                new BinTreeF32(HashAlgorithms.Fnv1a("lightMapColorScale"), 0.6f),
            }),
        };
        components.AddRange(extraComponents);

        var container = new BinTreeObject(HashAlgorithms.Fnv1a("TestMap"), ContainerCls, new BinTreeProperty[]
        {
            new BinTreeContainer(ComponentsField, BinPropertyType.Struct, components),
        });

        var tree = new BinTree(new[] { container }, Array.Empty<string>());
        var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static IReadOnlyList<BinTreeProperty> ComponentsOf(byte[] bin)
    {
        var tree = new BinTree(new MemoryStream(bin, false));
        var container = tree.Objects.Values.First(o => o.ClassHash == ContainerCls);
        return ((BinTreeContainer)container.Properties[ComponentsField]).Elements.ToList();
    }

    [Fact]
    public void A_bin_without_graphics_features_reports_none()
    {
        Assert.Empty(MapGraphicsFeatures.Read(MapBin()));
    }

    [Fact]
    public void Adding_lighting_v2_appends_a_component_and_keeps_the_others()
    {
        byte[] original = MapBin();

        byte[]? updated = MapGraphicsFeatures.Add(original, MapGraphicsFeatures.LightingV2, null, out var result);

        Assert.NotNull(updated);
        Assert.True(result.Changed);
        var components = ComponentsOf(updated!);
        Assert.Equal(2, components.Count);
        Assert.Contains(components, c => c is BinTreeStruct s && s.ClassHash == SunCls);
        Assert.Contains(components, c => c is BinTreeStruct s && s.ClassHash == MapGraphicsFeatures.LightingV2.Hash);
        Assert.Contains(MapGraphicsFeatures.LightingV2, MapGraphicsFeatures.Read(updated!));
    }

    /// <summary>map12/jade ships MapLightingV2 with zero properties, so the bare marker is the shipped
    /// form rather than a shortcut.</summary>
    [Fact]
    public void The_default_form_is_a_bare_marker_with_no_properties()
    {
        byte[]? updated = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _);

        var added = ComponentsOf(updated!).OfType<BinTreeStruct>()
            .First(s => s.ClassHash == MapGraphicsFeatures.LightingV2.Hash);
        Assert.Empty(added.Properties);
    }

    /// <summary>The map21/ioniabase preset — the closest shipped analogue to a baked-terrain map.</summary>
    [Fact]
    public void The_ionia_preset_writes_the_two_measured_fields()
    {
        byte[]? updated = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2,
            MapGraphicsFeatures.IoniaBaseLightingV2Fields(), out _);

        var added = ComponentsOf(updated!).OfType<BinTreeStruct>()
            .First(s => s.ClassHash == MapGraphicsFeatures.LightingV2.Hash);
        Assert.Equal(2, added.Properties.Count);
        Assert.Equal(0.9f, ((BinTreeF32)added.Properties[HashAlgorithms.Fnv1a("MinimumEnvironmentColorContribution")]).Value);
        Assert.Equal(2000f, ((BinTreeF32)added.Properties[HashAlgorithms.Fnv1a("BounceLightFalloffDistance")]).Value);
    }

    [Fact]
    public void Adding_the_same_feature_twice_is_refused_not_duplicated()
    {
        byte[]? once = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _);

        byte[]? twice = MapGraphicsFeatures.Add(once!, MapGraphicsFeatures.LightingV2, null, out var result);

        Assert.Null(twice);
        Assert.False(result.Changed);
        Assert.Contains("already declared", result.Detail);
        Assert.Single(ComponentsOf(once!).OfType<BinTreeStruct>()
            .Where(s => s.ClassHash == MapGraphicsFeatures.LightingV2.Hash));
    }

    /// <summary>Adding fields to a feature already present updates it in place instead of appending a
    /// second copy.</summary>
    [Fact]
    public void Adding_fields_to_an_existing_feature_updates_it_in_place()
    {
        byte[]? once = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _);

        byte[]? updated = MapGraphicsFeatures.Add(once!, MapGraphicsFeatures.LightingV2,
            MapGraphicsFeatures.IoniaBaseLightingV2Fields(), out var result);

        Assert.NotNull(updated);
        Assert.True(result.Changed);
        var all = ComponentsOf(updated!).OfType<BinTreeStruct>()
            .Where(s => s.ClassHash == MapGraphicsFeatures.LightingV2.Hash).ToList();
        Assert.Single(all);
        Assert.Equal(2, all[0].Properties.Count);
    }

    [Fact]
    public void Removing_a_feature_drops_only_that_component()
    {
        byte[]? withV2 = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _);

        byte[]? removed = MapGraphicsFeatures.Remove(withV2!, MapGraphicsFeatures.LightingV2, out var result);

        Assert.NotNull(removed);
        Assert.True(result.Changed);
        Assert.Empty(MapGraphicsFeatures.Read(removed!));
        Assert.Single(ComponentsOf(removed!));                     // MapSunProperties survives
    }

    /// <summary>M414: Riot ships 0 empty containers in 33,645 materials and writing one crashes the game
    /// at map load. Removing the last component would produce exactly that, so it is refused.</summary>
    [Fact]
    public void Removing_the_last_component_is_refused_rather_than_emptying_the_container()
    {
        var lone = new BinTreeStruct(0, MapGraphicsFeatures.LightingV2.Hash, Array.Empty<BinTreeProperty>());
        var container = new BinTreeObject(HashAlgorithms.Fnv1a("TestMap"), ContainerCls, new BinTreeProperty[]
        {
            new BinTreeContainer(ComponentsField, BinPropertyType.Struct, new BinTreeProperty[] { lone }),
        });
        var ms = new MemoryStream();
        new BinTree(new[] { container }, Array.Empty<string>()).Write(ms);

        byte[]? removed = MapGraphicsFeatures.Remove(ms.ToArray(), MapGraphicsFeatures.LightingV2, out var result);

        Assert.Null(removed);
        Assert.False(result.Changed);
        Assert.Contains("empty", result.Detail);
    }

    [Fact]
    public void Removing_a_feature_that_is_not_there_reports_rather_than_rewriting()
    {
        byte[]? removed = MapGraphicsFeatures.Remove(MapBin(), MapGraphicsFeatures.Ssao, out var result);

        Assert.Null(removed);
        Assert.False(result.Changed);
        Assert.Contains("not declared", result.Detail);
    }

    [Fact]
    public void A_bin_with_no_map_container_is_reported_rather_than_corrupted()
    {
        var lonely = new BinTreeObject(1u, HashAlgorithms.Fnv1a("StaticMaterialDef"), Array.Empty<BinTreeProperty>());
        var ms = new MemoryStream();
        new BinTree(new[] { lonely }, Array.Empty<string>()).Write(ms);

        byte[]? updated = MapGraphicsFeatures.Add(ms.ToArray(), MapGraphicsFeatures.LightingV2, null, out var result);

        Assert.Null(updated);
        Assert.False(result.Changed);
        Assert.Contains("no MapContainer", result.Detail);
    }

    /// <summary>Components are written as Struct (0x82), which is what every shipped MapContainer uses —
    /// NOT the Embedded (0x83) form that material containers require (M416).</summary>
    [Fact]
    public void Components_are_written_as_struct_not_embedded()
    {
        byte[]? updated = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _);

        var added = ComponentsOf(updated!).First(c =>
            c is BinTreeStruct s && s.ClassHash == MapGraphicsFeatures.LightingV2.Hash);
        Assert.IsNotType<BinTreeEmbedded>(added);
        Assert.IsType<BinTreeStruct>(added);
    }

    [Fact]
    public void The_family_covers_the_shader_bound_shared_resources()
    {
        var names = MapGraphicsFeatures.All.Select(f => f.Name).ToList();
        Assert.Contains("MapLightingV2", names);       // IBL_CUBEMAP
        Assert.Contains("MapTerrainPaint", names);     // TERRAIN_BLEND
        Assert.Contains("MapSSAO", names);             // SSAO_TEXTURE
        Assert.Contains("MapDynamicLighting", names);  // DYNAMIC_ENV_LIGHT_*
        Assert.Contains("MapGameplayTexture", names);  // GAMEPLAY_TEXTURE
        Assert.Contains("MapLightRegions", names);     // LightRegionInfo
        Assert.All(MapGraphicsFeatures.All, f => Assert.NotEqual(0u, f.Hash));
    }
}
