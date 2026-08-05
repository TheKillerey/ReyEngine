using System.Collections.ObjectModel;
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
    [ObservableProperty] private string? _selectedShader;
    public string RoleText => Role.ToString();
}

public sealed record LegacyMapPortShaderSelection(
    LegacyPortShaderOptions RoleShaders,
    IReadOnlyDictionary<string, string> MaterialShaders,
    LegacyPortCleanupOptions Cleanup);

public sealed record LegacyDestinationContentSummary(
    int OrdinaryMeshes,
    int BushMeshes,
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

    public LegacyMapPortWindowViewModel(LegacyMapPortResult result, IReadOnlyList<string> shaderChoices,
        LegacyDestinationContentSummary destination)
    {
        Summary = $"{result.SourceFormat}: {result.SourceMeshCount:n0} source objects -> " +
                  $"{result.ImportedMeshCount:n0} mapgeo meshes, {result.Textures.Count:n0} textures, " +
                  $"{result.Materials.Count:n0} materials.";
        MeshCleanupText = $"Remove original non-bush meshes ({destination.OrdinaryMeshes:n0})";
        BushCleanupText = $"Remove original bushes / VertexDeform foliage ({destination.BushMeshes:n0})";
        MaterialCleanupText = $"Remove original materials that are no longer referenced ({destination.Materials:n0} total)";
        ParticleCleanupText = $"Remove original particles ({destination.Particles:n0})";
        PropCleanupText = $"Remove original animated props / mobs ({destination.Props:n0})";
        SoundCleanupText = $"Remove original map sounds ({destination.Sounds:n0})";
        ProbeCleanupText = $"Remove original cubemap probes ({destination.Probes:n0})";
        Add(result, shaderChoices, LegacyMaterialRole.Normal, "Normal alpha-tested surfaces",
            "DefaultEnv_Flat_AlphaTest. Used for ordinary opaque and cutout textures.", LegacyMapPorter.NormalShader);
        Add(result, shaderChoices, LegacyMaterialRole.Decal, "Alpha-blended decals",
            "Uses the selected shader with SrcAlpha / OneMinusSrcAlpha blending and a small alpha floor.", LegacyMapPorter.DecalShader);
        Add(result, shaderChoices, LegacyMaterialRole.Grass, "Grass and brushes",
            "VertexDeform. The porter also generates the mesh-pivot vertex channel used for movement.", LegacyMapPorter.GrassShader);
        Add(result, shaderChoices, LegacyMaterialRole.FourBlendTerrain, "Four-layer terrain",
            "4TextureBlend_WorldProjected with the NVR blend canvas and four authored layer textures.", LegacyMapPorter.TerrainShader);
        foreach (var material in result.Materials.OrderBy(material => material.Role).ThenBy(material => material.Name))
            Materials.Add(new LegacyPortMaterialRowViewModel
            {
                Name = material.Name,
                Role = material.Role,
                TextureName = Path.GetFileName(material.Samplers.Values.FirstOrDefault() ?? "(shader default)"),
                ShaderChoices = shaderChoices,
                SelectedShader = material.Shader,
            });
        foreach (var row in ShaderRows) row.SelectionChanged = ApplyRoleShader;
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
            SelectedShader = choices.FirstOrDefault(s => s.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                             ?? choices.FirstOrDefault(),
        });
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
            Materials.ToDictionary(material => material.Name, material => material.SelectedShader!, StringComparer.OrdinalIgnoreCase),
            cleanup));
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();
}
