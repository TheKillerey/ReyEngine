using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;

namespace ReyEngine.Core.Cleanup;

public sealed class CleanupScanOptions
{
    public required string ProjectRoot { get; init; }
    /// <summary>The editable project folders, as (display name, absolute root).</summary>
    public required IReadOnlyList<(string Name, string Root)> Folders { get; init; }
    public required IReferenceIndex References { get; init; }

    public bool ScanUnused { get; init; } = true;
    public bool ScanRiotIdentical { get; init; } = true;
    public bool IncludeEmptyFolders { get; init; }

    /// <summary>Every path hash any installed game WAD ships. A project file whose hash is in here is an
    /// OVERRIDE of real game content - the game can always request it, so it is never unused.</summary>
    public IReadOnlySet<ulong> GameWadHashes { get; init; } = new HashSet<ulong>();

    /// <summary>Read the Riot original for a hash, or null. Null delegate = no Riot reference available,
    /// which disables only the identical-to-Riot mode.</summary>
    public Func<ulong, byte[]?>? ReadRiot { get; init; }

    /// <summary>How many PROJECT WADs also carry this hash. Non-zero means deleting the loose file does
    /// not fall through to Riot - it exposes the packed copy instead, which may differ.</summary>
    public Func<ulong, int>? ProjectWadCopies { get; init; }

    /// <summary>Format-aware equality for containers whose bytes differ but whose content does not.
    /// Returns null when no comparer exists for the extension, and the caller falls back to raw bytes.</summary>
    public Func<string, byte[], byte[], bool?>? ContentEquivalent { get; init; }

    /// <summary>Files the project itself depends on (metadata, export config, recolour snapshots).</summary>
    public IReadOnlySet<string> ProtectedRelPaths { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<ulong> ProtectedHashes { get; init; } = new HashSet<ulong>();

    /// <summary>False when some project bins could not be parsed, so the reference set has known holes.
    /// A hole means references exist that were never seen, which is precisely the condition under which
    /// "nothing references it" stops being a safe thing to say.</summary>
    public bool ReferencesComplete { get; init; } = true;
    public string ReferenceGapReason { get; init; } = "";
}

/// <summary>
/// M302: the read-only half of Cleanup Project. Produces a complete preview and touches nothing.
///
/// <para>Every rule here is written to fail SAFE: a file lands in <see cref="CleanupGroup.Unused"/> only
/// when a positive case for deleting it can be made, and anything the scan cannot reason about lands in
/// <see cref="CleanupGroup.Protected"/> unticked. The cost of a false "unused" is a broken mod the user
/// may not notice for weeks; the cost of a false "protected" is a few unreclaimed megabytes.</para>
/// </summary>
public static class CleanupScanner
{
    public static CleanupReport Scan(CleanupScanOptions o,
        IProgress<(double Frac, string Stage)>? progress = null, CancellationToken ct = default)
    {
        var candidates = new List<CleanupCandidate>();
        var notes = new List<string>();
        int scanned = 0; long scannedBytes = 0;

        bool riotAvailable = o.ReadRiot is not null;
        string riotStatus = riotAvailable
            ? "Riot reference mounted - identical-file detection available."
            : "No Riot reference available - only unused-file scanning is possible.";
        if (!riotAvailable && o.ScanRiotIdentical)
            notes.Add("Identical-to-Riot scanning was requested but no Riot reference is mounted; that mode was skipped.");

        // "Unused" rests on two conditions: no game WAD ships the path, AND nothing in the project points
        // at it. If either cannot actually be evaluated, the verdict is not provable - so it is reported
        // as UNCERTAIN instead of quietly asserted. Without this the scan happily called shipped Riot
        // textures unused whenever the game folder failed to resolve.
        bool canProveUnused = o.GameWadHashes.Count > 0 && o.ReferencesComplete;
        if (o.ScanUnused && o.GameWadHashes.Count == 0)
            notes.Add("No game WAD index available (is the game folder set?) - it cannot be shown that the "
                    + "game never ships these paths, so unused results are listed as uncertain and left unticked.");
        if (o.ScanUnused && !o.ReferencesComplete)
            notes.Add("Some project bins could not be read" + (o.ReferenceGapReason.Length > 0 ? $" ({o.ReferenceGapReason})" : "")
                    + " - references they hold are invisible to this scan, so unused results are listed as uncertain.");

        int folderNo = 0;
        foreach (var (name, root) in o.Folders)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) { notes.Add($"{name}: folder missing on disk, skipped."); continue; }
            progress?.Report((0.9 * folderNo++ / Math.Max(1, o.Folders.Count), $"Scanning {name}…"));

            foreach (var (hash, abs) in WadPackService.EnumerateChunkFiles(root))
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(root, abs).Replace('\\', '/');
                long bytes;
                try { bytes = new FileInfo(abs).Length; } catch { continue; }
                scanned++; scannedBytes += bytes;

                var type = AssetTypeDetector.FromPath(rel);
                var c = Judge(o, name, rel, abs, hash, bytes, type, riotAvailable, canProveUnused);
                if (c is not null) candidates.Add(c);
            }
        }

        if (o.IncludeEmptyFolders)
        {
            progress?.Report((0.93, "Looking for empty folders…"));
            foreach (var (name, root) in o.Folders)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in EmptyDirectories(root))
                    candidates.Add(new CleanupCandidate(CleanupGroup.EmptyFolder,
                        Path.GetRelativePath(root, dir).Replace('\\', '/'), dir, name,
                        AssetType.Unknown, 0, "Contains no files at any depth"));
            }
        }

        progress?.Report((1.0, "Scan complete."));
        candidates.Sort((a, b) => a.Group != b.Group
            ? a.Group.CompareTo(b.Group)
            : b.Bytes.CompareTo(a.Bytes));
        return new CleanupReport(o.ProjectRoot, riotAvailable, riotStatus, candidates, scanned, scannedBytes, notes);
    }

    private static CleanupCandidate? Judge(CleanupScanOptions o, string folder, string rel, string abs,
        ulong hash, long bytes, AssetType type, bool riotAvailable, bool canProveUnused)
    {
        CleanupCandidate Row(CleanupGroup g, string why) =>
            new(g, rel, abs, folder, type, bytes, why);

        // ---- things that are never up for deletion ----
        if (rel.StartsWith(".reyengine/", StringComparison.OrdinalIgnoreCase))
            return null;                                   // editor metadata, not project content
        if (o.ProtectedRelPaths.Contains(rel) || o.ProtectedHashes.Contains(hash))
            return Row(CleanupGroup.Protected, "Referenced by project metadata or export settings");

        // A loose <hash>.ext chunk has no path, so the path-shaped fallbacks below cannot see it - but the
        // two conditions that actually decide "unused" never needed one. The file NAME is the chunk's WAD
        // path hash, so "no game WAD ships it" and "no project bin references it" are both answerable
        // directly, and the engine can only load a chunk by that same hash.
        bool loose = !rel.Contains('/');

        // ---- identical to the Riot original ----
        if (o.ScanRiotIdentical && riotAvailable)
        {
            byte[]? riot = null;
            try { riot = o.ReadRiot!(hash); } catch { /* unreadable original = no verdict */ }
            if (riot is not null)
            {
                int packed = 0;
                try { packed = o.ProjectWadCopies?.Invoke(hash) ?? 0; } catch { }
                if (packed > 0)
                    return Row(CleanupGroup.Protected,
                        "A project WAD also provides this path - deleting this file would expose that copy, not Riot's");

                byte[] mine;
                try { mine = File.ReadAllBytes(abs); } catch { return null; }
                var (same, how) = Equivalent(o, rel, mine, riot);
                if (same)
                    return Row(CleanupGroup.IdenticalToRiot, $"{how} the Riot original - removing it falls back to Riot");
            }
        }

        // ---- nothing can ever load it ----
        if (o.ScanUnused)
        {
            // The game ships this exact path, so it is an override of real content and is always loadable.
            if (o.GameWadHashes.Contains(hash)) return null;
            if (o.References.IsReferenced(rel, hash, out _)) return null;
            if (!canProveUnused)
                return Row(CleanupGroup.Protected,
                    "Nothing references it, but the scan could not confirm the game never ships it - unverified");
            return loose
                ? Row(CleanupGroup.Unused,
                    "No game WAD ships this hash and no project content references it (judged by hash - the file has no path)")
                : Row(CleanupGroup.Unused, "No game WAD ships this path and no project content references it");
        }
        return null;
    }

    /// <summary>Content equality, format-aware where a comparer exists and exact bytes otherwise. The
    /// fallback is deliberately the strict one: an unknown container that merely looks similar is not
    /// grounds for deleting the user's copy.</summary>
    private static (bool Same, string How) Equivalent(CleanupScanOptions o, string rel, byte[] a, byte[] b)
    {
        if (a.AsSpan().SequenceEqual(b)) return (true, "Byte-identical to");

        string ext = Path.GetExtension(rel).TrimStart('.').ToLowerInvariant();
        if (o.ContentEquivalent is not null)
        {
            bool? verdict = null;
            try { verdict = o.ContentEquivalent(ext, a, b); } catch { }
            if (verdict == true) return (true, "Content-equivalent to");
        }
        return (false, "");
    }

    /// <summary>Directories with no file at any depth. Deepest first, so removing them in order leaves no
    /// parent stranded.</summary>
    private static IEnumerable<string> EmptyDirectories(string root)
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories); }
        catch { yield break; }

        var found = new List<string>();
        foreach (var d in dirs)
        {
            var rel = Path.GetRelativePath(root, d).Replace('\\', '/');
            if (rel.StartsWith(".reyengine", StringComparison.OrdinalIgnoreCase)) continue;
            bool any;
            try { any = Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any(); }
            catch { continue; }
            if (!any) found.Add(d);
        }
        found.Sort((x, y) => y.Length.CompareTo(x.Length));
        foreach (var d in found) yield return d;
    }
}
