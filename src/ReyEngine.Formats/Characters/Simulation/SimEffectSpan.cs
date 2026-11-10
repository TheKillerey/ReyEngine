using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Characters.Simulation;

/// <summary>
/// M896: how long one VFX system plays on its own, measured from the moment it starts, as the character playback simulates it
/// (<c>EmitSingleOnce</c>: an isSingleParticle burst emits once). <see cref="Seconds"/> is when its last particle dies when nothing stops it;
/// <see cref="Endless"/> says it never ends by itself (an emitter with no <c>emitterLifetime</c> that keeps emitting, or an immortal particle -
/// <c>particleLifetime</c> &lt;= 0) and only a stop ends it; <see cref="StopTail"/> is how long its particles then need to die out
/// (<c>particleLinger</c>, else the particle's remaining life).
/// </summary>
public readonly record struct SimSystemSpan(float Seconds, bool Endless, float StopTail, string Name);

/// <summary>M896: the natural duration of a VFX system, children included. Pure; a hard depth cap guards against child cycles.</summary>
public static class SimEffectSpan
{
    public const int MaxChildDepth = 3;

    /// <param name="child">Resolves a child-set identifier to its system (an <c>effectKey</c> through the skin's resource map, an <c>effect</c> link directly); null skips children.</param>
    public static SimSystemSpan Of(VfxSystemDefinition system, Func<VfxChildIdentifier, VfxSystemDefinition?>? child = null) =>
        Of(system, child, 0);

    private static SimSystemSpan Of(VfxSystemDefinition system, Func<VfxChildIdentifier, VfxSystemDefinition?>? child, int depth)
    {
        float seconds = 0f, tail = 0f;
        bool endless = false;
        foreach (var e in system.Emitters)
        {
            if (e.Disabled || !e.IsVisual) continue;
            float start = MathF.Max(0f, e.TimeBeforeFirstEmission);
            float life = e.ParticleLifetime.Sample(0f) * ProbMax(e.ParticleLifetime);
            bool immortal = !(life > 0f);
            bool continuous = !e.IsSingleParticle && !(e.EmitterLifetime is > 0f) && MaxRate(e.Rate) > 0f;
            // a single burst (and a rate-0 continuous emitter, which emits its one first particle) has its last birth at its first emission
            float lastBirth = e.IsSingleParticle || MaxRate(e.Rate) <= 0f ? start : e.EmitterLifetime is > 0f ? start + e.EmitterLifetime.Value : start;
            if (continuous || immortal) endless = true;
            else seconds = MathF.Max(seconds, lastBirth + life);
            tail = MathF.Max(tail, e.ParticleLinger > 0f ? e.ParticleLinger : immortal ? 0f : life);

            if (e.Children is { } set && child is not null && depth < MaxChildDepth)
                foreach (var id in set.Children)
                {
                    if (id.IsEmpty || child(id) is not { } kid || ReferenceEquals(kid, system)) continue;
                    var k = Of(kid, child, depth + 1);
                    float born = lastBirth + (set.EmitOnDeath && !immortal ? life : 0f);
                    if (k.Endless) endless = true;
                    seconds = MathF.Max(seconds, born + k.Seconds);
                    tail = MathF.Max(tail, k.StopTail);
                }
        }
        return new SimSystemSpan(seconds, endless, tail, system.Name);
    }

    private static float MaxRate(VfxCurveF rate) =>
        rate.Values is { Length: > 0 } v ? MathF.Max(rate.Constant, v.Max()) : rate.Constant;

    private static float ProbMax(VfxCurveF curve) =>
        curve.Prob is { Length: > 0 } p && !p[0].IsEmpty ? MathF.Max(1f, p[0].Values.Max()) : 1f;
}

/// <summary>
/// M896: which of a spell's <c>DataValues</c> is the duration of its lasting effect. A NAME rule (a convention over Riot's tooltip names, not a
/// documented link): the value named exactly "Duration" wins; else the longest positive value whose name ends in "Duration" and whose last word before
/// it does not name a debuff on the target, a cooldown or a timer ("SlowDuration", "ChillDuration", "StackDuration", "PoisonDuration"...), because those
/// time something on the enemy rather than the zone / field / object the caster leaves. "SlowZoneDuration" counts (its last word is "Zone").
/// Measured on 16.22: Morgana W WDuration 5, Singed Q CloudDuration 3.25, Lux E SlowZoneDuration 5, Ziggs E MineDuration 10, Teemo R MushroomDuration 5,
/// Gangplank R ZoneDuration 8, Rumble R TrailDuration 4.5, MissFortune E BaseDuration 2.
/// </summary>
public static class SimDuration
{
    private static readonly HashSet<string> NotTheEffect = new(StringComparer.OrdinalIgnoreCase)
    {
        "Slow", "Stun", "Root", "Snare", "Silence", "Fear", "Flee", "Charm", "Taunt", "Knock", "Knockup", "Knockback", "Airborne", "Blind", "Sleep",
        "Suppress", "Suppression", "Grounded", "Cooldown", "CD", "Mark", "Reveal", "Disarm", "Polymorph", "Stasis", "Cast", "Windup", "Lockout", "Delay",
        "Chill", "Stack", "Stacks", "Debuff", "Poison", "Linger", "Haste", "MS", "Threshold", "CC", "Chain", "Shred", "Wound", "Wounds", "Burn", "Vfx",
    };

    public static KeyValuePair<string, float>? Pick(IReadOnlyList<KeyValuePair<string, float>>? values)
    {
        if (values is null || values.Count == 0) return null;
        foreach (var v in values)
            if (v.Key.Equals("Duration", StringComparison.OrdinalIgnoreCase) && v.Value > 0f) return v;
        KeyValuePair<string, float>? best = null;
        foreach (var v in values)
        {
            if (!(v.Value > 0f) || !v.Key.EndsWith("Duration", StringComparison.OrdinalIgnoreCase)) continue;
            if (NotTheEffect.Contains(LastWord(v.Key[..^"Duration".Length]))) continue;
            if (best is null || v.Value > best.Value.Value) best = v;
        }
        return best;
    }

    /// <summary>The last CamelCase word: "SlowZone" -> "Zone", "DeathVFX" -> "VFX", "W" -> "W".</summary>
    private static string LastWord(string s)
    {
        if (s.Length == 0) return "";
        int i = s.Length - 1;
        if (char.IsUpper(s[i])) { while (i > 0 && char.IsUpper(s[i - 1])) i--; return s[i..]; }   // a trailing acronym
        while (i > 0 && !char.IsUpper(s[i])) i--;
        return s[i..];
    }
}
