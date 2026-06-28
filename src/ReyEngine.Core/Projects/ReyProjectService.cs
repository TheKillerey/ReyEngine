using System.Text.Json;

namespace ReyEngine.Core.Projects;

public static class ReyProjectService
{
    public const string Extension = ".reyproject";

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
