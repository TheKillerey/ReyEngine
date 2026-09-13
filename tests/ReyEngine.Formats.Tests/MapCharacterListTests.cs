using System;
using System.IO;
using System.Linq;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M722: a placed character is preloaded only when the map's own bin lists it. The URF prop on the
/// Halloween Map453 was placed and never listed, so the game drew it unskinned - a shader hash miss for a
/// vertex shader without NUM_BLEND_WEIGHTS and "Missing shader constant WORLD_MATRIX" - and it never
/// appeared.</summary>
public class MapCharacterListTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static BinTreeProperty Link(string path) => new BinTreeObjectLink(0, H(path));

    /// <summary>Map453's shape in miniature: a champion-ish list and a scenery list, every listed character
    /// with its own Character object.</summary>
    private static byte[] MapBin(bool withMap = true)
    {
        var objects = new System.Collections.Generic.List<BinTreeObject>
        {
            new(H("Lists/Units"), H("MapCharacterList"), new BinTreeProperty[]
            {
                new BinTreeUnorderedContainer(H("characters"), BinPropertyType.ObjectLink, new[] { Link("Characters/SRU_OrderMinionMelee") }),
            }),
            new(H("Lists/Scenery"), H("MapCharacterList"), new BinTreeProperty[]
            {
                new BinTreeUnorderedContainer(H("characters"), BinPropertyType.ObjectLink, new[] { Link("Characters/GiantWolf"), Link("Characters/Golem") }),
            }),
        };
        foreach (var name in new[] { "SRU_OrderMinionMelee", "GiantWolf", "Golem" })
            objects.Add(new BinTreeObject(H("Characters/" + name), H("Character"), new BinTreeProperty[] { new BinTreeString(H("name"), name) }));
        if (withMap)
            objects.Add(new BinTreeObject(H("Maps/Shipping/Map453"), H("Map"), new BinTreeProperty[]
            {
                new BinTreeString(H("mapStringId"), "JD"),
                new BinTreeUnorderedContainer(H("characterLists"), BinPropertyType.ObjectLink, new[] { Link("Lists/Units"), Link("Lists/Scenery") }),
            }));
        var tree = new BinTree(objects, Array.Empty<string>());
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static BinTree Read(byte[] bin) => new(new MemoryStream(bin, writable: false));

    [Fact]
    public void APlacedCharacterJoinsTheListHoldingTheMapsOtherScenery()
    {
        var bin = MapBin();
        var written = MapCharacterListWriter.Register(bin, new[] { "Urf" }, new[] { "GiantWolf", "Golem", "Urf" },
            out var added, out uint list, out var error);

        Assert.Null(error);
        Assert.NotNull(written);
        Assert.Equal(new[] { "Urf" }, added);
        Assert.Equal(H("Lists/Scenery"), list);

        var tree = Read(written!);
        var chars = Assert.IsType<BinTreeUnorderedContainer>(tree.Objects[H("Lists/Scenery")].Properties[H("characters")]);
        Assert.Equal(BinPropertyType.ObjectLink, chars.ElementType);
        Assert.Equal(new[] { H("Characters/GiantWolf"), H("Characters/Golem"), H("Characters/Urf") },
            chars.Elements.OfType<BinTreeObjectLink>().Select(l => l.Value));
        // the other list is untouched
        Assert.Single(((BinTreeContainer)tree.Objects[H("Lists/Units")].Properties[H("characters")]).Elements);

        // and the Character object every listed character has on Map453
        var character = tree.Objects[H("Characters/Urf")];
        Assert.Equal(H("Character"), character.ClassHash);
        Assert.Equal("Urf", Assert.IsType<BinTreeString>(character.Properties[H("name")]).Value);
    }

    [Fact]
    public void RegisteringTwiceChangesNothingTheSecondTime()
    {
        var once = MapCharacterListWriter.Register(MapBin(), new[] { "Urf" }, new[] { "GiantWolf" }, out _, out _, out _)!;
        var twice = MapCharacterListWriter.Register(once, new[] { "urf" }, new[] { "GiantWolf" }, out var added, out _, out var error);

        Assert.Null(error);
        Assert.Same(once, twice);
        Assert.Empty(added);
        Assert.Contains(H("Characters/Urf"), MapCharacterListWriter.Listed(once));
    }

    [Fact]
    public void AnAlreadyListedCharacterIsLeftWhereItIs()
    {
        var bin = MapBin();
        var written = MapCharacterListWriter.Register(bin, new[] { "Golem", "GiantWolf" }, new[] { "Golem" }, out var added, out _, out _);
        Assert.Same(bin, written);
        Assert.Empty(added);
    }

    [Fact]
    public void AnExistingCharacterObjectIsKeptAndNotDuplicated()
    {
        // a map that already has an (unlisted) Character object for Urf, spelled differently
        var bin = MapBin();
        var tree = Read(bin);
        tree.Objects[H("Characters/Urf")] = new BinTreeObject(H("Characters/Urf"), H("Character"),
            new BinTreeProperty[] { new BinTreeString(H("name"), "URF") });
        using (var ms = new MemoryStream()) { tree.Write(ms); bin = ms.ToArray(); }

        var written = MapCharacterListWriter.Register(bin, new[] { "Urf" }, Array.Empty<string>(), out _, out uint list, out _)!;
        var back = Read(written);
        Assert.Equal("URF", ((BinTreeString)back.Objects[H("Characters/Urf")].Properties[H("name")]).Value);
        Assert.Equal(tree.Objects.Count, back.Objects.Count);
        // nothing placed matches any list: the longer list takes it
        Assert.Equal(H("Lists/Scenery"), list);
    }

    [Fact]
    public void ABinWithoutAMapObjectIsRefused()
    {
        Assert.Null(MapCharacterListWriter.Register(MapBin(withMap: false), new[] { "Urf" }, new[] { "Urf" },
            out var added, out _, out var error));
        Assert.Empty(added);
        Assert.Contains("characterLists", error);
    }
}
