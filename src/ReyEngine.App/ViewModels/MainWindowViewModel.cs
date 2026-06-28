using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Imaging;
using ReyEngine.App.Services;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly Logger _log = new();
    private readonly HashSyncService _sync = new();
    private readonly WadPathResolver _resolver;
    private WadArchive? _archive;
    private readonly AssetOverrideStore _overrides = new();
    private readonly Dictionary<ulong, AssetNodeViewModel> _nodesByHash = new();

    public DialogService Dialogs { get; } = new();
    public ConsoleViewModel Console { get; } = new();
    public InspectorViewModel Inspector { get; } = new();
    public MeshInspectorViewModel MeshInspector { get; } = new();
    public MapGeoInspectorViewModel MapGeoInspector { get; } = new();
    public ObservableCollection<AssetNodeViewModel> RootNodes { get; } = new();
    public ObservableCollection<BinNodeViewModel> BinTreeRoots { get; } = new();

    [ObservableProperty] private AssetNodeViewModel? _selectedNode;
    [ObservableProperty] private string _title = "ReyEngine";
    [ObservableProperty] private string _status = "Ready — open a .wad.client to begin";
    [ObservableProperty] private string _hashInput = "";
    [ObservableProperty] private ReyProject _project = new();
    [ObservableProperty] private bool _isBuilding;

    // Viewport-bound state
    [ObservableProperty] private MeshAsset? _currentMesh;
    [ObservableProperty] private SkeletonAsset? _currentSkeleton;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelTextures;
    [ObservableProperty] private bool _showWireframe;
    [ObservableProperty] private bool _showBones;
    [ObservableProperty] private bool _showBounds;
    [ObservableProperty] private bool _hasBinTree;

    public MainWindowViewModel()
    {
        _log.AddSink(Console);
        Project.GameDirectory = ReyProject.GuessGameDirectory();
        _log.Info("ReyEngine", "Editor started.");
        if (!string.IsNullOrEmpty(Project.GameDirectory))
            _log.Info("Project", $"Game directory: {Project.GameDirectory}");

        var db = _sync.LoadLocal(m => _log.Info("Hashes", m));
        _resolver = new WadPathResolver(db);
        if (db.WadCount + db.BinCount == 0)
            _log.Warn("Hashes", "No hash dictionary yet. Use Tools ▸ Sync Hashes to download from CommunityDragon.");
    }

    // ---- WAD ------------------------------------------------------------

    [RelayCommand]
    private async Task OpenWad()
    {
        var path = await Dialogs.OpenFileAsync("Open WAD archive", DialogService.Wad, DialogService.All);
        if (path is not null) LoadWad(path);
    }

    public void LoadWad(string path)
    {
        try
        {
            _log.Info("WAD", $"Opening {Path.GetFileName(path)} …");
            _archive?.Dispose();
            _archive = WadArchive.Open(path, _resolver);
            RebuildTree();
            ClearViewport();
            Inspector.Clear();

            _log.Success("WAD", $"Loaded {_archive.Entries.Count:n0} chunks; resolved {_archive.ResolvedCount:n0} paths.");
            Status = $"{_archive.Name} — {_archive.Entries.Count:n0} entries · {_archive.ResolvedCount:n0} resolved";
            Title = $"ReyEngine — {_archive.Name}";
        }
        catch (Exception ex)
        {
            _log.Error("WAD", ex.Message);
        }
    }

    private void RebuildTree()
    {
        if (_archive is null) return;
        var root = AssetTree.Build(_archive.Entries, _archive.Name);
        RootNodes.Clear();
        _nodesByHash.Clear();
        var rootVm = new AssetNodeViewModel(root);
        IndexNodes(rootVm);
        RootNodes.Add(rootVm);
        RefreshAllStatuses();
    }

    private void IndexNodes(AssetNodeViewModel node)
    {
        if (node.Entry is { } e) _nodesByHash[e.PathHash] = node;
        foreach (var c in node.Children) IndexNodes(c);
    }

    private void RefreshAllStatuses()
    {
        foreach (var ov in _overrides.All)
            if (_nodesByHash.TryGetValue(ov.PathHash, out var node)) node.Status = AssetStatus.Modified;
    }

    private void SetNodeStatus(ulong hash, AssetStatus status)
    {
        if (_nodesByHash.TryGetValue(hash, out var node)) node.Status = status;
    }

    /// <summary>Bytes for an asset — the project override if one exists, otherwise the WAD chunk.</summary>
    private byte[] GetAssetBytes(WadAssetEntry entry)
    {
        if (_overrides.TryGet(entry.PathHash, out var ov) && File.Exists(ov.OverrideFile))
            return File.ReadAllBytes(ov.OverrideFile);
        return _archive!.Extract(entry);
    }

    [RelayCommand]
    private void ReloadWad()
    {
        if (_archive is null) { _log.Warn("WAD", "No archive is open."); return; }
        LoadWad(_archive.FilePath);
    }

    [RelayCommand]
    private async Task ExportSelected()
    {
        var entry = SelectedNode?.Entry;
        if (entry is null || _archive is null) { _log.Warn("Export", "Select a file first."); return; }
        var outPath = await Dialogs.SaveFileAsync("Export asset", entry.DisplayName);
        if (outPath is null) return;
        try
        {
            _archive.ExtractToFile(entry, outPath);
            _log.Success("Export", $"Wrote {outPath}");
        }
        catch (Exception ex) { _log.Error("Export", ex.Message); }
    }

    // ---- Hashes ---------------------------------------------------------

    [RelayCommand]
    private async Task SyncHashes()
    {
        try
        {
            Status = "Syncing CommunityDragon hashes…";
            _log.Info("Hashes", "Downloading CommunityDragon hashes…");
            var db = await Task.Run(() => _sync.SyncAsync(m => _log.Info("Hashes", m)));
            _resolver.Swap(db);
            ApplyHashesToOpenWad();
            Status = $"Hashes synced — {db.WadCount:n0} WAD + {db.BinCount:n0} bin";
        }
        catch (Exception ex)
        {
            _log.Error("Hashes", $"Sync failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ReloadLocalHashes()
    {
        var db = _sync.LoadLocal(m => _log.Info("Hashes", m));
        _resolver.Swap(db);
        ApplyHashesToOpenWad();
    }

    private void ApplyHashesToOpenWad()
    {
        if (_archive is null) return;
        int resolved = _resolver.RefreshArchive(_archive);
        RebuildTree();
        _log.Success("Hashes", $"Resolved {resolved:n0} / {_archive.Entries.Count:n0} WAD paths.");
        Status = $"{_archive.Name} — {_archive.Entries.Count:n0} entries · {resolved:n0} resolved";
    }

    [RelayCommand]
    private void HashLookup()
    {
        if (string.IsNullOrWhiteSpace(HashInput)) { _log.Warn("Hash", "Type a path/string in the toolbar box."); return; }
        var s = HashInput.Trim();
        _log.Info("Hash", $"\"{s}\"");

        ulong wadHash = HashAlgorithms.WadPath(s);
        uint binHash = HashAlgorithms.Fnv1a(s);
        _log.Info("Hash", $"   xxhash64 (wad) = 0x{wadHash:x16}");
        _log.Info("Hash", $"   fnv1a    (bin) = 0x{binHash:x8}");
        _log.Info("Hash", $"   elf            = 0x{HashAlgorithms.Elf(s):x8}");

        LogCandidates("wad", _resolver.Database.WadCandidates(wadHash));
        LogCandidates("bin", _resolver.Database.BinCandidates(binHash));

        // If the user typed a raw hash, reverse-resolve it.
        var hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        if (hex.Length == 16 && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var h64))
            LogCandidates("wad↩", _resolver.Database.WadCandidates(h64));
        else if (hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var h32))
            LogCandidates("bin↩", _resolver.Database.BinCandidates(h32));
    }

    private void LogCandidates(string tag, IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0) return;
        if (candidates.Count == 1) { _log.Success("Hash", $"   {tag} → {candidates[0]}"); return; }
        _log.Warn("Hash", $"   {tag} CONFLICT ({candidates.Count} candidates):");
        foreach (var c in candidates) _log.Warn("Hash", $"      • {c}");
    }

    // ---- Selection / preview -------------------------------------------

    partial void OnSelectedNodeChanged(AssetNodeViewModel? value)
    {
        var entry = value?.Entry;
        if (entry is null) return;

        // Unresolved chunks have no extension — sniff the type from magic bytes so
        // preview/decode still works before a hash sync (guard against huge chunks).
        if (entry.Type == AssetType.Unknown && _archive is not null && entry.UncompressedSize < 32 * 1024 * 1024)
        {
            try { entry.Type = AssetTypeDetector.FromMagic(_archive.Extract(entry)); }
            catch { /* leave Unknown */ }
        }

        Inspector.ShowEntry(entry);
        Inspector.SetPreview(null);
        bool modified = _overrides.Has(entry.PathHash);
        Inspector.SetAssetStatus(
            modified ? "Modified — Project Override" : "Original — WAD",
            modified && _overrides.TryGet(entry.PathHash, out var ov) ? ov.OverrideFile : null);

        if (entry.Type is not AssetType.SkinnedMesh) ClearViewport();
        if (entry.Type != AssetType.Bin) ClearBinTree();

        switch (entry.Type)
        {
            case AssetType.Texture or AssetType.Dds:
                _ = TryPreviewTextureAsync(entry);
                break;
            case AssetType.SkinnedMesh:
                _ = LoadMeshAsync(entry);
                break;
            case AssetType.MapGeometry:
                _ = LoadMapGeoAsync(entry);
                break;
            case AssetType.Bin:
                _ = LoadBinAsync(entry);
                break;
        }
    }

    private void ClearViewport()
    {
        CurrentMesh = null;
        CurrentSkeleton = null;
        CurrentModelTextures = null;
        MeshInspector.Clear();
        MapGeoInspector.Clear();
    }

    private async Task LoadBinAsync(WadAssetEntry entry)
    {
        if (_archive is null) return;
        try
        {
            var doc = await Task.Run(() =>
                BinDocument.Parse(_archive.Extract(entry),
                    h => _resolver.Database.TryGetBinName(h, out var n) ? n : null));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                BinTreeRoots.Clear();
                foreach (var root in doc.Roots) BinTreeRoots.Add(new BinNodeViewModel(root));
                HasBinTree = BinTreeRoots.Count > 0;
                _log.Info("Bin", $"{entry.DisplayName}: {doc.Roots.Count} object(s)" +
                                 (doc.Dependencies.Count > 0 ? $", {doc.Dependencies.Count} dependencies" : ""));
            });
        }
        catch (Exception ex)
        {
            _log.Error("Bin", $"{entry.DisplayName}: {ex.Message}");
        }
    }

    private void ClearBinTree()
    {
        if (BinTreeRoots.Count == 0 && !HasBinTree) return;
        BinTreeRoots.Clear();
        HasBinTree = false;
    }

    private async Task TryPreviewTextureAsync(WadAssetEntry entry)
    {
        if (_archive is null) return;
        try
        {
            var img = await Task.Run(() => TextureDecoder.Decode(GetAssetBytes(entry)));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Inspector.SetPreview(BitmapFactory.FromRgba(img));
                _log.Info("Preview", $"Decoded {entry.DisplayName} ({img.Width}×{img.Height}).");
            });
        }
        catch (Exception ex) { _log.Error("Preview", $"{entry.DisplayName}: {ex.Message}"); }
    }

    private async Task LoadMeshAsync(WadAssetEntry entry)
    {
        if (_archive is null) return;
        try
        {
            var (mesh, skeleton, textures) = await Task.Run(() =>
            {
                var m = SkinnedMeshDecoder.Decode(_archive.Extract(entry));
                var s = TryPairSkeleton(entry);
                var t = TryLoadTextures(entry, m);
                return (m, s, t);
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentMesh = mesh;
                CurrentSkeleton = skeleton;
                CurrentModelTextures = textures;
                ShowBones = skeleton is not null;
                MeshInspector.ShowMesh(mesh, skeleton);
                _log.Success("Mesh", $"{entry.DisplayName}: {mesh.VertexCount:n0} verts, {mesh.TriangleCount:n0} tris, {mesh.SubMeshes.Count} submesh(es)" +
                                     (skeleton is null ? "" : $", {skeleton.BoneCount} bones"));
            });
        }
        catch (Exception ex)
        {
            _log.Error("Mesh", $"{entry.DisplayName}: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(ClearViewport);
        }
    }

    private async Task LoadMapGeoAsync(WadAssetEntry entry)
    {
        if (_archive is null) return;
        try
        {
            _log.Info("MapGeo", $"Decoding {entry.DisplayName} …");
            var (map, mesh, textures) = await Task.Run(() =>
            {
                var m = MapGeoDecoder.Decode(_archive.Extract(entry));
                var meshAsset = new MeshAsset
                {
                    Positions = m.Positions,
                    Normals = m.Normals,
                    Uvs = m.Uvs,
                    Indices = m.Indices,
                    VertexCount = m.VertexCount,
                    SubMeshes = m.Groups.Select(g => new SubMeshInfo(g.Material, g.StartIndex, g.IndexCount, 0)).ToList(),
                    BoundsMin = m.BoundsMin,
                    BoundsMax = m.BoundsMax,
                };
                var tex = TryLoadMapTextures(entry, m);
                return (m, meshAsset, tex);
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentSkeleton = null;
                ShowBones = false;
                CurrentMesh = mesh;
                CurrentModelTextures = textures;
                MapGeoInspector.Show(map, entry.Path);
                _log.Success("MapGeo", $"{entry.DisplayName}: v{map.Version}, {map.MeshCount:n0} meshes, {map.VertexCount:n0} verts, {map.TriangleCount:n0} tris, {map.MaterialCount} materials" +
                                       (map.Warnings.Count > 0 ? $", {map.Warnings.Count} warnings" : ""));
            });
        }
        catch (Exception ex)
        {
            _log.Error("MapGeo", $"{entry.DisplayName}: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(ClearViewport);
        }
    }

    /// <summary>Resolve the map's materials .bin → per-group diffuse textures (shared instances for reuse).</summary>
    private IReadOnlyList<TextureImage?>? TryLoadMapTextures(WadAssetEntry mapEntry, MapGeoAsset map)
    {
        if (_archive is null || !mapEntry.IsResolved) return null;

        var binPath = MapGeoMaterialResolver.MaterialsBinPathFor(mapEntry.Path);
        if (!_archive.TryGetEntry(HashAlgorithms.WadPath(binPath), out var binEntry))
        {
            _log.Info("MapGeo", "No materials .bin found — rendering flat.");
            return null;
        }

        var names = map.Groups.Select(g => g.Material).Where(m => m.Length > 0).Distinct().ToList();
        Dictionary<string, string> materialToTexture;
        try { materialToTexture = MapGeoMaterialResolver.Resolve(_archive.Extract(binEntry), names); }
        catch (Exception ex) { _log.Warn("MapGeo", $"materials parse failed: {ex.Message}"); return null; }
        if (materialToTexture.Count == 0) return null;

        var cache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        TextureImage? Load(string path)
        {
            if (cache.TryGetValue(path, out var hit)) return hit;
            TextureImage? img = null;
            if (_archive.TryGetEntry(HashAlgorithms.WadPath(path), out var te))
            {
                try { img = TextureDecoder.Decode(_archive.Extract(te)); } catch { /* unsupported */ }
            }
            cache[path] = img;
            return img;
        }

        var result = new TextureImage?[map.Groups.Count];
        for (int i = 0; i < map.Groups.Count; i++)
            if (materialToTexture.TryGetValue(map.Groups[i].Material, out var path))
                result[i] = Load(path);

        int unique = cache.Values.Count(v => v is not null);
        _log.Success("MapGeo", $"Loaded {unique} unique textures ({materialToTexture.Count}/{names.Count} materials resolved).");
        return result;
    }

    /// <summary>Find the skin .bin for a .skn, extract per-submesh diffuse textures, decode them.</summary>
    private IReadOnlyList<TextureImage?>? TryLoadTextures(WadAssetEntry skn, MeshAsset mesh)
    {
        if (_archive is null || !skn.IsResolved) return null;

        SkinMaterialInfo? material = null;
        var binPath = SkinPaths.BinPathForSkn(skn.Path);
        if (binPath is not null && _archive.TryGetEntry(HashAlgorithms.WadPath(binPath), out var binEntry))
        {
            try { material = SkinMaterialExtractor.Extract(_archive.Extract(binEntry)); }
            catch (Exception ex) { _log.Warn("Material", $"bin parse failed: {ex.Message}"); }
        }
        if (material is null || !material.HasAny)
        {
            _log.Info("Material", $"No skin material found for {skn.DisplayName} (flat shading).");
            return null;
        }

        var cache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        TextureImage? Load(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (cache.TryGetValue(path, out var hit)) return hit;
            TextureImage? img = null;
            if (_archive.TryGetEntry(HashAlgorithms.WadPath(path), out var te))
            {
                try { img = TextureDecoder.Decode(_archive.Extract(te)); } catch { /* unsupported */ }
            }
            cache[path] = img;
            return img;
        }

        var result = new TextureImage?[mesh.SubMeshes.Count];
        int loaded = 0;
        for (int i = 0; i < mesh.SubMeshes.Count; i++)
        {
            var path = material.SubmeshTexture.TryGetValue(mesh.SubMeshes[i].Material, out var p) ? p : material.DefaultTexture;
            var img = Load(path);
            result[i] = img;
            if (img is not null) loaded++;
        }
        _log.Success("Material", $"Applied {loaded}/{mesh.SubMeshes.Count} submesh textures for {skn.DisplayName}.");
        return result;
    }

    /// <summary>Find the matching .skl for a resolved .skn inside the same WAD.</summary>
    private SkeletonAsset? TryPairSkeleton(WadAssetEntry skn)
    {
        if (_archive is null || !skn.IsResolved || !skn.Path.EndsWith(".skn", StringComparison.OrdinalIgnoreCase))
            return null;
        var sklPath = skn.Path[..^4] + ".skl";
        var hash = HashAlgorithms.WadPath(sklPath);
        if (!_archive.TryGetEntry(hash, out var sklEntry)) return null;
        try { return SkeletonDecoder.Decode(_archive.Extract(sklEntry)); }
        catch { return null; }
    }

    [RelayCommand]
    private async Task AssignSkeleton()
    {
        if (SelectedNode?.Entry is not { Type: AssetType.SkinnedMesh }) { _log.Warn("Skeleton", "Select a .skn first."); return; }
        var sklType = new FilePickerFileType("Skeleton") { Patterns = new[] { "*.skl" } };
        var path = await Dialogs.OpenFileAsync("Assign skeleton (.skl)", sklType, DialogService.All);
        if (path is null) return;
        try
        {
            var skeleton = await Task.Run(() => SkeletonDecoder.Decode(File.ReadAllBytes(path)));
            CurrentSkeleton = skeleton;
            ShowBones = true;
            MeshInspector.SetSkeleton(skeleton);
            _log.Success("Skeleton", $"Assigned {Path.GetFileName(path)} ({skeleton.BoneCount} bones).");
        }
        catch (Exception ex) { _log.Error("Skeleton", ex.Message); }
    }

    // ---- Project ---------------------------------------------------------

    [RelayCommand]
    private async Task NewProject()
    {
        var wad = _archive?.FilePath ?? await Dialogs.OpenFileAsync("Select source WAD for the project", DialogService.Wad, DialogService.All);
        if (wad is null) { _log.Warn("Project", "Open a .wad.client first."); return; }

        var proj = ReyProjectService.NewFromWad(wad);
        proj.GameDirectory = Project.GameDirectory;
        Project = proj;
        _overrides.Clear();
        if (_archive is null || !string.Equals(_archive.FilePath, wad, StringComparison.OrdinalIgnoreCase)) LoadWad(wad);
        else RebuildTree();
        _log.Success("Project", $"New project '{proj.Name}' from {Path.GetFileName(wad)}.");
        UpdateTitle();
    }

    [RelayCommand]
    private async Task OpenProject()
    {
        var path = await Dialogs.OpenFileAsync("Open ReyEngine project", DialogService.Project, DialogService.All);
        if (path is null) return;
        try
        {
            var proj = ReyProjectService.Open(path);
            Project = proj;
            _overrides.LoadFrom(proj);
            if (proj.SourceWadPath is not null && File.Exists(proj.SourceWadPath)) LoadWad(proj.SourceWadPath);
            else _log.Warn("Project", "Source WAD not found — open it manually.");
            _log.Success("Project", $"Opened '{proj.Name}' with {_overrides.Count} override(s).");
            UpdateTitle();
        }
        catch (Exception ex) { _log.Error("Project", ex.Message); }
    }

    [RelayCommand]
    private async Task SaveProject()
    {
        if (Project.ProjectFilePath is null) { await SaveProjectAs(); return; }
        _overrides.SaveTo(Project);
        ReyProjectService.Save(Project, Project.ProjectFilePath);
        _log.Success("Project", $"Saved {Project.ProjectFilePath}");
        UpdateTitle();
    }

    [RelayCommand]
    private async Task SaveProjectAs()
    {
        var suggested = (string.IsNullOrEmpty(Project.Name) ? "project" : Project.Name) + ReyProjectService.Extension;
        var path = await Dialogs.SaveFileAsync("Save project as", suggested);
        if (path is null) return;
        if (!path.EndsWith(ReyProjectService.Extension, StringComparison.OrdinalIgnoreCase)) path += ReyProjectService.Extension;
        _overrides.SaveTo(Project);
        ReyProjectService.Save(Project, path);
        _log.Success("Project", $"Saved {path}");
        UpdateTitle();
    }

    private async Task<bool> EnsureProjectSavedAsync()
    {
        if (Project.SourceWadPath is null)
        {
            if (_archive is null) { _log.Warn("Project", "Open a WAD and create a project first."); return false; }
            await NewProject();
        }
        if (Project.ProjectFilePath is null) await SaveProjectAs();
        return Project.ProjectFilePath is not null;
    }

    // ---- Import / replace / revert --------------------------------------

    [RelayCommand]
    private async Task ReplaceSelected()
    {
        var entry = SelectedNode?.Entry;
        if (entry is null) { _log.Warn("Project", "Select an asset to replace."); return; }
        if (!await EnsureProjectSavedAsync()) return;

        var file = await Dialogs.OpenFileAsync($"Replace {entry.DisplayName}", DialogService.All);
        if (file is null) return;
        try
        {
            var stored = ProjectWorkspace.StoreOverride(Project, entry.PathHash, file);
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = entry.PathHash,
                ResolvedPath = entry.IsResolved ? entry.Path : null,
                OverrideFile = stored,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            SetNodeStatus(entry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            UpdateTitle();
            OnSelectedNodeChanged(SelectedNode); // refresh preview/status from override
            _log.Success("Project", $"Replaced {entry.DisplayName} with {Path.GetFileName(file)}.");
        }
        catch (Exception ex) { _log.Error("Project", ex.Message); }
    }

    [RelayCommand]
    private void RevertSelected()
    {
        var entry = SelectedNode?.Entry;
        if (entry is null || !_overrides.Has(entry.PathHash)) { _log.Warn("Project", "Selected asset is not modified."); return; }
        _overrides.Remove(entry.PathHash);
        SetNodeStatus(entry.PathHash, AssetStatus.Original);
        Project.IsDirty = true;
        UpdateTitle();
        OnSelectedNodeChanged(SelectedNode);
        _log.Success("Project", $"Reverted {entry.DisplayName} to original.");
    }

    [RelayCommand]
    private void ImportNewAsset() =>
        _log.Warn("Project", "Adding brand-new chunks needs a TOC rebuild — planned for a later milestone. Use Replace on an existing asset for now.");

    [RelayCommand]
    private async Task ExportModified()
    {
        var entry = SelectedNode?.Entry;
        if (entry is null || !_overrides.TryGet(entry.PathHash, out var ov)) { _log.Warn("Export", "Selected asset has no override."); return; }
        var outPath = await Dialogs.SaveFileAsync("Export modified asset", Path.GetFileName(ov.OverrideFile));
        if (outPath is null) return;
        try { File.Copy(ov.OverrideFile, outPath, true); _log.Success("Export", $"Wrote {outPath}"); }
        catch (Exception ex) { _log.Error("Export", ex.Message); }
    }

    [RelayCommand]
    private async Task CopyResolvedPath()
    {
        var entry = SelectedNode?.Entry;
        if (entry is null) return;
        await Dialogs.CopyAsync(entry.Path);
        _log.Info("Clipboard", entry.Path);
    }

    [RelayCommand]
    private async Task CopyHash()
    {
        var entry = SelectedNode?.Entry;
        if (entry is null) return;
        var h = $"0x{entry.PathHash:x16}";
        await Dialogs.CopyAsync(h);
        _log.Info("Clipboard", h);
    }

    // ---- Build -----------------------------------------------------------

    [RelayCommand]
    private async Task BuildPackage()
    {
        if (!await EnsureProjectSavedAsync()) return;
        _overrides.SaveTo(Project);
        ReyProjectService.Save(Project, Project.ProjectFilePath!);

        string buildDir;
        try { buildDir = ProjectWorkspace.BuildDir(Project); }
        catch (Exception ex) { _log.Error("Build", ex.Message); return; }
        var outPath = Path.Combine(buildDir, Path.GetFileName(Project.SourceWadPath!));

        if (BuildSafety.IsInsideGameInstall(outPath))
        {
            _log.Error("Build", "Refusing to write the build into a Riot/League install folder. Change the project output directory.");
            return;
        }

        _log.Info("Build", $"Building '{Project.Name}' → {outPath}");
        if (_overrides.Count == 0) _log.Warn("Build", "No overrides — output will mirror the source WAD.");
        IsBuilding = true;
        Status = "Building package…";
        try
        {
            var report = await Task.Run(() => BuildPackageService.Build(Project, outPath, null, CancellationToken.None));
            LogBuildReport(report);
            Status = report.Success
                ? $"Built {Path.GetFileName(outPath)} — {report.OutputSize / 1024.0 / 1024.0:0.0} MB in {report.Duration.TotalSeconds:0.0}s"
                : "Build failed — see console.";
        }
        catch (Exception ex) { _log.Error("Build", ex.Message); }
        finally { IsBuilding = false; }
    }

    private void LogBuildReport(BuildReport r)
    {
        _log.Info("Build", $"chunks: {r.ChunksTotal:n0} total · {r.ChunksReplaced} replaced · {r.ChunksCopied:n0} copied · {r.ChunksFailed} failed");
        foreach (var issue in r.Issues)
        {
            switch (issue.Severity)
            {
                case BuildSeverity.Error: _log.Error("Build", issue.Message); break;
                case BuildSeverity.Warning: _log.Warn("Build", issue.Message); break;
                default: _log.Info("Build", issue.Message); break;
            }
        }
        if (!string.IsNullOrEmpty(r.Validation)) _log.Info("Build", r.Validation);
        if (r.Success) _log.Success("Build", $"Output ready: {r.OutputPath}  ({r.OutputSize / 1024.0 / 1024.0:0.0} MB). Open it via File ▸ Open WAD to verify.");
    }

    [RelayCommand]
    private void OpenBuildFolder()
    {
        try
        {
            var dir = ProjectWorkspace.BuildDir(Project);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) { _log.Warn("Build", ex.Message); }
    }

    private void UpdateTitle()
    {
        var name = string.IsNullOrEmpty(Project.Name) ? "Untitled" : Project.Name;
        Title = $"ReyEngine — {name}{(Project.IsDirty ? " *" : "")}" + (_archive is not null ? $" — {_archive.Name}" : "");
    }

    // ---- Misc commands --------------------------------------------------

    [RelayCommand] private void ShaderPreview() => _log.Warn("Shader", "Shader/material preview lands in a later milestone.");
    [RelayCommand] private void ClearConsole() => Console.Clear();

    [RelayCommand]
    private void Exit() =>
        (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
}
