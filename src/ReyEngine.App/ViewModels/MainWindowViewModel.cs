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
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
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
    public AnimationInspectorViewModel Animation { get; } = new();
    public ObservableCollection<AssetNodeViewModel> RootNodes { get; } = new();
    public BinEditorViewModel BinEditor { get; } = new();
    public MaterialEditorViewModel MaterialEditor { get; } = new();

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
    [ObservableProperty] private AnimationClip? _currentAnimation;
    [ObservableProperty] private double _animationTime;
    [ObservableProperty] private bool _showWireframe;
    [ObservableProperty] private bool _showBones;
    [ObservableProperty] private bool _showBounds;
    [ObservableProperty] private bool _hasMaterialData;
    [ObservableProperty] private bool _hasInspectorBody;
    [ObservableProperty] private int _inspectorTab;
    private MapGeoAsset? _currentMap;

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

        Animation.ClipLoader = DecodeAnimation;
        Animation.ClipChanged = clip => CurrentAnimation = clip;
        Animation.TimeChanged = t => AnimationTime = t;

        BinEditor.CopyHandler = Dialogs.CopyAsync;

        MaterialEditor.CopyHandler = Dialogs.CopyAsync;
        MaterialEditor.TextureExists = TextureExistsByPath;
        MaterialEditor.LoadThumbnail = LoadThumbnailByPath;
        MaterialEditor.OpenTexture = OpenTextureByPath;
        MaterialEditor.ReplaceTextureAsset = ReplaceTextureForSlot;
        MaterialEditor.ApplyToViewport = ApplyMaterialToViewport;
        MaterialEditor.SaveOverride = SaveMaterialOverride;
    }

    // ---- Material editor: asset access helpers --------------------------

    private byte[]? ReadAssetByPath(string path)
    {
        if (_archive is null || string.IsNullOrEmpty(path)) return null;
        var hash = HashAlgorithms.WadPath(path);
        if (_overrides.TryGet(hash, out var ov) && File.Exists(ov.OverrideFile))
            return File.ReadAllBytes(ov.OverrideFile);
        return _archive.TryGetEntry(hash, out var te) ? _archive.Extract(te) : null;
    }

    private bool TextureExistsByPath(string path)
    {
        if (_archive is null || string.IsNullOrEmpty(path)) return false;
        var hash = HashAlgorithms.WadPath(path);
        return _overrides.Has(hash) || _archive.TryGetEntry(hash, out _);
    }

    private TextureImage? LoadTextureByPath(string path)
    {
        var bytes = ReadAssetByPath(path);
        if (bytes is null) return null;
        try { return TextureDecoder.Decode(bytes); } catch { return null; }
    }

    private Avalonia.Media.Imaging.Bitmap? LoadThumbnailByPath(string path)
    {
        var img = LoadTextureByPath(path);
        return img is null ? null : BitmapFactory.FromRgba(img);
    }

    private void OpenTextureByPath(string path)
    {
        if (_archive is null) return;
        var hash = HashAlgorithms.WadPath(path);
        if (_nodesByHash.TryGetValue(hash, out var node)) SelectedNode = node;
        else _log.Warn("Material", $"Texture not found in this WAD: {path}");
    }

    // ---- Animation ------------------------------------------------------

    private AnimationClip? DecodeAnimation(WadAssetEntry entry)
    {
        if (_archive is null) return null;
        try { return AnimationDecoder.Decode(_archive.Extract(entry), entry.DisplayName); }
        catch (Exception ex) { _log.Error("Anim", $"{entry.DisplayName}: {ex.Message}"); return null; }
    }

    private IEnumerable<AnimationEntryViewModel> FindAnimations(WadAssetEntry skn)
    {
        if (_archive is null || !skn.IsResolved) return Enumerable.Empty<AnimationEntryViewModel>();
        var parts = skn.Path.Split('/');
        int ci = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
        string champ = ci >= 0 && ci + 1 < parts.Length ? parts[ci + 1] : "";
        var marker = $"/characters/{champ}/";
        return _archive.Entries
            .Where(e => e.IsResolved && e.Path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)
                        && (champ.Length == 0 || e.Path.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Select(e => new AnimationEntryViewModel(e))
            .ToList();
    }

    [RelayCommand]
    private async Task AssignAnimation()
    {
        if (CurrentMesh is not { CanSkin: true } || CurrentSkeleton is null)
        { _log.Warn("Anim", "Select a skinned champion (.skn with a skeleton) first."); return; }
        var anmType = new FilePickerFileType("Animation") { Patterns = new[] { "*.anm" } };
        var path = await Dialogs.OpenFileAsync("Assign animation (.anm)", anmType, DialogService.All);
        if (path is null) return;
        try
        {
            var clip = AnimationDecoder.Decode(File.ReadAllBytes(path), Path.GetFileName(path));
            Animation.SetExternalClip(clip);
            _log.Success("Anim", $"Assigned {Path.GetFileName(path)} ({clip.Duration:0.00}s, {clip.Fps:0.#} fps).");
        }
        catch (Exception ex) { _log.Error("Anim", ex.Message); }
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
        if (entry.Type != AssetType.Bin) BinEditor.Clear();
        HasInspectorBody = entry.Type is AssetType.SkinnedMesh or AssetType.MapGeometry or AssetType.Bin;
        InspectorTab = entry.Type == AssetType.Bin ? 2 : 0;
        if (!HasInspectorBody)
        {
            MaterialEditor.Clear();
            HasMaterialData = false;
        }

        switch (entry.Type)
        {
            case AssetType.Texture or AssetType.Dds:
                _ = TryPreviewTextureAsync(entry);
                break;
            case AssetType.SkinnedMesh:
                _ = LoadMeshAsync(entry);
                TryLoadMaterialBin(entry, alsoRawBin: true);
                break;
            case AssetType.MapGeometry:
                _ = LoadMapGeoAsync(entry);
                TryLoadMaterialBin(entry, alsoRawBin: true);
                break;
            case AssetType.Bin:
                _ = LoadBinAsync(entry);
                TryLoadMaterialBin(entry, alsoRawBin: false);
                break;
        }
    }

    // ---- Material editor: load + apply + save ---------------------------

    private string? ResolveBinName(uint h) => _resolver.Database.TryGetBinName(h, out var n) ? n : null;

    private WadAssetEntry? ResolveMaterialBin(WadAssetEntry entry)
    {
        if (_archive is null) return null;
        if (entry.Type == AssetType.Bin) return entry;
        if (!entry.IsResolved) return null;
        string? binPath = entry.Type switch
        {
            AssetType.SkinnedMesh => SkinPaths.BinPathForSkn(entry.Path),
            AssetType.MapGeometry => MapGeoMaterialResolver.MaterialsBinPathFor(entry.Path),
            _ => null,
        };
        if (binPath is null) return null;
        return _archive.TryGetEntry(HashAlgorithms.WadPath(binPath), out var be) ? be : null;
    }

    private void TryLoadMaterialBin(WadAssetEntry entry, bool alsoRawBin)
    {
        var binEntry = ResolveMaterialBin(entry);
        if (binEntry is null) { MaterialEditor.Clear(); HasMaterialData = false; return; }
        _ = LoadMaterialBinAsync(binEntry, alsoRawBin);
    }

    private async Task LoadMaterialBinAsync(WadAssetEntry binEntry, bool alsoRawBin)
    {
        if (_archive is null) return;
        byte[] bytes;
        try { bytes = GetAssetBytes(binEntry); }
        catch (Exception ex) { _log.Warn("Material", $"{binEntry.DisplayName}: {ex.Message}"); return; }

        MaterialDocument? matDoc = null;
        BinEditorDocument? binDoc = null;
        await Task.Run(() =>
        {
            try { matDoc = MaterialDocument.Parse(bytes, ResolveBinName); } catch { matDoc = null; }
            if (alsoRawBin) { try { binDoc = BinEditorDocument.Parse(bytes, ResolveBinName); } catch { binDoc = null; } }
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (alsoRawBin && binDoc is not null) BinEditor.Load(binDoc, binEntry);
            if (matDoc is not null && matDoc.Materials.Count > 0)
            {
                MaterialEditor.Load(matDoc, binEntry);
                HasMaterialData = true;
                if (MaterialEditor.UnresolvedCount > 0)
                    _log.Warn("Material", $"{binEntry.DisplayName}: {matDoc.Materials.Count} material(s), {MaterialEditor.UnresolvedCount} texture path(s) unresolved in this WAD.");
                else
                    _log.Info("Material", $"{binEntry.DisplayName}: {matDoc.Materials.Count} material(s).");
            }
            else { MaterialEditor.Clear(); HasMaterialData = false; }
        });
    }

    private void ApplyMaterialToViewport()
    {
        var bytes = MaterialEditor.Serialize();
        if (bytes is null) return;
        try
        {
            if (MaterialEditor.Kind == MaterialSourceKind.ChampionSkin && CurrentMesh is { } mesh)
            {
                var info = SkinMaterialExtractor.Extract(bytes);
                CurrentModelTextures = BuildSubmeshTextures(mesh, info, "material preview");
            }
            else if (MaterialEditor.Kind == MaterialSourceKind.MapMaterials && _currentMap is { } map && CurrentMesh is not null)
            {
                var names = map.Groups.Select(g => g.Material).Where(m => m.Length > 0).Distinct().ToList();
                var m2t = MapGeoMaterialResolver.Resolve(bytes, names);
                CurrentModelTextures = BuildMapTextures(map, m2t, names.Count);
            }
            else { _log.Info("Material", "Nothing in the viewport to preview — select the matching .skn/.mapgeo."); return; }
            _log.Success("Material", "Applied material edits to the viewport (live).");
        }
        catch (Exception ex) { _log.Error("Material", $"Apply failed: {ex.Message}"); }
    }

    private async Task SaveMaterialOverride()
    {
        if (MaterialEditor.BinEntry is not { } binEntry) { _log.Warn("Material", "No material .bin open."); return; }
        if (!MaterialEditor.IsDirty) { _log.Info("Material", "No material edits to save."); return; }
        if (!await EnsureProjectSavedAsync()) return;

        var bytes = MaterialEditor.Serialize();
        if (bytes is null) return;
        try { _ = new LeagueToolkit.Core.Meta.BinTree(new MemoryStream(bytes, false)); }
        catch (Exception ex) { _log.Error("Material", $"Edited material .bin failed to re-parse — NOT saved: {ex.Message}"); return; }

        try
        {
            var dest = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, bytes, ".bin");
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = binEntry.PathHash,
                ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                OverrideFile = dest,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            UpdateTitle();
            ApplyMaterialToViewport();
            _log.Success("Material", $"Saved edited material {binEntry.DisplayName} to override ({bytes.Length:n0} bytes, re-parse OK). Build Package will include it.");
        }
        catch (Exception ex) { _log.Error("Material", ex.Message); }
    }

    private async Task ReplaceTextureForSlot(TextureSlotViewModel slot)
    {
        if (_archive is null) return;
        var path = slot.EditedPath;
        var hash = HashAlgorithms.WadPath(path);
        if (!_archive.TryGetEntry(hash, out _)) { _log.Warn("Material", $"Texture not in this WAD — can't replace: {path}"); return; }
        if (!await EnsureProjectSavedAsync()) return;

        var file = await Dialogs.OpenFileAsync($"Replace texture {Path.GetFileName(path)} (.dds/.tex)", DialogService.All);
        if (file is null) return;
        try
        {
            var stored = ProjectWorkspace.StoreOverride(Project, hash, file);
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = hash,
                ResolvedPath = path,
                OverrideFile = stored,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            SetNodeStatus(hash, AssetStatus.Modified);
            Project.IsDirty = true;
            UpdateTitle();
            slot.RefreshResolved();
            ApplyMaterialToViewport();
            _log.Success("Material", $"Replaced texture {Path.GetFileName(path)} with {Path.GetFileName(file)} (raw). Build Package will include it.");
        }
        catch (Exception ex) { _log.Error("Material", ex.Message); }
    }

    private void ClearViewport()
    {
        CurrentMesh = null;
        CurrentSkeleton = null;
        _currentMap = null;
        CurrentModelTextures = null;
        CurrentAnimation = null;
        AnimationTime = 0;
        Animation.Clear();
        MeshInspector.Clear();
        MapGeoInspector.Clear();
    }

    private async Task LoadBinAsync(WadAssetEntry entry)
    {
        if (_archive is null) return;
        try
        {
            var doc = await Task.Run(() =>
                BinEditorDocument.Parse(_archive.Extract(entry),
                    h => _resolver.Database.TryGetBinName(h, out var n) ? n : null));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                BinEditor.Load(doc, entry);
                _log.Info("Bin", $"{entry.DisplayName}: {doc.Roots.Count} object(s)" +
                                 (doc.Dependencies.Count > 0 ? $", {doc.Dependencies.Count} dependencies" : "") +
                                 " — primitive fields are editable.");
            });
        }
        catch (Exception ex)
        {
            _log.Error("Bin", $"{entry.DisplayName}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveBinToOverride()
    {
        if (BinEditor.Entry is not { } entry) { _log.Warn("Bin", "No .bin open."); return; }
        if (!BinEditor.IsDirty) { _log.Info("Bin", "No applied edits to save."); return; }
        if (!await EnsureProjectSavedAsync()) return;

        var bytes = BinEditor.Serialize();
        if (bytes is null) return;

        // Validate the edited .bin re-parses before committing it to the override layer.
        try { _ = new LeagueToolkit.Core.Meta.BinTree(new MemoryStream(bytes, false)); }
        catch (Exception ex) { _log.Error("Bin", $"Edited .bin failed to re-parse — NOT saved: {ex.Message}"); return; }

        try
        {
            var dest = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, ".bin");
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = entry.PathHash,
                ResolvedPath = entry.IsResolved ? entry.Path : null,
                OverrideFile = dest,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            SetNodeStatus(entry.PathHash, AssetStatus.Modified);
            Inspector.SetAssetStatus("Modified — Project Override", dest);
            Project.IsDirty = true;
            UpdateTitle();
            _log.Success("Bin", $"Saved edited {entry.DisplayName} to project override ({bytes.Length:n0} bytes, re-parse OK). Build Package will include it.");
        }
        catch (Exception ex) { _log.Error("Bin", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportEditedBin()
    {
        if (BinEditor.Entry is not { } entry) { _log.Warn("Bin", "No .bin open."); return; }
        var bytes = BinEditor.Serialize();
        if (bytes is null) return;
        var outPath = await Dialogs.SaveFileAsync("Export edited .bin", entry.DisplayName);
        if (outPath is null) return;
        try
        {
            await File.WriteAllBytesAsync(outPath, bytes);
            _log.Success("Bin", $"Exported edited {entry.DisplayName} → {outPath} ({bytes.Length:n0} bytes).");
        }
        catch (Exception ex) { _log.Error("Bin", ex.Message); }
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
                Animation.SetSkeleton(skeleton?.BoneCount ?? 0);
                Animation.SetAnimations(mesh.CanSkin && skeleton is not null
                    ? FindAnimations(entry)
                    : Enumerable.Empty<AnimationEntryViewModel>());
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
                _currentMap = map;
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
        try { materialToTexture = MapGeoMaterialResolver.Resolve(GetAssetBytes(binEntry), names); }
        catch (Exception ex) { _log.Warn("MapGeo", $"materials parse failed: {ex.Message}"); return null; }
        if (materialToTexture.Count == 0) return null;
        return BuildMapTextures(map, materialToTexture, names.Count);
    }

    /// <summary>Per-group diffuse textures from resolved map material→texture map (override-aware loads).</summary>
    private IReadOnlyList<TextureImage?> BuildMapTextures(MapGeoAsset map, Dictionary<string, string> materialToTexture, int materialCount)
    {
        var cache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        TextureImage? Load(string path)
        {
            if (cache.TryGetValue(path, out var hit)) return hit;
            return cache[path] = LoadTextureByPath(path);
        }

        var result = new TextureImage?[map.Groups.Count];
        for (int i = 0; i < map.Groups.Count; i++)
            if (materialToTexture.TryGetValue(map.Groups[i].Material, out var path))
                result[i] = Load(path);

        int unique = cache.Values.Count(v => v is not null);
        _log.Success("MapGeo", $"Loaded {unique} unique textures ({materialToTexture.Count}/{materialCount} materials resolved).");
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
            try { material = SkinMaterialExtractor.Extract(GetAssetBytes(binEntry)); }
            catch (Exception ex) { _log.Warn("Material", $"bin parse failed: {ex.Message}"); }
        }
        if (material is null || !material.HasAny)
        {
            _log.Info("Material", $"No skin material found for {skn.DisplayName} (flat shading).");
            return null;
        }
        return BuildSubmeshTextures(mesh, material, skn.DisplayName);
    }

    /// <summary>Per-submesh diffuse textures from resolved material info (override-aware loads).</summary>
    private IReadOnlyList<TextureImage?> BuildSubmeshTextures(MeshAsset mesh, SkinMaterialInfo material, string label)
    {
        var cache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        TextureImage? Load(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (cache.TryGetValue(path, out var hit)) return hit;
            return cache[path] = LoadTextureByPath(path);
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
        _log.Success("Material", $"Applied {loaded}/{mesh.SubMeshes.Count} submesh textures for {label}.");
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
