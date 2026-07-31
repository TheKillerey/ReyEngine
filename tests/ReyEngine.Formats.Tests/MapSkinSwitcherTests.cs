using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using System.Numerics;
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
    private static readonly uint AliasHash = H("Maps/Shipping/Map11/MapSkins/SR_Seasonal_Map");
    private static readonly uint TargetAudioHash = H("Audio/Default");
    private static readonly uint SourceAudioHash = H("Audio/Milkshake");
    private const uint SpawnOverridesField = 0x2d3285eb;
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    [Fact]
    public void Environment_route_changes_registered_slots_and_aliases_but_runtime_data_stays_with_each_object()
    {
        byte[] input = BuildWithThird("SR");
        var before = SafeBinTree.Parse(input);

        var result = MapSkinSwitcher.Switch(input, 11, TargetHash, SourceHash);
        var after = SafeBinTree.Parse(result.Bytes);
        var target = after.Objects[TargetHash];
        var source = after.Objects[SourceHash];
        var third = after.Objects[ThirdHash];
        var alias = after.Objects[AliasHash];

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
        Assert.Equal("Maps/MapGeometry/Map11/Milkshake_SRS",
            Assert.IsType<BinTreeString>(third.Properties[H("mMapContainerLink")]).Value);
        Assert.Equal("ASSETS/Maps/Milkshake.cfg",
            Assert.IsType<BinTreeString>(third.Properties[H("mMapObjectsCFG")]).Value);
        Assert.False(third.Properties.ContainsKey(H("mWorldParticlesINI")));
        Assert.Equal("other mode spawn data",
            Assert.IsType<BinTreeString>(third.Properties[SpawnOverridesField]).Value);
        Assert.True(third.Properties.ContainsKey(H("thirdOnly")));
        Assert.False(third.Properties.ContainsKey(H("mResourceResolvers")));

        Assert.Equal("SR_Seasonal_Map", Assert.IsType<BinTreeString>(alias.Properties[H("name")]).Value);
        Assert.Equal("Maps/MapGeometry/Map11/Milkshake_SRS",
            Assert.IsType<BinTreeString>(alias.Properties[H("mMapContainerLink")]).Value);
        Assert.Equal("alias runtime data", Assert.IsType<BinTreeString>(alias.Properties[SpawnOverridesField]).Value);

        Assert.Equal("Milkshake_SRS", Assert.IsType<BinTreeString>(source.Properties[H("name")]).Value);
        Assert.True(BinPropEquality.ObjectsEqual(before.Objects[SourceHash], source));
        Assert.True(BinPropEquality.PropsEqual(before.Objects[MapHash].Properties[H("mapSkins")],
            after.Objects[MapHash].Properties[H("mapSkins")]));
        Assert.Equal(new[] { TargetHash, ThirdHash, AliasHash }.Order(), result.RoutedSkinHashes.Order());
        Assert.Equal(11, result.ChangedRouteProperties);
        Assert.Equal(2, result.ChangedAudioProperties);
        Assert.Equal(TargetAudioHash, result.RoutedAudioTargetHash);
        Assert.Equal(SourceAudioHash, result.RoutedAudioSourceHash);
        var targetAudio = after.Objects[TargetAudioHash];
        Assert.Equal(H("Default"), Assert.IsType<BinTreeHash>(targetAudio.Properties[H("feature")]).Value);
        Assert.Contains("ASSETS/Sounds/Wwise2016/SFX/Shared/Milkshake_events.bnk",
            Strings(targetAudio.Properties[H("bankUnits")]));
        Assert.True(BinPropEquality.ObjectsEqual(before.Objects[SourceAudioHash], after.Objects[SourceAudioHash]));
        foreach (uint skinHash in result.RoutedSkinHashes)
        {
            var original = before.Objects[skinHash];
            var routed = after.Objects[skinHash];
            foreach (var (propertyHash, property) in original.Properties)
                if (!IsRouteField(propertyHash))
                    Assert.True(BinPropEquality.PropsEqual(property, routed.Properties[propertyHash]),
                        $"Runtime property 0x{propertyHash:x8} changed on slot 0x{skinHash:x8}.");
        }
        Assert.Contains("ASSETS/Maps/Info/Map11/GrassTint_Milkshake.tex",
            MapSkinSwitcher.AssetPaths(result.ReferencedStrings));
        Assert.Contains("ASSETS/Sounds/Wwise2016/SFX/Shared/Milkshake_events.bnk", result.ReferencedStrings);
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
        Assert.Contains("compatible gameplay IDs", vm.SwapSummary);
    }

    [Fact]
    public void Compatible_container_preserves_base_server_keys_and_source_authored_values()
    {
        byte[] target = Materials(
            Shop(0x1e1e8b6b, "Characters/sru_storekeepernorth/CharacterRecords/Root", 100, "Skin0"),
            Shop(0x9f77d47f, "Characters/sru_storekeepersouth/CharacterRecords/Root", 200, "Skin0"));
        byte[] source = Materials(
            Shop(0x4241132a, "Characters/sru_storekeepernorth/CharacterRecords/Root", 100, "Skin4"),
            Shop(0xd0c80c35, "Characters/sru_storekeepersouth/CharacterRecords/Root", 200, "Skin5"));

        var result = MapSkinSwitcher.BuildCompatibleContainer(target, source);
        var items = MaterialItems(result.Bytes);

        Assert.Equal(2, result.MatchedServerPlaceables);
        Assert.Equal(2, result.RemappedServerPlaceableKeys);
        Assert.Equal(new[] { 0x1e1e8b6bu, 0x9f77d47fu }, items.Keys.Order());
        Assert.Equal("Skin4", Assert.IsType<BinTreeString>(items[0x1e1e8b6b].Properties[H("Skin")]).Value);
        Assert.Equal("Skin5", Assert.IsType<BinTreeString>(items[0x9f77d47f].Properties[H("Skin")]).Value);
    }

    [Fact]
    public void Incompatible_source_container_is_rejected_instead_of_shipping_a_spawn_crash()
    {
        byte[] target = Materials(Shop(0x1e1e8b6b,
            "Characters/sru_storekeepernorth/CharacterRecords/Root", 100, "Skin0"));
        byte[] source = Materials(Shop(0x4241132a,
            "Characters/a_different_shopkeeper/CharacterRecords/Root", 100, "Skin4"));

        var ex = Assert.Throws<InvalidDataException>(() =>
            MapSkinSwitcher.BuildCompatibleContainer(target, source));

        Assert.Contains("matched 0 of 1", ex.Message);
    }

    [Fact]
    public void Compatible_container_rejects_a_base_key_owned_by_any_other_source_placeable()
    {
        byte[] target = Materials(Shop(0x1e1e8b6b,
            "Characters/sru_storekeepernorth/CharacterRecords/Root", 100, "Skin0"));
        byte[] source = Materials(
            Shop(0x4241132a, "Characters/sru_storekeepernorth/CharacterRecords/Root", 100, "Skin4"),
            (0x1e1e8b6b, new BinTreeStruct(0, H("MapDecoration"), new BinTreeProperty[]
            {
                new BinTreeString(H("name"), "unrelated object"),
            })));

        var ex = Assert.Throws<InvalidDataException>(() =>
            MapSkinSwitcher.BuildCompatibleContainer(target, source));

        Assert.Contains("already uses that key", ex.Message);
    }

    [Fact]
    public void Applying_the_same_skin_again_is_an_idempotent_valid_operation()
    {
        var first = MapSkinSwitcher.Switch(BuildWithThird("SR"), 11, TargetHash, SourceHash);

        var second = MapSkinSwitcher.Switch(first.Bytes, 11, TargetHash, SourceHash);

        Assert.Equal(0, second.ChangedRouteProperties);
        Assert.Equal(0, second.ChangedAudioProperties);
        Assert.Empty(second.RoutedSkinHashes);
        Assert.Equal(TargetAudioHash, second.RoutedAudioTargetHash);
        Assert.Equal(SourceAudioHash, second.RoutedAudioSourceHash);
        Assert.Contains("ASSETS/Sounds/Wwise2016/SFX/Shared/Milkshake_events.bnk", second.ReferencedStrings);
        var firstTree = SafeBinTree.Parse(first.Bytes);
        var secondTree = SafeBinTree.Parse(second.Bytes);
        Assert.All(firstTree.Objects, pair =>
            Assert.True(BinPropEquality.ObjectsEqual(pair.Value, secondTree.Objects[pair.Key])));
    }

    private static byte[] Build(string mode, bool includeSourceLink)
    {
        var links = includeSourceLink ? new[] { TargetHash, SourceHash } : new[] { TargetHash };
        return Write(new BinTree(new[]
        {
            MapObject(mode, links), TargetObject(), SourceObject(), TargetAudioObject(), SourceAudioObject(),
        }, Array.Empty<string>()));
    }

    private static byte[] BuildWithThird(string mode) => Write(new BinTree(new[]
    {
        MapObject(mode, TargetHash, SourceHash, ThirdHash),
        TargetObject(),
        SourceObject(),
        ThirdObject(),
        AliasObject(),
        TargetAudioObject(),
        SourceAudioObject(),
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
        new BinTreeString(H("mGrassTintTexture"), "ASSETS/Maps/Info/Map11/GrassTint_Other.tex"),
        new BinTreeString(SpawnOverridesField, "other mode spawn data"),
        new BinTreeString(H("thirdOnly"), "must survive"),
    });

    private static BinTreeObject AliasObject() => new(AliasHash, H("MapSkin"), new BinTreeProperty[]
    {
        new BinTreeString(H("name"), "SR_Seasonal_Map"),
        new BinTreeString(H("mMapContainerLink"), "Maps/MapGeometry/Map11/Base_SRX"),
        new BinTreeString(H("mMapObjectsCFG"), "ASSETS/Maps/Default.cfg"),
        new BinTreeString(H("mWorldParticlesINI"), "ASSETS/Maps/Default.ini"),
        new BinTreeString(H("mGrassTintTexture"), "ASSETS/Maps/Info/Map11/GrassTint_Default.tex"),
        new BinTreeString(SpawnOverridesField, "alias runtime data"),
    });

    private static BinTreeObject TargetAudioObject() => new(TargetAudioHash, H("FeatureAudioDataProperties"), new BinTreeProperty[]
    {
        new BinTreeUnorderedContainer(H("bankUnits"), BinPropertyType.String, new BinTreeProperty[]
        {
            new BinTreeString(0, "ASSETS/Sounds/Wwise2016/SFX/Shared/Default_events.bnk"),
        }),
        new BinTreeEmbedded(H("music"), H("MusicAudioDataProperties"), new BinTreeProperty[]
        {
            new BinTreeString(H("themeMusicID"), "Play_mus_map11_phase_select_base"),
        }),
        new BinTreeHash(H("feature"), H("Default")),
    });

    private static BinTreeObject SourceAudioObject() => new(SourceAudioHash, H("FeatureAudioDataProperties"), new BinTreeProperty[]
    {
        new BinTreeUnorderedContainer(H("bankUnits"), BinPropertyType.String, new BinTreeProperty[]
        {
            new BinTreeString(0, "ASSETS/Sounds/Wwise2016/SFX/Shared/Milkshake_events.bnk"),
        }),
        new BinTreeEmbedded(H("music"), H("MusicAudioDataProperties"), new BinTreeProperty[]
        {
            new BinTreeString(H("themeMusicID"), "Play_mus_map11_Milkshake_phase_select_base"),
        }),
        new BinTreeHash(H("feature"), 0xc6ae2d03),
    });

    private static byte[] Materials(params (uint Key, BinTreeStruct Value)[] placements)
    {
        var items = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct,
            placements.Select(item => new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                new BinTreeHash(0, item.Key), item.Value)));
        var container = new BinTreeObject(H("Maps/Chunks/Gameplay"), H("MapPlaceableContainer"),
            new BinTreeProperty[] { items });
        return Write(new BinTree(new[] { container }, Array.Empty<string>()));
    }

    private static (uint Key, BinTreeStruct Value) Shop(uint key, string characterRecord, float x, string skin) =>
        (key, new BinTreeStruct(0, 0x25e3f5d0, new BinTreeProperty[]
        {
            new BinTreeMatrix44(H("transform"), Matrix4x4.CreateTranslation(x, 0, 0)),
            new BinTreeEmbedded(H("Character"), H("MapCharacter"), new BinTreeProperty[]
            {
                new BinTreeString(H("CharacterRecord"), characterRecord),
            }),
            new BinTreeString(H("Skin"), skin),
        }));

    private static Dictionary<uint, BinTreeStruct> MaterialItems(byte[] bytes)
    {
        var tree = SafeBinTree.Parse(bytes);
        var map = Assert.IsType<BinTreeMap>(Assert.Single(tree.Objects).Value.Properties[H("items")]);
        return map.ToDictionary(entry => Assert.IsType<BinTreeHash>(entry.Key).Value,
            entry => Assert.IsType<BinTreeStruct>(entry.Value));
    }

    private static IReadOnlyList<string> Strings(BinTreeProperty property) => property switch
    {
        BinTreeString text => new[] { text.Value },
        BinTreeContainer container => container.Elements.SelectMany(Strings).ToList(),
        BinTreeStruct structure => structure.Properties.Values.SelectMany(Strings).ToList(),
        BinTreeOptional { Value: { } value } => Strings(value),
        _ => Array.Empty<string>(),
    };

    private static byte[] Write(BinTree tree)
    {
        using var stream = new MemoryStream();
        tree.Write(stream);
        return stream.ToArray();
    }

    private static bool IsRouteField(uint hash) => hash == H("mMapContainerLink")
        || hash == H("mMapObjectsCFG")
        || hash == H("mWorldParticlesINI")
        || hash == H("mGrassTintTexture");
}
