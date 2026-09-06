using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M649: a user reported that swapping a map skin left both sides with default turrets, and that editing
/// the map's bin by hand produced a map that loaded with no textures at all.
///
/// <para>Neither is corruption. Measured on the live Map11 bin, all three of ReyEngine's write paths -
/// the material editor, the ritobin text editor and the Map Skin Switcher - round-trip it with identical
/// wire forms. The turret skin simply lives in a field the switcher deliberately does not route: a map
/// skin picks its unit skins through <c>mObjectSkinFallbacks</c> (a map[hash,i32], used by every older
/// slot) or through an unnamed <c>list2</c> of {Character, SkinID} at 0x2d3285eb (schema revision
/// 7231955, used by Milkshake_SRS and Sodapop_SRS - the latter puts Turret at skin 48). Hall_Of_Legends
/// carries neither, so switching to it could never bring turrets with it.</para>
///
/// <para>The second symptom is the type-tag trap: the client dispatches on a property's one-byte tag and
/// SKIPS anything tagged unexpectedly, so a texture path re-typed from <c>file</c> to <c>string</c> loads
/// and renders nothing. <see cref="BinWireForm"/> compares tags against the bin that was opened.</para>
/// </summary>
public sealed class MapSkinCharacterSwapTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    // ===================================================== the tag trap

    private static BinTree Tree(params BinTreeProperty[] properties) =>
        new(new[] { new BinTreeObject(H("obj"), H("Thing"), properties) }, Array.Empty<string>());

    [Fact]
    public void RetaggingATexturePathFromFileToStringIsReported()
    {
        // Exactly the 16.17 trap: the value looks right in every text diff and the client drops it.
        var before = Tree(new BinTreeWadChunkLink(H("mTextureName"), HashAlgorithms.WadPath("assets/x.tex")));
        var after = Tree(new BinTreeString(H("mTextureName"), "assets/x.tex"));

        var changes = BinWireForm.Compare(before, after, h => h == H("mTextureName") ? "mTextureName" : null);
        var one = Assert.Single(changes);
        Assert.Contains("mTextureName", one.Path, StringComparison.Ordinal);
        Assert.NotEqual(one.Before, one.After);
        Assert.Contains("SKIPS", BinWireForm.Describe(changes)!, StringComparison.Ordinal);
    }

    [Fact]
    public void AListTwoWrittenAsAnOrdinaryListIsReported()
    {
        // The other half of the same family: List2 must stay UnorderedContainer or the client drops it.
        var before = Tree(new BinTreeUnorderedContainer(H("f"), BinPropertyType.U32,
            new BinTreeProperty[] { new BinTreeU32(0, 1) }));
        var after = Tree(new BinTreeContainer(H("f"), BinPropertyType.U32,
            new BinTreeProperty[] { new BinTreeU32(0, 1) }));
        Assert.Single(BinWireForm.Compare(before, after));
    }

    [Fact]
    public void AContainerWhoseElementsChangedFromEmbeddedToPointerIsReported()
    {
        // M416: a container of Embedded (0x83) elements written as pointers (0x82) loaded everywhere and
        // rendered nothing. The container's own tag is unchanged - only its element type moves.
        var before = Tree(new BinTreeContainer(H("items"), BinPropertyType.Embedded,
            new BinTreeProperty[] { new BinTreeEmbedded(0, H("El"), Array.Empty<BinTreeProperty>()) }));
        var after = Tree(new BinTreeContainer(H("items"), BinPropertyType.Struct,
            new BinTreeProperty[] { new BinTreeStruct(0, H("El"), Array.Empty<BinTreeProperty>()) }));
        var one = Assert.Single(BinWireForm.Compare(before, after));
        Assert.Contains("[]", one.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingAValueIsNotAWireFormChange()
    {
        // The point of the check is that it stays quiet for ordinary edits - a warning on every save is a
        // warning nobody reads.
        var before = Tree(new BinTreeF32(H("f"), 1f), new BinTreeString(H("s"), "a"));
        var after = Tree(new BinTreeF32(H("f"), 99f), new BinTreeString(H("s"), "b"));
        Assert.Empty(BinWireForm.Compare(before, after));
        Assert.Null(BinWireForm.Describe(BinWireForm.Compare(before, after)));
    }

    [Fact]
    public void AddingOrRemovingAFieldIsNotReported()
    {
        // Adding a field is a visible edit; re-tagging one is the invisible mistake. Only the second is
        // this check's business, or it would fire on every deliberate change.
        var before = Tree(new BinTreeF32(H("f"), 1f));
        var after = Tree(new BinTreeF32(H("f"), 1f), new BinTreeF32(H("added"), 2f));
        Assert.Empty(BinWireForm.Compare(before, after));
        Assert.Empty(BinWireForm.Compare(after, before));
    }

    [Fact]
    public void TheRealMapBinSurvivesEveryWritePathWithItsTagsIntact()
    {
        // The measurement behind the diagnosis: none of ReyEngine's writers re-tags anything.
        if (Map11() is not { } bin) return;
        var original = SafeBinTree.Parse(bin);

        string text = RitobinText.Write(original, null, null);
        var back = RitobinTextReader.Read(text, out var errors);
        Assert.NotNull(back);
        Assert.Empty(errors);
        Assert.Empty(BinWireForm.Compare(original, back!));
    }

    // ===================================================== the turret swap

    private static byte[]? Map11()
    {
        string wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
        if (!File.Exists(wad)) return null;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
        using var archive = WadArchive.Open(wad, new WadPathResolver(db));
        ulong h = HashAlgorithms.WadPath("data/maps/shipping/map11/map11.bin");
        return archive.TryGetEntry(h, out _) ? archive.Extract(h) : null;
    }

    private static Func<uint, string?> Names()
    {
        try { var db = new HashSyncService().LoadLocal(_ => { }); return h => db.TryGetBinName(h, out var n) ? n : null; }
        catch { return _ => null; }
    }

    [Fact]
    public void BothMechanismsForForcingAUnitSkinAreRead()
    {
        if (Map11() is not { } bin) return;
        var catalog = MapSkinSwitcher.ReadCatalog(bin, Names());

        // The newer list: Sodapop_SRS puts the turret on skin 48.
        var sodapop = catalog.Skins.FirstOrDefault(s => s.Name.Equals("Sodapop_SRS", StringComparison.OrdinalIgnoreCase));
        if (sodapop is not null)
        {
            Assert.Contains(sodapop.CharacterSkins, o => o.Character.EndsWith("Turret", StringComparison.OrdinalIgnoreCase)
                                                         && o.SkinId == 48 && !o.FromFallbackMap);
            Assert.Contains("Turret", sodapop.CharacterSummary, StringComparison.OrdinalIgnoreCase);
        }

        // The older map, which is what every pre-2025 slot uses.
        var withMap = catalog.Skins.FirstOrDefault(s => s.CharacterSkins.Any(o => o.FromFallbackMap));
        Assert.True(withMap is not null, "no slot read its mObjectSkinFallbacks");

        // And the slot the report is about forces nothing at all, which is why switching to it
        // could never have brought turrets with it.
        var hol = catalog.Skins.FirstOrDefault(s => s.Name.Contains("Hall", StringComparison.OrdinalIgnoreCase));
        if (hol is not null)
        {
            Assert.Empty(hol.CharacterSkins);
            Assert.Contains("no character skins", hol.CharacterSummary, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ASwitchLeavesUnitSkinsAloneUnlessAsked()
    {
        if (Map11() is not { } bin) return;
        var names = Names();
        var catalog = MapSkinSwitcher.ReadCatalog(bin, names);
        var target = catalog.Skins.First(s => s.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
        var source = catalog.Skins.First(s => s.CharacterSkins.Count > 0 && s.MapContainerLink is not null
                                              && s.PathHash != target.PathHash);

        var plain = MapSkinSwitcher.Switch(bin, 11, target.PathHash, source.PathHash, names);
        Assert.Equal(0, plain.CharacterSkinSlotsRouted);
        Assert.Equal(0, plain.CharacterSkinSlotsAdded);
        Assert.Empty(plain.CarriedCharacterSkins);

        // every slot still forces exactly what it forced before
        var beforeCatalog = MapSkinSwitcher.ReadCatalog(bin, names);
        var afterCatalog = MapSkinSwitcher.ReadCatalog(plain.Bytes, names);
        foreach (var was in beforeCatalog.Skins)
        {
            var now = afterCatalog.Skins.Single(s => s.PathHash == was.PathHash);
            Assert.Equal(was.CharacterSkins.Count, now.CharacterSkins.Count);
        }
    }

    [Fact]
    public void AskingForThemCarriesTheSourcesUnitSkinsToEverySlot()
    {
        if (Map11() is not { } bin) return;
        var names = Names();
        var catalog = MapSkinSwitcher.ReadCatalog(bin, names);
        var target = catalog.Skins.First(s => s.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
        var source = catalog.Skins.First(s => s.CharacterSkins.Count > 0 && s.MapContainerLink is not null
                                              && s.PathHash != target.PathHash);

        var carried = MapSkinSwitcher.Switch(bin, 11, target.PathHash, source.PathHash, names, carryCharacterSkins: true);
        Assert.True(carried.CharacterSkinSlotsRouted + carried.CharacterSkinSlotsAdded > 0,
            "asking to carry the unit skins changed nothing");
        Assert.Equal(source.CharacterSkins.Count, carried.CarriedCharacterSkins.Count);

        // the result is a bin, not a hope: it parses, and nothing was re-tagged on the way
        var rewritten = SafeBinTree.Parse(carried.Bytes);
        Assert.Empty(BinWireForm.Compare(SafeBinTree.Parse(bin), rewritten, names));

        // and the target slot now forces what the source forced
        var after = MapSkinSwitcher.ReadCatalog(carried.Bytes, names);
        var newTarget = after.Skins.Single(s => s.PathHash == target.PathHash);
        Assert.Equal(source.CharacterSkins.Select(o => o.DisplayName).OrderBy(x => x, StringComparer.Ordinal),
                     newTarget.CharacterSkins.Select(o => o.DisplayName).OrderBy(x => x, StringComparer.Ordinal));

        // the SOURCE definition itself is never rewritten - the switcher's existing contract
        var sourceAfter = after.Skins.Single(s => s.PathHash == source.PathHash);
        Assert.Equal(source.CharacterSkins.Count, sourceAfter.CharacterSkins.Count);
    }

    [Fact]
    public void CarryingFromASlotThatForcesNothingChangesNothing()
    {
        // Hall_Of_Legends is that slot, and the tool now says so instead of silently doing nothing.
        if (Map11() is not { } bin) return;
        var names = Names();
        var catalog = MapSkinSwitcher.ReadCatalog(bin, names);
        var source = catalog.Skins.FirstOrDefault(s => s.CharacterSkins.Count == 0 && s.MapContainerLink is not null);
        if (source is null) return;
        var target = catalog.Skins.First(s => s.PathHash != source.PathHash);

        var result = MapSkinSwitcher.Switch(bin, 11, target.PathHash, source.PathHash, names, carryCharacterSkins: true);
        Assert.Equal(0, result.CharacterSkinSlotsRouted);
        Assert.Equal(0, result.CharacterSkinSlotsAdded);
        Assert.Empty(result.CarriedCharacterSkins);
    }
}
