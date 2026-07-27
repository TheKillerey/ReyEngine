using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M198: the invariants that keep the Particle Editor's "not shown in the preview" badge honest.
///
/// <para>These lived only in a scratchpad harness until now, which was the standing risk M193 recorded:
/// any milestone that adds a resolver constant or parks a field can break the badge, and the breakage is
/// invisible until somebody re-runs a tool that is not in the repo. Nothing here needs a game install.</para>
///
/// <para>The failure the badge must never produce is a MISSING badge on a field the renderer ignores -
/// that is the editor telling the user an edit is visible when it is not. A spurious badge is merely
/// noisy. Every assertion below is oriented that way.</para>
/// </summary>
public class VfxPreviewCoverageTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    [Fact]
    public void CoverageSetLoads()
    {
        // If reflection ever fails, IgnoredNote badges everything and the rest of these tests are vacuous.
        Assert.True(VfxPreviewCoverage.CoverageAvailable);
    }

    [Fact]
    public void EveryParkedEmitterFieldIsBadged()
    {
        Assert.NotEmpty(VfxParkedEmitterFields.Names);
        foreach (var f in VfxParkedEmitterFields.Names)
            Assert.True(VfxPreviewCoverage.IgnoredNote(H(f)) is not null,
                $"parked emitter field '{f}' is NOT badged - the editor would claim editing it is visible");
    }

    [Fact]
    public void EveryParkedSystemFieldIsBadged()
    {
        Assert.NotEmpty(VfxParkedSystemFields.Names);
        foreach (var f in VfxParkedSystemFields.Names)
            Assert.True(VfxPreviewCoverage.IgnoredNote(H(f)) is not null,
                $"parked system field '{f}' is NOT badged");
    }

    [Fact]
    public void ParkedFieldsAreNotResolverConstants()
    {
        // This is the structural guarantee. VfxPreviewCoverage treats any resolver hash constant as
        // "the preview reads it", so a parked field declared there would silently lose its badge.
        foreach (var f in VfxParkedEmitterFields.Names.Concat(VfxParkedSystemFields.Names))
            Assert.False(VfxPreviewCoverage.IsParsed(H(f)),
                $"'{f}' is parked AND declared as a resolver constant - the badge depends on it not being both");
    }

    [Theory]
    // read by the resolver AND consumed by the renderer: a badge on any of these would be a lie
    [InlineData("rate")]
    [InlineData("particleLifetime")]
    [InlineData("Color")]
    [InlineData("birthScale0")]
    [InlineData("velocity")]
    [InlineData("texture")]
    [InlineData("blendMode")]
    [InlineData("SpawnShape")]
    [InlineData("primitive")]
    [InlineData("alphaErosionDefinition")]
    [InlineData("softParticleParams")]
    [InlineData("paletteDefinition")]
    [InlineData("reflectionDefinition")]
    [InlineData("Linger")]
    [InlineData("stencilMode")]
    [InlineData("particleName")]
    // nested fields inside those structs, which M192 taught the badge to ask about individually
    [InlineData("erosionMapName")]
    [InlineData("erosionDriveCurve")]
    [InlineData("deltaIn")]
    [InlineData("reflectionFresnel")]
    [InlineData("paletteCount")]
    // M209: applied since the winding was settled in the app (CW). It sat in the badged list from M191
    // to M208 because it was parsed but deliberately not applied.
    [InlineData("disableBackfaceCull")]
    public void RenderedFieldsAreNotBadged(string field) =>
        Assert.True(VfxPreviewCoverage.IgnoredNote(H(field)) is null,
            $"'{field}' IS rendered but got badged - over-badging trains the user to ignore the badge");

    [Theory]
    // M192: read by the shared resolver, consumed only by the MAP path (or by nothing at all)
    [InlineData("soundOnCreateDefault")]
    [InlineData("soundPersistentDefault")]
    [InlineData("visibilityRadius")]
    [InlineData("emitterLinger")]
    // parsed by the resolver, deliberately not applied by the renderer
    [InlineData("particleLingerType")]
    public void ParsedButUnrenderedFieldsAreParsedAndStillBadged(string field)
    {
        Assert.True(VfxPreviewCoverage.IsParsed(H(field)), $"'{field}' should be parsed by the resolver");
        Assert.True(VfxPreviewCoverage.IgnoredNote(H(field)) is not null,
            $"'{field}' is parsed but unrendered and MUST stay badged");
    }

    [Theory]
    // nested fields the resolver skips, inside structs it does read. Before M192 these inherited the
    // parent's verdict and falsely claimed to be visible - the Linger flags most damningly, since
    // VfxSystemResolver states in capitals that it ignores them.
    [InlineData("UseSeparateLingerColor")]
    [InlineData("UseLingerScale")]
    [InlineData("UseKeyedLingerVelocity")]
    [InlineData("UseLingerRotation")]
    [InlineData("UseKeyedLingerDrag")]
    [InlineData("UseKeyedLingerAcceleration")]
    [InlineData("erosionMapAddressMode")]
    [InlineData("uvScaleMult")]
    [InlineData("texAddressModeMult")]
    public void NestedUnreadFieldsAreBadged(string field) =>
        Assert.True(VfxPreviewCoverage.IgnoredNote(H(field)) is not null,
            $"nested field '{field}' is not read by the resolver and must be badged on its own hash");

    [Fact]
    public void AnUnknownHashIsBadged()
    {
        // Fail CLOSED. Under this badge's semantics the ABSENCE of a badge is the claim that an edit is
        // visible, so "I don't know" must show the badge, not hide it.
        Assert.NotNull(VfxPreviewCoverage.IgnoredNote(0xDEADBEEF));
    }

    [Fact]
    public void TheTwoNotesAreDistinguishable()
    {
        // "we parse it and do nothing" and "we never look at it" are different promises to the user.
        var parked = VfxPreviewCoverage.IgnoredNote(H("miscRenderFlags"));
        var neverRead = VfxPreviewCoverage.IgnoredNote(H("Filtering"));
        Assert.NotNull(parked);
        Assert.NotNull(neverRead);
        Assert.NotEqual(parked, neverRead);
    }
}
