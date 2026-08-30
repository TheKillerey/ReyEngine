using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M599: a sampler that clamps has to author all three address axes.
///
/// <para>Censused over every shipped map wad, <b>569 decal samplers</b>: 379 author nothing at all
/// (Wrap - Riot's commonest case by far), 115 are <c>U2 V2 W-</c> (Mirror, genuinely without W), 58 are
/// <c>W1</c> alone, and every one of the <b>17 that clamp writes the full U1/V1/W1 triple</b>.
/// <b>Zero</b> ship <c>U1 V1 W-</c>.</para>
///
/// <para>A ported Map453 arrived with 17 of its 19 decals at <c>U1 V1 W-</c> and 2 at <c>U1 V1 W1</c>,
/// and in game only those 2 clamped - the other 17 tiled their texture across the surface, which is what
/// the author reported as "only 1 or 2 are getting the clamp". The porter writes the three together
/// (M490), so a partial triple means something edited the sampler afterwards. The editor's preset apply
/// is one path that produces it: an axis the preset leaves unset is written as <c>null</c>, and null
/// REMOVES the field.</para>
///
/// <para>Mirror is deliberately not flagged: <c>U2 V2 W-</c> is a shape Riot really ships, 115 times.
/// The rule is about clamping specifically.</para>
/// </summary>
public sealed class SamplerAddressTripleTests
{
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    /// <summary>One material with one sampler carrying exactly the given axes.</summary>
    private static BinTree Material(int? u, int? v, int? w)
    {
        var sampler = new List<BinTreeProperty>
        {
            new BinTreeString(H("TextureName"), "DiffuseTexture"),
            new BinTreeString(H("texturePath"), "assets/maps/legacy/decal.dds"),
        };
        if (u is { } uu) sampler.Add(new BinTreeU32(H("addressU"), (uint)uu));
        if (v is { } vv) sampler.Add(new BinTreeU32(H("addressV"), (uint)vv));
        if (w is { } ww) sampler.Add(new BinTreeU32(H("addressW"), (uint)ww));

        var material = new BinTreeObject(H("LegacyPort/map1/Decal_test"), H("StaticMaterialDef"),
            new BinTreeProperty[]
            {
                new BinTreeString(H("name"), "LegacyPort/map1/Decal_test"),
                new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded,
                    new BinTreeProperty[] { new BinTreeEmbedded(0, H("StaticMaterialShaderSamplerDef"), sampler) }),
            });
        return new BinTree(new[] { material }, Array.Empty<string>());
    }

    private static IReadOnlyList<BinIssue> Check(int? u, int? v, int? w)
    {
        var tree = Material(u, v, w);
        using var stream = new MemoryStream();
        tree.Write(stream);
        return ModShapeValidator.ValidateBin(tree, stream.ToArray());
    }

    private static bool Flagged(IReadOnlyList<BinIssue> issues) =>
        issues.Any(i => i.Category == "partial-address-triple");

    [Fact]
    public void ClampingOnUAndVWithoutWIsFlagged()
    {
        // The exact shape the ported map shipped, and the one that tiled in game.
        Assert.True(Flagged(Check(1, 1, null)));
    }

    [Fact]
    public void TheFullClampTripleIsAccepted()
    {
        // What Riot writes on all 17 of its clamped decal samplers.
        Assert.False(Flagged(Check(1, 1, 1)));
    }

    [Fact]
    public void AuthoringNothingIsAcceptedBecauseThatIsRiotsCommonestCase()
    {
        // 379 of 569 decal samplers author no address field at all - absent is Wrap, and it is fine.
        Assert.False(Flagged(Check(null, null, null)));
    }

    [Fact]
    public void MirrorWithoutWIsAcceptedBecauseRiotShipsIt()
    {
        // 115 decal samplers are U2/V2 with no W. The rule is about clamping, not about the triple
        // for its own sake - flagging this would be inventing a convention Riot does not follow.
        Assert.False(Flagged(Check(2, 2, null)));
    }

    [Fact]
    public void WAloneIsAcceptedBecauseThatIsWhatMostMapSamplersDo()
    {
        // 58 decal samplers and 7,694 non-decal ones author addressW=1 by itself.
        Assert.False(Flagged(Check(null, null, 1)));
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(null, 1)]
    [InlineData(2, null)]
    public void OneAxisWithoutItsPartnerIsFlagged(int? u, int? v)
    {
        // U and V always move together in shipped data, whatever the mode.
        Assert.True(Flagged(Check(u, v, null)));
    }

    [Fact]
    public void TheFindingSaysWhatBreaksRatherThanJustNamingTheField()
    {
        var issue = Assert.Single(Check(1, 1, null).Where(i => i.Category == "partial-address-triple"));

        Assert.Contains("addressW", issue.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("17 of 17", issue.Detail);
        Assert.Contains("TILES", issue.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
