using System.Buffers.Binary;

namespace ReyEngine.Core.Build;

public sealed record DeadAssetEntry(string RelPath, string AbsPath, long Bytes);

public sealed record OutsideMapGroup(string Folder, int Files, long Bytes, int WadsTouched, string SampleWads);

public sealed record AssetUsageReport(
    int TotalFiles, long TotalBytes,
    IReadOnlyList<DeadAssetEntry> Dead, long DeadBytes,
    IReadOnlyList<OutsideMapGroup> OutsideMap, int OutsideMapFiles, long OutsideMapBytes,
    int MapScopedFiles, long MapScopedBytes,
    IReadOnlyList<string> MapWads);

/// <summary>
/// M136: which project files does the game actually need? Three verdicts per packable file:
///  - DEAD: no game WAD ships the path AND no project bin references it — nothing can ever
///    request it (same two-condition rule as the M129 unused-bin check). Safe to delete.
///  - OUTSIDE MAP: the path exists only in wads that are NOT this map's own — the file recolors
///    champion/item/TFT content and is what forces loaders to patch dozens of extra wads.
///  - map-scoped: lives in the map's own wad(s); normal mod content.
/// </summary>
public static class AssetUsageService
{
    public static AssetUsageReport Analyze(
        IReadOnlyList<(ulong Hash, string RelPath, string AbsPath, long Bytes)> projectFiles,
        string gameDataFinalDir,
        IReadOnlyCollection<string> mapNames,                    // e.g. ["map11"]
        Func<string, bool> referencedByProjectBins,              // rel path -> any project bin mentions it
        IProgress<(double Frac, string Stage)>? progress = null,
        CancellationToken ct = default)
    {
        var gameWads = Directory.Exists(gameDataFinalDir)
            ? Directory.EnumerateFiles(gameDataFinalDir, "*.wad.client", SearchOption.AllDirectories).ToList()
            : new List<string>();

        bool IsMapWad(string wadRel) => mapNames.Any(m =>
            wadRel.Contains($"/{m}.", StringComparison.OrdinalIgnoreCase)
            || wadRel.Contains($"/{m}.wad", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(wadRel).StartsWith(m + ".", StringComparison.OrdinalIgnoreCase));

        // per project hash: does it exist in a map wad / any other wad (collect names for display)
        var projectHashes = projectFiles.Select(p => p.Hash).ToHashSet();
        var inMapWad = new HashSet<ulong>();
        var otherWads = new Dictionary<ulong, List<string>>();
        int done = 0;
        foreach (var wadPath in gameWads)
        {
            ct.ThrowIfCancellationRequested();
            string wadRel = Path.GetRelativePath(gameDataFinalDir, wadPath).Replace('\\', '/');
            bool isMap = IsMapWad(wadRel);
            progress?.Report((0.9 * done++ / Math.Max(1, gameWads.Count), $"Scanning {wadRel}…"));
            foreach (var h in ReadTocHashes(wadPath))
            {
                if (!projectHashes.Contains(h)) continue;
                if (isMap) inMapWad.Add(h);
                else
                {
                    if (!otherWads.TryGetValue(h, out var list)) otherWads[h] = list = new List<string>(2);
                    if (list.Count < 3) list.Add(wadRel);
                }
            }
        }

        progress?.Report((0.92, "Classifying…"));
        var dead = new List<DeadAssetEntry>();
        var outsideByFolder = new Dictionary<string, (int Files, long Bytes, HashSet<string> Wads)>(StringComparer.OrdinalIgnoreCase);
        int mapFiles = 0; long mapBytes = 0; long deadBytes = 0; int outsideFiles = 0; long outsideBytes = 0;

        foreach (var f in projectFiles)
        {
            bool inMap = inMapWad.Contains(f.Hash);
            bool inOther = otherWads.ContainsKey(f.Hash);
            // loose hash-named files carry no path info — never judged
            bool loose = !f.RelPath.Contains('/');

            if (inMap || loose) { mapFiles++; mapBytes += f.Bytes; }
            else if (inOther)
            {
                outsideFiles++; outsideBytes += f.Bytes;
                var key = FolderKey(f.RelPath);
                var cur = outsideByFolder.TryGetValue(key, out var v) ? v : (0, 0L, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                foreach (var w in otherWads[f.Hash]) cur.Item3.Add(w);
                outsideByFolder[key] = (cur.Item1 + 1, cur.Item2 + f.Bytes, cur.Item3);
            }
            else if (!referencedByProjectBins(f.RelPath))
            {
                dead.Add(new DeadAssetEntry(f.RelPath, f.AbsPath, f.Bytes));
                deadBytes += f.Bytes;
            }
            else { mapFiles++; mapBytes += f.Bytes; }   // new content referenced by the mod's own bins
        }

        var outside = outsideByFolder
            .Select(kv => new OutsideMapGroup(kv.Key, kv.Value.Files, kv.Value.Bytes, kv.Value.Wads.Count,
                string.Join(", ", kv.Value.Wads.Take(3))))
            .OrderByDescending(g => g.WadsTouched).ThenByDescending(g => g.Bytes)
            .ToList();
        dead.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        progress?.Report((1.0, "Asset usage analysed."));
        return new AssetUsageReport(
            projectFiles.Count, projectFiles.Sum(f => f.Bytes),
            dead, deadBytes,
            outside, outsideFiles, outsideBytes,
            mapFiles, mapBytes,
            gameWads.Select(w => Path.GetRelativePath(gameDataFinalDir, w).Replace('\\', '/')).Where(IsMapWad).ToList());
    }

    private static string FolderKey(string rel)
    {
        var parts = rel.Replace('\\', '/').Split('/');
        return string.Join('/', parts.Take(Math.Min(3, Math.Max(1, parts.Length - 1))));
    }

    private static IEnumerable<ulong> ReadTocHashes(string wadPath)
    {
        using var fs = File.OpenRead(wadPath);
        var header = new byte[4 + 256 + 8 + 4];
        if (fs.Read(header, 0, header.Length) != header.Length) yield break;
        if (header[0] != 'R' || header[1] != 'W' || header[2] != 3) yield break;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(268));
        var table = new byte[count * 32L];
        if (fs.Read(table, 0, table.Length) != table.Length) yield break;
        for (int i = 0; i < count; i++)
            yield return BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(i * 32));
    }
}
