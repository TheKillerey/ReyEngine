using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ReyEngine.App.ViewModels;
using ReyEngine.App.Views;
using ReyEngine.Core;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Characters.Simulation;

namespace ReyEngine.App.SimCheck;

/// <summary>
/// M871: the in-app validation of the champion simulations.
///
/// <para><b>It drives the editor, it does not stand in for it.</b> Per champion it does what a user does: opens the Character window,
/// picks the champion in the picker, clicks the base skin card (<see cref="CharacterBrowserViewModel"/> -> <c>OpenSkin</c> ->
/// <c>MainWindowViewModel.LoadMeshPreviewAsync</c>, Map11 stage, D3D11 renderer), waits for the definition, and plays every ability
/// through the M869 player (<see cref="MeshPreviewViewModel.PlaySimulation(AbilitySim)"/>). The evidence is read from the same objects
/// the window shows: the player's trace, the animation player, the playback, and frames read back from the window's own
/// <see cref="Dx11ViewportSurface"/> as it renders them.</para>
///
/// <para><b>Sampling without guessing at the clock.</b> The run is the real one, on the dispatcher timer. At each sample time the driver
/// pauses the simulation (the window's own Pause: clock, clip and particles stop together), waits for two frames to be rendered, reads
/// the second back, and resumes. The frame is therefore the state at that simulation time, whatever the frame rate.</para>
/// </summary>
internal sealed class SimCheckDriver
{
    private readonly SimCheckOptions _options;
    private readonly MainWindow _main;
    private readonly MainWindowViewModel _vm;
    private readonly Collector _log = new();
    private readonly List<string> _unhandled = new();
    private readonly SimCheckThresholds _thresholds = SimCheckThresholds.Default;
    private StreamWriter? _logFile;
    private MeshPreviewWindow? _window;
    private string _gameVersion = "unknown";
    private string _outRoot = "";
    private readonly List<string> _writeFailures = new();
    private string SpeedText => _options.Speed.ToString(CultureInfo.InvariantCulture);
    private string PlacementText => _options.Placement.ToString().ToLowerInvariant();
    private double _peakWorkingSetMb;

    private MeshPreviewViewModel Mp => _vm.MeshPreview;

    public SimCheckDriver(SimCheckOptions options, MainWindow main, MainWindowViewModel vm)
    {
        _options = options; _main = main; _vm = vm;
    }

    private sealed class Collector : ILogSink
    {
        private readonly object _gate = new();
        private readonly List<LogEntry> _entries = new();
        public void Write(LogEntry entry)
        {
            if (entry.Level < LogLevel.Warning) return;
            lock (_gate) _entries.Add(entry);
        }
        public int Mark() { lock (_gate) return _entries.Count; }
        public List<LogEntry> Since(int mark) { lock (_gate) return _entries.Skip(mark).ToList(); }
    }

    private void Say(string line)
    {
        string stamped = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line;
        try { Console.WriteLine(stamped); Console.Out.Flush(); } catch { }
        try { _logFile?.WriteLine(stamped); _logFile?.Flush(); } catch { }
    }

    // ================================================================ the run

    public async Task<int> RunAsync()
    {
        int exit = 0;
        try { exit = await RunCoreAsync(); }
        catch (Exception ex) { Say("simcheck failed: " + ex); exit = 1; }
        finally
        {
            _logFile?.Dispose();
            SimCheckSession.ExitCode = exit;
            (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(exit);
        }
        return exit;
    }

    private async Task<int> RunCoreAsync()
    {
        var swAll = Stopwatch.StartNew();
        Dispatcher.UIThread.UnhandledException += (_, e) => { lock (_unhandled) _unhandled.Add(e.Exception.GetType().Name + ": " + e.Exception.Message); e.Handled = true; };
        _vm.AddLogSink(_log);
        if (_options.GameDirectory is { Length: > 0 } game) _vm.Project.GameDirectory = game;
        string? gameDir = _vm.Project.GameDirectory;
        string? final = ReyEngine.Core.Assets.GameReferenceLibrary.FindFinalDirectory(gameDir);
        string? championsDir = final is null ? null : Path.Combine(final, "Champions");
        if (championsDir is null || !Directory.Exists(championsDir))
        { Console.Error.WriteLine("no League install with DATA/FINAL/Champions found; pass --game <Game folder>."); return 2; }

        _gameVersion = ChampionSimSource.ReadGameVersion(gameDir!);
        _outRoot = _options.OutDirectory ?? Path.Combine(ReyPaths.DataRoot, "simcheck");
        Directory.CreateDirectory(Path.Combine(_outRoot, _gameVersion));
        _logFile = new StreamWriter(Path.Combine(_outRoot, _gameVersion, "_simcheck.log"), append: true) { AutoFlush = true };

        var roster = ChampionSimBatch.Roster(championsDir);
        var selection = SimCheckReports.Select(roster, _options.Spec);
        if (selection.Error is not null) { Say(selection.Error); return 2; }

        Say($"working set at start {WorkingSetMb():0} MB");
        Say($"simcheck {SimCheckReports.CheckVersion} game {_gameVersion}: {selection.Champions.Count} champion(s) -> {_outRoot}  (speed {_options.Speed.ToString(CultureInfo.InvariantCulture)}, placement {_options.Placement})");

        // The Character window, opened the way the menu opens it.
        _vm.OpenCharacterBrowserCommand.Execute(null);
        _window = _main.CharacterWindow;
        if (_window is null || Mp.Browser is not { } browser) { Say("the Character window did not open"); return 1; }
        Mp.SimSpeed = _options.Speed;
        Mp.UseDx11Preview = true;

        int done = 0, skipped = 0, writeErrors = 0;
        foreach (string champion in selection.Champions)
        {
            string reportPath = SimCheckReports.ReportPath(_outRoot, _gameVersion, champion);
            if (SimCheckReports.IsDone(reportPath, _gameVersion, ChampionSimDefinition.CurrentGeneratorVersion, _options.Force, SpeedText, PlacementText))
            { skipped++; Say($"{champion,-16} skipped (report exists)"); continue; }

            var sw = Stopwatch.StartNew();
            SimCheckChampionReport report;
            try { report = await CheckChampionAsync(champion, browser); }
            catch (Exception ex)
            {
                report = Blocked(champion, "check threw: " + ex.GetType().Name + ": " + ex.Message, sw.Elapsed.TotalSeconds);
            }
            // M873: a champion whose report cannot be written is recorded and skipped; it never ends the roster run
            try { SimCheckReports.WriteFile(reportPath, SimCheckReports.Write(report)); }
            catch (Exception ex)
            {
                writeErrors++;
                Say($"{champion,-16} REPORT NOT WRITTEN: {ex.GetType().Name}: {ex.Message}");
                _writeFailures.Add($"{champion}: {ex.GetType().Name}: {ex.Message}");
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                continue;
            }
            done++;
            Say($"{champion,-16} {report.Status,-10} {string.Join(' ', report.Abilities.Where(a => !a.Wrapper).Select(a => a.Key.Split(' ')[0] + "=" + a.Grade[0]))}"
                + $"  {report.WallSeconds.ToString("0", CultureInfo.InvariantCulture)} s  {report.WorkingSetMb.ToString("0", CultureInfo.InvariantCulture)} MB");

            // the window drops the last champion's scene when the next loads; give the garbage back between champions
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Say($"  memory after {champion}: {MemoryText()}");
        }

        var all = SimCheckReports.ReadAll(_outRoot, _gameVersion);
        var summary = SimCheckReports.Summarize(_gameVersion, ChampionSimDefinition.CurrentGeneratorVersion, all);
        try
        {
            SimCheckReports.WriteFile(Path.Combine(_outRoot, _gameVersion, "_summary.json"), SimCheckReports.Write(summary));
            SimCheckReports.WriteFile(Path.Combine(_outRoot, _gameVersion, "_summary.md"), SimCheckReports.SummaryMarkdown(summary));
        }
        catch (Exception ex) { writeErrors++; Say("the summary could not be written: " + ex.Message); }
        Say($"done: {done} checked, {skipped} skipped, {writeErrors} report write error(s), {swAll.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s, peak working set {_peakWorkingSetMb.ToString("0", CultureInfo.InvariantCulture)} MB; "
            + string.Join(", ", summary.ChampionStatus.Select(kv => kv.Value + " " + kv.Key)));
        foreach (string failure in _writeFailures) Say("  not written: " + failure);

        // M873: the exit code answers "is the roster complete?" for the champions this run covered
        var selected = new HashSet<string>(selection.Champions, StringComparer.OrdinalIgnoreCase);
        int code = SimCheckReports.ExitCode(all.Where(r => selected.Contains(r.Champion)), writeErrors);
        if (selection.Champions.Any(c => !all.Any(r => r.Champion.Equals(c, StringComparison.OrdinalIgnoreCase)))) code = SimCheckReports.ExitIncomplete;
        if (code != 0) Say($"exit {code}: not every selected champion is COMPLETE");
        return code;
    }

    private SimCheckChampionReport Blocked(string champion, string error, double seconds) =>
        new(champion, _gameVersion, ChampionSimDefinition.CurrentGeneratorVersion, SimCheckReports.CheckVersion, "BLOCKED",
            new[] { error }, error, 0, seconds, WorkingSetMb(), "D3D11", WindowText(), _options.Speed.ToString(CultureInfo.InvariantCulture),
            Array.Empty<string>(), Array.Empty<SimCheckAbilityReport>(), PlacementText);

    private string WindowText() => _window is null ? "" : $"{_window.ClientSize.Width:0}x{_window.ClientSize.Height:0} {_options.Placement}";

    /// <summary>M873: where the process's memory is - the managed heap after a full collection against the working set and private bytes. A heap that stays small while the
    /// working set grows is native or GPU memory (the stage), not frames, reports or bitmaps the driver keeps.</summary>
    private static string MemoryText()
    {
        using var p = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        return $"managed heap {GC.GetTotalMemory(false) / 1048576.0:0} MB (committed {info.TotalCommittedBytes / 1048576.0:0}), working set {p.WorkingSet64 / 1048576.0:0} MB, private {p.PrivateMemorySize64 / 1048576.0:0} MB";
    }

    private double WorkingSetMb()
    {
        using var p = Process.GetCurrentProcess();
        double mb = p.WorkingSet64 / 1048576.0;
        _peakWorkingSetMb = Math.Max(_peakWorkingSetMb, Math.Max(mb, p.PeakWorkingSet64 / 1048576.0));
        return mb;
    }

    // ================================================================ one champion

    private async Task<SimCheckChampionReport> CheckChampionAsync(string champion, CharacterBrowserViewModel browser)
    {
        var sw = Stopwatch.StartNew();
        int logMark = _log.Mark();
        var window = _window!;

        // ---- load: the picker, then the base skin's card
        var row = browser.Champions.FirstOrDefault(c => c.Folder.Equals(champion, StringComparison.OrdinalIgnoreCase));
        if (row is null) return Blocked(champion, "the Character picker does not list this champion", sw.Elapsed.TotalSeconds);
        int revision = Mp.Dx11SceneRevision;
        Mp.StopSimulation();
        browser.SelectedChampion = row;
        var character = browser.Characters.FirstOrDefault(c => c.Name.Equals(champion, StringComparison.OrdinalIgnoreCase)) ?? browser.Characters.FirstOrDefault();
        if (character is null) return Blocked(champion, "the picker lists no character for this champion", sw.Elapsed.TotalSeconds);
        browser.SelectedCharacter = character;
        var skin = browser.Skins.OrderBy(s => s.Number).FirstOrDefault(s => s.Number == 0) ?? browser.Skins.OrderBy(s => s.Number).FirstOrDefault();
        if (skin is null) return Blocked(champion, "the picker lists no skin", sw.Elapsed.TotalSeconds);
        browser.SelectSkinCommand.Execute(skin);
        if (!browser.CanOpen) return Blocked(champion, $"the base skin cannot be opened ({browser.Status})", sw.Elapsed.TotalSeconds);

        // the load is asynchronous: the window's scene revision moves when the new skin has reached it
        if (!await WaitUntil(() => Mp.Dx11SceneRevision != revision, TimeSpan.FromSeconds(180)))
            return Blocked(champion, "the skin did not finish loading: " + FirstErrors(_log.Since(logMark), browser.Status), sw.Elapsed.TotalSeconds);
        if (Mp.Dx11Scene is null) return Blocked(champion, "no D3D11 scene: " + Mp.Dx11Status, sw.Elapsed.TotalSeconds);
        try { await Mp.EnvironmentTask; } catch { }
        if (!await WaitUntil(() => window.Dx11SceneReady(Mp), TimeSpan.FromSeconds(240)))
            return Blocked(champion, "the stage and the champion never reached the D3D11 renderer: " + Mp.ArenaStatus + " | " + Mp.Dx11Status, sw.Elapsed.TotalSeconds);
        try { await Mp.SimulationTask; } catch { }
        if (!await WaitUntil(() => Mp.HasSimulation, TimeSpan.FromSeconds(30)))
            return Blocked(champion, "no simulation definition: " + Mp.SimSummary, sw.Elapsed.TotalSeconds);

        // the surface must really be drawing frames in this window state
        for (int i = 0; i < 6; i++)
            if (await FrameAsync(TimeSpan.FromSeconds(10)) is null)
                return Blocked(champion, "the D3D11 surface produced no frame (window state " + WindowText() + ")", sw.Elapsed.TotalSeconds);
        double loadSeconds = sw.Elapsed.TotalSeconds;
        Say($"  {champion} loaded in {loadSeconds:0.#} s, working set {WorkingSetMb():0} MB");
        var warnings = _log.Since(logMark).Where(e => e.Level == LogLevel.Warning).Select(e => $"{e.Category}: {e.Message}").Distinct().Take(20).ToList();
        var loadErrors = _log.Since(logMark).Where(e => e.Level == LogLevel.Error).Select(e => $"{e.Category}: {e.Message}").Distinct().Take(10).ToList();

        var def = Mp.SimulationDefinition!;
        Mp.ClearSimulationHistory();
        Mp.SimSpeed = _options.Speed;

        // the stage, as the load left it: where cleanup must bring everyone back
        Mp.StopSimulation();
        var stage = new StageSnapshot(Mp.CharacterPosition, Mp.TargetDummyPosition, PlaybackNames());

        // M874: a freshly loaded stage is still warming up (the map's own particles come and go for a few seconds); the first ability would otherwise measure its particle baseline in that transient
        await Task.Delay(3000);

        // ---- every ability, in the order the ability list shows them
        var results = new List<(SimCheckAbilityReport Report, List<(byte[] Bgra, int W, int H)> Cells, PixelRect Region, string[] Captions)>();
        var grades = new Dictionary<string, AbilityGrade>();
        foreach (var abilityRow in Mp.SimAbilities.ToList())
        {
            var ability = abilityRow.Ability;
            if (Mp.IsSimWrapper(ability))
            {
                var subs = Mp.SimAbilities.Where(r => r.Ability.ParentKey == ability.Key).Select(r => r.Key).ToList();
                results.Add((new SimCheckAbilityReport(ability.Key, abilityRow.Name, true, abilityRow.StatusText, SplitReasons(abilityRow.Reasons), "", Array.Empty<string>(),
                    Array.Empty<string>(), 0, Array.Empty<SimCheckStepReport>(), null, null, false, 0, 0, false, false, 0, 0, 0, 0, 0,
                    Array.Empty<SimCheckFrameReport>(), Array.Empty<string>(), Array.Empty<string>(), 0), new(), default, Array.Empty<string>()));
                grades[ability.Key] = new AbilityGrade(SimCheckGrade.Complete, Array.Empty<string>(), subs.ToArray());   // replaced below
                continue;
            }

            var result = await RunAbilityAsync(abilityRow, stage);
            results.Add(result);
            grades[ability.Key] = new AbilityGrade(Enum.Parse<SimCheckGrade>(result.Report.Grade, true), result.Report.Reasons, result.Report.Notes);
            Say($"  {champion} {ability.Key,-10} {result.Report.Grade,-10} {(result.Report.Reasons.Count == 0 ? "" : string.Join("; ", result.Report.Reasons))}");
        }

        // a wrapper is as good as its sub-casts
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i].Report;
            if (!r.Wrapper) continue;
            var subs = results.Where(x => !x.Report.Wrapper && def.Abilities.FirstOrDefault(a => a.Key == x.Report.Key)?.ParentKey == r.Key)
                .Select(x => (x.Report.Key, new AbilityGrade(Enum.Parse<SimCheckGrade>(x.Report.Grade, true), x.Report.Reasons, x.Report.Notes))).ToList();
            var g = SimCheckGrader.Wrapper(subs);
            grades[r.Key] = g;
            results[i] = (r with { Grade = g.Grade.ToString().ToUpperInvariant(), Reasons = g.Reasons, Notes = g.Notes }, results[i].Cells, results[i].Region, results[i].Captions);
        }

        Mp.StopSimulation();
        var status = SimCheckGrader.Champion(true, grades.Values);
        var reportAbilities = results.Select(r => r.Report).ToList();
        var failed = reportAbilities.Where(a => !a.Wrapper && a.Grade != "COMPLETE").Select(a => a.Key).ToList();
        var report = new SimCheckChampionReport(champion, _gameVersion, ChampionSimDefinition.CurrentGeneratorVersion, SimCheckReports.CheckVersion,
            status.ToString().ToUpperInvariant(),
            failed.Count == 0 ? Array.Empty<string>() : new[] { "not complete: " + string.Join(", ", failed) },
            null, loadSeconds, sw.Elapsed.TotalSeconds, WorkingSetMb(), "D3D11 (Dx11ViewportSurface)", WindowText(),
            _options.Speed.ToString(CultureInfo.InvariantCulture),
            warnings.Concat(loadErrors.Select(e => "ERROR " + e)).ToList(), reportAbilities, PlacementText);

        try
        {
            string folder = Path.GetDirectoryName(SimCheckReports.ReportPath(_outRoot, _gameVersion, champion))!;
            Directory.CreateDirectory(folder);
            SimCheckSheetWriter.Write(Path.Combine(folder, "sheet.png"), champion, report, results.Select(r => (r.Cells, r.Region, r.Captions)).ToList());
        }
        catch (Exception ex) { Say($"  {champion}: the contact sheet could not be written: {ex.Message}"); }
        return report;
    }

    private static IReadOnlyList<string> SplitReasons(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FirstErrors(List<LogEntry> entries, string status)
    {
        var errors = entries.Where(e => e.Level == LogLevel.Error).Select(e => $"{e.Category}: {e.Message}").Distinct().Take(3).ToList();
        return errors.Count > 0 ? string.Join(" | ", errors) : "no error was logged (" + status + ")";
    }

    private sealed record StageSnapshot(Vector3 Champion, Vector3? Dummy, List<string> Idle);

    private List<string> PlaybackNames() =>
        Mp.Playback?.Items.Select(i => i.System.Name ?? "?").OrderBy(n => n, StringComparer.Ordinal).ToList() ?? new List<string>();

    // ================================================================ one ability

    private async Task<(SimCheckAbilityReport Report, List<(byte[] Bgra, int W, int H)> Cells, PixelRect Region, string[] Captions)> RunAbilityAsync(
        SimAbilityRowViewModel row, StageSnapshot stage)
    {
        var ability = row.Ability;
        var wall = Stopwatch.StartNew();
        int logMark = _log.Mark();
        int unhandledMark; lock (_unhandled) unhandledMark = _unhandled.Count;
        var frames = new List<FrameEvidence>();
        var frameReports = new List<SimCheckFrameReport>();
        var cells = new List<(byte[] Bgra, int W, int H)>();
        var captions = new List<string>();
        string? exception = null;
        PixelRect region = default, cellRegion = default;

        // M873: what the PLAYER was supposed to play - the player's own resolution (Riot's authored name in the loaded skin's graph first) - so the check
        // grades the execution, and the grader flags a choice that differs from the definition's authored clip.
        var clipChoice = Mp.ChooseSimClip(ability);
        string? clipExpected = clipChoice?.File;
        string? clipDefinition = SimClipChooser.FileOf(ability.Clip?.AnmPath);
        string? clipSeen = null; bool clipAdvanced = false;
        var unitChange = new Dictionary<string, double>(); var unitWhite = new Dictionary<string, double>(); var unitHasMesh = new Dictionary<string, bool>();
        var positions = new List<(double Clock, float X, float Z)>();
        double noise = 0; int particleBaseline = 0;
        SimScript? script = null;
        bool timedOut = false;

        try
        {
            Mp.StopSimulation();
            Mp.ClearSimulationHistory();
            await Settle();
            // M873: the particle baseline is the MAX over a short window BEFORE the cast (the map's own effects come and go), not two frames
            for (int i = 0; i < PreCastFrames; i++)
            {
                if (await FrameAsync(TimeSpan.FromSeconds(5)) is { } pre) particleBaseline = Math.Max(particleBaseline, pre.LiveParticles);
                await Task.Delay(40);
            }
            Mp.PlaySimulation(ability);                       // BeginRun: places, frames, and runs the steps due at t = 0
            script = Mp.SimCurrentScript;
            if (!Mp.SimRunning || script is null) throw new InvalidOperationException("the player did not start");

            Mp.SimPaused = true;                              // the baseline: placed, the cast just begun, the clock held at 0
            for (int i = 0; i < 4; i++) await FrameAsync(TimeSpan.FromSeconds(10));
            var b1 = await FrameAsync(TimeSpan.FromSeconds(10)) ?? throw new InvalidOperationException("no baseline frame");
            region = RegionFor(b1, script);
            // M873: the caster's own pixels are left out of the effect ratio - at its start AND along any dash, blink or leap (the union of its boxes over
            // the whole cast window), so a caster that merely travels is not mistaken for an effect
            var casterBoxes = new List<PixelRect>();
            void AddBox(Vector3 world) { if (CasterBox(b1, world, 190f * Mp.SkinScale) is { } box) casterBoxes.Add(box); }
            AddBox(script.ChampionPosition);
            foreach (var d in script.All(SimStepKind.Displace)) { if (d.From is { } f) AddBox(f); if (d.To is { } t) AddBox(t); }
            // M874: a unit a mechanic moves or summons is no effect either: the boxes of the spawned units, and of the dummy or ally a MoveUnit moves (start, end, and the top of a knock-up)
            foreach (var st in script.Steps)
            {
                if (st.Kind == SimStepKind.Spawn)
                {
                    if (st.From is { } sf) AddBox(sf);
                    if (st.To is { } stTo) AddBox(stTo);
                    foreach (var wp in st.Path ?? Array.Empty<Vector3>()) AddBox(wp);
                }
                else if (st.Kind == SimStepKind.MoveUnit)
                {
                    if (st.From is { } mf) { AddBox(mf); AddBox(mf + new Vector3(0f, st.Height, 0f)); }
                    if (st.To is { } mt) AddBox(mt);
                }
            }
            int boxedPositions = 0;
            PixelRect? exclude = SimCheckFrames.Union(casterBoxes);
            particleBaseline = Math.Max(particleBaseline, b1.LiveParticles);
            await Task.Delay(400);                            // a moment of the map's own movement: the noise floor
            var b2 = await FrameAsync(TimeSpan.FromSeconds(10)) ?? b1;
            var wholeFrame = new PixelRect(0, 0, b1.Width, b1.Height);
            double ambient0 = region.Width >= b1.Width && region.Height >= b1.Height ? 0.0 : SimCheckFrames.ChangedRatio(b1.Bgra, b2.Bgra, b1.Width, b1.Height, wholeFrame, region);
            noise = Math.Max(0.0, SimCheckFrames.ChangedRatio(b1.Bgra, b2.Bgra, b1.Width, b1.Height, region) - ambient0);
            particleBaseline = Math.Max(particleBaseline, b2.LiveParticles);
            int cellW = 0, cellH = 0;
            PixelRect crop = default;
            void AddCell(Dx11FrameCapture tap, FrameEvidence ev)
            {
                if (cellW == 0)
                {
                    // M873: the cell shows the judged region (the yellow box) enlarged to the cell, not the whole frame - at the 2200-unit camera the
                    // actors are a few dozen pixels tall and the effects cannot be read in a downscaled full view.
                    cellW = 288; cellH = Math.Max(9, (int)Math.Round(288.0 * tap.Height / tap.Width));
                    crop = SimCheckFrames.CropFor(region, tap.Width, tap.Height, (double)cellW / cellH);
                    // the judged region, in the cell's own pixels, for the dashed outline of the sheet
                    cellRegion = new PixelRect((int)Math.Round((region.X - crop.X) * (double)cellW / crop.Width), (int)Math.Round((region.Y - crop.Y) * (double)cellH / crop.Height),
                        (int)Math.Round(region.Width * (double)cellW / crop.Width), (int)Math.Round(region.Height * (double)cellH / crop.Height)).Clamp(cellW, cellH);
                }
                cells.Add((SimCheckFrames.ResampleCrop(tap.Bgra, tap.Width, tap.Height, crop, cellW, cellH), cellW, cellH));
                captions.Add($"{ev.Label}  t={ev.Clock.ToString("0.00", CultureInfo.InvariantCulture)}s  d={(ev.ChangedRatio * 100).ToString("0.0", CultureInfo.InvariantCulture)}%  fx={(ev.EffectRatio * 100).ToString("0.0", CultureInfo.InvariantCulture)}%  p={ev.LiveParticles}");
                frameReports.Add(new SimCheckFrameReport(ev.Label, ev.PlannedTime, ev.Clock, ev.ChangedRatio, ev.EffectRatio, ev.LiveParticles,
                    tap.Width, tap.Height, new[] { region.X, region.Y, region.Width, region.Height }));
            }
            var baseline = new FrameEvidence("baseline", 0, Mp.SimClock, 0, 0, b1.LiveParticles);
            frames.Add(baseline);
            AddCell(b1, baseline);

            // ---- the run, sampled
            var plan = SimCheckSamples.Plan(script.Steps.Select(s => (s.Kind.ToString(), (double)s.Time)).ToList(), script.EndTime);
            double deadline = script.EndTime / _options.Speed * 3.0 + 40.0;
            var runClock = Stopwatch.StartNew();
            Mp.SimPaused = false;
            foreach (var sample in plan)
            {
                bool reached = await WaitUntil(() => !Mp.SimRunning || ReferenceEquals(Mp.SimCurrentScript, script) && Mp.SimClock >= sample.Time || runClock.Elapsed.TotalSeconds > deadline,
                    TimeSpan.FromSeconds(deadline + 5), 4);
                if (!Mp.SimRunning || !reached || runClock.Elapsed.TotalSeconds > deadline) break;

                // what the animation player and the caster are doing at this moment
                string playing = Mp.Animation.CurrentName;
                if (Mp.Animation.HasClip && playing is { Length: > 0 } && playing != "(none)")
                {
                    if (clipSeen is null || (clipExpected is not null && SameFile(playing, clipExpected))) clipSeen = playing;
                    if (Mp.Animation.Time > 0) clipAdvanced |= clipExpected is null || SameFile(playing, clipExpected);
                }
                positions.Add((Mp.SimClock, Mp.CharacterPosition.X, Mp.CharacterPosition.Z));

                Mp.SimPaused = true;
                await FrameAsync(TimeSpan.FromSeconds(10));
                var tap = await FrameAsync(TimeSpan.FromSeconds(10));
                Mp.SimPaused = false;
                if (tap is null) continue;
                double clockAtFrame = Mp.SimClock;
                // The map moves by itself (grass, props, water), and more the longer the run. The part of the frame OUTSIDE the region is the
                // control: what changed there is ambient, so it is taken off what changed inside.
                var whole = new PixelRect(0, 0, tap.Width, tap.Height);
                double ambient = region.Width >= tap.Width && region.Height >= tap.Height ? 0.0
                    : SimCheckFrames.ChangedRatio(b1.Bgra, tap.Bgra, tap.Width, tap.Height, whole, region);
                double changed = Math.Max(0.0, SimCheckFrames.ChangedRatio(b1.Bgra, tap.Bgra, tap.Width, tap.Height, region) - ambient);
                // positions the driver has collected so far join the union (the caster as it really moved)
                for (; boxedPositions < positions.Count; boxedPositions++)
                    AddBox(new Vector3(positions[boxedPositions].X, script.ChampionPosition.Y, positions[boxedPositions].Z));
                // the units as they really stand now join the union; the share of each spawned unit's own box that changed is the proof it is drawn
                foreach (var (unitId, unitPos, hasMesh) in Mp.SimSpawnedUnitStates)
                {
                    unitHasMesh[unitId] = hasMesh;
                    if (CasterBox(b1, unitPos) is { } ubox)
                    {
                        casterBoxes.Add(ubox);
                        double share = SimCheckFrames.ChangedRatio(b1.Bgra, tap.Bgra, tap.Width, tap.Height, ubox);
                        unitChange[unitId] = Math.Max(unitChange.GetValueOrDefault(unitId), share);
                        // and what colour that change is: untextured, a unit is a white silhouette (M874 fix)
                        if (SimCheckFrames.ChangedWhiteShare(b1.Bgra, tap.Bgra, tap.Width, tap.Height, ubox) is { } ws)
                            unitWhite[unitId] = Math.Max(unitWhite.GetValueOrDefault(unitId), ws);
                    }
                }
                foreach (var moved in script.All(SimStepKind.MoveUnit).Select(m => m.Unit).Distinct())
                    if (moved is { Length: > 0 } && Mp.SimUnitPosition(moved) is { } mp) AddBox(mp);
                exclude = SimCheckFrames.Union(casterBoxes);
                double effect = Math.Max(0.0, SimCheckFrames.ChangedRatio(b1.Bgra, tap.Bgra, tap.Width, tap.Height, region, exclude) - ambient);
                var ev = new FrameEvidence(sample.Label, sample.Time, clockAtFrame, changed, effect, tap.LiveParticles);
                frames.Add(ev);
                AddCell(tap, ev);
            }

            // ---- to the end
            timedOut = !await WaitUntil(() => !Mp.SimRunning, TimeSpan.FromSeconds(Math.Max(5.0, deadline - runClock.Elapsed.TotalSeconds)), 10);
            if (timedOut) Mp.SimPaused = false;
        }
        catch (Exception ex) { exception = ex.GetType().Name + ": " + ex.Message; }

        // ---- the evidence
        var trace = Mp.SimTrace.Where(t => t.Key == ability.Key).ToList();
        bool endReached = trace.Any(t => t.Kind == SimStepKind.End);
        int hitsExpected = script?.All(SimStepKind.Hit).Count() ?? 0;
        int hitsLogged = trace.Count(t => t.Kind == SimStepKind.Hit);
        var displace = script?.All(SimStepKind.Displace).Where(s => s.To is not null && s.From is not null).ToList() ?? new List<SimStep>();
        bool displaceOk = displace.All(d => SimCheckGrader.DisplacementApplied(d.From!.Value.X, d.From.Value.Z, d.To!.Value.X, d.To.Value.Z, positions, d.EndTime, _thresholds));

        // ---- the scripted mechanics: what the player recorded, read BEFORE the end-of-run reset clears the stage
        var adapterPlans = script is null ? new List<AdapterStepPlan>() : SimCheckAdapterSteps.Plans(script).ToList();
        var observations = new AdapterObservations
        {
            Trace = trace.Select(t => new StepEvidence(t.Kind.ToString(), t.Planned, t.Clock)).ToList(),
            Units = Mp.SimUnitTrack.Select(u => new UnitObservation(u.Clock, u.Unit, u.Position.X, u.Position.Y, u.Position.Z, u.Visible)).ToList(),
            Caster = Mp.SimCasterTrack.ToList(),
            UnitPixelChange = unitChange, UnitWhiteShare = unitWhite, UnitHasMesh = unitHasMesh,
            EffectsStarted = Mp.SimEffectStarts.ToList(),
        };
        var adapterSteps = SimCheckAdapterSteps.Verify(adapterPlans, observations);
        bool hadSpawn = script?.All(SimStepKind.Spawn).Any() ?? false;

        // ---- cleanup: let the end-of-run reset reach the frames, then compare with the stage
        if (Mp.SimRunning) Mp.StopSimulation();
        await Settle();
        var cleanup = new List<string>();
        if (Mp.SimRunning) cleanup.Add("the player is still running");
        if (Mp.SimCurrentScript is not null) cleanup.Add("a run is still held");
        var idleNow = PlaybackNames();
        if (!idleNow.SequenceEqual(stage.Idle)) cleanup.Add($"playback holds {idleNow.Count} effect(s), {stage.Idle.Count} at rest ({string.Join(", ", idleNow.Except(stage.Idle).Take(3))})");
        if (Mp.SelectedEvent is not null) cleanup.Add("an event is still selected");
        if (Mp.SimAllyPosition is not null) cleanup.Add("the ally dummy is still on the stage");
        if (Mp.SimSpawnedUnits.Count > 0) cleanup.Add($"{Mp.SimSpawnedUnits.Count} summoned unit(s) are still on the stage ({string.Join(", ", Mp.SimSpawnedUnits)})");
        if (Mp.SimAllyHidden) cleanup.Add("the ally is still hidden");
        if (Mp.SimLiveEffects.Count > 0) cleanup.Add($"{Mp.SimLiveEffects.Count} scripted effect(s) are still playing");
        if (!Animation_Loops()) cleanup.Add("the clip is left on 'play once'");
        if (Horizontal(Mp.CharacterPosition, stage.Champion) > 2f) cleanup.Add($"the champion is {Horizontal(Mp.CharacterPosition, stage.Champion):0} units from the stage point");
        if (stage.Dummy is { } sd && (Mp.TargetDummyPosition is not { } nd || Horizontal(nd, sd) > 2f)) cleanup.Add("the enemy dummy is not back on the stage");

        var errors = _log.Since(logMark).Where(e => e.Level == LogLevel.Error).Select(e => $"{e.Category}: {e.Message}").Distinct().ToList();
        if (Mp.Dx11Status.StartsWith("render threw", StringComparison.Ordinal) || Mp.Dx11Status.StartsWith("render failed", StringComparison.Ordinal))
            errors.Add("D3D11: " + Mp.Dx11Status);
        lock (_unhandled) errors.AddRange(_unhandled.Skip(unhandledMark).Select(u => "unhandled: " + u));

        var evidence = new AbilityEvidence
        {
            Key = ability.Key,
            Name = row.Name,
            Planned = script?.Steps.Select(s => new PlannedStep(s.Kind.ToString(), s.Time)).ToList() ?? new List<PlannedStep>(),
            Trace = trace.Select(t => new StepEvidence(t.Kind.ToString(), t.Planned, t.Clock)).ToList(),
            EndReached = endReached,
            TimedOut = timedOut,
            TimeoutSeconds = (script?.EndTime ?? 0) / _options.Speed * 3.0 + 40.0,
            ClipExpected = clipExpected,
            ClipChoiceSource = clipChoice?.Source,
            ClipDefinition = clipDefinition,
            ClipAuthored = ability.AuthoredClipName is { Length: > 0 },
            ExpectsEffects = SimCheckGrader.ExpectsEffects(ability) || hadSpawn || (script?.All(SimStepKind.Effect).Any() ?? false),
            AdapterSteps = adapterSteps,
            ClipSeen = clipSeen,
            ClipAdvanced = clipAdvanced,
            HitsExpected = hitsExpected,
            HitsLogged = hitsLogged,
            DisplacementExpected = displace.Count > 0,
            DisplacementApplied = displaceOk,
            NoiseRatio = noise,
            Frames = frames,
            ParticleBaseline = particleBaseline,
            CleanupProblems = cleanup,
            LogErrors = errors,
            Exception = exception,
            NeverStarted = script is null,
        };
        var grade = SimCheckGrader.Grade(evidence, _thresholds);

        var after = frames.Where(f => f.Label != "baseline").ToList();
        var report = new SimCheckAbilityReport(
            ability.Key, row.Name, false, row.StatusText, SplitReasons(row.Reasons), grade.Grade.ToString().ToUpperInvariant(), grade.Reasons, grade.Notes,
            script?.EndTime ?? 0,
            trace.Select(t => new SimCheckStepReport(t.Kind.ToString(), t.Planned, Math.Round(t.Clock, 3))).ToList(),
            clipExpected, clipSeen, clipAdvanced, hitsExpected, hitsLogged, displace.Count > 0, displaceOk,
            Math.Round(noise, 5), after.Count == 0 ? 0 : Math.Round(after.Max(f => f.ChangedRatio), 5), after.Count == 0 ? 0 : Math.Round(after.Max(f => f.EffectRatio), 5),
            particleBaseline, after.Count == 0 ? 0 : after.Max(f => f.LiveParticles), frameReports, cleanup, errors, Math.Round(wall.Elapsed.TotalSeconds, 2), adapterSteps);
        WorkingSetMb();
        return (report, cells, cellRegion, captions.ToArray());
    }

    /// <summary>Frames read before a cast to find the map's own particle peak.</summary>
    private const int PreCastFrames = 10;

    private bool Animation_Loops() => Mp.Animation.Loop;

    private static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFileName(a.Replace('\\', '/')), Path.GetFileName(b.Replace('\\', '/')), StringComparison.OrdinalIgnoreCase);

    private static float Horizontal(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    // ---- regions

    private PixelRect RegionFor(Dx11FrameCapture tap, SimScript script)
    {
        var points = new List<(float, float)>();
        void Add(Vector3 world)
        {
            foreach (var p in new[] { world, world + new Vector3(0f, 200f, 0f) })
                if (SimCheckFrames.ProjectToPixel(tap.View, tap.Projection, p, tap.Width, tap.Height, mirrorX: true, out float x, out float y))
                    points.Add((x, y));
        }
        Add(script.ChampionPosition);
        Add(script.EnemyPosition);
        if (script.AllyPosition is { } ally) Add(ally);
        Add(script.Aim);
        foreach (var d in script.All(SimStepKind.Displace)) if (d.To is { } to) Add(to);
        // M874: the places a summoned unit walks to and the units a mechanic moves are part of the picture too
        foreach (var st in script.Steps)
        {
            if (st.Kind == SimStepKind.Spawn) { foreach (var wp in st.Path ?? Array.Empty<Vector3>()) Add(wp); if (st.From is { } sf) Add(sf); }
            else if (st.Kind == SimStepKind.MoveUnit && st.To is { } mt) Add(mt);
        }
        var region = SimCheckFrames.RegionAround(points, tap.Width, tap.Height);
        if (Environment.GetEnvironmentVariable("SIMCHECK_DEBUG") == "1")
            Say($"    {script.Key} region {region} from {string.Join(' ', points.Select(p => $"({p.Item1:0},{p.Item2:0})"))} champ {script.ChampionPosition} enemy {script.EnemyPosition} aim {script.Aim}");
        return region;
    }

    private PixelRect? CasterBox(Dx11FrameCapture tap, Vector3 position, float tall = 190f)   // M895: the caster is as tall as its skinScale makes it
    {
        if (!SimCheckFrames.ProjectToPixel(tap.View, tap.Projection, position, tap.Width, tap.Height, true, out float fx, out float fy)
            || !SimCheckFrames.ProjectToPixel(tap.View, tap.Projection, position + new Vector3(0f, tall, 0f), tap.Width, tap.Height, true, out _, out float hy))
            return null;
        float height = MathF.Max(8f, MathF.Abs(fy - hy));
        return SimCheckFrames.Around(fx, fy, height * 0.7f, height * 1.2f, height * 0.15f, tap.Width, tap.Height);
    }

    // ================================================================ waiting

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout, int pollMilliseconds = 25)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) return false;
            await Task.Delay(pollMilliseconds);
        }
        return true;
    }

    /// <summary>The next frame the window's live loop renders, or null when none arrives in time (a window that is not drawing).</summary>
    private async Task<Dx11FrameCapture?> FrameAsync(TimeSpan timeout)
    {
        var next = _window!.NextDx11FrameAsync();
        var winner = await Task.WhenAny(next, Task.Delay(timeout));
        return winner == next ? ToCapture(await next) : null;
    }

    private static Dx11FrameCapture ToCapture(MeshPreviewWindow.Dx11FrameTap tap) =>
        new(tap.Bgra, tap.Width, tap.Height, tap.View, tap.Projection, tap.LiveParticles);

    /// <summary>A few frames, for the end-of-run reset and the camera to reach the screen.</summary>
    private async Task Settle()
    {
        for (int i = 0; i < 3; i++) await FrameAsync(TimeSpan.FromSeconds(5));
    }
}

/// <summary>One frame read back from the Character window's D3D11 surface.</summary>
internal sealed record Dx11FrameCapture(byte[] Bgra, int Width, int Height, Matrix4x4 View, Matrix4x4 Projection, int LiveParticles);
