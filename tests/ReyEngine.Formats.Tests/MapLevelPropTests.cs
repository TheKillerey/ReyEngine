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
/// M806: level props - the "LevelProp_" <c>GdsMapObject</c>s of type 10 that are not esports banners (Summoner's Rift's
/// critters and banner platforms, Bilgewater's boats, the Howling Abyss's poros ...). Drawn as their character's Skin0 like
/// a placed prop: with Props on, by the board stage's exact rule, behind the event gate when they or their chunk name a
/// controller. These tests pin the reader and what the view model publishes - with the decoded set injected where a
/// mounted game is needed (the real-data class below decodes it).
/// </summary>
public sealed class MapLevelPropTests
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

    // ---- the reader -------------------------------------------------------------------------------------------------

    private static BinTreeStruct Gds(string name, byte? type, Vector3 at, int? flags = null, uint controller = 0,
        uint? bannerData = null, string? defaultAnimation = null)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeMatrix44(H("transform"), Matrix4x4.CreateTranslation(at)),
            new BinTreeString(H("name"), name),
        };
        if (type is { } t) props.Add(new BinTreeU8(H("type"), t));
        var extras = new List<BinTreeProperty>();
        if (bannerData is { } data)
            extras.Add(new BinTreeStruct(0, H("GDSMapObjectBannerInfo"), new BinTreeProperty[] { new BinTreeObjectLink(H("BannerData"), data) }));
        if (defaultAnimation is not null)
            extras.Add(new BinTreeStruct(0, H("GDSMapObjectAnimationInfo"), new BinTreeProperty[] { new BinTreeString(H("defaultAnimation"), defaultAnimation) }));
        if (extras.Count > 0) props.Add(new BinTreeContainer(H("extraInfo"), BinPropertyType.Struct, extras));
        if (flags is { } f) props.Add(new BinTreeU8(H("mVisibilityFlags"), (byte)f));
        if (controller != 0) props.Add(new BinTreeObjectLink(H("VisibilityController"), controller));
        return new BinTreeStruct(0, H("GdsMapObject"), props);
    }

    private static BinTreeObject Container(uint path, params (uint Key, BinTreeStruct Item)[] items) =>
        new(path, H("MapPlaceableContainer"), new BinTreeProperty[]
        {
            new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct,
                items.Select(i => new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, i.Key), i.Item)).ToArray()),
        });

    /// <summary>A MapContainer listing <paramref name="listed"/> as its chunks, with a MapChunkVisibility component gating
    /// <paramref name="gatedChunk"/> by <paramref name="gate"/> (the shape base_srx's container has).</summary>
    private static BinTreeObject MapContainer(uint[] listed, uint gatedChunk = 0, uint gate = 0)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeMap(H("chunks"), BinPropertyType.Hash, BinPropertyType.ObjectLink,
                listed.Select((c, i) => new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, (uint)(i + 1)), new BinTreeObjectLink(0, c))).ToArray()),
        };
        if (gatedChunk != 0)
            props.Add(new BinTreeContainer(H("components"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                new BinTreeStruct(0, H("MapChunkVisibility"), new BinTreeProperty[]
                {
                    new BinTreeContainer(0x6355dd6f, BinPropertyType.Embedded, new BinTreeProperty[]
                    {
                        new BinTreeEmbedded(0, 0x6355dd6f, new BinTreeProperty[]
                        {
                            new BinTreeObjectLink(H("chunk"), gatedChunk),
                            new BinTreeObjectLink(H("VisibilityController"), gate),
                        }),
                    }),
                }),
            }));
        return new BinTreeObject(H("Maps/MapGeometry/Map11/Test"), H("MapContainer"), props);
    }

    private const uint MapObjects = 0x183e98d5, GatedChunk = 0xd3d72048;   // base_srx's level-prop chunk, and its Hall of Legends chunk

    [Fact]
    public void Only_type_10_LevelProps_that_are_not_banners_are_read_with_their_flags_controller_and_clip()
    {
        var bin = Write(
            Container(MapObjects,
                (1, Gds("LevelProp_sru_snail9", 10, new Vector3(4141, 97, 2237))),
                (2, Gds("LevelProp_sru_gromp_prop8", 10, new Vector3(11793, 43, 7372), flags: 128)),
                (3, Gds("LevelProp_TFT_BoardPoro", 10, new Vector3(1, 2, 3), defaultAnimation: "idle1")),
                (4, Gds("LevelProp_Srx_Banner_Vertical1", 10, new Vector3(4286, 50, 5191), controller: Banners, bannerData: 0x80cd203b)),   // a banner: M805's
                (5, Gds("Info_Swain_BirdSpawnNode227", 9, new Vector3(9419, 190, 6811))),                                                // a spawn node
                (6, Gds("LevelProp_sru_bird1", null, new Vector3(5, 5, 5))),                                                              // no type: not a level prop
                (7, Gds("LevelProp_sru_bird2", 9, new Vector3(6, 6, 6))),                                                                 // another type
                (8, Gds("group4", 10, new Vector3(7, 7, 7))),                                                                             // no LevelProp_ name
                (9, Gds("LevelProp_sru_lizard3", 10, new Vector3(8, 8, 8), controller: Msi))),                                           // its own controller
            MapContainer(new[] { MapObjects }));

        var props = MapPlaceableExtractor.ExtractLevelProps(bin);

        Assert.Equal(new[] { "LevelProp_sru_snail9", "LevelProp_sru_gromp_prop8", "LevelProp_TFT_BoardPoro", "LevelProp_sru_lizard3" }, props.Select(p => p.Name));
        var snail = props[0];
        Assert.Equal("sru_snail", snail.CharacterName);
        Assert.Equal("Characters/sru_snail/Skins/Skin0", snail.Skin);
        Assert.Equal(new Vector3(4141, 97, 2237), snail.Position);
        Assert.Equal((255, false, 0u, ""), (snail.VisibilityFlags, snail.HasVisibilityFlags, snail.VisibilityControllerHash, snail.IdleAnimation));
        Assert.Equal(new MapPlacementId(MapObjects, 1), snail.Id);
        Assert.Equal((128, true), (props[1].VisibilityFlags, props[1].HasVisibilityFlags));
        Assert.Equal("idle1", props[2].IdleAnimation);
        Assert.Equal(Msi, props[3].VisibilityControllerHash);

        // the banner stays M805's, and the existing readers are untouched
        Assert.Equal(new[] { "LevelProp_Srx_Banner_Vertical1" }, MapPlaceableExtractor.ExtractBanners(bin).Select(b => b.Name));
        Assert.Empty(MapPlaceableExtractor.Extract(bin).Props);
    }

    [Fact]
    public void A_level_prop_takes_its_chunks_controller_when_it_names_none_and_keeps_its_own_when_it_does()
    {
        var bin = Write(
            Container(GatedChunk,
                (1, Gds("LevelProp_HallStatue1", 10, new Vector3(1, 0, 0))),
                (2, Gds("LevelProp_HallStatue2", 10, new Vector3(2, 0, 0), controller: Msi))),
            Container(MapObjects, (3, Gds("LevelProp_sru_snail1", 10, new Vector3(3, 0, 0)))),
            MapContainer(new[] { GatedChunk, MapObjects }, GatedChunk, HallOfLegends));

        var props = MapPlaceableExtractor.ExtractLevelProps(bin).ToDictionary(p => p.Name);

        Assert.Equal(HallOfLegends, props["LevelProp_HallStatue1"].VisibilityControllerHash);   // the chunk's gate
        Assert.Equal(Msi, props["LevelProp_HallStatue2"].VisibilityControllerHash);             // its own wins
        Assert.Equal(0u, props["LevelProp_sru_snail1"].VisibilityControllerHash);               // an ungated chunk: none
    }

    [Fact]
    public void A_level_prop_two_containers_carry_is_read_once_from_the_chunk_the_map_lists()
    {
        uint copy = 0xdeef4439;   // Bloom lists a copy of its own
        var bin = Write(
            Container(MapObjects, (1, Gds("LevelProp_sru_snail1", 10, new Vector3(1, 0, 0)))),
            Container(copy, (1, Gds("LevelProp_sru_snail1", 10, new Vector3(9, 0, 0)))),
            MapContainer(new[] { copy }));

        var snail = Assert.Single(MapPlaceableExtractor.ExtractLevelProps(bin));
        Assert.Equal(copy, snail.Id.ContainerHash);
        Assert.Equal(new Vector3(9, 0, 0), snail.Position);
    }

    [Fact]
    public void A_bin_without_level_props_or_one_that_will_not_parse_reads_none()
    {
        Assert.Empty(MapPlaceableExtractor.ExtractLevelProps(Write(Container(MapObjects, (1, Gds("Info_BrazierLocation1", 9, Vector3.Zero))))));
        Assert.Empty(MapPlaceableExtractor.ExtractLevelProps(new byte[] { 1, 2, 3 }));
        Assert.Empty(MapPlaceableExtractor.ExtractLevelProps(Array.Empty<byte>()));
    }

    // ---- the view model -----------------------------------------------------------------------------------------------

    private static readonly MapVisibilityAxis Rift = new(MapVisibility.PrimaryAxisHash, "Map Visibility", 1, true, new[]
    {
        new VisibilityLayer("Base", 1), new VisibilityLayer("Infernal", 2), new VisibilityLayer("Mountain", 4), new VisibilityLayer("Void", 128),
    });

    private static BinTreeObject Event(uint path, string name) =>
        new(path, H("MutatorMapVisibilityController"), new BinTreeProperty[] { new BinTreeString(H("MutatorName"), name) });

    private static MapLevelProp Level(string name, float x, int flags = 255, uint controller = 0) =>
        new(name, new Vector3(x, 0f, 0f), Matrix4x4.CreateTranslation(x, 0f, 0f), MapBannerProp.CharacterOf(name),
            flags, flags != 255, controller);

    private static PropMesh DummyMesh(string skin) =>
        new(skin, new float[9], new float[9], new float[6], new uint[] { 0, 1, 2 }, new[] { new PropSubmesh(0, 3, null) });

    /// <summary>Two board stages: 1 (Base) and 2 (Infernal).</summary>
    private static MapBoardStageSet Stages() => new(new[]
    {
        new MapBoardStage("Base", "B_Base", "C_Base", null, 1, new Dictionary<string, bool>(), new[] { "B_Base" }, 0),
        new MapBoardStage("Infernal", "B_Infernal", "C_Infernal", 2, 2, new Dictionary<string, bool>(), new[] { "B_Infernal" }, 0),
    }, Array.Empty<MapLightingVolume>(), null);

    /// <summary>A Summoner's-Rift-shaped map as a load leaves it - three events, all off, Props off - with
    /// <paramref name="props"/> listed. <paramref name="decoded"/> puts a decoded set where the level-prop build leaves one
    /// (one dummy mesh per character), as if the first build had already finished; false leaves the build to the view model.</summary>
    private static MainWindowViewModel Rig(IReadOnlyList<MapLevelProp> props, bool decoded = true, MapBoardStageSet? stages = null,
        IReadOnlyList<MapBannerProp>? banners = null)
    {
        var definition = new MapVisibilityDefinition(new[] { Rift });
        var controllers = MapVisibilityControllers.Build(new[]
        {
            Write(Event(Msi, "MSITrophy"), Event(HallOfLegends, "SR_Hall_Of_Legends"), Event(Banners, "MapObjectESportSponsorBanners")),
        }, definition);
        var vm = new MainWindowViewModel();
        SetField(vm, "_currentMap", new MapGeoAsset
        {
            Positions = new float[9], Normals = new float[9], Uvs = new float[6], Indices = new uint[] { 0, 1, 2 },
            Groups = new[] { new MapGeoGroup("Ground", 0, 3, VisibilityFlags: 255) },
        });
        SetField(vm, "_currentMapEntry", new WadAssetEntry { Path = "data/maps/mapgeometry/map11/testrift.mapgeo", PathHash = 0x1234u, IsResolved = true });
        SetField(vm, "_currentMapBytes", new byte[] { 1 });
        SetField(vm, "_mapVisibility", definition);
        SetField(vm, "_mapControllers", controllers);
        SetField(vm, "_visibilityResolver", new MapVisibilityResolver(controllers, definition));
        Call(vm, "RebuildVisibilityAxes", definition, null);
        Call(vm, "SetMapLevelProps", props);
        if (decoded)
        {
            var meshes = props.Select(p => p.Skin).Distinct().ToDictionary(s => s, DummyMesh);
            SetField(vm, "_levelPropInstances", props.Select(p => PropInstanceData.Place(meshes[p.Skin], p.Transform)).ToList());
            SetField(vm, "_levelPropInstanceOwners", props.ToList());
            SetField(vm, "_levelPropBuild", Task.CompletedTask);
        }
        if (banners is not null)
        {
            Call(vm, "SetMapBanners", banners);
            var mesh = DummyMesh("Characters/Srx_Banner_Vertical/Skins/Skin0");
            SetField(vm, "_bannerInstances", banners.Select(b => PropInstanceData.Place(mesh, b.Transform)).ToList());
            SetField(vm, "_bannerInstanceOwners", banners.ToList());
            SetField(vm, "_bannerBuild", Task.CompletedTask);
        }
        if (stages is not null) Call(vm, "SetBoardStages", stages);
        Call(vm, "SetMapEvents", controllers, null, true);
        Call(vm, "ApplyMapVisibility");
        return vm;
    }

    private static MainWindowViewModel.MapEventViewModel Ev(MainWindowViewModel vm, string name) => vm.MapEvents.Single(e => e.Name == name);

    private static IReadOnlyList<float> PublishedX(MainWindowViewModel vm) =>
        vm.CurrentPropMeshes?.Instances.Select(i => i.Transform.Translation.X).Order().ToList() ?? (IReadOnlyList<float>)Array.Empty<float>();

    private static readonly MapLevelProp[] Critters =
    {
        Level("LevelProp_sru_snail9", 100f), Level("LevelProp_sru_snail8", 200f), Level("LevelProp_sru_gromp_prop8", 300f, flags: 128),
    };

    [Fact]
    public void Level_props_show_with_Props_on_and_only_then()
    {
        var vm = Rig(Critters);
        Assert.False(vm.ShowPropMeshes);
        Assert.Null(vm.CurrentPropMeshes);                                   // a map opens with Props off: nothing

        vm.ShowPropMeshes = true;                                            // the toggle, as the user switches it
        Assert.Equal(new[] { 100f, 200f, 300f }, PublishedX(vm));
        Assert.Equal(2, vm.CurrentPropMeshes!.Instances.Select(i => i.Mesh).Distinct().Count());   // one mesh per character

        vm.ShowPropMeshes = false;
        Assert.Empty(PublishedX(vm));
    }

    [Fact]
    public void The_layer_dropdown_does_not_filter_them_as_it_never_filtered_a_prop()
    {
        var vm = Rig(Critters);
        vm.ShowPropMeshes = true;

        vm.VisibilityAxes.Single().SelectedIndex = 1;                        // Base = 1: the gromp prop authors 128
        Assert.Equal(new[] { 100f, 200f, 300f }, PublishedX(vm));
        vm.VisibilityAxes.Single().SelectedIndex = 4;                        // Void = 128
        Assert.Equal(new[] { 100f, 200f, 300f }, PublishedX(vm));
    }

    [Fact]
    public void A_board_stage_applies_its_exact_mask_to_their_flags_and_flags_0_hides_one()
    {
        var vm = Rig(new[] { Level("LevelProp_A1", 100f), Level("LevelProp_B1", 200f, flags: 2), Level("LevelProp_C1", 300f, flags: 0) }, stages: Stages());
        vm.ShowPropMeshes = true;
        Assert.Equal(new[] { 100f, 200f }, PublishedX(vm));                  // Start: every layer, the disabled one hidden

        var picker = vm.BoardStageSelectors.Single();
        picker.SelectedIndex = 1;                                            // Base = 1: B (2) shares no bit
        Assert.Equal(new[] { 100f }, PublishedX(vm));
        picker.SelectedIndex = 2;                                            // Infernal = 2
        Assert.Equal(new[] { 100f, 200f }, PublishedX(vm));
        picker.SelectedIndex = 0;
        Assert.Equal(new[] { 100f, 200f }, PublishedX(vm));
    }

    [Fact]
    public void A_level_prop_its_chunk_gates_by_an_event_shows_only_while_the_event_is_on()
    {
        var vm = Rig(new[] { Level("LevelProp_A1", 100f), Level("LevelProp_HallStatue1", 200f, controller: HallOfLegends) });
        vm.ShowPropMeshes = true;
        Assert.Equal(new[] { 100f }, PublishedX(vm));

        Ev(vm, "SR_Hall_Of_Legends").IsOn = true;
        Assert.Equal(new[] { 100f, 200f }, PublishedX(vm));
        Assert.Contains("1 level prop(s) (drawn with Props on)", Ev(vm, "SR_Hall_Of_Legends").Tooltip);
        Ev(vm, "SR_Hall_Of_Legends").IsOn = false;
        Assert.Equal(new[] { 100f }, PublishedX(vm));
    }

    [Fact]
    public void Ticking_an_event_or_applying_visibility_without_a_change_does_not_republish()
    {
        var vm = Rig(Critters);
        vm.ShowPropMeshes = true;
        var published = vm.CurrentPropMeshes;

        Ev(vm, "MSITrophy").IsOn = true;
        Call(vm, "ApplyMapVisibility");

        Assert.Same(published, vm.CurrentPropMeshes);
    }

    [Fact]
    public void Nothing_is_decoded_until_Props_shows_one_and_then_once()
    {
        var vm = Rig(Critters, decoded: false);
        Assert.Null(GetField(vm, "_levelPropBuild"));                       // the map opened: nothing decoded
        Ev(vm, "MSITrophy").IsOn = true;
        Assert.Null(GetField(vm, "_levelPropBuild"));                       // an event with Props off decodes nothing

        vm.ShowPropMeshes = true;
        var build = Assert.IsAssignableFrom<Task>(GetField(vm, "_levelPropBuild"));
        vm.ShowPropMeshes = false;
        vm.ShowPropMeshes = true;
        Assert.Same(build, GetField(vm, "_levelPropBuild"));                // one build per map, however often Props is switched
    }

    [Fact]
    public void They_join_the_placed_props_and_the_banners_in_the_one_published_set()
    {
        var vm = Rig(Critters, banners: new[]
        {
            new MapBannerProp("LevelProp_Srx_Banner_Vertical1", new Vector3(900f, 0, 0), Matrix4x4.CreateTranslation(900f, 0, 0), "Srx_Banner_Vertical", "SLOT", Banners),
        });
        SetField(vm, "_propInstances", new List<PropInstanceData> { new(DummyMesh("Characters/SRU_Baron/Skins/Skin0"), Matrix4x4.CreateTranslation(800f, 0, 0)) });
        SetField(vm, "_showPropMeshes", true);                               // the backing field: the setter would decode the placed props
        Call(vm, "PublishAddedMeshPreview");
        Assert.Equal(new[] { 100f, 200f, 300f, 800f }, PublishedX(vm));      // the banners wait for their event

        Ev(vm, "MapObjectESportSponsorBanners").IsOn = true;
        Assert.Equal(new[] { 100f, 200f, 300f, 800f, 900f }, PublishedX(vm));

        SetField(vm, "_showPropMeshes", false);
        SetField(vm, "_propInstances", new List<PropInstanceData>());
        Call(vm, "PublishAddedMeshPreview");
        Assert.Equal(new[] { 900f }, PublishedX(vm));                        // Props off: the banners (event content) stay, as in M805
    }

    [Fact]
    public async Task A_level_prop_whose_skin_cannot_be_read_is_counted_and_not_drawn()
    {
        var vm = Rig(Critters, decoded: false);                              // no game mounted: no skin can be read
        var lines = new List<LogEntry>();
        ((Logger)typeof(MainWindowViewModel).GetField("_log", NonPublic)!.GetValue(vm)!).Logged += e => { lock (lines) lines.Add(e); };

        vm.ShowPropMeshes = true;
        await (Task)GetField(vm, "_levelPropBuild")!;

        Assert.Empty(PublishedX(vm));
        List<LogEntry> snapshot;
        lock (lines) snapshot = lines.ToList();
        string line = Assert.Single(snapshot, e => e.Category == "Props" && e.Message.StartsWith("Level props:")).Message;
        Assert.Contains("0 of 3 drawn", line);
        Assert.Contains("3 could not be resolved", line);
    }

    [Fact]
    public void A_map_without_level_props_publishes_exactly_what_it_did()
    {
        var vm = Rig(Array.Empty<MapLevelProp>(), decoded: false);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        foreach (var ev in vm.MapEvents) ev.IsOn = true;
        Call(vm, "ApplyMapVisibility");

        Assert.Null(vm.CurrentPropMeshes);
        Assert.DoesNotContain(nameof(MainWindowViewModel.CurrentPropMeshes), raised);
        Assert.Null(GetField(vm, "_levelPropBuild"));
    }

    [Fact]
    public void Clearing_the_viewport_drops_them_and_a_returning_tab_keeps_them()
    {
        var vm = Rig(Critters);
        vm.CurrentMesh = new MeshAsset
        {
            Positions = new float[9], Normals = new float[9], Uvs = new float[6], Indices = new uint[] { 0, 1, 2 },
            SubMeshes = new[] { new SubMeshInfo("m", 0, 3, 0) },
        };
        var scene = typeof(MainWindowViewModel).GetMethod("CaptureMapScene", NonPublic)!.Invoke(vm, null)!;

        Call(vm, "ClearViewport");
        Assert.Empty((System.Collections.ICollection)GetField(vm, "_mapLevelProps")!);
        Assert.Null(vm.CurrentPropMeshes);

        typeof(MainWindowViewModel).GetMethod("RestoreMapScene", NonPublic)!.Invoke(vm, new[] { scene });
        Assert.Equal(Critters, (IEnumerable<MapLevelProp>)GetField(vm, "_mapLevelProps")!);
        Assert.Null(GetField(vm, "_levelPropBuild"));                       // Props is off after the clear: nothing decoded yet
    }
}

/// <summary>
/// M806: the level props the installed maps ship. Skipped (with a SKIPPED line in the output) without the map WADs; the
/// "ran:" lines say what was actually checked.
/// </summary>
public sealed class MapLevelPropRealDataTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

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
    public void Base_srx_places_72_level_props_of_12_characters_in_its_listed_ungated_object_chunk()
    {
        if (Open("Map11") is not { } opened) return;
        using var wad = opened.Wad;
        var bin = Read(wad, "data/maps/mapgeometry/map11/base_srx.materials.bin")!;
        var props = MapPlaceableExtractor.ExtractLevelProps(bin);

        var families = props.GroupBy(p => p.CharacterName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key.ToLowerInvariant(), g => g.Count());
        Assert.Equal(new Dictionary<string, int>
        {
            ["sru_antlermouse"] = 11, ["sru_duck"] = 1, ["sru_stag"] = 2, ["sru_es_bannerplatform_chaos"] = 7, ["sru_es_bannerplatform_order"] = 5,
            ["sru_es_bannerwall_chaos"] = 4, ["sru_es_bannerwall_order"] = 4, ["sru_bird"] = 5, ["sru_dragon_prop"] = 2, ["sru_gromp_prop"] = 11,
            ["sru_lizard"] = 11, ["sru_snail"] = 9,
        }, families);
        Assert.Equal(72, props.Count);
        Assert.All(props, p => Assert.Equal(0u, p.VisibilityControllerHash));                  // no own controller, and the chunk is not gated
        Assert.All(props, p => Assert.Equal(0x183e98d5u, p.Id.ContainerHash));                 // one chunk, the one the map lists as 0x4185f7ea
        Assert.Equal(2, props.Count(p => p.VisibilityFlags == 128));                           // the two flagged gromp props
        Assert.All(props.Where(p => p.VisibilityFlags == 128), p => Assert.Equal("sru_gromp_prop", p.CharacterName));
        Assert.Equal(70, props.Count(p => !p.HasVisibilityFlags));
        Assert.DoesNotContain(props, p => p.Name.Contains("Srx_Banner", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(117, MapPlaceableExtractor.ExtractBanners(bin).Count);                      // M805's, untouched
        foreach (string character in families.Keys)
            Assert.True(Read(wad, $"data/characters/{character}/skins/skin0.bin") is not null, $"{character}: no skin0.bin in Map11");
        output.WriteLine($"ran: base_srx {props.Count} level prop(s): {string.Join(", ", families.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value}"))}");
    }

    [Theory]
    [InlineData("Map11")]
    [InlineData("Map12")]
    [InlineData("Map22")]
    public void Every_level_prop_character_a_map_wad_places_resolves_its_mesh_and_textures(string wadName)
    {
        if (Open(wadName) is not { } opened) return;
        using var wad = opened.Wad;
        string? Name(uint h) => opened.Db.TryGetBinName(h, out var n) ? n : null;
        string? WadPath(ulong h) => opened.Db.TryGetPath(h, out var p) ? p : null;
        var characters = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int gated = 0;
        foreach (var entry in wad.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase)))
        {
            var props = MapPlaceableExtractor.ExtractLevelProps(wad.Extract(entry));
            if (props.Count == 0) continue;
            counts[entry.Path[(entry.Path.LastIndexOf('/') + 1)..].Replace(".materials.bin", "")] = props.Count;
            gated += props.Count(p => p.VisibilityControllerHash != 0);
            foreach (var p in props) characters.Add(p.CharacterName);
        }
        Assert.NotEmpty(characters);
        Assert.Equal(0, gated);                                                                 // no shipped level prop is event-gated
        var report = new List<string>();
        foreach (string character in characters)
        {
            var skinBin = Read(wad, $"data/characters/{character.ToLowerInvariant()}/skins/skin0.bin");
            Assert.True(skinBin is not null, $"{character}: no skin0.bin");
            var meshRef = SkinMeshExtractor.Extract(skinBin!, WadPath);
            var mesh = SkinnedMeshDecoder.Decode(Read(wad, meshRef!.SimpleSkin!)!);
            Assert.True(mesh.VertexCount > 0, character);
            var materials = ChampionMaterialResolver.Resolve(skinBin!, Name, WadPath, p => Read(wad, p));
            Assert.True(materials.HasAny, $"{character}: no material");                       // every one resolves without a host bin
            foreach (string texture in materials.SubmeshDiffuse.Values.Append(materials.DefaultDiffuse ?? "").Where(t => t.Length > 0).Distinct())
                Assert.True(TextureDecoder.Decode(Read(wad, texture)!).Width > 0, $"{character}: {texture}");
            report.Add(character);
        }
        output.WriteLine($"ran: {wadName}: level props per bin {string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}"))}; "
            + $"{characters.Count} character(s), every mesh and texture decodes: {string.Join(", ", report)}");
    }

    [Theory]
    [InlineData("Map30", "data/maps/mapgeometry/map30/arenad.materials.bin")]
    [InlineData("Map453", "data/maps/mapgeometry/map453/jade_container.materials.bin")]
    [InlineData("Map22", "data/maps/mapgeometry/map22/anniversary.materials.bin")]
    public void A_map_without_level_props_reads_none(string wadName, string materialsPath)
    {
        if (Open(wadName) is not { } opened) return;
        using var wad = opened.Wad;
        if (Read(wad, materialsPath) is not { } bin) { output.WriteLine($"SKIPPED: {materialsPath} is not in {wadName}"); return; }

        Assert.Empty(MapPlaceableExtractor.ExtractLevelProps(bin));
        output.WriteLine($"ran: {materialsPath}: no level prop");
    }

    [Fact]
    public void The_Freljord_boards_poro_plays_the_clip_its_animation_info_names()
    {
        if (Open("Map22") is not { } opened) return;
        using var wad = opened.Wad;
        var found = new List<string>();
        foreach (string board in new[] { "freljord_avarosan", "freljord_frostguard", "freljord_wintersclaw" })
        {
            if (Read(wad, $"data/maps/mapgeometry/map22/{board}.materials.bin") is not { } bin) continue;
            var poro = Assert.Single(MapPlaceableExtractor.ExtractLevelProps(bin));
            Assert.Equal("TFT_BoardPoro", poro.CharacterName);
            Assert.Equal("idle1", poro.IdleAnimation);
            Assert.False(poro.HasVisibilityFlags);                                              // every layer: every board stage shows it
            Assert.True(MapVisibility.VisibleForStage(poro.VisibilityFlags, 8));
            found.Add(board);
        }
        Assert.NotEmpty(found);
        output.WriteLine($"ran: {string.Join(", ", found)}: one TFT_BoardPoro each, clip idle1, no flags");
    }

    [Fact]
    public async Task Through_the_view_model_Props_on_publishes_the_72_beside_the_89_placed_props_and_off_none()
    {
        if (Open("Map11") is not { } opened) return;
        using var wad = opened.Wad;
        string? Name(uint h) => opened.Db.TryGetBinName(h, out var n) ? n : null;
        string? WadPath(ulong h) => opened.Db.TryGetPath(h, out var p) ? p : null;
        const string dir = "data/maps/mapgeometry/map11/";
        var materials = Read(wad, dir + "base_srx.materials.bin")!;
        var bins = wad.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                && e.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && !e.Path.EndsWith("/base_srx.materials.bin", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Select(e => wad.Extract(e)).ToList();
        bins.Add(materials);
        var definition = MapVisibility.Parse(Read(wad, "data/maps/shipping/map11/map11.bin"), Name);
        var controllers = MapVisibilityControllers.Build(bins, definition);

        var vm = new MainWindowViewModel();
        void Set(string field, object? value) => typeof(MainWindowViewModel).GetField(field, NonPublic)!.SetValue(vm, value);
        object? Get(string field) => typeof(MainWindowViewModel).GetField(field, NonPublic)!.GetValue(vm);
        void Invoke(string method, params object?[] args) => typeof(MainWindowViewModel).GetMethod(method, NonPublic)!.Invoke(vm, args);
        Set("_archive", wad);   // the builds read skins, meshes and textures through it, as through a mounted project
        Set("_currentMap", MapGeoDecoder.Decode(Read(wad, dir + "base_srx.mapgeo")!, ExtendedChannelRule.From(materials, Name)));
        Set("_currentMapEntry", new WadAssetEntry { Path = dir + "base_srx.mapgeo", PathHash = HashAlgorithms.WadPath(dir + "base_srx.mapgeo"), IsResolved = true });
        Set("_mapVisibility", definition);
        Set("_mapControllers", controllers);
        Set("_visibilityResolver", new MapVisibilityResolver(controllers, definition));
        Invoke("RebuildVisibilityAxes", definition, null);
        vm.CurrentModelProps = MapPlaceableExtractor.Extract(materials, WadPath).Props;          // what the map load does
        Invoke("SetMapBanners", MapPlaceableExtractor.ExtractBanners(materials));
        Invoke("SetMapLevelProps", MapPlaceableExtractor.ExtractLevelProps(materials));
        Invoke("SetMapEvents", controllers, null, true);
        Invoke("ApplyMapVisibility");
        Assert.Null(Get("_levelPropBuild"));
        Assert.Null(vm.CurrentPropMeshes);                                                       // Props off: the default view is unchanged

        var clock = System.Diagnostics.Stopwatch.StartNew();
        vm.ShowPropMeshes = true;                                                               // the toggle: both decodes start, each publishes when done
        await (Task)Get("_levelPropBuild")!;
        while ((vm.CurrentPropMeshes?.Instances.Count ?? 0) < 89 + 72 && clock.Elapsed < TimeSpan.FromSeconds(120))
            await Task.Delay(50);                                                               // the placed props' decode, whose task the toggle does not keep
        var shown = vm.CurrentPropMeshes!.Instances;
        var level = (IReadOnlyList<PropInstanceData>)Get("_levelPropInstances")!;
        Assert.Equal(72, level.Count);
        Assert.Equal(72, shown.Count(i => level.Contains(i)));
        Assert.Equal(89 + 72, shown.Count);                                                      // the 89 placed props and the 72, one set
        var meshes = level.Select(i => i.Mesh).Distinct().ToList();
        Assert.Equal(12, meshes.Count);
        Assert.All(meshes, m => Assert.All(m.Submeshes, s => Assert.NotNull(s.Texture)));
        Assert.All(meshes, m => Assert.NotNull(m.SknBytes));                                   // D3D11 prepares Riot's shaders from these
        int animated = meshes.Count(m => m.CanAnimate);
        Assert.Equal(12, animated);                                                              // every SR level prop skin has an idle

        vm.ShowPropMeshes = false;
        Assert.DoesNotContain(vm.CurrentPropMeshes?.Instances ?? Array.Empty<PropInstanceData>(), i => level.Contains(i));
        output.WriteLine($"ran: base_srx Props on -> {shown.Count} published ({shown.Count - 72} placed props + 72 level props over {meshes.Count} mesh(es), "
            + $"{animated} animated) in {clock.Elapsed.TotalSeconds:0.00}s; Props off -> {vm.CurrentPropMeshes?.Instances.Count ?? 0}");
    }
}
