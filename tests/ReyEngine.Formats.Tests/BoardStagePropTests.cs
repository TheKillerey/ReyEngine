using System.Numerics;
using System.Reflection;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M798: the placed props (<c>MapAnimatedProp</c>) follow the TFT board stage (M797). A prop carries
/// <c>mVisibilityFlags</c> like a mesh does; with a stage on it shows only when those flags pass the SAME exact rule as
/// the meshes (<see cref="MapVisibility.VisibleForStage"/>), on both backends (they draw <c>CurrentPropMeshes</c>), for
/// the prop icons and for picking. "Start" and every map without stages leave the props as they were.
///
/// <para>The decode of a prop's mesh and textures needs a mounted game, so these tests put the decoded set where
/// <c>RefreshPropMeshesAsync</c> leaves it (<c>_propInstances</c> + <c>_propInstanceOwners</c>, one dummy mesh per
/// placement, keyed by its name) and drive everything downstream of it through the real view model. The real decode
/// and the drawing are the milestone's render check.</para>
/// </summary>
public sealed class BoardStagePropTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void SetField(MainWindowViewModel vm, string name, object? value) =>
        typeof(MainWindowViewModel).GetField(name, NonPublic)!.SetValue(vm, value);

    private static object? GetField(MainWindowViewModel vm, string name) =>
        typeof(MainWindowViewModel).GetField(name, NonPublic)!.GetValue(vm);

    private static T Call<T>(MainWindowViewModel vm, string name, params object?[] args) =>
        (T)typeof(MainWindowViewModel).GetMethod(name, NonPublic)!.Invoke(vm, args)!;

    private static void Call(MainWindowViewModel vm, string name, params object?[] args) =>
        typeof(MainWindowViewModel).GetMethod(name, NonPublic)!.Invoke(vm, args);

    // ---- a Map22-shaped board: level 1 on layer 64, level 7 on layer 8 ----------------------------------------------

    private static readonly MapVisibilityAxis Map22 = new(MapVisibility.PrimaryAxisHash, "Map Visibility", 67, true, new[]
    {
        new VisibilityLayer("Stage1", 4), new VisibilityLayer("Stage2", 8), new VisibilityLayer("Stage3", 16),
        new VisibilityLayer("Stage4", 32), new VisibilityLayer("base", 64),
    });

    private static readonly WadAssetEntry TestEntry = new()
    {
        Path = "data/maps/mapgeometry/map22/testboard.mapgeo", PathHash = 0x1234u, IsResolved = true,
    };

    private static MapGeoAsset ThreeGroupMap() => new()
    {
        Positions = new float[9], Normals = new float[9], Uvs = new float[6], Indices = new uint[] { 0, 1, 2 },
        Groups = new[]
        {
            new MapGeoGroup("Level1Ground", 0, 3, VisibilityFlags: 64),
            new MapGeoGroup("Level7Ground", 0, 3, VisibilityFlags: 8),
            new MapGeoGroup("Everywhere", 0, 3, VisibilityFlags: 255),
        },
    };

    /// <summary>Board ready (68 = 64 + 4) and Level 7 (8): the anniversary shape.</summary>
    private static MapBoardStageSet TwoStages() => new(new[]
    {
        new MapBoardStage("Board ready (level 1)", "MapBehavior_BoardReady", "ArenaSkin_Cue_BoardReady", null, 68,
            new Dictionary<string, bool>(), new[] { "MapBehavior_BoardReady" }, 0),
        new MapBoardStage("Level 7", "MapBehavior_LevelUp7Planning", "ArenaSkin_Cue_PlanningLevelUp7", 7, 8,
            new Dictionary<string, bool>(), new[] { "MapBehavior_BoardReady", "MapBehavior_LevelUp7Planning" }, 2),
    }, Array.Empty<MapLightingVolume>(), null);

    // ---- props ---------------------------------------------------------------------------------------------------

    /// <summary>A placement on the +X axis. <paramref name="flags"/> null = the map authors none (the reader's 255).</summary>
    private static MapAnimatedProp Prop(string name, int? flags, float x = 0f) =>
        new(name, new Vector3(x, 0f, 0f), Matrix4x4.CreateTranslation(x, 0f, 0f),
            "Characters/" + name + "/CharacterRecords/Root", "Characters/" + name + "/Skins/Skin0",
            VisibilityFlags: flags ?? 255, HasVisibilityFlags: flags is not null);

    /// <summary>One of each kind, 1000 apart: level 1's (64), level 7's (8), the level-1 backdrop (68), every layer (255)
    /// and none authored. Sorted by character name, which is the order the outliner - and so the instances - come in.</summary>
    private static MapAnimatedProp[] AllKinds() => new[]
    {
        Prop("Level1Statue", 64, 0f), Prop("Level7Ball", 8, 1000f), Prop("Backdrop", 68, 2000f),
        Prop("Everywhere", 255, 3000f), Prop("Unflagged", null, 4000f),
    };

    private static PropMesh Mesh(string key) =>
        new(key, new float[9], new float[9], new float[6], new uint[] { 0, 1, 2 }, new[] { new PropSubmesh(0, 3, null) });

    /// <summary>A view model holding a board, its placed props and the decoded instances for them.</summary>
    private sealed record Rig(MainWindowViewModel Vm, IReadOnlyList<AnimatedPropViewModel> Owners, List<PropInstanceData> Decoded);

    /// <summary>The board with <paramref name="props"/> placed and decoded the way <c>RefreshPropMeshesAsync</c> leaves
    /// them: one instance per editor-visible placement, its owner beside it. <paramref name="propsOn"/> false is the
    /// Props toggle off - nothing is drawn, and nothing may start a real decode either.</summary>
    private static Rig Board(MapBoardStageSet set, IReadOnlyList<MapAnimatedProp> props,
        MapVisibilityDefinition? definition = null, bool propsOn = true)
    {
        definition ??= new MapVisibilityDefinition(new[] { Map22 });
        var vm = new MainWindowViewModel();
        SetField(vm, "_currentMap", ThreeGroupMap());
        SetField(vm, "_currentMapEntry", TestEntry);
        SetField(vm, "_currentMapBytes", new byte[] { 1 });
        SetField(vm, "_mapVisibility", definition);
        SetField(vm, "_visibilityResolver", new MapVisibilityResolver(null, definition));
        vm.CurrentMesh = new MeshAsset
        {
            Positions = new float[9], Normals = new float[9], Uvs = new float[6], Indices = new uint[] { 0, 1, 2 },
            SubMeshes = new[] { new SubMeshInfo("m", 0, 3, 0) },
        };
        Call(vm, "RebuildVisibilityAxes", definition, null);
        vm.CurrentModelProps = props;   // Props are still off here: this places them in the outliner and decodes nothing
        Call(vm, "SetBoardStages", set);

        var owners = vm.MapContent.AllProps.Where(p => p.IsEditorVisible && !p.IsDisabled && !p.IsRemoved).ToList();
        var decoded = owners.Select(o => new PropInstanceData(Mesh(o.Prop.Name), o.CurrentTransform)).ToList();
        SetField(vm, "_propInstances", decoded);
        SetField(vm, "_propInstanceOwners", owners);
        if (propsOn)
        {
            // the backing field, not the property: the setter would start the real decode of these placements
            SetField(vm, "_showPropMeshes", true);
            Call(vm, "PublishAddedMeshPreview");
        }
        Call(vm, "UpdatePlaceableMarkers");
        Call(vm, "ApplyMapVisibility");
        return new Rig(vm, owners, decoded);
    }

    /// <summary>The placements the viewports are handed, by name, in draw order.</summary>
    private static List<string> Shown(MainWindowViewModel vm) =>
        vm.CurrentPropMeshes?.Instances.Select(i => i.Mesh.Key).ToList() ?? new List<string>();

    private static List<string> Names(IEnumerable<AnimatedPropViewModel> owners) => owners.Select(o => o.Prop.Name).ToList();

    // ---- Start and maps without stages: exactly as before ------------------------------------------------------------

    [Fact]
    public void Start_and_a_map_without_stages_publish_every_prop_and_ignore_the_layer_dropdown()
    {
        var props = AllKinds();
        foreach (var set in new[] { MapBoardStageSet.Empty, TwoStages() })
        {
            var rig = Board(set, props);
            var vm = rig.Vm;

            Assert.Equal(Names(rig.Owners), Shown(vm));                 // all five, in the outliner's order
            Assert.Equal(5, vm.PropMarkers!.Count);
            Assert.Equal(rig.Decoded, vm.CurrentPropMeshes!.Instances); // the very instances RefreshPropMeshesAsync built

            // props never followed Map Visibility's layer dropdown, and still do not
            vm.VisibilityAxes.Single().SelectedIndex = 2;               // Stage2 = 8
            Assert.Equal(Names(rig.Owners), Shown(vm));
            Assert.Equal(5, vm.PropMarkers!.Count);
        }
    }

    [Fact]
    public void A_map_without_stages_never_republishes_its_props()
    {
        var rig = Board(MapBoardStageSet.Empty, AllKinds());
        var vm = rig.Vm;
        var published = vm.CurrentPropMeshes;

        Call(vm, "ApplyMapVisibility");
        vm.VisibilityAxes.Single().SelectedIndex = 3;
        vm.VisibilityAxes.Single().SelectedIndex = 0;

        Assert.Same(published, vm.CurrentPropMeshes);                   // nothing here touched the published set
    }

    [Fact]
    public void Start_on_a_board_with_stages_does_not_republish_either()
    {
        var rig = Board(TwoStages(), AllKinds());
        var vm = rig.Vm;
        var published = vm.CurrentPropMeshes;

        Call(vm, "ApplyMapVisibility");                                 // every layer change, sun edit and tab switch does this

        Assert.Same(published, vm.CurrentPropMeshes);
    }

    // ---- a stage: the exact mask ---------------------------------------------------------------------------------------

    [Fact]
    public void A_stage_shows_only_the_props_that_share_a_bit_with_its_mask()
    {
        var rig = Board(TwoStages(), AllKinds());
        var vm = rig.Vm;
        var picker = vm.BoardStageSelectors.Single();

        picker.SelectedIndex = 2;                                       // Level 7 = 8
        Assert.Equal(new[] { "Everywhere", "Level7Ball", "Unflagged" }, Shown(vm));   // 64 and 68 share no bit with 8

        picker.SelectedIndex = 1;                                       // Board ready = 68 = 64 + 4
        Assert.Equal(new[] { "Backdrop", "Everywhere", "Level1Statue", "Unflagged" }, Shown(vm));   // 8 shares none of 68

        picker.SelectedIndex = 0;                                       // back to Start: all of them, as before
        Assert.Equal(Names(rig.Owners), Shown(vm));
        Assert.Equal(rig.Decoded, vm.CurrentPropMeshes!.Instances);
    }

    [Fact]
    public void Switching_stage_selects_among_the_decoded_instances_and_decodes_nothing_again()
    {
        var rig = Board(TwoStages(), AllKinds());
        var vm = rig.Vm;
        var picker = vm.BoardStageSelectors.Single();

        foreach (int index in new[] { 2, 1, 0, 2, 1 })
        {
            picker.SelectedIndex = index;
            Assert.Same(rig.Decoded, GetField(vm, "_propInstances"));   // the decoded set is never rebuilt
            Assert.Same(rig.Owners, GetField(vm, "_propInstanceOwners"));
            Assert.All(vm.CurrentPropMeshes!.Instances, shown => Assert.Contains(rig.Decoded, d => ReferenceEquals(d, shown)));
        }
    }

    [Fact]
    public void The_stage_gate_uses_the_flags_a_prop_has_now_not_the_ones_it_was_decoded_with()
    {
        // props (like sounds and particles) carry EditedVisibilityFlags; the live value decides. The state-changed handler
        // rebuilds the decode on a real edit, so the Props toggle stays off in this one and only the gate is asked.
        var rig = Board(TwoStages(), AllKinds(), propsOn: false);
        var vm = rig.Vm;
        vm.BoardStageSelectors.Single().SelectedIndex = 2;              // Level 7 = 8
        var levelOne = rig.Owners.Single(o => o.Prop.Name == "Level1Statue");   // authored 64
        var levelSeven = rig.Owners.Single(o => o.Prop.Name == "Level7Ball");   // authored 8

        Assert.False(Call<bool>(vm, "StageShowsProp", levelOne));
        Assert.True(Call<bool>(vm, "StageShowsProp", levelSeven));
        Assert.DoesNotContain(levelOne.Position, vm.PropMarkers!);
        Assert.Contains(levelSeven.Position, vm.PropMarkers!);

        levelOne.EditedVisibilityFlags = 8;                             // the user moves it onto level 7's layer
        levelSeven.EditedVisibilityFlags = 64;                          // and level 7's ball onto level 1's
        Assert.True(Call<bool>(vm, "StageShowsProp", levelOne));
        Assert.False(Call<bool>(vm, "StageShowsProp", levelSeven));
        Assert.Contains(levelOne.Position, vm.PropMarkers!);
        Assert.DoesNotContain(levelSeven.Position, vm.PropMarkers!);

        levelOne.EditedVisibilityFlags = null;                          // undone: the file's value again
        Assert.False(Call<bool>(vm, "StageShowsProp", levelOne));
    }

    [Fact]
    public void A_decode_that_finishes_under_a_stage_publishes_only_what_the_stage_shows()
    {
        // The real order of events on a map tab that returns, or an eye toggle, or an edited skin: RefreshPropMeshesAsync
        // assigns the instances and ends in PublishAddedMeshPreview - with the stage already on.
        var rig = Board(TwoStages(), AllKinds());
        var vm = rig.Vm;
        vm.BoardStageSelectors.Single().SelectedIndex = 2;
        Assert.Equal(3, Shown(vm).Count);

        var rebuilt = rig.Owners.Select(o => new PropInstanceData(Mesh(o.Prop.Name), o.CurrentTransform)).ToList();
        SetField(vm, "_propInstances", rebuilt);
        SetField(vm, "_propInstanceOwners", rig.Owners.ToList());
        Call(vm, "PublishAddedMeshPreview");

        Assert.Equal(new[] { "Everywhere", "Level7Ball", "Unflagged" }, Shown(vm));
        Assert.All(vm.CurrentPropMeshes!.Instances, shown => Assert.Contains(rebuilt, d => ReferenceEquals(d, shown)));
    }

    [Fact]
    public void Instances_and_owners_out_of_step_show_what_there_is_rather_than_guess()
    {
        var rig = Board(TwoStages(), AllKinds());
        var vm = rig.Vm;
        vm.BoardStageSelectors.Single().SelectedIndex = 2;

        SetField(vm, "_propInstanceOwners", rig.Owners.Take(2).ToList());   // a rebuild is between its two assignments
        Call(vm, "PublishAddedMeshPreview");

        Assert.Equal(Names(rig.Owners), Shown(vm));                     // unfiltered - the decode's own publish follows
    }

    [Fact]
    public void A_dragged_prop_stays_filtered_when_its_transform_is_republished()
    {
        var rig = Board(TwoStages(), AllKinds());
        var vm = rig.Vm;
        vm.BoardStageSelectors.Single().SelectedIndex = 2;

        rig.Owners.Single(o => o.Prop.Name == "Level7Ball").Offset = new Vector3(0f, 50f, 0f);
        Call(vm, "RefreshPropInstanceTransforms");                      // what a gizmo drag calls every frame

        Assert.Equal(new[] { "Everywhere", "Level7Ball", "Unflagged" }, Shown(vm));
        Assert.Equal(50f, vm.CurrentPropMeshes!.Instances.Single(i => i.Mesh.Key == "Level7Ball").Transform.Translation.Y);
    }

    [Fact]
    public void With_props_off_a_stage_publishes_no_props_and_is_filtered_when_they_are_turned_on()
    {
        var rig = Board(TwoStages(), AllKinds(), propsOn: false);
        var vm = rig.Vm;
        Assert.Null(vm.CurrentPropMeshes);

        vm.BoardStageSelectors.Single().SelectedIndex = 2;
        Assert.Null(vm.CurrentPropMeshes);                              // Props is off: nothing to draw
        Assert.Equal(3, vm.PropMarkers!.Count);                         // but the icons follow the stage

        SetField(vm, "_showPropMeshes", true);                          // Props turned on: the decode ends in this publish
        Call(vm, "PublishAddedMeshPreview");
        Assert.Equal(new[] { "Everywhere", "Level7Ball", "Unflagged" }, Shown(vm));
    }

    // ---- icons and picking share the gate ----------------------------------------------------------------------------

    [Fact]
    public void The_prop_icons_follow_the_stage()
    {
        var rig = Board(TwoStages(), AllKinds());
        var vm = rig.Vm;
        var picker = vm.BoardStageSelectors.Single();
        Vector3 At(string name) => rig.Owners.Single(o => o.Prop.Name == name).Position;

        Assert.Equal(5, vm.PropMarkers!.Count);

        picker.SelectedIndex = 2;
        Assert.Equal(new[] { At("Everywhere"), At("Level7Ball"), At("Unflagged") }, vm.PropMarkers!);

        picker.SelectedIndex = 1;
        Assert.Equal(new[] { At("Backdrop"), At("Everywhere"), At("Level1Statue"), At("Unflagged") }, vm.PropMarkers!);

        picker.SelectedIndex = 0;
        Assert.Equal(5, vm.PropMarkers!.Count);
    }

    /// <summary>Click the prop at <paramref name="x"/>: a ray straight down +Z through it (or the screen-space test, with
    /// the screen a flat XY view).</summary>
    private static object? Click(MainWindowViewModel vm, float x, bool screenSpace)
    {
        vm.SelectedOutlinerItem = null;
        if (screenSpace)
            vm.SelectAnyFromViewport(new Vector3(x, 0f, -500f), Vector3.UnitZ, additive: false,
                projectToScreen: v => new Vector2(v.X, v.Y), clickScreenPx: new Vector2(x, 0f));
        else
            vm.SelectAnyFromViewport(new Vector3(x, 0f, -500f), Vector3.UnitZ);
        return vm.SelectedOutlinerItem;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_click_cannot_pick_a_prop_the_stage_hides(bool screenSpace)
    {
        var rig = Board(TwoStages(), AllKinds());
        var vm = rig.Vm;
        var picker = vm.BoardStageSelectors.Single();
        float X(string name) => rig.Owners.Single(o => o.Prop.Name == name).Position.X;

        // Start: both are clickable
        Assert.Equal("Level1Statue", Assert.IsType<AnimatedPropViewModel>(Click(vm, X("Level1Statue"), screenSpace)).Prop.Name);
        Assert.Equal("Level7Ball", Assert.IsType<AnimatedPropViewModel>(Click(vm, X("Level7Ball"), screenSpace)).Prop.Name);

        picker.SelectedIndex = 2;                                       // Level 7: level 1's statue is gone, so is its click
        Assert.IsNotType<AnimatedPropViewModel>(Click(vm, X("Level1Statue"), screenSpace));
        Assert.Equal("Level7Ball", Assert.IsType<AnimatedPropViewModel>(Click(vm, X("Level7Ball"), screenSpace)).Prop.Name);

        picker.SelectedIndex = 1;                                       // Board ready: the other way round
        Assert.Equal("Level1Statue", Assert.IsType<AnimatedPropViewModel>(Click(vm, X("Level1Statue"), screenSpace)).Prop.Name);
        Assert.IsNotType<AnimatedPropViewModel>(Click(vm, X("Level7Ball"), screenSpace));
    }

    // ---- a map tab's picker: the props come back with it ---------------------------------------------------------------

    [Fact]
    public void A_tab_that_returns_republishes_the_props_for_the_stage_it_left()
    {
        var rig = Board(TwoStages(), AllKinds());
        var vm = rig.Vm;
        var picker = vm.BoardStageSelectors.Single();
        picker.SelectedIndex = 2;
        var atCapture = Shown(vm);
        var scene = typeof(MainWindowViewModel).GetMethod("CaptureMapScene", NonPublic)!.Invoke(vm, null)!;

        // the skinned-mesh tab does not clear the viewport, so the map's picker is live there and can move
        picker.SelectedIndex = 1;
        Assert.NotEqual(atCapture, Shown(vm));

        typeof(MainWindowViewModel).GetMethod("RestoreMapScene", NonPublic)!.Invoke(vm, new[] { scene });

        Assert.Equal(2, vm.BoardStageSelectors.Single().SelectedIndex);
        Assert.Equal(atCapture, Shown(vm));                             // the tab's stage, and its props
        Assert.Equal(3, vm.PropMarkers!.Count);
    }

    // ---- what the panel says ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_log_says_how_many_props_the_stage_shows_and_that_their_animations_are_not_played()
    {
        var rig = Board(TwoStages(), AllKinds());
        var vm = rig.Vm;
        var lines = new List<LogEntry>();
        ((Logger)GetField(vm, "_log")!).Logged += lines.Add;
        var picker = vm.BoardStageSelectors.Single();

        picker.SelectedIndex = 2;
        var level7 = Assert.Single(lines, e => e.Category == "Props");
        Assert.Contains("Board stage 'Level 7': 3 of 5 prop placement(s) shown", level7.Message);
        Assert.Contains("MapActionPlayAnimation", level7.Message);
        Assert.Contains("not played", level7.Message);

        lines.Clear();
        picker.SelectedIndex = 1;
        Assert.Contains("4 of 5 prop placement(s) shown", Assert.Single(lines, e => e.Category == "Props").Message);

        lines.Clear();
        picker.SelectedIndex = 0;
        Assert.DoesNotContain(lines, e => e.Category == "Props");       // Start: the map as it loads; nothing to explain
    }

    [Fact]
    public void A_board_without_props_says_nothing_about_them()
    {
        var rig = Board(TwoStages(), Array.Empty<MapAnimatedProp>());
        var lines = new List<LogEntry>();
        ((Logger)GetField(rig.Vm, "_log")!).Logged += lines.Add;

        rig.Vm.BoardStageSelectors.Single().SelectedIndex = 2;

        Assert.DoesNotContain(lines, e => e.Category == "Props");
        Assert.Null(rig.Vm.CurrentPropMeshes);
    }

    [Fact]
    public void The_stage_tooltip_names_the_props_and_what_is_not_played()
    {
        string? root = TestRunIsolation.RepoRoot();
        if (root is null) return;
        string path = Path.Combine(root, "src", "ReyEngine.App", "Views", "MapOutlinerView.axaml");
        if (!File.Exists(path)) return;
        string markup = File.ReadAllText(path);

        Assert.Contains("the placed props follow it too", markup);
        Assert.Contains("the props' stage animations (they keep their idle)", markup);
        // M797's own pins survive the rewording
        Assert.Contains("Not emulated: the lighting-volume and fog fades", markup);
        Assert.Contains("stencil masks", markup);
    }

    // ---- the shipped boards ----------------------------------------------------------------------------------------------

    private static (WadArchive Wad, HashDatabase Db)? Open(string wad)
    {
        string path = Path.Combine(Final, "Maps", "Shipping", wad + ".wad.client");
        if (!File.Exists(path)) return null;
        var db = new HashSyncService().LoadLocal(_ => { });
        return (WadArchive.Open(path, new WadPathResolver(db)), db);
    }

    /// <summary>A shipped Map22 board: its props, stages and visibility axis, read from the WAD.</summary>
    private (IReadOnlyList<MapAnimatedProp> Props, MapBoardStageSet Set, MapVisibilityDefinition Definition)? Shipped(string skin)
    {
        if (Open("Map22") is not { } opened) { output.WriteLine("SKIPPED: Map22.wad.client is not installed"); return null; }
        using var wad = opened.Wad;
        ulong binHash = HashAlgorithms.WadPath($"data/maps/mapgeometry/map22/{skin}.materials.bin");
        ulong map22Hash = HashAlgorithms.WadPath("data/maps/shipping/map22/map22.bin");
        if (!wad.TryGetEntry(binHash, out _) || !wad.TryGetEntry(map22Hash, out _))
        { output.WriteLine($"SKIPPED: {skin} bin missing"); return null; }
        byte[] mats = wad.Extract(binHash);
        string? Name(uint h) => opened.Db.TryGetBinName(h, out var n) ? n : null;
        return (MapPlaceableExtractor.Extract(mats).Props, MapBoardStages.Load(mats), MapVisibility.Parse(wad.Extract(map22Hash), Name));
    }

    [Fact]
    public void On_the_real_anniversary_board_level_7s_props_show_at_level_7_and_level_1s_do_not()
    {
        if (Shipped("anniversary") is not { } board) return;
        var rig = Board(board.Set, board.Props, board.Definition);
        var vm = rig.Vm;
        var picker = vm.BoardStageSelectors.Single();
        Assert.Equal(new[] { "Start", "Board ready (level 1)", "Level 7" }, picker.Options);

        // Start: every placement, as before (Map22's additive starting mask is not applied to props)
        var start = Shown(vm);
        Assert.Equal(Names(rig.Owners), start);
        Assert.Contains("LEVEL7_BALL", start);
        Assert.Contains("IntroPropBG", start);

        picker.SelectedIndex = 2;                                       // Level 7 = mask 8
        var level7 = Shown(vm);
        Assert.Contains("LEVEL7_BALL", level7);                         // flags 8
        Assert.DoesNotContain("IntroPropBG", level7);                   // flags 68 = 64 + 4: level 1's backdrop
        Assert.Contains("IntroProp", level7);                           // flags 127: every layer
        Assert.Contains("WinProp", level7);                             // flags 127
        Assert.Contains("Generic_Interaction5", level7);                // flags 8
        Assert.DoesNotContain("Generic_Interaction3", level7);          // flags 4
        Assert.DoesNotContain("Generic_Interaction4", level7);          // flags 4
        Assert.Contains("Generic_Interaction1", level7);                // none authored
        Assert.Contains("TFT_GoldMine_Home1", level7);                  // none authored

        picker.SelectedIndex = 1;                                       // Board ready = mask 68
        var ready = Shown(vm);
        Assert.DoesNotContain("LEVEL7_BALL", ready);                    // level 7's ball only exists at level 7
        Assert.Contains("IntroPropBG", ready);
        Assert.Contains("IntroProp", ready);                            // 127 covers both stages
        Assert.Contains("Generic_Interaction3", ready);
        Assert.Contains("Generic_Interaction4", ready);
        Assert.DoesNotContain("Generic_Interaction5", ready);
        Assert.Contains("TFT_GoldMine_Away3", ready);

        // and every placement is judged by the meshes' rule: no more, no fewer
        foreach (var (mask, shown) in new[] { (8, level7), (68, ready) })
        {
            var expected = rig.Owners.Where(o => MapVisibility.VisibleForStage(o.EffectiveVisibilityFlags, mask)).Select(o => o.Prop.Name);
            Assert.Equal(expected, shown);
        }

        picker.SelectedIndex = 0;
        Assert.Equal(start, Shown(vm));                                 // Start again: exactly what it was
        output.WriteLine($"ran: anniversary {rig.Owners.Count} placement(s): Start {start.Count}, Board ready {ready.Count}, "
            + $"Level 7 {level7.Count}; hidden at Level 7: {string.Join(", ", start.Except(level7))}");
    }

    [Fact]
    public void On_the_real_dawnbringer_board_the_level_7_statues_replace_the_level_1_ones()
    {
        if (Shipped("dawnbringernightbringer") is not { } board) return;
        var rig = Board(board.Set, board.Props, board.Definition);
        var vm = rig.Vm;
        var picker = vm.BoardStageSelectors.Single();
        Assert.Equal(new[] { "Start", "Board ready (level 1)", "Level 7" }, picker.Options);

        var start = Shown(vm);
        Assert.Contains("TFT_DawnbringerNightbringer_Props_Riven", start);        // Start shows both generations, as it always did
        Assert.Contains("TFT_DawnbringerNightbringer_Props_L7_Riven", start);

        picker.SelectedIndex = 2;                                       // Level 7 = mask 20
        var level7 = Shown(vm);
        Assert.Contains("TFT_DawnbringerNightbringer_Props_L7_Riven", level7);    // flags 4
        Assert.Contains("TFT_DawnbringerNightbringer_Props_L7_Yasuo", level7);    // flags 4
        Assert.Contains("Generic_Interaction4", level7);                          // flags 4
        Assert.DoesNotContain("TFT_DawnbringerNightbringer_Props_Riven", level7); // flags 64: level 1's
        Assert.DoesNotContain("TFT_DawnbringerNightbringer_Props_Yasuo", level7);
        Assert.Contains("TFT_GoldMine_Home1", level7);                            // none authored

        picker.SelectedIndex = 1;                                       // Board ready = mask 64
        var ready = Shown(vm);
        Assert.Contains("TFT_DawnbringerNightbringer_Props_Riven", ready);
        Assert.Contains("TFT_DawnbringerNightbringer_Props_Yasuo", ready);
        Assert.DoesNotContain("TFT_DawnbringerNightbringer_Props_L7_Riven", ready);
        Assert.DoesNotContain("Generic_Interaction4", ready);

        picker.SelectedIndex = 0;
        Assert.Equal(start, Shown(vm));
        output.WriteLine($"ran: dawnbringernightbringer {rig.Owners.Count} placement(s): Start {start.Count}, Board ready {ready.Count}, "
            + $"Level 7 {level7.Count}; hidden at Level 7: {string.Join(", ", start.Except(level7))}");
    }

    [Fact]
    public void Every_shipped_summoners_rift_and_arena_prop_is_published_whole()
    {
        // The non-TFT guard on real data: no stage exists, so no prop may ever be filtered - whatever flags it carries and
        // whatever the layer dropdown says. Every prop of every materials bin of the four non-TFT maps, on one board.
        var all = new List<MapAnimatedProp>();
        int bins = 0, flagged = 0;
        foreach (string wadName in new[] { "Map11", "Map12", "Map30", "Map453" })
        {
            if (Open(wadName) is not { } opened) { output.WriteLine($"SKIPPED: {wadName}.wad.client is not installed"); continue; }
            using var wad = opened.Wad;
            foreach (var entry in wad.Entries.Where(e => e.IsResolved
                         && e.Path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase)))
            {
                byte[] bytes = wad.Extract(entry);
                var props = MapPlaceableExtractor.Extract(bytes).Props;
                if (props.Count == 0) continue;
                Assert.Same(MapBoardStageSet.Empty, MapBoardStages.Load(bytes));   // none of these can carry a stage
                bins++;
                all.AddRange(props);
                flagged += props.Count(p => p.HasVisibilityFlags);
            }
        }
        if (bins == 0) { output.WriteLine("SKIPPED: none of Map11 / Map12 / Map30 / Map453 is installed, or none places a prop"); return; }

        var rig = Board(MapBoardStageSet.Empty, all);
        Assert.False(rig.Vm.HasBoardStages);
        Assert.NotEmpty(rig.Owners);
        Assert.Equal(Names(rig.Owners), Shown(rig.Vm));
        rig.Vm.VisibilityAxes.Single().SelectedIndex = 2;               // a layer picked in Map Visibility
        Assert.Equal(Names(rig.Owners), Shown(rig.Vm));
        output.WriteLine($"ran: {bins} non-TFT materials bin(s) with props, {all.Count} placement(s) "
            + $"({flagged} with authored flags), {rig.Owners.Count} editor-visible - all published whole");
    }
}
