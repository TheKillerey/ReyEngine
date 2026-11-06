using System.Numerics;
using ReyEngine.Core;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters.Simulation;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M869: the simulation planner on REAL champion definitions, generated from the installed game's champion WADs (skipped with a
/// message when the install is missing). Every expectation was read off the generated data before it was asserted.
/// </summary>
public sealed class SimulationPlannerTests(ITestOutputHelper output)
{
    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private static readonly string Champions = Path.Combine(Game, "DATA", "FINAL", "Champions");
    private static bool Installed => Directory.Exists(Champions);

    private static readonly Lazy<(HashDatabase Db, WadPathResolver Resolver)> Hashes = new(() =>
    {
        var db = new HashSyncService().LoadLocal(_ => { });
        return (db, new WadPathResolver(db));
    });

    private static readonly Dictionary<string, ChampionSimDefinition> Cache = new();

    private static ChampionSimDefinition Def(string champion)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(champion, out var d)) return d;
            var context = new ChampionSimContext
            {
                GameVersion = ChampionSimSource.ReadGameVersion(Game),
                ResolveBinName = h => Hashes.Value.Db.TryGetBinName(h, out var n) ? n : null,
                ResolveWadPath = h => Hashes.Value.Db.TryGetPath(h, out var p) ? p : null,
            };
            using var wad = WadArchive.Open(ChampionSimBatch.WadPathOf(Champions, champion), Hashes.Value.Resolver);
            return Cache[champion] = ChampionSimGenerator.Generate(wad, champion, context);
        }
    }

    private bool Skip()
    {
        if (Installed) return false;
        output.WriteLine("SKIPPED: League of Legends is not installed at " + Game);
        return true;
    }

    /// <summary>Champion at the origin facing +Z, the enemy dummy 500 units ahead: the shape of the Map11 stage.</summary>
    private static SimStage Stage() => new(Vector3.Zero, 0f, new Vector3(0f, 0f, 500f));

    private static AbilitySim Ab(ChampionSimDefinition d, string key) => Assert.Single(d.Abilities, a => a.Key == key);

    private static string Describe(SimScript s) =>
        string.Join("|", s.Steps.Select(x => $"{x.Kind}:{x.Time:R}:{x.EndTime:R}:{x.Source}:{x.Text}")) + $"#{s.EnemyPosition}#{s.AllyPosition}#{s.EndTime:R}";

    [Fact]
    public void Ezreal_Q_flies_a_direction_missile_and_hits_at_cast_plus_flight()
    {
        if (Skip()) return;
        var d = Def("Ezreal");
        var q = Ab(d, "Q");
        var script = SimulationPlanner.Plan(d, q, Stage());

        Assert.Equal(new[] { SimStepKind.Place, SimStepKind.Clip, SimStepKind.Composite, SimStepKind.Hit, SimStepKind.End },
            script.Steps.Select(s => s.Kind));
        var hit = script.Find(SimStepKind.Hit)!;
        // authored: cast 0.066 s, missile 2000 units/s, the enemy 500 away (the range is 1200, so the stage stays as it is)
        Assert.Equal(q.CastSeconds!.Value + 500f / 2000f, hit.Time, 3);
        Assert.Equal("authored", hit.Source);
        Assert.Equal("enemy", hit.Name);
        Assert.Equal(new Vector3(0f, 0f, 500f), script.EnemyPosition);
        Assert.Null(script.AllyPosition);
        Assert.Equal(SimAimTarget.Enemy, script.AimAt);
        Assert.Empty(script.All(SimStepKind.Displace));                     // no tag, no movement
        Assert.Equal(hit.Time + 1f, script.EndTime, 3);                      // last hit + the 1 s linger
        Assert.Equal(script.EndTime, script.Find(SimStepKind.End)!.Time);
        Assert.Equal("Spell1", script.Find(SimStepKind.Clip)!.Name);
        Assert.Equal("Q", script.Find(SimStepKind.Composite)!.Name);
        Assert.All(script.Steps, s => Assert.Contains(s.Source, new[] { "authored", "convention", "adapter" }));
    }

    [Fact]
    public void Ezreal_E_is_a_blink_at_the_cast_and_nothing_else_moves_him()
    {
        if (Skip()) return;
        var d = Def("Ezreal");
        var e = Ab(d, "E");
        Assert.Contains(SimulationPlanner.TeleportTag, e.Tags);
        var script = SimulationPlanner.Plan(d, e, Stage());

        var blink = Assert.Single(script.All(SimStepKind.Displace));
        Assert.Equal(SimDisplaceKind.Blink, blink.Displacement);
        Assert.Equal(e.CastSeconds!.Value, blink.Time, 3);
        Assert.Equal(blink.Time, blink.EndTime, 3);                          // instant
        // the authored display range is 475 and the enemy is 500 away: min(475, 500 - 150) = 350 toward the aim
        Assert.Equal(350f, Vector3.Distance(blink.From!.Value, blink.To!.Value), 1);
        Assert.Equal(new Vector3(0f, 0f, 350f), blink.To!.Value);
    }

    [Fact]
    public void Ahri_R_dashes_and_marks_the_timing_a_convention_when_the_clip_ends_before_the_cast()
    {
        if (Skip()) return;
        var d = Def("Ahri");
        var r = Ab(d, "R");
        var script = SimulationPlanner.Plan(d, r, Stage());

        var dash = Assert.Single(script.All(SimStepKind.Displace));
        Assert.Equal(SimDisplaceKind.Dash, dash.Displacement);
        Assert.Equal(r.CastSeconds!.Value, dash.Time, 3);
        // the authored clip (0.433 s) ends before the 0.5 s cast and no total time is authored: t0 + 0.25, said so
        Assert.Equal(dash.Time + SimulationPlanner.ConventionDashSeconds, dash.EndTime, 3);
        Assert.Equal("convention", dash.Source);
        Assert.Equal(350f, Vector3.Distance(dash.From!.Value, dash.To!.Value), 1);
        Assert.True(script.EndTime >= dash.EndTime + SimulationPlanner.DisplaceLinger - 1e-3f);
    }

    [Fact]
    public void Kalista_Q_E_R_have_hits_and_no_displacement()
    {
        if (Skip()) return;
        var d = Def("Kalista");
        foreach (var key in new[] { "Q", "E", "R" })
        {
            var a = Ab(d, key);
            var script = SimulationPlanner.Plan(d, a, Stage());
            Assert.Empty(script.All(SimStepKind.Displace));
            var hit = Assert.Single(script.All(SimStepKind.Hit));
            Assert.True(hit.Time >= a.CastSeconds!.Value, $"{key}: hit {hit.Time} before cast {a.CastSeconds}");
            Assert.Equal(SimStepKind.End, script.Steps[^1].Kind);
            output.WriteLine($"Kalista {key} {a.TargetingKind}: " + string.Join(" / ", script.Steps.Select(s => $"{s.Time:0.###} {s.Kind} ({s.Source}) {s.Text}")));
        }
        var q = SimulationPlanner.Plan(d, Ab(d, "Q"), Stage());
        Assert.Equal("authored", q.Find(SimStepKind.Hit)!.Source);
        // SelfAoe E: no aim point of its own
        Assert.Equal(SimAimTarget.Caster, SimulationPlanner.Plan(d, Ab(d, "E"), Stage()).AimAt);
    }

    [Fact]
    public void Aatrox_Q_is_a_wrapper_and_its_sub_casts_each_hit_at_the_cast()
    {
        if (Skip()) return;
        var d = Def("Aatrox");
        var wrapper = SimulationPlanner.Plan(d, Ab(d, "Q"), Stage());
        Assert.Equal(new[] { SimStepKind.Place, SimStepKind.Composite, SimStepKind.End }, wrapper.Steps.Select(s => s.Kind));

        for (int n = 1; n <= 3; n++)
        {
            var sub = Ab(d, "Q" + n);
            Assert.Equal("Q", sub.ParentKey);
            var script = SimulationPlanner.Plan(d, sub, Stage());
            Assert.Equal(new[] { SimStepKind.Place, SimStepKind.Clip, SimStepKind.Composite, SimStepKind.Hit, SimStepKind.End },
                script.Steps.Select(s => s.Kind));
            var hit = script.Find(SimStepKind.Hit)!;
            Assert.Equal(0f, hit.Time);                                      // no authored cast frame: at the start, as a convention
            Assert.Equal("convention", hit.Source);
            Assert.Equal("Q" + n, script.Find(SimStepKind.Composite)!.Name);
        }
    }

    [Fact]
    public void A_self_buff_has_no_hit_no_displacement_and_aims_at_the_caster()
    {
        if (Skip()) return;
        AbilitySim? found = null; ChampionSimDefinition? foundIn = null;
        foreach (var champion in new[] { "Garen", "Ezreal", "Ahri", "Annie", "Aatrox", "Kalista" })
        {
            var d = Def(champion);
            found = d.Abilities.FirstOrDefault(a => a.Key != "P" && a.TargetingKind == "Self" && a.Missiles.Count == 0
                && !a.Tags.Contains(SimulationPlanner.DashTag) && !a.Tags.Contains(SimulationPlanner.TeleportTag));
            if (found is not null) { foundIn = d; break; }
        }
        Assert.NotNull(found);
        output.WriteLine($"{foundIn!.Champion} {found!.Key} ({found.AbilityName})");
        var script = SimulationPlanner.Plan(foundIn, found, Stage());
        Assert.Equal(SimAimTarget.Caster, script.AimAt);
        Assert.Empty(script.All(SimStepKind.Hit));
        Assert.Empty(script.All(SimStepKind.Displace));
        Assert.Null(script.AllyPosition);
        Assert.Equal(SimStepKind.End, script.Steps[^1].Kind);
    }

    [Fact]
    public void The_passive_ends_after_three_seconds_and_aims_at_the_caster()
    {
        if (Skip()) return;
        var d = Def("Ezreal");
        var script = SimulationPlanner.Plan(d, Ab(d, "P"), Stage());
        Assert.Equal(SimulationPlanner.PassiveSeconds, script.EndTime);
        Assert.Equal(SimAimTarget.Caster, script.AimAt);
        Assert.Empty(script.All(SimStepKind.Hit));
    }

    [Fact]
    public void A_short_range_pulls_the_enemy_in_along_the_facing_and_a_long_one_leaves_it()
    {
        if (Skip()) return;
        var d = Def("Ezreal");
        foreach (var a in d.Abilities.Where(x => x.Key != "P"))
        {
            var script = SimulationPlanner.Plan(d, a, Stage());
            float expected = 500f;
            if (a.CastRange > 0f && a.CastRange < SimulationPlanner.UnboundedRange && a.CastRange < 550f)
                expected = Math.Clamp(a.CastRange * 0.8f, 250f, 900f);
            Assert.Equal(expected, Vector3.Distance(Vector3.Zero, script.EnemyPosition), 1);
        }
        // a made-up 200-unit range: 0.8 * 200 = 160, raised to the 250 minimum
        var melee = Ab(d, "Q") with { CastRange = 200f };
        Assert.Equal(250f, Vector3.Distance(Vector3.Zero, SimulationPlanner.Plan(d, melee, Stage()).EnemyPosition), 1);
        // the ground height function lifts the enemy
        var lifted = SimulationPlanner.Plan(d, Ab(d, "Q"), Stage() with { GroundHeight = _ => 42f });
        Assert.Equal(42f, lifted.EnemyPosition.Y);
    }

    [Fact]
    public void The_ally_rule_selects_a_known_set_of_abilities_across_the_roster()
    {
        string? dir = Directory.Exists(Path.Combine(ReyPaths.DataRoot, "champion_sims"))
            ? Directory.EnumerateDirectories(Path.Combine(ReyPaths.DataRoot, "champion_sims")).OrderBy(x => x, StringComparer.Ordinal).LastOrDefault()
            : null;
        if (dir is null) { output.WriteLine("SKIPPED: no generated definitions under " + ReyPaths.DataRoot); return; }

        var picked = new List<string>();
        int defs = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Where(f => !Path.GetFileName(f).StartsWith('_')).OrderBy(f => f, StringComparer.Ordinal))
        {
            var d = ChampionSimJson.Read(File.ReadAllBytes(file));
            defs++;
            foreach (var a in d.Abilities.Where(SimulationPlanner.NeedsAlly))
            {
                var script = SimulationPlanner.Plan(d, a, Stage());
                Assert.NotNull(script.AllyPosition);
                Assert.Equal(SimAimTarget.Ally, script.AimAt);
                Assert.Equal(250f, Vector3.Distance(Vector3.Zero, script.AllyPosition!.Value), 1);
                picked.Add($"{d.Champion} {a.Key} ({a.TargetingKind}; {a.DisplayName})");
            }
        }
        output.WriteLine($"{defs} definitions, {picked.Count} ability/abilities need an ally:");
        foreach (var p in picked) output.WriteLine("  " + p);
        Assert.True(defs >= 150);
        Assert.Equal(12, picked.Count);   // measured on 16.22.8348910
    }

    [Fact]
    public void Planning_is_deterministic()
    {
        if (Skip()) return;
        foreach (var champion in new[] { "Ezreal", "Ahri", "Kalista", "Aatrox" })
        {
            var d = Def(champion);
            foreach (var a in d.Abilities)
                Assert.Equal(Describe(SimulationPlanner.Plan(d, a, Stage())), Describe(SimulationPlanner.Plan(d, a, Stage())));
        }
    }

    [Fact]
    public void Every_ability_of_a_few_champions_plans_within_the_cap_and_ends_last()
    {
        if (Skip()) return;
        foreach (var champion in new[] { "Ezreal", "Ahri", "Kalista", "Aatrox", "Talon" })
        {
            var d = Def(champion);
            foreach (var a in d.Abilities)
            {
                var s = SimulationPlanner.Plan(d, a, Stage());
                Assert.InRange(s.EndTime, 0.5f, SimulationPlanner.MaxSeconds);
                Assert.Equal(SimStepKind.End, s.Steps[^1].Kind);
                Assert.Equal(SimStepKind.Place, s.Steps[0].Kind);
                Assert.True(s.Steps.Zip(s.Steps.Skip(1)).All(p => p.First.Time <= p.Second.Time), $"{champion} {a.Key}: steps out of order");
            }
        }
    }

    [Fact]
    public void An_adapter_for_the_champion_may_rewrite_the_script_and_others_are_ignored()
    {
        if (Skip()) return;
        var d = Def("Ezreal");
        var q = Ab(d, "Q");
        var mine = new Tag("Ezreal"); var other = new Tag("Kalista");
        var script = SimulationPlanner.Plan(d, q, Stage(), new ISimAdapter[] { other, mine });
        Assert.Equal("adapter", script.Steps[^1].Source);
        Assert.Equal(1, mine.Calls);
        Assert.Equal(0, other.Calls);
        Assert.Empty(SimulationPlanner.Adapters);   // none are registered yet
    }

    private sealed class Tag(string champion) : ISimAdapter
    {
        public int Calls;
        public string Champion { get; } = champion;
        public SimScript Adapt(ChampionSimDefinition definition, AbilitySim ability, SimStage stage, SimScript script)
        {
            Calls++;
            var steps = script.Steps.Concat(new[] { new SimStep(SimStepKind.End, script.EndTime, "adapter", "adapter end") { EndTime = script.EndTime } }).ToList();
            return script with { Steps = steps };
        }
    }

    // ===================================================== M869 review fixes

    [Fact]
    public void StopsFirstHit_ends_the_missile_at_the_enemy_and_piercing_does_not()
    {
        if (Skip()) return;
        var d = Def("Ezreal");
        var q = Ab(d, "Q");
        Assert.Contains(SimulationPlanner.StopsFirstHitTag, q.Tags);
        var hit = SimulationPlanner.Plan(d, q, Stage()).Find(SimStepKind.Hit)!;
        Assert.True(hit.StopsMissile);
        Assert.Contains("stops", hit.Text);

        var pierce = SimulationPlanner.Plan(d, q with { Tags = q.Tags.Concat(new[] { SimulationPlanner.PiercingTag }).ToList() }, Stage()).Find(SimStepKind.Hit)!;
        Assert.False(pierce.StopsMissile);
        Assert.Equal(hit.Time, pierce.Time);                                  // the hit time is the same either way

        // real data: Kalista Q carries both tags, so it flies on
        var k = Def("Kalista");
        Assert.False(SimulationPlanner.Plan(k, Ab(k, "Q"), Stage()).Find(SimStepKind.Hit)!.StopsMissile);
        var untagged = q with { Tags = q.Tags.Where(t => t != SimulationPlanner.StopsFirstHitTag).ToList() };
        Assert.False(SimulationPlanner.Plan(d, untagged, Stage()).Find(SimStepKind.Hit)!.StopsMissile);
    }

    [Fact]
    public void A_dash_and_a_blink_stop_at_the_last_walkable_ground()
    {
        if (Skip()) return;
        var wall = Stage() with { Walkable = p => p.Z <= 200f };              // a wall 200 units ahead
        var a = Def("Ahri");
        var dash = SimulationPlanner.Plan(a, Ab(a, "R"), wall).Find(SimStepKind.Displace)!;
        Assert.Equal(200f, dash.To!.Value.Z, 1);
        Assert.Contains("clamped", dash.Text);

        var e = Def("Ezreal");
        var blink = SimulationPlanner.Plan(e, Ab(e, "E"), wall);
        Assert.Equal(200f, blink.Find(SimStepKind.Displace)!.To!.Value.Z, 1);
        Assert.Contains(blink.Notes, n => n.Contains("walkable"));

        var shut = SimulationPlanner.Plan(a, Ab(a, "R"), Stage() with { Walkable = _ => false });
        Assert.Empty(shut.All(SimStepKind.Displace));
        Assert.Equal(350f, SimulationPlanner.Plan(a, Ab(a, "R"), Stage() with { Walkable = _ => true }).Find(SimStepKind.Displace)!.To!.Value.Z, 1);
    }

    [Fact]
    public void A_nav_grid_decides_walkability_when_no_predicate_is_given()
    {
        if (Skip()) return;
        // 40x40 cells of 25 units from z = 0, the cells from z = 200 on blocked
        const int n = 40;
        var flags = new ushort[n * n];
        for (int z = 0; z < n; z++) for (int x = 0; x < n; x++) flags[z * n + x] = z * 25 >= 200 ? ReyEngine.Formats.MapGeo.NavGrid.BlockedFlag : (ushort)0;
        var nav = ReyEngine.Formats.MapGeo.NavGrid.CreateForTests(new Vector3(-500f, 0f, 0f), 25f, n, n, flags, null);
        var a = Def("Ahri");
        var dash = SimulationPlanner.Plan(a, Ab(a, "R"), Stage() with { Nav = nav }).Find(SimStepKind.Displace)!;
        Assert.InRange(dash.To!.Value.Z, 150f, 200f);
        Assert.Contains("clamped", dash.Text);
    }

    [Fact]
    public void A_passive_with_no_visual_of_its_own_is_incomplete_not_blocked()
    {
        if (Skip()) return;
        var d = Def("Ezreal");
        var p = Ab(d, "P");
        Assert.True(p.Clip is null && p.CasterSystems.Count == 0);            // the measured shape of Ezreal's passive
        var verdict = ChampionSimValidator.Validate(p);
        Assert.Equal(SimStatus.Incomplete, verdict.Status);
        Assert.Contains(verdict.Reasons, r => r.Contains("no visual of its own in the data"));
        Assert.Contains(verdict.Checks.Single(c => c.Name == "visible").Codes!, c => c == "visible.passiveNone");

        // a non-passive with nothing to show is still Blocked
        var blocked = ChampionSimValidator.Validate(Ab(d, "Q") with { Clip = null, AuthoredClipName = "Spell9", CasterSystems = Array.Empty<SimSystemRef>(),
            TargetSystems = Array.Empty<SimSystemRef>(), Missiles = Array.Empty<MissileSim>(), ClipParticleEvents = Array.Empty<ClipParticleEventSim>() });
        Assert.Equal(SimStatus.Blocked, blocked.Status);
    }
}
