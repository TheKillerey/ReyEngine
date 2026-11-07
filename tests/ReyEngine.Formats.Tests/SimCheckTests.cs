using System.Numerics;
using ReyEngine.Formats.Characters.Simulation;

namespace ReyEngine.Formats.Tests;

/// <summary>M871: the pure parts of <c>--simcheck</c>: pixel metrics, regions, grading, sampling, resume and the sheet layout.</summary>
public sealed class SimCheckTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "reyengine-m871-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); } catch { } }

    private static byte[] Frame(int w, int h, byte v = 10)
    {
        var f = new byte[w * h * 4];
        for (int i = 0; i < f.Length; i += 4) { f[i] = f[i + 1] = f[i + 2] = v; f[i + 3] = 255; }
        return f;
    }

    private static void Paint(byte[] f, int w, PixelRect r, byte v)
    {
        for (int y = r.Y; y < r.Bottom; y++) for (int x = r.X; x < r.Right; x++) { int o = (y * w + x) * 4; f[o] = f[o + 1] = f[o + 2] = v; }
    }

    [Fact]
    public void Changed_ratio_counts_pixels_in_the_region_only()
    {
        var a = Frame(100, 50); var b = Frame(100, 50);
        Paint(b, 100, new PixelRect(10, 10, 20, 10), 200);
        Assert.Equal(200.0 / 5000, SimCheckFrames.ChangedRatio(a, b, 100, 50, new PixelRect(0, 0, 100, 50)), 6);
        Assert.Equal(1.0, SimCheckFrames.ChangedRatio(a, b, 100, 50, new PixelRect(10, 10, 20, 10)), 6);
        Assert.Equal(0.0, SimCheckFrames.ChangedRatio(a, b, 100, 50, new PixelRect(60, 0, 40, 50)), 6);
        Assert.Equal(0.0, SimCheckFrames.ChangedRatio(a, b, 100, 50, default));
        var c = Frame(100, 50, 20);   // below the channel threshold is noise
        Assert.Equal(0.0, SimCheckFrames.ChangedRatio(a, c, 100, 50, new PixelRect(0, 0, 100, 50)));
        var d = Frame(100, 50); for (int i = 3; i < d.Length; i += 4) d[i] = 0;   // alpha is ignored
        Assert.Equal(0.0, SimCheckFrames.ChangedRatio(a, d, 100, 50, new PixelRect(0, 0, 100, 50)));
        Assert.Equal(0.0, SimCheckFrames.ChangedRatio(a, new byte[8], 100, 50, new PixelRect(0, 0, 100, 50)));
    }

    [Fact]
    public void Exclusion_leaves_the_excluded_box_out_of_both_counts()
    {
        var a = Frame(100, 50); var b = Frame(100, 50);
        Paint(b, 100, new PixelRect(0, 0, 10, 10), 200);
        var whole = new PixelRect(0, 0, 100, 50);
        Assert.Equal(100.0 / 5000, SimCheckFrames.ChangedRatio(a, b, 100, 50, whole), 6);
        Assert.Equal(0.0, SimCheckFrames.ChangedRatio(a, b, 100, 50, whole, new PixelRect(0, 0, 10, 10)), 6);
        Assert.Equal(100.0 / 4900, SimCheckFrames.ChangedRatio(a, b, 100, 50, whole, new PixelRect(50, 20, 10, 10)), 6);
    }

    [Fact]
    public void Region_is_the_points_box_with_margins_cut_to_the_frame_and_falls_back_to_the_whole_frame()
    {
        var r = SimCheckFrames.RegionAround(new[] { (300f, 400f), (500f, 300f) }, 1000, 1000, 0.1f, 0.1f, 0.05f);
        Assert.Equal(new PixelRect(200, 200, 400, 250), r);
        var edge = SimCheckFrames.RegionAround(new[] { (10f, 10f) }, 100, 100, 0.2f, 0.2f, 0.2f);
        Assert.Equal(0, edge.X); Assert.Equal(0, edge.Y);
        Assert.True(edge.Right <= 100 && edge.Bottom <= 100);
        Assert.Equal(new PixelRect(0, 0, 80, 60), SimCheckFrames.RegionAround(Array.Empty<(float, float)>(), 80, 60));
        Assert.Equal(new PixelRect(0, 0, 80, 60), SimCheckFrames.RegionAround(new[] { (float.NaN, 1f) }, 80, 60));
    }

    [Fact]
    public void Projection_puts_the_look_at_point_in_the_middle_and_rejects_points_behind()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 100, -100), Vector3.Zero, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, 2f, 1f, 1000f);
        Assert.True(SimCheckFrames.ProjectToPixel(view, proj, Vector3.Zero, 200, 100, false, out float x, out float y));
        Assert.Equal(100f, x, 1); Assert.Equal(50f, y, 1);
        Assert.False(SimCheckFrames.ProjectToPixel(view, proj, new Vector3(0, 100, -500), 200, 100, false, out _, out _));
        SimCheckFrames.ProjectToPixel(view, proj, new Vector3(50, 0, 0), 200, 100, true, out float mx, out _);
        SimCheckFrames.ProjectToPixel(view, proj, new Vector3(-50, 0, 0), 200, 100, false, out float ux, out _);
        Assert.Equal(ux, mx, 3);
    }

    [Fact]
    public void Downscale_averages_boxes()
    {
        var f = Frame(4, 4, 0);
        Paint(f, 4, new PixelRect(0, 0, 2, 2), 100);
        var small = SimCheckFrames.Downscale(f, 4, 4, 2, 2);
        Assert.Equal(100, small[0]); Assert.Equal(0, small[4]); Assert.Equal(255, small[3]);
    }

    // ---------------------------------------------------------------- grading

    private static AbilityEvidence Good() => new()
    {
        Key = "Q",
        Planned = new[] { new PlannedStep("Place", 0), new PlannedStep("Clip", 0), new PlannedStep("Hit", 0.4), new PlannedStep("End", 1.0) },
        Trace = new[] { new StepEvidence("Place", 0, 0), new StepEvidence("Clip", 0, 0), new StepEvidence("Hit", 0.4, 0.41), new StepEvidence("End", 1.0, 1.01) },
        EndReached = true, ClipExpected = "a_spell1.anm", ClipSeen = "A_Spell1.anm", ClipAdvanced = true,
        HitsExpected = 1, HitsLogged = 1,
        Frames = new[] { new FrameEvidence("baseline", 0, 0, 0, 0, 0), new FrameEvidence("cast", 0.15, 0.15, 0.02, 0.01, 12) },
    };

    private static string One(AbilityEvidence e) => Assert.Single(SimCheckGrader.Grade(e).Reasons);

    [Fact]
    public void A_complete_run_is_complete()
    {
        var g = SimCheckGrader.Grade(Good());
        Assert.Equal(SimCheckGrade.Complete, g.Grade);
        Assert.Empty(g.Reasons);
    }

    [Fact]
    public void Each_rule_names_its_own_reason()
    {
        Assert.StartsWith("clip missing", One(Good() with { ClipSeen = null }));
        Assert.Contains("'b.anm' was playing", One(Good() with { ClipSeen = "b.anm" }));
        Assert.Contains("clock never advanced", One(Good() with { ClipAdvanced = false }));
        Assert.Equal("hit expected, none logged", One(Good() with { HitsLogged = 0 }));
        Assert.StartsWith("displacement", One(Good() with { DisplacementExpected = true, DisplacementApplied = false }));
        Assert.StartsWith("cleanup: ", One(Good() with { CleanupProblems = new[] { "the ally dummy is still on the stage" } }));
        Assert.StartsWith("log error: ", One(Good() with { LogErrors = new[] { "Mesh: boom" } }));
        Assert.StartsWith("exception: ", One(Good() with { Exception = "X" }));
        var noEnd = SimCheckGrader.Grade(Good() with { EndReached = false, Trace = Good().Trace.Take(3).ToArray() });
        Assert.Contains("End not reached", noEnd.Reasons);
    }

    [Fact]
    public void Visible_change_or_particles_are_needed_and_the_noise_is_taken_off()
    {
        var quiet = Good() with { Frames = new[] { new FrameEvidence("cast", 0.15, 0.15, 0.0005, 0, 4) }, ParticleBaseline = 3 };
        Assert.Contains("no visible change", One(quiet));
        var noisy = Good() with { NoiseRatio = 0.02, Frames = new[] { new FrameEvidence("cast", 0.15, 0.15, 0.021, 0.021, 0) } };
        Assert.Contains("no visible change", One(noisy));
        var particles = Good() with { Frames = new[] { new FrameEvidence("cast", 0.15, 0.15, 0, 0, 9) } };
        Assert.True(SimCheckGrader.Grade(particles).IsComplete);
        var pose = Good() with { Frames = new[] { new FrameEvidence("cast", 0.15, 0.15, 0.02, 0.0, 0) } };
        var g = SimCheckGrader.Grade(pose);
        Assert.True(g.IsComplete);
        Assert.Contains("caster's own pose", Assert.Single(g.Notes));
        Assert.Contains("no frame", One(Good() with { Frames = Array.Empty<FrameEvidence>() }));
    }

    [Fact]
    public void Steps_must_run_in_order_and_on_time()
    {
        var wrongOrder = Good() with { Trace = new[] { new StepEvidence("Place", 0, 0), new StepEvidence("Hit", 0.4, 0.4), new StepEvidence("Clip", 0, 0.5), new StepEvidence("End", 1, 1) } };
        Assert.Contains("step 2 was Hit", One(wrongOrder));
        var late = Good() with { Trace = new[] { new StepEvidence("Place", 0, 0), new StepEvidence("Clip", 0, 0), new StepEvidence("Hit", 0.4, 1.4), new StepEvidence("End", 1.0, 1.5) } };
        Assert.Contains("ran late", string.Join("|", SimCheckGrader.Grade(late).Reasons));
        var early = Good() with { Trace = new[] { new StepEvidence("Place", 0, 0), new StepEvidence("Clip", 0, 0), new StepEvidence("Hit", 0.4, 0.2), new StepEvidence("End", 1.0, 1.0) } };
        Assert.Contains("ran early", string.Join("|", SimCheckGrader.Grade(early).Reasons));
        Assert.Contains("timed out", string.Join("|", SimCheckGrader.Grade(Good() with { TimedOut = true, EndReached = false, TimeoutSeconds = 9 }).Reasons));
        Assert.Contains("did not start", string.Join("|", SimCheckGrader.Grade(Good() with { NeverStarted = true }).Reasons));
    }

    [Fact]
    public void Wrapper_and_champion_status_follow_their_parts()
    {
        var ok = new AbilityGrade(SimCheckGrade.Complete, Array.Empty<string>(), Array.Empty<string>());
        var bad = new AbilityGrade(SimCheckGrade.Incomplete, new[] { "x" }, Array.Empty<string>());
        Assert.True(SimCheckGrader.Wrapper(new[] { ("Q1", ok), ("Q2", ok) }).IsComplete);
        Assert.Contains("Q2", Assert.Single(SimCheckGrader.Wrapper(new[] { ("Q1", ok), ("Q2", bad) }).Reasons));
        Assert.False(SimCheckGrader.Wrapper(Array.Empty<(string, AbilityGrade)>()).IsComplete);
        Assert.Equal(SimCheckGrade.Complete, SimCheckGrader.Champion(true, new[] { ok, ok }));
        Assert.Equal(SimCheckGrade.Incomplete, SimCheckGrader.Champion(true, new[] { ok, bad }));
        Assert.Equal(SimCheckGrade.Blocked, SimCheckGrader.Champion(false, new[] { ok }));
    }

    [Fact]
    public void Displacement_is_applied_when_the_caster_reaches_the_end_or_most_of_the_way()
    {
        Assert.True(SimCheckGrader.DisplacementApplied(0, 0, 500, 0, new[] { (0.0, 0f, 0f), (1.1, 500f, 0f) }, 1.0));
        Assert.False(SimCheckGrader.DisplacementApplied(0, 0, 500, 0, new[] { (0.0, 0f, 0f), (1.1, 100f, 0f) }, 1.0));
        Assert.True(SimCheckGrader.DisplacementApplied(0, 0, 500, 0, new[] { (0.0, 0f, 0f), (0.5, 300f, 0f) }, 1.0));
        Assert.False(SimCheckGrader.DisplacementApplied(0, 0, 500, 0, new[] { (0.0, 0f, 0f) }, 1.0));
    }

    // ---------------------------------------------------------------- samples, layout, resume

    [Fact]
    public void Samples_are_cast_mid_hit_end_in_time_order_and_inside_the_run()
    {
        var steps = new (string, double)[] { ("Place", 0), ("Clip", 0), ("Composite", 0.1), ("Hit", 0.6), ("End", 1.4) };
        var plan = SimCheckSamples.Plan(steps, 1.4);
        // the hit sample (0.7) and the middle (0.7) collide: the hit wins
        Assert.Equal(new[] { "cast", "hit", "end" }, plan.Select(p => p.Label));
        Assert.Equal(0.15, plan[0].Time, 6);
        Assert.Equal(0.7, plan[1].Time, 6);
        Assert.Equal(1.2, plan[2].Time, 6);
        var spread = SimCheckSamples.Plan(new (string, double)[] { ("Clip", 0), ("Hit", 0.2), ("End", 3.0) }, 3.0);
        Assert.Equal(new[] { "cast", "hit", "mid", "end" }, spread.Select(p => p.Label));
        Assert.All(plan.Concat(spread), p => Assert.InRange(p.Time, 0, 2.98));
        Assert.True(spread.Zip(spread.Skip(1), (x, y) => y.Time > x.Time).All(x => x));
        Assert.DoesNotContain(SimCheckSamples.Plan(new (string, double)[] { ("Clip", 0), ("End", 1) }, 1), p => p.Label == "hit");
        Assert.Contains(SimCheckSamples.Plan(new (string, double)[] { ("Clip", 0), ("End", 0.3) }, 0.3), p => p.Label == "cast");
        Assert.Empty(SimCheckSamples.Plan(steps, 0));
    }

    [Fact]
    public void Sheet_is_at_most_1600_wide_and_the_cells_do_not_overlap()
    {
        foreach (int columns in new[] { 4, 5, 8 })
        {
            var l = SimCheckSheetLayout.Compute(6, columns, 765.0 / 812);
            Assert.True(l.Width <= 1600);
            Assert.Equal(l.HeaderHeight + 6 * l.RowHeight, l.Height);
            var last = l.CellRect(5, columns - 1);
            Assert.True(last.Right <= l.Width && last.Bottom <= l.Height);
            Assert.True(l.CellRect(0, 0).Right <= l.CellRect(0, 1).X);
            Assert.True(l.CaptionRect(0, 0).Bottom <= l.RowRect(0).Bottom);
            Assert.True(l.CellRect(0, 0).Bottom < l.CellRect(1, 0).Y);
        }
        Assert.True(SimCheckSheetLayout.Compute(0, 5, 0).Width <= 1600);
    }

    private static SimCheckChampionReport Report(string champion, string game = "1.0", string gen = "G1", string check = SimCheckReports.CheckVersion) =>
        new(champion, game, gen, check, "COMPLETE", Array.Empty<string>(), null, 1, 2, 3, "D3D11", "w", "1", Array.Empty<string>(), Array.Empty<SimCheckAbilityReport>());

    [Fact]
    public void Resume_skips_only_a_readable_report_of_the_same_versions_unless_forced()
    {
        string path = SimCheckReports.ReportPath(_temp, "1.0", "Ahri");
        Assert.False(SimCheckReports.IsDone(path, "1.0", "G1", false));
        SimCheckReports.WriteFile(path, SimCheckReports.Write(Report("Ahri")));
        Assert.True(SimCheckReports.IsDone(path, "1.0", "G1", false));
        Assert.False(SimCheckReports.IsDone(path, "1.0", "G1", true));
        Assert.False(SimCheckReports.IsDone(path, "1.1", "G1", false));
        Assert.False(SimCheckReports.IsDone(path, "1.0", "G2", false));
        File.WriteAllText(path, "{ not json");
        Assert.False(SimCheckReports.IsDone(path, "1.0", "G1", false));
        SimCheckReports.WriteFile(path, SimCheckReports.Write(Report("Ahri", check: "M0.old")));
        Assert.False(SimCheckReports.IsDone(path, "1.0", "G1", false));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Selection_understands_all_from_first_and_lists_in_roster_order()
    {
        var roster = new[] { "Aatrox", "Ahri", "Akali", "Ezreal", "Kalista" };
        Assert.Equal(roster, SimCheckReports.Select(roster, "all").Champions);
        Assert.Equal(new[] { "Akali", "Ezreal", "Kalista" }, SimCheckReports.Select(roster, "from:akali").Champions);
        Assert.Equal(new[] { "Aatrox", "Ahri" }, SimCheckReports.Select(roster, "first:2").Champions);
        Assert.Equal(new[] { "Kalista", "Ezreal" }, SimCheckReports.Select(roster, "kalista, EZREAL ,kalista").Champions);
        Assert.NotNull(SimCheckReports.Select(roster, "Nobody").Error);
        Assert.NotNull(SimCheckReports.Select(roster, "from:Nobody").Error);
        Assert.NotNull(SimCheckReports.Select(roster, "first:0").Error);
    }

    [Fact]
    public void Summary_counts_champions_abilities_and_groups_reasons_without_their_names_and_numbers()
    {
        static SimCheckAbilityReport Ab(string key, string grade, params string[] reasons) =>
            new(key, null, false, "OK", Array.Empty<string>(), grade, reasons, Array.Empty<string>(), 1, Array.Empty<SimCheckStepReport>(), null, null, false, 0, 0, false, false,
                0, 0, 0, 0, 0, Array.Empty<SimCheckFrameReport>(), Array.Empty<string>(), Array.Empty<string>(), 1);
        var a = Report("Ahri") with { Status = "INCOMPLETE", Abilities = new[] { Ab("Q", "COMPLETE"), Ab("E", "INCOMPLETE", "clip missing: 'a.anm' expected, 'b.anm' was playing") } };
        var b = Report("Zed") with { Status = "INCOMPLETE", Abilities = new[] { Ab("R", "INCOMPLETE", "clip missing: 'c.anm' expected, 'd.anm' was playing") } };
        var c = Report("Zyra") with { Status = "BLOCKED", LoadError = "no D3D11 scene" };
        var s = SimCheckReports.Summarize("1.0", "G1", new[] { b, c, a });
        Assert.Equal(3, s.Champions);
        Assert.Equal(1, s.ChampionStatus["BLOCKED"]); Assert.Equal(2, s.ChampionStatus["INCOMPLETE"]);
        Assert.Equal(1, s.AbilityStatus["COMPLETE"]); Assert.Equal(2, s.AbilityStatus["INCOMPLETE"]);
        Assert.Equal("Ahri", s.PerChampion[0].Champion);
        Assert.Equal(2, s.CommonReasons.First(r => r.Reason.StartsWith("clip missing")).Count);
        Assert.Contains("Zyra", SimCheckReports.SummaryMarkdown(s));
    }
}
