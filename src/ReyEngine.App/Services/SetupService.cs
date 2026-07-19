using System;
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

    // ---- Dominion (Map8) preview-backdrop asset pack ----
    public static string Map8InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReyEngine Projects", "Maps", "Map8");
    public static bool Map8Installed => File.Exists(Path.Combine(Map8InstallDir, "Scene", "room.nvr"));
    public const string Map8Url =
        $"https://github.com/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/download/maps/ReyEngine-Map8-Dominion.zip";

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
