using System.Numerics;
using System.Reflection;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M802: event-only map content. A <c>MutatorMapVisibilityController</c> names an event; content that names one as its
/// visibility controller shows only while that event is on, and a normal game has none on. These tests pin the data
/// facts (class and field hashes), the resolver rule with the shipped graph shapes and the ones no shipped graph has yet,
/// and that nothing else moves: a layer controller, a map without events, and a caller that passes no event state
/// resolve exactly as before.
/// </summary>
public sealed class MapEventTests
{
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    private const uint Primary = 0xc406a533, BaronPit = 0xec733fe2, Child = 0xe21083b5;
    private const uint FieldParents = 0x3044938a, FieldParentMode = 0xc9d3f06a, FieldPrimaryBits = 0x27639032;
    private static readonly uint Mutator = H("MutatorMapVisibilityController");

    private const uint Msi = 0x8f1ab207, HallOfLegends = 0x76c50391, Banners = 0x11a9b55d;   // the shipped Map11 path hashes

    private static readonly MapVisibilityAxis Rift = new(MapVisibility.PrimaryAxisHash, "Map Visibility", 1, true, new[]
    {
        new VisibilityLayer("Base", 1), new VisibilityLayer("Infernal", 2), new VisibilityLayer("Mountain", 4),
    });

    private static BinTreeObject Event(uint path, string? name) => new(path, Mutator,
        name is null ? Array.Empty<BinTreeProperty>() : new BinTreeProperty[] { new BinTreeString(H("MutatorName"), name) });

    private static BinTreeObject Layer(uint path, int bit) => new(path, Primary, new BinTreeProperty[] { new BinTreeU8(FieldPrimaryBits, (byte)bit) });

    private static BinTreeObject Combined(uint path, uint? mode, params uint[] parents)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeContainer(FieldParents, BinPropertyType.ObjectLink, parents.Select(p => (BinTreeProperty)new BinTreeObjectLink(0, p)).ToArray()),
        };
        if (mode is { } m) props.Add(new BinTreeU32(FieldParentMode, m));
        return new BinTreeObject(path, Child, props);
    }

    private static byte[] Bin(params BinTreeObject[] objects)
    {
        using var stream = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(stream);
        return stream.ToArray();
    }

    private static MapVisibilityResolver Resolver(MapVisibilityDefinition definition, params BinTreeObject[] objects) =>
        new(MapVisibilityControllers.Build(new[] { Bin(objects) }, definition), definition);

    private static readonly MapVisibilityDefinition NoAxes = MapVisibilityDefinition.Empty;
    private static readonly MapVisibilityDefinition RiftOnly = new(new[] { Rift });
    private static readonly IReadOnlyDictionary<uint, int> Nothing = new Dictionary<uint, int>();
    private static IReadOnlySet<string> On(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    // ---- the data facts -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_class_and_field_hashes_are_the_FNV1a_of_the_shipped_names()
    {
        Assert.Equal(0x4275b121u, H("MutatorMapVisibilityController"));   // verified on Map11 (M802 census)
        Assert.Equal(0xcb0a7522u, H("MutatorName"));
        Assert.Equal(Child, H("ChildMapVisibilityController"));
    }

    [Fact]
    public void A_map_lists_its_events_once_each_in_name_order()
    {
        var controllers = MapVisibilityControllers.Build(new[]
        {
            Bin(Event(Msi, "MSITrophy"), Event(HallOfLegends, "SR_Hall_Of_Legends"), Layer(0x100, 2)),
            Bin(Event(0x9999, "msitrophy"), Event(Banners, "MapObjectESportSponsorBanners"), Event(0x9998, null)),   // a repeat in another case, and a nameless one
        });

        Assert.Equal(new[] { "MapObjectESportSponsorBanners", "MSITrophy", "SR_Hall_Of_Legends" }, controllers.EventNames);
        Assert.Empty(MapVisibilityControllers.Build(new[] { Bin(Layer(0x100, 2)) }).EventNames);
        Assert.Empty(MapVisibilityControllers.Build(Array.Empty<byte[]>()).EventNames);
    }

    [Fact]
    public void The_layer_editor_lists_an_event_controller_as_an_event()
    {
        var controllers = MapVisibilityControllers.Build(new[] { Bin(Event(Msi, "MSITrophy"), Layer(0x100, 2)) }, RiftOnly);

        var info = controllers.List().Single(c => c.Hash == Msi);
        Assert.Equal("Event", info.Kind);
        Assert.Equal("Event 0x8f1ab207 / event MSITrophy", info.Label);
        Assert.Equal("Layer", controllers.List().Single(c => c.Hash == 0x100).Kind);
    }

    // ---- the rule ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Content_gated_by_an_event_is_hidden_in_a_normal_game_and_shown_when_its_event_is_on()
    {
        var resolver = Resolver(NoAxes, Event(Msi, "MSITrophy"), Event(HallOfLegends, "SR_Hall_Of_Legends"));

        Assert.False(resolver.IsVisible(255, Msi, Nothing, null, On()));                           // a normal game: nothing on
        Assert.False(resolver.IsVisible(255, HallOfLegends, Nothing, null, On()));
        Assert.True(resolver.IsVisible(255, Msi, Nothing, null, On("MSITrophy")));
        Assert.False(resolver.IsVisible(255, HallOfLegends, Nothing, null, On("MSITrophy")));      // the OTHER event stays off
        Assert.True(resolver.IsVisible(255, HallOfLegends, Nothing, null, On("MSITrophy", "SR_Hall_Of_Legends")));
        Assert.True(resolver.IsVisible(255, Msi, Nothing, null, On("msitrophy")));                 // a name matches in any case
    }

    [Fact]
    public void A_caller_with_no_event_state_sees_event_content_as_before_M802()
    {
        var resolver = Resolver(NoAxes, Event(Msi, "MSITrophy"));

        Assert.True(resolver.IsVisible(255, Msi, Nothing));                 // the old signature
        Assert.True(resolver.IsVisible(255, Msi, Nothing, null, null));     // events not evaluated
        Assert.True(resolver.EventsAllow(Msi, null));
        Assert.False(resolver.EventsAllow(Msi, On()));
        Assert.True(resolver.EventsAllow(Msi, On("MSITrophy")));
        Assert.True(resolver.EventsAllow(0, On()));                         // no controller: nothing to gate
    }

    [Fact]
    public void A_mutator_that_names_nothing_and_a_class_nobody_handles_stay_visible()
    {
        var resolver = Resolver(NoAxes, Event(0x9998, null), new BinTreeObject(0x9997, 0xe07edfa4, new BinTreeProperty[] { new BinTreeBool(H("DefaultVisible"), false) }));

        Assert.True(resolver.IsVisible(255, 0x9998, Nothing, null, On()));
        Assert.True(resolver.IsVisible(255, 0x9997, Nothing, null, On()));
        Assert.True(resolver.IsVisible(255, 0xdead, Nothing, null, On()));  // a controller no bin defines
    }

    [Fact]
    public void Layer_controllers_resolve_exactly_the_same_whatever_the_event_state()
    {
        var resolver = Resolver(RiftOnly, Layer(0x100, 4), Combined(0x101, 3, 0x100), Event(Msi, "MSITrophy"));
        foreach (var events in new IReadOnlySet<string>?[] { null, On(), On("MSITrophy"), On("Other") })
            foreach (int selected in new[] { 0, 1, 2, 4 })
                foreach (uint controller in new uint[] { 0, 0x100, 0x101 })
                    foreach (int flags in new[] { 0, 1, 2, 123, 255 })
                    {
                        var selections = new Dictionary<uint, int> { [MapVisibility.PrimaryAxisHash] = selected };
                        Assert.Equal(resolver.IsVisible(flags, controller, selections), resolver.IsVisible(flags, controller, selections, null, events));
                    }
    }

    [Fact]
    public void A_Child_controller_joins_its_events_as_a_union_and_ParentMode_3_inverts_it()
    {
        var resolver = Resolver(NoAxes,
            Event(Msi, "MSITrophy"), Event(HallOfLegends, "SR_Hall_Of_Legends"),
            Combined(0x200, null, Msi, HallOfLegends),      // either event
            Combined(0x201, 3, Msi, HallOfLegends));        // none of the events (the shipped ParentMode 3)

        Assert.False(resolver.IsVisible(255, 0x200, Nothing, null, On()));
        Assert.True(resolver.IsVisible(255, 0x200, Nothing, null, On("MSITrophy")));
        Assert.True(resolver.IsVisible(255, 0x200, Nothing, null, On("SR_Hall_Of_Legends")));

        Assert.True(resolver.IsVisible(255, 0x201, Nothing, null, On()));
        Assert.False(resolver.IsVisible(255, 0x201, Nothing, null, On("MSITrophy")));
        Assert.False(resolver.IsVisible(255, 0x201, Nothing, null, On("SR_Hall_Of_Legends")));
    }

    [Fact]
    public void A_graph_that_reaches_both_a_layer_and_an_event_is_satisfied_by_the_event_being_on()
    {
        // no shipped Child mixes the two, but the union is what ParentMode already means: any parent on is enough
        var resolver = Resolver(RiftOnly, Layer(0x100, 2), Event(Msi, "MSITrophy"), Combined(0x300, null, 0x100, Msi), Combined(0x301, 3, 0x100, Msi));
        var infernal = new Dictionary<uint, int> { [MapVisibility.PrimaryAxisHash] = 2 };
        var mountain = new Dictionary<uint, int> { [MapVisibility.PrimaryAxisHash] = 4 };

        // event off: the layer decides, as for any layer controller
        Assert.True(resolver.IsVisible(255, 0x300, infernal, null, On()));
        Assert.False(resolver.IsVisible(255, 0x300, mountain, null, On()));
        // event on: the union is satisfied whatever layer is picked
        Assert.True(resolver.IsVisible(255, 0x300, mountain, null, On("MSITrophy")));
        // inverted: the event on hides it, the event off leaves the inverted layer rule
        Assert.False(resolver.IsVisible(255, 0x301, mountain, null, On("MSITrophy")));
        Assert.True(resolver.IsVisible(255, 0x301, mountain, null, On()));
        Assert.False(resolver.IsVisible(255, 0x301, infernal, null, On()));
    }

    [Fact]
    public void An_event_gates_content_on_a_map_that_declares_no_layers_and_the_mesh_mask_still_applies()
    {
        var resolver = Resolver(RiftOnly, Event(Msi, "MSITrophy"));
        var baseOnly = new Dictionary<uint, int> { [MapVisibility.PrimaryAxisHash] = 1 };
        var infernal = new Dictionary<uint, int> { [MapVisibility.PrimaryAxisHash] = 2 };

        Assert.False(resolver.IsVisible(255, Msi, Nothing, null, On()));
        Assert.True(resolver.IsVisible(255, Msi, Nothing, null, On("MSITrophy")));
        // every shipped event mesh is on all layers (255), so the mask is moot there; a mesh that names only Infernal is not
        // shown on Base just because its event is on
        Assert.True(resolver.IsVisible(255, Msi, infernal, null, On("MSITrophy")));
        Assert.False(resolver.IsVisible(2, Msi, baseOnly, null, On("MSITrophy")));
        Assert.True(resolver.IsVisible(2, Msi, infernal, null, On("MSITrophy")));
    }

    [Fact]
    public void The_diagnostic_says_which_event_hides_the_content_and_where_to_tick_it()
    {
        var resolver = Resolver(RiftOnly, Event(Msi, "MSITrophy"));

        var off = resolver.Resolve(255, Msi, Nothing, null, On());
        Assert.False(off.Visible);
        Assert.Contains("event 'MSITrophy' is off", off.Reason);
        Assert.Contains("Visibility Layers > Events", off.Reason);
        Assert.Equal("Event: MSITrophy (off)", off.ControllerSummary);

        var on = resolver.Resolve(255, Msi, Nothing, null, On("MSITrophy"));
        Assert.True(on.Visible);
        Assert.Contains("event 'MSITrophy' is on", on.Reason);
        Assert.Equal("Event: MSITrophy (on)", on.ControllerSummary);

        Assert.DoesNotContain("Event", resolver.Resolve(255, 0, Nothing, null, On()).ControllerSummary);   // ungated content says nothing
    }

    // ---- the label -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("SR_Hall_Of_Legends", "SR Hall Of Legends")]
    [InlineData("MSITrophy", "MSI Trophy")]
    [InlineData("MapObjectESportSponsorBanners", "Map Object ESport Sponsor Banners")]
    [InlineData("Event2024Banner", "Event2024 Banner")]
    [InlineData("plain", "plain")]
    [InlineData("A__B", "A B")]
    public void An_event_reads_as_words_and_the_raw_name_stays_for_the_tooltip(string name, string label) =>
        Assert.Equal(label, MapEventNames.Label(name));

    // ---- a particle's sound follows its particle ---------------------------------------------------------------------------

    [Fact]
    public void A_sound_derived_from_a_particle_carries_the_particles_controller()
    {
        var particle = new MapParticlePlacement("Amb", Vector3.Zero, Matrix4x4.Identity, "S", "", SystemHash: 7, VisibilityControllerHash: Msi);
        var system = new VfxSystemDefinition(7, "amb", "", Array.Empty<VfxEmitterDefinition>(), PersistentSoundEventName: "Play_amb");

        var sound = Assert.Single(MapParticleAudioExtractor.Extract(new[] { particle }, new Dictionary<uint, VfxSystemDefinition> { [7] = system }));

        Assert.True(sound.FromParticleSystem);
        Assert.Equal(Msi, sound.VisibilityControllerHash);
    }
}

/// <summary>
/// M802: the events through the REAL view model - every consumer of map visibility. The decode of a map and the drawing are
/// the milestone's probe and render checks; this drives everything downstream of the controllers and content a load leaves.
/// </summary>
public sealed class MapEventViewModelTests(ITestOutputHelper output)
{
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
    private const uint Primary = 0xc406a533, Child = 0xe21083b5;
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);
    private static readonly uint Mutator = H("MutatorMapVisibilityController");
    private const uint Msi = 0x8f1ab207, HallOfLegends = 0x76c50391, Banners = 0x11a9b55d, Dragon = 0x300, NotHallOfLegends = 0x301;

    private static void SetField(MainWindowViewModel vm, string name, object? value) =>
        typeof(MainWindowViewModel).GetField(name, NonPublic)!.SetValue(vm, value);

    private static T Call<T>(MainWindowViewModel vm, string name, params object?[] args) =>
        (T)typeof(MainWindowViewModel).GetMethod(name, NonPublic)!.Invoke(vm, args)!;

    private static void Call(MainWindowViewModel vm, string name, params object?[] args) =>
        typeof(MainWindowViewModel).GetMethod(name, NonPublic)!.Invoke(vm, args);

    private static readonly MapVisibilityAxis Rift = new(MapVisibility.PrimaryAxisHash, "Map Visibility", 1, true, new[]
    {
        new VisibilityLayer("Base", 1), new VisibilityLayer("Infernal", 2), new VisibilityLayer("Mountain", 4),
    });

    private static byte[] Bin(params BinTreeObject[] objects)
    {
        using var stream = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(stream);
        return stream.ToArray();
    }

    private static BinTreeObject Event(uint path, string name) => new(path, Mutator, new BinTreeProperty[] { new BinTreeString(H("MutatorName"), name) });

    /// <summary>The controllers of a Summoner's-Rift-shaped map: three events, a dragon layer, and an ordinary Child.</summary>
    private static MapVisibilityControllers Controllers(MapVisibilityDefinition definition) =>
        MapVisibilityControllers.Build(new[]
        {
            Bin(Event(Msi, "MSITrophy"), Event(HallOfLegends, "SR_Hall_Of_Legends"), Event(Banners, "MapObjectESportSponsorBanners"),
                new BinTreeObject(Dragon, Primary, new BinTreeProperty[] { new BinTreeU8(0x27639032, 2) })),
        }, definition);

    private static readonly WadAssetEntry TestEntry = new()
    {
        Path = "data/maps/mapgeometry/map11/testrift.mapgeo", PathHash = 0x1234u, IsResolved = true,
    };

    private static MapGeoMesh Mesh(int index, uint controller) => new()
    {
        Index = index, Name = "Mesh" + index, VertexStart = 0, VertexCount = 3, Transform = Matrix4x4.Identity, Pivot = Vector3.Zero,
        VisibilityFlags = 255, ControllerHash = controller,
    };

    /// <summary>Four meshes of one group each: an ordinary one, the Hall of Legends', the MSI trophy's and a dragon-layer one.</summary>
    private static MapGeoAsset FourGroupMap() => new()
    {
        Positions = new float[9], Normals = new float[9], Uvs = new float[6], Indices = new uint[] { 0, 1, 2 },
        Meshes = new[] { Mesh(0, 0), Mesh(1, HallOfLegends), Mesh(2, Msi), Mesh(3, Dragon) },
        Groups = new[]
        {
            new MapGeoGroup("Ground", 0, 3, "Mesh0", VisibilityFlags: 255, MeshIndex: 0),
            new MapGeoGroup("HallOfLegends", 0, 3, "Mesh1", VisibilityFlags: 255, ControllerHash: HallOfLegends, MeshIndex: 1),
            new MapGeoGroup("MsiTrophy", 0, 3, "Mesh2", VisibilityFlags: 255, ControllerHash: Msi, MeshIndex: 2),
            new MapGeoGroup("InfernalOnly", 0, 3, "Mesh3", VisibilityFlags: 255, ControllerHash: Dragon, MeshIndex: 3),
        },
    };

    private static MapParticlePlacement Particle(string name, float x, uint controller = 0) =>
        new(name, new Vector3(x, 0f, 0f), Matrix4x4.CreateTranslation(x, 0f, 0f), "S", "", SystemHash: 1, VisibilityControllerHash: controller);

    private static MapSoundPlacement Sound(string name, float x, uint controller = 0, bool derived = true) =>
        new(name, "Play_" + name, new Vector3(x, 0f, 0f), Matrix4x4.CreateTranslation(x, 0f, 0f), FromParticleSystem: derived, VisibilityControllerHash: controller);

    private static MainWindowViewModel Rig(bool particles = true, bool sounds = true)
    {
        var definition = new MapVisibilityDefinition(new[] { Rift });
        var controllers = Controllers(definition);
        var vm = new MainWindowViewModel();
        SetField(vm, "_currentMap", FourGroupMap());
        SetField(vm, "_currentMapEntry", TestEntry);
        SetField(vm, "_currentMapBytes", new byte[] { 1 });
        SetField(vm, "_mapVisibility", definition);
        SetField(vm, "_mapControllers", controllers);
        SetField(vm, "_visibilityResolver", new MapVisibilityResolver(controllers, definition));
        vm.CurrentMesh = new MeshAsset
        {
            Positions = new float[9], Normals = new float[9], Uvs = new float[6], Indices = new uint[] { 0, 1, 2 },
            SubMeshes = new[] { new SubMeshInfo("m", 0, 3, 0) },
        };
        Call(vm, "RebuildVisibilityAxes", definition, null);
        if (particles)
            vm.CurrentModelParticles = new[]
            {
                Particle("Ordinary", 0f), Particle("SRU_MSI_Winner1", 1000f, Msi), Particle("Godrays", 2000f, HallOfLegends),
                Particle("SRU_MSI_Winner2", 3000f, Msi),
            };
        if (sounds)
            vm.CurrentModelSounds = new[] { Sound("Amb", 0f), Sound("MsiAmb", 1000f, Msi), Sound("DirectAudio", 2000f, derived: false) };
        Call(vm, "SetMapEvents", controllers, null, true);   // what BuildMapVisibility does at the end of a map load
        Call(vm, "ApplyMapVisibility");
        return vm;
    }

    private static MainWindowViewModel.MapEventViewModel Ev(MainWindowViewModel vm, string name) => vm.MapEvents.Single(e => e.Name == name);

    // ---- a map that opens -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_map_with_events_lists_them_all_off_and_hides_what_they_gate()
    {
        var vm = Rig();

        Assert.True(vm.HasMapEvents);
        Assert.Equal(new[] { "MapObjectESportSponsorBanners", "MSITrophy", "SR_Hall_Of_Legends" }, vm.MapEvents.Select(e => e.Name));
        Assert.Equal(new[] { "Map Object ESport Sponsor Banners", "MSI Trophy", "SR Hall Of Legends" }, vm.MapEvents.Select(e => e.Label));
        Assert.All(vm.MapEvents, e => Assert.False(e.IsOn));
        Assert.Equal(new[] { true, false, false, true }, vm.CurrentModelSubmeshVisible);   // the two event groups are hidden, the rest is not
        Assert.Equal(new[] { new Vector3(0f, 0f, 0f) }, vm.ParticleMarkers);               // only the ordinary particle
        Assert.Equal(new[] { 0f, 2000f }, vm.SoundMarkers!.Select(p => p.X).Order());   // the MSI particle's derived sound follows its particle; a direct MapAudio names no controller in Riot's data
        Assert.True(vm.ShowVisibilityLayers);
    }

    [Fact]
    public void The_events_block_shows_for_events_alone_and_for_layers_alone_and_not_for_neither()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.HasMapEvents);
        Assert.False(vm.ShowVisibilityLayers);
        Assert.Empty(vm.MapEvents);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Call(vm, "SetMapEvents", Controllers(MapVisibilityDefinition.Empty), null, false);   // events, no layer axes
        Assert.True(vm.HasMapEvents);
        Assert.False(vm.HasVisibilityAxes);
        Assert.True(vm.ShowVisibilityLayers);
        Assert.Contains(nameof(MainWindowViewModel.ShowVisibilityLayers), raised);

        Call(vm, "SetMapEvents", null, null, false);
        Call(vm, "RebuildVisibilityAxes", new MapVisibilityDefinition(new[] { Rift }), null);   // layers, no events
        Assert.False(vm.HasMapEvents);
        Assert.True(vm.ShowVisibilityLayers);
    }

    [Fact]
    public void A_map_without_a_mutator_controller_has_no_events_and_changes_nothing()
    {
        var definition = new MapVisibilityDefinition(new[] { Rift });
        var vm = new MainWindowViewModel();
        var controllers = MapVisibilityControllers.Build(new[] { Bin(new BinTreeObject(Dragon, Primary, new BinTreeProperty[] { new BinTreeU8(0x27639032, 2) })) }, definition);
        SetField(vm, "_currentMap", FourGroupMap());
        SetField(vm, "_mapVisibility", definition);
        SetField(vm, "_mapControllers", controllers);
        SetField(vm, "_visibilityResolver", new MapVisibilityResolver(controllers, definition));
        Call(vm, "RebuildVisibilityAxes", definition, null);
        var lines = new List<LogEntry>();
        ((Logger)typeof(MainWindowViewModel).GetField("_log", NonPublic)!.GetValue(vm)!).Logged += lines.Add;

        Call(vm, "SetMapEvents", controllers, null, true);
        Call(vm, "ApplyMapVisibility");

        Assert.False(vm.HasMapEvents);
        Assert.Empty(vm.MapEvents);
        Assert.DoesNotContain(lines, e => e.Category == "Events");          // nothing to announce
        Assert.Equal(new[] { true, true, true, true }, vm.CurrentModelSubmeshVisible);   // an unknown controller hash (0x8f1ab207) is unconstrained
    }

    [Fact]
    public void Opening_a_map_says_what_starts_hidden()
    {
        var lines = new List<LogEntry>();
        var vm = new MainWindowViewModel();
        ((Logger)typeof(MainWindowViewModel).GetField("_log", NonPublic)!.GetValue(vm)!).Logged += lines.Add;
        var definition = new MapVisibilityDefinition(new[] { Rift });
        var controllers = Controllers(definition);
        SetField(vm, "_currentMap", FourGroupMap());
        SetField(vm, "_mapControllers", controllers);
        SetField(vm, "_visibilityResolver", new MapVisibilityResolver(controllers, definition));
        vm.CurrentModelParticles = new[] { Particle("A", 0f), Particle("B", 1000f, Msi), Particle("C", 2000f, HallOfLegends) };

        Call(vm, "SetMapEvents", controllers, null, true);

        var line = Assert.Single(lines, e => e.Category == "Events").Message;
        Assert.Contains("3 event(s) gate content in this map: MapObjectESportSponsorBanners, MSITrophy, SR_Hall_Of_Legends", line);
        Assert.Contains("All are OFF, as in a normal game", line);
        Assert.Contains("2 mesh(es) (2 draw group(s)), 2 particle placement(s) and 0 sound(s) start hidden", line);
        Assert.Contains("Visibility Layers > Events", line);
    }

    // ---- a checkbox ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Ticking_an_event_shows_what_it_gates_on_the_meshes_the_particle_icons_and_the_sound_icons()
    {
        var vm = Rig();

        Ev(vm, "MSITrophy").IsOn = true;
        Assert.Equal(new[] { true, false, true, true }, vm.CurrentModelSubmeshVisible);
        Assert.Equal(new[] { 0f, 1000f, 3000f }, vm.ParticleMarkers!.Select(p => p.X).Order());     // both MSI winners, not the Hall of Legends godrays
        Assert.Equal(new[] { 0f, 1000f, 2000f }, vm.SoundMarkers!.Select(p => p.X).Order());          // and its sound with it

        Ev(vm, "SR_Hall_Of_Legends").IsOn = true;
        Assert.Equal(new[] { true, true, true, true }, vm.CurrentModelSubmeshVisible);
        Assert.Equal(new[] { 0f, 1000f, 2000f, 3000f }, vm.ParticleMarkers!.Select(p => p.X).Order());

        Ev(vm, "MSITrophy").IsOn = false;                                                   // and off again
        Assert.Equal(new[] { true, true, false, true }, vm.CurrentModelSubmeshVisible);
        Assert.Equal(new[] { 0f, 2000f }, vm.ParticleMarkers!.Select(p => p.X).Order());
    }

    [Fact]
    public void An_event_that_gates_nothing_the_editor_draws_changes_nothing()
    {
        var vm = Rig();
        var before = vm.CurrentModelSubmeshVisible!.ToList();
        var particlesBefore = vm.ParticleMarkers!.ToList();

        Ev(vm, "MapObjectESportSponsorBanners").IsOn = true;   // the banners' GdsMapObject props are not something the editor draws

        Assert.Equal(before, vm.CurrentModelSubmeshVisible);
        Assert.Equal(particlesBefore, vm.ParticleMarkers);
        Assert.Contains("Nothing this editor draws is gated by it", Ev(vm, "MapObjectESportSponsorBanners").Tooltip);
    }

    [Fact]
    public void The_tooltip_names_the_raw_event_and_counts_what_it_gates()
    {
        var vm = Rig();

        string msi = Ev(vm, "MSITrophy").Tooltip;
        Assert.Contains("Event \"MSITrophy\" (MutatorMapVisibilityController)", msi);
        Assert.Contains("gates 1 mesh(es), 2 particle placement(s) and 1 sound(s)", msi);
        Assert.Contains("Session only", msi);
        Assert.Contains("gates 1 mesh(es), 1 particle placement(s) and 0 sound(s)", Ev(vm, "SR_Hall_Of_Legends").Tooltip);
    }

    [Fact]
    public void Each_toggle_logs_one_line_with_the_counts()
    {
        var vm = Rig();
        var lines = new List<LogEntry>();
        ((Logger)typeof(MainWindowViewModel).GetField("_log", NonPublic)!.GetValue(vm)!).Logged += lines.Add;

        Ev(vm, "MSITrophy").IsOn = true;
        var on = Assert.Single(lines, e => e.Category == "Events").Message;
        Assert.Contains("Event 'MSITrophy' ON: it gates 1 mesh(es) (1 draw group(s)), 2 particle placement(s) and 1 sound(s).", on);
        Assert.Contains("Events on: MSITrophy;", on);
        Assert.Contains("still hidden by events: 1 mesh(es) (1 draw group(s)), 1 particle placement(s), 0 sound(s).", on);

        lines.Clear();
        Ev(vm, "MSITrophy").IsOn = false;
        var off = Assert.Single(lines, e => e.Category == "Events").Message;
        Assert.Contains("Event 'MSITrophy' off:", off);
        Assert.Contains("Events on: none;", off);
    }

    [Fact]
    public void Setting_a_checkbox_to_the_value_it_has_does_not_log_or_recompute()
    {
        var vm = Rig();
        var lines = new List<LogEntry>();
        ((Logger)typeof(MainWindowViewModel).GetField("_log", NonPublic)!.GetValue(vm)!).Logged += lines.Add;
        var published = vm.CurrentModelSubmeshVisible;

        Ev(vm, "MSITrophy").IsOn = false;

        Assert.Empty(lines);
        Assert.Same(published, vm.CurrentModelSubmeshVisible);
    }

    [Fact]
    public void A_dragon_layer_picked_beside_an_event_each_keep_their_own_rule()
    {
        var vm = Rig();
        var layers = vm.VisibilityAxes.Single();

        layers.SelectedIndex = 3;                                 // Mountain: the dragon-layer group (Infernal only) goes, the events stay off
        Assert.Equal(new[] { true, false, false, false }, vm.CurrentModelSubmeshVisible);

        Ev(vm, "SR_Hall_Of_Legends").IsOn = true;
        Assert.Equal(new[] { true, true, false, false }, vm.CurrentModelSubmeshVisible);

        layers.SelectedIndex = 2;                                 // Infernal
        Assert.Equal(new[] { true, true, false, true }, vm.CurrentModelSubmeshVisible);
    }

    // ---- picking ------------------------------------------------------------------------------------------------------------

    private static object? Click(MainWindowViewModel vm, float x)
    {
        vm.SelectedOutlinerItem = null;
        vm.SelectAnyFromViewport(new Vector3(x, 0f, -500f), Vector3.UnitZ);
        return vm.SelectedOutlinerItem;
    }

    [Fact]
    public void A_click_cannot_pick_a_particle_its_event_hides()
    {
        var vm = Rig(sounds: false);

        Assert.Equal("Ordinary", Assert.IsType<ParticlePlacementViewModel>(Click(vm, 0f)).Placement.Name);
        Assert.IsNotType<ParticlePlacementViewModel>(Click(vm, 1000f));          // SRU_MSI_Winner1: MSITrophy is off
        Assert.IsNotType<ParticlePlacementViewModel>(Click(vm, 2000f));          // the godrays: SR_Hall_Of_Legends is off

        Ev(vm, "MSITrophy").IsOn = true;
        Assert.Equal("SRU_MSI_Winner1", Assert.IsType<ParticlePlacementViewModel>(Click(vm, 1000f)).Placement.Name);
        Assert.IsNotType<ParticlePlacementViewModel>(Click(vm, 2000f));

        Ev(vm, "MSITrophy").IsOn = false;
        Assert.IsNotType<ParticlePlacementViewModel>(Click(vm, 1000f));          // and hidden again
    }

    [Fact]
    public void A_click_cannot_pick_a_sound_its_event_hides()
    {
        var vm = Rig(particles: false);

        Assert.Equal("Amb", Assert.IsType<MapSoundViewModel>(Click(vm, 0f)).Sound.Name);
        Assert.IsNotType<MapSoundViewModel>(Click(vm, 1000f));                   // MsiAmb derives from an MSI particle

        Ev(vm, "MSITrophy").IsOn = true;
        Assert.Equal("MsiAmb", Assert.IsType<MapSoundViewModel>(Click(vm, 1000f)).Sound.Name);
    }

    // ---- a map tab that returns, a viewport that is cleared -------------------------------------------------------------------

    [Fact]
    public void A_map_tab_that_returns_has_the_events_it_left_ticked()
    {
        var vm = Rig();
        Ev(vm, "SR_Hall_Of_Legends").IsOn = true;
        var atCapture = vm.CurrentModelSubmeshVisible!.ToList();
        var particlesAtCapture = vm.ParticleMarkers!.ToList();
        var scene = typeof(MainWindowViewModel).GetMethod("CaptureMapScene", NonPublic)!.Invoke(vm, null)!;

        // the skinned-mesh tab leaves the map loaded, and another map's load resets the events
        Ev(vm, "MSITrophy").IsOn = true;
        Call(vm, "SetMapEvents", null, null, false);
        Assert.False(vm.HasMapEvents);

        typeof(MainWindowViewModel).GetMethod("RestoreMapScene", NonPublic)!.Invoke(vm, new[] { scene });

        Assert.True(vm.HasMapEvents);
        Assert.Equal(new[] { "SR_Hall_Of_Legends" }, vm.MapEvents.Where(e => e.IsOn).Select(e => e.Name));
        Assert.Equal(atCapture, vm.CurrentModelSubmeshVisible);
        Assert.Equal(particlesAtCapture, vm.ParticleMarkers);
    }

    [Fact]
    public void Clearing_the_viewport_clears_the_events_and_a_new_map_starts_with_none_on()
    {
        var vm = Rig();
        Ev(vm, "MSITrophy").IsOn = true;

        Call(vm, "ClearViewport");

        Assert.False(vm.HasMapEvents);
        Assert.Empty(vm.MapEvents);
        Assert.False(vm.ShowVisibilityLayers);

        // the next map (same event names) opens with everything off again
        var next = Rig();
        Assert.All(next.MapEvents, e => Assert.False(e.IsOn));
    }

    [Fact]
    public void Events_are_never_written_to_the_settings_and_never_dirty_the_project()
    {
        string settings = ReyEngine.Core.Settings.EditorSettings.StorePath;
        byte[]? before = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        DateTime? timeBefore = File.Exists(settings) ? File.GetLastWriteTimeUtc(settings) : null;

        var vm = Rig();
        vm.Project.IsDirty = false;
        Ev(vm, "MSITrophy").IsOn = true;
        Ev(vm, "SR_Hall_Of_Legends").IsOn = true;
        Ev(vm, "MSITrophy").IsOn = false;
        Assert.False(vm.Project.IsDirty);                           // a view choice, not an edit
        Assert.False(vm.HasMapMoves);

        byte[]? after = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        Assert.Equal(before is null, after is null);
        if (before is not null) Assert.True(before.AsSpan().SequenceEqual(after), "settings.json changed");
        Assert.Equal(timeBefore, File.Exists(settings) ? File.GetLastWriteTimeUtc(settings) : null);
        output.WriteLine($"settings file {(before is null ? "absent" : $"{before.Length} bytes")} before and after three toggles");
    }

    // ---- the panel markup ----------------------------------------------------------------------------------------------------

    private static string? Outliner()
    {
        string? root = TestRunIsolation.RepoRoot();
        string? path = root is null ? null : Path.Combine(root, "src", "ReyEngine.App", "Views", "MapOutlinerView.axaml");
        return path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void The_events_block_binds_to_members_that_exist_and_paints_with_theme_resources()
    {
        if (Outliner() is not { } markup) return;
        var checkbox = typeof(MainWindowViewModel.MapEventViewModel);

        Assert.Contains("IsVisible=\"{Binding ShowVisibilityLayers}\"", markup);
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("ShowVisibilityLayers"));
        Assert.Contains("ItemsSource=\"{Binding MapEvents}\"", markup);
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("MapEvents"));
        // an EMPTY block is still a visible StackPanel child and would take a spacing gap on every map without events
        Assert.Contains("IsVisible=\"{Binding HasMapEvents}\"", markup);
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("HasMapEvents"));
        foreach (string member in new[] { "Label", "IsOn", "Tooltip" })
        {
            Assert.Contains("{Binding " + member + "}", markup);
            Assert.NotNull(checkbox.GetProperty(member));
        }

        int start = markup.IndexOf("M802: only a map whose bins hold", StringComparison.Ordinal);
        Assert.True(start > 0);
        string section = markup[start..markup.IndexOf("view toggles (what renders in the viewport)", start, StringComparison.Ordinal)];
        Assert.DoesNotContain("=\"#", section);
        Assert.Contains("{DynamicResource ReyTextDimBrush}", section);
        Assert.Contains("<CheckBox", section);
    }

    [Fact]
    public void The_events_block_sits_inside_the_visibility_layers_section_after_the_board_stage()
    {
        if (Outliner() is not { } markup) return;
        int layers = markup.IndexOf("VISIBILITY LAYERS", StringComparison.Ordinal);
        int stage = markup.IndexOf("M797: TFT boards only", StringComparison.Ordinal);
        int events = markup.IndexOf("M802: only a map whose bins hold", StringComparison.Ordinal);
        int toggles = markup.IndexOf("view toggles (what renders in the viewport)", StringComparison.Ordinal);
        Assert.True(layers > 0 && layers < stage && stage < events && events < toggles);
    }
}

/// <summary>
/// M802: Summoner's Rift, from the installed game. Skipped (with a SKIPPED line in the output) without Map11; the
/// "ran:" lines say what was actually checked.
/// </summary>
public sealed class MapEventRealDataTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    private static void SetField(MainWindowViewModel vm, string name, object? value) =>
        typeof(MainWindowViewModel).GetField(name, NonPublic)!.SetValue(vm, value);

    private static void Call(MainWindowViewModel vm, string name, params object?[] args) =>
        typeof(MainWindowViewModel).GetMethod(name, NonPublic)!.Invoke(vm, args);

    private sealed record Rift(WadArchive Wad, HashDatabase Db, MapVisibilityDefinition Definition, MapVisibilityControllers Controllers,
        byte[] Materials, byte[] MapGeo) : IDisposable
    {
        public void Dispose() => Wad.Dispose();
        public string? Name(uint hash) => Db.TryGetBinName(hash, out var n) ? n : null;
    }

    /// <summary>A shipped map folder the way <c>BuildMapVisibility</c> reads it: the shipping bin's axes, then every
    /// sibling bin of the mapgeo's folder with the map's own materials bin last.</summary>
    private Rift? OpenMap(string wadName, string skin)
    {
        string path = Path.Combine(Final, "Maps", "Shipping", wadName + ".wad.client");
        if (!File.Exists(path)) { output.WriteLine($"SKIPPED: {wadName}.wad.client is not installed"); return null; }
        var db = new HashSyncService().LoadLocal(_ => { });
        var wad = WadArchive.Open(path, new WadPathResolver(db));
        string id = wadName[3..];
        string dir = $"data/maps/mapgeometry/map{id}/";
        ulong materialsHash = HashAlgorithms.WadPath(dir + skin + ".materials.bin");
        ulong geoHash = HashAlgorithms.WadPath(dir + skin + ".mapgeo");
        ulong shippingHash = HashAlgorithms.WadPath($"data/maps/shipping/map{id}/map{id}.bin");
        if (!wad.TryGetEntry(materialsHash, out _) || !wad.TryGetEntry(geoHash, out _))
        { output.WriteLine($"SKIPPED: {wadName}/{skin} is not in the WAD"); wad.Dispose(); return null; }

        string? Name(uint h) => db.TryGetBinName(h, out var n) ? n : null;
        var bins = wad.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                && e.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && e.PathHash != materialsHash)
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Select(e => wad.Extract(e)).ToList();
        byte[] materials = wad.Extract(materialsHash);
        bins.Add(materials);
        var definition = wad.TryGetEntry(shippingHash, out _) ? MapVisibility.Parse(wad.Extract(shippingHash), Name) : MapVisibilityDefinition.Empty;
        return new Rift(wad, db, definition, MapVisibilityControllers.Build(bins, definition), materials, wad.Extract(geoHash));
    }

    private MainWindowViewModel Vm(Rift rift, MapGeoAsset map, IReadOnlyList<MapParticlePlacement>? particles = null)
    {
        var vm = new MainWindowViewModel();
        SetField(vm, "_currentMap", map);
        SetField(vm, "_currentMapEntry", new WadAssetEntry { Path = "data/maps/mapgeometry/map11/base_srx.mapgeo", PathHash = 0x1234u, IsResolved = true });
        SetField(vm, "_currentMapBytes", new byte[] { 1 });
        SetField(vm, "_mapVisibility", rift.Definition);
        SetField(vm, "_mapControllers", rift.Controllers);
        SetField(vm, "_visibilityResolver", new MapVisibilityResolver(rift.Controllers, rift.Definition));
        Call(vm, "RebuildVisibilityAxes", rift.Definition, null);
        if (particles is not null) vm.CurrentModelParticles = particles;
        Call(vm, "SetMapEvents", rift.Controllers, null, true);
        Call(vm, "ApplyMapVisibility");
        return vm;
    }

    [Fact]
    public void Base_srx_names_three_events()
    {
        if (OpenMap("Map11", "base_srx") is not { } rift) return;
        using var _ = rift;

        Assert.Equal(new[] { "MapObjectESportSponsorBanners", "MSITrophy", "SR_Hall_Of_Legends" }, rift.Controllers.EventNames);
        Assert.Equal(0x4275b121u, H("MutatorMapVisibilityController"));
        // the three controllers are the ones the dump showed: path hash -> name
        foreach (var (hash, name) in new[] { (0x76c50391u, "SR_Hall_Of_Legends"), (0x11a9b55du, "MapObjectESportSponsorBanners"), (0x8f1ab207u, "MSITrophy") })
            Assert.Equal(new[] { name }, rift.Controllers.Resolve(hash).Mutators);
        output.WriteLine($"ran: base_srx events [{string.Join(", ", rift.Controllers.EventNames)}] from {rift.Controllers.Count} controller(s)");
    }

    [Fact]
    public void No_shipped_Child_controller_has_an_event_among_its_parents_and_ParentMode_is_absent_1_or_3()
    {
        // The census behind the rule: a Child joins its parents as a union and ParentMode 3 inverts it. Nothing shipped
        // combines that with an event, so an event's graph is always a single direct controller today - a patch that
        // changes this should fail here, where the semantics get looked at again, not pass unnoticed.
        if (OpenMap("Map11", "base_srx") is not { } rift) return;
        using var _ = rift;
        uint child = H("ChildMapVisibilityController"), mutator = H("MutatorMapVisibilityController");
        const uint parentsField = 0x3044938a, parentModeField = 0xc9d3f06a;
        var modes = new SortedDictionary<string, int>();
        int children = 0, withEventParent = 0, bins = 0;
        foreach (var entry in rift.Wad.Entries.Where(e => e.IsResolved && e.Path.StartsWith("data/maps/mapgeometry/map11/", StringComparison.OrdinalIgnoreCase)
                     && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
        {
            var tree = SafeBinTree.Parse(rift.Wad.Extract(entry));
            bins++;
            foreach (var o in tree.Objects.Values.Where(o => o.ClassHash == child))
            {
                children++;
                string mode = o.Properties.TryGetValue(parentModeField, out var m) && m is BinTreeU32 u ? u.Value.ToString() : "absent";
                modes[mode] = modes.GetValueOrDefault(mode) + 1;
                if (o.Properties.TryGetValue(parentsField, out var p) && p is BinTreeContainer parents
                    && parents.Elements.OfType<BinTreeObjectLink>().Any(l => tree.Objects.TryGetValue(l.Value, out var po) && po.ClassHash == mutator))
                    withEventParent++;
            }
        }

        Assert.True(children > 0);
        Assert.Equal(0, withEventParent);
        Assert.Subset(new HashSet<string> { "absent", "1", "3" }, new HashSet<string>(modes.Keys));
        output.WriteLine($"ran: {children} Child controller(s) across {bins} Map11 bin(s); ParentMode {string.Join(", ", modes.Select(kv => $"{kv.Key} x{kv.Value}"))}; "
            + $"{withEventParent} with an event among their parents");
    }

    [Fact]
    public void The_MSI_winners_are_hidden_by_default_and_shown_with_MSITrophy_on()
    {
        if (OpenMap("Map11", "base_srx") is not { } rift) return;
        using var _ = rift;
        var particles = MapParticleExtractor.Extract(rift.Materials, rift.Name);
        var resolver = new MapVisibilityResolver(rift.Controllers, rift.Definition);
        var none = new Dictionary<uint, int>();
        var winners = particles.Where(p => p.Name is "SRU_MSI_Winner1" or "SRU_MSI_Winner2").ToList();

        Assert.Equal(2, winners.Count);
        Assert.All(winners, p => Assert.Equal(0x8f1ab207u, p.VisibilityControllerHash));
        foreach (var p in winners)
        {
            Assert.False(resolver.IsVisible(p.VisibilityFlags, p.VisibilityControllerHash, none, null, new HashSet<string>()));
            Assert.True(resolver.IsVisible(p.VisibilityFlags, p.VisibilityControllerHash, none, null, new HashSet<string> { "MSITrophy" }));
            Assert.False(resolver.IsVisible(p.VisibilityFlags, p.VisibilityControllerHash, none, null, new HashSet<string> { "SR_Hall_Of_Legends" }));
        }

        // and through the view model's own gate, for every placement in the bin
        var vm = Vm(rift, MapGeoDecoder.Decode(rift.MapGeo, ExtendedChannelRule.From(rift.Materials, rift.Name)), particles);
        bool Visible(MapParticlePlacement p) => (bool)typeof(MainWindowViewModel).GetMethod("IsParticleVisible", NonPublic)!.Invoke(vm, new object?[] { p, null })!;
        int gatedByEvents = particles.Count(p => p.VisibilityControllerHash is 0x8f1ab207u or 0x76c50391u or 0x11a9b55du);
        Assert.InRange(gatedByEvents, 3, 10);
        Assert.False(Visible(winners[0]) || Visible(winners[1]));
        Assert.Equal(particles.Count - gatedByEvents, particles.Count(Visible));

        vm.MapEvents.Single(e => e.Name == "MSITrophy").IsOn = true;
        Assert.True(Visible(winners[0]) && Visible(winners[1]));
        Assert.Equal(particles.Count - (gatedByEvents - 2), particles.Count(Visible));
        output.WriteLine($"ran: {particles.Count} placement(s) in base_srx; {gatedByEvents} gated by an event (SRU_MSI_Winner1/2 + the Hall of Legends godrays), "
            + $"{particles.Count(Visible)} shown with MSITrophy on; markers {vm.ParticleMarkers?.Count}");
    }

    [Fact]
    public void Base_srx_default_view_hides_the_Hall_of_Legends_meshes_and_nothing_else_and_each_event_brings_back_exactly_its_own()
    {
        if (OpenMap("Map11", "base_srx") is not { } rift) return;
        using var _ = rift;
        var map = MapGeoDecoder.Decode(rift.MapGeo, ExtendedChannelRule.From(rift.Materials, rift.Name));
        var vm = Vm(rift, map);

        // what hides is what the census found: the meshes whose controller reaches an event
        var gatedMeshes = map.Meshes.Where(m => rift.Controllers.Resolve(m.EffectiveController).Mutators.Count > 0).ToList();
        int gatedGroups = map.Groups.Count(g => gatedMeshes.Any(m => m.Index == g.MeshIndex));
        Assert.True(gatedMeshes.Count >= 2);
        Assert.All(gatedMeshes, m => Assert.Equal(new[] { "SR_Hall_Of_Legends" }, rift.Controllers.Resolve(m.EffectiveController).Mutators));

        var atDefault = vm.CurrentModelSubmeshVisible!.ToList();
        Assert.Equal(gatedGroups, atDefault.Count(v => !v));                                       // exactly those groups are hidden
        Assert.Equal(map.Groups.Count - gatedGroups, atDefault.Count(v => v));

        // before M802 (events not evaluated) every group of the map showed: this is the visible default change
        var before = new MapVisibilityResolver(rift.Controllers, rift.Definition);
        int shownBefore = map.Groups.Count(g =>
        {
            var mesh = map.Meshes.FirstOrDefault(m => m.Index == g.MeshIndex);
            return before.IsVisible(mesh?.EffectiveVisibility ?? g.VisibilityFlags, mesh?.EffectiveController ?? g.ControllerHash, new Dictionary<uint, int>());
        });
        Assert.Equal(map.Groups.Count, shownBefore);

        vm.MapEvents.Single(e => e.Name == "MapObjectESportSponsorBanners").IsOn = true;
        Assert.Equal(atDefault, vm.CurrentModelSubmeshVisible);                                    // gates nothing the map draws
        vm.MapEvents.Single(e => e.Name == "MSITrophy").IsOn = true;
        Assert.Equal(atDefault, vm.CurrentModelSubmeshVisible);                                    // no MSI mesh in base_srx
        vm.MapEvents.Single(e => e.Name == "SR_Hall_Of_Legends").IsOn = true;
        Assert.All(vm.CurrentModelSubmeshVisible!, visible => Assert.True(visible));                                   // every group shows again
        output.WriteLine($"ran: base_srx {map.Meshes.Count} mesh(es) / {map.Groups.Count} group(s); default view hides {gatedMeshes.Count} mesh(es) "
            + $"({gatedGroups} group(s)) [{string.Join(", ", gatedMeshes.Select(m => m.Name))}], all {map.Groups.Count} shown before M802 and with the Hall of Legends ticked");
    }

    [Fact]
    public void The_banner_props_are_hidden_by_default_and_shown_with_their_event_on()
    {
        if (OpenMap("Map11", "base_srx") is not { } rift) return;
        using var _ = rift;
        var tree = SafeBinTree.Parse(rift.Materials);
        uint gds = H("GdsMapObject"), container = H("MapPlaceableContainer"), controllerField = H("VisibilityController"), itemsField = H("items");
        var banners = new List<uint>();
        foreach (var o in tree.Objects.Values.Where(o => o.ClassHash == container))
            if (o.Properties.TryGetValue(itemsField, out var items) && items is BinTreeMap map)
                foreach (var kv in map)
                    if (kv.Value is BinTreeStruct s && s.ClassHash == gds && s.Properties.TryGetValue(controllerField, out var vc) && vc is BinTreeObjectLink link)
                        banners.Add(link.Value);
        var resolver = new MapVisibilityResolver(rift.Controllers, rift.Definition);
        var none = new Dictionary<uint, int>();

        Assert.InRange(banners.Count, 100, 200);
        Assert.All(banners, hash => Assert.Equal(0x11a9b55du, hash));
        Assert.Equal(banners.Count, banners.Count(hash => !resolver.IsVisible(255, hash, none, null, new HashSet<string>())));
        Assert.Equal(0, banners.Count(hash => !resolver.IsVisible(255, hash, none, null, new HashSet<string> { "MapObjectESportSponsorBanners" })));
        output.WriteLine($"ran: {banners.Count} GdsMapObject banner prop(s) in base_srx: all hidden by default, all shown with MapObjectESportSponsorBanners on "
            + "(M805: drawn as their characters while it is on - see MapBannerRealDataTests)");
    }

    [Fact]
    public void Play_All_takes_the_Hall_of_Legends_godrays_only_when_its_event_is_on()
    {
        if (OpenMap("Map11", "base_srx") is not { } rift) return;
        using var _ = rift;
        var systems = VfxSystemResolver.ExtractAll(rift.Materials);
        var particles = MapParticleExtractor.Extract(rift.Materials, rift.Name);
        var godrays = particles.Single(p => p.Name == "2024_Hall_of_Legends_Ahri_Godrays1");
        Assert.Equal(0x76c50391u, godrays.VisibilityControllerHash);
        var vm = Vm(rift, MapGeoDecoder.Decode(rift.MapGeo, ExtendedChannelRule.From(rift.Materials, rift.Name)), particles);
        SetField(vm, "_vfxSystems", systems);

        bool godraysVisual = systems.TryGetValue(godrays.SystemHash, out var system) && system.Emitters.Any(e => e.IsVisual);
        vm.PlayAllParticles = true;
        bool PlayingGodrays() => vm.CurrentParticlePlayback!.Items.Any(i => i.System.PathHash == godrays.SystemHash && i.Transform == godrays.Transform);
        int atDefault = vm.CurrentParticlePlayback!.Items.Count;
        Assert.False(PlayingGodrays());

        vm.MapEvents.Single(e => e.Name == "SR_Hall_Of_Legends").IsOn = true;
        int withHall = vm.CurrentParticlePlayback!.Items.Count;
        if (godraysVisual)
        {
            Assert.True(PlayingGodrays());
            Assert.Equal(atDefault + 1, withHall);
        }
        else Assert.Equal(atDefault, withHall);
        vm.MapEvents.Single(e => e.Name == "SR_Hall_Of_Legends").IsOn = false;
        Assert.Equal(atDefault, vm.CurrentParticlePlayback!.Items.Count);
        output.WriteLine($"ran: Play All {atDefault} system(s) at the default, {withHall} with SR_Hall_Of_Legends on (the godrays system {(godraysVisual ? "has" : "has no")} visual emitters)");
    }

    [Theory]
    [InlineData("Map12", "bloom")]
    [InlineData("Map12", "base")]
    [InlineData("Map22", "anniversary")]
    [InlineData("Map30", "arenad")]
    [InlineData("Map453", "jade_container")]
    public void A_map_without_events_resolves_every_group_exactly_as_before(string wadName, string skin)
    {
        if (OpenMap(wadName, skin) is not { } rift) return;
        using var _ = rift;
        var map = MapGeoDecoder.Decode(rift.MapGeo, ExtendedChannelRule.From(rift.Materials, rift.Name));
        var resolver = new MapVisibilityResolver(rift.Controllers, rift.Definition);
        var none = new Dictionary<uint, int>();

        Assert.Empty(rift.Controllers.EventNames);
        int checkedGroups = 0;
        foreach (var g in map.Groups)
        {
            var mesh = map.Meshes.FirstOrDefault(m => m.Index == g.MeshIndex);
            int flags = mesh?.EffectiveVisibility ?? g.VisibilityFlags;
            uint controller = mesh?.EffectiveController ?? g.ControllerHash;
            foreach (int selected in rift.Definition.Primary is { } primary ? new[] { 0 }.Concat(primary.Layers.Select(l => l.Bit)).ToArray() : new[] { 0 })
            {
                var selections = new Dictionary<uint, int> { [MapVisibility.PrimaryAxisHash] = selected };
                Assert.Equal(resolver.IsVisible(flags, controller, selections), resolver.IsVisible(flags, controller, selections, null, new HashSet<string>()));
                checkedGroups++;
            }
        }

        var vm = Vm(rift, map);
        Assert.False(vm.HasMapEvents);
        Assert.Empty(vm.MapEvents);
        output.WriteLine($"ran: {wadName}/{skin}: no event; {map.Groups.Count} group(s) x the layer choices = {checkedGroups} decision(s), identical with and without an (empty) event set");
    }
}
