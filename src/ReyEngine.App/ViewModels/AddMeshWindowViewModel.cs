using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;

namespace ReyEngine.App.ViewModels;

/// <summary>One imported mesh row — include it or not.</summary>
public sealed partial class AddMeshRowViewModel : ObservableObject
{
    public required ImportedSceneMesh Mesh { get; init; }
    [ObservableProperty] private bool _include = true;
    public string Info => $"{Mesh.Positions.Length / 3:n0} verts · {Mesh.Indices.Length / 3:n0} tris · mat '{Mesh.MaterialName}'";
    public bool TooLarge => Mesh.Positions.Length / 3 > 65535;
}

/// <summary>Per imported material: how it becomes a map material.</summary>
public sealed partial class AddMeshMaterialViewModel : ObservableObject
{
    public required ImportedSceneMaterial Source { get; init; }
    public required IReadOnlyList<string> ExistingMaterials { get; init; }
    public required IReadOnlyList<string> ShaderChoices { get; init; }   // League shaders from the catalogue

    /// <summary>0 = use an existing map material, 1 = create a new one from a League shader.</summary>
    [ObservableProperty] private int _mode = 1;
    [ObservableProperty] private string? _existingMaterial;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private int _shaderIndex;
    [ObservableProperty] private bool _useImportedTexture;

    public bool IsExistingMode => Mode == 0;
    public bool IsNewMode => Mode == 1;
    partial void OnModeChanged(int value)
    {
        OnPropertyChanged(nameof(IsExistingMode));
        OnPropertyChanged(nameof(IsNewMode));
    }

    public bool HasImportedTexture => Source.HasTexture;
    public string TextureNote => Source.EmbeddedTexture is not null
        ? "embedded texture found"
        : Source.DiffuseTexturePath is { Length: > 0 } p ? $"texture file: {p}" : "no texture in the import";
}

/// <summary>What the host executes when the user confirms.</summary>
public sealed record AddMeshMaterialPlan(
    string ImportedName,
    bool CreateNew,
    string? ExistingMaterial,          // when CreateNew == false
    string? NewName,                   // when CreateNew == true
    string? ShaderPath,                // the League shader the new material is built from
    byte[]? TextureBytes,              // png/jpg blob to convert + save (null = no texture change)
    string? TextureFileNameHint);

public sealed record AddMeshPlan(
    IReadOnlyList<ImportedSceneMesh> Meshes,
    IReadOnlyList<AddMeshMaterialPlan> Materials,
    IReadOnlyDictionary<string, string> MeshMaterialNames,   // imported material name -> final map material name
    int VisibilityMask);

/// <summary>
/// M123: the Add Mesh window — import a scene file (.fbx/.glb/.gltf/.obj/.scb/.sco), choose which
/// meshes to add, set up one map material per imported material (existing, or a clone of a template
/// with optional shader + imported texture), assign visibility layers, then hand the plan to the host.
/// </summary>
public sealed partial class AddMeshWindowViewModel : ObservableObject
{
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _status = "Choose a mesh file to import.";
    [ObservableProperty] private bool _hasScene;

    public ObservableCollection<AddMeshRowViewModel> Meshes { get; } = new();
    public ObservableCollection<AddMeshMaterialViewModel> Materials { get; } = new();
    public ObservableCollection<LayerToggle> Layers { get; } = new();

    public sealed partial class LayerToggle : ObservableObject
    {
        public required string Name { get; init; }
        public required int Bit { get; init; }
        [ObservableProperty] private bool _isOn = true;
    }

    // host-provided context
    public IReadOnlyList<string> ExistingMaterials { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ShaderChoices { get; init; } = Array.Empty<string>();
    public Func<string, Task<string?>>? PickFile;       // returns a path or null
    public Action<AddMeshPlan>? Confirmed;
    public Action? Cancelled;

    private ImportedScene? _scene;

    public AddMeshWindowViewModel()
    {
        foreach (var d in MapVisibility.Dragons)
            Layers.Add(new LayerToggle { Name = d.Name, Bit = d.Bit });
    }

    [RelayCommand]
    private async Task Browse()
    {
        if (PickFile is null) return;
        var path = await PickFile("Import mesh (.fbx / .glb / .gltf / .obj / .scb / .sco)");
        if (path is not null) LoadFile(path);
    }

    public void LoadFile(string path)
    {
        FilePath = path;
        Meshes.Clear();
        Materials.Clear();
        _scene = null;
        HasScene = false;

        ImportedScene? scene;
        string? err = null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".obj" or ".scb" or ".sco")
            scene = ImportLegacy(path, out err);
        else
            scene = SceneMeshImporter.Import(path, out err);

        if (scene is null)
        {
            Status = $"Import failed: {err}";
            return;
        }
        _scene = scene;
        foreach (var m in scene.Meshes) Meshes.Add(new AddMeshRowViewModel { Mesh = m });
        int defaultShader = 0;
        for (int i = 0; i < ShaderChoices.Count; i++)
            if (ShaderChoices[i].EndsWith("DefaultEnv_Flat", StringComparison.OrdinalIgnoreCase)) { defaultShader = i; break; }
        foreach (var mat in scene.Materials)
            Materials.Add(new AddMeshMaterialViewModel
            {
                Source = mat,
                ExistingMaterials = ExistingMaterials,
                ShaderChoices = ShaderChoices,
                ExistingMaterial = ExistingMaterials.FirstOrDefault(),
                NewName = SanitizeName(mat.Name),
                ShaderIndex = defaultShader,
                UseImportedTexture = mat.HasTexture,
            });
        HasScene = true;
        int totalVerts = scene.Meshes.Sum(m => m.Positions.Length / 3);
        Status = $"{scene.Meshes.Count} mesh(es), {scene.Materials.Count} material(s), {totalVerts:n0} verts total.";
    }

    /// <summary>.obj/.scb/.sco keep working through the old importers — one mesh, no material info.</summary>
    private static ImportedScene? ImportLegacy(string path, out string? err)
    {
        err = null;
        try
        {
            float[]? pos, nrm = null, uv = null; int[]? idx;
            if (path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            {
                var m = ObjMeshImporter.Import(File.ReadAllText(path), Path.GetFileName(path));
                if (m is null) { err = "obj parse failed"; return null; }
                (pos, nrm, uv, idx) = (m.Positions, m.Normals, m.Uvs, m.Indices);
            }
            else
            {
                var sm = StaticObjectDecoder.Decode(File.ReadAllBytes(path), path);
                if (sm is null) { err = "scb/sco parse failed"; return null; }
                (pos, uv, idx) = (sm.Positions, sm.Uvs, Array.ConvertAll(sm.Indices, i => (int)i));
            }
            if (pos is null || idx is null) { err = "empty mesh"; return null; }
            var name = Path.GetFileNameWithoutExtension(path);
            var mesh = new ImportedSceneMesh(name, "Imported", pos, nrm ?? new float[pos.Length], uv ?? new float[pos.Length / 3 * 2], idx);
            return new ImportedScene(new[] { mesh }, new[] { new ImportedSceneMaterial("Imported", null) });
        }
        catch (Exception ex) { err = ex.Message; return null; }
    }

    private static string SanitizeName(string raw)
    {
        var cleaned = new string(raw.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return $"Rey_{cleaned}";
    }

    [RelayCommand]
    private void Confirm()
    {
        if (_scene is null) return;
        var include = Meshes.Where(r => r.Include && !r.TooLarge).Select(r => r.Mesh).ToList();
        if (include.Count == 0) { Status = "Nothing selected to add."; return; }

        var plans = new List<AddMeshMaterialPlan>();
        var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Materials)
        {
            bool createNew = m.Mode == 1;
            string final = createNew
                ? m.NewName.Trim()
                : m.ExistingMaterial ?? "";
            if (createNew && final.Length == 0) { Status = $"Material '{m.Source.Name}' needs a name."; return; }
            if (!createNew && final.Length == 0) { Status = $"Material '{m.Source.Name}': pick an existing material."; return; }

            string? shader = createNew && m.ShaderIndex >= 0 && m.ShaderIndex < m.ShaderChoices.Count
                ? m.ShaderChoices[m.ShaderIndex] : null;
            if (createNew && shader is null) { Status = $"Material '{m.Source.Name}': pick a shader."; return; }
            byte[]? texBytes = null;
            string? texHint = null;
            if (createNew && m.UseImportedTexture && m.Source.HasTexture)
            {
                if (m.Source.EmbeddedTexture is not null) { texBytes = m.Source.EmbeddedTexture; texHint = m.Source.Name; }
                else if (m.Source.DiffuseTexturePath is { } rel)
                {
                    // relative to the scene file; silently skipped when missing
                    var abs = Path.IsPathRooted(rel) ? rel : Path.Combine(Path.GetDirectoryName(FilePath) ?? "", rel);
                    if (File.Exists(abs)) { texBytes = File.ReadAllBytes(abs); texHint = Path.GetFileNameWithoutExtension(abs); }
                }
            }
            plans.Add(new AddMeshMaterialPlan(m.Source.Name, createNew, m.ExistingMaterial, final, shader, texBytes, texHint));
            nameMap[m.Source.Name] = final;
        }

        int mask = 0;
        foreach (var l in Layers) if (l.IsOn) mask |= l.Bit;
        if (mask == 0) mask = 255;   // no layers picked = visible everywhere, never invisible

        Confirmed?.Invoke(new AddMeshPlan(include, plans, nameMap, mask));
    }

    [RelayCommand] private void Cancel() => Cancelled?.Invoke();
}
