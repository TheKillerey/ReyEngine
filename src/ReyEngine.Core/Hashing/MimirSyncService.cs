using System.Security.Cryptography;
using System.Text.Json;

namespace ReyEngine.Core.Hashing;

/// <summary>What a sync did.</summary>
public sealed record MimirSyncResult(string ReleaseTag, int Downloaded, int Reused, long Bytes, bool UpToDate)
{
    public string Summary => UpToDate
        ? $"Already on {ReleaseTag} — nothing to download."
        : $"{ReleaseTag}: {Downloaded} table(s) downloaded, {Reused} reused, {Bytes / 1024.0 / 1024.0:0.0} MB.";
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
/// <para>Tables already present with the right hash are reused, so re-running after a partial sync only
/// fetches what is missing rather than the whole ~64 MB again.</para>
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

        int downloaded = 0, reused = 0;
        long bytes = 0;
        foreach (var (name, table, kind) in manifest.UsableTables())
        {
            ct.ThrowIfCancellationRequested();
            string target = Path.Combine(ReyPaths.MimirDir, table.File);

            // Reuse anything already correct: a re-run after a failed sync should fetch only the gap.
            if (!force && File.Exists(target) && HashMatches(target, table.Sha256))
            {
                log($"  {name}: already current ({table.Entries:n0} entries)");
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

        File.WriteAllText(ReyPaths.MimirManifestFile, manifest.ToJson());
        var result = new MimirSyncResult(tag, downloaded, reused, bytes, UpToDate: false);
        log(result.Summary);
        return result;
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
        manifest.UsableTables().All(t => File.Exists(Path.Combine(ReyPaths.MimirDir, t.Table.File)));

    /// <summary>Open the cached tables. Returns an empty set when nothing is synced, which is a legitimate
    /// state: the CommunityDragon path still works and Mimir is an optional replacement for it.</summary>
    public static IReadOnlyList<(string Name, MimirTableKind Kind, HashDbFile Db)> OpenLocal(Action<string>? log = null)
    {
        var opened = new List<(string, MimirTableKind, HashDbFile)>();
        var manifest = MimirManifest.Load(ReyPaths.MimirManifestFile);
        if (manifest is null) return opened;

        foreach (var (name, table, kind) in manifest.UsableTables())
        {
            string path = Path.Combine(ReyPaths.MimirDir, table.File);
            if (!File.Exists(path)) continue;
            try { opened.Add((name, kind, HashDbFile.Open(path))); }
            catch (Exception ex) { log?.Invoke($"{name}: {ex.Message}"); }
        }
        return opened;
    }
}
