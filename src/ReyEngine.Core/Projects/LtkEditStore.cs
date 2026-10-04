using System.Globalization;
using System.Text;
using System.Text.Json;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Core.Projects;

/// <summary>
/// M823: the edits a person made on top of an imported LTK package's GameData, as ReyEngine keeps them - ONE literal module per bin.
///
/// <para><b>Why a module and never the bin.</b> A bin the imported GameData targets is the game's bin with the package's declarations
/// applied to it. The editor shows it so (<c>GameDataPreview</c>), and saving what it shows as a file would hand LTK its own output
/// to apply the declarations to again (<c>+list</c> duplicates, a clone that already exists, a <c>-list</c> that finds nothing). So an edit of
/// such a bin is kept as the DIFFERENCE between that bin and the edited one: a module of literal values, <c>diff(B, E)</c> with <c>B</c> the
/// bin as LTK makes it from the game and the IMPORTED declarations alone. It runs after every imported module that touches the bin, so
/// at install it turns <c>B</c> into <c>E</c>; and as it states values and not a bin, it keeps applying after Riot's next patch.</para>
///
/// <para><b>Where it lives.</b> In the layer's own folder of the store (<see cref="LtkProjectStore"/>), beside the imported document and
/// apart from it - the imported document stays byte for byte as the package wrote it:</para>
/// <code>
/// .reyengine/ltk/game_data/&lt;key&gt;/declarations.json       the package's document, never written after the import
/// .reyengine/ltk/game_data/&lt;key&gt;/reyengine-edits.json    ReyEngine's modules for this layer: {"version":1,"modules":[...]}
/// </code>
/// <para>The layer is the LAST one, in LTK's apply order, that applies anything to the bin, so the module follows every imported module that
/// touches it. A module is written once per bin: saving the bin again replaces it, recomputed against the bin as it is now.
/// Each module is a document module - <c>target</c> and <c>edits</c> - whose <c>origin.module</c> is set when the document is composed
/// (<see cref="GameDataDocumentText.ModuleNode"/>), because it counts the modules in front.</para>
///
/// <para><b>The file is the person's own</b> and is opened by hand: it is read with a limit (<see cref="MaxFileBytes"/>), a file that cannot be read or is
/// not a document of this shape is an <see cref="InvalidDataException"/> that names it, and a write either happens whole or leaves the files as they were.</para>
/// </summary>
public static class LtkEditStore
{
    public const string FileName = "reyengine-edits.json";

    /// <summary>16 MiB: as much as a layer's whole GameData document may hold when the preview reads it. The edits are composed into that document, so a file past it could never be applied.</summary>
    public const long MaxFileBytes = 16L << 20;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The file a layer's edits live in. Safe: the key is proven to be a folder name of the store's own.</summary>
    /// <exception cref="ArgumentException"><paramref name="layerKey"/> is not a key <see cref="LtkProjectStore.KeyFor"/> could have made.</exception>
    public static string PathOf(string projectRoot, string layerKey) =>
        System.IO.Path.Combine(LtkProjectStore.DirectoryOf(projectRoot, layerKey), FileName);

    /// <summary>The chunk a module of this store is for: its <c>target</c> is a game path (hashed the way the loader hashes it) or the sixteen hexadecimal digits of the hash.</summary>
    public static bool TryChunkOf(GameDataModuleText module, out ulong chunk)
    {
        chunk = 0;
        string? target = module.Target;
        if (string.IsNullOrEmpty(target)) return false;
        if (target.Length == 16 && ulong.TryParse(target, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out chunk)) return true;
        chunk = HashAlgorithms.WadPath(target.Replace('\\', '/'));
        return true;
    }

    // ===================================================== reading

    /// <summary>
    /// The text of a store file, read with a limit and without making a writer of it wait: the file is replaced by renaming a new one over it, which a reader that holds it open for sharing must not stop.
    /// </summary>
    /// <exception cref="IOException">The file holds more than <paramref name="maxBytes"/>, or cannot be read.</exception>
    internal static string ReadFileText(string path, long maxBytes = MaxFileBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        long length = stream.Length;
        if (length > maxBytes) throw new IOException($"the file is {length:N0} bytes, more than the {maxBytes:N0} the editor reads");
        var buffer = new byte[length];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        // a file that grew after its length was read holds more than it said
        if (total == buffer.Length && stream.ReadByte() >= 0) throw new IOException($"the file is more than the {maxBytes:N0} bytes the editor reads");
        return Utf8.GetString(buffer, 0, total);
    }

    /// <summary>
    /// The modules ReyEngine keeps on top of one layer, in the order they are applied; empty when there are none.
    /// </summary>
    /// <param name="maxBytes">The most the file may hold: a longer one is not read.</param>
    /// <exception cref="InvalidDataException">The file cannot be read, is longer than <paramref name="maxBytes"/>, or is not a document of the store's shape: it names the file, because it is the person's own and
    /// is opened by hand - a package must not be written, nor an edit dropped, behind their back.</exception>
    public static IReadOnlyList<GameDataModuleText> Read(string projectRoot, ProjectLayer layer, long maxBytes = MaxFileBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        ArgumentNullException.ThrowIfNull(layer);
        if (!LtkProjectStore.IsSafeKey(layer.DeclarationsKey)) return Array.Empty<GameDataModuleText>();
        string path = PathOf(projectRoot, layer.DeclarationsKey!);
        if (!System.IO.File.Exists(path)) return Array.Empty<GameDataModuleText>();

        string text;
        try { text = ReadFileText(path, maxBytes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The edits kept on top of layer '{layer.Name}' ({path}) cannot be read: {ex.Message}", ex);
        }
        return Parse(text, path, layer);
    }

    /// <summary>
    /// The modules in the text of a layer's store file, which the caller read (with a limit of its own) from <paramref name="path"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The text is not a document of the store's shape; it names the file and the layer.</exception>
    public static IReadOnlyList<GameDataModuleText> Parse(string text, string path, ProjectLayer layer)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(layer);
        GameDataDocumentText document;
        try { document = GameDataDocumentText.Read(text); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The edits kept on top of layer '{layer.Name}' ({path}) are not valid JSON: {ex.Message}", ex);
        }
        if (!document.IsExpectedShape)
            throw new InvalidDataException($"The edits kept on top of layer '{layer.Name}' ({path}) are not a GameData document ({{\"version\", \"modules\"}}).");
        foreach (var module in document.Modules)
            if (!module.IsTarget)
                throw new InvalidDataException($"Module {module.Index} of the edits kept on top of layer '{layer.Name}' ({path}) has no target.");
        return document.Modules;
    }

    /// <summary>Whether the project keeps any edit on top of the GameData of any layer. Cheap: a file is looked for, none is read.</summary>
    public static bool Any(ReyProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.RootPath is null) return false;
        foreach (var layer in project.Layers)
        {
            if (!LtkProjectStore.IsSafeKey(layer.DeclarationsKey)) continue;
            try { if (System.IO.File.Exists(PathOf(project.RootPath, layer.DeclarationsKey!))) return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return false;
    }

    /// <summary>The layer that holds the edit for a chunk, and its module; null when none does.</summary>
    /// <exception cref="InvalidDataException">A layer's file is damaged (<see cref="Read"/>).</exception>
    public static (ProjectLayer Layer, GameDataModuleText Module)? Find(ReyProject project, ulong chunk)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.RootPath is null) return null;
        foreach (var layer in project.Layers)
            foreach (var module in Read(project.RootPath, layer))
                if (TryChunkOf(module, out ulong c) && c == chunk) return (layer, module);
        return null;
    }

    /// <summary>Every chunk the project keeps an edit for. Empty for a project that keeps none.</summary>
    /// <exception cref="InvalidDataException">A layer's file is damaged (<see cref="Read"/>).</exception>
    public static IReadOnlySet<ulong> Chunks(ReyProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var chunks = new HashSet<ulong>();
        if (project.RootPath is null) return chunks;
        foreach (var layer in project.Layers)
            foreach (var module in Read(project.RootPath, layer))
                if (TryChunkOf(module, out ulong c)) chunks.Add(c);
        return chunks;
    }

    // ===================================================== writing

    /// <summary>
    /// Keeps <paramref name="moduleText"/> as the edit for <paramref name="chunk"/> on top of <paramref name="layer"/>: it replaces the one the layer held for the chunk,
    /// in its place, or follows the others. A chunk has one edit in all: the one any other layer held is dropped, so a bin whose last layer changed is not edited twice.
    /// Either all of it happens or the files are as they were: the new module is written FIRST and the other layers' copies are taken away behind it, and a failure
    /// anywhere puts back what was already done.
    /// </summary>
    /// <param name="moduleText">The module as JSON: <c>target</c>, <c>edits</c> and the <c>origin</c> the document requires (its <c>module</c> is renumbered when the document is composed).</param>
    /// <exception cref="InvalidOperationException">The layer has no stored GameData document to follow, or the project has no folder.</exception>
    /// <exception cref="InvalidDataException">A layer's file is damaged (<see cref="Read"/>).</exception>
    /// <exception cref="IOException">A file could not be written; the files are as they were.</exception>
    public static void Set(ReyProject project, ProjectLayer layer, ulong chunk, string moduleText)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentException.ThrowIfNullOrEmpty(moduleText);
        if (project.RootPath is null || !LtkProjectStore.IsSafeKey(layer.DeclarationsKey))
            throw new InvalidOperationException($"Layer '{layer.Name}' keeps no GameData of an imported package, so there is nothing for an edit to follow.");

        var changes = new List<(string Path, IReadOnlyList<string> Modules)>();

        // the layer that keeps the edit: its module for the chunk replaced in its place, or added behind the others
        var modules = new List<string>();
        bool replaced = false;
        foreach (var module in Read(project.RootPath, layer))
        {
            if (TryChunkOf(module, out ulong c) && c == chunk)
            {
                if (!replaced) modules.Add(moduleText);
                replaced = true;
            }
            else modules.Add(module.Text);
        }
        if (!replaced) modules.Add(moduleText);
        string target = PathOf(project.RootPath, layer.DeclarationsKey!);
        changes.Add((target, modules));

        // and no other layer keeps one
        foreach (var other in project.Layers)
        {
            if (ReferenceEquals(other, layer) || !LtkProjectStore.IsSafeKey(other.DeclarationsKey)) continue;
            string path = PathOf(project.RootPath, other.DeclarationsKey!);
            if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) continue;
            var held = Read(project.RootPath, other);
            var kept = held.Where(m => !(TryChunkOf(m, out ulong c) && c == chunk)).Select(m => m.Text).ToList();
            if (kept.Count != held.Count) changes.Add((path, kept));
        }
        WriteAll(changes);
    }

    /// <summary>Takes the edit for <paramref name="chunk"/> away. True when the project kept one: the bin is then, on top of the package's GameData, what that makes of the game's.</summary>
    /// <exception cref="InvalidDataException">A layer's file is damaged (<see cref="Read"/>).</exception>
    /// <exception cref="IOException">A file could not be written; the files are as they were.</exception>
    public static bool Remove(ReyProject project, ulong chunk)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.RootPath is null) return false;
        var changes = new List<(string Path, IReadOnlyList<string> Modules)>();
        foreach (var layer in project.Layers)
        {
            if (!LtkProjectStore.IsSafeKey(layer.DeclarationsKey)) continue;
            string path = PathOf(project.RootPath, layer.DeclarationsKey!);
            if (changes.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var held = Read(project.RootPath, layer);
            var kept = held.Where(m => !(TryChunkOf(m, out ulong c) && c == chunk)).Select(m => m.Text).ToList();
            if (kept.Count != held.Count) changes.Add((path, kept));
        }
        WriteAll(changes);
        return changes.Count > 0;
    }

    /// <summary>
    /// The files written in order, a file with no module left deleted. A failure puts back every file already changed (best effort: what could not be put back is left, and the failure is the one that is thrown), so a caller never
    /// has a chunk's module taken from one layer and not written in the other.
    /// </summary>
    private static void WriteAll(IReadOnlyList<(string Path, IReadOnlyList<string> Modules)> changes)
    {
        var done = new List<(string Path, byte[]? Before)>();
        try
        {
            foreach (var (path, modules) in changes)
            {
                byte[]? before = System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
                if (modules.Count == 0)
                {
                    if (before is not null) System.IO.File.Delete(path);
                }
                else WriteBytes(path, Compose(modules));
                done.Add((path, before));
            }
        }
        catch
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                var (path, before) = done[i];
                try
                {
                    if (before is null) { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
                    else WriteBytes(path, before);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing more can be done for this one */ }
            }
            throw;
        }
    }

    private static byte[] Compose(IReadOnlyList<string> moduleTexts)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"version\": 1,\n  \"modules\": [\n");
        for (int i = 0; i < moduleTexts.Count; i++)
            sb.Append("    ").Append(moduleTexts[i]).Append(i + 1 < moduleTexts.Count ? ",\n" : "\n");
        sb.Append("  ]\n}\n");
        return Utf8.GetBytes(sb.ToString());
    }

    /// <summary>The file, written whole beside itself and moved into place, so that an interruption leaves the edits as they were.</summary>
    private static void WriteBytes(string path, byte[] bytes)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        try
        {
            System.IO.File.WriteAllBytes(temp, bytes);
            System.IO.File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { System.IO.File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
