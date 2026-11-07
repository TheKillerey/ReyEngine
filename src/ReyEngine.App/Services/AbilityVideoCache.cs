using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using ReyEngine.Core;
using System.Threading.Tasks;

namespace ReyEngine.App.Services;

/// <summary>The outcome of <see cref="AbilityVideoCache.EnsureAsync"/>: the cached file, or null with a short message.</summary>
public sealed record AbilityVideoResult(string? Path, string Message);

/// <summary>
/// M878: Riot's per-ability videos and poster images, downloaded on demand and cached under
/// <c>&lt;DataRoot&gt;/cache/ability-videos/&lt;id&gt;/&lt;file&gt;</c>. They are not in any WAD: the client's champion-details plugin
/// (<c>rcp-fe-lol-champion-details.js</c>) builds <c>abilityVideoBasePath + ability.abilityVideoPath</c> with the default base
/// <see cref="BaseUrl"/> (the base is overridable by the client's <c>web_assets_base_path</c> system setting). One file per request,
/// never a bulk download; a failure leaves no file behind and never throws.
/// </summary>
public sealed class AbilityVideoCache
{
    public const string BaseUrl = "https://lol.dyn.riotcdn.net/x/videos/";

    private static readonly Regex PathShape = new(@"^champion-abilities/(\d{3,5})/([A-Za-z0-9_.\-]+\.(?:webm|jpg|png))$", RegexOptions.Compiled);

    private static readonly HttpClient SharedHttp = CreateClient();
    private readonly string _root;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    /// <param name="root">The cache folder; <c>&lt;DataRoot&gt;/cache/ability-videos</c> when null.</param>
    public AbilityVideoCache(string? root = null, HttpClient? http = null, TimeSpan? timeout = null)
    {
        _root = root ?? Path.Combine(ReyPaths.DataRoot, "cache", "ability-videos");
        _http = http ?? SharedHttp;
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };    // the per-request timeout is a CancellationTokenSource
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"ReyEngine/{AppInfo.Version}");
        return c;
    }

    /// <summary>The cache file of a client path ("champion-abilities/0266/ability_0266_Q1.webm"), or null when the path is not that shape
    /// (so a hostile or odd value can never leave the cache folder).</summary>
    public string? CachePath(string? clientPath)
    {
        var m = PathShape.Match((clientPath ?? "").Trim().TrimStart('/'));
        return m.Success ? Path.Combine(_root, m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    /// <summary>The URL a client path is fetched from, or null when the path is not that shape.</summary>
    public static string? UrlOf(string? clientPath)
    {
        var m = PathShape.Match((clientPath ?? "").Trim().TrimStart('/'));
        return m.Success ? BaseUrl + m.Value : null;
    }

    /// <summary>The cached file when it is already there, else null. No network.</summary>
    public string? Cached(string? clientPath) => CachePath(clientPath) is { } p && File.Exists(p) && new FileInfo(p).Length > 0 ? p : null;

    /// <summary>The cached file, downloaded first when missing (temp file, then rename). Runs on the thread pool.</summary>
    public async Task<AbilityVideoResult> EnsureAsync(string? clientPath, CancellationToken ct = default)
    {
        string? target = CachePath(clientPath), url = UrlOf(clientPath);
        if (target is null || url is null) return new AbilityVideoResult(null, "No video for this ability.");
        if (Cached(clientPath) is { } hit) return new AbilityVideoResult(hit, "");
        string tmp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        try
        {
            return await Task.Run(async () =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeout);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return new AbilityVideoResult(null, $"Download failed (HTTP {(int)response.StatusCode}).");
                    await using var src = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                    await src.CopyToAsync(dst, cts.Token).ConfigureAwait(false);
                }
                if (new FileInfo(tmp).Length == 0) return new AbilityVideoResult(null, "Download failed (empty file).");
                File.Move(tmp, target, overwrite: true);
                return new AbilityVideoResult(target, "");
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new AbilityVideoResult(null, ct.IsCancellationRequested ? "Cancelled." : "Download timed out."); }
        catch (Exception ex) { return new AbilityVideoResult(null, "Download failed: " + ex.Message); }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* a leftover .part is harmless */ } }
    }
}
