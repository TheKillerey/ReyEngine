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
    string? ThumbnailPath = null)
{
    /// <summary>
    /// M742: every layer this send writes, lowest priority first. Empty keeps the single-layer behaviour,
    /// where everything lands in <see cref="Layer"/>.
    ///
    /// <para>A layer the project no longer declares is LEFT where it is rather than deleted: the manager's
    /// own editor can add layers to a mod, and a send is not entitled to remove what it did not write.</para>
    /// </summary>
    public IReadOnlyList<LtkLayer> Layers { get; init; } = System.Array.Empty<LtkLayer>();

    /// <summary>M757: per layer, the <c>game_data.yaml</c> text to write at the layer's root
    /// (<c>content/&lt;layer&gt;/game_data.yaml</c>, where league-mod's project loader looks). A layer with
    /// declarations and no files is still written.</summary>
    public IReadOnlyDictionary<string, string> GameData { get; init; } = new Dictionary<string, string>();

    /// <summary>M816: files that sit in a layer's folder itself rather than in a WAD folder - the <c>.ptch</c> files an imported
    /// GameData document names under <c>overrides</c>, at the layer-relative paths it names them by
    /// (<c>content/&lt;layer&gt;/&lt;RelPath&gt;</c>, where league-mod's project loader resolves them).</summary>
    public IReadOnlyList<(string Layer, string RelPath, string AbsPath)> LayerFiles { get; init; } = System.Array.Empty<(string, string, string)>();

    /// <summary>M816: <c>license</c> of <c>mod.config.json</c> - a string, or <c>{name, url}</c> - when the project has one.</summary>
    public Projects.ProjectLicense? License { get; init; }

    /// <summary>M816: <c>tags</c>, <c>champions</c> and <c>maps</c> of <c>mod.config.json</c>. Each is written only when it has an
    /// entry, and a send leaves the config's own list alone otherwise (the manager's editor can set them).</summary>
    public IReadOnlyList<string> Tags { get; init; } = System.Array.Empty<string>();

    /// <inheritdoc cref="Tags"/>
    public IReadOnlyList<string> Champions { get; init; } = System.Array.Empty<string>();

    /// <inheritdoc cref="Tags"/>
    public IReadOnlyList<string> Maps { get; init; } = System.Array.Empty<string>();
}

/// <summary>M742: the layer list a project sends, base first and priority-ordered.</summary>
public static class LtkProjectLayers
{
    /// <summary>The text of the base layer a send declares when the project does not re-declare it. M816: an imported base layer
    /// (which a package gave declarations or a display name, and so is re-declared) is given the same text.</summary>
    public const string BaseDescription = "Base layer of the mod";

    /// <summary>
    /// Every layer a send declares: the project's own, plus "base", which always exists because a WAD
    /// folder no layer claims ships there. A project that names no layers yields base alone, which is what
    /// the format shipped with.
    /// </summary>
    /// <exception cref="InvalidOperationException">A layer name that cannot be a folder name (<see cref="FantomeLayers.PathProblem"/>): a
    /// send writes <c>content/&lt;layer&gt;/</c>, and the name may have come from a package (review). Only a name that is unsafe as a PATH
    /// is refused - a layer called "Particle Fix", which the M744 dialog allowed and a send has always written to
    /// <c>content/Particle Fix/</c>, still sends; it is the .fantome export (<see cref="ForFantome"/>) that needs the stricter
    /// <see cref="FantomeLayers.NameProblem"/>.</exception>
    public static IReadOnlyList<LtkLayer> Of(Projects.ReyProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        RefuseUnsafeNames(project);
        var layers = new List<LtkLayer>
        {
            new(Projects.ProjectLayer.BaseLayer, 0, BaseDescription),
        };
        foreach (var l in project.Layers)
        {
            if (string.IsNullOrWhiteSpace(l.Name)) continue;
            int at = layers.FindIndex(x => string.Equals(x.Name, l.Name, StringComparison.OrdinalIgnoreCase));
            var mapped = new LtkLayer(l.Name, l.Priority, l.Description) { DisplayName = l.DisplayName, StringOverrides = l.StringOverrides };
            if (at >= 0) layers[at] = mapped; else layers.Add(mapped);
        }
        return layers.OrderBy(l => l.Priority).ToList();
    }

    /// <summary>
    /// M816 review: every layer of the project is a folder name somewhere - <c>content/&lt;layer&gt;/</c> in a send (which deletes and writes
    /// there), a folder of the build output - and a name can come from a package. A layer whose name is unsafe as a PATH
    /// (<see cref="FantomeLayers.PathProblem"/>: a separator, a colon, <c>.</c> or <c>..</c>, a trailing dot or space, a device name, over a
    /// hundred characters, another casing of <c>base</c>) stops the table before anything is made from it. Exactly <c>base</c> passes (see
    /// <see cref="ForFantome"/>); an unnamed layer is left out by <see cref="Of"/> and is not a name to check.
    /// </summary>
    private static void RefuseUnsafeNames(Projects.ReyProject project)
    {
        foreach (var l in project.Layers)
        {
            if (string.IsNullOrWhiteSpace(l.Name)) continue;   // Of leaves an unnamed layer out
            if (FantomeLayers.PathProblem(l.Name) is { } problem)
                throw new InvalidOperationException(problem + " Rename the layer in Project > Project Settings.");
        }
    }

    /// <summary>
    /// M816 review: the same for the .fantome export, whose rule is the layout's: a layer is a directory called <c>WAD_&lt;layer&gt;/</c>, which
    /// <c>ltk_fantome</c> only recognises for ASCII letters, digits, '-' and '_' (<see cref="FantomeLayers.NameProblem"/>). A name the send
    /// writes - "Particle Fix" - is refused here, as it always was.
    /// </summary>
    private static void RefuseUnexportableNames(Projects.ReyProject project)
    {
        foreach (var l in project.Layers)
        {
            if (string.IsNullOrWhiteSpace(l.Name)) continue;   // Of leaves an unnamed layer out
            if (string.Equals(l.Name, Projects.ProjectLayer.BaseLayer, StringComparison.Ordinal)) continue;   // base re-declared: see ForFantome
            if (FantomeLayers.NameProblem(l.Name) is { } problem)
                throw new InvalidOperationException(problem + " Rename the layer in Project > Project Settings.");
        }
    }

    /// <summary>
    /// M816: <paramref name="options"/> with what a project that imported an LTK-layered .fantome adds: the license, tags, champions and
    /// maps its <c>mod.config.json</c> should name, the layers' manifests (<paramref name="manifests"/>, by layer) and the override
    /// files the imported declarations name. A project that never imported one changes nothing but the manifests it already had.
    /// </summary>
    public static LtkSendOptions ForSend(LtkSendOptions options, Projects.ReyProject project,
        IReadOnlyList<Projects.ImportedLayerData> imported, IReadOnlyDictionary<string, string> manifests)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(project);
        return options with
        {
            License = project.ModLicense,
            Tags = project.ModTags,
            Champions = project.ModChampions,
            Maps = project.ModMaps,
            GameData = manifests,
            LayerFiles = imported.SelectMany(d => d.Files.Select(f => (d.Layer, f.Path, f.FullPath))).ToList(),
        };
    }

    /// <summary>
    /// M814: the layer table Export .fantome writes - the SAME layers and priorities as <see cref="Of"/>
    /// (which Send to LTK Manager declares), with the base layer first and the others by priority and name,
    /// each carrying its declarations in <paramref name="gameData"/> when it has any.
    ///
    /// <para>A .fantome layer is a directory name (<c>WAD_&lt;layer&gt;/</c>), so its name has to be one the
    /// format reads back; <see cref="FantomeLayers.NameProblem"/> says which are not, and one that is not stops
    /// the export before anything is built. It is checked on the project's own layers, ahead of <see cref="Of"/>, whose rule is the looser
    /// one of a PATH (<see cref="FantomeLayers.PathProblem"/>): a layer called "Particle Fix" is sent and is not exported.</para>
    ///
    /// <para>One layer name passes unchecked: exactly <see cref="Projects.ProjectLayer.BaseLayer"/>, compared
    /// ordinally. <see cref="Of"/> lets a project re-declare its own base layer (to give it a priority or a
    /// description; <c>LtkWorkshopExporterTests.Redeclaring_base_replaces_it_rather_than_duplicating_it</c> pins
    /// it) and a send accepts that, so the export does too - base is the layer every mod has, and it is written
    /// under the name the format knows it by. Only another casing of it ("BASE", "Base") is refused, because
    /// that is not a spelling the project's folder routing (<see cref="Projects.ReyProject.LayerOf"/>) or the
    /// editor's base row ever produces.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">A layer name the format cannot carry, or declarations for a
    /// layer the project does not have.</exception>
    public static IReadOnlyList<FantomeLayer> ForFantome(
        Projects.ReyProject project, IReadOnlyDictionary<string, JsonNode>? gameData = null,
        IReadOnlyList<Projects.ImportedLayerData>? imported = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        RefuseUnexportableNames(project);

        var table = FantomeExporter.OrderLayers(Of(project).Select(l =>
            new FantomeLayer(l.Name, l.Priority, l.DisplayName) { StringOverrides = l.StringOverrides }));

        // M816: what an import stored for a layer rides it, ahead of whatever ReyEngine declares for it
        if (imported is { Count: > 0 })
            table = table.Select(l => imported.FirstOrDefault(i => i.Layer.Equals(l.Name, StringComparison.OrdinalIgnoreCase)) is { } data
                ? l with
                {
                    ImportedGameData = data.DocumentText,
                    OverrideFiles = data.Files.Select(f => new FantomeOverrideFile(f.Path, f.FullPath)).ToList(),
                    // M823: the edits made on top of it follow, numbered on from the imported modules
                    EditModules = data.Edits.Select((m, i) => GameDataDocumentText.ModuleNode(m.Text, data.Modules.Count + i)).ToList(),
                }
                : l).ToList();

        if (gameData is null || gameData.Count == 0) return table;

        foreach (string key in gameData.Keys)
            if (!table.Any(t => t.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Declarations were made for layer '{key}', which the project does not have.");
        return table
            .Select(l => gameData.FirstOrDefault(kv => kv.Key.Equals(l.Name, StringComparison.OrdinalIgnoreCase)) is { Value: { } doc }
                ? l with { GameData = doc }
                : l)
            .ToList();
    }
}

/// <param name="Name">Layer name, matching the content folder under <c>content/</c>.</param>
/// <param name="Priority">Lowest first; a higher number is applied over a lower one.</param>
public sealed record LtkLayer(string Name, int Priority, string Description)
{
    /// <summary>M816: <c>display_name</c> of the layer's <c>mod.config.json</c> entry; null writes none.</summary>
    public string? DisplayName { get; init; }

    /// <summary>M816: <c>string_overrides</c> (locale, field, text); null or empty writes none.</summary>
    public JsonObject? StringOverrides { get; init; }
}

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
        IReadOnlyList<(string Layer, string WadFolder, string RelPath, string AbsPath)> files,
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

        // M742: which layers this send owns. Files name their own; anything unnamed lands in o.Layer, so a
        // caller that knows nothing about layers behaves exactly as before.
        var layerNames = new List<string>();
        var spellings = new HashSet<string>(StringComparer.Ordinal);   // every spelling of a layer name that reaches a path, as it was written
        void Owns(string name)
        {
            spellings.Add(name);
            if (!layerNames.Contains(name, StringComparer.OrdinalIgnoreCase)) layerNames.Add(name);
        }
        foreach (var f in files) Owns(string.IsNullOrWhiteSpace(f.Layer) ? o.Layer : f.Layer);
        foreach (var name in o.GameData.Keys) Owns(name);   // M757: a layer may hold only declarations
        foreach (var (name, _, _) in o.LayerFiles) Owns(name);   // M816: and the files they name
        if (layerNames.Count == 0) Owns(o.Layer);

        // M816 review: a layer name is a folder name, and this deletes and writes below it. It can come from a package, and
        // Path.Combine(<mod>/content, "..\..") is the workshop itself while Path.Combine(<mod>/content, @"C:\Temp\x") is not below the
        // mod at all. Every name that becomes a folder - the layers of the files, of the declarations, of the override files and
        // the ones the config declares - is proven to be one (FantomeLayers.PathProblem: a name only has to be PATH-safe here, so a
        // layer called "Particle Fix" is sent as it always was), and its folder to lie below <mod>/content, BEFORE anything is created,
        // deleted or written, so a name refused here leaves the workshop exactly as it was.
        // (every spelling is proven, not just the first of those that name one folder: "BASE" beside "base" is the same folder on this file
        // system and still not a name a send writes)
        string contentRoot = Path.Combine(modFull, "content");
        var layerDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string spelling in spellings)
        {
            string dir = FantomeLayers.LayerDirectory(contentRoot, spelling);
            if (!layerDirs.ContainsKey(spelling)) layerDirs[spelling] = dir;
        }
        foreach (var declared in o.Layers)
            if (FantomeLayers.PathProblem(declared.Name) is { } why)
                throw new InvalidOperationException($"Layer '{declared.Name}' cannot be declared: {why}");
        string LayerRoot(string name) => layerDirs[string.IsNullOrWhiteSpace(name) ? o.Layer : name];

        Directory.CreateDirectory(modFolder);

        // Replace the layer's contents so a file deleted from the project disappears from the mod too.
        // Guarded twice: the delete is confined to content/<layer>, and on an UPDATE the folder had to
        // carry a mod.config.json to be matched at all, so this cannot be pointed at an arbitrary
        // directory that merely shares a name.
        int deleted = 0;
        foreach (string layer in layerNames)
        {
            string root = layerDirs[layer];
            if (Directory.Exists(root))
            {
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { deleted++; }
                Directory.Delete(root, recursive: true);
            }
            Directory.CreateDirectory(root);
        }

        int written = 0;
        long bytes = 0;
        foreach (var (fileLayer, wadFolder, rel, abs) in files)
        {
            ct.ThrowIfCancellationRequested();
            string layerRoot = LayerRoot(fileLayer);
            string dest = Path.Combine(layerRoot, MountFolderName(wadFolder), rel.Replace('/', Path.DirectorySeparatorChar));
            string destFull = Path.GetFullPath(dest);
            if (!FantomeLayers.IsStrictlyBelow(destFull, layerRoot))
                continue;   // a traversing relative path is skipped, not written
            Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
            File.Copy(abs, destFull, overwrite: true);
            written++;
            bytes += new FileInfo(destFull).Length;
        }

        // M757: the declarations, after the files - the layer folders were just recreated above
        foreach (var (layer, text) in o.GameData)
        {
            string path = Path.Combine(LayerRoot(layer), "game_data.yaml");
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            written++;
            bytes += new FileInfo(path).Length;
        }

        // M816: the override files the declarations name, at the layer-relative paths they spell them by. The relative path
        // is checked as the WAD files' is: one that climbs out of the layer is skipped, not written.
        foreach (var (layer, rel, abs) in o.LayerFiles)
        {
            ct.ThrowIfCancellationRequested();
            string layerRoot = LayerRoot(layer);
            string dest = Path.GetFullPath(Path.Combine(layerRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!FantomeLayers.IsStrictlyBelow(dest, layerRoot)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(abs, dest, overwrite: true);
            written++;
            bytes += new FileInfo(dest).Length;
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

        // M742: the project's layers, merged into whatever the file already declares - an entry the
        // manager or the user added by hand keeps its other fields, and a layer this project does not
        // know about is left alone.
        var declared = o.Layers.Count > 0
            ? o.Layers
            : new[] { new LtkLayer(o.Layer, 0, "Base layer of the mod") };

        // Built as a FRESH array of clones rather than edited in place: assigning a node that already has
        // a parent back onto that parent throws, so the first version of this merge updated a brand-new
        // mod fine and threw on every later send - the content shipped layered while the config still
        // advertised one layer, which is exactly how it was found.
        var layers = new JsonArray();
        if (cfg["layers"] is JsonArray existingLayers)
            foreach (var node in existingLayers)
                if (node is not null) layers.Add(node.DeepClone());
        foreach (var layer in declared)
        {
            var found = layers.OfType<JsonObject>().FirstOrDefault(j =>
                string.Equals(j["name"]?.GetValue<string>(), layer.Name, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                found = new JsonObject
                {
                    ["name"] = layer.Name,
                    ["priority"] = layer.Priority,
                    ["description"] = layer.Description,
                };
                layers.Add(found);
            }
            else
            {
                found["priority"] = layer.Priority;
                if (!string.IsNullOrWhiteSpace(layer.Description)) found["description"] = layer.Description;
            }
            // M816: an imported layer's display name and string overrides (ModProjectLayer's display_name and string_overrides)
            if (!string.IsNullOrWhiteSpace(layer.DisplayName)) found["display_name"] = layer.DisplayName;
            if (layer.StringOverrides is { Count: > 0 } overrides) found["string_overrides"] = overrides.DeepClone();
        }
        cfg["layers"] = layers;

        // M816: what an imported package said about itself. Written only when the project has it, so a send of a project that
        // never imported one leaves these fields as the config (or the manager's editor) has them. mod.config.json spells a
        // license's object with lowercase keys, unlike the .fantome's.
        if (o.License is { } license)
        {
            if (license.AsObject || !string.IsNullOrEmpty(license.Url))
            {
                var licenseObject = new JsonObject { ["name"] = license.Name };
                if (!string.IsNullOrEmpty(license.Url)) licenseObject["url"] = license.Url;
                cfg["license"] = licenseObject;
            }
            else cfg["license"] = license.Name;
        }
        if (o.Tags.Count > 0) cfg["tags"] = new JsonArray(o.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        if (o.Champions.Count > 0) cfg["champions"] = new JsonArray(o.Champions.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        if (o.Maps.Count > 0) cfg["maps"] = new JsonArray(o.Maps.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());

        File.WriteAllText(path, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
