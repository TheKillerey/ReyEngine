using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;

namespace ReyEngine.Core.Projects;

/// <summary>One WAD picked in the wizard + which content categories to extract from it (M73).</summary>
public sealed record WadSelection(string WadPath, IReadOnlyCollection<string> Categories);

/// <summary>Everything the New Project wizard collected (M73).</summary>
public sealed record ProjectCreationSpec(
    string ProjectName,
    string Location,               // parent folder; the project is created at Location/ProjectName
    string? Author,
    string GameDirectory,          // chosen install (Live/PBE) — becomes the read-only fallback source
    IReadOnlyList<WadSelection> Wads);

public sealed record ProjectCreationResult(string RootPath, int ExtractedFiles, int FailedChunks);

/// <summary>
/// M73: materialises a wizard spec into a ready-to-edit folder project: extracts the selected content
/// categories from each chosen WAD into unpacked folders (cslol-style ASSETS/DATA layout — editable
/// ProjectFolders), keeps the WADs as read-only Riot references so everything else still resolves, and
/// writes .reyengine/project.json. Never deletes anything; per-chunk failures are counted, not fatal.
/// </summary>
public static class ProjectCreator
{
    public static ProjectCreationResult Create(ProjectCreationSpec spec, IHashResolver resolver, IProgress<string>? progress = null)
    {
        string root = Path.Combine(spec.Location, Sanitize(spec.ProjectName));
        Directory.CreateDirectory(root);

        int extracted = 0, failed = 0;
        var folders = new List<string>();
        var references = new List<string>();

        foreach (var sel in spec.Wads)
        {
            if (!File.Exists(sel.WadPath)) continue;
            references.Add(sel.WadPath);
            if (sel.Categories.Count == 0) continue;   // reference-only WAD, nothing extracted

            string wadBase = Path.GetFileName(sel.WadPath);
            if (wadBase.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase)) wadBase = wadBase[..^".wad.client".Length];
            string outDir = Path.Combine(root, Sanitize(wadBase));

            progress?.Report($"Extracting {wadBase}…");
            using var wad = WadArchive.Open(sel.WadPath, resolver);
            var wanted = new HashSet<string>(sel.Categories, StringComparer.OrdinalIgnoreCase);
            int done = 0;
            foreach (var entry in wad.Entries)
            {
                string category = entry.IsResolved ? AssetCategories.Classify(entry.Path) : AssetCategories.Unresolved;
                if (!wanted.Contains(category)) continue;
                try
                {
                    string target = entry.IsResolved
                        ? Path.Combine(outDir, entry.Path.Replace('/', Path.DirectorySeparatorChar))
                        : Path.Combine(outDir, $"{entry.PathHash:x16}.bin");   // loose chunk — FolderMount resolves it
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    wad.ExtractToFile(entry, target);
                    extracted++;
                }
                catch { failed++; }   // subchunked/corrupt chunks must never kill project creation
                if (++done % 500 == 0) progress?.Report($"Extracting {wadBase}… {done:n0} checked, {extracted:n0} written");
            }
            if (Directory.Exists(outDir)) folders.Add(Sanitize(wadBase));
        }

        progress?.Report("Writing project…");
        var project = new ReyProject
        {
            Name = spec.ProjectName,
            RootPath = root,
            ProjectFolders = folders,
            ReferenceWads = references,
            GameDirectory = spec.GameDirectory,
            OutputDirectory = Path.Combine(root, "Build"),
            ModName = spec.ProjectName,
            ModAuthor = spec.Author,
            ProjectVersion = 1,
        };
        ReyProjectService.Save(project, Path.Combine(root, ReyProjectService.FolderMetaDir, ReyProjectService.FolderMetaFile));
        return new ProjectCreationResult(root, extracted, failed);
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
