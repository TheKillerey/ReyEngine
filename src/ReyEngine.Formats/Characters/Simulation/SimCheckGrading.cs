using System.Globalization;

namespace ReyEngine.Formats.Characters.Simulation;

public enum SimCheckGrade { Complete, Incomplete, Blocked }

/// <summary>
/// M871: the numbers the grade rests on. Chosen from measurement, not tuned to a verdict - see the M871 commit message and
/// <c>docs/plans/champion-simulation.md</c> for the frames they were read off.
/// </summary>
/// <param name="MinChangedRatio">A frame after the cast counts as "changed" when its changed-pixel ratio (see <see cref="SimCheckFrames.ChangedRatio"/>) exceeds the baseline's own noise by at least this much.</param>
/// <param name="MinParticleGain">Live particles during the cast above the highest count seen before it (the map's own effects are running).</param>
/// <param name="MaxLateSeconds">A step may run this late (simulation clock) before the timeline is called off.</param>
/// <param name="DisplacementTolerance">How near (world units, XZ) the caster must get to a displacement's end point.</param>
public sealed record SimCheckThresholds(
    double MinChangedRatio = 0.0015,
    int MinParticleGain = 4,
    double MaxLateSeconds = 0.25,
    double DisplacementTolerance = 60.0)
{
    public static SimCheckThresholds Default { get; } = new();
}

/// <summary>One sampled frame of an ability run.</summary>
/// <param name="Label">"baseline", "cast", "mid", "hit" or "end".</param>
/// <param name="PlannedTime">The simulation time the sample was aimed at.</param>
/// <param name="Clock">The simulation clock when the frame was taken.</param>
/// <param name="ChangedRatio">Changed pixels / region pixels against the baseline.</param>
/// <param name="EffectRatio">The same with the caster's own box left out: change that is not just the caster's pose.</param>
public sealed record FrameEvidence(string Label, double PlannedTime, double Clock, double ChangedRatio, double EffectRatio, int LiveParticles);

public sealed record StepEvidence(string Kind, double Planned, double Clock);

public sealed record PlannedStep(string Kind, double Time);

/// <summary>Everything the run of one ability measured. The grader reads nothing else.</summary>
public sealed record AbilityEvidence
{
    public required string Key { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<PlannedStep> Planned { get; init; } = Array.Empty<PlannedStep>();
    public IReadOnlyList<StepEvidence> Trace { get; init; } = Array.Empty<StepEvidence>();
    public bool EndReached { get; init; }
    public bool TimedOut { get; init; }
    public double TimeoutSeconds { get; init; }

    /// <summary>The clip the player was supposed to play: the SAME resolution the player uses (M873: <c>MeshPreviewViewModel.ChooseSimClip</c>).</summary>
    public string? ClipExpected { get; init; }
    /// <summary>How the player chose <see cref="ClipExpected"/>: "authored", "authored-composite", "definition" or "composite".</summary>
    public string? ClipChoiceSource { get; init; }
    /// <summary>M873: the file of the definition's own clip (the base skin's authored one); null when the definition has none.</summary>
    public string? ClipDefinition { get; init; }
    /// <summary>M873: the spell authors a clip name. Then the clip the player plays must be the definition's (the check runs on the base skin).</summary>
    public bool ClipAuthored { get; init; }
    /// <summary>
    /// M873: the data gives this ability visuals of its own (caster, target, return or missile systems, or visual clip particle events). Then COMPLETE needs
    /// those effects on screen - the effect ratio with the caster's box left out, or a particle gain - and the caster's pose changing is not enough.
    /// </summary>
    public bool ExpectsEffects { get; init; }
    /// <summary>The clip the animation player held while the cast ran (null: none was playing).</summary>
    public string? ClipSeen { get; init; }
    /// <summary>The animation clock moved while that clip was in force.</summary>
    public bool ClipAdvanced { get; init; }

    public int HitsExpected { get; init; }
    public int HitsLogged { get; init; }

    public bool DisplacementExpected { get; init; }
    public bool DisplacementApplied { get; init; }

    /// <summary>Changed-pixel ratio of two baseline frames a moment apart: what the map moves by itself.</summary>
    public double NoiseRatio { get; init; }
    public IReadOnlyList<FrameEvidence> Frames { get; init; } = Array.Empty<FrameEvidence>();
    public int ParticleBaseline { get; init; }

    public IReadOnlyList<string> CleanupProblems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LogErrors { get; init; } = Array.Empty<string>();
    public string? Exception { get; init; }
    /// <summary>The player produced no run at all (nothing started).</summary>
    public bool NeverStarted { get; init; }
}

public sealed record AbilityGrade(SimCheckGrade Grade, IReadOnlyList<string> Reasons, IReadOnlyList<string> Notes)
{
    public bool IsComplete => Grade == SimCheckGrade.Complete;
}

/// <summary>
/// M871: the grading rules. COMPLETE needs ALL of: the clip played (when the ability has one), a frame after the cast that changed or
/// particles that appeared, no exception or error in the log, the planned steps executed in order and the End reached, the hit
/// logged when one is planned, the displacement applied when one is planned, and a clean scene afterwards. Otherwise INCOMPLETE with
/// every reason that applies. BLOCKED is the champion that did not load (decided by the caller; see <see cref="Champion"/>).
/// </summary>
public static class SimCheckGrader
{
    private static string F(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
    private static string S(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public static AbilityGrade Grade(AbilityEvidence e, SimCheckThresholds? thresholds = null)
    {
        var t = thresholds ?? SimCheckThresholds.Default;
        var reasons = new List<string>();
        var notes = new List<string>();

        if (e.Exception is { Length: > 0 } ex) reasons.Add("exception: " + ex);
        foreach (string err in e.LogErrors.Take(3)) reasons.Add("log error: " + err);
        if (e.LogErrors.Count > 3) reasons.Add($"{e.LogErrors.Count - 3} more log error(s)");

        if (e.NeverStarted)
        {
            reasons.Add("the player did not start the ability");
            return new AbilityGrade(SimCheckGrade.Incomplete, reasons, notes);
        }

        // ---- the timeline
        if (e.TimedOut) reasons.Add($"End not reached after {S(e.TimeoutSeconds)} s (timed out)");
        else if (!e.EndReached) reasons.Add("End not reached");
        reasons.AddRange(CheckSteps(e, t));

        // ---- the clip
        if (e.ClipExpected is { Length: > 0 })
        {
            if (e.ClipSeen is not { Length: > 0 }) reasons.Add($"clip missing: '{e.ClipExpected}' expected, no clip was playing");
            else if (!SameClip(e.ClipExpected, e.ClipSeen)) reasons.Add($"clip missing: '{e.ClipExpected}' expected, '{e.ClipSeen}' was playing");
            else if (!e.ClipAdvanced) reasons.Add($"clip '{e.ClipSeen}' was selected but its clock never advanced");
            // M873: Riot's authored clip wins. The check runs on the base skin, whose graph is the definition's: a different file is the player's mistake.
            if (e.ClipAuthored && e.ClipDefinition is { Length: > 0 } && !SameClip(e.ClipExpected, e.ClipDefinition))
                reasons.Add($"clip mismatch: the authored clip is '{e.ClipDefinition}', the player chose '{e.ClipExpected}' ({e.ClipChoiceSource ?? "unknown"})");
        }
        else if (e.ClipSeen is { Length: > 0 }) notes.Add($"no clip in the definition; '{e.ClipSeen}' was playing");
        if (e.ClipAuthored && e.ClipDefinition is not { Length: > 0 } && e.ClipExpected is { Length: > 0 })
            notes.Add($"the authored clip is not in the base graph; '{e.ClipExpected}' ({e.ClipChoiceSource ?? "composite"}) was played instead");

        // ---- hit and displacement
        if (e.HitsExpected > 0 && e.HitsLogged < e.HitsExpected)
            reasons.Add(e.HitsLogged == 0 ? "hit expected, none logged" : $"{e.HitsExpected} hit(s) expected, {e.HitsLogged} logged");
        if (e.DisplacementExpected && !e.DisplacementApplied) reasons.Add("displacement planned, not applied to the caster");

        // ---- what is on screen
        var after = e.Frames.Where(f => f.Label != "baseline").ToList();
        if (after.Count == 0) reasons.Add("no frame was captured after the cast");
        else
        {
            double peak = after.Max(f => f.ChangedRatio), peakEffect = after.Max(f => f.EffectRatio);
            int particlePeak = after.Max(f => f.LiveParticles);
            bool changed = peak - e.NoiseRatio >= t.MinChangedRatio;
            bool effect = peakEffect - e.NoiseRatio >= t.MinChangedRatio;
            bool particles = particlePeak - e.ParticleBaseline >= t.MinParticleGain;
            string gained = $"no live particles gained ({particlePeak} vs {e.ParticleBaseline} before)";
            if (e.ExpectsEffects)
            {
                // M873: the data says this ability has effects, so they must show: change OUTSIDE the caster's box, or new particles.
                if (!effect && !particles)
                    reasons.Add(changed
                        ? $"only the caster pose changed (effect ratio {F(peakEffect)} vs noise {F(e.NoiseRatio)}, needs +{F(t.MinChangedRatio)}) and {gained}; the ability effects never showed"
                        : $"no visible change (peak changed ratio {F(peak)} vs noise {F(e.NoiseRatio)}, needs +{F(t.MinChangedRatio)}) and {gained}");
                else if (!effect) notes.Add($"no pixel change outside the caster, but {particlePeak - e.ParticleBaseline} particle(s) gained");
                else if (!particles) notes.Add($"effect pixels changed (effect ratio {F(peakEffect)}); no particle gain ({particlePeak} vs {e.ParticleBaseline})");
            }
            else if (!changed && !particles)
                reasons.Add($"no visible change (peak changed ratio {F(peak)} vs noise {F(e.NoiseRatio)}, needs +{F(t.MinChangedRatio)}) and {gained}");
            else if (e.ClipExpected is { Length: > 0 } && !particles)
                notes.Add("animation only - no visual systems in the data");
        }

        // ---- cleanup
        foreach (string problem in e.CleanupProblems) reasons.Add("cleanup: " + problem);

        return new AbilityGrade(reasons.Count == 0 ? SimCheckGrade.Complete : SimCheckGrade.Incomplete, reasons, notes);
    }

    /// <summary>
    /// M873: does the data give <paramref name="a"/> effects of its own? Caster, target or return systems, a missile effect or hit effect, or a clip particle event
    /// whose system is not known to be invisible. An unresolved system counts (the data says there is one); one known to have no visual emitter does not.
    /// </summary>
    public static bool ExpectsEffects(AbilitySim a)
    {
        static bool Visible(SimSystemRef? r) => r is not null && r.Visual != false;
        return a.CasterSystems.Any(Visible) || a.TargetSystems.Any(Visible) || a.ReturnSystems.Any(Visible)
               || a.Missiles.Any(m => Visible(m.Effect) || Visible(m.HitEffect))
               || a.ClipParticleEvents.Any(p => Visible(p.System));
    }

    private static bool SameClip(string expected, string seen) =>
        string.Equals(Path.GetFileName(expected.Replace('\\', '/')), Path.GetFileName(seen.Replace('\\', '/')), StringComparison.OrdinalIgnoreCase);

    /// <summary>The executed steps against the planned ones: same kinds in the same order, none early, none later than the allowance.</summary>
    private static IEnumerable<string> CheckSteps(AbilityEvidence e, SimCheckThresholds t)
    {
        var problems = new List<string>();
        var planned = e.Planned;
        var trace = e.Trace;
        if (planned.Count == 0) { problems.Add("the plan has no steps"); return problems; }
        int common = Math.Min(planned.Count, trace.Count);
        for (int i = 0; i < common; i++)
        {
            if (!string.Equals(planned[i].Kind, trace[i].Kind, StringComparison.Ordinal))
            {
                problems.Add($"step {i + 1} was {trace[i].Kind}, planned {planned[i].Kind}");
                return problems;
            }
            if (trace[i].Clock < trace[i].Planned - 1e-4) problems.Add($"{trace[i].Kind} ran early ({S(trace[i].Clock)} s, planned {S(trace[i].Planned)} s)");
            else if (trace[i].Clock > trace[i].Planned + t.MaxLateSeconds) problems.Add($"{trace[i].Kind} ran late ({S(trace[i].Clock)} s, planned {S(trace[i].Planned)} s)");
        }
        if (trace.Count < planned.Count && !e.TimedOut)
            problems.Add("steps not executed: " + string.Join(", ", planned.Skip(trace.Count).Select(p => p.Kind)));
        else if (trace.Count > planned.Count)
            problems.Add("more steps executed than planned");
        return problems;
    }

    /// <summary>An entry that only owns sub-casts is as good as they are: COMPLETE when every sub-cast is, else INCOMPLETE naming them.</summary>
    public static AbilityGrade Wrapper(IReadOnlyList<(string Key, AbilityGrade Grade)> subCasts)
    {
        if (subCasts.Count == 0) return new AbilityGrade(SimCheckGrade.Incomplete, new[] { "a wrapper with no sub-cast to play" }, Array.Empty<string>());
        var bad = subCasts.Where(s => !s.Grade.IsComplete).Select(s => s.Key).ToList();
        return bad.Count == 0
            ? new AbilityGrade(SimCheckGrade.Complete, Array.Empty<string>(), new[] { "covered by its sub-casts: " + string.Join(", ", subCasts.Select(s => s.Key)) })
            : new AbilityGrade(SimCheckGrade.Incomplete, new[] { "sub-cast(s) incomplete: " + string.Join(", ", bad) }, Array.Empty<string>());
    }

    /// <summary>The champion's status: BLOCKED when it did not load, COMPLETE when every ability is, else INCOMPLETE.</summary>
    public static SimCheckGrade Champion(bool loaded, IEnumerable<AbilityGrade> abilities)
    {
        if (!loaded) return SimCheckGrade.Blocked;
        var list = abilities.ToList();
        return list.Count > 0 && list.All(a => a.IsComplete) ? SimCheckGrade.Complete : SimCheckGrade.Incomplete;
    }

    /// <summary>Did the caster really travel along a displacement? Positions are sampled XZ while the run was after the displacement's end.</summary>
    public static bool DisplacementApplied(float fromX, float fromZ, float toX, float toZ, IReadOnlyList<(double Clock, float X, float Z)> samples,
        double displaceEnd, SimCheckThresholds? thresholds = null)
    {
        double tol = (thresholds ?? SimCheckThresholds.Default).DisplacementTolerance;
        double total = Math.Sqrt((toX - fromX) * (toX - fromX) + (toZ - fromZ) * (toZ - fromZ));
        if (total < 1e-3) return true;                       // nothing to travel
        double Dist(float x, float z) => Math.Sqrt((x - toX) * (x - toX) + (z - toZ) * (z - toZ));
        // reached the end point at or after the span ended ...
        if (samples.Any(s => s.Clock >= displaceEnd - 1e-3 && Dist(s.X, s.Z) <= tol)) return true;
        // ... or, when no sample fell after the span, got most of the way there
        if (!samples.Any(s => s.Clock >= displaceEnd - 1e-3))
            return samples.Any(s => Dist(s.X, s.Z) <= total * 0.5);
        return false;
    }
}
