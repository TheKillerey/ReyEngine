namespace ReyEngine.Formats.Characters.Simulation;

public sealed record SamplePoint(string Label, double Time);

/// <summary>M871: at which simulation times an ability's frames are taken.</summary>
public static class SimCheckSamples
{
    /// <summary>How long after the cast starts the "cast" frame is taken: a few frames for the first particles to be born.</summary>
    public const double CastDelay = 0.15;
    /// <summary>How long after the hit lands the "hit" frame is taken.</summary>
    public const double HitDelay = 0.10;
    /// <summary>The "end" frame is taken this long before the End step.</summary>
    public const double EndLead = 0.20;
    /// <summary>Two samples closer than this are one (a pause and a frame are not worth less than it).</summary>
    public const double MinSpacing = 0.05;

    /// <summary>
    /// The frames after the baseline (taken at t = 0 before the clock moves): cast (the composite's or clip's start + 0.15 s), mid (50% of
    /// the run), hit (the Hit step + 0.1 s, when there is one) and end (End - 0.2 s). In time order; a sample inside the spacing of another
    /// is dropped (cast, hit and end win over mid), and none lies at or after the End.
    /// </summary>
    public static IReadOnlyList<SamplePoint> Plan(IReadOnlyList<(string Kind, double Time)> steps, double endTime)
    {
        if (!(endTime > 0.0)) return Array.Empty<SamplePoint>();
        double castStart = steps.Where(s => s.Kind is "Composite" or "Clip").Select(s => s.Time).DefaultIfEmpty(0.0).Min();
        var wanted = new List<SamplePoint> { new("cast", castStart + CastDelay) };
        if (steps.FirstOrDefault(s => s.Kind == "Hit") is { Kind: "Hit" } hit) wanted.Add(new SamplePoint("hit", hit.Time + HitDelay));
        wanted.Add(new SamplePoint("end", Math.Max(0.0, endTime - EndLead)));
        wanted.Add(new SamplePoint("mid", endTime * 0.5));      // last in priority: the frame the others replace first

        // priority order decides who survives a collision; the result is in time order
        var kept = new List<SamplePoint>();
        foreach (var p in wanted.Select(p => p with { Time = Math.Min(p.Time, endTime - 0.02) }))
        {
            if (p.Time < 0.0) continue;
            if (kept.Any(k => Math.Abs(k.Time - p.Time) < MinSpacing)) continue;
            kept.Add(p);
        }
        var result = kept.OrderBy(k => k.Time).ToList();
        return result;
    }
}
