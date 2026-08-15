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
        // M467. All four deliberately NON-default, or the reflection guard below would compare a default
        // against a default and pass without the writer ever touching them. Values are Riot's own: 75 is
        // what 151 of the 176 authoring maps use, 0.4 and 0.02 both appear in the shipped spread.
        SunRadiusForShadows = 75f,
        ScaleSunShadowIntensity = 0.4f,
        ShadowBias = 0.002f,
        SurfaceAreaToShadowMapScale = 0.02f,
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

    /// <summary>M463: the five fields the Lighting panel gained controls for. Each is round-tripped ON ITS
    /// OWN, from a bin that authors none of them, so a writer that dropped exactly one would fail here
    /// rather than hide behind the four that still worked — which is what the all-fields-at-once test
    /// above cannot distinguish.</summary>
    [Theory]
    [MemberData(nameof(NewlyExposedFields))]
    public void Each_newly_exposed_field_round_trips_on_its_own(string name, MapSunProperties sun)
    {
        byte[] bin = BinWith(new BinTreeVector4(H("sunColor"), Vector4.One));

        byte[]? outBin = MapSunProperties.Write(bin, sun, out var result);

        Assert.True(result.Written, name);
        Assert.Equal(sun, MapSunProperties.Extract(outBin!));
    }

    public static TheoryData<string, MapSunProperties> NewlyExposedFields() => new()
    {
        // Non-unit on purpose: Riot ships sunDirection with lengths up to 8.775 (Map22
        // base_dragon_cloud is <2, 8, -3>), and the panel must not normalise it on the way to the bin.
        { "sunDirection", new MapSunProperties { SunDirection = new Vector3(2f, 8f, -3f) } },
        { "horizonColor", new MapSunProperties { HorizonColor = new Vector4(0.35f, 0.55f, 0.91f, 1f) } },
        { "groundColor", new MapSunProperties { GroundColor = new Vector4(0.1f, 0.12f, 0.14f, 1f) } },
        { "fogColor", new MapSunProperties { FogColor = new Vector4(0.2f, 0.3f, 0.4f, 1f) } },
        // RAW, in Riot's negative reversed convention - the panel edits these unmodified.
        { "fogStartAndEnd", new MapSunProperties { FogStartAndEnd = new Vector2(-10000f, -50000f) } },
    };

    /// <summary>A guard against the defect M463 fixed, restated so it cannot come back: the record carried
    /// ten fields, the writer persisted ten, and the panel edited four — so six could only ever be saved
    /// back exactly as loaded. This walks the record by REFLECTION, so a field added to
    /// <see cref="MapSunProperties"/> later fails here until the writer round-trips it too.</summary>
    [Fact]
    public void Every_field_the_record_declares_survives_a_write()
    {
        byte[] bin = BinWith(new BinTreeVector4(H("sunColor"), Vector4.One));

        byte[]? outBin = MapSunProperties.Write(bin, Authored, out var result);
        var back = MapSunProperties.Extract(outBin!);

        Assert.True(result.Written);
        Assert.NotNull(back);
        // Public instance properties only. A sealed record's compiler-generated EqualityContract is
        // private, so it never shows up here.
        var declared = typeof(MapSunProperties).GetProperties()
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToList();
        // Fourteen today: M463's ten plus M467's four shadow fields. A new field must be added to Extract
        // AND Write, not just to the record.
        Assert.Equal(14, declared.Count);
        foreach (var p in declared)
            Assert.Equal(p.GetValue(Authored), p.GetValue(back));
    }

    /// <summary>M467: the four shadow fields land under the hashes the GAME resolves. This is the failure
    /// that has no symptom — a wrong field hash writes a property the client silently skips, the bin still
    /// validates, the map still loads, and nothing in the viewport differs. The expected values are Riot's
    /// published hashes from the shipped binfields list, not recomputed from the same function under
    /// test.</summary>
    [Theory]
    [InlineData("SunRadiusForShadows", 0xd8851203u)]
    [InlineData("ScaleSunShadowIntensity", 0xba02f116u)]
    [InlineData("ShadowBias", 0xd14e6310u)]
    [InlineData("surfaceAreaToShadowMapScale", 0x09c4fe2cu)]
    public void Shadow_fields_use_Riots_published_hashes(string field, uint published)
    {
        Assert.Equal(published, H(field));

        byte[] outBin = MapSunProperties.Write(BinWith(new BinTreeVector4(H("sunColor"), Vector4.One)),
                                               Authored, out _)!;
        var tree = new BinTree(new MemoryStream(outBin, false));
        var sun = tree.Objects.Values.SelectMany(o => o.Properties.Values)
            .OfType<BinTreeContainer>().SelectMany(c => c.Elements)
            .OfType<BinTreeStruct>().Single(s => s.ClassHash == H("MapSunProperties"));

        Assert.True(sun.Properties.ContainsKey(published), $"{field} was not written under 0x{published:x8}");
        Assert.IsType<BinTreeF32>(sun.Properties[published]);
    }

    /// <summary>Summoner's Rift authors none of the four, so it runs SunRadiusForShadows at 0 — that is the
    /// state a user starts from. Setting only that one field must add exactly it and leave the other three
    /// absent, so a map that gains sun shadows does not silently also gain a bias no shipped map uses.</summary>
    [Fact]
    public void Enabling_only_the_coverage_radius_adds_only_that_field()
    {
        byte[] bin = BinWith(new BinTreeVector4(H("sunColor"), Vector4.One));
        var sun = new MapSunProperties { SunRadiusForShadows = 75f };   // the Riot-dominant value

        byte[]? outBin = MapSunProperties.Write(bin, sun, out var result);

        var tree = new BinTree(new MemoryStream(outBin!, false));
        var s = tree.Objects.Values.SelectMany(o => o.Properties.Values)
            .OfType<BinTreeContainer>().SelectMany(c => c.Elements)
            .OfType<BinTreeStruct>().Single(x => x.ClassHash == H("MapSunProperties"));

        Assert.True(s.Properties.ContainsKey(H("SunRadiusForShadows")));
        Assert.False(s.Properties.ContainsKey(H("ScaleSunShadowIntensity")));
        Assert.False(s.Properties.ContainsKey(H("ShadowBias")));
        Assert.False(s.Properties.ContainsKey(H("surfaceAreaToShadowMapScale")));
        Assert.Equal(1, result.FieldsAdded);
        Assert.Equal(75f, MapSunProperties.Extract(outBin!)!.SunRadiusForShadows);
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
