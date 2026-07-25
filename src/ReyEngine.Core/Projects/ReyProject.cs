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

    /// <summary>M171: texture recolours, stored as DESCRIPTIONS of the edit rather than as the edited
    /// files. Every one is re-derived from the pristine source, so re-opening a project and nudging a
    /// slider costs exactly one BC generation instead of one more each time.</summary>
    public List<TextureRecolorRecord> TextureRecolors { get; set; } = new();

    /// <summary>M132: pack only known game file types into wads — editor leftovers, notes, PSDs and
    /// other unknown extensions are skipped (each skip is logged). Default on.</summary>
    public bool PackKnownTypesOnly { get; set; } = true;

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

/// <summary>M171: one texture's recolour. The sliders are stored, NOT the recoloured pixels — the file
/// on disk is only ever a rendering of these numbers applied to the original texture.
///
/// <see cref="BaseSnapshot"/> matters when the project has no Riot reference WAD mounted to read the
/// original from: without a pristine base, re-editing would compound BC loss, so the first recolour
/// stashes a copy. When the reference IS available (the normal case) this stays null and costs nothing.</summary>
public sealed class TextureRecolorRecord
{
    public ulong PathHash { get; set; }
    public string AssetPath { get; set; } = "";
    /// <summary>Workspace-relative file holding the original bytes, when we had to keep our own copy.</summary>
    public string? BaseSnapshot { get; set; }

    public float HueDegrees { get; set; }
    public float Saturation { get; set; } = 1f;
    public float Brightness { get; set; } = 1f;
    public float Contrast { get; set; } = 1f;
    public float InputBlack { get; set; }
    public float InputWhite { get; set; } = 1f;
    public float Gamma { get; set; } = 1f;
    public float TintR { get; set; } = 1f;
    public float TintG { get; set; } = 1f;
    public float TintB { get; set; } = 1f;
    public float Strength { get; set; } = 1f;
}

/// <summary>One overridden chunk: its path hash + the on-disk replacement file.</summary>
public sealed class ProjectAssetOverride
{
    public ulong PathHash { get; set; }
    public string? ResolvedPath { get; set; }
    public string OverrideFile { get; set; } = "";
    public string AddedUtc { get; set; } = "";
}
