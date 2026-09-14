using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using ReyEngine.Core;

namespace ReyEngine.App.Services;

/// <summary>
/// M93: everything the first-run wizard (and Settings) needs to set the tool up — status checks for
/// each component plus one shared download-and-extract helper. All installs go to per-user locations,
/// never into the game or the app folder (which may be read-only).
/// </summary>
public static class SetupService
{
    // ---- CommunityDragon hashes ----
    public static bool HashesInstalled => File.Exists(ReyPaths.MergedCache);

    // ---- vgmstream (Wwise audio decoder; ISC-licensed) ----
    public static string VgmstreamDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReyEngine", "tools", "vgmstream");
    public static string VgmstreamExe => Path.Combine(VgmstreamDir, "vgmstream-cli.exe");
    public static bool VgmstreamInstalled => File.Exists(VgmstreamExe);
    public const string VgmstreamUrl =
        "https://github.com/vgmstream/vgmstream/releases/latest/download/vgmstream-win64.zip";

    // ---- legacy (NVR) map asset packs — download + view via Open Legacy Map / preview backdrop ----
    private static string MapsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReyEngine Projects", "Maps");
    /// <summary>M725: a pack folder is only installed when the room it exists for is actually there.
    /// Public because the download path has to re-check it: extracting a renamed, truncated or wrong-archive
    /// zip used to leave the row claiming "installed" and wearing the backdrop badge, while the backdrop
    /// itself silently never appeared.</summary>
    public static bool HasNvr(string dir) => File.Exists(Path.Combine(dir, "Scene", "room.nvr"));

    // Dominion (Map8)
    public static string Map8InstallDir => Path.Combine(MapsRoot, "Map8");
    public static bool Map8Installed => HasNvr(Map8InstallDir);
    public const string Map8Url =
        $"https://github.com/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/download/maps/ReyEngine-Map8-Dominion.zip";

    // M141: Twisted Treeline (Map10) — height-blended ground
    public static string Map10InstallDir => Path.Combine(MapsRoot, "Map10");
    public static bool Map10Installed => HasNvr(Map10InstallDir);
    public const string Map10Url =
        $"https://github.com/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/download/maps/ReyEngine-Map10-TwistedTreeline.zip";

    /// <summary>
    /// The two preview environments the Character Viewer offers, and the only ones.
    ///
    /// <para>M725: named ONCE, here. The Map8 pack was "Dominion" in the first-run wizard and the README and
    /// "Crystal Scar (Map8)" in Settings - the same download under two names, which reads as two different
    /// maps that you are missing one of. It is Dominion; Crystal Scar is the arena Dominion is played on.</para>
    /// </summary>
    public static IReadOnlyList<(string Name, string InstallDir, string Url, bool Installed)> LegacyMapPacks => new[]
    {
        (BackdropDominion, Map8InstallDir, Map8Url, Map8Installed),
        (BackdropTwistedTreeline, Map10InstallDir, Map10Url, Map10Installed),
    };

    public const string BackdropDominion = "Dominion (Map8)";
    public const string BackdropTwistedTreeline = "Twisted Treeline (Map10)";

    /// <summary>The install folder for a pack name, or null when the name is not one of the two.</summary>
    public static string? BackdropFolderFor(string? name) => name switch
    {
        BackdropDominion => Map8InstallDir,
        BackdropTwistedTreeline => Map10InstallDir,
        _ => null,
    };

    /// <summary>Which of the two a folder is, by the install directory it sits in. Null for anything else -
    /// an arena, or a legacy map opened through Open Legacy Map, neither of which is a preview backdrop.</summary>
    public static string? BackdropNameFor(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        string f = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(f, Path.GetFullPath(Map8InstallDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return BackdropDominion;
        if (string.Equals(f, Path.GetFullPath(Map10InstallDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return BackdropTwistedTreeline;
        return null;
    }

    /// <summary>Download a zip with progress and extract it into <paramref name="destDir"/> (created;
    /// existing files overwritten). Reports "Downloading… NN%" / "Extracting…" through
    /// <paramref name="status"/>. Throws on network/extract failure — callers show the message.</summary>
    public static async Task DownloadAndExtractAsync(string url, string destDir, IProgress<string> status)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "ReyEngine-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            status.Report("Downloading… 0%");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                var buffer = new byte[81920];
                long read = 0; int n; int lastPct = -1;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    int pct = total > 0 ? (int)(read * 100 / total) : -1;
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        status.Report(pct >= 0 ? $"Downloading… {pct}%" : $"Downloading… {read / 1048576} MB");
                    }
                }
            }

            status.Report("Extracting…");
            Directory.CreateDirectory(destDir);
            await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(tmp, destDir, overwriteFiles: true));
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
