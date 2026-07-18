using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace ReyEngine.App.Services;

/// <summary>M81: checks GitHub for a newer release. v1 policy: notify + open the release page in the
/// browser (no silent binary replacement — honest and safe for a beta). Never throws.</summary>
public static class UpdateService
{
    public sealed record UpdateCheck(bool Success, bool UpdateAvailable, string? LatestVersion, string? ReleaseUrl, string? Error);

    public static async Task<UpdateCheck> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ReyEngine", AppInfo.Version));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            // NOT /releases/latest — that endpoint excludes prereleases, and every beta release is one.
            // The list is newest-first; take the first non-draft entry whose tag is an actual version
            // (asset releases like 'maps' must never read as an update).
            var json = await http.GetStringAsync(
                $"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases?per_page=10");
            using var doc = JsonDocument.Parse(json);
            string tag = ""; string url = AppInfo.RepoUrl;
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                string t = rel.GetProperty("tag_name").GetString() ?? "";
                if (ParseVersion(t) is null) continue;
                tag = t;
                url = rel.TryGetProperty("html_url", out var u) ? u.GetString() ?? AppInfo.RepoUrl : AppInfo.RepoUrl;
                break;
            }

            var latest = ParseVersion(tag);
            var current = ParseVersion(AppInfo.Version);
            bool newer = latest is not null && current is not null && latest > current;
            return new UpdateCheck(true, newer, tag, url, null);
        }
        catch (Exception ex)
        {
            // 404 = no release published yet; network errors etc. — all non-fatal.
            return new UpdateCheck(false, false, null, null, ex.Message);
        }
    }

    /// <summary>Parse "v1.2.3", "1.2.3-beta" etc. into a comparable Version (extras ignored).</summary>
    private static Version? ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V');
        int dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? v : null;
    }
}
