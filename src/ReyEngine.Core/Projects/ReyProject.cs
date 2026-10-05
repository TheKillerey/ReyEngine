using System.Text.Json.Serialization;
using ReyEngine.Core.Decoding;

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

    /// <summary>
    /// M742: which modpkg LAYER each WAD folder ships in. Empty means the shipped convention - everything
    /// in "base".
    ///
    /// <para>LTK Manager's modpkg format carries a mod as layers a user can switch off one at a time
    /// (<c>layerStates</c> per profile, missing = enabled). That is what makes an optional half possible:
    /// the Harrowing map's champion particle fix ships beside the map instead of inside it, so someone who
    /// dislikes it - or who runs another mod touching the same champions - turns off one layer rather than
    /// the whole mod.</para>
    ///
    /// <para>A folder named by no layer ships in "base", so a project that never heard of layers exports
    /// exactly as it did before.</para>
    /// </summary>
    public List<ProjectLayer> Layers { get; set; } = new();

    /// <summary>The layer a WAD folder ships in - <see cref="ProjectLayer.BaseLayer"/> when none claims it.</summary>
    public string LayerOf(string folderName) =>
        Layers.FirstOrDefault(l => l.Folders.Any(f =>
            string.Equals(f, folderName, StringComparison.OrdinalIgnoreCase)))?.Name
        ?? ProjectLayer.BaseLayer;

    /// <summary>
    /// M816: the layer a <c>ProjectFolders</c> entry ships in.
    ///
    /// <para>Two folders can be one WAD in two layers: an imported LTK-layered .fantome that has
    /// <c>WAD/Map11.wad.client</c> and <c>WAD_winter/Map11.wad.client</c> becomes <c>Map11</c> and
    /// <c>layers/winter/Map11</c>. Their leaf, which names the WAD and which <see cref="LayerOf"/> is asked,
    /// is the same, so the leaf cannot say which layer is which. A layer that lists a whole entry
    /// (<see cref="ProjectLayer.Folders"/>) claims that folder alone, and that is asked first.</para>
    ///
    /// <para>Every other folder is answered exactly as before: the leaf of the resolved path through
    /// <see cref="LayerOf"/>. Only an entry that has a path in it can be claimed whole, and Project Settings has only
    /// ever written leaf names, so a project that predates M816 cannot hold such a claim and ships as it did. An
    /// unnamed or <c>base</c>-less answer is <see cref="ProjectLayer.BaseLayer"/>.</para>
    /// </summary>
    public string LayerOfFolder(string entry)
    {
        string whole = NormalizeFolderEntry(entry);
        if (whole.Contains('/'))
        {
            var claimant = Layers.FirstOrDefault(l => l.Folders.Any(f => string.Equals(NormalizeFolderEntry(f), whole, StringComparison.OrdinalIgnoreCase)));
            if (claimant is not null) return claimant.Name;
        }
        return LayerOf(LeafOfFolder(entry));
    }

    /// <summary>M816: whether a layer lists this <c>ProjectFolders</c> entry whole (see <see cref="LayerOfFolder"/>). Such a folder
    /// ships as the WAD its leaf names, so two of them can carry one WAD name in two layers.</summary>
    public bool IsClaimedWhole(string entry)
    {
        string whole = NormalizeFolderEntry(entry);
        return whole.Contains('/')
            && Layers.Any(l => l.Folders.Any(f => string.Equals(NormalizeFolderEntry(f), whole, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The leaf of the folder an entry resolves to - the name a plain claim and the WAD itself go by.</summary>
    public string LeafOfFolder(string entry) =>
        System.IO.Path.GetFileName(ResolveProjectPath(entry).TrimEnd('/', '\\'));

    /// <summary>
    /// M816 review: folder entries that would ship as ONE WAD of ONE layer, where at least one is claimed whole. A WAD ships under the leaf of
    /// its folder, so two folders with one leaf in one layer are one WAD to the game: a send merges their trees and an export would store
    /// two files under one name. A layer cannot claim two such folders by accident - an import puts each WAD of each layer in a folder of
    /// its own - but Project Settings can move both into one layer, and then this says so, with the folders, before anything is built.
    /// Collisions among folders no layer claims whole are not reported: those projects ship as they always have.
    /// </summary>
    /// <returns>One item per WAD that more than one folder would ship: the layer, the WAD's file name, and the folder entries.</returns>
    public IReadOnlyList<(string Layer, string Wad, IReadOnlyList<string> Folders)> WholeClaimCollisions()
    {
        var byWad = new Dictionary<(string Layer, string Wad), (string Layer, string Wad, List<string> Folders)>();
        foreach (string entry in ProjectFolders)
        {
            string layer = LayerOfFolder(entry), wad = Build.FantomeLayers.WadFileName(LeafOfFolder(entry));
            var key = (layer.ToLowerInvariant(), wad.ToLowerInvariant());
            if (!byWad.TryGetValue(key, out var group)) byWad[key] = group = (layer, wad, new List<string>());
            group.Folders.Add(entry);
        }
        return byWad.Values
            .Where(g => g.Folders.Count > 1 && g.Folders.Any(IsClaimedWhole))
            .Select(g => (g.Layer, g.Wad, (IReadOnlyList<string>)g.Folders))
            .ToList();
    }

    /// <summary>An entry with '/' separators and no leading or trailing ones, as a layer claims it.</summary>
    public static string NormalizeFolderEntry(string entry) => entry.Replace('\\', '/').Trim('/');

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
    /// other unknown extensions are skipped (each skip is logged). Default on. M816 review: a project made by importing a .fantome
    /// is created with it OFF - the folders hold a package's content, every chunk of which a re-export must pack.</summary>
    public bool PackKnownTypesOnly { get; set; } = true;

    // M17 .fantome mod metadata.
    public string? ModName { get; set; }
    public string? ModAuthor { get; set; }
    public string ModVersion { get; set; } = "1.0.0";
    public string? ModDescription { get; set; }
    public string? ModHeart { get; set; }
    public string? ModHome { get; set; }
    public string? ThumbnailPath { get; set; }

    // M816: the rest of what an LTK-layered .fantome's META/info.json carries. Written back by Export .fantome and, where
    // mod.config.json has a field for it, by Send to LTK Manager. A project that never imported one holds none and exports
    // exactly as before.

    /// <summary>M816: the license the package named (<c>License</c>: a string, or <c>{Name, Url}</c>). Null when none.</summary>
    public ProjectLicense? ModLicense { get; set; }

    /// <summary>M816: <c>Tags</c> (for example <c>map-skin</c>).</summary>
    public List<string> ModTags { get; set; } = new();

    /// <summary>M816: <c>Champions</c> the mod targets.</summary>
    public List<string> ModChampions { get; set; } = new();

    /// <summary>M816: <c>Maps</c> the mod targets (for example <c>summoners-rift</c>).</summary>
    public List<string> ModMaps { get; set; } = new();

    /// <summary>M816: <c>Generator</c> of the package this project was imported from ("ltk_mod_project 0.16.2"). Informational:
    /// an export names ReyEngine as its own generator and does not copy this.</summary>
    public string? ImportedGenerator { get; set; }

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

    /// <summary>
    /// M757: send each overridden GAME bin as LTK Manager game-data declarations - its changes against the
    /// game's copy, in the layer's <c>game_data.yaml</c> - instead of as a whole file, so Riot's later
    /// changes to every key the mod does not touch survive a patch. Needs LTK Manager 1.21 or newer (1.20.0 reads
    /// declarations but refuses a whole layer that creates or removes an object); off by
    /// default because an older manager, or any other loader, would ignore the edits entirely. A bin whose
    /// changes cannot be declared (a removed property, a changed class) still ships whole.
    ///
    /// <para>M814: Export .fantome, and the rebuild after a Riot patch, follow the same setting: the declarations
    /// are the layer's <c>GameData</c> in <c>META/info.json</c> and the declared bins are not packed. cslol-manager
    /// ignores GameData, so a .fantome exported with this on is incomplete there.</para>
    /// </summary>
    public bool ShipBinEditsAsDeclarations { get; set; }

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

/// <summary>
/// M816: a mod's license as a .fantome's <c>META/info.json</c> spells it - either a bare identifier
/// (<c>"License": "MIT"</c>) or an object naming it with an optional link (<c>{"Name": "...", "Url": "..."}</c>,
/// <c>ltk_fantome</c>'s <c>FantomeLicense</c>).
///
/// <para>The two shapes mean different things to a reader - a bare string is an SPDX identifier, an object is a
/// license the author named - so the shape is kept: <see cref="AsObject"/> is true for an object, including
/// one with a name and no link, which the sample (Crauzer's Winter Rift) is.</para>
/// </summary>
public sealed class ProjectLicense
{
    public string Name { get; set; } = "";
    public string? Url { get; set; }

    /// <summary>True when the package wrote an object; false for a bare string. A link implies an object.</summary>
    public bool AsObject { get; set; }
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

    /// <summary>M824: set when the Chroma Studio's BODY RECOLOUR made this record - the colour transform it applied (the
    /// M813 <see cref="ColorTransform"/>, in the schema that type pins). Null for a record the Recolor Textures tool made,
    /// which is described by the M171 fields above and behaves exactly as before; a record with a transform ignores those
    /// fields (they stay at their neutral defaults), and Recolor Textures clears it when it takes the record over.</summary>
    public ColorTransform? Transform { get; set; }

    /// <summary>M824: the skin bin (<c>data/characters/lillia/skins/skin49.bin</c>) the Chroma Studio recoloured this texture
    /// for, so re-opening that skin restores the sliders and the set of textures. Null with <see cref="Transform"/>.</summary>
    public string? ChromaSkin { get; set; }

    /// <summary>M824: the WAD folders of the project the file was written to, when the Chroma Studio wrote it. A chunk some Riot WADs
    /// hold more than once (Aatrox's base diffuse is in Aatrox.wad.client AND Shaders.wad.client, byte for byte) is written into every
    /// holder's folder, because the game reads whichever copy it mounts first; Revert needs the list to take them all out. Null for a
    /// record the Recolor Textures tool made, and for a Chroma Studio record that wrote one folder.</summary>
    public List<string>? WadFolders { get; set; }
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
    // M759: the rest of the environment fog, nullable for the same reason as the six above
    public bool? FogEnabled { get; set; }
    public double? FogAltColorR { get; set; }
    public double? FogAltColorG { get; set; }
    public double? FogAltColorB { get; set; }
    public double? FogEmissiveRemap { get; set; }
    public double? FogLowQualityEmissiveRemap { get; set; }

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
