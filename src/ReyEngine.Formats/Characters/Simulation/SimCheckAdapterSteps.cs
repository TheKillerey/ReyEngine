using System.Globalization;
using System.Numerics;

namespace ReyEngine.Formats.Characters.Simulation;

/// <summary>One scripted-mechanic step the plan promised, as the check sees it.</summary>
public sealed record AdapterStepPlan(string Kind, double Time, double EndTime, string Source, string Text, string? Unit, Vector3? From, Vector3? To, float Height,
    SimMoveKind? Move, string? Resource, string? Why, bool IsJump);

/// <summary>One recorded position of a unit during the run.</summary>
public sealed record UnitObservation(double Clock, string Unit, float X, float Y, float Z, bool Visible);

/// <summary>What the run observed about the mechanics (the grader reads nothing else).</summary>
public sealed record AdapterObservations
{
    /// <summary>The executed steps (kind and planned time), in order.</summary>
    public IReadOnlyList<StepEvidence> Trace { get; init; } = Array.Empty<StepEvidence>();
    public IReadOnlyList<UnitObservation> Units { get; init; } = Array.Empty<UnitObservation>();
    /// <summary>The caster's position over the run: clock, X, Y, Z.</summary>
    public IReadOnlyList<(double Clock, float X, float Y, float Z)> Caster { get; init; } = Array.Empty<(double, float, float, float)>();
    /// <summary>Per spawned unit: the largest share of the pixels of its projected box that changed against the baseline (the unit is absent from the baseline).</summary>
    public IReadOnlyDictionary<string, double> UnitPixelChange { get; init; } = new Dictionary<string, double>();
    /// <summary>Per spawned unit: the largest share of its changed box pixels that were near-white at a sample (a unit drawn without its textures is a white silhouette).</summary>
    public IReadOnlyDictionary<string, double> UnitWhiteShare { get; init; } = new Dictionary<string, double>();
    /// <summary>Per spawned unit: whether the host built its meshes (without them nothing can be drawn).</summary>
    public IReadOnlyDictionary<string, bool> UnitHasMesh { get; init; } = new Dictionary<string, bool>();
    /// <summary>Effects that started (system names), as the player recorded them.</summary>
    public IReadOnlyList<string> EffectsStarted { get; init; } = Array.Empty<string>();
}

/// <summary>The verdict of one scripted-mechanic step.</summary>
public sealed record AdapterStepEvidence(string Kind, double Time, string Text, string Source, string? Why, bool Executed, bool Passed, string Detail);

/// <summary>
/// M874: checking the mechanics, not only the pixels. Every Spawn, Despawn, MoveUnit, Effect, Hide, Show, Attack and Jump of a script gets its own verdict:
/// it ran (it is in the trace at its planned time) and it did what it says - a spawned unit is DRAWN (changed pixels in its projected box and its meshes built), a moved unit
/// really moved (the recorded positions), a jump crossed its span in an arc, a hidden unit was hidden, an effect started.
/// </summary>
public static class SimCheckAdapterSteps
{
    /// <summary>A spawned unit counts as drawn when this share of its projected box differs from the baseline, in which it is absent.</summary>
    public const double MinUnitChangedRatio = 0.02;
    /// <summary>A unit whose changed pixels are more white than this is drawn untextured (a flat silhouette), not rendered.</summary>
    public const double MaxUnitWhiteShare = 0.6;
    /// <summary>How near (world units, XZ) a moved unit must get to the end of its span.</summary>
    public const double Tolerance = 60.0;

    public static bool IsAdapterStep(SimStep s) =>
        s.Kind is SimStepKind.Spawn or SimStepKind.Despawn or SimStepKind.MoveUnit or SimStepKind.Effect or SimStepKind.Hide or SimStepKind.Show or SimStepKind.Attack
        || s is { Kind: SimStepKind.Displace, Displacement: SimDisplaceKind.Jump };

    public static IReadOnlyList<AdapterStepPlan> Plans(SimScript script) =>
        script.Steps.Where(IsAdapterStep)
            .Select(s => new AdapterStepPlan(s.Kind.ToString(), s.Time, s.EndTime, s.Source, s.Text, s.Unit, s.From, s.To, s.Height, s.Move, s.Resource, s.Why,
                s is { Kind: SimStepKind.Displace, Displacement: SimDisplaceKind.Jump })).ToList();

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static double Dist(float x1, float z1, float x2, float z2) => Math.Sqrt((x1 - x2) * (double)(x1 - x2) + (z1 - z2) * (double)(z1 - z2));

    public static IReadOnlyList<AdapterStepEvidence> Verify(IReadOnlyList<AdapterStepPlan> plans, AdapterObservations o)
    {
        var result = new List<AdapterStepEvidence>();
        foreach (var p in plans)
        {
            bool executed = o.Trace.Any(t => string.Equals(t.Kind, p.Kind, StringComparison.Ordinal) && Math.Abs(t.Planned - p.Time) < 1e-3);
            bool passed; string detail;
            if (!executed) { passed = false; detail = "the step never ran"; }
            else (passed, detail) = p.Kind switch
            {
                nameof(SimStepKind.Spawn) => Spawn(p, o),
                nameof(SimStepKind.MoveUnit) => Move(p, o),
                nameof(SimStepKind.Hide) => Hidden(p, o, expectVisible: false),
                nameof(SimStepKind.Show) => Hidden(p, o, expectVisible: true),
                nameof(SimStepKind.Effect) => Effect(p, o),
                nameof(SimStepKind.Displace) when p.IsJump => Jump(p, o),
                _ => (true, "ran"),
            };
            result.Add(new AdapterStepEvidence(p.Kind, Math.Round(p.Time, 3), p.Text, p.Source, p.Why, executed, passed, detail));
        }
        return result;
    }

    private static (bool, string) Spawn(AdapterStepPlan p, AdapterObservations o)
    {
        string unit = p.Unit ?? "unit";
        if (!o.UnitHasMesh.GetValueOrDefault(unit)) return (false, "the host built no meshes for the unit, so nothing can be drawn");
        double changed = o.UnitPixelChange.GetValueOrDefault(unit);
        double white = o.UnitWhiteShare.GetValueOrDefault(unit);
        if (white > MaxUnitWhiteShare) return (false, $"drawn as a flat white silhouette: {F(white * 100)}% of its changed pixels are near-white (its textures are not bound)");
        return changed >= MinUnitChangedRatio
            ? (true, $"drawn: {F(changed * 100)}% of its projected box changed")
            : (false, $"not rendered: {F(changed * 100)}% of its projected box changed, needs {F(MinUnitChangedRatio * 100)}%");
    }

    /// <summary>How long after a step's end its last position may still be read (the 50 ms sampling, and a frame).</summary>
    private const double EndSlack = 0.15;

    private static (bool, string) Move(AdapterStepPlan p, AdapterObservations o)
    {
        string unit = p.Unit ?? "";
        // only the samples INSIDE the step's own span: a later step that starts where this one ended must not vouch for it
        var span = o.Units.Where(u => u.Unit == unit && u.Clock >= p.Time - 1e-3 && u.Clock <= p.EndTime + EndSlack).OrderBy(u => u.Clock).ToList();
        if (span.Count == 0 || p.From is not { } from || p.To is not { } to) return (false, "no recorded positions of the unit in the step's span");
        if (p.Move == SimMoveKind.Knockup)
        {
            double rise = span.Select(u => u.Y).Max() - from.Y;
            var last = span[^1];
            bool back = Math.Abs(last.Y - from.Y) <= Math.Max(15.0, p.Height * 0.1) && Dist(last.X, last.Z, from.X, from.Z) <= Tolerance;
            if (rise < p.Height * 0.5) return (false, $"rose only {F(rise)} of {F(p.Height)} units");
            return back ? (true, $"rose {F(rise)} of {F(p.Height)} units and came back to its spot") : (false, $"rose {F(rise)} units but did not come back to its spot by the end of the span");
        }
        double total = Dist(from.X, from.Z, to.X, to.Z);
        var end = span.Where(u => u.Clock >= p.EndTime - 0.1).DefaultIfEmpty(span[^1]).OrderBy(u => Math.Abs(u.Clock - p.EndTime)).First();
        double left = Dist(end.X, end.Z, to.X, to.Z);
        double started = span.Max(u => total - Dist(u.X, u.Z, to.X, to.Z));
        return left <= Tolerance && started >= Math.Min(total * 0.3, total) ? (true, $"moved {F(Math.Min(started, total))} of {F(total)} units and stood at the end of its span")
            : (false, $"{F(left)} units from the end of its span at its end ({F(Math.Max(0, started))} of {F(total)} covered)");
    }

    private static (bool, string) Hidden(AdapterStepPlan p, AdapterObservations o, bool expectVisible)
    {
        string unit = p.Unit ?? "";
        var after = o.Units.Where(u => u.Unit == unit && u.Clock >= p.Time - 1e-3).ToList();
        if (after.Count == 0) return (false, "no recorded state of the unit");
        bool seen = after.Any(u => u.Visible == expectVisible);
        return seen ? (true, expectVisible ? "shown again" : "hidden") : (false, expectVisible ? "never visible after the step" : "still visible after the step");
    }

    private static (bool, string) Effect(AdapterStepPlan p, AdapterObservations o) =>
        p.Resource is { } name && o.EffectsStarted.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? (true, "started") : (false, $"the system '{p.Resource}' never started (not in the skin's systems)");

    private static (bool, string) Jump(AdapterStepPlan p, AdapterObservations o)
    {
        if (p.From is not { } from || p.To is not { } to) return (false, "no span");
        var samples = o.Caster.Where(c => c.Clock >= p.Time - 1e-3 && c.Clock <= p.EndTime + EndSlack).OrderBy(c => c.Clock).ToList();
        if (samples.Count == 0) return (false, "no recorded caster positions in the span");
        double total = Dist(from.X, from.Z, to.X, to.Z);
        var last = samples.Where(c => c.Clock >= p.EndTime - 0.1).DefaultIfEmpty(samples[^1]).OrderBy(c => Math.Abs(c.Clock - p.EndTime)).First();
        double left = Dist(last.X, last.Z, to.X, to.Z);
        double rise = samples.Select(c => c.Y).Max() - from.Y;
        if (left > Tolerance) return (false, $"the caster was {F(left)} units from the landing point at the end of the span (the {F(total)}-unit span was not crossed)");
        bool arc = p.Height <= 0f || rise >= p.Height * 0.3;
        return arc ? (true, $"crossed {F(total)} units and landed, peak {F(rise)} above the start") : (false, $"crossed the span but never left the ground (rose {F(rise)} of {F(p.Height)})");
    }
}
