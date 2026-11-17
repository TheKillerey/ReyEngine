using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters.Simulation;
using ReyEngine.Formats.Characters.Simulation.Adapters;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M874: the ally rule by mAffectsTypeFlags, the Kalista and Talon adapter scripts, and the check of the scripted mechanics. The scripts are pure and deterministic: they are
/// planned here from the installed champion WADs (skipped with a message when the install is missing), with the stage the player uses (champion at the origin, the enemy 500 ahead).
/// </summary>
public sealed class AdapterTests(ITestOutputHelper output)
{
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

    private static readonly Dictionary<string, ChampionSimDefinition> Defs = new();

    private static ChampionSimDefinition Def(string champion)
    {
        lock (Defs)
        {
            if (Defs.TryGetValue(champion, out var d)) return d;
            var ctx = new ChampionSimContext
            {
                GameVersion = "test",
                ResolveBinName = h => Hashes.Value.Db.TryGetBinName(h, out var n) ? n : null,
                ResolveWadPath = h => Hashes.Value.Db.TryGetPath(h, out var p) ? p : null,
            };
            using var wad = WadArchive.Open(ChampionSimBatch.WadPathOf(Champions, champion), Hashes.Value.Resolver);
            return Defs[champion] = ChampionSimGenerator.Generate(wad, champion, ctx);
        }
    }

    private static AbilitySim Ab(ChampionSimDefinition d, string key) => Assert.Single(d.Abilities, a => a.Key == key);
    private static SimStage Stage(Func<Vector3, bool>? walkable = null) => new(Vector3.Zero, 0f, new Vector3(0f, 0f, 500f), Walkable: walkable);
    private static SimScript Plan(ChampionSimDefinition d, string key, SimStage? stage = null) => SimulationPlanner.Plan(d, Ab(d, key), stage ?? Stage());
    private static IEnumerable<SimStep> Of(SimScript s, SimStepKind k) => s.Steps.Where(x => x.Kind == k);

    // ===================================================== the ally rule

    private static AbilitySim Synthetic(uint? flags, string kind = "Target", params string[] tags) =>
        new("Q", null, 0, "XQ", "XQ", "XQ", null, null, Array.Empty<string>(), null, null, kind, 600, 0, 0, tags, flags,
            null, null, 0.25f, 1f, null, Array.Empty<MissileSim>(), Array.Empty<SimSystemRef>(), Array.Empty<SimSystemRef>(), Array.Empty<SimSystemRef>(),
            Array.Empty<ClipParticleEventSim>(), Array.Empty<ClipSoundEventSim>(), new ScenarioHint(false, false, false, null), Array.Empty<FieldSource>(), Array.Empty<string>());

    [Theory]
    [InlineData(1u, true)]       // Annie E, Kayle W, Milio W: the friendly bit alone
    [InlineData(1029u, true)]    // Bard W
    [InlineData(1045u, true)]    // LeeSin W
    [InlineData(16385u, true)]   // Galio R, Rakan E
    [InlineData(17413u, true)]   // KSante E
    [InlineData(6154u, false)]   // Vladimir Q, Riven E: no friendly bit
    [InlineData(24575u, false)]  // Senna Q: both bits
    [InlineData(16387u, false)]  // TahmKench R: both bits
    public void The_ally_rule_reads_the_friendly_and_enemy_bits_of_mAffectsTypeFlags(uint flags, bool ally)
    {
        // INFERRED bit meanings (friendly 0x1, enemy 0x2), measured over the twelve tag-rule abilities: see SimulationPlanner.NeedsAlly
        Assert.Equal(ally, SimulationPlanner.NeedsAlly(Synthetic(flags, "Target", "PositiveEffect_Boon")));
    }

    [Fact]
    public void The_ally_rule_needs_a_tag_a_unit_target_and_flags()
    {
        Assert.False(SimulationPlanner.NeedsAlly(Synthetic(1u, "Target")));                                   // no helpful tag
        Assert.False(SimulationPlanner.NeedsAlly(Synthetic(1u, "Location", "Trait_Shield")));                 // not unit-targeted
        Assert.False(SimulationPlanner.NeedsAlly(Synthetic(null, "Target", "Trait_ActiveHeal")));             // no flags: no evidence
        Assert.True(SimulationPlanner.NeedsAlly(Synthetic(1u, "TargetOrLocation", "Trait_Shield")));
    }

    [Fact]
    public void Across_the_roster_the_rule_picks_the_eight_ally_abilities_and_drops_the_four_that_also_hit_enemies()
    {
        if (Skip()) return;
        var picked = new List<string>();
        foreach (var champion in new[] { "Annie", "Bard", "Galio", "KSante", "Kayle", "LeeSin", "Milio", "Rakan", "Riven", "Senna", "TahmKench", "Vladimir" })
            foreach (var a in Def(champion).Abilities.Where(a => a.ParentKey is null))
                if (SimulationPlanner.NeedsAlly(a)) picked.Add($"{champion} {a.Key}");
        output.WriteLine(string.Join(", ", picked));
        Assert.Equal(new[] { "Annie E", "Bard W", "Galio R", "KSante E", "Kayle W", "LeeSin W", "Milio W", "Rakan E" }, picked.OrderBy(x => x, StringComparer.Ordinal));
    }

    // ===================================================== the adapters

    [Fact]
    public void Adapters_are_registered_for_Brand_Kalista_and_Talon_only()
    {
        Assert.Equal(new[] { "Brand", "Kalista", "Talon" }, SimulationPlanner.Adapters.Select(a => a.Champion).OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_value_that_is_not_data_is_labelled_adapter_with_its_reason_in_all_the_scripts()
    {
        if (Skip()) return;
        foreach (var (champion, keys) in new[] { ("Kalista", new[] { "P", "W", "E", "R" }), ("Talon", new[] { "P", "Q", "E" }) })
        {
            var d = Def(champion);
            foreach (var key in keys)
            {
                var s = Plan(d, key, Stage(p => !(p.Z < -559f && p.Z > -760f)));
                foreach (var st in s.Steps.Where(x => x.Kind == SimStepKind.Place || x is { Kind: SimStepKind.Hit, Source: "adapter" }))
                    Assert.False(string.IsNullOrWhiteSpace(st.Why), $"{champion} {key} {st.Kind}: no reason");
                foreach (var st in s.Steps.Where(x => SimCheckAdapterSteps.IsAdapterStep(x) && x.Kind != SimStepKind.Attack))
                {
                    Assert.Equal("adapter", st.Source);
                    Assert.False(string.IsNullOrWhiteSpace(st.Why), $"{champion} {key} {st.Kind}: no reason");
                }
                foreach (var attack in Of(s, SimStepKind.Attack))
                {
                    Assert.Equal("authored", attack.Source);                    // the clip, cast point and missile are the record's
                    Assert.False(string.IsNullOrWhiteSpace(attack.Why));        // the decision to attack is the adapter's
                    Assert.NotNull(attack.Clip);
                }
                Assert.Equal(SimStepKind.End, s.Steps[^1].Kind);
                Assert.True(s.EndTime <= SimulationPlanner.MaxSeconds);
                Assert.True(s.Steps.Zip(s.Steps.Skip(1)).All(p => p.First.Time <= p.Second.Time), $"{champion} {key}: out of order");
            }
        }
    }

    [Fact]
    public void Kalista_P_attacks_then_hops_twice_to_alternating_sides_by_300_units()
    {
        if (Skip()) return;
        var s = Plan(Def("Kalista"), "P");
        Assert.Equal(2, Of(s, SimStepKind.Attack).Count());
        var hops = s.Steps.Where(x => x.Kind == SimStepKind.Displace).ToList();
        Assert.Equal(2, hops.Count);
        Assert.All(hops, h => { Assert.Equal(SimDisplaceKind.Jump, h.Displacement); Assert.Equal(300f, Vector3.Distance(h.From!.Value, h.To!.Value), 1); Assert.Contains("castRangeDisplayOverride", h.Why); Assert.NotNull(h.Clip); });
        Assert.True(hops[0].To!.Value.X * hops[1].To!.Value.X <= 0f || Math.Sign(hops[0].To!.Value.X - hops[0].From!.Value.X) != Math.Sign(hops[1].To!.Value.X - hops[1].From!.Value.X), "the hops go to opposite sides");
        // each hop starts at its attack's cast point: after the attack began, before it ends
        var attacks = Of(s, SimStepKind.Attack).ToList();
        Assert.InRange(hops[0].Time, attacks[0].Time, attacks[0].EndTime);
        Assert.True(attacks[1].Time >= hops[0].EndTime);
        Assert.Equal(2, Of(s, SimStepKind.Hit).Count());
        Assert.Contains(Of(s, SimStepKind.Effect), e => e.Resource!.Contains("Blade_Hero", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Kalista_W_spawns_the_Sentinel_at_the_cast_walks_at_560_patrols_and_it_is_removed()
    {
        if (Skip()) return;
        var d = Def("Kalista");
        var w = Ab(d, "W");
        var s = Plan(d, "W");
        var spawn = Assert.Single(Of(s, SimStepKind.Spawn));
        Assert.Equal(w.CastSeconds!.Value, spawn.Time, 3);
        Assert.Equal("KalistaSpawn", spawn.Resource);
        Assert.Equal(560f, spawn.Speed);
        Assert.True(spawn.Loop && spawn.Path!.Count == 3, "a patrol around the target");
        Assert.Contains("baseMoveSpeed 560", spawn.Why);
        Assert.Contains("conventions", spawn.Why);
        // the sentinel's systems ride the unit, not Kalista
        var composite = Assert.Single(Of(s, SimStepKind.Composite));
        Assert.Contains("Kalista_Base_W_Avatar", composite.Exclude!);
        var fx = Of(s, SimStepKind.Effect).ToList();
        Assert.Equal(new[] { "Kalista_Base_W_Avatar", "Kalista_Base_W_Glow" }, fx.Select(f => f.Resource).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(fx, f => { Assert.Equal("sentinel", f.Unit); Assert.Contains("root", f.Why); });
        var despawn = Assert.Single(Of(s, SimStepKind.Despawn));
        Assert.True(despawn.Time > spawn.Time && despawn.Time < s.EndTime);
        Assert.InRange(s.EndTime, 5.5f, 6.5f);                                              // out, two patrol loops and a moment more: it visibly patrols
        Assert.True(despawn.Time - spawn.Time - Vector3.Distance(spawn.From!.Value, spawn.To!.Value) / 560f >= 2 * (spawn.Path!.Zip(spawn.Path.Skip(1).Append(spawn.Path[0]), Vector3.Distance).Sum() / 560f) - 0.01f);
        // about 600 ahead, beside the enemy
        Assert.InRange(spawn.To!.Value.Z, 500f, 700f);
        Assert.InRange(Math.Abs(spawn.To!.Value.X), 100f, 300f);
    }

    [Fact]
    public void Kalista_E_stacks_four_spears_with_three_attacks_and_Q_then_rends_and_removes_the_markers()
    {
        if (Skip()) return;
        var s = Plan(Def("Kalista"), "E");
        Assert.Equal(3, Of(s, SimStepKind.Attack).Count());
        var composites = Of(s, SimStepKind.Composite).ToList();
        Assert.Equal(new[] { "Q", "E" }, composites.Select(c => c.Name));
        var markers = Of(s, SimStepKind.Effect).Where(e => e.Resource!.Contains("Spear_tar", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Time).ToList();
        Assert.Equal(new[] { "Kalista_Base_E_Spear_tar1", "Kalista_Base_E_Spear_tar2", "Kalista_Base_E_Spear_tar3", "Kalista_Base_E_Spear_tar4" }, markers.Select(m => m.Resource));
        Assert.All(markers, m => { Assert.Equal("enemy", m.Unit); Assert.Contains("inferred", m.Why); Assert.Equal(composites[1].Time, m.EndTime, 3); });   // removed at the rend
        // markers appear on the hits, in order: three attacks then Q
        var hits = Of(s, SimStepKind.Hit).OrderBy(h => h.Time).ToList();
        for (int i = 0; i < 4; i++) Assert.Equal(hits[i].Time, markers[i].Time, 3);
        // the rend plays out4 (the stack count) and not the numbered systems that belong to other counts or to the target
        var rend = composites[1];
        Assert.Contains("Kalista_Base_E_Spear_out3", rend.Exclude!);
        Assert.DoesNotContain("Kalista_Base_E_Spear_out4", rend.Exclude!);
        Assert.Contains("Kalista_Base_E_Spear_tar1", rend.Exclude!);
    }

    [Fact]
    public void Kalista_R_pulls_hides_holds_flings_and_knocks_up_in_that_order_and_the_ally_ends_beside_the_enemy()
    {
        if (Skip()) return;
        var d = Def("Kalista");
        var s = Plan(d, "R");
        Assert.NotNull(s.AllyPosition);
        var order = s.Steps.Where(x => x.Kind is SimStepKind.MoveUnit or SimStepKind.Hide or SimStepKind.Show).Select(x => x.Kind == SimStepKind.MoveUnit ? x.Move!.Value.ToString() : x.Kind.ToString()).ToList();
        Assert.Equal(new[] { "Pull", "Hide", "Show", "Fling", "Knockup" }, order);
        var pull = Of(s, SimStepKind.MoveUnit).First(m => m.Move == SimMoveKind.Pull);
        Assert.Equal(700f, pull.Speed);
        Assert.Equal(Ab(d, "R").CastSeconds!.Value, pull.Time, 3);
        Assert.Contains("KalistaRAlly FixedSpeed", pull.Why);
        var fling = Of(s, SimStepKind.MoveUnit).First(m => m.Move == SimMoveKind.Fling);
        Assert.Equal(700f, fling.Speed);
        Assert.Contains("convention", fling.Why);
        Assert.InRange(Vector3.Distance(fling.To!.Value, s.EnemyPosition), 100f, 200f);   // beside the enemy
        var knock = Of(s, SimStepKind.MoveUnit).First(m => m.Move == SimMoveKind.Knockup);
        Assert.Equal("enemy", knock.Unit);
        Assert.Equal(1.0f, knock.EndTime - knock.Time, 3);
        Assert.Equal(knock.From, knock.To);                                                // back to the same spot
        Assert.True(knock.Height >= 300f, "high enough to read at the stage camera");
        Assert.True(Of(s, SimStepKind.Show).Single().Time - Of(s, SimStepKind.Hide).Single().Time >= 0.99f);   // the carry
        var hit = Assert.Single(Of(s, SimStepKind.Hit));
        Assert.Equal(fling.EndTime, hit.Time, 3);
        Assert.Contains(Of(s, SimStepKind.Effect), e => e.Resource == "Kalista_Base_R_Ally_Vanish_Cone");
    }

    [Fact]
    public void Kalista_Q_keeps_the_generic_plan()
    {
        if (Skip()) return;
        var d = Def("Kalista");
        var withAdapter = SimulationPlanner.Plan(d, Ab(d, "Q"), Stage());
        var generic = SimulationPlanner.Plan(d, Ab(d, "Q"), Stage(), Array.Empty<ISimAdapter>());
        Assert.Equal(generic.Steps.Select(x => (x.Kind, x.Time)), withAdapter.Steps.Select(x => (x.Kind, x.Time)));
    }

    [Fact]
    public void Talon_Q_leaps_at_2000_from_beyond_melee_range_then_strikes_and_Q1_Q2_stand_inside_melee_range()
    {
        if (Skip()) return;
        var d = Def("Talon");
        var q = Plan(d, "Q");
        var leap = Assert.Single(q.Steps, x => x.Kind == SimStepKind.Displace);
        Assert.Equal(SimDisplaceKind.Jump, leap.Displacement);
        float distance = Vector3.Distance(leap.From!.Value, leap.To!.Value);
        Assert.Equal(distance / 2000f, leap.EndTime - leap.Time, 3);
        Assert.Contains("FixedSpeed", leap.Why);
        Assert.InRange(Vector3.Distance(q.ChampionPosition, q.EnemyPosition), 171f, 575f);            // beyond TalonQAttack's 170, inside TalonQ's 575
        var strike = Assert.Single(Of(q, SimStepKind.Composite));
        Assert.Equal("Q1", strike.Name);
        Assert.True(strike.Time >= leap.EndTime - 1e-3f);
        Assert.Single(Of(q, SimStepKind.Hit));
        foreach (var key in new[] { "Q1", "Q2" })
            Assert.True(Vector3.Distance(Plan(d, key).ChampionPosition, Plan(d, key).EnemyPosition) < 170f, $"{key}: the enemy is inside the melee range");
    }

    [Fact]
    public void Talon_E_jumps_over_the_wall_the_navgrid_has_and_says_why_when_it_cannot()
    {
        if (Skip()) return;
        var d = Def("Talon");
        // a 200-unit wall 559 units from the stage point, as Map11's grid has south of it: found, not hard-coded
        var s = Plan(d, "E", Stage(p => !(p.Z < -559f && p.Z > -759f)));
        var jump = Assert.Single(s.Steps, x => x.Kind == SimStepKind.Displace);
        Assert.Equal(SimDisplaceKind.Jump, jump.Displacement);
        Assert.True(jump.From!.Value.Z > -559f && jump.To!.Value.Z < -759f, "from the near edge to the first walkable ground beyond");
        Assert.True(Vector3.Distance(jump.From!.Value, jump.To!.Value) <= 625f + 1f);
        Assert.Contains("WallJumpDistance 625", jump.Why);
        Assert.Contains(s.Notes, n => n.Contains("units from the stage point"));
        Assert.Equal(jump.From, s.ChampionPosition);                                        // Talon starts at the wall's foot
        Assert.Contains(Of(s, SimStepKind.Composite), c => c.Exclude!.Contains("Talon_Base_E_land"));
        Assert.Contains(Of(s, SimStepKind.Effect), e => e.Resource == "Talon_Base_E_land" && e.Time >= jump.EndTime - 1e-3f);
        // a wall too thick to cross: no jump, and the reason is on the script
        var thick = Plan(d, "E", Stage(p => !(p.Z < -559f && p.Z > -1559f)));
        Assert.Empty(thick.Steps.Where(x => x.Kind == SimStepKind.Displace));
        Assert.Contains(thick.Notes, n => n.Contains("WallJumpDistance") || n.Contains("does not end"));
        // no navgrid at all
        Assert.Contains(Plan(d, "E", Stage()).Notes, n => n.Contains("no navgrid"));
    }

    [Fact]
    public void Talon_P_stacks_on_three_ability_hits_then_a_crit_attack_bleeds_for_two_seconds()
    {
        if (Skip()) return;
        var s = Plan(Def("Talon"), "P");
        Assert.Equal(new[] { "W", "Q1", "R" }, Of(s, SimStepKind.Composite).Select(c => c.Name));
        var stacks = Of(s, SimStepKind.Effect).Where(e => e.Resource!.Contains("P_Stack_", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Time).ToList();
        Assert.Equal(new[] { "Talon_Base_P_Stack_1", "Talon_Base_P_Stack_2", "Talon_Base_P_Stack_3" }, stacks.Select(x => x.Resource));
        Assert.All(stacks, x => Assert.Contains("threshold of three is Riot's tooltip", x.Why));
        var attack = Assert.Single(Of(s, SimStepKind.Attack));
        Assert.Equal("TalonCritAttack", attack.Name);
        Assert.True(attack.Time > stacks[^1].Time);
        var bleed = Assert.Single(Of(s, SimStepKind.Effect), e => e.Resource == "Talon_Base_P_tar_tick");
        Assert.Equal(2f, bleed.EndTime - bleed.Time, 3);
        Assert.Contains("BleedDuration", bleed.Why);
        Assert.All(stacks, x => Assert.Equal(attack.Time + attack.HitAfter, x.EndTime, 3));   // the stacks end where the bleed starts
    }

    [Fact]
    public void Talon_W_and_R_keep_their_record_timing()
    {
        if (Skip()) return;
        var d = Def("Talon");
        foreach (var key in new[] { "W", "R" })
        {
            var a = SimulationPlanner.Plan(d, Ab(d, key), Stage());
            var b = SimulationPlanner.Plan(d, Ab(d, key), Stage(), Array.Empty<ISimAdapter>());
            Assert.Equal(b.Steps.Select(x => (x.Kind, x.Time)), a.Steps.Select(x => (x.Kind, x.Time)));
        }
    }

    [Fact]
    public void The_scripts_are_deterministic()
    {
        if (Skip()) return;
        string Describe(SimScript s) => string.Join("|", s.Steps.Select(x => $"{x.Kind}:{x.Time:R}:{x.EndTime:R}:{x.Source}:{x.Unit}:{x.Resource}:{x.Text}")) + $"#{s.ChampionPosition}#{s.EnemyPosition}#{s.AllyPosition}";
        foreach (var (champion, key) in new[] { ("Kalista", "P"), ("Kalista", "W"), ("Kalista", "E"), ("Kalista", "R"), ("Talon", "Q"), ("Talon", "P") })
            Assert.Equal(Describe(Plan(Def(champion), key)), Describe(Plan(Def(champion), key)));
    }

    // ===================================================== the check of the mechanics

    private static SimStep Step(SimStepKind kind, float t, float end = 0f, string? unit = null) => new(kind, t, "adapter", kind.ToString()) { Unit = unit, EndTime = end > 0f ? end : t };

    private static AdapterObservations Seen(params SimStep[] ran) => new() { Trace = ran.Select(r => new StepEvidence(r.Kind.ToString(), r.Time, r.Time)).ToList() };

    [Fact]
    public void A_spawn_passes_only_when_its_unit_is_built_and_its_box_changed()
    {
        var spawn = Step(SimStepKind.Spawn, 0.5f, unit: "sentinel");
        var plans = SimCheckAdapterSteps.Plans(new SimScript("X", "W", Vector3.Zero, 0, Vector3.Zero, null, SimAimTarget.Caster, Vector3.Zero, "", new[] { spawn }, 1f, Array.Empty<string>()));
        var ok = Seen(spawn) with { UnitHasMesh = new Dictionary<string, bool> { ["sentinel"] = true }, UnitPixelChange = new Dictionary<string, double> { ["sentinel"] = 0.2 } };
        Assert.True(Assert.Single(SimCheckAdapterSteps.Verify(plans, ok)).Passed);
        Assert.Contains("not rendered", Assert.Single(SimCheckAdapterSteps.Verify(plans, ok with { UnitPixelChange = new Dictionary<string, double> { ["sentinel"] = 0.001 } })).Detail);
        Assert.Contains("no meshes", Assert.Single(SimCheckAdapterSteps.Verify(plans, ok with { UnitHasMesh = new Dictionary<string, bool> { ["sentinel"] = false } })).Detail);
        var never = Assert.Single(SimCheckAdapterSteps.Verify(plans, new AdapterObservations()));
        Assert.False(never.Executed); Assert.False(never.Passed);
    }

    [Fact]
    public void A_move_must_reach_its_end_a_knockup_must_rise_and_a_jump_must_cross_in_an_arc()
    {
        var move = new SimStep(SimStepKind.MoveUnit, 1f, "adapter", "pull") { Unit = "ally", Move = SimMoveKind.Pull, From = new Vector3(250, 0, 0), To = Vector3.Zero, EndTime = 1.4f };
        var knock = new SimStep(SimStepKind.MoveUnit, 2f, "adapter", "knockup") { Unit = "enemy", Move = SimMoveKind.Knockup, From = new Vector3(0, 0, 500), To = new Vector3(0, 0, 500), Height = 180f, EndTime = 3f };
        var jump = new SimStep(SimStepKind.Displace, 0.5f, "adapter", "hop") { Displacement = SimDisplaceKind.Jump, From = Vector3.Zero, To = new Vector3(300, 0, 0), Height = 90f, EndTime = 0.75f };
        var script = new SimScript("X", "R", Vector3.Zero, 0, Vector3.Zero, null, SimAimTarget.Caster, Vector3.Zero, "", new[] { move, knock, jump }, 4f, Array.Empty<string>());
        var plans = SimCheckAdapterSteps.Plans(script);
        var good = Seen(move, knock, jump) with
        {
            Units = new[] { new UnitObservation(1.0, "ally", 250, 0, 0, true), new UnitObservation(1.4, "ally", 0, 0, 0, true),
                            new UnitObservation(2.0, "enemy", 0, 0, 500, true), new UnitObservation(2.5, "enemy", 0, 150, 500, true), new UnitObservation(3.0, "enemy", 0, 0, 500, true) },
            Caster = new[] { (0.5, 0f, 0f, 0f), (0.62, 150f, 80f, 0f), (0.75, 300f, 0f, 0f) },
        };
        Assert.All(SimCheckAdapterSteps.Verify(plans, good), v => Assert.True(v.Passed, v.Kind + ": " + v.Detail));
        // a unit that never moved, an enemy that stayed on the ground, a caster that slid
        var bad = Seen(move, knock, jump) with
        {
            Units = new[] { new UnitObservation(1.0, "ally", 250, 0, 0, true), new UnitObservation(1.5, "ally", 250, 0, 0, true), new UnitObservation(2.5, "enemy", 0, 0, 500, true) },
            Caster = new[] { (0.5, 0f, 0f, 0f), (0.8, 300f, 0f, 0f) },
        };
        var verdicts = SimCheckAdapterSteps.Verify(plans, bad);
        Assert.All(verdicts, v => Assert.False(v.Passed, v.Kind + ": " + v.Detail));
        Assert.Contains("never left the ground", verdicts.Single(v => v.Kind == "Displace").Detail);
    }

    [Fact]
    public void Hide_show_and_effect_steps_are_checked_against_the_recorded_state()
    {
        var hide = Step(SimStepKind.Hide, 1f, unit: "ally"); var show = Step(SimStepKind.Show, 2f, unit: "ally");
        var fx = new SimStep(SimStepKind.Effect, 0.5f, "adapter", "fx") { Resource = "Kalista_Base_R_tar", Unit = "enemy", EndTime = 1f };
        var script = new SimScript("X", "R", Vector3.Zero, 0, Vector3.Zero, null, SimAimTarget.Caster, Vector3.Zero, "", new[] { fx, hide, show }, 3f, Array.Empty<string>());
        var plans = SimCheckAdapterSteps.Plans(script);
        var ok = Seen(fx, hide, show) with
        {
            Units = new[] { new UnitObservation(1.1, "ally", 0, 0, 0, false), new UnitObservation(2.1, "ally", 0, 0, 0, true) },
            EffectsStarted = new[] { "kalista_base_r_tar" },
        };
        Assert.All(SimCheckAdapterSteps.Verify(plans, ok), v => Assert.True(v.Passed, v.Detail));
        var bad = Seen(fx, hide, show) with { Units = new[] { new UnitObservation(1.1, "ally", 0, 0, 0, true) }, EffectsStarted = Array.Empty<string>() };
        Assert.All(SimCheckAdapterSteps.Verify(plans, bad), v => Assert.False(v.Passed));
    }

    [Fact]
    public void A_failed_scripted_step_makes_the_ability_incomplete_and_a_clean_one_does_not()
    {
        var baseEvidence = new AbilityEvidence
        {
            Key = "W",
            Planned = new[] { new PlannedStep("Place", 0), new PlannedStep("End", 1) },
            Trace = new[] { new StepEvidence("Place", 0, 0), new StepEvidence("End", 1, 1) },
            EndReached = true, ExpectsEffects = true,
            Frames = new[] { new FrameEvidence("baseline", 0, 0, 0, 0, 0), new FrameEvidence("cast", 0.15, 0.15, 0.05, 0.03, 10) },
        };
        var failed = new AdapterStepEvidence("Spawn", 0.5, "Sentinel", "adapter", "why", true, false, "not rendered: 0% of its projected box changed, needs 2%");
        var passed = failed with { Passed = true, Detail = "drawn" };
        Assert.Equal(SimCheckGrade.Complete, SimCheckGrader.Grade(baseEvidence with { AdapterSteps = new[] { passed } }).Grade);
        var g = SimCheckGrader.Grade(baseEvidence with { AdapterSteps = new[] { passed, failed } });
        Assert.Equal(SimCheckGrade.Incomplete, g.Grade);
        Assert.Contains("adapter step Spawn", Assert.Single(g.Reasons));
    }

    // ---- M874 fixes: a later step cannot vouch for an earlier one; the unit's colour; Talon E's wall search ----

    [Fact]
    public void A_later_step_that_starts_where_an_earlier_one_ended_cannot_vouch_for_it()
    {
        var pull = new SimStep(SimStepKind.MoveUnit, 1f, "adapter", "pull") { Unit = "ally", Move = SimMoveKind.Pull, From = new Vector3(250, 0, 0), To = Vector3.Zero, EndTime = 1.4f };
        var script = new SimScript("X", "R", Vector3.Zero, 0, Vector3.Zero, null, SimAimTarget.Caster, Vector3.Zero, "", new[] { pull }, 4f, Array.Empty<string>());
        var plans = SimCheckAdapterSteps.Plans(script);
        // the ally never moved during the pull's span, but stands at the destination LATER (the next step put it there)
        var masked = Seen(pull) with { Units = new[] { new UnitObservation(1.0, "ally", 250, 0, 0, true), new UnitObservation(1.4, "ally", 250, 0, 0, true), new UnitObservation(3.0, "ally", 0, 0, 0, true) } };
        Assert.False(Assert.Single(SimCheckAdapterSteps.Verify(plans, masked)).Passed);

        // a jump halfway across is not a crossing; one that lands is
        var hop = new SimStep(SimStepKind.Displace, 0.5f, "adapter", "hop") { Displacement = SimDisplaceKind.Jump, From = Vector3.Zero, To = new Vector3(300, 0, 0), Height = 90f, EndTime = 0.75f };
        var hops = SimCheckAdapterSteps.Plans(new SimScript("X", "P", Vector3.Zero, 0, Vector3.Zero, null, SimAimTarget.Caster, Vector3.Zero, "", new[] { hop }, 2f, Array.Empty<string>()));
        var half = Seen(hop) with { Caster = new[] { (0.5, 0f, 0f, 0f), (0.62, 150f, 80f, 0f), (0.75, 160f, 0f, 0f), (1.2, 300f, 0f, 0f) } };
        Assert.Contains("units from the landing point", Assert.Single(SimCheckAdapterSteps.Verify(hops, half)).Detail);
        var landed = Seen(hop) with { Caster = new[] { (0.5, 0f, 0f, 0f), (0.62, 150f, 80f, 0f), (0.75, 300f, 0f, 0f) } };
        Assert.True(Assert.Single(SimCheckAdapterSteps.Verify(hops, landed)).Passed);

        // a knock-up that rose and never came back
        var knock = new SimStep(SimStepKind.MoveUnit, 2f, "adapter", "knockup") { Unit = "enemy", Move = SimMoveKind.Knockup, From = new Vector3(0, 0, 500), To = new Vector3(0, 0, 500), Height = 180f, EndTime = 3f };
        var kp = SimCheckAdapterSteps.Plans(new SimScript("X", "R", Vector3.Zero, 0, Vector3.Zero, null, SimAimTarget.Caster, Vector3.Zero, "", new[] { knock }, 4f, Array.Empty<string>()));
        var stuck = Seen(knock) with { Units = new[] { new UnitObservation(2.5, "enemy", 0, 170, 500, true), new UnitObservation(3.0, "enemy", 0, 170, 500, true) } };
        Assert.Contains("did not come back", Assert.Single(SimCheckAdapterSteps.Verify(kp, stuck)).Detail);
    }

    [Fact]
    public void A_spawned_unit_drawn_as_a_white_silhouette_is_not_rendered()
    {
        var spawn = Step(SimStepKind.Spawn, 0.5f, unit: "sentinel");
        var plans = SimCheckAdapterSteps.Plans(new SimScript("X", "W", Vector3.Zero, 0, Vector3.Zero, null, SimAimTarget.Caster, Vector3.Zero, "", new[] { spawn }, 1f, Array.Empty<string>()));
        var seen = Seen(spawn) with { UnitHasMesh = new Dictionary<string, bool> { ["sentinel"] = true }, UnitPixelChange = new Dictionary<string, double> { ["sentinel"] = 0.3 },
                                      UnitWhiteShare = new Dictionary<string, double> { ["sentinel"] = 0.95 } };
        Assert.Contains("flat white silhouette", Assert.Single(SimCheckAdapterSteps.Verify(plans, seen)).Detail);
        Assert.True(Assert.Single(SimCheckAdapterSteps.Verify(plans, seen with { UnitWhiteShare = new Dictionary<string, double> { ["sentinel"] = 0.1 } })).Passed);

        // the pixel measure: a box whose changed pixels are all white, one whose are teal, and one with nothing changed
        var baseline = new byte[10 * 10 * 4]; var white = (byte[])baseline.Clone(); var teal = (byte[])baseline.Clone();
        for (int i = 0; i < 100; i++) { white[i * 4] = white[i * 4 + 1] = white[i * 4 + 2] = 255; teal[i * 4] = 180; teal[i * 4 + 1] = 200; teal[i * 4 + 2] = 40; }
        var box = new PixelRect(0, 0, 10, 10);
        Assert.Equal(1.0, SimCheckFrames.ChangedWhiteShare(baseline, white, 10, 10, box)!.Value, 6);
        Assert.Equal(0.0, SimCheckFrames.ChangedWhiteShare(baseline, teal, 10, 10, box)!.Value, 6);
        Assert.Null(SimCheckFrames.ChangedWhiteShare(baseline, baseline, 10, 10, box));
    }

    [Fact]
    public void Talon_E_tries_the_next_wall_caps_the_relocation_and_refuses_unwalkable_ground()
    {
        if (Skip()) return;
        var d = Def("Talon");
        // standing on unwalkable ground: no wall search at all, and it says so
        var onWall = Plan(d, "E", Stage(p => false));
        Assert.Empty(onWall.Steps.Where(x => x.Kind == SimStepKind.Displace));
        Assert.Contains(onWall.Notes, n => n.Contains("unwalkable"));
        // the only wall is 1400 units away: more than the 400 the demo moves him
        var far = Plan(d, "E", Stage(p => !(p.Z < -1400f && p.Z > -1500f)));
        Assert.Empty(far.Steps.Where(x => x.Kind == SimStepKind.Displace));
        Assert.Contains(far.Notes, n => n.Contains("more than the 700"));
        // a nearer wall too thick to cross is skipped for a crossable one behind the other side
        var two = Plan(d, "E", Stage(p => !(p.Z < -300f && p.Z > -1300f) && !(p.X > 559f && p.X < 759f)));
        var jump = Assert.Single(two.Steps, x => x.Kind == SimStepKind.Displace);
        Assert.True(jump.From!.Value.X > 400f && jump.To!.Value.X > 759f, "the east wall, 200 units thick, is the one crossed");
        Assert.Contains(two.Notes, n => n.Contains("next wall"));
        // a wall thinner than ten units is found
        var thin = Plan(d, "E", Stage(p => !(p.Z < -559f && p.Z > -564f)));
        Assert.Single(thin.Steps, x => x.Kind == SimStepKind.Displace);
        // the enemy dummy's spot is on walkable ground
        var s = Plan(d, "E", Stage(p => !(p.Z < -559f && p.Z > -759f) && !(p.Z > -100f)));
        Assert.True(s.Steps.Any(x => x.Kind == SimStepKind.Displace) ? !(s.EnemyPosition.Z > -100f) : true);
    }
}
