using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Characters.Simulation;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;
using ReyEngine.Formats.Vfx;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M869: the simulation player in the Character window's view model, driven with a real champion (its real VFX closure, clips,
/// ability records and generated definition) on the Map11 stub stage the M868 tests use. The clock is stepped by hand
/// (<see cref="MeshPreviewViewModel.AdvanceSimulation"/>), which is what makes every run reproducible. No renderer is involved:
/// what these prove is the order, the timing, the cleanup and the bookkeeping, not how it looks.
/// </summary>
public sealed class SimulationPlayerTests(ITestOutputHelper output) : IDisposable
{
    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private const string Final = Game + @"\DATA\FINAL";
    private static bool Installed => Directory.Exists(Final + @"\Champions");

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "reyengine-m869-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); } catch { /* best effort */ }
    }

    private static readonly Lazy<(HashDatabase Db, WadPathResolver Resolver)> Hashes = new(() =>
    {
        var db = new HashSyncService().LoadLocal(_ => { });
        return (db, new WadPathResolver(db));
    });

    private bool Skip()
    {
        if (Installed) return false;
        output.WriteLine("SKIPPED: League of Legends is not installed at " + Game);
        return true;
    }

    private sealed record Skin(Dictionary<uint, VfxSystemDefinition> Systems, Dictionary<uint, uint> Resources,
        IReadOnlyList<AnimClipInfo> Clips, IReadOnlyList<AbilitySlot> Abilities);

    private static Skin ReadSkin(string champion)
    {
        var db = Hashes.Value.Db;
        using var wad = WadArchive.Open(Path.Combine(Final, "Champions", champion + ".wad.client"), Hashes.Value.Resolver);
        string lower = champion.ToLowerInvariant();
        var systems = new Dictionary<uint, VfxSystemDefinition>();
        var resources = new Dictionary<uint, uint>();
        var queue = new Queue<ulong>();
        var visited = new HashSet<ulong>();
        queue.Enqueue(HashAlgorithms.WadPath($"data/characters/{lower}/skins/skin0.bin"));
        while (queue.TryDequeue(out var hash))
        {
            if (!visited.Add(hash) || !wad.TryGetEntry(hash, out _)) continue;
            var bytes = wad.Extract(hash);
            foreach (var (k, v) in VfxSystemResolver.ExtractAll(bytes)) systems.TryAdd(k, v);
            foreach (var (k, v) in VfxSystemResolver.ExtractResourceMap(bytes)) resources.TryAdd(k, v);
            foreach (var dep in VfxSystemResolver.ExtractDependencies(bytes)) queue.Enqueue(HashAlgorithms.WadPath(dep));
        }
        var clips = ChampionAnimationData.ParseClips(wad.Extract(HashAlgorithms.WadPath($"data/characters/{lower}/animations/skin0.bin")),
            h => db.TryGetBinName(h, out var n) ? n : null, h => db.TryGetPath(h, out var p) ? p : null);
        var abilities = ChampionSpellData.Read(wad.Extract(HashAlgorithms.WadPath(ChampionRecord.PathFor(lower))), champion);
        return new Skin(systems, resources, clips, abilities);
    }

    private ChampionSimLoader.Result Load(string champion, string? cache = null) =>
        ChampionSimLoader.Load(Final, champion, Hashes.Value.Resolver,
            h => Hashes.Value.Db.TryGetBinName(h, out var n) ? n : null,
            h => Hashes.Value.Db.TryGetPath(h, out var p) ? p : null,
            cache ?? Path.Combine(_temp, "cache"));

    // ---- a fake install with an empty Map11 and a stub scene, as the M868 tests do ----

    private sealed class NoHashes : IHashResolver
    {
        public bool TryGetPath(ulong hash, out string path) { path = ""; return false; }
        public string ResolvePath(ulong hash) => "";
    }

    private string MakeInstall()
    {
        string final = Path.Combine(_temp, "Game", "DATA", "FINAL");
        Directory.CreateDirectory(Path.Combine(final, "Maps", "Shipping"));
        File.WriteAllBytes(Path.Combine(final, "DATA.wad.client"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(final, "Maps", "Shipping", "Map11.wad.client"), Array.Empty<byte>());
        return Path.Combine(_temp, "Game");
    }

    private static MeshAsset Floor() => new()
    {
        Positions = new float[] { -500f, 0f, -500f, 500f, 0f, -500f, 0f, 0f, 500f },
        Normals = new float[] { 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f },
        Uvs = new float[6],
        Indices = new uint[] { 0, 1, 2 },
        SubMeshes = new[] { new SubMeshInfo("ground", 0, 3, 3) },
        VertexCount = 3,
    };

    private static ArenaScene Scene()
    {
        var none = new TextureImage?[1];
        var bg = new MapPreviewBackground("Map11", Floor(), none, none, none, none, none, new[] { false },
            new List<ReyEngine.Formats.Lighting.PointLight>(), MeshCount: 1, MissingTextures: 0);
        var prop = new PropMesh("Map11", bg.Mesh.Positions, bg.Mesh.Normals, bg.Mesh.Uvs, bg.Mesh.Indices, new List<PropSubmesh>());
        return new ArenaScene("Map11", bg, prop, Nav: null, Spawn: new Vector3(10f, 0f, 20f),
            BoundsMin: new Vector3(-500f, 0f, -500f), BoundsMax: new Vector3(500f, 0f, 500f), 1, 0, 0, 0);
    }

    private static PropMesh DummyMesh() => new("dummy", new float[9], new float[9], new float[6], new uint[] { 0, 1, 2 }, new List<PropSubmesh>());

    /// <summary>A preview with the champion loaded the way the host loads it, standing on the stub stage, its definition read.</summary>
    private async Task<MeshPreviewViewModel> PlayerAsync(string champion)
    {
        var skin = ReadSkin(champion);
        var vm = new MeshPreviewViewModel();
        vm.LoadDummyMesh = DummyMesh;
        vm.SetVfx(skin.Systems, skin.Resources);
        vm.SetActions(CharacterActions.Build(Array.Empty<string>(), skin.Clips));
        vm.SetAbilities(skin.Abilities);
        vm.LoadSimulationDefinition = c => Task.FromResult(Load(c).Definition);

        vm.ConfigureArena(new MeshPreviewViewModel.ArenaHost(MakeInstall(), new NoHashes(), _ => null, _ => null,
            LoadViewport: null, LoadScene: _ => Task.FromResult(Scene())));
        await vm.EnvironmentTask;
        vm.BeginSimulation(champion);
        await vm.SimulationTask;
        return vm;
    }

    /// <summary>Step the simulation until it stops (or a generous cap). Returns the number of steps taken.</summary>
    private static int RunToEnd(MeshPreviewViewModel vm, double dt = 0.016)
    {
        int n = 0;
        while (vm.SimRunning && n < 5000) { vm.AdvanceSimulation(dt); n++; }
        Assert.False(vm.SimRunning, "the simulation did not finish");
        return n;
    }

    // ===================================================== the definition

    [Fact]
    public async Task The_definition_loads_once_per_champion_is_cached_and_regenerated_when_stale()
    {
        if (Skip()) return;
        var first = Load("Ezreal");
        Assert.NotNull(first.Definition);
        Assert.False(first.FromCache);
        Assert.True(File.Exists(first.CachePath));
        byte[] bytes = File.ReadAllBytes(first.CachePath!);

        var second = Load("Ezreal");
        Assert.True(second.FromCache);
        Assert.Equal(bytes, ChampionSimJson.Write(second.Definition!));

        // a cache written by another generator version is not trusted
        File.WriteAllBytes(first.CachePath!, System.Text.Encoding.UTF8.GetBytes(
            System.Text.Encoding.UTF8.GetString(bytes).Replace(ChampionSimDefinition.CurrentGeneratorVersion, "M0.old")));
        var third = Load("Ezreal");
        Assert.False(third.FromCache);
        Assert.Equal(ChampionSimDefinition.CurrentGeneratorVersion, third.Definition!.GeneratorVersion);
        Assert.Equal(bytes, File.ReadAllBytes(first.CachePath!));         // and the cache is repaired

        // case-insensitive champion name (the host passes the lower-case folder name)
        Assert.True(Load("ezreal").FromCache);

        // the view model: once per champion, however many skins are shown
        var vm = await PlayerAsync("Ezreal");
        Assert.Equal(1, vm.SimulationLoadCount);
        vm.BeginSimulation("Ezreal");
        vm.BeginSimulation("ezreal");
        Assert.Equal(1, vm.SimulationLoadCount);
        Assert.True(vm.HasSimulation);
        Assert.Equal(new[] { "P", "Q", "W", "E", "R" }, vm.SimAbilities.Take(5).Select(r => r.Key));
        Assert.All(vm.SimAbilities, r => Assert.Contains(r.StatusText, new[] { "OK", "PARTIAL", "BLOCKED" }));
        output.WriteLine(vm.SimSummary);
        vm.StopControl();
    }

    [Fact]
    public async Task Sub_casts_are_listed_under_their_parent_and_indented()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Aatrox");
        var keys = vm.SimAbilities.Select(r => r.Key).ToList();
        int q = keys.IndexOf("Q");
        Assert.Equal(new[] { "Q1", "Q2", "Q3" }, keys.Skip(q + 1).Take(3));
        Assert.All(vm.SimAbilities.Where(r => r.Ability.ParentKey is not null), r => Assert.True(r.IsSub && r.Indent > 0));
        Assert.All(vm.SimAbilities.Where(r => r.Ability.ParentKey is null), r => Assert.False(r.IsSub));
        vm.StopControl();
    }

    // ===================================================== a run

    [Fact]
    public async Task Playing_Q_runs_place_clip_composite_hit_end_at_the_planned_times()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        var stageChampion = vm.CharacterPosition;
        vm.PlaySimulation("Q");

        Assert.True(vm.SimRunning);
        var script = vm.SimCurrentScript!;
        Assert.Equal(new[] { SimStepKind.Place, SimStepKind.Clip, SimStepKind.Composite, SimStepKind.Hit, SimStepKind.End },
            script.Steps.Select(s => s.Kind));
        // Place, Clip and Composite are due at t=0 and ran synchronously at the start
        Assert.Equal(new[] { SimStepKind.Place, SimStepKind.Clip, SimStepKind.Composite }, vm.SimTrace.Select(t => t.Kind));
        Assert.NotNull(vm.Playback);                                          // the composite is playing through the existing path
        Assert.Contains(vm.Playback!.Items, i => i.TravelTo is not null);      // and it has the missile
        Assert.Equal(script.EnemyPosition, vm.TargetDummyPosition);
        Assert.Contains("Q", vm.SimCurrentAbility);

        int steps = RunToEnd(vm);
        Assert.Equal(new[] { SimStepKind.Place, SimStepKind.Clip, SimStepKind.Composite, SimStepKind.Hit, SimStepKind.End },
            vm.SimTrace.Select(t => t.Kind));
        foreach (var t in vm.SimTrace)
        {
            Assert.True(t.Clock >= t.Planned - 1e-6, $"{t.Kind} ran early: {t.Clock} < {t.Planned}");
            Assert.True(t.Clock < t.Planned + 0.0161, $"{t.Kind} ran late: {t.Clock} vs {t.Planned}");
        }
        Assert.Contains(vm.SimLog, l => l.Contains("hits enemy"));
        output.WriteLine($"{steps} steps; " + string.Join(" / ", vm.SimLog));

        // cleanup: nothing playing, everyone back on the stage
        Assert.Null(vm.Playback);
        Assert.Null(vm.SelectedEvent);
        Assert.Null(vm.SimAllyPosition);
        Assert.Equal(stageChampion, vm.CharacterPosition);
        vm.StopControl();
    }

    [Fact]
    public async Task Showcase_visits_P_Q_W_E_R_in_order_with_a_reset_between_each()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        vm.ClearSimulationHistory();
        vm.ShowcaseSimulation();
        int guard = 0;
        while (vm.SimRunning && guard++ < 20000) vm.AdvanceSimulation(0.05);
        Assert.False(vm.SimRunning);

        var expected = vm.SimulationDefinition!.Abilities.Where(a => a.ParentKey is null).Select(a => a.Key).ToList();
        Assert.Equal(new[] { "P", "Q", "W", "E", "R" }, vm.SimVisited.Take(5));
        Assert.Equal(expected, vm.SimVisited);
        // the scene is reset once per ability at least (between them and after the last)
        Assert.True(vm.SimResetCount >= vm.SimVisited.Count, $"{vm.SimResetCount} resets for {vm.SimVisited.Count} abilities");
        Assert.Equal(vm.SimVisited.Count, vm.SimTrace.Count(t => t.Kind == SimStepKind.End));
        Assert.Null(vm.Playback);
        output.WriteLine("visited " + string.Join(",", vm.SimVisited) + $"; {vm.SimResetCount} resets");
        vm.StopControl();
    }

    [Fact]
    public async Task Showcase_plays_sub_casts_in_order_inside_their_parent()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Aatrox");
        vm.ClearSimulationHistory();
        vm.ShowcaseSimulation();
        int guard = 0;
        while (vm.SimRunning && guard++ < 40000) vm.AdvanceSimulation(0.05);
        var visited = vm.SimVisited;
        int q1 = visited.IndexOf("Q1");
        Assert.True(q1 > 0);
        Assert.Equal(new[] { "Q1", "Q2", "Q3" }, visited.Skip(q1).Take(3));
        Assert.DoesNotContain("Q", visited);                                   // the wrapper owns nothing to show
        Assert.True(visited.IndexOf("Q3") < visited.IndexOf("W"));
        vm.StopControl();
    }

    [Fact]
    public async Task Pause_freezes_the_simulation_clock_and_resume_continues_it()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        vm.PlaySimulation("Q");
        vm.AdvanceSimulation(0.1);
        double before = vm.SimClock;
        Assert.Equal(0.1, before, 6);
        Assert.Equal(1.0, vm.SimParticleTimeScale);

        vm.ToggleSimulationPauseCommand.Execute(null);
        Assert.True(vm.SimPaused);
        Assert.Equal(0.0, vm.SimParticleTimeScale);                            // the D3D11 surface multiplies its live step by this
        Assert.False(vm.Animation.IsPlaying);
        for (int i = 0; i < 100; i++) vm.AdvanceSimulation(0.1);
        Assert.Equal(before, vm.SimClock);
        Assert.True(vm.SimRunning);

        vm.ToggleSimulationPauseCommand.Execute(null);
        Assert.False(vm.SimPaused);
        vm.AdvanceSimulation(0.1);
        Assert.Equal(before + 0.1, vm.SimClock, 6);
        vm.StopSimulation();
        Assert.False(vm.SimPaused);
        vm.StopControl();
    }

    [Fact]
    public async Task Speed_scales_the_simulation_clock_the_clip_and_the_particle_step()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        vm.PlaySimulation("Q");
        int at1 = RunToEnd(vm, 0.01);

        vm.SimSpeedIndex = 4;                                                  // 2x
        Assert.Equal(2.0, vm.SimSpeed);
        // chosen but not in force: nothing runs, so the clip and both particle simulators stay at their own speed
        Assert.Equal(1.0, vm.Animation.Speed);
        Assert.Equal(1.0, vm.SimParticleTimeScale);
        Assert.Equal(1.0, vm.SimEffectiveSpeed);
        vm.PlaySimulation("Q");
        Assert.Equal(2.0, vm.Animation.Speed);
        Assert.Equal(2.0, vm.SimParticleTimeScale);
        Assert.Equal(2.0, vm.SimEffectiveSpeed);
        int at2 = RunToEnd(vm, 0.01);
        output.WriteLine($"1x: {at1} steps, 2x: {at2} steps");
        Assert.InRange(at1, at2 * 2 - 3, at2 * 2 + 3);                         // half the wall time

        vm.SimSpeedIndex = 0;                                                  // 0.25x
        vm.PlaySimulation("Q");
        Assert.Equal(0.25, vm.SimParticleTimeScale);
        Assert.Equal(0.25, vm.Animation.Speed);
        int slow = RunToEnd(vm, 0.01);
        Assert.InRange(slow, at1 * 4 - 6, at1 * 4 + 6);
        vm.StopControl();
    }

    [Fact]
    public async Task Stop_and_reset_leave_no_playback_no_event_no_ally_and_the_stage_positions()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ahri");
        var stage = (Champion: vm.CharacterPosition, Yaw: vm.CharacterYaw, Dummy: vm.TargetDummyPosition);
        vm.PlaySimulation("R");                                                // a dash
        for (int i = 0; i < 40; i++) vm.AdvanceSimulation(0.016);
        Assert.True(vm.SimRunning);
        Assert.NotNull(vm.Playback);
        Assert.NotEqual(stage.Champion, vm.CharacterPosition);                 // the dash moved her (0.5 s cast + 0.25 s dash, now at 0.64 s)

        vm.StopSimulation();
        Assert.False(vm.SimRunning);
        Assert.Null(vm.Playback);
        Assert.Null(vm.SelectedEvent);
        Assert.Null(vm.SimAllyPosition);
        Assert.Equal(stage.Champion, vm.CharacterPosition);
        Assert.Equal(stage.Yaw, vm.CharacterYaw);
        Assert.Equal(stage.Dummy, vm.TargetDummyPosition);
        Assert.Empty(vm.SimAbilities.Where(r => r.IsRunning));

        // a stopped simulation ignores further time, and Stop with nothing running is harmless
        vm.AdvanceSimulation(1.0);
        vm.StopSimulation();
        Assert.False(vm.SimRunning);
        vm.StopControl();
    }

    [Fact]
    public async Task A_dash_moves_the_caster_across_its_window_and_no_displacement_survives_the_run()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ahri");
        var start = vm.CharacterPosition;
        vm.PlaySimulation("R");
        var dash = vm.SimCurrentScript!.Find(SimStepKind.Displace)!;
        Assert.Equal(SimDisplaceKind.Dash, dash.Displacement);

        // before the window: nothing moved
        vm.AdvanceSimulation(dash.Time - 0.05);
        Assert.Equal(start, vm.CharacterPosition);
        // half way through: half the distance
        vm.AdvanceSimulation(0.05 + (dash.EndTime - dash.Time) / 2.0);
        float mid = Vector3.Distance(start, vm.CharacterPosition);
        float full = Vector3.Distance(dash.From!.Value, dash.To!.Value);
        Assert.InRange(mid, full * 0.45f, full * 0.55f);
        // after: at the end point, then the run ends and she is back
        vm.AdvanceSimulation((dash.EndTime - dash.Time) / 2.0 + 0.01);
        Assert.InRange(Vector3.Distance(start, vm.CharacterPosition), full * 0.99f, full * 1.01f);
        RunToEnd(vm);
        Assert.Equal(start, vm.CharacterPosition);
        vm.StopControl();
    }

    [Fact]
    public async Task Replay_is_deterministic()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        vm.PlaySimulation("Q");
        string script1 = Describe(vm.SimCurrentScript!);
        RunToEnd(vm, 0.02);
        var trace1 = vm.SimTrace.ToList();
        var log1 = vm.SimLog.ToList();

        vm.ReplaySimulation();
        Assert.True(vm.SimRunning);
        Assert.Equal(script1, Describe(vm.SimCurrentScript!));
        RunToEnd(vm, 0.02);
        Assert.Equal(trace1, vm.SimTrace);
        Assert.Equal(log1, vm.SimLog);
        vm.StopControl();
    }

    private static string Describe(SimScript s) =>
        string.Join("|", s.Steps.Select(x => $"{x.Kind}:{x.Time:R}:{x.EndTime:R}:{x.Source}:{x.Text}")) + $"#{s.ChampionPosition}#{s.EnemyPosition}#{s.AllyPosition}";

    [Fact]
    public async Task The_ally_dummy_exists_only_for_an_ally_ability_and_is_removed_after()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Kayle");
        var ally = vm.SimulationDefinition!.Abilities.Single(a => a.Key == "W");
        Assert.True(SimulationPlanner.NeedsAlly(ally));
        int stage = vm.SceneProps!.Instances.Count;                            // the enemy dummy (and the floor, under D3D11)

        vm.PlaySimulation("W");
        Assert.NotNull(vm.SimAllyPosition);
        Assert.Equal(SimAimTarget.Ally, vm.SimCurrentScript!.AimAt);
        Assert.Equal(stage + 1, vm.SceneProps!.Instances.Count);               // plus the ally, one more dummy mesh
        Assert.Equal(vm.SimAllyPosition, vm.SimCurrentScript.AllyPosition);
        Assert.NotEqual(vm.CameraFocus, (vm.CharacterPosition + vm.TargetDummyPosition!.Value) * 0.5f);   // the camera frames all three
        RunToEnd(vm);
        Assert.Null(vm.SimAllyPosition);
        Assert.Equal(stage, vm.SceneProps!.Instances.Count);

        // an ability that needs no ally never brings one
        vm.PlaySimulation("Q");
        Assert.Null(vm.SimAllyPosition);
        Assert.Equal(stage, vm.SceneProps!.Instances.Count);
        RunToEnd(vm);

        // and stopping an ally ability mid-run removes it too
        vm.PlaySimulation("W");
        Assert.NotNull(vm.SimAllyPosition);
        vm.StopSimulation();
        Assert.Null(vm.SimAllyPosition);
        Assert.Equal(stage, vm.SceneProps!.Instances.Count);
        vm.StopControl();
    }

    [Fact]
    public async Task The_passive_plays_its_composite_and_ends_after_about_three_seconds()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        vm.PlaySimulation("P");
        Assert.Equal(SimulationPlanner.PassiveSeconds, vm.SimCurrentScript!.EndTime);
        RunToEnd(vm, 0.05);
        Assert.Contains(vm.SimTrace, t => t.Kind == SimStepKind.Composite);
        Assert.InRange(vm.SimTrace[^1].Clock, 3.0 - 1e-6, 3.06);
        vm.StopControl();
    }

    [Fact]
    public async Task A_subject_that_is_not_a_character_clears_the_list_and_stops_a_run()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        vm.PlaySimulation("Q");
        vm.LeaveStage();
        Assert.False(vm.SimRunning);
        Assert.False(vm.HasSimulation);
        Assert.Empty(vm.SimAbilities);
        Assert.Null(vm.Playback);
    }

    // ===================================================== M869 review fixes

    private MeshAsset MeshForShow() => Floor();

    [Fact]
    public async Task Replay_after_a_champion_change_does_not_run_the_old_champions_ability()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        vm.PlaySimulation("Q");
        RunToEnd(vm);
        Assert.NotEmpty(vm.SimLog);

        vm.BeginSimulation("Kalista");
        Assert.Empty(vm.SimLog);
        Assert.Equal("", vm.SimNotes);
        Assert.Equal("", vm.SimStatusLine);
        vm.ReplaySimulation();                                                 // nothing to replay: the list was cleared
        Assert.False(vm.SimRunning);
        await vm.SimulationTask;
        vm.ReplaySimulation();
        Assert.False(vm.SimRunning);
        vm.StopControl();
    }

    [Fact]
    public async Task Replay_of_a_wrapper_replays_the_whole_sub_cast_sequence()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Aatrox");
        vm.PlaySimulation("Q");
        int guard = 0; while (vm.SimRunning && guard++ < 5000) vm.AdvanceSimulation(0.05);
        Assert.Equal(new[] { "Q1", "Q2", "Q3" }, vm.SimVisited);

        vm.ClearSimulationHistory();
        vm.ReplaySimulation();
        guard = 0; while (vm.SimRunning && guard++ < 5000) vm.AdvanceSimulation(0.05);
        Assert.Equal(new[] { "Q1", "Q2", "Q3" }, vm.SimVisited);
        vm.StopControl();
    }

    [Fact]
    public async Task Playing_while_paused_unpauses_and_the_new_run_moves()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        vm.PlaySimulation("Q");
        vm.AdvanceSimulation(0.1);
        vm.ToggleSimulationPauseCommand.Execute(null);
        Assert.True(vm.SimPaused);

        vm.PlaySimulation("W");
        Assert.False(vm.SimPaused);
        Assert.Equal(1.0, vm.SimParticleTimeScale);
        vm.AdvanceSimulation(0.1);
        Assert.Equal(0.1, vm.SimClock, 6);

        vm.ToggleSimulationPauseCommand.Execute(null);
        vm.ReplaySimulation();
        Assert.False(vm.SimPaused);
        vm.AdvanceSimulation(0.1);
        Assert.Equal(0.1, vm.SimClock, 6);
        vm.StopControl();
    }

    [Fact]
    public async Task The_chosen_speed_is_only_in_force_during_a_run_and_the_animate_speed_comes_back()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        vm.Animation.Speed = 1.3;                                             // the Animate tab's own slider
        vm.SimSpeedIndex = 0;                                                 // 0.25x
        Assert.Equal(1.3, vm.Animation.Speed);
        Assert.Equal(1.0, vm.SimParticleTimeScale);

        vm.PlaySimulation("Q");
        Assert.Equal(0.25, vm.Animation.Speed);
        Assert.Equal(0.25, vm.SimEffectiveSpeed);
        RunToEnd(vm, 0.05);                                                   // finish
        Assert.Equal(1.3, vm.Animation.Speed);
        Assert.Equal(1.0, vm.SimEffectiveSpeed);

        vm.PlaySimulation("Q");                                               // stop
        vm.StopSimulationAndResetCommand.Execute(null);
        Assert.Equal(1.3, vm.Animation.Speed);
        Assert.Equal(1.0, vm.SimParticleTimeScale);

        vm.PlaySimulation("Q");                                               // another champion shown
        vm.BeginSimulation("Kalista");
        Assert.Equal(1.3, vm.Animation.Speed);

        await vm.SimulationTask;
        vm.PlaySimulation("Q");                                               // a prop shown
        vm.LeaveStage();
        Assert.Equal(1.3, vm.Animation.Speed);
        Assert.False(vm.SimEffectivePaused);
        vm.StopControl();
    }

    [Fact]
    public async Task A_new_mesh_or_skin_stops_the_run()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        vm.PlaySimulation("Q");
        vm.AdvanceSimulation(0.1);
        Assert.True(vm.SimRunning);
        vm.Show("another skin", MeshForShow(), null, null, keepStage: true);
        Assert.False(vm.SimRunning);
        Assert.Null(vm.Playback);
        vm.StopControl();
    }

    [Fact]
    public async Task A_failed_definition_load_is_tried_again_the_next_time()
    {
        var vm = new MeshPreviewViewModel();
        int calls = 0;
        ChampionSimDefinition? def = null;
        vm.LoadSimulationDefinition = _ => { calls++; return Task.FromResult(def); };
        vm.BeginSimulation("Ezreal");
        await vm.SimulationTask;
        Assert.False(vm.HasSimulation);
        Assert.Equal(1, calls);

        if (Skip()) return;
        def = Load("Ezreal").Definition;
        vm.BeginSimulation("Ezreal");                                         // shown again: asks again
        await vm.SimulationTask;
        Assert.Equal(2, calls);
        Assert.True(vm.HasSimulation);
        vm.BeginSimulation("Ezreal");                                         // loaded now: not asked a third time
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_missile_that_stops_at_the_first_hit_ends_at_the_enemy_and_the_impact_waits_for_it()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        vm.PlaySimulation("Q");
        var hit = vm.SimCurrentScript!.Find(SimStepKind.Hit)!;
        Assert.True(hit.StopsMissile);
        var dummy = vm.TargetDummyPosition!.Value;
        var missile = vm.Playback!.Items.Single(i => i.TravelTo is not null && i.System.Name.EndsWith("_mis"));
        Assert.Equal(dummy, missile.TravelTo);                                // not the full range
        var impact = vm.Playback.Items.First(i => i.System.Name.EndsWith("_tar"));
        Assert.Equal(hit.Time, impact.StartDelay, 3);                         // and the impact plays when the missile arrives
        Assert.Equal(dummy, impact.WorldPos);
        vm.StopControl();
    }

    [Fact]
    public async Task A_piercing_missile_flies_on_past_the_enemy()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        var def = vm.SimulationDefinition!;
        var q = def.Abilities.Single(a => a.Key == "Q");
        var piercing = q with { Tags = q.Tags.Concat(new[] { SimulationPlanner.PiercingTag }).ToList() };
        vm.SetSimulationDefinition(def with { Abilities = def.Abilities.Select(a => a.Key == "Q" ? piercing : a).ToList() });
        vm.PlaySimulation("Q");
        Assert.False(vm.SimCurrentScript!.Find(SimStepKind.Hit)!.StopsMissile);
        var dummy = vm.TargetDummyPosition!.Value;
        var missile = vm.Playback!.Items.Single(i => i.TravelTo is not null && i.System.Name.EndsWith("_mis"));
        Assert.True(Vector3.Distance(vm.CharacterPosition, missile.TravelTo!.Value) > Vector3.Distance(vm.CharacterPosition, dummy) + 300f);
        // the impact still plays on the dummy at the hit time
        Assert.Equal(vm.SimCurrentScript.Find(SimStepKind.Hit)!.Time, vm.Playback.Items.First(i => i.System.Name.EndsWith("_tar")).StartDelay, 3);
        vm.StopControl();
    }

    [Fact]
    public async Task Stop_and_reset_resets_the_scene_even_when_nothing_is_running()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        var stageDummy = vm.TargetDummyPosition;
        vm.MoveDummy(new Vector3(300f, 0f, 300f));
        Assert.NotEqual(stageDummy, vm.TargetDummyPosition);
        Assert.False(vm.SimRunning);
        vm.StopSimulationAndResetCommand.Execute(null);
        Assert.Equal(stageDummy, vm.TargetDummyPosition);
        vm.StopControl();
    }

    [Fact]
    public async Task The_dummy_gizmo_is_off_until_asked_for()
    {
        if (Skip()) return;
        var vm = await PlayerAsync("Ezreal");
        Assert.NotNull(vm.TargetDummyPosition);
        Assert.Null(vm.DummyGizmoPivot);
        vm.MoveDummyEnabled = true;
        Assert.Equal(vm.TargetDummyPosition, vm.DummyGizmoPivot);
        vm.StopControl();
    }
}
