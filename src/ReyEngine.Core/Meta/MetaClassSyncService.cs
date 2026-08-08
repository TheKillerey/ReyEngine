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

    /// <summary>Download the database, cache it, and return it parsed. Downloads to a temporary file and
    /// moves it into place only on success, so an interrupted sync cannot leave a truncated cache that then
    /// fails to parse on every subsequent launch.</summary>
    public async Task<MetaClassDatabase> SyncAsync(Action<string> log, int? build = null,
        CancellationToken ct = default)
    {
        ReyPaths.EnsureMetaDir();
        log("Downloading LeagueToolkit meta-class database…");

        string target = ReyPaths.MetaDbFile;
        string temp = target + ".part";
        try
        {
            using (var response = await Http.GetAsync(MetaDbUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long? size = response.Content.Headers.ContentLength;
                if (size is { } s) log($"meta.db.json — {s / 1024.0 / 1024.0:0.0} MB");
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(temp);
                await src.CopyToAsync(dst, ct);
            }
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }

        log("Parsing meta classes…");
        var db = MetaClassDatabase.Load(target, build, log);
        log("Meta-class sync complete.");
        return db;
    }

    /// <summary>Load whatever is cached - no network. Empty when nothing has been synced yet, which is a
    /// legitimate state: every consumer treats the meta database as an OPTIONAL enrichment.</summary>
    public MetaClassDatabase LoadLocal(Action<string> log, int? build = null)
        => MetaClassDatabase.Load(ReyPaths.MetaDbFile, build, log);

    /// <summary>Has anything been downloaded yet?</summary>
    public static bool HasLocalCopy => File.Exists(ReyPaths.MetaDbFile);
}
