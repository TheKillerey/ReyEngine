using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

public sealed class MapSkinSwitcherTests
{
    private static readonly uint MapHash = H("Maps/Shipping/Map11");
    private static readonly uint TargetHash = H("Maps/Shipping/Map11/MapSkins/Default");
    private static readonly uint SourceHash = H("Maps/Shipping/Map11/MapSkins/Milkshake_SRS");
    private static readonly uint ThirdHash = H("Maps/Shipping/Map11/MapSkins/OtherMode");
    private const uint SpawnOverridesField = 0x2d3285eb;
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    [Fact]
    public void Environment_route_changes_target_but_spawn_time_runtime_data_stays_with_its_slot()
    {
        byte[] input = BuildWithThird("SR");
        var before = SafeBinTree.Parse(input);

        var result = MapSkinSwitcher.Switch(input, 11, TargetHash, SourceHash);
        var after = SafeBinTree.Parse(result.Bytes);
        var target = after.Objects[TargetHash];
        var source = after.Objects[SourceHash];
        var third = after.Objects[ThirdHash];

        Assert.Equal("Default", Assert.IsType<BinTreeString>(target.Properties[H("name")]).Value);
        Assert.Equal("Maps/MapGeometry/Map11/Milkshake_SRS",
            Assert.IsType<BinTreeString>(target.Properties[H("mMapContainerLink")]).Value);
        Assert.Equal("ASSETS/Maps/NavGrid/Map11/Default.aimesh_ngrid",
            Assert.IsType<BinTreeString>(target.Properties[H("mNavigationMesh")]).Value);
        Assert.Equal("default spawn data",
            Assert.IsType<BinTreeString>(target.Properties[SpawnOverridesField]).Value);
        Assert.True(target.Properties.ContainsKey(H("targetOnly")));
        Assert.False(target.Properties.ContainsKey(H("mAlternateAssets")));
        Assert.False(target.Properties.ContainsKey(H("mResourceResolvers")));

        Assert.Equal("OtherMode", Assert.IsType<BinTreeString>(third.Properties[H("name")]).Value);
        Assert.Equal("Maps/MapGeometry/Map11/OtherMode",
            Assert.IsType<BinTreeString>(third.Properties[H("mMapContainerLink")]).Value);
        Assert.Equal("other mode spawn data",
            Assert.IsType<BinTreeString>(third.Properties[SpawnOverridesField]).Value);
        Assert.True(third.Properties.ContainsKey(H("thirdOnly")));

        Assert.Equal("Milkshake_SRS", Assert.IsType<BinTreeString>(source.Properties[H("name")]).Value);
        Assert.True(BinPropEquality.ObjectsEqual(before.Objects[SourceHash], source));
        Assert.True(BinPropEquality.PropsEqual(before.Objects[MapHash].Properties[H("mapSkins")],
            after.Objects[MapHash].Properties[H("mapSkins")]));
        Assert.True(BinPropEquality.ObjectsEqual(before.Objects[ThirdHash], third));
        Assert.Equal(new[] { TargetHash }, result.RoutedSkinHashes);
        Assert.Equal(4, result.ChangedRouteProperties);
        Assert.Contains("ASSETS/Maps/Info/Map11/GrassTint_Milkshake.tex",
            MapSkinSwitcher.AssetPaths(result.ReferencedStrings));
        Assert.DoesNotContain("ASSETS/Sounds/Wwise2016/SFX/Shared/Milkshake_events.bnk", result.ReferencedStrings);
    }

    [Fact]
    public void Catalog_exposes_only_registered_skin_objects()
    {
        var catalog = MapSkinSwitcher.ReadCatalog(Build("SR", includeSourceLink: false));

        Assert.Equal("SR", catalog.MapStringId);
        var only = Assert.Single(catalog.Skins);
        Assert.Equal("Default", only.Name);
        Assert.Equal("Maps/MapGeometry/Map11/Base_SRX", only.MapContainerLink);
    }

    [Theory]
    [InlineData(22, "SR")]
    [InlineData(11, "TFT")]
    public void Tft_maps_are_blocked_by_id_and_by_shipping_bin_identity(int mapId, string mode)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MapSkinSwitcher.Switch(Build(mode, includeSourceLink: true), mapId, TargetHash, SourceHash));

        Assert.Contains("paid cosmetics", ex.Message);
    }

    [Fact]
    public void Unregistered_source_cannot_be_injected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MapSkinSwitcher.Switch(Build("SR", includeSourceLink: false), 11, TargetHash, SourceHash));

        Assert.Contains("not a registered", ex.Message);
    }

    [Fact]
    public void Missing_registered_object_is_rejected_before_any_rewrite()
    {
        byte[] broken = Write(new BinTree(new[]
        {
            MapObject("SR", TargetHash, 0xDEADu),
            TargetObject(),
        }, Array.Empty<string>()));

        var ex = Assert.Throws<InvalidDataException>(() => MapSkinSwitcher.ReadCatalog(broken));

        Assert.Contains("missing or non-MapSkin", ex.Message);
    }

    [Theory]
    [InlineData("Maps/MapGeometry/Map11/Milkshake_SRS", "data/Maps/MapGeometry/Map11/Milkshake_SRS.materials.bin")]
    [InlineData("data/maps/mapgeometry/map12/base", "data/maps/mapgeometry/map12/base.materials.bin")]
    [InlineData(null, null)]
    public void Container_links_resolve_to_companion_material_bins(string? link, string? expected) =>
        Assert.Equal(expected, MapSkinSwitcher.ContainerBinPath(link));

    [Fact]
    public void View_model_defaults_to_Default_and_never_offers_the_target_as_its_source()
    {
        var catalog = MapSkinSwitcher.ReadCatalog(Build("SR", includeSourceLink: true));
        var map = new MapSkinMapViewModel
        {
            MapId = 11,
            ShippingBinEntry = new WadAssetEntry { PathHash = 11, Path = "data/maps/shipping/map11/map11.bin" },
            Catalog = catalog,
        };

        var vm = new MapSkinSwitcherViewModel(new[] { map });

        Assert.Equal("Default", vm.SelectedTarget!.Info.Name);
        Assert.Equal("Milkshake_SRS", Assert.Single(vm.SourceSkins).Info.Name);
        Assert.True(vm.CanApply);
        Assert.Contains("keeps its runtime identity", vm.SwapSummary);
    }

    private static byte[] Build(string mode, bool includeSourceLink)
    {
        var links = includeSourceLink ? new[] { TargetHash, SourceHash } : new[] { TargetHash };
        return Write(new BinTree(new[] { MapObject(mode, links), TargetObject(), SourceObject() }, Array.Empty<string>()));
    }

    private static byte[] BuildWithThird(string mode) => Write(new BinTree(new[]
    {
        MapObject(mode, TargetHash, SourceHash, ThirdHash),
        TargetObject(),
        SourceObject(),
        ThirdObject(),
    }, Array.Empty<string>()));

    private static BinTreeObject MapObject(string mode, params uint[] skins) => new(MapHash, H("Map"), new BinTreeProperty[]
    {
        new BinTreeString(H("mapStringId"), mode),
        new BinTreeUnorderedContainer(H("mapSkins"), BinPropertyType.ObjectLink,
            skins.Select(hash => new BinTreeObjectLink(0, hash))),
    });

    private static BinTreeObject TargetObject() => new(TargetHash, H("MapSkin"), new BinTreeProperty[]
    {
        new BinTreeString(H("name"), "Default"),
        new BinTreeString(H("mMapContainerLink"), "Maps/MapGeometry/Map11/Base_SRX"),
        new BinTreeString(H("mMapObjectsCFG"), "ASSETS/Maps/Default.cfg"),
        new BinTreeString(H("mWorldParticlesINI"), "ASSETS/Maps/Default.ini"),
        new BinTreeString(H("mGrassTintTexture"), "ASSETS/Maps/Info/Map11/GrassTint_Default.tex"),
        new BinTreeString(H("mNavigationMesh"), "ASSETS/Maps/NavGrid/Map11/Default.aimesh_ngrid"),
        new BinTreeString(SpawnOverridesField, "default spawn data"),
        new BinTreeString(H("targetOnly"), "must survive"),
    });

    private static BinTreeObject SourceObject() => new(SourceHash, H("MapSkin"), new BinTreeProperty[]
    {
        new BinTreeString(H("name"), "Milkshake_SRS"),
        new BinTreeString(H("mMapContainerLink"), "Maps/MapGeometry/Map11/Milkshake_SRS"),
        new BinTreeString(H("mMapObjectsCFG"), "ASSETS/Maps/Milkshake.cfg"),
        new BinTreeString(H("mWorldParticlesINI"), "ASSETS/Maps/Milkshake.ini"),
        new BinTreeString(H("mGrassTintTexture"), "ASSETS/Maps/Info/Map11/GrassTint_Milkshake.tex"),
        new BinTreeString(H("mNavigationMesh"), "ASSETS/Maps/NavGrid/Map11/AIPath_SRX_2.aimesh_ngrid"),
        new BinTreeString(SpawnOverridesField, "milkshake spawn data that crashes Default"),
        new BinTreeContainer(H("mResourceResolvers"), BinPropertyType.ObjectLink, new BinTreeProperty[]
        {
            new BinTreeObjectLink(0, H("MilkshakeResolver")),
        }),
        new BinTreeEmbedded(H("mAlternateAssets"), H("MapAlternateAssets"), new BinTreeProperty[]
        {
            new BinTreeString(H("texture"), "ASSETS/Maps/Info/Map11/Milkshake.tex"),
            new BinTreeContainer(H("banks"), BinPropertyType.String, new BinTreeProperty[]
            {
                new BinTreeString(0, "ASSETS/Sounds/Wwise2016/SFX/Shared/Milkshake_events.bnk"),
            }),
        }),
    });

    private static BinTreeObject ThirdObject() => new(ThirdHash, H("MapSkin"), new BinTreeProperty[]
    {
        new BinTreeString(H("name"), "OtherMode"),
        new BinTreeString(H("mMapContainerLink"), "Maps/MapGeometry/Map11/OtherMode"),
        new BinTreeString(H("mMapObjectsCFG"), "ASSETS/Maps/Other.cfg"),
        new BinTreeString(H("mWorldParticlesINI"), "ASSETS/Maps/Other.ini"),
        new BinTreeString(H("mGrassTintTexture"), "ASSETS/Maps/Info/Map11/GrassTint_Other.tex"),
        new BinTreeString(SpawnOverridesField, "other mode spawn data"),
        new BinTreeString(H("thirdOnly"), "must survive"),
    });

    private static byte[] Write(BinTree tree)
    {
        using var stream = new MemoryStream();
        tree.Write(stream);
        return stream.ToArray();
    }
}
