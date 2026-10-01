using System.Numerics;
using System.Reflection;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M805: Summoner's Rift's esports sponsor banners. A banner is a <c>GdsMapObject</c> carrying a
/// <c>GDSMapObjectBannerInfo</c>, a LevelProp whose name names the character drawn ("LevelProp_Srx_Banner_Vertical1" is
/// Srx_Banner_Vertical's Skin0); it shows only while its event (MapObjectESportSponsorBanners) is on, and its skin's
/// materials live in the map's shipping bin. These tests pin the reader, the material lookup, and what the view model
/// publishes - with the decoded set injected where a mounted game is needed (the real-data class below decodes it).
/// </summary>
public sealed class MapBannerTests
{
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
    private const uint Banners = 0x11a9b55d, Msi = 0x8f1ab207, HallOfLegends = 0x76c50391;   // the shipped Map11 path hashes

    private static void SetField(MainWindowViewModel vm, string name, object? value) =>
        typeof(MainWindowViewModel).GetField(name, NonPublic)!.SetValue(vm, value);

    private static object? GetField(MainWindowViewModel vm, string name) =>
        typeof(MainWindowViewModel).GetField(name, NonPublic)!.GetValue(vm);

    private static void Call(MainWindowViewModel vm, string name, params object?[] args) =>
        typeof(MainWindowViewModel).GetMethod(name, NonPublic)!.Invoke(vm, args);

    private static byte[] Write(params BinTreeObject[] objects)
    {
        using var stream = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(stream);
        return stream.ToArray();
    }

    // ---- the name rule ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("LevelProp_Srx_Banner_Vertical1", "Srx_Banner_Vertical")]
    [InlineData("LevelProp_Srx_Banner_VerticalThin64", "Srx_Banner_VerticalThin")]
    [InlineData("LevelProp_Srx_Banner_Hero1", "Srx_Banner_Hero")]
    [InlineData("levelprop_sru_snail9", "sru_snail")]
    [InlineData("Info_Swain_BirdSpawnNode227", "")]
    [InlineData("LevelProp_", "")]
    [InlineData("LevelProp_42", "")]
    public void A_LevelProp_names_its_character_without_the_prefix_and_the_number(string name, string character)
    {
        Assert.Equal(character, MapBannerProp.CharacterOf(name));
        var banner = new MapBannerProp(name, Vector3.Zero, Matrix4x4.Identity, character, "", Banners);
        Assert.Equal(character.Length == 0 ? "" : $"Characters/{character}/Skins/Skin0", banner.Skin);
    }

    // ---- the reader -------------------------------------------------------------------------------------------------

    private static BinTreeStruct Gds(string name, byte type, Matrix4x4 transform, uint? bannerData = null, uint controller = 0)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeMatrix44(H("transform"), transform),
            new BinTreeString(H("name"), name),
            new BinTreeU8(H("type"), type),
        };
        if (bannerData is { } data)
            props.Add(new BinTreeContainer(H("extraInfo"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                new BinTreeStruct(0, H("GDSMapObjectBannerInfo"), new BinTreeProperty[] { new BinTreeObjectLink(H("BannerData"), data) }),
            }));
        if (controller != 0) props.Add(new BinTreeObjectLink(H("VisibilityController"), controller));
        return new BinTreeStruct(0, H("GdsMapObject"), props);
    }

    private static BinTreeObject Container(uint path, params (uint Key, BinTreeStruct Item)[] items) =>
        new(path, H("MapPlaceableContainer"), new BinTreeProperty[]
        {
            new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct,
                items.Select(i => new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, i.Key), i.Item)).ToArray()),
        });

    private static BinTreeObject BannerData(uint path, string slot) =>
        new(path, H("EsportsBannerData"), new BinTreeProperty[] { new BinTreeString(H("bannerName"), slot), new BinTreeU32(H("Team"), 100) });

    private static readonly Matrix4x4 Vertical1 = new(0.79474604f, 0.014146179f, 0.48472705f, 0, -0.05277278f, 0.9276109f, 0.05945369f, 0,
        -0.4820534f, -0.07822783f, 0.79264545f, 0, 4285.924f, 49.966034f, 5190.948f, 1);   // base_srx's LevelProp_Srx_Banner_Vertical1

    [Fact]
    public void Only_GdsMapObjects_with_banner_info_are_read_with_their_character_slot_and_event()
    {
        var bin = Write(
            Container(H("Maps/MapGeometry/SR/Chunks/Esports_Banners"),
                (0xc8fd50ab, Gds("LevelProp_Srx_Banner_Vertical1", 10, Vertical1, 0x80cd203b, Banners)),
                (0x68b2fb69, Gds("LevelProp_Srx_Banner_Hero1", 10, Matrix4x4.CreateTranslation(-340, 803, 813), 0x3ec23ecb, Banners))),
            Container(0x183e98d5,
                (0x0eed00ab, Gds("LevelProp_sru_snail9", 10, Matrix4x4.CreateTranslation(4141, 97, 2237))),            // a LevelProp, no banner
                (0xe6310b71, Gds("Info_Swain_BirdSpawnNode227", 9, Matrix4x4.CreateTranslation(9419, 190, 6811))),    // a spawn node
                (0x1234, new BinTreeStruct(0, H("MapAnimatedProp"), new BinTreeProperty[] { new BinTreeString(H("PropName"), "Sru_Duckie") }))),   // another class
            BannerData(0x80cd203b, "ORDER_MIDLANE_VERT_BANNER_1"));

        var banners = MapPlaceableExtractor.ExtractBanners(bin);

        Assert.Equal(new[] { "LevelProp_Srx_Banner_Vertical1", "LevelProp_Srx_Banner_Hero1" }, banners.Select(b => b.Name));
        var vertical = banners[0];
        Assert.Equal("Srx_Banner_Vertical", vertical.CharacterName);
        Assert.Equal("Characters/Srx_Banner_Vertical/Skins/Skin0", vertical.Skin);
        Assert.Equal("ORDER_MIDLANE_VERT_BANNER_1", vertical.BannerName);
        Assert.Equal(Banners, vertical.VisibilityControllerHash);
        Assert.Equal(Vertical1, vertical.Transform);
        Assert.Equal(new Vector3(4285.924f, 49.966034f, 5190.948f), vertical.Position);
        Assert.Equal(new MapPlacementId(H("Maps/MapGeometry/SR/Chunks/Esports_Banners"), 0xc8fd50ab), vertical.Id);
        Assert.Equal("", banners[1].BannerName);   // its EsportsBannerData is not in this bin: the slot stays unnamed, the banner is still read

        // and the existing reader still reads no GdsMapObject as a prop
        Assert.DoesNotContain(MapPlaceableExtractor.Extract(bin).Props, p => p.Name.StartsWith("LevelProp_", StringComparison.Ordinal));
    }

    private static BinTreeObject MapContainer(params uint[] chunks) => new(H("Maps/MapGeometry/Map11/Bloom"), H("MapContainer"), new BinTreeProperty[]
    {
        new BinTreeMap(H("chunks"), BinPropertyType.Hash, BinPropertyType.ObjectLink,
            chunks.Select((c, i) => new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, (uint)(i + 1)), new BinTreeObjectLink(0, c))).ToArray()),
    });

    [Fact]
    public void A_banner_two_containers_carry_is_read_once_from_the_chunk_the_map_lists()
    {
        // Bloom: the base chunk (not listed) and Bloom's own copy (listed), the same keys, a few banners moved in the copy
        uint baseChunk = H("Maps/MapGeometry/SR/Chunks/Esports_Banners"), ownCopy = 0x4c091240;
        var moved = Matrix4x4.CreateTranslation(14173.0625f, 744.7658f, 14951.278f);
        var bin = Write(
            Container(baseChunk, (0xc8fd50ab, Gds("LevelProp_Srx_Banner_Vertical1", 10, Vertical1, 0x80cd203b, Banners)),
                                 (0x2c85bb05, Gds("LevelProp_Srx_Banner_Hero3", 10, Matrix4x4.CreateTranslation(14173.0625f, 717.52216f, 15139.571f), 0x3ec23ecb, Banners))),
            Container(ownCopy, (0xc8fd50ab, Gds("LevelProp_Srx_Banner_Vertical1", 10, Vertical1, 0x80cd203b, Banners)),
                               (0x2c85bb05, Gds("LevelProp_Srx_Banner_Hero3", 10, moved, 0x3ec23ecb, Banners))),
            MapContainer(ownCopy));

        var banners = MapPlaceableExtractor.ExtractBanners(bin);

        Assert.Equal(new[] { "LevelProp_Srx_Banner_Vertical1", "LevelProp_Srx_Banner_Hero3" }, banners.Select(b => b.Name));
        Assert.All(banners, b => Assert.Equal(ownCopy, b.Id.ContainerHash));               // the listed chunk's copies, in place
        Assert.Equal(moved, banners[1].Transform);                                           // where that skin moved it
    }

    [Fact]
    public void Without_a_listed_copy_the_first_container_is_read_and_distinct_keys_all_are()
    {
        var bin = Write(
            Container(0x11111111, (1, Gds("LevelProp_Srx_Banner_Vertical1", 10, Vertical1, 0x80cd203b, Banners))),
            Container(0x22222222, (1, Gds("LevelProp_Srx_Banner_Vertical1", 10, Matrix4x4.CreateTranslation(1, 2, 3), 0x80cd203b, Banners)),
                                  (2, Gds("LevelProp_Srx_Banner_Vertical2", 10, Matrix4x4.CreateTranslation(4, 5, 6), 0x81cd21ce, Banners))));

        var banners = MapPlaceableExtractor.ExtractBanners(bin);

        Assert.Equal(2, banners.Count);
        Assert.Equal(new MapPlacementId(0x11111111, 1), banners[0].Id);
        Assert.Equal(Vertical1, banners[0].Transform);
        Assert.Equal(new MapPlacementId(0x22222222, 2), banners[1].Id);
    }

    [Fact]
    public void A_bin_without_banners_or_one_that_will_not_parse_reads_none()
    {
        Assert.Empty(MapPlaceableExtractor.ExtractBanners(Write(Container(0x183e98d5, (1, Gds("LevelProp_sru_snail9", 10, Matrix4x4.Identity))))));
        Assert.Empty(MapPlaceableExtractor.ExtractBanners(new byte[] { 1, 2, 3 }));
        Assert.Empty(MapPlaceableExtractor.ExtractBanners(Array.Empty<byte>()));
    }

    // ---- the materials: from the map's shipping bin, only where the skin leaves a link unresolved -------------------------

    private static readonly Dictionary<uint, string> Names = new[]
    {
        "StaticMaterialDef", "SkinCharacterDataProperties", "SkinMeshDataProperties", "SkinMeshDataProperties_MaterialOverride",
        "StaticMaterialShaderSamplerDef",
        // the field names, as the hash tables name them for the real bins (an unnamed simpleSkin would read as a texture)
        "skinMeshProperties", "simpleSkin", "material", "materialOverride", "submesh", "samplerValues", "TextureName", "texturePath", "name",
    }.ToDictionary(H, n => n);

    private static string? Resolve(uint hash) => Names.TryGetValue(hash, out var n) ? n : null;

    private static BinTreeObject Material(string path, string texture) => new(H(path), H("StaticMaterialDef"), new BinTreeProperty[]
    {
        new BinTreeString(H("name"), path),
        new BinTreeContainer(H("samplerValues"), BinPropertyType.Embedded, new BinTreeProperty[]
        {
            new BinTreeEmbedded(0, H("StaticMaterialShaderSamplerDef"), new BinTreeProperty[]
            {
                new BinTreeString(H("TextureName"), "Diffuse_Texture"),
                new BinTreeString(H("texturePath"), texture),
            }),
        }),
    });

    private const string Holders = "Maps/Shipping/Map11/Esports/Materials/SRX_Banner_Holders";
    private const string Flag = "Maps/Shipping/Map11/Esports/Materials/ENV_Skinned_VertexWave_Base_Flag_inst";

    /// <summary>The shape of Srx_Banner_Vertical's skin0.bin: a default material and a Banner_Flag override, both links into
    /// map11.bin, which the skin does not name among its dependencies. <paramref name="local"/> defines objects in the skin
    /// bin itself.</summary>
    private static byte[] BannerSkin(params BinTreeObject[] local)
    {
        var skin = new BinTreeObject(H("Characters/Srx_Banner_Vertical/Skins/Skin0"), H("SkinCharacterDataProperties"), new BinTreeProperty[]
        {
            new BinTreeEmbedded(H("skinMeshProperties"), H("SkinMeshDataProperties"), new BinTreeProperty[]
            {
                new BinTreeString(H("simpleSkin"), "ASSETS/Characters/Srx_Banner_Vertical/Skins/Base/Srx_Banner_Vertical.skn"),
                new BinTreeObjectLink(H("Material"), H(Holders)),
                new BinTreeContainer(H("materialOverride"), BinPropertyType.Embedded, new BinTreeProperty[]
                {
                    new BinTreeEmbedded(0, H("SkinMeshDataProperties_MaterialOverride"), new BinTreeProperty[]
                    {
                        new BinTreeObjectLink(H("Material"), H(Flag)),
                        new BinTreeString(H("submesh"), "Banner_Flag"),
                    }),
                }),
            }),
        });
        using var stream = new MemoryStream();
        new BinTree(local.Prepend(skin).ToArray(), new[] { "DATA/Characters/Srx_Banner_Vertical/Srx_Banner_Vertical.bin" }).Write(stream);
        return stream.ToArray();
    }

    private static LoadedBin Map11() => new("data/maps/shipping/map11/map11.bin", new BinTree(new[]
    {
        Material(Holders, "assets/characters/srx_banner_hero/skins/base/srx_banner_holders.tex"),
        Material(Flag, "assets/characters/srx_banner_flags/skins/base/srx_banner_flags.tex"),
    }, Array.Empty<string>()));

    [Fact]
    public void A_skin_material_link_resolves_from_the_host_bin_when_the_skin_and_its_dependencies_leave_it_unresolved()
    {
        var readDeps = new List<string>();
        byte[]? ReadBin(string path) { readDeps.Add(path); return null; }   // the character bin holds no material

        var doc = MaterialDocument.Parse(BannerSkin(), Resolve, null, ReadBin, Map11());

        Assert.Equal(new[] { "DATA/Characters/Srx_Banner_Vertical/Srx_Banner_Vertical.bin" }, readDeps);   // the dependencies are still read first
        var flag = Assert.Single(doc.Materials, m => m.Name == Flag);
        Assert.Equal("data/maps/shipping/map11/map11.bin", flag.LinkedFromBin);
        Assert.Equal(new[] { "Banner_Flag" }, flag.Submeshes);
        Assert.True(flag.IsStaticMaterialDef);
        Assert.True(Assert.Single(doc.Materials, m => m.Name == Holders).IsDefault);
        Assert.Equal("assets/characters/srx_banner_flags/skins/base/srx_banner_flags.tex", doc.SubmeshDiffuse()["Banner_Flag"]);
        Assert.Equal("assets/characters/srx_banner_hero/skins/base/srx_banner_holders.tex", doc.DefaultDiffusePath);

        // the resolver the GL prop path uses says the same, submesh by submesh
        var resolved = ChampionMaterialResolver.Resolve(BannerSkin(), Resolve, null, ReadBin, Map11());
        Assert.Equal("assets/characters/srx_banner_flags/skins/base/srx_banner_flags.tex", resolved.For("Banner_Flag"));
        Assert.Equal("assets/characters/srx_banner_hero/skins/base/srx_banner_holders.tex", resolved.For("Banner_Holder"));
    }

    [Fact]
    public void Without_a_host_bin_the_links_stay_unresolved_exactly_as_before()
    {
        var before = MaterialDocument.Parse(BannerSkin(), Resolve, null, _ => null);
        var unchanged = MaterialDocument.Parse(BannerSkin(), Resolve);

        foreach (var doc in new[] { before, unchanged })
        {
            Assert.DoesNotContain(doc.Materials, m => m.Name is Holders or Flag);
            Assert.Null(doc.DefaultDiffusePath);
            Assert.Empty(doc.SubmeshDiffuse());
        }
        Assert.False(ChampionMaterialResolver.Resolve(BannerSkin(), Resolve, null, _ => null).HasAny);
    }

    [Fact]
    public void A_material_the_skin_bin_defines_itself_wins_over_the_host_bin()
    {
        var doc = MaterialDocument.Parse(BannerSkin(Material(Flag, "assets/own/flag.tex")), Resolve, null, _ => null, Map11());

        var flag = Assert.Single(doc.Materials, m => m.Name == Flag);
        Assert.Null(flag.LinkedFromBin);                                                       // its own, editable
        Assert.Equal("assets/own/flag.tex", doc.SubmeshDiffuse()["Banner_Flag"]);
        Assert.Equal("data/maps/shipping/map11/map11.bin", Assert.Single(doc.Materials, m => m.Name == Holders).LinkedFromBin);   // the other still comes from the host
    }

    [Fact]
    public void A_host_object_that_is_not_a_StaticMaterialDef_is_not_taken()
    {
        var host = new LoadedBin("data/maps/shipping/map11/map11.bin", new BinTree(new[]
        {
            new BinTreeObject(H(Flag), H("EsportsBannerData"), new BinTreeProperty[] { new BinTreeString(H("bannerName"), "x") }),
        }, Array.Empty<string>()));

        var doc = MaterialDocument.Parse(BannerSkin(), Resolve, null, _ => null, host);

        Assert.DoesNotContain(doc.Materials, m => m.LinkedFromBin is not null);
    }

    // ---- the view model -----------------------------------------------------------------------------------------------

    private static readonly MapVisibilityAxis Rift = new(MapVisibility.PrimaryAxisHash, "Map Visibility", 1, true, new[]
    {
        new VisibilityLayer("Base", 1), new VisibilityLayer("Infernal", 2), new VisibilityLayer("Mountain", 4),
    });

    private static BinTreeObject Event(uint path, string name) =>
        new(path, H("MutatorMapVisibilityController"), new BinTreeProperty[] { new BinTreeString(H("MutatorName"), name) });

    private static MapVisibilityControllers Controllers(MapVisibilityDefinition definition) =>
        MapVisibilityControllers.Build(new[]
        {
            Write(Event(Msi, "MSITrophy"), Event(HallOfLegends, "SR_Hall_Of_Legends"), Event(Banners, "MapObjectESportSponsorBanners")),
        }, definition);

    private static MapGeoAsset OneGroupMap() => new()
    {
        Positions = new float[9], Normals = new float[9], Uvs = new float[6], Indices = new uint[] { 0, 1, 2 },
        Groups = new[] { new MapGeoGroup("Ground", 0, 3, VisibilityFlags: 255) },
    };

    private static MapBannerProp Banner(string name, float x, uint controller = Banners) =>
        new(name, new Vector3(x, 0f, 0f), Matrix4x4.CreateTranslation(x, 0f, 0f), MapBannerProp.CharacterOf(name), "SLOT", controller);

    private static PropMesh DummyMesh(string skin) =>
        new(skin, new float[9], new float[9], new float[6], new uint[] { 0, 1, 2 }, new[] { new PropSubmesh(0, 3, null) });

    /// <summary>A Summoner's-Rift-shaped map as a load leaves it - three events, all off - with <paramref name="banners"/>
    /// listed. <paramref name="decoded"/> puts a decoded set where the banner build leaves one (one dummy mesh per
    /// character), as if the first build had already finished; false leaves the build to the view model.</summary>
    private static MainWindowViewModel Rig(IReadOnlyList<MapBannerProp> banners, bool decoded = true)
    {
        var definition = new MapVisibilityDefinition(new[] { Rift });
        var controllers = Controllers(definition);
        var vm = new MainWindowViewModel();
        SetField(vm, "_currentMap", OneGroupMap());
        SetField(vm, "_currentMapEntry", new WadAssetEntry { Path = "data/maps/mapgeometry/map11/testrift.mapgeo", PathHash = 0x1234u, IsResolved = true });
        SetField(vm, "_currentMapBytes", new byte[] { 1 });
        SetField(vm, "_mapVisibility", definition);
        SetField(vm, "_mapControllers", controllers);
        SetField(vm, "_visibilityResolver", new MapVisibilityResolver(controllers, definition));
        Call(vm, "RebuildVisibilityAxes", definition, null);
        Call(vm, "SetMapBanners", banners);
        if (decoded)
        {
            var meshes = banners.Select(b => b.Skin).Distinct().ToDictionary(s => s, DummyMesh);
            SetField(vm, "_bannerInstances", banners.Select(b => PropInstanceData.Place(meshes[b.Skin], b.Transform)).ToList());
            SetField(vm, "_bannerInstanceOwners", banners.ToList());
            SetField(vm, "_bannerBuild", Task.CompletedTask);
        }
        Call(vm, "SetMapEvents", controllers, null, true);
        Call(vm, "ApplyMapVisibility");
        return vm;
    }

    private static MainWindowViewModel.MapEventViewModel Ev(MainWindowViewModel vm, string name) => vm.MapEvents.Single(e => e.Name == name);

    /// <summary>The view model's log lines, collected under a lock: a fresh view model starts background work of its own that
    /// logs on other threads while a test awaits.</summary>
    private sealed class LogCapture
    {
        private readonly List<LogEntry> _lines = new();
        public void Add(LogEntry entry) { lock (_lines) _lines.Add(entry); }
        public void Clear() { lock (_lines) _lines.Clear(); }
        public List<LogEntry> Snapshot() { lock (_lines) return _lines.ToList(); }
    }

    private static LogCapture Capture(MainWindowViewModel vm)
    {
        var capture = new LogCapture();
        ((Logger)typeof(MainWindowViewModel).GetField("_log", NonPublic)!.GetValue(vm)!).Logged += capture.Add;
        return capture;
    }

    private static IReadOnlyList<float> PublishedX(MainWindowViewModel vm) =>
        vm.CurrentPropMeshes?.Instances.Select(i => i.Transform.Translation.X).Order().ToList() ?? (IReadOnlyList<float>)Array.Empty<float>();

    private static readonly MapBannerProp[] ThreeBanners =
    {
        Banner("LevelProp_Srx_Banner_Vertical1", 100f), Banner("LevelProp_Srx_Banner_Vertical2", 200f), Banner("LevelProp_Srx_Banner_Hero1", 300f),
    };

    [Fact]
    public void Banners_are_published_only_while_their_event_is_on()
    {
        var vm = Rig(ThreeBanners);
        Assert.Null(vm.CurrentPropMeshes);                                  // a normal game: no event on, no banner

        Ev(vm, "MSITrophy").IsOn = true;
        Assert.Null(vm.CurrentPropMeshes);                                  // another event shows none of them

        Ev(vm, "MapObjectESportSponsorBanners").IsOn = true;
        Assert.Equal(new[] { 100f, 200f, 300f }, PublishedX(vm));
        Assert.Equal(2, vm.CurrentPropMeshes!.Instances.Select(i => i.Mesh).Distinct().Count());   // one mesh per character, shared

        Ev(vm, "MapObjectESportSponsorBanners").IsOn = false;
        Assert.Empty(PublishedX(vm));
    }

    [Fact]
    public void The_Props_toggle_does_not_hide_them_and_they_join_the_props_and_added_meshes()
    {
        var vm = Rig(ThreeBanners);
        Assert.False(vm.ShowPropMeshes);
        Ev(vm, "MapObjectESportSponsorBanners").IsOn = true;
        Assert.Equal(3, vm.CurrentPropMeshes!.Instances.Count);             // Props is off: they are event content, not the Props view

        var propMesh = DummyMesh("Characters/SRU_Baron/Skins/Skin0");
        SetField(vm, "_propInstances", new List<PropInstanceData> { new(propMesh, Matrix4x4.CreateTranslation(900f, 0, 0)) });
        Call(vm, "PublishAddedMeshPreview");
        Assert.Equal(new[] { 100f, 200f, 300f, 900f }, PublishedX(vm));     // with the placed props, not instead of them
    }

    [Fact]
    public void Ticking_an_event_that_does_not_change_the_banners_does_not_republish()
    {
        var vm = Rig(ThreeBanners);
        Ev(vm, "MapObjectESportSponsorBanners").IsOn = true;
        var published = vm.CurrentPropMeshes;

        Ev(vm, "MSITrophy").IsOn = true;                                     // every D3D11 republish reloads the whole prop set
        Ev(vm, "SR_Hall_Of_Legends").IsOn = true;

        Assert.Same(published, vm.CurrentPropMeshes);
    }

    [Fact]
    public void Nothing_is_decoded_until_an_event_shows_a_banner_and_then_once()
    {
        var vm = Rig(ThreeBanners, decoded: false);
        Assert.Null(GetField(vm, "_bannerBuild"));                          // the map opened: nothing decoded

        Ev(vm, "MSITrophy").IsOn = true;
        Assert.Null(GetField(vm, "_bannerBuild"));                          // an event that shows no banner decodes nothing

        Ev(vm, "MapObjectESportSponsorBanners").IsOn = true;
        var build = Assert.IsAssignableFrom<Task>(GetField(vm, "_bannerBuild"));
        Ev(vm, "MapObjectESportSponsorBanners").IsOn = false;
        Ev(vm, "MapObjectESportSponsorBanners").IsOn = true;
        Assert.Same(build, GetField(vm, "_bannerBuild"));                   // one build per map, however often it is ticked
    }

    [Fact]
    public async Task A_banner_whose_skin_cannot_be_read_is_counted_and_not_drawn()
    {
        var vm = Rig(ThreeBanners, decoded: false);                         // no game mounted: no skin can be read
        var lines = Capture(vm);

        Ev(vm, "MapObjectESportSponsorBanners").IsOn = true;
        await (Task)GetField(vm, "_bannerBuild")!;

        Assert.Empty(PublishedX(vm));
        string line = Assert.Single(lines.Snapshot(), e => e.Category == "Props").Message;
        Assert.Contains("0 of 3 drawn", line);
        Assert.Contains("3 could not be resolved", line);
        Assert.Contains("Sponsor logos are not shown", line);
    }

    [Fact]
    public void The_tooltip_and_the_log_count_the_banners_an_event_gates()
    {
        var vm = Rig(ThreeBanners);
        var lines = Capture(vm);

        string tip = Ev(vm, "MapObjectESportSponsorBanners").Tooltip;
        Assert.Contains("In this map it gates 0 mesh(es), 0 particle placement(s) and 0 sound(s), and 3 esports banner prop(s).", tip);
        Assert.Contains("drawn as their characters' Skin0 (Srx_Banner_Hero, Srx_Banner_Vertical)", tip);
        Assert.Contains("sponsor logos are not", tip);
        Assert.DoesNotContain("does not show", tip);                        // M802's "objects the editor does not show" is gone
        Assert.Contains("Nothing this editor draws is gated by it", Ev(vm, "MSITrophy").Tooltip);   // this rig has no MSI content

        Ev(vm, "MapObjectESportSponsorBanners").IsOn = true;
        string on = Assert.Single(lines.Snapshot(), e => e.Category == "Events").Message;
        Assert.Contains("Event 'MapObjectESportSponsorBanners' ON: it gates 0 mesh(es) (0 draw group(s)), 0 particle placement(s) and 0 sound(s), and 3 esports banner prop(s).", on);
        Assert.EndsWith("still hidden by events: 0 mesh(es) (0 draw group(s)), 0 particle placement(s), 0 sound(s).", on);

        lines.Clear();
        Ev(vm, "MapObjectESportSponsorBanners").IsOn = false;
        Assert.EndsWith("0 sound(s), 3 banner prop(s).", Assert.Single(lines.Snapshot(), e => e.Category == "Events").Message);
    }

    [Fact]
    public void Opening_a_map_with_banners_says_they_start_hidden()
    {
        var vm = Rig(ThreeBanners);
        var lines = Capture(vm);

        Call(vm, "SetMapEvents", GetField(vm, "_mapControllers"), null, true);

        string line = Assert.Single(lines.Snapshot(), e => e.Category == "Events").Message;
        Assert.Contains("0 mesh(es) (0 draw group(s)), 0 particle placement(s) and 0 sound(s) start hidden. So do 3 esports banner prop(s). Tick an event", line);
    }

    [Fact]
    public void A_map_without_banners_never_publishes_for_them()
    {
        var vm = Rig(Array.Empty<MapBannerProp>(), decoded: false);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        foreach (var ev in vm.MapEvents) ev.IsOn = true;
        foreach (var ev in vm.MapEvents) ev.IsOn = false;

        Assert.Null(vm.CurrentPropMeshes);
        Assert.DoesNotContain(nameof(MainWindowViewModel.CurrentPropMeshes), raised);
        Assert.Null(GetField(vm, "_bannerBuild"));
        Assert.Contains("Nothing this editor draws is gated by it, so ticking it changes nothing on screen.", Ev(vm, "MapObjectESportSponsorBanners").Tooltip);
    }

    [Fact]
    public void A_banner_without_a_controller_shows_and_one_whose_controller_is_unknown_shows()
    {
        // not shipped (every Map11 banner names 0x11a9b55d): the event gate's own rule, as for a particle's sound
        var vm = Rig(new[] { Banner("LevelProp_Srx_Banner_Vertical1", 100f, controller: 0), Banner("LevelProp_Srx_Banner_Vertical2", 200f, controller: 0xdead) });
        Assert.Equal(new[] { 100f, 200f }, PublishedX(vm));
    }

    [Fact]
    public void Clearing_the_viewport_drops_the_banners_and_nothing_republishes_them()
    {
        var vm = Rig(ThreeBanners);
        Ev(vm, "MapObjectESportSponsorBanners").IsOn = true;
        Assert.NotNull(vm.CurrentPropMeshes);

        Call(vm, "ClearViewport");

        Assert.Null(vm.CurrentPropMeshes);
        Assert.Empty((System.Collections.ICollection)GetField(vm, "_mapBanners")!);
        Call(vm, "PublishAddedMeshPreview");                                // anything that republishes later
        Assert.Null(vm.CurrentPropMeshes);
    }

    [Fact]
    public void A_map_tab_that_returns_keeps_its_banners_and_the_event_it_left_ticked()
    {
        var vm = Rig(ThreeBanners);
        vm.CurrentMesh = new MeshAsset
        {
            Positions = new float[9], Normals = new float[9], Uvs = new float[6], Indices = new uint[] { 0, 1, 2 },
            SubMeshes = new[] { new SubMeshInfo("m", 0, 3, 0) },
        };
        Ev(vm, "MapObjectESportSponsorBanners").IsOn = true;
        var scene = typeof(MainWindowViewModel).GetMethod("CaptureMapScene", NonPublic)!.Invoke(vm, null)!;

        Call(vm, "SetMapBanners", (object?)null);
        Call(vm, "SetMapEvents", null, null, false);
        typeof(MainWindowViewModel).GetMethod("RestoreMapScene", NonPublic)!.Invoke(vm, new[] { scene });

        Assert.Equal(ThreeBanners, (IEnumerable<MapBannerProp>)GetField(vm, "_mapBanners")!);
        Assert.True(Ev(vm, "MapObjectESportSponsorBanners").IsOn);
        Assert.NotNull(GetField(vm, "_bannerBuild"));                       // decoded again for the returning tab
    }
}

/// <summary>
/// M805: the banners of Summoner's Rift as the installed game ships them. Skipped (with a SKIPPED line in the output)
/// without Map11; the "ran:" lines say what was actually checked.
/// </summary>
public sealed class MapBannerRealDataTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);
    private const string MapBin = "data/maps/shipping/map11/map11.bin";

    private (WadArchive Wad, HashDatabase Db)? Open(string wadName)
    {
        string path = Path.Combine(Final, "Maps", "Shipping", wadName + ".wad.client");
        if (!File.Exists(path)) { output.WriteLine($"SKIPPED: {wadName}.wad.client is not installed"); return null; }
        var db = new HashSyncService().LoadLocal(_ => { });
        return (WadArchive.Open(path, new WadPathResolver(db)), db);
    }

    private static byte[]? Read(WadArchive wad, string path) =>
        wad.TryGetEntry(BinTexturePath.HashOfReference(path), out _) ? wad.Extract(BinTexturePath.HashOfReference(path)) : null;

    [Fact]
    public void Base_srx_places_117_banners_of_four_characters_every_one_gated_by_the_sponsor_banner_event()
    {
        if (Open("Map11") is not { } opened) return;
        using var wad = opened.Wad;
        var banners = MapPlaceableExtractor.ExtractBanners(Read(wad, "data/maps/mapgeometry/map11/base_srx.materials.bin")!);

        Assert.Equal(117, banners.Count);
        var families = banners.GroupBy(b => b.CharacterName).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<string, int> { ["Srx_Banner_Hero"] = 4, ["Srx_Banner_Horizontal"] = 30, ["Srx_Banner_Vertical"] = 19, ["Srx_Banner_VerticalThin"] = 64 }, families);
        Assert.All(banners, b => Assert.Equal(0x11a9b55du, b.VisibilityControllerHash));
        Assert.All(banners, b => Assert.Equal(H("Maps/MapGeometry/SR/Chunks/Esports_Banners"), b.Id.ContainerHash));
        Assert.All(banners, b => Assert.NotEqual("", b.BannerName));                       // every slot names its EsportsBannerData
        Assert.Equal(117, banners.Select(b => b.Id.ItemKey).Distinct().Count());
        foreach (string character in families.Keys)
            Assert.True(Read(wad, $"data/characters/{character.ToLowerInvariant()}/skins/skin0.bin") is not null, $"{character}: no skin0.bin in Map11");
        output.WriteLine($"ran: base_srx {banners.Count} banner(s): {string.Join(", ", families.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value}"))}; "
            + $"slots e.g. {string.Join(", ", banners.Take(3).Select(b => b.BannerName))}");
    }

    [Fact]
    public void Every_banner_character_resolves_its_mesh_and_its_two_textures_with_the_map_bin_and_none_without_it()
    {
        if (Open("Map11") is not { } opened) return;
        using var wad = opened.Wad;
        string? Name(uint h) => opened.Db.TryGetBinName(h, out var n) ? n : null;
        string? WadPath(ulong h) => opened.Db.TryGetPath(h, out var p) ? p : null;
        var host = new LoadedBin(MapBin, SafeBinTree.Parse(Read(wad, MapBin)!));
        var report = new List<string>();

        foreach (string character in new[] { "srx_banner_hero", "srx_banner_horizontal", "srx_banner_vertical", "srx_banner_verticalthin" })
        {
            var skinBin = Read(wad, $"data/characters/{character}/skins/skin0.bin")!;
            var meshRef = SkinMeshExtractor.Extract(skinBin, WadPath)!;
            var mesh = SkinnedMeshDecoder.Decode(Read(wad, meshRef.SimpleSkin!)!);
            Assert.True(mesh.CanSkin);
            Assert.Equal(new[] { "Banner_Flag", "Banner_Holder" }, mesh.SubMeshes.Select(s => s.Material).Order());

            var withMap = ChampionMaterialResolver.Resolve(skinBin, Name, WadPath, p => Read(wad, p), host);
            var without = ChampionMaterialResolver.Resolve(skinBin, Name, WadPath, p => Read(wad, p));
            Assert.False(without.HasAny);                                                   // the gap M805 closes
            Assert.Equal("assets/characters/srx_banner_flags/skins/base/srx_banner_flags.tex", withMap.For("Banner_Flag"));
            Assert.Equal("assets/characters/srx_banner_hero/skins/base/srx_banner_holders.tex", withMap.For("Banner_Holder"));
            foreach (string texture in new[] { withMap.For("Banner_Flag")!, withMap.For("Banner_Holder")! })
            {
                var image = TextureDecoder.Decode(Read(wad, texture)!);                     // not the encrypted sponsor art
                Assert.Equal(1024, image.Width);
            }
            report.Add($"{character}: {mesh.VertexCount} verts, {mesh.SubMeshes.Count} submeshes");
        }
        output.WriteLine($"ran: {string.Join("; ", report)}; flag srx_banner_flags.tex + holder srx_banner_holders.tex decode (1024x1024) only with {MapBin}");
    }

    [Fact]
    public void A_banner_mesh_under_its_transform_lands_in_the_box_the_object_authors()
    {
        if (Open("Map11") is not { } opened) return;
        using var wad = opened.Wad;
        string? WadPath(ulong h) => opened.Db.TryGetPath(h, out var p) ? p : null;
        var bin = Read(wad, "data/maps/mapgeometry/map11/base_srx.materials.bin")!;
        var banners = MapPlaceableExtractor.ExtractBanners(bin);
        // the authored boxes, read straight from the bin by placement key
        var tree = SafeBinTree.Parse(bin);
        var boxes = new Dictionary<uint, (Vector3 Min, Vector3 Max)>();
        var container = (BinTreeMap)tree.Objects[H("Maps/MapGeometry/SR/Chunks/Esports_Banners")].Properties[H("items")];
        foreach (var kv in container)
            if (kv.Value is BinTreeStruct s && s.Properties.TryGetValue(H("boxMin"), out var lo) && s.Properties.TryGetValue(H("boxMax"), out var hi))
                boxes[((BinTreeHash)kv.Key).Value] = (((BinTreeVector3)lo).Value, ((BinTreeVector3)hi).Value);
        var meshes = new Dictionary<string, MeshAsset>();
        int inside = 0;
        var outside = new List<string>();
        foreach (var b in banners)
        {
            if (!meshes.TryGetValue(b.CharacterName, out var mesh))
            {
                var meshRef = SkinMeshExtractor.Extract(Read(wad, $"data/characters/{b.CharacterName.ToLowerInvariant()}/skins/skin0.bin")!, WadPath)!;
                meshes[b.CharacterName] = mesh = SkinnedMeshDecoder.Decode(Read(wad, meshRef.SimpleSkin!)!);
            }
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            for (int i = 0; i < mesh.Positions.Length; i += 3)
            {
                var v = Vector3.Transform(new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]), b.Transform);
                min = Vector3.Min(min, v); max = Vector3.Max(max, v);
            }
            var centre = (min + max) / 2f;
            var box = boxes[b.Id.ItemKey];
            if (centre.X >= box.Min.X && centre.Y >= box.Min.Y && centre.Z >= box.Min.Z && centre.X <= box.Max.X && centre.Y <= box.Max.Y && centre.Z <= box.Max.Z) inside++;
            else outside.Add(b.Name);
        }
        // two VerticalThin boxes (50, 51) sit about 1,000 units from their own transforms in the shipped data
        Assert.True(inside >= 110, $"{inside} of {banners.Count} inside; outside: {string.Join(", ", outside)}");
        output.WriteLine($"ran: {inside} of {banners.Count} banner meshes centred inside their authored box; outside: {string.Join(", ", outside)}");
    }

    [Fact]
    public async Task Through_the_view_model_none_is_decoded_or_drawn_by_default_and_all_117_with_their_event_on()
    {
        if (Open("Map11") is not { } opened) return;
        using var wad = opened.Wad;
        string? Name(uint h) => opened.Db.TryGetBinName(h, out var n) ? n : null;
        var materials = Read(wad, "data/maps/mapgeometry/map11/base_srx.materials.bin")!;
        string dir = "data/maps/mapgeometry/map11/";
        var bins = wad.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                && e.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && !e.Path.EndsWith("/base_srx.materials.bin", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Select(e => wad.Extract(e)).ToList();
        bins.Add(materials);
        var definition = MapVisibility.Parse(Read(wad, MapBin), Name);
        var controllers = MapVisibilityControllers.Build(bins, definition);

        var vm = new MainWindowViewModel();
        void Set(string field, object? value) => typeof(MainWindowViewModel).GetField(field, NonPublic)!.SetValue(vm, value);
        object? Get(string field) => typeof(MainWindowViewModel).GetField(field, NonPublic)!.GetValue(vm);
        void Invoke(string method, params object?[] args) => typeof(MainWindowViewModel).GetMethod(method, NonPublic)!.Invoke(vm, args);
        Set("_archive", wad);   // the banner build reads skins, meshes, textures and map11.bin through it, as through a mounted project
        Set("_currentMap", MapGeoDecoder.Decode(Read(wad, dir + "base_srx.mapgeo")!, ExtendedChannelRule.From(materials, Name)));
        Set("_currentMapEntry", new WadAssetEntry { Path = dir + "base_srx.mapgeo", PathHash = HashAlgorithms.WadPath(dir + "base_srx.mapgeo"), IsResolved = true });
        Set("_mapVisibility", definition);
        Set("_mapControllers", controllers);
        Set("_visibilityResolver", new MapVisibilityResolver(controllers, definition));
        Invoke("RebuildVisibilityAxes", definition, null);
        Invoke("SetMapBanners", MapPlaceableExtractor.ExtractBanners(materials));   // what the map load does
        Invoke("SetMapEvents", controllers, null, true);
        Invoke("ApplyMapVisibility");

        Assert.Null(Get("_bannerBuild"));
        Assert.Null(vm.CurrentPropMeshes);
        Assert.Contains("and 117 esports banner prop(s).", vm.MapEvents.Single(e => e.Name == "MapObjectESportSponsorBanners").Tooltip);

        vm.MapEvents.Single(e => e.Name == "MapObjectESportSponsorBanners").IsOn = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await (Task)Get("_bannerBuild")!;
        var shown = vm.CurrentPropMeshes!.Instances;
        Assert.Equal(117, shown.Count);
        var meshes = shown.Select(i => i.Mesh).Distinct().ToList();
        Assert.Equal(4, meshes.Count);
        foreach (var mesh in meshes)
        {
            Assert.All(mesh.Submeshes, s => Assert.NotNull(s.Texture));                      // flag and holder both textured
            Assert.All(mesh.Submeshes, s => Assert.NotNull(s.Material));                     // and on their materials' render state
            Assert.Equal(MapBin, mesh.HostBin!.Path);                                        // the D3D11 scene resolves the same materials
            Assert.InRange(mesh.HostBin.Tree.Objects.Count, 1, 10);                          // only the materials, not the 4.6 MB bin
            Assert.NotNull(mesh.SknBytes);
            Assert.True(mesh.CanAnimate);                                                    // its idle, like any placed prop
        }

        vm.MapEvents.Single(e => e.Name == "MSITrophy").IsOn = true;
        Assert.Equal(117, vm.CurrentPropMeshes!.Instances.Count);
        vm.MapEvents.Single(e => e.Name == "MapObjectESportSponsorBanners").IsOn = false;
        Assert.Empty(vm.CurrentPropMeshes?.Instances ?? Array.Empty<PropInstanceData>());
        vm.MapEvents.Single(e => e.Name == "MapObjectESportSponsorBanners").IsOn = true;
        Assert.Equal(meshes, vm.CurrentPropMeshes!.Instances.Select(i => i.Mesh).Distinct().ToList());   // not decoded again
        output.WriteLine($"ran: base_srx 117 banner(s) decoded into {meshes.Count} mesh(es) in {clock.Elapsed.TotalSeconds:0.00}s on the first tick, "
            + $"hidden again with the event off and shown again from the same meshes; host bin objects {meshes[0].HostBin!.Tree.Objects.Count}");
    }

    [Theory]
    [InlineData("Map12", "data/maps/mapgeometry/map12/bloom.materials.bin")]
    [InlineData("Map12", "data/maps/mapgeometry/map12/base.materials.bin")]
    [InlineData("Map22", "data/maps/mapgeometry/map22/anniversary.materials.bin")]
    [InlineData("Map30", "data/maps/mapgeometry/map30/arenad.materials.bin")]
    [InlineData("Map453", "data/maps/mapgeometry/map453/jade_container.materials.bin")]
    public void A_map_without_banners_reads_none(string wadName, string materialsPath)
    {
        if (Open(wadName) is not { } opened) return;
        using var wad = opened.Wad;
        if (Read(wad, materialsPath) is not { } bin) { output.WriteLine($"SKIPPED: {materialsPath} is not in {wadName}"); return; }

        Assert.Empty(MapPlaceableExtractor.ExtractBanners(bin));
        output.WriteLine($"ran: {materialsPath}: no banner");
    }

    [Fact]
    public void Every_Map11_skin_that_carries_banners_reads_the_same_117_once()
    {
        if (Open("Map11") is not { } opened) return;
        using var wad = opened.Wad;
        var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in wad.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase)))
        {
            var banners = MapPlaceableExtractor.ExtractBanners(wad.Extract(entry));
            if (banners.Count > 0) counts[entry.Path[(entry.Path.LastIndexOf('/') + 1)..]] = banners.Count;
        }
        Assert.Contains("base_srx.materials.bin", counts.Keys);
        Assert.Contains("bloom.materials.bin", counts.Keys);   // 234 banner GdsMapObjects there: the chunk it lists and the base chunk
        Assert.All(counts.Values, n => Assert.Equal(117, n));

        // Bloom reads its own copy, the one its MapContainer lists - with Hero3 where Bloom moved it
        var bloom = MapPlaceableExtractor.ExtractBanners(Read(wad, "data/maps/mapgeometry/map11/bloom.materials.bin")!);
        Assert.All(bloom, b => Assert.Equal(0x4c091240u, b.Id.ContainerHash));
        var hero3 = bloom.Single(b => b.Name == "LevelProp_Srx_Banner_Hero3");
        Assert.Equal(14951.278f, hero3.Position.Z, 0.01f);
        output.WriteLine($"ran: {counts.Count} Map11 materials bin(s) with banners, 117 each: {string.Join(", ", counts.Keys)}; bloom's from its listed chunk 0x4c091240 (Hero3 at {hero3.Position})");
    }
}
