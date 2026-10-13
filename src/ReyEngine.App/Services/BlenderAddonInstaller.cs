using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReyEngine.App.Services;

/// <summary>
/// M682: installs the Blender add-on into every Blender the user has, from inside the tool - instead of Edit ▸
/// Preferences ▸ Add-ons ▸ Install… by hand for each version.
///
/// <para>M841: the add-on is MapgeoAddon now (TheKillerey/MapgeoAddon), a package folder, which replaced the
/// single-file "ReyEngine Bridge" (reyengine_bridge.py). ReyEngine no longer ships an add-on of its own; the
/// source is a setting - a local folder, a .zip, or a URL (by default the latest GitHub release). Installing
/// copies the package to <c>scripts\addons\MapgeoAddon</c> and enables module <c>MapgeoAddon</c>; a
/// reyengine_bridge.py found in the same folder is disabled and then removed, because the two would both
/// claim the link and the old one speaks a protocol this ReyEngine refuses.</para>
///
/// <para>A Blender install is found by its per-user config folder, %AppData%\Blender Foundation\Blender\
/// &lt;major.minor&gt;, and by blender.exe in the usual places (Program Files, Steam, a per-user install);
/// an executable's version is the &lt;major.minor&gt; folder that sits beside it. Enabling needs Blender
/// itself: when the matching blender.exe is known it is run in the background with a one-line script that
/// enables the module and saves the preferences - otherwise the files are in place and one tick in Blender's
/// add-on list finishes the job, which the status says.</para>
/// </summary>
public static class BlenderAddonInstaller
{
    public const string ModuleName = "MapgeoAddon";
    public const string LegacyModuleName = "reyengine_bridge";
    public const string LegacyFileName = LegacyModuleName + ".py";

    /// <summary>The default source: the latest MapgeoAddon release on GitHub.</summary>
    public const string DefaultSource = "https://github.com/TheKillerey/MapgeoAddon/releases/latest";

    /// <summary>One Blender the user has, by its config folder. The executable is optional - a Blender
    /// unpacked from a zip somewhere unusual has no known place to find it.</summary>
    public sealed record BlenderInstall(string Version, string ConfigDir, string? Executable)
    {
        public string AddonsDir => Path.Combine(ConfigDir, "scripts", "addons");
        public string AddonDir => Path.Combine(AddonsDir, ModuleName);
        public bool AddonInstalled => File.Exists(Path.Combine(AddonDir, "__init__.py"));
        public string LegacyAddonFile => Path.Combine(AddonsDir, LegacyFileName);
        public bool LegacyBridgeInstalled => File.Exists(LegacyAddonFile);

        /// <summary>bl_info["version"] of the installed copy, or null.</summary>
        public System.Version? InstalledVersion => AddonInstalled ? ReadVersion(AddonDir) : null;

        /// <summary>Installed and at least as new as <paramref name="available"/>. Unknown (null) when the
        /// source's version is not known yet - a URL that has not been downloaded.</summary>
        public bool? AddonCurrent(System.Version? available)
        {
            if (!AddonInstalled) return false;
            if (available is null || InstalledVersion is not { } installed) return null;
            return installed >= available;
        }
    }

    public sealed record InstallResult(bool Copied, bool Enabled, string Message);

    public static string DefaultConfigRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blender Foundation", "Blender");

    public static string DefaultProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <summary>Where a blender.exe may live besides Program Files\Blender Foundation\Blender X.Y. Cheap
    /// directory checks only - no registry walk, no drive scan.</summary>
    public static IReadOnlyList<string> DefaultExtraExecutableRoots()
    {
        var roots = new List<string>();
        void Add(string? path) { if (!string.IsNullOrEmpty(path)) roots.Add(path); }
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        Add(Path.Combine(pf86, "Blender Foundation"));
        Add(Path.Combine(pf86, "Steam", "steamapps", "common", "Blender"));
        Add(Path.Combine(DefaultProgramFiles, "Steam", "steamapps", "common", "Blender"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Blender Foundation"));
        foreach (var drive in SafeDrives())
            Add(Path.Combine(drive, "SteamLibrary", "steamapps", "common", "Blender"));
        return roots;
    }

    private static IEnumerable<string> SafeDrives()
    {
        try { return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static readonly Regex VersionFolder = new(@"^\d+\.\d+$", RegexOptions.Compiled);

    /// <summary>Every Blender version with a config folder or a known blender.exe, newest first.</summary>
    /// <param name="extraExecutableRoots">Folders that are, or contain, a Blender install (blender.exe beside a
    /// &lt;major.minor&gt; folder). Null = <see cref="DefaultExtraExecutableRoots"/>.</param>
    public static IReadOnlyList<BlenderInstall> Discover(string? configRoot = null, string? programFiles = null,
        IEnumerable<string>? extraExecutableRoots = null)
    {
        configRoot ??= DefaultConfigRoot;
        programFiles ??= DefaultProgramFiles;

        // version -> exe, from everywhere cheap to look
        var executables = new Dictionary<string, string>(StringComparer.Ordinal);
        var roots = new List<string> { Path.Combine(programFiles, "Blender Foundation") };
        roots.AddRange(extraExecutableRoots ?? DefaultExtraExecutableRoots());
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var (version, exe) in ExecutablesUnder(root))
                executables.TryAdd(version, exe);

        var found = new Dictionary<string, BlenderInstall>(StringComparer.Ordinal);
        if (Directory.Exists(configRoot))
        {
            foreach (string dir in Directory.GetDirectories(configRoot))
            {
                string version = Path.GetFileName(dir);
                if (!VersionFolder.IsMatch(version)) continue;
                found[version] = new BlenderInstall(version, dir, executables.GetValueOrDefault(version));
            }
        }
        // An installed Blender that was never started has no config folder yet; its add-ons folder is where
        // it will look on first start, so it is a target too.
        foreach (var (version, exe) in executables)
            found.TryAdd(version, new BlenderInstall(version, Path.Combine(configRoot, version), exe));

        return found.Values
            .OrderByDescending(b => System.Version.TryParse(b.Version, out var v) ? v : new Version(0, 0))
            .ToList();
    }

    /// <summary>blender.exe files directly in <paramref name="root"/> or one level below it, each with the
    /// version named by the &lt;major.minor&gt; folder beside it.</summary>
    private static IEnumerable<(string Version, string Exe)> ExecutablesUnder(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var candidates = new List<string> { root };
        try { candidates.AddRange(Directory.GetDirectories(root)); } catch { }
        foreach (string dir in candidates)
        {
            string exe = Path.Combine(dir, "blender.exe");
            if (!File.Exists(exe)) continue;
            string? version = null;
            try { version = Directory.GetDirectories(dir).Select(d => Path.GetFileName(d)).FirstOrDefault(n => n is not null && VersionFolder.IsMatch(n)); }
            catch { }
            if (version is null)
            {
                var m = Regex.Match(Path.GetFileName(dir) ?? "", @"(\d+\.\d+)");
                if (m.Success) version = m.Groups[1].Value;
            }
            if (version is not null) yield return (version, exe);
        }
    }

    // ---- the add-on source ------------------------------------------------------------------------

    /// <summary>The source to install from: the setting, or the default release URL when it is empty.</summary>
    public static string EffectiveSource(string? setting) =>
        string.IsNullOrWhiteSpace(setting) ? DefaultSource : setting.Trim().Trim('"');

    public static bool IsUrl(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The add-on package folder in a local source (the folder itself, or a MapgeoAddon folder in it),
    /// or null. A URL or a zip has no folder until <see cref="PrepareSourceAsync"/> made one.</summary>
    public static string? LocalPackageFolder(string source)
    {
        if (IsUrl(source) || !Directory.Exists(source)) return null;
        return FindPackage(source);
    }

    /// <summary>The version of a local folder source, or null (a zip or a URL, or no bl_info).</summary>
    public static Version? SourceVersion(string source) =>
        LocalPackageFolder(source) is { } folder ? ReadVersion(folder) : null;

    /// <summary>A package folder ready to copy: a folder source as it is; a .zip extracted to a temporary
    /// folder; a URL downloaded first (a GitHub "releases/latest" page is asked for its .zip asset).
    /// The returned cleanup deletes whatever was made.</summary>
    public static async Task<(string Folder, Action Cleanup)> PrepareSourceAsync(string source, CancellationToken ct = default,
        HttpClient? http = null)
    {
        source = EffectiveSource(source);
        if (LocalPackageFolder(source) is { } folder) return (folder, () => { });
        if (Directory.Exists(source))
            throw new InvalidOperationException($"{source} holds no MapgeoAddon package (no __init__.py with bl_info).");

        string temp = Path.Combine(Path.GetTempPath(), "reyengine-mapgeoaddon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        void Cleanup() { try { Directory.Delete(temp, recursive: true); } catch { } }
        try
        {
            string zip = source;
            if (IsUrl(source))
            {
                zip = Path.Combine(temp, "MapgeoAddon.zip");
                await DownloadAsync(source, zip, ct, http);
            }
            else if (!File.Exists(source))
                throw new FileNotFoundException($"The add-on source {source} does not exist.");

            string extracted = Path.Combine(temp, "x");
            ExtractSafely(zip, extracted);
            string package = FindPackage(extracted)
                ?? throw new InvalidOperationException($"{Path.GetFileName(zip)} holds no MapgeoAddon package (no __init__.py with bl_info).");
            return (package, Cleanup);
        }
        catch { Cleanup(); throw; }
    }

    /// <summary>Ceilings on what an install downloads and unpacks. MapgeoAddon's release zip is a few MB.</summary>
    public const long MaxDownloadBytes = 200L * 1024 * 1024;
    public const long MaxExtractedBytes = 400L * 1024 * 1024;
    public const int MaxZipEntries = 5000;

    /// <summary>Unpack a zip with ceilings, and never outside <paramref name="target"/> (a "..\" entry).</summary>
    internal static void ExtractSafely(string zip, string target)
    {
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > MaxZipEntries)
            throw new InvalidOperationException($"{Path.GetFileName(zip)} has {archive.Entries.Count:n0} entries - not an add-on package.");
        long total = archive.Entries.Sum(e => e.Length);
        if (total > MaxExtractedBytes)
            throw new InvalidOperationException($"{Path.GetFileName(zip)} unpacks to {total / (1024 * 1024):n0} MB - not an add-on package.");
        string root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            string destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{Path.GetFileName(zip)} has an entry outside its folder ({entry.FullName}).");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static Uri RequireHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Only https sources are installed from: {url}");
        return uri;
    }

    /// <summary>GET with redirects followed by hand, so every hop is checked to be https.</summary>
    private static async Task<HttpResponseMessage> GetHttpsAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        for (int hop = 0; hop < 8; hop++)
        {
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)response.StatusCode;
            if (code is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                response.Dispose();
                uri = RequireHttps((next.IsAbsoluteUri ? next : new Uri(uri, next)).ToString());
                continue;
            }
            response.EnsureSuccessStatusCode();
            return response;
        }
        throw new InvalidOperationException("Too many redirects.");
    }

    private static async Task DownloadAsync(string url, string target, CancellationToken ct, HttpClient? http)
    {
        RequireHttps(url);
        var client = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        try
        {
            client.DefaultRequestHeaders.UserAgent.TryParseAdd("ReyEngine");
            string download = url;
            var release = Regex.Match(url, @"^https://github\.com/([^/]+)/([^/]+)/releases(?:/(latest|tag/[^/?#]+))?/?$", RegexOptions.IgnoreCase);
            if (release.Success)
            {
                string owner = release.Groups[1].Value, repo = release.Groups[2].Value;
                string which = release.Groups[3].Success && release.Groups[3].Value.StartsWith("tag/", StringComparison.OrdinalIgnoreCase)
                    ? "tags/" + release.Groups[3].Value[4..] : "latest";
                using var api = await GetHttpsAsync(client, new Uri($"https://api.github.com/repos/{owner}/{repo}/releases/{which}"), ct);
                using var doc = JsonDocument.Parse(await api.Content.ReadAsStringAsync(ct));
                // only an asset of THIS repository's release: the API answer names the download, and it is not
                // trusted to point anywhere else
                string prefix = $"https://github.com/{owner}/{repo}/releases/download/";
                download = doc.RootElement.TryGetProperty("assets", out var assets)
                    ? assets.EnumerateArray()
                        .Select(a => a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null)
                        .FirstOrDefault(u => u is not null && u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                             && u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ?? ""
                    : "";
                if (download.Length == 0) throw new InvalidOperationException($"The release at {url} has no .zip asset to install.");
            }
            using var response = await GetHttpsAsync(client, RequireHttps(download), ct);
            if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
                throw new InvalidOperationException($"The download is {response.Content.Headers.ContentLength / (1024 * 1024):n0} MB - more than an add-on.");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(target);
            var buffer = new byte[81920];
            long written = 0;
            int n;
            while ((n = await body.ReadAsync(buffer, ct)) > 0)
            {
                written += n;
                if (written > MaxDownloadBytes) throw new InvalidOperationException("The download exceeded 200 MB - not an add-on package.");
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
        finally { if (http is null) client.Dispose(); }
    }

    /// <summary>The folder holding the add-on's __init__.py with a bl_info: <paramref name="root"/> itself,
    /// else the shallowest such folder below it (a release zip wraps it in MapgeoAddon/, a GitHub zipball in
    /// owner-repo-sha/).</summary>
    private static string? FindPackage(string root)
    {
        if (IsPackage(root)) return root;
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (depth >= 3) continue;
            string[] children;
            try { children = Directory.GetDirectories(dir); } catch { continue; }
            foreach (string child in children.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            {
                if (IsPackage(child)) return child;
                queue.Enqueue((child, depth + 1));
            }
        }
        return null;
    }

    private static bool IsPackage(string dir)
    {
        string init = Path.Combine(dir, "__init__.py");
        if (!File.Exists(init)) return false;
        try { return File.ReadAllText(init).Contains("bl_info", StringComparison.Ordinal); }
        catch { return false; }
    }

    private static readonly Regex BlVersion = new(@"[""']version[""']\s*:\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.Compiled);

    /// <summary>bl_info["version"] of a package folder, or null.</summary>
    public static Version? ReadVersion(string packageFolder)
    {
        try
        {
            var m = BlVersion.Match(File.ReadAllText(Path.Combine(packageFolder, "__init__.py")));
            return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
        }
        catch { return null; }
    }

    // ---- installing --------------------------------------------------------------------------------

    /// <summary>The line Blender runs, in the background: switch the old bridge off (if it was on), switch
    /// MapgeoAddon on, and remember both.</summary>
    public static string EnableExpression =>
        "import bpy, addon_utils; "
        + $"addon_utils.disable('{LegacyModuleName}', default_set=True); "
        + $"bpy.ops.preferences.addon_enable(module='{ModuleName}'); "
        + "bpy.ops.wm.save_userpref()";

    /// <summary>
    /// Why an existing add-on folder must not be replaced, or null: a git working copy (a developer runs the add-on
    /// from their checkout, uncommitted work included) or a junction/symbolic link to a folder elsewhere. Replacing
    /// either would delete files that are not ReyEngine's to delete.
    /// </summary>
    public static string? ManagedByHand(string addonDir)
    {
        try
        {
            if (!Directory.Exists(addonDir)) return null;
            var info = new DirectoryInfo(addonDir);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                return "is a link (junction or symbolic link) to " + (info.LinkTarget ?? "another folder");
            if (Directory.Exists(Path.Combine(addonDir, ".git")) || File.Exists(Path.Combine(addonDir, ".git")))
                return "is a git working copy";
            return null;
        }
        catch (Exception ex) { return "could not be checked (" + ex.Message + ")"; }
    }

    /// <summary>Files of a package that are never copied: version control, caches, release builds.</summary>
    private static bool Skip(string relative)
    {
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (part is ".git" or "__pycache__" or ".github" or "releases" or ".vscode" or ".idea") return true;
        return relative.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(relative).Equals(".gitignore", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(relative).Equals(".gitattributes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copy the package into the install's scripts\addons\MapgeoAddon (replacing an older copy), remove a
    /// reyengine_bridge.py beside it, and, when its blender.exe is known and <paramref name="enable"/> is set,
    /// enable the add-on there (and disable the old bridge in the same run). Never throws: the message says
    /// what happened.
    /// </summary>
    public static async Task<InstallResult> InstallAsync(BlenderInstall target, string packageFolder, bool enable,
        CancellationToken ct = default)
    {
        bool hadLegacy = target.LegacyBridgeInstalled;
        if (ManagedByHand(target.AddonDir) is { } why)
            return new InstallResult(false, false, $"Blender {target.Version}: {target.AddonDir} {why}, so it was left alone - "
                + "update it there yourself (git pull), or remove it and install again.");
        string staging = target.AddonDir + ".installing";
        string aside = target.AddonDir + ".previous";
        bool movedAside = false;
        try
        {
            Directory.CreateDirectory(target.AddonsDir);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            foreach (string file in Directory.EnumerateFiles(packageFolder, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(packageFolder, file);
                if (Skip(relative)) continue;
                string destination = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
            // the old copy is renamed aside, not deleted, until the new one is in place - a failed move puts it back
            if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
            if (Directory.Exists(target.AddonDir)) { Directory.Move(target.AddonDir, aside); movedAside = true; }
            Directory.Move(staging, target.AddonDir);
            if (movedAside) { try { Directory.Delete(aside, recursive: true); } catch { /* a locked file: harmless leftover */ } }
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            try { if (movedAside && !Directory.Exists(target.AddonDir) && Directory.Exists(aside)) Directory.Move(aside, target.AddonDir); } catch { }
            return new InstallResult(false, false, $"Could not copy MapgeoAddon into {target.AddonsDir}: {ex.Message}");
        }

        bool ok = false;
        string runNote = "";
        if (enable && target.Executable is not null)
            (ok, runNote) = await RunEnableAsync(target.Executable, ct);

        // The old bridge goes AFTER Blender had its chance to switch it off, so the preferences do not keep
        // an entry for a module that is gone.
        string legacyNote = "";
        if (hadLegacy)
        {
            try
            {
                File.Delete(target.LegacyAddonFile);
                string cache = Path.Combine(target.AddonsDir, "__pycache__");
                if (Directory.Exists(cache))
                    foreach (string pyc in Directory.GetFiles(cache, LegacyModuleName + ".*.pyc")) File.Delete(pyc);
                legacyNote = " The old ReyEngine Bridge add-on (reyengine_bridge.py) was " + (ok ? "disabled and " : "") + "removed.";
            }
            catch (Exception ex) { legacyNote = $" The old reyengine_bridge.py could not be removed ({ex.Message}) - delete it from {target.AddonsDir}."; }
        }

        string version = ReadVersion(target.AddonDir)?.ToString(3) is { } v ? " " + v : "";
        if (!enable || target.Executable is null)
            return new InstallResult(true, false, (target.Executable is null
                ? $"MapgeoAddon{version} installed for Blender {target.Version}. Its blender.exe was not found, so tick \"Rey's Mapgeo Blender Addon\" once in Edit ▸ Preferences ▸ Add-ons."
                : $"MapgeoAddon{version} installed for Blender {target.Version}.") + legacyNote);

        return new InstallResult(true, ok, (ok
            ? $"MapgeoAddon{version} installed and enabled for Blender {target.Version}."
            : $"MapgeoAddon{version} installed for Blender {target.Version}, but enabling it there failed. Tick \"Rey's Mapgeo Blender Addon\" once in Edit ▸ Preferences ▸ Add-ons." + runNote) + legacyNote);
    }

    private static async Task<(bool Ok, string Note)> RunEnableAsync(string executable, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-b");
            psi.ArgumentList.Add("--python-expr");
            psi.ArgumentList.Add(EnableExpression);
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Blender did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(180));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string err = (await error).Trim();
            bool ok = process.ExitCode == 0 && !err.Contains("Traceback", StringComparison.Ordinal);
            return (ok, ok ? "" : $" (exit {process.ExitCode})" + (err.Length > 0 ? "\n" + err[^Math.Min(err.Length, 300)..] : ""));
        }
        catch (Exception ex) { return (false, $" (Blender could not be run: {ex.Message})"); }
    }
}
