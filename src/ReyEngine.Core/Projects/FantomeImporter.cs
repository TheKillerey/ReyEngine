using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Core.Assets;

namespace ReyEngine.Core.Projects;

/// <summary>
/// M814: one layer of an LTK-layered .fantome whose content this import did NOT bring into the project.
/// </summary>
/// <param name="Name">The layer, spelled as the archive spells it (the <c>Layers</c> key when it has one, else the
/// <c>WAD_&lt;layer&gt;</c> directory).</param>
/// <param name="Wads">WADs stored in its <c>WAD_&lt;layer&gt;/</c> directory: one per packed file or raw folder.</param>
/// <param name="GameDataModules">Modules of its <c>Layers.&lt;name&gt;.GameData</c> declaration document: one per
/// declared game bin.</param>
public sealed record FantomeSkippedLayer(string Name, int Wads, int GameDataModules);

public sealed record FantomeImportResult(string RootPath, string ProjectName, int Wads, int ExtractedFiles, int RawFiles, int FailedChunks)
{
    /// <summary>
    /// M814: what the package held in LTK's layered layout that was not imported - empty for a package of the base
    /// layer's <c>WAD/</c> and <c>RAW/</c> alone, which is everything this import reads.
    ///
    /// <para>Export .fantome writes the layered layout (<c>WAD_&lt;layer&gt;/</c> for a layer other than base,
    /// <c>GameData</c> in <c>META/info.json</c> for game bins shipped as declarations), and LTK Manager reads it. The
    /// import is older: it reads <c>WAD/</c>, <c>RAW/</c> and the thumbnail only. Counting what it leaves is the
    /// difference between a layered mod importing as a smaller one without a word and importing with the omission
    /// said. Reading it is not done yet; nothing here converts it.</para>
    /// </summary>
    public IReadOnlyList<FantomeSkippedLayer> SkippedLayers { get; init; } = Array.Empty<FantomeSkippedLayer>();

    /// <summary>WADs found in <c>WAD_&lt;layer&gt;/</c> directories and not imported.</summary>
    public int LayerWads => SkippedLayers.Sum(l => l.Wads);

    /// <summary>Declared game bins found in <c>Layers.*.GameData</c> and not imported.</summary>
    public int GameDataModules => SkippedLayers.Sum(l => l.GameDataModules);

    /// <summary>The line to show the user, or null when nothing was left behind: which content was NOT imported,
    /// layer by layer, and that the project built from this import would ship without it.</summary>
    public string? NotImportedWarning
    {
        get
        {
            var left = SkippedLayers.Where(l => l.Wads > 0 || l.GameDataModules > 0).ToList();
            if (left.Count == 0) return null;

            static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
            var what = new List<string>();
            if (LayerWads > 0) what.Add($"{N(LayerWads)} WAD(s) stored in WAD_<layer>/ directories");
            if (GameDataModules > 0) what.Add($"{N(GameDataModules)} declared game bin(s) (Layers.*.GameData)");

            var perLayer = left.Select(l =>
            {
                var parts = new List<string>();
                if (l.Wads > 0) parts.Add($"{N(l.Wads)} WAD(s)");
                if (l.GameDataModules > 0) parts.Add($"{N(l.GameDataModules)} declared bin(s)");
                return $"{l.Name}: {string.Join(", ", parts)}";
            });

            return $"This .fantome uses LTK's layered layout, and the import reads only the base layer's WAD/ folders and RAW/. "
                 + $"NOT imported: {string.Join(" and ", what)} - {string.Join("; ", perLayer)}. "
                 + "ReyEngine does not read them yet - a later version will - so a mod built from this project would ship without them. "
                 + "The .fantome itself is untouched.";
        }
    }
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
/// <para><b>M814: LTK's layered layout is not imported.</b> Export .fantome writes layers other than base to
/// <c>WAD_&lt;layer&gt;/</c> and game bins shipped as declarations to <c>Layers.&lt;name&gt;.GameData</c> in
/// <c>META/info.json</c>. This import reads <c>WAD/</c>, <c>RAW/</c> and the thumbnail, as it always did, so it
/// leaves both behind - and now counts them (<see cref="FantomeImportResult.SkippedLayers"/>) so the caller can say
/// so instead of presenting a smaller mod as the whole one.</para>
/// </summary>
public static class FantomeImporter
{
    public static FantomeImportResult Import(string fantomePath, string projectsRoot, string? gameDirectory,
        IHashResolver resolver, IProgress<string>? progress = null)
    {
        using var zip = ZipFile.OpenRead(fantomePath);

        // ---- META/info.json → project identity ----
        string name = Path.GetFileNameWithoutExtension(fantomePath);
        string? author = null, version = null, description = null, heart = null, home = null;
        if (FindEntry(zip, "META/info.json") is { } info)
        {
            try
            {
                using var doc = JsonDocument.Parse(ReadAll(info));
                var r = doc.RootElement;
                if (r.TryGetProperty("Name", out var n) && n.GetString() is { Length: > 0 } nv) name = nv;
                author = r.TryGetProperty("Author", out var a) ? a.GetString() : null;
                version = r.TryGetProperty("Version", out var v) ? v.GetString() : null;
                description = r.TryGetProperty("Description", out var d) ? d.GetString() : null;
                heart = r.TryGetProperty("Heart", out var h) ? h.GetString() : null;
                home = r.TryGetProperty("Home", out var ho) ? ho.GetString() : null;
            }
            catch { /* malformed info.json — fall back to the file name */ }
        }

        string root = UniqueDir(projectsRoot, Sanitize(name));
        Directory.CreateDirectory(root);

        int wads = 0, extracted = 0, raw = 0, failed = 0;
        var folders = new List<string>();

        // ---- WAD/… → per-WAD project folders. A fantome ships each WAD one of two ways:
        //   packed    : WAD/Foo.wad.client is a single .wad.client FILE (unpack via WadArchive)
        //   raw folder: WAD/Foo.wad.client/ is a DIRECTORY of loose files already at resolved paths
        //               (the cslol "unpacked" layout, e.g. many HUD/UI mods) — just copy them across
        // Both end as root/<Foo>/ with files at their resolved paths, so downstream is identical.
        // Group WAD/ entries by the first segment after WAD/ to tell the two apart.
        var wadGroups = new Dictionary<string, List<ZipArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            string full = entry.FullName.Replace('\\', '/');
            if (!full.StartsWith("WAD/", StringComparison.OrdinalIgnoreCase)) continue;
            string rest = full["WAD/".Length..];
            if (rest.Length == 0) continue;
            int slash = rest.IndexOf('/');
            string wadName = slash < 0 ? rest : rest[..slash];   // "Foo.wad.client"
            if (!wadGroups.TryGetValue(wadName, out var list)) wadGroups[wadName] = list = new();
            list.Add(entry);
        }

        foreach (var (wadName, entries) in wadGroups)
        {
            string wadBase = wadName.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase)
                ? wadName[..^".wad.client".Length] : wadName;
            string outDir = Path.Combine(root, Sanitize(wadBase));

            // packed = a single file entry named exactly WAD/<wadName> with content
            var packedFile = entries.FirstOrDefault(e =>
                string.Equals(e.FullName.Replace('\\', '/'), $"WAD/{wadName}", StringComparison.OrdinalIgnoreCase)
                && e.Length > 0);

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
                progress?.Report($"Unpacking {wadName}…");
                string tmp = Path.Combine(Path.GetTempPath(), $"reyimport-{Guid.NewGuid():N}.wad.client");
                try
                {
                    packedFile.ExtractToFile(tmp, overwrite: true);
                    using var wad = WadArchive.Open(tmp, resolver);
                    // M298: a WAD that had to be repaired to be readable is worth saying so.
                    if (wad.RepairNote is { } repaired) progress?.Report($"{wadName}: {repaired}");

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
                            progress?.Report($"{wadName}: recovered real paths for {recovered.Count:n0} of "
                                + $"{unresolvedHashes.Count:n0} unnamed chunk(s) from the mod's .bin files");
                    }

                    int done = 0, sniffed = 0, anonymous = 0;
                    foreach (var we in wad.Entries)
                    {
                        try
                        {
                            string target;
                            if (we.IsResolved)
                                target = SafeTarget(outDir, we.Path);
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
                                if (++done % 500 == 0) progress?.Report($"Unpacking {wadBase}… {done:n0} chunks");
                                continue;
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            wad.ExtractToFile(we, target);
                            extracted++;
                        }
                        catch { failed++; }
                        if (++done % 500 == 0) progress?.Report($"Unpacking {wadBase}… {done:n0} chunks");
                    }
                    if (sniffed + anonymous > 0)
                        progress?.Report($"{wadName}: {sniffed:n0} chunk(s) with no known path were named from "
                            + $"their contents; {anonymous:n0} stayed .bin (type not recognised)");
                }
                catch (Exception ex)
                {
                    // One unreadable WAD must not abort the whole import - skip it, let the rest through.
                    progress?.Report($"Skipped {wadName}: {ex.Message}");
                    failed++;
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            else
            {
                // raw folder: copy the loose files, stripping the WAD/<wadName>/ prefix
                progress?.Report($"Copying {wadName} (raw folder)…");
                string prefix = $"WAD/{wadName}/";
                int done = 0;
                foreach (var e in entries)
                {
                    if (e.Length == 0) continue;   // directory placeholder
                    string full = e.FullName.Replace('\\', '/');
                    if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string rel = full[prefix.Length..];
                    if (rel.Length == 0) continue;
                    try
                    {
                        string target = SafeTarget(outDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        e.ExtractToFile(target, overwrite: true);
                        extracted++;
                    }
                    catch { failed++; }
                    if (++done % 500 == 0) progress?.Report($"Copying {wadBase}… {done:n0} files");
                }
            }

            if (Directory.Exists(outDir)) { wads++; folders.Add(Sanitize(wadBase)); }
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
            Directory.CreateDirectory(Path.GetDirectoryName(thumb)!);
            img.ExtractToFile(thumb, overwrite: true);
        }

        // ---- read-only Riot references: the same WADs from the game install ----
        var references = new List<string>();
        if (gameDirectory is not null && Directory.Exists(gameDirectory))
        {
            var installWads = GameInstallLocator.ListWads(gameDirectory);
            foreach (var folder in folders)
            {
                if (folder is "RAW") continue;
                // GameWad.Name is the base name WITHOUT the .wad.client suffix (e.g. "Katarina")
                var match = installWads.FirstOrDefault(w =>
                    string.Equals(w.Name, folder, StringComparison.OrdinalIgnoreCase));
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
        };
        if (RiotPatchVersionDetector.Detect(gameDirectory) is { } installed)
            project.RiotPatchVersion = RiotPatchVersionDetector.InferProjectBaseline(project.ModVersion, installed.Patch);
        ReyProjectService.Save(project, Path.Combine(root, ReyProjectService.FolderMetaDir, ReyProjectService.FolderMetaFile));

        // M814: LTK's layered layout is read by LTK Manager and not by this import - say what was left behind
        var layered = FindLayeredContent(zip);
        var result = new FantomeImportResult(root, name, wads, extracted, raw, failed) { SkippedLayers = layered };
        if (result.NotImportedWarning is not null)
            progress?.Report($"Not imported: {result.LayerWads} WAD(s) in WAD_<layer>/ and {result.GameDataModules} GameData bin(s)");
        return result;
    }

    /// <summary>
    /// M814: the layered content of a .fantome that <see cref="Import"/> does not read: the WADs of
    /// <c>WAD_&lt;layer&gt;/</c> directories and the modules of each <c>Layers.&lt;name&gt;.GameData</c> document
    /// (<c>ltk_fantome</c> 0.15.1, <c>ltk_game_data</c> 0.8: a document is <c>{version, modules:[...]}</c>, a module one
    /// declared bin). A layer is listed when it holds either; the base layer shows up only for its GameData,
    /// because its <c>WAD/</c> is what the import reads.
    ///
    /// <para>Tolerant by design: it runs after the import has written the project, so a package whose
    /// <c>info.json</c> is malformed, or whose <c>Layers</c> is not a table, simply reports what it can read.
    /// A directory entry is not a WAD; two entries of one WAD are one.</para>
    /// </summary>
    private static IReadOnlyList<FantomeSkippedLayer> FindLayeredContent(ZipArchive zip)
    {
        // layer (case-insensitive, as the format compares them) -> the spelling to show, distinct WADs, GameData modules
        var spelled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wads = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var modules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in zip.Entries)
        {
            string full = entry.FullName.Replace('\\', '/');
            if (!full.StartsWith("WAD_", StringComparison.OrdinalIgnoreCase) || full.EndsWith('/')) continue;
            int slash = full.IndexOf('/');
            if (slash < 0) continue;                              // "WAD_x" with no directory is not a layer's folder
            string layer = full["WAD_".Length..slash];
            string rest = full[(slash + 1)..];
            if (layer.Length == 0 || rest.Length == 0) continue;
            int next = rest.IndexOf('/');
            string wad = next < 0 ? rest : rest[..next];          // a packed file or the raw folder named like one
            spelled.TryAdd(layer, layer);
            if (!wads.TryGetValue(layer, out var set)) wads[layer] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(wad);
        }

        if (FindEntry(zip, "META/info.json") is { } info)
        {
            try
            {
                using var doc = JsonDocument.Parse(ReadAll(info));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("Layers", out var table) && table.ValueKind == JsonValueKind.Object)
                {
                    foreach (var layer in table.EnumerateObject())
                    {
                        if (layer.Value.ValueKind != JsonValueKind.Object
                            || !layer.Value.TryGetProperty("GameData", out var gameData) || gameData.ValueKind != JsonValueKind.Object
                            || !gameData.TryGetProperty("modules", out var list) || list.ValueKind != JsonValueKind.Array)
                            continue;
                        int count = list.GetArrayLength();
                        if (count == 0) continue;
                        spelled[layer.Name] = layer.Name;         // the table's spelling wins over a directory's
                        modules[layer.Name] = count;
                    }
                }
            }
            catch { /* a malformed info.json was already tolerated above; report what the directories show */ }
        }

        return spelled.Keys
            .OrderBy(k => FantomeLayerOrder(k), StringComparer.Ordinal)
            .Select(k => new FantomeSkippedLayer(
                spelled[k],
                wads.TryGetValue(k, out var w) ? w.Count : 0,
                modules.TryGetValue(k, out var m) ? m : 0))
            .ToList();
    }

    /// <summary>The base layer first, then the others in plain order - a stable order for a message and a test.</summary>
    private static string FantomeLayerOrder(string layer) =>
        string.Equals(layer, "base", StringComparison.OrdinalIgnoreCase) ? "\0" : layer.ToUpperInvariant();

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string path) =>
        zip.Entries.FirstOrDefault(e => string.Equals(
            e.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));

    private static string ReadAll(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

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
