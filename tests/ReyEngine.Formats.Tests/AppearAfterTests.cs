using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M748 (experimental): a client-side prop that appears only after N seconds of game time.
///
/// <para>Written as a <c>LogicDriverVisibilityController</c> of the placement's own - <c>PathHash</c> first,
/// as on every controller Riot ships - whose <c>VisibilityDriver</c> is
/// <c>FloatComparisonMaterialDriver { TimeMaterialDriver{} , FloatLiteral N, mOperator 1 }</c>, linked as the
/// placement's last field. Operator 1 reads as "greater than" from Riot's own ladders. Whether the game
/// honours a logic-driven controller on a map placement is not known - Riot ships none - and is what the
/// in-game test answers; these tests pin that what is written is what was meant.</para>
/// </summary>
public sealed class AppearAfterTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static byte[] Map()
    {
        var chars = new BinTreeObject(H("chars"), H("MapPlaceableContainer"), new BinTreeProperty[]
        {
            new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, new[]
            {
                new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 1),
                    new BinTreeStruct(0, 0x9aa5b4bcu, new BinTreeProperty[] { new BinTreeMatrix44(H("transform"), Matrix4x4.Identity) })),
            }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { chars }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static (byte[] Bin, MapPlacementId Id) Place(float? seconds)
    {
        byte[] bin = Map();
        var id = MapPlaceableWriter.NewAnimatedPropId(new BinTree(new MemoryStream(bin, false)), H("Urf_1"));
        var output = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(id)
            {
                CreateAnimatedProp = true, Name = "Urf_1", PropName = "Urf_Ghost_Jade", IdleAnimation = "Idle1",
                Transform = Matrix4x4.CreateTranslation(3714, 30, 10401), AppearAfterSeconds = seconds,
            },
        }, out string? error);
        Assert.Null(error);
        return (output!, id);
    }

    private static BinTreeStruct Item(BinTree tree, MapPlacementId id) =>
        (BinTreeStruct)((BinTreeMap)tree.Objects[id.ContainerHash].Properties[H("items")])
            .First(e => e.Key is BinTreeHash k && k.Value == id.ItemKey).Value;

    [Fact]
    public void TheGateIsTheControllerRiotsClassesSpell()
    {
        var (bin, id) = Place(60);
        var tree = new BinTree(new MemoryStream(bin, false));
        uint ctrlHash = H(MapPlaceableWriter.AppearAfterControllerPath(id));

        // the placement links it, as its LAST field
        var item = Item(tree, id);
        Assert.Equal(H("VisibilityController"), item.Properties.Keys.Last());
        Assert.Equal(ctrlHash, Assert.IsType<BinTreeObjectLink>(item.Properties[H("VisibilityController")]).Value);

        // the controller: PathHash first, then the driver
        var ctrl = tree.Objects[ctrlHash];
        Assert.Equal(H("LogicDriverVisibilityController"), ctrl.ClassHash);
        Assert.Equal(new[] { H("PathHash"), H("VisibilityDriver") }, ctrl.Properties.Keys.ToArray());
        Assert.Equal(ctrlHash, Assert.IsType<BinTreeHash>(ctrl.Properties[H("PathHash")]).Value);

        var cmp = Assert.IsAssignableFrom<BinTreeStruct>(ctrl.Properties[H("VisibilityDriver")]);
        Assert.Equal(H("FloatComparisonMaterialDriver"), cmp.ClassHash);
        var a = Assert.IsAssignableFrom<BinTreeStruct>(cmp.Properties[H("mValueA")]);
        Assert.Equal(H("TimeMaterialDriver"), a.ClassHash);
        Assert.Empty(a.Properties);                      // Riot's most common form, 867 of them
        var b = Assert.IsAssignableFrom<BinTreeStruct>(cmp.Properties[H("mValueB")]);
        Assert.Equal(H("FloatLiteralMaterialDriver"), b.ClassHash);
        Assert.Equal(60f, Assert.IsType<BinTreeF32>(b.Properties[H("mValue")]).Value);
        Assert.Equal(1u, Assert.IsType<BinTreeU32>(cmp.Properties[H("mOperator")]).Value);   // greater than
    }

    [Fact]
    public void WithoutAGateNothingIsAdded()
    {
        var (bin, id) = Place(null);
        var tree = new BinTree(new MemoryStream(bin, false));
        Assert.False(Item(tree, id).Properties.ContainsKey(H("VisibilityController")));
        Assert.Single(tree.Objects);   // just the container
    }

    [Fact]
    public void TheEditorReadsTheSecondsBack()
    {
        var (bin, _) = Place(90);
        var prop = Assert.Single(MapPlaceableExtractor.Extract(bin).Props);
        Assert.Equal(90f, prop.AppearAfterSeconds);
        Assert.Null(Assert.Single(MapPlaceableExtractor.Extract(Place(null).Bin).Props).AppearAfterSeconds);
    }

    [Fact]
    public void ChangingTheTimeReplacesTheControllerRatherThanAddingOne()
    {
        var (bin, id) = Place(60);
        bin = MapPlaceableWriter.WriteEdits(bin, new[] { new MapPlacementEdit(id) { AppearAfterSeconds = 120 } }, out string? error)!;
        Assert.Null(error);
        var tree = new BinTree(new MemoryStream(bin, false));
        Assert.Equal(2, tree.Objects.Count);
        Assert.Equal(120f, MapPlaceableWriter.ReadAppearAfter(tree, Item(tree, id)));
    }

    [Fact]
    public void ZeroRemovesTheGateAndItsController()
    {
        var (bin, id) = Place(60);
        bin = MapPlaceableWriter.WriteEdits(bin, new[] { new MapPlacementEdit(id) { AppearAfterSeconds = 0 } }, out string? error)!;
        Assert.Null(error);
        var tree = new BinTree(new MemoryStream(bin, false));
        Assert.Single(tree.Objects);
        Assert.False(Item(tree, id).Properties.ContainsKey(H("VisibilityController")));
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void ANonsenseTimeIsRefused(float seconds)
    {
        byte[] bin = Map();
        var id = MapPlaceableWriter.NewAnimatedPropId(new BinTree(new MemoryStream(bin, false)), 1);
        var output = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(id) { CreateAnimatedProp = true, PropName = "X", Transform = Matrix4x4.Identity, AppearAfterSeconds = seconds },
        }, out string? error);
        Assert.Null(output);
        Assert.NotNull(error);
    }

    [Fact]
    public void TheWindowsCarryTheTimeAndLeaveItOffByDefault()
    {
        Assert.Equal(0m, new AddPropViewModel().AppearAfterSeconds);
        Assert.Equal(0m, new CharacterCreatorViewModel().AppearAfterSeconds);
        Assert.Equal(0f, new AddPropRequest("X", "r", "s", null).AppearAfterSeconds);

        string? host = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && host is null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.CharacterCreator.cs");
            if (File.Exists(p)) host = File.ReadAllText(p);
        }
        if (host is null) return;
        Assert.Contains("request.AsAnimatedProp, request.AppearAfterSeconds", host);
        Assert.Contains("AppearAfterSeconds = appearAfterSeconds > 0 ? appearAfterSeconds : null,", host);
    }
}
