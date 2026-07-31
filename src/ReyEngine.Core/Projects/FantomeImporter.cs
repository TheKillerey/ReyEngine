using System.IO.Compression;
using System.Text.Json;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;

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

        var projectWads = new List<string>();   // M299: WADs kept packed, mounted as project WADs
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
                // M299: keep the WAD AS A WAD.
                //
                // This used to unpack every chunk to a loose file, which produced three problems the user
                // saw as one. It turned a 382 MB archive into 1.3 GB of files; it left the project looking
                // nothing like the mod that went in; and - the confusing part - every chunk whose path hash
                // the database cannot resolve was written as "<hash>.bin" at the tree root. On this mod
                // that was 756 of 3,954 files, and those are exactly the CUSTOM textures, because custom
                // paths are the ones a hash database is least likely to know. They still LOADED, since
                // assets resolve by path hash at runtime, so the map rendered with textures the user could
                // not find by name anywhere in their own project.
                //
                // A project WAD has none of those problems: paths keep their identity inside it, the
                // Content Browser lists it, the build repacks it, and the import is a copy rather than
                // 3,954 decompressions.
                progress?.Report($"Copying {wadName}…");
                string dest = Path.Combine(root, wadName);
                try
                {
                    using (var src = packedFile.Open())
                    using (var dst = File.Create(dest))
                        src.CopyTo(dst);

                    // Opened once, purely to report on it - a WAD that cannot be read is worth saying so
                    // at import rather than at first use, and the repair note (M298) belongs here too.
                    try
                    {
                        using var wad = WadArchive.Open(dest, resolver);
                        if (wad.RepairNote is { } repaired) progress?.Report($"{wadName}: {repaired}");
                        extracted += wad.Entries.Count;
                        int unresolved = wad.Entries.Count - wad.ResolvedCount;
                        if (unresolved > 0)
                            progress?.Report($"{wadName}: {unresolved:n0} of {wad.Entries.Count:n0} chunk(s) "
                                + "have no known path - they keep their hash inside the WAD and still load");
                    }
                    catch (Exception ex) { progress?.Report($"{wadName}: unreadable ({ex.Message})"); failed++; }

                    wads++;
                    projectWads.Add(wadName);
                }
                catch (Exception ex)
                {
                    // One bad WAD must not abort the whole import - skip it and let the rest through.
                    progress?.Report($"Skipped {wadName}: {ex.Message}");
                    failed++;
                }
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
                        string target = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        e.ExtractToFile(target, overwrite: true);
                        extracted++;
                    }
                    catch { failed++; }
                    if (++done % 500 == 0) progress?.Report($"Copying {wadBase}… {done:n0} files");
                }
            }

            if (packedFile is null && Directory.Exists(outDir)) { wads++; folders.Add(Sanitize(wadBase)); }
        }

        // ---- RAW/ → loose files, copied as-is (mount resolves them by relative path) ----
        foreach (var entry in zip.Entries)
        {
            string full = entry.FullName.Replace('\\', '/');
            if (!full.StartsWith("RAW/", StringComparison.OrdinalIgnoreCase) || entry.Length == 0) continue;
            string rel = full["RAW/".Length..];
            if (rel.Length == 0) continue;
            string target = Path.Combine(root, "RAW", rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            raw++;
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
            // M299: packed WADs no longer register a folder, so match against BOTH lists. Missing this
            // would silently drop the read-only Riot reference, and the symptom would be assets that
            // resolve from the mod but not from the game - i.e. a half-rendered map, blamed on the mod.
            var wadBaseNames = projectWads.Select(w =>
                w.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase)
                    ? w[..^".wad.client".Length] : w);
            foreach (var folder in folders.Concat(wadBaseNames))
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
            ProjectWads = projectWads,
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

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
