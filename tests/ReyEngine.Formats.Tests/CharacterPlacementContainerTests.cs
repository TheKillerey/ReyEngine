using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M746: a placed character goes into the container the map keeps its characters in.
///
/// <para>Every prop the Character Creator placed on the ported Map453 failed to spawn in game - the URF
/// ghost, and then a plain S3Yonkey placed as a control, although Riot's own S3Yonkeys on the same map
/// spawn. The placements were byte-for-byte the shape Riot writes. What differed was WHERE: the writer
/// used "the first MapPlaceableContainer", and <c>mapContainer.chunks</c> names those containers as layers.
/// On Map453 the first is a scratch layer holding one test light, fifteen null entries and two markers,
/// and no character; Riot's characters live in three other chunks. Across the shipped maps the first
/// container holds characters in 1 of the 25 bins that have any.</para>
/// </summary>
public sealed class CharacterPlacementContainerTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private const uint SceneryCharacter = 0x9aa5b4bcu;
    private const uint UnitCharacter = 0xad65d8c4u;

    private static BinTreeStruct Item(uint cls) =>
        new(0, cls, new BinTreeProperty[] { new BinTreeMatrix44(H("transform"), Matrix4x4.Identity) });

    private static BinTreeObject Container(string name, params uint[] itemClasses)
    {
        var items = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct,
            itemClasses.Select((c, i) => new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                new BinTreeHash(0, H(name + i)), Item(c))));
        return new BinTreeObject(H(name), H("MapPlaceableContainer"), new BinTreeProperty[] { items });
    }

    private static BinTree Tree(params BinTreeObject[] containers) =>
        new(containers, Array.Empty<string>());

    [Fact]
    public void ACharacterGoesWhereTheMapKeepsItsCharacters_NotIntoTheFirstContainer()
    {
        // Map453's shape: a scratch layer first, the characters in later chunks.
        var tree = Tree(
            Container("scratch", H("MapDynamicPointLight"), 0u, 0u),
            Container("vfx", H("MapParticle"), H("MapParticle"), SceneryCharacter),
            Container("structures", UnitCharacter, UnitCharacter, SceneryCharacter, SceneryCharacter),
            Container("jungle", UnitCharacter));

        Assert.Equal(H("scratch"), MapPlaceableWriter.NewParticleId(tree, H("Urf_1")).ContainerHash);

        var id = MapPlaceableWriter.NewCharacterId(tree, H("Urf_1"));
        Assert.True(id.IsValid);
        Assert.Equal(H("structures"), id.ContainerHash);
    }

    [Fact]
    public void AttackableCharactersCountTooWhenPickingTheContainer()
    {
        // The jade turrets, inhibitors and nexus are the attackable class; a map whose scenery characters
        // are few still keeps its characters with them.
        var tree = Tree(
            Container("scratch", 0u),
            Container("oneProp", SceneryCharacter),
            Container("units", UnitCharacter, UnitCharacter, UnitCharacter));

        Assert.Equal(H("units"), MapPlaceableWriter.NewCharacterId(tree, H("Yonkey_3")).ContainerHash);
    }

    [Fact]
    public void AMapWithNoCharactersFallsBackToTheOldChoice()
    {
        var tree = Tree(Container("first", H("MapParticle")), Container("second", H("MapAudio")));
        Assert.Equal(MapPlaceableWriter.NewParticleId(tree, H("X")), MapPlaceableWriter.NewCharacterId(tree, H("X")));
    }

    [Fact]
    public void TheNewKeyIsFreeInTheChosenContainer()
    {
        var tree = Tree(Container("scratch", 0u), Container("chars", SceneryCharacter, SceneryCharacter));
        var id = MapPlaceableWriter.NewCharacterId(tree, H("Urf_1"));

        var items = (BinTreeMap)tree.Objects[H("chars")].Properties[H("items")];
        Assert.DoesNotContain(items, e => e.Key is BinTreeHash k && k.Value == id.ItemKey);
    }

    [Fact]
    public void ThePlacementIsWrittenIntoThatContainer()
    {
        var tree = Tree(Container("scratch", 0u), Container("chars", SceneryCharacter));
        byte[] bin;
        using (var ms = new MemoryStream()) { tree.Write(ms); bin = ms.ToArray(); }

        var id = MapPlaceableWriter.NewCharacterId(new BinTree(new MemoryStream(bin, false)), H("Urf_1"));
        var output = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(id)
            {
                CreateCharacter = true,
                Name = "Urf_1",
                Transform = Matrix4x4.CreateTranslation(3714, 30, 10401),
                CharacterRecord = "Characters/Urf_Ghost_Jade/CharacterRecords/Root",
                Skin = "Characters/Urf_Ghost_Jade/Skins/Skin0",
            },
        }, out string? error);

        Assert.Null(error);
        var back = new BinTree(new MemoryStream(output!, false));
        var chars = (BinTreeMap)back.Objects[H("chars")].Properties[H("items")];
        var scratch = (BinTreeMap)back.Objects[H("scratch")].Properties[H("items")];
        Assert.Equal(2, chars.Count);
        Assert.Single(scratch);
    }

    [Fact]
    public void TheCharacterCreatorUsesIt()
    {
        string? src = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && src is null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.CharacterCreator.cs");
            if (File.Exists(p)) src = File.ReadAllText(p);
        }
        if (src is null) return;
        Assert.Contains("MapPlaceableWriter.NewCharacterId(tree, HashAlgorithms.Fnv1a(placementName))", src);
        Assert.DoesNotContain("MapPlaceableWriter.NewParticleId(tree, HashAlgorithms.Fnv1a(placementName))", src);
    }
}
