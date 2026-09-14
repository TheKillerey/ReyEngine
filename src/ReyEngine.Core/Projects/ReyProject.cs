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

    // M308: automatic Riot-patch rebasing. This is the base version the editable project currently
    // targets, not the mod's marketing version. New projects capture it from the selected game install;
    // legacy projects can infer it once from a patch-style ModVersion such as 16.10.0.
    public string? RiotPatchVersion { get; set; }
    public bool AutoUpdateOnRiotPatch { get; set; } = true;
    public bool AutoBuildAfterPatchUpdate { get; set; } = true;
    public string? LastPatchUpdateUtc { get; set; }
    public string? LastPatchUpdateSummary { get; set; }
    public string? LastPatchBackupDirectory { get; set; }
    public bool PatchUpdateNeedsReview { get; set; }

    /// <summary>M171: texture recolours, stored as DESCRIPTIONS of the edit rather than as the edited
    /// files. Every one is re-derived from the pristine source, so re-opening a project and nudging a
    /// slider costs exactly one BC generation instead of one more each time.</summary>
    public List<TextureRecolorRecord> TextureRecolors { get; set; } = new();

    /// <summary>M287: per-map lighting the USER authored - sun/sky, the baked-light scale, the Light.dat
    /// fit sliders, and the point-light table itself. Keyed by mapgeo, because two maps in one project
    /// have nothing to say to each other about lighting.
    ///
    /// <para>The lights are stored RESOLVED rather than as a path to re-parse. A Light.dat cannot express
    /// a light's per-light intensity or its name (LightDatFile folds intensity into the 0-255 colour), so
    /// re-reading the file on open would silently mangle every light the user tuned - and lights added
    /// after the import were never in a file at all.</para></summary>
    public List<MapLightingRecord> MapLighting { get; set; } = new();

    /// <summary>M600: what the legacy map port was last run with, so a re-port repeats the same port
    /// instead of silently falling back to defaults.
    ///
    /// <para>Nothing recorded these before. The wizard seeded itself from
    /// <c>LegacyPortCleanupOptions</c>/<c>LegacyPortShaderOptions</c> defaults every time, so the seven
    /// cleanup flags, the decal-plane choice, the alignment correction and the per-role shaders lived only
    /// in the dialog and were gone the moment it closed. Re-running a port to change ONE option meant
    /// re-deriving the other twelve from memory, and getting one wrong produced a different map with no
    /// diagnostic saying so - a ported Map453 was re-run to turn decal planes off and there was no record
    /// of which cleanup flags the first run had used.</para>
    ///
    /// <para>Null means this project has never completed a port; the wizard then opens on its defaults as
    /// before. Only a port that actually ran writes this, so a cancelled dialog changes nothing.</para></summary>
    public LegacyPortSettings? LegacyPort { get; set; }

    /// <summary>M730: what the editor did to a project bin, so a Riot patch update can do it again to the new
    /// original instead of carrying the old result across - see <see cref="BinRecipeRecord"/>. Empty for every
    /// project saved before this; the updater infers a recipe from such a bin the first time it sees one.</summary>
    public List<BinRecipeRecord> BinRecipes { get; set; } = new();

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

    /// <summary>
    /// M470: which LTK Manager workshop mod "Send to LTK Manager" targets, by its <c>mod.config.json</c>
    /// name. Null until the first send, which records whatever it used.
    ///
    /// <para>Needed because a workshop slug is NOT derivable from the mod name. Measured on the user's own
    /// workshop: the folder is <c>oldriftday</c> while the display name is "Old Summoner's Rift - Day",
    /// which slugifies to <c>old-summoners-rift-day</c> — so name-matching alone would CREATE a duplicate
    /// on a mod the user has been shipping for months, which is precisely the case "update if it already
    /// exists" has to handle. Set this to the existing slug to adopt a mod that predates ReyEngine.</para>
    /// </summary>
    public string? LtkWorkshopSlug { get; set; }

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

/// <summary>M287: one map's authored lighting. Defaults match the view-model's own initial values, so a
/// record written by an older build - or a hand-edited one missing a field - restores to what the editor
/// would have shown anyway rather than to zero.</summary>
/// <summary>
/// M515: is this record the artefact of the capture bug rather than something a person chose?
///
/// <para>Until M515 a map load published the map's point lights BEFORE the map's own MapSunProperties had
/// been read, and the publish captured — so the record was written with the sun sliders still at the
/// renderer's no-sun fallback. Every later open then restored that fallback over the map's authored sun,
/// and only "Reset to map" put it back. A user reported it as "sunlight never applies when we open the
/// map".</para>
///
/// <para>The signature is the fallback exactly: sun 0.75 grey at intensity 1, sky 0.35 grey at 1,
/// direction (0.4, 0.85, 0.45), and fog 0..0 — which is a range the fog code treats as "none" and nobody
/// dials in by hand. Matched to 1e-9, so a value a person actually typed is safe.</para>
/// </summary>
public static class MapLightingArtefact
{
    // 1e-9, not something looser: the only error to absorb is a JSON round trip of an exact constant,
    // which is ~1e-16. Anything wider starts swallowing values a person actually chose.
    private static bool Is(double value, double expected) => Math.Abs(value - expected) < 1e-9;

    public static bool LooksLikeUntouchedFallback(MapLightingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Is(record.SunIntensity, 1.0)
            && Is(record.SunColorR, 0.75) && Is(record.SunColorG, 0.75) && Is(record.SunColorB, 0.75)
            && Is(record.SkyIntensity, 1.0)
            && Is(record.SkyColorR, 0.35) && Is(record.SkyColorG, 0.35) && Is(record.SkyColorB, 0.35)
            && Is(record.SunDirX ?? 0.4, 0.4) && Is(record.SunDirY ?? 0.85, 0.85) && Is(record.SunDirZ ?? 0.45, 0.45)
            && Is(record.FogStartRaw ?? 0.0, 0.0) && Is(record.FogEndRaw ?? 0.0, 0.0);
    }
}

public sealed class MapLightingRecord
{
    public ulong PathHash { get; set; }
    public string MapgeoPath { get; set; } = "";

    public double SunIntensity { get; set; } = 1.0;
    public double SunColorR { get; set; } = 0.75;
    public double SunColorG { get; set; } = 0.75;
    public double SunColorB { get; set; } = 0.75;
    public double SkyIntensity { get; set; } = 1.0;
    public double SkyColorR { get; set; } = 0.35;
    public double SkyColorG { get; set; } = 0.35;
    public double SkyColorB { get; set; } = 0.35;
    public double LightmapScale { get; set; } = 1.0;

    // M463: the six MapSunProperties fields the Lighting panel gained controls for. Without them, an
    // unsaved fog or sun-direction edit was lost on the next map switch while an unsaved sun-colour edit
    // survived - the same panel remembering four of its ten fields.
    //
    // NULLABLE, and that is load-bearing rather than tidy. These records are JSON and every project saved
    // before this milestone has no such keys, so a non-nullable double would deserialize to 0 and the
    // restore would then overwrite the map's AUTHORED sun direction with (0,0,0) - a degenerate vector
    // that lights nothing. Null means "this record predates the field, keep what the map authored".
    public double? SunDirX { get; set; }
    public double? SunDirY { get; set; }
    public double? SunDirZ { get; set; }
    public double? HorizonColorR { get; set; }
    public double? HorizonColorG { get; set; }
    public double? HorizonColorB { get; set; }
    public double? GroundColorR { get; set; }
    public double? GroundColorG { get; set; }
    public double? GroundColorB { get; set; }
    public double? FogColorR { get; set; }
    public double? FogColorG { get; set; }
    public double? FogColorB { get; set; }
    public double? FogStartRaw { get; set; }
    public double? FogEndRaw { get; set; }

    // The Light.dat fit block - what "spread and shift this table onto this map" resolved to.
    public double LightIntensity { get; set; } = 1.0;
    public double LightRadiusScale { get; set; } = 1.0;
    // M457: 0 is Riot's own linear falloff. A project saved before M457 carries whatever it was tuned to
    // and keeps it - only a NEW project starts on Riot's curve.
    public double FalloffSoftness { get; set; }
    public double PositionScale { get; set; } = 1.0;
    public double ScaleX { get; set; } = 1.0;
    public double ScaleZ { get; set; } = 1.0;
    public double OffsetX { get; set; }
    public double OffsetZ { get; set; }

    /// <summary>Where an imported Light.dat came from, kept so Save still round-trips to the same file
    /// after reopening. A HINT only - never re-parsed on load, or a file changed or moved behind the
    /// editor's back would silently overwrite the edits this record exists to protect.</summary>
    public string? LightDatPath { get; set; }

    public List<SavedPointLight> Lights { get; set; } = new();
}

/// <summary>One point light as the editor holds it. Colour is 0-255 to match both the Light.dat text form
/// and the view-model, so the number a user reads in the inspector is the number in the file.</summary>
public sealed class SavedPointLight
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double R { get; set; }
    public double G { get; set; }
    public double B { get; set; }
    public double Radius { get; set; }
    /// <summary>Editor-only: Light.dat has no field for it, which is the main reason this table is
    /// persisted here rather than being recovered by re-reading the .dat.</summary>
    public double Intensity { get; set; } = 1.0;
    public string Name { get; set; } = "Light";
}

/// <summary>One overridden chunk: its path hash + the on-disk replacement file.</summary>
public sealed class ProjectAssetOverride
{
    public ulong PathHash { get; set; }
    public string? ResolvedPath { get; set; }
    public string OverrideFile { get; set; } = "";
    public string AddedUtc { get; set; } = "";
}

/// <summary>M600: the legacy map port wizard's state, persisted per project so a re-port repeats the run
/// rather than the defaults. Plain settable properties because this round-trips through project.json.
///
/// <para>Shader names are stored as the strings the porter uses, not as an enum: the shader catalogue is
/// read from the installed client, so a name that exists today may not tomorrow. A stored name that no
/// longer resolves falls back to the default for its role rather than failing the port.</para></summary>
public sealed class LegacyPortSettings
{
    // The seven cleanup flags, in the order LegacyPortCleanupOptions declares them.
    public bool RemoveOriginalMeshes { get; set; } = true;
    public bool RemoveOriginalBushes { get; set; } = true;
    public bool RemoveUnusedOriginalMaterials { get; set; } = true;
    public bool RemoveOriginalParticles { get; set; } = true;
    public bool RemoveOriginalProps { get; set; } = true;
    public bool RemoveOriginalSounds { get; set; } = true;
    public bool RemoveOriginalProbes { get; set; } = true;

    public bool FixImportedMapPosition { get; set; }
    public bool ImportLegacyParticles { get; set; } = true;
    public bool ImportLegacySounds { get; set; } = true;

    /// <summary>M550: rebuild decals as flat planes. Off is the porter's default and stays the default
    /// here - a remembered ON is a choice the user made, not something this should introduce.</summary>
    public bool GenerateDecalQuads { get; set; }
    public float DecalLift { get; set; } = 4f;
    public bool DecalSingleImage { get; set; }

    /// <summary>M473: the alignment correction. Stored as three floats rather than a Vector3 so the JSON
    /// stays readable and hand-editable.</summary>
    public float CorrectionX { get; set; }
    public float CorrectionY { get; set; }
    public float CorrectionZ { get; set; }

    // Per-role shader choices. Empty means "use the porter default for that role".
    public string? NormalShader { get; set; }
    public string? DecalShader { get; set; }
    public string? GrassShader { get; set; }
    public string? TerrainShader { get; set; }

    /// <summary>When this was written, so a project carrying settings from a much older ReyEngine is
    /// recognisable in a bug report rather than looking like a fresh run.</summary>
    public DateTime? SavedUtc { get; set; }
}
