using System.Globalization;
using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Characters.Simulation;

public enum SimStepKind
{
    Place, Clip, Composite, Displace, Channel, Hit, End,
    /// <summary>M874: a basic attack on a unit, from the champion's own attack spell (clip, cast point, missile, hit effect).</summary>
    Attack,
    /// <summary>M874: a summoned unit appears (a character model with its own skin, clips and optional waypoint path).</summary>
    Spawn,
    /// <summary>M874: a summoned unit is removed.</summary>
    Despawn,
    /// <summary>M874: a unit is hidden (the ally inside Kalista).</summary>
    Hide,
    /// <summary>M874: a hidden unit is shown again.</summary>
    Show,
    /// <summary>M874: a non-caster unit (the enemy dummy, the ally dummy or a spawned unit) moves from A to B over a time span.</summary>
    MoveUnit,
    /// <summary>M874: one named VFX system plays on a unit or at a position for a time span (stack markers, vanish cones).</summary>
    Effect,
    /// <summary>M893: a returning missile flies back from where it arrived to the caster (Ahri Q, Sivir Q, Ekko Q). From = the turn-around point, To = the caster, Time..EndTime = the flight back.</summary>
    Return,
    /// <summary>M896: the systems of the cast that never end by themselves (a zone, a storm, a buff loop) stop emitting; their particles die out before the End step.
    /// Its <see cref="SimStep.Why"/> says where the hold came from (an authored duration or the convention).</summary>
    Release,
}

public enum SimDisplaceKind
{
    Dash, Blink,
    /// <summary>M874: an arc of the caster with a height (a hop, a leap, a wall jump).</summary>
    Jump,
}

/// <summary>M874: how a <see cref="SimStepKind.MoveUnit"/> moves its unit.</summary>
public enum SimMoveKind { Pull, Fling, Knockup }

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

    // ---- M874: the fields the scripted-mechanics steps carry (all optional; the older steps leave them empty) ----

    /// <summary>The unit a step is about: "caster", "enemy", "ally" or the id of a spawned unit ("sentinel").</summary>
    public string? Unit { get; init; }
    /// <summary>Arc height in world units for a Jump or a Knockup.</summary>
    public float Height { get; init; }
    /// <summary>Units per second where a step moves a unit at a speed (a Spawn's walk, a Pull, a Fling).</summary>
    public float? Speed { get; init; }
    public SimMoveKind? Move { get; init; }
    /// <summary>A Spawn: waypoints walked in order at <see cref="Speed"/>; with <see cref="Loop"/> the last leg returns to the first waypoint and repeats (a patrol).</summary>
    public IReadOnlyList<Vector3>? Path { get; init; }
    public bool Loop { get; init; }
    /// <summary>A Spawn: the character folder and skin ("KalistaSpawn" + the loaded skin's number, 0 when it has no sentinel skin of its own). An Effect: the system name.</summary>
    public string? Resource { get; init; }
    /// <summary>A Spawn: the graph clip names to play while standing and while walking.</summary>
    public string? IdleClip { get; init; }
    public string? RunClip { get; init; }
    /// <summary>The clip a step plays by itself (an Attack's, a hop's): the definition's clip, resolved in the loaded skin like a spell's.</summary>
    public SimClip? Clip { get; init; }
    /// <summary>An Attack: seconds from its start to the hit (the record's cast point plus the missile's flight).</summary>
    public float HitAfter { get; init; }
    /// <summary>Why a value of this step is not data: "hop distance 300 = castRangeDisplayOverride; the record has no distance". Shown by the UI and the report.</summary>
    public string? Why { get; init; }
    /// <summary>A Composite step: caster systems (by name) that are NOT played at the caster because they belong to another unit (Kalista W's sentinel effects).</summary>
    public IReadOnlyList<string>? Exclude { get; init; }
    /// <summary>An Effect: the bone to ride on the unit, or null for its root. The data names none for the units the adapters use.</summary>
    public string? Bone { get; init; }
    /// <summary>A Composite step of another ability than the run's own (Kalista's Q inside her E, Talon's W, Q and R inside his passive): that ability planned on the stage as it is at that moment.
    /// The player aims the cast from it.</summary>
    public SimScript? Sub { get; init; }
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
    /// <summary>M896: the most seconds the effects of a cast may extend a script to. A convention: long enough for the authored zones and buffs of the
    /// abilities measured, short enough for a check run; a longer one is clipped and the plan says so in its notes.</summary>
    public const float EffectMaxSeconds = 10f;
    /// <summary>M896: how long a system that never ends by itself is held when the spell authors no duration. A convention (the particle preview's own
    /// endless-emitter cycle, VfxParticleSimulator.InfiniteRunSeconds), counted from the system's start.</summary>
    public const float EndlessHoldSeconds = 3f;
    /// <summary>M896: the most seconds the End waits after a Release for the stopped particles to die out.</summary>
    public const float ReleaseTailMaxSeconds = 2f;
    public const float PassiveSeconds = 3f;
    public const float ConventionDashSeconds = 0.25f;
    /// <summary>A dash stops this far short of the enemy.</summary>
    public const float DashStandOff = 150f;
    public const float AllySideOffset = 250f;
    public const float EnemyMargin = 50f, EnemyMinDistance = 250f, EnemyMaxDistance = 900f;
    /// <summary>The authored range Riot uses for "not range-limited".</summary>
    public const float UnboundedRange = 20000f;

    private static readonly string[] AllyTags = { "PositiveEffect_Boon", "Trait_Shield", "Trait_ActiveHeal" };

    /// <summary>Adapters in effect when a caller passes none (M874: Kalista and Talon).</summary>
    public static IReadOnlyList<ISimAdapter> Adapters { get; } = new ISimAdapter[] { new Adapters.KalistaSimAdapter(), new Adapters.TalonSimAdapter() };

    /// <summary>True when the ability is staged with an ally: an adapter said so, or it is a boon / shield / heal that needs a unit.</summary>
    public static bool NeedsAlly(AbilitySim a) => a.Scenario.NeedsAlly || AllyByTags(a);

    /// <summary>
    /// M874: an ability is aimed at an ally when a boon / shield / heal tag says it helps, a unit is its target, AND <c>mAffectsTypeFlags</c> has the friendly
    /// bit (0x1) set and the enemy bit (0x2) clear. THE BIT MEANINGS ARE INFERRED, not documented: measured over the twelve abilities the tag rule alone picked
    /// (16.22), 0x1 is set on every ability that is plainly friendly (Annie E 1, Kayle W 1, Milio W 1, Bard W 1029, LeeSin W 1045, Galio R 16385, Rakan E 16385,
    /// KSante E 17413) and 0x2 on every one that also hits enemies (Vladimir Q 6154, Riven E 6154, Senna Q 24575 and TahmKench R 16387 carry both bits). The tag-only
    /// rule staged an ally for Senna Q, TahmKench R, Vladimir Q and Riven E, which are not ally-aimed; flags with no friendly bit, or with the enemy bit, are not either.
    /// </summary>
    private static bool AllyByTags(AbilitySim a) =>
        IsUnitTargeted(a.TargetingKind) && a.Tags.Any(t => AllyTags.Contains(t, StringComparer.Ordinal))
        && a.AffectsTypeFlags is { } flags && (flags & FriendlyBit) != 0 && (flags & EnemyBit) == 0;

    /// <summary>Inferred (see <see cref="NeedsAlly"/>): the friendly bit of <c>mAffectsTypeFlags</c>.</summary>
    public const uint FriendlyBit = 0x1;
    /// <summary>Inferred: the enemy bit of <c>mAffectsTypeFlags</c>.</summary>
    public const uint EnemyBit = 0x2;
    /// <summary>M882: seconds between a clip-less root spell's cast and the basic attack that plays its child's clip. A convention.</summary>
    public const float FollowUpGap = 0.35f;

    private static bool IsUnitTargeted(string kind) => Normalise(kind) is "target" or "targetorlocation";

    /// <param name="playedClip">M873: the clip the player will actually play (its duration measured from the file), when that is not the definition's base-skin clip - a skin that
    /// overrides the clip with a different length. The Clip step and the script end are timed from it; null keeps the definition's clip.</param>
    /// <param name="spanOf">M896: the natural duration of a system the cast plays (<see cref="SimEffectSpan"/>), or null when it is not known. With it the End covers
    /// every caster, aim, target and clip-event system to its last particle and the systems that never end by themselves get a Release step; without it the
    /// End is the clip / hit / displacement timing alone, as before.</param>
    public static SimScript Plan(ChampionSimDefinition def, AbilitySim ability, SimStage stage, IEnumerable<ISimAdapter>? adapters = null, SimClip? playedClip = null,
        Func<SimSystemRef, SimSystemSpan?>? spanOf = null)
    {
        var script = PlanCore(def, ability, stage, playedClip, spanOf);
        foreach (var adapter in adapters ?? Adapters)
            if (adapter.Champion.Equals(def.Champion, StringComparison.OrdinalIgnoreCase))
                script = adapter.Adapt(def, ability, stage, script);
        return script;
    }

    private static SimScript PlanCore(ChampionSimDefinition def, AbilitySim a, SimStage stage, SimClip? playedClip = null, Func<SimSystemRef, SimSystemSpan?>? spanOf = null)
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
        var planClip = playedClip ?? a.Clip;
        float? clipSeconds = planClip?.DurationSeconds;
        if (planClip is { } clip)
            steps.Add(new SimStep(SimStepKind.Clip, 0f, clip.Source is "authored" or "authored-composite" or "convention" or "adapter" ? clip.Source : "convention",
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
                    // M893: a DestroyOnHit behaviour in the missile's own list decides stop vs fly-on; the tags only speak where it does not.
                    bool destroyOnHit = m.Flight?.DestroyedOnHit == true;
                    bool piercing = !destroyOnHit && a.Tags.Contains(PiercingTag, StringComparer.Ordinal);
                    float lane = (m.Width > 0f ? m.Width * 0.5f : 0f) + UnitRadius;
                    float reachRange = boundedRange ? a.CastRange : 1000f;
                    bool stops = !piercing && aimAt == SimAimTarget.Enemy && (destroyOnHit || a.Tags.Contains(StopsFirstHitTag, StringComparer.Ordinal))
                                 && OnLine(champion, hitUnit, hitUnit, reachRange, lane);
                    string bone = HitBoneOf(m.HitBone, m.HaveHitBone) ?? HitBoneOf(a.HitBoneName, a.HaveHitBone) ?? "";
                    steps.Add(new SimStep(SimStepKind.Hit, at, m.Source == "authored" && m.MotionSource is not null ? "authored" : "convention",
                        $"{who} hits {hitTarget}" + (stops ? (destroyOnHit ? " and stops (DestroyOnHit)" : " and stops") : piercing ? " and flies on" : ""))
                    {
                        Name = hitTarget, From = champion, To = hitUnit, EndTime = at, StopsMissile = stops,
                        Bone = bone.Length > 0 ? bone : null,
                        Why = bone.Length > 0 ? HitBoneWhy(bone) : null,
                    });
                    hitPlanned = true; lastHit = at;

                    // M893: a returning missile flies back to the caster from where it turned. Authored (ReturnToCasterOnMovementComplete in its own behaviours)
                    // or the sibling "...Return..." missile spell of the ability (a name convention, recorded as such).
                    var back = ReturnLeg(a, m);
                    if (back is { } leg && !stops && FlightSeconds(leg, hitDistance) is { } backFlight && at + backFlight <= MaxSeconds)
                    {
                        string legWho = leg.Effect?.Name ?? leg.SpellName ?? a.Key;
                        steps.Add(new SimStep(SimStepKind.Return, at, leg.ReturnSource == "name" ? "convention" : "authored",
                            $"{legWho} returns to the caster ({leg.ReturnSource})")
                        { Name = hitTarget, From = hitUnit, To = champion, EndTime = at + backFlight, Why = $"return leg from {leg.ReturnSource}" });
                        lastHit = at + backFlight;
                    }
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
                string bone = HitBoneOf(a.HitBoneName, a.HaveHitBone) ?? "";
                steps.Add(new SimStep(SimStepKind.Hit, cast, castAuthored ? "authored" : "convention", $"{who} lands on {hitTarget}")
                { Name = hitTarget, From = champion, To = hitUnit, EndTime = cast, Bone = bone.Length > 0 ? bone : null, Why = bone.Length > 0 ? HitBoneWhy(bone) : null });
                hitPlanned = true; lastHit = cast;
            }
            else notes.Add($"self area: the enemy is {hitDistance:0} away, radius {a.CastRadius:0}; no hit");
        }

        // ---- M882: the basic attack that plays the clip a clip-less root spell does not ----
        if (!passive && a.FollowUpAttack is { Clip: not null } follow)
        {
            float startAt = MathF.Max(cast, lastDisplaceEnd) + FollowUpGap;
            var (att, fhit, fhitAt, fclipEnd) = ReyEngine.Formats.Characters.Simulation.Adapters.SimAdapterKit.AttackOn(follow, startAt, "enemy", champion, hitUnit,
                $"{a.Key} authors no clip; its child spell {follow.Name} does, and plays on the basic attack that follows the cast (a {FollowUpGap:0.##} s gap is a convention)");
            steps.Add(att);
            if (fhit is not null) steps.Add(fhit);
            hitPlanned = true; lastHit = MathF.Max(lastHit, fhitAt);
            clipSeconds = MathF.Max(clipSeconds ?? 0f, fclipEnd);
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
        if (spanOf is not null)
        {
            float hitAt = steps.Where(x => x.Kind == SimStepKind.Hit).Select(x => x.Time).DefaultIfEmpty(cast).Min();
            var effects = EffectEnd(a, spanOf, hitAt, hitPlanned && !passive, end, cast);
            if (effects.Release is { } release)
            {
                if (release.EndTime > EffectMaxSeconds)
                    release = release with { Time = MathF.Max(0f, EffectMaxSeconds - (release.EndTime - release.Time)), EndTime = EffectMaxSeconds };
                steps.Add(release);
            }
            if (effects.End > end + 1e-4f)
            {
                notes.Add($"end: the effects play to {F2(effects.End)} s ({effects.Longest}); the timing alone ended at {F2(end)} s");
                end = effects.End;
                endSource = effects.Source;
            }
            if (end > EffectMaxSeconds)
            {
                notes.Add($"end: the effects want {F2(end)} s; clipped at the {EffectMaxSeconds:0} s cap");
                end = EffectMaxSeconds;
            }
        }
        steps.Add(new SimStep(SimStepKind.End, end, endSource, $"end at {end.ToString("0.##", CultureInfo.InvariantCulture)} s, then clean up")
        { EndTime = end });

        // stable order: by time, then by kind (End is always last)
        var ordered = steps.Select((s, i) => (s, i))
            .OrderBy(x => x.s.Time).ThenBy(x => x.s.Kind == SimStepKind.End ? 1 : 0).ThenBy(x => x.s.Kind).ThenBy(x => x.i)
            .Select(x => x.s).ToList();

        return new SimScript(def.Champion, a.Key, champion, yaw, enemy, ally, aimAt, aim, a.TargetingKind, ordered, end, notes);
    }

    private static string F2(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// M896: when the systems of a cast are done. Caster and aim systems start with the cast composite (t = 0), target systems at the hit, clip particle events
    /// at their authored second - the times the player starts them. A system that ends by itself plays to its last particle. One that never does is held
    /// until the spell's authored duration from the cast (<see cref="AbilitySim.DurationSeconds"/>), else <see cref="EndlessHoldSeconds"/> from its start
    /// and no earlier than the timing's own end; then the Release step stops it and the End waits for its particles (at most <see cref="ReleaseTailMaxSeconds"/>).
    /// Missiles are not counted: the player ends a missile at its arrival.
    /// </summary>
    public static (float End, string Source, string Longest, SimStep? Release) EffectEnd(AbilitySim a, Func<SimSystemRef, SimSystemSpan?> spanOf,
        float hitAt, bool hitPlanned, float timingEnd, float cast)
    {
        var played = new List<(SimSystemRef Ref, float Start, float? AuthoredEnd)>();
        foreach (var r in a.CasterSystems) played.Add((r, 0f, null));
        foreach (var r in a.AimSystems ?? Array.Empty<SimSystemRef>()) played.Add((r, 0f, null));
        if (hitPlanned) foreach (var r in a.TargetSystems) played.Add((r, hitAt, null));
        foreach (var e in a.ClipParticleEvents) if (e.System is { } sys) played.Add((sys, MathF.Max(0f, e.Seconds), e.EndSeconds));

        float finite = 0f, tail = 0f;
        string longest = "";
        var endless = new List<(string Name, float Start, float? AuthoredEnd)>();
        foreach (var (r, start, authoredEnd) in played)
        {
            if (r.Visual == false || spanOf(r) is not { } span) continue;
            if (span.Endless)
            {
                endless.Add((span.Name, start, authoredEnd));
                tail = MathF.Max(tail, span.StopTail);
            }
            float at = start + span.Seconds;
            // a clip event authored to stop at its end second stops there and its particles die out
            if (authoredEnd is { } ae && ae > start && at > ae + span.StopTail) at = ae + span.StopTail;
            if (at > finite) { finite = at; longest = $"{span.Name} to {F2(at)} s"; }
        }
        if (endless.Count == 0) return (finite, "convention", longest, null);

        bool authored = a.DurationSeconds is > 0f;
        float hold = authored ? cast + a.DurationSeconds!.Value
            : MathF.Max(timingEnd, endless.Max(x => x.AuthoredEnd is { } ae && ae > x.Start ? ae : x.Start + EndlessHoldSeconds));
        float stopTail = MathF.Min(tail, ReleaseTailMaxSeconds);
        float end = MathF.Max(finite, hold + stopTail);
        var names = endless.Select(x => x.Name).Distinct().ToList();
        string list = string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", ..." : "");
        string why = authored
            ? $"held for DataValues '{a.DurationField}' = {F2(a.DurationSeconds!.Value)} s from the cast (which value is the effect's duration is a name rule, SimDuration)"
            : $"the spell authors no duration; held {EndlessHoldSeconds:0} s from the system's start, no earlier than the cast's own end (a convention)";
        var release = new SimStep(SimStepKind.Release, hold, authored ? "authored" : "convention",
            $"release {names.Count} endless system(s) ({list}); their particles die out over {F2(stopTail)} s")
        { EndTime = hold + stopTail, Why = why, Name = list };
        if (hold + stopTail >= finite) longest = $"{list} held to {F2(hold)} s + {F2(stopTail)} s die-out";
        return (end, authored ? "authored" : "convention", longest, release);
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

    /// <summary>M893: the hit bone a spell authors, or null when it names none or <c>bHaveHitBone</c> is false.</summary>
    private static string? HitBoneOf(string? name, bool? have) => !string.IsNullOrWhiteSpace(name) && have != false ? name : null;

    private static string HitBoneWhy(string bone) =>
        $"mHitBoneName = {bone} (authored): the bone on the target the hit effect rides; a target without that bone gets the effect at its root";

    /// <summary>M893: the missile that flies the way back for <paramref name="outbound"/>: itself when its behaviours say ReturnToCasterOnMovementComplete, else the ability's sibling
    /// return missile (not the dead-caster variant) that has a speed or a duration. Null when the ability has no return leg.</summary>
    public static MissileSim? ReturnLeg(AbilitySim a, MissileSim outbound)
    {
        if (outbound.Flight is { ReturnsToCaster: true })
            return outbound.ReturnSource is null ? outbound with { ReturnSource = "behavior:ReturnToCasterOnMovementComplete" } : outbound;
        return a.Missiles.FirstOrDefault(x => !ReferenceEquals(x, outbound) && x.ReturnSource is not null && !x.IsDeadVariant
                                              && (x.Speed is > 0f || x.Duration is > 0f));
    }

    /// <summary>The missile whose arrival counts as the hit: a visual, outbound one, the earliest. Null when the ability has none.</summary>
    private static MissileSim? PickMissile(AbilitySim a)
    {
        var outbound = a.Missiles.Where(m => m.SpellName?.Contains("Return", StringComparison.OrdinalIgnoreCase) != true && m.ReturnSource != "name").ToList();
        if (outbound.Count == 0) outbound = a.Missiles.ToList();
        var moving = outbound.Where(m => m.Speed is > 0f || m.Duration is > 0f).ToList();
        var visual = moving.Where(m => m.Effect?.Visual == true).ToList();
        var pool = visual.Count > 0 ? visual : moving.Count > 0 ? moving : outbound;
        // earliest arrival over a nominal distance; stable on ties
        return pool.OrderBy(m => FlightSeconds(m, 1000f) ?? float.MaxValue).FirstOrDefault();
    }

    /// <summary>Seconds a missile takes over <paramref name="distance"/>; null when the data gives neither a speed nor a duration.</summary>
    public static float? FlightSeconds(MissileSim m, float distance) => m.Flight?.AcceleratingSeconds(distance) ?? m.MotionKind switch
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
