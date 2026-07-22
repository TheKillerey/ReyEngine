using System.Buffers.Binary;

namespace ReyEngine.Core.Build;

public sealed record WadFootprintEntry(string WadName, int Files, long Bytes);

public sealed record FanoutSource(string Folder, int Files, int WadsTouched, long Bytes);

public sealed record OverlayFootprint(
    int ProjectFiles,
    long ProjectBytes,
    int GameWadsScanned,
    int TouchedWads,
    int UnmatchedFiles,
    IReadOnlyList<WadFootprintEntry> Wads,
    IReadOnlyList<FanoutSource> TopSources);

/// <summary>
/// M134: how wide will this mod hit the game? Loaders (LTK, cslol) overlay mod files by PATH HASH
/// across every game WAD that contains the hash — shared paths (assets/characters/…) exist in
/// dozens of champion WADs, so a texture-heavy map mod can force 200+ WADs to be patched. That
/// scale crashed the game via LTK (E_INVALIDARG at load, Jul 2026). This service reports the
/// fan-out BEFORE the loader finds out: which WADs get touched, and which project folders cause it.
/// </summary>
public static class OverlayFootprintService
{
    public static OverlayFootprint Analyze(
        IReadOnlyList<(ulong Hash, string RelPath, long Bytes)> projectFiles,
        string gameDataFinalDir,
        IProgress<(double Frac, string Stage)>? progress = null,
        CancellationToken ct = default)
    {
        var byHash = new Dictionary<ulong, (string Rel, long Bytes)>(projectFiles.Count);
        foreach (var f in projectFiles) byHash[f.Hash] = (f.RelPath, f.Bytes);

        var gameWads = Directory.Exists(gameDataFinalDir)
            ? Directory.EnumerateFiles(gameDataFinalDir, "*.wad.client", SearchOption.AllDirectories).ToList()
            : new List<string>();

        var wadEntries = new List<WadFootprintEntry>();
        // per project-file: how many wads carry its hash (for the fan-out sources)
        var hitCount = new Dictionary<ulong, int>();
        var wadsPerFolder = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        int done = 0;
        foreach (var wadPath in gameWads)
        {
            ct.ThrowIfCancellationRequested();
            string wadName = Path.GetRelativePath(gameDataFinalDir, wadPath).Replace('\\', '/');
            progress?.Report(((double)done++ / Math.Max(1, gameWads.Count), $"Scanning {wadName}…"));

            int files = 0; long bytes = 0;
            foreach (var h in ReadTocHashes(wadPath))
            {
                if (!byHash.TryGetValue(h, out var pf)) continue;
                files++; bytes += pf.Bytes;
                hitCount[h] = hitCount.GetValueOrDefault(h) + 1;
                var folder = FolderKey(pf.Rel);
                if (!wadsPerFolder.TryGetValue(folder, out var set)) wadsPerFolder[folder] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(wadName);
            }
            if (files > 0) wadEntries.Add(new WadFootprintEntry(wadName, files, bytes));
        }
        wadEntries.Sort((a, b) => b.Files.CompareTo(a.Files));

        // fan-out sources: project folders ranked by how many wads their files touch
        var folderFiles = new Dictionary<string, (int Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in projectFiles)
        {
            var key = FolderKey(f.RelPath);
            var cur = folderFiles.GetValueOrDefault(key);
            folderFiles[key] = (cur.Files + 1, cur.Bytes + f.Bytes);
        }
        var sources = wadsPerFolder
            .Select(kv => new FanoutSource(kv.Key, folderFiles.GetValueOrDefault(kv.Key).Files,
                kv.Value.Count, folderFiles.GetValueOrDefault(kv.Key).Bytes))
            .OrderByDescending(s => s.WadsTouched).ThenByDescending(s => s.Bytes)
            .ToList();

        int unmatched = projectFiles.Count(f => !hitCount.ContainsKey(f.Hash));

        return new OverlayFootprint(
            projectFiles.Count, projectFiles.Sum(f => f.Bytes), gameWads.Count,
            wadEntries.Count, unmatched, wadEntries, sources);
    }

    /// <summary>Group key for fan-out attribution: first three path segments
    /// (e.g. <c>assets/characters/ahri</c>) — specific enough to act on, coarse enough to rank.</summary>
    private static string FolderKey(string rel)
    {
        var parts = rel.Replace('\\', '/').Split('/');
        return string.Join('/', parts.Take(Math.Min(3, Math.Max(1, parts.Length - 1))));
    }

    /// <summary>Raw v3.x TOC hash read — no entry objects, no resolver; scanning 200 wads stays fast.</summary>
    private static IEnumerable<ulong> ReadTocHashes(string wadPath)
    {
        byte[] header;
        using var fs = File.OpenRead(wadPath);
        header = new byte[4 + 256 + 8 + 4];
        if (fs.Read(header, 0, header.Length) != header.Length) yield break;
        if (header[0] != 'R' || header[1] != 'W' || header[2] != 3) yield break;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(268));
        var table = new byte[count * 32L];
        if (fs.Read(table, 0, table.Length) != table.Length) yield break;
        for (int i = 0; i < count; i++)
            yield return BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(i * 32));
    }
}
