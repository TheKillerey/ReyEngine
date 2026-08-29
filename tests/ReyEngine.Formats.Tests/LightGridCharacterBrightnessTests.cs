using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M596: <c>lightGridCharacterFullBrightIntensity</c> is the map's dial for how brightly CHARACTERS are
/// self-lit, and baking a lightgrid used to overwrite it with a hardcoded 0.25.
///
/// <para>Censused over every shipped map wad — 200 <c>MapBakeProperties</c>: <b>129 author 0.5</b>, 25
/// author 0.65, 29 leave it out, 6 author 1.0, and the remainder scatter between 0.4 and 1.22.
/// <b>Zero author 0.25.</b> It was below the entire corpus.</para>
///
/// <para>Map453 (Jade) declares <b>1.0</b>. Baking a neutral grid onto a port of it therefore cut every
/// champion, minion and turret to a quarter of their intended self-illumination — which reached the user
/// as "the tower looks darker" on a map whose terrain was otherwise correct. The value is consumed by
/// <c>LIGHTGRID_SCALE.y</c> in <c>LIT_UBER_PS</c>, a character shader, so nothing in the map geometry or
/// the materials bin shows it going wrong.</para>
///
/// <para>The rule is therefore: <b>keep what the map already declares</b>, and only fall back to the
/// corpus mode when it declares nothing.</para>
/// </summary>
public sealed class LightGridCharacterBrightnessTests
{
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    /// <summary>A bin shaped like a real one: MapContainer -> components -> MapBakeProperties.</summary>
    private static byte[] BinDeclaring(float? fullBright)
    {
        var bake = new List<BinTreeProperty> { new BinTreeU32(H("lightGridSize"), 256) };
        if (fullBright is { } f)
            bake.Add(new BinTreeF32(H("lightGridCharacterFullBrightIntensity"), f));
        bake.Add(new BinTreeString(H("lightGridFileName"), "ASSETS/Maps/Lightmaps/x/LightGrid.dat"));

        var container = new BinTreeObject(H("MapContainerInstance"), H("MapContainer"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("components"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("MapBakeProperties"), bake),
            }),
        });

        using var stream = new MemoryStream();
        new BinTree(new[] { container }, Array.Empty<string>()).Write(stream);
        return stream.ToArray();
    }

    [Fact]
    public void TheCorpusDefaultIsWhatRiotActuallyShipsMostOften()
    {
        // 129 of the 171 shipped MapBakeProperties that author the field say 0.5.
        Assert.Equal(0.5f, NeutralLightGrid.CorpusCharacterFullBrightIntensity);
    }

    [Fact]
    public void TheOldHardcodedValueIsNotTheDefaultAnyMore()
    {
        // 0 of 200 shipped maps author 0.25. A default nothing in the corpus uses is a value we invented.
        Assert.NotEqual(0.25f, NeutralLightGrid.CorpusCharacterFullBrightIntensity);
    }

    [Fact]
    public void AMapThatDeclaresItsOwnValueIsReadBackUnchanged()
    {
        // Jade's value. Baking must not be allowed to quietly replace it.
        var declared = MapBakeProperties.Read(BinDeclaring(1.0f));

        Assert.NotNull(declared);
        Assert.Equal(1.0f, declared!.Value.FullBright);
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(0.65f)]
    [InlineData(0.5f)]
    [InlineData(0.4f)]
    public void EveryValueRiotShipsSurvivesAReadBack(float authored)
    {
        var declared = MapBakeProperties.Read(BinDeclaring(authored));

        Assert.NotNull(declared);
        Assert.Equal(authored, declared!.Value.FullBright);
    }

    [Fact]
    public void AMapThatDeclaresNothingReadsBackAsZeroSoTheCallerCanTellTheDifference()
    {
        // The fallback has to be distinguishable from a real authored value, or "absent" silently becomes
        // "0", which would be darker still than the bug being fixed.
        var declared = MapBakeProperties.Read(BinDeclaring(null));

        Assert.NotNull(declared);
        Assert.Equal(0f, declared!.Value.FullBright);
    }

    /// <summary>The grid file and the bin must agree — they do in 173/173 joinable shipped pairs, and the
    /// client reads the header copy for characters and the bin copy for everything else.</summary>
    [Theory]
    [InlineData(1.0f)]
    [InlineData(0.5f)]
    public void TheGridCarriesExactlyTheIntensityItWasBuiltWith(float fullBright)
    {
        var grid = NeutralLightGrid.Build(15000f, 15000f, 16, 16, fullBright);

        Assert.Equal(fullBright, grid.CharacterFullBrightIntensity);
        Assert.Equal(fullBright, LightGridFile.Read(grid.Write()).CharacterFullBrightIntensity);
    }

    [Fact]
    public void BuildingWithoutSayingSoUsesTheCorpusValueRatherThanTheOldConstant()
    {
        var grid = NeutralLightGrid.Build(15000f, 15000f, 16, 16,
            NeutralLightGrid.CorpusCharacterFullBrightIntensity);

        Assert.Equal(0.5f, grid.CharacterFullBrightIntensity);
    }
}
