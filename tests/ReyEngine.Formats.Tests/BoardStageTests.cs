using System.Numerics;
using System.Reflection;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Vfx;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M797: the TFT "Board stage" selector. A Map22 board is shown in a game state exactly as its own
/// <c>MapBehavior</c>s define it: the visibility mask the behaviour leaves the board on (which REPLACES the map's
/// starting mask - the additive rule drew dawnbringernightbringer's level-1 cloud vortex round its level-7 board),
/// the particle placements it switches on and off by name, and the lighting volume that mask turns on.
/// </summary>
public sealed class BoardStageSyntheticTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    // ---- a materials bin shaped like Riot's: MapBehavior items in a MapPlaceableContainer ------------------------

    private static BinTreeStruct SetVisibility(int mask, float? start = null, float? fade = null, bool omitMask = false)
    {
        var p = new List<BinTreeProperty>();
        if (start is { } s) p.Add(new BinTreeF32(H("startTime"), s));
        if (!omitMask) p.Add(new BinTreeU8(H("VisibilityFlags"), (byte)mask));
        if (fade is { } f) p.Add(new BinTreeF32(H("TransitionTime"), f));
        return new BinTreeStruct(0, H("MapActionSetVisibilityFlag"), p);
    }

    private static BinTreeStruct Toggle(float? start, bool? shown, params string[] names)
    {
        var p = new List<BinTreeProperty>();
        if (start is { } s) p.Add(new BinTreeF32(H("startTime"), s));
        p.Add(new BinTreeContainer(H("MapParticleName"), BinPropertyType.String,
            names.Select(n => (BinTreeProperty)new BinTreeString(0, n)).ToArray()));
        if (shown is { } v) p.Add(new BinTreeBool(H("shown"), v));
        return new BinTreeStruct(0, H("MapActionToggleMapParticle"), p);
    }

    private static BinTreeStruct Action(string cls, float? start = null) =>
        new(0, H(cls), start is { } s ? new BinTreeProperty[] { new BinTreeF32(H("startTime"), s) } : Array.Empty<BinTreeProperty>());

    private static BinTreeStruct Behavior(string name, string cue, params BinTreeStruct[] actions)
    {
        var p = new List<BinTreeProperty>
        {
            new BinTreeString(H("name"), name),
            new BinTreeString(H("Cue"), cue),
        };
        if (actions.Length > 0)
            p.Add(new BinTreeContainer(H("Actions"), BinPropertyType.Struct, actions.Cast<BinTreeProperty>().ToArray()));
        return new BinTreeStruct(0, H("MapBehavior"), p);
    }

    private static byte[] Bin(params BinTreeStruct[] items)
    {
        uint key = 0x100;
        var pairs = items.Select(i => new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, key++), i)).ToArray();
        var container = new BinTreeObject(0x5000u, H("MapPlaceableContainer"), new BinTreeProperty[]
        {
            new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, pairs),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { container }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    // ---- reading -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_behaviour_is_read_with_its_name_cue_and_actions_in_file_order()
    {
        var bin = Bin(Behavior("MapBehavior_LevelUp7Planning", "ArenaSkin_Cue_PlanningLevelUp7",
            Action("MapActionPlayAnimation"),
            SetVisibility(64),
            SetVisibility(8, start: 3.75f),
            Toggle(0.5f, null, "Transition1"),
            Toggle(5f, false, "Transition1"),
            Toggle(1f, false, "SkyLevel1", "IdleLevel1"),
            Toggle(4f, null, "SkyLevel7")));

        var b = Assert.Single(MapBehaviors.Extract(bin));

        Assert.Equal("MapBehavior_LevelUp7Planning", b.Name);
        Assert.Equal("ArenaSkin_Cue_PlanningLevelUp7", b.Cue);
        Assert.Equal(7, b.Actions.Count);
        Assert.IsType<MapOtherAction>(b.Actions[0]);
        Assert.Equal(64, Assert.IsType<MapSetVisibilityAction>(b.Actions[1]).Mask);
        Assert.Equal(3.75f, b.Actions[2].StartTime);
        Assert.Equal(new[] { "SkyLevel1", "IdleLevel1" }, Assert.IsType<MapToggleParticleAction>(b.Actions[5]).ParticleNames);
    }

    [Fact]
    public void The_end_state_is_the_last_action_by_start_time_with_ties_in_list_order()
    {
        // anniversary's LevelUp7Planning: 64 at 0, then 8 at 3.75 - the LATER start time wins, not the later list entry
        var later = Assert.Single(MapBehaviors.Extract(Bin(Behavior("b", "c",
            SetVisibility(8, start: 3.75f), SetVisibility(64)))));
        Assert.Equal(8, later.EndVisibilityMask);

        // equal start times: the later list entry
        var tied = Assert.Single(MapBehaviors.Extract(Bin(Behavior("b", "c",
            SetVisibility(68), SetVisibility(76), SetVisibility(20, start: 0f)))));
        Assert.Equal(20, tied.EndVisibilityMask);

        // per particle name, the same rule: shown at 0.5, hidden at 5, so it ends hidden whatever the list order
        var toggles = Assert.Single(MapBehaviors.Extract(Bin(Behavior("b", "c",
            Toggle(5f, false, "A"), Toggle(0.5f, null, "A", "B"), Toggle(4f, null, "C"))))).EndParticleToggles;
        Assert.False(toggles["A"]);
        Assert.True(toggles["B"]);
        Assert.True(toggles["C"]);
        Assert.False(toggles.ContainsKey("D"));
        Assert.True(toggles.ContainsKey("a"));   // names compare case-insensitively
    }

    [Fact]
    public void An_absent_start_time_is_zero_and_an_absent_shown_is_true()
    {
        var b = Assert.Single(MapBehaviors.Extract(Bin(Behavior("b", "c", SetVisibility(4), Toggle(null, null, "P")))));

        Assert.All(b.Actions, a => Assert.Equal(0f, a.StartTime));
        Assert.True(Assert.IsType<MapToggleParticleAction>(b.Actions[1]).Shown);
    }

    [Fact]
    public void Actions_the_viewport_does_not_apply_are_counted_and_never_dropped()
    {
        var b = Assert.Single(MapBehaviors.Extract(Bin(Behavior("b", "c",
            Action("MapActionPlayAnimation"), Action("MapActionPlaySoundAtLocation", 1f),
            new BinTreeStruct(0, 0xa0b62126u, Array.Empty<BinTreeProperty>()),   // the unnamed lighting-volume fade
            SetVisibility(20)))));

        Assert.Equal(4, b.Actions.Count);
        Assert.Equal(3, b.UnappliedActionCount);
        Assert.Equal(0xa0b62126u, ((MapOtherAction)b.Actions[2]).ClassHash);
        Assert.Equal(20, b.EndVisibilityMask);
    }

    [Fact]
    public void A_visibility_action_without_its_mask_is_left_unapplied_rather_than_guessed()
    {
        // carousel_set10's GameStart is one: Riot omitted VisibilityFlags, and the default is not known to be 0
        var b = Assert.Single(MapBehaviors.Extract(Bin(Behavior("GameStart", "c", SetVisibility(0, omitMask: true)))));

        Assert.False(b.SetsVisibility);
        Assert.Null(b.EndVisibilityMask);
        Assert.Equal(1, b.UnappliedActionCount);
    }

    [Fact]
    public void A_bin_that_never_names_the_class_is_rejected_by_the_byte_scan_before_any_parse()
    {
        Assert.Empty(MapBehaviors.Extract(null));
        Assert.Empty(MapBehaviors.Extract(Array.Empty<byte>()));
        Assert.Empty(MapBehaviors.Extract(new byte[64]));   // not a bin at all, and no class hash: never parsed
        Assert.Empty(MapBehaviors.Extract(Bin()));           // a real bin with no behaviour in it

        // the class hash present but the bytes junk: the parse fails and the answer is still "none", never a throw
        var junk = new byte[64];
        BitConverter.GetBytes(H("MapBehavior")).CopyTo(junk, 20);
        Assert.Empty(MapBehaviors.Extract(junk));
    }

    // ---- stages ---------------------------------------------------------------------------------------------------------

    private static MapBehavior B(string name, string cue, params MapBehaviorAction[] actions) => new(name, cue, null, actions);
    private static MapSetVisibilityAction Set(int mask, float at = 0f) => new(at, mask, null);
    private static MapToggleParticleAction Tog(bool shown, params string[] names) => new(0f, names, shown);

    [Fact]
    public void Board_ready_comes_first_then_the_levels_by_number()
    {
        var stages = MapBoardStages.Build(new[]
        {
            B("MapBehavior_PlanningLevelUp7", "ArenaSkin_Cue_PlanningLevelUp7", Set(12)),
            B("MapBehavior_PlanningLevelUp3", "ArenaSkin_Cue_PlanningLevelUp3", Set(74)),
            B("MapBehavior_BoardReady", "ArenaSkin_Cue_BoardReady", Set(70)),
            B("MapBehavior_PlanningLevelUp5", "ArenaSkin_Cue_PlanningLevelUp5", Set(82)),
        });

        Assert.Equal(new[] { "Board ready (level 1)", "Level 3", "Level 5", "Level 7" }, stages.Select(s => s.Label));
        Assert.Equal(new[] { 70, 74, 82, 12 }, stages.Select(s => s.Mask));
        Assert.Equal(new int?[] { null, 3, 5, 7 }, stages.Select(s => s.Level));
    }

    [Fact]
    public void Debug_behaviours_and_events_are_not_stages()
    {
        var stages = MapBoardStages.Build(new[]
        {
            B("MapBehavior_Transition1_Debug", "LevelUp7Debug", Set(8)),
            B("MapBehavior_Transition1_DebugBack", "Transition1_DebugBack", Set(68)),
            B("MapBehavior_WinStreak", "ArenaSkin_Cue_WinStreak3", Set(32)),          // an event over whatever stage is on
            B("MapBehavior_Set8CatDeactivate", "Set8CatDeactivate", Set(67)),
            B("MapBehavior_LevelUp7", "ArenaSkin_Cue_LevelUp7"),                       // a bare cue: no action, no mask
            B("MapBehavior_LevelUp7Planning", "ArenaSkin_Cue_PlanningLevelUp7", Set(20)),
        });

        var only = Assert.Single(stages);
        Assert.Equal("Level 7", only.Label);
        Assert.Equal(20, only.Mask);
    }

    [Fact]
    public void Of_two_behaviours_for_one_level_the_planning_one_wins()
    {
        // choncc: LevelUp7 sets 68 and LevelUp7Planning ends on 52; set10_kda: a ReconnectLevelUp6 beside PlanningLevelUp6
        var stages = MapBoardStages.Build(new[]
        {
            B("MapBehavior_LevelUp7", "ArenaSkin_Cue_LevelUp7", Set(68)),
            B("MapBehavior_LevelUp7Planning", "ArenaSkin_Cue_PlanningLevelUp7", Set(68, 0.1f), Set(100, 0.8f), Set(52, 2.8f)),
            B("MapBehavior_ReconnectLevelUp6", "ReconnectLevelUp6", Set(8), Tog(false, "x"), Tog(false, "y")),
            B("MapBehavior_PlanningLevelUp6", "ArenaSkin_Cue_PlanningLevelUp6", Set(8)),
        });

        Assert.Equal(new[] { "Level 6", "Level 7" }, stages.Select(s => s.Label));
        Assert.Equal("MapBehavior_PlanningLevelUp6", stages[0].BehaviorName);
        Assert.Equal("MapBehavior_LevelUp7Planning", stages[1].BehaviorName);
        Assert.Equal(52, stages[1].Mask);
    }

    [Fact]
    public void A_stage_runs_the_chain_up_to_it_so_the_mask_is_replaced_and_the_switches_accumulate()
    {
        var stages = MapBoardStages.Build(new[]
        {
            B("MapBehavior_BoardReady", "ArenaSkin_Cue_BoardReady", Set(68), Tog(true, "Level1Sky", "Ghost")),
            B("MapBehavior_LevelUp7Planning", "ArenaSkin_Cue_PlanningLevelUp7", Set(64, 0f), Set(8, 3.75f),
                Tog(false, "Level1Sky"), Tog(true, "Level7Sky"),
                new MapOtherAction(1f, 0xb3a20911u)),
        });

        var ready = stages[0];
        var level7 = stages[1];

        Assert.Equal(68, ready.Mask);
        Assert.Equal(new[] { "MapBehavior_BoardReady" }, ready.Chain);
        Assert.Equal(8, level7.Mask);                                             // replaced, not OR-ed onto 68
        Assert.Equal(new[] { "MapBehavior_BoardReady", "MapBehavior_LevelUp7Planning" }, level7.Chain);
        Assert.False(level7.ParticleToggles["Level1Sky"]);                        // the later behaviour overrides
        Assert.True(level7.ParticleToggles["Ghost"]);                             // board-ready's switch stays
        Assert.True(level7.ParticleToggles["Level7Sky"]);
        Assert.Equal(1, level7.UnappliedActionCount);
        Assert.True(ready.ParticleToggles["Level1Sky"]);                          // and level 7 did not leak backwards
    }

    [Fact]
    public void A_bin_with_no_stage_behaviour_has_no_stages()
    {
        // events and debug behaviours only, which is most of the shipped TFT boards' behaviours
        var events = Bin(
            Behavior("MapBehavior_Victory", "ArenaSkin_Cue_Victory", Action("MapActionPlaySoundAtLocation")),
            Behavior("MapBehavior_Transition1_Debug", "Transition1_Debug", SetVisibility(20)));

        Assert.Same(MapBoardStageSet.Empty, MapBoardStages.Load(events));
        Assert.False(MapBoardStages.Load(events).HasStages);
        Assert.Same(MapBoardStageSet.Empty, MapBoardStages.Load(Bin()));    // no behaviour at all: Summoner's Rift's shape
        Assert.Same(MapBoardStageSet.Empty, MapBoardStages.Load(null));
        Assert.Empty(MapBoardStages.Build(Array.Empty<MapBehavior>()));
    }

    // ---- the mask rule ----------------------------------------------------------------------------------------------------

    private static readonly MapVisibilityAxis Map22 = new(MapVisibility.PrimaryAxisHash, "Map Visibility", 67, true, new[]
    {
        new VisibilityLayer("Stage1", 4), new VisibilityLayer("Stage2", 8), new VisibilityLayer("Stage3", 16),
        new VisibilityLayer("Stage4", 32), new VisibilityLayer("base", 64),
    });

    [Fact]
    public void A_stage_mask_is_exact_where_the_additive_rule_kept_level_one_on_beside_level_seven()
    {
        // dawnbringernightbringer, level 7 = 20 (Stage1 + Stage3), WITHOUT the base bit 64
        Assert.True(MapVisibility.VisibleForMask(64, Map22, 16));    // the additive rule: base content stays -> the white vortex
        Assert.False(MapVisibility.VisibleForStage(64, 20));         // the stage rule: it does not
        Assert.False(MapVisibility.VisibleForStage(67, 20));         // 64 + 2 + 1: level-1 content
        Assert.True(MapVisibility.VisibleForStage(4, 20));
        Assert.True(MapVisibility.VisibleForStage(16, 20));
        Assert.True(MapVisibility.VisibleForStage(7, 20));           // 1 + 2 + 4 shares Stage1
        Assert.False(MapVisibility.VisibleForStage(8, 20));          // Stage2: another level
    }

    [Fact]
    public void All_layers_content_stays_on_under_every_stage_as_it_does_everywhere_else()
    {
        foreach (int mask in new[] { 4, 8, 20, 64, 68 })
        {
            Assert.True(MapVisibility.VisibleForStage(255, mask));
            Assert.True(MapVisibility.VisibleForStage(0, mask));
        }
    }

    [Fact]
    public void The_resolver_takes_a_stage_mask_for_the_primary_axis_and_leaves_every_other_axis_alone()
    {
        var baron = new MapVisibilityAxis(MapVisibility.BaronPitAxisHash, "Baron Pit", 1, false,
            new[] { new VisibilityLayer("Base", 1), new VisibilityLayer("Cup", 2) });
        var definition = new MapVisibilityDefinition(new[] { Map22, baron });
        var resolver = new MapVisibilityResolver(null, definition);
        var noSelection = new Dictionary<uint, int>();

        Assert.False(resolver.IsVisible(64, 0, noSelection, stageMask: 20));
        Assert.True(resolver.IsVisible(4, 0, noSelection, stageMask: 20));
        Assert.True(resolver.IsVisible(255, 0, noSelection, stageMask: 20));
        // a stage stands in for the primary axis's own selection, which is not consulted
        Assert.False(resolver.IsVisible(64, 0, new Dictionary<uint, int> { [MapVisibility.PrimaryAxisHash] = 64 }, stageMask: 20));
        // the other axes still apply: a Baron Pit selection does not turn a stage off
        Assert.True(resolver.IsVisible(4, 0, new Dictionary<uint, int> { [MapVisibility.BaronPitAxisHash] = 2 }, stageMask: 20));

        var why = resolver.Resolve(64, 0, noSelection, stageMask: 20);
        Assert.False(why.Visible);
        Assert.Contains("board stage", why.FilterSummary);
        Assert.Contains("20", why.Reason);
    }

    [Fact]
    public void A_visibility_controller_is_judged_by_the_stage_mask_alone()
    {
        // The controller graph is the game's own; a stage must not fold Map22's initial 67 into it.
        byte[] controllers = Write(new BinTree(new[]
        {
            new BinTreeObject(0x100, 0xc406a533, new BinTreeProperty[] { new BinTreeU8(0x27639032, 64) }),   // a primary-axis leaf: "on in base"
        }, Array.Empty<string>()));
        var definition = new MapVisibilityDefinition(new[] { Map22 });
        var resolver = new MapVisibilityResolver(MapVisibilityControllers.Build(new[] { controllers }, definition), definition);

        // Additive: the initial 67 contains base, so a controller on base shows whatever layer is picked
        Assert.True(resolver.IsVisible(255, 0x100, new Dictionary<uint, int> { [MapVisibility.PrimaryAxisHash] = 16 }));
        // Stage: level 7 = 20 has no base bit, so the same controller is off; board-ready (64) turns it on
        Assert.False(resolver.IsVisible(255, 0x100, new Dictionary<uint, int>(), stageMask: 20));
        Assert.True(resolver.IsVisible(255, 0x100, new Dictionary<uint, int>(), stageMask: 64));
    }

    [Fact]
    public void Without_a_stage_the_resolver_answers_exactly_as_before()
    {
        var resolver = new MapVisibilityResolver(null, new MapVisibilityDefinition(new[] { Map22 }));
        foreach (int bit in new[] { 0, 4, 8, 16, 32, 64 })
            for (int flags = 0; flags < 256; flags++)
            {
                var selection = new Dictionary<uint, int> { [MapVisibility.PrimaryAxisHash] = bit };
                Assert.Equal(MapVisibility.VisibleForMask(flags, Map22, bit), resolver.IsVisible(flags, 0, selection));
                Assert.Equal(resolver.IsVisible(flags, 0, selection), resolver.IsVisible(flags, 0, selection, stageMask: null));
            }
    }

    // ---- lighting -----------------------------------------------------------------------------------------------------------

    private static readonly Matrix4x4 Board = new(2300f, 0, 0, 0, 0, 3500f, 0, 0, 0, 0, 2500f, 0, 2000f, 0, 2000f, 1);

    private static MapLightingVolume Volume(string name, int flags, float scale, Matrix4x4? at = null) =>
        new(name, at ?? Board, new MapSunProperties { LightMapColorScale = scale }, flags);

    [Fact]
    public void The_volume_a_stage_turns_on_is_M785s_rule_asked_with_the_stage_mask()
    {
        var volumes = new[] { Volume("LightingVolume1", 64, 2f), Volume("LightingVolume2", 8, 1.5f) };

        Assert.Equal("LightingVolume1", MapLighting.ActiveForMask(volumes, 68)!.Name);   // anniversary board-ready
        Assert.Equal("LightingVolume2", MapLighting.ActiveForMask(volumes, 8)!.Name);    // anniversary level 7
        Assert.Null(MapLighting.ActiveForMask(volumes, 0));
        Assert.Null(MapLighting.ActiveForMask(volumes, 4));                              // neither volume is on
        Assert.Null(MapLighting.ActiveForMask(volumes, 72));                             // both are: which wins is not known

        // ActiveAtStart is the same rule, unchanged
        Assert.Equal("LightingVolume2", MapLighting.ActiveAtStart(volumes, () => 8)!.Name);
        Assert.Null(MapLighting.ActiveAtStart(volumes, () => 0));
    }

    [Fact]
    public void The_sun_for_a_mask_keeps_the_M207_and_M785_fallbacks()
    {
        var global = new MapSunProperties { LightMapColorScale = 1f };
        var pair = new[] { Volume("LightingVolume1", 64, 2f), Volume("LightingVolume2", 8, 1.5f) };
        var apart = new[] { Volume("LightingVolume1", 64, 2f), Volume("LightingVolume2", 8, 1.5f, Board with { M41 = 9000f }) };

        Assert.Equal(1.5f, MapLighting.SunForMask(pair, global, 8)!.LightMapColorScale);
        Assert.Equal(2f, MapLighting.SunForMask(pair, global, 68)!.LightMapColorScale);
        Assert.Same(global, MapLighting.SunForMask(pair, global, 4));                    // undecidable: the global sun
        Assert.Same(global, MapLighting.SunForMask(apart, global, 8));                   // not co-located: M207 stands
        Assert.Equal(2f, MapLighting.SunForMask(new[] { Volume("Only", 64, 2f) }, global, 8)!.LightMapColorScale);   // a lone volume
        Assert.Same(global, MapLighting.SunForMask(Array.Empty<MapLightingVolume>(), global, 8));
    }

    // ---- what a stage changes, and what it leaves to Start ------------------------------------------------------------------

    /// <summary>The nine fields a lighting volume authors and the reader models.</summary>
    private static readonly string[] VolumeFields =
    {
        nameof(MapSunProperties.SunColor), nameof(MapSunProperties.SunDirection), nameof(MapSunProperties.SkyLightColor),
        nameof(MapSunProperties.SkyLightScale), nameof(MapSunProperties.LightMapColorScale), nameof(MapSunProperties.HorizonColor),
        nameof(MapSunProperties.GroundColor), nameof(MapSunProperties.FogColor), nameof(MapSunProperties.FogStartAndEnd),
    };

    private static IEnumerable<PropertyInfo> SunFields() =>
        typeof(MapSunProperties).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is not null);

    /// <summary>A sun whose every field holds a value no other <paramref name="n"/> produces, so a field's source in a
    /// combined sun can be read off it.</summary>
    private static MapSunProperties Distinct(int n)
    {
        var sun = new MapSunProperties();
        int i = 0;
        foreach (var p in SunFields())
        {
            float f = n * 100 + ++i;
            object value = p.PropertyType == typeof(float) ? f
                : p.PropertyType == typeof(bool) ? n % 2 == 0
                : p.PropertyType == typeof(Vector2) ? new Vector2(f, f + 0.5f)
                : p.PropertyType == typeof(Vector3) ? new Vector3(f, f + 0.5f, f + 0.25f)
                : p.PropertyType == typeof(Vector4) ? new Vector4(f, f + 0.5f, f + 0.25f, 1f)
                : throw new NotSupportedException($"{p.Name}: {p.PropertyType}");
            p.SetValue(sun, value);
        }
        return sun;
    }

    [Fact]
    public void A_stage_lays_exactly_the_nine_volume_fields_over_the_board()
    {
        var board = Distinct(1);
        var lit = Distinct(2);

        var result = MapLighting.WithVolumeFields(board, lit);

        foreach (var p in SunFields())
        {
            Assert.False(Equals(p.GetValue(board), p.GetValue(lit)), "the test data must differ in " + p.Name);
            var expected = VolumeFields.Contains(p.Name) ? p.GetValue(lit) : p.GetValue(board);
            Assert.True(Equals(expected, p.GetValue(result)), p.Name + " came from the wrong sun");
        }
        Assert.Same(lit, MapLighting.WithVolumeFields(null, lit));   // no Start sun to keep: the stage's own
    }

    /// <summary>A board bin the way Riot ships one: a global sun that authors fog OFF and a shadow radius, and volumes.</summary>
    private static byte[] LightingBin(params (string Name, int Flags, float Scale)[] volumes)
    {
        var sun = new BinTreeStruct(0, H("MapSunProperties"), new BinTreeProperty[]
        {
            new BinTreeVector4(H("sunColor"), new Vector4(0.5f, 0.5f, 0.5f, 1f)),
            new BinTreeBool(H("fogEnabled"), false),
            new BinTreeF32(H("SunRadiusForShadows"), 75f),
        });
        var objects = new List<BinTreeObject>
        {
            new(1u, H("MapContainer"), new BinTreeProperty[]
            {
                new BinTreeContainer(H("components"), BinPropertyType.Struct, new BinTreeProperty[] { sun }),
            }),
        };
        uint key = 0x100;
        foreach (var (name, flags, scale) in volumes)
        {
            var volume = new BinTreeStruct(0, H("MapLightingVolume"), new BinTreeProperty[]
            {
                new BinTreeMatrix44(H("transform"), Board),
                new BinTreeString(H("name"), name),
                new BinTreeU8(H("mVisibilityFlags"), (byte)flags),
                new BinTreeF32(H("lightMapColorScale"), scale),
            });
            var items = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, new[]
            {
                new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, key), volume),
            });
            objects.Add(new BinTreeObject(0x5000u + key, H("MapPlaceableContainer"), new BinTreeProperty[] { items }));
            key++;
        }
        return Write(new BinTree(objects, Array.Empty<string>()));
    }

    [Fact]
    public void A_stage_never_shows_a_fog_or_shadow_value_the_start_view_does_not()
    {
        var bin = LightingBin(("LightingVolume1", 64, 2f), ("LightingVolume2", 8, 1.5f));
        var global = MapSunProperties.Extract(bin)!;
        var volumes = MapLighting.Extract(bin, global).Volumes;
        Assert.False(global.FogEnabled);
        Assert.Equal(75f, global.SunRadiusForShadows);

        // A volume-lit Start is the M785 reading and stays exactly that: nine fields from the volume, the reader's
        // defaults elsewhere. Reading the volume on top of the global sun would have switched the fog off HERE, on the
        // start view of 22 of the 32 shipped stage boards, which is not this feature's to change.
        var start = MapLighting.EffectiveSun(bin, () => 67)!;
        Assert.Equal(2f, start.LightMapColorScale);
        Assert.True(start.FogEnabled);
        Assert.Equal(0f, start.SunRadiusForShadows);

        var stage = new MapBoardStage("Level 7", "b", "c", 7, 8, new Dictionary<string, bool>(), new[] { "b" }, 0);
        var set = new MapBoardStageSet(new[] { stage }, volumes, global);

        // ... so a stage lays only its volume's fields over whatever Start has:
        var overVolumeStart = set.SunFor(stage, start)!;
        Assert.Equal(1.5f, overVolumeStart.LightMapColorScale);
        Assert.True(overVolumeStart.FogEnabled);                     // Start's default, not a new one
        Assert.Equal(0f, overVolumeStart.SunRadiusForShadows);

        // ... and over a Start the board's own sun lights (10 of the 32 boards) it keeps the board's own values
        var overGlobalStart = set.SunFor(stage, global)!;
        Assert.Equal(1.5f, overGlobalStart.LightMapColorScale);
        Assert.False(overGlobalStart.FogEnabled);
        Assert.Equal(75f, overGlobalStart.SunRadiusForShadows);

        Assert.Same(volumes[1].Lighting, set.SunFor(stage));         // no Start given: as before
    }

    [Fact]
    public void Only_a_bin_naming_both_class_hashes_is_parsed_for_stages()
    {
        var eventsOnly = Bin(Behavior("MapBehavior_Victory", "ArenaSkin_Cue_Victory", Action("MapActionPlaySoundAtLocation")));
        var withStage = Bin(Behavior("MapBehavior_BoardReady", "ArenaSkin_Cue_BoardReady", SetVisibility(68)));

        Assert.True(MapBehaviors.Names(eventsOnly, H("MapBehavior")));
        Assert.False(MapBehaviors.MayHaveStages(eventsOnly));        // Map12's bloom, the Arena maps: a scan, no parse
        Assert.Same(MapBoardStageSet.Empty, MapBoardStages.Load(eventsOnly));

        Assert.True(MapBehaviors.MayHaveStages(withStage));
        Assert.True(MapBoardStages.Load(withStage).HasStages);

        Assert.False(MapBehaviors.MayHaveStages(null));
        Assert.False(MapBehaviors.MayHaveStages(new byte[64]));
        Assert.False(MapBehaviors.MayHaveStages(Bin()));
    }

    private static byte[] Write(BinTree tree)
    {
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }
}

/// <summary>M797 on the shipped boards: read-only over the installed game's Map22 / Map11 / Map12 / Map30 WADs. A
/// machine without them skips (like the other real-data tests here) and says so in the output.</summary>
public sealed class BoardStageRealDataTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private static (WadArchive Wad, HashDatabase Db)? Open(string wad)
    {
        string path = Path.Combine(Final, "Maps", "Shipping", wad + ".wad.client");
        if (!File.Exists(path)) return null;
        var db = new HashSyncService().LoadLocal(_ => { });
        return (WadArchive.Open(path, new WadPathResolver(db)), db);
    }

    private static byte[]? Read(WadArchive wad, string path)
    {
        ulong h = HashAlgorithms.WadPath(path);
        return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
    }

    private static MapBoardStageSet Board(WadArchive wad, string skin)
    {
        var bin = Read(wad, $"data/maps/mapgeometry/map22/{skin}.materials.bin");
        Assert.NotNull(bin);
        return MapBoardStages.Load(bin);
    }

    [Fact]
    public void The_shipped_boards_end_on_the_masks_their_own_behaviours_set()
    {
        if (Open("Map22") is not { } opened) { output.WriteLine("SKIPPED: Map22.wad.client is not installed"); return; }
        using var wad = opened.Wad;

        // anniversary: BoardReady -> 68; LevelUp7Planning -> 64 at 0, then 8 at 3.75
        var anniversary = Board(wad, "anniversary");
        Assert.Equal(new[] { "Board ready (level 1)", "Level 7" }, anniversary.Stages.Select(s => s.Label));
        Assert.Equal(68, anniversary.Stages[0].Mask);
        Assert.Equal(8, anniversary.Stages[1].Mask);
        var l7 = anniversary.Stages[1].ParticleToggles;
        Assert.Equal(4, l7.Count);
        Assert.True(l7["TFT_Anniversary_Skybox_Ievel7_1"]);           // shown at 4 s
        Assert.False(l7["TFT_Anniversary_Skybox_Ievel1_1"]);          // level 1's sky, hidden at 1 s
        Assert.False(l7["TFT_Anniversary_Idle_Ievel1_SoulFighter1"]);
        Assert.False(l7["TFT_Anniversary_Transition1"]);              // shown at 0.5 s, hidden at 5 s: it ends hidden

        // dawnbringernightbringer: BoardReady -> 64; LevelUp7Planning -> 68 -> 76 -> 20
        var dawn = Board(wad, "dawnbringernightbringer");
        Assert.Equal(64, dawn.Stages[0].Mask);
        Assert.Equal(20, dawn.Stages[1].Mask);
        Assert.False(Assert.Single(dawn.Stages[1].ParticleToggles).Value);   // TFT_DawnbringerNightbringer_Levelup1: hidden at 5 s

        // 7yanniversary has no board-ready mask: level 7 alone, 24 -> 56 -> 20
        var seventh = Board(wad, "7yanniversary");
        var only = Assert.Single(seventh.Stages);
        Assert.Equal("Level 7", only.Label);
        Assert.Equal(20, only.Mask);

        output.WriteLine($"ran: anniversary {string.Join(", ", anniversary.Stages.Select(s => s.Label + "=" + s.Mask))}; "
            + $"dawnbringernightbringer {string.Join(", ", dawn.Stages.Select(s => s.Label + "=" + s.Mask))}; "
            + $"7yanniversary {only.Label}={only.Mask}");
    }

    [Fact]
    public void A_stage_lights_the_board_with_the_volume_its_mask_turns_on()
    {
        if (Open("Map22") is not { } opened) { output.WriteLine("SKIPPED: Map22.wad.client is not installed"); return; }
        using var wad = opened.Wad;

        var anniversary = Board(wad, "anniversary");
        Assert.Equal(2, anniversary.Volumes.Count);
        Assert.Equal("LightingVolume1", anniversary.VolumeFor(anniversary.Stages[0])!.Name);    // 68 shares 64
        Assert.Equal("LightingVolume2", anniversary.VolumeFor(anniversary.Stages[1])!.Name);    // 8 shares 8
        Assert.Equal(2f, anniversary.SunFor(anniversary.Stages[0])!.LightMapColorScale);        // M785: the start volume
        Assert.Equal(1.5f, anniversary.SunFor(anniversary.Stages[1])!.LightMapColorScale);

        var dawn = Board(wad, "dawnbringernightbringer");
        Assert.Equal("LightingVolume1", dawn.VolumeFor(dawn.Stages[0])!.Name);                  // 64
        Assert.Equal("LightingVolume2", dawn.VolumeFor(dawn.Stages[1])!.Name);                  // 20 shares 16
        output.WriteLine($"ran: anniversary board-ready {anniversary.SunFor(anniversary.Stages[0])!.LightMapColorScale}, "
            + $"level 7 {anniversary.SunFor(anniversary.Stages[1])!.LightMapColorScale}; dawn volumes "
            + $"{string.Join(", ", dawn.Volumes.Select(v => v.Name + "/" + v.VisibilityFlags))}");
    }

    [Fact]
    public void Dawnbringers_level_7_has_no_level_1_content_where_the_additive_rule_kept_it()
    {
        if (Open("Map22") is not { } opened) { output.WriteLine("SKIPPED: Map22.wad.client is not installed"); return; }
        using var wad = opened.Wad;
        var db = opened.Db;
        var mats = Read(wad, "data/maps/mapgeometry/map22/dawnbringernightbringer.materials.bin")!;
        var axis = MapVisibility.Parse(Read(wad, "data/maps/shipping/map22/map22.bin"), h => db.TryGetBinName(h, out var n) ? n : null).Primary;
        Assert.NotNull(axis);
        Assert.Equal(67, axis!.InitialMask);

        var level7 = MapBoardStages.Load(mats).Stages.Single(s => s.Label == "Level 7");
        var placements = MapParticleExtractor.Extract(mats, h => db.TryGetBinName(h, out var n) ? n : null);

        // placements on layer 64 alone are level 1's: the sky bowl / cloud vortex M796 measured round the level-7 board
        var levelOne = placements.Where(p => p.HasVisibilityFlags && p.VisibilityFlags == 64).ToList();
        Assert.NotEmpty(levelOne);
        Assert.All(levelOne, p => Assert.True(MapVisibility.VisibleForMask(p.VisibilityFlags, axis, 16), p.Name));   // additive: still on
        Assert.All(levelOne, p => Assert.False(MapVisibility.VisibleForStage(p.VisibilityFlags, level7.Mask), p.Name)); // stage: off

        // and level 7's own content is on
        var levelSeven = placements.Where(p => p.HasVisibilityFlags && (p.VisibilityFlags & level7.Mask) != 0).ToList();
        Assert.NotEmpty(levelSeven);
        Assert.All(levelSeven, p => Assert.True(MapVisibility.VisibleForStage(p.VisibilityFlags, level7.Mask), p.Name));
        output.WriteLine($"ran: {levelOne.Count} level-1 placement(s) on the additive rule and off under level 7 (mask {level7.Mask}); "
            + $"{levelSeven.Count} placement(s) share a bit with it");
    }

    [Fact]
    public void Summoner_s_Rift_Map12_Arena_and_Map453_have_no_board_stages_in_any_of_their_bins()
    {
        // EVERY materials bin of the four non-TFT maps, not a sample: 26 + 5 + 10 + 1 on the installed game.
        int checkedBins = 0, naming = 0, mayHave = 0;
        foreach (string wadName in new[] { "Map11", "Map12", "Map30", "Map453" })
        {
            if (Open(wadName) is not { } opened) { output.WriteLine($"SKIPPED: {wadName}.wad.client is not installed"); continue; }
            using var wad = opened.Wad;
            foreach (var entry in wad.Entries.Where(e => e.IsResolved
                         && e.Path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase)))   // any folder of the WAD
            {
                var bytes = wad.Extract(entry);
                Assert.Same(MapBoardStageSet.Empty, MapBoardStages.Load(bytes));
                checkedBins++;
                if (MapBehaviors.Names(bytes, HashAlgorithms.Fnv1a("MapBehavior"))) naming++;
                if (MapBehaviors.MayHaveStages(bytes)) mayHave++;
            }
        }
        Assert.Equal(0, mayHave);   // none can carry a stage, so none is parsed for one
        output.WriteLine($"ran: {checkedBins} Map11 / Map12 / Map30 / Map453 materials bin(s), none with a stage; "
            + $"{naming} name MapBehavior (Map12's bloom, the Arena maps) and {mayHave} also set a mask");
    }

    [Fact]
    public void Every_shipped_board_yields_well_formed_stages()
    {
        if (Open("Map22") is not { } opened) { output.WriteLine("SKIPPED: Map22.wad.client is not installed"); return; }
        using var wad = opened.Wad;
        int bins = 0, withStages = 0, stages = 0;
        foreach (var entry in wad.Entries.Where(e => e.IsResolved
                     && e.Path.StartsWith("data/maps/mapgeometry/map22/", StringComparison.OrdinalIgnoreCase)
                     && e.Path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase)))
        {
            bins++;
            var set = MapBoardStages.Load(wad.Extract(entry));
            if (!set.HasStages) continue;
            withStages++;
            stages += set.Stages.Count;
            Assert.Equal(set.Stages.Count, set.Stages.Select(s => s.Label).Distinct().Count());   // one entry per stage
            Assert.Equal(set.Stages.Count(s => s.Level is null), set.Stages.Take(1).Count(s => s.Level is null));   // board-ready, if any, is first
            Assert.Equal(set.Stages.Where(s => s.Level is not null).Select(s => s.Level), set.Stages.Where(s => s.Level is not null).Select(s => s.Level).Order());
            Assert.All(set.Stages, s =>
            {
                Assert.Equal(s.BehaviorName, s.Chain[^1]);
                Assert.NotEmpty(s.Chain);
                Assert.True(s.Mask is > 0 and <= 255, $"{entry.Path} {s.Label} mask {s.Mask}");
            });
        }
        Assert.True(withStages > 0);
        output.WriteLine($"ran: {bins} Map22 materials bins, {withStages} with stages, {stages} stages in all");
    }
}

/// <summary>M797 through the real view model: picking a stage changes what both viewports are handed.</summary>
public sealed class BoardStageViewModelTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void SetField(MainWindowViewModel vm, string name, object? value) =>
        typeof(MainWindowViewModel).GetField(name, NonPublic)!.SetValue(vm, value);

    private static T Call<T>(MainWindowViewModel vm, string name, params object?[] args) =>
        (T)typeof(MainWindowViewModel).GetMethod(name, NonPublic)!.Invoke(vm, args)!;

    private static void Call(MainWindowViewModel vm, string name, params object?[] args) =>
        typeof(MainWindowViewModel).GetMethod(name, NonPublic)!.Invoke(vm, args);

    private static readonly MapVisibilityAxis Map22 = new(MapVisibility.PrimaryAxisHash, "Map Visibility", 67, true, new[]
    {
        new VisibilityLayer("Stage1", 4), new VisibilityLayer("Stage2", 8), new VisibilityLayer("Stage3", 16),
        new VisibilityLayer("Stage4", 32), new VisibilityLayer("base", 64),
    });

    /// <summary>A three-group map: level 1's layer (64), level 7's (8), and everything (255).</summary>
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

    private static MapBoardStageSet TwoStages() => new(new[]
    {
        new MapBoardStage("Board ready (level 1)", "MapBehavior_BoardReady", "ArenaSkin_Cue_BoardReady", null, 68,
            new Dictionary<string, bool>(), new[] { "MapBehavior_BoardReady" }, 0),
        new MapBoardStage("Level 7", "MapBehavior_LevelUp7Planning", "ArenaSkin_Cue_PlanningLevelUp7", 7, 8,
            new Dictionary<string, bool> { ["Sky1"] = false }, new[] { "MapBehavior_BoardReady", "MapBehavior_LevelUp7Planning" }, 2),
    }, Array.Empty<MapLightingVolume>(), null);

    private static MainWindowViewModel BoardVm(MapBoardStageSet set)
    {
        var vm = new MainWindowViewModel();
        var definition = new MapVisibilityDefinition(new[] { Map22 });
        SetField(vm, "_currentMap", ThreeGroupMap());
        SetField(vm, "_mapVisibility", definition);
        SetField(vm, "_visibilityResolver", new MapVisibilityResolver(null, definition));
        Call(vm, "RebuildVisibilityAxes", definition, null);
        Call(vm, "SetBoardStages", set);
        return vm;
    }

    [Fact]
    public void A_map_without_stages_has_no_picker_and_the_old_layer_rule()
    {
        var vm = BoardVm(MapBoardStageSet.Empty);
        Call(vm, "ApplyMapVisibility");

        Assert.Empty(vm.BoardStageSelectors);
        Assert.Equal(new[] { true, true, true }, vm.CurrentModelSubmeshVisible);

        // a layer picked in Map Visibility is the additive M771 reading, exactly as before
        vm.VisibilityAxes.Single().SelectedIndex = 2;   // Stage2 = 8
        Assert.Equal(new[] { true, true, true }, vm.CurrentModelSubmeshVisible);   // 64 stays on beside it: initial 67 is additive
        vm.VisibilityAxes.Single().SelectedIndex = 0;
    }

    [Fact]
    public void Start_is_the_default_and_changes_nothing()
    {
        var vm = BoardVm(TwoStages());
        Call(vm, "ApplyMapVisibility");

        var picker = Assert.Single(vm.BoardStageSelectors);
        Assert.Equal(new[] { "Start", "Board ready (level 1)", "Level 7" }, picker.Options);
        Assert.Equal(0, picker.SelectedIndex);
        Assert.Null(picker.Selected);
        Assert.Equal(new[] { true, true, true }, vm.CurrentModelSubmeshVisible);
        Assert.False(picker.HasSummary);
    }

    [Fact]
    public void Picking_a_stage_shows_exactly_its_mask_on_the_map_meshes()
    {
        var vm = BoardVm(TwoStages());
        var picker = vm.BoardStageSelectors.Single();

        picker.SelectedIndex = 2;   // Level 7 = 8
        Assert.Equal(new[] { false, true, true }, vm.CurrentModelSubmeshVisible);
        Assert.True(picker.HasSummary);
        Assert.Contains("Mask 8", picker.Summary);

        picker.SelectedIndex = 1;   // Board ready = 68 shares 64, not 8
        Assert.Equal(new[] { true, false, true }, vm.CurrentModelSubmeshVisible);

        picker.SelectedIndex = 0;   // back to Start
        Assert.Equal(new[] { true, true, true }, vm.CurrentModelSubmeshVisible);
        Assert.False(picker.HasSummary);
    }

    [Fact]
    public void A_stage_and_a_map_visibility_layer_hand_the_mask_back_and_forth()
    {
        var vm = BoardVm(TwoStages());
        var picker = vm.BoardStageSelectors.Single();
        var axis = vm.VisibilityAxes.Single();

        axis.SelectedIndex = 3;         // Stage3 = 16, the additive reading
        picker.SelectedIndex = 2;       // a stage takes the mask: the layer steps aside to All
        Assert.Equal(0, axis.SelectedIndex);
        Assert.Equal(new[] { false, true, true }, vm.CurrentModelSubmeshVisible);

        axis.SelectedIndex = 1;         // a layer takes it back: the stage returns to Start
        Assert.Equal(0, picker.SelectedIndex);
        Assert.Null(picker.Selected);
        Assert.Equal(1, axis.SelectedIndex);
    }

    [Fact]
    public void A_stage_switches_a_named_placement_off_and_a_start_disabled_one_on()
    {
        var vm = BoardVm(new MapBoardStageSet(new[]
        {
            new MapBoardStage("Level 7", "b", "c", 7, 8,
                new Dictionary<string, bool> { ["Sky1"] = false, ["Flash"] = true }, new[] { "b" }, 0),
        }, Array.Empty<MapLightingVolume>(), null));
        var sky = new MapParticlePlacement("Sky1", Vector3.Zero, Matrix4x4.Identity, "S", "", VisibilityFlags: 255);
        var flash = new MapParticlePlacement("Flash", Vector3.Zero, Matrix4x4.Identity, "S", "", VisibilityFlags: 255, StartDisabled: true);
        var idle = new MapParticlePlacement("Idle", Vector3.Zero, Matrix4x4.Identity, "S", "", VisibilityFlags: 255, StartDisabled: true);
        var levelOne = new MapParticlePlacement("LevelOne", Vector3.Zero, Matrix4x4.Identity, "S", "", VisibilityFlags: 64, HasVisibilityFlags: true);
        var levelSeven = new MapParticlePlacement("LevelSeven", Vector3.Zero, Matrix4x4.Identity, "S", "", VisibilityFlags: 8, HasVisibilityFlags: true);

        bool Visible(MapParticlePlacement p) => Call<bool>(vm, "IsParticleVisible", p, null);
        bool On(MapParticlePlacement p) => Call<bool>(vm, "StageSwitchesOn", p.Name);

        Assert.True(Visible(sky));                       // Start: nothing is switched
        Assert.True(Visible(levelOne));
        Assert.False(On(flash));

        vm.BoardStageSelectors.Single().SelectedIndex = 1;   // Level 7

        Assert.False(Visible(sky));                      // switched off by name, whatever its mask says
        Assert.True(Visible(flash));
        Assert.True(On(flash));                          // a startDisabled placement the stage switched on may play (M784 lets it)
        Assert.False(On(idle));                          // one it did not stays off
        Assert.False(Visible(levelOne));                 // the exact mask: 64 shares no bit with 8
        Assert.True(Visible(levelSeven));
        Assert.True(MainWindowViewModel.PlaysAtMapLoad(sky) && !MainWindowViewModel.PlaysAtMapLoad(flash));   // the M784 predicate is untouched
    }

    // ---- lighting, through the REAL apply and capture-guard path -------------------------------------------------------
    //
    // BoardVm has no map entry, and ApplyBoardStageLighting returns before it touches anything without one - so a test
    // built on it proves nothing about the lighting. LitBoardVm has the entry, a board sun that authors fog OFF and a
    // shadow radius, and two co-located volumes (flags 64 and 8, the anniversary shape).

    private static readonly Matrix4x4 BoardAt = new(2300f, 0, 0, 0, 0, 3500f, 0, 0, 0, 0, 2500f, 0, 2000f, 0, 2000f, 1);

    private static readonly WadAssetEntry TestEntry = new()
    {
        Path = "data/maps/mapgeometry/map22/testboard.mapgeo", PathHash = 0x1234u, IsResolved = true,
    };

    private static MapSunProperties BoardSun() => new()
    {
        SunColor = new Vector4(0.5f, 0.5f, 0.5f, 1f), LightMapColorScale = 1f, FogEnabled = false, SunRadiusForShadows = 75f,
    };

    /// <summary>A volume as the reader builds one: the nine fields it models, the schema defaults elsewhere.</summary>
    private static MapLightingVolume Vol(string name, int flags, float scale) =>
        new(name, BoardAt, new MapSunProperties { LightMapColorScale = scale, SunColor = new Vector4(scale / 2, scale / 2, scale / 2, 1f) }, flags);

    private static MainWindowViewModel LitBoardVm(out MapBoardStageSet set, out MapSunProperties startSun)
    {
        var v1 = Vol("LightingVolume1", 64, 2f);
        var v2 = Vol("LightingVolume2", 8, 1.5f);
        set = new MapBoardStageSet(TwoStages().Stages, new[] { v1, v2 }, BoardSun());
        startSun = v1.Lighting;   // M785: the flag-64 volume is the one on at the start (initial mask 67)

        var vm = new MainWindowViewModel();
        var definition = new MapVisibilityDefinition(new[] { Map22 });
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
        Call(vm, "ApplySunProperties", startSun);   // what the map load publishes
        Call(vm, "SetBoardStages", set);
        Call(vm, "ApplyMapVisibility");
        return vm;
    }

    [Fact]
    public void Picking_a_stage_runs_the_real_lighting_path_and_never_dirties_the_project()
    {
        var vm = LitBoardVm(out _, out _);
        vm.Project.IsDirty = false;
        var picker = vm.BoardStageSelectors.Single();
        Assert.Equal(2.0, vm.CurrentLightmapScale);

        picker.SelectedIndex = 2;                                   // Level 7: LightingVolume2
        Assert.Equal(1.5, vm.CurrentLightmapScale);                 // the apply RAN - the capture guard below is not vacuous
        Assert.Contains("LightingVolume2", picker.Summary);
        picker.SelectedIndex = 1;                                   // Board ready: mask 68 shares 64
        Assert.Equal(2.0, vm.CurrentLightmapScale);
        picker.SelectedIndex = 0;

        Assert.False(vm.Project.IsDirty);                           // ApplySunProperties -> RebuildSun -> CaptureMapLighting is guarded
        Assert.Empty(vm.Project.MapLighting);
    }

    [Fact]
    public void A_stage_picked_while_another_map_is_opening_leaves_the_loaders_capture_guard_on()
    {
        // LoadMapGeoAsync holds _applyingLighting across the whole open (M515), awaiting in between, and
        // ApplySunProperties clears the flag when it finishes: a stage picked in that window must give it back.
        var vm = LitBoardVm(out _, out _);
        vm.Project.IsDirty = false;
        SetField(vm, "_applyingLighting", true);

        vm.BoardStageSelectors.Single().SelectedIndex = 2;

        Assert.Equal(1.5, vm.CurrentLightmapScale);
        Assert.True((bool)typeof(MainWindowViewModel).GetField("_applyingLighting", NonPublic)!.GetValue(vm)!);
        Assert.False(vm.Project.IsDirty);
    }

    [Fact]
    public void A_stage_view_keeps_what_start_has_in_every_field_a_volume_does_not_model()
    {
        var vm = LitBoardVm(out _, out var start);
        var picker = vm.BoardStageSelectors.Single();
        Assert.True(start.FogEnabled);                              // a volume-lit Start: the reader's defaults (M785, unchanged)

        picker.SelectedIndex = 2;

        Assert.Equal(1.5f, vm.CurrentSunProperties!.LightMapColorScale);   // the volume's field changed ...
        Assert.Equal(start.FogEnabled, vm.CurrentSunProperties.FogEnabled);                // ... the others are Start's, not a default
        Assert.Equal(start.SunRadiusForShadows, vm.CurrentSunProperties.SunRadiusForShadows);
        Assert.Equal(start.FogAlternateColor, vm.CurrentSunProperties.FogAlternateColor);
    }

    [Fact]
    public void Leaving_a_stage_for_a_map_visibility_layer_puts_the_start_lighting_back()
    {
        var vm = LitBoardVm(out _, out var start);
        var picker = vm.BoardStageSelectors.Single();
        picker.SelectedIndex = 2;
        Assert.Equal(1.5, vm.CurrentLightmapScale);

        vm.VisibilityAxes.Single().SelectedIndex = 3;               // Stage3: the layer takes the mask back

        Assert.Equal(0, picker.SelectedIndex);
        Assert.Null(picker.Selected);
        Assert.Equal(2.0, vm.CurrentLightmapScale);                 // Start's lighting is back, not stranded on Level 7's
        Assert.Equal(start.SunColor.X, vm.CurrentSunProperties!.SunColor.X, 5);
        Assert.False(picker.HasSummary);
    }

    [Fact]
    public void A_hand_adjusted_baked_brightness_survives_a_stage_and_the_summary_says_so()
    {
        var vm = LitBoardVm(out _, out _);
        var picker = vm.BoardStageSelectors.Single();

        vm.CurrentLightmapScale = 1.9;                              // the "Baked brightness" box: nothing captures it

        picker.SelectedIndex = 2;
        Assert.Equal(1.9, vm.CurrentLightmapScale);                 // not silently replaced by the volume's 1.5
        Assert.Contains("adjusted", picker.Summary);
        Assert.Equal(new[] { false, true, true }, vm.CurrentModelSubmeshVisible);   // the mask is the stage's regardless

        // the way out the message names: Reset lighting, then pick again
        Call(vm, "ResetLighting");
        Assert.Equal(2.0, vm.CurrentLightmapScale);
        picker.SelectedIndex = 0;
        picker.SelectedIndex = 2;
        Assert.Equal(1.5, vm.CurrentLightmapScale);
    }

    [Fact]
    public void A_slider_moved_by_hand_counts_as_adjusted_lighting_too()
    {
        var vm = LitBoardVm(out _, out _);
        var picker = vm.BoardStageSelectors.Single();

        vm.SunIntensity = 0.4;                                      // captured into a project record by the panel

        picker.SelectedIndex = 2;
        Assert.Equal(2.0, vm.CurrentLightmapScale);
        Assert.Contains("adjusted", picker.Summary);                // "adjusted", not "saved": nothing was saved yet
    }

    [Fact]
    public void Lighting_saved_in_the_project_wins_over_a_stage_and_the_summary_says_so()
    {
        var vm = LitBoardVm(out _, out _);
        var picker = vm.BoardStageSelectors.Single();
        vm.Project.MapLighting.Add(new MapLightingRecord { PathHash = TestEntry.PathHash, SunIntensity = 0.7 });
        var before = vm.CurrentSunProperties;

        picker.SelectedIndex = 2;

        Assert.Equal(2.0, vm.CurrentLightmapScale);
        Assert.Equal(before, vm.CurrentSunProperties);
        Assert.Contains("saved in the project", picker.Summary);
        Assert.DoesNotContain("LightingVolume2", picker.Summary);   // the summary reports what happened, not what the stage asked for
    }

    [Fact]
    public void A_stale_authored_sun_cannot_skip_a_stage_or_leave_stage_lighting_under_start()
    {
        var vm = LitBoardVm(out _, out var start);
        var picker = vm.BoardStageSelectors.Single();
        picker.SelectedIndex = 2;
        Assert.Equal(1.5, vm.CurrentLightmapScale);

        // Another board's load leaves _baseSunAuthored holding a sun that EQUALS this board's Start sun (two boards can
        // share one). The old shortcut compared the wanted sun with it and returned without applying anything.
        SetField(vm, "_baseSunAuthored", start);
        picker.SelectedIndex = 0;

        Assert.Equal(2.0, vm.CurrentLightmapScale);
    }

    [Fact]
    public void A_lone_volume_board_says_which_volume_lights_it()
    {
        var only = Vol("LightingVolume1", 64, 2f);
        var set = new MapBoardStageSet(TwoStages().Stages, new[] { only }, BoardSun());
        var vm = LitBoardVm(out _, out _);
        Call(vm, "SetBoardStages", set);                            // replaces the picker with a lone-volume board's

        var picker = vm.BoardStageSelectors.Single();
        picker.SelectedIndex = 2;

        Assert.Contains("lighting volume 'LightingVolume1'", picker.Summary);   // not "the board's own sun"
        Assert.Same(only, set.VolumeFor(set.Stages[1]));
    }

    [Fact]
    public void A_stage_lit_by_the_global_sun_says_so()
    {
        var vm = LitBoardVm(out _, out _);
        var v1 = Vol("LightingVolume1", 64, 2f);
        var v2 = Vol("LightingVolume2", 72, 1.5f);                  // both volumes share a bit with 8 or 68: undecidable
        Call(vm, "SetBoardStages", new MapBoardStageSet(TwoStages().Stages, new[] { v1, v2 }, BoardSun()));

        var picker = vm.BoardStageSelectors.Single();
        picker.SelectedIndex = 1;                                   // mask 68 shares a bit with both

        Assert.Contains("the board's own sun", picker.Summary);
    }

    // ---- "Save sun & sky to map" -----------------------------------------------------------------------------------------

    private static bool Refused(MainWindowViewModel vm) => Call<bool>(vm, "RefuseSunSaveForBoardStage");

    [Fact]
    public void The_save_to_map_is_refused_while_a_stages_lighting_is_on_screen()
    {
        var vm = LitBoardVm(out _, out _);
        var picker = vm.BoardStageSelectors.Single();
        Assert.False(Refused(vm));                                  // Start: the panel holds the board's own lighting

        picker.SelectedIndex = 2;
        Assert.True(Refused(vm));                                   // Level 7's volume is on the panel

        picker.SelectedIndex = 0;
        Assert.False(Refused(vm));                                  // Start's lighting is back

        // lighting the user owns is what the button is FOR: a stage that left it alone is not stage lighting
        vm.Project.MapLighting.Add(new MapLightingRecord { PathHash = TestEntry.PathHash, SunIntensity = 0.7 });
        picker.SelectedIndex = 2;
        Assert.False(Refused(vm));
    }

    [Fact]
    public void The_save_command_asks_before_it_touches_anything()
    {
        string? root = TestRunIsolation.RepoRoot();
        if (root is null) return;
        string source = File.ReadAllText(Path.Combine(root, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs"));
        int at = source.IndexOf("private async Task SaveSunToMap()", StringComparison.Ordinal);
        Assert.True(at > 0, "SaveSunToMap has moved or been renamed");
        int guard = source.IndexOf("RefuseSunSaveForBoardStage()", at, StringComparison.Ordinal);
        int firstUse = source.IndexOf("_currentMapEntry", at, StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < firstUse, "the board-stage refusal must come before SaveSunToMap reads anything");
    }

    // ---- a map tab's picker, through the REAL capture and restore --------------------------------------------------------

    private static object Capture(MainWindowViewModel vm) =>
        typeof(MainWindowViewModel).GetMethod("CaptureMapScene", NonPublic)!.Invoke(vm, null)
        ?? throw new InvalidOperationException("no scene captured");

    private static void Restore(MainWindowViewModel vm, object scene) =>
        typeof(MainWindowViewModel).GetMethod("RestoreMapScene", NonPublic)!.Invoke(vm, new[] { scene });

    [Fact]
    public void A_tab_switch_puts_the_picker_back_where_the_tab_left_it()
    {
        var vm = LitBoardVm(out _, out _);
        var picker = vm.BoardStageSelectors.Single();
        picker.SelectedIndex = 2;
        var atCapture = vm.CurrentModelSubmeshVisible!.ToList();
        var scene = Capture(vm);

        Call(vm, "SetBoardStages", MapBoardStageSet.Empty);         // another map opens in the tab
        Assert.False(vm.HasBoardStages);
        Assert.Empty(vm.BoardStageSelectors);

        Restore(vm, scene);                                         // and the board's tab comes back
        var back = Assert.Single(vm.BoardStageSelectors);
        Assert.True(vm.HasBoardStages);
        Assert.Equal(2, back.SelectedIndex);
        Assert.Equal(atCapture, vm.CurrentModelSubmeshVisible!);
        Assert.Equal(1.5, vm.CurrentLightmapScale);
        Assert.Contains("LightingVolume2", back.Summary);
    }

    [Fact]
    public void A_stage_picked_while_a_skinned_mesh_tab_is_up_does_not_re_arm_when_the_map_tab_returns()
    {
        // OnSelectedNodeChanged does not clear the viewport for a .skn, so the map's picker stays live while the mesh tab
        // is up and can be moved there. The tab's own snapshot is what has to win.
        var vm = LitBoardVm(out _, out _);
        vm.VisibilityAxes.Single().SelectedIndex = 3;               // Stage3: the additive layer; the picker is on Start
        var atCapture = vm.CurrentModelSubmeshVisible!.ToList();
        Assert.Equal(new[] { true, false, true }, atCapture);
        var scene = Capture(vm);

        var picker = vm.BoardStageSelectors.Single();
        picker.SelectedIndex = 2;                                   // picked in the mesh tab: axis back to All, Level 7's lighting
        Assert.Equal(0, vm.VisibilityAxes.Single().SelectedIndex);
        Assert.Equal(1.5, vm.CurrentLightmapScale);

        Restore(vm, scene);

        picker = vm.BoardStageSelectors.Single();
        var axis = vm.VisibilityAxes.Single();
        Assert.Equal(0, picker.SelectedIndex);                      // NOT Level 7
        Assert.Null(picker.Selected);
        Assert.Equal(3, axis.SelectedIndex);                        // the layer the tab left
        Assert.Equal(atCapture, vm.CurrentModelSubmeshVisible!);    // the meshes follow that layer, not a re-armed stage
        Assert.Equal(2.0, vm.CurrentLightmapScale);                 // Start's lighting, from the snapshot

        // the exclusion still holds afterwards, and the stage's lighting is not skipped
        picker.SelectedIndex = 2;
        Assert.Equal(0, vm.VisibilityAxes.Single().SelectedIndex);
        Assert.Equal(1.5, vm.CurrentLightmapScale);
        Assert.Equal(new[] { false, true, true }, vm.CurrentModelSubmeshVisible!);
    }

    [Fact]
    public void HasBoardStages_follows_the_picker_so_an_empty_items_control_takes_no_spacing()
    {
        var vm = new MainWindowViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(vm.HasBoardStages);

        Call(vm, "SetBoardStages", TwoStages());
        Assert.True(vm.HasBoardStages);
        Assert.Contains(nameof(MainWindowViewModel.HasBoardStages), raised);

        Call(vm, "SetBoardStages", MapBoardStageSet.Empty);
        Assert.False(vm.HasBoardStages);
    }

    [Fact]
    public void A_negative_layer_index_from_the_combo_is_read_as_all_and_takes_nothing_back_from_a_stage()
    {
        var vm = BoardVm(TwoStages());
        var picker = vm.BoardStageSelectors.Single();
        picker.SelectedIndex = 2;

        vm.VisibilityAxes.Single().SelectedIndex = -1;              // a ComboBox can push -1; SelectedBit reads it as All

        Assert.Equal(2, picker.SelectedIndex);
        Assert.NotNull(picker.Selected);
    }

    [Fact]
    public void On_the_real_anniversary_board_a_stage_drives_the_lighting_and_the_play_all_set()
    {
        string wadPath = Path.Combine(Final, @"Maps\Shipping\Map22.wad.client");
        if (!File.Exists(wadPath)) { output.WriteLine("SKIPPED: Map22.wad.client is not installed"); return; }
        var db = new HashSyncService().LoadLocal(_ => { });
        using var wad = WadArchive.Open(wadPath, new WadPathResolver(db));
        ulong binHash = HashAlgorithms.WadPath("data/maps/mapgeometry/map22/anniversary.materials.bin");
        ulong map22Hash = HashAlgorithms.WadPath("data/maps/shipping/map22/map22.bin");
        if (!wad.TryGetEntry(binHash, out var entry) || !wad.TryGetEntry(map22Hash, out _)) { output.WriteLine("SKIPPED: anniversary bin missing"); return; }
        byte[] mats = wad.Extract(binHash);
        string? Name(uint h) => db.TryGetBinName(h, out var n) ? n : null;

        var definition = MapVisibility.Parse(wad.Extract(map22Hash), Name);
        var systems = VfxSystemResolver.ExtractAll(mats);
        var placements = MapParticleExtractor.Extract(mats, Name);
        var set = MapBoardStages.Load(mats);
        var startSun = MapLighting.EffectiveSun(mats, () => definition.Primary?.InitialMask);

        var vm = new MainWindowViewModel();
        SetField(vm, "_currentMap", ThreeGroupMap());
        SetField(vm, "_currentMapEntry", entry);
        SetField(vm, "_mapVisibility", definition);
        SetField(vm, "_visibilityResolver", new MapVisibilityResolver(null, definition));
        SetField(vm, "_vfxSystems", systems);
        Call(vm, "RebuildVisibilityAxes", definition, null);
        Call(vm, "ApplySunProperties", startSun);                 // what the map load publishes
        vm.CurrentModelParticles = placements;
        Call(vm, "SetBoardStages", set);
        var picker = vm.BoardStageSelectors.Single();

        uint Sys(string placement) => placements.Single(p => p.Name == placement).SystemHash;
        uint sky1 = Sys("TFT_Anniversary_Skybox_Ievel1_1"), sky7 = Sys("TFT_Anniversary_Skybox_Ievel7_1");

        Assert.Equal(2.0, vm.CurrentLightmapScale);               // the flag-64 volume: M785
        vm.PlayAllParticles = true;
        var start = vm.CurrentParticlePlayback!.Items.Select(i => i.System.PathHash).ToList();
        Assert.Contains(sky1, start);                             // Start shows every layer, level 1's sky and level 7's alike
        Assert.Contains(sky7, start);

        picker.SelectedIndex = 2;                                 // Level 7 (mask 8)
        Assert.Equal(1.5, vm.CurrentLightmapScale);               // the flag-8 volume
        var level7 = vm.CurrentParticlePlayback!.Items.Select(i => i.System.PathHash).ToList();
        Assert.Contains(sky7, level7);                            // level 7's sky
        Assert.DoesNotContain(sky1, level7);                      // level 1's is gone: flag 64, and switched off by name
        Assert.DoesNotContain(Sys("TFT_Anniversary_Transition1"), level7);   // startDisabled, shown at 0.5 s and hidden at 5 s

        picker.SelectedIndex = 1;                                 // Board ready (mask 68)
        var ready = vm.CurrentParticlePlayback!.Items.Select(i => i.System.PathHash).ToList();
        Assert.Contains(sky1, ready);
        Assert.DoesNotContain(sky7, ready);
        Assert.Equal(2.0, vm.CurrentLightmapScale);

        picker.SelectedIndex = 0;                                 // Start: exactly what it was
        Assert.Equal(2.0, vm.CurrentLightmapScale);
        Assert.Equal(start, vm.CurrentParticlePlayback!.Items.Select(i => i.System.PathHash).ToList());
        output.WriteLine($"ran: Play All {start.Count} system(s) at Start, {level7.Count} at Level 7, {ready.Count} at Board ready");
    }
}

/// <summary>M797: the panel markup. Avalonia resolves a binding at runtime, so a typo in one passes the build and every
/// other test (the headless probe that drives the real view lives in the milestone's scratch folder, because this
/// project does not reference Avalonia.Headless) - this pins the names the markup uses to the members that exist.</summary>
public sealed class BoardStagePanelTests
{
    private static string? Outliner()
    {
        string? root = TestRunIsolation.RepoRoot();
        string? path = root is null ? null : Path.Combine(root, "src", "ReyEngine.App", "Views", "MapOutlinerView.axaml");
        return path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void The_picker_binds_to_members_that_exist()
    {
        if (Outliner() is not { } markup) return;
        var picker = typeof(MainWindowViewModel.BoardStageViewModel);

        Assert.Contains("ItemsSource=\"{Binding BoardStageSelectors}\"", markup);
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("BoardStageSelectors"));
        // an EMPTY items control is still a visible StackPanel child and takes a spacing gap on every non-TFT map
        Assert.Contains("IsVisible=\"{Binding HasBoardStages}\"", markup);
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("HasBoardStages"));
        foreach (string member in new[] { "Options", "SelectedIndex", "Summary", "HasSummary" })
        {
            Assert.Contains("{Binding " + member + "}", markup);
            Assert.NotNull(picker.GetProperty(member));
        }
    }

    [Fact]
    public void The_picker_says_what_a_stage_does_not_emulate()
    {
        if (Outliner() is not { } markup) return;

        Assert.Contains("Not emulated: the lighting-volume and fog fades", markup);
        Assert.Contains("stencil masks", markup);
    }

    [Fact]
    public void The_picker_uses_theme_resources_and_no_colour_literal()
    {
        if (Outliner() is not { } markup) return;
        int start = markup.IndexOf("M797: TFT boards only", StringComparison.Ordinal);
        Assert.True(start > 0);
        string section = markup[start..markup.IndexOf("view toggles (what renders in the viewport)", start, StringComparison.Ordinal)];

        Assert.DoesNotContain("=\"#", section);
        Assert.Contains("{DynamicResource ReyTextDimBrush}", section);
    }
}
