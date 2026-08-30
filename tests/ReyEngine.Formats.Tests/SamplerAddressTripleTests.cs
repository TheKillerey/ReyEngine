using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M599, narrowed by M601: <c>addressU</c> and <c>addressV</c> move together.
///
/// <para>M599 flagged a clamped sampler that left <c>addressW</c> out, from Riot's DECAL census: of 569
/// decal samplers, 379 author nothing (Wrap), 115 are <c>U2 V2 W-</c> (Mirror), 58 are <c>W1</c> alone,
/// and all <b>17</b> that clamp write the full <c>U1 V1 W1</c> triple - none writes <c>U1 V1 W-</c>.</para>
///
/// <para><b>That rule is retracted.</b> The map author tested it in game: a ported map whose decals sit at
/// <c>U1 V1 W-</c> renders correctly, and they confirmed that state as the one they wanted. Widening the
/// census past decals shows the shape is ordinary - Riot ships <c>U1 V1 W-</c> on <b>124</b> map samplers.
/// A 569-row decal-only base was too narrow to call it broken, and the rule fired on 17 samplers that
/// were fine. <c>addressW</c> is the third axis of a volume texture; a 2D sampler uses U and V.</para>
///
/// <para>What survives is the weaker claim the wider census still supports, and which no shipped material
/// violates in a way worth flagging: setting one of U/V without the other leaves one axis on the shader
/// default while the other is authored.</para>
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
        issues.Any(i => i.Category == "half-address-pair");

    [Fact]
    public void ClampingOnUAndVWithoutWIsAcceptedBecauseItRendersCorrectly()
    {
        // M601: the retraction. The author confirmed this exact shape in game, and Riot ships it 124
        // times on map samplers. M599 called it broken from a decal-only census and was wrong.
        Assert.False(Flagged(Check(1, 1, null)));
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
    public void TheFindingSaysWhichAxisIsMissingItsPartner()
    {
        var issue = Assert.Single(Check(1, null, null).Where(i => i.Category == "half-address-pair"));

        Assert.Contains("addressV", issue.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("together", issue.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
