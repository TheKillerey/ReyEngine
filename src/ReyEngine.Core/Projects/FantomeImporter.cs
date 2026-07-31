using System.IO.Compression;
using System.Text.Json;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Core.Assets;

namespace ReyEngine.Core.Projects;

public sealed record FantomeImportResult(string RootPath, string ProjectName, int Wads, int ExtractedFiles, int RawFiles, int FailedChunks);

/// <summary>
/// M94: converts a .fantome mod package into an editable ReyEngine folder project. A fantome zip holds
/// META/info.json (name/author/version/description), WAD/… (mod WADs) and optionally RAW/ loose files +
/// META/image.png. A WAD ships either PACKED (<c>WAD/Foo.wad.client</c> is a .wad.client file) or as a
/// RAW FOLDER (<c>WAD/Foo.wad.client/…</c> is a directory of loose files already at resolved paths — the
/// cslol "unpacked" layout common to HUD/UI mods, M139). Both become the same per-WAD project folder;
/// RAW/ files are copied as-is, and same-named Riot WADs from the game install become read-only
/// references so everything else still resolves. Never touches the source .fantome.
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
            ProjectVersion = 1,
        };
        ReyProjectService.Save(project, Path.Combine(root, ReyProjectService.FolderMetaDir, ReyProjectService.FolderMetaFile));
        return new FantomeImportResult(root, name, wads, extracted, raw, failed);
    }

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
