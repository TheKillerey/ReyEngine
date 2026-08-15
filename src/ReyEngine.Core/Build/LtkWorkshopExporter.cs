using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReyEngine.Core.Build;

/// <summary>Where LTK Manager keeps its own configuration, and what it says the workshop root is.</summary>
public static class LtkManagerLocator
{
    /// <summary>The manager's Tauri app-data folder. Its settings.json holds <c>workshopPath</c>.</summary>
    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dev.leaguetoolkit.manager");

    public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    /// Read the workshop root out of LTK Manager's own settings rather than guessing a path.
    ///
    /// <para>Asking the manager is the point: the observed install has
    /// <c>"workshopPath": "D:\\Workshopmods"</c>, which no default would have produced, and the same file
    /// carries <c>"watcherEnabled": true</c> — the manager watches that folder, so writing a mod there is
    /// how a mod gets in without touching library.json, the mod ids, or the enabled/ordering state of the
    /// user's profile. Editing that database directly is the thing this deliberately does NOT do.</para>
    /// </summary>
    public static bool TryFindWorkshopRoot(out string? root, out string detail)
    {
        root = null;
        if (!File.Exists(SettingsFile))
        { detail = $"LTK Manager settings not found at {SettingsFile} — is it installed?"; return false; }

        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(SettingsFile)); }
        catch (Exception ex) { detail = $"LTK Manager settings did not parse: {ex.Message}"; return false; }

        string? path = node?["workshopPath"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path))
        { detail = "LTK Manager has no workshopPath set — set a workshop folder in the manager first."; return false; }

        if (!Directory.Exists(path))
        { detail = $"LTK Manager's workshopPath does not exist: {path}"; return false; }

        root = path;
        detail = path;
        return true;
    }

    /// <summary>Is the manager running? It owns these files, and a write while it is watching can race the
    /// import. Reported rather than enforced — the caller decides whether to warn or refuse.</summary>
    public static bool IsManagerRunning() =>
        System.Diagnostics.Process.GetProcessesByName("ltk-manager").Length > 0;
}

/// <summary>What to write. The caller supplies the file set, so this stays pure and testable.</summary>
/// <param name="WorkshopRoot">LTK Manager's workshop folder.</param>
/// <param name="Slug">Folder name and <c>mod.config.json.name</c>. Lowercase, no spaces.</param>
/// <param name="Layer">Content layer; the shipped convention is a single "base".</param>
public sealed record LtkSendOptions(
    string WorkshopRoot,
    string Slug,
    string DisplayName,
    string Version,
    string Description,
    string Author,
    string Layer = "base",
    string? ThumbnailPath = null);

/// <param name="Created">True when the mod folder did not exist and was created.</param>
public sealed record LtkSendResult(
    bool Created, string ModFolder, int FilesWritten, int FilesDeleted, long BytesWritten, string Detail);

/// <summary>
/// M470: write a ReyEngine project into LTK Manager's workshop folder as a source mod, creating it or
/// updating it in place.
///
/// <para><b>Why the workshop and not the library.</b> LTK Manager stores installed mods as
/// <c>%APPDATA%\dev.leaguetoolkit.manager\{library.json, archives\&lt;uuid&gt;.fantome, mods\&lt;uuid&gt;\}</c>.
/// Writing there means minting UUIDs and rewriting library.json — a file that also holds the user's
/// profiles, enabled set, mod order and folder tree. Getting that wrong costs the user their setup, and
/// the format is another application's private schema that can change under us. The workshop folder is the
/// manager's own supported ingest path (<c>watcherEnabled: true</c>), so this writes there and lets the
/// manager own its database.</para>
///
/// <para><b>Layout</b>, matched against the user's existing workshop mods (oldriftday, mapforcer, …):</para>
/// <code>
/// &lt;workshop&gt;\&lt;slug&gt;\
///     mod.config.json                  name, display_name, version, description, authors[], tags[], maps[], layers[]
///     content\&lt;layer&gt;\&lt;Wad&gt;.wad.client\...   real relative paths (mapforcer ships exactly this)
///     build\, README.md, thumbnail.*   left alone unless we have a replacement
/// </code>
/// </summary>
public static class LtkWorkshopExporter
{
    /// <summary>A project folder named "Map453" mounts as "Map453.wad.client" — the cslol/fantome
    /// convention the workshop follows too. Names that already carry the suffix are left alone, because
    /// both <c>Map11.wad</c> and <c>Map11.wad.client</c> occur in the user's own workshop.</summary>
    public static string MountFolderName(string projectFolderName)
    {
        string n = projectFolderName.Trim().TrimEnd('/', '\\');
        if (n.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".wad", StringComparison.OrdinalIgnoreCase)) return n;
        return n + ".wad.client";
    }

    /// <summary>Folder-name and config <c>name</c> form: lowercase, alphanumerics and dashes only. Matches
    /// the shipped slugs (oldriftday, oldriftbeach, winterriftseason25).</summary>
    public static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if ((c == ' ' || c == '-' || c == '_') && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-') is { Length: > 0 } s ? s : "reyengine-mod";
    }

    /// <summary>
    /// Find an existing workshop mod whose config <c>name</c> matches <paramref name="slug"/>, even when
    /// its FOLDER is named something else. Without this, a mod the user renamed on disk would be sent a
    /// second time as a duplicate rather than updated, which is the one thing "including updating if it
    /// already exists" has to get right.
    /// </summary>
    public static string? FindExistingModFolder(string workshopRoot, string slug)
    {
        string direct = Path.Combine(workshopRoot, slug);
        if (File.Exists(Path.Combine(direct, "mod.config.json"))) return direct;

        foreach (var dir in Directory.EnumerateDirectories(workshopRoot))
        {
            string cfg = Path.Combine(dir, "mod.config.json");
            if (!File.Exists(cfg)) continue;
            try
            {
                var n = JsonNode.Parse(File.ReadAllText(cfg))?["name"]?.GetValue<string>();
                if (string.Equals(n, slug, StringComparison.OrdinalIgnoreCase)) return dir;
            }
            catch { /* a mod we cannot read is a mod we must not claim to match */ }
        }
        return null;
    }

    /// <param name="files">(project folder name, path relative to that folder, absolute source path).</param>
    public static LtkSendResult Send(
        LtkSendOptions o,
        IReadOnlyList<(string WadFolder, string RelPath, string AbsPath)> files,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(files);
        if (!Directory.Exists(o.WorkshopRoot))
            throw new DirectoryNotFoundException($"workshop root does not exist: {o.WorkshopRoot}");

        string? existing = FindExistingModFolder(o.WorkshopRoot, o.Slug);
        bool created = existing is null;
        string modFolder = existing ?? Path.Combine(o.WorkshopRoot, o.Slug);

        // Never operate outside the workshop. A slug that traverses ("../../Windows") would otherwise
        // resolve to a real folder and then get its contents replaced.
        string rootFull = Path.GetFullPath(o.WorkshopRoot);
        string modFull = Path.GetFullPath(modFolder);
        if (!modFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(modFull, rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"refusing to write outside the workshop root: {modFull}");
        if (string.Equals(modFull, rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("refusing to treat the workshop root itself as a mod folder");

        Directory.CreateDirectory(modFolder);
        string layerRoot = Path.Combine(modFolder, "content", o.Layer);

        // Replace the layer's contents so a file deleted from the project disappears from the mod too.
        // Guarded twice: the delete is confined to content/<layer>, and on an UPDATE the folder had to
        // carry a mod.config.json to be matched at all, so this cannot be pointed at an arbitrary
        // directory that merely shares a name.
        int deleted = 0;
        if (Directory.Exists(layerRoot))
        {
            foreach (var f in Directory.EnumerateFiles(layerRoot, "*", SearchOption.AllDirectories)) { deleted++; }
            Directory.Delete(layerRoot, recursive: true);
        }
        Directory.CreateDirectory(layerRoot);

        int written = 0;
        long bytes = 0;
        foreach (var (wadFolder, rel, abs) in files)
        {
            ct.ThrowIfCancellationRequested();
            string dest = Path.Combine(layerRoot, MountFolderName(wadFolder), rel.Replace('/', Path.DirectorySeparatorChar));
            string destFull = Path.GetFullPath(dest);
            if (!destFull.StartsWith(Path.GetFullPath(layerRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;   // a traversing relative path is skipped, not written
            Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
            File.Copy(abs, destFull, overwrite: true);
            written++;
            bytes += new FileInfo(destFull).Length;
        }

        WriteConfig(modFolder, o);

        if (o.ThumbnailPath is { Length: > 0 } thumb && File.Exists(thumb))
            File.Copy(thumb, Path.Combine(modFolder, "thumbnail" + Path.GetExtension(thumb)), overwrite: true);

        string readme = Path.Combine(modFolder, "README.md");
        if (!File.Exists(readme))
            File.WriteAllText(readme, $"# {o.DisplayName}\n\n{o.Description}\n");

        return new LtkSendResult(created, modFolder, written, deleted, bytes,
            $"{(created ? "created" : "updated")} {Path.GetFileName(modFolder)}: {written} file(s), "
            + $"{bytes / 1048576.0:0.0} MB, {deleted} replaced");
    }

    /// <summary>
    /// Write mod.config.json, MERGING into whatever is already there.
    ///
    /// <para>A typed round-trip would silently drop every field this class does not model — the user's
    /// existing configs carry <c>tags</c>, <c>maps</c> and per-layer descriptions, and the manager may add
    /// more in any release. So only the fields we own are assigned and the rest of the document is left
    /// exactly as found. Same reasoning as the .bin writers: never rewrite a structure you only partly
    /// understand.</para>
    /// </summary>
    private static void WriteConfig(string modFolder, LtkSendOptions o)
    {
        string path = Path.Combine(modFolder, "mod.config.json");
        JsonObject cfg;
        if (File.Exists(path))
        {
            try { cfg = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
            catch { cfg = new JsonObject(); }
        }
        else cfg = new JsonObject();

        cfg["name"] = o.Slug;
        cfg["display_name"] = o.DisplayName;
        cfg["version"] = o.Version;
        cfg["description"] = o.Description;

        // Authors: only seed when absent. The observed configs use two different shapes - a bare string
        // list and a list of {name, role} - so an existing list is the user's choice and is left alone.
        if (cfg["authors"] is not JsonArray { Count: > 0 })
            cfg["authors"] = new JsonArray(new JsonObject
            {
                ["name"] = o.Author,
                ["role"] = "Author",
            });

        if (cfg["layers"] is not JsonArray { Count: > 0 })
            cfg["layers"] = new JsonArray(new JsonObject
            {
                ["name"] = o.Layer,
                ["priority"] = 0,
                ["description"] = "Base layer of the mod",
            });

        File.WriteAllText(path, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
