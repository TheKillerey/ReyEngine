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
/// and zero strings. It is also keyed by the class that OWNS the field, not the name alone:
/// <c>SkinMeshDataProperties.texture</c> is link-only while <c>VfxEmitterDefinitionData.texture</c> is
/// still a String, and any skin bin with both (real cases: sru_blue skin4, sru_red skin3, three drakes,
/// baron) saw "texture" written both ways under name-only keying and never converted its own texture —
/// a pure white model in game. An Optional field (<c>iconCircle</c>/<c>iconSquare</c>) is a further trap:
/// its payload is always read back with NameHash 0, so a survey of the payload alone never finds it.</para>
///
/// <para>Three follow-ups on the per-class fix itself: a class the reference never mentions at all falls
/// back to the old name-only verdict (there is no per-class evidence either way); an EMPTY Optional keeps
/// a stale element type unless it is explicitly retyped (BinTreeCloner's M756 mechanism, reused); and a
/// List/File container's elements carry no name of their own, so it is surveyed by its own (class, name)
/// and its declared ElementType instead.</para>
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

    // ---- per-class calibration (the sru_blue/sru_red/dragon skin4/skin3/skin0 bug) -----------------

    /// <summary>One skin bin holding a <c>SkinCharacterDataProperties</c> (link-only <c>texture</c> since
    /// 16.17) beside a <c>VfxSystemDefinitionData</c> (whose emitters' <c>texture</c> is still a String).
    /// Real failing bins: sru_blue/skins/skin4.bin, sru_red/skins/skin3.bin, the earth/hextech/chemtech
    /// dragon skin0 bins — every one a skin bin that also carries particle systems.</summary>
    private static BinTree SkinAndVfxBin(Func<uint, BinTreeProperty> skinTexture, Func<uint, BinTreeProperty> vfxTexture)
    {
        var skinMesh = new BinTreeEmbedded(H("skinMeshProperties"), H("SkinMeshDataProperties"),
            new[] { skinTexture(H("texture")) });
        var skin = new BinTreeObject(H("Skin"), H("SkinCharacterDataProperties"), new BinTreeProperty[] { skinMesh });

        var emitter = new BinTreeEmbedded(0, H("VfxEmitterDefinitionData"), new[] { vfxTexture(H("texture")) });
        var emitters = new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Embedded,
            new BinTreeProperty[] { emitter });
        var vfx = new BinTreeObject(H("Vfx"), H("VfxSystemDefinitionData"), new BinTreeProperty[] { emitters });

        return new BinTree(new[] { skin, vfx }, Array.Empty<string>());
    }

    [Fact]
    public void ASkinTextureConvertsEvenWhenAVfxEmitterInTheSameBinKeepsTheSameFieldNameAsAString()
    {
        var reference = SkinAndVfxBin(
            h => new BinTreeWadChunkLink(h, HashAlgorithms.WadPath(TexB)),
            h => new BinTreeString(h, TexB));
        var mine = SkinAndVfxBin(
            h => new BinTreeString(h, TexA),
            h => new BinTreeString(h, TexA));

        Assert.Equal(1, BinAssetLinkMigration.AlignWith(mine, reference));

        var skinMesh = (BinTreeStruct)mine.Objects[H("Skin")].Properties[H("skinMeshProperties")];
        var skinTexture = Assert.IsType<BinTreeWadChunkLink>(skinMesh.Properties[H("texture")]);
        Assert.Equal(HashAlgorithms.WadPath(TexA), skinTexture.Value);

        var emitters = (BinTreeContainer)mine.Objects[H("Vfx")].Properties[H("complexEmitterDefinitionData")];
        var emitterStruct = (BinTreeStruct)emitters.Elements[0];
        Assert.IsType<BinTreeString>(emitterStruct.Properties[H("texture")]);
    }

    [Fact]
    public void AnOptionalWrappedStringConvertsUnderTheOptionalsOwnName()
    {
        // iconCircle/iconSquare are Optional<File>; LeagueToolkit always reads the payload back with
        // NameHash 0, so a survey that looked at the payload's own name (rather than the Optional's) would
        // never see this field — 24 leftover icon Strings in one mod, 432 in another.
        static BinTree Bin(Func<uint, BinTreeProperty> icon) => new(new[]
        {
            new BinTreeObject(H("Skin"), H("SkinCharacterDataProperties"),
                new BinTreeProperty[] { new BinTreeOptional(H("iconCircle"), icon(0)) }),
        }, Array.Empty<string>());

        var reference = Bin(nameHash => new BinTreeWadChunkLink(nameHash, HashAlgorithms.WadPath(TexB)));
        var mine = Bin(nameHash => new BinTreeString(nameHash, TexA));

        Assert.Equal(1, BinAssetLinkMigration.AlignWith(mine, reference));

        var optional = Assert.IsType<BinTreeOptional>(mine.Objects[H("Skin")].Properties[H("iconCircle")]);
        var link = Assert.IsType<BinTreeWadChunkLink>(optional.Value);
        Assert.Equal(HashAlgorithms.WadPath(TexA), link.Value);
        Assert.Equal(BinPropertyType.WadChunkLink, optional.ValueType);

        // The Optional's element type must actually flip to WadChunkLink on the wire, or an empty icon
        // written later would still declare itself String and get skipped all over again.
        using var ms = new MemoryStream();
        mine.Write(ms);
        var back = SafeBinTree.Parse(ms.ToArray());
        var backOptional = Assert.IsType<BinTreeOptional>(back.Objects[H("Skin")].Properties[H("iconCircle")]);
        Assert.Equal(BinPropertyType.WadChunkLink, backOptional.ValueType);
        Assert.Equal(HashAlgorithms.WadPath(TexA), Assert.IsType<BinTreeWadChunkLink>(backOptional.Value).Value);
    }

    [Fact]
    public void TheSameFieldNameOnTwoClassesConvertsIndependently()
    {
        // ClassY.texture is genuinely both in the reference (left alone, same as
        // AFieldThePatchWritesBothWaysIsLeftAlone above); ClassZ.texture is link-only there. Sharing a
        // field name must not let one class's ambiguity block — or one class's certainty force — the other.
        var reference = new BinTree(new[]
        {
            new BinTreeObject(H("Y1"), H("ClassY"), new BinTreeProperty[] { new BinTreeWadChunkLink(H("texture"), 1) }),
            new BinTreeObject(H("Y2"), H("ClassY"), new BinTreeProperty[] { new BinTreeString(H("texture"), TexB) }),
            new BinTreeObject(H("Z"), H("ClassZ"), new BinTreeProperty[] { new BinTreeWadChunkLink(H("texture"), 2) }),
        }, Array.Empty<string>());

        var mine = new BinTree(new[]
        {
            new BinTreeObject(H("Y3"), H("ClassY"), new BinTreeProperty[] { new BinTreeString(H("texture"), TexA) }),
            new BinTreeObject(H("Z2"), H("ClassZ"), new BinTreeProperty[] { new BinTreeString(H("texture"), TexA) }),
        }, Array.Empty<string>());

        Assert.Equal(1, BinAssetLinkMigration.AlignWith(mine, reference));
        Assert.IsType<BinTreeString>(mine.Objects[H("Y3")].Properties[H("texture")]);
        Assert.IsType<BinTreeWadChunkLink>(mine.Objects[H("Z2")].Properties[H("texture")]);
    }

    // ---- class absent from the reference: name-only fallback ---------------------------------------

    [Fact]
    public void AModOnlyClassFallsBackToTheNameLevelVerdictWhenUnambiguous()
    {
        // texturePath is link-only game-wide (11,695 links / 0 strings on the installed patch). A mod can
        // add an object of a class this particular reference bin never happens to mention at all - there is
        // no per-class evidence for it either way, so this falls back to the name-only verdict, exactly
        // what the pre-per-class code did.
        var reference = new BinTree(new[]
        {
            new BinTreeObject(H("K"), H("KnownClass"), new BinTreeProperty[] { new BinTreeWadChunkLink(H("texturePath"), 1) }),
        }, Array.Empty<string>());

        var mine = new BinTree(new[]
        {
            new BinTreeObject(H("M"), H("ModOnlyClass"), new BinTreeProperty[] { new BinTreeString(H("texturePath"), TexA) }),
        }, Array.Empty<string>());

        Assert.Equal(1, BinAssetLinkMigration.AlignWith(mine, reference));
        var link = Assert.IsType<BinTreeWadChunkLink>(mine.Objects[H("M")].Properties[H("texturePath")]);
        Assert.Equal(HashAlgorithms.WadPath(TexA), link.Value);
    }

    [Fact]
    public void AModOnlyClassWithAnAmbiguousNameStaysAString()
    {
        // Same absent-class fallback, but the field NAME itself is ambiguous game-wide (like "texture") -
        // the fallback must not guess just because there is no per-class evidence.
        var reference = new BinTree(new[]
        {
            new BinTreeObject(H("A"), H("KnownClassA"), new BinTreeProperty[] { new BinTreeWadChunkLink(H("texture"), 1) }),
            new BinTreeObject(H("B"), H("KnownClassB"), new BinTreeProperty[] { new BinTreeString(H("texture"), TexB) }),
        }, Array.Empty<string>());

        var mine = new BinTree(new[]
        {
            new BinTreeObject(H("M"), H("ModOnlyClass"), new BinTreeProperty[] { new BinTreeString(H("texture"), TexA) }),
        }, Array.Empty<string>());

        Assert.Equal(0, BinAssetLinkMigration.AlignWith(mine, reference));
        Assert.IsType<BinTreeString>(mine.Objects[H("M")].Properties[H("texture")]);
    }

    [Fact]
    public void ADragonStyleReferenceWithOneTextureOwnerDoesNotConvertAModEmittersTexture()
    {
        // A skin0.bin with no VFX shows "texture" link-only under SkinMeshDataProperties alone. A mod-added
        // VfxEmitterDefinitionData.texture (a String in the schema) is a class the reference never mentions;
        // one owner is not evidence for it.
        var reference = new BinTree(new[]
        {
            new BinTreeObject(H("Skin"), H("SkinMeshDataProperties"), new BinTreeProperty[] { new BinTreeWadChunkLink(H("texture"), 1) }),
        }, Array.Empty<string>());
        var mine = new BinTree(new[]
        {
            new BinTreeObject(H("E"), H("VfxEmitterDefinitionData"), new BinTreeProperty[] { new BinTreeString(H("texture"), TexA) }),
        }, Array.Empty<string>());

        Assert.Equal(0, BinAssetLinkMigration.AlignWith(mine, reference));
        Assert.IsType<BinTreeString>(mine.Objects[H("E")].Properties[H("texture")]);
    }

    [Fact]
    public void ANameLinkOnlyUnderTwoClassesConvertsForAClassTheReferenceNeverMentions()
    {
        var reference = new BinTree(new[]
        {
            new BinTreeObject(H("A"), H("ClassA"), new BinTreeProperty[] { new BinTreeWadChunkLink(H("someIcon"), 1) }),
            new BinTreeObject(H("B"), H("ClassB"), new BinTreeProperty[] { new BinTreeWadChunkLink(H("someIcon"), 2) }),
        }, Array.Empty<string>());
        var mine = new BinTree(new[]
        {
            new BinTreeObject(H("M"), H("ModOnlyClass"), new BinTreeProperty[] { new BinTreeString(H("someIcon"), TexA) }),
        }, Array.Empty<string>());

        Assert.Equal(1, BinAssetLinkMigration.AlignWith(mine, reference));
        Assert.IsType<BinTreeWadChunkLink>(mine.Objects[H("M")].Properties[H("someIcon")]);
    }

    // ---- empty Optionals keep a stale element type ---------------------------------------------------

    [Fact]
    public void AnEmptyOptionalIsRetypedToWadChunkLinkAndSurvivesARoundTrip()
    {
        // iconCircle/iconSquare are commonly unset (no override icon). BinTreeCloner's own precedent (M756):
        // an empty Optional's element TYPE is itself part of the wire form and must not go stale, or the
        // client sees Optional<String> where the schema now says Optional<File>.
        var reference = new BinTree(new[]
        {
            new BinTreeObject(H("Skin"), H("SkinCharacterDataProperties"), new BinTreeProperty[]
                { new BinTreeOptional(H("iconCircle"), new BinTreeWadChunkLink(0, HashAlgorithms.WadPath(TexB))) }),
        }, Array.Empty<string>());

        byte[] Bytes(BinTree t) { using var ms = new MemoryStream(); t.Write(ms); return ms.ToArray(); }

        var mine = new BinTree(new[]
        {
            new BinTreeObject(H("Skin"), H("SkinCharacterDataProperties"), new BinTreeProperty[]
                { new BinTreeOptional(H("iconCircle"), new BinTreeString(0, TexA)) }),
        }, Array.Empty<string>());
        // The only public way to an EMPTY option that still declares its element type (M756): write a SET
        // one, read it back, then clear its value.
        mine = SafeBinTree.Parse(Bytes(mine));
        var before = (BinTreeOptional)mine.Objects[H("Skin")].Properties[H("iconCircle")];
        before.Value = null;
        Assert.Equal(BinPropertyType.String, before.ValueType);   // still declares String, just unset

        Assert.Equal(1, BinAssetLinkMigration.AlignWith(mine, reference));

        var after = Assert.IsType<BinTreeOptional>(mine.Objects[H("Skin")].Properties[H("iconCircle")]);
        Assert.Null(after.Value);
        Assert.Equal(BinPropertyType.WadChunkLink, after.ValueType);

        using var ms = new MemoryStream();
        mine.Write(ms);
        var back = SafeBinTree.Parse(ms.ToArray());
        var backOptional = Assert.IsType<BinTreeOptional>(back.Objects[H("Skin")].Properties[H("iconCircle")]);
        Assert.Null(backOptional.Value);
        Assert.Equal(BinPropertyType.WadChunkLink, backOptional.ValueType);
    }

    [Fact]
    public void AModOnlyClassContainerFieldGetsNoNameOnlyFallbackWhileItsScalarFieldDoes()
    {
        // Containers get no name-only fallback, by design: the (class, field) pair for a container must be
        // established directly by THAT class in the reference. KnownClass.pathList is link-only there, and
        // "pathList" would be an unambiguous name-only verdict too if containers used one - but they don't,
        // so ModOnlyClass's absence from the reference leaves its own pathList untouched. The scalar sibling
        // on the SAME absent class, in the SAME bin, still falls back (as in the tests above).
        var reference = new BinTree(new[]
        {
            new BinTreeObject(H("K"), H("KnownClass"), new BinTreeProperty[]
            {
                new BinTreeWadChunkLink(H("texturePath"), 1),
                new BinTreeContainer(H("pathList"), BinPropertyType.WadChunkLink,
                    new BinTreeProperty[] { new BinTreeWadChunkLink(0, 2) }),
            }),
        }, Array.Empty<string>());

        var mine = new BinTree(new[]
        {
            new BinTreeObject(H("M"), H("ModOnlyClass"), new BinTreeProperty[]
            {
                new BinTreeString(H("texturePath"), TexA),
                new BinTreeContainer(H("pathList"), BinPropertyType.String,
                    new BinTreeProperty[] { new BinTreeString(0, TexA) }),
            }),
        }, Array.Empty<string>());

        Assert.Equal(1, BinAssetLinkMigration.AlignWith(mine, reference));   // the scalar only

        var scalar = Assert.IsType<BinTreeWadChunkLink>(mine.Objects[H("M")].Properties[H("texturePath")]);
        Assert.Equal(HashAlgorithms.WadPath(TexA), scalar.Value);

        var container = Assert.IsType<BinTreeContainer>(mine.Objects[H("M")].Properties[H("pathList")]);
        Assert.Equal(BinPropertyType.String, container.ElementType);
        Assert.IsType<BinTreeString>(Assert.Single(container.Elements));
    }

    [Fact]
    public void AnEmptyContainerIsRetypedToWadChunkLinkAndSurvivesARoundTrip()
    {
        // An empty List<String> still declares its element type on the wire, the same trap as an empty
        // Optional (M756). ConvertContainer's counting treats retyping an otherwise-untouched empty
        // container as 1 converted property - there is nothing else to count, but the wire form did change.
        var reference = new BinTree(new[]
        {
            new BinTreeObject(H("Ref"), H("SomeClass"), new BinTreeProperty[]
                { new BinTreeContainer(H("pathList"), BinPropertyType.WadChunkLink, new BinTreeProperty[] { new BinTreeWadChunkLink(0, 1) }) }),
        }, Array.Empty<string>());

        var mine = new BinTree(new[]
        {
            new BinTreeObject(H("Mine"), H("SomeClass"), new BinTreeProperty[]
                { new BinTreeContainer(H("pathList"), BinPropertyType.String, Array.Empty<BinTreeProperty>()) }),
        }, Array.Empty<string>());

        Assert.Equal(1, BinAssetLinkMigration.AlignWith(mine, reference));

        var container = Assert.IsType<BinTreeContainer>(mine.Objects[H("Mine")].Properties[H("pathList")]);
        Assert.Equal(BinPropertyType.WadChunkLink, container.ElementType);
        Assert.Empty(container.Elements);

        using var ms = new MemoryStream();
        mine.Write(ms);
        var back = SafeBinTree.Parse(ms.ToArray());
        var backContainer = Assert.IsType<BinTreeContainer>(back.Objects[H("Mine")].Properties[H("pathList")]);
        Assert.Equal(BinPropertyType.WadChunkLink, backContainer.ElementType);
        Assert.Empty(backContainer.Elements);
    }

    [Fact]
    public void AmbiguousOptionalEvidenceLeavesTheFieldAlone()
    {
        // One reference object has iconCircle EMPTY (declaring String); another has it SET to a link. That
        // is genuinely both for SkinCharacterDataProperties, the same as any other ambiguous field -
        // converting would be guessing, whether the evidence comes from set values or empty ones.
        byte[] Bytes(BinTree t) { using var ms = new MemoryStream(); t.Write(ms); return ms.ToArray(); }

        var seed = new BinTree(new[]
        {
            new BinTreeObject(H("Empty"), H("SkinCharacterDataProperties"), new BinTreeProperty[]
                { new BinTreeOptional(H("iconCircle"), new BinTreeString(0, TexB)) }),
            new BinTreeObject(H("Set"), H("SkinCharacterDataProperties"), new BinTreeProperty[]
                { new BinTreeOptional(H("iconCircle"), new BinTreeWadChunkLink(0, HashAlgorithms.WadPath(TexB))) }),
        }, Array.Empty<string>());

        var reference = SafeBinTree.Parse(Bytes(seed));
        var emptyOptional = (BinTreeOptional)reference.Objects[H("Empty")].Properties[H("iconCircle")];
        emptyOptional.Value = null;
        Assert.Equal(BinPropertyType.String, emptyOptional.ValueType);   // still declares String, just unset

        var mine = new BinTree(new[]
        {
            new BinTreeObject(H("Mine"), H("SkinCharacterDataProperties"), new BinTreeProperty[]
                { new BinTreeOptional(H("iconCircle"), new BinTreeString(0, TexA)) }),
        }, Array.Empty<string>());

        Assert.Equal(0, BinAssetLinkMigration.AlignWith(mine, reference));

        var optional = Assert.IsType<BinTreeOptional>(mine.Objects[H("Mine")].Properties[H("iconCircle")]);
        Assert.IsType<BinTreeString>(optional.Value);
    }

    // ---- List<String>/List<File> containers: no name hash on the elements ---------------------------

    [Fact]
    public void AListOfPathsContainerConvertsBothContainerKindsAndSurvivesARoundTrip()
    {
        // alternateIconsCircle is List<String> until build 8104348 and List<File> after (meta.db.json
        // ~19822). Container elements carry NameHash 0, so only the CONTAINER's own (class, name) and its
        // declared ElementType decide. "extraPaths" is a synthetic UnorderedContainer field exercising the
        // same rule on the other container wire form - each must keep its own concrete type.
        static BinTree Bin(BinPropertyType elementType, Func<string, BinTreeProperty> el) => new(new[]
        {
            new BinTreeObject(H("Skin"), H("SkinCharacterDataProperties"), new BinTreeProperty[]
            {
                new BinTreeContainer(H("alternateIconsCircle"), elementType, new[] { el("a.dds"), el("b.dds") }),
                new BinTreeUnorderedContainer(H("extraPaths"), elementType, new[] { el("c.dds") }),
            }),
        }, Array.Empty<string>());

        var reference = Bin(BinPropertyType.WadChunkLink, s => new BinTreeWadChunkLink(0, HashAlgorithms.WadPath(s)));
        var mine = Bin(BinPropertyType.String, s => new BinTreeString(0, s));

        Assert.Equal(3, BinAssetLinkMigration.AlignWith(mine, reference));   // 2 circle elements + 1 extra element

        var circle = Assert.IsType<BinTreeContainer>(mine.Objects[H("Skin")].Properties[H("alternateIconsCircle")]);
        Assert.Equal(BinPropertyType.WadChunkLink, circle.ElementType);
        Assert.Equal(2, circle.Elements.Count);
        Assert.All(circle.Elements, e => Assert.IsType<BinTreeWadChunkLink>(e));

        var extra = Assert.IsType<BinTreeUnorderedContainer>(mine.Objects[H("Skin")].Properties[H("extraPaths")]);
        Assert.Equal(BinPropertyType.WadChunkLink, extra.ElementType);
        Assert.Single(extra.Elements);
        Assert.All(extra.Elements, e => Assert.IsType<BinTreeWadChunkLink>(e));

        using var ms = new MemoryStream();
        mine.Write(ms);
        var back = SafeBinTree.Parse(ms.ToArray());
        var backCircle = Assert.IsType<BinTreeContainer>(back.Objects[H("Skin")].Properties[H("alternateIconsCircle")]);
        Assert.Equal(BinPropertyType.WadChunkLink, backCircle.ElementType);
        Assert.Equal(2, backCircle.Elements.Count);
        Assert.All(backCircle.Elements, e => Assert.IsType<BinTreeWadChunkLink>(e));
        var backExtra = Assert.IsType<BinTreeUnorderedContainer>(back.Objects[H("Skin")].Properties[H("extraPaths")]);
        Assert.Equal(BinPropertyType.WadChunkLink, backExtra.ElementType);
        Assert.Single(backExtra.Elements);
    }

    // ---- composition paths: map values and Optional-wrapped structs re-root correctly ---------------

    [Fact]
    public void AStructInsideAMapValueReRootsToItsOwnClass()
    {
        // shaderMacros-style maps (M123) hold structs as VALUES; the struct's own class - not whatever class
        // owns the map - must calibrate its fields. HolderClass carries its OWN "texture" field with the
        // OPPOSITE evidence (String-only) from the nested struct's ClassM.texture (link-only): if the nested
        // field were wrongly rooted under HolderClass instead of ClassM, it would stay a String instead of
        // converting, and this test would catch that. ClassN.texture is a further distractor, ambiguous by
        // name alone.
        static BinTree Bin(Func<uint, BinTreeProperty> holderTexture, Func<uint, BinTreeProperty> mapTexture,
            Func<uint, BinTreeProperty> otherTexture) => new(new[]
        {
            new BinTreeObject(H("Holder"), H("HolderClass"), new BinTreeProperty[]
            {
                holderTexture(H("texture")),
                new BinTreeMap(H("entries"), BinPropertyType.Hash, BinPropertyType.Embedded, new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, H("key1")),
                        new BinTreeEmbedded(0, H("ClassM"), new[] { mapTexture(H("texture")) })),
                }),
            }),
            new BinTreeObject(H("Other"), H("ClassN"), new BinTreeProperty[] { otherTexture(H("texture")) }),
        }, Array.Empty<string>());

        var reference = Bin(
            h => new BinTreeString(h, TexB),                                 // HolderClass.texture: String-only
            h => new BinTreeWadChunkLink(h, HashAlgorithms.WadPath(TexB)),    // ClassM.texture: link-only
            h => new BinTreeString(h, TexB));                                // ClassN.texture: String-only
        var mine = Bin(
            h => new BinTreeString(h, TexA),
            h => new BinTreeString(h, TexA),
            h => new BinTreeString(h, TexA));

        Assert.Equal(1, BinAssetLinkMigration.AlignWith(mine, reference));

        var map = (BinTreeMap)mine.Objects[H("Holder")].Properties[H("entries")];
        var entryStruct = (BinTreeStruct)map.Values.Single();
        Assert.IsType<BinTreeWadChunkLink>(entryStruct.Properties[H("texture")]);          // ClassM: converted
        Assert.IsType<BinTreeString>(mine.Objects[H("Holder")].Properties[H("texture")]);  // HolderClass: untouched
        Assert.IsType<BinTreeString>(mine.Objects[H("Other")].Properties[H("texture")]);    // ClassN: untouched
    }

    [Fact]
    public void AnOptionalWrappedStructReRootsToTheWrappedStructsClass()
    {
        // An Optional can wrap a struct, not just a scalar. HolderClass carries its OWN "texture" field
        // with the OPPOSITE evidence (String-only) from the wrapped struct's ClassP.texture (link-only): if
        // the wrapped field were wrongly rooted under HolderClass instead of ClassP, it would stay a String
        // instead of converting. ClassQ.texture is a further distractor, ambiguous by name alone.
        static BinTree Bin(Func<uint, BinTreeProperty> holderTexture, Func<uint, BinTreeProperty> wrappedTexture,
            Func<uint, BinTreeProperty> otherTexture) => new(new[]
        {
            new BinTreeObject(H("Holder"), H("HolderClass"), new BinTreeProperty[]
            {
                holderTexture(H("texture")),
                new BinTreeOptional(H("maybeThing"), new BinTreeEmbedded(0, H("ClassP"), new[] { wrappedTexture(H("texture")) })),
            }),
            new BinTreeObject(H("Other"), H("ClassQ"), new BinTreeProperty[] { otherTexture(H("texture")) }),
        }, Array.Empty<string>());

        var reference = Bin(
            h => new BinTreeString(h, TexB),
            h => new BinTreeWadChunkLink(h, HashAlgorithms.WadPath(TexB)),
            h => new BinTreeString(h, TexB));
        var mine = Bin(
            h => new BinTreeString(h, TexA),
            h => new BinTreeString(h, TexA),
            h => new BinTreeString(h, TexA));

        Assert.Equal(1, BinAssetLinkMigration.AlignWith(mine, reference));

        var optional = (BinTreeOptional)mine.Objects[H("Holder")].Properties[H("maybeThing")];
        var wrapped = (BinTreeStruct)optional.Value!;
        Assert.IsType<BinTreeWadChunkLink>(wrapped.Properties[H("texture")]);
        Assert.IsType<BinTreeString>(mine.Objects[H("Holder")].Properties[H("texture")]);
        Assert.IsType<BinTreeString>(mine.Objects[H("Other")].Properties[H("texture")]);
    }
}
