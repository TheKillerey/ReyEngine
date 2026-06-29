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

    // M11 project-folder editor.
    public int ProjectVersion { get; set; } = 1;
    /// <summary>The opened project folder (folder-mode). Null for legacy single-WAD projects.</summary>
    public string? RootPath { get; set; }
    /// <summary>Editable mod .wad.client files (relative to <see cref="RootPath"/>).</summary>
    public List<string> ProjectWads { get; set; } = new();
    /// <summary>Editable unpacked-WAD folders (relative to <see cref="RootPath"/>).</summary>
    public List<string> ProjectFolders { get; set; } = new();
    /// <summary>Read-only Riot reference WAD paths (absolute).</summary>
    public List<string> ReferenceWads { get; set; } = new();
    public List<string> RecentAssets { get; set; } = new();

    // M17 .fantome mod metadata.
    public string? ModName { get; set; }
    public string? ModAuthor { get; set; }
    public string ModVersion { get; set; } = "1.0.0";
    public string? ModDescription { get; set; }
    public string? ModHeart { get; set; }
    public string? ModHome { get; set; }
    public string? ThumbnailPath { get; set; }

    [JsonIgnore] public string EffectiveModName => string.IsNullOrWhiteSpace(ModName) ? Name : ModName!;
    [JsonIgnore] public bool IsFolderProject => RootPath is not null;
    [JsonIgnore] public string? ProjectFilePath { get; set; }
    [JsonIgnore] public bool IsDirty { get; set; }

    [JsonIgnore]
    public string? WorkspaceDirectory =>
        ProjectFilePath is null ? null : System.IO.Path.GetDirectoryName(ProjectFilePath);

    [JsonIgnore]
    public string? OverridesDirectory =>
        WorkspaceDirectory is null ? null : System.IO.Path.Combine(WorkspaceDirectory, "overrides");

    /// <summary>Absolute path of a project-relative entry (folder or WAD).</summary>
    public string ResolveProjectPath(string relativeOrAbsolute) =>
        System.IO.Path.IsPathRooted(relativeOrAbsolute) || RootPath is null
            ? relativeOrAbsolute
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(RootPath, relativeOrAbsolute));

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
