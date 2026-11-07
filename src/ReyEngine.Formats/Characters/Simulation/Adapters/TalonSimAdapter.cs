using System.Numerics;
using static ReyEngine.Formats.Characters.Simulation.Adapters.SimAdapterKit;

namespace ReyEngine.Formats.Characters.Simulation.Adapters;

/// <summary>
/// M874: what Talon's compiled spell scripts do and the data cannot say. Measured on 16.22.8348910:
/// <list type="bullet">
/// <item><b>Q</b>: TalonQ (clip empty, cast 0.52, range 575, FixedSpeed 2000 with CastOnMovementComplete) is a LEAP to the target; TalonQAttack (range 170) is the melee strike and TalonQDashAttack (clip Spell1) the strike after the leap. The record has no targeting type.</item>
/// <item><b>E</b>: TalonE (Location, range 6000, display 725; WallJumpDistance 625, WallLockoutWidth 1250) hops over terrain; the hop's speed and duration are not data.</item>
/// <item><b>P</b>: TalonPassive (StackDuration 6, BleedDuration 2) bleeds after a basic attack on a target that took ability hits; the threshold (three) is Riot's tooltip, not data.</item>
/// </list>
/// W and R already play their out-and-back from the record and are left alone. Every value below that is not read from a record is a convention, Source "adapter", with its reason.
/// </summary>
public sealed class TalonSimAdapter : ISimAdapter
{
    public string Champion => "Talon";

    public const float MeleeRange = 170f;         // TalonQAttack castRange (record; not in the definition)
    public const float LeapStandOff = 150f;       // convention: where the leap ends, inside the melee range
    public const float LeapEnemyDistance = 400f;  // convention: beyond the melee range 170, inside TalonQ's 575
    public const float LeapHeight = 60f;          // convention: the arc height is not data
    public const float LeapSpeed = 2000f;         // TalonQ FixedSpeed mSpeed (record)
    public const float WallFootGap = 50f;         // convention: Talon starts at the wall's foot
    public const float WallJumpDistance = 625f;   // TalonE DataValue WallJumpDistance (record; not in the definition)
    public const float WallJumpSpeed = 1200f;     // convention: the hop's speed is not data
    public const float WallJumpHeight = 140f;     // convention
    public const float BleedSeconds = 2f;         // TalonPassive DataValue BleedDuration (record; not in the definition)
    public const int StackThreshold = 3;          // INFERENCE: Riot's tooltip (three separate hits), not data

    public SimScript Adapt(ChampionSimDefinition def, AbilitySim ability, SimStage stage, SimScript script) => ability.Key switch
    {
        "Q" when ability.ParentKey is null => Leap(def, ability, stage, script),
        "Q1" or "Q2" => Melee(def, ability, stage, script),
        "E" when ability.ParentKey is null => WallJump(def, ability, stage, script),
        "P" when ability.ParentKey is null => Passive(def, stage, script),
        _ => script,
    };

    private static (Vector3 Axis, float Dist) Axis(SimStage stage)
    {
        var to = Flat(stage.Enemy - stage.Champion);
        float len = to.Length();
        return (len > 1e-3f ? to / len : new Vector3(MathF.Sin(stage.Yaw), 0f, MathF.Cos(stage.Yaw)), len);
    }

    // ================================================================== Q1 / Q2: the melee strike

    private static SimScript Melee(ChampionSimDefinition def, AbilitySim q, SimStage stage, SimScript script)
    {
        var (axis, _) = Axis(stage);
        var enemy = Ground(stage, stage.Champion + axis * (MeleeRange * 0.7f));
        var planned = Generic(def, q, stage with { Enemy = enemy });
        return Rebuild(planned, planned.Steps, Array.Empty<SimStep>(), planned.EndTime,
            new[] { $"Talon {q.Key}: the enemy stands {F(MeleeRange * 0.7f)} units away, inside TalonQAttack's melee range {F(MeleeRange)} (adapter: the record has no targeting type)" });
    }

    // ================================================================== Q: the leap

    private static SimScript Leap(ChampionSimDefinition def, AbilitySim q, SimStage stage, SimScript script)
    {
        var strike = Ability(def, "Q1");
        if (strike is null) return script;
        var (axis, _) = Axis(stage);
        var champion = stage.Champion;
        var enemy = Ground(stage, champion + axis * LeapEnemyDistance);
        var landing = Ground(stage, enemy - axis * LeapStandOff);
        float speed = q.Missiles.FirstOrDefault(m => m.Speed is > 0f)?.Speed ?? LeapSpeed;
        float cast = q.CastSeconds ?? 0.3f;
        float leap = Flat(landing - champion).Length() / speed;
        float landAt = cast + leap;
        var sub = Generic(def, strike, stage with { Champion = landing, Enemy = enemy });

        var steps = new List<SimStep>
        {
            new(SimStepKind.Place, 0f, "convention", $"place champion; the enemy {F(LeapEnemyDistance)} units away, beyond the melee range {F(MeleeRange)} and inside TalonQ's range {F(q.CastRange)} (adapter)")
            { From = champion, To = enemy, Name = "Q" },
            new(SimStepKind.Displace, cast, Adapter, $"leap {F(Flat(landing - champion).Length())} units to the enemy at {F(speed)} u/s")
            { Displacement = SimDisplaceKind.Jump, From = champion, To = landing, EndTime = landAt, Height = LeapHeight, Unit = "caster",
              Why = $"speed {F(speed)} = TalonQ FixedSpeed with CastOnMovementComplete (record); a leap is chosen because the enemy is beyond the melee range {F(MeleeRange)} (TalonQAttack castRange) and inside {F(q.CastRange)} "
                    + "(TalonQ castRange); the record has no targeting type; the arc height and the stand-off are conventions" },
        };
        steps.AddRange(Embed(sub, "Q1", landAt, "the strike after the leap is TalonQDashAttack (clip Spell1)"));
        float end = landAt + MathF.Max(sub.EndTime, 0.9f);
        return Rebuild(script, Array.Empty<SimStep>(), steps, end, new[] { "Talon Q: adapter script - the leap, then the dash attack" },
            champion: champion, enemy: enemy, aimAt: SimAimTarget.Enemy, aim: enemy);
    }

    // ================================================================== E: over the wall

    /// <summary>The furthest the demo may move Talon from the stage point to put him at a wall's foot (convention; Map11's wall is 559 away): a wall further away is not used.</summary>
    public const float MaxRelocation = 700f;

    private static SimScript WallJump(ChampionSimDefinition def, AbilitySim e, SimStage stage, SimScript script)
    {
        if (stage.Walkable is not { } walkable)
            return AddNote(script, "Talon E: no navgrid on the stage, so there is no wall to jump (the generic plan plays)");
        var origin = stage.Champion;
        if (!walkable(origin))
            return AddNote(script, "Talon E: the stage point itself is unwalkable, so no wall is searched (the generic plan plays)");

        // every wall within 1500 units in sixteen directions, nearest first; the first one that can be crossed and staged wins. Nothing here is hard-coded to a map.
        var walls = new List<(float Dist, Vector3 Dir)>();
        for (int i = 0; i < 16; i++)
        {
            float yaw = i * MathF.PI / 8f;
            var dir = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
            if (FirstBlocked(walkable, origin, dir, 1500f, 5f) is { } d) walls.Add((d, dir));
        }
        if (walls.Count == 0)
            return AddNote(script, "Talon E: the navgrid has no unwalkable ground within 1500 units of the stage point (the generic plan plays)");

        var notes = new List<string>();
        foreach (var wall in walls.OrderBy(w => w.Dist))
        {
            var dirToWall = wall.Dir;
            float edge = wall.Dist - 5f;                                       // the last walkable sample before the wall
            float footAt = MathF.Max(0f, edge - WallFootGap);
            string where = $"the wall {F(wall.Dist)} units from the stage point";
            if (footAt > MaxRelocation) { notes.Add($"{where}: Talon would start {F(footAt)} units from the stage point, more than the {F(MaxRelocation)} the demo moves him; next wall"); continue; }
            if (FirstWalkableAfter(walkable, origin, dirToWall, wall.Dist, wall.Dist + WallJumpDistance + 400f, 5f) is not { } far)
            { notes.Add($"{where} does not end within {F(WallJumpDistance)} units; next wall"); continue; }
            float thickness = far - wall.Dist;
            float jumpLength = far - footAt;
            if (jumpLength > WallJumpDistance + 1f)
            { notes.Add($"{where}: the far side is {F(jumpLength)} units from its foot, more than WallJumpDistance {F(WallJumpDistance)} (thickness {F(thickness)}); next wall"); continue; }

            var start = Ground(stage, origin + dirToWall * footAt);
            var landing = Ground(stage, origin + dirToWall * far);
            // the enemy dummy stands 450 units from the start, on ground that can be stood on
            var side = Right(dirToWall);
            Vector3? enemySpot = null;
            foreach (var candidate in new[] { start - dirToWall * 450f, start + side * 450f, start - side * 450f })
                if (walkable(candidate)) { enemySpot = Ground(stage, candidate); break; }
            if (enemySpot is not { } enemy) { notes.Add($"{where}: no walkable ground for the enemy dummy near Talon's start; next wall"); continue; }

            float yawToWall = Yaw(dirToWall);
            var basis = Generic(def, e, stage with { Champion = start, Yaw = yawToWall, Enemy = enemy });
            float cast = e.CastSeconds ?? 0.25f;
            float seconds = MathF.Max(0.3f, MathF.Min(0.8f, Flat(landing - start).Length() / WallJumpSpeed));
            const string why = "the hop's speed and duration are not data (spellTotalTime 1.0 covers the whole cast); 1200 u/s with a 0.3-0.8 s clamp and the 140-unit arc are conventions; "
                               + "the wall is the nearest crossable unwalkable ground on the stage navgrid, the far side its first walkable ground; the distance is bounded by WallJumpDistance 625 (record)";
            var landName = SystemName(def, "Talon_Base_E_land");
            var keepComposite = basis.Steps.Select(st => st.Kind == SimStepKind.Composite && landName is not null
                ? st with { Exclude = new[] { landName }, Why = "the composite's E systems play at the cast; the landing burst (Talon_Base_E_land) is played by the script when he lands" }
                : st);
            var extra = new List<SimStep>
            {
                new(SimStepKind.Displace, cast, Adapter, $"wall jump {F(Flat(landing - start).Length())} units over a wall {F(thickness)} units thick")
                { Displacement = SimDisplaceKind.Jump, From = start, To = landing, EndTime = cast + seconds, Height = WallJumpHeight, Unit = "caster", Why = why },
            };
            if (Effect(def, "Talon_Base_E_land", cast + seconds, cast + seconds + 1.0f, "caster", landing, "named in the skin; played at the landing by the adapter") is { } land) extra.Add(land);
            float end = MathF.Max(basis.EndTime, cast + seconds + 0.8f);
            notes.Add($"Talon E: {where} ({F(thickness)} thick, found on the navgrid); Talon starts at its foot ({F(WallFootGap)} units back, a convention) and jumps to its far side");
            return Rebuild(basis, keepComposite, extra, end, notes, champion: start, yaw: yawToWall, enemy: enemy, aimAt: SimAimTarget.Enemy, aim: landing);
        }
        return script with { Notes = script.Notes.Concat(notes.Select(n => "Talon E: " + n)).Concat(new[] { "Talon E: no wall could be crossed; the generic plan plays" }).ToList() };
    }

    private static SimScript AddNote(SimScript script, string note) => script with { Notes = script.Notes.Concat(new[] { note }).ToList() };

    // ================================================================== P: three hits, a stack each, then the bleed

    private static SimScript Passive(ChampionSimDefinition def, SimStage stage, SimScript script)
    {
        var w = Ability(def, "W"); var q = Ability(def, "Q"); var q1 = Ability(def, "Q1"); var r = Ability(def, "R");
        if (w is null || q is null || q1 is null || r is null) return script;
        var crit = Attack(def, "TalonCritAttack") ?? BasicAttack(def);
        if (crit is null) return script;
        var (axis, _) = Axis(stage);
        var champion = stage.Champion;
        var enemy = Ground(stage, champion + axis * LeapEnemyDistance);
        var landing = Ground(stage, enemy - axis * LeapStandOff);
        const string stackWhy = "the stack systems (Talon_Base_P_Stack_1..3) are named in the skin; which ability hit adds which stack, and that they play on the target, are inferred; the threshold of three is Riot's tooltip, not data";
        var steps = new List<SimStep>
        {
            new(SimStepKind.Place, 0f, "convention", "place champion and enemy; Blade's End: W, Q and R each hit and add a stack, a basic attack then bleeds (adapter)")
            { From = champion, To = enemy, Name = "P" },
        };

        // 1. W (the out-and-back is the record's)
        var wScript = Generic(def, w, stage with { Enemy = enemy });
        steps.AddRange(Embed(wScript, "W", 0f, "stack 1: Talon's W"));
        float wHit = wScript.Find(SimStepKind.Hit)?.Time ?? 0.5f;
        float t = MathF.Max(wScript.Steps.Where(s => s.Kind == SimStepKind.Hit).Select(s => s.Time).DefaultIfEmpty(0.5f).Max() + 0.15f, 0.9f);
        var stackStarts = new List<float> { wHit };

        // 2. Q: the leap, then the strike
        float speed = q.Missiles.FirstOrDefault(m => m.Speed is > 0f)?.Speed ?? LeapSpeed;
        float cast = q.CastSeconds ?? 0.3f;
        float landAt = t + cast + Flat(landing - champion).Length() / speed;
        var strike = Generic(def, q1, stage with { Champion = landing, Enemy = enemy });
        steps.Add(new SimStep(SimStepKind.Displace, t + cast, Adapter, $"Q leaps {F(Flat(landing - champion).Length())} units at {F(speed)} u/s")
        { Displacement = SimDisplaceKind.Jump, From = champion, To = landing, EndTime = landAt, Height = LeapHeight, Unit = "caster",
          Why = "speed = TalonQ FixedSpeed (record); the leap and its arc height are the adapter's reading (see the Q script)" });
        steps.AddRange(Embed(strike, "Q1", landAt, "stack 2: Talon's Q strike"));
        stackStarts.Add(landAt + (strike.Find(SimStepKind.Hit)?.Time ?? 0f));
        t = landAt + MathF.Max(strike.Steps.Where(s => s.Kind == SimStepKind.Hit).Select(s => s.Time).DefaultIfEmpty(0f).Max() + 0.5f, 0.6f);

        // 3. R, from where he now stands
        var rScript = Generic(def, r, stage with { Champion = landing, Enemy = enemy });
        steps.AddRange(Embed(rScript, "R", t, "stack 3: Talon's R"));
        stackStarts.Add(t + (rScript.Find(SimStepKind.Hit)?.Time ?? 0.5f));
        t = stackStarts[^1] + 0.4f;

        // 4. the basic attack that bleeds
        var (att, hit, hitAt, _) = AttackOn(crit, t, "enemy", landing, enemy, "TalonPassiveAttack: the attack after three stacks is the passive's (crit clip); that it follows the third stack is script-side");
        steps.Add(att); if (hit is not null) steps.Add(hit);
        float bleedEnd = hitAt + BleedSeconds;
        for (int i = 0; i < stackStarts.Count; i++)
            if (Effect(def, $"Talon_Base_P_Stack_{i + 1}", stackStarts[i], hitAt, "enemy", enemy, stackWhy) is { } stack) steps.Add(stack);
        if (Effect(def, "Talon_Base_P_tar_tick", hitAt, bleedEnd, "enemy", enemy,
                "bleed for 2 s = TalonPassive DataValue BleedDuration (record, not in the definition); the tick system is named in the skin") is { } bleed) steps.Add(bleed);
        float end = bleedEnd + 0.3f;
        if (end > SimulationPlanner.MaxSeconds) end = SimulationPlanner.MaxSeconds;
        return Rebuild(script, Array.Empty<SimStep>(), steps, end, new[] { "Talon P: adapter script - W, Q and R stack, a basic attack bleeds" },
            champion: champion, enemy: enemy, aimAt: SimAimTarget.Enemy, aim: enemy);
    }
}
