using System.Text.RegularExpressions;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;

namespace ReyEngine.Formats.Characters.Simulation;

public sealed record BatchAbilitySummary(string Key, string Status, IReadOnlyList<string> Reasons);

public sealed record BatchChampionSummary(string Champion, string Status, IReadOnlyList<string> Reasons,
    IReadOnlyList<BatchAbilitySummary> Abilities);

public sealed record CodeCount(string Code, int Count);

/// <summary>The <c>_summary.json</c> document: counts per status, per ability, and the failure codes ranked by frequency.</summary>
public sealed record BatchSummary(
    string GameVersion,
    string GeneratorVersion,
    int Champions,
    IReadOnlyList<string> Skipped,
    IReadOnlyDictionary<string, int> ChampionStatus,
    IReadOnlyDictionary<string, int> AbilityStatus,
    IReadOnlyList<CodeCount> FailureCodes,
    IReadOnlyList<BatchChampionSummary> PerChampion);

/// <summary>
/// M867: generates the definitions of a roster. One WAD open per champion; the VFX closure and the animation graph are walked
/// once each inside <see cref="ChampionSimSource.Load"/>.
/// </summary>
public static class ChampionSimBatch
{
    private static readonly Regex LocaleSuffix = new(@"\.[a-z]{2}_[A-Z]{2}\.wad\.client$", RegexOptions.Compiled);

    /// <summary>Every <c>Champions/*.wad.client</c> without a locale suffix, by champion name, sorted. TFTChampion is excluded:
    /// it ships a stub CharacterRecord with no spells.</summary>
    public static IReadOnlyList<string> Roster(string championsDirectory) =>
        Directory.EnumerateFiles(championsDirectory, "*.wad.client")
            .Select(Path.GetFileName)
            .Where(f => f is not null && !LocaleSuffix.IsMatch(f))
            .Select(f => f![..^".wad.client".Length])
            .Where(n => !n.Equals("TFTChampion", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    public static string WadPathOf(string championsDirectory, string champion) =>
        Path.Combine(championsDirectory, champion + ".wad.client");

    /// <summary>Generate and write one JSON per champion plus <c>_summary.json</c> into <paramref name="outDirectory"/>.
    /// A champion whose record authors no abilities is skipped (and listed), not written.</summary>
    public static BatchSummary Run(string championsDirectory, IEnumerable<string> champions, string outDirectory,
        ChampionSimContext context, IHashResolver? wadResolver, Action<string>? log = null)
    {
        Directory.CreateDirectory(outDirectory);
        var summaries = new List<BatchChampionSummary>();
        var skipped = new List<string>();
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        var abilityStatus = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var championStatus = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var champion in champions.OrderBy(c => c, StringComparer.Ordinal))
        {
            string path = WadPathOf(championsDirectory, champion);
            if (!File.Exists(path)) { skipped.Add($"{champion}: no WAD"); continue; }

            // One champion's failure - in the read, the JSON write (a NaN in its data) or the grading - costs that
            // champion, never the roster run: the summary is written only at the end.
            ChampionSimDefinition def;
            ChampionValidation verdict;
            try
            {
                using (var wad = WadArchive.Open(path, wadResolver))
                    def = ChampionSimGenerator.Generate(wad, champion, context);
                if (def.Abilities.Count == 0) { skipped.Add($"{champion}: the record authors no abilities"); continue; }
                byte[] json = ChampionSimJson.Write(def);
                verdict = ChampionSimValidator.Validate(def);
                File.WriteAllBytes(Path.Combine(outDirectory, champion + ".json"), json);
            }
            catch (Exception ex)
            {
                skipped.Add($"{champion}: {ex.GetType().Name}: {ex.Message}");
                log?.Invoke($"{champion,-16} ERROR {ex.Message}");
                continue;
            }

            foreach (var a in verdict.Abilities)
            {
                abilityStatus[a.Status.ToString()] = abilityStatus.GetValueOrDefault(a.Status.ToString()) + 1;
                foreach (var check in a.Checks)
                    foreach (var code in check.Codes ?? Array.Empty<string>())
                        codes[code] = codes.GetValueOrDefault(code) + 1;
            }
            championStatus[verdict.Status.ToString()] = championStatus.GetValueOrDefault(verdict.Status.ToString()) + 1;
            summaries.Add(new BatchChampionSummary(champion, verdict.Status.ToString(), verdict.Reasons,
                verdict.Abilities.Select(a => new BatchAbilitySummary(a.Key, a.Status.ToString(), a.Reasons)).ToList()));

            log?.Invoke($"{champion,-16} {verdict.Status,-10} " + string.Join(' ',
                verdict.Abilities.Select(a => $"{a.Key.Split(' ')[0]}={a.Status.ToString()[0]}")));
        }

        var summary = new BatchSummary(
            context.GameVersion, ChampionSimDefinition.CurrentGeneratorVersion, summaries.Count, skipped,
            championStatus, abilityStatus,
            codes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new CodeCount(kv.Key, kv.Value)).ToList(),
            summaries);
        File.WriteAllText(Path.Combine(outDirectory, "_summary.json"), ChampionSimJson.WriteString(summary),
            new System.Text.UTF8Encoding(false));
        return summary;
    }
}
