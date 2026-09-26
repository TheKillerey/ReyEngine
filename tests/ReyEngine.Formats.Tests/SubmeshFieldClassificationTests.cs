using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Undo;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M775: the material editor showed <c>initialSubmeshToHide</c> ("orb") and <c>submeshRenderOrder</c>
/// ("body top bowl orb frame glass glass_out") as TEXTURE SLOTS - Open/Copy/Replace Texture… and a "path
/// not found" warning triangle for a value that was never a path. <see cref="BinTexturePath.Is"/> accepted
/// any <c>BinTreeString</c> or <c>BinTreeWadChunkLink</c>, and two GENERIC callers ran it over every field
/// a skin's <c>skinMeshProperties</c> block carries.
///
/// <para><see cref="BinTexturePath.IsTextureField"/> is the fix: it classifies by content/name, not merely
/// by wire type. Censused across all 174 champion WADs' skin bins (15,043 <c>skinMeshProperties</c> blocks):
/// every string field besides <c>skeleton</c>/<c>simpleSkin</c> (excluded by name before classification
/// even runs) is one of six submesh-name lists - initialSubmeshToHide, submeshRenderOrder,
/// initialSubmeshShadowsToHide, initialSubmeshMouseOversToHide, InitialSubmeshAvatarToHide,
/// EmitterSubmeshAvatarToHide - never a texture path.</para>
/// </summary>
public sealed class SubmeshFieldClassificationTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    // ===================================================== IsTextureField unit tests

    [Fact]
    public void SubmeshNameListsAreNotTextures()
    {
        Assert.False(BinTexturePath.IsTextureField("initialSubmeshToHide", new BinTreeString(H("x"), "orb")));
        Assert.False(BinTexturePath.IsTextureField("submeshRenderOrder",
            new BinTreeString(H("x"), "body top bowl orb frame glass glass_out")));
        Assert.False(BinTexturePath.IsTextureField("initialSubmeshShadowsToHide", new BinTreeString(H("x"), "Banner Wings")));
        Assert.False(BinTexturePath.IsTextureField("initialSubmeshMouseOversToHide", new BinTreeString(H("x"), "Light,Drum")));
        // an empty list field still isn't a texture - its name doesn't end in Texture/Map
        Assert.False(BinTexturePath.IsTextureField("initialSubmeshToHide", new BinTreeString(H("x"), "")));
    }

    [Fact]
    public void RealTexturePathsAreTextures()
    {
        Assert.True(BinTexturePath.IsTextureField("texture",
            new BinTreeString(H("x"), "ASSETS/Characters/Ahri/Skins/Base/Ahri_Base_TX_CM.tex")));
        Assert.True(BinTexturePath.IsTextureField("glossTexture",
            new BinTreeString(H("x"), "ASSETS/Characters/Foo/Skins/Base/Foo_Gloss.dds")));
        Assert.True(BinTexturePath.IsTextureField("reflectionMap",
            new BinTreeString(H("x"), "ASSETS/Characters/Foo/Skins/Base/Foo_Refl.tex")));
        // the hex-hash form (M592) - no extension, but still an addressable reference
        Assert.True(BinTexturePath.IsTextureField("reflectionMap", new BinTreeString(H("x"), "0x0123456789abcdef")));
    }

    [Fact]
    public void AnEmptyFieldNamedLikeATextureIsStillATextureSlot()
    {
        // M705: emissiveTexture/glossTexture/reflectionMap are frequently absent-valued but must still show
        // as a (empty) slot, not vanish into the settings list, so a user can fill one in.
        Assert.True(BinTexturePath.IsTextureField("emissiveTexture", new BinTreeString(H("x"), "")));
        Assert.True(BinTexturePath.IsTextureField("texture", new BinTreeString(H("x"), "")));
        Assert.True(BinTexturePath.IsTextureField("reflectionMap", new BinTreeString(H("x"), "")));
    }

    [Fact]
    public void AWadChunkLinkIsAlwaysATextureRegardlessOfName()
    {
        // 16.17 turned every material texturePath into a WadChunkLink (M590); the link form carries no
        // readable name to check, so it must always classify as a texture slot.
        Assert.True(BinTexturePath.IsTextureField("submeshRenderOrder", new BinTreeWadChunkLink(H("x"), 0x1234UL)));
        Assert.True(BinTexturePath.IsTextureField("texture", new BinTreeWadChunkLink(H("x"), 0UL)));
    }

    [Fact]
    public void AnOrdinarySettingIsNeitherAPathNorATextureName()
    {
        Assert.False(BinTexturePath.IsTextureField("castShadows", new BinTreeString(H("x"), "true")));
        // A path-shaped value (separator + extension) reads as a texture reference by CONTENT even under a
        // non-texture name - which is why MaterialDocument.Parse excludes skeleton/simpleSkin BY NAME
        // before this classifier ever sees them (they point at .skl/.skn, not a sampler).
        Assert.True(BinTexturePath.IsTextureField("skeleton", new BinTreeString(H("x"), "ASSETS/Foo/Foo.skl")));
    }

    // ===================================================== real-asset: the fields land in SETTINGS

    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static MaterialDocument? ParseBin(string wadPath, string binPath)
    {
        if (!File.Exists(wadPath) || Database.Value is not { } db) return null;
        var resolver = new WadPathResolver(db);
        using var archive = WadArchive.Open(wadPath, resolver);
        ulong hash = HashAlgorithms.WadPath(binPath);
        if (!archive.TryGetEntry(hash, out var entry)) return null;
        string? Bin(uint h) => db.TryGetBinName(h, out var n) ? n : null;
        string? Wad(ulong h) => db.TryGetPath(h, out var p) ? p : null;
        return MaterialDocument.Parse(archive.Extract(entry), Bin, Wad);
    }

    [Fact]
    public void NexusSkin30ListsAreSettingsNotSlots()
    {
        // Nexus (a map "character", not a champion) lives in the map WAD, not Champions/.
        var doc = ParseBin(Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client"), "data/characters/nexus/skins/skin30.bin")
                  ?? ParseBin(Path.Combine(Final, "Maps", "Shipping", "Map12.wad.client"), "data/characters/nexus/skins/skin30.bin");
        if (doc is null) return;   // no game install on this machine
        AssertListFieldsAreSettings(doc);
    }

    [Fact]
    public void AnOrdinaryChampionSkinListsAreSettingsNotSlots()
    {
        if (!Directory.Exists(Final)) return;
        var doc = ParseBin(Path.Combine(Final, "Champions", "Ahri.wad.client"), "data/characters/ahri/skins/skin0.bin");
        if (doc is null) return;
        AssertListFieldsAreSettings(doc);
    }

    private static void AssertListFieldsAreSettings(MaterialDocument doc)
    {
        var skinDefault = doc.Materials.FirstOrDefault(m => m.Name == "(skin default texture)");
        if (skinDefault is null) return;   // this skin authors no skinMeshProperties fields at all

        string[] listFields =
        {
            "initialSubmeshToHide", "submeshRenderOrder", "initialSubmeshShadowsToHide",
            "initialSubmeshMouseOversToHide", "InitialSubmeshAvatarToHide", "EmitterSubmeshAvatarToHide",
        };
        bool foundAny = false;
        foreach (var field in listFields)
        {
            if (skinDefault.Parameters.Any(p => p.Name.Equals(field, StringComparison.OrdinalIgnoreCase)))
            {
                foundAny = true;
                Assert.DoesNotContain(skinDefault.Slots, s => s.SamplerName.Equals(field, StringComparison.OrdinalIgnoreCase));
            }
        }
        // If this skin authors none of the known list fields, there's nothing this test can check - that's
        // a fact about the asset, not a pass/fail on the classifier (a census census trap - CENSUS-POP).
        if (!foundAny) return;

        // And a REAL texture field, when present, must still be a slot.
        if (skinDefault.Parameters.Any(p => p.Name.Equals("texture", StringComparison.OrdinalIgnoreCase)))
            Assert.Fail("`texture` must never land in Parameters - it is the skin's own diffuse slot.");
    }

    // ===================================================== App layer: the checklist/reorder editors

    private static (MaterialEditorViewModel Owner, MaterialParameter Param) MakeParam(string fieldName, string value)
    {
        var owner = new MaterialEditorViewModel();
        var prop = new BinTreeString(H(fieldName), value);
        return (owner, new MaterialParameter(fieldName, prop));
    }

    [Fact]
    public void SetFieldChecklistTicksKnownNamesAndKeepsUnknownOnes()
    {
        var (owner, param) = MakeParam("initialSubmeshToHide", "orb frame_ghost");
        var vm = new SubmeshSetFieldViewModel(param, owner);
        vm.SetKnownSubmeshes(new[] { "body", "orb", "frame" });

        Assert.Equal(3, vm.Items.Count);
        Assert.True(vm.Items.Single(i => i.Name == "orb").IsChecked);
        Assert.False(vm.Items.Single(i => i.Name == "body").IsChecked);
        Assert.False(vm.Items.Single(i => i.Name == "frame").IsChecked);
        Assert.True(vm.HasUnknown);
        Assert.Contains("frame_ghost", vm.UnknownSummary);

        // Tick "body" - the field gains it while keeping "orb" and the unknown name untouched.
        vm.Items.Single(i => i.Name == "body").IsChecked = true;
        Assert.Equal("body orb frame_ghost", param.CurrentText);
        Assert.True(param.IsDirty);

        // Untick "orb" - it drops, the unknown name still survives.
        vm.Items.Single(i => i.Name == "orb").IsChecked = false;
        Assert.Equal("body frame_ghost", param.CurrentText);
    }

    [Fact]
    public void SetFieldWritesThroughUndo()
    {
        var (owner, param) = MakeParam("initialSubmeshToHide", "orb");
        owner.UndoService = new UndoRedoService();
        var vm = new SubmeshSetFieldViewModel(param, owner);
        vm.SetKnownSubmeshes(new[] { "orb", "body" });

        vm.Items.Single(i => i.Name == "body").IsChecked = true;
        Assert.Equal("orb body", param.CurrentText);
        Assert.True(owner.UndoService.CanUndo);

        owner.UndoService.Undo();
        Assert.Equal("orb", param.CurrentText);
        Assert.False(vm.Items.Single(i => i.Name == "body").IsChecked);

        owner.UndoService.Redo();
        Assert.Equal("orb body", param.CurrentText);
        Assert.True(vm.Items.Single(i => i.Name == "body").IsChecked);
    }

    [Fact]
    public void OrderFieldReordersAndAddsWithoutLosingUnlistedMeshSubmeshes()
    {
        var (owner, param) = MakeParam("submeshRenderOrder", "body top bowl");
        var vm = new SubmeshOrderFieldViewModel(param, owner);
        vm.SetKnownSubmeshes(new[] { "body", "top", "bowl", "orb" });

        Assert.Equal(new[] { "body", "top", "bowl" }, vm.Items.Select(i => i.Name));
        Assert.Equal(new[] { "orb" }, vm.Addable);   // "orb" isn't listed yet

        // Move "bowl" to the front - through the item's own command, the way the ▲ button does.
        vm.Items[2].MoveUpCommand.Execute(null);
        vm.Items[1].MoveUpCommand.Execute(null);
        Assert.Equal("bowl body top", param.CurrentText);

        // Add "orb" through the same command the "+ Add" button uses.
        vm.SelectedAddable = "orb";
        vm.AddSelectedCommand.Execute(null);
        Assert.Equal("bowl body top orb", param.CurrentText);
        Assert.False(vm.HasAddable);

        // Remove one - the rest keep their relative order.
        vm.Items.Single(i => i.Name == "top").RemoveCommand.Execute(null);
        Assert.Equal("bowl body orb", param.CurrentText);
    }

    [Fact]
    public void OrderFieldWritesThroughUndo()
    {
        var (owner, param) = MakeParam("submeshRenderOrder", "a b c");
        owner.UndoService = new UndoRedoService();
        var vm = new SubmeshOrderFieldViewModel(param, owner);
        vm.SetKnownSubmeshes(new[] { "a", "b", "c" });

        vm.Items[0].MoveDownCommand.Execute(null);   // a b c -> b a c
        Assert.Equal("b a c", param.CurrentText);

        owner.UndoService.Undo();
        Assert.Equal("a b c", param.CurrentText);
        Assert.Equal(new[] { "a", "b", "c" }, vm.Items.Select(i => i.Name));
    }
}
