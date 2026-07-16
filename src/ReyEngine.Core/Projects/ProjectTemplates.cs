namespace ReyEngine.Core.Projects;

/// <summary>
/// M73: New Project templates (Unreal-style). A template pre-selects which WAD group the picker opens on
/// and which content categories are checked by default — it never restricts what the user may pick.
/// </summary>
public sealed record ProjectTemplate(
    string Id,
    string Icon,
    string Name,
    string Description,
    string PreferredWadGroup,          // wizard opens the WAD picker on this group ("" = no preference)
    string[] DefaultCategories)
{
    public static readonly IReadOnlyList<ProjectTemplate> All = new[]
    {
        new ProjectTemplate("champion", "🗡", "Custom Champion Skin",
            "Retexture or re-model a champion: skin materials, textures, meshes and animations from the champion's WAD.",
            "Champions",
            new[] { AssetCategories.Materials, AssetCategories.Textures, AssetCategories.Models, AssetCategories.Animations }),

        new ProjectTemplate("map", "🗺", "Custom Map",
            "Edit a map environment: geometry, materials, terrain textures, VFX definitions and ambient audio.",
            "Maps",
            new[] { AssetCategories.MapGeometry, AssetCategories.Materials, AssetCategories.Textures, AssetCategories.Audio }),

        new ProjectTemplate("vfx", "✨", "VFX / Particles",
            "Recolour or rework particle systems: the VFX definitions live in .bin files, the sprites in textures.",
            "Champions",
            new[] { AssetCategories.Materials, AssetCategories.Textures }),

        new ProjectTemplate("audio", "🔊", "Audio / Music",
            "Replace sounds, music or voice-over: Wwise banks (.bnk) and wem packs (.wpk) from champion or map WADs.",
            "Maps",
            new[] { AssetCategories.Audio }),

        new ProjectTemplate("ui", "🖥", "UI / HUD",
            "Modify HUD atlases, fonts and interface textures from the shared UI and Global WADs.",
            "Core & UI",
            new[] { AssetCategories.Textures, AssetCategories.Materials, AssetCategories.Other }),

        new ProjectTemplate("empty", "📦", "Empty Project",
            "Start from a clean project folder — add WAD references and content later from inside the editor.",
            "",
            System.Array.Empty<string>()),
    };
}

/// <summary>M73: content-category names + the extension → category classifier shared by wizard and creator.</summary>
public static class AssetCategories
{
    public const string Materials   = "Materials & Data (.bin)";
    public const string Textures    = "Textures";
    public const string Models      = "Models & Meshes";
    public const string Animations  = "Animations";
    public const string MapGeometry = "Map Geometry";
    public const string Audio       = "Audio";
    public const string Other       = "Other files";
    public const string Unresolved  = "Unresolved chunks";

    public static readonly string[] All =
        { Materials, Textures, Models, Animations, MapGeometry, Audio, Other, Unresolved };

    /// <summary>Classify a RESOLVED wad path by extension. Unresolved chunks are classified by the caller.</summary>
    public static string Classify(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".bin" => Materials,
            ".tex" or ".dds" or ".png" or ".jpg" => Textures,
            ".skn" or ".skl" or ".scb" or ".sco" => Models,
            ".anm" => Animations,
            ".mapgeo" => MapGeometry,
            ".bnk" or ".wpk" or ".ogg" or ".wem" => Audio,
            _ => Other,
        };
    }
}
