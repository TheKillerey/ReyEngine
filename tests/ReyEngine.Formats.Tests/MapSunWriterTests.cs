using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M450: the write half of MapSunProperties. The shape follows MapBakeProperties.Write — the proven
/// writer for a sibling component in the same MapContainer.components[] container — so what these tests
/// pin is the part that is new: the update-vs-add rule and the canonical field hash.
/// </summary>
public class MapSunWriterTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private static uint Raw(string s) => HashAlgorithms.Fnv1aRaw(s);

    private static byte[] BinWith(params BinTreeProperty[] sunFields)
    {
        var sun = new BinTreeStruct(0, H("MapSunProperties"), sunFields);
        return ContainerBin(sun);
    }

    private static byte[] ContainerBin(params BinTreeProperty[] components)
    {
        var comps = new BinTreeContainer(H("components"), BinPropertyType.Struct, components);
        var obj = new BinTreeObject(1u, H("MapContainer"), new BinTreeProperty[] { comps });
        var ms = new MemoryStream();
        new BinTree(new[] { obj }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static readonly MapSunProperties Authored = new()
    {
        SunColor = new Vector4(0.46f, 0.55f, 0.76f, 1f),
        SunIntensityScale = 0.5f,     // M451: the split strength field jade authors
        SunDirection = new Vector3(-0.156f, 0.936f, 0.317f),
        SkyLightColor = new Vector4(0.78f, 0.52f, 0.94f, 1f),
        SkyLightScale = 1.5f,
        LightMapColorScale = 2.5f,
        HorizonColor = new Vector4(0.35f, 0.55f, 0.91f, 1f),
        GroundColor = new Vector4(0.1f, 0.1f, 0.1f, 1f),
        FogColor = new Vector4(0.2f, 0.3f, 0.4f, 1f),
        FogStartAndEnd = new Vector2(-10000f, -50000f),
    };

    [Fact]
    public void Written_values_extract_back_exactly()
    {
        byte[] bin = BinWith(new BinTreeVector4(H("sunColor"), Vector4.One));

        byte[]? outBin = MapSunProperties.Write(bin, Authored, out var result);

        Assert.NotNull(outBin);
        Assert.True(result.Written);
        Assert.False(result.CreatedComponent);
        Assert.Equal(Authored, MapSunProperties.Extract(outBin!));
    }

    /// <summary>Fields the record does not model must survive untouched — SunIntensityScale,
    /// fogAlternateColor and friends are real authored data in shipped maps.</summary>
    [Fact]
    public void Unmodeled_fields_are_preserved()
    {
        byte[] bin = BinWith(
            new BinTreeVector4(H("sunColor"), Vector4.One),
            new BinTreeF32(H("sunIntensityScale"), 0.5f),
            new BinTreeVector4(H("fogAlternateColor"), new Vector4(1f, 0f, 1f, 1f)));

        byte[]? outBin = MapSunProperties.Write(bin, Authored, out _);

        var tree = new BinTree(new MemoryStream(outBin!, false));
        var sun = tree.Objects.Values.SelectMany(o => o.Properties.Values)
            .OfType<BinTreeContainer>().SelectMany(c => c.Elements)
            .OfType<BinTreeStruct>().Single(s => s.ClassHash == H("MapSunProperties"));

        Assert.Equal(0.5f, Assert.IsType<BinTreeF32>(sun.Properties[H("sunIntensityScale")]).Value);
        Assert.Equal(new Vector4(1f, 0f, 1f, 1f),
            Assert.IsType<BinTreeVector4>(sun.Properties[H("fogAlternateColor")]).Value);
    }

    /// <summary>A bin with no sun component (mod bins) gets one created — the same append-by-class-hash
    /// pattern MapBakeProperties ships with.</summary>
    [Fact]
    public void A_missing_component_is_created()
    {
        byte[] bin = ContainerBin(new BinTreeStruct(0, H("MapBakeProperties"),
            new BinTreeProperty[] { new BinTreeU32(H("lightGridSize"), 256u) }));

        byte[]? outBin = MapSunProperties.Write(bin, Authored, out var result);

        Assert.NotNull(outBin);
        Assert.True(result.CreatedComponent);
        Assert.Equal(Authored, MapSunProperties.Extract(outBin!));
        // and the sibling component is still there
        Assert.NotNull(MapBakeProperties.Read(outBin!));
    }

    /// <summary>The conservative add rule: an absent field whose new value IS the read-side default stays
    /// absent — Extract returns the default either way, and adding it could only mean something different
    /// to the game. An absent field with a real value is added.</summary>
    [Fact]
    public void Absent_fields_are_added_only_when_non_default()
    {
        byte[] bin = BinWith(new BinTreeVector4(H("sunColor"), Vector4.One));
        var sun = new MapSunProperties { LightMapColorScale = 2f };   // everything else at defaults

        byte[]? outBin = MapSunProperties.Write(bin, sun, out var result);

        var tree = new BinTree(new MemoryStream(outBin!, false));
        var s = tree.Objects.Values.SelectMany(o => o.Properties.Values)
            .OfType<BinTreeContainer>().SelectMany(c => c.Elements)
            .OfType<BinTreeStruct>().Single(x => x.ClassHash == H("MapSunProperties"));

        Assert.True(s.Properties.ContainsKey(H("lightMapColorScale")));   // non-default -> added
        Assert.False(s.Properties.ContainsKey(H("fogColor")));            // default -> left absent
        Assert.False(s.Properties.ContainsKey(H("skyLightScale")));
        Assert.True(s.Properties.ContainsKey(H("sunColor")));             // existed -> updated in place
        Assert.Equal(1, result.FieldsUpdated);
        Assert.Equal(1, result.FieldsAdded);
    }

    /// <summary>The game resolves fields by the lowercased-name hash (measured on MapBakeProperties:
    /// Fnv1aRaw does not match), so a raw-hash variant is collapsed into the canonical one instead of
    /// leaving two copies of the same semantic field.</summary>
    [Fact]
    public void A_raw_hash_duplicate_is_collapsed_into_the_canonical_field()
    {
        byte[] bin = BinWith(new BinTreeVector4(Raw("sunColor"), Vector4.One));

        byte[]? outBin = MapSunProperties.Write(bin, Authored, out _);

        var tree = new BinTree(new MemoryStream(outBin!, false));
        var s = tree.Objects.Values.SelectMany(o => o.Properties.Values)
            .OfType<BinTreeContainer>().SelectMany(c => c.Elements)
            .OfType<BinTreeStruct>().Single(x => x.ClassHash == H("MapSunProperties"));

        Assert.False(s.Properties.ContainsKey(Raw("sunColor")));
        Assert.Equal(Authored.SunColor, Assert.IsType<BinTreeVector4>(s.Properties[H("sunColor")]).Value);
    }

    /// <summary>M451: the strength field follows the same rules as every other — round-trips when real,
    /// stays absent at its default of 1 so an unauthored map is not silently annotated.</summary>
    [Fact]
    public void Sun_intensity_scale_round_trips_and_defaults_stay_absent()
    {
        byte[] bin = BinWith(new BinTreeVector4(H("sunColor"), Vector4.One));

        byte[]? strong = MapSunProperties.Write(bin, new MapSunProperties { SunIntensityScale = 0.5f }, out _);
        Assert.Equal(0.5f, MapSunProperties.Extract(strong!)!.SunIntensityScale);

        byte[]? neutral = MapSunProperties.Write(bin, new MapSunProperties(), out _);
        var tree = new BinTree(new MemoryStream(neutral!, false));
        var s = tree.Objects.Values.SelectMany(o => o.Properties.Values)
            .OfType<BinTreeContainer>().SelectMany(c => c.Elements)
            .OfType<BinTreeStruct>().Single(x => x.ClassHash == H("MapSunProperties"));
        Assert.False(s.Properties.ContainsKey(H("SunIntensityScale")));
    }

    [Fact]
    public void An_unwritable_bin_reports_null_rather_than_corrupting()
    {
        Assert.Null(MapSunProperties.Write(new byte[] { 1, 2, 3 }, Authored, out var bad));
        Assert.False(bad.Written);

        // parseable, but nothing to attach to
        var ms = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(1u, H("SomethingElse"),
            Array.Empty<BinTreeProperty>()) }, Array.Empty<string>()).Write(ms);
        Assert.Null(MapSunProperties.Write(ms.ToArray(), Authored, out var none));
        Assert.Contains("MapContainer", none.Detail);
    }
}
