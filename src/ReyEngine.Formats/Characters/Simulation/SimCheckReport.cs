using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ReyEngine.Formats.Characters.Simulation;

public sealed record SimCheckStepReport(string Kind, double Planned, double Clock);

public sealed record SimCheckFrameReport(string Label, double PlannedTime, double Clock, double ChangedRatio, double EffectRatio,
    int LiveParticles, int Width, int Height, int[] Region);

public sealed record SimCheckAbilityReport(
    string Key,
    string? Name,
    bool Wrapper,
    string AssetStatus,
    IReadOnlyList<string> AssetReasons,
    string Grade,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Notes,
    double EndTime,
    IReadOnlyList<SimCheckStepReport> Steps,
    string? ClipExpected,
    string? ClipPlaying,
    bool ClipAdvanced,
    int HitsExpected,
    int HitsLogged,
    bool DisplacementExpected,
    bool DisplacementApplied,
    double NoiseRatio,
    double PeakChangedRatio,
    double PeakEffectRatio,
    int ParticleBaseline,
    int ParticlePeak,
    IReadOnlyList<SimCheckFrameReport> Frames,
    IReadOnlyList<string> CleanupProblems,
    IReadOnlyList<string> LogErrors,
    double WallSeconds,
    /// <summary>M874: each scripted-mechanic step of the plan and whether it passed (null for a report written before M874).</summary>
    IReadOnlyList<AdapterStepEvidence>? AdapterSteps = null);

public sealed record SimCheckChampionReport(
    string Champion,
    string GameVersion,
    string GeneratorVersion,
    string CheckVersion,
    string Status,
    IReadOnlyList<string> Reasons,
    string? LoadError,
    double LoadSeconds,
    double WallSeconds,
    double WorkingSetMb,
    string Renderer,
    string Window,
    string Speed,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SimCheckAbilityReport> Abilities,
    /// <summary>M873: the window placement the run used ("minimized", "offscreen", "corner"); part of the resume key.</summary>
    string? Placement = null);

public sealed record SimCheckSummaryEntry(string Champion, string Status, int Abilities, int Complete, int Incomplete, int Blocked,
    double WallSeconds, double WorkingSetMb, IReadOnlyList<string> Reasons);

public sealed record SimCheckReasonCount(string Reason, int Count);

public sealed record SimCheckSummary(
    string GameVersion,
    string GeneratorVersion,
    string CheckVersion,
    int Champions,
    IReadOnlyDictionary<string, int> ChampionStatus,
    IReadOnlyDictionary<string, int> AbilityStatus,
    IReadOnlyList<SimCheckReasonCount> CommonReasons,
    IReadOnlyList<SimCheckSummaryEntry> PerChampion);

/// <summary>
/// M871: reports on disk: JSON (indented, camelCase, LF, no BOM - the same form as the definitions), the roster summary, the
/// resume rule and the champion selection of the command line.
/// </summary>
public static class SimCheckReports
{
    /// <summary>Bumped whenever the checker's evidence or grading would change a verdict for the same data; part of the resume key.</summary>
    public const string CheckVersion = "M874.1";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(object report) => JsonSerializer.Serialize(report, Options) + "\n";

    public static SimCheckChampionReport? Read(string json)
    {
        try { return JsonSerializer.Deserialize<SimCheckChampionReport>(json, Options); }
        catch (JsonException) { return null; }
    }

    public static string ReportPath(string outRoot, string gameVersion, string champion) =>
        Path.Combine(outRoot, gameVersion, champion, "report.json");

    /// <summary>
    /// Is this champion already done? True when a readable report exists for the same game version, definition generator and checker, and - when the
    /// caller names them - the same speed and window placement (a run at another speed or in another window state is another measurement). M873: a
    /// BLOCKED report is never done (the champion did not load; a retry may), and a report that cannot be read (a run killed mid-write) is not done.
    /// </summary>
    public static bool IsDone(string reportPath, string gameVersion, string generatorVersion, bool force, string? speed = null, string? placement = null)
    {
        if (force || !File.Exists(reportPath)) return false;
        try
        {
            var r = Read(File.ReadAllText(reportPath));
            return r is not null && r.GameVersion == gameVersion && r.GeneratorVersion == generatorVersion && r.CheckVersion == CheckVersion
                   && !string.Equals(r.Status, "BLOCKED", StringComparison.OrdinalIgnoreCase)
                   && (speed is null || r.Speed == speed)
                   && (placement is null || string.Equals(r.Placement, placement, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// M873: the process exit code of a run: 0 when every checked champion is COMPLETE, 3 when any is BLOCKED or INCOMPLETE or when a champion's report could not
    /// be written (<paramref name="skipped"/>). Usage and install problems (2) and crashes (1) are decided by the caller.
    /// </summary>
    public const int ExitIncomplete = 3;
    public static int ExitCode(IEnumerable<SimCheckChampionReport> reports, int skipped = 0) =>
        skipped > 0 || reports.Any(r => !string.Equals(r.Status, "COMPLETE", StringComparison.OrdinalIgnoreCase)) ? ExitIncomplete : 0;

    /// <summary>Write via a temp file and a rename, so an interrupted run never leaves a half report that the resume rule would accept.</summary>
    public static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    // ---- which champions

    public sealed record Selection(IReadOnlyList<string> Champions, IReadOnlyList<string> Unknown, string? Error);

    /// <summary>
    /// <c>all</c>, <c>from:&lt;Champion&gt;</c> (that champion and every one after it in the roster), <c>first:&lt;N&gt;</c>, or a comma
    /// separated list (kept in the order given, names matched case-insensitively). The roster is the alphabetical one <c>--simgen</c> uses.
    /// </summary>
    public static Selection Select(IReadOnlyList<string> roster, string spec)
    {
        spec = spec.Trim();
        if (spec.Length == 0) return new Selection(Array.Empty<string>(), Array.Empty<string>(), "no champion given");
        if (spec.Equals("all", StringComparison.OrdinalIgnoreCase)) return new Selection(roster, Array.Empty<string>(), null);
        if (spec.StartsWith("from:", StringComparison.OrdinalIgnoreCase))
        {
            string name = spec[5..].Trim();
            int i = IndexOf(roster, name);
            return i < 0 ? new Selection(Array.Empty<string>(), new[] { name }, $"'{name}' is not in the roster")
                         : new Selection(roster.Skip(i).ToList(), Array.Empty<string>(), null);
        }
        if (spec.StartsWith("first:", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(spec[6..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
                return new Selection(Array.Empty<string>(), Array.Empty<string>(), "first:<N> needs a positive number");
            return new Selection(roster.Take(n).ToList(), Array.Empty<string>(), null);
        }
        var picked = new List<string>();
        var unknown = new List<string>();
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int i = IndexOf(roster, part);
            if (i < 0) unknown.Add(part);
            else if (!picked.Contains(roster[i])) picked.Add(roster[i]);
        }
        return new Selection(picked, unknown, unknown.Count > 0 ? "not in the roster: " + string.Join(", ", unknown) : null);
    }

    private static int IndexOf(IReadOnlyList<string> roster, string name)
    {
        for (int i = 0; i < roster.Count; i++)
            if (roster[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ---- the roster summary

    public static SimCheckSummary Summarize(string gameVersion, string generatorVersion, IEnumerable<SimCheckChampionReport> reports)
    {
        var list = reports.OrderBy(r => r.Champion, StringComparer.Ordinal).ToList();
        var champion = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var ability = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var per = new List<SimCheckSummaryEntry>();
        foreach (var r in list)
        {
            champion[r.Status] = champion.GetValueOrDefault(r.Status) + 1;
            foreach (var a in r.Abilities)
            {
                if (a.Wrapper) continue;      // a wrapper restates its sub-casts
                ability[a.Grade] = ability.GetValueOrDefault(a.Grade) + 1;
                foreach (string reason in a.Reasons) reasons[Normalize(reason)] = reasons.GetValueOrDefault(Normalize(reason)) + 1;
            }
            if (r.Status == "BLOCKED" && r.LoadError is { Length: > 0 } err) reasons[Normalize("load: " + err)] = reasons.GetValueOrDefault(Normalize("load: " + err)) + 1;
            var real = r.Abilities.Where(a => !a.Wrapper).ToList();
            per.Add(new SimCheckSummaryEntry(r.Champion, r.Status, real.Count, real.Count(a => a.Grade == "COMPLETE"),
                real.Count(a => a.Grade == "INCOMPLETE"), real.Count(a => a.Grade == "BLOCKED"), r.WallSeconds, r.WorkingSetMb,
                r.Status == "COMPLETE" ? Array.Empty<string>()
                    : r.LoadError is { Length: > 0 } le ? new[] { "load: " + le }
                    : real.Where(a => a.Grade != "COMPLETE").Select(a => a.Key + ": " + string.Join("; ", a.Reasons)).ToList()));
        }
        return new SimCheckSummary(gameVersion, generatorVersion, CheckVersion, list.Count, champion, ability,
            reasons.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(40)
                .Select(kv => new SimCheckReasonCount(kv.Key, kv.Value)).ToList(), per);
    }

    /// <summary>A reason with its numbers and names taken out, so "clip missing: 'a.anm' expected" and "... 'b.anm' ..." count as one.</summary>
    public static string Normalize(string reason)
    {
        // only PAIRED quotes are names; a lone apostrophe (an exception message, "don't") stays as it is
        string unquoted = System.Text.RegularExpressions.Regex.Replace(reason, @"(?<![\p{L}\p{N}])'[^'\r\n]*'(?![\p{L}\p{N}])", "'_'");
        var sb = new StringBuilder(unquoted.Length);
        foreach (char c in unquoted) sb.Append(char.IsDigit(c) ? '#' : c);
        // collapse digit runs and decimals
        string s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"#+([.,]#+)?", "#");
        int cut = s.IndexOf(" (", StringComparison.Ordinal);
        return (cut > 0 ? s[..cut] : s).Trim();
    }

    public static string SummaryMarkdown(SimCheckSummary s)
    {
        var sb = new StringBuilder();
        sb.Append("# Simulation check - game ").Append(s.GameVersion).Append('\n').Append('\n');
        sb.Append("Definitions ").Append(s.GeneratorVersion).Append(", checker ").Append(s.CheckVersion).Append(". ")
          .Append(s.Champions).Append(" champion(s): ").Append(string.Join(", ", s.ChampionStatus.Select(kv => kv.Value + " " + kv.Key))).Append(".\n");
        sb.Append("Abilities: ").Append(string.Join(", ", s.AbilityStatus.Select(kv => kv.Value + " " + kv.Key))).Append(".\n\n");
        if (s.CommonReasons.Count > 0)
        {
            sb.Append("## Most common reasons\n\n");
            foreach (var r in s.CommonReasons.Take(15)) sb.Append("- ").Append(r.Count).Append("x ").Append(r.Reason).Append('\n');
            sb.Append('\n');
        }
        sb.Append("## Champions\n\n| Champion | Status | Abilities (ok / partial / blocked) | Seconds | MB |\n| --- | --- | --- | --- | --- |\n");
        foreach (var e in s.PerChampion)
            sb.Append("| ").Append(e.Champion).Append(" | ").Append(e.Status).Append(" | ").Append(e.Complete).Append(" / ").Append(e.Incomplete)
              .Append(" / ").Append(e.Blocked).Append(" | ").Append(e.WallSeconds.ToString("0", CultureInfo.InvariantCulture))
              .Append(" | ").Append(e.WorkingSetMb.ToString("0", CultureInfo.InvariantCulture)).Append(" |\n");
        sb.Append("\n## Not complete\n\n");
        foreach (var e in s.PerChampion.Where(e => e.Status != "COMPLETE"))
        {
            sb.Append("- **").Append(e.Champion).Append("** ").Append(e.Status).Append('\n');
            foreach (string r in e.Reasons) sb.Append("  - ").Append(r).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Read every champion report under <c>&lt;out&gt;/&lt;gameVersion&gt;/*/report.json</c> for the same game version.</summary>
    public static IReadOnlyList<SimCheckChampionReport> ReadAll(string outRoot, string gameVersion)
    {
        string dir = Path.Combine(outRoot, gameVersion);
        var list = new List<SimCheckChampionReport>();
        if (!Directory.Exists(dir)) return list;
        foreach (string folder in Directory.EnumerateDirectories(dir))
        {
            string file = Path.Combine(folder, "report.json");
            if (!File.Exists(file)) continue;
            try { if (Read(File.ReadAllText(file)) is { } r && r.GameVersion == gameVersion && r.CheckVersion == CheckVersion) list.Add(r); }
            catch (IOException) { }
        }
        return list;
    }
}
