using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

public class MapVisibilityTests
{
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    [Fact]
    public void ReadsAxesStatesAndIndependentInitialMasksFromMapObject()
    {
        var tree = new BinTree(new[]
        {
            new BinTreeObject(1, H("Map"), new BinTreeProperty[]
            {
                new BinTreeU8(H("InitialVisibilityMask"), 67),
                Definition(H("VisibilityFlagDefines"), ("Base", null, 6), ("Stage 1", null, 2)),
                new BinTreeU8(0x30eafcaa, 1),
                Definition(0xd31ac6ce, ("Default", "Default", 0), ("Changed", "Changed", 3)),
            }),
        }, Array.Empty<string>());

        var parsed = MapVisibility.Parse(Write(tree));

        Assert.Equal(2, parsed.Axes.Count);
        Assert.Equal(67, parsed.Axes[0].InitialMask);
        Assert.Equal(new[] { 4, 64 }, parsed.Axes[0].Layers.Select(l => l.Bit));
        Assert.Equal("Baron Pit", parsed.Axes[1].Name);
        Assert.Equal(1, parsed.Axes[1].InitialMask);
        Assert.Equal(new[] { "Default", "Changed" }, parsed.Axes[1].Layers.Select(l => l.Name));
    }

    [Fact]
    public void InitialMaskIsActiveAlongsideSelectedState()
    {
        var axis = new MapVisibilityAxis(MapVisibility.PrimaryAxisHash, "Map Visibility", 67, true,
            new[] { new VisibilityLayer("Stage 2", 8) });

        Assert.True(MapVisibility.VisibleForMask(64, axis, 8));
        Assert.True(MapVisibility.VisibleForMask(8, axis, 8));
        Assert.False(MapVisibility.VisibleForMask(16, axis, 8));
        Assert.True(MapVisibility.VisibleForMask(255, axis, 8));
        Assert.True(MapVisibility.VisibleForMask(16, axis, 0));
    }

    [Fact]
    public void UnknownStateHashGetsStableUsefulFallback()
    {
        const uint unknown = 0xfb5eebdf;
        var tree = new BinTree(new[]
        {
            new BinTreeObject(1, H("Map"), new BinTreeProperty[]
            {
                Definition(H("VisibilityFlagDefines"), (null, null, 1, unknown)),
            }),
        }, Array.Empty<string>());

        var layer = Assert.Single(MapVisibility.Parse(Write(tree)).Primary!.Layers);
        Assert.Equal("Layer 2 [0xfb5eebdf]", layer.Name);
        Assert.Equal(2, layer.Bit);
    }

    [Fact]
    public void SecondaryStatesDoNotAlsoActivateTheirInitialBaseController()
    {
        var secondary = new MapVisibilityAxis(MapVisibility.BaronPitAxisHash, "Baron Pit", 1, false,
            new[] { new VisibilityLayer("Base", 1), new VisibilityLayer("Cup", 2) });
        var definition = new MapVisibilityDefinition(new[] { secondary });
        byte[] controllersBin = Write(new BinTree(new[]
        {
            new BinTreeObject(0x100, 0xec733fe2, new BinTreeProperty[] { new BinTreeU8(0x8bff8cdf, 1) }),
        }, Array.Empty<string>()));
        var resolver = new MapVisibilityResolver(MapVisibilityControllers.Build(new[] { controllersBin }, definition), definition);

        Assert.True(resolver.IsVisible(255, 0x100, new Dictionary<uint, int> { [MapVisibility.BaronPitAxisHash] = 1 }));
        Assert.False(resolver.IsVisible(255, 0x100, new Dictionary<uint, int> { [MapVisibility.BaronPitAxisHash] = 2 }));
    }

    [Fact]
    public void InvertedCustomControllerExcludesOnlyItsOwnPrimaryState()
    {
        var primary = new MapVisibilityAxis(MapVisibility.PrimaryAxisHash, "Map Visibility", 1, true,
            new[] { new VisibilityLayer("Base", 1), new VisibilityLayer("Infernal", 2), new VisibilityLayer("Mountain", 4) });
        var definition = new MapVisibilityDefinition(new[] { primary });
        byte[] controllersBin = Write(new BinTree(new[]
        {
            new BinTreeObject(0x200, 0xc406a533, new BinTreeProperty[] { new BinTreeU8(0x27639032, 4) }),
            new BinTreeObject(0x201, 0xe21083b5, new BinTreeProperty[]
            {
                new BinTreeContainer(0x3044938a, BinPropertyType.ObjectLink,
                    new BinTreeProperty[] { new BinTreeObjectLink(0, 0x200) }),
                new BinTreeU32(0xc9d3f06a, 3),
            }),
        }, Array.Empty<string>()));
        var resolver = new MapVisibilityResolver(MapVisibilityControllers.Build(new[] { controllersBin }, definition), definition);

        Assert.True(resolver.IsVisible(123, 0x201, new Dictionary<uint, int> { [MapVisibility.PrimaryAxisHash] = 2 }));
        Assert.False(resolver.IsVisible(123, 0x201, new Dictionary<uint, int> { [MapVisibility.PrimaryAxisHash] = 4 }));
    }

    private static BinTreeStruct Definition(uint field, params (string? Name, string? Public, int Bit, uint Hash)[] states) =>
        new(field, H("MapVisibilityFlagDefinitions"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("FlagDefinitions"), BinPropertyType.Struct, states.Select(s =>
            {
                var properties = new List<BinTreeProperty>();
                if (s.Name is not null) properties.Add(new BinTreeString(H("name"), s.Name));
                else if (s.Hash != 0) properties.Add(new BinTreeHash(H("name"), s.Hash));
                if (s.Public is not null) properties.Add(new BinTreeString(H("PublicName"), s.Public));
                if (s.Bit != 0) properties.Add(new BinTreeU8(H("BitIndex"), (byte)s.Bit));
                return (BinTreeProperty)new BinTreeStruct(0, H("MapVisibilityFlagDefinition"), properties);
            }).ToArray()),
        });

    private static BinTreeStruct Definition(uint field, params (string? Name, string? Public, int Bit)[] states) =>
        Definition(field, states.Select(s => (s.Name, s.Public, s.Bit, 0u)).ToArray());

    private static byte[] Write(BinTree tree)
    {
        using var stream = new MemoryStream();
        tree.Write(stream);
        return stream.ToArray();
    }
}
