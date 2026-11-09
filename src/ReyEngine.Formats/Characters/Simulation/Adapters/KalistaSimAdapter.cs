using System.Numerics;
using static ReyEngine.Formats.Characters.Simulation.Adapters.SimAdapterKit;

namespace ReyEngine.Formats.Characters.Simulation.Adapters;

/// <summary>
/// M874: what Kalista's compiled spell scripts do and the data cannot say. Measured on 16.22.8348910 (see docs/plans/champion-simulation.md):
/// <list type="bullet">
/// <item><b>P</b>: after an attack she hops (KalistaPassiveDashSpellActual: clip Spell2, cast 0.25, total 1.0). The record has castRangeDisplayOverride 300 and no distance
/// or speed; "after an attack" is script-side.</item>
/// <item><b>W</b>: the Sentinel is a character of its own (Characters/KalistaSpawn, base move speed 560, mesh and graph of its own); its lifetime, patrol and effect bones are not data.</item>
/// <item><b>E</b>: Riot's script stacks a spear on every hit; the stack-to-system mapping (Kalista_E_Spear_tar{n}) is inferred from the names.</item>
/// <item><b>R</b>: KalistaRAlly pulls the ally at speed 700 (record), the ally is carried and thrown (KalistaRAllyDash, direction targeting); the carry time and the throw speed are not data.</item>
/// </list>
/// Q already pierces and hits through the generic plan and is left alone. Every value below that is not read from a record is a convention, labelled Source "adapter" with the reason.
/// </summary>
public sealed class KalistaSimAdapter : ISimAdapter
{
    public string Champion => "Kalista";

    // ---- conventions (each one is repeated in the Why of the step that uses it) ----
    public const float HopDistance = 300f;       // KalistaPassiveDashSpellActual castRangeDisplayOverride; the record has no distance
    public const float HopSeconds = 0.25f;        // convention: the record has no dash speed or duration (its cast time is 0.25 s)
    public const float HopHeight = 90f;           // convention: the arc height is not data
    public const float AttackGap = 0.9f;          // convention: the attack cadence of a stack run
    public const float SentinelSpeed = 560f;      // KalistaSpawn baseMoveSpeed (record)
    public const float SentinelAhead = 600f;      // convention: "about 600 units ahead"
    public const float SentinelBeside = 200f;     // convention: beside the enemy
    public const float PatrolHalfLength = 250f;   // convention: the patrol path is not data
    public const int PatrolLoops = 2;             // convention: how long the Sentinel is shown: out, two loops, a moment more
    public const float AllyPullSpeed = 700f;      // KalistaRAlly FixedSpeed (record)
    public const float AllyHold = 1.0f;           // convention: the tether time is not data
    public const float AllyFlingSpeed = 700f;     // convention: the fling speed is not data; RAllyStun missileSpeed is 700
    public const float KnockupSeconds = 1.0f;     // KnockupDuration rank 1 (record DataValue)
    public const float KnockupHeight = 400f;      // convention: not data; high enough to read at the stage camera

    public SimScript Adapt(ChampionSimDefinition def, AbilitySim ability, SimStage stage, SimScript script)
    {
        if (ability.ParentKey is not null) return script;
        return ability.Key switch
        {
            "P" => Passive(def, stage, script),
            "W" => Sentinel(def, ability, stage, script),
            "E" => Rend(def, ability, stage, script),
            "R" => FatesCall(def, ability, stage, script),
            _ => script,
        };
    }

    private static (Vector3 Axis, Vector3 Enemy) Layout(SimStage stage)
    {
        var to = Flat(stage.Enemy - stage.Champion);
        float len = to.Length();
        var axis = len > 1e-3f ? to / len : new Vector3(MathF.Sin(stage.Yaw), 0f, MathF.Cos(stage.Yaw));
        float dist = Math.Clamp(len, 350f, 700f);
        return (axis, Ground(stage, stage.Champion + axis * dist));
    }

    private static Vector3 Walkable(SimStage stage, Vector3 from, Vector3 to)
    {
        if (stage.Walkable is not { } w) return to;
        var dir = Flat(to - from); float dist = dir.Length();
        if (dist < 1f) return to;
        dir /= dist;
        float reach = 0f;
        for (float d = 25f; d <= dist + 0.01f; d += 25f)
        {
            if (!w(from + dir * MathF.Min(d, dist))) break;
            reach = MathF.Min(d, dist);
        }
        return Ground(stage, from + dir * reach);
    }

    private static SimStep PlaceStep(SimStage stage, Vector3 champion, Vector3 enemy, Vector3? ally, Vector3 aim, string key, string text) =>
        new(SimStepKind.Place, 0f, "convention", text) { From = champion, To = aim, Name = key };

    // ================================================================== P: attack, hop, attack, hop

    private static SimScript Passive(ChampionSimDefinition def, SimStage stage, SimScript script)
    {
        if (BasicAttack(def) is not { } attack) return script;
        var (axis, enemy) = Layout(stage);
        var side = Right(axis);
        var champion = stage.Champion;
        var hopClip = Ability(def, "W")?.Clip is { } w ? w with { Source = Adapter } : null;
        const string hopWhy = "hop distance 300 = KalistaPassiveDashSpellActual castRangeDisplayOverride (the record has no distance or speed); the direction (beside the enemy axis, "
                              + "alternating), the 0.25 s duration and the arc height are conventions; hopping after an attack is script-side";
        var steps = new List<SimStep> { PlaceStep(stage, champion, enemy, null, champion, "P", "place champion and enemy; Martial Poise: two attacks, each followed by a hop (adapter)") };
        float t = 0f; var pos = champion; float end = 0f;
        for (int i = 0; i < 2; i++)
        {
            var (att, hit, _, _) = AttackOn(attack, t, "enemy", pos, enemy, "the passive's trigger (a hop after an attack) is script-side, not in the record");
            steps.Add(att); if (hit is not null) steps.Add(hit);
            float hopStart = t + (attack.CastSeconds ?? 0.5f), hopEnd = hopStart + HopSeconds;
            var dir = i == 0 ? -side : side;
            var to = Walkable(stage, pos, pos + dir * HopDistance);
            steps.Add(new SimStep(SimStepKind.Displace, hopStart, Adapter, $"hop {F(Flat(to - pos).Length())} units to the {(i == 0 ? "left" : "right")} of the enemy axis")
            { Displacement = SimDisplaceKind.Jump, From = pos, To = to, EndTime = hopEnd, Height = HopHeight, Clip = hopClip, Unit = "caster", Why = hopWhy });
            string blade = i == 0 ? "Kalista_Base_P_Blade_Hero_Left" : "Kalista_Base_P_Blade_Hero_Right";
            const string fxWhy = "the passive's systems are named in the skin; binding them to the hop is an adapter choice (the script that plays them ships in no bin)";
            if (Effect(def, blade, hopStart, hopEnd + 0.6f, "caster", pos, fxWhy) is { } e1) steps.Add(e1);
            if (Effect(def, "Kalista_Base_P_BladeFall_Hero", hopEnd, hopEnd + 0.8f, "caster", to, fxWhy) is { } e2) steps.Add(e2);
            pos = to; end = hopEnd + 0.8f;
            t = hopEnd + 0.15f;
        }
        return Rebuild(script, Array.Empty<SimStep>(), steps, end, new[] { "Kalista P: adapter script - two attacks, each followed by a hop" },
            champion: champion, enemy: enemy, aimAt: SimAimTarget.Caster, aim: champion);
    }

    // ================================================================== W: the Sentinel

    public const string SentinelFallback = "KalistaSpawn";

    /// <summary>M891: the Sentinel's character as W's own preload file names it (BBPreloadCharacter kalistaspawn), in the constant's casing when it is the same name; the constant when the definition carries none.</summary>
    internal static string SentinelCharacter(AbilitySim w)
    {
        string? named = w.PreloadCharacters?.FirstOrDefault(n => n.Length > 0);
        if (named is null) return SentinelFallback;
        return named.Equals(SentinelFallback, StringComparison.OrdinalIgnoreCase) ? SentinelFallback : named;
    }

    private static SimScript Sentinel(ChampionSimDefinition def, AbilitySim w, SimStage stage, SimScript script)
    {
        var (axis, enemy) = Layout(stage);
        var side = Right(axis);
        var champion = stage.Champion;
        float cast = w.CastSeconds ?? 0.5f;
        var spawnAt = Ground(stage, champion + axis * 60f);
        var aim = Walkable(stage, spawnAt, champion + axis * SentinelAhead + side * SentinelBeside);
        var patrolA = Walkable(stage, aim, aim + side * PatrolHalfLength);
        var patrolB = Walkable(stage, aim, aim - side * PatrolHalfLength);
        float walk = Flat(aim - spawnAt).Length() / SentinelSpeed;
        float loop = (Flat(patrolA - aim).Length() + Flat(patrolB - patrolA).Length() + Flat(aim - patrolB).Length()) / SentinelSpeed;
        float despawnAt = MathF.Max(script.EndTime, cast + walk + PatrolLoops * loop + 0.6f);
        string sentinel = SentinelCharacter(w);

        // the five W systems belong to the Sentinel, not to Kalista: they are not played at the caster
        var sentinelSystems = w.CasterSystems.Where(r => r.Name is not null).Select(r => r.Name!).ToList();
        var keep = script.Steps.Where(s => s.Kind is SimStepKind.Place or SimStepKind.Clip)
            .Concat(script.Steps.Where(s => s.Kind == SimStepKind.Composite).Select(s => s with { Exclude = sentinelSystems, Why = "W's systems (Alerted, Avatar, Glow, Glow2, ViewCone) are the Sentinel's; the cast plays the clip only" }))
            .ToList();
        var extra = new List<SimStep>
        {
            new(SimStepKind.Spawn, cast, Adapter,
                $"Sentinel ({sentinel}) appears at the cast and walks {F(Flat(aim - spawnAt).Length())} units at {F(SentinelSpeed)} u/s, then patrols")
            {
                Unit = "sentinel", Resource = sentinel, IdleClip = "Idle1", RunClip = "Run", From = spawnAt, To = aim, Speed = SentinelSpeed,
                Path = new[] { aim, patrolA, patrolB }, Loop = true, EndTime = despawnAt,
                Why = $"the unit is Characters/{sentinel} (record: baseMoveSpeed 560, mesh, graph, initialSubmeshToHide Alert; the loaded skin's own sentinel skin when it has one); "
                      + "where it goes (about 600 units ahead, beside the enemy), the spawn delay (the cast time 0.5 s), the patrol (a 500-unit back-and-forth, two loops) and the lifetime are conventions",
            },
        };
        const string fxWhy = "the Sentinel's effect systems are named in the skin (W_Avatar, W_Glow); the attach bone is not in the data, so they ride the unit's root";
        foreach (var name in new[] { "Kalista_Base_W_Avatar", "Kalista_Base_W_Glow" })
            if (Effect(def, name, cast, despawnAt, "sentinel", spawnAt, fxWhy, travelTo: aim, speed: SentinelSpeed) is { } e) extra.Add(e);
        extra.Add(new SimStep(SimStepKind.Despawn, despawnAt - 0.02f, Adapter, "the Sentinel is removed with the scene")
        { Unit = "sentinel", EndTime = despawnAt - 0.02f, Why = "its lifetime is not in the data; it ends with the run" });

        return Rebuild(script, keep, extra, despawnAt, new[] { "Kalista W: adapter script - the Sentinel is spawned, walks and patrols" },
            champion: champion, enemy: enemy, aimAt: SimAimTarget.Enemy, aim: aim);
    }

    // ================================================================== E: stacks, then the rend

    private static SimScript Rend(ChampionSimDefinition def, AbilitySim e, SimStage stage, SimScript script)
    {
        if (BasicAttack(def) is not { } attack || Ability(def, "Q") is not { } q) return script;
        var (axis, enemy) = Layout(stage);
        var champion = stage.Champion;
        var baseStage = stage with { Enemy = enemy };
        const string stackWhy = "a spear is stacked on every hit (three basic attacks and Q); which hit adds a spear and the stack-to-system mapping (Kalista_Base_E_Spear_tar{n}) are inferred from the names - the script that stacks them ships in no bin";
        var steps = new List<SimStep> { PlaceStep(stage, champion, enemy, null, enemy, "E", "place champion and enemy; Rend: three attacks and Q stack spears, then E (adapter)") };
        var markers = new List<(int N, float From)>();
        float t = 0f;
        for (int n = 1; n <= 3; n++)
        {
            var (att, hit, hitAt, _) = AttackOn(attack, t, "enemy", champion, enemy, "stacking a spear per attack is script-side");
            steps.Add(att); if (hit is not null) steps.Add(hit);
            markers.Add((n, hitAt));
            t += AttackGap;
        }
        var qScript = Generic(def, q, baseStage);
        steps.AddRange(Embed(qScript, "Q", t, "Q is the fourth hit of the stack run"));
        markers.Add((4, t + (qScript.Find(SimStepKind.Hit)?.Time ?? 0.4f)));
        float rendAt = markers[^1].From + 0.6f;

        foreach (var (n, from) in markers)
            if (Effect(def, $"Kalista_Base_E_Spear_tar{n}", from, rendAt, "enemy", enemy, stackWhy) is { } m) steps.Add(m);

        // the rend: the spears leave Kalista (out{n}, n = the stacks) and the target systems land; the other numbered systems and the stack markers are not played at her
        var eScript = Generic(def, e, baseStage);
        var notCast = e.CasterSystems.Where(r => r.Name is not null).Select(r => r.Name!)
            .Where(n => !n.Equals("Kalista_Base_E_Spear_Out_Sound", StringComparison.OrdinalIgnoreCase)
                        && !n.Equals("Kalista_Base_E_Spear_out4", StringComparison.OrdinalIgnoreCase)).ToList();
        steps.AddRange(Embed(eScript, "E", rendAt, "Rend: E detonates the four stacked spears (out4 = the stack count)", notCast));
        float end = rendAt + MathF.Max(1.0f, eScript.EndTime);
        return Rebuild(script, Array.Empty<SimStep>(), steps, end, new[] { "Kalista E: adapter script - three attacks and Q stack spears, then E rends" },
            champion: champion, enemy: enemy, aimAt: SimAimTarget.Enemy, aim: enemy);
    }

    // ================================================================== R: pull, hide, fling, knock up

    private static SimScript FatesCall(ChampionSimDefinition def, AbilitySim r, SimStage stage, SimScript script)
    {
        var (axis, enemy) = Layout(stage);
        var side = Right(axis);
        var champion = stage.Champion;
        var ally = stage.Ally is { } given ? Ground(stage, given) : Ground(stage, champion + side * SimulationPlanner.AllySideOffset);
        float cast = r.CastSeconds ?? 0.25f;
        var carried = Ground(stage, champion + axis * 40f);
        float pull = Flat(carried - ally).Length() / AllyPullSpeed;
        float pullEnd = cast + pull;
        float showAt = pullEnd + AllyHold;
        var landing = Walkable(stage, carried, enemy + side * 140f);
        float fling = Flat(landing - carried).Length() / AllyFlingSpeed;
        float arrive = showAt + fling;
        float knockEnd = arrive + KnockupSeconds;

        // the cast plays the ground, charge and missile systems; the vanish cone and the knock-up trail are timed by the script, not played at the cast
        var exclude = r.CasterSystems.Where(s => s.Name is not null).Select(s => s.Name!)
            .Where(n => n.Contains("Vanish_Cone", StringComparison.OrdinalIgnoreCase) || n.Contains("Knockup_Trail", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("Ally_FadeOut", StringComparison.OrdinalIgnoreCase)).ToList();
        var sub = Generic(def, r, stage with { Enemy = enemy, Ally = ally });
        var keep = script.Steps.Where(s => s.Kind is SimStepKind.Place or SimStepKind.Clip).ToList();
        var steps = new List<SimStep>
        {
            new(SimStepKind.Composite, 0f, Adapter, "spell composite R; its target effect lands when the ally arrives")
            { Name = "R", Sub = sub, From = champion, To = enemy, EndTime = arrive, Exclude = exclude,
              Why = "the impact (Kalista_Base_R_tar) is delayed to the ally's arrival; the vanish cone and the knock-up trail are played by the script" },
            new(SimStepKind.MoveUnit, cast, Adapter, $"the ally is pulled to Kalista at {F(AllyPullSpeed)} u/s")
            { Unit = "ally", Move = SimMoveKind.Pull, From = ally, To = carried, Speed = AllyPullSpeed, EndTime = pullEnd,
              Why = "pull speed 700 = KalistaRAlly FixedSpeed (record); that the ally is pulled at the cast is the adapter's reading of the record's children" },
            new(SimStepKind.Hide, pullEnd, Adapter, "the ally is inside Kalista")
            { Unit = "ally", EndTime = pullEnd, Why = "the data has no carry model: the ally vanishes (R_Ally_Vanish_Cone) and is hidden until it is thrown" },
            new(SimStepKind.Show, showAt, Adapter, "the ally is thrown")
            { Unit = "ally", EndTime = showAt, Why = $"carry time {F(AllyHold)} s is a convention: the data has no tether duration" },
            new(SimStepKind.MoveUnit, showAt, Adapter, $"the ally is flung toward the enemy at {F(AllyFlingSpeed)} u/s")
            { Unit = "ally", Move = SimMoveKind.Fling, From = carried, To = landing, Speed = AllyFlingSpeed, EndTime = arrive,
              Why = "KalistaRAllyDash is direction-targeted with no speed; 700 = KalistaRAllyStun missileSpeed, used by convention" },
            new(SimStepKind.Hit, arrive, Adapter, "Kalista_Base_R_tar lands on the enemy when the ally arrives") { Name = "enemy", From = carried, To = enemy, EndTime = arrive },
            new(SimStepKind.MoveUnit, arrive, Adapter, $"the enemy is knocked up for {F(KnockupSeconds)} s")
            { Unit = "enemy", Move = SimMoveKind.Knockup, From = enemy, To = enemy, Height = KnockupHeight, EndTime = knockEnd,
              Why = "duration 1.0 s = KnockupDuration rank 1 (record DataValue, not in the definition); the arc height is a convention" },
        };
        const string fxWhy = "named in the skin; binding it to this moment is the adapter's";
        if (Effect(def, "Kalista_Base_R_Ally_Vanish_Cone", pullEnd, pullEnd + 1.0f, "caster", carried, fxWhy) is { } cone) steps.Add(cone);
        if (Effect(def, "Kalista_Base_R_Knockup_Trail", showAt, arrive + 0.3f, "ally", carried, fxWhy, travelTo: landing, speed: AllyFlingSpeed) is { } trail) steps.Add(trail);
        float end = knockEnd + 0.5f;
        return Rebuild(script, keep, steps, end, new[] { "Kalista R: adapter script - the ally is pulled, hidden, flung and the enemy is knocked up" },
            champion: champion, enemy: enemy, ally: ally, aimAt: SimAimTarget.Enemy, aim: enemy);
    }
}
