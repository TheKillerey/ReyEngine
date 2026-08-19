using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M531: reading the legacy Particles.dat placement list.
///
/// <para>The expectations here are not read off the data - they come from the client's own parser. The
/// loader at VA 0x6C86D0 in League of Legends.exe calls sscanf with the format at VA 0x10A5354,
/// <c>%s %g %g %g %d %g %g %g %s</c>, which is why token 8 is discarded and why the vector and the
/// group are mutually exclusive.</para>
/// </summary>
public sealed class LegacyParticlePlacementTests
{
    private const string Map2 = @"K:\LeagueSandbox\League_Sandbox_Client\LEVELS\Map2\Particles.dat";

    [Fact]
    public void AnOrdinaryLineParsesIntoPathAndPosition()
    {
        var set = LegacyParticlePlacements.Parse(
            "Data\\Particles\\CANDLE.troy 10581.3 223.72 5570.36 -2147483648 0 0 0\r\n");

        var p = Assert.Single(set.Placements);
        Assert.Equal("Data\\Particles\\CANDLE.troy", p.SystemPath);
        Assert.Equal("candle", p.SystemName);
        Assert.Equal(new Vector3(10581.3f, 223.72f, 5570.36f), p.Position);
        Assert.True(p.AlwaysSpawns);
        Assert.Empty(set.Warnings);
    }

    [Fact]
    public void BothPathSeparatorsAndAnyCaseResolveToTheSameSystem()
    {
        // Map2 mixes "Data\Particles\CANDLE.troy" and "Data/Particles/PumpkinCandle.troy" in one file.
        var set = LegacyParticlePlacements.Parse(
            "Data\\Particles\\CANDLE.troy 1 2 3 -2147483648 0 0 0\n"
            + "Data/Particles/candle.TROY 4 5 6 -2147483648 0 0 0\n");

        Assert.Equal(2, set.Placements.Count);
        Assert.Equal(new[] { "candle" }, set.SystemNames);
    }

    [Fact]
    public void TheEighthTokenIsDiscardedTheWayTheClientDiscardsIt()
    {
        // The client composes (f6, f7, f7). f7==f8 on 2302 of 2306 corpus lines precisely because of it.
        var set = LegacyParticlePlacements.Parse("a.troy 0 0 0 -2147483648 1 2 3\n");

        var p = Assert.Single(set.Placements);
        Assert.Equal(new Vector3(1, 2, 3), p.RawVector);
        Assert.Equal(new Vector3(1, 2, 2), p.ClientVector);
    }

    [Fact]
    public void AVectorAndAGroupAreMutuallyExclusive()
    {
        // Two separate branches on the sscanf return count: ret==8 applies the vector, ret==9 the group.
        var set = LegacyParticlePlacements.Parse(
            "a.troy 0 0 0 -2147483648 1 2 2\n"
            + "b.troy 0 0 0 -2147483648 0 0 0 highWinds\n");

        Assert.True(set.Placements[0].VectorIsApplied);
        Assert.Null(set.Placements[0].EmitterGroup);

        Assert.False(set.Placements[1].VectorIsApplied);
        Assert.Equal("highWinds", set.Placements[1].EmitterGroup);
    }

    [Fact]
    public void ATenthTokenIsRecordedAsIgnoredBecauseTheClientNeverReadsIt()
    {
        // map11 writes "NONE CHAOSONLY". The format has 9 conversions, so CHAOSONLY reaches nothing -
        // the string does not even appear in the executable. Recording it beats implying a team filter.
        var set = LegacyParticlePlacements.Parse(
            "sruap_order_basedoor_shield_top.troy 2701.53 95.7479 4752.27 -2147483648 0 0 0 NONE CHAOSONLY\n");

        var p = Assert.Single(set.Placements);
        Assert.Equal("NONE", p.EmitterGroup);
        Assert.Equal(new[] { "CHAOSONLY" }, p.IgnoredTokens);
    }

    [Fact]
    public void BlankAndMalformedLinesAreSkippedWithAWarningRatherThanGuessedAt()
    {
        var set = LegacyParticlePlacements.Parse(
            "a.troy 1 2 3 -2147483648 0 0 0\n"
            + "\n"
            + "   \r\n"
            + "b.troy 1 2\n"
            + "c.troy x y z\n");

        Assert.Single(set.Placements);
        Assert.Equal(2, set.Warnings.Count);   // blank lines are silent; the two malformed ones are not
    }

    [Fact]
    public void FloatsParseUnderAGermanLocale()
    {
        var prior = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var set = LegacyParticlePlacements.Parse("a.troy 10581.3 223.72 5570.36 -2147483648 0 0 0\n");
            Assert.Equal(10581.3f, Assert.Single(set.Placements).Position.X, 3);
        }
        finally { Thread.CurrentThread.CurrentCulture = prior; }
    }

    [Fact]
    public void TheRealMap2FileParsesCompletely()
    {
        if (!File.Exists(Map2)) return;

        var set = LegacyParticlePlacements.ParseFile(Map2);

        Assert.Equal(554, set.Placements.Count);
        Assert.Empty(set.Warnings);
        Assert.Equal(16, set.SystemNames.Count);
        Assert.Contains("candle", set.SystemNames);
        Assert.All(set.Placements, p => Assert.True(p.AlwaysSpawns));

        // Every line in this file is the plain 8-token form, so nothing carries a group.
        Assert.All(set.Placements, p => Assert.Null(p.EmitterGroup));
    }
}
