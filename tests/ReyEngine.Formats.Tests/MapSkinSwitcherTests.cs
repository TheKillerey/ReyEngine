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
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    [Fact]
    public void Complete_source_definition_replaces_target_but_target_identity_and_table_survive()
    {
        byte[] input = Build("SR", includeSourceLink: true);
        var before = SafeBinTree.Parse(input);

        var result = MapSkinSwitcher.Switch(input, 11, TargetHash, SourceHash);
        var after = SafeBinTree.Parse(result.Bytes);
        var target = after.Objects[TargetHash];
        var source = after.Objects[SourceHash];

        Assert.Equal("Default", Assert.IsType<BinTreeString>(target.Properties[H("name")]).Value);
        Assert.Equal("Maps/MapGeometry/Map11/Milkshake_SRS",
            Assert.IsType<BinTreeString>(target.Properties[H("mMapContainerLink")]).Value);
        Assert.Equal("ASSETS/Maps/NavGrid/Map11/AIPath_SRX_2.aimesh_ngrid",
            Assert.IsType<BinTreeString>(target.Properties[H("mNavigationMesh")]).Value);
        Assert.True(target.Properties.ContainsKey(H("mAlternateAssets")));
        Assert.False(target.Properties.ContainsKey(H("targetOnly")));
        Assert.Equal("Milkshake_SRS", Assert.IsType<BinTreeString>(source.Properties[H("name")]).Value);
        Assert.True(BinPropEquality.ObjectsEqual(before.Objects[SourceHash], source));
        Assert.True(BinPropEquality.PropsEqual(before.Objects[MapHash].Properties[H("mapSkins")],
            after.Objects[MapHash].Properties[H("mapSkins")]));
        Assert.Contains("ASSETS/Sounds/Wwise2016/SFX/Shared/Milkshake_events.bnk", result.ReferencedStrings);
        Assert.Contains("ASSETS/Maps/NavGrid/Map11/AIPath_SRX_2.aimesh_ngrid",
            MapSkinSwitcher.AssetPaths(result.ReferencedStrings));
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
        Assert.Contains("keeps its slot identity", vm.SwapSummary);
    }

    private static byte[] Build(string mode, bool includeSourceLink)
    {
        var links = includeSourceLink ? new[] { TargetHash, SourceHash } : new[] { TargetHash };
        return Write(new BinTree(new[] { MapObject(mode, links), TargetObject(), SourceObject() }, Array.Empty<string>()));
    }

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
        new BinTreeString(H("targetOnly"), "must disappear"),
    });

    private static BinTreeObject SourceObject() => new(SourceHash, H("MapSkin"), new BinTreeProperty[]
    {
        new BinTreeString(H("name"), "Milkshake_SRS"),
        new BinTreeString(H("mMapContainerLink"), "Maps/MapGeometry/Map11/Milkshake_SRS"),
        new BinTreeString(H("mNavigationMesh"), "ASSETS/Maps/NavGrid/Map11/AIPath_SRX_2.aimesh_ngrid"),
        new BinTreeEmbedded(H("mAlternateAssets"), H("MapAlternateAssets"), new BinTreeProperty[]
        {
            new BinTreeString(H("texture"), "ASSETS/Maps/Info/Map11/Milkshake.tex"),
            new BinTreeContainer(H("banks"), BinPropertyType.String, new BinTreeProperty[]
            {
                new BinTreeString(0, "ASSETS/Sounds/Wwise2016/SFX/Shared/Milkshake_events.bnk"),
            }),
        }),
    });

    private static byte[] Write(BinTree tree)
    {
        using var stream = new MemoryStream();
        tree.Write(stream);
        return stream.ToArray();
    }
}
