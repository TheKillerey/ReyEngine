using System.Text.Json;

namespace ReyEngine.Core.Projects;

public static class ReyProjectService
{
    public const string Extension = ".reyproject";
    public const string FolderMetaDir = ".reyengine";
    public const string FolderMetaFile = "project.json";

    /// <summary>Open (or initialise) a project folder. Creates <c>.reyengine/project.json</c> if absent.</summary>
    public static ReyProject OpenFolder(string root)
    {
        var metaPath = Path.Combine(root, FolderMetaDir, FolderMetaFile);
        ReyProject project;
        bool isNew = !File.Exists(metaPath);
        if (!isNew)
        {
            project = JsonSerializer.Deserialize<ReyProject>(File.ReadAllText(metaPath)) ?? new ReyProject();
        }
        else
        {
            var scan = ProjectScanner.Scan(root);
            project = new ReyProject
            {
                Name = Path.GetFileName(root.TrimEnd('/', '\\')),
                ProjectWads = scan.Wads,
                ProjectFolders = scan.Folders,
                OutputDirectory = Path.Combine(root, "Build"),
                GameDirectory = ReyProject.GuessGameDirectory(),
                ProjectVersion = 1,
            };
        }
        project.RootPath = root;
        project.ProjectFilePath = metaPath;
        project.IsDirty = false;
        if (isNew) Save(project, metaPath);
        return project;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static ReyProject NewFromWad(string sourceWadPath)
    {
        return new ReyProject
        {
            Name = Path.GetFileNameWithoutExtension(sourceWadPath).Replace(".wad", "", StringComparison.OrdinalIgnoreCase),
            SourceWadPath = sourceWadPath,
            GameDirectory = ReyProject.GuessGameDirectory(),
            IsDirty = true,
        };
    }

    public static void Save(ReyProject project, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        project.ProjectFilePath = path;
        File.WriteAllText(path, JsonSerializer.Serialize(project, Options));
        project.IsDirty = false;
    }

    public static ReyProject Open(string path)
    {
        var project = JsonSerializer.Deserialize<ReyProject>(File.ReadAllText(path)) ?? new ReyProject();
        project.ProjectFilePath = path;
        project.IsDirty = false;
        return project;
    }
}
