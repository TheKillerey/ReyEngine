using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M590: a rebased mod must speak the target patch's wire form, not the one it was authored on.
///
/// <para>The three-way merge carries a mod's own objects across verbatim, so rebasing a 16.10 map onto
/// 16.17 left one bin holding two eras — 1,029 String texturePaths beside 254 WadChunkLinks inherited
/// from Riot's untouched objects. The client skips a property whose wire form disagrees with the schema
/// rather than reporting it (M507), so those materials lose their textures silently: a rebase that looks
/// clean and ships broken.</para>
///
/// <para>The field list is derived from the patch's own copy of the bin rather than hardcoded, because
/// some fields are genuinely both — measured on the installed patch, <c>texturePath</c> is 11,695 links
/// and zero strings while <c>texture</c> is 2,863 links against 70,402 strings.</para>
/// </summary>
public sealed class BinAssetLinkMigrationTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private const string TexA = "assets/maps/a.tex";
    private const string TexB = "assets/maps/b.tex";

    /// <summary>One object with a samplerValues container holding one sampler.</summary>
    private static BinTree Tree(Func<uint, BinTreeProperty> texturePath, params BinTreeProperty[] extra)
    {
        var sampler = new BinTreeEmbedded(0, 0x0904b150, new[]
        {
            (BinTreeProperty)new BinTreeString(H("TextureName"), "DiffuseTexture"),
            texturePath(H("texturePath")),
        });
        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("name"), "Mat"),
            new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded, new[] { (BinTreeProperty)sampler }),
        };
        props.AddRange(extra);
        return new BinTree(new[] { new BinTreeObject(H("Mat"), H("StaticMaterialDef"), props) }, Array.Empty<string>());
    }

    private static BinTreeProperty? FindTexturePath(BinTree tree)
    {
        foreach (var o in tree.Objects.Values)
            foreach (var p in o.Properties.Values)
                if (p is BinTreeContainer c)
                    foreach (var el in c.Elements)
                        if (el is BinTreeStruct s && s.Properties.TryGetValue(H("texturePath"), out var tp))
                            return tp;
        return null;
    }

    [Fact]
    public void AStringOnALinkOnlyFieldBecomesALink()
    {
        var reference = Tree(h => new BinTreeWadChunkLink(h, HashAlgorithms.WadPath(TexB)));
        var mine = Tree(h => new BinTreeString(h, TexA));

        Assert.Equal(1, BinAssetLinkMigration.AlignWith(mine, reference));

        var back = Assert.IsType<BinTreeWadChunkLink>(FindTexturePath(mine));
        Assert.Equal(HashAlgorithms.WadPath(TexA), back.Value);
    }

    [Fact]
    public void TheHashMeansExactlyWhatTheStringMeant()
    {
        // The conversion must be meaning-preserving: WadPath(the old string) is what the loader derived
        // from that string anyway.
        var reference = Tree(h => new BinTreeWadChunkLink(h, 1));
        var mine = Tree(h => new BinTreeString(h, "ASSETS/Maps/KitPieces/Mixed_Case.tex"));

        BinAssetLinkMigration.AlignWith(mine, reference);

        var back = Assert.IsType<BinTreeWadChunkLink>(FindTexturePath(mine));
        Assert.Equal(HashAlgorithms.WadPath("assets/maps/kitpieces/mixed_case.tex"), back.Value);
    }

    [Fact]
    public void AFieldThePatchWritesBothWaysIsLeftAlone()
    {
        // `texture` really is both on the live patch (2,863 links / 70,402 strings). Converting it would
        // be guessing, and guessing wrong repoints an asset.
        var reference = new BinTree(new[]
        {
            new BinTreeObject(H("A"), H("X"), new BinTreeProperty[] { new BinTreeWadChunkLink(H("texture"), 7) }),
            new BinTreeObject(H("B"), H("X"), new BinTreeProperty[] { new BinTreeString(H("texture"), TexB) }),
        }, Array.Empty<string>());

        var mine = new BinTree(new[]
        {
            new BinTreeObject(H("C"), H("X"), new BinTreeProperty[] { new BinTreeString(H("texture"), TexA) }),
        }, Array.Empty<string>());

        Assert.Equal(0, BinAssetLinkMigration.AlignWith(mine, reference));
        Assert.IsType<BinTreeString>(mine.Objects.Values.Single().Properties[H("texture")]);
    }

    [Fact]
    public void AFieldThePatchOnlyWritesAsAStringIsLeftAlone()
    {
        var reference = Tree(h => new BinTreeString(h, TexB));
        var mine = Tree(h => new BinTreeString(h, TexA));

        Assert.Equal(0, BinAssetLinkMigration.AlignWith(mine, reference));
        Assert.IsType<BinTreeString>(FindTexturePath(mine));
    }

    [Fact]
    public void AlreadyMigratedContentIsNotTouchedTwice()
    {
        var reference = Tree(h => new BinTreeWadChunkLink(h, 1));
        var mine = Tree(h => new BinTreeWadChunkLink(h, HashAlgorithms.WadPath(TexA)));

        Assert.Equal(0, BinAssetLinkMigration.AlignWith(mine, reference));
        Assert.Equal(HashAlgorithms.WadPath(TexA),
            Assert.IsType<BinTreeWadChunkLink>(FindTexturePath(mine)).Value);
    }

    [Fact]
    public void TheResultStillRoundTrips()
    {
        var reference = Tree(h => new BinTreeWadChunkLink(h, 1));
        var mine = Tree(h => new BinTreeString(h, TexA));
        BinAssetLinkMigration.AlignWith(mine, reference);

        using var ms = new MemoryStream();
        mine.Write(ms);
        var back = SafeBinTree.Parse(ms.ToArray());
        Assert.Equal(HashAlgorithms.WadPath(TexA),
            Assert.IsType<BinTreeWadChunkLink>(FindTexturePath(back)).Value);
    }

    [Fact]
    public void TheMergeDoesItWithoutBeingAsked()
    {
        // The whole point: a rebase must not leave the mod speaking the old form.
        byte[] Bytes(BinTree t) { using var ms = new MemoryStream(); t.Write(ms); return ms.ToArray(); }

        byte[] oldBase = Bytes(Tree(h => new BinTreeString(h, TexB)));
        byte[] mod = Bytes(Tree(h => new BinTreeString(h, TexA)));          // the mod's edit, old form
        byte[] newBase = Bytes(Tree(h => new BinTreeWadChunkLink(h, HashAlgorithms.WadPath(TexB))));

        var (merged, report) = BinThreeWayMerge.Merge(oldBase, mod, newBase);

        Assert.True(report.Relinked > 0, "the merge should have rewritten the mod's String onto the link form");
        var back = Assert.IsType<BinTreeWadChunkLink>(FindTexturePath(SafeBinTree.Parse(merged)));
        Assert.Equal(HashAlgorithms.WadPath(TexA), back.Value);   // the MOD's texture, in the patch's form
    }

    [Fact]
    public void AMergeOntoAnOldStyleBaseChangesNothing()
    {
        // Rebasing between two pre-16.17 patches must not start rewriting forms.
        byte[] Bytes(BinTree t) { using var ms = new MemoryStream(); t.Write(ms); return ms.ToArray(); }

        byte[] oldBase = Bytes(Tree(h => new BinTreeString(h, TexB)));
        byte[] mod = Bytes(Tree(h => new BinTreeString(h, TexA)));
        byte[] newBase = Bytes(Tree(h => new BinTreeString(h, TexB)));

        var (merged, report) = BinThreeWayMerge.Merge(oldBase, mod, newBase);

        Assert.Equal(0, report.Relinked);
        Assert.IsType<BinTreeString>(FindTexturePath(SafeBinTree.Parse(merged)));
    }
}
