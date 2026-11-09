using System.Diagnostics;
using System.Runtime.InteropServices;
using ReyEngine.Core;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Characters.Simulation;
using ReyEngine.Formats.LtkGameData;

namespace ReyEngine.App;

/// <summary>
/// M867: <c>ReyEngine.App.exe --simgen &lt;Champion|all&gt; [--out &lt;dir&gt;] [--game &lt;Game folder&gt;]</c>.
///
/// <para>Headless: handled in <c>Program.Main</c> before Avalonia starts, so no window, no settings.json and no project is
/// touched. Writes <c>&lt;Champion&gt;.json</c> per champion and <c>_summary.json</c> into the output folder, which defaults
/// to <c>data/champion_sims/&lt;gameVersion&gt;/</c> under <see cref="ReyPaths.DataRoot"/> - the same data root the hash
/// tables and shader catalogues use. The game folder defaults to the first install <see cref="GameInstallLocator"/> finds
/// (Live before PBE). Exit code 0 on success, 2 for a usage or install problem.</para>
/// </summary>
internal static class SimGenCommand
{
    public static bool Handles(string[] args) =>
        args.Length > 0 && args[0].Equals("--simgen", StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);

    public static int Run(string[] args)
    {
        // A WinExe has no console of its own: join the one that started it so the summary lines are visible.
        try { AttachConsole(-1); } catch { }
        var stopwatch = Stopwatch.StartNew();

        string? target = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        string? outDir = null, game = null;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--out", StringComparison.OrdinalIgnoreCase)) outDir = args[i + 1];
            else if (args[i].Equals("--game", StringComparison.OrdinalIgnoreCase)) game = args[i + 1];
        }
        if (target is null)
        {
            Console.Error.WriteLine("usage: ReyEngine.App.exe --simgen <Champion|all> [--out <dir>] [--game <Game folder>]");
            return 2;
        }

        game ??= GameInstallLocator.Discover().FirstOrDefault()?.GameDirectory;
        string? championsDir = game is null ? null : Path.Combine(game, "DATA", "FINAL", "Champions");
        if (championsDir is null || !Directory.Exists(championsDir))
        {
            Console.Error.WriteLine($"no League install with DATA/FINAL/Champions found{(game is null ? "" : " at " + game)}; pass --game <Game folder>.");
            return 2;
        }

        string version = ChampionSimSource.ReadGameVersion(game!);
        outDir ??= Path.Combine(ReyPaths.DataRoot, "champion_sims", version);

        var roster = ChampionSimBatch.Roster(championsDir);
        var champions = target.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? roster
            : roster.Where(c => c.Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (champions.Count == 0)
        {
            Console.Error.WriteLine($"'{target}' is not a champion WAD in {championsDir}.");
            return 2;
        }

        {
            var built = GameFileIndex.Shared(game!).EnsureBuilt();
            Console.WriteLine($"game file index: {built.Archives} archives, {built.Chunks} chunks, {(built.FromCache ? "cache" : "built")} in {built.Elapsed.TotalMilliseconds:0} ms, table {built.TableBytes / 1048576.0:0.0} MiB");
        }
        var db = new HashSyncService().LoadLocal(_ => { });
        var resolver = new WadPathResolver(db);
        var client = ClientNameCatalog.Load(ClientNameCatalog.FindDataWad(ClientNameCatalog.InstallRootFromChampions(championsDir) ?? ""));
        using var abilities = ClientAbilityCatalog.Open(ClientNameCatalog.FindDataWad(ClientNameCatalog.InstallRootFromChampions(championsDir) ?? ""));

        var context = new ChampionSimContext
        {
            GameVersion = version,
            ResolveBinName = h => db.TryGetBinName(h, out var n) ? n : null,
            ResolveWadPath = h => db.TryGetPath(h, out var p) ? p : null,
            Client = client,
            ClientAbilities = abilities,
            ReadGameFile = GameFileIndex.Shared(game!).AsReader(),
        };

        Console.WriteLine($"simgen {version}: {champions.Count} champion(s) -> {outDir}");
        var summary = ChampionSimBatch.Run(championsDir, champions, outDir, context, resolver, Console.WriteLine);

        Console.WriteLine();
        Console.WriteLine($"champions {summary.Champions}, skipped {summary.Skipped.Count}; "
            + string.Join(", ", summary.ChampionStatus.Select(kv => $"{kv.Key} {kv.Value}"))
            + " | abilities " + string.Join(", ", summary.AbilityStatus.Select(kv => $"{kv.Key} {kv.Value}"))
            + $" | {stopwatch.Elapsed.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s");
        foreach (var s in summary.Skipped) Console.WriteLine("skipped " + s);
        foreach (var c in summary.FailureCodes.Take(10)) Console.WriteLine($"  {c.Count,6}  {c.Code}");
        Console.Out.Flush();
        return 0;
    }
}
