using System.Text.Json;

namespace ReyEngine.Core.Hashing;

/// <summary>
/// Downloads the CommunityDragon hash lists (game split files, lcu, bin*) and merges
/// them into a <see cref="HashDatabase"/> + local binary cache. After the first sync the
/// app loads from cache and never needs the network again.
///
/// <para>M731: the listing GitHub returns carries each file's blob sha, and the sha every file was last
/// synced at is kept in <see cref="ManifestFile"/>. A sync fetches only the lists that changed, and an
/// automatic one can say "nothing changed" after a single request instead of ~100 MB of downloads.</para>
/// </summary>
public sealed class HashSyncService
{
    private const string ContentsApi = "https://api.github.com/repos/CommunityDragon/Data/contents/hashes/lol";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ReyEngine/0.1 (+hash-sync)");
        return c;
    }

    /// <summary>One file as GitHub lists it. <paramref name="Sha"/> is the git blob sha - it changes exactly
    /// when the content does, which is what makes it a cheap "is my copy current" check.</summary>
    public sealed record RemoteFile(string Name, string Url, long Size, string Sha);

    /// <summary>M731: name → blob sha of every file as last synced.</summary>
    public static string ManifestFile => Path.Combine(ReyPaths.HashesDir, "communitydragon.manifest.json");

    /// <summary>Does this install use the CommunityDragon lists at all?</summary>
    public static bool HasLocalRaw =>
        Directory.Exists(ReyPaths.CommunityDragonDir) && Directory.EnumerateFiles(ReyPaths.CommunityDragonDir, "hashes.*").Any();

    /// <summary>The files worth downloading: missing locally, never synced, or synced at another sha.</summary>
    public static IReadOnlyList<RemoteFile> ChangedFiles(IEnumerable<RemoteFile> listing,
        IReadOnlyDictionary<string, string> synced, Func<string, bool> existsLocally) =>
        listing.Where(f => !existsLocally(f.Name)
                           || !synced.TryGetValue(f.Name, out var sha)
                           || !string.Equals(sha, f.Sha, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Download everything that changed, parse, cache. Returns a fresh populated database - or null
    /// when <paramref name="onlyIfChanged"/> is set and no file moved since the last sync.</summary>
    public async Task<HashDatabase?> SyncAsync(Action<string> log, CancellationToken ct = default, bool onlyIfChanged = false)
    {
        ReyPaths.EnsureHashDirs();
        log("Downloading CommunityDragon hash file list…");
        var files = await GetFileListAsync(ct);
        log($"Found {files.Count} hash files on CommunityDragon/Data.");

        var synced = LoadManifest(ManifestFile);
        var changed = ChangedFiles(files, synced, name => File.Exists(Path.Combine(ReyPaths.CommunityDragonDir, name)));
        if (onlyIfChanged && changed.Count == 0 && File.Exists(ReyPaths.MergedCache))
        {
            log("Every CommunityDragon hash file is current.");
            return null;
        }
        if (changed.Count < files.Count)
            log($"{files.Count - changed.Count} file(s) unchanged since the last sync - kept.");

        long total = 0;
        for (int i = 0; i < changed.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = changed[i];
            log($"Downloading [{i + 1}/{changed.Count}] {file.Name}  ({file.Size / 1024.0 / 1024.0:0.0} MB)…");
            await DownloadAsync(file.Url, Path.Combine(ReyPaths.CommunityDragonDir, file.Name), ct);
            total += file.Size;
        }
        log($"Downloaded {total / 1024.0 / 1024.0:0.0} MB. Parsing…");

        var db = ParseLocalRaw(log);
        db.LoadManualDirectory(ReyPaths.HashesDir);

        log($"Loaded {db.WadCount:n0} WAD + {db.BinCount:n0} bin entries ({db.ConflictCount:n0} conflicts).");
        log("Saving merged cache…");
        db.SaveCache(ReyPaths.MergedCache);
        SaveManifest(ManifestFile, files);
        log("Hash sync complete.");
        return db;
    }

    public static Dictionary<string, string> LoadManifest(string path)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } map)
                return new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch { /* a manifest that will not read means "sync everything", which is safe */ }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static void SaveManifest(string path, IEnumerable<RemoteFile> files)
    {
        try
        {
            var map = files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Sha, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* without it the next sync downloads everything again - the pre-M731 behaviour */ }
    }

    /// <summary>Load from local cache (or raw files) — no network. Returns null counts if nothing local.</summary>
    public HashDatabase LoadLocal(Action<string> log)
    {
        var db = new HashDatabase();

        // M495: Mimir's memory-mapped tables come first, because when they are present the expensive
        // CommunityDragon path has nothing left to do. They are ~64 MB mapped rather than ~554 MB parsed
        // into dictionaries, and mapping them costs nothing until a lookup actually happens.
        var tables = MimirSyncService.OpenLocal(m => log($"  mimir: {m}"));
        foreach (var (name, kind, table) in tables)
        {
            db.AttachTable(kind, table);
            log($"  {name}: {table.EntryCount:n0} entries ({kind}, memory-mapped)");
        }
        if (tables.Count > 0)
            log($"Mimir hash tables: {tables.Count} table(s), {db.TableEntryCount:n0} entries.");

        // The legacy path stays as a FALLBACK rather than being deleted. Existing installs already have the
        // merged cache, and a user who never syncs Mimir must keep working exactly as before. Loading both
        // is also harmless: the dictionaries win ties by design, so anything locally known still shadows a
        // published table.
        if (tables.Count == 0)
        {
            if (db.LoadCache(ReyPaths.MergedCache))
            {
                log($"Loaded hash cache: {db.WadCount:n0} WAD + {db.BinCount:n0} bin entries.");
            }
            else if (Directory.Exists(ReyPaths.CommunityDragonDir) &&
                     Directory.EnumerateFiles(ReyPaths.CommunityDragonDir).Any())
            {
                var parsed = ParseLocalRaw(log);
                if (parsed.WadCount + parsed.BinCount > 0) parsed.SaveCache(ReyPaths.MergedCache);
                foreach (var (_, kind, table) in tables) parsed.AttachTable(kind, table);
                db = parsed;
            }
        }

        db.LoadManualDirectory(ReyPaths.HashesDir);
        return db;
    }

    private static HashDatabase ParseLocalRaw(Action<string> log)
    {
        var db = new HashDatabase();
        if (!Directory.Exists(ReyPaths.CommunityDragonDir)) return db;
        foreach (var f in Directory.EnumerateFiles(ReyPaths.CommunityDragonDir, "hashes.*"))
        {
            var (n, isBin) = db.LoadTextFile(f);
            if (n > 0) log($"  parsed {Path.GetFileName(f)} → {n:n0} {(isBin ? "bin" : "wad")} entries");
        }
        return db;
    }

    private static async Task<List<RemoteFile>> GetFileListAsync(CancellationToken ct)
    {
        var json = await Http.GetStringAsync(ContentsApi, ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<RemoteFile>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = el.GetProperty("name").GetString();
            if (name is null || !name.StartsWith("hashes.", StringComparison.Ordinal)) continue;
            var url = el.GetProperty("download_url").GetString();
            if (url is null) continue;
            string sha = el.TryGetProperty("sha", out var s) ? s.GetString() ?? "" : "";
            list.Add(new RemoteFile(name, url, el.GetProperty("size").GetInt64(), sha));
        }
        return list;
    }

    private static async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(dest);
        await resp.Content.CopyToAsync(fs, ct);
    }
}
