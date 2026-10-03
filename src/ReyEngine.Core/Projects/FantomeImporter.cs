using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Core.Assets;

namespace ReyEngine.Core.Projects;

/// <summary>M816: one layer an import brought into the project.</summary>
/// <param name="Name">The layer, as the package spells it.</param>
/// <param name="Priority">Its priority (0 for the base layer, whatever the file says).</param>
/// <param name="DisplayName">Its display name, or null.</param>
/// <param name="Wads">WADs it holds: one per packed file or raw folder.</param>
/// <param name="GameDataModules">Modules of its GameData document, stored as they were written.</param>
/// <param name="OverrideFiles">Files its declarations name under <c>overrides</c>, stored byte for byte.</param>
public sealed record FantomeImportedLayer(string Name, int Priority, string? DisplayName, int Wads, int GameDataModules, int OverrideFiles);

public sealed record FantomeImportResult(string RootPath, string ProjectName, int Wads, int ExtractedFiles, int RawFiles, int FailedChunks)
{
    /// <summary>
    /// M816: the layers the package declared (and the <c>WAD_&lt;layer&gt;/</c> directories it holds), in the order a loader
    /// applies them. Empty for a package of <c>WAD/</c> and <c>RAW/</c> alone - the cslol layout - and for one whose only layer is a
    /// base with nothing to say.
    /// </summary>
    public IReadOnlyList<FantomeImportedLayer> Layers { get; init; } = Array.Empty<FantomeImportedLayer>();

    /// <summary>M816: WADs found in <c>WAD_&lt;layer&gt;/</c> directories - already counted in <see cref="Wads"/>.</summary>
    public int LayerWads => Layers.Where(l => !FantomeLayers.IsBase(l.Name)).Sum(l => l.Wads);

    /// <summary>M816: modules of the GameData documents stored, over every layer.</summary>
    public int GameDataModules => Layers.Sum(l => l.GameDataModules);

    /// <summary>M816: override files stored, over every layer.</summary>
    public int OverrideFiles => Layers.Sum(l => l.OverrideFiles);

    /// <summary>M816: names the package's hashtables gave (lines read from the tables it declared), usable for chunk names.</summary>
    public int TableNames { get; init; }

    /// <summary>M816: unpacked chunks whose name came from one of the package's hashtables.</summary>
    public int ChunksNamedByTables { get; init; }

    /// <summary>
    /// M816: what the import did not bring in, or brought in as something other than it was: a key of <c>info.json</c> that
    /// ReyEngine does not carry, a value of the wrong type, a hashtable of a shape that cannot name a chunk, an override file
    /// that belongs to no layer, a layer whose name no folder can carry (it is imported under a safe name, and the note says which).
    /// One plain sentence each. Empty when everything the package held is in the project - which is the case for every package
    /// <c>ltk_mod_project</c> writes.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The line to show the user, or null when nothing was left behind. Since M816 the layered layout is imported - layers,
    /// <c>WAD_&lt;layer&gt;/</c> folders, GameData documents and override files, hashtables, license, tags, maps and
    /// champions - so what remains here is the small print of <see cref="Notes"/>.
    /// </summary>
    public string? NotImportedWarning => Notes.Count == 0
        ? null
        : "Not everything in this .fantome was carried into the project: " + string.Join(" ", Notes)
          + " The .fantome itself is untouched.";
}

/// <summary>
/// M94: converts a .fantome mod package into an editable ReyEngine folder project. A fantome zip holds
/// META/info.json (name/author/version/description), WAD/… (mod WADs) and optionally RAW/ loose files +
/// META/image.png. A WAD ships either PACKED (<c>WAD/Foo.wad.client</c> is a .wad.client file) or as a
/// RAW FOLDER (<c>WAD/Foo.wad.client/…</c> is a directory of loose files already at resolved paths — the
/// cslol "unpacked" layout common to HUD/UI mods, M139). Both become the same per-WAD project folder;
/// RAW/ files are copied as-is, and same-named Riot WADs from the game install become read-only
/// references so everything else still resolves. Never touches the source .fantome.
///
/// <para><b>M816: LTK's layered layout is imported.</b> A package written by LTK's tools (<c>ltk_mod_project</c>, and
/// Export .fantome since M814) names its layers in <c>Layers</c>, keeps a layer other than base in <c>WAD_&lt;layer&gt;/</c>,
/// declares game-bin changes as that layer's <c>GameData</c> document, names the files of its packed WADs in the hashtables
/// <c>Hashtables</c> lists, and keeps the files those declarations name under <c>META/game_data/&lt;layer&gt;/</c>. All of it comes
/// into the project:</para>
/// <list type="bullet">
/// <item><description><b>Layers</b> are <see cref="ReyProject.Layers"/>, with priority, display name and string overrides. The
/// base layer is listed only when it has something to say.</description></item>
/// <item><description><b>WAD folders.</b> A base WAD is the folder <c>&lt;Wad&gt;</c>, as ever. A WAD of another layer is
/// <c>layers/&lt;layer&gt;/&lt;Wad&gt;</c>, which the layer claims whole (<see cref="ProjectLayer.Folders"/>,
/// <see cref="ReyProject.LayerOfFolder"/>): the leaf still names the WAD, so <c>WAD/Map11.wad.client</c> and
/// <c>WAD_winter/Map11.wad.client</c> are two folders and two WADs. The <c>layers</c> folder is renamed (<c>layers (2)</c>) if a
/// base WAD already has that name.</description></item>
/// <item><description><b>Names.</b> The tables are read before anything is unpacked and win over the dictionary, so a chunk the
/// author named comes out under that name; the files are kept with the project (<see cref="LtkProjectStore"/>) and taught to
/// the hash database whenever it opens.</description></item>
/// <item><description><b>GameData</b> documents and override files are stored byte for byte in <see cref="LtkProjectStore"/>, never in
/// a WAD folder; the export writes them back.</description></item>
/// <item><description><b>Metadata</b>: license (string or object, shape kept), tags, champions, maps, the generator, README and LICENSE
/// text.</description></item>
/// <item><description><b>Layer names are a package's text, and they become folders</b> (review): a name <see cref="FantomeLayers.NameProblem"/>
/// refuses - <c>..\..</c>, <c>C:\Temp\x</c>, <c>\\server\share</c>, <c>a/b</c>, <c>con</c>, <c>my layer</c> - is never put in the project as it is.
/// The layer is KEPT, with its GameData, priority and display name, under <see cref="FantomeLayers.SafeName"/> of it, and a Note says
/// so; the key its declarations are stored under (<see cref="LtkProjectStore.KeyFor"/>) is made from the safe name, and
/// <see cref="LtkProjectStore.DirectoryOf"/> refuses any key that is not such a name. Leaving the layer out was the other choice, and
/// dropped declarations a package's author wrote.</description></item>
/// <item><description><b>Every chunk is kept</b> (review): the project is made with <see cref="ReyProject.PackKnownTypesOnly"/> off, so an
/// export packs what the package held, a <c>.json</c>, <c>.gmesh</c> or <c>.txt</c> chunk included.</description></item>
/// </list>
///
/// <para><b>Checked</b> on Crauzer's <c>winter-rift-2025_0.3.0.fantome</c> (<c>ltk_mod_project</c> 0.16.2: 3 layers, 13 GameData
/// modules, 347 packed chunks): all 347 chunks come out under the names of the package's table, each layer's GameData is stored as the
/// exact text of <c>info.json</c> it came from, and Export .fantome writes the same text, the same layers and the same chunks back -
/// league-mod's own reader parses the export and <c>ltk_game_data</c> applies both packages to the same bytes over the installed game
/// (see the M816 commit). Synthetic packages pin what that one does not hold: the same WAD in two layers, override files, string
/// overrides, a license as a string and as an object, a layer of GameData alone, a cslol package.</para>
///
/// <para>Limits: GameData is stored, not applied (M817 applies it, M818 shows it); keys of <c>info.json</c> that no tool of
/// this family writes are listed in <see cref="FantomeImportResult.Notes"/> and not carried; a layer name no folder can carry is
/// imported under a safe name (the original is in the Notes, and an export cannot give it back); string overrides nested past 64 levels
/// are a Note and not read; hashtables that are not WAD paths or bin names
/// are kept and not used; an export writes the harvested table of the chunks it packs and not the tables the package declared.</para>
/// </summary>
public static class FantomeImporter
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    /// <summary>Where the WAD folders of layers other than base go, inside the project: <c>layers/&lt;layer&gt;/&lt;Wad&gt;</c>.</summary>
    public const string LayersFolder = "layers";

    /// <param name="resolver">Names for the chunks of packed WADs. The package's own hashtables are consulted first. A
    /// <see cref="HashDatabase"/> given here is also taught the package's names; the app's shared
    /// <see cref="WadPathResolver"/> is not changed from this (other threads read it): the app teaches it on its own thread when
    /// the project opens, from the tables the project keeps.</param>
    public static FantomeImportResult Import(string fantomePath, string projectsRoot, string? gameDirectory,
        IHashResolver resolver, IProgress<string>? progress = null)
    {
        using var zip = ZipFile.OpenRead(fantomePath);
        var notes = new List<string>();

        // ---- META/info.json → project identity ----
        string name = Path.GetFileNameWithoutExtension(fantomePath);
        FantomeInfoDocument? info = null;
        if (FindEntry(zip, "META/info.json") is { } infoEntry)
        {
            // ltk_fantome strips a UTF-8 byte order mark and surrounding whitespace before it parses. The text is decoded as it
            // always was (a StreamReader, which also knows the other byte order marks), and JsonDocument takes the whitespace.
            info = FantomeInfoDocument.TryRead(ReadAll(infoEntry));
            // a malformed info.json falls back to the file name, as it always has - and now says so
            if (info is null) notes.Add("META/info.json is not a JSON object, so the mod is named after the file and its layers, license, tags and the rest were not read.");
        }
        if (info?.Name is { Length: > 0 } nv) name = nv;
        string? author = info?.Author, version = info?.Version, description = info?.Description, heart = info?.Heart, home = info?.Home;
        if (info is not null)
        {
            notes.AddRange(info.Problems);
            if (info.UnknownKeys.Count > 0)
                notes.Add($"META/info.json has key(s) ReyEngine does not carry ({string.Join(", ", info.UnknownKeys)}), so an export leaves them out.");
        }

        string root = UniqueDir(projectsRoot, Sanitize(name));
        Directory.CreateDirectory(root);

        // ---- the layer table: what the file declares, plus a layer for every WAD_<layer>/ directory it does not ----
        IReadOnlyList<FantomeInfoLayer> declaredTable = Array.Empty<FantomeInfoLayer>();
        if (info is not null)
        {
            declaredTable = info.LayersInApplyOrder(out var droppedLayers);
            notes.AddRange(droppedLayers);
        }

        // M816 review: a layer name is the package's text, and it ends up a FOLDER name - content/<layer>/ in LTK Manager's workshop (which
        // Send to LTK Manager deletes and writes), a folder of the build output, WAD_<layer>/ in a .fantome. A table can spell one as
        // "..\..", "C:\Temp\x", "\\server\share", "a/b" or "con", and a name like that must never reach a path. The layer is KEPT - its
        // GameData, priority and display name are the package's content - under a name that is safe (FantomeLayers.SafeName), and the
        // Notes say which. (The other choice, leaving the layer out, would drop declarations a package author wrote.) The WAD_<layer>/
        // directories count too: a directory can be called CON.
        var directoryLayers = new List<string>();
        foreach (var entry in zip.Entries)
            if (TryPlace(entry.FullName.Replace('\\', '/'), out string directoryLayer, out _) && !FantomeLayers.IsBase(directoryLayer)
                && !directoryLayers.Contains(directoryLayer, StringComparer.OrdinalIgnoreCase))
                directoryLayers.Add(directoryLayer);
        var renamedLayers = SafeLayerNames(declaredTable, directoryLayers, notes);
        if (renamedLayers.Count > 0)
            declaredTable = declaredTable.Select(l => renamedLayers.TryGetValue(l.Name, out var safe) ? l with { Name = safe } : l).ToList();
        string Safe(string layerName) => renamedLayers.TryGetValue(layerName, out var safe) ? safe : layerName;

        var layerSpelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // lowercase layer -> the name as declared
        foreach (var l in declaredTable) layerSpelling[l.Name] = l.Name;

        // ---- sort the archive's entries: WAD/ (base), WAD_<layer>/ (the others) ----
        var groups = new List<WadGroup>();
        var groupByKey = new Dictionary<(string Layer, string Wad), WadGroup>();
        foreach (var entry in zip.Entries)
        {
            string full = entry.FullName.Replace('\\', '/');
            if (!TryPlace(full, out string layerDir, out string rest)) continue;
            int slash = rest.IndexOf('/');
            string wadName = slash < 0 ? rest : rest[..slash];   // "Foo.wad.client"
            string layer = FantomeLayers.IsBase(layerDir) ? FantomeLayers.Base
                : layerSpelling.TryGetValue(Safe(layerDir), out var spelled) ? spelled : Safe(layerDir);
            var key = (layer.ToLowerInvariant(), wadName.ToLowerInvariant());
            if (!groupByKey.TryGetValue(key, out var group))
            {
                groupByKey[key] = group = new WadGroup(layer, wadName);
                groups.Add(group);
            }
            group.Entries.Add((entry, rest));
        }
        foreach (var g in groups)
            if (!FantomeLayers.IsBase(g.Layer) && !layerSpelling.ContainsKey(g.Layer))
                layerSpelling[g.Layer] = g.Layer;   // a WAD_<layer>/ directory declares its layer at priority 0

        // the layers a loader applies, with the undeclared ones (priority 0) in
        var layerOrder = declaredTable.Select(l => (l.Name, l.Priority)).ToList();
        foreach (var undeclared in groups.Select(g => g.Layer).Where(l => !FantomeLayers.IsBase(l))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(l => !layerOrder.Any(o => string.Equals(o.Name, l, OIC))))
            layerOrder.Add((undeclared, 0));
        layerOrder = layerOrder
            .OrderBy(l => FantomeLayers.IsBase(l.Name) ? 0 : 1).ThenBy(l => l.Priority)
            .ThenBy(l => l.Name, Comparer<string>.Create(FantomeLayers.NaturalCompare)).ToList();

        // ---- the names the package's own hashtables give, before anything is unpacked ----
        var tableNames = new Dictionary<ulong, string>();
        var tableFiles = new List<(FantomeInfoHashtable Entry, byte[] Bytes)>();
        int tableNameCount = 0;
        if (info is not null && info.Hashtables.Count > 0)
        {
            progress?.Report("Reading the package's hashtables…");
            foreach (var table in info.Hashtables)
            {
                if (FindEntry(zip, table.Path) is not { } tableEntry)
                {
                    notes.Add($"The hashtable {table.Path} is listed in META/info.json but is not in the package, so its names were not read.");
                    continue;
                }
                byte[] bytes = ReadBytes(tableEntry);
                tableFiles.Add((table, bytes));
                var stored = new StoredHashtable("", table.Path, table.Category, table.Algorithm, table.Bits);
                var names = FantomeHashtables.ReadNames(bytes, out int rejected);
                tableNameCount += names.Count;
                if (rejected > 0)
                    notes.Add($"The hashtable {table.Path} has {N(rejected)} line(s) that are not names (printable ASCII, no backslash), which were left out.");
                if (stored.IsWadPaths)
                    foreach (string n in names) tableNames.TryAdd(HashAlgorithms.WadPath(n), n);
                else if (!stored.IsBinNames)
                    notes.Add($"The hashtable {table.Path} declares {table.Category}/{table.Algorithm}/{N(table.Bits)} bits, which cannot name a chunk here; it is kept with the project and not used.");
            }
            if (resolver is HashDatabase learning)
                foreach (var (hash, n) in tableNames) learning.AddWad(hash, n);   // the caller handed over a database of its own
        }
        var importResolver = new TableFirstResolver(tableNames, resolver);

        // ---- the folders the layers' WADs become ----
        var baseFolders = groups.Where(g => FantomeLayers.IsBase(g.Layer))
            .Select(g => Sanitize(WadBase(g.WadName))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string layersRoot = LayersFolder;
        for (int n = 2; baseFolders.Contains(layersRoot); n++) layersRoot = $"{LayersFolder} ({n})";
        // a layer's folder under layers/: its name, made safe for a folder and distinct from the other layers' (KeyFor also keeps a Windows
        // device name such as "nul" from being one)
        var layerFolderNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedLayerFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in layerOrder.Where(l => !FantomeLayers.IsBase(l.Name)))
            layerFolderNames[l.Name] = LtkProjectStore.KeyFor(l.Name, usedLayerFolders);

        int wads = 0, extracted = 0, raw = 0, failed = 0, named = 0;
        var folders = new List<string>();
        var wadsOfLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var layerFolderEntries = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // ---- WAD/… and WAD_<layer>/… → per-WAD project folders. A fantome ships each WAD one of two ways:
        //   packed    : WAD/Foo.wad.client is a single .wad.client FILE (unpack via WadArchive)
        //   raw folder: WAD/Foo.wad.client/ is a DIRECTORY of loose files already at resolved paths
        //               (the cslol "unpacked" layout, e.g. many HUD/UI mods) — just copy them across
        // Both end as a folder with files at their resolved paths, so downstream is identical.
        // Base first, then each layer in the order a loader applies them.
        foreach (var group in groups.OrderBy(g => layerOrder.FindIndex(o => string.Equals(o.Name, g.Layer, OIC))))
        {
            string wadName = group.WadName;
            var entries = group.Entries;
            string wadBase = WadBase(wadName);
            bool isBase = FantomeLayers.IsBase(group.Layer);
            string folderEntry = isBase
                ? Sanitize(wadBase)
                : $"{layersRoot}/{layerFolderNames[group.Layer]}/{Sanitize(wadBase)}";
            string outDir = Path.Combine(root, folderEntry.Replace('/', Path.DirectorySeparatorChar));
            string label = isBase ? wadName : $"{group.Layer}/{wadName}";

            // packed = a single file entry named exactly WAD/<wadName> with content
            ZipArchiveEntry? packedFile = null;
            foreach (var (candidate, candidateRest) in entries)
                if (string.Equals(candidateRest, wadName, OIC) && candidate.Length > 0) { packedFile = candidate; break; }

            if (packedFile is not null)
            {
                // M300: unpack, and give every chunk a name that says what it IS.
                //
                // M299 kept the WAD packed instead. That was the wrong reading of the request: the wanted
                // layout is the unpacked tree - assets/, data/, and hash-named files at the root for
                // chunks whose path is unknown.
                //
                // What actually made custom textures unfindable was never the unpacking, it was the
                // NAMING: every unresolved chunk was written ".bin". A hash database knows Riot's paths,
                // so the chunks it cannot name are precisely the mod's OWN assets - the files most worth
                // finding were the only ones disguised. 756 of 3,954 on the reported mod. They are now
                // sniffed from their magic bytes and written .dds / .tex / .scb / .anm / .png / .bnk,
                // which is what makes them show up as textures rather than as anonymous blobs.
                progress?.Report($"Unpacking {label}…");
                string tmp = Path.Combine(Path.GetTempPath(), $"reyimport-{Guid.NewGuid():N}.wad.client");
                try
                {
                    packedFile.ExtractToFile(tmp, overwrite: true);
                    using var wad = WadArchive.Open(tmp, importResolver);
                    // M298: a WAD that had to be repaired to be readable is worth saying so.
                    if (wad.RepairNote is { } repaired) progress?.Report($"{label}: {repaired}");

                    // M301: before writing anything, ask the mod's own .bin files to name the chunks the
                    // hash database could not. A custom asset is unknown to the database precisely BECAUSE
                    // it is custom, but the mod's bins reference it by literal path - so the real name is
                    // already in the archive. Recovers 200 of 756 on the reported mod, and they are
                    // overwhelmingly the textures (.dds/.tex) the user could not find.
                    var unresolvedHashes = wad.Entries.Where(e => !e.IsResolved)
                                                      .Select(e => e.PathHash).ToHashSet();
                    Dictionary<ulong, string> recovered = new();
                    if (unresolvedHashes.Count > 0)
                    {
                        progress?.Report($"Recovering names from {wadBase}'s own .bin files…");
                        // Only bins carry paths, so only chunks that COULD be one are read - unresolved, or
                        // resolved to a .bin. That is still a superset of every bin in the archive, so the
                        // result is identical, but it avoids decompressing the textures and meshes twice.
                        var binCandidates = wad.Entries
                            .Where(e => !e.IsResolved
                                     || e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                            .Select(e => e.PathHash);
                        recovered = WadPathRecovery.Recover(
                            binCandidates, unresolvedHashes,
                            h => { try { return wad.Extract(h); } catch { return null; } });
                        if (recovered.Count > 0)
                            progress?.Report($"{label}: recovered real paths for {recovered.Count:n0} of "
                                + $"{unresolvedHashes.Count:n0} unnamed chunk(s) from the mod's .bin files");
                    }

                    int done = 0, sniffed = 0, anonymous = 0;
                    foreach (var we in wad.Entries)
                    {
                        try
                        {
                            string target;
                            if (we.IsResolved)
                            {
                                target = SafeTarget(outDir, we.Path);
                                if (tableNames.ContainsKey(we.PathHash)) named++;
                            }
                            else if (recovered.TryGetValue(we.PathHash, out var real))
                                target = SafeTarget(outDir, SafeRelative(real));
                            else
                            {
                                // Read it once, name it from what it is, then write the same bytes.
                                var bytes = wad.Extract(we.PathHash);
                                string? ext = AssetTypeDetector.FileExtensionFromMagic(bytes);
                                if (ext is not null) sniffed++; else anonymous++;
                                target = Path.Combine(outDir, $"{we.PathHash:x16}.{ext ?? "bin"}");
                                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                                File.WriteAllBytes(target, bytes);
                                extracted++;
                                if (++done % 500 == 0) progress?.Report($"Unpacking {label}… {done:n0} chunks");
                                continue;
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            wad.ExtractToFile(we, target);
                            extracted++;
                        }
                        catch { failed++; }
                        if (++done % 500 == 0) progress?.Report($"Unpacking {label}… {done:n0} chunks");
                    }
                    if (sniffed + anonymous > 0)
                        progress?.Report($"{label}: {sniffed:n0} chunk(s) with no known path were named from "
                            + $"their contents; {anonymous:n0} stayed .bin (type not recognised)");
                }
                catch (Exception ex)
                {
                    // One unreadable WAD must not abort the whole import - skip it, let the rest through.
                    progress?.Report($"Skipped {label}: {ex.Message}");
                    failed++;
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            else
            {
                // raw folder: copy the loose files, stripping the <wadName>/ prefix
                progress?.Report($"Copying {label} (raw folder)…");
                string prefix = wadName + "/";
                int done = 0;
                foreach (var (e, rest) in entries)
                {
                    if (e.Length == 0) continue;   // directory placeholder
                    if (!rest.StartsWith(prefix, OIC)) continue;
                    string rel = rest[prefix.Length..];
                    if (rel.Length == 0) continue;
                    try
                    {
                        string target = SafeTarget(outDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        e.ExtractToFile(target, overwrite: true);
                        extracted++;
                    }
                    catch { failed++; }
                    if (++done % 500 == 0) progress?.Report($"Copying {label}… {done:n0} files");
                }
            }

            if (Directory.Exists(outDir))
            {
                wads++;
                folders.Add(folderEntry);
                wadsOfLayer[group.Layer] = wadsOfLayer.GetValueOrDefault(group.Layer) + 1;
                if (!isBase)
                {
                    if (!layerFolderEntries.TryGetValue(group.Layer, out var list)) layerFolderEntries[group.Layer] = list = new();
                    list.Add(folderEntry);
                }
            }
        }

        // ---- RAW/ → loose files, copied as-is (mount resolves them by relative path) ----
        foreach (var entry in zip.Entries)
        {
            string full = entry.FullName.Replace('\\', '/');
            if (!full.StartsWith("RAW/", StringComparison.OrdinalIgnoreCase) || entry.Length == 0) continue;
            string rel = full["RAW/".Length..];
            if (rel.Length == 0) continue;
            try
            {
                string target = SafeTarget(Path.Combine(root, "RAW"), rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                raw++;
            }
            catch { failed++; }
        }
        if (raw > 0) folders.Add("RAW");

        // ---- thumbnail ----
        string? thumb = null;
        if (FindEntry(zip, "META/image.png") is { } img)
        {
            thumb = Path.Combine(root, ReyProjectService.FolderMetaDir, "thumbnail.png");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(thumb)!);
                img.ExtractToFile(thumb, overwrite: true);
            }
            catch (Exception ex) when (LtkProjectStore.IsStoreFailure(ex))
            {
                // a damaged entry is a Note, like a WAD chunk that would not unpack - it must not end an import that has done all its work (review, round 3)
                thumb = null;
                notes.Add($"The package's META/image.png could not be kept ({ex.Message}), so the project has no thumbnail.");
            }
        }

        // ---- M816: GameData documents, override files, hashtables and the package's text files, kept with the project ----
        progress?.Report("Keeping the package's declarations…");
        var takenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keyOfLayer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var moduleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var overrideCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var notCarried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // "layer\npath" of an override file the package holds and this import could not keep
        foreach (var l in declaredTable.Where(l => l.GameDataText is not null))
        {
            string key = LtkProjectStore.KeyFor(l.Name, takenKeys);
            // M816 review (round 3): a write the file system refuses is a Note, as it is for a WAD chunk - not the end of an import that has
            // already unpacked every WAD
            try { LtkProjectStore.WriteDeclarations(root, key, l.GameDataText!); }
            catch (Exception ex) when (LtkProjectStore.IsStoreFailure(ex))
            {
                notes.Add($"The GameData of layer '{l.Name}' could not be kept ({ex.Message}), so the layer was imported without it.");
                continue;
            }
            keyOfLayer[l.Name] = key;
            try
            {
                var doc = GameDataDocumentText.Read(l.GameDataText!);
                moduleCounts[l.Name] = doc.Modules.Count;
                if (!doc.IsExpectedShape)
                    notes.Add($"The GameData of layer '{l.Name}' is not {{version, modules}} as ltk_game_data reads it; it is kept as it is and an export writes it back unchanged.");
            }
            catch (System.Text.Json.JsonException) { moduleCounts[l.Name] = 0; }
        }

        foreach (var entry in zip.Entries)
        {
            string full = entry.FullName.Replace('\\', '/');
            if (full.EndsWith('/') || !full.StartsWith("META/game_data/", OIC)) continue;
            string tail = full["META/game_data/".Length..];
            int slash = tail.IndexOf('/');
            if (slash <= 0 || slash == tail.Length - 1)
            {
                notes.Add($"The file {full} is under META/game_data/ but not in a layer's folder, so it was not carried.");
                continue;
            }
            string layerDir = Safe(tail[..slash]), rel = tail[(slash + 1)..];   // a folder of a renamed layer is found by the new name
            if (!layerSpelling.TryGetValue(layerDir, out var layerName) && !declaredTable.Any(l => string.Equals(l.Name, layerDir, OIC)))
            {
                notes.Add($"The override file {full} belongs to layer '{tail[..slash]}', which the package does not declare, so it was not carried.");
                continue;
            }
            layerName ??= layerDir;
            if (!keyOfLayer.TryGetValue(layerName, out var layerKey))
            {
                notes.Add($"The override file {full} belongs to layer '{layerName}', which has no GameData document naming it, so it was not carried.");
                continue;
            }
            // the file is checked (a segment Windows cannot make as it is named is a Note) and written under the same guard as a WAD chunk:
            // a write the file system refuses must not end an import that has already unpacked every WAD (review, round 3)
            string? refused = LtkProjectStore.PathProblem(rel);
            if (refused is null)
            {
                try
                {
                    if (LtkProjectStore.WriteOverrideFile(root, layerKey, rel, ReadBytes(entry)))
                        overrideCounts[layerName] = overrideCounts.GetValueOrDefault(layerName) + 1;
                    continue;
                }
                catch (Exception ex) when (LtkProjectStore.IsStoreFailure(ex)) { refused = ex.Message; }
            }
            notes.Add($"The override file {full} was not carried: {refused}.");
            notCarried.Add(layerName + "\n" + rel);
        }

        // a module that names an override file the package does not hold is a layer LTK Manager refuses (InputMissing): say so
        // (one the package holds and this import could not keep has its own Note already)
        foreach (var l in declaredTable.Where(l => keyOfLayer.ContainsKey(l.Name)))
        {
            GameDataDocumentText doc;
            try { doc = GameDataDocumentText.Read(l.GameDataText!); } catch (System.Text.Json.JsonException) { continue; }
            var held = new HashSet<string>(
                LtkProjectStore.ReadLayerFilePaths(root, keyOfLayer[l.Name]), StringComparer.OrdinalIgnoreCase);
            foreach (string wanted in doc.OverridePaths())
                if (!held.Contains(wanted) && !notCarried.Contains(l.Name + "\n" + wanted))
                    notes.Add($"Layer '{l.Name}' declares the override file '{wanted}', which the package does not hold, so LTK Manager would refuse the layer.");
        }

        notes.AddRange(LtkProjectStore.WriteHashtables(root, tableFiles));
        foreach (var (entryName, storeName) in new[]
                 {
                     ("META/README.md", "README.md"), ("README.md", "README.md"),
                     ("META/LICENSE", "LICENSE"), ("META/LICENSE.md", "LICENSE.md"), ("META/LICENSE.txt", "LICENSE.txt"),
                 })
        {
            // LTK reads META/README.md, or a root README.md when META/ has none
            if (storeName == "README.md" && File.Exists(Path.Combine(LtkProjectStore.RootOf(root), "meta", storeName))) continue;
            if (FindEntry(zip, entryName) is not { } textEntry) continue;
            try { LtkProjectStore.WriteMetaFile(root, storeName, ReadBytes(textEntry)); }
            catch (Exception ex) when (LtkProjectStore.IsStoreFailure(ex))
            {
                notes.Add($"The package's {entryName} could not be kept ({ex.Message}), so an export leaves it out.");
            }
        }

        // ---- the project's layers ----
        var projectLayers = new List<ProjectLayer>();
        var summaries = new List<FantomeImportedLayer>();
        foreach (var (layerName, priority) in layerOrder)
        {
            var declared = declaredTable.FirstOrDefault(l => string.Equals(l.Name, layerName, OIC));
            bool isBase = FantomeLayers.IsBase(layerName);
            bool hasData = declared is not null && (declared.GameDataText is not null || declared.DisplayName is not null || declared.StringOverrides is not null);
            int layerWads = wadsOfLayer.GetValueOrDefault(layerName);
            if (declared is not null && declared.UnknownKeys.Count > 0)
                notes.Add($"Layer '{layerName}' has key(s) ReyEngine does not carry ({string.Join(", ", declared.UnknownKeys)}), so an export leaves them out.");

            // every project has a base layer; it is listed only when the package gave it something to keep
            if (isBase && !hasData) continue;
            projectLayers.Add(new ProjectLayer
            {
                Name = layerName,
                Priority = isBase ? 0 : priority,
                // a base layer that is re-declared to carry what a package gave it has no text of its own: a send declares the usual one
                Description = isBase ? LtkProjectLayers.BaseDescription : "",
                DisplayName = declared?.DisplayName,
                StringOverrides = declared?.StringOverrides,
                DeclarationsKey = keyOfLayer.GetValueOrDefault(layerName),
                Folders = layerFolderEntries.GetValueOrDefault(layerName) ?? new List<string>(),
            });
            summaries.Add(new FantomeImportedLayer(layerName, isBase ? 0 : priority, declared?.DisplayName, layerWads,
                moduleCounts.GetValueOrDefault(layerName), overrideCounts.GetValueOrDefault(layerName)));
        }

        // ---- read-only Riot references: the same WADs from the game install ----
        var references = new List<string>();
        if (gameDirectory is not null && Directory.Exists(gameDirectory))
        {
            var installWads = GameInstallLocator.ListWads(gameDirectory);
            foreach (var folder in folders)
            {
                if (folder is "RAW") continue;
                // GameWad.Name is the base name WITHOUT the .wad.client suffix (e.g. "Katarina"); M816: a layer's folder is
                // layers/<layer>/<Wad>, and it is the WAD's name that matches
                string wadFolder = folder.Contains('/') ? folder[(folder.LastIndexOf('/') + 1)..] : folder;
                var match = installWads.FirstOrDefault(w =>
                    string.Equals(w.Name, wadFolder, StringComparison.OrdinalIgnoreCase));
                if (match is not null && !references.Contains(match.Path)) references.Add(match.Path);
            }
        }

        progress?.Report("Writing project…");
        var project = new ReyProject
        {
            Name = name,
            RootPath = root,
            ProjectFolders = folders,
            ReferenceWads = references,
            GameDirectory = gameDirectory,
            OutputDirectory = Path.Combine(root, "Build"),
            ModName = name,
            ModAuthor = author,
            ModVersion = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version!,
            ModDescription = description,
            ModHeart = heart,
            ModHome = home,
            ThumbnailPath = thumb,
            ProjectVersion = ReyProjectService.CurrentProjectVersion,
            Layers = projectLayers,
            // M816 review: an import keeps every chunk of the package. The default (true) packs only the file types the game's own WADs
            // carry (WadPackService.KnownGameExtensions), which is right for a folder a person fills with sources and notes and wrong for
            // one that is a package's content: a re-export would drop a .json, .gmesh or .txt chunk the author shipped - and Send to LTK
            // Manager, which does not filter, would still send it. The project's setting is the user's to change.
            PackKnownTypesOnly = false,
            ModLicense = info?.License,
            ModTags = info?.Tags.ToList() ?? new List<string>(),
            ModChampions = info?.Champions.ToList() ?? new List<string>(),
            ModMaps = info?.Maps.ToList() ?? new List<string>(),
            ImportedGenerator = info?.Generator,
        };
        if (RiotPatchVersionDetector.Detect(gameDirectory) is { } installed)
            project.RiotPatchVersion = RiotPatchVersionDetector.InferProjectBaseline(project.ModVersion, installed.Patch);
        ReyProjectService.Save(project, Path.Combine(root, ReyProjectService.FolderMetaDir, ReyProjectService.FolderMetaFile));

        var result = new FantomeImportResult(root, name, wads, extracted, raw, failed)
        {
            Layers = summaries,
            TableNames = tableNameCount,
            ChunksNamedByTables = named,
            Notes = notes,
        };
        if (result.GameDataModules > 0 || result.LayerWads > 0 || result.OverrideFiles > 0)
            progress?.Report($"Layers: {result.Layers.Count} - {result.GameDataModules} GameData module(s), {result.LayerWads} layer WAD(s), {result.OverrideFiles} override file(s)");
        if (result.NotImportedWarning is not null) progress?.Report("Not everything was carried into the project - see the log.");
        return result;
    }

    /// <summary>
    /// M816 review: the layers of the package whose names <see cref="FantomeLayers.NameProblem"/> refuses (the declared ones, in the order
    /// a loader applies them, then the <c>WAD_&lt;layer&gt;/</c> directories no table declares), each with the name it is imported under
    /// (<see cref="FantomeLayers.SafeName"/>). A name that is fine is not in the answer. The names already in use - the base layer, every
    /// declared name that is fine, every directory name that is fine - are never handed out, so a renamed layer cannot be taken for another
    /// one, or pick up the WADs of a directory it has nothing to do with. Each rename is a Note.
    /// </summary>
    private static Dictionary<string, string> SafeLayerNames(IReadOnlyList<FantomeInfoLayer> declared, IReadOnlyList<string> directoryLayers, List<string> notes)
    {
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FantomeLayers.Base };
        foreach (var l in declared)
            if (!FantomeLayers.IsBase(l.Name) && FantomeLayers.NameProblem(l.Name) is null) taken.Add(l.Name);
        foreach (string d in directoryLayers)
            if (FantomeLayers.NameProblem(d) is null) taken.Add(d);

        foreach (string name in declared.Select(l => l.Name).Concat(directoryLayers))
        {
            if (FantomeLayers.IsBase(name) || renamed.ContainsKey(name) || FantomeLayers.NameProblem(name) is not { } problem) continue;
            string safe = FantomeLayers.SafeName(name, taken);
            renamed[name] = safe;
            notes.Add(Printable($"The package names a layer '{Brief(name)}', which cannot be a layer name here ({Brief(problem, 240)}) - it was imported as '{safe}', "
                + $"with the content, priority and display name it had; an export writes it under '{safe}'."));
        }
        return renamed;
    }

    /// <summary>A package's text cut to a length a log line can hold (a name can be as long as the package likes).</summary>
    private static string Brief(string text, int max = 80) => text.Length <= max ? text : text[..max] + "...";

    /// <summary>A package's text for a log line: a control character (a name can hold a line break) shows as '?'.</summary>
    private static string Printable(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) sb.Append(char.IsControl(c) ? '?' : c);
        return sb.ToString();
    }

    private sealed class WadGroup(string layer, string wadName)
    {
        public string Layer { get; } = layer;
        public string WadName { get; } = wadName;
        public List<(ZipArchiveEntry Entry, string Tail)> Entries { get; } = new();
    }

    /// <summary>The package's own tables first, the caller's resolver second - the order <c>ltk_mod_project</c>'s importer names chunks in:
    /// the author's table is the authority on the names the author invented.</summary>
    private sealed class TableFirstResolver(IReadOnlyDictionary<ulong, string> own, IHashResolver fallback) : IHashResolver
    {
        public bool TryGetPath(ulong hash, out string path)
        {
            if (own.TryGetValue(hash, out path!)) return true;
            return fallback.TryGetPath(hash, out path);
        }

        public string ResolvePath(ulong hash) => TryGetPath(hash, out var p) ? p : $"0x{hash:x16}.unknown";
    }

    /// <summary>
    /// Where an archive entry belongs, by its name, as <c>ltk_fantome</c>'s <c>classify_entry</c> places it: <c>WAD/</c> is the base
    /// layer's WAD directory, <c>WAD_&lt;layer&gt;/</c> another layer's (a name of ASCII letters, digits, '-' and '_'; <c>base</c> in
    /// any casing is not a layer). The prefixes match without regard to case. <paramref name="rest"/> is the path below the directory;
    /// false for a directory record or an entry outside both.
    /// </summary>
    private static bool TryPlace(string full, out string layerDir, out string rest)
    {
        layerDir = rest = "";
        if (full.StartsWith("WAD/", OIC))
        {
            layerDir = FantomeLayers.Base;
            rest = full["WAD/".Length..];
            return rest.Length > 0;
        }
        if (full.StartsWith("WAD_", OIC))
        {
            string after = full["WAD_".Length..];
            int slash = after.IndexOf('/');
            if (slash <= 0) return false;
            string dir = after[..slash];
            if (!FantomeLayers.IsLayerName(dir) || FantomeLayers.IsBase(dir)) return false;
            layerDir = dir;
            rest = after[(slash + 1)..];
            return rest.Length > 0;
        }
        return false;
    }

    private static string WadBase(string wadName) =>
        wadName.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase) ? wadName[..^".wad.client".Length] : wadName;

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string ReadAll(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>An entry's bytes. M816 review: the buffer starts empty and grows with what is read. The length the archive's header declares is
    /// the package's claim - a few bytes of header can say two gigabytes - and league-mod's <c>read_entry</c> sizes nothing from it either.</summary>
    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string path) =>
        zip.Entries.FirstOrDefault(e => string.Equals(
            e.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));

    private static string UniqueDir(string parent, string name)
    {
        string root = Path.Combine(parent, name);
        int i = 2;
        while (Directory.Exists(root)) root = Path.Combine(parent, $"{name} ({i++})");
        return root;
    }

    /// <summary>M301: turn a path recovered from archive BYTES into a safe relative path.
    ///
    /// <para>A resolved path comes from the hash database and is trusted. A recovered one is a printable run
    /// scraped out of a .bin, so it is attacker-shaped input: "..\..\windows\system32\x.dll" hashes just
    /// as happily as a real path, and combining it unchecked would write outside the project. Each segment
    /// is scrubbed and any traversal segment dropped.</para></summary>
    private static string SafeRelative(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var clean = new List<string>(parts.Length);
        foreach (var raw in parts)
        {
            if (raw is "." or "..") continue;
            string seg = Sanitize(raw);                 // strips ':' too, so a drive letter cannot survive
            if (seg.Length > 0) clean.Add(seg);
        }
        return clean.Count == 0 ? "recovered.bin" : Path.Combine(clean.ToArray());
    }

    private static string SafeTarget(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Archive entry has a rooted path: {relativePath}");

        string fullRoot = Path.GetFullPath(root);
        string target = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)
                        .Replace('\\', Path.DirectorySeparatorChar)));
        string rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(rootPrefix, comparison))
            throw new InvalidDataException($"Archive entry escapes its destination: {relativePath}");

        return target;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim();
        return name is "" or "." or ".." ? "_" : name;
    }
}
