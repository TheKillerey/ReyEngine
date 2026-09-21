using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M747: placing a character as Riot's <c>MapAnimatedProp</c> - the client-side decoration form.
///
/// <para>Scenery-character placements ReyEngine wrote on the ported Map453 never spawned in game, not even
/// a plain S3Yonkey whose Riot-placed twins on the same map do. Every test was a replay recorded on vanilla
/// data, and the working theory is that character placements are spawned by the server while this form is
/// created by the client, like particles. The shape here is Riot's, taken from all 3,058 shipped props:
/// transform, a STRING name, PropName, PlayIdleAnimation + IdleAnimationName when it plays, SkinID only when
/// it is not 0 (0 of 3,058 write a 0), and Dimension = 6 (2,906 of 3,058; every one that writes it).</para>
/// </summary>
public sealed class MapAnimatedPropTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private const uint SceneryCharacter = 0x9aa5b4bcu;
    private const uint Dimension = 0x670b6ae3u;

    private static BinTreeObject Container(string name, params BinTreeStruct[] items) =>
        new(H(name), H("MapPlaceableContainer"), new BinTreeProperty[]
        {
            new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct,
                items.Select((it, i) => new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, H(name + i)), it))),
        });

    private static BinTreeStruct Item(uint cls) =>
        new(0, cls, new BinTreeProperty[] { new BinTreeMatrix44(H("transform"), Matrix4x4.Identity) });

    private static byte[] Bytes(params BinTreeObject[] objects)
    {
        using var ms = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static BinTreeStruct Written(byte[] bin, MapPlacementId id)
    {
        var tree = new BinTree(new MemoryStream(bin, false));
        var items = (BinTreeMap)tree.Objects[id.ContainerHash].Properties[H("items")];
        return (BinTreeStruct)items.First(e => e.Key is BinTreeHash k && k.Value == id.ItemKey).Value;
    }

    private static (byte[] Bin, MapPlacementId Id) Place(string prop, uint skinId, string? idle)
    {
        byte[] bin = Bytes(Container("scratch", Item(0)), Container("chars", Item(SceneryCharacter)));
        var id = MapPlaceableWriter.NewAnimatedPropId(new BinTree(new MemoryStream(bin, false)), H(prop + "_1"));
        var output = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(id)
            {
                CreateAnimatedProp = true,
                Name = prop + "_1",
                Transform = Matrix4x4.CreateTranslation(3714, 30, 10401),
                PropName = prop,
                SkinId = skinId,
                IdleAnimation = idle,
            },
        }, out string? error);
        Assert.Null(error);
        Assert.NotNull(output);
        return (output!, id);
    }

    // ===================================================== the shape Riot writes

    [Fact]
    public void ThePlacementCarriesRiotsFieldsInRiotsOrder()
    {
        var (bin, id) = Place("Urf_Ghost_Jade", 2, "Idle1");
        var item = Written(bin, id);

        Assert.Equal(H("MapAnimatedProp"), item.ClassHash);
        Assert.Equal(new[] { H("transform"), H("name"), H("PropName"), H("PlayIdleAnimation"), H("IdleAnimationName"),
                             H("SkinID"), Dimension }, item.Properties.Keys.ToArray());

        // a STRING name on this class - the character placement carries a hash
        Assert.Equal("Urf_Ghost_Jade_1", Assert.IsType<BinTreeString>(item.Properties[H("name")]).Value);
        Assert.Equal("Urf_Ghost_Jade", Assert.IsType<BinTreeString>(item.Properties[H("PropName")]).Value);
        Assert.True(Assert.IsType<BinTreeBool>(item.Properties[H("PlayIdleAnimation")]).Value);
        Assert.Equal(2u, Assert.IsType<BinTreeU32>(item.Properties[H("SkinID")]).Value);
        Assert.Equal((byte)6, Assert.IsType<BinTreeU8>(item.Properties[Dimension]).Value);
        Assert.Equal(new Vector3(3714, 30, 10401),
            Assert.IsType<BinTreeMatrix44>(item.Properties[H("transform")]).Value.Translation);
    }

    [Fact]
    public void SkinZeroIsLeftOutAsRiotLeavesItOut()
    {
        var (bin, id) = Place("S3Yonkey", 0, "Idle1");
        Assert.False(Written(bin, id).Properties.ContainsKey(H("SkinID")));
    }

    [Fact]
    public void APropWithNoClipCarriesNeitherIdleField()
    {
        // Riot's "transform,name,PropName,Dimension" shape - a prop that stands still.
        var (bin, id) = Place("S3Yonkey", 0, null);
        Assert.Equal(new[] { H("transform"), H("name"), H("PropName"), Dimension },
            Written(bin, id).Properties.Keys.ToArray());
    }

    [Fact]
    public void ItIsNotACharacterPlacement()
    {
        // The whole point: no Character component for the server to own, nothing attackable.
        var item = Written(Place("Urf_Ghost_Jade", 0, "Idle1").Bin, Place("Urf_Ghost_Jade", 0, "Idle1").Id);
        Assert.False(item.Properties.ContainsKey(H("Character")));
        Assert.False(item.Properties.ContainsKey(H("CharacterMesh")));
        Assert.NotEqual(SceneryCharacter, item.ClassHash);
    }

    [Fact]
    public void APropWithoutAPropNameIsRefused()
    {
        byte[] bin = Bytes(Container("chars", Item(SceneryCharacter)));
        var id = MapPlaceableWriter.NewAnimatedPropId(new BinTree(new MemoryStream(bin, false)), 1);
        var output = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(id) { CreateAnimatedProp = true, Transform = Matrix4x4.Identity },
        }, out string? error);
        Assert.Null(output);
        Assert.NotNull(error);
    }

    // ===================================================== where it goes

    [Fact]
    public void ItGoesWhereTheMapKeepsItsAnimatedProps()
    {
        var tree = new BinTree(new[]
        {
            Container("scratch", Item(0)),
            Container("chars", Item(SceneryCharacter), Item(SceneryCharacter)),
            Container("ducks", Item(H("MapAnimatedProp"))),
        }, Array.Empty<string>());
        Assert.Equal(H("ducks"), MapPlaceableWriter.NewAnimatedPropId(tree, 7).ContainerHash);
    }

    [Fact]
    public void OnAMapWithNoneItGoesWhereTheCharactersAre()
    {
        // Map453 carries no MapAnimatedProp at all; the scratch layer that comes first is never the answer.
        var tree = new BinTree(new[] { Container("scratch", Item(0)), Container("chars", Item(SceneryCharacter)) },
            Array.Empty<string>());
        Assert.Equal(H("chars"), MapPlaceableWriter.NewAnimatedPropId(tree, 7).ContainerHash);
    }

    // ===================================================== skins by number

    [Theory]
    [InlineData("Characters/Urf_Ghost_Jade/Skins/Skin0", true, 0u)]
    [InlineData("Characters/S3Yonkey/Skins/Skin12", true, 12u)]
    [InlineData("Skin3", true, 3u)]
    [InlineData("Characters/X/Skins/Meta", false, 0u)]
    [InlineData("Characters/X/Skins/Skin-1", false, 0u)]
    public void ASkinPathBecomesItsNumber(string path, bool ok, uint expected)
    {
        Assert.Equal(ok, MapPlaceableWriter.TrySkinNumber(path, out uint n));
        if (ok) Assert.Equal(expected, n);
    }

    [Fact]
    public void ChangingTheSkinWritesTheNumberAndDropsItAtZero()
    {
        var (bin, id) = Place("S3Yonkey", 0, "Idle1");
        bin = MapPlaceableWriter.WriteEdits(bin, new[] { new MapPlacementEdit(id) { Skin = "Characters/S3Yonkey/Skins/Skin4" } }, out _)!;
        Assert.Equal(4u, ((BinTreeU32)Written(bin, id).Properties[H("SkinID")]).Value);

        bin = MapPlaceableWriter.WriteEdits(bin, new[] { new MapPlacementEdit(id) { Skin = "Characters/S3Yonkey/Skins/Skin0" } }, out _)!;
        Assert.False(Written(bin, id).Properties.ContainsKey(H("SkinID")));
    }

    // ===================================================== the editor sees it

    [Fact]
    public void TheEditorReadsItBackAsAProp()
    {
        var (bin, id) = Place("Urf_Ghost_Jade", 3, "Idle1");
        var (_, props, _) = MapPlaceableExtractor.Extract(bin);

        var prop = Assert.Single(props);
        Assert.True(prop.IsAnimatedPropClass);
        Assert.Equal(id, prop.Id);
        Assert.Equal("Urf_Ghost_Jade_1", prop.Name);
        Assert.Equal("Urf_Ghost_Jade", prop.CharacterName);
        Assert.Equal("Characters/Urf_Ghost_Jade/CharacterRecords/Root", prop.CharacterRecord);
        Assert.Equal("Characters/Urf_Ghost_Jade/Skins/Skin3", prop.Skin);
        Assert.Equal("Idle1", prop.IdleAnimation);
        Assert.True(prop.PlaysIdle);
        Assert.Equal(new Vector3(3714, 30, 10401), prop.Position);
    }

    [Fact]
    public void RiotsShippedPropsAreNoLongerSkipped()
    {
        // Before M747 the reader recognised a prop only by a Character component, so all 3,058 of these -
        // SR's ducks among them - were invisible in the editor.
        const string wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
        if (!File.Exists(wad)) return;
        var db = new HashSyncService().LoadLocal(_ => { });
        using var archive = ReyEngine.Core.Wad.WadArchive.Open(wad, new WadPathResolver(db));
        int found = 0;
        foreach (var e in archive.Entries.Where(x => x.IsResolved && x.Path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase)))
            found += MapPlaceableExtractor.Extract(archive.Extract(e)).Props.Count(p => p.IsAnimatedPropClass);
        Assert.True(found > 0);
    }

    // ===================================================== the windows

    [Fact]
    public void BothWindowsOfferTheChoiceAndDefaultToTheClientSideForm()
    {
        Assert.True(new AddPropViewModel().PlaceAsAnimatedProp);
        Assert.True(new CharacterCreatorViewModel().PlaceAsAnimatedProp);
        Assert.True(new AddPropRequest("S3Yonkey", "r", "s", null).AsAnimatedProp);
    }

    [Fact]
    public void TheHostPassesTheWindowsChoiceThrough()
    {
        string? src = null, addProp = null, creator = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && src is null; dir = dir.Parent)
        {
            string app = Path.Combine(dir.FullName, "src", "ReyEngine.App");
            if (!Directory.Exists(app)) continue;
            src = File.ReadAllText(Path.Combine(app, "ViewModels", "MainWindowViewModel.CharacterCreator.cs"));
            addProp = File.ReadAllText(Path.Combine(app, "Views", "AddPropWindow.axaml"));
            creator = File.ReadAllText(Path.Combine(app, "Views", "CharacterCreatorWindow.axaml"));
        }
        if (src is null) return;
        Assert.Contains("CreateCharacterFromFolderAsync(result, place, vm.PlaceAsAnimatedProp,", src);
        Assert.Contains("request.AsAnimatedProp", src);
        Assert.Contains("MapPlaceableWriter.NewAnimatedPropId(tree, HashAlgorithms.Fnv1a(placementName))", src);
        Assert.Contains("{Binding PlaceAsAnimatedProp}", addProp);
        Assert.Contains("{Binding PlaceAsAnimatedProp}", creator);
    }
}
