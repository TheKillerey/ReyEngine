using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>One imported mesh row — include it or not.</summary>
public sealed partial class AddMeshRowViewModel : ObservableObject
{
    public required ImportedSceneMesh Mesh { get; init; }
    [ObservableProperty] private bool _include = true;

    /// <summary>Raised so the window can re-derive which materials still need setting up. A mapgeo brings
    /// in hundreds of materials; only the ones an included mesh actually uses are worth a card.</summary>
    public Action? IncludeChanged;
    partial void OnIncludeChanged(bool value) => IncludeChanged?.Invoke();

    public string Info => $"{Mesh.Positions.Length / 3:n0} verts · {Mesh.Indices.Length / 3:n0} tris · mat '{Mesh.MaterialName}'";
    public bool TooLarge => Mesh.Positions.Length / 3 > 65535;

    /// <summary>Everything the search box matches against, lower-cased once.</summary>
    public string SearchText => _searchText ??= (Mesh.Name + " " + Mesh.MaterialName).ToLowerInvariant();
    private string? _searchText;
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

    private bool _samplersBuilt;

    /// <summary>M654: build them the first time this card appears and not again. The card list is rebuilt
    /// on every mesh tick, and a mapgeo selection can carry hundreds of materials - re-deriving every
    /// one of their sampler sets per click is a visible hitch for no gain.</summary>
    public void EnsureSamplers()
    {
        if (!_samplersBuilt) RefreshSamplers();
    }

    /// <summary>Rebuild the sampler rows for the currently chosen shader, keeping any path the user has
    /// already typed for a sampler of the same name.</summary>
    public void RefreshSamplers()
    {
        _samplersBuilt = true;
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

    /// <summary>M512: the bin the original material is copied out of. Copying it is the best option by a
    /// distance — the shader, samplers, macros and render state are Riot's own, and the permutation is
    /// cooked by definition because the game ships it.
    ///
    /// <para>M654: settable, because the sibling file is not always there. A mapgeo pulled out of a WAD
    /// on its own has no <c>.materials.bin</c> next to it, and until M654 that silently meant "the
    /// original material cannot be carried over" with no way to say where it lives.</para></summary>
    [ObservableProperty] private string? _sourceBinPath;

    /// <summary>M654: is THIS material actually an object in that bin? Checked by the same lookup the
    /// import uses (FNV-1a of the name against the object table), so the option is offered exactly when
    /// it will work rather than whenever a file happens to sit next to the mapgeo.</summary>
    [ObservableProperty] private bool _isInSourceBin;

    public bool CanCopyFromSource => SourceBinPath is { Length: > 0 } && IsInSourceBin;
    partial void OnSourceBinPathChanged(string? value) => RaiseCopyAvailability();
    partial void OnIsInSourceBinChanged(bool value) => RaiseCopyAvailability();

    private void RaiseCopyAvailability()
    {
        OnPropertyChanged(nameof(CanCopyFromSource));
        OnPropertyChanged(nameof(CopyNote));
        // Never leave the row sitting on an option that is no longer reachable.
        if (Mode == 2 && !CanCopyFromSource) Mode = 1;
    }

    public string CopyNote => CanCopyFromSource
        ? "Copied verbatim from the source map's materials.bin — shader, samplers, macros and render state included."
        : SourceBinPath is { Length: > 0 }
            ? "Not in the chosen materials.bin — pick the bin that belongs to this mapgeo."
            : "No materials.bin chosen, so the original cannot be copied.";

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
///
/// <para>M654: made usable for a mapgeo source. A shipping mapgeo is not a model, it is a library —
/// base_srx alone offers 601 meshes across 186 materials, and banner_test 1,664 across 277. With every
/// row ticked by default and no way to filter, "Add To Map" meant "append all of Summoner's Rift".
/// So: a search box, select all / none over whatever the search leaves, nothing ticked when the import
/// is clearly a library rather than a model, and a material card only for the materials the meshes you
/// actually ticked use.</para>
/// </summary>
public sealed partial class AddMeshWindowViewModel : ObservableObject
{
    /// <summary>Above this many meshes an import is a library to pick from, not a model to add, so
    /// nothing starts ticked. Riot's smallest shipping mapgeo is already several times this.</summary>
    public const int AutoIncludeLimit = 64;

    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _status = "Choose a mesh file to import.";
    [ObservableProperty] private bool _hasScene;

    /// <summary>Every mesh in the import. <see cref="VisibleMeshes"/> is what the list shows.</summary>
    public ObservableCollection<AddMeshRowViewModel> Meshes { get; } = new();
    public ObservableCollection<AddMeshRowViewModel> VisibleMeshes { get; } = new();
    /// <summary>Only the materials an included mesh actually uses — see the class remark.</summary>
    public ObservableCollection<AddMeshMaterialViewModel> Materials { get; } = new();
    public ObservableCollection<LayerToggle> Layers { get; } = new();

    private readonly List<AddMeshMaterialViewModel> _allMaterials = new();
    private bool _suspendRefilter;

    [ObservableProperty] private string _meshSearch = "";
    partial void OnMeshSearchChanged(string value) => ApplySearch();

    /// <summary>M654: where the original materials are copied from. Pre-filled with the sibling bin when
    /// one is there, and pickable when it is not.</summary>
    [ObservableProperty] private string _sourceBinPath = "";
    [ObservableProperty] private string _sourceBinNote = "";
    [ObservableProperty] private bool _showSourceBinRow;

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
    /// <summary>M654: same contract as <see cref="PickFile"/>, for the source .materials.bin.</summary>
    public Func<string, Task<string?>>? PickBin;
    /// <summary>M514: (shader) -> its declared samplers, so the window can offer them for setup.</summary>
    public Func<string, IReadOnlyList<(string Name, string DefaultPath)>>? SamplersForShader { get; init; }
    /// <summary>M514: does this texture path resolve? Used to mark a sampler that would draw nothing.</summary>
    public Func<string, bool>? TextureExists { get; init; }
    public Action<AddMeshPlan>? Confirmed;
    public Action? Cancelled;

    private ImportedScene? _scene;

    public int IncludedCount => Meshes.Count(m => m.Include && !m.TooLarge);
    public string SelectionSummary => Meshes.Count == 0
        ? ""
        : VisibleMeshes.Count == Meshes.Count
            ? $"{IncludedCount:n0} of {Meshes.Count:n0} selected"
            : $"{IncludedCount:n0} of {Meshes.Count:n0} selected · {VisibleMeshes.Count:n0} shown";
    public string MaterialsSummary => Materials.Count == 0
        ? "Tick a mesh above and the material it uses shows up here."
        : $"{Materials.Count:n0} of {_allMaterials.Count:n0} material(s) — only the ones your selection uses.";

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

    [RelayCommand]
    private async Task BrowseSourceBin()
    {
        if (PickBin is null) return;
        var path = await PickBin("Materials .bin that belongs to this mapgeo");
        if (path is not null) UseSourceBin(path);
    }

    public void LoadFile(string path)
    {
        ImportedScene? scene;
        string? err = null;
        string? sourceBin = null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        bool isMapGeo = ext is ".mapgeo";
        if (isMapGeo)
        {
            // M512: a League map is the richest source of League-shaped geometry, and its materials are
            // already right. The sibling bin is where they live - when it is there.
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
            Reset();
            FilePath = path;
            Status = $"Import failed: {err}";
            return;
        }
        LoadScene(scene, path, isMapGeo, sourceBin);
    }

    /// <summary>
    /// The presentation half of <see cref="LoadFile"/>: everything that happens once a scene has been
    /// decoded. Separate so the selection, search and material rules can be exercised against a scene of
    /// a chosen shape without a file of that shape having to exist.
    /// </summary>
    public void LoadScene(ImportedScene scene, string path, bool isMapGeo, string? sourceBin = null)
    {
        Reset();
        FilePath = path;
        _scene = scene;

        // A library, not a model: ticking 601 meshes by default and offering no way to untick them is
        // how "Add To Map" came to mean "append the whole source map".
        bool includeByDefault = scene.Meshes.Count <= AutoIncludeLimit;
        _suspendRefilter = true;
        foreach (var m in scene.Meshes)
            Meshes.Add(new AddMeshRowViewModel { Mesh = m, Include = includeByDefault, IncludeChanged = OnIncludeChanged });

        int defaultShader = 0;
        for (int i = 0; i < ShaderChoices.Count; i++)
            if (ShaderChoices[i].EndsWith("DefaultEnv_Flat", StringComparison.OrdinalIgnoreCase)) { defaultShader = i; break; }
        foreach (var mat in scene.Materials)
            _allMaterials.Add(new AddMeshMaterialViewModel
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
            });
        _suspendRefilter = false;

        ShowSourceBinRow = isMapGeo;
        HasScene = true;
        ApplySearch();
        RebuildMaterialList();
        if (sourceBin is not null) UseSourceBin(sourceBin);
        else if (isMapGeo)
            SourceBinNote = "No materials.bin next to this mapgeo - choose one to copy the original materials.";

        int totalVerts = scene.Meshes.Sum(m => m.Positions.Length / 3);
        Status = $"{scene.Meshes.Count:n0} mesh(es), {scene.Materials.Count:n0} material(s), {totalVerts:n0} verts total."
            + (includeByDefault
                ? ""
                : $" Nothing is selected - this import has more than {AutoIncludeLimit} meshes, so search for what you want and select it.");
    }

    private void Reset()
    {
        Meshes.Clear();
        VisibleMeshes.Clear();
        Materials.Clear();
        _allMaterials.Clear();
        MeshSearch = "";
        SourceBinPath = "";
        SourceBinNote = "";
        ShowSourceBinRow = false;
        _scene = null;
        HasScene = false;
    }

    /// <summary>
    /// M654: point the material cards at a bin and check, for each one, whether the original is really in
    /// there — by the same FNV-1a-of-the-name lookup the import itself does, so "Copy the original" is
    /// offered exactly when it will succeed.
    /// </summary>
    public void UseSourceBin(string path)
    {
        SourceBinPath = path;
        HashSet<uint>? objects = null;
        string? failure = null;
        try
        {
            var tree = SafeBinTree.Parse(File.ReadAllBytes(path));
            objects = tree.Objects.Keys.ToHashSet();
        }
        catch (Exception ex) { failure = ex.Message; }

        int found = 0;
        foreach (var m in _allMaterials)
        {
            bool present = objects is not null && objects.Contains(HashAlgorithms.Fnv1a(m.Source.Name));
            m.SourceBinPath = path;
            m.IsInSourceBin = present;
            if (present) { found++; if (m.Mode == 1) m.Mode = 2; }
        }

        SourceBinNote = failure is not null
            ? $"{Path.GetFileName(path)} could not be read: {failure}"
            : found == 0
                ? $"{Path.GetFileName(path)} holds none of this mapgeo's materials — is it the right bin?"
                : $"{Path.GetFileName(path)}: {found:n0} of {_allMaterials.Count:n0} original material(s) can be copied.";
    }

    private void OnIncludeChanged()
    {
        if (_suspendRefilter) return;
        RebuildMaterialList();
        RaiseSelectionSummary();
    }

    private void ApplySearch()
    {
        string needle = MeshSearch.Trim().ToLowerInvariant();
        VisibleMeshes.Clear();
        foreach (var row in Meshes)
            if (needle.Length == 0 || row.SearchText.Contains(needle, StringComparison.Ordinal))
                VisibleMeshes.Add(row);
        RaiseSelectionSummary();
    }

    /// <summary>Select all / none applies to what the search is SHOWING. That pairing is the point:
    /// type "rock", select all, and you have every rock and nothing else.</summary>
    [RelayCommand] private void SelectAllMeshes() => SetVisibleIncluded(true);
    [RelayCommand] private void SelectNoMeshes() => SetVisibleIncluded(false);

    private void SetVisibleIncluded(bool include)
    {
        _suspendRefilter = true;
        foreach (var row in VisibleMeshes)
            if (!row.TooLarge) row.Include = include;
        _suspendRefilter = false;
        RebuildMaterialList();
        RaiseSelectionSummary();
    }

    /// <summary>M654: one click for "every material comes from the source map" — a mapgeo selection can
    /// still pull in dozens of materials, and setting each one by hand is the same wall the mesh list
    /// was.</summary>
    [RelayCommand]
    private void ApplyModeToAll(string? mode)
    {
        if (!int.TryParse(mode, out int value)) return;
        foreach (var m in Materials)
            if (value != 2 || m.CanCopyFromSource) m.Mode = value;
    }

    private void RebuildMaterialList()
    {
        var used = Meshes.Where(r => r.Include && !r.TooLarge)
            .Select(r => r.Mesh.MaterialName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Materials.Clear();
        foreach (var m in _allMaterials)
            if (used.Contains(m.Source.Name)) Materials.Add(m);
        foreach (var m in Materials) m.EnsureSamplers();
        OnPropertyChanged(nameof(MaterialsSummary));
    }

    private void RaiseSelectionSummary()
    {
        OnPropertyChanged(nameof(IncludedCount));
        OnPropertyChanged(nameof(SelectionSummary));
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
