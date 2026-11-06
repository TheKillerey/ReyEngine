using System.Globalization;
using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Characters.Simulation;

public enum SimStepKind { Place, Clip, Composite, Displace, Channel, Hit, End }

public enum SimDisplaceKind { Dash, Blink }

/// <summary>Who the ability is aimed at in a scenario.</summary>
public enum SimAimTarget { Caster, Enemy, Ally }

/// <summary>
/// M869: one timed step of a <see cref="SimScript"/>. <see cref="Source"/> says what the step rests on: "authored" (a Riot record
/// field), "convention" (a rule the editor applies: the name-token spell composite, the stage layout, a default timing) or
/// "adapter" (per-champion code, <see cref="ISimAdapter"/>). The UI and the validator show it so inference is never read as data.
/// </summary>
public sealed record SimStep(SimStepKind Kind, float Time, string Source, string Text)
{
    /// <summary>Seconds. Equal to <see cref="Time"/> for an instant step; the end of a span for Displace and Channel.</summary>
    public float EndTime { get; init; }
    public Vector3? From { get; init; }
    public Vector3? To { get; init; }
    public SimDisplaceKind? Displacement { get; init; }
    /// <summary>Clip name, composite key, or the unit a hit lands on ("enemy" / "ally").</summary>
    public string? Name { get; init; }
    /// <summary>On a Hit: the missile ends at the unit it hit (<c>Trait_Ranged_StopsFirstHit</c> with the unit on its line) instead of flying on.</summary>
    public bool StopsMissile { get; init; }
}

/// <summary>
/// M869: where the scenario stands. The champion and the enemy dummy are on the stage; the ally is optional (the planner puts one
/// beside the champion when the ability needs one and the stage names none).
/// </summary>
public sealed record SimStage(Vector3 Champion, float Yaw, Vector3 Enemy, Vector3? Ally = null,
    Func<Vector3, float>? GroundHeight = null, NavGrid? Nav = null, Func<Vector3, bool>? Walkable = null);

/// <summary>The planned run of one ability: where everyone stands, the timed steps, and what the plan could not decide.</summary>
public sealed record SimScript(
    string Champion,
    string Key,
    Vector3 ChampionPosition,
    float ChampionYaw,
    Vector3 EnemyPosition,
    Vector3? AllyPosition,
    SimAimTarget AimAt,
    Vector3 Aim,
    string TargetingKind,
    IReadOnlyList<SimStep> Steps,
    float EndTime,
    IReadOnlyList<string> Notes)
{
    public bool NeedsAlly => AllyPosition is not null;
    public SimStep? Find(SimStepKind kind) => Steps.FirstOrDefault(s => s.Kind == kind);
    public IEnumerable<SimStep> All(SimStepKind kind) => Steps.Where(s => s.Kind == kind);
}

/// <summary>M869: the extension point for per-champion knowledge the data cannot carry (Kalista W, Talon E). None are registered yet.</summary>
public interface ISimAdapter
{
    /// <summary>The champion (WAD / folder name) this adapter belongs to.</summary>
    string Champion { get; }

    /// <summary>Rewrite or extend the script the planner made. Steps it adds or changes must carry Source "adapter".</summary>
    SimScript Adapt(ChampionSimDefinition definition, AbilitySim ability, SimStage stage, SimScript script);
}

/// <summary>
/// M869: a <see cref="ChampionSimDefinition"/> ability plus a <see cref="SimStage"/> becomes a deterministic, timed
/// <see cref="SimScript"/>. Pure: no clock, no randomness, no renderer. The player (App) runs the script; nothing here draws.
///
/// <para>What is evidence and what is convention. Timing comes from the definition (cast frame, total time, clip length,
/// missile speed). A displacement exists ONLY when a tag says so: <c>Trait_PlayerSelectedDashDirection</c> is a dash,
/// <c>PositiveEffect_Teleport</c> a blink; no other ability moves the caster. The stage layout (the enemy 80% of a limited
/// range away, an ally 250 units to the side) is convention and says so on its step.</para>
/// </summary>
public static class SimulationPlanner
{
    public const string DashTag = "Trait_PlayerSelectedDashDirection";
    public const string TeleportTag = "PositiveEffect_Teleport";
    public const string ChannelTag = "Trait_ChannelSpell";
    public const string StopsFirstHitTag = "Trait_Ranged_StopsFirstHit";
    public const string PiercingTag = "Trait_Ranged_Piercing";
    /// <summary>A unit counts as on a missile's line within half its width plus this (the dummy's radius).</summary>
    public const float UnitRadius = 65f;
    /// <summary>How far along a displacement the walkability is sampled.</summary>
    public const float WalkSampleStep = 25f;

    /// <summary>Seconds a script may run at most.</summary>
    public const float MaxSeconds = 8f;
    public const float HitLinger = 1f;
    public const float DisplaceLinger = 0.5f;
    public const float PassiveSeconds = 3f;
    public const float ConventionDashSeconds = 0.25f;
    /// <summary>A dash stops this far short of the enemy.</summary>
    public const float DashStandOff = 150f;
    public const float AllySideOffset = 250f;
    public const float EnemyMargin = 50f, EnemyMinDistance = 250f, EnemyMaxDistance = 900f;
    /// <summary>The authored range Riot uses for "not range-limited".</summary>
    public const float UnboundedRange = 20000f;

    private static readonly string[] AllyTags = { "PositiveEffect_Boon", "Trait_Shield", "Trait_ActiveHeal" };

    /// <summary>Adapters in effect when a caller passes none. Empty until a per-champion milestone adds some.</summary>
    public static IReadOnlyList<ISimAdapter> Adapters { get; } = Array.Empty<ISimAdapter>();

    /// <summary>True when the ability is staged with an ally: an adapter said so, or it is a boon / shield / heal that needs a unit.</summary>
    public static bool NeedsAlly(AbilitySim a) => a.Scenario.NeedsAlly || AllyByTags(a);

    private static bool AllyByTags(AbilitySim a) =>
        IsUnitTargeted(a.TargetingKind) && a.Tags.Any(t => AllyTags.Contains(t, StringComparer.Ordinal));

    private static bool IsUnitTargeted(string kind) => Normalise(kind) is "target" or "targetorlocation";

    public static SimScript Plan(ChampionSimDefinition def, AbilitySim ability, SimStage stage, IEnumerable<ISimAdapter>? adapters = null)
    {
        var script = PlanCore(def, ability, stage);
        foreach (var adapter in adapters ?? Adapters)
            if (adapter.Champion.Equals(def.Champion, StringComparison.OrdinalIgnoreCase))
                script = adapter.Adapt(def, ability, stage, script);
        return script;
    }

    private static SimScript PlanCore(ChampionSimDefinition def, AbilitySim a, SimStage stage)
    {
        var notes = new List<string>();
        var steps = new List<SimStep>();
        var champion = stage.Champion;
        var forward = Forward(stage.Yaw);
        var right = new Vector3(forward.Z, 0f, -forward.X);   // 90 degrees clockwise seen from above

        // ---- who stands where ----
        bool passive = a.Key == "P";
        string kind = Normalise(a.TargetingKind);
        bool boundedRange = a.CastRange > 0f && a.CastRange < UnboundedRange;
        bool hasMissiles = a.Missiles.Count > 0;
        bool hasTargetSystems = a.TargetSystems.Count > 0 || a.Missiles.Any(m => m.HitEffect is not null);

        var enemy = stage.Enemy;
        float enemyDistance = Flat(enemy - champion).Length();
        var enemyAxis = enemyDistance > 1e-3f ? Flat(enemy - champion) / enemyDistance : forward;
        string placeNote = "";
        float wanted = enemyDistance;
        if (boundedRange && a.CastRange < enemyDistance + EnemyMargin)
            wanted = Math.Clamp(a.CastRange * 0.8f, EnemyMinDistance, EnemyMaxDistance);
        else if (enemyDistance > EnemyMaxDistance)
            wanted = EnemyMaxDistance;
        if (MathF.Abs(wanted - enemyDistance) > 0.5f)
        {
            enemy = At(stage, champion + enemyAxis * wanted);
            placeNote = $"enemy moved from {enemyDistance:0} to {wanted:0} units (range {(boundedRange ? a.CastRange.ToString("0", CultureInfo.InvariantCulture) : "unbounded")})";
            enemyDistance = wanted;
        }
        else enemy = At(stage, enemy);

        Vector3? ally = null;
        bool aimAtAlly = false;
        if (NeedsAlly(a))
        {
            ally = stage.Ally is { } given ? At(stage, given) : At(stage, champion + right * AllySideOffset);
            aimAtAlly = IsUnitTargeted(a.TargetingKind);
            notes.Add(a.Scenario.NeedsAlly ? "ally: the definition's scenario asks for one"
                : "ally: a boon / shield / heal with a unit target (tag rule)");
        }

        // ---- the aim ----
        SimAimTarget aimAt;
        string aimReason = "";
        switch (kind)
        {
            case "self": case "selfaoe": aimAt = SimAimTarget.Caster; break;
            case "walldetection": case "terrainlocation": case "terraintype":
                aimAt = SimAimTarget.Enemy;
                aimReason = $"{a.TargetingKind}: pending adapter (aimed at the enemy dummy)";
                notes.Add(aimReason);
                break;
            case "": aimAt = hasMissiles || hasTargetSystems ? SimAimTarget.Enemy : SimAimTarget.Caster; break;
            default: aimAt = SimAimTarget.Enemy; break;
        }
        if (passive) aimAt = SimAimTarget.Caster;
        if (aimAt == SimAimTarget.Enemy && aimAtAlly && ally is not null) aimAt = SimAimTarget.Ally;
        var aim = aimAt switch { SimAimTarget.Enemy => enemy, SimAimTarget.Ally => ally!.Value, _ => champion };

        // facing: toward the aim when it is somewhere else
        float yaw = stage.Yaw;
        var toAim = Flat(aim - champion);
        if (toAim.LengthSquared() > 1f) yaw = MathF.Atan2(toAim.X, toAim.Z);
        var aimDir = toAim.LengthSquared() > 1f ? Vector3.Normalize(toAim) : Forward(yaw);

        steps.Add(new SimStep(SimStepKind.Place, 0f, "convention",
            $"place champion, enemy{(ally is null ? "" : ", ally")}; aim at {aimAt.ToString().ToLowerInvariant()}"
            + (placeNote.Length > 0 ? "; " + placeNote : "") + (aimReason.Length > 0 ? "; " + aimReason : ""))
        { From = champion, To = aim, Name = a.Key });

        // ---- clip and composite ----
        float? clipSeconds = a.Clip?.DurationSeconds;
        if (a.Clip is { } clip)
            steps.Add(new SimStep(SimStepKind.Clip, 0f, clip.Source is "authored" or "convention" or "adapter" ? clip.Source : "convention",
                $"clip {clip.Name}" + (clipSeconds is { } cs ? $" ({cs.ToString("0.###", CultureInfo.InvariantCulture)} s)" : ""))
            { Name = clip.Name, EndTime = clipSeconds ?? 0f });
        steps.Add(new SimStep(SimStepKind.Composite, 0f, "convention", $"spell composite {a.Key}") { Name = a.Key });

        // ---- timing ----
        bool castAuthored = a.CastSeconds is > 0f;
        float cast = a.CastSeconds is > 0f ? a.CastSeconds.Value : 0f;
        string castSource = castAuthored ? "authored" : "convention";
        float? total = a.TotalSeconds is > 0f ? a.TotalSeconds : null;

        // ---- displacement: only with evidence ----
        float lastDisplaceEnd = 0f;
        bool teleport = a.Tags.Contains(TeleportTag, StringComparer.Ordinal);
        bool dash = a.Tags.Contains(DashTag, StringComparer.Ordinal);
        if (!passive && (teleport || dash))
        {
            float limit = float.MaxValue;
            if (boundedRange) limit = MathF.Min(limit, a.CastRange);
            if (a.Scenario.DistanceHint is > 0f) limit = MathF.Min(limit, a.Scenario.DistanceHint.Value);
            float distance = MathF.Min(limit, MathF.Max(0f, enemyDistance - DashStandOff));
            if (limit == float.MaxValue) distance = MathF.Max(0f, enemyDistance - DashStandOff);
            var dir = aimAt == SimAimTarget.Caster ? Forward(yaw) : aimDir;
            var walkable = stage.Walkable ?? (stage.Nav is { } nav ? p => NavGridPath.IsWalkable(nav, p) : null);
            string clampNote = "";
            if (walkable is not null && distance >= 1f)
            {
                float reach = WalkableReach(walkable, champion, dir, distance);
                if (reach < distance - 0.5f)
                {
                    clampNote = $" (clamped from {distance:0}: unwalkable ground)";
                    notes.Add($"{(teleport ? "blink" : "dash")}: stopped at the last walkable ground, {reach:0} of {distance:0} units");
                    distance = reach;
                }
            }
            var to = At(stage, champion + dir * distance);
            if (distance < 1f)
                notes.Add($"{(teleport ? "blink" : "dash")}: no room (enemy {enemyDistance:0} away, stand-off {DashStandOff:0}); no displacement");
            else if (teleport)
            {
                steps.Add(new SimStep(SimStepKind.Displace, cast, castSource,
                    $"blink {distance:0} units" + clampNote)
                { Displacement = SimDisplaceKind.Blink, From = champion, To = to, EndTime = cast });
                lastDisplaceEnd = cast;
            }
            else
            {
                float? candidate = total ?? clipSeconds;
                float t1; string dashSource;
                if (candidate is { } c && c > cast + 0.05f) { t1 = c; dashSource = "authored"; }
                else { t1 = cast + ConventionDashSeconds; dashSource = "convention"; }
                steps.Add(new SimStep(SimStepKind.Displace, cast, dashSource,
                    $"dash {distance:0} units" + clampNote)
                { Displacement = SimDisplaceKind.Dash, From = champion, To = to, EndTime = t1 });
                lastDisplaceEnd = t1;
            }
        }

        // ---- the hit ----
        // The unit the ability reaches: the ally for an ally-aimed spell, else the enemy dummy (also for a self-centred spell
        // that launches missiles, like Kalista R). Distances are measured from where the caster stood at the cast, which is
        // what the composite draws: it builds its missiles from the caster's position when the event starts.
        var hitTarget = aimAt == SimAimTarget.Ally ? "ally" : "enemy";
        var hitUnit = aimAt == SimAimTarget.Ally ? ally!.Value : enemy;
        float hitDistance = Flat(hitUnit - champion).Length();
        bool hitPlanned = false;
        float lastHit = 0f;
        if (!passive && hasMissiles)
        {
            var m = PickMissile(a)!;
            string who = m.Effect?.Name ?? m.SpellName ?? a.Key;
            if (FlightSeconds(m, hitDistance) is { } flight)
            {
                float at = cast + flight;
                if (at > MaxSeconds)
                    notes.Add($"{who}: flight {flight:0.##} s is past the {MaxSeconds:0} s cap; no hit step");
                else
                {
                    // A missile that stops at the first unit ends at the one it hits - when that unit is on its line. Piercing keeps flying.
                    bool piercing = a.Tags.Contains(PiercingTag, StringComparer.Ordinal);
                    float lane = (m.Width > 0f ? m.Width * 0.5f : 0f) + UnitRadius;
                    float reachRange = boundedRange ? a.CastRange : 1000f;
                    bool stops = !piercing && aimAt == SimAimTarget.Enemy && a.Tags.Contains(StopsFirstHitTag, StringComparer.Ordinal)
                                 && OnLine(champion, hitUnit, hitUnit, reachRange, lane);
                    steps.Add(new SimStep(SimStepKind.Hit, at, m.Source == "authored" && m.MotionSource is not null ? "authored" : "convention",
                        $"{who} hits {hitTarget}" + (stops ? " and stops" : piercing ? " and flies on" : ""))
                    { Name = hitTarget, From = champion, To = hitUnit, EndTime = at, StopsMissile = stops });
                    hitPlanned = true; lastHit = at;
                }
            }
            else
            {
                steps.Add(new SimStep(SimStepKind.Hit, cast, "convention", $"{who} hits {hitTarget} (no authored speed: at the cast)")
                { Name = hitTarget, From = champion, To = hitUnit, EndTime = cast });
                hitPlanned = true; lastHit = cast;
            }
        }
        else if (!passive && hasTargetSystems && kind != "self")
        {
            // targeted / area / cone / melee, and a self-centred area: the target systems play at the cast
            bool inside = kind != "selfaoe" || a.CastRadius <= 0f || hitDistance <= a.CastRadius;
            if (inside)
            {
                string who = a.TargetSystems.FirstOrDefault(s => s.Name is not null)?.Name ?? a.Key;
                steps.Add(new SimStep(SimStepKind.Hit, cast, castAuthored ? "authored" : "convention", $"{who} lands on {hitTarget}")
                { Name = hitTarget, From = champion, To = hitUnit, EndTime = cast });
                hitPlanned = true; lastHit = cast;
            }
            else notes.Add($"self area: the enemy is {hitDistance:0} away, radius {a.CastRadius:0}; no hit");
        }

        // ---- channel ----
        float channelEnd = 0f;
        if (!passive && a.Tags.Contains(ChannelTag, StringComparer.Ordinal))
        {
            float? until = total ?? clipSeconds;
            if (until is { } u && u > cast)
            {
                steps.Add(new SimStep(SimStepKind.Channel, cast, "authored", $"channel until {u.ToString("0.###", CultureInfo.InvariantCulture)} s")
                { EndTime = u });
                channelEnd = u;
            }
            else notes.Add("channel: no total time or clip length to hold for");
        }

        // ---- the end ----
        float end;
        string endSource = "convention";
        if (passive) end = PassiveSeconds;
        else
        {
            end = MathF.Max(clipSeconds ?? 0f, channelEnd);
            if (hitPlanned) end = MathF.Max(end, lastHit + HitLinger);
            if (lastDisplaceEnd > 0f || steps.Any(x => x.Kind == SimStepKind.Displace)) end = MathF.Max(end, lastDisplaceEnd + DisplaceLinger);
            if (end <= 0f) end = 1f;
            if (end > MaxSeconds) { end = MaxSeconds; notes.Add($"end: capped at {MaxSeconds:0} s"); }
        }
        steps.Add(new SimStep(SimStepKind.End, end, endSource, $"end at {end.ToString("0.##", CultureInfo.InvariantCulture)} s, then clean up")
        { EndTime = end });

        // stable order: by time, then by kind (End is always last)
        var ordered = steps.Select((s, i) => (s, i))
            .OrderBy(x => x.s.Time).ThenBy(x => x.s.Kind == SimStepKind.End ? 1 : 0).ThenBy(x => x.s.Kind).ThenBy(x => x.i)
            .Select(x => x.s).ToList();

        return new SimScript(def.Champion, a.Key, champion, yaw, enemy, ally, aimAt, aim, a.TargetingKind, ordered, end, notes);
    }

    /// <summary>How far from <paramref name="from"/> along <paramref name="dir"/> the ground stays walkable, up to <paramref name="distance"/>.</summary>
    private static float WalkableReach(Func<Vector3, bool> walkable, Vector3 from, Vector3 dir, float distance)
    {
        float reach = 0f;
        for (float d = WalkSampleStep; ; d += WalkSampleStep)
        {
            float at = MathF.Min(d, distance);
            if (!walkable(from + dir * at)) return reach;
            reach = at;
            if (at >= distance) return distance;
        }
    }

    /// <summary>Is <paramref name="unit"/> within <paramref name="halfWidth"/> of the line from the caster toward <paramref name="aim"/>, no further than <paramref name="length"/>?</summary>
    private static bool OnLine(Vector3 caster, Vector3 aim, Vector3 unit, float length, float halfWidth)
    {
        var dir = Flat(aim - caster);
        if (dir.LengthSquared() < 1e-6f) return false;
        dir = Vector3.Normalize(dir);
        var rel = Flat(unit - caster);
        float along = Vector3.Dot(rel, dir);
        return along >= 0f && along <= length && (rel - dir * along).Length() <= halfWidth;
    }

    /// <summary>The missile whose arrival counts as the hit: a visual, outbound one, the earliest. Null when the ability has none.</summary>
    private static MissileSim? PickMissile(AbilitySim a)
    {
        var outbound = a.Missiles.Where(m => m.SpellName?.Contains("Return", StringComparison.OrdinalIgnoreCase) != true).ToList();
        if (outbound.Count == 0) outbound = a.Missiles.ToList();
        var moving = outbound.Where(m => m.Speed is > 0f || m.Duration is > 0f).ToList();
        var visual = moving.Where(m => m.Effect?.Visual == true).ToList();
        var pool = visual.Count > 0 ? visual : moving.Count > 0 ? moving : outbound;
        // earliest arrival over a nominal distance; stable on ties
        return pool.OrderBy(m => FlightSeconds(m, 1000f) ?? float.MaxValue).FirstOrDefault();
    }

    /// <summary>Seconds a missile takes over <paramref name="distance"/>; null when the data gives neither a speed nor a duration.</summary>
    public static float? FlightSeconds(MissileSim m, float distance) => m.MotionKind switch
    {
        "ConstantSpeed" when m.Speed is > 0f => distance / m.Speed.Value,
        "FixedDuration" when m.Duration is > 0f => m.Duration.Value,
        _ => m.Speed is > 0f ? distance / m.Speed.Value : m.Duration is > 0f ? m.Duration.Value : null,
    };

    private static Vector3 At(SimStage stage, Vector3 p) =>
        stage.GroundHeight is { } h ? new Vector3(p.X, h(p), p.Z) : p;

    private static Vector3 Forward(float yaw) => new(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
    private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);
    private static string Normalise(string? kind) => (kind ?? "").Trim().ToLowerInvariant();
}
