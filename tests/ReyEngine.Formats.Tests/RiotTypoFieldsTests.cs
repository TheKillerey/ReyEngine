using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M198 (tier 5.3): Riot ships two misspelled VFX field names, and any writer that "helpfully" corrects
/// them silently corrupts the bin - the game looks up the MISSPELLED hash and would find nothing.
///
/// <para>The names are <c>palleteSrcMixColor</c> (16,439 occurrences on VfxPaletteDefinitionData - "pallete"
/// for "palette") and <c>TextureMultFilpU</c>/<c>V</c> ("Filp" for "Flip"). Because .bin stores a 32-bit
/// FNV-1a of the name rather than the name itself, a correction is not cosmetic: it writes a different key.</para>
///
/// <para>These tests are synthetic on purpose. They build a tree, write it, read it back and compare, so
/// they need no game install and cannot be weakened by whatever happens to be in one.</para>
/// </summary>
public class RiotTypoFieldsTests
{
    // Measured with the project's own FNV-1a; asserted below so a hashing change cannot pass silently.
    private const uint PalleteSrcMixColor = 0x143f06d1;   // Riot's spelling, the one that ships
    private const uint PaletteSrcMixColor = 0x8af10fa7;   // the "corrected" spelling - a DIFFERENT key
    private const uint TextureMultFilpU = 0x39123dda;
    private const uint TextureMultFilpV = 0x38123c47;
    private const uint TextureMultFlipU = 0x38b0b8cc;
    private const uint TextureMultFlipV = 0x3bb0bd85;

    [Fact]
    public void CorrectingTheTypoProducesADifferentHash()
    {
        // This is the whole reason 5.3 exists. If these were equal, a spelling fix would be harmless.
        Assert.Equal(PalleteSrcMixColor, HashAlgorithms.Fnv1a("palleteSrcMixColor"));
        Assert.Equal(PaletteSrcMixColor, HashAlgorithms.Fnv1a("paletteSrcMixColor"));
        Assert.NotEqual(HashAlgorithms.Fnv1a("palleteSrcMixColor"), HashAlgorithms.Fnv1a("paletteSrcMixColor"));

        Assert.Equal(TextureMultFilpU, HashAlgorithms.Fnv1a("TextureMultFilpU"));
        Assert.Equal(TextureMultFilpV, HashAlgorithms.Fnv1a("TextureMultFilpV"));
        Assert.NotEqual(HashAlgorithms.Fnv1a("TextureMultFilpU"), HashAlgorithms.Fnv1a("TextureMultFlipU"));
        Assert.NotEqual(HashAlgorithms.Fnv1a("TextureMultFilpV"), HashAlgorithms.Fnv1a("TextureMultFlipV"));
    }

    [Fact]
    public void TypoFieldsSurviveATreeRoundTripUnchanged()
    {
        var written = RoundTrip(BuildTreeWithTypoFields());

        var palette = written.Objects.Values.Single(o => o.ClassHash == HashAlgorithms.Fnv1a("VfxPaletteDefinitionData"));
        Assert.True(palette.Properties.ContainsKey(PalleteSrcMixColor),
            "palleteSrcMixColor (Riot's spelling) did not survive the round trip");
        Assert.False(palette.Properties.ContainsKey(PaletteSrcMixColor),
            "the writer invented the CORRECTED spelling - the game would not find this field");
        var colour = Assert.IsType<BinTreeVector4>(palette.Properties[PalleteSrcMixColor]);
        Assert.Equal(new Vector4(0.25f, 0.5f, 0.75f, 1f), colour.Value);

        var mult = written.Objects.Values.Single(o => o.ClassHash == HashAlgorithms.Fnv1a("VfxTextureMultDefinitionData"));
        Assert.True(mult.Properties.ContainsKey(TextureMultFilpU), "TextureMultFilpU did not survive");
        Assert.True(mult.Properties.ContainsKey(TextureMultFilpV), "TextureMultFilpV did not survive");
        Assert.False(mult.Properties.ContainsKey(TextureMultFlipU), "the writer invented TextureMultFlipU");
        Assert.False(mult.Properties.ContainsKey(TextureMultFlipV), "the writer invented TextureMultFlipV");
        Assert.True(((BinTreeBool)mult.Properties[TextureMultFilpU]).Value);
        Assert.False(((BinTreeBool)mult.Properties[TextureMultFilpV]).Value);
    }

    [Fact]
    public void TypoFieldsSurviveASecondRoundTripByteForByte()
    {
        // Once through the writer is the interesting hop; twice proves the output is a fixed point, which is
        // what makes repeated Save Override runs safe.
        var first = Write(BuildTreeWithTypoFields());
        var second = Write(Read(first));
        Assert.Equal(first, second);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static BinTree BuildTreeWithTypoFields()
    {
        var palette = new BinTreeObject(0x1001u, HashAlgorithms.Fnv1a("VfxPaletteDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeVector4(PalleteSrcMixColor, new Vector4(0.25f, 0.5f, 0.75f, 1f)),
            new BinTreeString(HashAlgorithms.Fnv1a("paletteTexture"), "ASSETS/Test/pal.tex"),
            new BinTreeI32(HashAlgorithms.Fnv1a("paletteCount"), 4),
        });
        var mult = new BinTreeObject(0x1002u, HashAlgorithms.Fnv1a("VfxTextureMultDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeBool(TextureMultFilpU, true),
            new BinTreeBool(TextureMultFilpV, false),
            new BinTreeString(HashAlgorithms.Fnv1a("textureMult"), "ASSETS/Test/mult.tex"),
        });
        return new BinTree(new[] { palette, mult }, Array.Empty<string>());
    }

    private static byte[] Write(BinTree tree)
    {
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static BinTree Read(byte[] bytes) => new(new MemoryStream(bytes, false));

    private static BinTree RoundTrip(BinTree tree) => Read(Write(tree));
}
