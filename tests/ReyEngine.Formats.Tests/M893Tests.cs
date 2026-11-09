using System.Numerics;
using ReyEngine.Core;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Characters.Simulation;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M893: the spell fields the simulation never read - the target hit bone, the caster start bone, the hit-effect orient type, the missile behaviours, the height solver
/// and the start / end height fields - on the installed game's champion WADs (skipped with a message when the install is missing). Each expectation was read off the
/// record (binaudit dumps of 16.22) before it was asserted.
/// </summary>
public sealed class M893Tests(ITestOutputHelper output)
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

    private static IReadOnlyList<AbilitySlot> Slots(string champion)
    {
        using var wad = WadArchive.Open(ChampionSimBatch.WadPathOf(Champions, champion), Hashes.Value.Resolver);
        ulong hash = HashAlgorithms.WadPath(ChampionRecord.PathFor(champion.ToLowerInvariant()));
        return ChampionSpellData.Read(wad.Extract(hash), champion);
    }

    private bool Skip()
    {
        if (Installed) return false;
        output.WriteLine("SKIPPED: League of Legends is not installed at " + Game);
        return true;
    }

    private static SimStage Stage() => new(Vector3.Zero, 0f, new Vector3(0f, 0f, 500f));
    private static AbilitySim Ab(ChampionSimDefinition d, string key) => Assert.Single(d.Abilities, a => a.Key == key);

    // ===================================================== the definition reads the fields

    [Fact]
    public void Ahri_attacks_leave_from_R_hand_and_Q_has_a_return_missile()
    {
        if (Skip()) return;
        var d = Def("Ahri");
        // the census example "FixedSpeedMovement.mStartBoneName = R_hand" is Ahri's basic attack missile, not her Q
        var attack = d.Attacks.SelectMany(a => a.Missiles).Where(m => m.Flight?.StartBoneName == "R_hand").ToList();
        Assert.NotEmpty(attack);
        Assert.All(attack, m => Assert.Contains(m.FlightSources!, f => f.Group.EndsWith(".mStartBoneName") && f.Source == "authored"));

        var q = Ab(d, "Q");
        var outbound = Assert.Single(q.Missiles, m => m.ReturnSource is null);
        Assert.Equal(2500f, outbound.Speed);
        Assert.Contains("CastOnHit", outbound.Flight!.Behaviors);
        Assert.Contains("DestroyOnMovementComplete", outbound.Flight.Behaviors);

        var back = Assert.Single(q.Missiles, m => m.ReturnSource == "name" && !m.IsDeadVariant);
        Assert.Contains("Return", back.SpellName!);
        Assert.NotNull(back.Effect);
        Assert.Equal("root", back.Flight!.StartBoneName);
        Assert.Equal("AcceleratingMovement", back.Flight.MovementClass);
        // it accelerates from 60 u/s: 500 units take well under a second, not the 8 s the initial speed alone would say
        Assert.InRange(back.Flight.AcceleratingSeconds(500f)!.Value, 0.3f, 1.5f);
    }

    [Fact]
    public void Kalista_hit_effect_orient_type_is_stored_raw()
    {
        if (Skip()) return;
        // Kalista authors it on her basic attacks (2 on all five); no ability spell of hers carries it
        var d = Def("Kalista");
        var withOrient = d.Attacks.SelectMany(a => a.Missiles).Where(m => m.HitEffectOrientType is not null).ToList();
        Assert.NotEmpty(withOrient);
        Assert.All(withOrient, m => Assert.Equal(2u, m.HitEffectOrientType));
        Assert.All(withOrient, m => Assert.Contains(m.FlightSources!, f => f.Group == "SpellDataResource.mHitEffectOrientType" && f.Source == "authored"));
    }

    [Fact]
    public void Ziggs_Q_is_lobbed_and_Ezreal_Q_is_flat()
    {
        if (Skip()) return;
        var ziggs = Slots("Ziggs")[0].Missiles.Select(m => m.Flight).Where(f => f is not null).ToList();
        var lob = ziggs.First(f => f!.MovementClass == "FixedSpeedMovement" && f.HeightSolver == "GravityHeightSolver" && f.HeightSolverFields.ContainsKey("mGravity"));
        Assert.All(ziggs, f => Assert.Equal("GravityHeightSolver", f!.HeightSolver));
        Assert.Equal(7000f, lob!.HeightSolverFields["mGravity"]);
        Assert.Equal(60f, lob.OffsetInitialTargetHeight);
        Assert.Equal(60f, lob.TargetHeightAugment);
        Assert.True(lob.IsArc);
        Assert.Contains("DestroyOnMovementComplete", lob.Behaviors);

        var ezreal = Slots("Ezreal")[0].Missiles.Select(m => m.Flight).Where(f => f is not null).ToList();
        Assert.NotEmpty(ezreal);
        Assert.All(ezreal, f => Assert.False(f!.IsArc));
    }

    [Fact]
    public void Unknown_behaviour_classes_come_back_as_hashes_not_as_nothing()
    {
        if (Skip()) return;
        var all = new[] { "Ahri", "Ziggs", "Draven", "Zoe" }.SelectMany(c => Slots(c)).SelectMany(s => s.Missiles)
            .Select(m => m.Flight).Where(f => f is not null).SelectMany(f => f!.Behaviors).ToList();
        Assert.All(all, b => Assert.True(b.Length > 0 && (char.IsLetter(b[0]) || b.StartsWith("0x") || b == "?")));
    }

    // ===================================================== the planner uses them

    [Fact]
    public void A_return_leg_is_planned_for_Ahri_Q()
    {
        if (Skip()) return;
        var d = Def("Ahri");
        var script = SimulationPlanner.Plan(d, Ab(d, "Q"), Stage());
        var hit = script.Find(SimStepKind.Hit)!;
        var back = Assert.Single(script.All(SimStepKind.Return));
        Assert.Equal(hit.Time, back.Time, 3);                                   // it turns where the outbound arrives
        Assert.True(back.EndTime > back.Time);                                  // and flies back to the caster
        Assert.Equal(Vector3.Zero, back.To!.Value);
        Assert.Equal(new Vector3(0f, 0f, 500f), back.From!.Value);
        Assert.True(script.EndTime >= back.EndTime + SimulationPlanner.HitLinger - 0.001f);
        Assert.Contains("name", back.Why);
    }

    [Fact]
    public void A_missile_without_a_return_plans_none()
    {
        if (Skip()) return;
        var d = Def("Ezreal");
        Assert.Empty(SimulationPlanner.Plan(d, Ab(d, "Q"), Stage()).All(SimStepKind.Return));
    }

    [Fact]
    public void The_hit_effect_goes_on_the_authored_hit_bone_and_DestroyOnHit_stops_the_missile()
    {
        if (Skip()) return;
        var d = Def("Ahri");
        // Ahri E: the missile spell authors mHitBoneName "spine" (the root spell says "head": the missile spell wins) and a DestroyOnHit behaviour
        var hit = SimulationPlanner.Plan(d, Ab(d, "E"), Stage()).Find(SimStepKind.Hit)!;
        Assert.Equal("spine", hit.Bone);
        Assert.Contains("mHitBoneName", hit.Why);
        Assert.True(hit.StopsMissile);
        Assert.Contains("DestroyOnHit", hit.Text);
    }

    [Fact]
    public void A_destroy_on_hit_behaviour_decides_stop_over_the_tag_guess()
    {
        var piercingTagged = new AbilitySim("Q", null, 0, "XQ", "XQ", "XQ", null, null, Array.Empty<string>(), null, null, "direction", 1000f, 0f, 0f,
            new[] { SimulationPlanner.PiercingTag }, null, null, null, 0.1f, null, null,
            new[]
            {
                new MissileSim("XMis", null, null, "ConstantSpeed", 1000f, null, "mMissileSpec", 60f, "authored")
                {
                    Flight = new MissileFlight("FixedSpeedMovement", null, null, null, null, null, null, null,
                        new Dictionary<string, float>(), new[] { "DestroyOnHit" }),
                },
            },
            Array.Empty<SimSystemRef>(), Array.Empty<SimSystemRef>(), Array.Empty<SimSystemRef>(), Array.Empty<ClipParticleEventSim>(), Array.Empty<ClipSoundEventSim>(),
            new ScenarioHint(true, false, false, 500f), Array.Empty<FieldSource>(), Array.Empty<string>());
        var def = new ChampionSimDefinition(1, "t", "t", "X", null, null, "b", null, new[] { piercingTagged }, Array.Empty<AttackSim>(), Array.Empty<string>());

        var hit = SimulationPlanner.Plan(def, piercingTagged, Stage(), Array.Empty<ISimAdapter>()).Find(SimStepKind.Hit)!;
        Assert.True(hit.StopsMissile);                                          // DestroyOnHit wins over Trait_Ranged_Piercing
    }

    // ===================================================== the height maths

    [Fact]
    public void A_gravity_path_starts_and_ends_at_the_given_heights_and_rises_between()
    {
        var path = new MissileHeightPath(MissileHeightKind.Gravity, 0.5f) { Gravity = 7000f };
        Assert.Equal(100f, path.HeightAt(0f, 100f, 50f), 2);
        Assert.Equal(50f, path.HeightAt(1f, 100f, 50f), 2);
        float mid = path.HeightAt(0.5f, 100f, 50f);
        Assert.Equal(75f + 7000f * 0.25f / 8f, mid, 2);                         // chord midpoint + g T^2 / 8
        Assert.True(path.IsArc);
    }

    [Fact]
    public void Flat_solvers_and_end_height_follow_the_spec()
    {
        var blended = new MissileHeightPath(MissileHeightKind.BlendedLinear, 1f);
        Assert.Equal(0f, blended.HeightAt(0f, 0f, 100f), 3);
        Assert.Equal(100f, blended.HeightAt(1f, 0f, 100f), 3);
        Assert.False(blended.IsArc);

        var flight = new MissileFlight("FixedSpeedMovement", "R_hand", 100f, 100f, true, null, null, null, new Dictionary<string, float>(), Array.Empty<string>());
        Assert.Equal(150f + 100f + 100f, flight.EndHeightAboveGround(150f));   // launch height + initial offset + augment
        var noEnd = flight with { UseHeightOffsetAtEnd = false };
        Assert.Equal(100f, noEnd.EndHeightAboveGround(150f));
    }

    [Fact]
    public void The_new_fields_survive_a_json_round_trip()
    {
        if (Skip()) return;
        var d = Def("Ahri");
        var bytes = ChampionSimJson.Write(d);
        var again = ChampionSimJson.Read(bytes);
        Assert.Equal(bytes, ChampionSimJson.Write(again));
        Assert.Contains(again.Attacks.SelectMany(a => a.Missiles), m => m.Flight?.StartBoneName == "R_hand");
    }
}
