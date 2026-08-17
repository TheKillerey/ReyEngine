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
/// <summary>
/// M514: one sampler of the shader being set up, with the texture it will point at.
///
/// <para>Without this the new material was born pointing at the shader's declared default — which for
/// DefaultEnv_Flat is ASSETS/Shared/Materials/rock_texture.tex, a path that exists in no wad and no
/// project. The material was structurally perfect and drew untextured, which is what "the material is
/// empty" looks like from the outside.</para>
/// </summary>
public sealed partial class AddMeshSamplerViewModel : ObservableObject
{
    public required string Name { get; init; }
    /// <summary>Asked of the host, which is the only layer that knows where textures live.</summary>
    public Func<string, bool>? Exists { private get; init; }

    [ObservableProperty] private string _path = "";

    partial void OnPathChanged(string value)
    {
        OnPropertyChanged(nameof(Resolved));
        OnPropertyChanged(nameof(Note));
    }

    /// <summary>Null when there is no way to check — the same restraint the audit uses, so an unknown
    /// never renders as a warning.</summary>
    public bool? Resolved => Exists is null || Path.Length == 0 ? null : Exists(Path);
    public bool IsMissing => Resolved == false;
    public string Note => Resolved switch
    {
        false => "not found — the surface will draw untextured",
        true => "found",
        _ => "",
    };
}

public sealed partial class AddMeshMaterialViewModel : ObservableObject
{
    /// <summary>M514: the samplers this shader declares, editable before the material is created.</summary>
    public ObservableCollection<AddMeshSamplerViewModel> Samplers { get; } = new();
    public bool HasSamplers => Samplers.Count > 0;

    /// <summary>(shader name) -> its declared samplers and their default paths. Supplied by the host,
    /// which owns the shader catalogue.</summary>
    public Func<string, IReadOnlyList<(string Name, string DefaultPath)>>? SamplersForShader { private get; init; }
    public Func<string, bool>? TextureExists { private get; init; }

    /// <summary>Rebuild the sampler rows for the currently chosen shader, keeping any path the user has
    /// already typed for a sampler of the same name.</summary>
    public void RefreshSamplers()
    {
        if (SamplersForShader is null || ShaderIndex < 0 || ShaderIndex >= ShaderChoices.Count)
        { Samplers.Clear(); OnPropertyChanged(nameof(HasSamplers)); return; }

        var kept = Samplers.ToDictionary(x => x.Name, x => x.Path, StringComparer.OrdinalIgnoreCase);
        Samplers.Clear();
        foreach (var (name, defaultPath) in SamplersForShader(ShaderChoices[ShaderIndex]))
            Samplers.Add(new AddMeshSamplerViewModel
            {
                Name = name,
                Exists = TextureExists,
                Path = kept.TryGetValue(name, out var already) && already.Length > 0 ? already : defaultPath,
            });
        OnPropertyChanged(nameof(HasSamplers));
    }

    partial void OnShaderIndexChanged(int value) => RefreshSamplers();

    public required ImportedSceneMaterial Source { get; init; }
    public required IReadOnlyList<string> ExistingMaterials { get; init; }
    public required IReadOnlyList<string> ShaderChoices { get; init; }   // League shaders from the catalogue

    /// <summary>0 = use an existing map material, 1 = create a new one from a League shader,
    /// 2 = copy the real material out of the source map's own .materials.bin (M512).</summary>
    [ObservableProperty] private int _mode = 1;

    /// <summary>M512: set when the import came from a mapgeo whose sibling .materials.bin actually holds
    /// this material. Copying it is the best option by a distance — the shader, samplers, macros and
    /// render state are Riot's own, and the permutation is cooked by definition because the game ships
    /// it. Only offered when it is really there.</summary>
    public string? SourceBinPath { get; init; }
    public bool CanCopyFromSource => SourceBinPath is { Length: > 0 };
    [ObservableProperty] private string? _existingMaterial;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private int _shaderIndex;
    [ObservableProperty] private bool _useImportedTexture;

    public bool IsExistingMode => Mode == 0;
    public bool IsNewMode => Mode == 1;
    public bool IsCopyMode => Mode == 2;
    partial void OnModeChanged(int value)
    {
        OnPropertyChanged(nameof(IsExistingMode));
        OnPropertyChanged(nameof(IsNewMode));
        OnPropertyChanged(nameof(IsCopyMode));
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
    string? TextureFileNameHint,
    // M512: copy the material verbatim out of another map's bin instead of building one.
    string? CopyFromBin = null,
    string? CopyFromMaterial = null,
    // M514: sampler name -> texture path, as set up in the window.
    IReadOnlyDictionary<string, string>? SamplerPaths = null);

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
    /// <summary>M514: (shader) -> its declared samplers, so the window can offer them for setup.</summary>
    public Func<string, IReadOnlyList<(string Name, string DefaultPath)>>? SamplersForShader { get; init; }
    /// <summary>M514: does this texture path resolve? Used to mark a sampler that would draw nothing.</summary>
    public Func<string, bool>? TextureExists { get; init; }
    public Action<AddMeshPlan>? Confirmed;
    public Action? Cancelled;

    private ImportedScene? _scene;

    public void SetVisibilityLayers(IEnumerable<VisibilityLayer> layers)
    {
        Layers.Clear();
        foreach (var layer in layers)
            Layers.Add(new LayerToggle { Name = layer.Name, Bit = layer.Bit });
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
        string? sourceBin = null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".mapgeo")
        {
            // M512: a League map is the richest source of League-shaped geometry, and its materials are
            // already right. The sibling bin is where they live.
            scene = ReyEngine.Formats.MapGeo.MapGeoMeshImporter.ToScene(File.ReadAllBytes(path), out err);
            string sibling = Path.ChangeExtension(path, null) + ".materials.bin";
            if (File.Exists(sibling)) sourceBin = sibling;
        }
        else if (ext is ".obj" or ".scb" or ".sco")
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
                SamplersForShader = SamplersForShader,
                TextureExists = TextureExists,
                SourceBinPath = sourceBin,
                // Copying the real thing beats rebuilding it from a shader whenever it is available.
                Mode = sourceBin is null ? 1 : 2,
            });
        // Fill the sampler rows for whatever shader each material starts on.
        foreach (var m in Materials) m.RefreshSamplers();
        HasScene = true;
        int totalVerts = scene.Meshes.Sum(m => m.Positions.Length / 3);
        Status = $"{scene.Meshes.Count} mesh(es), {scene.Materials.Count} material(s), {totalVerts:n0} verts total."
            + (sourceBin is not null ? $" Materials can be copied from {Path.GetFileName(sourceBin)}." : "");
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
            bool copyFromSource = m.Mode == 2 && m.CanCopyFromSource;
            string final = createNew || copyFromSource
                ? m.NewName.Trim()
                : m.ExistingMaterial ?? "";
            if ((createNew || copyFromSource) && final.Length == 0)
            { Status = $"Material '{m.Source.Name}' needs a name."; return; }
            if (!createNew && !copyFromSource && final.Length == 0)
            { Status = $"Material '{m.Source.Name}': pick an existing material."; return; }

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
            // M514: only the samplers the user actually filled in. An empty path means "leave the
            // shader's own default", which is a different intent from "point it at nothing".
            var samplerPaths = m.Samplers
                .Where(x => x.Path.Trim().Length > 0)
                .ToDictionary(x => x.Name, x => x.Path.Trim(), StringComparer.OrdinalIgnoreCase);

            plans.Add(new AddMeshMaterialPlan(m.Source.Name, createNew, m.ExistingMaterial, final, shader,
                texBytes, texHint,
                copyFromSource ? m.SourceBinPath : null,
                copyFromSource ? m.Source.Name : null,
                samplerPaths.Count > 0 ? samplerPaths : null));
            nameMap[m.Source.Name] = final;
        }

        int mask = 0;
        foreach (var l in Layers) if (l.IsOn) mask |= l.Bit;
        if (mask == 0) mask = 255;   // no layers picked = visible everywhere, never invisible

        Confirmed?.Invoke(new AddMeshPlan(include, plans, nameMap, mask));
    }

    [RelayCommand] private void Cancel() => Cancelled?.Invoke();
}
