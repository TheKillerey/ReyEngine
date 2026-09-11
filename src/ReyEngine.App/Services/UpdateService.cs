using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ReyEngine.App.Services;

/// <summary>M81: checks GitHub for a newer release. M681: brings the release notes and the assets along,
/// so the update dialog can show the changelog and the applier can download the build - and owns the
/// update MODE (ask / auto / manual), including the default an installer may have set. Never throws.</summary>
public static class UpdateService
{
    public sealed record ReleaseAsset(string Name, string Url, long Size);

    public sealed record UpdateCheck(bool Success, bool UpdateAvailable, string? LatestVersion, string? ReleaseUrl, string? Error,
        string? Body = null, string? ReleaseName = null, IReadOnlyList<ReleaseAsset>? Assets = null);

    public const string ModeAsk = "ask";
    public const string ModeAuto = "auto";
    public const string ModeManual = "manual";
    /// <summary>The Settings list, in order: index 0 asks, 1 installs automatically, 2 is manual.</summary>
    public static readonly string[] Modes = { ModeAsk, ModeAuto, ModeManual };

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
            string tag = ""; string url = AppInfo.RepoUrl; string? body = null; string? name = null;
            var assets = new List<ReleaseAsset>();
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                string t = rel.GetProperty("tag_name").GetString() ?? "";
                if (ParseVersion(t) is null) continue;
                tag = t;
                url = rel.TryGetProperty("html_url", out var u) ? u.GetString() ?? AppInfo.RepoUrl : AppInfo.RepoUrl;
                // M681: the release notes (docs/release-notes/<tag>.md ships as the body) and the builds
                body = rel.TryGetProperty("body", out var b) ? b.GetString() : null;
                name = rel.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (rel.TryGetProperty("assets", out var arr))
                    foreach (var a in arr.EnumerateArray())
                        assets.Add(new ReleaseAsset(
                            a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "",
                            a.TryGetProperty("browser_download_url", out var au) ? au.GetString() ?? "" : "",
                            a.TryGetProperty("size", out var asz) && asz.TryGetInt64(out long sz) ? sz : 0));
                break;
            }

            var latest = ParseVersion(tag);
            var current = ParseVersion(AppInfo.Version);
            bool newer = latest is not null && current is not null && latest > current;
            return new UpdateCheck(true, newer, tag, url, null, body, name, assets);
        }
        catch (Exception ex)
        {
            // 404 = no release published yet; network errors etc. — all non-fatal.
            return new UpdateCheck(false, false, null, null, ex.Message);
        }
    }

    /// <summary>Parse "v1.2.3", "1.2.3-beta" etc. into a comparable Version (extras ignored).</summary>
    public static Version? ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V');
        int dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? v : null;
    }

    public static void OpenReleasePage(UpdateCheck check)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(check.ReleaseUrl ?? AppInfo.RepoUrl) { UseShellExecute = true }); }
        catch { }
    }

    // ---- M681: the mode ----------------------------------------------------------------------------

    /// <summary>The mode the app acts on: the user's explicit choice, else what the installer wrote
    /// (HKCU\Software\ReyEngine\AutoUpdate, 1 = automatic, 0 = ask - an MSI can set it from its
    /// AUTOUPDATE property), else ask.</summary>
    public static string EffectiveMode(string? configured)
    {
        if (configured is ModeAsk or ModeAuto or ModeManual) return configured;
        return InstallerAutoUpdateDefault switch { true => ModeAuto, false => ModeAsk, null => ModeAsk };
    }

    /// <summary>What the installer asked for, when one did: true/false from the registry value, null when
    /// nothing wrote it (a portable zip install).</summary>
    public static bool? InstallerAutoUpdateDefault
    {
        get
        {
            var v = ReadRegistry("AutoUpdate");
            return v switch { int i => i != 0, string t when int.TryParse(t, out int ti) => ti != 0, _ => null };
        }
    }

    public static int IndexOfMode(string mode) => Math.Max(0, Array.IndexOf(Modes, mode));
    public static string ModeAtIndex(int index) => index >= 0 && index < Modes.Length ? Modes[index] : ModeAsk;

    /// <summary>A value under HKCU\Software\ReyEngine, then HKLM. Windows only; null elsewhere or when absent.</summary>
    internal static object? ReadRegistry(string valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var user = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\ReyEngine");
            if (user?.GetValue(valueName) is { } uv) return uv;
            using var machine = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\ReyEngine");
            return machine?.GetValue(valueName);
        }
        catch { return null; }
    }

    // ---- M681: the changelog ----------------------------------------------------------------------

    /// <summary>The release body - Markdown, written by hand in docs/release-notes - as the plain text the
    /// update dialog shows: headings stand alone, bullets keep their shape, emphasis and links lose their
    /// syntax. Not a Markdown renderer; a reader that never shows a stray asterisk.</summary>
    public static string ChangelogToText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "No release notes were published for this version.";
        var sb = new StringBuilder();
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith('#'))
            {
                if (sb.Length > 0) sb.AppendLine();
                line = line.TrimStart('#').Trim().ToUpperInvariant();
            }
            else if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
            {
                int indent = line.Length - line.TrimStart().Length;
                line = new string(' ', indent) + "\u2022 " + line.TrimStart()[2..];
            }
            line = Regex.Replace(line, @"\*\*(.+?)\*\*", "$1");
            line = Regex.Replace(line, @"__(.+?)__", "$1");
            line = Regex.Replace(line, @"`(.+?)`", "$1");
            line = Regex.Replace(line, @"\[(.+?)\]\((.+?)\)", "$1");
            sb.AppendLine(line);
        }
        return sb.ToString().Trim();
    }
}
