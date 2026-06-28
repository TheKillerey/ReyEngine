using System.Text.Json.Serialization;

namespace ReyEngine.Core.Projects;

/// <summary>
/// A ReyEngine editing project: a source .wad.client plus a set of asset overrides that are
/// applied non-destructively when building an output package. Serialized as a .reyproject file.
/// </summary>
public sealed class ReyProject
{
    public string Name { get; set; } = "Untitled";
    public string? SourceWadPath { get; set; }
    public string? OutputDirectory { get; set; }
    public string? GameDirectory { get; set; }
    public string? HashDirectory { get; set; }
    public List<ProjectAssetOverride> Overrides { get; set; } = new();

    [JsonIgnore] public string? ProjectFilePath { get; set; }
    [JsonIgnore] public bool IsDirty { get; set; }

    [JsonIgnore]
    public string? WorkspaceDirectory =>
        ProjectFilePath is null ? null : System.IO.Path.GetDirectoryName(ProjectFilePath);

    [JsonIgnore]
    public string? OverridesDirectory =>
        WorkspaceDirectory is null ? null : System.IO.Path.Combine(WorkspaceDirectory, "overrides");

    public static string GuessGameDirectory()
    {
        string[] candidates =
        {
            @"C:\Riot Games\League of Legends\Game",
            @"D:\Riot Games\League of Legends\Game",
            @"C:\Program Files\Riot Games\League of Legends\Game",
        };
        foreach (var c in candidates)
            if (Directory.Exists(c)) return c;
        return "";
    }
}

/// <summary>One overridden chunk: its path hash + the on-disk replacement file.</summary>
public sealed class ProjectAssetOverride
{
    public ulong PathHash { get; set; }
    public string? ResolvedPath { get; set; }
    public string OverrideFile { get; set; } = "";
    public string AddedUtc { get; set; } = "";
}
