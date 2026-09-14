namespace ReyEngine.Core.Meta;

/// <summary>
/// <para>M367: downloads the LeagueToolkit meta-class database and caches it locally, mirroring
/// <see cref="ReyEngine.Core.Hashing.HashSyncService"/> deliberately - same fetch-on-demand shape, same
/// "after the first sync the app never needs the network again" behaviour.</para>
///
/// <para><b>Fetched, never vendored,</b> for the same two reasons the CommunityDragon hashes are
/// (<c>/data/hashes/communitydragon/</c> is gitignored): <c>lol-meta-classes</c> publishes NO licence, so
/// this repo should not redistribute a copy of it; and it is re-dumped every patch, so a committed snapshot
/// would be stale almost immediately. Downloading it puts the copy on the user's machine, under their
/// control, exactly like the hash lists.</para>
///
/// <para>M731: the ETag the copy on disk was downloaded with is kept beside it, so
/// <see cref="SyncIfChangedAsync"/> can ask GitHub with <c>If-None-Match</c> and learn "unchanged" from a
/// 304 instead of fetching 3.6 MB to compare.</para>
/// </summary>
public sealed class MetaClassSyncService
{
    /// <summary>Raw file on the default branch. The repo has no releases and no API for this, so the raw
    /// URL is the documented way to consume it.</summary>
    private const string MetaDbUrl =
        "https://raw.githubusercontent.com/LeagueToolkit/lol-meta-classes/main/db/meta.db.json";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ReyEngine/0.4 (+meta-class-sync)");
        return c;
    }

    /// <summary>M731: the ETag of the cached database, beside it.</summary>
    public static string EtagFile => ReyPaths.MetaDbFile + ".etag";

    /// <summary>Download the database, cache it, and return it parsed. Downloads to a temporary file and
    /// moves it into place only on success, so an interrupted sync cannot leave a truncated cache that then
    /// fails to parse on every subsequent launch.</summary>
    public async Task<MetaClassDatabase> SyncAsync(Action<string> log, int? build = null,
        CancellationToken ct = default)
    {
        await DownloadAsync(log, ifNoneMatch: null, ct);
        log("Parsing meta classes…");
        var db = MetaClassDatabase.Load(ReyPaths.MetaDbFile, build, log);
        log("Meta-class sync complete.");
        return db;
    }

    /// <summary>M731: fetch only when GitHub reports different content than the copy on disk was downloaded
    /// with. Null when the copy is current. A copy from before M731 has no ETag on record, so its first
    /// automatic check downloads once and records one.</summary>
    public async Task<MetaClassDatabase?> SyncIfChangedAsync(Action<string> log, int? build = null,
        CancellationToken ct = default)
    {
        if (!await DownloadAsync(log, ReadEtag(EtagFile), ct)) return null;
        log("Parsing meta classes…");
        var db = MetaClassDatabase.Load(ReyPaths.MetaDbFile, build, log);
        log("Meta-class sync complete.");
        return db;
    }

    /// <summary>False when the server answered 304 Not Modified to <paramref name="ifNoneMatch"/>.</summary>
    private static async Task<bool> DownloadAsync(Action<string> log, string? ifNoneMatch, CancellationToken ct)
    {
        ReyPaths.EnsureMetaDir();
        log(ifNoneMatch is null ? "Downloading LeagueToolkit meta-class database…" : "Checking the LeagueToolkit meta-class database…");

        string target = ReyPaths.MetaDbFile;
        string temp = target + ".part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, MetaDbUrl);
            if (ifNoneMatch is { Length: > 0 }) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotModified) return false;
                response.EnsureSuccessStatusCode();
                long? size = response.Content.Headers.ContentLength;
                if (size is { } s) log($"meta.db.json — {s / 1024.0 / 1024.0:0.0} MB");
                await using (var src = await response.Content.ReadAsStreamAsync(ct))
                await using (var dst = File.Create(temp))
                    await src.CopyToAsync(dst, ct);
                File.Move(temp, target, overwrite: true);
                WriteEtag(EtagFile, response.Headers.ETag?.Tag);
            }
            return true;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    public static string? ReadEtag(string path)
    {
        try { return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } tag ? tag : null; }
        catch { return null; }
    }

    /// <summary>Record the tag, or forget it when the server sent none - a stale tag would make the next
    /// check believe an unrelated copy is current.</summary>
    public static void WriteEtag(string path, string? etag)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(etag)) { if (File.Exists(path)) File.Delete(path); }
            else File.WriteAllText(path, etag.Trim());
        }
        catch { /* best effort: without it the next check downloads once more */ }
    }

    /// <summary>Load whatever is cached - no network. Empty when nothing has been synced yet, which is a
    /// legitimate state: every consumer treats the meta database as an OPTIONAL enrichment.</summary>
    public MetaClassDatabase LoadLocal(Action<string> log, int? build = null)
        => MetaClassDatabase.Load(ReyPaths.MetaDbFile, build, log);

    /// <summary>Has anything been downloaded yet?</summary>
    public static bool HasLocalCopy => File.Exists(ReyPaths.MetaDbFile);
}
