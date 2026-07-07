using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Documents;
using ReyEngine.App.Imaging;
using ReyEngine.App.Services;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Selection;
using ReyEngine.Core.Undo;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Vfx;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Skeletons;
using ReyEngine.Rendering;

namespace ReyEngine.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly Logger _log = new();
    private readonly HashSyncService _sync = new();
    private readonly WadPathResolver _resolver;
    private WadArchive? _archive;
    private AssetMountService? _mounts;          // project mode: the virtual file system
    private readonly AssetOverrideStore _overrides = new();
    private readonly ReyEngine.App.Services.ThumbnailService _thumbnails; // Content Browser lazy thumbnails
    private readonly Dictionary<ulong, AssetNodeViewModel> _nodesByHash = new();

    private bool ContentLoaded => _archive is not null || _mounts is not null;

    /// <summary>Read an asset's bytes, mount-aware (project mode) or override-aware (single WAD).</summary>
    private byte[] ReadAsset(ulong hash)
    {
        if (_mounts is not null)
            return _mounts.Read(hash) ?? throw new FileNotFoundException($"0x{hash:x16} not in any mount.");
        if (_overrides.TryGet(hash, out var ov) && File.Exists(ov.OverrideFile)) return File.ReadAllBytes(ov.OverrideFile);
        return _archive!.Extract(hash);
    }

    private bool TryResolveEntry(ulong hash, out WadAssetEntry entry)
    {
        if (_mounts is not null)
        {
            if (_mounts.TryGet(hash, out var a)) { entry = a.ToEntry(); return true; }
            entry = null!; return false;
        }
        if (_archive is not null) return _archive.TryGetEntry(hash, out entry!);
        entry = null!; return false;
    }

    private IEnumerable<WadAssetEntry> AssetEntries =>
        _mounts is not null ? _mounts.Assets.Select(a => a.ToEntry())
        : _archive is not null ? _archive.Entries
        : Enumerable.Empty<WadAssetEntry>();

    public DialogService Dialogs { get; } = new();
    public ConsoleViewModel Console { get; } = new();
    public InspectorViewModel Inspector { get; } = new();
    public MeshInspectorViewModel MeshInspector { get; } = new();
    public MapGeoInspectorViewModel MapGeoInspector { get; } = new();
    public AnimationInspectorViewModel Animation { get; } = new();
    public ObservableCollection<AssetNodeViewModel> RootNodes { get; } = new();
    public BinEditorViewModel BinEditor { get; } = new();
    public MaterialEditorViewModel MaterialEditor { get; } = new();
    public ContentBrowserViewModel ContentBrowser { get; } = new();
    public MapContentViewModel MapContent { get; } = new();

    // ---- Undo/Redo (M29) -------------------------------------------------
    public UndoRedoService UndoService { get; } = new();
    public bool CanUndo => UndoService.CanUndo;
    public bool CanRedo => UndoService.CanRedo;
    public string UndoLabel => UndoService.UndoName is { } u ? $"Undo {u}" : "Undo";
    public string RedoLabel => UndoService.RedoName is { } r ? $"Redo {r}" : "Redo";

    [RelayCommand] private void Undo() => UndoService.Undo();
    [RelayCommand] private void Redo() => UndoService.Redo();
    public ObservableCollection<RecentProjectViewModel> RecentProjectList { get; } = new();
    public bool HasRecentProjects => RecentProjectList.Count > 0;

    [ObservableProperty] private AssetNodeViewModel? _selectedNode;
    [ObservableProperty] private bool _projectMode;
    [ObservableProperty] private bool _inspectionMode;
    [ObservableProperty] private string _title = "ReyEngine";
    [ObservableProperty] private string _status = "Ready — open a .wad.client to begin";
    [ObservableProperty] private string _hashInput = "";
    [ObservableProperty] private ReyProject _project = new();
    [ObservableProperty] private bool _isBuilding;

    // Viewport-bound state
    [ObservableProperty] private MeshAsset? _currentMesh;
    [ObservableProperty] private SkeletonAsset? _currentSkeleton;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelMaskTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelGradientTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelEmissiveTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelMatCapTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelMatCapMaskTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelLightmapTextures; // M33: per-submesh baked lightmap atlas
    [ObservableProperty] private IReadOnlyList<bool>? _currentModelSubmeshVisible;

    // M35: placed particle systems (MapParticle) on the current map.
    [ObservableProperty] private IReadOnlyList<MapParticlePlacement>? _currentModelParticles;
    [ObservableProperty] private bool _showParticles = true;
    [ObservableProperty] private object? _selectedParticleTreeItem;                               // TreeView selection (group or leaf)
    [ObservableProperty] private ParticlePlacementViewModel? _selectedParticleNode;               // the selected placement (leaf)
    [ObservableProperty] private IReadOnlyList<System.Numerics.Vector3>? _particleMarkers;         // positions shown in the viewport
    [ObservableProperty] private System.Numerics.Vector3? _selectedParticleMarker;
    [ObservableProperty] private System.Numerics.Vector3? _particleFocusPoint;                     // set to recentre the camera

    public bool HasParticles => MapContent.HasParticles;

    partial void OnShowParticlesChanged(bool value) => UpdateParticleMarkers();
    partial void OnCurrentModelParticlesChanged(IReadOnlyList<MapParticlePlacement>? value)
    {
        MapContent.SetParticles(value ?? Array.Empty<MapParticlePlacement>());
        OnPropertyChanged(nameof(HasParticles));
        UpdateParticleMarkers();
    }
    partial void OnSelectedParticleTreeItemChanged(object? value)
        => SelectedParticleNode = value as ParticlePlacementViewModel;
    partial void OnSelectedParticleNodeChanged(ParticlePlacementViewModel? value)
    {
        SelectedParticleMarker = value?.CurrentPosition;
        RefreshParticleMoveFields(value);
        if (value is { } p)
        {
            ShowParticles = true;
            ParticleFocusPoint = p.CurrentPosition;
            // M50b: exclusive selection — a particle selection deselects meshes/props/probes
            _selection.Clear();
            if (SelectedPropTreeItem is not null) SelectedPropTreeItem = null;
            if (SelectedProbe is not null) SelectedProbe = null;
        }
        RebuildParticlePlayback();   // M36: play the newly-selected system (or stop if none)
    }

    /// <summary>M50b: one material slot of the selected mesh (Unity Mesh-Renderer style).</summary>
    public sealed record MeshMaterialSlotViewModel(string Name, string Detail);

    [ObservableProperty] private IReadOnlyList<int>? _selectedSubmeshIndices;              // M50b: outline highlight
    [ObservableProperty] private IReadOnlyList<MeshMaterialSlotViewModel>? _selectedMeshMaterials;
    [ObservableProperty] private bool _hasSelectedMeshMaterials;
    [ObservableProperty] private bool _assetDataExpanded;   // M50b: Overview/Materials/Raw-BIN hidden until wanted

    /// <summary>Open a selected-mesh material in the full Materials editor (expands the asset-data area).</summary>
    [RelayCommand]
    private void EditSelectedMaterial(MeshMaterialSlotViewModel? slot)
    {
        if (slot is null) return;
        AssetDataExpanded = true;
        InspectorTab = 1;
        MaterialEditor.Search = slot.Name;
        MaterialEditor.AutoPreviewDiffuse(slot.Name);   // M50c: show the texture immediately
    }

    private void UpdateParticleMarkers() =>
        ParticleMarkers = (ShowParticles && MapContent.HasParticles)
            ? MapContent.AllParticles.Select(v => v.CurrentPosition).ToList() : null;

    // ---- M38: cubemap probes + animated props (placed characters) ----
    [ObservableProperty] private IReadOnlyList<MapCubemapProbe>? _currentModelProbes;
    [ObservableProperty] private IReadOnlyList<MapAnimatedProp>? _currentModelProps;
    [ObservableProperty] private IReadOnlyList<System.Numerics.Vector3>? _propMarkers;
    [ObservableProperty] private IReadOnlyList<System.Numerics.Vector3>? _probeMarkers;
    [ObservableProperty] private bool _showPlaceables = true;
    [ObservableProperty] private object? _selectedPropTreeItem;
    [ObservableProperty] private AnimatedPropViewModel? _selectedPropNode;
    [ObservableProperty] private CubemapProbeViewModel? _selectedProbe;
    [ObservableProperty] private string _selectedPlaceableInfo = "";

    partial void OnCurrentModelProbesChanged(IReadOnlyList<MapCubemapProbe>? value)
    { MapContent.SetProbes(value ?? Array.Empty<MapCubemapProbe>()); UpdatePlaceableMarkers(); }
    partial void OnCurrentModelPropsChanged(IReadOnlyList<MapAnimatedProp>? value)
    { MapContent.SetProps(value ?? Array.Empty<MapAnimatedProp>()); UpdatePlaceableMarkers(); _ = RefreshPropMeshesAsync(); }

    // ---- M41: render the placed prop meshes (SRU_Baron, dragons, camps…) at their placements ----
    [ObservableProperty] private bool _showPropMeshes;
    [ObservableProperty] private PropRenderSet? _currentPropMeshes;

    partial void OnShowPropMeshesChanged(bool value) => _ = RefreshPropMeshesAsync();

    private async System.Threading.Tasks.Task RefreshPropMeshesAsync()
    {
        if (!ShowPropMeshes || CurrentModelProps is not { Count: > 0 } props) { CurrentPropMeshes = null; return; }
        var snapshot = props.ToList();
        var (set, resolved, failed) = await System.Threading.Tasks.Task.Run(() => BuildPropRenderSet(snapshot));
        if (!ShowPropMeshes) return;   // toggled off while decoding
        CurrentPropMeshes = set;
        _log.Info("Props", $"Rendering {resolved} prop mesh(es); {failed} couldn't be resolved (shown as markers).");
    }

    /// <summary>Decode each unique prop skin once (mesh + per-submesh diffuse) and place an instance per
    /// placement. Runs off the UI thread. Returns the set + resolved/failed counts (logged on return).</summary>
    private (PropRenderSet? set, int resolved, int failed) BuildPropRenderSet(IReadOnlyList<MapAnimatedProp> props)
    {
        var meshBySkin = new Dictionary<string, PropMesh?>(StringComparer.OrdinalIgnoreCase);
        var texByPath = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        var instances = new List<PropInstanceData>();
        int failed = 0;
        foreach (var p in props)
        {
            if (string.IsNullOrEmpty(p.Skin)) { failed++; continue; }
            if (!meshBySkin.TryGetValue(p.Skin, out var mesh))
                meshBySkin[p.Skin] = mesh = TryBuildPropMesh(p.Skin, texByPath);
            if (mesh is not null) instances.Add(new PropInstanceData(mesh, p.Transform));
            else failed++;
        }
        return (instances.Count > 0 ? new PropRenderSet(instances) : null, instances.Count, failed);
    }

    private PropMesh? TryBuildPropMesh(string skin, Dictionary<string, TextureImage?> texCache)
    {
        try
        {
            var binBytes = ReadAssetByPath("data/" + skin.ToLowerInvariant() + ".bin");
            if (binBytes is null) return null;
            var meshRef = SkinMeshExtractor.Extract(binBytes);
            if (meshRef?.SimpleSkin is not { } sknPath) return null;
            var sknBytes = ReadAssetByPath(sknPath);
            if (sknBytes is null) return null;

            var mesh = SkinnedMeshDecoder.Decode(sknBytes);
            var mat = ChampionMaterialResolver.Resolve(binBytes, ResolveBinName);
            TextureImage? Tex(string? path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                if (texCache.TryGetValue(path, out var img)) return img;
                return texCache[path] = LoadTextureByPath(path);
            }
            var subs = mesh.SubMeshes
                .Select(s => new PropSubmesh(s.StartIndex, s.IndexCount, Tex(mat.For(s.Material) ?? meshRef.DefaultTexture)))
                .ToList();
            return new PropMesh(skin, mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, subs);
        }
        catch { return null; }
    }
    partial void OnShowPlaceablesChanged(bool value) => UpdatePlaceableMarkers();

    private void UpdatePlaceableMarkers()
    {
        PropMarkers = (ShowPlaceables && MapContent.HasProps) ? MapContent.AllProps.Select(p => p.Position).ToList() : null;
        ProbeMarkers = (ShowPlaceables && MapContent.HasProbes) ? MapContent.Probes.Select(p => p.Position).ToList() : null;
    }

    partial void OnSelectedPropTreeItemChanged(object? value)
    { if (value is AnimatedPropViewModel p) SelectedPropNode = p; }
    partial void OnSelectedPropNodeChanged(AnimatedPropViewModel? value)
    {
        if (value is not { } p) return;
        SelectedProbe = null;
        _selection.Clear();                       // M50b: exclusive selection
        if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
        SelectedParticleMarker = p.Position;
        ParticleFocusPoint = p.Position;
        SelectedPlaceableInfo = $"{p.Name}\n{p.Info}\n({p.Position.X:0}, {p.Position.Y:0}, {p.Position.Z:0})";
    }
    partial void OnSelectedProbeChanged(CubemapProbeViewModel? value)
    {
        if (value is not { } p) return;
        SelectedPropNode = null;
        _selection.Clear();                       // M50b: exclusive selection
        if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
        SelectedParticleMarker = p.Position;
        ParticleFocusPoint = p.Position;
        SelectedPlaceableInfo = $"{p.Name}\ncubemap: {p.Info}\n({p.Position.X:0}, {p.Position.Y:0}, {p.Position.Z:0})";
    }

    // ---- Particle playback (M36) — simulate & render the selected placed system live in the viewport ----
    private static readonly IReadOnlyDictionary<uint, VfxSystemDefinition> EmptyVfx = new Dictionary<uint, VfxSystemDefinition>();
    private IReadOnlyDictionary<uint, VfxSystemDefinition> _vfxSystems = EmptyVfx;
    private readonly Dictionary<uint, IReadOnlyList<TextureImage?>> _vfxTextureCache = new();  // system hash -> sprites
    [ObservableProperty] private bool _playParticlePreview;
    [ObservableProperty] private bool _playAllParticles;
    [ObservableProperty] private VfxPlayback? _currentParticlePlayback;

    /// <summary>Cap on simultaneously-played placements for "Play All" (keeps the sim/draw cost sane).</summary>
    private const int MaxPlayAllInstances = 250;

    partial void OnPlayParticlePreviewChanged(bool value) { if (value) PlayAllParticles = false; RebuildParticlePlayback(); }
    partial void OnPlayAllParticlesChanged(bool value) { if (value) PlayParticlePreview = false; RebuildParticlePlayback(); }

    /// <summary>Resolve (and cache) one sprite per emitter for a system; nulls → viewport soft-dot fallback.</summary>
    private IReadOnlyList<TextureImage?> ResolveSystemTextures(VfxSystemDefinition sys)
    {
        if (_vfxTextureCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var texs = new List<TextureImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
            texs.Add(e.TexturePath is { } p ? LoadTextureByPath(p) : null);
        _vfxTextureCache[sys.PathHash] = texs;
        return texs;
    }

    /// <summary>M47: resolve each emitter's .scb/.sco mesh primitive (null when not a mesh emitter or
    /// the mesh doesn't resolve — those billboard as before). Cached per system.</summary>
    private readonly Dictionary<uint, IReadOnlyList<Formats.Meshes.StaticMeshData?>?> _vfxMeshCache = new();
    private IReadOnlyList<Formats.Meshes.StaticMeshData?>? ResolveSystemMeshes(VfxSystemDefinition sys)
    {
        if (_vfxMeshCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        List<Formats.Meshes.StaticMeshData?>? meshes = null;
        for (int i = 0; i < sys.Emitters.Count; i++)
        {
            var e = sys.Emitters[i];
            if (!e.IsMeshPrimitive || string.IsNullOrEmpty(e.MeshPath)) continue;
            var bytes = ReadAssetByPath(e.MeshPath);
            Formats.Meshes.StaticMeshData? mesh = null;
            if (bytes is not null)
            {
                // M47b: skinned mesh primitives (butterflies/dragonflies, .skn) render in bind pose via
                // the same mesh-particle path (no per-particle wing animation yet); .scb/.sco are static.
                if (e.MeshPath.EndsWith(".skn", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var m = SkinnedMeshDecoder.Decode(bytes);
                        // M48: wing-flap — load the primitive's skeleton + idle animation so the viewport
                        // can CPU-skin the mesh per frame (falls back to bind pose when either is missing).
                        Formats.Meshes.VfxMeshAnimation? anim = null;
                        if (m.CanSkin && e.MeshSkeletonPath is { } sklP && e.MeshAnimationPath is { } anmP)
                        {
                            try
                            {
                                var sklB = ReadAssetByPath(sklP);
                                var anmB = ReadAssetByPath(anmP);
                                if (sklB is not null && anmB is not null)
                                    anim = new Formats.Meshes.VfxMeshAnimation(m,
                                        SkeletonDecoder.Decode(sklB),
                                        AnimationDecoder.Decode(anmB, Path.GetFileName(anmP)));
                            }
                            catch { /* bind pose fallback */ }
                        }
                        mesh = new Formats.Meshes.StaticMeshData(m.Positions, m.Uvs, m.Indices, Path.GetFileName(e.MeshPath))
                        { Animation = anim };
                    }
                    catch { /* keep billboard fallback */ }
                }
                else mesh = Formats.Meshes.StaticObjectDecoder.Decode(bytes, e.MeshPath);
            }
            if (mesh is null) continue;
            meshes ??= Enumerable.Repeat<Formats.Meshes.StaticMeshData?>(null, sys.Emitters.Count).ToList();
            meshes[i] = mesh;
        }
        return _vfxMeshCache[sys.PathHash] = meshes;
    }

    // ---- Champion-skin VFX (M37) — a loaded skin's effect library, played at the model origin ----
    public ObservableCollection<VfxSystemItemViewModel> ChampionVfxSystems { get; } = new();
    [ObservableProperty] private bool _hasChampionVfx;
    [ObservableProperty] private VfxSystemItemViewModel? _selectedChampionVfx;

    /// <summary>Populate the champion VFX list from a skin's parsed systems (visual systems only, sorted).</summary>
    private void SetChampionVfx(IReadOnlyDictionary<uint, VfxSystemDefinition> systems)
    {
        _vfxSystems = systems;
        _vfxTextureCache.Clear(); _vfxMeshCache.Clear();
        ChampionVfxSystems.Clear();
        foreach (var s in systems.Values
                     .Where(s => s.Emitters.Any(e => e.IsVisual))
                     .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            ChampionVfxSystems.Add(new VfxSystemItemViewModel { Hash = s.PathHash, Name = s.Name, EmitterCount = s.Emitters.Count(e => e.IsVisual) });
        HasChampionVfx = ChampionVfxSystems.Count > 0;
        SelectedChampionVfx = null;
    }

    partial void OnSelectedChampionVfxChanged(VfxSystemItemViewModel? value)
    {
        if (value is null || !_vfxSystems.TryGetValue(value.Hash, out var sys))
        {
            CurrentParticlePlayback = null;
            return;
        }
        // champion VFX are authored around the character root (origin); play one system there.
        CurrentParticlePlayback = new VfxPlayback(new[] { new VfxPlaybackItem(sys, System.Numerics.Vector3.Zero, ResolveSystemTextures(sys), ResolveSystemMeshes(sys)) });
        _log.Info("VFX", $"Playing '{sys.Name}' — {sys.Emitters.Count} emitter(s), {ResolveSystemTextures(sys).Count(t => t is not null)} sprite(s) resolved.");
    }

    [RelayCommand]
    private void StopChampionVfx() => SelectedChampionVfx = null;

    /// <summary>Rebuild the live playback request (M36): all visible placements, or just the selected one.</summary>
    private void RebuildParticlePlayback()
    {
        if (PlayAllParticles)
        {
            var items = new List<VfxPlaybackItem>();
            foreach (var v in MapContent.AllParticles)
            {
                if (!_vfxSystems.TryGetValue(v.Placement.SystemHash, out var s) || !s.Emitters.Any(e => e.IsVisual)) continue;
                items.Add(new VfxPlaybackItem(s, v.CurrentPosition, ResolveSystemTextures(s), ResolveSystemMeshes(s)));
                if (items.Count >= MaxPlayAllInstances) break;
            }
            CurrentParticlePlayback = items.Count > 0 ? new VfxPlayback(items) : null;
            _log.Info("Particles", items.Count >= MaxPlayAllInstances
                ? $"Playing all — capped at {MaxPlayAllInstances} placements (of {MapContent.ParticleCount})."
                : $"Playing all — {items.Count} placement(s).");
            return;
        }

        if (!PlayParticlePreview || SelectedParticleNode is not { } node
            || !_vfxSystems.TryGetValue(node.Placement.SystemHash, out var sys) || sys.Emitters.Count == 0)
        {
            CurrentParticlePlayback = null;
            return;
        }
        var texs = ResolveSystemTextures(sys);
        CurrentParticlePlayback = new VfxPlayback(new[] { new VfxPlaybackItem(sys, node.CurrentPosition, texs, ResolveSystemMeshes(sys)) });
        _log.Info("Particles", $"Playing '{sys.Name}' — {sys.Emitters.Count} emitter(s), {texs.Count(t => t is not null)} sprite(s) resolved.");
    }

    // ---- Particle move (M35 adjustment) — reposition a placed particle, live + persisted to the mod ----
    [ObservableProperty] private string _particleMoveX = "0";
    [ObservableProperty] private string _particleMoveY = "0";
    [ObservableProperty] private string _particleMoveZ = "0";
    /// <summary>Dirty flag: at least one particle has been moved and can be saved to the mod.</summary>
    [ObservableProperty] private bool _hasParticleMoves;

    private void RefreshParticleMoveFields(ParticlePlacementViewModel? node)
    {
        var p = node?.CurrentPosition ?? System.Numerics.Vector3.Zero;
        ParticleMoveX = p.X.ToString("0.###", CultureInfo.InvariantCulture);
        ParticleMoveY = p.Y.ToString("0.###", CultureInfo.InvariantCulture);
        ParticleMoveZ = p.Z.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Apply the numeric X/Y/Z as the selected particle's new absolute world position (live preview).</summary>
    [RelayCommand]
    private void ApplyParticleMove()
    {
        if (SelectedParticleNode is not { } node) return;
        if (!TryParseVector3(ParticleMoveX, ParticleMoveY, ParticleMoveZ, out var target))
        { _log.Warn("Particles", "Enter valid X/Y/Z numbers."); return; }
        node.Offset = target - node.Placement.Position;
        SelectedParticleMarker = node.CurrentPosition;
        UpdateParticleMarkers();
        HasParticleMoves = MapContent.AllParticles.Any(v => v.IsMoved);
        RebuildParticlePlayback();   // M36: follow the moved particle if it's playing
        _log.Info("Particles", $"Moved '{node.Name}' to ({target.X:0.#}, {target.Y:0.#}, {target.Z:0.#}).");
    }

    [RelayCommand]
    private void ResetParticleMove()
    {
        if (SelectedParticleNode is not { } node) return;
        node.Offset = System.Numerics.Vector3.Zero;
        RefreshParticleMoveFields(node);
        SelectedParticleMarker = node.CurrentPosition;
        UpdateParticleMarkers();
        HasParticleMoves = MapContent.AllParticles.Any(v => v.IsMoved);
        RebuildParticlePlayback();
    }
    [ObservableProperty] private IReadOnlyList<ViewportMeshRenderer.SubmeshMaterial>? _currentModelSubmeshMaterials; // M32
    [ObservableProperty] private bool _hasFlowmapWater; // M44: current map has flowmap-river water → viewport animates it
    public ParticleEditorViewModel ParticleEditor { get; } = new(); // M46 Particle Editor
    [ObservableProperty] private bool _isParticleEditorActive;      // M46: overlay visible for the active tab
    [ObservableProperty] private double _currentLightmapScale = 1.0; // M45: MapSunProperties.lightMapColorScale
    private Formats.MapGeo.MapSunProperties? _currentSunProps; // M45: full sun/atmosphere component (future use)
    [ObservableProperty] private AnimationClip? _currentAnimation;
    [ObservableProperty] private double _animationTime;
    [ObservableProperty] private bool _showWireframe;
    [ObservableProperty] private bool _showBones;
    [ObservableProperty] private bool _showBounds;
    [ObservableProperty] private bool _cullBackfaces = true; // M34: respect per-material cullEnable by default (off = force all two-sided)
    [ObservableProperty] private bool _hasMaterialData;
    [ObservableProperty] private bool _hasInspectorBody;
    [ObservableProperty] private int _inspectorTab;
    [ObservableProperty] private int _previewMode; // 0 Basic · 1 RiotApprox · 2 Debug base · 3 Debug alpha · 4 Debug normal
    [ObservableProperty] private string _shaderDbStatus = "Riot shaders not scanned.";
    private MapGeoAsset? _currentMap;
    private IReadOnlyDictionary<string, MaterialProfile>? _currentMapProfiles; // M34: material name → render-state profile
    // M44: the current map's flowmap-water Flow_Map / Flowing_Normal textures (per group; null when the map has
    // no water). Kept in fields so they survive the ClearSecondaryTextures() that follows a map load and can be
    // re-published — the same reason baked lightmaps are handled outside ClearSecondaryTextures.
    private IReadOnlyList<TextureImage?>? _mapFlowMasks;
    private IReadOnlyList<TextureImage?>? _mapFlowGrads;

    /// <summary>M44: (re)publish the map's flow-water textures onto the mask/gradient channels (slots 1/2) the
    /// water shader samples, and flag the viewport to animate. Safe to call after ClearSecondaryTextures().</summary>
    private void PublishMapFlowWater()
    {
        CurrentModelMaskTextures = _mapFlowMasks;
        CurrentModelGradientTextures = _mapFlowGrads;
        HasFlowmapWater = _mapFlowMasks is not null;
    }
    private MapVisibilityControllers? _mapControllers;
    private MapVisibilityResolver? _visibilityResolver;
    private ShaderDatabase? _shaderDb;

    public MainWindowViewModel()
    {
        _log.AddSink(Console);
        _cullBackfaces = Settings.CullBackfacesDefault;   // M40: honor saved viewport default
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

        UndoService.Changed += () =>
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            OnPropertyChanged(nameof(UndoLabel));
            OnPropertyChanged(nameof(RedoLabel));
            UpdateTitle();
        };
        UndoService.Error += msg => _log.Warn("Undo", msg);

        _selection.Changed += OnMeshSelectionChanged;

        BinEditor.CopyHandler = Dialogs.CopyAsync;
        BinEditor.UndoService = UndoService;

        MaterialEditor.UndoService = UndoService;
        MaterialEditor.CopyHandler = Dialogs.CopyAsync;
        MaterialEditor.TextureExists = TextureExistsByPath;
        MaterialEditor.LoadThumbnail = LoadThumbnailByPath;
        MaterialEditor.OpenTexture = OpenTextureByPath;
        MaterialEditor.ReplaceTextureAsset = ReplaceTextureForSlot;
        MaterialEditor.ApplyToViewport = ApplyMaterialToViewport;
        MaterialEditor.SaveOverride = SaveMaterialOverride;

        // M46 Particle Editor wiring
        ParticleEditor.ResolveTextures = ResolveSystemTextures;
        ParticleEditor.ResolveMeshes = ResolveSystemMeshes;   // M47: .scb/.sco mesh primitives
        ParticleEditor.Info = m => _log.Info("Particle", m);
        ParticleEditor.Error = m => _log.Error("Particle", m);
        ParticleEditor.MarkDocumentDirty = () => { }; // window has its own dirty state via Document.IsDirty
        ParticleEditor.LoadThumbnail = LoadThumbnailByPath;
        ParticleEditor.SaveOverrideAsync = SaveParticleOverride;

        ContentBrowser.FileSelected = OpenAssetDocument;
        ContentBrowser.ExtractMaterials = ExtractMaterialsForNode;
        ContentBrowser.MaterialSelected = OpenMaterialAsset;
        _thumbnails = new ThumbnailService(p =>
        {
            var img = LoadTextureByPath(p);
            return img is null ? null : BitmapFactory.FromRgbaThumbnail(img);
        });
        ContentBrowser.RequestThumbnails = nodes =>
        {
            foreach (var n in nodes) _thumbnails.Request(n.ThumbnailPath, bmp => n.Thumbnail = bmp);
        };
        MapContent.OpenMap = OpenAssetDocument;
        LoadRecentProjects(RecentProjects.Load());
    }

    /// <summary>Extract a material library's (.materials.bin / skin .bin) materials as virtual assets (M33).</summary>
    private IReadOnlyList<MaterialAssetViewModel> ExtractMaterialsForNode(AssetNodeViewModel node)
    {
        if (node.Entry is not { } e) return System.Array.Empty<MaterialAssetViewModel>();
        try
        {
            var mats = MaterialLibraryExtractor.Extract(GetAssetBytes(e), ResolveBinName);
            return mats.Select(m => new MaterialAssetViewModel(m, e, e.ReadOnly)).ToList();
        }
        catch (Exception ex)
        {
            _log.Warn("Material", $"Could not read materials from {e.DisplayName}: {ex.Message}");
            return System.Array.Empty<MaterialAssetViewModel>();
        }
    }

    /// <summary>Open a material virtual-asset in the Material Editor, filtered to the chosen material (M33).</summary>
    private async void OpenMaterialAsset(MaterialAssetViewModel material)
    {
        // Show the inspector body + its source-bin overview, then load the materials and reveal the tab.
        Inspector.ShowEntry(material.SourceEntry);
        Inspector.SetAssetStatus(material.ReadOnly ? "Read-only Riot material" : "Project material (editable)", null);
        HasInspectorBody = true;

        await LoadMaterialBinAsync(material.SourceEntry, alsoRawBin: false);
        if (!HasMaterialData)
        {
            _log.Warn("Material", $"'{material.FullName}': no editable materials resolved from {material.SourceBin}.");
            return;
        }
        MaterialEditor.Search = material.FullName; // filter the editor to the clicked material
        InspectorTab = 1;                          // the "Materials" tab
        _log.Info("Material", $"Opened '{material.FullName}' ({material.Profile}) from {material.SourceBin}" +
                              (material.ReadOnly ? " — read-only reference (Copy To Project to edit)." : "."));
    }

    // ---- Document / viewport tabs (M33) --------------------------------------------------------------

    public ObservableCollection<EditorDocument> Documents { get; } = new();
    [ObservableProperty] private EditorDocument? _activeDocument;
    private bool _restoringScene;

    /// <summary>A cached map viewport scene — lets a map tab restore fully (edits/selection/visibility) on
    /// re-activation instead of re-decoding, so it "stays loaded" while other assets are inspected.</summary>
    private sealed record MapScene(
        MapGeoAsset Map, byte[] MapBytes, WadAssetEntry Entry, MapVisibilityControllers? Controllers,
        MeshAsset Mesh, IReadOnlyList<TextureImage?>? Textures,
        IReadOnlyList<ViewportMeshRenderer.SubmeshMaterial>? Materials,
        IReadOnlyList<TextureImage?>? Lightmaps,
        IReadOnlyList<TextureImage?>? FlowMasks, IReadOnlyList<TextureImage?>? FlowGrads, // M44 flow-water
        double LightmapScale, Formats.MapGeo.MapSunProperties? SunProps, // M45 sun properties
        IReadOnlyList<MapParticlePlacement>? Particles,
        IReadOnlyDictionary<uint, VfxSystemDefinition> VfxSystems,
        IReadOnlyList<MapCubemapProbe>? Probes, IReadOnlyList<MapAnimatedProp>? Props,
        int DragonIndex, int BaronIndex, bool HasMoves, int[] SelectedMeshIndices,
        List<MapLayerGroupViewModel> LayerGroups, string MapName, List<MapPieceViewModel> Pieces);

    /// <summary>User opened an asset — create or focus its tab and activate it.</summary>
    private void OpenAssetDocument(AssetNodeViewModel? node)
    {
        if (node?.Entry is not { } entry) { SelectedNode = node; return; }
        var doc = Documents.FirstOrDefault(d => d.Key == entry.PathHash);
        if (doc is null)
        {
            var kind = EditorDocument.KindOf(entry.Type);
            // M46: dedicated particle bins (path mentions particles) open straight in the Particle Editor
            // WINDOW. Other VFX-bearing bins (skin bins, map materials.bin) keep their normal editor; use
            // Tools -> Open in Particle Editor for those.
            if (kind == DocumentKind.Bin && entry.IsResolved && entry.Path.Contains("particles", StringComparison.OrdinalIgnoreCase))
            {
                OpenParticleEditorFor(entry);
                return;
            }
            doc = new EditorDocument
            {
                Title = entry.DisplayName,
                Kind = kind,
                Key = entry.PathHash,
                Entry = entry,
            };
            Documents.Add(doc);
        }
        ActivateDocument(doc);
    }

    [RelayCommand]
    private void ActivateDocument(EditorDocument? doc)
    {
        if (doc is null) return;
        if (ReferenceEquals(ActiveDocument, doc)) return;

        CaptureActiveScene(); // snapshot the outgoing map (if any) so it restores later
        foreach (var d in Documents) d.IsActive = ReferenceEquals(d, doc);
        ActiveDocument = doc;

        var node = doc.Entry is { } e && _nodesByHash.TryGetValue(e.PathHash, out var n) ? n : null;

        if (doc.Scene is MapScene scene)
        {
            _restoringScene = true;
            try { SelectedNode = node; RestoreMapScene(scene); }
            finally { _restoringScene = false; }
        }
        else
        {
            SelectedNode = node; // triggers the normal load path (OnSelectedNodeChanged)
        }
    }

    /// <summary>M46: open a particle .bin in the Particle Editor WINDOW (separate top-level window;
    /// the main layout stays untouched).</summary>
    public Action? ShowParticleEditorWindow; // wired by MainWindow (owns the window instance)

    private void OpenParticleEditorFor(WadAssetEntry entry)
    {
        try
        {
            var bytes = ReadAsset(entry.PathHash);
            bool editable = !entry.ReadOnly;
            if (!ParticleEditor.Load(entry, bytes, editable))
            {
                _log.Warn("Particle", $"{entry.DisplayName} contains no VFX systems.");
                return;
            }
            ShowParticleEditorWindow?.Invoke();
            _log.Info("Particle", $"Particle Editor: {entry.DisplayName} — {ParticleEditor.Systems.Count} system(s){(editable ? "" : " (read-only Riot reference)")}.");
        }
        catch (Exception ex) { _log.Error("Particle", ex.Message); }
    }

    /// <summary>M46 Tools menu: open the ACTIVE document's .bin in the Particle Editor window.</summary>
    [RelayCommand]
    private void OpenActiveInParticleEditor()
    {
        if (ActiveDocument?.Entry is not { } entry) { _log.Info("Particle", "Open a .bin document first."); return; }
        OpenParticleEditorFor(entry);
    }

    /// <summary>M46: write the edited particle .bin to the project override (mirrors SaveMaterialOverride).</summary>
    private async Task SaveParticleOverride()
    {
        if (ParticleEditor.Entry is not { } entry) { _log.Warn("Particle", "No particle .bin open."); return; }
        if (!GuardEditable(entry)) return;
        if (ParticleEditor.Document is not { } pdoc) return;
        if (!pdoc.IsDirty) { _log.Info("Particle", "No particle edits to save."); return; }
        if (!await EnsureProjectSavedAsync()) return;

        var bytes = pdoc.Serialize();
        try { _ = new LeagueToolkit.Core.Meta.BinTree(new MemoryStream(bytes, false)); }
        catch (Exception ex) { _log.Error("Particle", $"Edited particle .bin failed to re-parse — NOT saved: {ex.Message}"); return; }

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
            Project.IsDirty = true;
            UpdateTitle();
            _log.Success("Particle", $"Saved edited particles {entry.DisplayName} to override ({bytes.Length:n0} bytes, re-parse OK). Build Package will include it.");
        }
        catch (Exception ex) { _log.Error("Particle", ex.Message); }
    }

    [RelayCommand]
    private void CloseDocument(EditorDocument? doc)
    {
        if (doc is null) return;
        bool wasActive = ReferenceEquals(doc, ActiveDocument);
        if (doc.Scene is MapScene ms) UndoService.PurgeContext(ms.Map);
        doc.IsActive = false;
        Documents.Remove(doc);
        if (!wasActive) return;

        ActiveDocument = null; // so activating the next tab doesn't snapshot the dying scene
        var next = Documents.LastOrDefault();
        if (next is not null) ActivateDocument(next);
        else ClearViewport();
    }

    private void CaptureActiveScene()
    {
        if (ActiveDocument is { Kind: DocumentKind.Map }) ActiveDocument.Scene = CaptureMapScene();
    }

    /// <summary>Reflect a map's unsaved mesh edits as a dirty dot on its tab.</summary>
    partial void OnHasMapMovesChanged(bool value)
    {
        if (ActiveDocument is { Kind: DocumentKind.Map } d) d.IsDirty = value;
    }

    private MapScene? CaptureMapScene()
    {
        if (_currentMap is not { } map || _currentMapBytes is null || _currentMapEntry is not { } entry || CurrentMesh is not { } mesh)
            return null;
        return new MapScene(map, _currentMapBytes, entry, _mapControllers, mesh,
            CurrentModelTextures, CurrentModelSubmeshMaterials, CurrentModelLightmapTextures,
            _mapFlowMasks, _mapFlowGrads,
            CurrentLightmapScale, _currentSunProps,
            CurrentModelParticles, _vfxSystems, CurrentModelProbes, CurrentModelProps,
            SelectedDragonIndex, SelectedBaronIndex, HasMapMoves,
            _selection.Items.Select(m => m.Index).ToArray(),
            MapContent.LayerGroups.ToList(), MapContent.MapName, MapContent.Pieces.ToList());
    }

    private void RestoreMapScene(MapScene s)
    {
        CurrentSkeleton = null; ShowBones = false;
        _currentMap = s.Map; _currentMapBytes = s.MapBytes; _currentMapEntry = s.Entry;
        _mapControllers = s.Controllers;
        _visibilityResolver = new MapVisibilityResolver(s.Controllers);
        CurrentMesh = s.Mesh;
        CurrentModelTextures = s.Textures;
        ClearSecondaryTextures();
        CurrentModelLightmapTextures = s.Lightmaps;
        _mapFlowMasks = s.FlowMasks; _mapFlowGrads = s.FlowGrads; PublishMapFlowWater(); // M44
        CurrentLightmapScale = s.LightmapScale; _currentSunProps = s.SunProps;           // M45
        CurrentModelSubmeshMaterials = s.Materials;
        CurrentModelParticles = s.Particles;
        _vfxSystems = s.VfxSystems;
        CurrentModelProbes = s.Probes;
        CurrentModelProps = s.Props;
        SelectedParticleTreeItem = null;
        MapGeoInspector.Show(s.Map, s.Entry.Path);
        MapContent.SetLayerGroups(s.LayerGroups);
        MapContent.ShowMap(s.MapName, s.Pieces);
        HasMapMoves = s.HasMoves;
        Inspector.ShowEntry(s.Entry);
        HasInspectorBody = true;
        InspectorTab = 0;
        TryLoadMaterialBin(s.Entry, alsoRawBin: true);

        var meshes = s.SelectedMeshIndices
            .Select(i => s.Map.Meshes.FirstOrDefault(x => x.Index == i))
            .Where(m => m is not null).Select(m => m!).ToList();
        _selection.SetMany(meshes);
        SelectedDragonIndex = s.DragonIndex;
        SelectedBaronIndex = s.BaronIndex;
        ApplyMapVisibility();   // recompute the visibility array from the restored filters
        MeshVerticesRevision++; // re-upload possibly-edited vertices
        _log.Info("MapGeo", $"Restored map tab '{s.MapName}' ({s.Map.MeshCount:n0} meshes).");
    }

    /// <summary>Push the freshly-built asset tree into the Content Browser + Map Content panels.</summary>
    private void RefreshContentPanels()
    {
        ContentBrowser.SetRoots(RootNodes);
        var maps = _nodesByHash.Values
            .Where(n => n.Entry is { Type: AssetType.MapGeometry })
            .Where(n => !ProjectMode || n.Entry!.SourceKind != AssetSourceKind.RiotReference)
            .OrderBy(n => n.Entry!.Path, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(n => n.Entry!.PathHash)
            .ToList();
        MapContent.SetMaps(maps);
        MapContent.ClearMap();
    }

    // ---- Material editor: asset access helpers --------------------------

    private byte[]? ReadAssetByPath(string path)
    {
        if (!ContentLoaded || string.IsNullOrEmpty(path)) return null;
        var hash = HashAlgorithms.WadPath(path);
        return TryResolveEntry(hash, out _) ? ReadAsset(hash) : null;
    }

    private bool TextureExistsByPath(string path)
    {
        if (!ContentLoaded || string.IsNullOrEmpty(path)) return false;
        return TryResolveEntry(HashAlgorithms.WadPath(path), out _);
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
        if (!ContentLoaded) return;
        var hash = HashAlgorithms.WadPath(path);
        if (_nodesByHash.TryGetValue(hash, out var node)) SelectedNode = node;
        else _log.Warn("Material", $"Texture not found: {path}");
    }

    // ---- Animation ------------------------------------------------------

    private AnimationClip? DecodeAnimation(WadAssetEntry entry)
    {
        if (!ContentLoaded) return null;
        try { return AnimationDecoder.Decode(ReadAsset(entry.PathHash), entry.DisplayName); }
        catch (Exception ex) { _log.Error("Anim", $"{entry.DisplayName}: {ex.Message}"); return null; }
    }

    private IEnumerable<AnimationEntryViewModel> FindAnimations(WadAssetEntry skn)
    {
        if (!ContentLoaded || !skn.IsResolved) return Enumerable.Empty<AnimationEntryViewModel>();
        var parts = skn.Path.Split('/');
        int ci = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
        string champ = ci >= 0 && ci + 1 < parts.Length ? parts[ci + 1] : "";
        var marker = $"/characters/{champ}/";
        const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
        bool Match(string path, bool resolved) =>
            resolved && path.EndsWith(".anm", OIC) && (champ.Length == 0 || path.Contains(marker, OIC));

        var seen = new HashSet<ulong>();
        var list = new List<AnimationEntryViewModel>();
        foreach (var e in AssetEntries)
            if (Match(e.Path, e.IsResolved) && seen.Add(e.PathHash)) list.Add(new AnimationEntryViewModel(e));

        // If the mod doesn't ship this unit's animations, fall back to the original game WADs.
        if (list.Count == 0 && _mounts is not null)
            foreach (var fb in _mounts.Fallback)
                foreach (var a in fb.Enumerate())
                    if (Match(a.VirtualPath, a.IsResolved) && seen.Add(a.PathHash)) list.Add(new AnimationEntryViewModel(a.ToEntry()));

        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
            Documents.Clear(); ActiveDocument = null;  // fresh source — old tabs are stale
            RebuildTree();
            ClearViewport();
            Inspector.Clear();
            UndoService.Clear(); // new inspection context = fresh history

            _mounts?.Dispose(); _mounts = null;
            ProjectMode = false; InspectionMode = true;
            _log.Success("WAD", $"Loaded {_archive.Entries.Count:n0} chunks; resolved {_archive.ResolvedCount:n0} paths.");
            _log.Info("WAD", "Single-WAD inspection mode — open a project folder (File ▸ Open Project Folder) to edit and build mods.");
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
        if (!ContentLoaded) return;
        var root = AssetTree.Build(_archive.Entries, _archive.Name);
        RootNodes.Clear();
        _nodesByHash.Clear();
        var rootVm = new AssetNodeViewModel(root);
        IndexNodes(rootVm);
        RootNodes.Add(rootVm);
        RefreshAllStatuses();
        RefreshContentPanels();
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
    private byte[] GetAssetBytes(WadAssetEntry entry) => ReadAsset(entry.PathHash);

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
        if (entry is null || !ContentLoaded) { _log.Warn("Export", "Select a file first."); return; }
        var outPath = await Dialogs.SaveFileAsync("Export asset", entry.DisplayName);
        if (outPath is null) return;
        try
        {
            File.WriteAllBytes(outPath, ReadAsset(entry.PathHash));
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
        if (!ContentLoaded) return;
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
        if (_restoringScene) return; // a document tab is restoring its cached scene — don't re-load
        var entry = value?.Entry;
        if (entry is null) return;

        // Unresolved chunks have no extension — sniff the type from magic bytes so
        // preview/decode still works before a hash sync (guard against huge chunks).
        if (entry.Type == AssetType.Unknown && _archive is not null && entry.UncompressedSize < 32 * 1024 * 1024)
        {
            try { entry.Type = AssetTypeDetector.FromMagic(ReadAsset(entry.PathHash)); }
            catch { /* leave Unknown */ }
        }

        Inspector.ShowEntry(entry);
        Inspector.SetPreview(null);
        bool modified = _overrides.Has(entry.PathHash);
        string source = !ProjectMode ? "WAD"
            : entry.SourceKind switch
            {
                AssetSourceKind.RiotReference => "Read-only Riot asset",
                AssetSourceKind.ProjectOverride => "Project override (editable)",
                _ => "Project asset (editable)",
            };
        Inspector.SetAssetStatus(
            modified ? $"Modified — {source}" : source,
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
                _ = LoadMeshPreviewAsync(entry);   // M50: separate model window — the map viewport stays untouched
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
        if (!ContentLoaded) return null;
        if (entry.Type == AssetType.Bin) return entry;
        if (!entry.IsResolved) return null;
        string? binPath = entry.Type switch
        {
            AssetType.SkinnedMesh => SkinPaths.BinPathForSkn(entry.Path),
            AssetType.MapGeometry => MapGeoMaterialResolver.MaterialsBinPathFor(entry.Path),
            _ => null,
        };
        if (binPath is null) return null;
        return TryResolveEntry(HashAlgorithms.WadPath(binPath), out var be) ? be : null;
    }

    private void TryLoadMaterialBin(WadAssetEntry entry, bool alsoRawBin)
    {
        var binEntry = ResolveMaterialBin(entry);
        if (binEntry is null) { MaterialEditor.Clear(); HasMaterialData = false; return; }
        _ = LoadMaterialBinAsync(binEntry, alsoRawBin);
    }

    private async Task LoadMaterialBinAsync(WadAssetEntry binEntry, bool alsoRawBin)
    {
        if (!ContentLoaded) return;
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
                // M50: the materials list lives in the Inspector's Materials tab now (the Content
                // Browser quick-list was removed) — jump straight to it for materials.bin selections.
                if (binEntry.Path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase))
                { InspectorTab = 1; AssetDataExpanded = true; }
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
                var resolved = ChampionMaterialResolver.Resolve(bytes, ResolveBinName);
                CurrentModelTextures = BuildSubmeshTextures(mesh, resolved, "material preview");
            }
            else if (MaterialEditor.Kind == MaterialSourceKind.MapMaterials && _currentMap is { } map && CurrentMesh is not null)
            {
                var names = map.Groups.Select(g => g.Material).Where(m => m.Length > 0).Distinct().ToList();
                var m2t = MapGeoMaterialResolver.Resolve(bytes, names);
                var profiles = MaterialProfiles.ForMapMaterials(bytes, names, ResolveBinName);
                CurrentModelTextures = BuildMapTextures(map, m2t, profiles, names.Count);
            }
            else { _log.Info("Material", "Nothing in the viewport to preview — select the matching .skn/.mapgeo."); return; }
            _log.Success("Material", "Applied material edits to the viewport (live).");
        }
        catch (Exception ex) { _log.Error("Material", $"Apply failed: {ex.Message}"); }
    }

    private async Task SaveMaterialOverride()
    {
        if (MaterialEditor.BinEntry is not { } binEntry) { _log.Warn("Material", "No material .bin open."); return; }
        if (!GuardEditable(binEntry)) return;
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
            UndoService.MarkSaved();
            _log.Success("Material", $"Saved edited material {binEntry.DisplayName} to override ({bytes.Length:n0} bytes, re-parse OK). Build Package will include it.");
        }
        catch (Exception ex) { _log.Error("Material", ex.Message); }
    }

    private async Task ReplaceTextureForSlot(TextureSlotViewModel slot)
    {
        if (!ContentLoaded) return;
        var path = slot.EditedPath;
        var hash = HashAlgorithms.WadPath(path);
        if (!TryResolveEntry(hash, out _)) { _log.Warn("Material", $"Texture not found — can't replace: {path}"); return; }
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

    private void ClearSecondaryTextures()
    {
        CurrentModelMaskTextures = null;
        CurrentModelGradientTextures = null;
        CurrentModelEmissiveTextures = null;
        CurrentModelMatCapTextures = null;
        CurrentModelMatCapMaskTextures = null;
        // NOTE: CurrentModelLightmapTextures is NOT reset here — like CurrentModelSubmeshMaterials it is
        // published by BuildMapTextures (which runs before the UI-thread ClearSecondaryTextures call) and
        // reset explicitly on the mesh/clear paths, so clearing it here would wipe a freshly-loaded map's.
    }

    private void ClearViewport()
    {
        CurrentMesh = null;
        CurrentSkeleton = null;
        if (_currentMap is { } clearedMap) UndoService.PurgeContext(clearedMap);
        _currentMap = null;
        _currentMapProfiles = null;
        _mapControllers = null;
        _visibilityResolver = null;
        _currentMapBytes = null;
        _currentMapEntry = null;
        _selection.Clear();
        HasMapMoves = false;
        CurrentModelTextures = null;
        ClearSecondaryTextures();
        CurrentModelLightmapTextures = null;
        CurrentModelSubmeshMaterials = null;
        CurrentModelSubmeshVisible = null;
        HasFlowmapWater = false;
        _mapFlowMasks = null;
        _mapFlowGrads = null;
        CurrentLightmapScale = 1.0;
        _currentSunProps = null;
        CurrentModelParticles = null;
        SelectedParticleTreeItem = null;
        ParticleMarkers = null;
        CurrentModelProbes = null;
        CurrentModelProps = null;
        CurrentPropMeshes = null;
        ShowPropMeshes = false;
        PropMarkers = null;
        ProbeMarkers = null;
        SelectedPropTreeItem = null;
        SelectedPropNode = null;
        SelectedProbe = null;
        SelectedPlaceableInfo = "";
        PlayParticlePreview = false;
        PlayAllParticles = false;
        CurrentParticlePlayback = null;
        SelectedChampionVfx = null;
        ChampionVfxSystems.Clear();
        HasChampionVfx = false;
        _vfxSystems = EmptyVfx;
        _vfxTextureCache.Clear(); _vfxMeshCache.Clear();
        CurrentAnimation = null;
        AnimationTime = 0;
        Animation.Clear();
        MeshInspector.Clear();
        MapGeoInspector.Clear();
    }

    // ---- Map dragon/baron visibility layers (M22) ----------------------

    public IReadOnlyList<string> DragonOptions { get; } =
        new[] { "All Layers" }.Concat(MapVisibility.Dragons.Select(d => d.Name)).ToList();
    public IReadOnlyList<string> BaronOptions { get; } =
        new[] { "All" }.Concat(MapVisibility.Barons.Select(b => b.Name)).ToList();

    [ObservableProperty] private int _selectedDragonIndex;
    [ObservableProperty] private int _selectedBaronIndex;

    partial void OnSelectedDragonIndexChanged(int value) => ApplyMapVisibility();
    partial void OnSelectedBaronIndexChanged(int value) => ApplyMapVisibility();

    /// <summary>Compute per-group visibility from the selected dragon + baron layers and push it to the viewport.</summary>
    private void ApplyMapVisibility()
    {
        if (_currentMap is not { } map) { CurrentModelSubmeshVisible = null; return; }
        int dragonBit = SelectedDragonIndex <= 0 ? 0 : MapVisibility.Dragons[SelectedDragonIndex - 1].Bit;
        int baronBit = SelectedBaronIndex <= 0 ? 0 : MapVisibility.Barons[SelectedBaronIndex - 1].Bit;
        var resolver = _visibilityResolver ??= new MapVisibilityResolver(_mapControllers);
        var vis = new bool[map.Groups.Count];
        for (int i = 0; i < vis.Length; i++)
        {
            var g = map.Groups[i];
            vis[i] = resolver.IsVisible(g.VisibilityFlags, g.ControllerHash, dragonBit, baronBit);
        }
        CurrentModelSubmeshVisible = vis;
        RefreshMeshDetails();  // keep the inspector's mesh details + "why visible/hidden" in sync
        PruneSelectionToVisible(); // hidden (filtered-out) meshes must not stay selected/transformable
    }

    /// <summary>Current dragon/baron bits from the selectors (0 = "All").</summary>
    private int CurrentDragonBit => SelectedDragonIndex <= 0 ? 0 : MapVisibility.Dragons[SelectedDragonIndex - 1].Bit;
    private int CurrentBaronBit => SelectedBaronIndex <= 0 ? 0 : MapVisibility.Barons[SelectedBaronIndex - 1].Bit;

    /// <summary>Visibility diagnostic for the primary-selected mesh under the current dragon/baron filters (M33).</summary>
    [ObservableProperty] private string _meshVisibilityReason = "";

    /// <summary>The full mesh-details inspector for the selected mapgeo mesh (M33).</summary>
    public MeshDetailsViewModel MeshDetails { get; } = new();

    private void RefreshMeshDetails()
    {
        if (_selection.Primary is not { } m || _visibilityResolver is null)
        { MeshVisibilityReason = ""; MeshDetails.Clear(); return; }
        var d = _visibilityResolver.Resolve(m.VisibilityFlags, m.ControllerHash, CurrentDragonBit, CurrentBaronBit);
        MeshVisibilityReason = d.Reason;
        if (_selection.Count == 1)
        {
            string? material = _currentMap?.Groups.FirstOrDefault(g => g.MeshIndex == m.Index)?.Material;
            string? source = _currentMapEntry is { } e ? Path.GetFileName(MapGeoMaterialResolver.MaterialsBinPathFor(e.Path)) : null;
            MaterialProfile? profile = material is not null ? _currentMapProfiles?.GetValueOrDefault(material) : null;
            MeshDetails.Load(m, material, source, d, profile);
        }
        else MeshDetails.Clear(); // multi-select uses the batch panel, not per-mesh details
    }

    /// <summary>Drop any selected meshes that the current visibility filter hides (a mesh is visible if
    /// at least one of its submesh groups is visible), so batch transforms never touch filtered geometry.</summary>
    private void PruneSelectionToVisible()
    {
        if (_selection.IsEmpty || _currentMap is not { } map || CurrentModelSubmeshVisible is not { } vis) return;
        var visibleMeshIndices = new HashSet<int>();
        int n = System.Math.Min(map.Groups.Count, vis.Count);
        for (int i = 0; i < n; i++)
            if (vis[i]) visibleMeshIndices.Add(map.Groups[i].MeshIndex);
        var keep = _selection.Items.Where(m => visibleMeshIndices.Contains(m.Index)).ToList();
        if (keep.Count != _selection.Count) _selection.SetMany(keep);
    }

    /// <summary>Tools ▸ Map Material Diagnostics — scan the loaded map's bins + mapgeo and write an honest
    /// report (classes, exposed vs unknown fields, lighting/lightmap/visibility signals) to
    /// <c>.reyengine/reports/materials_diagnostics_&lt;map&gt;.json</c> (M33).</summary>
    [RelayCommand]
    private void MapMaterialDiagnostics()
    {
        if (_currentMap is null || _currentMapEntry is not { } entry || _currentMapBytes is null)
        { _log.Warn("Diagnostics", "Load a map first, then run Map Material Diagnostics."); return; }
        try
        {
            var dir = entry.Path[..(entry.Path.LastIndexOf('/') + 1)];
            var bins = new List<(string, byte[])>();
            foreach (var e in AssetEntries.Where(e => e.IsResolved
                         && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                         && e.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)))
            { try { bins.Add((e.Path, ReadAsset(e.PathHash))); } catch { /* skip unreadable */ } }

            var report = MapDiagnosticsReport.Build(entry.DisplayName, bins, _currentMapBytes, ResolveBinName);
            var safe = new string(entry.DisplayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            var path = Path.Combine(ProjectWorkspace.ReportsDir(Project), $"materials_diagnostics_{safe}.json");
            File.WriteAllText(path, report.ToJson());
            _log.Success("Diagnostics", $"Map diagnostics written: {path}");
            foreach (var f in report.LightmapFindings.Concat(report.LightingFindings)
                         .Concat(report.VisibilityFindings).Concat(report.PreviewFindings))
                _log.Info("Diagnostics", f);
        }
        catch (Exception ex) { _log.Error("Diagnostics", ex.Message); }
    }

    /// <summary>Index the baron/dragon visibility controllers from the map's sibling .bin files.</summary>
    private void BuildMapControllers(string mapgeoPath)
    {
        var dir = mapgeoPath[..(mapgeoPath.LastIndexOf('/') + 1)];
        var bins = new List<byte[]>();
        foreach (var e in AssetEntries.Where(e => e.IsResolved
                     && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                     && e.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)))
        {
            try { bins.Add(ReadAsset(e.PathHash)); } catch { /* skip unreadable bins */ }
        }
        _mapControllers = MapVisibilityControllers.Build(bins);
        _visibilityResolver = new MapVisibilityResolver(_mapControllers);
        if (_mapControllers.BaronControllerCount > 0)
            _log.Info("MapGeo", $"Baron visibility: {_mapControllers.Count} controllers ({_mapControllers.BaronControllerCount} baron) from {bins.Count} bin(s).");
        else
            _log.Info("MapGeo", $"No baron visibility controllers found in this map's bins — baron filter inactive (dragon layers still work).");
    }

    /// <summary>Build the Map Content layer-group outline (Meshes → Layer Groups → mesh names).</summary>
    private void BuildMapLayerGroups(MapGeoAsset map)
    {
        var groups = map.Meshes
            .GroupBy(m => m.VisibilityFlags)
            .Select(g =>
            {
                var vm = new MapLayerGroupViewModel
                {
                    Name = $"{MapVisibility.DragonLabel(g.Key)} — {g.Count()} mesh(es)",
                    Bit = g.Key,
                };
                foreach (var mesh in g.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                    vm.Meshes.Add(new MapPieceViewModel { Name = mesh.Name, Info = "", MeshIndex = mesh.Index });
                return vm;
            })
            .OrderByDescending(vm => vm.Meshes.Count)
            .ToList();
        MapContent.SetLayerGroups(groups);
    }

    // ---- Mesh move / rotate / scale (M25/M26) ---------------------------
    // "Position" shown/edited is the mesh's own pivot (local bbox center) + its offset — the world-space
    // location of the mesh's center, which stays meaningful independent of any applied rotation/scale.

    private byte[]? _currentMapBytes;
    private WadAssetEntry? _currentMapEntry;

    [ObservableProperty] private MapGeoMesh? _selectedMapMesh;
    [ObservableProperty] private object? _selectedTreeItem;
    [ObservableProperty] private string _meshMoveX = "0";
    [ObservableProperty] private string _meshMoveY = "0";
    [ObservableProperty] private string _meshMoveZ = "0";
    [ObservableProperty] private string _meshRotateX = "0";
    [ObservableProperty] private string _meshRotateY = "0";
    [ObservableProperty] private string _meshRotateZ = "0";
    [ObservableProperty] private string _meshScaleX = "1";
    [ObservableProperty] private string _meshScaleY = "1";
    [ObservableProperty] private string _meshScaleZ = "1";
    [ObservableProperty] private int _meshVerticesRevision;
    [ObservableProperty] private bool _hasMapMoves;

    // ---- Multi-selection + batch transform (M30) -----------------------
    private readonly SelectionSet<MapGeoMesh> _selection = new();
    private bool _syncingTreeSelection;   // reentrancy guard: tree<->selection sync must not recurse

    [ObservableProperty] private IReadOnlyList<(System.Numerics.Vector3 min, System.Numerics.Vector3 max)>? _selectionBoxes;
    [ObservableProperty] private System.Numerics.Vector3? _groupBoundsMin;
    [ObservableProperty] private System.Numerics.Vector3? _groupBoundsMax;
    [ObservableProperty] private System.Numerics.Vector3? _gizmoPivot;   // selection center = gizmo origin
    [ObservableProperty] private bool _isMultiSelect;                    // 2+ meshes → batch inspector
    [ObservableProperty] private bool _isSingleSelect;                   // exactly 1 → single-mesh inspector
    [ObservableProperty] private string _selectionStatus = "";          // e.g. "3 meshes selected"

    // Batch transform deltas — applied to the whole selection around its center (blank/identity = no-op).
    [ObservableProperty] private string _batchMoveX = "0";
    [ObservableProperty] private string _batchMoveY = "0";
    [ObservableProperty] private string _batchMoveZ = "0";
    [ObservableProperty] private string _batchRotateX = "0";
    [ObservableProperty] private string _batchRotateY = "0";
    [ObservableProperty] private string _batchRotateZ = "0";
    [ObservableProperty] private string _batchScaleX = "1";
    [ObservableProperty] private string _batchScaleY = "1";
    [ObservableProperty] private string _batchScaleZ = "1";

    /// <summary>M51: single selection over the unified hierarchy — routes by node type (mesh piece,
    /// particle placement, animated prop, probe). Folder/group clicks are ignored.</summary>
    [ObservableProperty] private object? _selectedOutlinerItem;
    partial void OnSelectedOutlinerItemChanged(object? value)
    {
        if (_syncingTreeSelection) return;
        switch (value)
        {
            case MapPieceViewModel { MeshIndex: >= 0 } p when _currentMap is { } map
                && map.Meshes.FirstOrDefault(x => x.Index == p.MeshIndex) is { } m:
                _selection.SetSingle(m);
                break;
            case ParticlePlacementViewModel pp:
                SelectedParticleNode = pp;
                break;
            case AnimatedPropViewModel ap:
                SelectedPropTreeItem = ap;
                break;
            case CubemapProbeViewModel pr:
                SelectedProbe = pr;
                break;
        }
    }

    partial void OnSelectedTreeItemChanged(object? value)
    {
        if (_syncingTreeSelection) return; // sync is pushing the selection INTO the tree — don't loop back
        // Match by MapGeoMesh.Index (the env-mesh index), not list position — they diverge if any mesh
        // failed to decode. A plain tree click is a single-select (Ctrl+click toggling is handled separately).
        if (value is MapPieceViewModel { MeshIndex: >= 0 } p && _currentMap is { } map
            && map.Meshes.FirstOrDefault(x => x.Index == p.MeshIndex) is { } m)
            _selection.SetSingle(m);
        else if (value is null)
            _selection.Clear();
    }

    /// <summary>
    /// Blender/UE-style viewport click-selection: cast the pick ray at the map's visible triangles and
    /// select the nearest-hit mesh. Plain click = single-select; <paramref name="additive"/> (Ctrl) toggles
    /// the hit mesh in/out of the current set; an empty non-additive click clears the selection.
    /// </summary>
    public void SelectMeshFromViewport(System.Numerics.Vector3 rayOrigin, System.Numerics.Vector3 rayDir, bool additive = false)
    {
        if (_currentMap is not { } map || map.Groups.Count == 0) return;
        var submeshes = map.Groups.Select(g => (g.StartIndex, g.IndexCount)).ToList();
        int hit = ViewportMeshPicker.PickSubmesh(map.Positions, map.Indices, submeshes,
            CurrentModelSubmeshVisible, rayOrigin, rayDir, out _);
        if (hit < 0)
        {
            if (!additive) _selection.Clear(); // empty click clears; Ctrl+empty keeps the set (UE/Blender)
            return;
        }
        int meshIndex = map.Groups[hit].MeshIndex;
        var mesh = map.Meshes.FirstOrDefault(x => x.Index == meshIndex);
        if (mesh is null) return;
        if (additive) _selection.Toggle(mesh);
        else _selection.SetSingle(mesh);
        var name = mesh.Name?.Length > 0 ? mesh.Name : $"#{meshIndex}";
        _log.Info("MapGeo", additive ? $"{(_selection.Contains(mesh) ? "Added" : "Removed")} '{name}' ({_selection.Count} selected)."
                                      : $"Selected '{name}' (viewport click).");
    }

    /// <summary>Ctrl+click on a Map Content tree row: toggle that mesh in/out of the selection.</summary>
    public void ToggleMeshSelectionFromTree(MapPieceViewModel piece)
    {
        if (_currentMap is not { } map || piece.MeshIndex < 0) return;
        if (map.Meshes.FirstOrDefault(x => x.Index == piece.MeshIndex) is { } m) _selection.Toggle(m);
    }

    /// <summary>Central selection handler (raised by <see cref="SelectionSet{T}.Changed"/>): re-derive the
    /// primary mesh, single/multi flags, status text, tree highlight, and all viewport visuals.</summary>
    private void OnMeshSelectionChanged()
    {
        var primary = _selection.Primary;
        SelectedMapMesh = primary;
        IsMultiSelect = _selection.IsMulti;
        IsSingleSelect = _selection.Count == 1;
        SelectionStatus = _selection.Count switch { 0 => "", 1 => "1 mesh selected", var n => $"{n} meshes selected" };
        if (primary is not null) RefreshMeshTransformFields(primary);
        SyncTreeHighlight();
        RefreshSelectionVisuals();
        RefreshMeshDetails();
    }

    /// <summary>Mirror the SelectionSet onto the tree: mark selected rows' <c>IsSelected</c>, and keep the
    /// TreeView's single SelectedItem pointed at the primary (guarded so it doesn't feed back).</summary>
    private void SyncTreeHighlight()
    {
        var selectedIndices = _selection.Items.Select(m => m.Index).ToHashSet();
        MapPieceViewModel? primaryPiece = null;
        foreach (var g in MapContent.LayerGroups)
            foreach (var piece in g.Meshes)
            {
                piece.IsSelected = piece.MeshIndex >= 0 && selectedIndices.Contains(piece.MeshIndex);
                if (piece.IsSelected && _selection.Primary is { } pm && piece.MeshIndex == pm.Index) primaryPiece = piece;
            }
        _syncingTreeSelection = true;
        SelectedTreeItem = primaryPiece; // scrolls/anchors the tree to the primary without re-triggering select
        SelectedOutlinerItem = primaryPiece; // M51: unified hierarchy mirrors the selection
        _syncingTreeSelection = false;
    }

    /// <summary>Recompute the per-mesh selection highlight boxes (live vertex bounds), the combined group
    /// bounds, and the gizmo pivot (selection center). Call after selecting and after any vertex-moving edit.</summary>
    private void RefreshSelectionVisuals()
    {
        if (_selection.IsEmpty || _currentMap is not { } map)
        {
            SelectionBoxes = null; GroupBoundsMin = GroupBoundsMax = GizmoPivot = null;
            SelectedSubmeshIndices = null; SelectedMeshMaterials = null; HasSelectedMeshMaterials = false;
            return;
        }

        // M50b: a mesh selection is EXCLUSIVE — deselect placeables so the inspector doesn't keep
        // showing the previously-selected particle/prop/probe next to the mesh sections (Unity-style).
        if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
        if (SelectedPropTreeItem is not null) SelectedPropTreeItem = null;
        if (SelectedProbe is not null) SelectedProbe = null;
        SelectedPlaceableInfo = "";

        // M50b: outline highlight (mesh wireframe overlay) + the selection's assigned materials.
        var meshIdx = _selection.Items.Select(m => m.Index).ToHashSet();
        var subIdx = new List<int>();
        var mats = new List<MeshMaterialSlotViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < map.Groups.Count; i++)
        {
            if (!meshIdx.Contains(map.Groups[i].MeshIndex)) continue;
            subIdx.Add(i);
            var name = map.Groups[i].Material;
            if (name.Length > 0 && seen.Add(name))
                mats.Add(new MeshMaterialSlotViewModel(name,
                    _currentMapProfiles?.GetValueOrDefault(name)?.RenderStateSummary ?? ""));
        }
        SelectedSubmeshIndices = subIdx;
        SelectedMeshMaterials = mats;
        HasSelectedMeshMaterials = mats.Count > 0;
        var boxes = new List<(System.Numerics.Vector3 min, System.Numerics.Vector3 max)>(_selection.Count);
        var gmin = new System.Numerics.Vector3(float.MaxValue);
        var gmax = new System.Numerics.Vector3(float.MinValue);
        foreach (var m in _selection.Items)
        {
            if (m.VertexCount <= 0) continue;
            var min = new System.Numerics.Vector3(float.MaxValue);
            var max = new System.Numerics.Vector3(float.MinValue);
            int start = m.VertexStart * 3, end = (m.VertexStart + m.VertexCount) * 3;
            for (int i = start; i < end; i += 3)
            {
                var p = new System.Numerics.Vector3(map.Positions[i], map.Positions[i + 1], map.Positions[i + 2]);
                min = System.Numerics.Vector3.Min(min, p);
                max = System.Numerics.Vector3.Max(max, p);
            }
            boxes.Add((min, max));
            gmin = System.Numerics.Vector3.Min(gmin, min);
            gmax = System.Numerics.Vector3.Max(gmax, max);
        }
        if (boxes.Count == 0) { SelectionBoxes = null; GroupBoundsMin = GroupBoundsMax = GizmoPivot = null; return; }
        SelectionBoxes = null;   // M50b: selection reads as a mesh OUTLINE now, not AABB boxes
        // Group bounds box only makes sense for a multi-selection; a single mesh already has its highlight box.
        GroupBoundsMin = _selection.IsMulti ? gmin : null;
        GroupBoundsMax = _selection.IsMulti ? gmax : null;
        GizmoPivot = (gmin + gmax) * 0.5f; // selection center = combined bbox center
    }

    // Drag state captured at gizmo-press so the WHOLE drag is one undo step. For a multi-selection we
    // record every mesh's before-state and the primary's start offset (to derive the world delta).
    private (MapGeoMesh mesh, MeshTransformCommand.State before)[] _dragBefore = System.Array.Empty<(MapGeoMesh, MeshTransformCommand.State)>();
    private System.Numerics.Vector3 _dragStartPrimaryOffset;

    /// <summary>Called at gizmo-press: capture the transform(s) so the whole drag becomes ONE undo step.</summary>
    public void BeginMeshDrag()
    {
        _dragBefore = _selection.Items.Select(m => (m, MeshTransformCommand.State.Capture(m))).ToArray();
        _dragStartPrimaryOffset = _selection.Primary?.Offset ?? System.Numerics.Vector3.Zero;
    }

    /// <summary>Live-drag the selection to an absolute primary offset (called every pointer-move frame by
    /// the viewport's translate gizmo). Single mesh moves via its own offset; a multi-selection moves rigidly
    /// as a group (world delta applied through the GroupMatrix). Cheap + silent; <see cref="EndMeshDrag"/> logs.</summary>
    public void DragSelectedMeshTo(System.Numerics.Vector3 absoluteOffset)
    {
        if (_selection.Primary is not { } primary || _currentMap is not { } map) return;
        if (_selection.IsMulti)
        {
            // Restore all meshes to their drag-start state, then batch-translate by the total world delta —
            // absolute-from-start so repeated frames don't accumulate.
            var worldDelta = absoluteOffset - _dragStartPrimaryOffset;
            foreach (var (mesh, before) in _dragBefore) { before.ApplyTo(mesh); map.ApplyMeshTransform(mesh); }
            map.BatchTranslate(_selection.Items, worldDelta);
        }
        else
        {
            map.TranslateMesh(primary, absoluteOffset);
        }
        RefreshMeshTransformFields(primary);
        RefreshSelectionVisuals();
        MeshVerticesRevision++;
    }

    // ---- M42: transform gizmo mode / space / snap ----
    /// <summary>Active gizmo: 0 = Move, 1 = Rotate, 2 = Scale.</summary>
    [ObservableProperty] private int _transformMode;
    /// <summary>Gizmo axes follow the mesh's own rotation (Local) instead of world axes.</summary>
    [ObservableProperty] private bool _gizmoLocalSpace;
    [ObservableProperty] private bool _snapEnabled;

    public bool IsMoveMode => TransformMode == 0;
    public bool IsRotateMode => TransformMode == 1;
    public bool IsScaleMode => TransformMode == 2;
    public string GizmoSpaceLabel => GizmoLocalSpace ? "Local" : "World";

    // snap increments (world units / degrees / scale ratio)
    public const float MoveSnap = 100f, RotateSnap = 15f, ScaleSnap = 0.25f;
    public float ApplyMoveSnap(float v) => SnapEnabled ? MathF.Round(v / MoveSnap) * MoveSnap : v;
    public float ApplyRotateSnap(float v) => SnapEnabled ? MathF.Round(v / RotateSnap) * RotateSnap : v;
    public float ApplyScaleSnap(float v) => SnapEnabled ? MathF.Max(0.05f, MathF.Round(v / ScaleSnap) * ScaleSnap) : v;

    partial void OnTransformModeChanged(int value)
    {
        OnPropertyChanged(nameof(IsMoveMode));
        OnPropertyChanged(nameof(IsRotateMode));
        OnPropertyChanged(nameof(IsScaleMode));
        GizmoRevision++;
    }
    partial void OnGizmoLocalSpaceChanged(bool value) { OnPropertyChanged(nameof(GizmoSpaceLabel)); OnPropertyChanged(nameof(GizmoAxes)); GizmoRevision++; }

    /// <summary>Bumped whenever the gizmo's mode/space changes so the viewport rebuilds its handles.</summary>
    [ObservableProperty] private int _gizmoRevision;

    [RelayCommand] private void SetTransformMode(string mode) { if (int.TryParse(mode, out var m)) TransformMode = m; }
    [RelayCommand] private void ToggleGizmoSpace() => GizmoLocalSpace = !GizmoLocalSpace;

    /// <summary>Live rotate the selected mesh about its pivot (M42 gizmo). Single-select only.</summary>
    public void RotateSelectedMeshTo(System.Numerics.Vector3 rotationDegrees)
    {
        if (_selection.Primary is not { } primary || _currentMap is not { } map) return;
        map.RotateMesh(primary, rotationDegrees);
        RefreshMeshTransformFields(primary);
        RefreshSelectionVisuals();
        MeshVerticesRevision++;
    }

    /// <summary>Live scale the selected mesh about its pivot (M42 gizmo). Single-select only.</summary>
    public void ScaleSelectedMeshTo(System.Numerics.Vector3 scale)
    {
        if (_selection.Primary is not { } primary || _currentMap is not { } map) return;
        map.ScaleMesh(primary, scale);
        RefreshMeshTransformFields(primary);
        RefreshSelectionVisuals();
        MeshVerticesRevision++;
    }

    /// <summary>The selected mesh's current rotation/scale — the drag's start state for gizmo rotate/scale.</summary>
    public (System.Numerics.Vector3 rot, System.Numerics.Vector3 scale) SelectedMeshRotScale =>
        _selection.Primary is { } p ? (p.RotationDegrees, p.Scale) : (System.Numerics.Vector3.Zero, System.Numerics.Vector3.One);

    /// <summary>The selected mesh's local axes (its rotation applied to world X/Y/Z) for Local-space gizmo.</summary>
    public (System.Numerics.Vector3 x, System.Numerics.Vector3 y, System.Numerics.Vector3 z) SelectedMeshLocalAxes
    {
        get
        {
            if (!GizmoLocalSpace || _selection.Primary is not { } p)
                return (System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ);
            var r = p.RotationDegrees * (MathF.PI / 180f);
            var q = System.Numerics.Quaternion.CreateFromYawPitchRoll(r.Y, r.X, r.Z);
            return (System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, q),
                    System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, q),
                    System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, q));
        }
    }

    /// <summary>The three gizmo axis directions (world, or the selected mesh's local axes) for the viewport.</summary>
    public IReadOnlyList<System.Numerics.Vector3> GizmoAxes
    {
        get { var (x, y, z) = SelectedMeshLocalAxes; return new[] { x, y, z }; }
    }

    partial void OnGizmoPivotChanged(System.Numerics.Vector3? value) => OnPropertyChanged(nameof(GizmoAxes));

    public void EndMeshDrag()
    {
        if (_selection.Primary is not { } primary || _currentMap is not { } map || _dragBefore.Length == 0) return;
        string verb = TransformMode == 1 ? "Rotate" : TransformMode == 2 ? "Scale" : "Move";
        if (_selection.IsMulti)
        {
            var entries = _dragBefore.Select(b => (b.mesh, b.before, MeshTransformCommand.State.Capture(b.mesh)));
            var cmd = new BatchTransformCommand($"{verb} Meshes", map, entries, MakeBatchRefresh(map));
            if (cmd.HasChange) UndoService.PushApplied(cmd);
            _log.Info("MapGeo", $"{verb}d {_dragBefore.Length} meshes via gizmo.");
        }
        else
        {
            PushTransformCommand($"{verb} Mesh", map, primary, _dragBefore[0].before, MeshTransformCommand.State.Capture(primary));
            _log.Info("MapGeo", $"{verb}d '{primary.Name}' via gizmo.");
        }
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes);
    }

    private void RefreshMeshTransformFields(MapGeoMesh m)
    {
        var pos = m.Pivot + m.Offset;
        MeshMoveX = pos.X.ToString("0.###", CultureInfo.InvariantCulture);
        MeshMoveY = pos.Y.ToString("0.###", CultureInfo.InvariantCulture);
        MeshMoveZ = pos.Z.ToString("0.###", CultureInfo.InvariantCulture);
        MeshRotateX = m.RotationDegrees.X.ToString("0.###", CultureInfo.InvariantCulture);
        MeshRotateY = m.RotationDegrees.Y.ToString("0.###", CultureInfo.InvariantCulture);
        MeshRotateZ = m.RotationDegrees.Z.ToString("0.###", CultureInfo.InvariantCulture);
        MeshScaleX = m.Scale.X.ToString("0.###", CultureInfo.InvariantCulture);
        MeshScaleY = m.Scale.Y.ToString("0.###", CultureInfo.InvariantCulture);
        MeshScaleZ = m.Scale.Z.ToString("0.###", CultureInfo.InvariantCulture);
        SelectedMeshNormalsFlipped = m.FlipNormals;
    }

    private static bool TryParseVector3(string sx, string sy, string sz, out System.Numerics.Vector3 v)
    {
        v = default;
        if (!float.TryParse(sx, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(sy, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(sz, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return false;
        v = new System.Numerics.Vector3(x, y, z);
        return true;
    }

    /// <summary>UI sync run after a transform command executes OR undoes (viewport, fields, highlight, dirty).</summary>
    private Action MakeTransformRefresh(MapGeoAsset map, MapGeoMesh mesh) => () =>
    {
        MeshVerticesRevision++;   // re-upload the edited vertices to the viewport (GL thread)
        if (ReferenceEquals(SelectedMapMesh, mesh))
        {
            RefreshMeshTransformFields(mesh);
            RefreshSelectionVisuals();
        }
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes);
    };

    /// <summary>UI sync run after a BATCH transform command executes OR undoes: re-upload vertices, refresh
    /// the primary's fields, recompute all selection visuals, and update the dirty flag.</summary>
    private Action MakeBatchRefresh(MapGeoAsset map) => () =>
    {
        MeshVerticesRevision++;
        if (SelectedMapMesh is { } primary) RefreshMeshTransformFields(primary);
        RefreshSelectionVisuals();
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes);
    };

    /// <summary>Run a batch operation on the whole selection as ONE undo step: capture every mesh's
    /// before-state, apply <paramref name="op"/>, then push a single <see cref="BatchTransformCommand"/>.</summary>
    private void RunBatch(string name, MapGeoAsset map, Action op)
    {
        var before = _selection.Items.Select(m => (mesh: m, state: MeshTransformCommand.State.Capture(m))).ToList();
        op();
        var entries = before.Select(b => (b.mesh, b.state, MeshTransformCommand.State.Capture(b.mesh)));
        var cmd = new BatchTransformCommand(name, map, entries, MakeBatchRefresh(map));
        if (!cmd.HasChange) return;
        UndoService.PushApplied(cmd);
        MeshVerticesRevision++;
        if (SelectedMapMesh is { } primary) RefreshMeshTransformFields(primary);
        RefreshSelectionVisuals();
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes);
    }

    /// <summary>Push an already-applied transform edit as one undo step (no-op if nothing changed).</summary>
    private void PushTransformCommand(string name, MapGeoAsset map, MapGeoMesh mesh,
        MeshTransformCommand.State before, MeshTransformCommand.State after)
    {
        if (before == after) return;
        UndoService.PushApplied(new MeshTransformCommand(name, map, mesh, before, after, MakeTransformRefresh(map, mesh)));
    }

    [RelayCommand]
    private void ApplyMeshMove()
    {
        if (SelectedMapMesh is not { } m || _currentMap is not { } map) return;
        if (!TryParseVector3(MeshMoveX, MeshMoveY, MeshMoveZ, out var target))
        { _log.Warn("MapGeo", "Enter valid position X/Y/Z numbers."); return; }
        if (!TryParseVector3(MeshRotateX, MeshRotateY, MeshRotateZ, out var rotation))
        { _log.Warn("MapGeo", "Enter valid rotation X/Y/Z numbers (degrees)."); return; }
        if (!TryParseVector3(MeshScaleX, MeshScaleY, MeshScaleZ, out var scale))
        { _log.Warn("MapGeo", "Enter valid scale X/Y/Z numbers."); return; }
        if (scale.X == 0 || scale.Y == 0 || scale.Z == 0)
        { _log.Warn("MapGeo", "Scale cannot be zero on any axis."); return; }

        var before = MeshTransformCommand.State.Capture(m);
        map.TranslateMesh(m, target - m.Pivot);
        map.RotateMesh(m, rotation);
        map.ScaleMesh(m, scale);
        PushTransformCommand("Transform Mesh", map, m, before, MeshTransformCommand.State.Capture(m));
        MeshVerticesRevision++;           // re-upload the edited vertices to the viewport
        RefreshSelectionVisuals();
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes);
        _log.Info("MapGeo", $"Transformed '{m.Name}': pos ({target.X:0.#}, {target.Y:0.#}, {target.Z:0.#}), " +
                            $"rot ({rotation.X:0.#}°, {rotation.Y:0.#}°, {rotation.Z:0.#}°), scale ({scale.X:0.##}, {scale.Y:0.##}, {scale.Z:0.##}).");
    }

    [RelayCommand]
    private void ResetMeshTransform()
    {
        if (SelectedMapMesh is not { } m || _currentMap is not { } map) return;
        var before = MeshTransformCommand.State.Capture(m);
        map.ResetMesh(m);
        PushTransformCommand("Reset Transform", map, m, before, MeshTransformCommand.State.Capture(m));
        RefreshMeshTransformFields(m);
        RefreshSelectionVisuals();
        MeshVerticesRevision++;
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes);
        _log.Info("MapGeo", $"Reset '{m.Name}' to its original transform.");
    }

    /// <summary>True when the selected mesh has its normals manually flipped (drives the toggle's checked state).</summary>
    [ObservableProperty] private bool _selectedMeshNormalsFlipped;

    /// <summary>M34: flip the selected mesh's vertex normals (live preview edit). Useful for meshes that a
    /// converter exported with inward-facing normals. Note: for two-sided (cullEnable=false) materials the
    /// two-sided lighting already lights both faces, so flipping there darkens rather than fixes.</summary>
    [RelayCommand]
    private void FlipMeshNormals()
    {
        if (SelectedMapMesh is not { } m || _currentMap is not { } map) return;
        map.SetFlipNormals(m, !m.FlipNormals);
        SelectedMeshNormalsFlipped = m.FlipNormals;
        MeshVerticesRevision++; // re-upload the flipped normals to the viewport (GL thread)
        RefreshMeshDetails();
        _log.Info("MapGeo", $"{(m.FlipNormals ? "Flipped" : "Restored")} normals on '{m.Name}'.");
    }

    // ---- Batch transform commands (M30) — operate on the whole selection around its center -------------

    private System.Numerics.Vector3 SelectionCenter() =>
        GizmoPivot ?? System.Numerics.Vector3.Zero; // gizmo pivot IS the live selection center

    [RelayCommand]
    private void ApplyBatchMove()
    {
        if (!_selection.IsMulti || _currentMap is not { } map) return;
        if (!TryParseVector3(BatchMoveX, BatchMoveY, BatchMoveZ, out var delta))
        { _log.Warn("MapGeo", "Enter valid batch move X/Y/Z numbers."); return; }
        if (delta == System.Numerics.Vector3.Zero) return;
        RunBatch("Batch Move", map, () => map.BatchTranslate(_selection.Items, delta));
        _log.Info("MapGeo", $"Moved {_selection.Count} meshes by ({delta.X:0.#}, {delta.Y:0.#}, {delta.Z:0.#}).");
    }

    [RelayCommand]
    private void ApplyBatchRotate()
    {
        if (!_selection.IsMulti || _currentMap is not { } map) return;
        if (!TryParseVector3(BatchRotateX, BatchRotateY, BatchRotateZ, out var euler))
        { _log.Warn("MapGeo", "Enter valid batch rotation X/Y/Z numbers (degrees)."); return; }
        if (euler == System.Numerics.Vector3.Zero) return;
        var center = SelectionCenter();
        RunBatch("Batch Rotate", map, () => map.BatchRotate(_selection.Items, euler, center));
        _log.Info("MapGeo", $"Rotated {_selection.Count} meshes by ({euler.X:0.#}°, {euler.Y:0.#}°, {euler.Z:0.#}°) about the selection center.");
    }

    [RelayCommand]
    private void ApplyBatchScale()
    {
        if (!_selection.IsMulti || _currentMap is not { } map) return;
        if (!TryParseVector3(BatchScaleX, BatchScaleY, BatchScaleZ, out var scale))
        { _log.Warn("MapGeo", "Enter valid batch scale X/Y/Z numbers."); return; }
        if (scale.X == 0 || scale.Y == 0 || scale.Z == 0)
        { _log.Warn("MapGeo", "Batch scale cannot be zero on any axis."); return; }
        if (scale == System.Numerics.Vector3.One) return;
        var center = SelectionCenter();
        RunBatch("Batch Scale", map, () => map.BatchScale(_selection.Items, scale, center));
        _log.Info("MapGeo", $"Scaled {_selection.Count} meshes by ({scale.X:0.##}, {scale.Y:0.##}, {scale.Z:0.##}) about the selection center.");
    }

    /// <summary>Reset every selected mesh to its original transform as one undo step.</summary>
    [RelayCommand]
    private void ResetSelected()
    {
        if (_currentMap is not { } map || _selection.IsEmpty) return;
        RunBatch("Reset Selected", map, () => { foreach (var m in _selection.Items) map.ResetMesh(m); });
        _log.Info("MapGeo", $"Reset {_selection.Count} selected mesh(es) to their original transforms.");
    }

    [RelayCommand]
    private void ClearSelection() => _selection.Clear();

    [RelayCommand]
    private async Task SaveMeshMoves()
    {
        if (_currentMap is not { } map || _currentMapBytes is null || _currentMapEntry is not { } entry) return;
        if (!MapGeoWriter.HasMoves(map.Meshes)) { _log.Info("MapGeo", "No mesh moves to save."); return; }
        if (!GuardEditable(entry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        var bytes = MapGeoWriter.TryWriteWithMoves(_currentMapBytes, map.Meshes, out var err);
        if (bytes is null) { _log.Error("MapGeo", $"Could not save mesh moves: {err}"); return; }
        try
        {
            var dest = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, ".mapgeo");
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = entry.PathHash,
                ResolvedPath = entry.IsResolved ? entry.Path : null,
                OverrideFile = dest,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            SetNodeStatus(entry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            UpdateTitle();
            int moved = map.Meshes.Count(x => x.IsMoved);
            UndoService.MarkSaved();
            _log.Success("MapGeo", $"Saved {moved} mesh move(s) to override ({bytes.Length:n0} bytes). Build Package will include it.");
        }
        catch (Exception ex) { _log.Error("MapGeo", ex.Message); }
    }

    /// <summary>Persist the moved particles into the map's .materials.bin override (M35).</summary>
    [RelayCommand]
    private async Task SaveParticleMoves()
    {
        if (_currentMapEntry is not { } mapEntry) return;
        var moved = MapContent.AllParticles.Where(v => v.IsMoved).ToList();
        if (moved.Count == 0) { _log.Info("Particles", "No particle moves to save."); return; }
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry)) { _log.Error("Particles", "No materials .bin to save into."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        var moves = moved.Select(v => (v.Placement.Transform, v.CurrentPosition)).ToList();
        var bytes = MapParticleWriter.WriteMoves(GetAssetBytes(binEntry), moves, out var err);
        if (bytes is null) { _log.Error("Particles", $"Could not save particle moves: {err}"); return; }
        if (err is not null) _log.Warn("Particles", err);
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
            HasParticleMoves = false;
            _log.Success("Particles", $"Saved {moved.Count} particle move(s) to the materials.bin override. Build Package will include it.");
        }
        catch (Exception ex) { _log.Error("Particles", ex.Message); }
    }

    private async Task LoadBinAsync(WadAssetEntry entry)
    {
        if (!ContentLoaded) return;
        try
        {
            var doc = await Task.Run(() =>
                BinEditorDocument.Parse(ReadAsset(entry.PathHash),
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
        if (!GuardEditable(entry)) return;
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
            UndoService.MarkSaved();
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
        if (!ContentLoaded) return;
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

    // ---- M50: model preview window (separate viewport; main viewport stays on the map) ----
    public MeshPreviewViewModel MeshPreview { get; } = new();
    public Action? ShowMeshPreviewWindow;   // wired by MainWindow (owns the window instance)

    private async Task LoadMeshPreviewAsync(WadAssetEntry entry)
    {
        if (!ContentLoaded) return;
        try
        {
            var (mesh, skeleton, textures) = await Task.Run(() =>
            {
                var m = SkinnedMeshDecoder.Decode(ReadAsset(entry.PathHash));
                var s = TryPairSkeleton(entry);
                var t = TryLoadPreviewDiffuse(entry, m);
                return (m, s, t);
            });
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MeshPreview.Show(entry.DisplayName, mesh, skeleton, textures);
                MeshInspector.ShowMesh(mesh, skeleton);
                ShowMeshPreviewWindow?.Invoke();
                _log.Success("Mesh", $"{entry.DisplayName}: {mesh.VertexCount:n0} verts, {mesh.TriangleCount:n0} tris — model preview window.");
            });
        }
        catch (Exception ex) { _log.Error("Mesh", ex.Message); }
    }

    /// <summary>Per-submesh diffuse textures for the model-preview window — NO side effects on the main
    /// viewport's texture/material state (unlike BuildSubmeshTextures, which publishes to it).</summary>
    private IReadOnlyList<TextureImage?>? TryLoadPreviewDiffuse(WadAssetEntry skn, MeshAsset mesh)
    {
        if (!ContentLoaded || !skn.IsResolved) return null;
        var binPath = SkinPaths.BinPathForSkn(skn.Path);
        if (binPath is null || !TryResolveEntry(HashAlgorithms.WadPath(binPath), out var binEntry)) return null;
        var resolved = ChampionMaterialResolver.Resolve(GetAssetBytes(binEntry), ResolveBinName);
        if (!resolved.HasAny) return null;
        var cache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        var result = new TextureImage?[mesh.SubMeshes.Count];
        for (int i = 0; i < mesh.SubMeshes.Count; i++)
        {
            var p = resolved.For(mesh.SubMeshes[i].Material);
            if (string.IsNullOrEmpty(p)) continue;
            if (!cache.TryGetValue(p, out var img)) cache[p] = img = LoadTextureByPath(p);
            result[i] = img;
        }
        return result;
    }

    private async Task LoadMeshAsync(WadAssetEntry entry)
    {
        if (!ContentLoaded) return;
        try
        {
            var (mesh, skeleton, textures, vfx) = await Task.Run(() =>
            {
                var m = SkinnedMeshDecoder.Decode(ReadAsset(entry.PathHash));
                var s = TryPairSkeleton(entry);
                var t = TryLoadTextures(entry, m);
                var v = TryLoadChampionVfx(entry);
                return (m, s, t, v);
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentMesh = mesh;
                CurrentSkeleton = skeleton;
                CurrentModelTextures = textures;
                SetChampionVfx(vfx);
                CurrentModelLightmapTextures = null; // champions/skinned meshes have no map baked lightmaps
                HasFlowmapWater = false;             // M44: only maps carry flowmap water
                if (textures is null) CurrentModelSubmeshMaterials = null; // flat mesh — no per-material data
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
        if (!ContentLoaded) return;
        try
        {
            _log.Info("MapGeo", $"Decoding {entry.DisplayName} …");
            var rawMapBytes = ReadAsset(entry.PathHash);
            var (map, mesh, textures) = await Task.Run(() =>
            {
                var m = MapGeoDecoder.Decode(rawMapBytes);
                var meshAsset = new MeshAsset
                {
                    Positions = m.Positions,
                    Normals = m.Normals,
                    Uvs = m.Uvs,
                    Colors = m.Colors,
                    LightmapUvs = m.LightmapUvs,
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
                if (_currentMap is { } replacedMap) UndoService.PurgeContext(replacedMap); // stale transform commands
                _currentMap = map;
                _currentMapBytes = rawMapBytes;
                _currentMapEntry = entry;
                _selection.Clear();
                CurrentModelTextures = textures;
                ClearSecondaryTextures(); // maps don't use champion secondary samplers
                PublishMapFlowWater();    // M44: re-apply flow-water textures wiped by ClearSecondaryTextures
                MapGeoInspector.Show(map, entry.Path);
                MapContent.ShowMap(entry.DisplayName, map.Groups
                    .Select((g, i) => new MapPieceViewModel { Name = string.IsNullOrEmpty(g.Material) ? $"Mesh {i}" : g.Material, Info = $"{g.IndexCount / 3:n0} tris" })
                    .ToList());
                BuildMapLayerGroups(map);
                BuildMapControllers(entry.Path);
                SelectedBaronIndex = 0;
                SelectedDragonIndex = 0; // handlers call ApplyMapVisibility; _currentMap is already set
                ApplyMapVisibility();    // ensure reset even if the index was already 0
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
        if (!ContentLoaded || !mapEntry.IsResolved) return null;

        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
        {
            _log.Info("MapGeo", $"No materials .bin found for {mapEntry.DisplayName} — rendering flat.");
            return null;
        }

        // M35: placed particle systems live in the same materials.bin (MapPlaceableContainer.items).
        // M36: the VfxSystemDefinitions they reference live in the same bin too — parse them for playback.
        try
        {
            var binBytes = GetAssetBytes(binEntry);
            var particles = MapParticleExtractor.Extract(binBytes, ResolveBinName);
            CurrentModelParticles = particles.Count > 0 ? particles : null;
            _vfxSystems = VfxSystemResolver.ExtractAll(binBytes);
            if (particles.Count > 0) _log.Info("MapGeo", $"{particles.Count:n0} placed particle system(s) ({particles.Select(p => p.SystemPath).Distinct().Count()} unique, {_vfxSystems.Count} definitions).");

            // M38: cubemap reflection probes + animated props (placed characters) from the same bin.
            var (probes, props) = MapPlaceableExtractor.Extract(binBytes);
            CurrentModelProbes = probes.Count > 0 ? probes : null;
            CurrentModelProps = props.Count > 0 ? props : null;
            if (probes.Count > 0 || props.Count > 0)
                _log.Info("MapGeo", $"{probes.Count} cubemap probe(s), {props.Count} animated prop(s) ({props.Select(p => p.CharacterName).Distinct().Count()} characters).");
        }
        catch { CurrentModelParticles = null; _vfxSystems = EmptyVfx; CurrentModelProbes = null; CurrentModelProps = null; }

        var names = map.Groups.Select(g => g.Material).Where(m => m.Length > 0).Distinct().ToList();
        var (materialToTexture, profiles) = ResolveMapMaterials(binEntry, names);
        if (materialToTexture.Count == 0)
        {
            _log.Info("MapGeo", "Materials .bin didn't resolve any textures — rendering flat.");
            return null;
        }
        return BuildMapTextures(map, materialToTexture, profiles, names.Count);
    }

    /// <summary>Resolve map material→texture (+ M32 profiles), falling back to the original game
    /// .materials.bin when the project's copy is broken (malformed .bin) or resolves nothing.</summary>
    private (Dictionary<string, string> textures, Dictionary<string, MaterialProfile> profiles) ResolveMapMaterials(WadAssetEntry binEntry, List<string> names)
    {
        try
        {
            var bytes = GetAssetBytes(binEntry);
            var r = MapGeoMaterialResolver.Resolve(bytes, names);
            if (r.Count > 0)
            {
                ApplySunProperties(bytes);   // M45: MapContainer -> MapSunProperties (lightMapColorScale etc.)
                return (r, MaterialProfiles.ForMapMaterials(bytes, names, ResolveBinName));
            }
        }
        catch (Exception ex) { _log.Warn("MapGeo", $"project materials.bin parse failed: {ex.Message}"); }

        var fb = _mounts?.ReadFallback(binEntry.PathHash);
        if (fb is not null)
        {
            try
            {
                var r = MapGeoMaterialResolver.Resolve(fb, names);
                if (r.Count > 0)
                {
                    _log.Info("MapGeo", "Used the original game materials.bin (the project's copy was broken/empty).");
                    ApplySunProperties(fb);
                    return (r, MaterialProfiles.ForMapMaterials(fb, names, ResolveBinName));
                }
            }
            catch (Exception ex) { _log.Warn("MapGeo", $"game materials.bin parse failed: {ex.Message}"); }
        }
        return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, MaterialProfile>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>M45: read the MapContainer's MapSunProperties component and publish what the renderer uses
    /// (lightMapColorScale — the game's baked-light multiplier, e.g. 2.0 on Map12 Bloom).</summary>
    private void ApplySunProperties(byte[] materialsBin)
    {
        var sun = Formats.MapGeo.MapSunProperties.Extract(materialsBin);
        _currentSunProps = sun;
        CurrentLightmapScale = sun?.LightMapColorScale ?? 1.0;
        if (sun is not null)
            _log.Info("Map", $"MapSunProperties: lightMapColorScale={sun.LightMapColorScale:0.##}, " +
                             $"skyLightScale={sun.SkyLightScale:0.##}, sunColor=({sun.SunColor.X:0.##}, {sun.SunColor.Y:0.##}, {sun.SunColor.Z:0.##}), " +
                             $"fog {sun.FogStartAndEnd.X:0}..{sun.FogStartAndEnd.Y:0}");
    }

    /// <summary>
    /// Resolve a mapgeo's companion .materials.bin, tolerating renamed copies (a mod folder often holds
    /// "base_srx - Kopie.mapgeo" whose materials are still the original "base_srx.materials.bin").
    /// </summary>
    private bool TryResolveMaterialsBin(string mapgeoPath, out WadAssetEntry binEntry)
    {
        const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

        var direct = MapGeoMaterialResolver.MaterialsBinPathFor(mapgeoPath);
        if (TryResolveEntry(HashAlgorithms.WadPath(direct), out binEntry)) return true;

        int slash = direct.LastIndexOf('/');
        string dir = slash < 0 ? "" : direct[..(slash + 1)];
        string file = direct[dir.Length..];
        string stem = file.EndsWith(".materials.bin", OIC) ? file[..^".materials.bin".Length] : file;

        // Strip "copy" suffixes (Windows/Explorer in several languages) and retry — the stripped name
        // usually exists in the game fallback.
        string cleaned = StripCopySuffix(stem);
        if (!cleaned.Equals(stem, OIC) &&
            TryResolveEntry(HashAlgorithms.WadPath(dir + cleaned + ".materials.bin"), out binEntry)) return true;

        // Last resort: any sibling .materials.bin in the same folder of the loaded project.
        foreach (var e in AssetEntries)
            if (e.IsResolved && e.Path.EndsWith(".materials.bin", OIC))
            {
                int s = e.Path.LastIndexOf('/');
                var d = s < 0 ? "" : e.Path[..(s + 1)];
                if (d.Equals(dir, OIC)) { binEntry = e; return true; }
            }

        binEntry = null!;
        return false;
    }

    private static string StripCopySuffix(string name)
    {
        string[] suffixes = { " - Kopie", " - Copy", " - copia", " - copie", " - Copie", " copy", "_copy", " (1)", " (2)", " (3)" };
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var sfx in suffixes)
                if (name.EndsWith(sfx, StringComparison.OrdinalIgnoreCase)) { name = name[..^sfx.Length]; changed = true; }
        }
        return name;
    }

    /// <summary>Per-group diffuse textures from resolved map material→texture map (override-aware loads).
    /// Also publishes the per-group preview materials (UV transform + specular flag) from the profiles (M32).</summary>
    private IReadOnlyList<TextureImage?> BuildMapTextures(MapGeoAsset map, Dictionary<string, string> materialToTexture,
        Dictionary<string, MaterialProfile> profilesByName, int materialCount)
    {
        var cache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        TextureImage? Load(string path)
        {
            if (cache.TryGetValue(path, out var hit)) return hit;
            return cache[path] = LoadTextureByPath(path);
        }

        _currentMapProfiles = profilesByName; // M34: cache for the mesh inspector's render-state rows

        var result = new TextureImage?[map.Groups.Count];
        var lightmaps = new TextureImage?[map.Groups.Count];
        var flowMaps = new TextureImage?[map.Groups.Count];    // M44: Flow_Map -> mask slot (1)
        var flowNormals = new TextureImage?[map.Groups.Count]; // M44: Flowing_Normal_Map -> gradient slot (2)
        var submeshMats = new ViewportMeshRenderer.SubmeshMaterial[map.Groups.Count];
        // Per-mesh mirrored (negative-determinant) flag, for the two-sided/mirrored render state (M34).
        var mirroredByMesh = map.Meshes.ToDictionary(m => m.Index, m => m.IsMirrored);
        int lmGroups = 0, flowGroups = 0;
        for (int i = 0; i < map.Groups.Count; i++)
        {
            var matName = map.Groups[i].Material;
            if (materialToTexture.TryGetValue(matName, out var path))
                result[i] = Load(path);
            if (profilesByName.TryGetValue(matName, out var prof))
            {
                submeshMats[i] = ToSubmeshMaterial(prof);
                LogUvTransform(prof, matName);

                // M44 flowmap river water: load the Flow_Map + Flowing_Normal textures into the mask/gradient
                // slots the water shader samples (slots 1/2). Falls back to a flat animated look if missing.
                if (prof.IsFlowmap)
                {
                    if (!string.IsNullOrEmpty(prof.FlowMapPath)) flowMaps[i] = Load(prof.FlowMapPath);
                    if (!string.IsNullOrEmpty(prof.FlowNormalPath)) flowNormals[i] = Load(prof.FlowNormalPath);
                    flowGroups++;
                    if (flowGroups <= 3)   // M44 diagnostic: confirm detection + texture loads for the first few
                    {
                        // Channel histogram of the flow map (B = water mask, R = phase, G = flow) so the
                        // shader's channel mapping can be sanity-checked against the real texture values.
                        string gstat = "";
                        if (flowMaps[i] is { } fmImg && fmImg.Rgba.Length >= 4)
                        {
                            long cnt = 0, bHi = 0; double rSum = 0, gSum = 0, bSum = 0;
                            var px = fmImg.Rgba;
                            for (int o = 0; o + 2 < px.Length; o += 64)   // every 16th pixel
                            {
                                rSum += px[o]; gSum += px[o + 1]; bSum += px[o + 2]; cnt++;
                                if (px[o + 2] > 128) bHi++;
                            }
                            if (cnt > 0) gstat = $" R={rSum / cnt / 255.0:0.00} G={gSum / cnt / 255.0:0.00} " +
                                                 $"B={bSum / cnt / 255.0:0.00} (water {bHi * 100 / cnt}%)";
                        }
                        _log.Info("Water", $"flowmap '{matName}': flowMap={(flowMaps[i] is not null ? "OK" : "miss")} " +
                                           $"normal={(flowNormals[i] is not null ? "OK" : "miss")} " +
                                           $"speed={prof.FlowSpeed:0.###} alpha={prof.WaterAlpha:0.##}{gstat}");
                    }
                }
            }
            else submeshMats[i] = ViewportMeshRenderer.SubmeshMaterial.Default;

            if (mirroredByMesh.TryGetValue(map.Groups[i].MeshIndex, out var mir) && mir)
                submeshMats[i] = submeshMats[i] with { Mirrored = true };

            // Baked lightmap: the group's BakedLight atlas (mesh already carries the uv7*scale+bias UVs).
            var lmPath = map.Groups[i].LightmapTexture;
            if (!string.IsNullOrEmpty(lmPath)) { lightmaps[i] = Load(lmPath); if (lightmaps[i] is not null) lmGroups++; }
        }
        CurrentModelSubmeshMaterials = submeshMats;
        CurrentModelLightmapTextures = lmGroups > 0 ? lightmaps : null;
        // M44: stash the flow textures (mask/gradient channels) and publish them. A later ClearSecondaryTextures()
        // on the load path wipes the channels, so the load code re-calls PublishMapFlowWater() from these fields.
        _mapFlowMasks = flowGroups > 0 ? flowMaps : null;
        _mapFlowGrads = flowGroups > 0 ? flowNormals : null;
        PublishMapFlowWater();

        int unique = cache.Values.Count(v => v is not null);
        int spec = submeshMats.Count(m => m.UsesSpecular);
        _log.Success("MapGeo", $"Loaded {unique} unique textures ({materialToTexture.Count}/{materialCount} materials resolved)" +
                               (spec > 0 ? $", {spec} group(s) with specular." : ".") +
                               (lmGroups > 0 ? $" {lmGroups} group(s) with baked lightmaps." : "") +
                               (flowGroups > 0 ? $" {flowGroups} flowmap-water group(s)." : ""));
        return result;
    }

    /// <summary>Find the skin .bin for a .skn, resolve per-submesh diffuse textures, decode them.</summary>
    private IReadOnlyList<TextureImage?>? TryLoadTextures(WadAssetEntry skn, MeshAsset mesh)
    {
        if (!ContentLoaded || !skn.IsResolved) return null;

        var binPath = SkinPaths.BinPathForSkn(skn.Path);
        if (binPath is null || !TryResolveEntry(HashAlgorithms.WadPath(binPath), out var binEntry))
        {
            _log.Info("Material", $"No skin .bin found for {skn.DisplayName} (flat shading).");
            return null;
        }
        var resolved = ChampionMaterialResolver.Resolve(GetAssetBytes(binEntry), ResolveBinName);
        if (!resolved.HasAny)
        {
            _log.Info("Material", $"No skin material found for {skn.DisplayName} (flat shading).");
            return null;
        }
        return BuildSubmeshTextures(mesh, resolved, skn.DisplayName);
    }

    /// <summary>Parse the champion skin's VFX library from its .bin (M37). Empty when there's no skin bin.</summary>
    private IReadOnlyDictionary<uint, VfxSystemDefinition> TryLoadChampionVfx(WadAssetEntry skn)
    {
        if (!ContentLoaded || !skn.IsResolved) return EmptyVfx;
        var binPath = SkinPaths.BinPathForSkn(skn.Path);
        if (binPath is null || !TryResolveEntry(HashAlgorithms.WadPath(binPath), out var binEntry)) return EmptyVfx;
        try { return VfxSystemResolver.ExtractAll(GetAssetBytes(binEntry)); }
        catch { return EmptyVfx; }
    }

    /// <summary>Map a Formats <see cref="MaterialProfile"/> to the renderer's per-submesh material (M32).</summary>
    private static ViewportMeshRenderer.SubmeshMaterial ToSubmeshMaterial(MaterialProfile p) =>
        new(p.UsesRim, p.UsesSpecular, p.UvScale, p.UvOffset, p.UvRotationDegrees,
            AlphaMode: p.RenderMode switch
            {
                MaterialRenderMode.Cutout => 1,
                MaterialRenderMode.Transparent => 2,
                _ => 0,
            },
            DoubleSided: p.DoubleSided,
            Tint: p.Tint,
            AlphaCutoff: p.AlphaCutoff ?? 0.35f,
            ClampU: p.ClampU,
            ClampV: p.ClampV,
            IsFlowmap: p.IsFlowmap,
            FlowSpeed: p.FlowSpeed,
            FlowStrength: p.FlowStrength,
            FlowTile: p.FlowTile,
            ColorInside: p.ColorInside,
            ColorOutside: p.ColorOutside,
            WaterAlpha: p.WaterAlpha);

    private readonly HashSet<string> _loggedUvTransforms = new(StringComparer.Ordinal);

    /// <summary>Log the UV transform applied to a material once (spec: "log which UV transform was applied").</summary>
    private void LogUvTransform(MaterialProfile p, string label)
    {
        if (!p.HasUvTransform) return;
        var key = $"{label}|{p.UvScale}|{p.UvOffset}|{p.UvRotationDegrees}";
        if (!_loggedUvTransforms.Add(key)) return;
        _log.Info("Material", $"UV transform on '{label}': scale ({p.UvScale.X:0.###}, {p.UvScale.Y:0.###})" +
                              $" offset ({p.UvOffset.X:0.###}, {p.UvOffset.Y:0.###})" +
                              (p.UvRotationDegrees != 0 ? $" rot {p.UvRotationDegrees:0.#}°" : "") +
                              (p.UvScaleSource is not null ? $"  [from {p.UvScaleSource}]" : "") +
                              (p.UvOffsetSource is not null ? $"  [offset from {p.UvOffsetSource}]" : ""));
    }

    /// <summary>Per-submesh diffuse textures from the resolved champion material (override-aware loads).</summary>
    private IReadOnlyList<TextureImage?> BuildSubmeshTextures(MeshAsset mesh, ChampionMaterialResolver.Result material, string label)
    {
        var cache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        TextureImage? Load(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (cache.TryGetValue(path, out var hit)) return hit;
            return cache[path] = LoadTextureByPath(path);
        }

        int n = mesh.SubMeshes.Count;
        var result = new TextureImage?[n];
        var masks = new TextureImage?[n];
        var grads = new TextureImage?[n];
        var emis = new TextureImage?[n];
        var matcaps = new TextureImage?[n];
        var matcapMasks = new TextureImage?[n];
        var submeshMats = new ViewportMeshRenderer.SubmeshMaterial[n];
        int loaded = 0, secondary = 0;
        for (int i = 0; i < n; i++)
        {
            var sub = mesh.SubMeshes[i].Material;
            var img = Load(material.For(sub));
            result[i] = img;
            if (img is not null) loaded++;
            masks[i] = Load(material.ForMask(sub));
            grads[i] = Load(material.ForGradient(sub));
            emis[i] = Load(material.ForEmissive(sub));
            matcaps[i] = Load(material.ForMatCap(sub));
            matcapMasks[i] = Load(material.ForMatCapMask(sub));
            if (masks[i] is not null || grads[i] is not null || emis[i] is not null || matcaps[i] is not null) secondary++;
            submeshMats[i] = ToSubmeshMaterial(material.Profile(sub));
            LogUvTransform(material.Profile(sub), sub);
        }
        CurrentModelSubmeshMaterials = submeshMats;
        HasFlowmapWater = false; // M44: champion skins never carry flowmap water
        // Publish the secondary layers (mask/gradient/emissive/matcap) for the RiotApprox preview.
        CurrentModelMaskTextures = material.SubmeshMask.Count > 0 || material.DefaultMask is not null ? masks : null;
        CurrentModelGradientTextures = material.SubmeshGradient.Count > 0 || material.DefaultGradient is not null ? grads : null;
        CurrentModelEmissiveTextures = material.SubmeshEmissive.Count > 0 || material.DefaultEmissive is not null ? emis : null;
        CurrentModelMatCapTextures = material.SubmeshMatCap.Count > 0 || material.DefaultMatCap is not null ? matcaps : null;
        CurrentModelMatCapMaskTextures = material.SubmeshMatCapMask.Count > 0 || material.DefaultMatCapMask is not null ? matcapMasks : null;

        int distinct = cache.Values.Count(v => v is not null);
        var extra = material.HasSecondary ? $", {secondary} with secondary samplers (mask/gradient/emissive)" : "";
        _log.Success("Material", $"Applied {loaded}/{n} submesh textures ({distinct} distinct{extra}) for {label}.");
        return result;
    }

    /// <summary>Find the matching .skl for a resolved .skn inside the same WAD.</summary>
    private SkeletonAsset? TryPairSkeleton(WadAssetEntry skn)
    {
        if (!ContentLoaded || !skn.IsResolved || !skn.Path.EndsWith(".skn", StringComparison.OrdinalIgnoreCase))
            return null;
        var sklPath = skn.Path[..^4] + ".skl";
        var hash = HashAlgorithms.WadPath(sklPath);
        if (!TryResolveEntry(hash, out var sklEntry)) return null;
        try { return SkeletonDecoder.Decode(ReadAsset(sklEntry.PathHash)); }
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
            LoadRecentProjects(RecentProjects.Add(Path.GetDirectoryName(path) ?? path));
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
        UndoService.MarkSaved();
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
        if (!GuardEditable(entry)) return;
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
        _log.Warn("Project", "Adding brand-new chunks isn't supported: WAD v3.4 stores a separate subchunk table that can't be safely relocated without risking corruption. Use Replace on an existing asset, or repoint a material to an existing texture path.");

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

    // ---- Project folder mode (M11) --------------------------------------

    [RelayCommand]
    private async Task OpenProjectFolder()
    {
        var folder = await Dialogs.OpenFolderAsync("Open project folder");
        if (folder is not null) OpenProjectAt(folder);
    }

    [RelayCommand]
    private void OpenRecentProject(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        if (!Directory.Exists(folder)) { _log.Warn("Project", $"Folder no longer exists: {folder}"); return; }
        OpenProjectAt(folder);
    }

    private void OpenProjectAt(string folder)
    {
        try
        {
            var project = ReyProjectService.OpenFolder(folder);
            Project = project;
            _overrides.LoadFrom(project);
            _archive?.Dispose(); _archive = null;
            BuildMounts();
            BuildProjectTree();
            ClearViewport(); Inspector.Clear(); BinEditor.Clear(); MaterialEditor.Clear();
            UndoService.Clear(); // new project = fresh history
            ProjectMode = true; InspectionMode = false;
            HasMaterialData = false; HasInspectorBody = false;
            LoadCachedShaderDb();
            LoadRecentProjects(RecentProjects.Add(folder));
            UpdateTitle();
            Status = $"Project '{project.Name}' — {_mounts!.Count:n0} assets across {_mounts.Mounts.Count} mount(s)";
            _log.Success("Project", $"Opened '{project.Name}': {project.ProjectFolders.Count} folder(s), {project.ProjectWads.Count} WAD(s), {project.ReferenceWads.Count} Riot reference(s); {_mounts.Count:n0} assets mounted.");
            if (project.ReferenceWads.Count == 0)
                _log.Info("Project", "No Riot references yet — add one via Project ▸ Manage Riot References to preview/copy source assets.");
        }
        catch (Exception ex) { _log.Error("Project", ex.Message); }
    }

    private void LoadRecentProjects(IEnumerable<string> folders)
    {
        RecentProjectList.Clear();
        foreach (var f in folders)
            RecentProjectList.Add(new RecentProjectViewModel(f, OpenRecentProject));
        OnPropertyChanged(nameof(HasRecentProjects));
    }

    private void BuildMounts()
    {
        _mounts?.Dispose();
        _mounts = new AssetMountService();
        if (Project.OverridesDirectory is { } ov) _mounts.Add(new OverrideMount(ov, _resolver));
        foreach (var f in Project.ProjectFolders)
            try { _mounts.Add(new FolderMount(Project.ResolveProjectPath(f), _resolver, f == "." ? Project.Name : f)); }
            catch (Exception ex) { _log.Warn("Project", $"folder {f}: {ex.Message}"); }
        foreach (var w in Project.ProjectWads)
            try { _mounts.Add(new WadMount(WadArchive.Open(Project.ResolveProjectPath(w), _resolver), AssetSourceKind.ProjectWad, editable: true)); }
            catch (Exception ex) { _log.Warn("Project", $"WAD {w}: {ex.Message}"); }
        foreach (var r in Project.ReferenceWads)
            try { _mounts.Add(new WadMount(WadArchive.Open(r, _resolver), AssetSourceKind.RiotReference, editable: false, name: Path.GetFileName(r))); }
            catch (Exception ex) { _log.Warn("Project", $"reference {Path.GetFileName(r)}: {ex.Message}"); }

        AddGameFallback();
        _mounts.Rebuild();
    }

    /// <summary>Mount the original Riot game WADs as read-only fallback so missing assets resolve from the install.</summary>
    private void AddGameFallback()
    {
        if (_mounts is null) return;
        var mapNames = Project.ProjectFolders.Concat(Project.ProjectWads)
            .Select(p => p == "." ? Project.Name : Path.GetFileNameWithoutExtension(p).Replace(".wad", "", StringComparison.OrdinalIgnoreCase))
            .Append(Project.Name);

        int n = 0;
        foreach (var wad in GameReferenceLibrary.Discover(Project.GameDirectory, mapNames))
        {
            if (Project.ReferenceWads.Contains(wad, StringComparer.OrdinalIgnoreCase)) continue; // already an explicit reference
            try { _mounts.AddFallback(new WadMount(WadArchive.Open(wad), AssetSourceKind.RiotReference, editable: false, name: Path.GetFileName(wad))); n++; }
            catch (Exception ex) { _log.Warn("Project", $"game fallback {Path.GetFileName(wad)}: {ex.Message}"); }
        }
        if (n > 0)
            _log.Info("Project", $"Mounted {n} game WAD(s) as read-only fallback — missing skin bins/textures resolve from the original game files. (Set the game folder via Tools if assets are still missing.)");
        else if (string.IsNullOrEmpty(Project.GameDirectory))
            _log.Warn("Project", "No game folder configured — missing base assets (textures/skin bins not in the mod) won't resolve. Set it so ReyEngine can fall back to the original WADs.");
    }

    private void BuildProjectTree()
    {
        if (_mounts is null) return;
        RootNodes.Clear();
        _nodesByHash.Clear();
        _thumbnails.Clear();

        var projectGroup = new AssetTreeNode { Name = "Project", IsFolder = true };
        foreach (var mount in _mounts.Mounts.Where(m => m.Kind != AssetSourceKind.RiotReference))
        {
            var entries = mount.Enumerate().Select(a => a.ToEntry()).ToList();
            if (entries.Count == 0) continue;
            projectGroup.Children.Add(AssetTree.Build(entries, mount.Name));
        }

        var riotGroup = new AssetTreeNode { Name = "Riot References", IsFolder = true };
        foreach (var mount in _mounts.Mounts.Where(m => m.Kind == AssetSourceKind.RiotReference))
            riotGroup.Children.Add(AssetTree.Build(mount.Enumerate().Select(a => a.ToEntry()).ToList(), mount.Name));

        var projectVm = new AssetNodeViewModel(projectGroup);
        var riotVm = new AssetNodeViewModel(riotGroup);

        // M33: graft the project's materials in as virtual "ASSETS/<material path>" tree nodes so every
        // StaticMaterialDef in a .materials.bin / skin .bin is browsable (and openable) as a first-class
        // asset. Project mounts only — reference WADs hold far too many materials to extract eagerly.
        foreach (var mount in _mounts.Mounts.Where(m => m.Kind != AssetSourceKind.RiotReference))
        {
            var mountVm = projectVm.Children.FirstOrDefault(c => c.Name == mount.Name);
            if (mountVm is null) continue;
            InjectMaterialAssets(mountVm, mount.Enumerate().Select(a => a.ToEntry()).ToList(), readOnly: false);
        }

        RootNodes.Add(projectVm);
        if (riotGroup.Children.Count > 0) RootNodes.Add(riotVm);

        // Index Riot first, then Project, so a conflicted asset's *project* node wins status updates.
        IndexNodes(riotVm);
        IndexNodes(projectVm);
        RefreshAllStatuses();
        RefreshContentPanels();
    }

    /// <summary>Graft each material-library bin's materials into the tree as virtual "ASSETS/&lt;name&gt;" nodes (M33).</summary>
    private void InjectMaterialAssets(AssetNodeViewModel mountVm, IReadOnlyList<WadAssetEntry> entries, bool readOnly)
    {
        AssetNodeViewModel? assetsRoot = null;
        int count = 0;
        foreach (var e in entries.Where(x => x.IsResolved && MaterialLibraryExtractor.IsMaterialLibrary(x.Path)))
        {
            IReadOnlyList<Formats.Materials.MaterialSummary> mats;
            try { mats = MaterialLibraryExtractor.Extract(GetAssetBytes(e), ResolveBinName); }
            catch { continue; }
            if (mats.Count == 0) continue;

            assetsRoot ??= GetOrAddChildFolder(mountVm, "ASSETS");
            foreach (var m in mats)
            {
                var matVm = new MaterialAssetViewModel(m, e, readOnly);
                var parts = m.Name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var folder = assetsRoot;
                for (int i = 0; i < parts.Length - 1; i++) folder = GetOrAddChildFolder(folder, parts[i]);
                folder.AddChild(AssetNodeViewModel.MaterialLeaf(matVm));
                count++;
            }
        }
        if (count > 0) _log.Info("Materials", $"{mountVm.Name}: exposed {count} material(s) as virtual assets under ASSETS/.");
    }

    private static AssetNodeViewModel GetOrAddChildFolder(AssetNodeViewModel parent, string name)
    {
        var existing = parent.Children.FirstOrDefault(c => c.IsFolder && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var f = AssetNodeViewModel.VirtualFolder(name);
        parent.AddChild(f);
        return f;
    }

    /// <summary>Re-enumerate the override mount after a save/copy so reads + conflicts reflect new files.</summary>
    private void RefreshOverrideMount()
    {
        if (_mounts is null) return;
        BuildMounts();
    }

    [RelayCommand]
    private async Task SetGameFolder()
    {
        var folder = await Dialogs.OpenFolderAsync("Select the League of Legends 'Game' folder (for reference fallback)");
        if (folder is null) return;
        Project.GameDirectory = folder;
        var probe = GameReferenceLibrary.Discover(folder, Project.ProjectFolders.Append(Project.Name));
        if (probe.Count == 0) { _log.Warn("Project", $"No game WADs found under {folder}. Pick the 'Game' folder (it should contain DATA/FINAL)."); return; }
        if (ProjectMode)
        {
            ReyProjectService.Save(Project, Project.ProjectFilePath!);
            BuildMounts(); BuildProjectTree();
        }
        _log.Success("Project", $"Game folder set: {folder} — {probe.Count} reference WAD(s) available as fallback. Reload the asset to apply.");
    }

    // ---- Riot shader database (M18) -------------------------------------

    private string? ShaderCachePath =>
        Project.WorkspaceDirectory is { } w ? Path.Combine(w, "shader_cache.json") : null;

    private void LoadCachedShaderDb()
    {
        _shaderDb = ShaderCachePath is { } p ? ShaderCacheService.Load(p) : null;
        ShaderDbStatus = _shaderDb is { } d
            ? $"Riot shaders: {d.Shaders.Count:n0} ({d.VertexCount} VS · {d.PixelCount} PS), cached."
            : "Riot shaders not scanned — Tools ▸ Scan Riot Shaders.";
    }

    [RelayCommand]
    private async Task ScanRiotShaders()
    {
        var path = GameReferenceLibrary.FindShaderCache(Project.GameDirectory);
        if (path is null)
        {
            _log.Warn("Shader", "ShaderCache.dx11.wad.client not found — set the game folder in Project Settings first.");
            return;
        }
        _log.Info("Shader", $"Scanning {Path.GetFileName(path)} …");
        Status = "Scanning Riot shaders…";
        try
        {
            var db = await Task.Run(() =>
            {
                using var wad = WadArchive.Open(path, _resolver);
                return ShaderScanner.Scan(wad);
            });
            _shaderDb = db;
            if (ShaderCachePath is { } cp) { ShaderCacheService.Save(db, cp); }
            ShaderDbStatus = $"Riot shaders: {db.Shaders.Count:n0} ({db.VertexCount} VS · {db.PixelCount} PS), cached.";
            _log.Success("Shader", $"Scanned {db.Shaders.Count:n0} shaders ({db.VertexCount} vertex, {db.PixelCount} pixel). Cached to {(ShaderCachePath is null ? "(memory)" : ".reyengine/shader_cache.json")}.");
            Status = ShaderDbStatus;
        }
        catch (Exception ex) { _log.Error("Shader", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportShaderDump()
    {
        var entry = SelectedNode?.Entry;
        if (entry is null || _archive is null && _mounts is null) { _log.Warn("Shader", "Select a shader (.dx11) asset first."); return; }
        if (!entry.Path.Contains(".dx11", StringComparison.OrdinalIgnoreCase)) { _log.Warn("Shader", "Selected asset isn't a shader (.dx11)."); return; }
        var outPath = await Dialogs.SaveFileAsync("Export shader bytecode", entry.DisplayName);
        if (outPath is null) return;
        try { await File.WriteAllBytesAsync(outPath, ReadAsset(entry.PathHash)); _log.Success("Shader", $"Wrote {outPath}."); }
        catch (Exception ex) { _log.Error("Shader", ex.Message); }
    }

    [RelayCommand]
    private async Task SetOutputFolder()
    {
        var folder = await Dialogs.OpenFolderAsync("Select the build output folder");
        if (folder is null) return;
        if (BuildSafety.IsInsideGameInstall(folder)) { _log.Error("Project", "Refusing to set the output inside a Riot/League install folder."); return; }
        Project.OutputDirectory = folder;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        _log.Success("Project", $"Build output folder set: {folder}");
    }

    // ---- Project Settings dialog + .fantome export (M17) ----------------

    public event Action? RequestProjectSettings;

    [RelayCommand]
    private void OpenProjectSettings()
    {
        if (!ProjectMode) { _log.Warn("Project", "Open a project folder first."); return; }
        RequestProjectSettings?.Invoke();
    }

    // ---- Editor preferences (M40): keybinds + camera feel, persisted to %AppData%/ReyEngine ----
    public ReyEngine.Core.Settings.EditorSettings Settings { get; } = ReyEngine.Core.Settings.EditorSettings.Load();
    public event Action? RequestSettings;

    [RelayCommand]
    private void OpenSettings() => RequestSettings?.Invoke();

    /// <summary>Called by the view after the Preferences dialog is saved: persist + let the view re-apply.</summary>
    public void ApplyEditorSettings(SettingsViewModel vm)
    {
        Settings.CopyFrom(vm.ToSettings());
        Settings.Save();
        CullBackfaces = Settings.CullBackfacesDefault;
        _log.Success("Settings", "Preferences saved.");
    }

    /// <summary>Called by the view after the settings dialog is saved.</summary>
    public void ApplyProjectSettings(ProjectSettingsViewModel vm)
    {
        vm.ApplyTo(Project);
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        if (ProjectMode) { BuildMounts(); BuildProjectTree(); }
        _log.Success("Project", "Project settings saved.");
    }

    [RelayCommand]
    private async Task ExportFantome()
    {
        if (!ProjectMode || Project.RootPath is null) { _log.Warn("Export", "Open a project folder first."); return; }
        if (string.IsNullOrWhiteSpace(Project.ModAuthor))
            _log.Info("Export", "Tip: set the author / version / thumbnail in Project ▸ Project Settings for a complete package.");

        string name = Project.EffectiveModName;
        string author = string.IsNullOrWhiteSpace(Project.ModAuthor) ? "Unknown" : Project.ModAuthor!;
        var suggested = SanitizeFileName($"{name} by {author}.fantome");
        var outPath = await Dialogs.SaveFileAsync("Export .fantome", suggested);
        if (outPath is null) return;
        if (!outPath.EndsWith(".fantome", StringComparison.OrdinalIgnoreCase)) outPath += ".fantome";

        var thumb = LoadThumbnailPng(Project.ThumbnailPath);
        var meta = new FantomeMeta
        {
            Name = name,
            Author = author,
            Version = string.IsNullOrWhiteSpace(Project.ModVersion) ? "1.0.0" : Project.ModVersion,
            Description = Project.ModDescription ?? "",
            Heart = Project.ModHeart,
            Home = Project.ModHome,
        };

        IsBuilding = true; Status = "Exporting .fantome…";
        try
        {
            await Task.Run(() =>
            {
                var buildRoot = Project.OutputDirectory ?? Path.Combine(Project.RootPath, "Build");
                if (BuildSafety.IsInsideGameInstall(buildRoot))
                    throw new InvalidOperationException("Build output is inside the game install — change it in Project Settings.");
                Directory.CreateDirectory(buildRoot);
                BuildProjectCore(buildRoot);
                var wads = Directory.GetFiles(buildRoot, "*.wad.client").ToList();
                if (wads.Count == 0) throw new InvalidOperationException("No WAD was produced — the project has no packable content.");
                FantomeExporter.Export(meta, wads, thumb, outPath);
            });
            _log.Success("Export", $"Wrote {outPath} ({new FileInfo(outPath).Length / 1048576.0:0.0} MB) — {meta.Name} v{meta.Version} by {meta.Author}.");
            Status = $"Exported {Path.GetFileName(outPath)}";
        }
        catch (Exception ex) { _log.Error("Export", ex.Message); }
        finally { IsBuilding = false; }
    }

    private byte[]? LoadThumbnailPng(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load(path);
            using var ms = new MemoryStream();
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, ms);
            return ms.ToArray();
        }
        catch (Exception ex) { _log.Warn("Export", $"thumbnail: {ex.Message}"); return null; }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        return name.Trim();
    }

    [RelayCommand]
    private async Task ManageRiotReferences()
    {
        if (!ProjectMode) { _log.Warn("Project", "Open a project folder first."); return; }
        var path = await Dialogs.OpenFileAsync("Add Riot reference WAD", DialogService.Wad, DialogService.All);
        if (path is null) return;
        if (Project.ReferenceWads.Contains(path, StringComparer.OrdinalIgnoreCase)) { _log.Info("Project", "Reference already added."); return; }
        Project.ReferenceWads.Add(path);
        Project.IsDirty = true;
        ReyProjectService.Save(Project, Project.ProjectFilePath!);
        BuildMounts();
        BuildProjectTree();
        _log.Success("Project", $"Added Riot reference {Path.GetFileName(path)} — {_mounts!.Count:n0} assets now mounted.");
    }

    [RelayCommand]
    private void CopyAssetToProject()
    {
        var entry = SelectedNode?.Entry;
        if (entry is null) { _log.Warn("Project", "Select an asset to copy."); return; }
        if (!ProjectMode || _mounts is null) { _log.Warn("Project", "Copy to Project needs an open project."); return; }
        if (entry.SourceKind != AssetSourceKind.RiotReference)
        { _log.Info("Project", "Asset is already editable in the project."); return; }

        try
        {
            var bytes = ReadAsset(entry.PathHash);
            var ext = Path.GetExtension(entry.IsResolved ? entry.Path : ".bin");
            var dest = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, string.IsNullOrEmpty(ext) ? ".bin" : ext);
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = entry.PathHash,
                ResolvedPath = entry.IsResolved ? entry.Path : null,
                OverrideFile = dest,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            Project.IsDirty = true;
            RefreshOverrideMount();
            BuildProjectTree();
            if (_nodesByHash.TryGetValue(entry.PathHash, out var node)) SelectedNode = node;
            UpdateTitle();
            _log.Success("Project", $"Copied {entry.DisplayName} into the project ({bytes.Length:n0} bytes). It is now editable.");
        }
        catch (Exception ex) { _log.Error("Project", ex.Message); }
    }

    /// <summary>Block editing read-only Riot assets; suggest Copy to Project.</summary>
    private bool GuardEditable(WadAssetEntry? entry)
    {
        if (ProjectMode && entry is { SourceKind: AssetSourceKind.RiotReference })
        {
            _log.Warn("Project", $"'{entry.DisplayName}' is a read-only Riot asset. Right-click ▸ Copy Asset To Project to edit it.");
            return false;
        }
        return true;
    }

    [RelayCommand]
    private async Task BuildProject()
    {
        if (_mounts is null || Project.RootPath is null) { _log.Warn("Build", "Open a project folder first."); return; }
        var buildRoot = Project.OutputDirectory ?? Path.Combine(Project.RootPath, "Build");
        if (BuildSafety.IsInsideGameInstall(buildRoot))
        { _log.Error("Build", "Refusing to build into a Riot/League install folder. Change the output directory in Project Settings."); return; }

        _overrides.SaveTo(Project);
        ReyProjectService.Save(Project, Project.ProjectFilePath!);
        Directory.CreateDirectory(buildRoot);
        _log.Info("Build", $"Building project '{Project.Name}' → {buildRoot}");
        IsBuilding = true; Status = "Building project…";
        try
        {
            await Task.Run(() => BuildProjectCore(buildRoot));
            Status = $"Built project to {buildRoot}";
            _log.Success("Build", $"Project build ready: {buildRoot}. Open it via File ▸ Open Project Folder to verify.");
        }
        catch (Exception ex) { _log.Error("Build", ex.Message); }
        finally { IsBuilding = false; }
    }

    private void BuildProjectCore(string buildRoot)
    {
        var overridesByHash = _overrides.All.ToDictionary(o => o.PathHash, o => o.OverrideFile);
        int wads = 0, staged = 0, files = 0, skipped = 0;

        // Project WADs: safe in-place replace of existing chunks.
        foreach (var w in Project.ProjectWads)
        {
            var src = Project.ResolveProjectPath(w);
            if (!File.Exists(src)) { _log.Warn("Build", $"missing project WAD {w}"); continue; }
            using var arc = WadArchive.Open(src, _resolver);
            var apply = new Dictionary<ulong, byte[]>();
            foreach (var (hash, file) in overridesByHash)
                if (arc.TryGetEntry(hash, out _) && File.Exists(file)) apply[hash] = File.ReadAllBytes(file);
            var outWad = Path.Combine(buildRoot, Path.GetFileName(w));
            var report = new BuildReport { OutputPath = outWad };
            WadRepackService.Repack(src, apply, outWad, report);
            foreach (var i in report.Issues) _log.Warn("Build", i.Message);
            _log.Info("Build", $"WAD {Path.GetFileName(w)}: {apply.Count} replaced → {Path.GetFileName(outWad)}");
            wads++;
        }

        // Project folders: stage (copy tree + apply overrides as files — new files are safe in folder format).
        var stagedFolders = new List<(string name, string dir)>();
        foreach (var f in Project.ProjectFolders)
        {
            var srcFolder = Project.ResolveProjectPath(f);
            var name = f == "." ? Project.Name : f.Replace('/', '_');
            var outFolder = Path.Combine(buildRoot, "staged", name);
            files += CopyTree(srcFolder, outFolder);
            stagedFolders.Add((name, outFolder));
            staged++;
        }

        // Apply overrides into the first staged folder at their resolved path.
        if (stagedFolders.Count > 0)
        {
            var outFolder = stagedFolders[0].dir;
            foreach (var ov in _overrides.All)
            {
                if (!File.Exists(ov.OverrideFile)) { skipped++; continue; }
                string rel = ov.ResolvedPath ?? $"0x{ov.PathHash:x16}{Path.GetExtension(ov.OverrideFile)}";
                var dest = Path.Combine(outFolder, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(ov.OverrideFile, dest, overwrite: true);
                files++;
            }
        }

        // Pack each staged folder into a distributable .wad.client.
        int packed = 0;
        foreach (var (name, dir) in stagedFolders)
        {
            var outWad = Path.Combine(buildRoot, name + ".wad.client");
            WadPackReport pr;
            try { pr = WadPackService.Pack(dir, outWad); }
            catch (Exception ex) { _log.Error("Build", $"Pack failed for {name}: {ex.Message}"); continue; }
            foreach (var w in pr.Warnings) _log.Warn("Build", w);
            if (pr.Success)
            {
                packed++;
                _log.Success("Build", $"Packed {name}.wad.client — {pr.Chunks:n0} chunks, {pr.InputBytes / 1048576.0:0.0}→{pr.OutputBytes / 1048576.0:0.0} MB. {pr.Validation}");
            }
            else _log.Error("Build", $"Pack didn't validate for {name} — the staged folder is at {dir}.");
        }

        _log.Info("Build", $"project WADs: {wads} · folders packed: {packed}/{staged} · files: {files:n0} · skipped: {skipped}");
    }

    private static int CopyTree(string src, string dst)
    {
        int n = 0;
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            if (rel.StartsWith(".reyengine", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            n++;
        }
        return n;
    }

    [RelayCommand]
    private async Task BuildPackage()
    {
        if (ProjectMode) { await BuildProject(); return; }
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
        bool dirty = Project.IsDirty || UndoService.IsDirty;
        Title = $"ReyEngine — {name}{(dirty ? " *" : "")}" + (_archive is not null ? $" — {_archive.Name}" : "");
    }

    // ---- Misc commands --------------------------------------------------

    [RelayCommand] private void ShaderPreview() => _log.Warn("Shader", "Shader/material preview lands in a later milestone.");
    [RelayCommand] private void ClearConsole() => Console.Clear();

    [RelayCommand]
    private void Exit() =>
        (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
}
