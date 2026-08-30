using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

public sealed partial class LegacyPortShaderRowViewModel : ObservableObject
{
    public required LegacyMaterialRole Role { get; init; }
    public required string Label { get; init; }
    public required string Hint { get; init; }
    public required int MaterialCount { get; init; }
    public required IReadOnlyList<string> ShaderChoices { get; init; }
    public Action<LegacyMaterialRole, string?>? SelectionChanged;
    [ObservableProperty] private string? _selectedShader;
    public string CountText => $"{MaterialCount:n0} material(s) detected";
    partial void OnSelectedShaderChanged(string? value) => SelectionChanged?.Invoke(Role, value);
}

public sealed partial class LegacyPortMaterialRowViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required LegacyMaterialRole Role { get; init; }
    public required string TextureName { get; init; }
    public required IReadOnlyList<string> ShaderChoices { get; init; }
    /// <summary>M533: what the row was seeded with, so confirming the dialog can tell a shader the USER
    /// picked from one it merely displayed. Sending back every row overrode the porter's own decisions.</summary>
    public string? InitialShader { get; init; }
    [ObservableProperty] private string? _selectedShader;
    public string RoleText => Role.ToString();
}

public sealed record LegacyMapPortShaderSelection(
    LegacyPortShaderOptions RoleShaders,
    IReadOnlyDictionary<string, string> MaterialShaders,
    LegacyPortCleanupOptions Cleanup,
    bool FixImportedMapPosition,
    // M473: the correction is no longer a constant only a rebuild could change.
    System.Numerics.Vector3 PositionCorrection,
    // M531: import the source map's own particles - Particles.dat plus the .troybin it names.
    bool ImportLegacyParticles = true,
    // M550: rebuild decals as flat planes instead of terrain-following patches.
    LegacyPortDecalOptions? Decals = null,
    // M575: re-host the source client's Wwise audio in a bank this map already loads.
    bool ImportLegacySounds = true);

public sealed record LegacyDestinationContentSummary(
    int OrdinaryMeshes,
    int BushMeshes,
    int PreviousImportMeshes,
    int Materials,
    int Particles,
    int Props,
    int Sounds,
    int Probes);

public sealed partial class LegacyMapPortWindowViewModel : ObservableObject
{
    [ObservableProperty] private string _status = "Review the detected material roles and their target shaders.";
    [ObservableProperty] private bool _removeOriginalMeshes = true;
    [ObservableProperty] private bool _removeOriginalBushes = true;
    [ObservableProperty] private bool _removeUnusedOriginalMaterials = true;
    [ObservableProperty] private bool _removeOriginalParticles = true;
    [ObservableProperty] private bool _removeOriginalProps = true;
    [ObservableProperty] private bool _removeOriginalSounds = true;
    [ObservableProperty] private bool _removeOriginalProbes = true;
    [ObservableProperty] private bool _fixImportedMapPosition;
    // M550: off by default. The legacy patches follow the terrain and the planes do not, so this is a
    // deliberate trade the user opts into, not a correction.
    [ObservableProperty] private bool _generateDecalQuads;
    [ObservableProperty] private string _decalLift = "4";
    // M554: off keeps the patch's own UV range, so the texture stays the size it was and repeats as it
    // did. On gives one image per plane at authored scale, which shrinks the decal to a median 30% of the
    // area it covered.
    [ObservableProperty] private bool _decalSingleImage;

    // M473: the alignment correction, seeded from the measured default but editable. It was a hardcoded
    // constant, so a port that landed a few hundred units out could not be fixed without a rebuild.
    [ObservableProperty] private string _correctionX =
        Formats.MapGeo.LegacyMapPorter.LegacyPositionCorrection.X.ToString("0.###", CultureInfo.InvariantCulture);
    [ObservableProperty] private string _correctionY =
        Formats.MapGeo.LegacyMapPorter.LegacyPositionCorrection.Y.ToString("0.###", CultureInfo.InvariantCulture);
    [ObservableProperty] private string _correctionZ =
        Formats.MapGeo.LegacyMapPorter.LegacyPositionCorrection.Z.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The typed correction, falling back to the measured default on anything unparseable — a
    /// half-typed number must not silently port the map to the origin. InvariantCulture because this is a
    /// German-locale machine and "1000,834" and "1000.834" both have to mean the same thing.</summary>
    /// <summary>M550: German locale ships a comma decimal separator, so the parse is invariant.</summary>
    public float ParsedDecalLift =>
        float.TryParse(DecalLift, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
            ? Math.Clamp(value, 0f, 100f)
            : 4f;

    private System.Numerics.Vector3 ParsedCorrection
    {
        get
        {
            var d = Formats.MapGeo.LegacyMapPorter.LegacyPositionCorrection;
            return new System.Numerics.Vector3(Num(CorrectionX, d.X), Num(CorrectionY, d.Y), Num(CorrectionZ, d.Z));
            static float Num(string s, float fallback) =>
                float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                || float.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v) ? v : fallback;
        }
    }

    [ObservableProperty] private string _cleanupOutcome = "";
    private readonly LegacyDestinationContentSummary _destination;
    public string Summary { get; }
    public string MeshCleanupText { get; }
    public string BushCleanupText { get; }
    public string MaterialCleanupText { get; }
    public string ParticleCleanupText { get; }
    public string PropCleanupText { get; }
    public string SoundCleanupText { get; }
    public string ProbeCleanupText { get; }
    public ObservableCollection<LegacyPortShaderRowViewModel> ShaderRows { get; } = new();
    public ObservableCollection<LegacyPortMaterialRowViewModel> Materials { get; } = new();
    public Action<LegacyMapPortShaderSelection>? Confirmed;
    public Action? Cancelled;

    /// <param name="remembered">M600: what this project's last completed port used, or null when it has
    /// never been ported. Seeded BEFORE the shader rows are built so a remembered role shader is what the
    /// rows open on - see Add(), whose "preferred" argument is the porter default otherwise.</param>
    public LegacyMapPortWindowViewModel(LegacyMapPortResult result, IReadOnlyList<string> shaderChoices,
        LegacyDestinationContentSummary destination, Core.Projects.LegacyPortSettings? remembered = null)
    {
        _destination = destination;
        if (remembered is not null) SeedFrom(remembered);
        Summary = $"{result.SourceFormat}: {result.SourceMeshCount:n0} source objects -> " +
                  $"{result.ImportedMeshCount:n0} mapgeo meshes, {result.Textures.Count:n0} textures, " +
                  $"{result.Materials.Count:n0} materials.";
        MeshCleanupText = $"Remove original non-bush meshes ({destination.OrdinaryMeshes:n0})";
        BushCleanupText = $"Remove original gameplay bushes ({destination.BushMeshes:n0})";
        MaterialCleanupText = $"Remove unused original materials ({destination.Materials:n0} total before cleanup)";
        ParticleCleanupText = $"Remove original particles ({destination.Particles:n0})";
        PropCleanupText = $"Remove original animated props / mobs ({destination.Props:n0})";
        SoundCleanupText = $"Remove original map sounds ({destination.Sounds:n0})";
        ProbeCleanupText = $"Remove original cubemap probes ({destination.Probes:n0})";
        // A remembered shader only wins when the installed client still OFFERS it - the catalogue comes
        // from the game, so a name stored against an older patch can simply be gone. Falling back beats
        // seeding a row with a shader the port would then refuse.
        string Preferred(string? stored, string fallback) =>
            !string.IsNullOrWhiteSpace(stored) && shaderChoices.Contains(stored, StringComparer.OrdinalIgnoreCase)
                ? stored! : fallback;

        Add(result, shaderChoices, LegacyMaterialRole.Normal, "Normal alpha-tested surfaces",
            "DefaultEnv_Flat_AlphaTest. Used for ordinary opaque and cutout textures.",
            Preferred(remembered?.NormalShader, LegacyMapPorter.NormalShader));
        Add(result, shaderChoices, LegacyMaterialRole.Decal, "Alpha-blended decals",
            "Uses the selected shader with SrcAlpha / OneMinusSrcAlpha blending and a small alpha floor.",
            Preferred(remembered?.DecalShader, LegacyMapPorter.DecalShader));
        Add(result, shaderChoices, LegacyMaterialRole.Grass, "Grass and brushes",
            "VertexDeform. The porter also generates the mesh-pivot vertex channel used for movement.",
            Preferred(remembered?.GrassShader, LegacyMapPorter.GrassShader));
        Add(result, shaderChoices, LegacyMaterialRole.FourBlendTerrain, "Four-layer terrain",
            "4TextureBlend_WorldProjected with the NVR blend canvas and four authored layer textures.",
            Preferred(remembered?.TerrainShader, LegacyMapPorter.TerrainShader));
        foreach (var material in result.Materials.OrderBy(material => material.Role).ThenBy(material => material.Name))
            Materials.Add(new LegacyPortMaterialRowViewModel
            {
                Name = material.Name,
                Role = material.Role,
                TextureName = Path.GetFileName(material.Samplers.Values.FirstOrDefault() ?? "(shader default)"),
                ShaderChoices = shaderChoices,
                SelectedShader = material.Shader,
                InitialShader = material.Shader,
            });
        foreach (var row in ShaderRows) row.SelectionChanged = ApplyRoleShader;
        UpdateCleanupOutcome();
    }

    partial void OnRemoveOriginalMeshesChanged(bool value) => UpdateCleanupOutcome();
    partial void OnRemoveOriginalBushesChanged(bool value) => UpdateCleanupOutcome();
    partial void OnRemoveUnusedOriginalMaterialsChanged(bool value) => UpdateCleanupOutcome();
    /// <summary>M531: bring the source map's particles across - its Particles.dat placement list and
    /// each .troybin it names. Off means the ported map keeps whatever particles the destination had.</summary>
    [ObservableProperty] private bool _importLegacyParticles = true;

    /// <summary>M575: bring the source client's map audio across. Off by default is tempting because it
    /// rewrites two shipped banks, but the ambience is the most-missed half of a legacy map and the
    /// rewrite is additive - everything already in those banks is preserved byte for byte.</summary>
    [ObservableProperty] private bool _importLegacySounds = true;

    partial void OnRemoveOriginalParticlesChanged(bool value) => UpdateCleanupOutcome();
    partial void OnRemoveOriginalPropsChanged(bool value) => UpdateCleanupOutcome();
    partial void OnRemoveOriginalSoundsChanged(bool value) => UpdateCleanupOutcome();
    partial void OnRemoveOriginalProbesChanged(bool value) => UpdateCleanupOutcome();

    private void UpdateCleanupOutcome()
    {
        int retainedMeshes = (RemoveOriginalMeshes ? 0 : _destination.OrdinaryMeshes)
            + (RemoveOriginalBushes ? 0 : _destination.BushMeshes);
        var retained = new List<string>();
        if (!RemoveOriginalBushes && _destination.BushMeshes > 0)
            retained.Add($"{_destination.BushMeshes:n0} gameplay bush meshes");
        if (!RemoveOriginalMeshes && _destination.OrdinaryMeshes > 0)
            retained.Add($"{_destination.OrdinaryMeshes:n0} other meshes");
        string meshResult = retainedMeshes == 0
            ? "No ordinary destination mapgeo meshes will remain."
            : $"This selection keeps {string.Join(" and ", retained)} from the destination.";
        if (_destination.PreviousImportMeshes > 0)
            meshResult += $" {_destination.PreviousImportMeshes:n0} meshes from the earlier legacy import will always be replaced.";
        string materialResult = !RemoveUnusedOriginalMaterials
            ? " All original material definitions will remain."
            : retainedMeshes > 0 || !RemoveOriginalParticles || !RemoveOriginalProps || !RemoveOriginalSounds || !RemoveOriginalProbes
                ? " Materials required by any retained meshes or bin content must remain too."
                : " Unreferenced original materials will be removed after the imported materials are rebuilt.";
        CleanupOutcome = meshResult + materialResult;
    }

    [RelayCommand]
    private void FullReplacement()
    {
        SetCleanup(LegacyPortCleanupOptions.FullReplacement);
        Status = "Full replacement selected. Disable bush deletion if the legacy map has no authored bushes.";
    }

    [RelayCommand]
    private void KeepDestinationSupport()
    {
        SetCleanup(LegacyPortCleanupOptions.KeepDestinationSupport);
        Status = "Keeping destination bushes, particles, props, sounds, and probes; replacing its main geometry.";
    }

    [RelayCommand]
    private void KeepEverything()
    {
        SetCleanup(LegacyPortCleanupOptions.KeepEverything);
        Status = "All destination content will remain underneath the imported legacy map.";
    }

    /// <summary>M600: open the dialog on the last completed port rather than on the defaults.</summary>
    private void SeedFrom(Core.Projects.LegacyPortSettings s)
    {
        RemoveOriginalMeshes = s.RemoveOriginalMeshes;
        RemoveOriginalBushes = s.RemoveOriginalBushes;
        RemoveUnusedOriginalMaterials = s.RemoveUnusedOriginalMaterials;
        RemoveOriginalParticles = s.RemoveOriginalParticles;
        RemoveOriginalProps = s.RemoveOriginalProps;
        RemoveOriginalSounds = s.RemoveOriginalSounds;
        RemoveOriginalProbes = s.RemoveOriginalProbes;
        FixImportedMapPosition = s.FixImportedMapPosition;
        ImportLegacyParticles = s.ImportLegacyParticles;
        ImportLegacySounds = s.ImportLegacySounds;
        GenerateDecalQuads = s.GenerateDecalQuads;
        DecalSingleImage = s.DecalSingleImage;
        DecalLift = s.DecalLift.ToString("0.###", CultureInfo.InvariantCulture);
        CorrectionX = s.CorrectionX.ToString("0.###", CultureInfo.InvariantCulture);
        CorrectionY = s.CorrectionY.ToString("0.###", CultureInfo.InvariantCulture);
        CorrectionZ = s.CorrectionZ.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>M600: what a completed port should record. Read off the SELECTION rather than the dialog
    /// so it stores what actually ran.</summary>
    public static Core.Projects.LegacyPortSettings Remember(LegacyMapPortShaderSelection selection) => new()
    {
        RemoveOriginalMeshes = selection.Cleanup.RemoveOriginalMeshes,
        RemoveOriginalBushes = selection.Cleanup.RemoveOriginalBushes,
        RemoveUnusedOriginalMaterials = selection.Cleanup.RemoveUnusedOriginalMaterials,
        RemoveOriginalParticles = selection.Cleanup.RemoveOriginalParticles,
        RemoveOriginalProps = selection.Cleanup.RemoveOriginalProps,
        RemoveOriginalSounds = selection.Cleanup.RemoveOriginalSounds,
        RemoveOriginalProbes = selection.Cleanup.RemoveOriginalProbes,
        FixImportedMapPosition = selection.FixImportedMapPosition,
        ImportLegacyParticles = selection.ImportLegacyParticles,
        ImportLegacySounds = selection.ImportLegacySounds,
        GenerateDecalQuads = selection.Decals?.GenerateQuads ?? false,
        DecalLift = selection.Decals?.Lift ?? LegacyPortDecalOptions.Defaults.Lift,
        DecalSingleImage = selection.Decals?.SingleImage ?? false,
        CorrectionX = selection.PositionCorrection.X,
        CorrectionY = selection.PositionCorrection.Y,
        CorrectionZ = selection.PositionCorrection.Z,
        NormalShader = selection.RoleShaders.NormalShader,
        DecalShader = selection.RoleShaders.DecalShader,
        GrassShader = selection.RoleShaders.GrassShader,
        TerrainShader = selection.RoleShaders.TerrainShader,
        SavedUtc = DateTime.UtcNow,
    };

    /// <summary>M600: rebuild a selection from remembered settings, for a port that runs with no dialog.
    /// A stored role shader the installed client no longer offers falls back to the porter default, the
    /// same rule the dialog's rows use.</summary>
    public static LegacyMapPortShaderSelection Replay(Core.Projects.LegacyPortSettings s,
        IReadOnlyList<string> shaderChoices)
    {
        string Pick(string? stored, string fallback) =>
            !string.IsNullOrWhiteSpace(stored) && shaderChoices.Contains(stored, StringComparer.OrdinalIgnoreCase)
                ? stored! : fallback;

        return new LegacyMapPortShaderSelection(
            new LegacyPortShaderOptions(
                Pick(s.NormalShader, LegacyMapPorter.NormalShader),
                Pick(s.DecalShader, LegacyMapPorter.DecalShader),
                Pick(s.GrassShader, LegacyMapPorter.GrassShader),
                Pick(s.TerrainShader, LegacyMapPorter.TerrainShader)),
            // No per-material overrides: those are row-level choices the dialog makes, and replaying a
            // guess at them would change materials the user never touched.
            new Dictionary<string, string>(),
            new LegacyPortCleanupOptions(s.RemoveOriginalMeshes, s.RemoveOriginalBushes,
                s.RemoveUnusedOriginalMaterials, s.RemoveOriginalParticles, s.RemoveOriginalProps,
                s.RemoveOriginalSounds, s.RemoveOriginalProbes),
            s.FixImportedMapPosition,
            new System.Numerics.Vector3(s.CorrectionX, s.CorrectionY, s.CorrectionZ),
            s.ImportLegacyParticles,
            new LegacyPortDecalOptions(s.GenerateDecalQuads, s.DecalLift, s.DecalSingleImage),
            s.ImportLegacySounds);
    }

    private void SetCleanup(LegacyPortCleanupOptions options)
    {
        RemoveOriginalMeshes = options.RemoveOriginalMeshes;
        RemoveOriginalBushes = options.RemoveOriginalBushes;
        RemoveUnusedOriginalMaterials = options.RemoveUnusedOriginalMaterials;
        RemoveOriginalParticles = options.RemoveOriginalParticles;
        RemoveOriginalProps = options.RemoveOriginalProps;
        RemoveOriginalSounds = options.RemoveOriginalSounds;
        RemoveOriginalProbes = options.RemoveOriginalProbes;
    }

    private void ApplyRoleShader(LegacyMaterialRole role, string? shader)
    {
        if (string.IsNullOrWhiteSpace(shader)) return;
        foreach (var material in Materials.Where(material => material.Role == role))
            material.SelectedShader = shader;
    }

    private void Add(LegacyMapPortResult result, IReadOnlyList<string> choices, LegacyMaterialRole role,
        string label, string hint, string preferred)
    {
        ShaderRows.Add(new LegacyPortShaderRowViewModel
        {
            Role = role,
            Label = label,
            Hint = hint,
            MaterialCount = result.Materials.Count(m => m.Role == role),
            ShaderChoices = choices,
            // M534: open on what the porter ACTUALLY chose for this role, not on the role's nominal
            // default. The alpha classifier now runs before this dialog, so on a gradient-heavy map most
            // Normal materials arrive on DefaultEnv_Flat - and a dropdown that still displayed
            // DefaultEnv_Flat_AlphaTest made picking it a no-op, which is how "I asked for AlphaTest
            // everywhere and got Flat" happened. Showing the real value makes choosing the other one a
            // change, which is what carries it through.
            SelectedShader = Common(result, role, choices)
                             ?? choices.FirstOrDefault(s => s.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                             ?? choices.FirstOrDefault(),
        });
    }

    /// <summary>The shader the most materials of this role actually carry, when the choice list offers
    /// it. Null when the role has no materials, so the caller keeps its nominal default.</summary>
    private static string? Common(LegacyMapPortResult result, LegacyMaterialRole role, IReadOnlyList<string> choices)
    {
        string? common = result.Materials.Where(m => m.Role == role)
            .GroupBy(m => m.Shader, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
        return common is null ? null
            : choices.FirstOrDefault(s => s.Equals(common, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void Confirm()
    {
        if (ShaderRows.Any(row => string.IsNullOrWhiteSpace(row.SelectedShader)))
        {
            Status = "Select a shader for every imported material role.";
            return;
        }
        string Pick(LegacyMaterialRole role) => ShaderRows.Single(row => row.Role == role).SelectedShader!;
        if (Materials.Any(material => string.IsNullOrWhiteSpace(material.SelectedShader)))
        {
            Status = "Select a shader for every generated material.";
            return;
        }
        var options = new LegacyPortShaderOptions(
            Pick(LegacyMaterialRole.Normal), Pick(LegacyMaterialRole.Decal),
            Pick(LegacyMaterialRole.Grass), Pick(LegacyMaterialRole.FourBlendTerrain));
        var cleanup = new LegacyPortCleanupOptions(RemoveOriginalMeshes, RemoveOriginalBushes,
            RemoveUnusedOriginalMaterials, RemoveOriginalParticles, RemoveOriginalProps,
            RemoveOriginalSounds, RemoveOriginalProbes);
        Confirmed?.Invoke(new LegacyMapPortShaderSelection(options,
            // M533: only the rows the user actually CHANGED. Every row is seeded with the shader the
            // porter proposed BEFORE ApplyShaderOptions runs, so sending them all back re-applied those
            // stale values on top of the porter's decisions - which is why the alpha classifier's result
            // never survived even once its own lookup was fixed.
            Materials.Where(material => !string.Equals(material.SelectedShader, material.InitialShader, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(material => material.Name, material => material.SelectedShader!, StringComparer.OrdinalIgnoreCase),
            cleanup, FixImportedMapPosition, ParsedCorrection, ImportLegacyParticles,
            new LegacyPortDecalOptions(GenerateDecalQuads, ParsedDecalLift, DecalSingleImage),
            ImportLegacySounds));
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();
}
