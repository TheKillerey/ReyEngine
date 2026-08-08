using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M384: grass tint + map-state parsing, shaped after Riot's real
/// <c>data/maps/shipping/map11/map11.bin</c>. Every property name and nesting level here was read off the
/// shipped bin, including the trap that cost a wrong first implementation: MapSkin.mAlternateAssets is an
/// embedded STRUCT (class MapAlternateAssets) whose own same-named property holds the container.
/// </summary>
public class MapStateDataTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static byte[] Write(params BinTreeObject[] objects)
    {
        using var ms = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    /// <summary>A MapSkin with a default tint and the given (flagName, tintPath) alternates.</summary>
    private static BinTreeObject Skin(uint pathHash, string? defaultTint,
        params (string Flag, string Tint)[] alternates)
    {
        var props = new List<BinTreeProperty>();
        if (defaultTint is not null)
            props.Add(new BinTreeString(H("mGrassTintTexture"), defaultTint));

        if (alternates.Length > 0)
        {
            var elements = alternates.Select(a => (BinTreeProperty)new BinTreeStruct(0, H("MapAlternateAsset"),
                new BinTreeProperty[]
                {
                    new BinTreeString(H("mGrassTintTextureName"), a.Tint),
                    new BinTreeHash(H("mVisibilityFlagName"), H(a.Flag)),
                })).ToList();

            // the two-level nesting the real bin uses
            var inner = new BinTreeContainer(H("mAlternateAssets"), BinPropertyType.Struct, elements);
            props.Add(new BinTreeStruct(H("mAlternateAssets"), H("MapAlternateAssets"),
                new BinTreeProperty[] { inner }));
        }

        return new BinTreeObject(pathHash, H("MapSkin"), props);
    }

    /// <summary>A Map object carrying VisibilityFlagDefines.</summary>
    private static BinTreeObject MapObj(params (string Name, string Public, byte Bit, float? Time)[] flags)
    {
        var defs = flags.Select(f =>
        {
            var p = new List<BinTreeProperty>
            {
                new BinTreeHash(H("name"), H(f.Name)),
                new BinTreeString(H("PublicName"), f.Public),
                new BinTreeU8(H("BitIndex"), f.Bit),
            };
            if (f.Time is { } t) p.Add(new BinTreeF32(H("TransitionTime"), t));
            return (BinTreeProperty)new BinTreeStruct(0, H("MapVisibilityFlagDefinition"), p);
        }).ToList();

        var defines = new BinTreeStruct(H("VisibilityFlagDefines"), H("MapVisibilityFlagDefinitions"),
            new BinTreeProperty[]
            {
                new BinTreeContainer(H("FlagDefinitions"), BinPropertyType.Struct, defs),
            });

        return new BinTreeObject(H("Maps/Shipping/Map11"), H("Map"), new BinTreeProperty[] { defines });
    }

    // Riot's real values, so a schema change shows up as a test failure rather than a silent behaviour change.
    private static byte[] RealisticBin() => Write(
        MapObj(("Fire", "Infernal", 1, 8f), ("earth", "Mountain", 2, 8f), ("Ocean", "Ocean", 3, 9f),
               ("CLOUD", "Cloud", 4, 4.5f), ("Hextech", "Hextech", 5, null),
               ("Chemtech", "Chemtech", 6, null), ("Void", "Void", 7, 1f)),
        Skin(H("Maps/Shipping/Map11/MapSkins/SocialSR"), "ASSETS/Maps/Info/Map11/GrassTint_SRX.tex",
            ("Fire", "ASSETS/Maps/Info/Map11/GrassTint_SRX_Infernal.tex"),
            ("Ocean", "ASSETS/Maps/Info/Map11/GrassTint_SRX_Ocean.tex")));

    // ---- parsing ----

    [Fact]
    public void ParsesTheDefaultGrassTint()
    {
        var st = MapStateData.Parse(RealisticBin());
        var skin = Assert.Single(st.Skins);
        Assert.Equal("ASSETS/Maps/Info/Map11/GrassTint_SRX.tex", skin.GrassTintTexture);
    }

    /// <summary>The nesting trap: reading mAlternateAssets as a container yields nothing and looks like a
    /// map that simply has no alternates.</summary>
    [Fact]
    public void ParsesAlternatesThroughTheWrapperStruct()
    {
        var st = MapStateData.Parse(RealisticBin());
        Assert.Equal(2, st.Skins[0].AlternateAssets.Count);
        Assert.Contains(st.Skins[0].AlternateAssets,
            a => a.GrassTintTexture!.EndsWith("GrassTint_SRX_Infernal.tex"));
    }

    [Fact]
    public void ParsesRiotsTransitionTimes()
    {
        var st = MapStateData.Parse(RealisticBin());
        Assert.Equal(8f, st.TransitionTimeForBit(1));
        Assert.Equal(9f, st.TransitionTimeForBit(3));
        Assert.Equal(4.5f, st.TransitionTimeForBit(4));
        Assert.Equal(1f, st.TransitionTimeForBit(7));
    }

    /// <summary>Hextech and Chemtech author NO TransitionTime. Null must stay null - substituting a
    /// default would invent a transition Riot does not define.</summary>
    [Fact]
    public void AbsentTransitionTimeStaysNullRatherThanZero()
    {
        var st = MapStateData.Parse(RealisticBin());
        Assert.Null(st.TransitionTimeForBit(5));
        Assert.Null(st.TransitionTimeForBit(6));
    }

    [Fact]
    public void PublicNamesAreKeptForTheUi()
    {
        var st = MapStateData.Parse(RealisticBin());
        Assert.Equal("Infernal", st.FlagByBit(1)!.PublicName);
        Assert.Equal("Mountain", st.FlagByBit(2)!.PublicName);
    }

    // ---- resolution ----

    [Fact]
    public void NoActiveFlagUsesTheDefaultTint()
    {
        var st = MapStateData.Parse(RealisticBin());
        var c = st.ResolveGrassTint(st.Skins[0], _ => false);
        Assert.Equal("ASSETS/Maps/Info/Map11/GrassTint_SRX.tex", c.ActivePath);
        Assert.False(c.FromAlternate);
        Assert.Equal("mGrassTintTexture", c.SourceLabel);
    }

    [Fact]
    public void AMatchingFlagSelectsItsAlternate()
    {
        var st = MapStateData.Parse(RealisticBin());
        var c = st.ResolveGrassTint(st.Skins[0], bit => bit == 1);   // Infernal
        Assert.Equal("ASSETS/Maps/Info/Map11/GrassTint_SRX_Infernal.tex", c.ActivePath);
        Assert.True(c.FromAlternate);
        Assert.Equal(1, c.BitIndex);
        Assert.Equal("ASSETS/Maps/Info/Map11/GrassTint_SRX.tex", c.DefaultPath);
    }

    /// <summary>Switching states must not leave the previous alternate active.</summary>
    [Fact]
    public void SwitchingStateSwitchesTheAlternate()
    {
        var st = MapStateData.Parse(RealisticBin());
        Assert.EndsWith("Infernal.tex", st.ResolveGrassTint(st.Skins[0], b => b == 1).ActivePath);
        Assert.EndsWith("Ocean.tex", st.ResolveGrassTint(st.Skins[0], b => b == 3).ActivePath);
        Assert.EndsWith("GrassTint_SRX.tex", st.ResolveGrassTint(st.Skins[0], _ => false).ActivePath);
    }

    /// <summary>A flag that is active but has no alternate authored falls back to the default rather than
    /// picking an unrelated one. SocialSR really does this for Void.</summary>
    [Fact]
    public void ActiveFlagWithoutAnAlternateFallsBackToDefault()
    {
        var st = MapStateData.Parse(RealisticBin());
        var c = st.ResolveGrassTint(st.Skins[0], bit => bit == 7);   // Void
        Assert.Equal("ASSETS/Maps/Info/Map11/GrassTint_SRX.tex", c.ActivePath);
        Assert.False(c.FromAlternate);
    }

    /// <summary>Nothing here compares a dragon NAME. A skin inventing its own state resolves purely by
    /// mVisibilityFlagName -> flag definition -> BitIndex.</summary>
    [Fact]
    public void ResolutionIsGenericOverCustomStates()
    {
        var bin = Write(
            MapObj(("MyCustomWeather", "Custom", 3, 2.5f)),
            Skin(H("Maps/Shipping/Map11/MapSkins/Custom"), "custom/default.tex",
                ("MyCustomWeather", "custom/weather.tex")));
        var st = MapStateData.Parse(bin);

        Assert.Equal(2.5f, st.TransitionTimeForBit(3));
        Assert.Equal("custom/weather.tex", st.ResolveGrassTint(st.Skins[0], b => b == 3).ActivePath);
        Assert.Equal("custom/default.tex", st.ResolveGrassTint(st.Skins[0], _ => false).ActivePath);
    }

    // ---- robustness: this runs against modded bins ----

    [Fact]
    public void GarbageBytesParseToEmptyRatherThanThrowing()
    {
        var st = MapStateData.Parse(new byte[] { 1, 2, 3, 4, 5 });
        Assert.Empty(st.Skins);
        Assert.Empty(st.FlagDefinitions);
    }

    [Fact]
    public void ASkinWithNoTintAtAllResolvesToNull()
    {
        var bin = Write(MapObj(("Fire", "Infernal", 1, 8f)), Skin(H("bare"), null));
        var st = MapStateData.Parse(bin);
        Assert.Null(st.ResolveGrassTint(st.Skins[0], _ => false).ActivePath);
    }

    [Fact]
    public void NullSkinIsHandled()
    {
        var st = MapStateData.Parse(RealisticBin());
        Assert.Null(st.ResolveGrassTint(null, _ => true).ActivePath);
    }
}
