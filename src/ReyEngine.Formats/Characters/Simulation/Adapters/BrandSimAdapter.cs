using static ReyEngine.Formats.Characters.Simulation.Adapters.SimAdapterKit;

namespace ReyEngine.Formats.Characters.Simulation.Adapters;

/// <summary>
/// M903: Brand's passive (Blaze) on the stage. The data names the systems and nothing else: the skin carries Brand_Base_P_FireMark, FireMark2 and FireMark3 (the stack marks on the
/// burning enemy), Brand_Base_P_Timer and redring (the countdown and the blast radius around it) and Brand_Base_P_Apoc_Detonate_big (the detonation); BrandAblaze and
/// BrandAblazeDetonateMarker are buff spells whose BuffData authors no effect, the passive has no missile and no hit, and the compiled script that spawns the effects ships in no bin.
/// Riot's P thumbnail shows Brand's own body unchanged and the marks on the enemies, so they play on the enemy dummy, in the order the stacks go: mark 1, 2, 3, then the fuse
/// (timer and ring) and the detonation. Every time below is a convention (Source "adapter"); which system belongs to which stack is read from the system's name.
/// </summary>
public sealed class BrandSimAdapter : ISimAdapter
{
    public string Champion => "Brand";

    public const float FirstMarkAt = 0.3f;     // convention: the first hit
    public const float StackSeconds = 1.2f;    // convention: the time between two stacks (not data)
    public const float FuseSeconds = 2f;       // convention: the third mark to the detonation (the tooltip's 2 s, not data)

    public SimScript Adapt(ChampionSimDefinition def, AbilitySim ability, SimStage stage, SimScript script)
    {
        if (ability.Key != "P" || ability.ParentKey is not null) return script;
        var enemy = script.EnemyPosition;
        const string why = "which system is which stack comes from its name (FireMark, FireMark2, FireMark3, Timer, redring, Apoc_Detonate_big); the times between the stacks and the fuse are not data";

        float second = FirstMarkAt + StackSeconds, third = second + StackSeconds, boom = third + FuseSeconds;
        var plan = new (string System, float From, float To, string Text)[]
        {
            ("Brand_Base_P_FireMark", FirstMarkAt, second, "stack 1"),
            ("Brand_Base_P_FireMark2", second, third, "stack 2"),
            ("Brand_Base_P_FireMark3", third, boom, "stack 3"),
            ("Brand_Base_P_Timer", third, boom, "the fuse"),
            ("Brand_Base_P_redring", third, boom, "the blast radius"),
            ("Brand_Base_P_Apoc_Detonate_big", boom, boom + 1.2f, "the detonation"),
        };
        var extra = new List<SimStep>();
        var played = new List<string>();
        foreach (var (system, from, to, text) in plan)
            if (Effect(def, system, from, to, "enemy", enemy, $"{text}: {why}") is { } step) { extra.Add(step); played.Add(step.Resource!); }
        if (extra.Count == 0) return script;

        // the composite keeps the caster-side systems; the ones the script plays by stack are not played again at the cast
        var keep = script.Steps.Select(s => s.Kind == SimStepKind.Composite
            ? s with { Exclude = (s.Exclude ?? Array.Empty<string>()).Concat(played).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                       Why = "the stack marks, the fuse and the detonation play on the enemy dummy by the adapter's timeline; the composite keeps the rest" }
            : s);
        return Rebuild(script, keep, extra, boom + 1.2f,
            new[] { "Brand P: adapter script - FireMark 1, 2, 3, then the timer and ring, then the detonation, all on the enemy dummy" });
    }
}
