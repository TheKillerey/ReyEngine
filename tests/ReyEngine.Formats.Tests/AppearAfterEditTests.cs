using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M750: the game-clock gate of a placed client-side prop is edited in place from the inspector, so
/// trying another time no longer means removing the prop and placing it again. The value travels with the
/// prop's other edits and is saved by Save Map Content Edits through the same writer verb M748 added.
/// </summary>
public sealed class AppearAfterEditTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static byte[] MapWithProp(float? seconds, out MapPlacementId id)
    {
        var chars = new BinTreeObject(H("chars"), H("MapPlaceableContainer"), new BinTreeProperty[]
        {
            new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, new[]
            {
                new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 1),
                    new BinTreeStruct(0, 0x9aa5b4bcu, new BinTreeProperty[] { new BinTreeMatrix44(H("transform"), Matrix4x4.Identity) })),
            }),
        });
        byte[] bin;
        using (var ms = new MemoryStream()) { new BinTree(new[] { chars }, Array.Empty<string>()).Write(ms); bin = ms.ToArray(); }
        id = MapPlaceableWriter.NewAnimatedPropId(new BinTree(new MemoryStream(bin, false)), H("Urf_1"));
        return MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(id)
            {
                CreateAnimatedProp = true, Name = "Urf_1", PropName = "Urf_Ghost_Jade", IdleAnimation = "Idle1",
                Transform = Matrix4x4.Identity, AppearAfterSeconds = seconds,
            },
        }, out _)!;
    }

    private static AnimatedPropViewModel Node(byte[] bin) =>
        new() { Prop = Assert.Single(MapPlaceableExtractor.Extract(bin).Props) };

    /// <summary>The edit the save path builds for this node - the same expression, kept in step by the
    /// source pin below.</summary>
    private static MapPlacementEdit EditFor(AnimatedPropViewModel p) => new(p.Prop.Id)
    {
        AppearAfterSeconds = p.AppearAfterChanged ? (float)p.AppearAfterSeconds : null,
    };

    [Fact]
    public void TheNodeShowsTheTimeTheMapHolds()
    {
        var node = Node(MapWithProp(60, out _));
        Assert.True(node.CanAppearAfter);
        Assert.Equal(60m, node.AppearAfterSeconds);
        Assert.False(node.AppearAfterChanged);
        Assert.False(node.HasEdits);
    }

    [Fact]
    public void ChangingItIsAnEditAndSettingItBackIsNot()
    {
        var node = Node(MapWithProp(60, out _));
        node.AppearAfterSeconds = 120;
        Assert.True(node.AppearAfterChanged);
        Assert.True(node.HasEdits);

        node.AppearAfterSeconds = 60;
        Assert.False(node.AppearAfterChanged);
        Assert.False(node.HasEdits);
    }

    [Fact]
    public void SavingChangesTheTimeInPlace()
    {
        byte[] bin = MapWithProp(60, out var id);
        var node = Node(bin);
        node.AppearAfterSeconds = 120;

        bin = MapPlaceableWriter.WriteEdits(bin, new[] { EditFor(node) }, out string? error)!;
        Assert.Null(error);
        var reread = Node(bin);
        Assert.Equal(id, reread.Prop.Id);                 // the same placement, not a new one
        Assert.Equal(120m, reread.AppearAfterSeconds);
        Assert.Equal(2, new BinTree(new MemoryStream(bin, false)).Objects.Count);   // one controller, replaced
    }

    [Fact]
    public void AGateCanBeAddedToAPropPlacedWithoutOne()
    {
        byte[] bin = MapWithProp(null, out _);
        var node = Node(bin);
        Assert.Equal(0m, node.AppearAfterSeconds);
        node.AppearAfterSeconds = 45;

        bin = MapPlaceableWriter.WriteEdits(bin, new[] { EditFor(node) }, out string? error)!;
        Assert.Null(error);
        Assert.Equal(45m, Node(bin).AppearAfterSeconds);
    }

    [Fact]
    public void ZeroRemovesTheGate()
    {
        byte[] bin = MapWithProp(60, out _);
        var node = Node(bin);
        node.AppearAfterSeconds = 0;

        bin = MapPlaceableWriter.WriteEdits(bin, new[] { EditFor(node) }, out string? error)!;
        Assert.Null(error);
        Assert.Null(Node(bin).Prop.AppearAfterSeconds);
        Assert.Single(new BinTree(new MemoryStream(bin, false)).Objects);   // the controller went with it
    }

    [Fact]
    public void ACharacterPlacementOffersNoGate()
    {
        // Server-spawned (M747): a client-side gate would be written and never read.
        var node = new AnimatedPropViewModel
        {
            Prop = new MapAnimatedProp("S3Yonkey_1", Vector3.Zero, Matrix4x4.Identity,
                "Characters/S3Yonkey/CharacterRecords/Root", "Characters/S3Yonkey/Skins/Skin0"),
        };
        Assert.False(node.CanAppearAfter);
        node.AppearAfterSeconds = 60;
        Assert.False(node.AppearAfterChanged);
        Assert.False(node.HasEdits);
    }

    [Fact]
    public void TheSavePathAndTheInspectorCarryIt()
    {
        string? host = null, inspector = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && host is null; dir = dir.Parent)
        {
            string app = Path.Combine(dir.FullName, "src", "ReyEngine.App");
            if (!Directory.Exists(app)) continue;
            host = File.ReadAllText(Path.Combine(app, "ViewModels", "MainWindowViewModel.cs"));
            inspector = File.ReadAllText(Path.Combine(app, "Views", "SceneObjectInspectorView.axaml"));
        }
        if (host is null) return;
        Assert.Contains("AppearAfterSeconds = p.AppearAfterChanged ? (float)p.AppearAfterSeconds : null,", host);
        Assert.Contains("{Binding SelectedPropNode.AppearAfterSeconds}", inspector);
        Assert.Contains("{Binding SelectedPropNode.CanAppearAfter}", inspector);
    }
}
