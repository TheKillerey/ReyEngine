using System.Text.Json;

namespace ReyEngine.Core.Hashing;

/// <summary>
/// Downloads the CommunityDragon hash lists (game split files, lcu, bin*) and merges
/// them into a <see cref="HashDatabase"/> + local binary cache. After the first sync the
/// app loads from cache and never needs the network again.
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

    /// <summary>Download everything, parse, cache. Returns a fresh populated database.</summary>
    public async Task<HashDatabase> SyncAsync(Action<string> log, CancellationToken ct = default)
    {
        ReyPaths.EnsureHashDirs();
        log("Downloading CommunityDragon hash file list…");
        var files = await GetFileListAsync(ct);
        log($"Found {files.Count} hash files on CommunityDragon/Data.");

        long total = 0;
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (name, url, size) = files[i];
            log($"Downloading [{i + 1}/{files.Count}] {name}  ({size / 1024.0 / 1024.0:0.0} MB)…");
            await DownloadAsync(url, Path.Combine(ReyPaths.CommunityDragonDir, name), ct);
            total += size;
        }
        log($"Downloaded {total / 1024.0 / 1024.0:0.0} MB. Parsing…");

        var db = ParseLocalRaw(log);
        db.LoadManualDirectory(ReyPaths.HashesDir);

        log($"Loaded {db.WadCount:n0} WAD + {db.BinCount:n0} bin entries ({db.ConflictCount:n0} conflicts).");
        log("Saving merged cache…");
        db.SaveCache(ReyPaths.MergedCache);
        log("Hash sync complete.");
        return db;
    }

    /// <summary>Load from local cache (or raw files) — no network. Returns null counts if nothing local.</summary>
    public HashDatabase LoadLocal(Action<string> log)
    {
        var db = new HashDatabase();
        if (db.LoadCache(ReyPaths.MergedCache))
        {
            log($"Loaded hash cache: {db.WadCount:n0} WAD + {db.BinCount:n0} bin entries.");
        }
        else if (Directory.Exists(ReyPaths.CommunityDragonDir) &&
                 Directory.EnumerateFiles(ReyPaths.CommunityDragonDir).Any())
        {
            db = ParseLocalRaw(log);
            if (db.WadCount + db.BinCount > 0) db.SaveCache(ReyPaths.MergedCache);
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

    private static async Task<List<(string name, string url, long size)>> GetFileListAsync(CancellationToken ct)
    {
        var json = await Http.GetStringAsync(ContentsApi, ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<(string, string, long)>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = el.GetProperty("name").GetString();
            if (name is null || !name.StartsWith("hashes.", StringComparison.Ordinal)) continue;
            var url = el.GetProperty("download_url").GetString();
            if (url is null) continue;
            list.Add((name, url, el.GetProperty("size").GetInt64()));
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
