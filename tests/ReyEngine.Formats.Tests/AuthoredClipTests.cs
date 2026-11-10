using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters.Simulation;
using ReyEngine.Formats.Skeletons;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M873: the clip Riot authored wins. The graph walk (sequencer, selector, parametric, condition, parallel), the player's clip choice, the strict
/// grading rules, the resume key, the exit code and the sheet crop. Real-data tests read the installed champion WADs and skip with a message
/// when the install is missing.
/// </summary>
public sealed class AuthoredClipTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "reyengine-m873-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); } catch { } }

    // ---------------------------------------------------------------- synthetic graph

    private static AnimClipInfo Atom(string name, string file) =>
        new(name, "assets/characters/x/skins/base/animations/" + file, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<uint>());

    private static uint H(string name) => AnimationClipGraph.HashOf(name);

    private static AnimClipNode Node(string name, AnimClipNodeKind kind, params (string Child, float? Value)[] children) =>
        new(name, H(name), kind, kind.ToString(), children.Select(c => new AnimClipChild(H(c.Child), c.Value)).ToList());

    [Fact]
    public void An_atomic_clip_resolves_to_itself_and_wins_over_a_node_of_the_same_key()
    {
        var a = Atom("Spell1", "x_spell1.anm");
        var r = AnimationClipGraph.Resolve("spell1", new[] { a }, new[] { Node("Spell1", AnimClipNodeKind.Sequencer, ("Other", null)) });
        Assert.NotNull(r);
        Assert.False(r!.IsComposite);
        Assert.Same(a, r.First);
        Assert.Null(AnimationClipGraph.Resolve("Nope", new[] { a }, Array.Empty<AnimClipNode>()));
    }

    [Fact]
    public void A_hash_named_clip_is_found_by_the_hash_of_the_authored_name()
    {
        var a = Atom($"0x{H("Spell3"):x8}", "x_spell3.anm");
        Assert.Same(a, AnimationClipGraph.Resolve("Spell3", new[] { a }, Array.Empty<AnimClipNode>())!.First);
    }

    [Fact]
    public void A_sequencer_plays_its_children_in_order_and_expands_nested_entries()
    {
        var atoms = new[] { Atom("In", "in.anm"), Atom("Loop", "loop.anm"), Atom("Out", "out.anm") };
        var nodes = new[]
        {
            Node("Spell1", AnimClipNodeKind.Sequencer, ("Inner", null), ("Out", null)),
            Node("Inner", AnimClipNodeKind.Sequencer, ("In", null), ("Loop", null)),
        };
        var r = AnimationClipGraph.Resolve("Spell1", atoms, nodes)!;
        Assert.Equal("sequencer", r.Kind);
        Assert.Equal(new[] { "In", "Loop", "Out" }, r.Chain.Select(c => c.Name));
        Assert.Equal(new[] { "Spell1", "Inner" }, r.Via);
    }

    [Fact]
    public void Selector_takes_its_first_pair_parametric_the_value_nearest_zero_condition_the_branch_without_a_threshold()
    {
        var atoms = new[] { Atom("A", "a.anm"), Atom("B", "b.anm"), Atom("C", "c.anm") };
        var nodes = new[]
        {
            Node("S", AnimClipNodeKind.Selector, ("A", 60f), ("B", 20f)),
            Node("P", AnimClipNodeKind.Parametric, ("A", -180f), ("B", -45f), ("C", 45f)),
            Node("F", AnimClipNodeKind.ConditionFloat, ("A", 460f), ("B", null)),
            Node("L", AnimClipNodeKind.ConditionBool, ("B", 0f), ("A", 1f)),   // false branch first
            Node("Q", AnimClipNodeKind.Parallel, ("C", null), ("A", null)),
        };
        Assert.Equal("A", AnimationClipGraph.Resolve("S", atoms, nodes)!.First.Name);
        Assert.Equal("B", AnimationClipGraph.Resolve("P", atoms, nodes)!.First.Name);
        Assert.Equal("B", AnimationClipGraph.Resolve("F", atoms, nodes)!.First.Name);
        Assert.Equal("B", AnimationClipGraph.Resolve("L", atoms, nodes)!.First.Name);
        Assert.Equal("C", AnimationClipGraph.Resolve("Q", atoms, nodes)!.First.Name);
    }

    [Fact]
    public void A_cycle_or_a_node_with_no_clip_resolves_to_nothing_instead_of_looping()
    {
        var nodes = new[] { Node("X", AnimClipNodeKind.Sequencer, ("Y", null)), Node("Y", AnimClipNodeKind.Selector, ("X", 1f)), Node("E", AnimClipNodeKind.Other) };
        Assert.Null(AnimationClipGraph.Resolve("X", Array.Empty<AnimClipInfo>(), nodes));
        Assert.Null(AnimationClipGraph.Resolve("E", Array.Empty<AnimClipInfo>(), nodes));
    }

    // ---------------------------------------------------------------- the player's choice

    private static AbilitySim Ability(string? authored, SimClip? clip, IReadOnlyList<SimClip>? chain = null) =>
        new("Q", null, 0, "XQ", "XQ", "XQ", null, null, Array.Empty<string>(), null, null, "Self", 0, 0, 0, Array.Empty<string>(), null,
            authored, clip, null, null, null, Array.Empty<MissileSim>(), Array.Empty<SimSystemRef>(), Array.Empty<SimSystemRef>(), Array.Empty<SimSystemRef>(),
            Array.Empty<ClipParticleEventSim>(), Array.Empty<ClipSoundEventSim>(), new ScenarioHint(false, false, false, null),
            Array.Empty<FieldSource>(), Array.Empty<string>(), chain);

    private static SimClip Clip(string name, string file, string source = "authored") =>
        new(name, "assets/characters/x/skins/base/animations/" + file, source, 30f, 1f, 1f / 30f, "anmFps");

    [Fact]
    public void The_authored_clip_beats_the_composites_guess()
    {
        var own = new[] { Atom("Crit", "x_crit.anm"), Atom("Spell2", "x_spell2.anm") };
        var a = Ability("Crit", Clip("Crit", "x_crit.anm"));
        var c = SimClipChooser.Choose(a, own, Array.Empty<AnimClipNode>(), f => true, "x_spell2.anm")!;
        Assert.Equal("x_crit.anm", c.File);
        Assert.Equal("authored", c.Source);
    }

    [Fact]
    public void An_authored_composite_name_plays_the_first_atomic_clip_and_keeps_the_rest_in_the_chain()
    {
        var own = new[] { Atom("In", "in.anm"), Atom("Loop", "loop.anm") };
        var nodes = new[] { Node("Spell4", AnimClipNodeKind.Sequencer, ("In", null), ("Loop", null)) };
        var a = Ability("Spell4", Clip("In", "in.anm", "authored-composite"));
        var c = SimClipChooser.Choose(a, own, nodes, f => true, "wrong.anm")!;
        Assert.Equal("in.anm", c.File);
        Assert.Equal("authored-composite", c.Source);
        Assert.Equal(new[] { "in.anm", "loop.anm" }, c.Chain);
    }

    [Fact]
    public void Another_skins_clip_is_never_played_the_definitions_base_file_is_the_fallback_then_the_composite()
    {
        // the loaded skin's graph has no 'Spell3' at all (a sibling skin's graph does, but the chooser is only given this skin's)
        var a = Ability("Spell3", Clip("Spell3_East", "spell3_east.anm", "authored-composite"));
        var none = Array.Empty<AnimClipInfo>();
        var c = SimClipChooser.Choose(a, none, Array.Empty<AnimClipNode>(), f => true, "ahri_skin14_spell3.anm")!;
        Assert.Equal("spell3_east.anm", c.File);
        Assert.Equal("definition", c.Source);
        // the base file is not loaded either: only then the composite's
        var d = SimClipChooser.Choose(a, none, Array.Empty<AnimClipNode>(), f => f == "ahri_skin14_spell3.anm", "ahri_skin14_spell3.anm")!;
        Assert.Equal("composite", d.Source);
        Assert.Null(SimClipChooser.Choose(a, none, Array.Empty<AnimClipNode>(), f => false, null));
    }

    [Fact]
    public void A_skin_with_its_own_version_of_an_unauthored_clip_plays_its_own()
    {
        var own = new[] { Atom("Spell1_2Run", "skin5_spell1_2run.anm") };
        var a = Ability(null, Clip("Spell1_2Run", "x_spell1_2run.anm", "convention"));
        var c = SimClipChooser.Choose(a, own, Array.Empty<AnimClipNode>(), f => true, "x_spell1_in.anm")!;
        Assert.Equal("skin5_spell1_2run.anm", c.File);
        // and the base skin plays the definition's, not the composite's other guess
        var baseOwn = new[] { Atom("Spell1_2Run", "x_spell1_2run.anm") };
        Assert.Equal("x_spell1_2run.anm", SimClipChooser.Choose(a, baseOwn, Array.Empty<AnimClipNode>(), f => true, "x_spell1_in.anm")!.File);
    }

    // ---------------------------------------------------------------- real data

    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private static readonly string Champions = Path.Combine(Game, "DATA", "FINAL", "Champions");
    private static readonly Lazy<(HashDatabase Db, WadPathResolver Resolver)> Hashes = new(() =>
    {
        var db = new HashSyncService().LoadLocal(_ => { });
        return (db, new WadPathResolver(db));
    });

    private bool Skip()
    {
        if (Directory.Exists(Champions)) return false;
        output.WriteLine("SKIPPED: League of Legends is not installed at " + Game);
        return true;
    }

    private static ChampionSimContext Context() => new()
    {
        GameVersion = "test",
        ResolveBinName = h => Hashes.Value.Db.TryGetBinName(h, out var n) ? n : null,
        ResolveWadPath = h => Hashes.Value.Db.TryGetPath(h, out var p) ? p : null,
    };

    private static (ChampionSimInputs Inputs, ChampionSimDefinition Def) Load(string champion)
    {
        using var wad = WadArchive.Open(ChampionSimBatch.WadPathOf(Champions, champion), Hashes.Value.Resolver);
        var ctx = Context();
        var inputs = ChampionSimSource.Load(wad, champion, ctx.ResolveBinName, ctx.ResolveWadPath);
        return (inputs, ChampionSimGenerator.Generate(inputs, ctx));
    }

    private static AbilitySim Slot(ChampionSimDefinition d, string key) => Assert.Single(d.Abilities, a => a.Key == key);
    private static string? FileOf(AbilitySim a) => SimClipChooser.FileOf(a.Clip?.AnmPath);

    [Theory]
    [InlineData("Amumu", "W", "Crit", "amumu_crit.anm")]
    [InlineData("Amumu", "E", "Crit", "amumu_crit.anm")]
    [InlineData("Ashe", "Q", "Spell3", "ashe_spell3.anm")]
    [InlineData("Anivia", "Q", "Spell4", "anivia_spell4.anm")]
    [InlineData("Bard", "W", "Spell3", "bard_spell3.anm")]
    [InlineData("Bard", "E", "Spell2", "bard_spell2.anm")]
    public void The_authored_name_decides_the_clip_even_when_it_looks_swapped(string champion, string key, string authored, string file)
    {
        if (Skip()) return;
        var (inputs, def) = Load(champion);
        var a = Slot(def, key);
        Assert.Equal(authored, a.AuthoredClipName);
        Assert.Equal(file, FileOf(a), ignoreCase: true);
        Assert.Equal("authored", a.Clip!.Source);
        // the player, offered a composite that guessed another clip, still plays the authored one
        var choice = SimClipChooser.Choose(a, inputs.Clips, inputs.ClipNodes, _ => true, "composite_guess.anm")!;
        Assert.Equal(file, choice.File, ignoreCase: true);
    }

    [Fact]
    public void Ahri_E_resolves_through_a_graph_condition_to_a_base_skin_clip_never_skin14s()
    {
        if (Skip()) return;
        var (inputs, def) = Load("Ahri");
        var e = Slot(def, "E");
        Assert.Equal("Spell3", e.AuthoredClipName);
        Assert.Equal("authored-composite", e.Clip!.Source);
        Assert.DoesNotContain("skin14", FileOf(e)!, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(e.Clip.Via!);
        var choice = SimClipChooser.Choose(e, inputs.Clips, inputs.ClipNodes, _ => true, "ahri_skin14_spell3.anm")!;
        Assert.Equal(FileOf(e), choice.File, ignoreCase: true);
        Assert.Equal("authored-composite", choice.Source);
    }

    [Fact]
    public void Composite_clip_names_resolve_in_the_real_graphs()
    {
        if (Skip()) return;
        // Heimerdinger Q: a sequencer (through two nested entries) ending in an idle
        var (_, hd) = Load("Heimerdinger");
        var q = Slot(hd, "Q");
        Assert.Equal("authored-composite", q.Clip!.Source);
        Assert.True(q.ClipChain is { Count: > 1 }, "a sequencer keeps every clip it plays");
        Assert.Equal(q.Clip.Name, q.ClipChain![0].Name);
        // Kalista Q: a bool condition; E: a parallel
        var (_, kd) = Load("Kalista");
        Assert.Equal("authored-composite", Slot(kd, "Q").Clip!.Source);
        Assert.Equal("authored-composite", Slot(kd, "E").Clip!.Source);
        // AurelionSol authors no clip name for Q (M882): a root with no clip and no child clip plays no cast clip, instead of the SpellN convention guess; one whose
        // children author non-attack clips keeps the convention clip and says so
        var (ai, ad) = Load("AurelionSol");
        var aq = Slot(ad, "Q");
        Assert.Null(aq.AuthoredClipName);
        if (aq.Clip is null) Assert.Contains(aq.Issues, i => i.StartsWith("authors no clip", StringComparison.Ordinal));
        else
        {
            Assert.Equal("convention", aq.Clip.Source);
            Assert.Contains(aq.Issues, i => i.StartsWith("convention clip kept", StringComparison.Ordinal));
            Assert.Equal(FileOf(aq), SimClipChooser.Choose(aq, ai.Clips, ai.ClipNodes, _ => true, "aurelionsol_spell1_in.anm")!.File, ignoreCase: true);
        }
        // Akshan E authors 'Spell2', which is no key of its graph: nothing authored resolves, the convention variant is labelled as one
        var (ki, kad) = Load("Akshan");
        var ae = Slot(kad, "E");
        Assert.Equal("Spell2", ae.AuthoredClipName);
        Assert.Null(AnimationClipGraph.Resolve("Spell2", ki.Clips, ki.ClipNodes));
        Assert.Equal("convention", ae.Clip!.Source);
    }

    [Fact]
    public void The_generator_version_records_the_change_and_the_definition_round_trips()
    {
        Assert.Equal("M896.1", ChampionSimDefinition.CurrentGeneratorVersion);
        if (Skip()) return;
        var (_, def) = Load("Ahri");
        var bytes = ChampionSimJson.Write(def);
        Assert.Equal(bytes, ChampionSimJson.Write(ChampionSimJson.Read(bytes)));
    }

    // ---------------------------------------------------------------- strict grading

    private static AbilityEvidence Run(bool expectsEffects, double changed, double effect, int particles, bool clip = true) => new()
    {
        Key = "Q",
        Planned = new[] { new PlannedStep("Place", 0), new PlannedStep("End", 1.0) },
        Trace = new[] { new StepEvidence("Place", 0, 0), new StepEvidence("End", 1.0, 1.0) },
        EndReached = true, ExpectsEffects = expectsEffects,
        ClipExpected = clip ? "a.anm" : null, ClipSeen = clip ? "a.anm" : null, ClipAdvanced = clip,
        Frames = new[] { new FrameEvidence("baseline", 0, 0, 0, 0, 0), new FrameEvidence("cast", 0.15, 0.15, changed, effect, particles) },
        ParticleBaseline = 0,
    };

    [Fact]
    public void An_ability_with_effects_needs_them_on_screen_a_pose_alone_fails()
    {
        var pose = SimCheckGrader.Grade(Run(true, 0.05, 0.0, 0));
        Assert.Equal(SimCheckGrade.Incomplete, pose.Grade);
        Assert.Contains("only the caster pose changed", Assert.Single(pose.Reasons));
        Assert.True(SimCheckGrader.Grade(Run(true, 0.05, 0.02, 0)).IsComplete);     // effect pixels outside the caster
        Assert.True(SimCheckGrader.Grade(Run(true, 0.0, 0.0, 12)).IsComplete);      // or new particles
        Assert.Equal(SimCheckGrade.Incomplete, SimCheckGrader.Grade(Run(true, 0.0, 0.0, 0)).Grade);
    }

    [Fact]
    public void A_pose_only_pass_is_for_abilities_without_visual_systems_and_says_so()
    {
        var g = SimCheckGrader.Grade(Run(false, 0.05, 0.0, 0));
        Assert.True(g.IsComplete);
        Assert.Equal("animation only - no visual systems in the data", Assert.Single(g.Notes));
        Assert.Equal(SimCheckGrade.Incomplete, SimCheckGrader.Grade(Run(false, 0.0, 0.0, 0)).Grade);
        // particles gained: the ability is not "animation only"
        Assert.Empty(SimCheckGrader.Grade(Run(false, 0.05, 0.0, 12)).Notes);
    }

    [Fact]
    public void A_dash_with_effects_in_the_data_but_none_on_screen_is_not_complete_though_the_caster_moved()
    {
        // the caster travelled (changed pixels) but nothing outside the union of its own boxes changed and no particle was born
        var dash = Run(true, 0.08, 0.0, 0) with { DisplacementExpected = true, DisplacementApplied = true };
        var g = SimCheckGrader.Grade(dash);
        Assert.Equal(SimCheckGrade.Incomplete, g.Grade);
        Assert.Contains("only the caster pose changed", Assert.Single(g.Reasons));
        Assert.True(SimCheckGrader.Grade(dash with { Frames = new[] { new FrameEvidence("cast", 0.15, 0.15, 0.08, 0.03, 0) } }).IsComplete);
    }

    [Fact]
    public void Union_of_caster_boxes_covers_the_whole_path_and_stays_in_the_frame()
    {
        var u = SimCheckFrames.Union(new PixelRect[] { new(10, 20, 30, 40), new(200, 10, 30, 40), default });
        Assert.Equal(new PixelRect(10, 10, 220, 50), u);
        Assert.Null(SimCheckFrames.Union(Array.Empty<PixelRect>()));
    }

    [Fact]
    public void A_reason_with_a_lone_apostrophe_still_normalises_and_paired_quotes_are_names()
    {
        Assert.Equal("exception: InvalidOperationException: can't open '_' now", SimCheckReports.Normalize("exception: InvalidOperationException: can't open 'a.anm' now"));
        Assert.Equal(SimCheckReports.Normalize("clip missing: 'a.anm' expected, 'b.anm' was playing"), SimCheckReports.Normalize("clip missing: 'c.anm' expected, 'd.anm' was playing"));
        Assert.Equal("log error: Mesh: don't # items", SimCheckReports.Normalize("log error: Mesh: don't 12 items"));
    }

    [Fact]
    public void The_chain_head_must_be_loaded_or_the_choice_falls_through()
    {
        var own = new[] { Atom("In", "in.anm"), Atom("Loop", "loop.anm") };
        var nodes = new[] { Node("Spell4", AnimClipNodeKind.Sequencer, ("In", null), ("Loop", null)) };
        var a = Ability("Spell4", Clip("In", "in.anm", "authored-composite"));
        // in.anm is not loaded, loop.anm is: the second clip must not be played under the authored label
        var c = SimClipChooser.Choose(a, own, nodes, f => f == "loop.anm", "comp.anm")!;
        Assert.NotEqual("authored-composite", c.Source);
        Assert.Equal("composite", c.Source);
    }

    [Fact]
    public void The_plan_is_timed_from_the_clip_the_player_plays_not_the_base_skins()
    {
        var clip = Clip("Spell1", "x_spell1.anm") with { DurationSeconds = 1.0f };
        var a = Ability("Spell1", clip);
        var def = new ChampionSimDefinition(ChampionSimDefinition.CurrentSchemaVersion, ChampionSimDefinition.CurrentGeneratorVersion, "t", "X", null, null, "b", null,
            new[] { a }, Array.Empty<AttackSim>(), Array.Empty<string>());
        var stage = new SimStage(System.Numerics.Vector3.Zero, 0f, new System.Numerics.Vector3(0, 0, 600));
        var baseScript = SimulationPlanner.Plan(def, a, stage);
        var skinClip = clip with { AnmPath = "assets/characters/x/skins/skin5/animations/skin5_spell1.anm", DurationSeconds = 2.5f };
        var skinScript = SimulationPlanner.Plan(def, a, stage, null, skinClip);
        Assert.Equal(1.0f, baseScript.Find(SimStepKind.Clip)!.EndTime, 3);
        Assert.Equal(2.5f, skinScript.Find(SimStepKind.Clip)!.EndTime, 3);
        Assert.True(skinScript.EndTime >= 2.5f);
        Assert.True(skinScript.EndTime > baseScript.EndTime);
    }


    [Fact]
    public void The_authored_clip_must_be_the_one_the_player_chose()
    {
        var ok = Run(false, 0.05, 0.0, 0) with { ClipAuthored = true, ClipDefinition = "a.anm", ClipChoiceSource = "authored" };
        Assert.True(SimCheckGrader.Grade(ok).IsComplete);
        var bad = ok with { ClipDefinition = "b.anm" };
        Assert.Contains("clip mismatch", Assert.Single(SimCheckGrader.Grade(bad).Reasons));
    }

    [Fact]
    public void Effects_are_expected_from_caster_target_return_missile_and_clip_particle_systems()
    {
        var none = Ability(null, null);
        Assert.False(SimCheckGrader.ExpectsEffects(none));
        var visible = new SimSystemRef(null, null, "0x1", "Sys", true);
        var invisible = new SimSystemRef(null, null, "0x2", "Dark", false);
        Assert.True(SimCheckGrader.ExpectsEffects(none with { CasterSystems = new[] { visible } }));
        Assert.True(SimCheckGrader.ExpectsEffects(none with { TargetSystems = new[] { new SimSystemRef(null, null, "0x3", null, null) } }));
        Assert.False(SimCheckGrader.ExpectsEffects(none with { CasterSystems = new[] { invisible } }));
        Assert.True(SimCheckGrader.ExpectsEffects(none with { Missiles = new[] { new MissileSim("m", visible, null, "ConstantSpeed", 1, null, null, 1, "authored") } }));
    }

    // ---------------------------------------------------------------- resume and exit code

    private static SimCheckChampionReport Report(string champion, string status = "COMPLETE", string speed = "1", string? placement = "minimized") =>
        new(champion, "1.0", "G1", SimCheckReports.CheckVersion, status, Array.Empty<string>(), null, 1, 2, 3, "D3D11", "w", speed, Array.Empty<string>(),
            Array.Empty<SimCheckAbilityReport>(), placement);

    [Fact]
    public void A_blocked_report_is_never_done_and_a_different_speed_or_placement_is_not_done()
    {
        string path = SimCheckReports.ReportPath(_temp, "1.0", "Ahri");
        SimCheckReports.WriteFile(path, SimCheckReports.Write(Report("Ahri", "BLOCKED")));
        Assert.False(SimCheckReports.IsDone(path, "1.0", "G1", false));
        SimCheckReports.WriteFile(path, SimCheckReports.Write(Report("Ahri", "INCOMPLETE")));
        Assert.True(SimCheckReports.IsDone(path, "1.0", "G1", false, "1", "minimized"));
        Assert.False(SimCheckReports.IsDone(path, "1.0", "G1", false, "0.5", "minimized"));
        Assert.False(SimCheckReports.IsDone(path, "1.0", "G1", false, "1", "offscreen"));
        Assert.True(SimCheckReports.IsDone(path, "1.0", "G1", false));                    // a caller that names neither still resumes
        // a report written before the placement was recorded does not satisfy a run that names one
        SimCheckReports.WriteFile(path, SimCheckReports.Write(Report("Ahri", placement: null)));
        Assert.False(SimCheckReports.IsDone(path, "1.0", "G1", false, "1", "minimized"));
    }

    [Fact]
    public void The_exit_code_is_3_unless_every_champion_is_complete_and_every_report_was_written()
    {
        Assert.Equal(0, SimCheckReports.ExitCode(new[] { Report("A"), Report("B") }));
        Assert.Equal(3, SimCheckReports.ExitCode(new[] { Report("A"), Report("B", "INCOMPLETE") }));
        Assert.Equal(3, SimCheckReports.ExitCode(new[] { Report("A"), Report("B", "BLOCKED") }));
        Assert.Equal(3, SimCheckReports.ExitCode(new[] { Report("A") }, skipped: 1));
        Assert.Equal(0, SimCheckReports.ExitCode(Array.Empty<SimCheckChampionReport>()));
    }

    // ---------------------------------------------------------------- the sheet crop

    [Fact]
    public void The_crop_is_the_region_grown_to_the_cell_aspect_and_kept_inside_the_frame()
    {
        var region = new PixelRect(500, 300, 200, 120);
        var crop = SimCheckFrames.CropFor(region, 1280, 720, 16.0 / 9.0);
        Assert.InRange(crop.Width / (double)crop.Height, 1.76, 1.79);
        Assert.True(crop.X <= region.X && crop.Right >= region.Right && crop.Y <= region.Y && crop.Bottom >= region.Bottom);
        Assert.True(crop.Width < 1280 / 2);
        // a region at the frame's corner slides in, it is not cut
        var corner = SimCheckFrames.CropFor(new PixelRect(0, 0, 100, 60), 1280, 720, 16.0 / 9.0);
        Assert.Equal(0, corner.X); Assert.Equal(0, corner.Y);
        Assert.InRange(corner.Width / (double)corner.Height, 1.74, 1.8);
        // an empty or frame-sized region is the whole frame
        Assert.Equal(new PixelRect(0, 0, 1280, 720), SimCheckFrames.CropFor(default, 1280, 720, 16.0 / 9.0));
        Assert.Equal(new PixelRect(0, 0, 1280, 720), SimCheckFrames.CropFor(new PixelRect(0, 0, 1280, 720), 1280, 720, 16.0 / 9.0));
    }

    [Fact]
    public void Resampling_a_crop_upscales_and_keeps_the_pixels_of_the_crop()
    {
        var f = new byte[8 * 8 * 4];
        for (int i = 0; i < f.Length; i += 4) { f[i] = f[i + 1] = f[i + 2] = 10; f[i + 3] = 255; }
        for (int y = 2; y < 4; y++) for (int x = 2; x < 4; x++) { int o = (y * 8 + x) * 4; f[o] = f[o + 1] = f[o + 2] = 240; }
        var big = SimCheckFrames.ResampleCrop(f, 8, 8, new PixelRect(2, 2, 2, 2), 16, 16);
        Assert.Equal(16 * 16 * 4, big.Length);
        Assert.All(Enumerable.Range(0, 256), i => Assert.Equal(240, big[i * 4]));       // the crop is entirely the bright block
        Assert.Equal(255, big[3]);
        Assert.Empty(SimCheckFrames.ResampleCrop(f, 8, 8, default, 0, 0));
    }
}
