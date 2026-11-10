using System.Globalization;
using System.Numerics;

namespace ReyEngine.Formats.Characters.Simulation.Adapters;

/// <summary>
/// M874: the small toolbox the per-champion adapters share. Pure: it reads the definition and the stage, builds steps, and never draws.
///
/// <para>The rule every adapter follows: a value that is in a Riot record is read from the definition and says so; a value that is not (a hop
/// distance, a hold time, which hit adds a stack) is a convention, its step carries Source "adapter", and <see cref="SimStep.Why"/> says why.</para>
/// </summary>
public static class SimAdapterKit
{
    public const string Adapter = "adapter";

    public static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

    public static Vector3 Ground(SimStage stage, Vector3 p) => stage.GroundHeight is { } h ? new Vector3(p.X, h(p), p.Z) : p;

    /// <summary>The unit vector to the right of <paramref name="axis"/> seen from above (the planner's own convention).</summary>
    public static Vector3 Right(Vector3 axis) => new(axis.Z, 0f, -axis.X);

    public static float Yaw(Vector3 axis) => MathF.Atan2(axis.X, axis.Z);

    public static AbilitySim? Ability(ChampionSimDefinition def, string key) => def.Abilities.FirstOrDefault(a => a.Key == key);

    /// <summary>The canonical name of a VFX system the definition knows (any ability's caster, target, return, missile or clip-event systems), or null.
    /// Only a name the skin's resource map resolved counts: an unresolved name is never used.</summary>
    public static string? SystemName(ChampionSimDefinition def, string name)
    {
        foreach (var a in def.Abilities)
        {
            foreach (var r in a.CasterSystems.Concat(a.TargetSystems).Concat(a.ReturnSystems).Concat(a.AimSystems ?? Array.Empty<SimSystemRef>())
                         .Concat(a.Missiles.SelectMany(m => new[] { m.Effect, m.HitEffect }).Where(r => r is not null).Select(r => r!))
                         .Concat(a.ClipParticleEvents.Select(e => e.System).Where(r => r is not null).Select(r => r!)))
                if (r.Name is { } n && n.Equals(name, StringComparison.OrdinalIgnoreCase)) return n;
        }
        foreach (var at in def.Attacks)
        {
            if (at.HitEffect?.Name is { } h && h.Equals(name, StringComparison.OrdinalIgnoreCase)) return h;
            foreach (var m in at.Missiles)
                if (m.Effect?.Name is { } n && n.Equals(name, StringComparison.OrdinalIgnoreCase)) return n;
        }
        return null;
    }

    /// <summary>The basic attack a plain swing uses: the first one in the record's pool, else the first non-crit, else any.</summary>
    public static AttackSim? BasicAttack(ChampionSimDefinition def) =>
        def.Attacks.FirstOrDefault(a => a.InBasicPool && !a.IsCrit) ?? def.Attacks.FirstOrDefault(a => !a.IsCrit) ?? def.Attacks.FirstOrDefault();

    public static AttackSim? Attack(ChampionSimDefinition def, string name) =>
        def.Attacks.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>An Attack step and the Hit step it lands, from the champion's own attack spell. The clip, the cast point, the missile speed and the hit effect are the record's
    /// (Source "authored"); the decision to attack now, and on whom, is the adapter's and says so in <see cref="SimStep.Why"/>.</summary>
    public static (SimStep Attack, SimStep? Hit, float HitAt, float ClipEnd) AttackOn(AttackSim attack, float t, string unit, Vector3 from, Vector3 target, string why)
    {
        float cast = attack.CastSeconds ?? 0f;
        var missile = attack.Missiles.FirstOrDefault(m => m.Speed is > 0f || m.Duration is > 0f);
        float flight = missile is null ? 0f : SimulationPlanner.FlightSeconds(missile, Flat(target - from).Length()) ?? 0f;
        float hitAfter = cast + flight;
        float clipEnd = t + MathF.Max(attack.Clip?.DurationSeconds ?? 0f, hitAfter);
        string castBasis = attack.CastSeconds is null ? "no cast point in the record" : "the record's cast point (mCastTime, else castFrame at the clip's frame rate)";
        var step = new SimStep(SimStepKind.Attack, t, "authored",
            $"basic attack {attack.Name} on {unit}: clip {attack.ClipName}, cast {F(cast)} s ({castBasis})"
            + (missile is null ? "; melee" : $"; missile {missile.Effect?.Name ?? "?"} {F(missile.Speed ?? 0f)} u/s, flight {F(flight)} s")
            + (attack.HitEffect?.Name is { } h ? $"; hit {h}" : ""))
        {
            Name = attack.Name, Unit = unit, Clip = attack.Clip, From = from, To = target, EndTime = clipEnd, HitAfter = hitAfter, Why = why,
        };
        var hit = new SimStep(SimStepKind.Hit, t + hitAfter, "authored",
            $"{attack.Name} lands on {unit}" + (missile is null ? "" : $" ({missile.Effect?.Name ?? "missile"} arrives)"))
        {
            Name = unit, From = from, To = target, EndTime = t + hitAfter,
            Bone = HitBone(attack, missile),
            Why = HitBone(attack, missile) is { } bone
                ? $"mHitBoneName = {bone} (authored): the bone on the target the hit effect rides; a target without that bone gets the effect at its root" : null,
        };
        return (step, hit, t + hitAfter, clipEnd);
    }

    /// <summary>M893: the target bone an attack's hit effect rides (<c>mHitBoneName</c> of the missile spell, else of the attack spell), or null.</summary>
    private static string? HitBone(AttackSim attack, MissileSim? missile) =>
        missile is { HitBone: { Length: > 0 } mb } && missile.HaveHitBone != false ? mb
        : attack.HitBoneName is { Length: > 0 } ab && attack.HaveHitBone != false ? ab : null;

    /// <summary>An Effect step: one named system, on a unit or at a position, from <paramref name="t"/> to <paramref name="end"/>. A resolved name only: null when the definition does not know it.</summary>
    public static SimStep? Effect(ChampionSimDefinition def, string system, float t, float end, string unit, Vector3 at, string why,
        Vector3? travelTo = null, float? speed = null, string? bone = null, string? source = null)
    {
        if (SystemName(def, system) is not { } name) return null;
        return new SimStep(SimStepKind.Effect, t, source ?? Adapter, $"{name} on {unit}" + (travelTo is null ? "" : " (follows the unit's walk)"))
        { Resource = name, Unit = unit, From = at, To = travelTo, Speed = speed, EndTime = end, Why = why, Bone = bone };
    }

    /// <summary>Copy the steps of a planned sub-script (another ability's, or the same one on another stage) into a parent script at <paramref name="offset"/>:
    /// its hits, displacements and channels, plus one Composite step that plays that ability's cast with the hit landing when the sub-script's does.
    /// Place, Clip and End are the parent's; the sub-script itself rides on the Composite step so the player can aim that cast.</summary>
    public static IEnumerable<SimStep> Embed(SimScript sub, string abilityKey, float offset, string why, IReadOnlyList<string>? exclude = null)
    {
        float hitAt = sub.Find(SimStepKind.Hit)?.Time ?? 0f;
        yield return new SimStep(SimStepKind.Composite, offset, Adapter, $"spell composite {abilityKey}" + (hitAt > 0f ? $", hit at +{F(hitAt)} s" : ""))
        {
            Name = abilityKey, Sub = sub, From = sub.ChampionPosition, To = sub.Aim, EndTime = hitAt > 0f ? offset + hitAt : 0f, Why = why, Exclude = exclude,
        };
        foreach (var s in sub.Steps)
        {
            if (s.Kind is SimStepKind.Hit or SimStepKind.Displace or SimStepKind.Channel)
                yield return s with { Time = s.Time + offset, EndTime = s.EndTime + offset };
        }
    }

    private static SimStep Fit(SimStep s, float end, float inside)
    {
        if (s.Kind == SimStepKind.End) return s;
        float time = s.Time >= end ? MathF.Max(0f, end - inside) : s.Time;
        float stop = s.EndTime > end ? end : s.EndTime;
        return time == s.Time && stop == s.EndTime ? s : s with { Time = time, EndTime = MathF.Max(stop, time) };
    }

    /// <summary>The script with <paramref name="extra"/> added and the End moved to <paramref name="end"/> (capped), steps in stable time order, End last.</summary>
    public static SimScript Rebuild(SimScript basis, IEnumerable<SimStep> keep, IEnumerable<SimStep> extra, float end, IEnumerable<string>? notes = null,
        Vector3? champion = null, float? yaw = null, Vector3? enemy = null, Vector3? ally = null, SimAimTarget? aimAt = null, Vector3? aim = null)
    {
        // M898: the End covers the adapter's own steps, up to the effects cap (the planner's End may already be past the 8 s timing cap since M896); a step that would
        // still land at or after the capped End is pulled just inside it, so it runs before the End instead of never.
        const float inside = 0.02f;
        var all = keep.Concat(extra).Where(s => s.Kind != SimStepKind.End).ToList();
        if (all.Count > 0) end = MathF.Max(end, all.Max(s => s.Time) + inside);
        if (end > SimulationPlanner.EffectMaxSeconds) end = SimulationPlanner.EffectMaxSeconds;
        keep = keep.Select(s => Fit(s, end, inside)).ToList();
        extra = extra.Select(s => Fit(s, end, inside)).ToList();
        // every value that is not data says so: the stage layout is the editor's convention, and an adapter's Hit is placed by the adapter
        const string placeWhy = "the stage layout (the champion at the stage point, the enemy dummy ahead) is the editor's convention; the data has none";
        const string hitWhy = "when the ability lands is the adapter's script (the record has no hit time for this mechanic)";
        var steps = keep.Where(s => s.Kind != SimStepKind.End).Concat(extra)
            .Select(s => string.IsNullOrWhiteSpace(s.Why) && (s.Kind == SimStepKind.Place || s is { Kind: SimStepKind.Hit, Source: Adapter }) ? s with { Why = s.Kind == SimStepKind.Place ? placeWhy : hitWhy } : s)
            .ToList();
        steps.Add(new SimStep(SimStepKind.End, end, "convention", $"end at {F(end)} s, then clean up") { EndTime = end });
        var ordered = steps.Select((s, i) => (s, i))
            .OrderBy(x => x.s.Time).ThenBy(x => x.s.Kind == SimStepKind.End ? 1 : 0).ThenBy(x => x.s.Kind).ThenBy(x => x.i)
            .Select(x => x.s).ToList();
        return basis with
        {
            Steps = ordered, EndTime = end, Notes = basis.Notes.Concat(notes ?? Array.Empty<string>()).ToList(),
            ChampionPosition = champion ?? basis.ChampionPosition, ChampionYaw = yaw ?? basis.ChampionYaw,
            EnemyPosition = enemy ?? basis.EnemyPosition, AllyPosition = ally ?? basis.AllyPosition,
            AimAt = aimAt ?? basis.AimAt, Aim = aim ?? basis.Aim,
        };
    }

    /// <summary>A planned script of <paramref name="ability"/> without any adapter (the generic plan the adapter builds on).</summary>
    public static SimScript Generic(ChampionSimDefinition def, AbilitySim ability, SimStage stage, SimClip? playedClip = null) =>
        SimulationPlanner.Plan(def, ability, stage, Array.Empty<ISimAdapter>(), playedClip);

    /// <summary>How far from <paramref name="from"/> along <paramref name="dir"/> the first unwalkable sample lies (step <paramref name="step"/>), or null within <paramref name="max"/>.</summary>
    public static float? FirstBlocked(Func<Vector3, bool> walkable, Vector3 from, Vector3 dir, float max, float step = 10f)
    {
        for (float d = step; d <= max; d += step)
            if (!walkable(from + dir * d)) return d;
        return null;
    }

    /// <summary>The first walkable sample after <paramref name="blockedAt"/> along the ray, within <paramref name="max"/>; null when the wall does not end.</summary>
    public static float? FirstWalkableAfter(Func<Vector3, bool> walkable, Vector3 from, Vector3 dir, float blockedAt, float max, float step = 10f)
    {
        for (float d = blockedAt + step; d <= max; d += step)
            if (walkable(from + dir * d)) return d;
        return null;
    }
}
