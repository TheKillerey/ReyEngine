using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M590: <c>texturePath</c> is a <c>WadChunkLink</c> from patch 16.17 onward.
///
/// <para>Measured across the installed patch: <b>12,688 of 12,688</b> texturePath properties in 199
/// shipped materials bins are WadChunkLink and <b>zero</b> are String, where the same
/// <c>base_srx.materials.bin</c> was 237 String / 0 link at both 16.14 and 16.15. It is not only
/// textures — <c>mAnimationFilePath</c> is 8,983 link / 0 string, and <c>oldAsset</c> 3,432 / 0.</para>
///
/// <para>Reading only the String form meant every material on the current patch came back with ZERO
/// texture slots — nothing in the viewport, nothing in the material browser, and the raw bin editor
/// printing the literal word "WadChunkLink" where the path belonged.</para>
///
/// <para>Both forms stay supported: a project can hold bins from either era, and rewriting one into the
/// other would break the client shipped alongside it.</para>
/// </summary>
public sealed class WadChunkLinkTexturePathTests
{
    private const string Map11 = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
    private const string BaseSrx = "data/maps/mapgeometry/map11/base_srx.materials.bin";
    private const string SomeTexture = "assets/maps/kitpieces/srs/base/textures/periph_bot_a_1bitalpha.tex";

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    // ---- the primitive ---------------------------------------------------------------------------

    [Fact]
    public void AWadChunkLinkReadsAsItsResolvedPath()
    {
        var link = new BinTreeWadChunkLink(H("texturePath"), HashAlgorithms.WadPath(SomeTexture));
        Assert.Equal(SomeTexture, BinTexturePath.Read(link, h => h == HashAlgorithms.WadPath(SomeTexture) ? SomeTexture : null));
    }

    [Fact]
    public void AnUnknownHashStaysHexAndSurvivesARoundTrip()
    {
        // The honest outcome: we genuinely do not know the path. It must not read as empty (which looks
        // like "no texture") and an edit elsewhere must not destroy it.
        var link = new BinTreeWadChunkLink(H("texturePath"), 0xdeadbeefcafef00dUL);
        string shown = BinTexturePath.Read(link, _ => null);
        Assert.Equal("0xdeadbeefcafef00d", shown);

        BinTexturePath.Write(link, shown);
        Assert.Equal(0xdeadbeefcafef00dUL, link.Value);
    }

    [Fact]
    public void WritingAPathStoresTheWadHash()
    {
        var link = new BinTreeWadChunkLink(H("texturePath"), 0);
        BinTexturePath.Write(link, SomeTexture);
        Assert.Equal(HashAlgorithms.WadPath(SomeTexture), link.Value);
    }

    [Fact]
    public void CaseDoesNotChangeTheHash()
    {
        // Riot's 16.15 strings were mixed case and hash to the same chunk as the lowercase 16.17 links.
        var a = new BinTreeWadChunkLink(H("texturePath"), 0);
        var b = new BinTreeWadChunkLink(H("texturePath"), 0);
        BinTexturePath.Write(a, "ASSETS/Maps/KitPieces/SRS/Base/Textures/Periph_Bot_A_1bitalpha.tex");
        BinTexturePath.Write(b, SomeTexture);
        Assert.Equal(a.Value, b.Value);
    }

    [Fact]
    public void TheStringFormIsPreservedRatherThanMigrated()
    {
        // A pre-16.17 bin must keep writing strings, or the client that ships with it reads nothing.
        var str = new BinTreeString(H("texturePath"), "assets/old.tex");
        Assert.True(BinTexturePath.Write(str, SomeTexture));
        Assert.IsType<BinTreeString>(str);
        Assert.Equal(SomeTexture, str.Value);
    }

    [Fact]
    public void BothFormsAgreeOnTheChunkTheyName()
    {
        var str = new BinTreeString(H("texturePath"), SomeTexture);
        var link = new BinTreeWadChunkLink(H("texturePath"), HashAlgorithms.WadPath(SomeTexture));
        Assert.Equal(BinTexturePath.HashOf(str), BinTexturePath.HashOf(link));
        Assert.True(BinTexturePath.SameTarget(str, link));
    }

    [Fact]
    public void ABareSixteenDigitTokenIsAPathNotAHash()
    {
        // Hash-named mod files look like this. Treating one as a raw hash would silently repoint the
        // texture at whatever that value happens to address.
        var link = new BinTreeWadChunkLink(H("texturePath"), 0);
        BinTexturePath.Write(link, "84541dd835d2c63a.tex");
        Assert.Equal(HashAlgorithms.WadPath("84541dd835d2c63a.tex"), link.Value);
        Assert.NotEqual(0x84541dd835d2c63aUL, link.Value);
    }

    // ---- the raw bin editor (what the report showed) ----------------------------------------------

    [Fact]
    public void TheBinEditorShowsAPathInsteadOfTheTypeName()
    {
        var link = new BinTreeWadChunkLink(H("texturePath"), HashAlgorithms.WadPath(SomeTexture));
        Assert.Equal(BinValueKind.WadPath, BinValueEditor.KindOf(link));
        Assert.Equal(SomeTexture, BinValueEditor.Format(link, _ => null,
            h => h == HashAlgorithms.WadPath(SomeTexture) ? SomeTexture : null));

        // and it is editable, which it was not while it fell through to ReadOnly
        BinValueEditor.Apply(link, "assets/other.tex");
        Assert.Equal(HashAlgorithms.WadPath("assets/other.tex"), link.Value);
    }

    // ---- against what Riot actually ships ---------------------------------------------------------

    private static byte[]? ShippedBaseSrx()
    {
        if (!File.Exists(Map11)) return null;
        using var wad = WadArchive.Open(Map11);
        ulong h = HashAlgorithms.WadPath(BaseSrx);
        return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
    }

    [Fact]
    public void EveryMaterialInAShippedBinHasItsTextureSlots()
    {
        if (ShippedBaseSrx() is not { } bytes) return;   // no game install
        var doc = MaterialDocument.Parse(bytes, _ => null);

        // The regression: this was 0 on 16.17 because the reader only accepted BinTreeString.
        int slots = doc.Materials.Sum(m => m.Slots.Count);
        Assert.True(slots > 100, $"expected the shipped bin's texture slots to be read, got {slots}");
        Assert.All(doc.Materials.SelectMany(m => m.Slots), s => Assert.NotEqual(0UL, s.ChunkHash));
    }

    /// <summary>A resolver that can name every chunk, so this measures the PLUMBING rather than how
    /// complete the machine's hash dictionary happens to be.</summary>
    private static string? NameEverything(ulong h) => $"assets/synthetic/{h:x16}.tex";

    [Fact]
    public void AResolverTurnsThoseSlotsIntoRealPaths()
    {
        if (ShippedBaseSrx() is not { } bytes) return;

        var doc = MaterialDocument.Parse(bytes, _ => null, NameEverything);
        var slots = doc.Materials.SelectMany(m => m.Slots).ToList();
        Assert.NotEmpty(slots);

        // Given a resolver, nothing may come back as hex — hex means the link was never resolved.
        Assert.All(slots, s =>
        {
            Assert.False(s.Path.StartsWith("0x", StringComparison.Ordinal), s.Path);
            Assert.Equal($"assets/synthetic/{s.ChunkHash:x16}.tex", s.Path);
        });

        // Without one, the same slots report hex — visible and round-trippable, never silently empty.
        var blind = MaterialDocument.Parse(bytes, _ => null);
        Assert.All(blind.Materials.SelectMany(m => m.Slots),
            s => Assert.StartsWith("0x", s.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void EditingAShippedSlotKeepsTheLinkFormAndRoundTrips()
    {
        if (ShippedBaseSrx() is not { } bytes) return;
        var doc = MaterialDocument.Parse(bytes, _ => null);
        var slot = doc.Materials.SelectMany(m => m.Slots).First();
        Assert.True(slot.IsWadChunkLink, "16.17 ships the link form");

        slot.SetPath(SomeTexture);
        Assert.Equal(HashAlgorithms.WadPath(SomeTexture), slot.ChunkHash);

        var back = MaterialDocument.Parse(doc.Serialize(), _ => null)
            .Materials.SelectMany(m => m.Slots).First();
        Assert.True(back.IsWadChunkLink);
        Assert.Equal(HashAlgorithms.WadPath(SomeTexture), back.ChunkHash);
    }

    [Fact]
    public void TheViewportResolverFindsADiffuseForShippedMaterials()
    {
        if (ShippedBaseSrx() is not { } bytes) return;
        var names = MaterialDocument.Parse(bytes, _ => null).Materials.Select(m => m.Name).ToList();

        var resolved = MapGeoMaterialResolver.Resolve(bytes, names, NameEverything);

        // Was 0 on 16.17: the resolver cast texturePath to BinTreeString and skipped every sampler.
        Assert.True(resolved.Count > 100, $"expected diffuse textures for most materials, got {resolved.Count}");
        Assert.All(resolved.Values, v => Assert.EndsWith(".tex", v, StringComparison.OrdinalIgnoreCase));
    }
}
