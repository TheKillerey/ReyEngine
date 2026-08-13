using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Lighting;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M446: MapDynamicPointLight is the only point-light class that ships with a POSITION — M196 measured 0
/// MapPointLight placements across 50,107 bins against 791 surviving type definitions. So it is the bridge
/// every light-consuming system has to cross, and the wire shape below is copied from Riot's only two
/// instances rather than derived from the schema.
/// </summary>
public class DynamicPointLightTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>A bin with a MapPlaceableContainer whose <c>items</c> is a MAP (0x86) — the measured shape,
    /// not a list.</summary>
    private static byte[] BinWith(params BinTreeProperty[] items)
    {
        var map = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct,
            items.Select((p, i) => new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                new BinTreeHash(0, 0x1000u + (uint)i), p)));
        var obj = new BinTreeObject(1u, H("MapPlaceableContainer"), new BinTreeProperty[] { map });
        var ms = new MemoryStream();
        new BinTree(new[] { obj }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static BinTreeStruct Light(string name, Vector3 pos, float radius, float intensity, Vector4? color = null)
    {
        var m = Matrix4x4.Identity;
        m.M41 = pos.X; m.M42 = pos.Y; m.M43 = pos.Z;
        var props = new List<BinTreeProperty>
        {
            new BinTreeMatrix44(H("transform"), m),
            new BinTreeString(H("name"), name),
            new BinTreeF32(H("intensityScale"), intensity),
            new BinTreeF32(H("radius"), radius),
        };
        if (color is { } c) props.Add(new BinTreeVector4(H("lightColor"), c));
        return new BinTreeStruct(0, H("MapDynamicPointLight"), props);
    }

    [Fact]
    public void A_placement_is_read_with_its_translation_from_the_last_matrix_row()
    {
        byte[] bin = BinWith(Light("L1", new Vector3(2200, 250, 2300), 1000f, 5f, new Vector4(1, 0, 0, 1)));

        var light = Assert.Single(DynamicPointLights.Read(bin));

        Assert.Equal("L1", light.Name);
        Assert.Equal(new Vector3(2200, 250, 2300), light.Position);
        Assert.Equal(1000f, light.Radius);
        Assert.Equal(5f, light.IntensityScale);
        Assert.Equal(new Vector3(1, 0, 0), light.Color);
    }

    /// <summary>Riot's own two placements omit lightColor entirely, so the default has to be the schema's
    /// white — reading them as black would silently kill every shipped light.</summary>
    [Fact]
    public void An_omitted_colour_falls_back_to_white()
    {
        byte[] bin = BinWith(Light("NoColour", Vector3.Zero, 1000f, 1000f));

        Assert.Equal(Vector3.One, Assert.Single(DynamicPointLights.Read(bin)).Color);
    }

    [Fact]
    public void A_bin_with_no_lights_reads_as_empty_rather_than_throwing()
    {
        Assert.Empty(DynamicPointLights.Read(BinWith()));
        Assert.Empty(DynamicPointLights.Read(Array.Empty<byte>()));
        Assert.Empty(DynamicPointLights.Read(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Written_lights_round_trip_through_a_real_bin()
    {
        byte[] bin = BinWith(Light("Old", Vector3.Zero, 500f, 1f));
        var wanted = new[]
        {
            new DynamicPointLight("A", new Vector3(10, 20, 30), new Vector3(1, 0, 0), 600f, 5f),
            new DynamicPointLight("B", new Vector3(40, 50, 60), new Vector3(0, 0, 1), 300f, 2.5f),
        };

        byte[]? outBin = DynamicPointLights.Write(bin, wanted, out int removed, out int written);

        Assert.NotNull(outBin);
        Assert.Equal(1, removed);          // the pre-existing placement is replaced, not appended to
        Assert.Equal(2, written);
        var back = DynamicPointLights.Read(outBin!);
        Assert.Equal(2, back.Count);
        Assert.DoesNotContain(back, l => l.Name == "Old");
        Assert.Equal(wanted[0], back.Single(l => l.Name == "A"));
        Assert.Equal(wanted[1], back.Single(l => l.Name == "B"));
    }

    /// <summary>The wire form is the whole ballgame: a Struct (0x82) loads and renders, an Embedded (0x83)
    /// in a placeable map is the M416 shape that loads everywhere and renders nothing.</summary>
    [Fact]
    public void Written_placements_use_the_struct_wire_form_inside_a_map()
    {
        byte[]? outBin = DynamicPointLights.Write(BinWith(),
            new[] { new DynamicPointLight("A", Vector3.One, Vector3.One, 500f, 1f) }, out _, out _);

        var tree = new BinTree(new MemoryStream(outBin!, false));
        var container = Assert.Single(tree.Objects.Values);
        var map = Assert.IsType<BinTreeMap>(Assert.Single(container.Properties.Values));
        var value = Assert.Single(map).Value;

        Assert.IsType<BinTreeStruct>(value);                 // exact type, not a subclass
        Assert.False(value is BinTreeEmbedded);
    }

    [Fact]
    public void Writing_into_an_unparseable_bin_reports_null_rather_than_corrupting()
    {
        Assert.Null(DynamicPointLights.Write(new byte[] { 9, 9, 9 }, Array.Empty<DynamicPointLight>(),
            out _, out _));
    }

    // ---- the bridges ----

    /// <summary>Bake and in-game lighting must read the same four numbers, or a bake would preview
    /// something the clustered path does not render.</summary>
    [Fact]
    public void Bake_lights_carry_the_same_quantities()
    {
        var l = new DynamicPointLight("A", new Vector3(1, 2, 3), new Vector3(0.5f, 0.25f, 0f), 700f, 5f);

        var baked = Assert.Single(DynamicPointLights.ToBakeLights(new[] { l }));

        Assert.Equal(l.Position, baked.Position);
        Assert.Equal(l.Color, baked.Color);
        Assert.Equal(l.Radius, baked.Radius);
        Assert.Equal(l.IntensityScale, baked.Intensity);
    }

    [Fact]
    public void Legacy_lights_convert_with_position_colour_and_radius_preserved()
    {
        var legacy = new[]
        {
            new PointLight(new Vector3(100, 50, 200), new Vector3(1f, 0.5f, 0.25f), 800f, 2f),
            new PointLight(new Vector3(-10, 0, 10), Vector3.One, 400f),
        };

        var ported = DynamicPointLights.FromLegacy(legacy);

        Assert.Equal(2, ported.Count);
        Assert.Equal(new Vector3(100, 50, 200), ported[0].Position);
        Assert.Equal(new Vector3(1f, 0.5f, 0.25f), ported[0].Color);
        Assert.Equal(800f, ported[0].Radius);
        Assert.Equal(2f, ported[0].IntensityScale);          // carried across unchanged by default
        Assert.Equal(new[] { "PortedLight0", "PortedLight1" }, ported.Select(p => p.Name));
    }

    /// <summary>The intensity bridge is the one unmeasured value — Light.dat has no intensity field, and
    /// Riot's placements use 1000 where 5 was already bright enough in game. So it stays an explicit knob
    /// and must actually scale.</summary>
    [Fact]
    public void The_legacy_intensity_bridge_is_an_explicit_multiplier()
    {
        var legacy = new[] { new PointLight(Vector3.Zero, Vector3.One, 500f, 2f) };

        Assert.Equal(2f, DynamicPointLights.FromLegacy(legacy).Single().IntensityScale);
        Assert.Equal(5f, DynamicPointLights.FromLegacy(legacy, intensityScale: 2.5f).Single().IntensityScale);
    }
}
