using System.Security.Cryptography;
using System.Text.Json;

namespace ReyEngine.Core.Cleanup;

public sealed record CleanupRunResult(
    string ManifestPath, int Moved, int Failed, int FoldersRemoved, long BytesFreed,
    IReadOnlyList<string> Log);

/// <summary>
/// M302: the write half of Cleanup Project. Nothing is ever deleted outright - selected files are MOVED
/// into a project-local recycle area and recorded in a manifest that is written BEFORE the first move,
/// so an interrupted run still leaves a complete record to restore from.
/// </summary>
public static class CleanupExecutor
{
    private const string CleanupDir = "cleanup";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string BackupRoot(string projectRoot) =>
        Path.Combine(projectRoot, Projects.ReyProjectService.FolderMetaDir, CleanupDir);

    /// <summary>Move the selected files into the recycle area. <paramref name="runId"/> is supplied by the
    /// caller because <c>DateTime</c> formatting is the caller's business and a test needs it stable.</summary>
    public static CleanupRunResult Run(string projectRoot, string runId,
        IReadOnlyList<CleanupCandidate> selected, IProgress<(double, string)>? progress = null)
    {
        var log = new List<string>();
        string runDir = Path.Combine(BackupRoot(projectRoot), runId);
        string filesDir = Path.Combine(runDir, "files");
        Directory.CreateDirectory(filesDir);

        string stamp = DateTime.UtcNow.ToString("O");
        var manifest = new CleanupManifest
        {
            Id = runId,
            CreatedUtc = stamp,
            ProjectPath = projectRoot,
        };

        // Build the full record first - including the hash of every file as it is RIGHT NOW - so the
        // manifest on disk describes the whole intended run before a single file moves.
        var files = selected.Where(c => c.Group != CleanupGroup.EmptyFolder).ToList();
        var folders = selected.Where(c => c.Group == CleanupGroup.EmptyFolder).ToList();
        int i = 0;
        foreach (var c in files)
        {
            progress?.Report((0.4 * i++ / Math.Max(1, files.Count), "Recording…"));
            string safeRel = Path.Combine(Sanitize(c.Folder), c.RelPath.Replace('/', Path.DirectorySeparatorChar));
            manifest.Entries.Add(new CleanupManifestEntry
            {
                OriginalPath = c.AbsPath,
                BackupPath = Path.Combine(filesDir, safeRel),
                Bytes = c.Bytes,
                Reason = c.Reason,
                Group = c.Group.ToString(),
                Sha256 = TryHash(c.AbsPath),
                RemovedUtc = stamp,
            });
        }
        foreach (var f in folders) manifest.EmptyFolders.Add(f.AbsPath);

        string manifestPath = Path.Combine(runDir, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, Json));

        int moved = 0, failed = 0; long freed = 0;
        i = 0;
        foreach (var e in manifest.Entries)
        {
            progress?.Report((0.4 + 0.5 * i++ / Math.Max(1, manifest.Entries.Count), "Moving…"));
            try
            {
                if (!File.Exists(e.OriginalPath)) { log.Add($"missing, skipped: {e.OriginalPath}"); failed++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(e.BackupPath)!);
                File.Move(e.OriginalPath, e.BackupPath, overwrite: true);
                moved++; freed += e.Bytes;
            }
            catch (Exception ex) { log.Add($"failed: {e.OriginalPath} - {ex.Message}"); failed++; }
        }

        // Folders last: files moving out is exactly what can make a folder empty.
        int folderCount = 0;
        foreach (var dir in manifest.EmptyFolders.OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                { Directory.Delete(dir); folderCount++; }
            }
            catch (Exception ex) { log.Add($"folder failed: {dir} - {ex.Message}"); }
        }

        progress?.Report((1.0, "Cleanup complete."));
        return new CleanupRunResult(manifestPath, moved, failed, folderCount, freed, log);
    }

    /// <summary>Every cleanup run still held in the recycle area, newest first.</summary>
    public static IReadOnlyList<CleanupManifest> ListRuns(string projectRoot)
    {
        var root = BackupRoot(projectRoot);
        var runs = new List<CleanupManifest>();
        if (!Directory.Exists(root)) return runs;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var mf = Path.Combine(dir, "manifest.json");
            if (!File.Exists(mf)) continue;
            try
            {
                if (JsonSerializer.Deserialize<CleanupManifest>(File.ReadAllText(mf)) is { } m) runs.Add(m);
            }
            catch { /* a corrupt manifest must not hide the others */ }
        }
        return runs.OrderByDescending(r => r.CreatedUtc, StringComparer.Ordinal).ToList();
    }

    /// <summary>Put a run's files back where they came from.</summary>
    public static (int Restored, int Failed, IReadOnlyList<string> Log) Restore(string projectRoot, string runId)
    {
        var log = new List<string>();
        string runDir = Path.Combine(BackupRoot(projectRoot), runId);
        string manifestPath = Path.Combine(runDir, "manifest.json");
        if (!File.Exists(manifestPath)) return (0, 0, new[] { "No manifest for run " + runId });

        CleanupManifest? m;
        try { m = JsonSerializer.Deserialize<CleanupManifest>(File.ReadAllText(manifestPath)); }
        catch (Exception ex) { return (0, 0, new[] { "Unreadable manifest: " + ex.Message }); }
        if (m is null) return (0, 0, new[] { "Empty manifest." });

        int ok = 0, bad = 0;
        foreach (var e in m.Entries)
        {
            try
            {
                if (!File.Exists(e.BackupPath)) { log.Add($"backup gone: {e.BackupPath}"); bad++; continue; }
                // Never clobber: if something has since taken the original path, that newer file wins and
                // the backup stays put rather than being silently overwritten by an undo.
                if (File.Exists(e.OriginalPath)) { log.Add($"occupied, left in backup: {e.OriginalPath}"); bad++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(e.OriginalPath)!);
                File.Move(e.BackupPath, e.OriginalPath);
                ok++;
            }
            catch (Exception ex) { log.Add($"restore failed: {e.OriginalPath} - {ex.Message}"); bad++; }
        }
        foreach (var d in m.EmptyFolders)
            try { Directory.CreateDirectory(d); } catch { }

        m.Restored = bad == 0;
        try { File.WriteAllText(manifestPath, JsonSerializer.Serialize(m, Json)); } catch { }
        return (ok, bad, log);
    }

    private static string TryHash(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return ""; }
    }

    private static string Sanitize(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Trim().Length == 0 ? "project" : name.Trim();
    }
}
