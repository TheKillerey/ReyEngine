using System.Security.Cryptography;
using System.Text.Json;

namespace ReyEngine.Core.Hashing;

/// <summary>What a sync did.</summary>
public sealed record MimirSyncResult(string ReleaseTag, int Downloaded, int Reused, long Bytes, bool UpToDate)
{
    public string Summary => UpToDate
        ? $"Already on {ReleaseTag} — nothing to download."
        : $"{ReleaseTag}: {Downloaded} table(s) downloaded, {Reused} reused, {Bytes / 1024.0 / 1024.0:0.0} MB.";

    /// <summary>M731: files of older releases removed after the sync. Best effort - a table the running
    /// database still maps stays until a later sync, when nothing maps it any more.</summary>
    public int Removed { get; init; }
}

/// <summary>
/// M495: fetches Mimir's published hash tables and keeps them current.
///
/// <para>Mirrors <see cref="ReyEngine.Core.Meta.MetaClassSyncService"/> deliberately — same fetch-on-demand
/// shape, same "after the first sync the app never needs the network again" behaviour, same reason for not
/// vendoring the data: it is third party and re-generated per patch, so a committed copy would be stale
/// almost immediately and would not be ours to redistribute.</para>
///
/// <para><b>Every table is verified against the manifest's SHA-256 before it is kept.</b> These files are
/// memory-mapped and then believed — a truncated download would not fail loudly, it would silently serve
/// wrong names or throw deep inside a lookup. Downloads land in a <c>.part</c> file and are moved into
/// place only after the hash matches, so an interrupted sync cannot leave a corrupt table behind.</para>
///
/// <para><b>M731: each release's tables live in their own folder, <c>mimir/&lt;tag&gt;/</c>.</b> A table the
/// running database has memory-mapped is never replaced: <c>MemoryMappedFile.CreateFromFile</c> opens it
/// with <c>FileShare.Read</c>, so moving a new file over it fails on Windows, and disposing the map under a
/// lookup on another thread is the torn-read race M508 already paid for once. A new release is written beside
/// the old one, the manifest is switched to it last, and the old folder is removed by a later sync once
/// nothing maps it. Tables already present with the right hash - in the new folder, the previous release's
/// folder, or the flat layout releases before M731 used - are copied or reused rather than fetched again.</para>
/// </summary>
public sealed class MimirSyncService
{
    private const string ReleasesApi = "https://api.github.com/repos/LeagueToolkit/Mimir/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        // GitHub's API rejects requests without a User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ReyEngine/0.4 (+mimir-hash-sync)");
        return c;
    }

    /// <summary>The release currently cached on disk, or null when nothing has been synced.</summary>
    public MimirManifest? Local => MimirManifest.Load(ReyPaths.MimirManifestFile);

    /// <summary>Ask GitHub what the newest release is. Returns the tag and the asset download URLs.</summary>
    public async Task<(string Tag, Dictionary<string, string> Assets)> LatestAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(ReleasesApi, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        string tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("assets", out var list))
            foreach (var asset in list.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (name.Length > 0 && url.Length > 0) assets[name] = url;
            }
        return (tag, assets);
    }

    // ---- M731: where a release's tables live ------------------------------------------------------

    /// <summary>A release tag as a folder name: <c>hashes-2026-09-14</c> is already one; anything a file
    /// system refuses becomes an underscore.</summary>
    public static string SafeTag(string tag)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(tag.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length == 0 ? "release" : safe;
    }

    public static string TableDir(string root, string tag) => Path.Combine(root, SafeTag(tag));

    /// <summary>The file a table is read from: the release's folder when it holds one, else the flat path
    /// releases before M731 were written to (existing installs), else where the release's folder would put it.</summary>
    public static string ResolveTablePath(string root, string? tag, string file)
    {
        string? versioned = string.IsNullOrEmpty(tag) ? null : Path.Combine(TableDir(root, tag), file);
        if (versioned is not null && File.Exists(versioned)) return versioned;
        string flat = Path.Combine(root, file);
        if (File.Exists(flat)) return flat;
        return versioned ?? flat;
    }

    /// <summary>Download the latest release's tables into the local cache, verifying each one.</summary>
    public async Task<MimirSyncResult> SyncAsync(Action<string> log, bool force = false,
        CancellationToken ct = default)
    {
        ReyPaths.EnsureMimirDir();
        log("Checking the Mimir hash-table release…");
        var (tag, assets) = await LatestAsync(ct);
        if (tag.Length == 0) throw new InvalidOperationException("The Mimir release has no tag.");

        var current = Local;
        if (!force && current is not null && current.ReleaseTag == tag && TablesPresent(current))
        {
            log($"Already on {tag}.");
            return new MimirSyncResult(tag, 0, current.Tables.Count, 0, UpToDate: true);
        }

        if (!assets.TryGetValue("manifest.json", out var manifestUrl))
            throw new InvalidOperationException($"Release {tag} publishes no manifest.json.");

        log($"Fetching manifest for {tag}…");
        string manifestJson = await Http.GetStringAsync(manifestUrl, ct);
        var manifest = MimirManifest.Parse(manifestJson)
            ?? throw new InvalidOperationException("The Mimir manifest could not be parsed.");
        manifest.ReleaseTag = tag;

        string root = ReyPaths.MimirDir;
        string tableDir = TableDir(root, tag);
        Directory.CreateDirectory(tableDir);

        int downloaded = 0, reused = 0;
        long bytes = 0;
        foreach (var (name, table, kind) in manifest.UsableTables())
        {
            ct.ThrowIfCancellationRequested();
            string target = Path.Combine(tableDir, table.File);

            // Reuse anything already correct: a re-run after a failed sync should fetch only the gap.
            if (!force && File.Exists(target) && HashMatches(target, table.Sha256))
            {
                log($"  {name}: already current ({table.Entries:n0} entries)");
                reused++;
                continue;
            }
            // A table the previous release shipped with this exact content is copied, not fetched again -
            // most tables do not change between two daily releases.
            if (!force && ReusableCopy(root, current, table, target) is { } previous)
            {
                File.Copy(previous, target, overwrite: true);
                log($"  {name}: unchanged since {current?.ReleaseTag ?? "the last sync"} ({table.Entries:n0} entries)");
                reused++;
                continue;
            }
            if (!assets.TryGetValue(table.File, out var url))
            { log($"  !! {name}: {table.File} is not published in {tag} — skipped"); continue; }

            log($"  {name}: downloading {table.File} ({table.Entries:n0} entries, {kind})…");
            long size = await DownloadVerifiedAsync(url, target, table.Sha256, ct);
            bytes += size;
            downloaded++;
        }

        // The manifest is switched LAST, so a sync that dies above leaves the previous release in use.
        File.WriteAllText(ReyPaths.MimirManifestFile, manifest.ToJson());
        int removed = RemoveOtherReleases(root, tag, manifest);
        var result = new MimirSyncResult(tag, downloaded, reused, bytes, UpToDate: false) { Removed = removed };
        log(result.Summary);
        if (removed > 0) log($"  {removed} file(s) of older releases removed.");
        return result;
    }

    /// <summary>
    /// A file elsewhere in the cache whose content IS this table, if there is one: the same file name in the
    /// previous release's folder or the flat layout, or - since Mimir names every table by release date
    /// (<c>game-2026-09-14.lhdb</c>) - any table of the previous release with the same SHA-256 under its own
    /// name. Between two weekly releases most tables do not change at all.
    /// </summary>
    public static string? ReusableCopy(string root, MimirManifest? previous, MimirTable table, string target)
    {
        var candidates = new List<string>();
        if (previous is not null)
        {
            candidates.Add(ResolveTablePath(root, previous.ReleaseTag, table.File));
            if (table.Sha256.Length > 0)
                foreach (var (_, previousTable, _) in previous.UsableTables())
                    if (string.Equals(previousTable.Sha256.Replace("-", ""), table.Sha256.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                        candidates.Add(ResolveTablePath(root, previous.ReleaseTag, previousTable.File));
        }
        candidates.Add(Path.Combine(root, table.File));
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate) || PathsEqual(candidate, target)) continue;
            if (HashMatches(candidate, table.Sha256)) return candidate;
        }
        return null;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Delete other releases' folders, and every flat table the current manifest does not name - the layout
    /// releases before M731 used, where nothing ever deleted anything: five dated releases, ~300 MB, were
    /// found beside each other. Best effort: a file the running database still maps refuses to go and is
    /// left for a later sync. Only <c>.lhdb</c> tables and stray <c>.part</c> downloads are touched.
    /// </summary>
    public static int RemoveOtherReleases(string root, string keepTag, MimirManifest current)
    {
        int removed = 0;
        string keep = SafeTag(keepTag);
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, recursive: true); removed++; }
            catch { /* still mapped - next time */ }
        }
        var currentFiles = current.UsableTables().Select(t => t.Table.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(root))
        {
            string name = Path.GetFileName(path);
            bool table = name.EndsWith(".lhdb", StringComparison.OrdinalIgnoreCase);
            bool stray = name.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
            if (!table && !stray) continue;
            if (table && currentFiles.Contains(name)) continue;
            try { File.Delete(path); removed++; }
            catch { /* still mapped - next time */ }
        }
        return removed;
    }

    /// <summary>Fetch to a .part file, hash it, and only then move it into place. A table that fails
    /// verification is deleted rather than kept — it would otherwise be mapped and believed.</summary>
    private static async Task<long> DownloadVerifiedAsync(string url, string target, string expectedSha256,
        CancellationToken ct)
    {
        string temp = target + ".part";
        try
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(temp);
                await src.CopyToAsync(dst, ct);
            }

            if (expectedSha256.Length > 0 && !HashMatches(temp, expectedSha256))
                throw new InvalidDataException(
                    $"{Path.GetFileName(target)} failed its SHA-256 check — the download was incomplete or altered.");

            long size = new FileInfo(temp).Length;
            File.Move(temp, target, overwrite: true);
            return size;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    private static bool HashMatches(string path, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return true;
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream))
                .Equals(expectedHex.Replace("-", ""), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool TablesPresent(MimirManifest manifest) =>
        manifest.UsableTables().All(t => File.Exists(ResolveTablePath(ReyPaths.MimirDir, manifest.ReleaseTag, t.Table.File)));

    /// <summary>Open the cached tables. Returns an empty set when nothing is synced, which is a legitimate
    /// state: the CommunityDragon path still works and Mimir is an optional replacement for it.</summary>
    public static IReadOnlyList<(string Name, MimirTableKind Kind, HashDbFile Db)> OpenLocal(Action<string>? log = null)
    {
        var opened = new List<(string, MimirTableKind, HashDbFile)>();
        var manifest = MimirManifest.Load(ReyPaths.MimirManifestFile);
        if (manifest is null) return opened;

        foreach (var (name, table, kind) in manifest.UsableTables())
        {
            string path = ResolveTablePath(ReyPaths.MimirDir, manifest.ReleaseTag, table.File);
            if (!File.Exists(path)) continue;
            try { opened.Add((name, kind, HashDbFile.Open(path))); }
            catch (Exception ex) { log?.Invoke($"{name}: {ex.Message}"); }
        }
        return opened;
    }
}
