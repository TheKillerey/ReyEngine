using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M751: an existing scenery-character placement becomes a client-side MapAnimatedProp in place, so a prop
/// placed before M747 - which never spawns, because the server spawns character placements - does not have
/// to be placed again. Same container, same key, same transform; only what the new form can say is
/// carried, and anything else is refused with the reason rather than dropped.
/// </summary>
public sealed class ConvertToClientPropTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private const uint SceneryCharacter = 0x9aa5b4bcu;
    private static readonly Matrix4x4 Where = Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(3714, 30, 10401);

    private static byte[] Empty()
    {
        var chars = new BinTreeObject(H("chars"), H("MapPlaceableContainer"), new BinTreeProperty[]
        {
            new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, new[]
            {
                new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 1),
                    new BinTreeStruct(0, SceneryCharacter, new BinTreeProperty[] { new BinTreeMatrix44(H("transform"), Matrix4x4.Identity) })),
            }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { chars }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    /// <summary>A character placement exactly as ReyEngine wrote them before M747.</summary>
    private static (byte[] Bin, MapPlacementId Id) OldStyle(string skin = "Characters/Urf_Ghost_Jade/Skins/Skin2",
        string record = "Characters/Urf_Ghost_Jade/CharacterRecords/Root")
    {
        byte[] bin = Empty();
        var id = MapPlaceableWriter.NewCharacterId(new BinTree(new MemoryStream(bin, false)), H("Urf_Ghost_Jade_1"));
        bin = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(id)
            {
                CreateCharacter = true, Name = "Urf_Ghost_Jade_1", Transform = Where,
                CharacterRecord = record, Skin = skin, IdleAnimation = "Idle1",
            },
        }, out string? error)!;
        Assert.Null(error);
        return (bin, id);
    }

    private static BinTreeStruct Item(byte[] bin, MapPlacementId id) =>
        (BinTreeStruct)((BinTreeMap)new BinTree(new MemoryStream(bin, false)).Objects[id.ContainerHash].Properties[H("items")])
            .First(e => e.Key is BinTreeHash k && k.Value == id.ItemKey).Value;

    private static byte[] Convert(byte[] bin, MapPlacementId id, float? appearAfter = null)
    {
        Assert.Null(MapPlaceableWriter.WhyNotConvertible(bin, id));
        var output = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(id) { ConvertToAnimatedProp = true, AppearAfterSeconds = appearAfter },
        }, out string? error);
        Assert.Null(error);
        return output!;
    }

    [Fact]
    public void ItBecomesAClientSidePropInPlace()
    {
        var (bin, id) = OldStyle();
        bin = Convert(bin, id);

        var item = Item(bin, id);                         // same container, same key
        Assert.Equal(H("MapAnimatedProp"), item.ClassHash);
        Assert.Equal(Where, ((BinTreeMatrix44)item.Properties[H("transform")]).Value);
        Assert.Equal("Urf_Ghost_Jade", ((BinTreeString)item.Properties[H("PropName")]).Value);
        Assert.Equal(2u, ((BinTreeU32)item.Properties[H("SkinID")]).Value);
        Assert.True(((BinTreeBool)item.Properties[H("PlayIdleAnimation")]).Value);
        Assert.Equal("Idle1", ((BinTreeString)item.Properties[H("IdleAnimationName")]).Value);
        Assert.Equal((byte)6, ((BinTreeU8)item.Properties[0x670b6ae3u]).Value);
        Assert.False(item.Properties.ContainsKey(H("Character")));
        Assert.False(item.Properties.ContainsKey(H("CharacterMesh")));
        Assert.IsType<BinTreeString>(item.Properties[H("name")]);   // a string on this class

        var prop = Assert.Single(MapPlaceableExtractor.Extract(bin).Props);
        Assert.True(prop.IsAnimatedPropClass);
        Assert.Equal(id, prop.Id);
        Assert.Equal("Characters/Urf_Ghost_Jade/Skins/Skin2", prop.Skin);
    }

    [Fact]
    public void SkinZeroIsLeftOut()
    {
        var (bin, id) = OldStyle(skin: "Characters/Urf_Ghost_Jade/Skins/Skin0");
        Assert.False(Item(Convert(bin, id), id).Properties.ContainsKey(H("SkinID")));
    }

    [Fact]
    public void TheVisibilityMaskComesAlong()
    {
        var (bin, id) = OldStyle();
        bin = MapPlaceableWriter.WriteEdits(bin, new[] { new MapPlacementEdit(id) { VisibilityFlags = 3 } }, out _)!;
        var item = Item(Convert(bin, id), id);
        Assert.Equal((byte)3, ((BinTreeU8)item.Properties[H("mVisibilityFlags")]).Value);
        // and in Riot's position: after the name, before PropName
        var keys = item.Properties.Keys.ToList();
        Assert.True(keys.IndexOf(H("mVisibilityFlags")) < keys.IndexOf(H("PropName")));
    }

    [Fact]
    public void ItCanBeGivenATimeInTheSameSave()
    {
        var (bin, id) = OldStyle();
        bin = Convert(bin, id, appearAfter: 60);
        Assert.Equal(60f, Assert.Single(MapPlaceableExtractor.Extract(bin).Props).AppearAfterSeconds);
    }

    // ===================================================== refusals, each with its reason

    [Fact]
    public void AnAttackableUnitIsRefused()
    {
        var (bin, id) = OldStyle();
        var tree = new BinTree(new MemoryStream(bin, false));
        var items = (BinTreeMap)tree.Objects[id.ContainerHash].Properties[H("items")];
        var unit = new BinTreeStruct(0, 0xad65d8c4u, Item(bin, id).Properties.Values.ToArray());
        tree.Objects[id.ContainerHash].Properties[H("items")] = new BinTreeMap(H("items"), items.KeyType, items.ValueType,
            items.Select(e => new KeyValuePair<BinTreeProperty, BinTreeProperty>(e.Key,
                e.Key is BinTreeHash k && k.Value == id.ItemKey ? unit : e.Value)));
        using var ms = new MemoryStream();
        tree.Write(ms);
        Assert.Contains("attackable", MapPlaceableWriter.WhyNotConvertible(ms.ToArray(), id));
    }

    [Fact]
    public void ANonRootRecordIsRefused()
    {
        // Jade_Turret's placements use Jade_Outer / Jade_Inner records; as a prop it would silently become Root.
        var (bin, id) = OldStyle(record: "Characters/Jade_Turret/CharacterRecords/Jade_Outer", skin: "Characters/Jade_Turret/Skins/Skin4");
        Assert.Contains("Root record", MapPlaceableWriter.WhyNotConvertible(bin, id));
        Assert.Null(MapPlaceableWriter.WriteEdits(bin, new[] { new MapPlacementEdit(id) { ConvertToAnimatedProp = true } }, out _));
    }

    [Fact]
    public void ASkinOfAnotherCharacterIsRefused()
    {
        var (bin, id) = OldStyle(skin: "Characters/S3Yonkey/Skins/Skin0");
        Assert.Contains("own Skins/SkinN", MapPlaceableWriter.WhyNotConvertible(bin, id));
    }

    [Fact]
    public void AFieldTheNewFormCannotSayIsRefusedNotDropped()
    {
        var (bin, id) = OldStyle();
        var tree = new BinTree(new MemoryStream(bin, false));
        var items = (BinTreeMap)tree.Objects[id.ContainerHash].Properties[H("items")];
        var withExtra = Item(bin, id);
        withExtra.Properties[0xbbe68da1u] = new BinTreeBool(0xbbe68da1u, false);   // as Riot's structure placements carry
        tree.Objects[id.ContainerHash].Properties[H("items")] = new BinTreeMap(H("items"), items.KeyType, items.ValueType,
            items.Select(e => new KeyValuePair<BinTreeProperty, BinTreeProperty>(e.Key,
                e.Key is BinTreeHash k && k.Value == id.ItemKey ? withExtra : e.Value)));
        using var ms = new MemoryStream();
        tree.Write(ms);
        Assert.Contains("0xbbe68da1", MapPlaceableWriter.WhyNotConvertible(ms.ToArray(), id));
    }

    [Fact]
    public void AClientSidePropIsAlreadyConverted()
    {
        var (bin, id) = OldStyle();
        bin = Convert(bin, id);
        Assert.Contains("already", MapPlaceableWriter.WhyNotConvertible(bin, id));
    }

    [Fact]
    public void RiotsOwnMap453PlacementsGetAnAnswerEachAndNeverAThrow()
    {
        const string wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client";
        if (!File.Exists(wad)) return;
        var db = new HashSyncService().LoadLocal(_ => { });
        using var archive = ReyEngine.Core.Wad.WadArchive.Open(wad, new WadPathResolver(db));
        var entry = archive.Entries.First(e => e.IsResolved && e.Path.EndsWith("map453/jade_container.materials.bin", StringComparison.OrdinalIgnoreCase));
        byte[] bin = archive.Extract(entry);
        var props = MapPlaceableExtractor.Extract(bin).Props.Where(p => !p.IsAnimatedPropClass).ToList();
        Assert.NotEmpty(props);
        var answers = props.Select(p => MapPlaceableWriter.WhyNotConvertible(bin, p.Id)).ToList();
        // the turrets and nexus are attackable - never decorations
        Assert.Contains(answers, a => a is not null && a.Contains("attackable"));
    }

    [Fact]
    public void RiotsOwnPlacementIsRecognisedAndOursIsNot()
    {
        // Riot's are spawned by the server whatever the mod says - converting one would draw a second copy.
        var (riot, id) = OldStyle();
        Assert.True(MapPlaceableWriter.ShippedBy(riot, id));
        Assert.False(MapPlaceableWriter.ShippedBy(Empty(), id));
        Assert.False(MapPlaceableWriter.ShippedBy(new byte[] { 1, 2, 3 }, id));
    }

    [Fact]
    public void TheInspectorOffersIt()
    {
        string? inspector = null, host = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && inspector is null; dir = dir.Parent)
        {
            string app = Path.Combine(dir.FullName, "src", "ReyEngine.App");
            if (!Directory.Exists(app)) continue;
            inspector = File.ReadAllText(Path.Combine(app, "Views", "SceneObjectInspectorView.axaml"));
            host = File.ReadAllText(Path.Combine(app, "ViewModels", "MainWindowViewModel.CharacterCreator.cs"));
        }
        if (inspector is null) return;
        Assert.Contains("{Binding ConvertPropToClientSideCommand}", inspector);
        Assert.Contains("MapPlaceableWriter.WhyNotConvertible(source, id)", host);
        Assert.Contains("if (MapContent.HasPlacementEdits)", host);   // the reload would drop them
        Assert.Contains("MapPlaceableWriter.ShippedBy(riot, id)", host);   // Riot's own are refused
    }
}
