using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ReyEngine.Core.Hashing;

namespace ReyEngine.App.Services;

/// <summary>
/// M97c: old-patch game files from raw.communitydragon.org — the "old original" side of the
/// three-way patch update. CDragon keeps every patch since 10.1 as extracted game trees and serves
/// .bin files as raw PROP binaries (verified: PROP magic, version 3, HTTP range support).
/// Downloads are cached on disk per patch, keyed by path hash (longname bins exceed MAX_PATH).
/// </summary>
public static class CommunityDragonClient
{
    private const string Base = "https://raw.communitydragon.org";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"ReyEngine/{AppInfo.Version}");
        return c;
    }

    /// <summary>All game patches CDragon archives ("15.24", "14.10", …), newest first.</summary>
    public static async Task<IReadOnlyList<string>> ListPatchesAsync()
    {
        await using var s = await Http.GetStreamAsync($"{Base}/json/");
        using var doc = await JsonDocument.ParseAsync(s);
        var patches = new List<(int Maj, int Min, string Name)>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var m = Regex.Match(name, @"^(\d+)\.(\d+)$");
            if (m.Success) patches.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), name));
        }
        return patches.OrderByDescending(p => p.Maj).ThenByDescending(p => p.Min).Select(p => p.Name).ToList();
    }

    /// <summary>The old-patch original of a game file (raw PROP bin). Null when that patch never had
    /// the file (mod-only path, or renamed since). Cached under <paramref name="cacheDir"/>.</summary>
    public static async Task<byte[]?> DownloadBinAsync(string patch, string gamePath, string cacheDir)
    {
        string cacheFile = Path.Combine(cacheDir, patch, $"{HashAlgorithms.WadPath(gamePath):x16}.bin");
        if (File.Exists(cacheFile)) return await File.ReadAllBytesAsync(cacheFile);
        string missMarker = cacheFile + ".404";
        if (File.Exists(missMarker)) return null;

        using var resp = await Http.GetAsync($"{Base}/{patch}/game/{gamePath.ToLowerInvariant()}");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            await File.WriteAllBytesAsync(missMarker, Array.Empty<byte>());
            return null;
        }
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync();

        // must be a real bin, never an HTML error page
        bool prop = bytes.Length > 8
            && ((bytes[0] == 'P' && bytes[1] == 'R' && bytes[2] == 'O' && bytes[3] == 'P')
                || (bytes[0] == 'P' && bytes[1] == 'T' && bytes[2] == 'C' && bytes[3] == 'H'));
        if (!prop) return null;

        await File.WriteAllBytesAsync(cacheFile, bytes);
        return bytes;
    }

    /// <summary>Default download cache: %LocalAppData%\ReyEngine\cdragon.</summary>
    public static string DefaultCacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReyEngine", "cdragon");
}
