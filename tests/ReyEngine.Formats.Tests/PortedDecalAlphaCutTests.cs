using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M598: a ported decal's alpha cut is 0.005, and the census that says so has to be the DECAL census.
///
/// <para>This value has now been set from the corpus twice and got it wrong once, because the obvious
/// population is the wrong one. Over every shipped wad, 3,877 materials sit on an alpha-test shader with
/// blending enabled. Split by whether the material is a decal:</para>
///
/// <list type="table">
///   <item><term>decal-named (34)</term><description>32 at <b>0.005</b>, 2 at 0.001, <b>0 at 0.3</b></description></item>
///   <item><term>everything else (3,843)</term><description>3,221 at 0.3, 347 at 0.9, 220 at 0.005</description></item>
/// </list>
///
/// <para>M543 read the combined set - which is 99% not decals - saw 0.3 as the mode and applied it to the
/// decal role. Riot's own decals in the destination bin (<c>lanetowerdcl_decalVersion3_no_shadow</c> and
/// its siblings, in the same <c>jade_container.materials.bin</c> the port writes into) are in the 0.005
/// group. The two populations disagree, so the aggregate mode is not evidence about decals.</para>
///
/// <para>The mechanism M543 argued from was also wrong: it said "at 0.005 nothing is discarded". The test
/// discards alpha <b>below</b> the cutoff, and a fully transparent texel is below any positive one - on
/// the ported legacy decal textures 0.005 discards 48-55% of the image, the entire clear surround, so the
/// ground still composites through and nothing stamps depth there. What 0.3 did instead was cut the art
/// along the arbitrary alpha-0.3 contour of a GRADIENT channel (legacy decal alpha is gloss, not a mask -
/// every ported decal texture measured classifies <see cref="LegacyAlphaKind.Gradient"/>), leaving
/// 71.8% of <c>order_base_decal_mid</c> drawn as a hard-edged slab of colour on the ground.</para>
/// </summary>
public sealed class PortedDecalAlphaCutTests
{
    /// <summary>32 of Riot's 34 blended alpha-test decals; the other 2 are 0.001. Nothing is at 0.3.</summary>
    private const float RiotDecalCut = 0.005f;

    /// <summary>The mode of the NON-decal population. Correct there, and the value that leaked onto decals.</summary>
    private const float NonDecalCorpusMode = 0.3f;

    /// <summary>Run a real role through the porter's shader mapping and read what it authored.</summary>
    private static Vector4 AlphaTestFor(LegacyMaterialRole role)
    {
        var plan = new LegacyMaterialPlan("m", role, "old",
            new Dictionary<string, string>(), new Dictionary<string, Vector4>(),
            new Dictionary<string, bool>(), new Dictionary<string, bool>());
        var source = new LegacyMapPortResult(Array.Empty<byte>(), Array.Empty<LegacyTextureCopy>(),
            new[] { plan }, "room.nvr", "NVR", 1, 1, 0, 0, 1, Array.Empty<string>());

        return LegacyMapPorter.ApplyShaderOptions(source, LegacyPortShaderOptions.Defaults)
            .Materials.Single().Parameters["AlphaTestValue"];
    }

    [Fact]
    public void ADecalUsesTheValueRiotUsesOnDecals()
    {
        Assert.Equal(RiotDecalCut, AlphaTestFor(LegacyMaterialRole.Decal).X, 6);
    }

    [Fact]
    public void ADecalIsNotGivenTheNonDecalCorpusMode()
    {
        // The specific regression: 0 of Riot's 34 decals author this, and it drew them as slabs.
        Assert.NotEqual(NonDecalCorpusMode, AlphaTestFor(LegacyMaterialRole.Decal).X);
    }

    [Fact]
    public void AnOrdinarySurfaceKeepsItsOwnCutAndIsNotDraggedAlong()
    {
        // Normal surfaces are a genuine cutout population and keep 0.35; the fix is scoped to decals.
        Assert.Equal(0.35f, AlphaTestFor(LegacyMaterialRole.Normal).X, 6);
    }

    [Fact]
    public void TheDecalCutIsStrictlyBelowTheOrdinaryOne()
    {
        // A decal feathers into the ground; a cutout is on or off. If these ever converge, one of the two
        // populations has been read as the other again.
        Assert.True(AlphaTestFor(LegacyMaterialRole.Decal).X < AlphaTestFor(LegacyMaterialRole.Normal).X);
    }

    [Fact]
    public void TheCutIsPositiveSoAFullyTransparentTexelIsStillDiscarded()
    {
        // The whole reason 0.005 is safe: it is above zero, so the clear surround is still cut and never
        // stamps depth. A cut of exactly 0 would keep every transparent texel and is the failure M543
        // was actually reaching for.
        Assert.True(AlphaTestFor(LegacyMaterialRole.Decal).X > 0f);
    }

    [Theory]
    [InlineData(LegacyMaterialRole.Decal)]
    [InlineData(LegacyMaterialRole.Normal)]
    public void OnlyTheXChannelCarriesTheCut(LegacyMaterialRole role)
    {
        var v = AlphaTestFor(role);
        Assert.Equal(0f, v.Y);
        Assert.Equal(0f, v.Z);
        Assert.Equal(0f, v.W);
    }
}
