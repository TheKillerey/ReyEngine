using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// <para>M277. The shader cache's stage separator is NOT stable, and the day it changed cost an afternoon.</para>
///
/// <para>Up to 2026-07-29 every entry in <c>ShaderCache.dx11.wad.client</c> was <c>{shader}.vs.dx11</c>;
/// that patch renamed all 2,176 of them - blob containers included - to <c>{shader}.vs-dx11</c>. Nothing
/// degraded gracefully: the reader missed every shader at once, the viewport reported "0 material(s), 21
/// unresolved" with no reason, and the D3D11 status line sat on "preparing scene..." forever. A lookup that
/// finds nothing is indistinguishable from an asset that was never shipped, which is why this took so long
/// to localise and why it is worth a test.</para>
///
/// <para><b>These are deliberately not written against the helper's own output.</b> The oracle is a set of
/// path strings copied VERBATIM out of the patched cache (harness mode <c>wadls</c>: 2,176 chunks, 2,154
/// resolved, all hyphenated), and the inputs are what production actually asks for - the string
/// <see cref="ShaderCacheReader.TocPathFor"/> builds, and the <c>{toc}_{N}</c> container name
/// <c>LoadBlob</c> derives from it. Delete hyphen support and these go red, which is the whole point: this
/// project has already been bitten by a blend-mode test that only asserted a helper returned what the
/// helper returned.</para>
/// </summary>
public class ShaderCacheNamingTests
{
    /// <summary>Verbatim from the live cache after the 2026-07-29 patch. Not generated - typed from the
    /// listing, so nothing in the code under test had a hand in producing them.</summary>
    private static readonly string[] ShippedHyphenated =
    {
        "assets/shaders/generated/shaders/staticmesh/env_glowsign.vs-dx11_0",
        "assets/shaders/generated/shaders/staticmesh/env_glowsign.vs-dx11",
        "assets/shaders/hlsl/particlesystem/distortion_vs.vs-dx11",
        "assets/shaders/generated/shaders/skinnedmesh/pbr_toonshading.ps-dx11_900",
        "assets/shaders/generated/shaders/skinnedmesh/pbr_toonshading.ps-dx11",
    };

    /// <summary>The pre-patch spelling, which mods and older installs still ship.</summary>
    private static readonly string[] ShippedDotted =
    {
        "assets/shaders/generated/shaders/staticmesh/env_glowsign.vs.dx11",
        "assets/shaders/generated/shaders/staticmesh/env_glowsign.vs.dx11_0",
    };

    private static Func<string, bool> CacheOf(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    /// <summary>The request production makes is the DOTTED canonical form. Against a cache holding only the
    /// hyphenated names it must still land on the real entry.</summary>
    [Fact]
    public void ATocRequestFindsTheHyphenatedEntryThePatchShipped()
    {
        string asked = ShaderCacheReader.TocPathFor(
            "assets/shaders/generated/shaders/staticmesh/env_glowsign", DxbcStage.Vertex);
        Assert.Equal("assets/shaders/generated/shaders/staticmesh/env_glowsign.vs.dx11", asked);

        Assert.Equal(
            "assets/shaders/generated/shaders/staticmesh/env_glowsign.vs-dx11",
            ShaderCacheReader.ResolveCachePath(asked, CacheOf(ShippedHyphenated)));
    }

    /// <summary>The step that actually broke. Finding the TOC is not enough: <c>LoadBlob</c> builds the
    /// container name from the path the CALLER passed, so a reader that resolved the TOC by trying both
    /// spellings still asked for <c>....vs.dx11_0</c> and got nothing. Measured on Map12/bloom at the time:
    /// 1,389 of 1,389 TOCs resolved, 1,389 of 1,389 permutations resolved, 0 of 1,389 blobs loaded.</summary>
    [Fact]
    public void TheBlobContainerResolvesSeparatelyFromTheTocRequest()
    {
        string toc = ShaderCacheReader.TocPathFor(
            "assets/shaders/generated/shaders/staticmesh/env_glowsign", DxbcStage.Vertex);

        // exactly the string LoadBlob derives for blob index 0..99
        Assert.Equal(
            "assets/shaders/generated/shaders/staticmesh/env_glowsign.vs-dx11_0",
            ShaderCacheReader.ResolveCachePath($"{toc}_0", CacheOf(ShippedHyphenated)));

        // ...and index 900 lands in the _900 container, which is a different shipped entry
        string ps = ShaderCacheReader.TocPathFor(
            "assets/shaders/generated/shaders/skinnedmesh/pbr_toonshading", DxbcStage.Pixel);
        Assert.Equal(
            "assets/shaders/generated/shaders/skinnedmesh/pbr_toonshading.ps-dx11_900",
            ShaderCacheReader.ResolveCachePath($"{ps}_900", CacheOf(ShippedHyphenated)));
    }

    /// <summary>Support for the OLD spelling is not allowed to fall out either. Mods and older installs ship
    /// it, and swapping to the new name would simply move the outage rather than end it.</summary>
    [Fact]
    public void TheDottedSpellingStillResolvesWhenThatIsWhatIsInstalled()
    {
        string toc = ShaderCacheReader.TocPathFor(
            "assets/shaders/generated/shaders/staticmesh/env_glowsign", DxbcStage.Vertex);
        var dotted = CacheOf(ShippedDotted);

        Assert.Equal(ShippedDotted[0], ShaderCacheReader.ResolveCachePath(toc, dotted));
        Assert.Equal(ShippedDotted[1], ShaderCacheReader.ResolveCachePath($"{toc}_0", dotted));
    }

    /// <summary>An unrecognised layout must say so, not return a path that is not there. Silence is what the
    /// rename exploited.</summary>
    [Fact]
    public void NeitherSpellingPresentIsNullRatherThanAGuess()
    {
        string toc = ShaderCacheReader.TocPathFor("assets/shaders/generated/nope", DxbcStage.Pixel);
        Assert.Null(ShaderCacheReader.ResolveCachePath(toc, CacheOf(ShippedHyphenated)));

        // and the caller can report BOTH names it tried - the diagnostic that was missing
        var tried = ShaderCacheReader.CachePathCandidates($"{toc}_0");
        Assert.Contains("assets/shaders/generated/nope.ps.dx11_0", tried);
        Assert.Contains("assets/shaders/generated/nope.ps-dx11_0", tried);
    }

    /// <summary>The name table. <c>TocPaths</c> filters WAD entries by this test, and when it went empty no
    /// material ever reached a TOC lookup at all - a 256 ms scene build that resolved nothing, which reads
    /// like a scene bug rather than a cache bug. Containers end <c>_N</c> and are not TOCs.</summary>
    [Fact]
    public void ShippedHyphenatedEntriesAreRecognisedAsStageTocs()
    {
        Assert.True(ShaderCacheReader.IsTocPath(
            "assets/shaders/generated/shaders/staticmesh/env_glowsign.vs-dx11"));
        Assert.True(ShaderCacheReader.IsTocPath(
            "assets/shaders/hlsl/particlesystem/distortion_vs.vs-dx11"));
        Assert.True(ShaderCacheReader.IsTocPath(
            "assets/shaders/generated/shaders/skinnedmesh/pbr_toonshading.ps-dx11"));

        Assert.False(ShaderCacheReader.IsTocPath(
            "assets/shaders/generated/shaders/staticmesh/env_glowsign.vs-dx11_0"));
        Assert.False(ShaderCacheReader.IsTocPath("assets/shaders/generated/env_glowsign"));
    }

    /// <summary>Stage detection off a hyphenated path. The old rule was a single <c>.vs.dx11</c> EndsWith
    /// with Pixel as the else, so every hyphenated VERTEX toc would have reported itself as a pixel shader -
    /// a wrong answer rather than a missing one, which is worse.</summary>
    [Theory]
    [InlineData("assets/shaders/hlsl/particlesystem/distortion_vs.vs-dx11",
        "assets/shaders/hlsl/particlesystem/distortion_vs", DxbcStage.Vertex)]
    [InlineData("assets/shaders/generated/shaders/skinnedmesh/pbr_toonshading.ps-dx11",
        "assets/shaders/generated/shaders/skinnedmesh/pbr_toonshading", DxbcStage.Pixel)]
    [InlineData("assets/shaders/generated/shaders/staticmesh/env_glowsign.vs.dx11",
        "assets/shaders/generated/shaders/staticmesh/env_glowsign", DxbcStage.Vertex)]
    [InlineData("assets/shaders/generated/shaders/staticmesh/env_glowsign.ps.dx11",
        "assets/shaders/generated/shaders/staticmesh/env_glowsign", DxbcStage.Pixel)]
    public void StageIsReadFromEitherSeparator(string path, string name, DxbcStage stage)
    {
        Assert.True(ShaderCacheReader.TryStripStage(path, out string got, out var gotStage));
        Assert.Equal(name, got);
        Assert.Equal(stage, gotStage);
        Assert.Equal(name, ShaderCacheReader.StripStage(path));
    }

    /// <summary>A path with no stage suffix is "unknown layout", not "pixel". The distinction is what lets a
    /// caller fail loudly instead of proceeding with a wrong stage.</summary>
    [Fact]
    public void APathWithNoStageSuffixReportsUnknownRatherThanDefaultingToPixel()
    {
        Assert.False(ShaderCacheReader.TryStripStage("shaders/env/foo", out string name, out var stage));
        Assert.Equal("shaders/env/foo", name);
        Assert.Equal(DxbcStage.Unknown, stage);
    }

    /// <summary>Round trip through the whole shipped sample: every hyphenated TOC in the oracle must be
    /// reachable from the canonical request built out of its own stripped name. This is the property the
    /// scene builder depends on, stated once over real data.
    ///
    /// <para>The <c>checked</c> count is not decoration. The loop skips non-TOCs via
    /// <see cref="ShaderCacheReader.IsTocPath"/>, so deleting hyphen support makes that guard reject every
    /// row and the assertions never run - the test would go GREEN on a totally broken reader. Pinning the
    /// count to the 3 TOCs in the oracle is what stops it passing vacuously; verified by removing hyphen
    /// support and watching this test fail on the count rather than sail through.</para></summary>
    [Fact]
    public void EveryShippedTocIsReachableFromItsCanonicalRequest()
    {
        var exists = CacheOf(ShippedHyphenated);
        int checkedTocs = 0;
        foreach (var shipped in ShippedHyphenated)
        {
            if (!ShaderCacheReader.IsTocPath(shipped)) continue;      // containers are handled above
            ShaderCacheReader.TryStripStage(shipped, out string name, out var stage);
            string asked = ShaderCacheReader.TocPathFor(name, stage);
            Assert.Equal(shipped, ShaderCacheReader.ResolveCachePath(asked, exists));
            checkedTocs++;
        }

        // ShippedHyphenated holds 5 entries, 2 of which are _N containers.
        Assert.Equal(3, checkedTocs);
    }
}
