using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Core.Projects;

/// <summary>M816: one table of names an imported .fantome declared (<c>Hashtables</c>), as the project keeps it.</summary>
/// <param name="File">The table's file, relative to <see cref="LtkProjectStore.RootOf"/>.</param>
/// <param name="Source">Where the package held it (<c>META/hashes/game.harvested.hashes.txt</c>).</param>
/// <param name="Category">The lookup domain: <c>game</c>, <c>binentries</c>, <c>binhashes</c>, or a spelling this tool does not know.</param>
/// <param name="Algorithm">The hash: <c>xxh64</c> or <c>fnv1a_32</c>, or a spelling this tool does not know.</param>
/// <param name="Bits">The declared key width.</param>
public sealed record StoredHashtable(string File, string Source, string Category, string Algorithm, int Bits)
{
    /// <summary>Whether its names can be taught to the hash database: WAD paths (<c>game</c>, <c>xxh64</c>, 64 bits), or bin names
    /// (<c>binentries</c> / <c>binhashes</c>, <c>fnv1a_32</c>, 32 bits). A table of another shape is kept for the package and
    /// not used.</summary>
    public bool IsWadPaths => Category == "game" && Algorithm == "xxh64" && Bits == 64;

    /// <inheritdoc cref="IsWadPaths"/>
    public bool IsBinNames => Category is "binentries" or "binhashes" && Algorithm == "fnv1a_32" && Bits == 32;

    public bool IsUsable => IsWadPaths || IsBinNames;
}

/// <summary>M816: a file an imported layer's GameData document names under <c>overrides</c> (a <c>.ptch</c>), as the project keeps it.</summary>
/// <param name="Path">The layer-relative path the declarations name it by, with '/' separators.</param>
/// <param name="FullPath">Where the project holds it.</param>
/// <param name="Length">Its size in bytes.</param>
public sealed record ImportedOverrideFile(string Path, string FullPath, long Length);

/// <summary>
/// M816: what an imported .fantome's layer declared, as the project holds it. The read side of <see cref="LtkProjectStore"/>,
/// which M817 (the apply engine) and M818 (the editor preview) read.
/// </summary>
public sealed class ImportedLayerData
{
    internal ImportedLayerData(string layer, string key, string directory, GameDataDocumentText document, IReadOnlyList<ImportedOverrideFile> files,
        IReadOnlyList<GameDataModuleText>? edits = null)
    {
        Layer = layer;
        Key = key;
        Directory = directory;
        Document = document;
        Files = files;
        Edits = edits ?? Array.Empty<GameDataModuleText>();
    }

    /// <summary>The project layer these declarations belong to - its CURRENT name.</summary>
    public string Layer { get; }

    /// <summary>The folder name under <c>.reyengine/ltk/game_data</c>; unchanged by a rename of the layer.</summary>
    public string Key { get; }

    /// <summary>The folder that holds <see cref="DocumentPath"/> and the override files.</summary>
    public string Directory { get; }

    /// <summary>The GameData document, as the package spelled it. <see cref="GameDataDocumentText.Modules"/> are its modules.</summary>
    public GameDataDocumentText Document { get; }

    /// <summary>The document's text - <c>Document.Text</c>, byte for byte what the package held.</summary>
    public string DocumentText => Document.Text;

    public string DocumentPath => System.IO.Path.Combine(Directory, LtkProjectStore.DeclarationsFileName);

    /// <summary>The modules in execution order (see <see cref="GameDataDocumentText.Modules"/>).</summary>
    public IReadOnlyList<GameDataModuleText> Modules => Document.Modules;

    /// <summary>
    /// M823: the modules ReyEngine keeps on top of this layer's imported ones (<see cref="LtkEditStore"/>) - one per bin the person edited - in the order they
    /// are applied, which is after every imported module of the layer. <see cref="Modules"/> is the package's alone and stays so: an export writes the imported
    /// document as it came, then these, then the declarations the planner makes of the project's own bins.
    /// </summary>
    public IReadOnlyList<GameDataModuleText> Edits { get; }

    /// <summary>M823: the first free place in the layer's numbering - the <c>origin.module</c> of the first module added after the imported and edit modules.</summary>
    public int OwnStart => Modules.Count + Edits.Count;

    /// <summary>The override files the layer carries, sorted by path. A document's <c>overrides</c> name them by
    /// <see cref="ImportedOverrideFile.Path"/>.</summary>
    public IReadOnlyList<ImportedOverrideFile> Files { get; }

    /// <summary>The bytes of an override file by its layer-relative path (compared without regard to case), or null.</summary>
    public byte[]? ReadFile(string path)
    {
        string wanted = path.Replace('\\', '/');
        var file = Files.FirstOrDefault(f => string.Equals(f.Path, wanted, StringComparison.OrdinalIgnoreCase));
        return file is null ? null : System.IO.File.ReadAllBytes(file.FullPath);
    }

    /// <summary>The document as a node. Number tokens are kept (<c>1.0</c> stays <c>1.0</c>); string escapes may be spelled
    /// differently.</summary>
    public JsonNode? ParseDocument() =>
        JsonNode.Parse(Document.Text, documentOptions: new JsonDocumentOptions { MaxDepth = GameDataDocumentText.MaxDepth });
}

/// <summary>
/// M816: the part of a project that came from an LTK-layered .fantome and belongs to no WAD folder - each layer's GameData
/// document and override files, the hashtables the package declared, and its README and license text. Everything is a file
/// under <c>&lt;project&gt;/.reyengine/ltk/</c>.
///
/// <code>
/// .reyengine/ltk/
///     game_data/&lt;key&gt;/declarations.json     a layer's GameData document, byte for byte as the package held it
///     game_data/&lt;key&gt;/files/&lt;path&gt;          its override files at their layer-relative paths (META/game_data/&lt;layer&gt;/&lt;path&gt;)
///     hashes/&lt;file&gt;.txt                     the declared hashtables, byte for byte
///     hashtables.json                       what each table declares (category, algorithm, width, where it came from)
///     meta/README.md, LICENSE               the package's own text files
/// </code>
///
/// <para><b>Why files under .reyengine, and not WAD content or project.json.</b> A GameData document is not a game file: packed
/// into a WAD it would be handed to the game as a chunk, and it never is one. <c>.reyengine/</c> is the folder that is already
/// the project's own, not the mod's: WAD packing, the folder mounts, the project scanner and Cleanup Project all skip it
/// (<c>WadPackService.EnumerateChunkFiles</c>, <c>FolderMount</c>, <c>CleanupScanner</c>), Cleanup only ever looks at the WAD
/// folders, and its own backups go to <c>.reyengine/cleanup</c> beside this. Not in project.json: a document is hundreds
/// of kilobytes (Crauzer's Winter Rift: 714 KB of info.json) that every project save would rewrite, and keeping it a separate
/// file is what keeps it byte-for-byte.</para>
///
/// <para>The layers' names, priorities, display names and string overrides ARE in project.json (<see cref="ProjectLayer"/>):
/// they are small, and the user edits them in Project Settings. A layer points at its folder here with
/// <see cref="ProjectLayer.DeclarationsKey"/>.</para>
///
/// <para><b>Survives:</b> saving and loading the project (the files are not in project.json, and the key is); Patch Update
/// (it rebases bins in WAD folders and writes <c>.reyengine/backups</c>); Cleanup Project (it scans the WAD folders and moves
/// files to <c>.reyengine/cleanup</c>; nothing here is a WAD file); copying the project folder (every path is relative).
/// Not covered: a user deleting the folder.</para>
/// </summary>
public static class LtkProjectStore
{
    public const string DirectoryName = "ltk";
    public const string DeclarationsFileName = "declarations.json";
    private const string GameDataDirectory = "game_data";
    private const string FilesDirectory = "files";
    private const string HashesDirectory = "hashes";
    private const string MetaDirectory = "meta";
    private const string HashtablesFileName = "hashtables.json";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary><c>&lt;project&gt;/.reyengine/ltk</c>.</summary>
    public static string RootOf(string projectRoot) =>
        System.IO.Path.Combine(projectRoot, ReyProjectService.FolderMetaDir, DirectoryName);

    /// <summary>The folder a layer's declarations live in.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not a folder name <see cref="KeyFor"/> could have made (project.json
    /// is a file a person can edit, and this folder is written to and read from).</exception>
    public static string DirectoryOf(string projectRoot, string key)
    {
        if (!IsSafeKey(key))
            throw new ArgumentException($"'{key}' is not a folder name a layer's declarations are kept under.", nameof(key));
        return System.IO.Path.Combine(RootOf(projectRoot), GameDataDirectory, key);
    }

    /// <summary>M818: the folder a layer's override files are stored in (<c>&lt;layer folder&gt;/files</c>), at their layer-relative paths. The overlay lists it and reads what a document names from it.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not a folder name <see cref="KeyFor"/> could have made.</exception>
    public static string FilesDirectoryOf(string projectRoot, string key) =>
        System.IO.Path.Combine(DirectoryOf(projectRoot, key), FilesDirectory);

    /// <summary>
    /// M816 review: whether <paramref name="key"/> is a name <see cref="KeyFor"/> could have made: one or more ASCII letters, digits, '-' and
    /// '_', which no Windows device name is. A key is a single folder name, so it holds no separator, no drive and no <c>..</c>.
    /// </summary>
    public static bool IsSafeKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 120) return false;
        foreach (char c in key)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')) return false;
        return !FantomeLayers.IsWindowsDeviceName(key);
    }

    /// <summary>
    /// A folder name for a layer's declarations that no earlier layer holds: the layer's name where that is a name a folder
    /// can have, else its unusable characters made into '_'; a Windows device name gets a trailing '_'; a name already in
    /// <paramref name="taken"/> (compared without regard to case) gets a number.
    /// </summary>
    public static string KeyFor(string layerName, ISet<string> taken)
    {
        var sb = new StringBuilder();
        foreach (char c in layerName.Trim())
            sb.Append(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' ? c : '_');
        string key = sb.Length == 0 ? "layer" : sb.ToString();
        if (key.Length > 100) key = key[..100];
        if (FantomeLayers.IsWindowsDeviceName(key)) key += "_";

        string candidate = key;
        for (int n = 2; taken.Any(t => string.Equals(t, candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = key + "-" + n.ToString(CultureInfo.InvariantCulture);
        taken.Add(candidate);
        return candidate;
    }

    // ===================================================== writing (the importer's side)

    /// <summary>Stores a layer's GameData document exactly as given. Returns the file's path.</summary>
    public static string WriteDeclarations(string projectRoot, string key, string documentText)
    {
        string dir = DirectoryOf(projectRoot, key);
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, DeclarationsFileName);
        System.IO.File.WriteAllBytes(path, Utf8.GetBytes(documentText));
        return path;
    }

    /// <summary>The longest name of one folder or file below a layer's stored files (NTFS allows 255 UTF-16 units to a name).</summary>
    public const int MaxSegmentLength = 255;

    /// <summary>
    /// Why <paramref name="relativePath"/> cannot be the layer-relative path of an override file, or null. The reader
    /// (<c>FantomeReader::extract_game_data</c>) skips an absolute path and one that climbs with <c>..</c>; this also refuses
    /// a drive, an empty segment and a backslash, since the file is written below a folder of this project.
    ///
    /// <para>M816 review (round 3): and a segment Windows cannot make as it is named - <see cref="FantomeLayers.SegmentProblem"/>: one that ends
    /// with a dot or a space (the file would be written under another name than the one the declarations spell), a device name (<c>nul</c>,
    /// <c>con.txt</c>), or one over <see cref="MaxSegmentLength"/> characters. Each of those used to throw when the file was written, after
    /// the whole WAD had been unpacked.</para>
    /// </summary>
    public static string? PathProblem(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return "the path is empty";
        if (relativePath.Contains('\\')) return "the path has a backslash";
        if (relativePath.StartsWith('/')) return "the path is absolute";
        foreach (string segment in relativePath.Split('/'))
        {
            if (segment.Length == 0) return "the path has an empty segment";
            if (segment is "." or "..") return "the path leaves the layer";
            if (segment.Contains(':')) return "the path names a drive";
            if (segment.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return "the path has a character a file name cannot hold";
            if (FantomeLayers.SegmentProblem(segment, MaxSegmentLength) is { } why)
                return $"the path has a segment that {why}";
        }
        return null;
    }

    /// <summary>Stores an override file at its layer-relative path. A path <see cref="PathProblem"/> refuses is not written.</summary>
    public static bool WriteOverrideFile(string projectRoot, string key, string relativePath, byte[] bytes)
    {
        if (PathProblem(relativePath) is not null) return false;
        string full = System.IO.Path.Combine(DirectoryOf(projectRoot, key), FilesDirectory, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, bytes);
        return true;
    }

    /// <summary>
    /// Stores the declared hashtables and what each declares. A table whose file name another already took gets a number.
    ///
    /// <para>M816 review (round 3): the file a table is kept in is the store's own, and its name is made safe - a table the package called
    /// <c>nul</c>, <c>con.txt</c>, <c>names.</c> or a name of 300 characters is kept as <c>table.txt</c> (numbered if need be), with its original
    /// path in the manifest's <c>Source</c>, because its names are what name the package's chunks. A table that still cannot be written (a
    /// disk that refuses, a path too long) is left out and said so in the returned problems - it no longer ends the import.</para>
    /// </summary>
    /// <returns>One sentence per table, or the manifest, that could not be stored; empty when everything was.</returns>
    public static IReadOnlyList<string> WriteHashtables(string projectRoot, IReadOnlyList<(FantomeInfoHashtable Entry, byte[] Bytes)> tables)
    {
        var problems = new List<string>();
        if (tables.Count == 0) return problems;
        string root = RootOf(projectRoot);
        string dir = System.IO.Path.Combine(root, HashesDirectory);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifest = new JsonArray();
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            foreach (var (entry, bytes) in tables)
            {
                string name = System.IO.Path.GetFileName(entry.Path.Replace('\\', '/'));
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                if (name.Length == 0 || FantomeLayers.SegmentProblem(name, MaxStoredTableNameLength) is not null) name = "table.txt";
                string unique = name;
                for (int n = 2; !taken.Add(unique); n++)
                    unique = System.IO.Path.GetFileNameWithoutExtension(name) + "-" + n.ToString(CultureInfo.InvariantCulture) + System.IO.Path.GetExtension(name);

                try { System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, unique), bytes); }
                catch (Exception ex) when (IsStoreFailure(ex))
                {
                    problems.Add($"The hashtable {entry.Path} could not be kept ({ex.Message}), so its names are not used when the project opens.");
                    continue;
                }
                manifest.Add(new JsonObject
                {
                    ["File"] = HashesDirectory + "/" + unique,
                    ["Source"] = entry.Path,
                    ["Category"] = entry.Category,
                    ["Algorithm"] = entry.Algorithm,
                    ["Bits"] = entry.Bits,
                });
            }
            if (manifest.Count > 0)
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, HashtablesFileName),
                    manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }), Utf8);
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            problems.Add($"The hashtables the package declared could not be kept ({ex.Message}), so their names are not used when the project opens.");
        }
        return problems;
    }

    /// <summary>The longest name a stored hashtable file is given (the folder it is in is below the project, which has a path of its own).</summary>
    private const int MaxStoredTableNameLength = 100;

    /// <summary>
    /// M816 review (round 3): whether <paramref name="ex"/> is the file system, or a path, refusing a write - the failures an import records
    /// as a Note and goes on from, as its WAD chunk loop does, and not the ones (a bug, an out-of-memory) it must not hide.
    /// </summary>
    public static bool IsStoreFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException
            or InvalidDataException;

    /// <summary>Stores one of the package's own text files (<c>README.md</c>, <c>LICENSE</c>) under a name.</summary>
    public static void WriteMetaFile(string projectRoot, string name, byte[] bytes)
    {
        string dir = System.IO.Path.Combine(RootOf(projectRoot), MetaDirectory);
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, System.IO.Path.GetFileName(name)), bytes);
    }

    // ===================================================== reading (M817 / M818 and the exports)

    /// <summary>
    /// The layers that imported declarations, in the order a loader applies them: the base layer first, then by priority and
    /// name as a person reads it. A layer whose <see cref="ProjectLayer.DeclarationsKey"/> names a missing document is left out.
    /// </summary>
    /// <param name="includeEdits">M823: whether the edits kept on top of each layer (<see cref="LtkEditStore"/>) are read too. An export reads them (a damaged file must not be left out of a package behind
    /// the person's back); the preview does not need to: it reads them itself, and a file it cannot use refuses the EDITS and leaves the package's own modules to preview.</param>
    public static IReadOnlyList<ImportedLayerData> ReadLayers(ReyProject project, bool includeEdits = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        var found = new List<ImportedLayerData>();
        foreach (var layer in project.Layers
                     .OrderBy(l => FantomeLayers.IsBase(l.Name) ? 0 : 1)
                     .ThenBy(l => l.Priority)
                     .ThenBy(l => l.Name, Comparer<string>.Create(FantomeLayers.NaturalCompare)))
            if (ReadLayer(project, layer, includeEdits) is { } data) found.Add(data);
        return found;
    }

    /// <summary>
    /// The text of a layer's stored GameData document, untouched and unparsed; null when the layer imported none (it has no
    /// <see cref="ProjectLayer.DeclarationsKey"/>, the key is not one, or the file is gone). For a reader that wants to decide what a document
    /// that is not JSON means - Cleanup Project counts it as a hole in what it can see - where <see cref="ReadLayer(ReyProject, string)"/>
    /// throws.
    /// </summary>
    /// <exception cref="IOException">The file could not be read.</exception>
    public static string? ReadDeclarationsText(ReyProject project, ProjectLayer layer)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layer);
        if (project.RootPath is null || !IsSafeKey(layer.DeclarationsKey)) return null;
        string document = System.IO.Path.Combine(DirectoryOf(project.RootPath, layer.DeclarationsKey!), DeclarationsFileName);
        return System.IO.File.Exists(document) ? Utf8.GetString(System.IO.File.ReadAllBytes(document)) : null;
    }

    /// <summary>
    /// M823: the text of the edits kept on top of a layer's GameData (<see cref="LtkEditStore"/>), untouched and unparsed; null when the layer keeps none. The edits are a GameData document in the same shape as
    /// <see cref="ReadDeclarationsText"/>, for a reader that wants to see what they name (Cleanup Project counts the assets they point at as used).
    /// </summary>
    /// <exception cref="IOException">The file could not be read, or holds more than <see cref="LtkEditStore.MaxFileBytes"/>.</exception>
    public static string? ReadEditsText(ReyProject project, ProjectLayer layer)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layer);
        if (project.RootPath is null || !IsSafeKey(layer.DeclarationsKey)) return null;
        string file = LtkEditStore.PathOf(project.RootPath, layer.DeclarationsKey!);
        return System.IO.File.Exists(file) ? LtkEditStore.ReadFileText(file) : null;
    }

    /// <summary>
    /// M819: whether the project stores a GameData document for any layer - a file exists where a layer's <see cref="ProjectLayer.DeclarationsKey"/> says. The cheap question the editor asks before it builds anything for GameData:
    /// a project with no layers (every project that never imported a layered .fantome) answers without touching the disk, and gets no preview, no overlay and no change. Nothing is read or parsed.
    /// </summary>
    public static bool HasGameData(ReyProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.RootPath is null || project.Layers.Count == 0) return false;
        foreach (var layer in project.Layers)
        {
            if (!IsSafeKey(layer.DeclarationsKey)) continue;
            try
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(DirectoryOf(project.RootPath, layer.DeclarationsKey!), DeclarationsFileName))) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return false;
    }

    /// <summary>One layer's imported declarations, by the layer's name (without regard to case); null when it imported none.</summary>
    public static ImportedLayerData? ReadLayer(ReyProject project, string layerName)
    {
        ArgumentNullException.ThrowIfNull(project);
        var layer = project.Layers.FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
        return layer is null ? null : ReadLayer(project, layer, includeEdits: true);
    }

    private static ImportedLayerData? ReadLayer(ReyProject project, ProjectLayer layer, bool includeEdits)
    {
        if (project.RootPath is null || string.IsNullOrWhiteSpace(layer.DeclarationsKey)) return null;
        if (!IsSafeKey(layer.DeclarationsKey)) return null;   // project.json is a file a person can edit: a key that is no folder name of ours names nothing

        string dir = DirectoryOf(project.RootPath, layer.DeclarationsKey);
        string document = System.IO.Path.Combine(dir, DeclarationsFileName);
        if (!System.IO.File.Exists(document)) return null;

        GameDataDocumentText text;
        try { text = GameDataDocumentText.Read(Utf8.GetString(System.IO.File.ReadAllBytes(document))); }
        catch (JsonException ex)
        {
            // the file is the project's own and a person can open it: say which one is broken instead of writing it into a package
            throw new InvalidDataException($"The GameData document of layer '{layer.Name}' ({document}) is not valid JSON: {ex.Message}", ex);
        }

        var files = new List<ImportedOverrideFile>();
        string filesDir = System.IO.Path.Combine(dir, FilesDirectory);
        if (System.IO.Directory.Exists(filesDir))
            foreach (string file in System.IO.Directory.EnumerateFiles(filesDir, "*", SearchOption.AllDirectories))
                files.Add(new ImportedOverrideFile(
                    System.IO.Path.GetRelativePath(filesDir, file).Replace('\\', '/'), file, new FileInfo(file).Length));
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        // M823: and the edits kept on top of it, which are the person's own file (a damaged one is said, by its name, and not dropped)
        return new ImportedLayerData(layer.Name, layer.DeclarationsKey, dir, text, files, includeEdits ? LtkEditStore.Read(project.RootPath, layer) : null);
    }

    /// <summary>The layer-relative paths of the override files stored for a layer key, with '/' separators.</summary>
    public static IReadOnlyList<string> ReadLayerFilePaths(string projectRoot, string key)
    {
        string filesDir = System.IO.Path.Combine(DirectoryOf(projectRoot, key), FilesDirectory);
        if (!System.IO.Directory.Exists(filesDir)) return Array.Empty<string>();
        return System.IO.Directory.EnumerateFiles(filesDir, "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(filesDir, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>The declared hashtables the project keeps; empty when the package declared none.</summary>
    public static IReadOnlyList<StoredHashtable> ReadHashtables(string projectRoot)
    {
        string manifest = System.IO.Path.Combine(RootOf(projectRoot), HashtablesFileName);
        if (!System.IO.File.Exists(manifest)) return Array.Empty<StoredHashtable>();
        try
        {
            var tables = new List<StoredHashtable>();
            if (JsonNode.Parse(System.IO.File.ReadAllText(manifest, Utf8)) is not JsonArray list) return tables;
            foreach (var item in list)
                if (item is JsonObject o
                    && o["File"]?.GetValue<string>() is { } file && o["Category"]?.GetValue<string>() is { } category
                    && o["Algorithm"]?.GetValue<string>() is { } algorithm && o["Bits"]?.GetValue<int>() is { } bits)
                    tables.Add(new StoredHashtable(file, o["Source"]?.GetValue<string>() ?? file, category, algorithm, bits));
            return tables;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException)
        {
            return Array.Empty<StoredHashtable>();   // a manifest a person broke is no names, not a project that will not open
        }
    }

    /// <summary>
    /// The names in one stored table, in file order. Null when the file is gone or the entry names a path outside the store.
    /// </summary>
    public static IReadOnlyList<string>? ReadTableNames(string projectRoot, StoredHashtable table)
    {
        if (table.File.Contains("..") || System.IO.Path.IsPathRooted(table.File)) return null;
        string path = System.IO.Path.Combine(RootOf(projectRoot), table.File.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(path)) return null;
        return FantomeHashtables.ReadNames(System.IO.File.ReadAllBytes(path), out _);
    }

    /// <summary>
    /// Teaches the hash database every name of the project's stored tables that it can use: WAD paths by the hash of their
    /// lowercased path (<c>AddWad(HashAlgorithms.WadPath(name), name)</c>), bin names by FNV-1a. Idempotent. Returns the
    /// number of names offered.
    ///
    /// <para>The database is not thread-safe, so this is for the thread that owns it - the app calls it when a project opens
    /// and again after the dictionary is swapped for a synced one.</para>
    /// </summary>
    public static int LoadHashtables(string projectRoot, HashDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        int offered = 0;
        foreach (var table in ReadHashtables(projectRoot))
        {
            if (!table.IsUsable || ReadTableNames(projectRoot, table) is not { } names) continue;
            foreach (string name in names)
            {
                if (table.IsWadPaths) database.AddWad(HashAlgorithms.WadPath(name), name);
                else database.AddBin(HashAlgorithms.Fnv1a(name), name);
                offered++;
            }
        }
        return offered;
    }

    /// <summary>The package's own text files the project keeps (entry name in a .fantome, file): <c>META/README.md</c>, <c>META/LICENSE</c>.</summary>
    public static IReadOnlyList<(string EntryName, string FullPath)> ReadMetaFiles(string projectRoot)
    {
        string dir = System.IO.Path.Combine(RootOf(projectRoot), MetaDirectory);
        if (!System.IO.Directory.Exists(dir)) return Array.Empty<(string, string)>();
        return System.IO.Directory.EnumerateFiles(dir)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => ("META/" + System.IO.Path.GetFileName(f), f))
            .ToList();
    }
}
