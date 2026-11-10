using System.Globalization;
using System.Numerics;
using ReyEngine.Core;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Characters.Simulation;
using ReyEngine.Formats.Vfx;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M896: the simulation's End covers the natural duration of the systems a cast plays (<see cref="SimEffectSpan"/>), and a system that never ends by itself is
/// released at the spell's authored duration (mDataValues) or the 3 s convention. The real-asset half reads the installed game (skipped when missing).
/// </summary>
public sealed class M896Tests(ITestOutputHelper output)
{
    // ---- synthetic: the end-time rule ----

    private static VfxEmitterDefinition Emitter(string name, float life, float? emitterLife, bool single, float rate = 10f, float linger = 0f, float delay = 0f,
        VfxChildParticleSet? children = null) =>
        new(name, VfxCurveF.Const(rate), VfxCurveF.Const(life), emitterLife, linger, delay, single, false, 0,
            VfxCurve3.Const(Vector3.One), null, new VfxCurve4(Vector4.One, null, null), null, null, null, null,
            VfxCurve3.Const(Vector3.Zero), "particles/x.tex", Vector2.One, 1, false, false, Children: children);

    private static VfxSystemDefinition System(uint hash, string name, params VfxEmitterDefinition[] emitters) => new(hash, name, "", emitters);

    [Fact]
    public void Span_finite_burst_is_delay_plus_life()
    {
        var s = SimEffectSpan.Of(System(1, "Burst", Emitter("a", 2.5f, null, single: true, delay: 0.5f)));
        Assert.False(s.Endless);
        Assert.Equal(3f, s.Seconds, 3);
    }

    [Fact]
    public void Span_timed_emitter_is_lifetime_plus_particle_life_and_continuous_is_endless()
    {
        var timed = SimEffectSpan.Of(System(1, "Zone", Emitter("a", 1f, 4f, single: false)));
        Assert.False(timed.Endless);
        Assert.Equal(5f, timed.Seconds, 3);
        var loop = SimEffectSpan.Of(System(2, "Loop", Emitter("a", 1f, null, single: false, linger: 0.75f)));
        Assert.True(loop.Endless);
        Assert.Equal(0.75f, loop.StopTail, 3);
        var immortal = SimEffectSpan.Of(System(3, "Immortal", Emitter("a", -1f, null, single: true, linger: 0.4f)));
        Assert.True(immortal.Endless);
    }

    [Fact]
    public void Span_counts_children_born_on_death()
    {
        var kid = System(9, "Kid", Emitter("k", 2f, null, single: true));
        var parent = System(1, "Parent", Emitter("p", 1f, null, single: true, children: new VfxChildParticleSet(new[] { new VfxChildIdentifier(0, 9) }, true, 1f, Array.Empty<string>())));
        var s = SimEffectSpan.Of(parent, id => id.EffectLink == 9 ? kid : null);
        Assert.Equal(3f, s.Seconds, 3);   // dies at 1 s, the child lives 2 s
    }

    private static AbilitySim Ability(SimSystemRef[] casters, SimSystemRef[]? targets = null, float? duration = null) =>
        new("W", null, 1, "XW", "XW", "XW", null, null, Array.Empty<string>(), null, null, "Location", 900f, 0f, 0f, Array.Empty<string>(), null,
            null, new SimClip("Spell2", "x.anm", "authored", 30f, 1.0f, 1f / 30f, "anmFps"), 0.25f, null, null,
            Array.Empty<MissileSim>(), casters, targets ?? Array.Empty<SimSystemRef>(), Array.Empty<SimSystemRef>(),
            Array.Empty<ClipParticleEventSim>(), Array.Empty<ClipSoundEventSim>(), new ScenarioHint(true, false, false, null),
            Array.Empty<FieldSource>(), Array.Empty<string>(), DurationSeconds: duration, DurationField: duration is null ? null : "Duration");

    private static SimSystemRef Ref(string name) => new("0x1", null, "0x2", name, true);

    private static ChampionSimDefinition Def(AbilitySim a) => new(ChampionSimDefinition.CurrentSchemaVersion, ChampionSimDefinition.CurrentGeneratorVersion, "t", "Test", null, null,
        "data/characters/test/skins/skin0.bin", null, new[] { a }, Array.Empty<AttackSim>(), Array.Empty<string>());

    [Fact]
    public void End_covers_a_long_finite_system()
    {
        var a = Ability(new[] { Ref("Long") });
        var stage = new SimStage(Vector3.Zero, 0f, new Vector3(0f, 0f, 500f));
        var before = SimulationPlanner.Plan(Def(a), a, stage);
        var after = SimulationPlanner.Plan(Def(a), a, stage, spanOf: _ => new SimSystemSpan(4.2f, false, 0f, "Long"));
        Assert.Equal(1f, before.EndTime, 3);          // the clip alone
        Assert.Equal(4.2f, after.EndTime, 3);         // the system's last particle
        Assert.Null(after.Find(SimStepKind.Release));
        Assert.Contains(after.Notes, n => n.Contains("Long to 4.2 s"));
    }

    [Fact]
    public void Endless_system_is_released_at_the_authored_duration_then_dies_out()
    {
        var a = Ability(new[] { Ref("Zone") }, duration: 5f);
        var stage = new SimStage(Vector3.Zero, 0f, new Vector3(0f, 0f, 500f));
        var s = SimulationPlanner.Plan(Def(a), a, stage, spanOf: _ => new SimSystemSpan(1f, true, 0.6f, "Zone"));
        var release = s.Find(SimStepKind.Release)!;
        Assert.Equal("authored", release.Source);
        Assert.Equal(5.25f, release.Time, 3);         // cast 0.25 + Duration 5
        Assert.Equal(5.85f, s.EndTime, 3);            // + the 0.6 s die-out
        Assert.True(s.Steps.ToList().IndexOf(release) < s.Steps.ToList().IndexOf(s.Find(SimStepKind.End)!));
    }

    [Fact]
    public void Endless_system_without_a_duration_holds_the_convention_and_the_cap_clips()
    {
        var a = Ability(new[] { Ref("Loop") });
        var stage = new SimStage(Vector3.Zero, 0f, new Vector3(0f, 0f, 500f));
        var s = SimulationPlanner.Plan(Def(a), a, stage, spanOf: _ => new SimSystemSpan(0f, true, 5f, "Loop"));
        var release = s.Find(SimStepKind.Release)!;
        Assert.Equal("convention", release.Source);
        Assert.Equal(SimulationPlanner.EndlessHoldSeconds, release.Time, 3);
        Assert.Equal(SimulationPlanner.EndlessHoldSeconds + SimulationPlanner.ReleaseTailMaxSeconds, s.EndTime, 3);

        var longOne = Ability(new[] { Ref("Mushroom") }, duration: 300f);
        var capped = SimulationPlanner.Plan(Def(longOne), longOne, stage, spanOf: _ => new SimSystemSpan(0f, true, 1f, "Mushroom"));
        Assert.Equal(SimulationPlanner.EffectMaxSeconds, capped.EndTime, 3);
        Assert.True(capped.Find(SimStepKind.Release)!.Time <= SimulationPlanner.EffectMaxSeconds - 1f + 1e-3f);
        Assert.Contains(capped.Notes, n => n.Contains("clipped at the 10 s cap"));
    }

    [Fact]
    public void Target_system_counts_from_the_hit()
    {
        var a = Ability(Array.Empty<SimSystemRef>(), new[] { Ref("Impact") });
        var stage = new SimStage(Vector3.Zero, 0f, new Vector3(0f, 0f, 500f));
        var s = SimulationPlanner.Plan(Def(a), a, stage, spanOf: _ => new SimSystemSpan(3f, false, 0f, "Impact"));
        var hit = s.Find(SimStepKind.Hit)!;
        Assert.Equal(hit.Time + 3f, s.EndTime, 3);
    }

    [Fact]
    public void Duration_name_rule()
    {
        KeyValuePair<string, float> V(string n, float v) => new(n, v);
        Assert.Equal("Duration", SimDuration.Pick(new[] { V("SlowDuration", 2f), V("Duration", 5f) })!.Value.Key);
        Assert.Equal("ZoneDuration", SimDuration.Pick(new[] { V("SlowDuration", 9f), V("ZoneDuration", 4f) })!.Value.Key);
        Assert.Null(SimDuration.Pick(new[] { V("StunDuration", 1f), V("Damage", 80f) }));
        Assert.Equal("SlowZoneDuration", SimDuration.Pick(new[] { V("SlowLingerDuration", 1f), V("SlowZoneDuration", 5f) })!.Value.Key);   // Lux E
        Assert.Null(SimDuration.Pick(new[] { V("ChillDuration", 1f), V("SlowDurationAtMaxMultiplier", 1.5f) }));                     // Anivia R
        Assert.Equal("VoidlingDuration", SimDuration.Pick(new[] { V("VoidlingDuration", 8f), V("StackDuration", 25000f) })!.Value.Key);   // Malzahar W
    }

    // ---- the no-restart rule, on the real D3D11 particle driver (skipped without the game's shader cache) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_republished_playback_keeps_running_simulators_and_a_released_item_stops_its_own(bool keep)
    {
        const string final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
        if (!Directory.Exists(final)) { output.WriteLine("SKIPPED: no game"); return; }
        using var cache = ReyEngine.Formats.Shaders.ShaderCacheReader.Open(final, null, out _);
        Assert.NotNull(cache);
        using var renderer = new ReyEngine.Rendering.D3D11.ShaderPreviewRenderer();
        Assert.True(renderer.Initialize(out var error), error);
        var emitter = new VfxEmitterDefinition("zone", VfxCurveF.Const(20), VfxCurveF.Const(1f), null, 0.5f, 0, false, false, 3, VfxCurve3.Const(new Vector3(50)), null,
            VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero), "assets/test/white.dds", Vector2.One, 1, false, false);
        var zone = new ReyEngine.App.ViewModels.VfxPlaybackItem(new VfxSystemDefinition(1, "zone", "", new[] { emitter }), Matrix4x4.Identity,
            new ReyEngine.Core.Decoding.TextureImage?[] { new(1, 1, new byte[] { 255, 255, 255, 255 }) });
        var driver = new ReyEngine.App.Services.D3D11MapParticles(renderer, cache!) { KeepRunningAcrossRebuild = keep };
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 100, 300), Vector3.Zero, Vector3.UnitY);
        var vp = view * Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1, 1, 5000);
        var sims = (Dictionary<ReyEngine.App.ViewModels.VfxPlaybackItem, ReyEngine.Rendering.Vfx.VfxParticleSimulator>)typeof(ReyEngine.App.Services.D3D11MapParticles)
            .GetField("_sims", global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance)!.GetValue(driver)!;

        driver.SetPlayback(new ReyEngine.App.ViewModels.VfxPlayback(new[] { zone }));
        for (int i = 0; i < 30; i++) driver.Tick(1f / 60f, view, vp, new Vector3(0, 100, 300), 300);   // half a second in
        // a republish: a NEW playback holding an equal item (what every simulation step does)
        driver.SetPlayback(new ReyEngine.App.ViewModels.VfxPlayback(new[] { zone with { } }));
        driver.Tick(1f / 60f, view, vp, new Vector3(0, 100, 300), 300);
        float age = Assert.Single(sims).Value.Emitters[0].EmitterAge;
        if (keep) { Assert.Equal(1, driver.KeptRunning); Assert.InRange(age, 0.5f, 0.53f); }   // carried on
        else Assert.InRange(age, 0.015f, 0.018f);                                                 // the old behaviour: started over

        driver.SetPlayback(new ReyEngine.App.ViewModels.VfxPlayback(new[] { zone with { Released = true } }));
        driver.Tick(1f / 60f, view, vp, new Vector3(0, 100, 300), 300);
        var released = Assert.Single(sims).Value;
        Assert.True(released.IsStopped);
        if (keep) Assert.True(released.LiveParticleCount > 0, "a released zone's particles die out, they are not cut");
    }

    // ---- real assets: the measurement ----

    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private static readonly string Champions = Path.Combine(Game, "DATA", "FINAL", "Champions");

    private static readonly (string Champ, string Key)[] Long =
    {
        ("Anivia", "R"), ("Morgana", "W"), ("Singed", "Q"), ("Lux", "E"), ("Brand", "W"), ("Karthus", "E"), ("Ziggs", "E"), ("Teemo", "R"),
        ("Gangplank", "R"), ("Veigar", "E"), ("Zilean", "Q"), ("Malzahar", "W"), ("Swain", "W"), ("Rumble", "R"), ("MissFortune", "E"),
    };

    /// <summary>Opt-in (REYENGINE_M896_PROBE=1): per ability, the End without and with the effect spans, and the authored duration.</summary>
    [Fact]
    public void Probe_planned_end_versus_natural_duration()
    {
        if (global::System.Environment.GetEnvironmentVariable("REYENGINE_M896_PROBE") != "1" || !Directory.Exists(Champions)) { output.WriteLine("SKIPPED (opt-in)"); return; }
        var db = new HashSyncService().LoadLocal(_ => { });
        var resolver = new WadPathResolver(db);
        var context = new ChampionSimContext
        {
            GameVersion = ChampionSimSource.ReadGameVersion(Game),
            ResolveBinName = h => db.TryGetBinName(h, out var n) ? n : null,
            ResolveWadPath = h => db.TryGetPath(h, out var p) ? p : null,
        };
        var I = CultureInfo.InvariantCulture;
        foreach (var (champ, key) in Long)
        {
            using var wad = WadArchive.Open(ChampionSimBatch.WadPathOf(Champions, champ), resolver);
            var inputs = ChampionSimSource.Load(wad, champ, context.ResolveBinName, context.ResolveWadPath);
            var def = ChampionSimGenerator.Generate(inputs, context);
            var a = def.Abilities.First(x => x.Key == key);
            VfxSystemDefinition? Child(VfxChildIdentifier id) =>
                id.EffectLink != 0 && inputs.Systems.TryGetValue(id.EffectLink, out var l) ? l
                : inputs.ResourceMap.TryGetValue(id.EffectKey, out var o) && inputs.Systems.TryGetValue(o, out var k) ? k : null;
            SimSystemSpan? SpanOf(SimSystemRef r) =>
                r.ObjectHash is { } h && uint.TryParse(h.AsSpan(2), NumberStyles.HexNumber, I, out var oh) && inputs.Systems.TryGetValue(oh, out var sys)
                    ? SimEffectSpan.Of(sys, Child) : null;
            var stage = new SimStage(Vector3.Zero, 0f, new Vector3(0f, 0f, 500f));
            var before = SimulationPlanner.Plan(def, a, stage);
            var after = SimulationPlanner.Plan(def, a, stage, spanOf: SpanOf);
            var spans = a.CasterSystems.Concat(a.AimSystems ?? Array.Empty<SimSystemRef>()).Concat(a.TargetSystems)
                .Select(r => SpanOf(r)).Where(x => x is not null).Select(x => x!.Value)
                .Select(x => $"{x.Name}={(x.Endless ? "endless" : x.Seconds.ToString("0.##", I))}");
            var rel = after.Find(SimStepKind.Release);
            output.WriteLine($"{champ} {key}: before {before.EndTime.ToString("0.##", I)} s, after {after.EndTime.ToString("0.##", I)} s, duration {a.DurationField}={a.DurationSeconds?.ToString("0.##", I)}"
                + (rel is null ? "" : $", release {rel.Time.ToString("0.##", I)} [{rel.Source}]") + $" | {string.Join("; ", spans)}"
                + $" | dv: {string.Join(",", ChampionSpellData.Read(inputs.RecordBin!, champ).Where(s => s.Slot == key).SelectMany(s => s.DataValues).Where(v => v.Key.Contains("uration", StringComparison.OrdinalIgnoreCase)).Select(v => v.Key + "=" + v.Value.ToString("0.##", I)))}");
        }
    }
}
