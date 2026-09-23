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
using ReyEngine.Core.Painting;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Meta;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Selection;
using ReyEngine.Core.Undo;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Lighting;
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
    private readonly MimirSyncService _mimir = new();   // M495

    // M367: the LeagueToolkit meta-class schema. Lazy and thread-safe: it is a ~3.6 MB parse that most
    // sessions never need, and when it IS needed the first call arrives from a background bin parse.
    // Optional by design - nothing here fails when it was never synced, it just resolves fewer names.
    private readonly MetaClassSyncService _metaSync = new();
    private Lazy<MetaClassDatabase> _meta = null!;
    private MetaClassDatabase Meta => _meta.Value;
    private readonly WadPathResolver _resolver;
    private WadArchive? _archive;
    private AssetMountService? _mounts;          // project mode: the virtual file system
    private string? _lastGameFallbackNotice;
    private readonly AssetOverrideStore _overrides = new();
    private readonly ReyEngine.App.Services.ThumbnailService _thumbnails; // Content Browser lazy thumbnails
    private readonly Dictionary<ulong, AssetNodeViewModel> _nodesByHash = new();
    private WorkshopCatalogService? _workshopCatalog;
    private Task<WorkshopCatalog>? _commonMaterialCatalogTask;
    private string? _commonMaterialCatalogDirectory;

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

    // ---- M519: the bottom dock (Content Browser | Console) --------------------------------------
    //
    // Merging the two panes into one tabbed dock means the console can be behind a tab when something
    // goes wrong. The badge is what keeps that honest: it counts the warnings and errors that have
    // arrived since the tab was last looked at, and clears when it is opened.

    /// <summary>0 = Content Browser, 1 = Console.</summary>
    [ObservableProperty] private int _bottomDockTab;

    [ObservableProperty] private int _unseenConsoleProblems;
    public bool HasUnseenConsoleProblems => UnseenConsoleProblems > 0;

    partial void OnUnseenConsoleProblemsChanged(int value) => OnPropertyChanged(nameof(HasUnseenConsoleProblems));

    partial void OnBottomDockTabChanged(int value)
    {
        if (value == ConsoleTabIndex) UnseenConsoleProblems = 0;
    }

    private const int ConsoleTabIndex = 1;

    /// <summary>Called for every log line. Only counts while the console is NOT the visible tab —
    /// a problem you are already looking at is not unseen.</summary>
    private void OnConsoleEntry(ReyEngine.Core.Diagnostics.LogEntry entry)
    {
        if (BottomDockTab == ConsoleTabIndex) return;
        if (entry.Level is ReyEngine.Core.Diagnostics.LogLevel.Warning
            or ReyEngine.Core.Diagnostics.LogLevel.Error)
            UnseenConsoleProblems++;
    }

    /// <summary>Bring the console to the front — used by anything that wants the user to read it.</summary>
    [RelayCommand]
    private void ShowConsole() => BottomDockTab = ConsoleTabIndex;

    [RelayCommand]
    private void ShowContentBrowser() => BottomDockTab = 0;
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

    // M131: determinate build/export progress in the status bar
    [ObservableProperty] private double _buildProgress;          // 0..100
    [ObservableProperty] private string _buildStage = "";
    public bool BuildProgressActive => IsBuilding;
    partial void OnIsBuildingChanged(bool value)
    {
        if (!value) { BuildProgress = 0; BuildStage = ""; }
        OnPropertyChanged(nameof(BuildProgressActive));
    }

    /// <summary>UI-thread progress sink for build/export pipelines.</summary>
    private IProgress<(double Frac, string Stage)> BuildProgressSink() =>
        new Progress<(double Frac, string Stage)>(t =>
        {
            BuildProgress = Math.Clamp(t.Frac, 0, 1) * 100.0;
            BuildStage = t.Stage;
            Status = t.Stage;
        });

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
    [NotifyPropertyChangedFor(nameof(ActiveOverlayCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveOverlays))]
    [NotifyPropertyChangedFor(nameof(OverlayBadge))]
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

    // ---- M205: the re-link picker -------------------------------------------------------------------
    // MapPlaceableWriter has accepted a SystemLink since M199 and M204 wired it to the view model, but
    // there was no way to choose a system. A map bin defines a few hundred of them, so this is a filtered
    // list rather than a bare dropdown - the same shape the Particle Editor's SYSTEMS panel uses.

    /// <summary>Every VFX system defined in the loaded map, sorted, for the re-link picker.</summary>
    private readonly List<VfxSystemItemViewModel> _relinkAll = new();
    public ObservableCollection<VfxSystemItemViewModel> RelinkChoices { get; } = new();

    [ObservableProperty] private string _relinkFilter = "";
    [ObservableProperty] private VfxSystemItemViewModel? _selectedRelinkChoice;

    partial void OnRelinkFilterChanged(string value) => ApplyRelinkFilter();

    partial void OnSelectedRelinkChoiceChanged(VfxSystemItemViewModel? value)
    {
        if (SelectedParticleNode is not { } node) return;
        // Choosing the placement's CURRENT system clears the edit rather than recording a no-op re-link.
        node.EditedSystemHash = value is null || value.Hash == node.Placement.SystemHash ? 0u : value.Hash;
        RefreshPlacementDirtyFlag();   // M700
        RebuildParticlePlayback();
    }

    /// <summary>Rebuild the candidate list from the loaded map. Only systems with a visual emitter: linking
    /// a placement to a system that draws nothing would look like a broken save.</summary>
    private void RebuildRelinkChoices()
    {
        _relinkAll.Clear();
        foreach (var s in _vfxSystems.Values
                     .Where(s => s.Emitters.Any(e => e.IsVisual))
                     .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            _relinkAll.Add(new VfxSystemItemViewModel { Hash = s.PathHash, Name = s.Name, EmitterCount = s.Emitters.Count(e => e.IsVisual) });
        ApplyRelinkFilter();
    }

    private void ApplyRelinkFilter()
    {
        var keep = RelinkFilter;
        RelinkChoices.Clear();
        foreach (var c in string.IsNullOrWhiteSpace(keep)
                     ? _relinkAll
                     : _relinkAll.Where(c => c.Name.Contains(keep, StringComparison.OrdinalIgnoreCase)))
            RelinkChoices.Add(c);
    }

    /// <summary>Point the picker at what the selected placement currently links to, WITHOUT recording an
    /// edit - assigning SelectedRelinkChoice fires its own handler, which compares against the authored
    /// hash and clears the edit when they match.</summary>
    private void SyncRelinkPicker(ParticlePlacementViewModel? node)
    {
        if (node is null) { SelectedRelinkChoice = null; return; }
        uint current = node.EditedSystemHash != 0 ? node.EditedSystemHash : node.Placement.SystemHash;
        SelectedRelinkChoice = _relinkAll.FirstOrDefault(c => c.Hash == current);
    }

    partial void OnSelectedParticleNodeChanged(ParticlePlacementViewModel? value)
    {
        OnPropertyChanged(nameof(ShowSingleParticleCard));   // M644
        SelectedParticleMarker = value?.CurrentPosition;
        RefreshParticleMoveFields(value);
        SyncRelinkPicker(value);   // M205
        if (value is { } p)
        {
            ShowParticles = true;
            // M55b: selection no longer moves the camera — use the Focus button/command instead
            // M50b: exclusive selection — a particle selection deselects meshes/props/probes
            _selection.Clear();
            if (SelectedPropTreeItem is not null) SelectedPropTreeItem = null;
            if (SelectedProbe is not null) SelectedProbe = null;
            GizmoPivot = p.CurrentPosition;   // M75: the gizmo now works on placements too
        }
        else if (_selection.IsEmpty && SelectedSound is null) GizmoPivot = null;
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
        InspectorTab = InspectorTabs.Materials;
        MaterialEditor.Search = slot.Name;
        MaterialEditor.AutoPreviewDiffuse(slot.Name);   // M50c: show the texture immediately
    }

    /// <summary>M195 (4.4): particles now resolve through the SAME controller path meshes already use
    /// (see UpdateSubmeshVisibility). 4,237 placements bind a VisibilityController that this ignored, so
    /// they stayed visible while the meshes around them switched. Placements use the same map-defined
    /// axes and controller graph as geometry.</summary>
    private bool IsParticleVisible(MapParticlePlacement particle, int? visibilityOverride = null) =>
        particle.VisibilityControllerHash == 0
            ? MapVisibility.VisibleForMask(visibilityOverride ?? particle.VisibilityFlags, _mapVisibility.Primary, CurrentPrimaryVisibilityBit)
            : (_visibilityResolver ??= new MapVisibilityResolver(_mapControllers, _mapVisibility))
                .IsVisible(visibilityOverride ?? particle.VisibilityFlags, particle.VisibilityControllerHash, CurrentVisibilitySelections);

    private bool IsSoundVisible(MapSoundPlacement sound, int? visibilityOverride = null) =>
        MapVisibility.VisibleForMask(visibilityOverride ?? sound.VisibilityFlags, _mapVisibility.Primary, CurrentPrimaryVisibilityBit);

    // M383: ONE gate per placeable category, read by BOTH the marker builders (what is DRAWN) and
    // SelectAnyFromViewport (what is PICKABLE). They used to be written out separately and had drifted:
    // picking omitted ShowPropIcons and ShowSoundIcons entirely, so turning those icons off stopped the
    // markers being drawn while leaving them clickable - an invisible marker would take the click and
    // select a prop or sound instead of the mesh behind it. Anything pickable must be visible; keeping
    // the predicate in one place is what stops that pair going out of sync again.
    private bool CanPickParticles => ShowParticles && MapContent.HasParticles;
    private bool CanPickProps => ShowPlaceables && ShowPropIcons && MapContent.HasProps;
    private bool CanPickProbes => ShowPlaceables && MapContent.HasProbes;
    private bool CanPickSounds => ShowPlaceables && ShowSoundIcons && MapContent.HasSounds;
    /// <summary>Lights are the odd one out: PointLightViewModel is a plain ObservableObject with no
    /// per-item eye/disable/delete state, so ShowLightMarkers - the flag that decides whether the glow
    /// icon is drawn at all - is the only filter available, and picking now honours it.</summary>
    private bool CanPickLights => ShowDynamicLights && ShowLightMarkers;

    private void UpdateParticleMarkers() =>
        ParticleMarkers = CanPickParticles
            ? MapContent.AllParticles.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved
                && IsParticleVisible(v.Placement, v.EffectiveVisibilityFlags)).Select(v => v.CurrentPosition).ToList() : null;

    // ---- M38: cubemap probes + animated props (placed characters) ----
    [ObservableProperty] private IReadOnlyList<MapCubemapProbe>? _currentModelProbes;
    [ObservableProperty] private IReadOnlyList<MapAnimatedProp>? _currentModelProps;
    [ObservableProperty] private IReadOnlyList<System.Numerics.Vector3>? _propMarkers;
    [ObservableProperty] private IReadOnlyList<System.Numerics.Vector3>? _probeMarkers;
    [NotifyPropertyChangedFor(nameof(ActiveOverlayCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveOverlays))]
    [NotifyPropertyChangedFor(nameof(OverlayBadge))]
    [ObservableProperty] private bool _showPlaceables = true;
    [ObservableProperty] private bool _playPropAnimations;   // M54: play prop idle animations in the viewport

    // ---- M55: sound placements (MapAudio) + bucket-grid overlay ----
    [ObservableProperty] private IReadOnlyList<MapSoundPlacement>? _currentModelSounds;
    [ObservableProperty] private IReadOnlyList<System.Numerics.Vector3>? _soundMarkers;
    [NotifyPropertyChangedFor(nameof(ActiveOverlayCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveOverlays))]
    [NotifyPropertyChangedFor(nameof(OverlayBadge))]
    [ObservableProperty] private bool _showBucketGrid;
    [ObservableProperty] private float[]? _bucketGridLines;

    /// <summary>M562: navgrid flag cells, as a pos3+bary3 soup.</summary>
    [ObservableProperty] private float[]? _bushCellLines;

    /// <summary>M565: where each visible layer sits in that soup, and its colour.</summary>
    [ObservableProperty] private (int Start, int Count, System.Numerics.Vector4 Color)[]? _bushCellLayers;

    /// <summary>One toggleable layer per flag the loaded grid actually contains.</summary>
    public System.Collections.ObjectModel.ObservableCollection<NavGridLayerViewModel> NavGridLayers { get; } = new();

    public bool HasNavGrid => NavGridLayers.Count > 0;

    /// <summary>
    /// M562: show where the game blocks vision, as opposed to where the foliage is.
    ///
    /// <para>These are two unrelated things and only the first is gameplay. The swaying bush art is
    /// ordinary geometry on a VertexDeform material; the volume that actually hides a champion lives in
    /// <c>assets/maps/navgrid/&lt;map&gt;/aipath.aimesh_ngrid</c>, on a 50-unit lattice.</para>
    /// </summary>
    [ObservableProperty] private bool _showBushAreas;

    /// <summary>The navgrid beside the open map, or null when the map ships none.</summary>
    private ReyEngine.Formats.MapGeo.NavGrid? _navGrid;

    partial void OnShowBushAreasChanged(bool value) => RebuildBushCellLines();

    /// <summary>M565: the button switches the overlay on and turns on the biggest layer, so a first click
    /// draws something rather than nothing. The flyout is where the rest are chosen.</summary>
    [RelayCommand]
    private void ToggleNavGridOverlay()
    {
        ShowBushAreas = !ShowBushAreas;
        if (ShowBushAreas && NavGridLayers.Count > 0 && !NavGridLayers.Any(l => l.IsVisible))
            NavGridLayers[0].IsVisible = true;
        else RebuildBushCellLines();
    }

    /// <summary>
    /// Reads the navgrid that sits beside the open mapgeo. Opportunistic: a map without one simply has no
    /// overlay, and nothing about loading a map should fail because of it.
    /// </summary>
    private void LoadNavGridForCurrentMap(string? mapGeoPath)
    {
        _navGrid = null;
        BushCellLines = null;
        BushCellLayers = null;
        NavGridLayers.Clear();
        OnPropertyChanged(nameof(HasNavGrid));
        if (string.IsNullOrWhiteSpace(mapGeoPath)) return;

        // .../mapgeometry/<map>/<file>.mapgeo -> the folder name IS the map key the navgrid path uses.
        var parts = mapGeoPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        int at = Array.FindIndex(parts, x => x.Equals("mapgeometry", StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= parts.Length)
        { _log.Info("NavGrid", $"No map folder in '{mapGeoPath}', so no navgrid to look for."); return; }
        string map = parts[at + 1];

        try
        {
            string navPath = ReyEngine.Formats.MapGeo.NavGrid.PathFor(map);
            ulong hash = HashAlgorithms.WadPath(navPath);
            if (!TryResolveEntry(hash, out _))
            { _log.Info("NavGrid", $"This map ships no navgrid ({navPath}), so there is nothing to show."); return; }
            if (!ReyEngine.Formats.MapGeo.NavGrid.TryParse(ReadAsset(hash), out var grid, out string? why) || grid is null)
            { if (why is not null) _log.Info("NavGrid", $"{map}: {why}"); return; }

            _navGrid = grid;
            var present = grid.PresentFlags();
            _log.Info("NavGrid", $"{map}: v{grid.VersionMajor}.{grid.VersionMinor}, {grid.CountX}x{grid.CountZ} "
                + $"cells of {grid.CellSize:n0} units, {present.Count} distinct flag(s)"
                + (grid.HasHeights ? "." : ", and NO ground heights - its cells sit at the grid floor."));

            // M565: the bits are NOT named. M562 called 0x0004 the bush and the reporter, looking at the
            // cells on their own map, identified them as the area only one team may walk. So every flag
            // present becomes its own toggleable layer and whoever is looking at the map does the naming -
            // which is the only way any of these get identified honestly.
            for (int i = 0; i < present.Count; i++)
            {
                var (mask, cells) = present[i];
                NavGridLayers.Add(new NavGridLayerViewModel
                {
                    Mask = mask,
                    Cells = cells,
                    Share = grid.CellCount > 0 ? cells / (double)grid.CellCount : 0,
                    Color = LayerColor(i),
                    Changed = RebuildBushCellLines,
                });
                _log.Info("NavGrid", $"  0x{mask:x4} (bit {System.Numerics.BitOperations.TrailingZeroCount(mask)}): "
                    + $"{cells:n0} cell(s), {(grid.CellCount > 0 ? 100.0 * cells / grid.CellCount : 0):n1}%");
            }
            OnPropertyChanged(nameof(HasNavGrid));
            if (ShowBushAreas) RebuildBushCellLines();
        }
        catch (Exception ex) { _log.Info("NavGrid", "Could not read the navgrid: " + ex.Message); }
    }

    /// <summary>Distinct, readable against terrain, and stable per slot so a layer keeps its colour.</summary>
    private static System.Numerics.Vector4 LayerColor(int index)
    {
        System.Numerics.Vector4[] palette =
        {
            new(0.26f, 0.85f, 0.36f, 0.80f),   // green
            new(0.95f, 0.35f, 0.35f, 0.80f),   // red
            new(0.35f, 0.62f, 0.98f, 0.80f),   // blue
            new(0.98f, 0.78f, 0.28f, 0.80f),   // amber
            new(0.78f, 0.45f, 0.95f, 0.80f),   // violet
            new(0.30f, 0.88f, 0.85f, 0.80f),   // cyan
            new(0.98f, 0.55f, 0.80f, 0.80f),   // pink
            new(0.70f, 0.70f, 0.72f, 0.80f),   // grey
        };
        return palette[index % palette.Length];
    }

    private void RebuildBushCellLines()
    {
        if (!ShowBushAreas) { BushCellLines = null; BushCellLayers = null; return; }
        if (_navGrid is not { } grid)
        {
            BushCellLines = null; BushCellLayers = null;
            _log.Info("NavGrid", "No navgrid is loaded for this map, so there is nothing to show.");
            return;
        }
        var visible = NavGridLayers.Where(l => l.IsVisible).ToList();
        if (visible.Count == 0)
        {
            BushCellLines = null; BushCellLayers = null;
            _log.Info("NavGrid", "No navgrid layer is switched on.");
            return;
        }

        // One shared buffer, laid out layer by layer, with a (start, count, colour) range per layer. That
        // keeps a single VBO and one draw per layer, rather than a colour attribute the shared wireframe
        // shader would have to grow.
        //
        // Enough lift to clear a cell's own recorded ground without floating. Depth testing is off for
        // this pass (M563), so the lift is cosmetic rather than what makes the overlay visible.
        const float Lift = 12f;
        var verts = new List<float>();
        var ranges = new List<(int Start, int Count, System.Numerics.Vector4 Color)>();
        var lo = new System.Numerics.Vector3(float.MaxValue);
        var hi = new System.Numerics.Vector3(float.MinValue);
        int total = 0;

        foreach (var layer in visible)
        {
            int startVert = verts.Count / 6;
            foreach (var (cl, ch) in grid.CellsWith(layer.Mask))
            {
                float y = cl.Y + Lift;
                void V(float x, float z, float b0, float b1, float b2)
                { verts.Add(x); verts.Add(y); verts.Add(z); verts.Add(b0); verts.Add(b1); verts.Add(b2); }
                V(cl.X, cl.Z, 1, 0, 0); V(ch.X, cl.Z, 0, 1, 0); V(ch.X, ch.Z, 0, 0, 1);
                V(cl.X, cl.Z, 1, 0, 0); V(ch.X, ch.Z, 0, 1, 0); V(cl.X, ch.Z, 0, 0, 1);
                lo = System.Numerics.Vector3.Min(lo, cl); hi = System.Numerics.Vector3.Max(hi, ch);
                total++;
            }
            int count = verts.Count / 6 - startVert;
            if (count > 0) ranges.Add((startVert, count, layer.Color));
        }

        if (total == 0)
        {
            BushCellLines = null; BushCellLayers = null;
            _log.Info("NavGrid", "The selected layer(s) mark no cells on this map.");
            return;
        }

        BushCellLines = verts.ToArray();
        BushCellLayers = ranges.ToArray();
        _log.Info("NavGrid", $"Showing {total:n0} cell(s) across {ranges.Count} layer(s), spanning "
            + $"X {lo.X:n0}..{hi.X:n0} Z {lo.Z:n0}..{hi.Z:n0}."
            + (grid.HasHeights ? "" : " The grid carries no heights, so they sit at its floor."));
    }

    partial void OnCurrentModelSoundsChanged(IReadOnlyList<MapSoundPlacement>? value)
    { MapContent.SetSounds(value ?? Array.Empty<MapSoundPlacement>()); UpdatePlaceableMarkers(); }

    // ---- M56: Wwise audio — banks, one-shot playback, positional map ambience ----
    public Services.SoundPlaybackService Sound { get; } = new();
    /// <summary>M138: wav/mp3/ogg → .wem via Wwise's own encoder (League ships Vorbis wems only).</summary>
    public Services.WemEncoder Encoder { get; } = new();
    /// <summary>M138: recovered Wwise event names (id → name), cached across sessions.</summary>
    public Services.WwiseNameIndex WwiseNames { get; private set; } = Services.WwiseNameIndex.Load();

    /// <summary>M138: rebuild the Wwise event-name index by harvesting name strings from every mounted
    /// .bin and matching their FNV-1 hashes against the ids in the mounted event banks.</summary>
    [RelayCommand]
    private async Task RebuildWwiseNames()
    {
        if (!ContentLoaded) { _log.Warn("Audio", "Open a project or WAD first."); return; }
        IsBuilding = true;
        var progress = BuildProgressSink();
        try
        {
            var result = await Task.Run(() =>
            {
                var wanted = new HashSet<uint>();
                var eventBanks = AssetEntries.Where(e => e.IsResolved
                    && e.Path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)).ToList();
                int i = 0;
                foreach (var e in eventBanks)
                {
                    if (++i % 25 == 0) progress.Report((0.35 * i / Math.Max(1, eventBanks.Count), $"Reading banks… {i}/{eventBanks.Count}"));
                    try
                    {
                        if (Formats.Audio.BnkFile.Parse(ReadAsset(e.PathHash)) is { HasHirc: true } b)
                            foreach (var id in b.Events.Keys) wanted.Add(id);
                    }
                    catch { }
                }

                var idx = new Services.WwiseNameIndex();
                var bins = AssetEntries.Where(e => e.IsResolved
                    && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToList();
                i = 0;
                foreach (var e in bins)
                {
                    if (++i % 100 == 0) progress.Report((0.35 + 0.6 * i / Math.Max(1, bins.Count), $"Scanning bins… {i:n0}/{bins.Count:n0}"));
                    try
                    {
                        var strings = new List<string>();
                        Formats.Meta.BinStringHarvester.Collect(
                            Formats.Meta.SafeBinTree.Parse(ReadAsset(e.PathHash)), strings);
                        idx.Harvest(strings, wanted);
                    }
                    catch { }
                }
                progress.Report((0.97, "Deriving sibling events…"));
                int derived = idx.ExpandVerbs(wanted);
                return (Index: idx, Wanted: wanted.Count, Derived: derived);
            });

            result.Index.Merge(WwiseNames);   // keep anything a previous scan found
            WwiseNames = result.Index;
            WwiseNames.Save();
            _log.Success("Audio", $"Wwise names: {WwiseNames.Count:n0} of {result.Wanted:n0} event id(s) resolved "
                + $"({100.0 * WwiseNames.Count / Math.Max(1, result.Wanted):0.#}%, {result.Derived} derived from Play_/Stop_ siblings). Cached for next time.");
        }
        catch (Exception ex) { _log.Error("Audio", ex.Message); }
        finally { IsBuilding = false; }
    }
    private Formats.Audio.AudioBankSet? _mapAudioBanks;
    [ObservableProperty] private MapSoundViewModel? _selectedSound;
    [ObservableProperty] private bool _ambienceEnabled;
    [ObservableProperty] private string _audioStatus = "";
    private System.Numerics.Vector3 _lastCamPosForAudio;

    /// <summary>Load the map's Wwise banks (env/mus events + audio bnk/wpk under sounds/wwise matching
    /// mapN). Called from the map-load background task; cheap misses are fine.</summary>
    private void LoadMapAudioBanks(string mapgeoPath, IReadOnlyList<MapSoundPlacement> sounds)
    {
        _mapAudioBanks = null;
        AudioStatus = "";
        var m = System.Text.RegularExpressions.Regex.Match(mapgeoPath, @"map(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return;
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"map{m.Groups[1].Value}" };
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "env_", "mus_" };
        foreach (var sound in sounds)
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                         sound.EventName, @"_map(\d+)_", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                tags.Add($"map{match.Groups[1].Value}");
            if (sound.EventName.Contains("_Env_", StringComparison.OrdinalIgnoreCase)) families.Add("env_");
            if (sound.EventName.Contains("_Mus_", StringComparison.OrdinalIgnoreCase)) families.Add("mus_");
            if (sound.EventName.Contains("_Misc_", StringComparison.OrdinalIgnoreCase)) families.Add("misc_");
            if (sound.EventName.Contains("_Npc_", StringComparison.OrdinalIgnoreCase)) families.Add("npc_");
        }
        var set = new Formats.Audio.AudioBankSet();
        int banks = 0, packs = 0;
        foreach (var e in AssetEntries)
        {
            if (!e.IsResolved) continue;
            var p = e.Path;
            // Load only shared bank families referenced by this map. Map11 materials can carry
            // historical Map1/Map10 VFX-audio events while current assets use Map11.
            if (!p.Contains("sounds/wwise", StringComparison.OrdinalIgnoreCase)
                || !p.Contains("/sfx/shared/", StringComparison.OrdinalIgnoreCase)) continue;
            var file = Path.GetFileName(p);
            if (!families.Any(prefix => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
            if (!file.Contains("_global_", StringComparison.OrdinalIgnoreCase)
                && !tags.Any(tag => file.Contains(tag, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                if (p.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
                { if (Formats.Audio.BnkFile.Parse(ReadAsset(e.PathHash)) is { } b) { set.AddBank(b, e.PathHash, e.Path); banks++; } }
                else if (p.EndsWith(".wpk", StringComparison.OrdinalIgnoreCase))
                { if (Formats.Audio.WpkFile.Parse(ReadAsset(e.PathHash)) is { } w) { set.AddPack(w, e.PathHash, e.Path); packs++; } }
            }
            catch { /* skip broken/subchunked banks */ }
        }
        if (!set.IsEmpty)
        {
            _mapAudioBanks = set;
            _log.Info("Audio", $"{string.Join('/', tags.Order())}: {banks} bank(s) + {packs} wem pack(s) — {set.EventCount} event(s), {set.WemCount} wem(s)." +
                               (Sound.IsAvailable ? "" : " vgmstream-cli NOT found — playback disabled."));
        }
    }

    /// <summary>Resolve + decode + play one wem of the selected sound's event (one-shot).</summary>
    [RelayCommand]
    private void PlaySelectedSound()
    {
        if (SelectedSound is not { } snd) return;
        if (_mapAudioBanks is null) { AudioStatus = "No audio banks loaded for this map."; return; }
        if (!Sound.IsAvailable) { AudioStatus = "vgmstream-cli.exe not found (needed to decode Wwise Vorbis)."; return; }
        var wems = _mapAudioBanks.ResolveEvent(snd.EventName);
        if (wems.Count == 0) { AudioStatus = $"Event not found in the loaded banks: {snd.EventName}"; return; }
        var wemData = wems.Select(id => (Id: id, Data: _mapAudioBanks.GetWemData(id))).FirstOrDefault(x => x.Data is not null);
        if (wemData.Data is null) { AudioStatus = $"wem data missing ({wems.Count} candidate id(s))."; return; }
        var wav = Sound.DecodeToWav(wemData.Id, wemData.Data);
        if (wav is null) { AudioStatus = "Decode failed."; return; }
        Sound.PlayWav(wav, 1f, loop: false, tag: "oneshot");
        AudioStatus = $"Playing {snd.EventName} (wem {wemData.Id}, {wems.Count} candidate(s)).";
    }

    [RelayCommand]
    private void StopAllSounds() { Sound.StopAll(); AudioStatus = ""; }

    /// <summary>M57: replace the wem behind the selected sound's event with an imported .wem file, rebuild
    /// the owning bank/pack, validate it re-parses + decodes, and save it to the project override.</summary>
    [RelayCommand]
    private async Task ReplaceSelectedSoundWem()
    {
        if (SelectedSound is not { } snd || _mapAudioBanks is null) return;
        var wems = _mapAudioBanks.ResolveEvent(snd.EventName);
        var targetId = wems.FirstOrDefault(id => _mapAudioBanks.SourceOf(id) is not null);
        if (targetId == 0) { AudioStatus = "This event has no editable embedded wem in the loaded banks."; return; }
        if (_mapAudioBanks.SourceOf(targetId) is not { } src) return;
        if (!TryResolveEntry(src.PathHash, out var bankEntry)) { AudioStatus = "Bank asset not resolvable for override."; return; }
        if (!GuardEditable(bankEntry)) return;

        var file = await Dialogs.OpenFileAsync($"Replace wem {targetId} (.wem)",
            new Avalonia.Platform.Storage.FilePickerFileType("Wwise wem") { Patterns = new[] { "*.wem" } }, DialogService.All);
        if (file is null) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            var newData = await File.ReadAllBytesAsync(file);
            // sanity: League wems are RIFF/WAVE
            if (newData.Length < 12 || newData[0] != (byte)'R' || newData[1] != (byte)'I' || newData[2] != (byte)'F' || newData[3] != (byte)'F')
            { AudioStatus = "Not a RIFF/WAVE .wem file. Convert to .wem first (e.g. via a Wwise tool)."; return; }

            var rebuilt = _mapAudioBanks.ReplaceWem(targetId, newData);
            if (rebuilt is not { } rb) { AudioStatus = "Rebuild failed (wem not embedded here)."; return; }

            // validate: the rebuilt bank/pack must re-parse and the new wem must decode
            bool reparse = src.Bnk is not null
                ? Formats.Audio.BnkFile.Parse(rb.Bytes)?.GetWemData(targetId) is not null
                : Formats.Audio.WpkFile.Parse(rb.Bytes)?.GetWemData(targetId) is not null;
            if (!reparse) { AudioStatus = "Rebuilt bank failed to re-parse — NOT saved."; return; }
            if (Sound.DecodeToWav(targetId, newData) is null)
                _log.Warn("Audio", "Imported wem didn't decode with vgmstream — saving anyway (it may still be valid in-game).");

            // M417: project file first - see the mapgeo and placement saves.
            if (!TryWriteToProjectFile(bankEntry, rb.Bytes, out var dest))
            {
                dest = ProjectWorkspace.StoreOverrideBytes(Project, bankEntry.PathHash, rb.Bytes, Path.GetExtension(rb.Path));
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = bankEntry.PathHash,
                    ResolvedPath = bankEntry.IsResolved ? bankEntry.Path : null,
                    OverrideFile = dest,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(bankEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            UpdateTitle();
            Sound.ClearCache(targetId);   // so Play uses the new audio
            AudioStatus = $"Replaced wem {targetId} in {Path.GetFileName(rb.Path)} ({rb.Bytes.Length:n0} B). Build Package will include it.";
            _log.Success("Audio", $"Replaced wem {targetId} for '{snd.EventName}' in {Path.GetFileName(rb.Path)} → override.");
        }
        catch (Exception ex) { _log.Error("Audio", ex.Message); AudioStatus = ex.Message; }
    }

    /// <summary>M70: load a legacy Riot Light.dat point-light table and render it as dynamic point lights.
    /// The lights are in the file's own map world space, so it lines up when the loaded map matches the
    /// Light.dat (e.g. the old Map1 file on classic SR geometry).</summary>
    [RelayCommand]
    private async Task LoadLightDat()
    {
        var file = await Dialogs.OpenFileAsync("Load Riot Light.dat (point lights)",
            new Avalonia.Platform.Storage.FilePickerFileType("Light.dat") { Patterns = new[] { "*.dat" } }, DialogService.All);
        if (file is null) return;
        try
        {
            var lights = LightDatFile.Parse(await File.ReadAllBytesAsync(file));
            if (lights.Count == 0) { _log.Warn("Lights", $"No point lights parsed from {Path.GetFileName(file)}."); return; }
            LightDatPath = file;              // M152: Save writes straight back here
            LoadEditableLights(lights);       // republishes DynamicLights + status
            ShowDynamicLights = true;
            _log.Success("Lights", $"Loaded {lights.Count} point light(s) from {Path.GetFileName(file)}. Toggle 'Lights' in the viewport toolbar.");
        }
        catch (Exception ex) { _log.Error("Lights", ex.Message); }
    }

    partial void OnAmbienceEnabledChanged(bool value)
    {
        if (!value) { Sound.StopAll(); return; }
        UpdateAmbience(_lastCamPosForAudio, force: true);
    }

    /// <summary>M56: positional ambience — loop the nearest sound placements with distance-based volume.
    /// Called from the viewport when the camera moves.</summary>
    public void UpdateAmbience(System.Numerics.Vector3 camPos, bool force = false)
    {
        _lastCamPosForAudio = camPos;
        if (!AmbienceEnabled || _mapAudioBanks is null || !Sound.IsAvailable) return;

        const int maxVoices = 6;
        var nearest = MapContent.Sounds
            .Select((vm, i) => (Vm: vm, Index: i))
            .Where(x => x.Vm.IsEditorVisible && !x.Vm.IsDisabled && !x.Vm.IsRemoved
                && IsSoundVisible(x.Vm.Sound, x.Vm.EffectiveVisibilityFlags))
            .Select(x => (Sound: x.Vm.Sound, x.Index, Dist: System.Numerics.Vector3.Distance(x.Vm.Position, camPos)))
            .Where(x => x.Dist < x.Sound.Radius)
            .OrderBy(x => x.Dist)
            .Take(maxVoices)
            .ToList();

        var wanted = new HashSet<string>(nearest.Select(x => $"amb:{x.Index}"));
        // stop voices out of range
        foreach (var s in _activeAmbience.ToList())
            if (!wanted.Contains(s)) { Sound.StopTag(s); _activeAmbience.Remove(s); }
        // start/adjust in-range voices
        foreach (var x in nearest)
        {
            string voiceTag = $"amb:{x.Index}";
            float vol = Math.Clamp(1f - x.Dist / Math.Max(1f, x.Sound.Radius), 0f, 1f);
            if (_activeAmbience.Contains(voiceTag))
            {
                if (Sound.IsTagPlaying(voiceTag)) Sound.SetTagVolume(voiceTag, vol);
                continue;
            }
            var wems = _mapAudioBanks.ResolveEvent(x.Sound.EventName);
            var wem = wems.Select(id => (Id: id, Data: _mapAudioBanks.GetWemData(id))).FirstOrDefault(w => w.Data is not null);
            if (wem.Data is null) continue;
            var wav = Sound.DecodeToWav(wem.Id, wem.Data);
            if (wav is null) continue;
            Sound.PlayWav(wav, vol, loop: x.Sound.Loop, tag: voiceTag);
            _activeAmbience.Add(voiceTag);
        }
    }
    private readonly HashSet<string> _activeAmbience = new();

    // M77b: the toolbar toggle is the ONLY control of the overlay — selection never shows or hides it.
    partial void OnShowBucketGridChanged(bool value) => RebuildBucketGridLines();

    /// <summary>M77: the loaded map has culling grids (drives the toolbar toggle/rebuild visibility).</summary>
    [ObservableProperty] private bool _hasBucketGrids;

    /// <summary>M77: regenerate every bucket grid from the map's CURRENT world-space triangles (uses the
    /// M58 builder — same rules the game data follows). Preview updates immediately; saving the map writes
    /// the regenerated grids into the mapgeo (the save path re-runs the builder over the final geometry).</summary>
    // ============================================================ M412: bucket-grid bake box
    // The grid has NO height (it is a 2D X/Z culling grid); the "height" here is the triangle REJECT
    // FILTER the bake applies (M410). The preview volume is the DERIVED X/Z extent extruded through that
    // slab, computed by the bake's own derivation so it cannot disagree with a real rebuild.

    [NotifyPropertyChangedFor(nameof(ActiveOverlayCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveOverlays))]
    [NotifyPropertyChangedFor(nameof(OverlayBadge))]
    [ObservableProperty] private bool _showBakeBox;
    [ObservableProperty] private string _bakeHeightMinText = "-120";
    [ObservableProperty] private string _bakeHeightMaxText = "5000";
    [ObservableProperty] private string _bakeBucketSizeText = "500";
    /// <summary>What the panel shows: grids, cells, cell size - the cell count is the redraw driver
    /// because the BOX can be byte-identical while the grid inside it changes completely (M411).</summary>
    [ObservableProperty] private string _bakeBoxInfo = "";
    /// <summary>The preview volume for both viewports, or null when nothing survives the filter.</summary>
    [ObservableProperty] private (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)? _bakeBox;

    /// <summary>German locale: the user types "4,5" as naturally as "4.5", and file convention is
    /// InvariantCulture - accept both rather than making one of them silently wrong.</summary>
    private static bool ParseBakeFloat(string text, out float value) =>
        float.TryParse((text ?? "").Trim().Replace(',', '.'),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);

    // M412: the save path re-bakes independently of the Rebuild command, so it must use the SAME
    // settings or the saved file silently diverges from the preview the user approved.
    private float SaveBakeSize() => BakeSettings()?.Size ?? Formats.MapGeo.MapBucketGridBuilder.TargetBucketSize;
    private float SaveBakeMin() => BakeSettings()?.Min ?? Formats.MapGeo.MapBucketGridBuilder.HeightRangeMin;
    private float SaveBakeMax() => BakeSettings()?.Max ?? Formats.MapGeo.MapBucketGridBuilder.HeightRangeMax;

    private (float Size, float Min, float Max)? BakeSettings()
    {
        if (!ParseBakeFloat(BakeBucketSizeText, out float size)
            || !ParseBakeFloat(BakeHeightMinText, out float min)
            || !ParseBakeFloat(BakeHeightMaxText, out float max)) return null;
        return (size, min, max);
    }

    partial void OnShowBakeBoxChanged(bool value)
    {
        if (!value) { BakeBox = null; BakeBoxInfo = ""; return; }
        _ = RefreshBakeBoxPreviewAsync();
    }
    partial void OnBakeHeightMinTextChanged(string value) { if (ShowBakeBox) _ = RefreshBakeBoxPreviewAsync(); }
    partial void OnBakeHeightMaxTextChanged(string value) { if (ShowBakeBox) _ = RefreshBakeBoxPreviewAsync(); }
    partial void OnBakeBucketSizeTextChanged(string value) { if (ShowBakeBox) _ = RefreshBakeBoxPreviewAsync(); }

    private int _bakeBoxGeneration;

    /// <summary>Debounced dry-run of the bake. A full Rebuild costs seconds on Summoner's Rift, so typing
    /// "5000" must not run it four times: 300 ms of quiet first, and a generation counter discards any
    /// result that finished after the settings moved on.</summary>
    private async Task RefreshBakeBoxPreviewAsync()
    {
        int gen = ++_bakeBoxGeneration;
        await Task.Delay(300);
        if (gen != _bakeBoxGeneration) return;
        if (_currentMap is not { } map) { BakeBoxInfo = "Load a map first."; BakeBox = null; return; }
        if (BakeSettings() is not { } cfg)
        { BakeBoxInfo = "Enter numbers (height min/max, bucket size)."; BakeBox = null; return; }

        try
        {
            var (grids, box) = await Task.Run(() =>
            {
                var g = Formats.MapGeo.MapBucketGridBuilder.Rebuild(map, cfg.Size, cfg.Min, cfg.Max);
                (System.Numerics.Vector3, System.Numerics.Vector3)? b = null;
                if (g.Count > 0)
                {
                    float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                    foreach (var e in g)
                    { minX = MathF.Min(minX, e.MinX); minZ = MathF.Min(minZ, e.MinZ);
                      maxX = MathF.Max(maxX, e.MaxX); maxZ = MathF.Max(maxZ, e.MaxZ); }
                    b = (new System.Numerics.Vector3(minX, cfg.Min, minZ),
                         new System.Numerics.Vector3(maxX, cfg.Max, maxZ));
                }
                return (g, b);
            });
            if (gen != _bakeBoxGeneration) return;   // stale - the settings moved on while we baked

            BakeBox = box;
            BakeBoxInfo = grids.Count == 0
                ? "Nothing survives the height filter - no geometry would be baked."
                : $"{grids.Count} grid(s), {grids[0].BucketsPerSide}x{grids[0].BucketsPerSide} cells, "
                  + $"cell {grids[0].BucketSizeX:0}x{grids[0].BucketSizeZ:0} - "
                  + $"{grids.Sum(g2 => g2.Vertices.Count):n0} vert(s)";
        }
        catch (Exception ex)
        {
            if (gen != _bakeBoxGeneration) return;
            // Inverted range and the per-cell u16 ceilings arrive here. Shown, not thrown: mid-typing
            // states are transient and a message beats a crash.
            BakeBox = null;
            BakeBoxInfo = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RebuildBucketGrids()
    {
        if (_currentMap is not { } map) { _log.Warn("BucketGrid", "Load a map first."); return; }
        Status = "Rebuilding bucket grids…";
        try
        {
            // M412: the panel's settings, falling back to the shipped defaults when unparsable.
            var cfg = BakeSettings() ?? (MapBucketGridBuilder.TargetBucketSize,
                MapBucketGridBuilder.HeightRangeMin, MapBucketGridBuilder.HeightRangeMax);
            var grids = await Task.Run(() => MapBucketGridBuilder.Rebuild(map, cfg.Size, cfg.Min, cfg.Max));
            var infos = grids.Select(g =>
            {
                var mp = new float[g.Vertices.Count * 3];
                for (int i = 0; i < g.Vertices.Count; i++)
                { mp[i * 3] = g.Vertices[i].X; mp[i * 3 + 1] = g.Vertices[i].Y; mp[i * 3 + 2] = g.Vertices[i].Z; }
                // Bucket-grid indices are PER-BUCKET LOCAL (BaseVertex + u16) — resolve to global for preview.
                var resolved = new List<int>(g.Indices.Count);
                foreach (var cell in g.Buckets)
                {
                    int faces = cell.InsideFaceCount + cell.StickingOutFaceCount;
                    for (int f = 0; f < faces; f++)
                    {
                        int i0 = (int)cell.StartIndex + f * 3;
                        if (i0 + 2 >= g.Indices.Count) break;
                        int a = (int)cell.BaseVertex + g.Indices[i0];
                        int b = (int)cell.BaseVertex + g.Indices[i0 + 1];
                        int c = (int)cell.BaseVertex + g.Indices[i0 + 2];
                        if (a >= g.Vertices.Count || b >= g.Vertices.Count || c >= g.Vertices.Count) continue;
                        resolved.Add(a); resolved.Add(b); resolved.Add(c);
                    }
                }
                return new MapBucketGridInfo(g.Key.ControllerHash, g.MinX, g.MinZ, g.MaxX, g.MaxZ,
                    g.BucketSizeX, g.BucketSizeZ, g.BucketsPerSide, g.BucketsPerSide,
                    false, g.Vertices.Count, g.Indices.Count, g.Key.RegionHash, mp, resolved.ToArray());
            }).ToList();
            map.BucketGrids = infos;
            MapContent.SetBucketGrids(infos);
            HasBucketGrids = infos.Count > 0;
            ShowBucketGrid = true;
            RebuildBucketGridLines();
            _log.Success("BucketGrid", $"Rebuilt {infos.Count} grid(s) from the current geometry — " +
                $"{infos.Sum(i => i.VertexCount):n0} baked vert(s) / {infos.Sum(i => i.IndexCount) / 3:n0} tri(s). " +
                "Saving the map writes them into the mapgeo.");
            Status = "Bucket grids rebuilt";
        }
        catch (Exception ex) { _log.Error("BucketGrid", ex.Message); Status = "Bucket grid rebuild failed"; }
    }

    /// <summary>M55b: explicitly frame the camera on the selected placeable (selection itself no longer
    /// moves the camera — Unity-style: select is passive, Focus is an action).</summary>
    [RelayCommand]
    private void FocusSelectedPlaceable()
    {
        if (SelectedParticleMarker is { } pos) ParticleFocusPoint = pos;
    }

    /// <summary>M55/M77b: bucket-grid overlay — the grid's COMPLETE baked scene mesh as 3D wireframe
    /// (every unique triangle edge; a bucket grid is a simplified bake of the map). No flat cell lines,
    /// no sampling. PERF: the array builds OFF the UI thread (a master grid holds 600k+ triangles) and
    /// uploads once; stale builds are dropped when the map/toggle changes mid-build.</summary>
    private int _bucketLinesBuildId;
    private async void RebuildBucketGridLines()
    {
        if (!ShowBucketGrid || _currentMap is not { } map || map.BucketGrids.Count == 0)
        { BucketGridLines = null; return; }
        int buildId = ++_bucketLinesBuildId;
        var grids = map.BucketGrids;
        var lines = await Task.Run(() => BuildBucketGridLineArray(grids));
        if (buildId != _bucketLinesBuildId || !ShowBucketGrid) return;   // superseded while building
        BucketGridLines = lines;
    }

    /// <summary>M77b: pos3+bary3 triangle soup (6 floats/vertex) — the viewport draws it with the
    /// barycentric wireframe shader, giving the full-mesh wireframe look at triangle-raster cost.</summary>
    private static float[] BuildBucketGridLineArray(IReadOnlyList<MapBucketGridInfo> grids)
    {
        long totalTris = 0;
        foreach (var g in grids)
            if (g.MeshIndices is { } gi) totalTris += gi.Length / 3;
        var verts = new float[totalTris * 3 * 6];
        int k = 0;
        foreach (var g in grids)
        {
            if (g is not { MeshPositions: { } pos, MeshIndices: { } idx }) continue;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            for (int c = 0; c < 3; c++)
            {
                int v = idx[t + c];
                verts[k++] = pos[v * 3]; verts[k++] = pos[v * 3 + 1]; verts[k++] = pos[v * 3 + 2];
                verts[k++] = c == 0 ? 1f : 0f;
                verts[k++] = c == 1 ? 1f : 0f;
                verts[k++] = c == 2 ? 1f : 0f;
            }
        }
        return verts;
    }
    [ObservableProperty] private object? _selectedPropTreeItem;
    [ObservableProperty] private AnimatedPropViewModel? _selectedPropNode;
    [ObservableProperty] private CubemapProbeViewModel? _selectedProbe;
    [ObservableProperty] private string _selectedPlaceableInfo = "";
    public ObservableCollection<string> PropSkinChoices { get; } = new();
    /// <summary>M677: the selected prop's character clips, by .anm file name, with the idle entry first.</summary>
    public ObservableCollection<string> PropAnimationChoices { get; } = new();

    partial void OnCurrentModelProbesChanged(IReadOnlyList<MapCubemapProbe>? value)
    { MapContent.SetProbes(value ?? Array.Empty<MapCubemapProbe>()); UpdatePlaceableMarkers(); }
    partial void OnCurrentModelPropsChanged(IReadOnlyList<MapAnimatedProp>? value)
    {
        _propClipTables.Clear();   // M679: another map, another set of skins and graphs
        MapContent.SetProps(value ?? Array.Empty<MapAnimatedProp>()); UpdatePlaceableMarkers(); _ = RefreshPropMeshesAsync();
    }

    // ---- M41: render the placed prop meshes (SRU_Baron, dragons, camps…) at their placements ----
    [ObservableProperty] private bool _showPropMeshes;
    [ObservableProperty] private PropRenderSet? _currentPropMeshes;

    partial void OnShowPropMeshesChanged(bool value) => _ = RefreshPropMeshesAsync();

    private async System.Threading.Tasks.Task RefreshPropMeshesAsync()
    {
        if (!ShowPropMeshes || CurrentModelProps is not { Count: > 0 } props)
        {
            _propInstances = System.Array.Empty<PropInstanceData>();   // M79
            PublishAddedMeshPreview();   // keep any added meshes visible even with props off
            return;
        }
        var visible = MapContent.AllProps
            .Where(p => p.IsEditorVisible && !p.IsDisabled && !p.IsRemoved).ToList();
        var snapshot = visible
            .Select(p => (Prop: p.Prop with { Skin = p.EffectiveSkin, VisibilityFlags = p.EffectiveVisibilityFlags,
                                              Transform = p.CurrentTransform, Position = p.CurrentPosition },   // M699
                          // M677: the clip chosen for this placement. M723: with none chosen, the clip the
                          // PLACEMENT names rather than a guess at the skin's idle - IdleAnimationName is
                          // what the game asks the graph for, and a skin whose base idle is not its first
                          // idle was previewed playing the wrong one. TryBuildPropMesh still falls back to
                          // the guess when the named clip resolves to nothing.
                          Clip: p.EffectiveAnimation ?? (p.Prop.IdleAnimation is { Length: > 0 } named ? named : null)))
            .ToList();
        var (set, owners, resolved, failed) = await System.Threading.Tasks.Task.Run(() => BuildPropRenderSet(snapshot));
        if (!ShowPropMeshes) return;   // toggled off while decoding
        _propInstances = set?.Instances ?? (IReadOnlyList<PropInstanceData>)System.Array.Empty<PropInstanceData>();   // M79
        // M699: which placement each instance came from, so a gizmo drag moves it without decoding anything again
        _propInstanceOwners = owners.Select(i => visible[i]).ToList();
        PublishAddedMeshPreview();      // props + added meshes combined
        _log.Info("Props", $"Rendering {resolved} prop mesh(es); {failed} couldn't be resolved (shown as markers).");
    }

    /// <summary>Decode each unique prop skin once (mesh + per-submesh diffuse) and place an instance per
    /// placement. Runs off the UI thread. Returns the set + resolved/failed counts (logged on return).</summary>
    private (PropRenderSet? set, IReadOnlyList<int> owners, int resolved, int failed) BuildPropRenderSet(
        IReadOnlyList<(MapAnimatedProp Prop, string? Clip)> props)
    {
        // M677: one mesh per (skin, clip). The pose is per mesh in both renderers, so two placements of one
        // skin that should play different clips need two meshes - the geometry is uploaded twice, which is
        // the price of the choice and is paid only when it is made.
        var meshBySkin = new Dictionary<string, PropMesh?>(StringComparer.OrdinalIgnoreCase);
        var texByPath = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        var instances = new List<PropInstanceData>();
        int failed = 0;
        var owners = new List<int>(props.Count);   // M699: instance i belongs to props[owners[i]]
        for (int i = 0; i < props.Count; i++)
        {
            var (p, clip) = props[i];
            if (string.IsNullOrEmpty(p.Skin)) { failed++; continue; }
            string key = clip is null ? p.Skin : p.Skin + "|" + clip;
            if (!meshBySkin.TryGetValue(key, out var mesh))
                meshBySkin[key] = mesh = TryBuildPropMesh(p.Skin, texByPath, clip);
            if (mesh is not null) { instances.Add(PropInstanceData.Place(mesh, p.Transform)); owners.Add(i); }   // M676: skinScale under it
            else failed++;
        }
        return (instances.Count > 0 ? new PropRenderSet(instances) : null, owners, instances.Count, failed);
    }

    private PropMesh? TryBuildPropMesh(string skin, Dictionary<string, TextureImage?> texCache, string? clip = null)
    {
        try
        {
            var binBytes = ReadAssetByPath("data/" + skin.ToLowerInvariant() + ".bin");
            if (binBytes is null) return null;
            var meshRef = SkinMeshExtractor.Extract(binBytes, ResolveWadPath);
            if (meshRef?.SimpleSkin is not { } sknPath) return null;
            var sknBytes = ReadAssetByPath(sknPath);
            if (sknBytes is null) return null;

            var mesh = SkinnedMeshDecoder.Decode(sknBytes);
            var mat = ChampionMaterialResolver.Resolve(binBytes, ResolveBinName, ResolveWadPath);

            // M676: the skin's own scale, which the game applies to the whole model and this preview never
            // did - Baron and the camps drew at the mesh's authored size whatever the bin said.
            float skinScale = 1f;
            try
            {
                var doc = Formats.Materials.MaterialDocument.Parse(binBytes, ResolveBinName, ResolveWadPath);
                if (doc.SkinMesh?.SkinScale is { } authored && authored > 0f) skinScale = authored;
            }
            catch { /* an unparseable skin keeps scale 1 rather than losing the prop */ }
            TextureImage? Tex(string? path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                if (texCache.TryGetValue(path, out var img)) return img;
                return texCache[path] = LoadTextureByPath(path);
            }
            // M678: the same layers and render state the character window resolves for this skin (M664),
            // so a prop in the GL viewport blends, cuts out, tints and glows as the window shows it.
            var subs = mesh.SubMeshes
                .Select(s => new PropSubmesh(s.StartIndex, s.IndexCount, Tex(mat.For(s.Material) ?? meshRef.DefaultTexture))
                {
                    Mask = Tex(mat.ForMask(s.Material)),
                    Gradient = Tex(mat.ForGradient(s.Material)),
                    Emissive = Tex(mat.ForEmissive(s.Material)),
                    MatCap = Tex(mat.ForMatCap(s.Material)),
                    MatCapMask = Tex(mat.ForMatCapMask(s.Material)),
                    Material = mat.HasAny ? ToSubmeshMaterial(mat.Profile(s.Material)) with { BlendWritesDepth = true } : null,
                })
                .ToList();

            // M54: idle-animation payload — the character's skeleton + a best-match idle .anm, so the
            // viewport can play the ambient idles (Baron breathing, camps shuffling...).
            SkeletonAsset? skeleton = null;
            AnimationClip? idle = null;
            if (mesh.CanSkin && meshRef.Skeleton is { } sklPath)
            {
                try
                {
                    var sklBytes = ReadAssetByPath(sklPath);
                    if (sklBytes is not null) skeleton = SkeletonDecoder.Decode(sklBytes);
                    // M677: the chosen clip when there is one, the idle otherwise - and the idle when the
                    // chosen name resolves to nothing, rather than a prop frozen in bind pose.
                    if (skeleton is not null) idle = (clip is not null ? TryFindClip(skin, clip) : null) ?? TryFindIdleClip(skin);
                }
                catch { skeleton = null; idle = null; }
            }
            return new PropMesh(skin, mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, subs)
            {
                SknMesh = mesh, Skeleton = skeleton, IdleClip = idle,
                SknBytes = sknBytes, SkinBinBytes = binBytes, SkinScale = skinScale,   // M676
            };
        }
        catch { return null; }
    }

    /// <summary>M679: the clips a placed skin plays, from its OWN animation graph (see
    /// <see cref="Formats.Characters.PropAnimations"/>), cached per skin for the life of the loaded map.
    /// The folder scan the prop card and the idle picker used until M679 is the fallback for a skin with
    /// no graph. Read from the prop builder's background task as well as the UI thread.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<Formats.Skeletons.AnimClipInfo>> _propClipTables
        = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<Formats.Skeletons.AnimClipInfo> PropClipTable(string skin)
    {
        if (_propClipTables.TryGetValue(skin, out var cached)) return cached;
        IReadOnlyList<Formats.Skeletons.AnimClipInfo> clips = Array.Empty<Formats.Skeletons.AnimClipInfo>();
        try
        {
            var binBytes = ReadAssetByPath("data/" + skin.ToLowerInvariant() + ".bin");
            if (binBytes is not null)
                clips = Formats.Characters.PropAnimations.ResolveClips(binBytes, ReadAssetByPath, ResolveBinName, ResolveWadPath);
        }
        catch { }
        if (clips.Count == 0)
        {
            const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
            var parts = skin.ToLowerInvariant().Split('/');
            int ci = Array.IndexOf(parts, "characters");
            if (ci >= 0 && ci + 1 < parts.Length)
            {
                string marker = $"characters/{parts[ci + 1]}/";
                clips = Formats.Characters.PropAnimations.FromFiles(AssetEntries
                    .Where(e => e.IsResolved && e.Path.EndsWith(".anm", OIC) && e.Path.Contains(marker, OIC))
                    .Select(e => e.Path));
            }
        }
        _propClipTables[skin] = clips;
        return clips;
    }

    /// <summary>M677/M679: what the prop card offers - the graph's clip names, idle-ranked first.</summary>
    private IReadOnlyList<string> PropClipNames(string skin) =>
        PropClipTable(skin).Select(Formats.Characters.PropAnimations.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private AnimationClip? DecodePropClip(Formats.Skeletons.AnimClipInfo? clip)
    {
        if (clip is null) return null;
        try
        {
            var bytes = ReadAssetByPath(clip.AnmPath);
            return bytes is null ? null : AnimationDecoder.Decode(bytes, Formats.Characters.PropAnimations.DisplayName(clip));
        }
        catch { return null; }
    }

    /// <summary>M677/M679: the named clip, as the card listed it.</summary>
    private AnimationClip? TryFindClip(string skin, string name) =>
        DecodePropClip(Formats.Characters.PropAnimations.Find(PropClipTable(skin), name));

    /// <summary>M54/M679: the idle a placed skin plays - its graph's base idle, else a bored one, else any.
    /// Null when the character ships no idle animation.</summary>
    private AnimationClip? TryFindIdleClip(string skin) =>
        DecodePropClip(Formats.Characters.PropAnimations.PickIdle(PropClipTable(skin)));
    partial void OnShowPlaceablesChanged(bool value) => UpdatePlaceableMarkers();

    // ---- M123: independent icon toggles - audio + mob icons no longer all-or-nothing ----
    [NotifyPropertyChangedFor(nameof(ActiveOverlayCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveOverlays))]
    [NotifyPropertyChangedFor(nameof(OverlayBadge))]
    [ObservableProperty] private bool _showSoundIcons = true;
    [NotifyPropertyChangedFor(nameof(ActiveOverlayCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveOverlays))]
    [NotifyPropertyChangedFor(nameof(OverlayBadge))]
    [ObservableProperty] private bool _showPropIcons = true;
    partial void OnShowSoundIconsChanged(bool value) => UpdatePlaceableMarkers();
    partial void OnShowPropIconsChanged(bool value) => UpdatePlaceableMarkers();

    private void UpdatePlaceableMarkers()
    {
        PropMarkers = CanPickProps ? MapContent.AllProps
            .Where(p => p.IsEditorVisible && !p.IsDisabled && !p.IsRemoved).Select(p => p.Position).ToList() : null;
        ProbeMarkers = CanPickProbes ? MapContent.Probes
            .Where(p => p.IsEditorVisible && !p.IsDisabled && !p.IsRemoved).Select(p => p.Position).ToList() : null;
        SoundMarkers = CanPickSounds
            ? MapContent.Sounds.Where(s => s.IsEditorVisible && !s.IsDisabled && !s.IsRemoved
                && IsSoundVisible(s.Sound, s.EffectiveVisibilityFlags)).Select(s => s.Position).ToList() : null;   // M55
    }

    /// <summary>One refresh path for the eye, runtime Disable, and pending Delete controls shared by all
    /// Map Content leaves. The eye never dirties a file. Disable writes a zero visibility mask and can be
    /// restored; Delete is kept separate and removes the object only when map edits are saved.</summary>
    private void OnMapContentItemStateChanged(MapOutlinerItemViewModel item)
    {
        switch (item)
        {
            case ParticlePlacementViewModel p:
                if (p.IsDisabled != (p.EffectiveVisibilityFlags == 0))
                    p.EditedVisibilityFlags = p.IsDisabled ? 0 : (p.Placement.VisibilityFlags == 0 ? 255 : null);
                break;
            case AnimatedPropViewModel p:
                if (p.IsDisabled != (p.EffectiveVisibilityFlags == 0))
                    p.EditedVisibilityFlags = p.IsDisabled ? 0 : (p.Prop.VisibilityFlags == 0 ? 255 : null);
                break;
            case CubemapProbeViewModel p:
                if (p.IsDisabled != (p.EffectiveVisibilityFlags == 0))
                    p.EditedVisibilityFlags = p.IsDisabled ? 0 : (p.Probe.VisibilityFlags == 0 ? 255 : null);
                break;
            case MapSoundViewModel s:
                if (s.Sound.FromParticleSystem)
                {
                    var owner = MapContent.AllParticles.FirstOrDefault(p => p.Placement.Name == s.Sound.Name
                        && p.Placement.Transform == s.Sound.Transform);
                    if (owner is not null)
                    {
                        owner.EditedVisibilityFlags = s.EditedVisibilityFlags;
                        owner.IsDisabled = s.IsDisabled;
                        owner.IsRemoved = s.IsRemoved;
                    }
                    break;
                }
                if (s.IsDisabled != (s.EffectiveVisibilityFlags == 0))
                    s.EditedVisibilityFlags = s.IsDisabled ? 0 : (s.Sound.VisibilityFlags == 0 ? 255 : null);
                break;
            case MapPieceViewModel piece when _currentMap is { } map
                && map.Meshes.FirstOrDefault(m => m.Index == piece.MeshIndex) is { } mesh:
                if (piece.IsDisabled != (mesh.EffectiveVisibility == 0))
                    mesh.VisibilityEdit = piece.IsDisabled ? 0 : (mesh.VisibilityFlags == 0 ? 255 : null);
                HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes)
                    || MapContent.AllMapPieces.Any(x => x.IsRemoved);
                ApplyMapVisibility();
                break;
            case AddedMapMeshViewModel added:
                if (added.IsDisabled)
                {
                    if (added.VisibilityMask != 0) added.EnabledVisibilityMask = added.VisibilityMask;
                    added.VisibilityMask = 0;
                }
                else added.VisibilityMask = added.EnabledVisibilityMask;
                PublishAddedMeshPreview();
                break;
        }

        RefreshPlacementDirtyFlag();   // M700
        UpdateParticleMarkers();
        UpdatePlaceableMarkers();
        RebuildParticlePlayback();
        if (AmbienceEnabled) UpdateAmbience(_lastCamPosForAudio, force: true);
        if (item is AnimatedPropViewModel) _ = RefreshPropMeshesAsync();
        RefreshPlacementLayerEditor();
    }

    partial void OnSelectedPropTreeItemChanged(object? value)
    { if (value is AnimatedPropViewModel p) SelectedPropNode = p; }

    /// <summary>M677: the selected placed prop in the character window - the same skin, opened the way the
    /// character browser opens a champion (<see cref="ICharacterBrowserHost.OpenSkin"/>), so its materials,
    /// clips and drivers can be inspected and edited there. The map wad already holds the assets, which is
    /// why nothing has to be mounted first.</summary>
    [RelayCommand]
    private void OpenPropInCharacterEditor()
    {
        if (SelectedPropNode is not { } node) return;
        string skin = node.EffectiveSkin;
        // M728: the placement's own skin bin - read here for the mesh, and handed on with it below
        string skinBin = "data/" + skin.ToLowerInvariant() + ".bin";
        try
        {
            var binBytes = ReadAssetByPath(skinBin);
            if (binBytes is null) { _log.Warn("Props", $"{skin}: the skin bin is not in this map."); return; }
            var meshRef = SkinMeshExtractor.Extract(binBytes, ResolveWadPath);
            if (meshRef?.SimpleSkin is not { Length: > 0 } sknPath)
            { _log.Warn("Props", $"{skin}: the skin names no mesh."); return; }
            if (!TryResolveEntry(BinTexturePath.HashOfReference(sknPath), out var entry))
            { _log.Warn("Props", $"{skin}: {sknPath} is not in this map."); return; }

            _log.Info("Props", $"{node.Prop.CharacterName} / {node.EffectiveSkinName} — {Path.GetFileName(sknPath)}");
            ShowMeshPreviewWindow?.Invoke();
            // M728: with the bin it came from. Worked out again from the mesh's folder, a skin that shares another
            // skin's mesh opened as that other skin.
            _ = LoadMeshPreviewAsync(entry, skinBin);
            TryLoadMaterialBin(entry, alsoRawBin: true, skinBin: skinBin);
        }
        catch (Exception ex) { _log.Error("Props", ex.Message); }
    }
    partial void OnSelectedPropNodeChanged(AnimatedPropViewModel? value)
    {
        PropSkinChoices.Clear();
        PropAnimationChoices.Clear();
        if (value is not { } p)
        {
            // M699: the gizmo followed this prop, so it goes away with it unless something else is selected
            if (_selection.IsEmpty && SelectedParticleNode is null && SelectedSound is null && SelectedAddedMesh is null
                && SelectedLight is null) GizmoPivot = null;
            return;
        }
        // M677: the clips this character ships, idle first. The list follows the SKIN in play - a swapped
        // skin of the same character has the same animations, so the character is enough.
        PropAnimationChoices.Add(AnimatedPropViewModel.IdleChoice);
        foreach (string clip in PropClipNames(p.EffectiveSkin)) PropAnimationChoices.Add(clip);
        var marker = $"characters/{p.Prop.CharacterName}/skins/";
        foreach (var path in AssetEntries.Where(e => e.IsResolved
                     && e.Path.Contains(marker, StringComparison.OrdinalIgnoreCase)
                     && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                 .Select(e => e.Path.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ? e.Path[5..] : e.Path)
                 .Select(path => path[..^4])
                 .Append(p.Prop.Skin)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            PropSkinChoices.Add(path);
        SelectedProbe = null;
        _selection.Clear();                       // M50b: exclusive selection
        if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
        if (SelectedParticleNode is not null) SelectedParticleNode = null;   // M76: viewport picks bypass the tree item
        if (SelectedSound is not null) SelectedSound = null;   // M56
        SelectedParticleMarker = p.Position;   // M55b: highlight only — camera stays (use Focus)
        GizmoPivot = p.CurrentPosition;        // M699: the gizmo drives props too
        SelectedPlaceableInfo = $"{p.Name}\n{p.Info}\n({p.Position.X:0}, {p.Position.Y:0}, {p.Position.Z:0})";
    }
    partial void OnSelectedProbeChanged(CubemapProbeViewModel? value)
    {
        if (value is not { } p) return;
        SelectedPropNode = null;
        _selection.Clear();                       // M50b: exclusive selection
        if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
        if (SelectedParticleNode is not null) SelectedParticleNode = null;   // M76: viewport picks bypass the tree item
        if (SelectedSound is not null) SelectedSound = null;   // M56
        SelectedParticleMarker = p.Position;   // M55b: highlight only — camera stays (use Focus)
        SelectedPlaceableInfo = $"{p.Name}\ncubemap: {p.Info}\n({p.Position.X:0}, {p.Position.Y:0}, {p.Position.Z:0})";
    }

    // ---- Particle playback (M36) — simulate & render the selected placed system live in the viewport ----
    private static readonly IReadOnlyDictionary<uint, VfxSystemDefinition> EmptyVfx = new Dictionary<uint, VfxSystemDefinition>();
    private IReadOnlyDictionary<uint, VfxSystemDefinition> _vfxSystems = EmptyVfx;
    private readonly Dictionary<uint, IReadOnlyList<TextureImage?>> _vfxTextureCache = new();  // system hash -> sprites
    private readonly Dictionary<uint, IReadOnlyList<TextureImage?>> _vfxTextureMultCache = new();
    private readonly Dictionary<uint, IReadOnlyList<TextureImage?>> _vfxDistortionTextureCache = new();
    private readonly Dictionary<uint, IReadOnlyList<TextureImage?>> _vfxColorTextureCache = new();  // M68: particleColorTexture gradients
    [ObservableProperty] private bool _playParticlePreview;
    [ObservableProperty] private bool _playAllParticles;
    [ObservableProperty] private VfxPlayback? _currentParticlePlayback;

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

    private IReadOnlyList<TextureImage?> ResolveSystemMultTextures(VfxSystemDefinition sys)
    {
        if (_vfxTextureMultCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var texs = new List<TextureImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
            texs.Add(e.TextureMultPath is { } p ? LoadTextureByPath(p) : null);
        _vfxTextureMultCache[sys.PathHash] = texs;
        return texs;
    }

    /// <summary>M174 (2.1): each emitter's alpha-erosion dissolve map, aligned to Emitters.</summary>
    private readonly Dictionary<uint, IReadOnlyList<TextureImage?>> _vfxErosionTextureCache = new();

    private IReadOnlyList<TextureImage?> ResolveSystemErosionTextures(VfxSystemDefinition sys)
    {
        if (_vfxErosionTextureCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var texs = new List<TextureImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
            texs.Add(e.AlphaErosion?.MapPath is { } p ? LoadTextureByPath(p) : null);
        _vfxErosionTextureCache[sys.PathHash] = texs;
        return texs;
    }

    /// <summary>M175 (2.6): each emitter's palette gradient strip, aligned to Emitters.</summary>
    private readonly Dictionary<uint, IReadOnlyList<TextureImage?>> _vfxPaletteTextureCache = new();

    private IReadOnlyList<TextureImage?> ResolveSystemPaletteTextures(VfxSystemDefinition sys)
    {
        if (_vfxPaletteTextureCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var texs = new List<TextureImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
            texs.Add(e.Palette?.TexturePath is { } p ? LoadTextureByPath(p) : null);
        _vfxPaletteTextureCache[sys.PathHash] = texs;
        return texs;
    }

    /// <summary>M181 (2.12): each emitter's reflection cubemap, aligned to Emitters. These are real DDS
    /// cubemaps (e.g. ASSETS/Shared/Particles/MissFortune_Bullet_CubeMap.dds) and go through the same
    /// decoder the M122 skybox uses, so face ordering is shared rather than reinvented.</summary>
    private readonly Dictionary<uint, IReadOnlyList<CubemapImage?>> _vfxReflectionCubeCache = new();

    private IReadOnlyList<CubemapImage?> ResolveSystemReflectionCubemaps(VfxSystemDefinition sys)
    {
        if (_vfxReflectionCubeCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var cubes = new List<CubemapImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
        {
            CubemapImage? cm = null;
            if (e.Reflection?.MapPath is { Length: > 0 } path)
            {
                try
                {
                    var bytes = ReadAssetByPath(path);
                    if (bytes is not null) cm = CubemapDecoder.TryDecodeDds(bytes);
                }
                catch { cm = null; }   // subchunked/corrupt chunks throw inside the mount read
            }
            cubes.Add(cm);
        }
        _vfxReflectionCubeCache[sys.PathHash] = cubes;
        return cubes;
    }

    private IReadOnlyList<TextureImage?> ResolveSystemDistortionTextures(VfxSystemDefinition sys)
    {
        if (_vfxDistortionTextureCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var texs = new List<TextureImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
            texs.Add(e.Distortion?.NormalMapTexturePath is { } p ? LoadTextureByPath(p) : null);
        _vfxDistortionTextureCache[sys.PathHash] = texs;
        return texs;
    }

    /// <summary>M68: resolve each emitter's particleColorTexture (the colour-over-life gradient the simulator
    /// samples on the CPU). Null when the emitter has none — it then keeps its birthColor/color curve.</summary>
    private IReadOnlyList<TextureImage?> ResolveSystemColorTextures(VfxSystemDefinition sys)
    {
        if (_vfxColorTextureCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var texs = new List<TextureImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
            texs.Add(e.ParticleColorTexturePath is { } p ? LoadTextureByPath(p) : null);
        _vfxColorTextureCache[sys.PathHash] = texs;
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
            // Never let one broken mesh (subchunked chunk, missing file) kill the whole playback build —
            // that silently froze "Play All" at the previously-playing single system.
            byte[]? bytes;
            try { bytes = ReadAssetByPath(e.MeshPath); }
            catch { bytes = null; }
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

    /// <summary>M754: the emission surfaces of a system's emitters - the .scb a trail is born along, the
    /// .skn an idle drips from. Cached beside the meshes and cleared with them; a surface that does not load
    /// is logged once with its reason and the emitter keeps emitting from its point.</summary>
    private readonly Dictionary<uint, IReadOnlyList<VfxSurfaceSampler?>?> _vfxSurfaceCache = new();
    private IReadOnlyList<VfxSurfaceSampler?>? ResolveSystemEmissionSurfaces(VfxSystemDefinition sys)
    {
        if (_vfxSurfaceCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var loaded = VfxEmissionSurfaceLoader.LoadSystem(sys, path =>
        {
            try { return ReadAssetByPath(path); } catch { return null; }
        }, (emitter, why) => _log.Warn("VFX", $"'{sys.Name}' / {emitter}: emission surface not shown - {why}."));
        return _vfxSurfaceCache[sys.PathHash] = loaded;
    }

    // ---- Champion-skin VFX (M37) — a loaded skin's effect library, played at the model origin ----
    public ObservableCollection<VfxSystemItemViewModel> ChampionVfxSystems { get; } = new();
    [ObservableProperty] private bool _hasChampionVfx;
    [ObservableProperty] private VfxSystemItemViewModel? _selectedChampionVfx;

    /// <summary>Populate the champion VFX list from a skin's parsed systems (visual systems only, sorted).</summary>
    private void SetChampionVfx(IReadOnlyDictionary<uint, VfxSystemDefinition> systems)
    {
        _vfxSystems = systems;
        _vfxTextureCache.Clear(); _vfxTextureMultCache.Clear(); _vfxDistortionTextureCache.Clear(); _vfxColorTextureCache.Clear(); _vfxMeshCache.Clear(); _vfxErosionTextureCache.Clear();
        _vfxPaletteTextureCache.Clear(); _vfxReflectionCubeCache.Clear(); _vfxSurfaceCache.Clear();
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
        CurrentParticlePlayback = new VfxPlayback(new[] { new VfxPlaybackItem(sys, System.Numerics.Vector3.Zero,
            ResolveSystemTextures(sys), ResolveSystemMeshes(sys), ResolveSystemMultTextures(sys), ResolveSystemDistortionTextures(sys),
            ResolveSystemColorTextures(sys), ResolveSystemErosionTextures(sys),
            ResolveSystemPaletteTextures(sys),
            emitterReflectionCubemaps: ResolveSystemReflectionCubemaps(sys))
            { EmitterEmissionSurfaces = ResolveSystemEmissionSurfaces(sys) } });   // M754
        _log.Info("VFX", $"Playing '{sys.Name}' — {sys.Emitters.Count} emitter(s), {ResolveSystemTextures(sys).Count(t => t is not null)} sprite(s) resolved.");
    }

    [RelayCommand]
    private void StopChampionVfx() => SelectedChampionVfx = null;

    /// <summary>Rebuild the live playback request (M36): all visible placements, or just the selected one.</summary>
    /// <summary>
    /// M403: should a Transitional placement be playing right now?
    ///
    /// <para>Non-transitional placements always pass - this only ever REMOVES transition bursts.</para>
    ///
    /// <para>A transitional placement plays while a crossfade into ITS state is running: its
    /// mVisibilityFlags MASK must contain the bit of the state being entered. Riot's placements carry
    /// masks 2/4/8/16/32/64, i.e. 1 &lt;&lt; BitIndex for Fire..Chemtech, which lines up with the flag table
    /// in Map*.bin.</para>
    ///
    /// <para>INFERRED, not measured: no consumer in the shipped data confirms that Transitional means
    /// "play during a transition". The name, the 1,159 one-shot bursts it marks, and the fact that the
    /// SRS_*_Transition_* systems carry it are the whole basis. If it turns out to mean something else,
    /// this gate is the single place to change.</para>
    /// </summary>
    private bool IsTransitionalParticleActive(Formats.MapGeo.MapParticlePlacement placement, int effectiveFlags)
    {
        if (placement.Transitional != true) return true;          // not a burst - unaffected
        if (!_grassTransition.IsRunning) return false;            // nothing is transitioning

        int bit = GrassTintChoice().BitIndex;
        if (bit < 0) return false;                                // returning to base fires nothing
        int mask = placement.HasVisibilityFlags ? effectiveFlags : 0;
        return (mask & (1 << bit)) != 0;
    }

    private void RebuildParticlePlayback()
    {
        if (PlayAllParticles)
        {
            var items = new List<VfxPlaybackItem>();
            foreach (var v in MapContent.AllParticles)
            {
                if (!v.IsEditorVisible || v.IsDisabled || v.IsRemoved) continue;
                if (!IsParticleVisible(v.Placement, v.EffectiveVisibilityFlags)) continue;
                // M403: Transitional placements are one-shot bursts fired BY a state change - the
                // SRS_*_Transition_DragonPit set and friends. Playing them with everything else would
                // leave an explosion looping over the dragon pit forever, so they are gated to an
                // actually-running transition into the state they belong to.
                if (!IsTransitionalParticleActive(v.Placement, v.EffectiveVisibilityFlags)) continue;
                if (!_vfxSystems.TryGetValue(v.EffectiveSystemHash, out var s) || !s.Emitters.Any(e => e.IsVisual)) continue;
                items.Add(new VfxPlaybackItem(s, v.CurrentTransform, ResolveSystemTextures(s), ResolveSystemMeshes(s),
                    ResolveSystemMultTextures(s), ResolveSystemDistortionTextures(s), ResolveSystemColorTextures(s),
                    ResolveSystemErosionTextures(s), ResolveSystemPaletteTextures(s),
                    ColorModulate: v.EffectiveTint)   // M203 tint; M204 shows a pending re-tint live
                    { EmitterEmissionSurfaces = ResolveSystemEmissionSurfaces(s) });   // M754
            }
            CurrentParticlePlayback = items.Count > 0 ? new VfxPlayback(items, CullByCamera: true) : null;
            _log.Info("Particles", $"Playing all — {items.Count} layer-visible placement(s); viewport culling keeps only nearby on-screen systems active.");
            return;
        }

        if (!PlayParticlePreview || SelectedParticleNode is not { IsEditorVisible: true, IsDisabled: false, IsRemoved: false } node
            || !_vfxSystems.TryGetValue(node.EffectiveSystemHash, out var sys) || sys.Emitters.Count == 0)
        {
            CurrentParticlePlayback = null;
            return;
        }
        var texs = ResolveSystemTextures(sys);
        CurrentParticlePlayback = new VfxPlayback(new[] { new VfxPlaybackItem(sys, node.CurrentTransform, texs,
            ResolveSystemMeshes(sys), ResolveSystemMultTextures(sys), ResolveSystemDistortionTextures(sys),
            ResolveSystemColorTextures(sys), ResolveSystemErosionTextures(sys),
            ResolveSystemPaletteTextures(sys),
            EmitterReflectionCubemaps: ResolveSystemReflectionCubemaps(sys))
            { EmitterEmissionSurfaces = ResolveSystemEmissionSurfaces(sys) } });   // M754
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
        RefreshPlacementDirtyFlag();   // M700
        RebuildParticlePlayback();   // M36: follow the moved particle if it's playing
        _log.Info("Particles", $"Moved '{node.Name}' to ({target.X:0.#}, {target.Y:0.#}, {target.Z:0.#}).");
    }

    /// <summary>M206: duplicate the selected placement. The copy is created in the scene immediately -
    /// offset so it is not hidden inside its source - and written to the .bin on Save to Mod, where the
    /// writer deep-clones the original so the copy keeps every field ReyEngine does not model.</summary>
    [RelayCommand]
    private void DuplicateParticlePlacement()
    {
        if (SelectedParticleNode is not { } node) return;
        if (!node.Placement.Id.IsValid) { _log.Warn("Particles", "This placement has no identity in the bin and cannot be duplicated."); return; }
        if (node.IsNew) { _log.Warn("Particles", "Save the existing copy before duplicating it again."); return; }
        if (_currentMapEntry is null || !TryResolveMaterialsBin(_currentMapEntry.Path, out var binEntry)) return;

        uint key;
        try { key = MapPlaceableWriter.NewItemKey(SafeBinTree.Parse(GetAssetBytes(binEntry)), node.Placement.Id); }
        catch (Exception ex) { _log.Error("Particles", $"Could not mint a key for the copy: {ex.Message}"); return; }

        // Nudged so the copy is visible rather than z-fighting inside the original.
        var copy = new ParticlePlacementViewModel
        {
            Placement = node.Placement with { Id = new MapPlacementId(node.Placement.Id.ContainerHash, key) },
            CloneSource = node.Placement.Id,
            Offset = new System.Numerics.Vector3(100f, 0f, 0f),
            StateChanged = OnMapContentItemStateChanged,
        };
        MapContent.AddParticlePlacement(copy);
        SelectedParticleNode = copy;
        RefreshPlacementDirtyFlag();   // M700
        UpdateParticleMarkers();
        RebuildParticlePlayback();
        _log.Info("Particles", $"Duplicated '{node.Placement.Name}'. Save to Mod writes it into the .bin.");
    }

    /// <summary>M204: resets EVERY pending edit on the placement, not only the transform - the button sits
    /// beside rename/tint/delete now, so "Reset" clearing only the move would be a trap.</summary>
    [RelayCommand]
    private void ResetParticleEdits()
    {
        if (SelectedParticleNode is not { } node) return;
        node.ResetEdits();
        RefreshParticleMoveFields(node);
        SelectedParticleMarker = node.CurrentPosition;
        GizmoPivot = node.CurrentPosition;
        UpdateParticleMarkers();
        RefreshPlacementDirtyFlag();   // M700
        RebuildParticlePlayback();
    }

    // ---- M75: placement gizmo — the viewport gizmo drives particles (move/rotate/scale) and sounds (move).
    // Mirrors the mesh drag API; per-frame updates are silent, EndPlacementDrag logs + refreshes playback. ----

    [ObservableProperty] private AddedMapMeshViewModel? _selectedAddedMesh;   // M79

    /// <summary>True when the gizmo should operate on a placement (no mesh selected, placement is).</summary>
    public bool HasPlacementGizmoTarget => SelectedParticleNode is not null || SelectedSound is not null
                                           || SelectedAddedMesh is not null || SelectedLight is not null   // M154
                                           || SelectedPropNode is not null;                                // M699

    /// <summary>Drag-start state for the active placement (sounds report identity rotation/scale).
    /// M154: a light has no offset model — it stores an absolute position, so it reports that as the
    /// "offset" and DragSelectedPlacementTo writes start+delta straight back as the new position.</summary>
    public (System.Numerics.Vector3 Offset, System.Numerics.Vector3 Rotation, System.Numerics.Vector3 Scale) PlacementDragStart =>
        SelectedParticleNode is { } p ? (p.Offset, p.RotationDegrees, p.Scale)
        : SelectedPropNode is { } r ? (r.Offset, r.RotationDegrees, r.Scale)   // M699
        : SelectedAddedMesh is { } a ? (a.Offset, a.RotationDegrees, a.Scale)
        : SelectedLight is { } l ? (l.Position, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One)
        : (SelectedSound?.Offset ?? System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One);

    // M76: undo support — the whole drag is ONE step, captured at press, pushed at release.
    private object? _placementDragTarget;
    private PlacementTransformCommand.State _placementDragBefore;

    /// <summary>Called at gizmo-press on a placement: capture the before-state for the undo step.</summary>
    public void BeginPlacementDrag()
    {
        _placementDragTarget = (object?)SelectedParticleNode ?? (object?)SelectedSound
                               ?? (object?)SelectedAddedMesh ?? (object?)SelectedLight   // M154
                               ?? SelectedPropNode;                                       // M699
        if (_placementDragTarget is { } t) _placementDragBefore = PlacementTransformCommand.State.Capture(t);
    }

    /// <summary>M76: re-sync everything a placement transform touches (used by undo/redo too).</summary>
    private void RefreshPlacementVisuals(object target)
    {
        switch (target)
        {
            case ParticlePlacementViewModel p:
                SelectedParticleMarker = p.CurrentPosition;
                if (ReferenceEquals(p, SelectedParticleNode)) { GizmoPivot = p.CurrentPosition; RefreshParticleMoveFields(p); }
                UpdateParticleMarkers();
                RebuildParticlePlayback();
                break;
            case MapSoundViewModel s:
                if (ReferenceEquals(s, SelectedSound)) { SelectedParticleMarker = s.Position; GizmoPivot = s.Position; }
                UpdatePlaceableMarkers();
                break;
            case AddedMapMeshViewModel a:   // M79
                if (ReferenceEquals(a, SelectedAddedMesh)) GizmoPivot = a.PivotWorld;
                PublishAddedMeshPreview();
                break;
            case PointLightViewModel l:   // M154
                if (ReferenceEquals(l, SelectedLight)) GizmoPivot = l.Position;
                RepublishLights();
                break;
            case AnimatedPropViewModel r:   // M699
                if (ReferenceEquals(r, SelectedPropNode))
                {
                    SelectedParticleMarker = r.CurrentPosition;
                    GizmoPivot = r.CurrentPosition;
                    SelectedPlaceableInfo = $"{r.Name}\n{r.Info}\n({r.Position.X:0}, {r.Position.Y:0}, {r.Position.Z:0})";
                }
                UpdatePlaceableMarkers();
                RefreshPropInstanceTransforms();
                break;
        }
        RefreshPlacementDirtyFlag();   // M700
    }

    public void DragSelectedPlacementTo(System.Numerics.Vector3 absoluteOffset)
    {
        // M152: a selected point light is dragged like any other placement.
        if (SelectedLight is { } light)
        {
            light.MoveTo(absoluteOffset);
            GizmoPivot = light.Position;
            return;
        }
        if (SelectedParticleNode is { } p)
        {
            p.Offset = absoluteOffset;
            SelectedParticleMarker = p.CurrentPosition;
            GizmoPivot = p.CurrentPosition;
            RefreshParticleMoveFields(p);
            UpdateParticleMarkers();
        }
        else if (SelectedAddedMesh is { } a)   // M79
        {
            a.Offset = absoluteOffset;
            GizmoPivot = a.PivotWorld;
            PublishAddedMeshPreview();
        }
        else if (SelectedSound is { } s)
        {
            s.Offset = absoluteOffset;
            SelectedParticleMarker = s.Position;
            GizmoPivot = s.Position;
            UpdatePlaceableMarkers();
        }
        else if (SelectedPropNode is { } r)   // M699
        {
            r.Offset = absoluteOffset;
            SelectedParticleMarker = r.CurrentPosition;
            GizmoPivot = r.CurrentPosition;
            // the coordinates in the panel tick with the drag, which is how you land on a number
        SelectedPlaceableInfo = $"{r.Name}\n{r.Info}\n({r.Position.X:0}, {r.Position.Y:0}, {r.Position.Z:0})";
            UpdatePlaceableMarkers();
            RefreshPropInstanceTransforms();
        }
    }

    /// <summary>Extra local rotation for the selected particle/added-mesh (sounds are point emitters — no-op).</summary>
    public void RotateSelectedPlacementTo(System.Numerics.Vector3 rotationDegrees)
    {
        if (SelectedParticleNode is { } p) p.RotationDegrees = rotationDegrees;
        else if (SelectedAddedMesh is { } a) { a.RotationDegrees = rotationDegrees; GizmoPivot = a.PivotWorld; PublishAddedMeshPreview(); }
        else if (SelectedPropNode is { } r) { r.RotationDegrees = rotationDegrees; RefreshPropInstanceTransforms(); }   // M699
    }

    /// <summary>Extra local scale for the selected particle/added-mesh (sounds are point emitters — no-op).</summary>
    public void ScaleSelectedPlacementTo(System.Numerics.Vector3 scale)
    {
        if (SelectedParticleNode is { } p) p.Scale = scale;
        else if (SelectedAddedMesh is { } a) { a.Scale = scale; GizmoPivot = a.PivotWorld; PublishAddedMeshPreview(); }
        else if (SelectedPropNode is { } r) { r.Scale = scale; RefreshPropInstanceTransforms(); }   // M699
    }

    public void EndPlacementDrag()
    {
        RefreshPlacementDirtyFlag();   // M700
        // M76: push the whole drag as ONE undo step (no-op when nothing actually changed).
        if (_placementDragTarget is { } target)
        {
            var after = PlacementTransformCommand.State.Capture(target);
            if (after != _placementDragBefore)
                UndoService.PushApplied(new PlacementTransformCommand(target, _placementDragBefore, after, _currentMap, RefreshPlacementVisuals));
            _placementDragTarget = null;
        }
        if (SelectedParticleNode is { } p)
        {
            RebuildParticlePlayback();   // live-preview the placement's new transform once, not per frame
            _log.Info("Particles", $"'{p.Name}' → pos ({p.CurrentPosition.X:0.#}, {p.CurrentPosition.Y:0.#}, {p.CurrentPosition.Z:0.#})" +
                (p.RotationDegrees != System.Numerics.Vector3.Zero ? $" · rot ({p.RotationDegrees.X:0.#}, {p.RotationDegrees.Y:0.#}, {p.RotationDegrees.Z:0.#})°" : "") +
                (p.Scale != System.Numerics.Vector3.One ? $" · scale ({p.Scale.X:0.##}, {p.Scale.Y:0.##}, {p.Scale.Z:0.##})" : ""));
        }
        else if (SelectedSound is { } s)
            _log.Info("Sounds", $"'{s.Name}' → ({s.Position.X:0.#}, {s.Position.Y:0.#}, {s.Position.Z:0.#}). Save Placement Edits writes it to the mod.");
        else if (SelectedAddedMesh is { } a)   // M79
            _log.Info("AddMesh", $"'{a.Name}' → ({a.Offset.X:0.#}, {a.Offset.Y:0.#}, {a.Offset.Z:0.#}). Save Map Edits appends it to the mapgeo.");
    }

    // ---- M79: add imported meshes to the map ----------------------------------------------------

    public bool HasAddedMeshes => MapContent.AddedMeshes.Count > 0;

    /// <summary>Import a mesh (.obj/.scb/.sco) and queue it to be appended to the loaded map. Placed at the
    /// current gizmo/camera focus, previewed as an overlay, and movable with the transform gizmo.</summary>
    [RelayCommand]
    private async Task AddMeshToMap()
    {
        if (_currentMap is null) { _log.Warn("AddMesh", "Open a map (.mapgeo) first."); return; }
        var file = await Dialogs.OpenFileAsync("Import mesh (.mapgeo / .fbx / .glb / .gltf / .obj / .scb / .sco / .skn)",
            new Avalonia.Platform.Storage.FilePickerFileType("Mesh")
            { Patterns = Formats.Meshes.SceneFileLoader.Extensions.Select(e => "*" + e).ToArray() },
            DialogService.All);
        if (file is null) return;

        // M123: the dedicated import + setup window replaces the old direct-add flow.
        var vm = await BuildAddMeshWindowAsync();
        if (vm is null)
        { _log.Warn("AddMesh", "No shader catalogue — pick a game environment in the Materials tab first."); return; }
        vm.LoadFile(file);
        ShowAddMeshWindow?.Invoke(vm);
    }

    /// <summary>Wired by MainWindow — owns the Add Mesh window instance.</summary>
    public Action<AddMeshWindowViewModel>? ShowAddMeshWindow;

    /// <summary>
    /// M656: add one mesh off the Workshop shelf.
    ///
    /// <para>It opens the Add Mesh window on that file with that mesh ticked rather than staging it
    /// directly, because the material question is not optional and that window is where it is asked
    /// properly (M654) - copy the original out of the source map's bin, reuse one this map already has,
    /// or build one from a League shader with its samplers set up. A second, quieter path that picked a
    /// material silently is how surfaces end up drawing untextured.</para>
    /// </summary>
    private async Task<string> AddWorkshopMeshAsync(WorkshopUserMesh mesh)
    {
        if (_currentMap is null) throw new InvalidOperationException("Open a map (.mapgeo) first.");
        if (!File.Exists(mesh.FilePath))
            throw new InvalidOperationException($"'{Path.GetFileName(mesh.FilePath)}' is no longer where the shelf points.");

        var vm = await BuildAddMeshWindowAsync();
        if (vm is null) throw new InvalidOperationException(
            "No shader catalogue — pick a game environment in the Materials tab first.");

        bool exact = vm.LoadFileAndSelectOnly(mesh.FilePath, new[] { mesh.MeshName });
        if (mesh.SourceMaterialsBin is { Length: > 0 } bin && File.Exists(bin)) vm.UseSourceBin(bin);
        ShowAddMeshWindow?.Invoke(vm);
        return exact
            ? $"Opened Add Mesh on '{mesh.MeshName}'. Set its material there, then Add To Map."
            : $"Opened '{Path.GetFileName(mesh.FilePath)}', but it no longer holds a mesh called "
              + $"'{mesh.MeshName}' — pick what you want in the window.";
    }

    /// <summary>The Add Mesh window with everything the host owns already wired. Shared by the toolbar
    /// command and the Workshop shelf so the two cannot drift apart.</summary>
    private async Task<AddMeshWindowViewModel?> BuildAddMeshWindowAsync()
    {
        // M123b: new materials build from the shader catalogue, so it must be loaded.
        if (MaterialEditor.Catalog is null && MaterialEditor.SelectedShaderEnvironment is { } env)
            await LoadShaderCatalogAsync(env);
        if (MaterialEditor.Catalog is not { } cat) return null;

        var patterns = Formats.Meshes.SceneFileLoader.Extensions.Select(e => "*" + e).ToArray();
        var vm = new AddMeshWindowViewModel
        {
            ExistingMaterials = MapMaterialNames,
            ShaderChoices = cat.Shaders
                .Where(sh => sh.Category is "StaticMesh" or "Environment")
                .Select(sh => sh.Name).ToList(),
            // M514: set the textures up in the window, rather than discovering afterwards that the new
            // material points at the shader's declared default - which for DefaultEnv_Flat is
            // ASSETS/Shared/Materials/rock_texture.tex, a path that exists in no wad and no project.
            SamplersForShader = shader => cat.Find(shader) is { } def
                ? def.Textures.Select(t => (t.Name, t.DefaultTexturePath)).ToList()
                : Array.Empty<(string, string)>(),
            TextureExists = TextureExistsByPath,
        };
        vm.SetVisibilityLayers(_mapVisibility.Primary?.Layers ?? Array.Empty<VisibilityLayer>());
        vm.PickFile = async title => await Dialogs.OpenFileAsync(title,
            new Avalonia.Platform.Storage.FilePickerFileType("Mesh") { Patterns = patterns },
            DialogService.All);
        // M654: a mapgeo extracted out of a wad on its own has no sibling .materials.bin, and until now
        // that silently meant the original materials could not be carried over at all.
        vm.PickBin = async title => await Dialogs.OpenFileAsync(title,
            new Avalonia.Platform.Storage.FilePickerFileType("Materials bin")
            { Patterns = new[] { "*.bin" } },
            DialogService.All);
        vm.Confirmed = plan => _ = ExecuteAddMeshPlanAsync(plan);
        return vm;
    }

    [RelayCommand]
    private void OpenWorkshop()
    {
        string? final = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        if (final is null)
        {
            _log.Error("Workshop", "Set a valid League game folder first (Project > Set Game Folder). The Workshop indexes DATA/FINAL.");
            return;
        }

        _workshopCatalog ??= new WorkshopCatalogService(_resolver.Database, ResolveBinName);
        // M655: the user's own shelf lives beside the census of the installed game, and outlives it -
        // the catalogue cache is thrown away whenever a patch changes the wads' fingerprint.
        _workshopUserLibrary ??= new WorkshopUserLibrary();
        var vm = new WorkshopViewModel(_workshopCatalog, final)
        {
            AddMaterial = ImportWorkshopMaterialAsync,
            AddParticle = ImportWorkshopParticleAsync,
            BuildParticlePreview = BuildWorkshopParticlePreview,   // M420
            UserLibrary = _workshopUserLibrary,
            PickTroyBins = () => Dialogs.OpenFilesAsync("Import legacy .troybin effect(s)",
                new Avalonia.Platform.Storage.FilePickerFileType("Legacy particle")
                { Patterns = new[] { "*.troybin" } },
                DialogService.All),
            PickBin = async () => await Dialogs.OpenFileAsync("Import particles from a .bin",
                new Avalonia.Platform.Storage.FilePickerFileType("League bin")
                { Patterns = new[] { "*.bin" } },
                DialogService.All),
            // M656: the same shelf, for geometry.
            PickMeshFile = async () => await Dialogs.OpenFileAsync("Shelve meshes from a file",
                new Avalonia.Platform.Storage.FilePickerFileType("Mesh")
                { Patterns = Formats.Meshes.SceneFileLoader.Extensions.Select(e => "*" + e).ToArray() },
                DialogService.All),
            AddMesh = AddWorkshopMeshAsync,
        };
        ShowWorkshopWindow?.Invoke(vm);
        _ = vm.InitializeAsync();
    }

    public Action<WorkshopViewModel>? ShowWorkshopWindow;

    /// <summary>M655: kept on the host so the shelf is the same object across Workshop windows.</summary>
    private WorkshopUserLibrary? _workshopUserLibrary;

    /// <summary>
    /// M654: make sure the whole-install index exists, because it is what knows which wad any asset lives
    /// in. Cached on disk against a wad fingerprint, so the second call in a session is cheap.
    /// Returns false when there is no game folder to index - the caller decides whether that is fatal.
    /// </summary>
    private async Task<bool> EnsureWorkshopIndexAsync()
    {
        string? final = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        if (final is null) return false;
        _workshopCatalog ??= new WorkshopCatalogService(_resolver.Database, ResolveBinName);
        if (_commonMaterialCatalogTask is null
            || !string.Equals(_commonMaterialCatalogDirectory, final, StringComparison.OrdinalIgnoreCase))
        {
            _commonMaterialCatalogDirectory = final;
            _log.Info("Material", "Loading the installed patch's asset index…");
            _commonMaterialCatalogTask = Task.Run(() => _workshopCatalog.LoadAsync(final, rebuild: false));
        }
        try { await _commonMaterialCatalogTask; return true; }
        catch (Exception ex)
        {
            _commonMaterialCatalogTask = null;
            _log.Warn("Material", $"The installed patch could not be indexed: {ex.Message}");
            return false;
        }
    }

    private async Task<ShaderMaterialSetup?> LoadCommonShaderSetupAsync(string shader)
    {
        if (!await EnsureWorkshopIndexAsync()) return null;
        var catalog = await _commonMaterialCatalogTask!;
        var template = catalog.Materials.FirstOrDefault(m =>
            m.Shader.Equals(shader, StringComparison.OrdinalIgnoreCase));
        if (template is not null)
            _log.Success("Material", $"{shader}: common setup uses {template.SetupUsageCount:n0} of "
                + $"{template.ShaderUsageCount:n0} shipped material(s).");
        return template?.CommonSetup;
    }

    /// <summary>M516: after a Workshop import, put the new material on the mesh that is selected. The
    /// import already puts it in the bin; without this the user has to go and find it in a picker that,
    /// until M516, did not even list it.</summary>
    private void AssignToSelectedAddedMesh(string materialName, string source)
    {
        if (SelectedAddedMesh is not { } mesh || materialName.Length == 0) return;
        mesh.Material = materialName;
        PublishAddedMeshPreview();
        MaterialsRevision++;   // M518: the viewport, not the picker's list - see AfterMeshMaterialEdit
        _log.Success("AddMesh", $"'{mesh.Name}' now uses '{materialName}' (from {source}).");
    }

    private async Task<string> ImportWorkshopMaterialAsync(WorkshopMaterialTemplate template, string newName)
    {
        if (_currentMapEntry is not { } mapEntry || _currentMap is null)
            throw new InvalidOperationException("Open the destination map before adding a material.");
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
            throw new InvalidOperationException("The open map has no companion materials .bin.");
        if (!await EnsureProjectSavedAsync())
            throw new InvalidOperationException("Save the project before adding Workshop content.");

        byte[] target = GetAssetBytes(binEntry);
        byte[] source = _workshopCatalog?.ReadBin(template.SourceBinHash, template.SourceWad)
            ?? throw new InvalidOperationException("The template source bin is no longer available. Rebuild the Workshop catalog.");
        byte[]? imported = MapMaterialFactory.ImportMaterial(target, source, template.MaterialHash,
            template.MaterialName, newName, out var error);
        if (imported is null) throw new InvalidOperationException(error ?? "The material could not be imported.");

        var staged = StageWorkshopAssets(template.TexturePaths, mapEntry);
        if (staged.Missing.Count > 0)
            throw new InvalidOperationException("Required texture(s) were not found in the installed patch: "
                + string.Join(", ", staged.Missing.Take(4)) + (staged.Missing.Count > 4 ? "..." : ""));
        if (!await SaveMapBinBytesAsync(binEntry, imported))
            throw new InvalidOperationException("The edited materials bin could not be saved.");

        // M516: remembered across the reload below, which clears the selection.
        var meshToAssign = SelectedAddedMesh;

        FinishWorkshopMutation();
        await LoadMapGeoAsync(mapEntry);
        _log.Success("Workshop", $"Added material '{newName}' from {template.Shader} with {staged.Written} asset(s).");

        // Put it on the mesh that was selected when the import started. Only if that mesh is still staged -
        // a reload can drop it, and assigning to something no longer in the scene would be a lie.
        if (meshToAssign is not null && MapContent.AddedMeshes.Contains(meshToAssign))
        {
            SelectedAddedMesh = meshToAssign;
            AssignToSelectedAddedMesh(newName, "the Workshop");
            return $"Added '{newName}' and put it on '{meshToAssign.Name}'. {staged.Written} texture asset(s) copied.";
        }
        return $"Added '{newName}' to the current map. {staged.Written} texture asset(s) copied.";
    }

    /// <summary>
    /// M420: build a playable preview of a Workshop template, so "what am I actually adding" is
    /// answered before adding it rather than after.
    ///
    /// <para>A legacy template is converted on the spot, which means the preview shows the CONVERTED
    /// effect - the same emitters, textures, ramps and mesh bindings the import will write - and not the
    /// original. That is the honest thing to preview: the conversion is lossy in known ways, and seeing
    /// the conversion is what tells you whether it is worth adding.</para>
    /// </summary>
    private VfxPlayback? BuildWorkshopParticlePreview(WorkshopParticleTemplate template)
    {
        if (_workshopCatalog is null) return null;
        VfxSystemDefinition? def = null;
        Dictionary<string, string>? aliases = null;

        try
        {
            if (template.IsLegacy)
            {
                byte[]? troyBytes = _workshopCatalog.ReadAsset(template.SourceBinPath);
                if (troyBytes is null) return null;
                if (!Formats.Particles.TroyBinFile.TryParse(troyBytes, out var troy, out _)) return null;
                var converted = Formats.Particles.TroyBinConverter.Convert(
                    troy!, template.Name, template.ParticlePath);
                aliases = converted.Assets.ToDictionary(
                    a => a.TargetPath, a => a.SourcePath, StringComparer.OrdinalIgnoreCase);
                def = VfxSystemResolver.ExtractAll(converted.BinBytes).Values.FirstOrDefault();
            }
            else
            {
                foreach (byte[] bin in _workshopCatalog.ReadBinClosure(template.SourceBinHash, template.SourceWad))
                {
                    if (!VfxSystemResolver.ExtractAll(bin).TryGetValue(template.SystemHash, out var found)) continue;
                    def = found;
                    break;
                }
            }
        }
        catch { return null; }
        if (def is null) return null;

        // the per-emitter resolvers all read through ReadAssetByPath, so pointing that at the catalog
        // for the duration is enough to make every one of them work on a whole-install template
        var previous = _workshopPreviewAliases;
        _workshopPreviewAliases = aliases ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // a converted legacy system reuses names across templates, so a stale cache entry would show
            // the previously previewed effect's sprites
            _vfxTextureCache.Remove(def.PathHash);
            _vfxMeshCache.Remove(def.PathHash);
            _vfxSurfaceCache.Remove(def.PathHash);
            return new VfxPlayback(new[] { new VfxPlaybackItem(def, System.Numerics.Vector3.Zero,
                ResolveSystemTextures(def), ResolveSystemMeshes(def), ResolveSystemMultTextures(def),
                ResolveSystemDistortionTextures(def), ResolveSystemColorTextures(def),
                ResolveSystemErosionTextures(def), ResolveSystemPaletteTextures(def),
                emitterReflectionCubemaps: ResolveSystemReflectionCubemaps(def))
                { EmitterEmissionSurfaces = ResolveSystemEmissionSurfaces(def) } });   // M754
        }
        catch { return null; }
        finally { _workshopPreviewAliases = previous; }
    }

    private async Task<string> ImportWorkshopParticleAsync(WorkshopParticleTemplate template, string newName)
    {
        if (_currentMapEntry is not { } mapEntry || _currentMap is not { } map)
            throw new InvalidOperationException("Open the destination map before adding a particle.");
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
            throw new InvalidOperationException("The open map has no companion materials .bin.");
        if (!await EnsureProjectSavedAsync())
            throw new InvalidOperationException("Save the project before adding Workshop content.");

        byte[] target = GetAssetBytes(binEntry);

        // M419: a legacy .troybin has no source bin to copy a graph out of - it is converted into one
        // here, and the same importer then runs unchanged.
        Formats.Particles.TroyConversionResult? legacy = null;
        IReadOnlyList<byte[]> closure;
        uint systemHash = template.SystemHash;
        if (template.IsLegacy)
        {
            byte[]? troyBytes = _workshopCatalog?.ReadAsset(template.SourceBinPath)
                ?? throw new InvalidOperationException("The legacy .troybin is no longer available. Rebuild the Workshop catalog.");
            if (!Formats.Particles.TroyBinFile.TryParse(troyBytes, out var troy, out var troyError))
                throw new InvalidOperationException($"That .troybin could not be read: {troyError}");
            legacy = Formats.Particles.TroyBinConverter.Convert(troy!, newName, $"Particles/{newName}");
            closure = new[] { legacy.BinBytes };
            systemHash = legacy.SystemHash;
        }
        else
        {
            closure = _workshopCatalog?.ReadBinClosure(template.SourceBinHash, template.SourceWad)
                ?? Array.Empty<byte[]>();
            if (closure.Count == 0)
                throw new InvalidOperationException("The template source bins are no longer available. Rebuild the Workshop catalog.");
        }

        var graph = BinObjectGraphImporter.Import(target, closure, new[] { systemHash }, out var graphError)
            ?? throw new InvalidOperationException(graphError ?? "The particle object graph could not be imported.");
        var tree = SafeBinTree.Parse(graph.Bytes);
        var id = MapPlaceableWriter.NewParticleId(tree, HashAlgorithms.Fnv1a(newName));
        if (!id.IsValid)
            throw new InvalidOperationException("This map has no MapPlaceableContainer, so it cannot safely hold particle placements.");

        var transform = System.Numerics.Matrix4x4.Identity;
        transform.Translation = GizmoPivot ?? map.Center;
        var edit = new MapPlacementEdit(id)
        {
            CreateParticle = true,
            Name = newName,
            Transform = transform,
            SystemLink = systemHash,
        };
        byte[] placed = MapPlaceableWriter.WriteEdits(graph.Bytes, new[] { edit }, out var placeError)
            ?? throw new InvalidOperationException(placeError ?? "The particle placement could not be created.");

        var staged = legacy is not null
            ? StageLegacyTroyAssets(legacy.Assets, mapEntry)
            : StageWorkshopAssets(graph.AssetPaths, mapEntry, assetsMayBeTheProjectsOwn: template.IsUser);
        if (staged.Missing.Count > 0)
            throw new InvalidOperationException("Required particle asset(s) were not found"
                + (template.IsUser ? " in this project or in the installed patch: " : " in the installed patch: ")
                + string.Join(", ", staged.Missing.Take(4)) + (staged.Missing.Count > 4 ? "..." : ""));
        if (!await SaveMapBinBytesAsync(binEntry, placed))
            throw new InvalidOperationException("The edited materials bin could not be saved.");

        FinishWorkshopMutation();
        await LoadMapGeoAsync(mapEntry);
        var added = MapContent.AllParticles.FirstOrDefault(x => x.Placement.Id == id);
        if (added is not null) SelectedParticleNode = added;
        _log.Success("Workshop", $"Added particle '{newName}': {graph.ImportedObjects} object(s), {staged.Written} asset(s).");
        if (legacy is not null)
        {
            // say what did NOT come across, at the moment the user can still act on it
            _log.Warn("Workshop", $"'{newName}' was converted from a legacy .troybin. {legacy.Provenance}");
            foreach (var e in legacy.Emitters.Where(e => e.Source != Formats.Particles.TroyTextureSource.NameMatch))
                _log.Info("Workshop", e.Source == Formats.Particles.TroyTextureSource.None
                    ? $"   emitter '{e.EmitterName}' has no texture — assign one in the Particle Editor."
                    : $"   emitter '{e.EmitterName}' took '{Path.GetFileName(e.TexturePath)}' by position, not by name — check it.");
            foreach (var e in legacy.Emitters.Where(e => e.MeshPath is not null))
                _log.Info("Workshop", $"   emitter '{e.EmitterName}' renders mesh '{Path.GetFileName(e.MeshPath)}'.");
            foreach (var mesh in legacy.UnboundMeshes)
                _log.Warn("Workshop", $"   mesh '{Path.GetFileName(mesh)}' could not be matched to an emitter — "
                    + "it is staged, so set the emitter's primitive to it in the Particle Editor.");
            return $"Added '{newName}' at the viewport focus, converted from a legacy .troybin. {legacy.Provenance}";
        }
        return $"Added '{newName}' at the viewport focus. {graph.ImportedObjects} linked object(s) and {staged.Written} asset(s) imported.";
    }

    /// <param name="assetsMayBeTheProjectsOwn">M655: for an effect imported from one of the user's OWN
    /// bins, an asset that already resolves in this project is already there - it is a project file, and
    /// no game wad will ever hold it. Off for a shipped template, where a path can resolve only because a
    /// game wad is MOUNTED for reference, and skipping it would leave the mod without the asset.</param>
    private (int Written, IReadOnlyList<string> Missing) StageWorkshopAssets(
        IEnumerable<string> paths, WadAssetEntry destinationMap, bool assetsMayBeTheProjectsOwn = false)
    {
        if (_workshopCatalog is null) return (0, paths.ToArray());
        var missing = new List<string>();
        var sources = new List<(string Path, byte[] Bytes)>();
        foreach (string raw in paths.Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = raw.Trim().Replace('\\', '/').TrimStart('/');
            if (path.Length == 0 || path.Split('/').Any(part => part == "..")) { missing.Add(raw); continue; }
            if (assetsMayBeTheProjectsOwn && TextureExistsByPath(path)) continue;
            byte[]? bytes = _workshopCatalog.ReadAsset(path);
            if (bytes is null) { missing.Add(path); continue; }
            sources.Add((path, bytes));
        }
        // Preflight the complete dependency set before touching the project. A failed import should not
        // leave half of a particle's textures behind as unexplained dead files.
        if (missing.Count > 0) return (0, missing);
        return WriteStagedAssets(sources, destinationMap, missing);
    }

    /// <summary>
    /// M419: stage a converted legacy .troybin effect's assets. Same destination rules as
    /// <see cref="StageWorkshopAssets"/>, but the bytes are transcoded on the way: legacy effects
    /// reference <c>DATA/Particles/x.dds</c> while shipped modern systems reference <c>.tex</c>
    /// 2,381,029 times against 70 <c>.dds</c>, so the converted system points at a .tex and the DDS is
    /// wrapped (or re-encoded when it cannot be wrapped) here.
    /// </summary>
    /// <summary>
    /// M531: import a legacy map's particles - the systems AND where they stood.
    ///
    /// <para>The placement list is <c>Particles.dat</c>, a plain-text file beside the source room. Each
    /// distinct <c>.troybin</c> it names is converted once and imported as a VfxSystemDefinitionData;
    /// each line becomes a MapParticle placement linked to it. Positions get the SAME translation the
    /// geometry got and nothing else - legacy particle coordinates were measured to share the NVR's
    /// space (541 of Map2's 554 sit within 25 units of an NVR vertex in XZ).</para>
    ///
    /// <para>Assets come off the legacy folder tree, NOT the installed patch: these systems reference 49
    /// textures by bare .tga name, none of which exist as .tga and none of which ship in the patch at
    /// all - they are .dds split across DATA/Particles and DATA/Shared/Particles.</para>
    ///
    /// <para>Returns the edited bin, or null with the reason logged. A failure leaves the caller's bytes
    /// untouched, because a half-imported particle set is worse than none: a placement whose system link
    /// dangles is a hard error at map load.</para>
    /// </summary>
    private byte[]? ImportLegacyParticles(byte[] binBytes, string sourceFile,
        System.Numerics.Vector3 translation, WadAssetEntry destinationMap, out string summary)
    {
        summary = "";
        if (!TryFindLegacyParticleSources(sourceFile, out string dat, out var folders))
        { _log.Info("Legacy Port", "No Particles.dat beside the source room - nothing to import."); return binBytes; }

        Formats.MapGeo.LegacyParticlePortPlan plan;
        try { plan = Formats.MapGeo.LegacyParticlePorter.PlanFromDisk(dat, folders, translation); }
        catch (Exception ex) { _log.Error("Legacy Port", $"Particles.dat could not be read: {ex.Message}"); return null; }

        foreach (string warning in plan.Warnings.Take(8)) _log.Warn("Legacy Port", warning);
        if (plan.Warnings.Count > 8)
            _log.Warn("Legacy Port", $"{plan.Warnings.Count - 8:n0} additional particle warning(s) omitted.");
        if (plan.Placements.Count == 0)
        { _log.Warn("Legacy Port", "Particles.dat named no importable systems."); return binBytes; }

        // M533: a RE-PORT must REPLACE the systems its own earlier run wrote.
        //
        // BinObjectGraphImporter refuses an object whose hash already exists with DIFFERENT data, so any
        // improvement to the troybin converter turns the second port of a map into a silent particle
        // WIPE: the cleanup pass above has already removed the old placements by the time the import is
        // refused, leaving the systems in the bin and nothing pointing at them. M532 is the measured
        // case - it changed birthScale0, so systems written by an M531 build no longer compare equal.
        //
        // Removing them first is safe because the hash is Fnv1a("Particles/<Name>"), a path only this
        // porter authors. It is the same thing the port already does for its own materials with
        // RemoveGeneratedMaterials("LegacyPort/").
        var priorSystems = SafeBinTree.Parse(binBytes);
        int replaced = plan.Systems.Count(system => priorSystems.Objects.Remove(system.SystemHash));
        if (replaced > 0)
        {
            using var resetStream = new MemoryStream();
            priorSystems.Write(resetStream);
            binBytes = resetStream.ToArray();
            _log.Info("Legacy Port", $"Replacing {replaced:n0} particle system(s) from an earlier port.");
        }

        // One import for every system at once, so the graph importer resolves shared dependencies once.
        var graph = BinObjectGraphImporter.Import(binBytes,
            plan.Systems.Select(system => system.Conversion.BinBytes).ToArray(),
            plan.Systems.Select(system => system.SystemHash).ToArray(), out var graphError);
        if (graph is null)
        { _log.Error("Legacy Port", $"Particle systems could not be imported: {graphError}"); return null; }

        // Keys must be reserved as a batch. NewParticleId reads the keys the tree holds RIGHT NOW, so
        // calling it per placement hands back the same key and the write keeps only the first.
        var tree = SafeBinTree.Parse(graph.Bytes);
        var ids = MapPlaceableWriter.NewPlacementIds(tree,
            plan.Placements.Select(placement => HashAlgorithms.Fnv1a(placement.Name)));
        if (ids.Count != plan.Placements.Count)
        { _log.Error("Legacy Port", "This map has no MapPlaceableContainer, so it cannot hold particle placements."); return null; }

        var edits = plan.Placements.Select((placement, i) => new MapPlacementEdit(ids[i])
        {
            CreateParticle = true,
            Name = placement.Name,
            Transform = placement.Transform,
            SystemLink = placement.SystemHash,
        }).ToList();

        byte[]? placed = MapPlaceableWriter.WriteEdits(graph.Bytes, edits, out var placeError);
        if (placed is null)
        { _log.Error("Legacy Port", $"Particle placements could not be written: {placeError}"); return null; }
        if (!string.IsNullOrWhiteSpace(placeError)) _log.Warn("Legacy Port", placeError);

        var staged = StageLegacyParticleAssets(plan.Assets, folders, destinationMap);
        if (staged.Missing.Count > 0)
            _log.Warn("Legacy Port", $"{staged.Missing.Count:n0} particle asset(s) were not found and will render "
                + "untextured: " + string.Join(", ", staged.Missing.Take(4))
                + (staged.Missing.Count > 4 ? "..." : ""));

        summary = $"{plan.Placements.Count:n0} particle placement(s) from {plan.Systems.Count:n0} system(s), "
            + $"{staged.Written:n0} asset(s)";
        return placed;
    }


    /// <summary>
    /// M575: re-host the source client's map audio in a bank this map already loads, and place the
    /// ambience bed.
    ///
    /// <para>Two things make this different from the particle import. The legacy banks are BKHD 88 and the
    /// current client reads 145, so the audio has to be repacked rather than copied. And the legacy client
    /// stores no sound POSITIONS at all - there is no Particles.dat equivalent - so only the ambience bed
    /// can be placed automatically. Everything else arrives as a named event for the user to place.</para>
    /// </summary>
    private byte[]? ImportLegacyAudio(byte[] binBytes, string sourceFile, System.Numerics.Vector3 translation,
        WadAssetEntry mapEntry, out string summary)
    {
        summary = "";
        // LEVELS/<Map>/Scene/room.nvr - the level folder is what names the bank family.
        string levelFolder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(sourceFile)) ?? "");
        if (string.IsNullOrWhiteSpace(levelFolder))
        { _log.Warn("Legacy Port", "The source room's level folder could not be identified - no audio imported."); return binBytes; }
        string slug = Formats.MapGeo.LegacyMapPorter.SlugFor(levelFolder);

        // Which banks does this map load? Only those are worth extending, and only their media counts as
        // "the game already ships this".
        string? mapBinPath = MapBinPathFor(mapEntry.Path);
        if (mapBinPath is null || !TryResolveEntry(HashAlgorithms.WadPath(mapBinPath), out var mapBinEntry))
        {
            _log.Warn("Legacy Port", $"No map bin at '{mapBinPath ?? "?"}' - cannot tell which banks this map "
                + "loads, so no audio was imported.");
            return binBytes;
        }

        IReadOnlyList<Formats.Audio.MapBankUnit> units;
        try { units = Formats.Audio.MapAudioDeclaration.Read(GetAssetBytes(mapBinEntry)); }
        catch (Exception ex)
        { _log.Warn("Legacy Port", $"The map bin's audio block could not be read: {ex.Message}"); return binBytes; }
        if (units.Count == 0)
        { _log.Warn("Legacy Port", "This map declares no Wwise banks, so there is nothing to extend."); return binBytes; }

        var bankCache = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        byte[]? BankBytes(string assetPath)
        {
            string key = assetPath.Replace('\\', '/').ToLowerInvariant();
            if (bankCache.TryGetValue(key, out var cached)) return cached;
            byte[]? bytes = null;
            if (TryResolveEntry(HashAlgorithms.WadPath(key), out var entry))
                try { bytes = GetAssetBytes(entry); } catch { bytes = null; }
            return bankCache[key] = bytes;
        }

        var declaredMedia = new HashSet<uint>();
        foreach (string path in units.SelectMany(u => u.BankPaths).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (BankBytes(path) is not { } bytes) continue;
            if (path.EndsWith(".wpk", StringComparison.OrdinalIgnoreCase))
            {
                if (Formats.Audio.WpkFile.Parse(bytes) is { } pack)
                    foreach (uint id in pack.Wems.Keys) declaredMedia.Add(id);
            }
            else if (Formats.Audio.BnkFile.Parse(bytes) is { } bank)
                foreach (uint id in bank.Wems.Keys) declaredMedia.Add(id);
        }

        var set = Formats.Audio.LegacyAudioPorter.Read(sourceFile, levelFolder, slug, declaredMedia);
        foreach (string note in set.Notes) _log.Info("Legacy Port", note);
        if (set.IsEmpty)
        {
            _log.Info("Legacy Port", "The source client has no map audio left to port - this map already ships all of it.");
            return binBytes;
        }

        int MediaCount(string assetPath)
        {
            if (BankBytes(assetPath) is not { } bytes) return -1;
            try { return Formats.Audio.BnkFile.Parse(bytes)?.Wems.Count ?? -1; } catch { return -1; }
        }
        var host = Formats.Audio.MapAudioDeclaration.HostCandidates(units, MediaCount).FirstOrDefault();
        if (host.Events is null || BankBytes(host.Events) is not { } hostEvents || BankBytes(host.Audio) is not { } hostAudio)
        {
            _log.Warn("Legacy Port", "This map declares no readable bank pair with room for media, so the audio "
                + "was not imported.");
            return binBytes;
        }

        var sounds = set.Clips.Select(c => new Formats.Audio.WwiseBankSound(c.EventName, c.WemId, c.Wem)).ToList();
        var injected = Formats.Audio.WwiseBankInjector.Append(
            hostEvents, hostAudio, $"reyengine/{slug}", sounds, out var audioError);
        if (injected is null)
        {
            _log.Warn("Legacy Port", $"The audio was not added to '{Path.GetFileName(host.Events)}': {audioError}");
            return binBytes;
        }

        WriteBakedAsset(host.Events.Replace('\\', '/').ToLowerInvariant(), injected.EventsBank, ".bnk");
        WriteBakedAsset(host.Audio.Replace('\\', '/').ToLowerInvariant(), injected.AudioBank, ".bnk");

        // The one placement that can be made without position data. The bed is the longest MULTI-CHANNEL
        // clip: mono one-shots are emitters that belonged somewhere specific, and the source does not
        // record where. Everything else is listed below so the user can place it.
        var bed = set.Clips.Where(c => c.Info.Channels >= 2).OrderByDescending(c => c.Bytes).FirstOrDefault()
               ?? set.Clips.OrderByDescending(c => c.Bytes).First();
        string placementSummary = "";
        var ids = MapPlaceableWriter.NewPlacementIds(SafeBinTree.Parse(binBytes),
            new[] { HashAlgorithms.Fnv1a(bed.EventName) });
        if (ids.Count == 0)
            _log.Warn("Legacy Port", "This map has no MapPlaceableContainer, so the ambience could not be placed. "
                + $"Its event is '{bed.EventName}'.");
        else
        {
            var transform = System.Numerics.Matrix4x4.Identity;
            transform.Translation = (_currentMap?.Center ?? System.Numerics.Vector3.Zero) + translation;
            var edit = new MapPlacementEdit(ids[0])
            {
                CreateSound = true,
                Name = $"LegacyPort_{slug}_Ambience",
                EventName = bed.EventName,
                Transform = transform,
            };
            byte[]? placed = MapPlaceableWriter.WriteEdits(binBytes, new[] { edit }, out var placeError);
            if (placed is null) _log.Warn("Legacy Port", $"The ambience placement was not written: {placeError}");
            else
            {
                if (!string.IsNullOrWhiteSpace(placeError)) _log.Warn("Legacy Port", placeError);
                binBytes = placed;
                placementSummary = $", placed '{bed.EventName}' at the map centre";
            }
        }

        // Said at the moment the user can act on it: these are in the bank and playable, but nothing in
        // the source says where they belonged, so they stay unplaced until someone places them.
        var unplaced = injected.AddedEvents.Where(n => n != bed.EventName).ToList();
        if (unplaced.Count > 0)
            _log.Info("Legacy Port", $"{unplaced.Count:n0} further sound(s) are in the bank but NOT placed - the "
                + "legacy client stores no sound positions. Place them from the map content panel: "
                + string.Join(", ", unplaced.Take(6)) + (unplaced.Count > 6 ? ", ..." : ""));

        summary = $"{injected.AddedEvents.Count:n0} sound(s) into {Path.GetFileName(host.Audio)} "
            + $"(bank family {set.BankMapId}){placementSummary}";
        return binBytes;
    }

    /// <summary>A map's own bin - <c>data/maps/shipping/map453/map453.bin</c> for a map453 mapgeo. That is
    /// where <c>MapAudioDataProperties</c> lives; the companion materials bin has no audio block.</summary>
    private static string? MapBinPathFor(string mapGeoPath)
    {
        var m = System.Text.RegularExpressions.Regex.Match(mapGeoPath, @"map(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? $"data/maps/shipping/map{m.Groups[1].Value}/map{m.Groups[1].Value}.bin" : null;
    }

    /// <summary>The legacy folder layout: <c>LEVELS/&lt;Map&gt;/Particles.dat</c> beside the room, and the
    /// .troybin corpus under <c>DATA/Particles</c> at the client root. Both are found by walking up from
    /// the source room rather than being asked for, since the port already knows where the room is.</summary>
    private static bool TryFindLegacyParticleSources(string sourceFile, out string dat, out string[] folders)
    {
        dat = ""; folders = Array.Empty<string>();
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFile) ?? ".");

        for (int up = 0; up < 4 && dir is not null; up++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Particles.dat");
            if (!File.Exists(candidate)) continue;
            dat = candidate;

            for (var root = dir.Parent; root is not null; root = root.Parent)
            {
                string data = Path.Combine(root.FullName, "DATA");
                if (!Directory.Exists(data)) continue;
                folders = new[]
                {
                    Path.Combine(data, "Particles"),
                    Path.Combine(data, "Shared", "Particles"),
                }.Where(Directory.Exists).ToArray();
                break;
            }
            return folders.Length > 0;
        }
        return false;
    }

    /// <summary>Stage particle art out of the LEGACY folder tree. The Workshop path cannot serve this -
    /// it resolves by WAD path-hash into the installed patch, where none of these assets exist.</summary>
    private (int Written, IReadOnlyList<string> Missing) StageLegacyParticleAssets(
        IReadOnlyList<Formats.Particles.TroyAssetMapping> assets, string[] folders, WadAssetEntry destinationMap)
    {
        var index = new Formats.MapGeo.LegacyAssetIndex(folders, Formats.MapGeo.LegacyAssetIndex.ParticleExtensions);
        var missing = new List<string>();
        var sources = new List<(string Path, byte[] Bytes)>();

        foreach (var asset in assets)
        {
            string target = asset.TargetPath.Trim().Replace('\\', '/').TrimStart('/');
            if (target.Length == 0 || target.Split('/').Any(part => part == "..")) { missing.Add(asset.SourcePath); continue; }
            if (!index.TryResolve(asset.SourcePath, out string file)) { missing.Add(asset.SourcePath); continue; }

            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (IOException) { missing.Add(asset.SourcePath); continue; }

            if (asset.NeedsTexTranscode)
            {
                try
                {
                    if (!TexWriter.TryWrapDds(bytes, out var wrapped))
                        wrapped = TexWriter.Write(TextureDecoder.Decode(bytes), TexFormat.Bc3, mipmaps: true);
                    bytes = wrapped;
                }
                catch { missing.Add(asset.SourcePath); continue; }
            }
            sources.Add((target, bytes));
        }

        // Unlike the Workshop path this writes what it HAS: a missing texture makes one emitter render
        // untextured, where refusing the whole batch would lose the entire particle set.
        var written = WriteStagedAssets(sources, destinationMap, new List<string>());
        return (written.Written, missing.Concat(written.Missing).ToArray());
    }

    /// <summary>
    /// M533: what would have to change for <paramref name="macro"/> to be legal on this material.
    ///
    /// <para>ShaderPermutationIndex.SuggestFixes judges a material AS IT STANDS, and this material is
    /// currently cooked - it renders - so asking it directly returns nothing. The question is about the
    /// material WITH the macro, which means probing.</para>
    ///
    /// <para>The probe runs on a THROWAWAY copy, re-parsed from the editor's own bytes. Setting the macro
    /// on the live model and undoing it afterwards would work most of the time, and "most of the time" is
    /// not a good enough guarantee for a document the user is about to save.</para>
    /// </summary>
    private IReadOnlyList<string> SuggestMacroFixes(Formats.Materials.MaterialBinding material, string macro)
    {
        if (ShaderPerms() is not { IsAvailable: true } perms) return Array.Empty<string>();
        try
        {
            if (MaterialEditor.Serialize() is not { } bytes) return Array.Empty<string>();
            var copy = Formats.Materials.MaterialDocument.Parse(bytes, ResolveBinName, ResolveWadPath);
            var probe = copy?.Materials.FirstOrDefault(m =>
                string.Equals(m.Name, material.Name, StringComparison.OrdinalIgnoreCase));
            if (probe is null || probe.SetMacro(macro, true) is null) return Array.Empty<string>();
            return perms.SuggestFixes(probe);
        }
        catch (Exception ex)
        {
            _log.Info("Material", $"Could not work out what {macro} would need: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private (int Written, IReadOnlyList<string> Missing) StageLegacyTroyAssets(
        IReadOnlyList<Formats.Particles.TroyAssetMapping> assets, WadAssetEntry destinationMap)
    {
        if (_workshopCatalog is null) return (0, assets.Select(a => a.SourcePath).ToArray());
        var missing = new List<string>();
        var sources = new List<(string Path, byte[] Bytes)>();
        foreach (var asset in assets)
        {
            string source = asset.SourcePath.Trim().Replace('\\', '/').TrimStart('/');
            string target = asset.TargetPath.Trim().Replace('\\', '/').TrimStart('/');
            if (target.Length == 0 || target.Split('/').Any(part => part == "..")) { missing.Add(source); continue; }
            byte[]? bytes = _workshopCatalog.ReadAsset(source);
            if (bytes is null) { missing.Add(source); continue; }

            if (asset.NeedsTexTranscode)
            {
                try
                {
                    // the legacy-map porter's route (M141): wrap the DDS when its format is already one
                    // the .tex container can carry, and only pay for a full re-encode when it is not
                    if (!TexWriter.TryWrapDds(bytes, out var wrapped))
                        wrapped = TexWriter.Write(TextureDecoder.Decode(bytes), TexFormat.Bc3, mipmaps: true);
                    bytes = wrapped;
                }
                catch { missing.Add(source); continue; }
            }
            sources.Add((target, bytes));
        }
        if (missing.Count > 0) return (0, missing);
        return WriteStagedAssets(sources, destinationMap, missing);
    }

    /// <summary>The write half of asset staging, shared by the modern and legacy paths.</summary>
    /// <param name="overwrite">M697: replace a file the project already has at that path. Off for every
    /// Workshop import - an imported texture must never clobber the user's own edit of it - and on for
    /// the Character Creator, which authored every path it writes and would otherwise report success
    /// while leaving the previous attempt's bins in place.</param>
    private (int Written, IReadOnlyList<string> Missing) WriteStagedAssets(
        IReadOnlyList<(string Path, byte[] Bytes)> sources, WadAssetEntry destinationMap, List<string> missing,
        bool overwrite = false)
    {
        int written = 0;
        foreach (var (path, bytes) in sources)
        {
            ulong hash = HashAlgorithms.WadPath(path);

            if (Project.IsFolderProject && Project.RootPath is { } root)
            {
                string folder = RiotWadFolderName(destinationMap);
                string baseDir = Path.GetFullPath(Path.Combine(root, folder));
                string file = Path.GetFullPath(Path.Combine(baseDir, path.Replace('/', Path.DirectorySeparatorChar)));
                if (!file.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                { missing.Add(path); continue; }
                if (!overwrite && File.Exists(file)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, bytes);
                if (!Project.ProjectFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) Project.ProjectFolders.Add(folder);
                ClearShadowOverride(hash, Path.GetExtension(path));
                written++;
                continue;
            }

            if (!overwrite && _overrides.TryGet(hash, out var existing) && File.Exists(existing.OverrideFile)) continue;
            string stored = ProjectWorkspace.StoreOverrideBytes(Project, hash, bytes, Path.GetExtension(path));
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = hash,
                ResolvedPath = path,
                OverrideFile = stored,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            written++;
        }
        return (written, missing);
    }

    private void FinishWorkshopMutation()
    {
        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        BuildMounts();
        BuildProjectTree();
        UpdateTitle();
    }

    /// <summary>M123: run the confirmed plan — create the new materials in the map's .materials.bin,
    /// bring imported textures into the project as plain DDS, then stage every included mesh.</summary>
    private async Task ExecuteAddMeshPlanAsync(AddMeshPlan plan)
    {
        if (_currentMap is not { } map || _currentMapEntry is not { } mapEntry) return;
        try
        {
            // M512: 0) materials copied verbatim out of the source map's own bin. Done first and as its
            // own step because nothing about them is derived - the shader, samplers, macros and render
            // state are Riot's, and the permutation is cooked by definition because the game ships it.
            var toCopy = plan.Materials.Where(m => m.CopyFromBin is { Length: > 0 }).ToList();
            if (toCopy.Count > 0)
            {
                if (!TryResolveMaterialsBin(mapEntry.Path, out var copyTarget))
                { _log.Error("AddMesh", "No materials .bin found for this map — cannot copy materials."); return; }
                var targetBytes = GetAssetBytes(copyTarget);
                if (targetBytes is null) { _log.Error("AddMesh", "Could not read the materials .bin."); return; }

                var copiedTextures = new List<string>();
                foreach (var m in toCopy)
                {
                    byte[] sourceBytes;
                    try { sourceBytes = File.ReadAllBytes(m.CopyFromBin!); }
                    catch (Exception ex)
                    { _log.Error("AddMesh", $"Source bin {m.CopyFromBin}: {ex.Message}"); return; }

                    uint hash = ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a(m.CopyFromMaterial!);
                    var imported = MapMaterialFactory.ImportMaterial(targetBytes, sourceBytes, hash,
                        m.CopyFromMaterial!, m.NewName!, out var copyError);
                    if (imported is null)
                    { _log.Error("AddMesh", $"Material '{m.CopyFromMaterial}': {copyError}"); return; }
                    targetBytes = imported;
                    copiedTextures.AddRange(TexturePathsOfMaterial(sourceBytes, m.CopyFromMaterial!));
                    _log.Success("AddMesh", $"Copied material '{m.CopyFromMaterial}' from "
                        + $"{Path.GetFileName(m.CopyFromBin)} as '{m.NewName}'.");
                }
                // M654: the copy points at the SOURCE map's textures, which are in the source map's wad
                // and nowhere near this project. Without bringing them across the material lands
                // structurally perfect and draws untextured - which is exactly what "the original
                // material was not carried over" looks like from the outside.
                await StageCopiedMaterialTexturesAsync(copiedTextures, mapEntry);
                if (!await SaveMapBinBytesAsync(copyTarget, targetBytes))
                { _log.Error("AddMesh", "Could not save the materials .bin — meshes were NOT staged."); return; }
            }

            // 1) new materials (cloned templates), textures first so the clone can point at them
            var toCreate = plan.Materials.Where(m => m.CreateNew).ToList();
            if (toCreate.Count > 0)
            {
                if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
                { _log.Error("AddMesh", "No materials .bin found for this map — cannot create materials."); return; }
                var binBytes = GetAssetBytes(binEntry);
                if (binBytes is null) { _log.Error("AddMesh", "Could not read the materials .bin."); return; }
                _log.Info("AddMesh", $"Materials bin: {binEntry.Path} ({binEntry.SourceKind}, {binBytes.Length:n0} bytes).");

                foreach (var m in toCreate)
                {
                    string? diffusePath = null;
                    if (m.TextureBytes is not null)
                        diffusePath = SaveImportedTexture(m.TextureBytes, m.TextureFileNameHint ?? m.NewName!);

                    var def = MaterialEditor.Catalog?.Find(m.ShaderPath);
                    if (def is null) { _log.Error("AddMesh", $"Material '{m.NewName}': shader '{m.ShaderPath}' not in the catalogue."); return; }
                    var commonSetup = await LoadCommonShaderSetupAsync(def.Name);
                    if (commonSetup is not null) def = def with { CommonSetup = commonSetup };
                    // M514: the sampler paths set up in the window, with the imported texture still
                    // winning for the diffuse when there was one.
                    var samplerOverrides = m.SamplerPaths is { Count: > 0 }
                        ? new Dictionary<string, string>(m.SamplerPaths, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (diffusePath is not null) samplerOverrides["__diffuse__"] = diffusePath;

                    var newBytes = MapMaterialFactory.CreateFromShader(binBytes, m.NewName!, def, out var err,
                        samplerOverrides.Count > 0 ? samplerOverrides : null);
                    if (newBytes is null)
                    {
                        // dump the exact input so the failure is reproducible offline
                        try
                        {
                            var dump = Path.Combine(Path.GetTempPath(), "reyengine_addmesh_fail.bin");
                            File.WriteAllBytes(dump, binBytes);
                            _log.Error("AddMesh", $"Material '{m.NewName}': {err} — input dumped to {dump}");
                        }
                        catch { _log.Error("AddMesh", $"Material '{m.NewName}': {err}"); }
                        return;
                    }
                    binBytes = newBytes;
                    _log.Success("AddMesh", $"Material '{m.NewName}' built from shader {m.ShaderPath}"
                        + (commonSetup is not null ? " using the most-used Riot setup" : "")
                        + (diffusePath is not null ? $" with diffuse {diffusePath}" : "") + ".");
                }
                if (!await SaveMapBinBytesAsync(binEntry, binBytes))
                { _log.Error("AddMesh", "Could not save the materials .bin — meshes were NOT staged."); return; }
            }

            // 2) stage the meshes at the camera/gizmo focus with their chosen materials + layer mask
            var place = GizmoPivot ?? map.Center;
            int staged = 0;
            foreach (var mesh in plan.Meshes)
            {
                var (cmin, cmax) = BoundsOf(mesh.Positions);
                var center = (cmin + cmax) * 0.5f;
                var material = plan.MeshMaterialNames.TryGetValue(mesh.MaterialName, out var mn) && mn.Length > 0
                    ? mn : DefaultMapMaterial();
                var vm = new AddedMapMeshViewModel
                {
                    Name = mesh.Name,
                    Positions = mesh.Positions, Normals = mesh.Normals, Uvs = mesh.Uvs,
                    Indices = mesh.Indices, LocalCenter = center,
                    Material = material,
                    Offset = place - center,
                    VisibilityMask = plan.VisibilityMask,
                    EnabledVisibilityMask = plan.VisibilityMask,
                    StateChanged = OnMapContentItemStateChanged,
                };
                MapContent.AddedMeshes.Add(vm);
                staged++;
            }
            OnPropertyChanged(nameof(HasAddedMeshes));
            PublishAddedMeshPreview();
            if (MapContent.AddedMeshes.Count > 0)
                SelectedOutlinerItem = MapContent.AddedMeshes[^1];   // M123e: routes to selection -> gizmo
            _log.Success("AddMesh", $"Staged {staged} mesh(es) (layer mask 0b{Convert.ToString(plan.VisibilityMask & 0xFF, 2).PadLeft(8, '0')}). "
                + "Position them with the gizmo, then Save Map Edits.");
        }
        catch (Exception ex) { _log.Error("AddMesh", ex.Message); }
    }

    /// <summary>
    /// M654: every texture a material in <paramref name="bin"/> references, by path.
    ///
    /// <para>Read through <see cref="Formats.Materials.MaterialDocument"/> rather than by scanning for
    /// strings, because since patch 16.17 the reference is a WadChunkLink and not a string at all
    /// (M590) - a string scan would find nothing in any bin the game currently ships.</para>
    /// </summary>
    private IReadOnlyList<string> TexturePathsOfMaterial(byte[] bin, string materialName)
    {
        try
        {
            var doc = Formats.Materials.MaterialDocument.Parse(bin, ResolveBinName, ResolveWadPath);
            var binding = doc.Materials.FirstOrDefault(m =>
                m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));
            if (binding is null) return Array.Empty<string>();
            return binding.Slots.Select(x => x.Path).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        }
        catch (Exception ex)
        {
            _log.Warn("AddMesh", $"Could not read '{materialName}' texture list: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// M654: copy a copied material's textures into the project.
    ///
    /// <para>Staged one at a time on purpose. <see cref="StageWorkshopAssets"/> preflights the whole set
    /// and writes nothing when any single asset is missing, which is right for a particle graph whose
    /// dependencies must all land together - but wrong here, where a material with nine of its ten
    /// textures is strictly better than one with none, and the tenth is worth naming rather than
    /// silently blocking the rest.</para>
    /// </summary>
    private async Task<int> StageCopiedMaterialTexturesAsync(IEnumerable<string> paths, WadAssetEntry mapEntry)
    {
        var wanted = paths.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => !TextureExistsByPath(x))     // already reachable here - nothing to bring across
            .ToList();
        if (wanted.Count == 0) return 0;

        if (!await EnsureWorkshopIndexAsync())
        {
            _log.Warn("AddMesh", $"{wanted.Count:n0} texture(s) of the copied material(s) are not in this "
                + "project and no game folder is set, so they were not brought across: "
                + string.Join(", ", wanted.Take(4)) + (wanted.Count > 4 ? " …" : ""));
            return 0;
        }

        int written = 0;
        var missing = new List<string>();
        foreach (string path in wanted)
        {
            var staged = StageWorkshopAssets(new[] { path }, mapEntry);
            written += staged.Written;
            missing.AddRange(staged.Missing);
        }
        if (written > 0)
            _log.Success("AddMesh", $"Brought {written:n0} texture(s) across with the copied material(s).");
        if (missing.Count > 0)
            _log.Warn("AddMesh", $"{missing.Count:n0} texture(s) of the copied material(s) are not in the "
                + "installed patch, so those surfaces will draw untextured: "
                + string.Join(", ", missing.Take(4)) + (missing.Count > 4 ? " …" : ""));
        return written;
    }

    /// <summary>Decode a png/jpg blob and write it into the project folder as an uncompressed DDS.
    /// Returns the WAD path the material should reference, or null on failure.</summary>
    private string? SaveImportedTexture(byte[] imageBytes, string nameHint)
    {
        try
        {
            var mount = ProjectFolderMounts.FirstOrDefault();
            if (mount is null) { _log.Warn("AddMesh", "No project folder — imported texture skipped."); return null; }

            using var ms = new MemoryStream(imageBytes, writable: false);
            var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
            int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
            var bgra = new byte[w * h * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { bmp.CopyPixels(new Avalonia.PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), bgra.Length, w * 4); }
            finally { handle.Free(); }
            for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);

            var clean = new string(nameHint.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
            string rel = $"assets/maps/reyimported/{clean.ToLowerInvariant()}.dds";
            string dest = Path.Combine(mount.Location, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, MapMaterialFactory.WriteDds(w, h, bgra));
            _log.Success("AddMesh", $"Imported texture saved: {rel} ({w}x{h}).");
            return rel.ToUpperInvariant().StartsWith("ASSETS") ? "ASSETS" + rel[6..] : rel;
        }
        catch (Exception ex) { _log.Warn("AddMesh", $"Imported texture failed: {ex.Message}"); return null; }
    }

    [RelayCommand]
    private void RemoveAddedMesh(AddedMapMeshViewModel? vm)
    {
        if (vm is null) return;
        MapContent.AddedMeshes.Remove(vm);
        if (ReferenceEquals(SelectedAddedMesh, vm)) { SelectedAddedMesh = null; GizmoPivot = null; }
        OnPropertyChanged(nameof(HasAddedMeshes));
        PublishAddedMeshPreview();
        _log.Info("AddMesh", $"Removed '{vm.Name}' from the add queue.");
    }

    private (float[]? Pos, float[]? Nrm, float[]? Uv, int[]? Idx) ImportMeshFile(string file)
    {
        if (file.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
        {
            var m = Formats.Meshes.ObjMeshImporter.Import(File.ReadAllText(file), Path.GetFileName(file));
            return m is null ? default : (m.Positions, m.Normals, m.Uvs, m.Indices);
        }
        // .scb / .sco → triangle soup (no normals; the appender synthesises none, so pass null → flat up)
        var sm = Formats.Meshes.StaticObjectDecoder.Decode(File.ReadAllBytes(file), file);
        if (sm is null) return default;
        return (sm.Positions, null, sm.Uvs, System.Array.ConvertAll(sm.Indices, i => (int)i));
    }

    private static (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max) BoundsOf(float[] pos)
    {
        var min = new System.Numerics.Vector3(float.MaxValue);
        var max = new System.Numerics.Vector3(float.MinValue);
        for (int i = 0; i + 2 < pos.Length; i += 3)
        {
            min = System.Numerics.Vector3.Min(min, new(pos[i], pos[i + 1], pos[i + 2]));
            max = System.Numerics.Vector3.Max(max, new(pos[i], pos[i + 1], pos[i + 2]));
        }
        return (min, max);
    }

    /// <summary>Pick a sensible default material for a new mesh: the first opaque map material, else the first.</summary>
    private string DefaultMapMaterial()
    {
        if (_currentMap is not { } map || map.Groups.Count == 0) return "";
        var opaque = map.Groups.FirstOrDefault(g => g.Material.Length > 0
            && _currentMapProfiles?.GetValueOrDefault(g.Material) is { RenderMode: MaterialRenderMode.Opaque });
        return (opaque ?? map.Groups.First(g => g.Material.Length > 0)).Material;
    }

    private List<string>? _mapMaterialNames;

    /// <summary>
    /// Every material this map can use — for the inspector's picker on an added mesh, and for the Add Mesh
    /// window's "use an existing material".
    ///
    /// <para>M516: read from the materials.bin, which is the thing that DEFINES materials. It used to be
    /// built from the mapgeo's groups, i.e. from the materials existing geometry already REFERENCES — so a
    /// material you had just created (Add Mesh, the Workshop, an import) was in the bin, used by nothing,
    /// and therefore absent from the only list you could pick from. The mesh kept a name the picker could
    /// not offer and the inspector could not show, which is what "I cannot apply any material to this
    /// mesh" looked like.</para>
    ///
    /// <para>The group materials are unioned in on purpose: a name the geometry uses and the bin has lost
    /// stays visible, because hiding it would hide the problem too.</para>
    /// </summary>
    public IReadOnlyList<string> MapMaterialNames
    {
        get
        {
            if (_mapMaterialNames is not null) return _mapMaterialNames;

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_currentMapEntry is { } entry && TryResolveMaterialsBin(entry.Path, out var binEntry))
                try
                {
                    foreach (var m in Formats.Materials.MaterialDocument
                                 .Parse(ReadAsset(binEntry.PathHash), ResolveBinName).Materials)
                        if (m.Name.Length > 0) names.Add(m.Name);
                }
                catch { /* unreadable bin - the group names below are still worth offering */ }

            if (_currentMap is { } map)
                foreach (var g in map.Groups)
                    if (g.Material.Length > 0) names.Add(g.Material);

            return _mapMaterialNames = names.ToList();
        }
    }

    /// <summary>Re-read the material list. Cheap to call: the parse only happens on the next read.</summary>
    private void InvalidateMapMaterialNames()
    {
        _mapMaterialNames = null;
        OnPropertyChanged(nameof(MapMaterialNames));
        OnPropertyChanged(nameof(AddedMeshMaterialMissing));
    }

    /// <summary>M516: the selected added mesh names a material this map does not have. Silent until now —
    /// the mesh drew with a fallback and the inspector showed an empty list.</summary>
    public bool AddedMeshMaterialMissing =>
        SelectedAddedMesh is { Material.Length: > 0 } mesh
        && !MapMaterialNames.Contains(mesh.Material, StringComparer.OrdinalIgnoreCase);

    /// <summary>Publish the added meshes as a preview overlay (combined with the prop overlay).</summary>
    private void PublishAddedMeshPreview()
    {
        var instances = new List<PropInstanceData>(_propInstances);
        foreach (var a in MapContent.AddedMeshes)
        {
            if (!a.IsEditorVisible || a.IsDisabled || a.IsRemoved) continue;
            var mesh = new PropMesh(a.Name + "|" + a.Indices.Length,
                a.Positions, a.Normals, a.Uvs, System.Array.ConvertAll(a.Indices, i => (uint)i),
                new[] { new PropSubmesh(0, a.Indices.Length, null) });
            instances.Add(new PropInstanceData(mesh, a.Transform));
        }
        CurrentPropMeshes = instances.Count > 0 ? new PropRenderSet(instances) : null;
    }
    private IReadOnlyList<PropInstanceData> _propInstances = System.Array.Empty<PropInstanceData>();

    /// <summary>M699: the placement each prop instance was built from, in instance order.</summary>
    private IReadOnlyList<AnimatedPropViewModel> _propInstanceOwners = System.Array.Empty<AnimatedPropViewModel>();

    /// <summary>
    /// M699: move the already-decoded prop instances to where their placements now are.
    ///
    /// <para>A drag runs every frame, and rebuilding the render set means decoding every mesh and texture
    /// again - seconds on a map with 94 placements. The meshes do not change when a prop moves, only the
    /// matrices do, so the instances are rebuilt from the meshes they already hold. The skin's own scale
    /// goes back under the placement through the same Place() the build uses, so a dragged prop keeps the
    /// size it had.</para>
    /// </summary>
    private void RefreshPropInstanceTransforms()
    {
        if (_propInstanceOwners.Count != _propInstances.Count) return;   // a rebuild is in flight - it will carry the move
        var updated = new PropInstanceData[_propInstances.Count];
        for (int i = 0; i < updated.Length; i++)
            updated[i] = PropInstanceData.Place(_propInstances[i].Mesh, _propInstanceOwners[i].CurrentTransform);
        _propInstances = updated;
        PublishAddedMeshPreview();   // props + added meshes, republished together
    }
    [ObservableProperty] private IReadOnlyList<ViewportMeshRenderer.SubmeshMaterial>? _currentModelSubmeshMaterials; // M32
    [ObservableProperty] private TextureImage? _currentGrassTint;                    // M78: map grass-tint texture
    [ObservableProperty] private System.Numerics.Vector4 _currentGrassTintRect;      // M78: minX, minZ, 1/spanX, 1/spanZ
    [ObservableProperty] private bool _hasFlowmapWater; // M44: current map has flowmap-river water → viewport animates it
    public ParticleEditorViewModel ParticleEditor { get; } = new(); // M46 Particle Editor
    [ObservableProperty] private bool _isParticleEditorActive;      // M46: overlay visible for the active tab
    [ObservableProperty] private double _currentLightmapScale = 1.0; // M45: MapSunProperties.lightMapColorScale
    [ObservableProperty] private MapSunProperties? _currentSunProperties;
    // M145: the fog toggle's visibility follows whichever map is loaded.
    partial void OnCurrentSunPropertiesChanged(MapSunProperties? value)
    {
        OnPropertyChanged(nameof(HasMapFog));   // M759: the viewport toggle keeps its state across maps
    }
    [ObservableProperty] private AnimationClip? _currentAnimation;
    [ObservableProperty] private double _animationTime;
    /// <summary>M248 (phase 6, step 1): render the viewport with Direct3D 11 instead of OpenGL.
    ///
    /// <para>Off by default and deliberately reversible. The OpenGL path is the only reference for what the
    /// editor used to look like, so it stays until the D3D11 one is trusted - deleting it would remove the
    /// ability to A/B a regression, which is the whole point of having both.</para></summary>
    [ObservableProperty] private bool _useDx11Viewport;

    /// <summary>What the D3D11 surface is doing, for the status bar. Empty when it is not running.</summary>
    [ObservableProperty] private string _dx11ViewportStatus = "";

    /// <summary>M263: the frame cost line is now just the milliseconds, as asked. The draw/cull/unbound
    /// detail moves here and shows on hover - it is the diagnostic that found M229, M230, M255 and M261,
    /// so it is worth keeping reachable even when it is not worth staring at.</summary>
    [ObservableProperty] private string _dx11ViewportDetail = "";

    /// <summary>M263: drives the TIME constant. Pausing freezes the clock where it is rather than resetting
    /// it, so this holds a moment rather than jumping back to frame zero.</summary>
    [ObservableProperty] private bool _animationsPlaying = true;

    /// <summary>
    /// M460: the D3D11 glow buffer and bloom chain. On by default, and DX11-only.
    ///
    /// <para>Not a new effect. Riot's environment pixel shaders - the ones this viewport already runs -
    /// write their glow to <c>SV_Target1</c>, and with a single render target bound it was discarded every
    /// frame (docs/research/frame-pipeline.md §3.3). Turning this off restores that: one render target,
    /// no chain, no composite, which is the A/B.</para>
    ///
    /// <para>Measured before it was built: over all 206 shipped map material bins, 116 of 8,714 drawn
    /// materials resolve to a permutation that writes a computed glow - so on most maps the difference is
    /// confined to lanterns, glowsigns and emissive props, and on a map with none of those there is
    /// nothing to see either way.</para>
    /// </summary>
    [ObservableProperty] private bool _showBloom = true;

    /// <summary>
    /// M465: the sun shadow map. DX11 only.
    ///
    /// <para>Not a new effect either. Every environment pixel shader this viewport runs already samples
    /// <c>SHADOW_MAP_DEPTH_PCF_SharedTexture</c> with five <c>SampleCmpLevelZero</c> taps
    /// (docs/research/light-system.md §1.7); until this milestone they sampled a 1x1 white texel, so
    /// nothing was ever in shadow. Turning this off restores that stand-in, which is the A/B.</para>
    ///
    /// <para>The gain is bounded and worth stating: a baked map's shader takes
    /// <c>shadow = min(pcf, bakedLightmap.a)</c>, so wherever the bake already carries a shadow this
    /// cannot darken it further. What it adds is props, anything the bake omits, and maps whose bake is
    /// stale or absent.</para>
    /// </summary>
    [ObservableProperty] private bool _showSunShadows = true;

    /// <summary>
    /// M462: hide every piece of editor decoration at once, so the viewport shows only what the GAME draws.
    ///
    /// <para>The distinction that defines the set: a toggle is decoration when it draws something the
    /// client never renders — placement icons, markers, debug grids, wireframe, bounds, bones, the
    /// selection outline. It is NOT decoration when it changes the rendered image itself: lightmaps,
    /// dynamic lights, fog and bloom all stay exactly as the user left them, because turning those off
    /// would make the viewport match the game LESS, which is the opposite of the point.</para>
    ///
    /// <para><c>ShowParticles</c> is in the hidden set despite its name: it toggles the MapParticle
    /// placement MARKERS, not the particle systems themselves (see its tooltip). Prop MESHES
    /// (<c>ShowPropMeshes</c>) are real geometry and stay.</para>
    ///
    /// <para>Every hidden toggle is restored to whatever it was on the way out, so this is a view mode and
    /// never a destructive edit to the user's setup.</para>
    /// </summary>
    [ObservableProperty] private bool _gameMode;

    /// <summary>The decoration toggles as they were before Game Mode turned them off. Null when off.</summary>
    private bool[]? _preGameModeToggles;

    partial void OnGameModeChanged(bool value)
    {
        if (value)
        {
            // Capture first, then clear - the setters below fire their own OnChanged handlers.
            _preGameModeToggles = new[]
            {
                ShowSoundIcons, ShowPropIcons, ShowParticles, ShowPlaceables, ShowLightMarkers,
                ShowBucketGrid, ShowBakeBox, ShowBounds, ShowBones, ShowWireframe,
            };
            ShowSoundIcons = ShowPropIcons = ShowParticles = ShowPlaceables = ShowLightMarkers = false;
            ShowBucketGrid = ShowBakeBox = ShowBounds = ShowBones = ShowWireframe = false;
            _log.Info("Viewport", "Game Mode: editor icons, markers and overlays hidden. "
                                + "Lighting, fog and bloom are untouched - this only removes decoration.");
        }
        else if (_preGameModeToggles is { Length: 10 } p)
        {
            ShowSoundIcons = p[0]; ShowPropIcons = p[1]; ShowParticles = p[2]; ShowPlaceables = p[3];
            ShowLightMarkers = p[4]; ShowBucketGrid = p[5]; ShowBakeBox = p[6]; ShowBounds = p[7];
            ShowBones = p[8]; ShowWireframe = p[9];
            _preGameModeToggles = null;
            _log.Info("Viewport", "Game Mode off - editor overlays restored.");
        }
        // The selection outline is drawn from SelectedSubmeshIndices, so it is suppressed by the binding
        // below rather than by clearing the selection - losing the user's selection on a view toggle would
        // be a destructive side effect of looking at something.
        OnPropertyChanged(nameof(HighlightSubmeshesForViewport));
    }

    /// <summary>What the viewport actually outlines: the selection, unless Game Mode is hiding decoration.
    /// The selection itself is preserved either way.</summary>
    public IReadOnlyList<int>? HighlightSubmeshesForViewport => GameMode ? null : SelectedSubmeshIndices;

    partial void OnSelectedSubmeshIndicesChanged(IReadOnlyList<int>? value) =>
        OnPropertyChanged(nameof(HighlightSubmeshesForViewport));

    [ObservableProperty] private bool _showWireframe;
    [NotifyPropertyChangedFor(nameof(ActiveOverlayCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveOverlays))]
    [NotifyPropertyChangedFor(nameof(OverlayBadge))]
    [ObservableProperty] private bool _showBones;
    [NotifyPropertyChangedFor(nameof(ActiveOverlayCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveOverlays))]
    [NotifyPropertyChangedFor(nameof(OverlayBadge))]
    [ObservableProperty] private bool _showBounds;
    [ObservableProperty] private bool _cullBackfaces = true; // M34: respect per-material cullEnable by default (off = force all two-sided)

    /// <summary>
    /// M557: render map transparents with the CLIENT's depth rules instead of the editor's kinder ones.
    ///
    /// <para>Off by default. On, transparents keep the depth mask and sort with everything else - which is
    /// what the game does, and what makes a decal stamp depth at its own plane, depth-reject the ground it
    /// should composite over, and come out BLACK. A diagnostic for "looks right here, wrong in game", not
    /// an authoring mode.</para>
    /// </summary>
    [ObservableProperty] private bool _clientDepthRules;

    partial void OnClientDepthRulesChanged(bool value)
    {
        Services.Dx11SceneBuilder.EmulateClientDepthRules = value;
        _log.Info("Viewport", value
            ? "Client depth rules ON - transparents keep the depth mask and sort with solid geometry, as the "
              + "game does. A decal that goes black in game should go black here too."
            : "Client depth rules OFF - transparents draw after solid geometry without writing depth.");
        NotifyMaterialsChanged();   // the DX11 scene bakes the depth state per material
    }

    [ObservableProperty] private bool _showLightmaps = true; // M69: baked lightmaps on by default; off = sun/sky fallback lighting
    // M70: legacy Riot dynamic point lights (Light.dat)
    [ObservableProperty] private bool _showDynamicLights;

    // M158: viewport lighting-mode preset. A convenience over the two flags above so the user can flip
    // between how a bake will look (Baked) and how the live editable lighting looks (Dynamic), plus a
    // debug view with both. -1 = "custom" (the individual toggles were flipped by hand, no preset owns
    // the current state). Setting a mode drives ShowLightmaps + ShowDynamicLights; flipping either flag
    // by hand resets the mode to custom rather than fighting the user.
    public const int LightingModeCustom = -1, LightingModeDynamic = 0, LightingModeBaked = 1, LightingModeCombined = 2;
    private bool _applyingLightingMode;
    [ObservableProperty] private int _lightingMode = LightingModeBaked;

    partial void OnLightingModeChanged(int value)
    {
        if (value < 0) return;   // custom: leave the flags as the user set them
        _applyingLightingMode = true;
        // Dynamic  = fallback sun/sky + editable point lights (no baked atlas) — the live authoring view.
        // Baked    = baked atlas only, point lights off — how the map ships after a bake.
        // Combined = both, a debug overlay to compare baked vs dynamic.
        ShowLightmaps = value != LightingModeDynamic;
        ShowDynamicLights = value != LightingModeBaked;
        _applyingLightingMode = false;
        OnPropertyChanged(nameof(IsLightingDynamic));
        OnPropertyChanged(nameof(IsLightingBaked));
        OnPropertyChanged(nameof(IsLightingCombined));
    }

    // Bindable one-per-mode flags for a segmented ToggleButton group in the toolbar.
    public bool IsLightingDynamic { get => LightingMode == LightingModeDynamic; set { if (value) LightingMode = LightingModeDynamic; } }
    public bool IsLightingBaked { get => LightingMode == LightingModeBaked; set { if (value) LightingMode = LightingModeBaked; } }
    public bool IsLightingCombined { get => LightingMode == LightingModeCombined; set { if (value) LightingMode = LightingModeCombined; } }

    partial void OnShowLightmapsChanged(bool value) => DropLightingPreset();
    partial void OnShowDynamicLightsChanged(bool value) => DropLightingPreset();
    private void DropLightingPreset()
    {
        if (_applyingLightingMode) return;
        if (LightingMode != LightingModeCustom)
        {
            LightingMode = LightingModeCustom;
            OnPropertyChanged(nameof(IsLightingDynamic));
            OnPropertyChanged(nameof(IsLightingBaked));
            OnPropertyChanged(nameof(IsLightingCombined));
        }
    }
    // M145: MapSunProperties distance fog. Off by default; only meaningful when the loaded map's sun
    // component authored a real fog range, which HasMapFog reflects so the toggle can hide itself.
    // M759: on by default now that it is the game's own fog - off, the viewport shows a map the game
    // never draws. HasMapFog follows the map's fogEnabled, which the panel edits.
    [ObservableProperty] private bool _showFog = true;
    public bool HasMapFog => CurrentSunProperties is { FogEnabled: true };
    [ObservableProperty] private double _dynamicLightIntensity = 1.0;
    [ObservableProperty] private double _dynamicLightRadiusScale = 1.0;   // M71: global light-radius multiplier

    /// <summary>
    /// M659: draw the placement icons THROUGH geometry. Off by default, which is the change - markers
    /// used to be drawn with no depth test at all, so a particle behind a wall or under the terrain
    /// showed anyway and a busy map read as a cloud of icons belonging to nothing visible.
    /// </summary>
    [ObservableProperty] private bool _iconsThroughWalls;

    /// <summary>M659: a wire ball at each dynamic point light showing how far it reaches. Off by default:
    /// a map with many lights is a lot of circles, and this is a thing you switch on to answer a
    /// question.</summary>
    [ObservableProperty] private bool _showLightRanges;
    // M160/M457: point-light falloff shape (0 = Riot's own linear 1-t, 1 = the legacy wide (1-t^2)^2).
    // Kept in sync with BakeSettings.FalloffSoftness so the Dynamic preview and the bake draw the same
    // pools. Defaults to 0 now that index 0 IS Riot's curve.
    [ObservableProperty] private double _lightFalloffSoftness;
    [ObservableProperty] private double _dynamicLightPositionScale = 1.0; // M71: master light-position spread (XZ)
    [ObservableProperty] private double _dynamicLightScaleX = 1.0;        // M71: per-axis fine scale
    [ObservableProperty] private double _dynamicLightScaleZ = 1.0;
    [ObservableProperty] private double _dynamicLightOffsetX = 0.0;       // M71: world-space translate
    [ObservableProperty] private double _dynamicLightOffsetZ = 0.0;
    [ObservableProperty] private IReadOnlyList<PointLight>? _dynamicLights;
    [ObservableProperty] private string? _dynamicLightsStatus;
    [ObservableProperty] private bool _hasDynamicLights;

    // ---- M152: place and edit the point lights, then save the table back ----

    /// <summary>The editable light set — M153: this IS MapContent.Lights, so the outliner's "Lights"
    /// folder and the renderer stay one source of truth. DynamicLights (what the viewport draws) is
    /// republished from it on every change, so edits are live.</summary>
    public ObservableCollection<PointLightViewModel> EditableLights => MapContent.Lights;
    [ObservableProperty] private PointLightViewModel? _selectedLight;
    [ObservableProperty] private string? _lightDatPath;      // where Save writes back to
    public bool HasSelectedLight => SelectedLight is not null;

    partial void OnSelectedLightChanged(PointLightViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedLight));
        GizmoPivot = value?.Position;   // M75 gizmo drives the selected light
    }

    /// <summary>
    /// M446 (D): surface the map's own <c>MapDynamicPointLight</c> placements in the light panel, so a
    /// light authored into the bin is visible and editable in the viewport instead of only appearing in
    /// game. Without this the editor showed one set of lights and the client rendered another.
    ///
    /// <para>Deduped on position+radius against what is already listed — the same rule the bake uses —
    /// because a ported light legitimately exists both in the panel and in the bin, and importing it twice
    /// would show (and later write back) a duplicate.</para>
    /// </summary>
    /// <returns>How many placements were added to the panel.</returns>
    public int ImportPlacedLightsIntoPanel(string? mapGeoPath)
    {
        int added = 0;
        foreach (var placed in ReadPlacedPointLights(mapGeoPath))
        {
            if (EditableLights.Any(vm =>
            {
                var pl = vm.ToPointLight();
                return System.Numerics.Vector3.Distance(pl.Position, placed.Position) < 1f
                       && Math.Abs(pl.Radius - placed.Radius) < 1f;
            })) continue;

            EditableLights.Add(new PointLightViewModel(
                new PointLight(placed.Position, placed.Color, placed.Radius, placed.IntensityScale), this)
            { Name = string.IsNullOrWhiteSpace(placed.Name) ? $"Placed {EditableLights.Count + 1}" : placed.Name });
            added++;
        }
        if (added > 0)
        {
            RepublishLights();
            _log.Info("Lights", $"{added} light(s) placed in the map are now in the light list. "
                              + "Toggle 'Lights' in the viewport toolbar to see them.");
        }
        return added;
    }

    /// <summary>Rebuild the render list from the editable set (called after any add/edit/delete).</summary>
    public void RepublishLights()
    {
        DynamicLights = EditableLights.Select(l => l.ToPointLight()).ToList();
        HasDynamicLights = EditableLights.Count > 0;
        DynamicLightsStatus = EditableLights.Count == 0
            ? "no point lights"
            : $"{EditableLights.Count} point light(s)" + (LightDatPath is { } p ? $" — {Path.GetFileName(p)}" : " — unsaved");
        // M287: every light edit funnels through here - add, delete, duplicate, gizmo drag, inspector
        // field - so one capture here covers all of them without hooking each one.
        CaptureMapLighting();
    }

    /// <summary>M287: true while a map load or a restore is writing the lighting properties, so the change
    /// notifications that follow do NOT capture. Without it the reset that ApplySunProperties performs on
    /// every map open would be captured as the user's authored state and overwrite the very record the
    /// restore is about to read - the save would destroy itself, once per map load.</summary>
    private bool _applyingLighting;

    /// <summary>Copy the current lighting into the project record for the open map. In memory only: this
    /// runs on every gizmo drag and every slider tick, and writing JSON at that rate would be a file
    /// write per frame. The disk write happens on the discrete commands via PersistMapLighting, and any
    /// other project save picks this up because IsDirty is set here.</summary>
    private void CaptureMapLighting()
    {
        if (_applyingLighting || _currentMapEntry is not { } entry) return;

        var rec = Project.MapLighting.FirstOrDefault(r => r.PathHash == entry.PathHash);
        if (rec is null)
        {
            rec = new ReyEngine.Core.Projects.MapLightingRecord
            { PathHash = entry.PathHash, MapgeoPath = entry.Path };
            Project.MapLighting.Add(rec);
        }
        rec.MapgeoPath = entry.Path;

        rec.SunIntensity = SunIntensity;
        rec.SunColorR = SunColorR; rec.SunColorG = SunColorG; rec.SunColorB = SunColorB;
        rec.SkyIntensity = SkyIntensity;
        rec.SkyColorR = SkyColorR; rec.SkyColorG = SkyColorG; rec.SkyColorB = SkyColorB;
        rec.LightmapScale = CurrentLightmapScale;

        // M463: the six added fields. Always written, so once a record has been touched by this build it
        // carries real values rather than the "predates the field" null.
        rec.SunDirX = SunDirX; rec.SunDirY = SunDirY; rec.SunDirZ = SunDirZ;
        rec.HorizonColorR = HorizonColorR; rec.HorizonColorG = HorizonColorG; rec.HorizonColorB = HorizonColorB;
        rec.GroundColorR = GroundColorR; rec.GroundColorG = GroundColorG; rec.GroundColorB = GroundColorB;
        rec.FogColorR = FogColorR; rec.FogColorG = FogColorG; rec.FogColorB = FogColorB;
        rec.FogStartRaw = FogStartRaw; rec.FogEndRaw = FogEndRaw;
        rec.FogEnabled = MapFogEnabled;   // M759
        rec.FogAltColorR = FogAltColorR; rec.FogAltColorG = FogAltColorG; rec.FogAltColorB = FogAltColorB;
        rec.FogEmissiveRemap = FogEmissiveRemap; rec.FogLowQualityEmissiveRemap = FogLowQualityEmissiveRemap;

        rec.LightIntensity = DynamicLightIntensity;
        rec.LightRadiusScale = DynamicLightRadiusScale;
        rec.FalloffSoftness = LightFalloffSoftness;
        rec.PositionScale = DynamicLightPositionScale;
        rec.ScaleX = DynamicLightScaleX;
        rec.ScaleZ = DynamicLightScaleZ;
        rec.OffsetX = DynamicLightOffsetX;
        rec.OffsetZ = DynamicLightOffsetZ;
        rec.LightDatPath = LightDatPath;

        rec.Lights = EditableLights.Select(l => new ReyEngine.Core.Projects.SavedPointLight
        {
            X = l.X, Y = l.Y, Z = l.Z,
            R = l.R, G = l.G, B = l.B,
            Radius = l.Radius, Intensity = l.Intensity, Name = l.Name,
        }).ToList();

        Project.IsDirty = true;
    }

    /// <summary>Capture and write. For the discrete edits - adding, deleting or importing lights - where
    /// losing the change to a crash would be worse than one JSON write.</summary>
    private void PersistMapLighting()
    {
        CaptureMapLighting();
        if (Project.ProjectFilePath is { } p) ReyProjectService.Save(Project, p);
    }

    /// <summary>M287: put the user's authored lighting back after a map load. MUST run after
    /// ApplySunProperties, which unconditionally resets sun/sky to the map's own values and SunIntensity to
    /// 1.0 - that reset is what made every edit look like it had never been made.</summary>
    private void RestoreMapLighting(Core.Assets.WadAssetEntry entry)
    {
        if (Project.MapLighting.FirstOrDefault(r => r.PathHash == entry.PathHash) is not { } rec) return;

        // M515: heal a record the capture bug wrote. Its sun block is the renderer's no-sun fallback to
        // 1e-9, which is not something anyone dials in - and restoring it would put that fallback back
        // over the sun this map actually authors, which is the reported symptom.
        if (_baseSunAuthored is not null && Core.Projects.MapLightingArtefact.LooksLikeUntouchedFallback(rec))
        {
            _log.Info("Lighting", "The saved lighting for this map is the editor's default sun, not "
                                + "something authored - keeping the map's own sun. Adjust the sliders and "
                                + "it will be remembered from here on.");
            RestoreMapPointLights(rec);      // the LIGHTS in that record are real; only the sun was junk
            return;
        }

        _applyingLighting = true;
        try
        {
            _suppressSunRebuild = true;
            SunIntensity = rec.SunIntensity;
            SunColorR = rec.SunColorR; SunColorG = rec.SunColorG; SunColorB = rec.SunColorB;
            SkyIntensity = rec.SkyIntensity;
            SkyColorR = rec.SkyColorR; SkyColorG = rec.SkyColorG; SkyColorB = rec.SkyColorB;

            // M463: null = a record written before these fields existed. Leaving the property alone then
            // keeps what ApplySunProperties just read out of the map, which is the only safe reading of
            // "this project has no opinion" - restoring a 0 would blank an authored sun direction.
            if (rec.SunDirX is { } sdx) SunDirX = sdx;
            if (rec.SunDirY is { } sdy) SunDirY = sdy;
            if (rec.SunDirZ is { } sdz) SunDirZ = sdz;
            if (rec.HorizonColorR is { } hr) HorizonColorR = hr;
            if (rec.HorizonColorG is { } hg) HorizonColorG = hg;
            if (rec.HorizonColorB is { } hb) HorizonColorB = hb;
            if (rec.GroundColorR is { } gr) GroundColorR = gr;
            if (rec.GroundColorG is { } gg) GroundColorG = gg;
            if (rec.GroundColorB is { } gb) GroundColorB = gb;
            if (rec.FogColorR is { } fr) FogColorR = fr;
            if (rec.FogColorG is { } fg) FogColorG = fg;
            if (rec.FogColorB is { } fb) FogColorB = fb;
            if (rec.FogStartRaw is { } fs) FogStartRaw = fs;
            if (rec.FogEndRaw is { } fe) FogEndRaw = fe;
            if (rec.FogEnabled is { } fon) MapFogEnabled = fon;   // M759: null = the record predates the field
            if (rec.FogAltColorR is { } ar) FogAltColorR = ar;
            if (rec.FogAltColorG is { } ag) FogAltColorG = ag;
            if (rec.FogAltColorB is { } ab) FogAltColorB = ab;
            if (rec.FogEmissiveRemap is { } er) FogEmissiveRemap = er;
            if (rec.FogLowQualityEmissiveRemap is { } lq) FogLowQualityEmissiveRemap = lq;

            _suppressSunRebuild = false;
            RebuildSun();

            ApplyStoredPointLights(rec);
        }
        finally { _applyingLighting = false; }

        RepublishLights();
        _log.Info("Lights", $"Restored this map's saved lighting — {rec.Lights.Count} point light(s)"
            + (rec.LightDatPath is { } p ? $", from {Path.GetFileName(p)}" : ""));
    }

    /// <summary>M515: the point-light half of a stored record, on its own. A record whose SUN block is the
    /// capture bug's artefact still holds real lights — the user placed those — so the two halves have to
    /// be restorable separately.</summary>
    private void RestoreMapPointLights(ReyEngine.Core.Projects.MapLightingRecord rec)
    {
        _applyingLighting = true;
        try { ApplyStoredPointLights(rec); }
        finally { _applyingLighting = false; }

        RepublishLights();
        if (rec.Lights.Count > 0)
            _log.Info("Lights", $"Restored {rec.Lights.Count} saved point light(s)"
                + (rec.LightDatPath is { } p ? $", from {Path.GetFileName(p)}" : "") + ".");
    }

    private void ApplyStoredPointLights(ReyEngine.Core.Projects.MapLightingRecord rec)
    {
        CurrentLightmapScale = rec.LightmapScale;
        DynamicLightIntensity = rec.LightIntensity;
        DynamicLightRadiusScale = rec.LightRadiusScale;
        LightFalloffSoftness = rec.FalloffSoftness;
        DynamicLightPositionScale = rec.PositionScale;
        DynamicLightScaleX = rec.ScaleX;
        DynamicLightScaleZ = rec.ScaleZ;
        DynamicLightOffsetX = rec.OffsetX;
        DynamicLightOffsetZ = rec.OffsetZ;
        LightDatPath = rec.LightDatPath;

        EditableLights.Clear();
        foreach (var s in rec.Lights)
            EditableLights.Add(new PointLightViewModel(
                PointLightViewModel.FromStored(s.X, s.Y, s.Z, s.R, s.G, s.B, s.Radius, s.Intensity), this)
            { Name = s.Name });
        SelectedLight = null;
    }

    private void LoadEditableLights(IEnumerable<PointLight> lights)
    {
        EditableLights.Clear();
        int n = 1;
        foreach (var l in lights)
            EditableLights.Add(new PointLightViewModel(l, this) { Name = $"Light {n++}" });
        SelectedLight = null;
        RepublishLights();
        PersistMapLighting();   // M287: an import is a discrete edit worth a write
    }

    /// <summary>Add a light at the camera's focus so it lands in view rather than at the origin.</summary>
    [RelayCommand]
    private void AddLight()
    {
        var at = GizmoPivot ?? SelectedParticleMarker ?? System.Numerics.Vector3.Zero;
        var vm = new PointLightViewModel(new PointLight(at, new System.Numerics.Vector3(1f, 0.85f, 0.6f), 600f), this)
        { Name = $"Light {EditableLights.Count + 1}" };
        EditableLights.Add(vm);
        SelectedLight = vm;
        ShowDynamicLights = true;
        RepublishLights();
        PersistMapLighting();   // M287
        _log.Info("Lights", $"Added a point light at ({at.X:0}, {at.Y:0}, {at.Z:0}). Drag the gizmo to place it.");
    }

    /// <summary>M289: the lights picked in the Lighting window's table. The outliner still drives the
    /// single <see cref="SelectedLight"/> that the inspector edits; this is the separate, list-shaped
    /// selection that exists so a 374-light Light.dat can be pruned without 374 clicks.</summary>
    public ObservableCollection<PointLightViewModel> SelectedLights { get; } = new();

    /// <summary>Delete every light in the table selection, falling back to the single outliner selection
    /// so the button does the obvious thing whichever way the user picked a light.</summary>
    [RelayCommand]
    private void DeleteSelectedLights()
    {
        var doomed = SelectedLights.Count > 0
            ? SelectedLights.ToList()
            : SelectedLight is { } one ? new List<PointLightViewModel> { one } : new List<PointLightViewModel>();
        if (doomed.Count == 0) return;

        foreach (var l in doomed) EditableLights.Remove(l);
        SelectedLights.Clear();
        SelectedLight = null;
        RepublishLights();
        PersistMapLighting();
        _log.Info("Lights", $"Deleted {doomed.Count} point light(s); {EditableLights.Count} left.");
    }

    /// <summary>Empty the table. Separate from the multi-delete because "remove these six" and "throw the
    /// whole imported .dat away" are different intents, and making the second one reachable only by
    /// select-all is how people delete more than they meant to.</summary>
    [RelayCommand]
    private void ClearAllLights()
    {
        int n = EditableLights.Count;
        if (n == 0) return;
        EditableLights.Clear();
        SelectedLights.Clear();
        SelectedLight = null;
        RepublishLights();
        PersistMapLighting();
        _log.Info("Lights", $"Removed all {n} point light(s).");
    }

    [RelayCommand]
    private void DeleteLight()
    {
        if (SelectedLight is not { } l) return;
        EditableLights.Remove(l);
        SelectedLights.Remove(l);
        SelectedLight = null;
        RepublishLights();
        PersistMapLighting();   // M287
    }

    [RelayCommand]
    private void DuplicateLight()
    {
        if (SelectedLight is not { } l) return;
        var copy = new PointLightViewModel(l.ToPointLight(), this);
        copy.X += 100;   // offset so the copy is visibly separate
        EditableLights.Add(copy);
        SelectedLight = copy;
        RepublishLights();
        PersistMapLighting();   // M287
    }

    /// <summary>Write the table back out in Riot's Light.dat format.</summary>
    [RelayCommand]
    private async Task SaveLightDat()
    {
        if (EditableLights.Count == 0) { _log.Warn("Lights", "No lights to save."); return; }
        string? target = LightDatPath;
        if (target is null)
        {
            target = await Dialogs.SaveFileAsync("Save Light.dat", "Light.dat");
            if (target is null) return;
        }
        try
        {
            await File.WriteAllBytesAsync(target, LightDatFile.Write(EditableLights.Select(l => l.ToPointLight())));
            LightDatPath = target;
            RepublishLights();
            _log.Success("Lights", $"Saved {EditableLights.Count} point light(s) to {Path.GetFileName(target)}.");
        }
        catch (Exception ex) { _log.Error("Lights", $"Save failed: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task SaveLightDatAs()
    {
        LightDatPath = null;
        await SaveLightDatCommand.ExecuteAsync(null);
    }

    // ---- M158: light baking ---------------------------------------------------------------------

    /// <summary>Set by the view so the ViewModel can open the (non-modal) Light Baking window.</summary>
    public Action? ShowLightBakeWindow { get; set; }

    /// <summary>M169: opens the Lighting window (was a 240px flyout on the viewport toolbar).</summary>
    public Action? ShowLightingWindow { get; set; }

    [RelayCommand]
    private void OpenLighting() => ShowLightingWindow?.Invoke();

    /// <summary>True when the loaded map actually has a lightmap layout to bake into. A lightmap-less
    /// mapgeo, or a legacy NVR map (which loads into MeshPreview, never into _currentMap), has nothing to
    /// re-light, so the command stays disabled.</summary>
    public bool CanBakeLighting => _currentMap is { } m && _currentMapEntry is not null
                                   && Formats.Baking.LightBaker.CanBakeExistingLayout(m);

    [RelayCommand]
    private void OpenLightBake()
    {
        // Deliberately NOT gated on CanBakeLighting: a map with no lightmap layout is exactly the case
        // where the window is most needed, because that is where a layout gets generated. Gating this on
        // "can already bake" made the layout generator unreachable on the maps that need it.
        if (!HasMapForLayout)
        {
            _log.Warn("Bake", "Open a mapgeo first.");
            return;
        }
        ShowLightBakeWindow?.Invoke();
    }

    /// <summary>
    /// M448: strip the baked-lightmap references from the mapgeo, returning it to its pre-bake state.
    ///
    /// <para>Clears every mesh's <c>BakedLight</c> channel — the atlas path and its UV scale/bias. That is
    /// the reference the client follows to a lightmap texture, so with it gone the map lights dynamically
    /// again and a fresh bake starts from a clean slate instead of layering on stale atlas assignments.</para>
    ///
    /// <para><b>References only.</b> The generated atlas <c>.tex</c> files are left on disk untouched:
    /// deleting project assets is not something a cleanup button should do silently, and the references are
    /// what actually change rendering. <c>StationaryLight</c> is also left alone — it is a different
    /// channel feeding <c>STATIONARY_LIGHT__TX</c>, not the lightmap.</para>
    /// </summary>
    [RelayCommand]
    private async Task CleanupLightmaps()
    {
        if (_currentMap is null || _currentMapBytes is null || _currentMapEntry is not { } entry)
        { _log.Warn("Bake", "No map is open."); return; }
        if (MapGeoWriter.HasMoves(_currentMap.Meshes) || MapGeoLayerWriter.HasEdits(_currentMap.Meshes)
            || MapContent.AddedMeshes.Count > 0)
        { _log.Warn("Bake", "Save your pending mesh edits first — this rewrites the mapgeo from the saved bytes."); return; }
        if (!GuardEditable(entry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            var source = _currentMapBytes;
            var extended = ExtendedChannelMaterialsFor(entry.Path);
            var (bytes, cleared) = await Task.Run(() =>
            {
                if (!Formats.MapGeo.MapGeoBinary.TryReadEditable(source, out var map, extended))
                    return ((byte[]?)null, 0);
                int n = 0;
                foreach (var mesh in map.Meshes)
                {
                    if (mesh.BakedLight.Texture.Length == 0 && mesh.BakedLight.Scale == System.Numerics.Vector2.Zero
                        && mesh.BakedLight.Bias == System.Numerics.Vector2.Zero) continue;
                    map.SetBakedLight(mesh, "", System.Numerics.Vector2.Zero, System.Numerics.Vector2.Zero);
                    n++;
                }
                return (n > 0 ? map.Write() : null, n);
            });

            if (cleared == 0) { _log.Info("Bake", "No mesh carries a baked-lightmap reference — nothing to clean up."); return; }
            if (bytes is null) { _log.Error("Bake", "This mapgeo does not round-trip byte-exactly, so it was not rewritten."); return; }

            // Validate BEFORE saving: it must re-read with the same material set AND still decode.
            if (!await Task.Run(() => Formats.MapGeo.MapGeoBinary.TryReadEditable(bytes, out _, extended)))
            { _log.Error("Bake", "The rewritten mapgeo did not re-read cleanly — not saved."); return; }
            await Task.Run(() => MapGeoDecoder.Decode(bytes, extended));

            string savedTo;
            if (TryWriteToProjectFile(entry, bytes, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, ".mapgeo");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = entry.PathHash,
                    ResolvedPath = entry.IsResolved ? entry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(entry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            _log.Success("Bake", $"Cleared the lightmap reference on {cleared:n0} mesh(es). The atlas .tex files "
                               + $"were left on disk. Saved to {savedTo} ({bytes.Length:n0} bytes).");
            await LoadMapGeoAsync(entry);
        }
        catch (Exception ex) { _log.Error("Bake", "Lightmaps could not be cleaned up: " + ex.Message); }
    }

    /// <summary>The value <see cref="ApplyBulkLightIntensityCommand"/> writes to every light.</summary>
    [ObservableProperty] private double _bulkLightIntensity = 5;

    /// <summary>Added to every light's Y by <see cref="ApplyBulkLightHeightOffsetCommand"/>.</summary>
    [ObservableProperty] private double _bulkLightHeightOffset = 100;

    /// <summary>
    /// M449: raise or lower every light together.
    ///
    /// <para>The fit panel offsets X and Z only — <c>LightPositionOffset</c> is a Vector2 and the bake's
    /// <c>ResolvePosition</c> has no Y term at all, so height could never be adjusted in bulk. This edits
    /// each light's own Y instead of adding a render-time term, which is the form that survives the port
    /// (M447) and reaches the game.</para>
    /// </summary>
    [RelayCommand]
    private void ApplyBulkLightHeightOffset()
    {
        if (EditableLights.Count == 0) { _log.Warn("Lights", "There are no lights to move."); return; }
        double dy = BulkLightHeightOffset;
        if (Math.Abs(dy) < 1e-6) { _log.Info("Lights", "Height offset is zero — nothing to do."); return; }
        foreach (var l in EditableLights) l.Y += dy;
        RepublishLights();
        _log.Success("Lights", $"Moved {EditableLights.Count:n0} light(s) by "
            + $"{dy.ToString("+0.###;-0.###", CultureInfo.InvariantCulture)} on Y.");
    }

    /// <summary>
    /// M449: set the lighting mode on EVERY material in the map at once.
    ///
    /// <para><c>NO_BAKED_LIGHTING</c> on = the material stops sampling its baked lightmap, which is the
    /// state the point lights were observed working in. Off = it uses the baked lightmap again.</para>
    ///
    /// <para><b>BOTH directions are guarded</b>, because either one asks the client for a define set it may
    /// never have cooked. Clearing: on Map11/base_srx 20 of 184 materials have no permutation without the
    /// macro, and clearing them blindly made the client log "Unable to find correct hash for shader" and
    /// render nothing (M166). Setting: M486 authored it across 78 DefaultEnv_Flat_AlphaTest materials and got
    /// the same error — that direction was unguarded until M491. Materials the game cooked no shader for are
    /// left as they are and reported, in whichever direction was asked.</para>
    /// </summary>
    [RelayCommand]
    private async Task SetAllMaterialsUnlit() => await SetAllMaterialsNoBakedLighting(true);

    /// <summary>The counterpart to <see cref="SetAllMaterialsUnlitCommand"/> — back to baked lightmaps.</summary>
    [RelayCommand]
    private async Task SetAllMaterialsBaked() => await SetAllMaterialsNoBakedLighting(false);

    /// <summary>
    /// M530: set the map's lighting mode in one step.
    ///
    /// <para>The two halves already existed but sat as separate buttons inside the Lighting window, and
    /// the one that matters most - baking - was a third place to go afterwards. This makes the choice
    /// the thing you pick: dynamic lights, the lightmaps you already have, or lightmaps baked now.</para>
    ///
    /// <para>The per-material permutation guard is unchanged and still does the refusing; a mode only
    /// says which direction to ask for.</para>
    /// </summary>
    [RelayCommand]
    private async Task SetMapLightingMode(string? mode)
    {
        if (!Enum.TryParse<Formats.Materials.MapLightingMode>(mode, ignoreCase: true, out var chosen))
        { _log.Warn("Materials", $"Unknown lighting mode '{mode}'."); return; }

        var plan = Formats.Materials.MapLightingPlan.For(chosen);
        _log.Info("Materials", $"Lighting mode: {plan.Label}. {plan.Explanation}");

        await SetAllMaterialsNoBakedLighting(plan.NoBakedLighting);

        // The bake is what turns "the materials want a lightmap" into "there is one". It opens its own
        // window rather than running blind, because a bake has settings and takes real time - and for a
        // map with no lightmap layout it offers to generate one first.
        if (plan.RunBake) OpenLightBake();
    }

    private async Task SetAllMaterialsNoBakedLighting(bool unlit)
    {
        if (_currentMapEntry is not { } entry)
        { _log.Warn("Materials", "No map is open."); return; }
        if (!TryResolveMaterialsBin(entry.Path, out var binEntry))
        { _log.Error("Materials", "No materials.bin was found alongside this mapgeo."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            byte[] source = ReadAsset(binEntry.PathHash);
            var doc = Formats.Materials.MaterialDocument.Parse(source, ResolveBinName, ResolveWadPath);
            var perms = ShaderPerms();
            bool canValidate = perms is not null && perms.IsAvailable;
            if (!unlit && !canValidate)
            { _log.Error("Materials", "No shader cache found (set the game folder) — refusing to clear "
                                    + "NO_BAKED_LIGHTING blindly, it can ask for a permutation Riot never cooked."); return; }

            int changed = 0, refused = 0, companion = 0, opaque = 0;
            foreach (var m in doc.Materials)
            {
                if (unlit)
                {
                    // M491: ADDING the macro asks the client for a define set just as much as clearing it
                    // does, and this direction had no check at all. M486 authored NO_BAKED_LIGHTING=1 across
                    // 78 DefaultEnv_Flat_AlphaTest materials and League answered "Unable to find correct hash
                    // for shader ... in wad" + "Failed to compile shader" - a map that renders nothing.
                    if (!canValidate)
                    {
                        if (m.SetMacro(Formats.Materials.MaterialBinding.MacroNoBakedLighting, true) is not null) changed++;
                        continue;
                    }

                    // M588: refusing was all this ever did, and on a ported map it refused EVERYTHING -
                    // all 88 materials of the Halloween Map453, because NO_BAKED_LIGHTING alone is not a
                    // cooked permutation on DefaultEnv_Flat_AlphaTest. SuggestFixes has been printing the
                    // way through ("add MULTIPLY_ALPHA=1") the whole time with no caller acting on it.
                    var outcome = Formats.Materials.NoBakedLightingFix.Apply(m, perms!);
                    if (outcome.Changed()) changed++;
                    else if (!outcome.Succeeded()) refused++;
                    if (outcome == Formats.Materials.NoBakedLightingOutcome.SetWithCompanion) companion++;
                    if (outcome == Formats.Materials.NoBakedLightingOutcome.RefusedOpaque) opaque++;
                }
                else if (!perms!.CanRemoveMacro(m, Formats.Materials.MaterialBinding.MacroNoBakedLighting)) refused++;
                else if (m.RemoveMacro(Formats.Materials.MaterialBinding.MacroNoBakedLighting)) changed++;
            }
            // The refusal reads in opposite directions: clearing leaves the macro ON, setting leaves it OFF.
            string refusedNote = refused == 0 ? "" : unlit
                ? $" {refused:n0} were left baked (the game ships no cooked permutation for them WITH the macro"
                  + (opaque > 0 ? $"; {opaque:n0} of those are opaque, where the companion switch would darken them" : "")
                  + ")."
                : $" {refused:n0} kept the macro (no cooked permutation without it).";
            // Said out loud because it changes render state, not just a define: the companion makes the
            // shader output premultiplied and the pass's source blend factor moves to One to absorb it.
            if (companion > 0)
                refusedNote += $" {companion:n0} needed MULTIPLY_ALPHA to have a cooked shader at all, and had "
                             + "their source blend factor moved to One so the premultiplied output composites "
                             + "the same as before.";
            if (unlit && !canValidate)
                _log.Warn("Materials", "No shader cache found (set the game folder) — writing NO_BAKED_LIGHTING "
                                     + "without checking that the game cooked a shader for it.");

            if (changed == 0)
            { _log.Info("Materials", $"Nothing to change — every material is already {(unlit ? "unlit" : "baked")}."
                                     + refusedNote); return; }

            byte[] bytes = doc.Serialize();
            var issues = Formats.Meta.ModShapeValidator.ValidateBin(
                Formats.Meta.SafeBinTree.Parse(bytes), bytes, ResolveBinName);
            if (issues.Count > 0)
            {
                foreach (var i in issues.Take(5)) _log.Error("Materials", $"[{i.Category}] {i.ObjectName}: {i.Detail}");
                _log.Error("Materials", $"{issues.Count} shape issue(s) — not saved."); return;
            }

            string savedTo;
            if (TryWriteToProjectFile(binEntry, bytes, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, bytes, ".bin");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = binEntry.PathHash,
                    ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            _log.Success("Materials", $"{(unlit ? "Set" : "Cleared")} NO_BAKED_LIGHTING on {changed:n0} material(s)."
                + refusedNote + $" Saved to {savedTo}.");
        }
        catch (Exception ex) { _log.Error("Materials", "Material lighting mode could not be set: " + ex.Message); }
    }

    /// <summary>
    /// M588: an imported mesh renders here and is INVISIBLE in game, and say so at the moment it is added.
    ///
    /// <para><see cref="MapGeoMeshAppender"/> writes Position/Normal/Texcoord0 and no Texcoord7, which is
    /// the honest thing to do — a fabricated lightmap coordinate would be worse than none. But the shaders
    /// the porter assigns sample the baked lightmap unless <c>NO_BAKED_LIGHTING</c> is set, and the two
    /// families want DIFFERENT vertex layouts: measured across all 224 cooked vertex permutations of
    /// DefaultEnv_Flat_AlphaTest, 128 read <c>POSITION NORMAL TEXCOORD0 TEXCOORD7</c> and 96 read
    /// <c>POSITION NORMAL TEXCOORD0</c>. The client cannot complete the first layout from a mesh with no
    /// Texcoord7 and skips the draw; our renderer builds its layout from what the mesh HAS, so the same
    /// mesh looks perfect here. Nothing said so, and a whole map's river went missing over it.</para>
    ///
    /// <para>Reports rather than fixes: the material may be shared with meshes that DO have the UV, and
    /// marking it unlit would unlight those. The fix is one click away in Lighting ▸ Dynamic lights.</para>
    /// </summary>
    private void WarnIfAddedMeshesWillNotDrawInGame(IEnumerable<string> materials)
    {
        var names = materials.Where(m => !string.IsNullOrWhiteSpace(m))
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0 || _currentMapEntry is not { } entry) return;
        if (!TryResolveMaterialsBin(entry.Path, out var binEntry)) return;

        try
        {
            var doc = Formats.Materials.MaterialDocument.Parse(ReadAsset(binEntry.PathHash), ResolveBinName, ResolveWadPath);
            foreach (string name in names)
            {
                var m = doc.Materials.FirstOrDefault(x =>
                    x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (m is null) continue;
                if (m.MacroOn(Formats.Materials.MaterialBinding.MacroNoBakedLighting)) continue;

                _log.Warn("MapGeo", $"'{name}' samples the baked lightmap, and an imported mesh carries no "
                    + "Texcoord7 to sample it with — the client will skip the draw and the mesh will be "
                    + "INVISIBLE in game, while rendering normally here. Fix it with Lighting ▸ Dynamic "
                    + "lights, or set NO_BAKED_LIGHTING on that material.");
            }
        }
        catch (Exception ex) { _log.Info("MapGeo", "Could not check the added meshes' lighting: " + ex.Message); }
    }

    /// <summary>
    /// M447: set the per-light strength on EVERY light at once.
    ///
    /// <para>The existing strength control is <see cref="DynamicLightIntensity"/>, which is a global
    /// multiplier applied at RENDER and BAKE time — it never reaches the bin, so turning it down dimmed the
    /// viewport and the ported map stayed bright. This writes each light's own
    /// <c>intensityScale</c> instead, which is the field the port persists and the game reads.</para>
    /// </summary>
    [RelayCommand]
    private void ApplyBulkLightIntensity()
    {
        if (EditableLights.Count == 0) { _log.Warn("Lights", "There are no lights to change."); return; }
        double v = Math.Max(0, BulkLightIntensity);
        foreach (var l in EditableLights) l.Intensity = v;
        RepublishLights();
        _log.Success("Lights", $"Set intensity {v.ToString(CultureInfo.InvariantCulture)} on "
                             + $"{EditableLights.Count:n0} light(s). Port them into the map to apply in game.");
    }

    /// <summary>
    /// M446 (C): write the editor's point lights into the map as <c>MapDynamicPointLight</c> placements —
    /// the bridge off the legacy light systems.
    ///
    /// <para><b>Why this is the port.</b> Light.dat lights (and anything else in the light panel) live only
    /// in the editor and the project file; the game never sees them, so a legacy map's lighting could only
    /// ever be BAKED in. Written as placements they become real dynamic lights the client renders with no
    /// bake at all — verified in game — and part B makes the baker read them too, so the same list now
    /// drives both paths.</para>
    ///
    /// <para>Existing placements are REPLACED, not appended: running this twice must not double the
    /// lights, and the editor's list is the authority for what the map should contain.</para>
    /// </summary>
    [RelayCommand]
    private async Task PortLightsToMap()
    {
        if (_currentMapEntry is not { } entry)
        { _log.Warn("Lighting", "No map is open, so there is nowhere to write lights."); return; }
        if (EditableLights.Count == 0)
        { _log.Warn("Lighting", "The light list is empty — import a Light.dat or add lights first."); return; }
        if (!TryResolveMaterialsBin(entry.Path, out var binEntry))
        { _log.Error("Lighting", "No materials.bin was found alongside this mapgeo."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            // M447: fold in the SAME global multipliers the viewport and the bake apply
            // (BakeLighting.ResolvePosition/ResolveRadius and LightIntensity). Without this the port wrote
            // raw per-light values while the preview showed them scaled, so the map never matched what you
            // were looking at — the fit panel's sliders moved the preview and did nothing to the export.
            float gIntensity = (float)DynamicLightIntensity;
            float gRadius = (float)DynamicLightRadiusScale;
            var gScaleXZ = new System.Numerics.Vector2((float)DynamicLightScaleX, (float)DynamicLightScaleZ);
            var gOffset = new System.Numerics.Vector2((float)DynamicLightOffsetX, (float)DynamicLightOffsetZ);
            float gPosScale = (float)DynamicLightPositionScale;

            var wanted = EditableLights.Select(vm =>
            {
                var pl = vm.ToPointLight();
                string name = string.IsNullOrWhiteSpace(vm.Name) ? "PortedLight" : vm.Name.Trim();
                var p = pl.Position * gPosScale;
                p.X = p.X * gScaleXZ.X + gOffset.X;
                p.Z = p.Z * gScaleXZ.Y + gOffset.Y;
                return new Formats.Lighting.DynamicPointLight(
                    name, p, pl.Color, pl.Radius * gRadius, pl.Intensity * gIntensity);
            }).ToList();

            byte[] source = ReadAsset(binEntry.PathHash);
            var (bytes, removed, written) = await Task.Run(() =>
            {
                var b = Formats.Lighting.DynamicPointLights.Write(source, wanted, out int r, out int w);
                return (b, r, w);
            });
            if (bytes is null)
            { _log.Error("Lighting", "The materials.bin could not be rewritten (it did not parse)."); return; }

            // Validate BEFORE saving: it must read back with exactly the lights we asked for, and pass the
            // shape rules that caught the pointer-element and empty-container crashes.
            var back = await Task.Run(() => Formats.Lighting.DynamicPointLights.Read(bytes));
            if (back.Count != wanted.Count)
            {
                _log.Error("Lighting", $"The rewritten bin carries {back.Count} light(s), expected "
                                       + $"{wanted.Count} — not saved.");
                return;
            }
            var issues = await Task.Run(() => Formats.Meta.ModShapeValidator.ValidateBin(
                Formats.Meta.SafeBinTree.Parse(bytes), bytes, ResolveBinName));
            if (issues.Count > 0)
            {
                foreach (var i in issues.Take(5)) _log.Error("Lighting", $"[{i.Category}] {i.ObjectName}: {i.Detail}");
                _log.Error("Lighting", $"{issues.Count} shape issue(s) — not saved.");
                return;
            }

            string savedTo;
            if (TryWriteToProjectFile(binEntry, bytes, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, bytes, ".bin");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = binEntry.PathHash,
                    ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            _log.Success("Lighting", $"Wrote {written:n0} dynamic point light(s) into the map"
                + (removed > 0 ? $", replacing {removed:n0} existing" : "")
                + $". They render in game without baking, and the baker now reads them too. Saved to {savedTo}.");
        }
        catch (Exception ex) { _log.Error("Lighting", "Lights could not be ported: " + ex.Message); }
    }

    /// <summary>
    /// M446 (B): the <c>MapDynamicPointLight</c> placements authored into this map's materials.bin.
    ///
    /// <para>Empty is the safe answer for every failure — a map with no placements, an unresolvable bin, an
    /// unparseable one — because the bake then behaves exactly as it did before placements existed.</para>
    /// </summary>
    private IReadOnlyList<Formats.Lighting.DynamicPointLight> ReadPlacedPointLights(string? mapGeoPath)
    {
        if (string.IsNullOrEmpty(mapGeoPath)) return Array.Empty<Formats.Lighting.DynamicPointLight>();
        try
        {
            if (!TryResolveMaterialsBin(mapGeoPath, out var binEntry))
                return Array.Empty<Formats.Lighting.DynamicPointLight>();
            var placed = Formats.Lighting.DynamicPointLights.Read(ReadAsset(binEntry.PathHash));
            if (placed.Count > 0)
                _log.Info("Bake", $"{placed.Count} dynamic point light(s) placed in the map will be baked: "
                                  + string.Join(", ", placed.Take(4).Select(p => p.Name)));
            return placed;
        }
        catch (Exception ex)
        {
            _log.Warn("Bake", "Placed point lights could not be read: " + ex.Message);
            return Array.Empty<Formats.Lighting.DynamicPointLight>();
        }
    }

    /// <summary>Assemble the bake inputs from the current map + the live viewport lighting, so a bake
    /// reproduces exactly what the viewport shows. Returns null when nothing can be baked.</summary>
    public Services.LightBakeInputs? GatherBakeInputs(Formats.Baking.BakeSettings settings) =>
        GatherBakeInputs(settings, requireLightmapLayout: true);

    /// <param name="requireLightmapLayout">Atlas baking needs an existing lightmap layout; the LIGHTGRID
    /// does not. The grid is a probe volume over the geometry and never touches a UV2 channel, so gating it
    /// on a layout is what made a lightgrid impossible to produce for a map that has none (M442).</param>
    public Services.LightBakeInputs? GatherBakeInputs(Formats.Baking.BakeSettings settings,
        bool requireLightmapLayout)
    {
        if (_currentMap is not { } map || _currentMapEntry is not { } entry) return null;
        if (requireLightmapLayout && !Formats.Baking.LightBaker.CanBakeExistingLayout(map)) return null;

        var lights = EditableLights
            .Select(l => l.ToPointLight())
            .Select(pl => new Formats.Baking.BakePointLight(pl.Position, pl.Color, pl.Radius, pl.Intensity))
            .ToList();

        // M446 (B): lights PLACED IN THE MAP bake too. MapDynamicPointLight is the only point-light class
        // that ships with a position (M196: 0 MapPointLight placements across 50,107 bins), so before this
        // a light authored into the bin lit the game and contributed nothing to a bake — the two disagreed
        // by construction. Placements are appended to the editor's own list rather than replacing it.
        foreach (var placed in ReadPlacedPointLights(entry.Path))
        {
            // Once Light.dat import writes placements (part C), the same light can exist in BOTH lists.
            // Position+radius is enough to spot that, and the editor's copy wins because it is what the
            // viewport is currently showing — the whole point of BuildLighting is bake == preview.
            if (lights.Any(l => System.Numerics.Vector3.Distance(l.Position, placed.Position) < 1f
                                && Math.Abs(l.Radius - placed.Radius) < 1f)) continue;
            lights.Add(new Formats.Baking.BakePointLight(
                placed.Position, placed.Color, placed.Radius, placed.IntensityScale));
        }

        var sun = CurrentSunProperties ?? _baseSun;
        var lighting = Services.LightBakeService.BuildLighting(
            sunDirectionTowardSun: sun.SunDirection,
            sunColor: new System.Numerics.Vector3(sun.SunColor.X, sun.SunColor.Y, sun.SunColor.Z),
            skyColor: new System.Numerics.Vector3(sun.SkyLightColor.X, sun.SkyLightColor.Y, sun.SkyLightColor.Z),
            skyScale: sun.SkyLightScale,
            lightMapColorScale: (float)CurrentLightmapScale,
            lights: lights,
            lightIntensity: (float)DynamicLightIntensity,
            lightRadiusScale: (float)DynamicLightRadiusScale,
            lightPositionScale: (float)DynamicLightPositionScale,
            lightPositionScaleXZ: new System.Numerics.Vector2((float)DynamicLightScaleX, (float)DynamicLightScaleZ),
            lightPositionOffset: new System.Numerics.Vector2((float)DynamicLightOffsetX, (float)DynamicLightOffsetZ),
            settings: settings);   // M168: the REAL settings — this used to be a throwaway default, so
                                   // SunShadows/PointLightShadows/FalloffSoftness ignored the UI entirely

        return new Services.LightBakeInputs
        {
            Map = map,
            MapgeoPath = entry.Path,
            Lighting = lighting,
            GroupLightmapEnabled = Services.LightBakeService.BuildGroupFlags(map, _currentMapProfiles),
            GroupOccluderEnabled = Services.LightBakeService.BuildOccluderFlags(map, _currentMapProfiles),
        };
    }

    /// <summary>M147: how many of the open map's meshes still lack a lightmap UV channel. Drives the
    /// layout panel: >0 means a layout can be generated for them, 0 means the map is fully covered.
    /// (HasLightmapUv reads Texcoord7 — see the M158 fix; it used to read the wrong channel.)</summary>
    public int MeshesWithoutLightmapUv => _currentMap?.Meshes.Count(m => !m.HasLightmapUv) ?? 0;

    /// <summary>
    /// M468: meshes that have lightmap UVs but NO lightmap texture bound — a map whose baked lighting has
    /// been stripped while the layout it was baked into survives.
    ///
    /// <para>This is worth its own counter because of what it actually costs, which is not obvious and cost
    /// this project a whole investigation. <b>In League, static map geometry does not cast a real-time sun
    /// shadow — its shadows ARE the lightmap.</b> defaultenv_flat blob 226 line 200 reads
    /// <c>shadow = min(realtimePCF, lightmap.w)</c>, so the lightmap's ALPHA is the static shadow mask, and
    /// line 204 adds <c>lightmap.rgb * LIGHT_MAP_COLOR_SCALE</c> on top. Strip the texture and both halves
    /// go with it. Characters and mobs keep their shadows either way, because they are drawn into the
    /// real-time shadow map by a different, skinned caster shader
    /// (<c>hlsl/skinnedmesh/shadow_map_vs</c>) that the lightmap has no say over — which is exactly the
    /// "shadows on characters but not on the terrain" symptom.</para>
    ///
    /// <para>Measured on the user's own map: Riot's shipped Map453 binds a lightmap on 447 of 448 meshes
    /// and ships the atlases; the edited copy bound 0 while keeping all 447 UV channels. That is the state
    /// <see cref="CleanupLightmaps"/> leaves behind, and it is recoverable by baking rather than by any
    /// material or MapSunProperties setting.</para>
    /// </summary>
    public int MeshesWithStrippedLightmap =>
        _currentMap?.Meshes.Count(m => m.HasLightmapUv && string.IsNullOrEmpty(m.BakedLightTexture)) ?? 0;

    /// <summary>
    /// M468: materials still compiling the BAKED path — no <c>NO_BAKED_LIGHTING</c> — which is only a
    /// problem next to <see cref="MeshesWithStrippedLightmap"/>, and next to it is the whole problem.
    ///
    /// <para>Stripping the lightmaps and switching the materials are two separate actions, and doing only
    /// the first leaves a map on the baked path with no bake: the shader still samples a lightmap that is
    /// no longer bound. Measured on the user's Map453 — <b>90 of 92 materials carry no macros at all</b>,
    /// one has <c>USE_DYNAMIC_LIGHTING=1</c>, one has <c>NO_BAKED_LIGHTING=1</c>. That map was never
    /// actually running dynamic lighting.</para>
    ///
    /// <para>The macro is what moves them. <c>FaeLights_Prototype_Mat</c>, the single material that has it,
    /// resolves to blob 45, which <b>samples SHADOW_MAP_DEPTH_PCF and samples NO lightmap</b> — so the
    /// <c>min(pcf, lightmap.w)</c> veto has nothing to clamp it with and real-time shadows land at full
    /// strength. Casting is still gone either way; that half is baked-only in League.</para>
    /// </summary>
    public int MaterialsStillOnBakedPath => _currentMapProfiles is null ? 0
        : _currentMapProfiles.Values.Count(p => !p.NoBakedLighting);

    /// <summary>M468: the map has a lightmap layout sitting unused. Surfaced rather than silent because the
    /// map still renders — it just renders with no baked light and no static shadows.</summary>
    public bool HasStrippedLightmap => HasMapForLayout && MeshesWithStrippedLightmap > 0;

    /// <summary>M147: is a mapgeo open at all (the layout panel is meaningful only then).</summary>
    public bool HasMapForLayout => _currentMap is not null && _currentMapEntry is not null;

    /// <summary>M147: the open map's mesh count, for the layout panel's "N of M" summary.</summary>
    public int MapMeshCountForLayout => _currentMap?.Meshes.Count ?? 0;

    /// <summary>M147: a map is open and some of it has no lightmap UVs — a layout can be generated.</summary>
    public bool NeedsLightmapLayout => HasMapForLayout && MeshesWithoutLightmapUv > 0;

    /// <summary>M147: give the open map a lightmap layout — unwrap UV2, pack atlas regions, assign each
    /// mesh its BakedLight reference — then save the REWRITTEN mapgeo and reload it. Unlike baking, this
    /// rewrites geometry, so the result is validated by re-reading it before anything is saved.</summary>
    public async Task<Formats.Baking.LightmapLayoutResult?> GenerateLightmapLayoutAsync(Formats.Baking.BakeSettings settings)
    {
        if (_currentMap is null || _currentMapEntry is not { } entry || _currentMapBytes is null)
        { _log.Warn("Layout", "No map open."); return null; }

        var sourceBytes = _currentMapBytes;
        // M164: exclude VertexDeform foliage only. NO_BAKED_LIGHTING must NOT exclude a mesh here: on a
        // map with no lightmaps EVERY material carries it (all 183 on Map11/base_srx), because it
        // describes the map's current state, not a wish about future lightmaps. Excluding on it removed
        // every mesh, produced an empty layout, and the save gate then correctly rejected the result.
        // Generating a layout is precisely the act of giving these meshes lightmaps — so the macro is
        // CLEARED below instead, which is the "remove incompatible shader macros" step.
        var excludeMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_currentMapProfiles is { } profs)
            foreach (var (name, p) in profs)
                if (p.IsVertexDeform) excludeMaterials.Add(name);

        // M314: DynamicEffect historically flowed through this builder automatically, but its new
        // lightmap cannot render in game without our experimental DX11 companion cache. Make the
        // geometry an explicit creator opt-in instead of silently committing to that second step.
        if (!settings.IncludeDynamicEffectMeshes)
        {
            if (!TryResolveMaterialsBin(entry.Path, out var layoutBinEntry))
            {
                _log.Error("Layout", "No materials.bin was found, so DynamicEffect meshes cannot be safely filtered.");
                return null;
            }
            try
            {
                var layoutDocument = Formats.Materials.MaterialDocument.Parse(
                    ReadAsset(layoutBinEntry.PathHash), ResolveBinName, ResolveWadPath);
                foreach (var material in layoutDocument.Materials)
                    if (string.Equals(material.RenderShader ?? material.ShaderName,
                            Services.ExperimentalDynamicEffectShaderService.RenderShader,
                            StringComparison.OrdinalIgnoreCase))
                        excludeMaterials.Add(material.Name);
            }
            catch (Exception ex)
            {
                _log.Error("Layout", "DynamicEffect materials could not be identified for layout filtering: " + ex.Message);
                return null;
            }
        }

        string atlasFolder = settings.ResolveOutputFolder(entry.Path);
        int atlasStartIndex = NextGeneratedAtlasIndex(_currentMap, atlasFolder);
        var extendedChannels = ExtendedChannelMaterialsFor(entry.Path);
        var (result, bytes) = await Task.Run(() =>
        {
            // TryReadEditable refuses anything we cannot reproduce byte-for-byte, so we never rewrite a
            // mapgeo we don't fully understand.
            if (!Formats.MapGeo.MapGeoBinary.TryReadEditable(sourceBytes, out var map, extendedChannels))
                return ((Formats.Baking.LightmapLayoutResult?)null, (byte[]?)null);

            var r = Formats.Baking.MapGeoLightmapBuilder.Build(map, new Formats.Baking.MapGeoLightmapBuilder.Settings
            {
                AtlasResolution = settings.AtlasResolution,
                TexelDensity = settings.TexelDensity,
                Padding = settings.Padding,
                AtlasStartIndex = atlasStartIndex,
                AtlasPathFormat = atlasFolder + "{0}.tex",
                // M163/M314: don't spend atlas space on moving VertexDeform foliage or on the
                // DynamicEffect materials the creator left opted out. Render-region meshes are skipped
                // by the builder's own default.
                ExcludeMaterials = excludeMaterials,
            });
            return (r, map.Write());
        });

        if (result is null || bytes is null)
        { _log.Error("Layout", "This mapgeo could not be safely rewritten (it does not round-trip byte-exactly)."); return null; }

        // Validate the rewrite BEFORE saving: it must decode again and actually carry the new layout.
        try
        {
            var check = await Task.Run(() => Formats.MapGeo.MapGeoDecoder.Decode(bytes));
            if (!Formats.Baking.LightBaker.CanBakeExistingLayout(check))
            { _log.Error("Layout", "The rewritten mapgeo decoded but carries no usable lightmap layout — not saved."); return null; }
        }
        catch (Exception ex)
        { _log.Error("Layout", $"The rewritten mapgeo failed to decode ({ex.Message}) — not saved."); return null; }

        WriteBakedAsset(entry.Path, bytes, ".mapgeo");
        int macrosCleared = ClearNoBakedLightingMacros(entry, result.LaidOutMaterials, settings);
        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        if (Project.IsFolderProject) { BuildMounts(); BuildProjectTree(); }
        UpdateTitle();

        foreach (var w in result.Warnings.Take(5)) _log.Warn("Layout", w);
        if (result.Warnings.Count > 5) _log.Warn("Layout", $"(+{result.Warnings.Count - 5} more warnings)");
        _log.Success("Layout", $"Generated a lightmap layout: {result.MeshesLaidOut} mesh(es) over {result.AtlasCount} atlas(es) " +
                               $"from {result.GeometriesUnwrapped} unique geometries" +
                               (result.MeshesExcluded > 0 ? $", {result.MeshesExcluded} excluded (material filter / render regions)" : "") +
                               (result.MeshesSkipped > 0 ? $", {result.MeshesSkipped} skipped" : "") +
                               (macrosCleared > 0 ? $"; cleared NO_BAKED_LIGHTING on {macrosCleared} material(s)" : "") +
                               $". Mapgeo rewritten ({bytes.Length:n0} bytes) — now bake into it.");

        await LoadMapGeoAsync(entry);
        OnPropertyChanged(nameof(CanBakeLighting));
        OnPropertyChanged(nameof(NeedsLightmapLayout));
        OnPropertyChanged(nameof(MeshesWithoutLightmapUv));
        OnPropertyChanged(nameof(MeshesWithStrippedLightmap));   // M468
        OnPropertyChanged(nameof(HasStrippedLightmap));
        OnPropertyChanged(nameof(LegacyImportedMeshCount));      // M472
        OnPropertyChanged(nameof(HasLegacyImport));
        return result;
    }

    /// <summary>M314: incremental layout passes must not reuse atlas 0. This is what allows a creator
    /// to opt DynamicEffect meshes in after first generating a conservative static-only layout.</summary>
    private static int NextGeneratedAtlasIndex(Formats.MapGeo.MapGeoAsset map, string atlasFolder)
    {
        int next = 0;
        foreach (string path in map.Groups.Select(g => g.LightmapTexture))
        {
            if (string.IsNullOrWhiteSpace(path)
                || !path.StartsWith(atlasFolder, StringComparison.OrdinalIgnoreCase)) continue;
            string file = Path.GetFileName(path);
            int dot = file.IndexOf('.');
            string indexText = dot < 0 ? Path.GetFileNameWithoutExtension(file) : file[..dot];
            if (int.TryParse(indexText, out int index) && index >= next) next = index + 1;
        }
        return next;
    }

    /// <summary>
    /// M444: the materials in this map that make the client read a LONGER per-mesh channel block, read
    /// from the mapgeo's own sibling materials.bin.
    ///
    /// <para>Every mapgeo read that may be written back has to pass this, because the mapgeo does not
    /// record which reader applies — the material does. Getting it wrong is silent: the mesh section
    /// desyncs, <see cref="Formats.MapGeo.MapGeoBinary.TryReadEditable"/> refuses the file (so we fail
    /// safe), and on a file we then rewrote the game would crash deep in map load with no useful
    /// message.</para>
    ///
    /// <para>An empty set is the safe answer whenever the bin cannot be read: it reproduces the default
    /// reader, which is correct for all 207 mapgeo files Riot ships.</para>
    /// </summary>
    private IReadOnlySet<string> ExtendedChannelMaterialsFor(string? mapGeoPath)
    {
        var empty = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(mapGeoPath)) return empty;
        try
        {
            if (!TryResolveMaterialsBin(mapGeoPath, out var binEntry)) return empty;
            var set = Formats.MapGeo.ExtendedChannelRule.From(ReadAsset(binEntry.PathHash), ResolveBinName);
            if (set.Count > 0)
                _log.Info("Map", $"{set.Count} material(s) use the extended mesh channel block: "
                                 + string.Join(", ", set.Take(4)));
            return set;
        }
        catch (Exception ex)
        {
            _log.Warn("Map", "Extended-channel materials could not be identified: " + ex.Message);
            return empty;
        }
    }

    /// <summary>
    /// Give lightmapped legacy SRX materials custom DX11 permutations, stage them as a ShaderCache
    /// companion WAD, then clear NO_BAKED_LIGHTING only on materials the generated cache positively
    /// covers. The operation remains repeatable after the macro was cleared so a creator can refresh the
    /// companion after a Riot patch. Riot's installed cache is never edited.
    /// </summary>
    public async Task<string> EnableExperimentalLightmapShadersAsync()
    {
        if (_currentMap is not { } map || _currentMapEntry is not { } mapEntry)
            return "Load a map first.";
        if (!Project.IsFolderProject || Project.RootPath is not { } root)
            return "Experimental shader-cache patches require a saved folder project.";
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
            return "No materials.bin was found alongside this mapgeo.";

        string? finalDir = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        if (finalDir is null)
            return "Set the League game folder in Project Settings first.";

        byte[] originalBin;
        Formats.Materials.MaterialDocument document;
        try
        {
            originalBin = ReadAsset(binEntry.PathHash);
            document = Formats.Materials.MaterialDocument.Parse(originalBin, ResolveBinName, ResolveWadPath);
        }
        catch (Exception ex) { return "Materials could not be read: " + ex.Message; }

        var lightmappedNames = map.Groups
            .Where(g => !string.IsNullOrWhiteSpace(g.LightmapTexture))
            .Select(g => g.Material)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = document.Materials
            .Where(m => lightmappedNames.Contains(m.Name)
                     && (string.Equals(m.RenderShader ?? m.ShaderName,
                             Services.ExperimentalDynamicEffectShaderService.RenderShader,
                             StringComparison.OrdinalIgnoreCase)
                         || Services.ExperimentalSrxBlendShaderService.Supports(m)))
            .ToList();
        if (candidates.Count == 0)
            return "No lightmapped material needing a supported experimental shader was found on this map.";

        IReadOnlyList<Services.ExperimentalLightmapShaderPatch> patches;
        try
        {
            patches = await Task.Run(() =>
            {
                using var cache = Formats.Shaders.ShaderCacheReader.Open(finalDir, _resolver.Database, out var cacheError)
                    ?? throw new InvalidOperationException(cacheError);
                using var definitions = new DisposableShaderDefinitions(finalDir);
                var built = new List<Services.ExperimentalLightmapShaderPatch>();
                var dynamicEffect = candidates.Where(m => string.Equals(m.RenderShader ?? m.ShaderName,
                        Services.ExperimentalDynamicEffectShaderService.RenderShader,
                        StringComparison.OrdinalIgnoreCase)).ToList();
                var srxBlend = candidates.Where(Services.ExperimentalSrxBlendShaderService.Supports).ToList();
                if (dynamicEffect.Count > 0)
                    built.Add(Services.ExperimentalDynamicEffectShaderService.Build(
                        cache, definitions.Value, dynamicEffect));
                if (srxBlend.Count > 0)
                    built.Add(Services.ExperimentalSrxBlendShaderService.Build(
                        cache, definitions.Value, srxBlend));
                return (IReadOnlyList<Services.ExperimentalLightmapShaderPatch>)built;
            });
        }
        catch (Exception ex)
        {
            _log.Error("Shader", "Experimental lightmap shader patch failed: " + ex.Message);
            return "Shader patch generation failed: " + ex.Message;
        }

        var supportedMaterials = patches.SelectMany(p => p.SupportedMaterials)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var patchAssets = patches.SelectMany(p => p.Assets)
            .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Single()).ToList();
        int cleared = 0;
        foreach (var material in candidates)
            if (supportedMaterials.Contains(material.Name)
                && material.RemoveMacro(Formats.Materials.MaterialBinding.MacroNoBakedLighting))
                cleared++;

        byte[]? changedBin = null;
        if (cleared > 0)
        {
            try
            {
                changedBin = document.Serialize();
                _ = Formats.Materials.MaterialDocument.Parse(changedBin, ResolveBinName, ResolveWadPath);
            }
            catch (Exception ex) { return "The material rewrite did not validate: " + ex.Message; }
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string backupRoot = Path.Combine(root, ".reyengine", "backups", "experimental-lightmaps-" + stamp);
        string shaderFolder = Path.Combine(root, "ShaderCache.dx11");
        try
        {
            Directory.CreateDirectory(backupRoot);
            File.WriteAllBytes(Path.Combine(backupRoot, Path.GetFileName(binEntry.Path)), originalBin);

            // Persist the companion folder before touching the material. A failed save or partial shader
            // write then leaves the still-unlit material safe; the crash-sensitive macro is cleared last.
            if (!Project.ProjectFolders.Contains("ShaderCache.dx11", StringComparer.OrdinalIgnoreCase))
                Project.ProjectFolders.Add("ShaderCache.dx11");
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);

            foreach (var asset in patchAssets)
            {
                string destination = Path.Combine(shaderFolder,
                    asset.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(destination))
                {
                    string backup = Path.Combine(backupRoot, "ShaderCache.dx11",
                        asset.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, overwrite: true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, asset.Bytes);
            }

            if (changedBin is not null) WriteBakedAsset(binEntry.Path, changedBin, ".bin");
        }
        catch (Exception ex)
        {
            _log.Error("Shader", "Could not stage the experimental cache patch: " + ex.Message);
            return "Could not stage the shader patch: " + ex.Message;
        }

        try
        {
            BuildMounts();
            BuildProjectTree();
            UpdateTitle();
        }
        catch (Exception ex)
        {
            // The patch is already transactionally complete on disk. A remount failure must not imply
            // that it was rolled back; rebuilding/reopening the project will pick up the staged files.
            _log.Warn("Shader", "Shader patch was staged, but the project view did not refresh: " + ex.Message);
        }

        // The bake filter is built from the loaded material profiles. Remounting alone leaves those
        // profiles carrying the pre-patch NO_BAKED_LIGHTING value, so the UI can claim five referenced
        // atlases and the baker still excludes every triangle. Reload before the command returns; the
        // Light Baking window refresh that follows now sees the rewritten materials immediately.
        await LoadMapGeoAsync(mapEntry);

        string detail = string.Join("; ", patches.Select(p => p.Detail));
        string result = $"Experimental lightmap shaders enabled on {supportedMaterials.Count:n0} material(s)"
                      + (cleared > 0 ? $"; removed NO_BAKED_LIGHTING from {cleared:n0}" : "; shader cache refreshed") + ". "
                      + $"Generated {detail} and staged ShaderCache.dx11.wad.client content. "
                      + "Build Package, install both WADs, and test in Practice Tool.";
        _log.Warn("Shader", result + $" Backup: {backupRoot}");
        return result;
    }

    /// <summary>ShaderPermutationIndex has a Dispose method for its LeagueToolkit WAD but predates the
    /// IDisposable interface. This tiny adapter keeps worker ownership explicit.</summary>
    private sealed class DisposableShaderDefinitions : IDisposable
    {
        public Formats.Materials.ShaderPermutationIndex Value { get; }
        public DisposableShaderDefinitions(string finalDir) => Value = new(finalDir);
        public void Dispose() => Value.Dispose();
    }

    /// <summary>M166: the shipped shader-permutation set, used to decide whether clearing a macro would
    /// leave the material asking for a shader the client cannot load. Built once per game directory.</summary>
    // ---- M249 (phase 6, step 2): hand the open map to the side-by-side D3D11 surface ----

    /// <summary>M252: the A/B diff result, into the console where it can be read and copied.</summary>
    public void LogRendererDiff(string text) => _log.Info("DX11", "renderer A/B diff" + System.Environment.NewLine + text);

    private Formats.Shaders.ShaderCacheReader? _dx11ShaderCache;
    private string? _dx11ShaderCacheDir;

    /// <summary>M266: the open shader cache, for the D3D11 surface's particle driver. Read-only and the
    /// minimum exposure that works: the driver takes its TEXTURES from the already-resolved VfxPlaybackItem
    /// lists, so it needs no asset reader at all - which is also what guarantees it resolves the same files
    /// the GL viewport does.</summary>
    public Formats.Shaders.ShaderCacheReader? Dx11ShaderCache => _dx11ShaderCache;

    /// <summary>M620: open the shader cache, or return the already-open one.
    ///
    /// <para>This used to live inside BuildDx11SceneAsync, which returns early unless a MAP is open - so
    /// opening a champion never opened the cache, and the character preview reported "no materials" for
    /// every character in the game. The cache has nothing to do with maps; it is per game folder.</para></summary>
    private Formats.Shaders.ShaderCacheReader? OpenDx11ShaderCache(out string? error)
    {
        error = null;
        string? dir = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        if (dir is null || !Directory.Exists(dir))
        {
            error = "the game folder is not set, so the shader cache cannot be opened";
            return null;
        }

        if (_dx11ShaderCache is not null
            && string.Equals(_dx11ShaderCacheDir, dir, StringComparison.OrdinalIgnoreCase))
            return _dx11ShaderCache;

        _dx11ShaderCache = Formats.Shaders.ShaderCacheReader.Open(dir, _resolver.Database, out error);
        _dx11ShaderCacheDir = dir;
        return _dx11ShaderCache;
    }

    // M365d: the SYNCHRONOUS BuildDx11Scene overload used to live here and has been deleted.
    //
    // It had no callers - MainWindow.axaml.cs:109 awaits BuildDx11SceneAsync, and that is the only
    // call site in the repo - but it was the twin that passed grassTintPath to the scene builder
    // while the live async one did not. So the grass tint read as correctly wired in every review,
    // and four separate fixes (M355, M365, M365b, M365c) were written against code that never ran.
    // Deleted rather than kept in sync: two builders that must agree WILL diverge, and this one
    // proved it silently.

    /// <summary>M250: the async form. The CPU half - mesh build, permutation resolution, and every texture
    /// decode - runs on a worker; only the D3D commit comes back to the UI thread. Map12/bloom spent 5.5 s
    /// in here synchronously, almost all of it decoding 2,860 texture bindings.</summary>
    public async Task<string> BuildDx11SceneAsync(ReyEngine.Rendering.D3D11.ShaderPreviewRenderer renderer)
    {
        if (_currentMap is not { } map) return "No map open - the D3D11 surface has no scene to draw yet.";
        if (_currentMapEntry is not { } mapEntry) return "The open map has no WAD entry.";

        string? dir = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        if (dir is null || !Directory.Exists(dir)) return "Game directory is not set, so the shader cache cannot be opened.";

        if (OpenDx11ShaderCache(out var cacheErr) is null)
            return "ShaderCache.dx11.wad.client: " + (cacheErr ?? "not readable");

        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
            return "No materials.bin alongside this mapgeo.";

        byte[] binBytes;
        try { binBytes = GetAssetBytes(binEntry); }
        catch (Exception ex) { return $"{binEntry.DisplayName}: {ex.Message}"; }

        // M501: prefer the material editor's LIVE bytes when it is editing this map's own bin. GetAssetBytes
        // reads the project file or WAD chunk from disk, so without this the D3D11 scene could only ever
        // show SAVED materials — while the GL path, fed by ApplyMaterialToViewport, showed unsaved ones.
        // The two viewports disagreeing about the same material is the thing that made this look like a
        // rendering bug rather than a plumbing one.
        if (MaterialEditor.Kind == MaterialSourceKind.MapMaterials
            && MaterialEditor.BinEntry is { } editing && editing.PathHash == binEntry.PathHash
            && MaterialEditor.IsDirty
            && MaterialEditor.Serialize() is { } liveBytes)
        {
            binBytes = liveBytes;
        }

        var cache = _dx11ShaderCache;
        var perms = ShaderPerms();
        // M365d: THE grass-tint bug. This overload is the only one the app calls, and it omitted this
        // argument, so grassTintPath was null and the entire tint block in Dx11SceneBuilder - guarded by
        // `if (!string.IsNullOrEmpty(grassTintPath))` - was skipped on every map. Both declared samplers
        // then took the opaque-white stand-in, and lerp(white, white, GRASS_INTERP) is white for any interp,
        // so the multiply was the identity: arithmetically indistinguishable from "no tint", always.
        // M355, M365, M365b and M365c all edited code inside that dead gate.
        //
        // Resolved OUTSIDE Task.Run, like ShaderPerms() above: FindGrassTintTexturePath reads _mounts and
        // _currentMapEntry, which are UI-thread state.
        string? grassTintPath = FindGrassTintTexturePath();

        // M456: does this map have point lights at all? Decides whether the builder pins Riot's own
        // USE_DYNAMIC_LIGHTING permutation, which is what moves the lights from the M452 additive overlay
        // into the material's own pixel shader. Read here, on the UI thread, for the same reason as the
        // two lines above - DynamicLights is view-model state.
        //
        // NOTE the consequence: the choice is baked into the scene. Loading a Light.dat AFTER the D3D11
        // scene was built leaves those materials on the overlay until the scene is rebuilt (a map change
        // or a viewport re-toggle). Turning the lights OFF afterwards is fine - an empty cluster grid
        // makes the in-shader loop a no-op.
        bool hasDynamicLights = DynamicLights is { Count: > 0 };

        Services.Dx11SceneBuilder.PreparedScene? prepared = null;
        string? error = null;
        await Task.Run(() =>
        {
            try
            {
                var doc = Formats.Materials.MaterialDocument.Parse(binBytes, ResolveBinName, ResolveWadPath);
                prepared = Services.Dx11SceneBuilder.Prepare(cache, perms, map, doc.Materials,
                    TryReadAssetBytes, mapEntry.Path, grassTintPath, hasDynamicLights);
            }
            catch (Exception ex) { error = ex.Message; }
        });

        if (prepared is null) return error ?? "the scene could not be prepared";

        var result = Services.Dx11SceneBuilder.Commit(renderer, prepared, AppInfo.DisplayVersion);
        _log.Info("DX11", $"viewport scene: {result.Materials} material(s), {result.Failed} unresolved, "
                          + $"{result.Slices} slice(s), {result.Textures} texture binding(s)");
        // M365c/M365d: say what happened to the grass tint instead of leaving it to be inferred from the
        // picture. This was added to the dead sync twin, so it never printed - and the silence read as
        // "nothing to report" rather than "this code does not run". NoSlot specifically means the resolved
        // permutation declared no tint sampler, which is a permutation-selection problem rather than a
        // texture one; the two look identical on screen and have completely different fixes.
        if (result.GrassTintBound > 0 || result.GrassTintNoSlot > 0)
            _log.Info("DX11", $"grass tint: {result.GrassTintBound} slice(s) bound"
                              + (result.GrassTintNoSlot > 0
                                  ? $", {result.GrassTintNoSlot} whose permutation declares no tint sampler"
                                  : ""));
        // M456: same reasoning as the grass-tint line. On a map WITH lights, a pinned count of zero means
        // every slice quietly stayed on the additive overlay - which looks identical to it working.
        if (hasDynamicLights)
            _log.Info("DX11", $"dynamic lighting: {result.DynamicLightingPinned} slice(s) pinned to Riot's "
                              + "in-shader light loop"
                              + (result.DynamicLightingPinFailed > 0
                                  ? $", {result.DynamicLightingPinFailed} declare the axis but cooked no "
                                    + "matching permutation"
                                  : ""));
        // M278: never log a failure COUNT on its own. This exact line read "0 material(s), 21 unresolved"
        // for an afternoon while the shader cache had simply been renamed underneath us, and it named
        // nothing that could be looked up.
        foreach (var why in result.Reasons) _log.Warn("DX11", "unresolved - " + why);
        return result.Report;
    }

    private Formats.Materials.ShaderPermutationIndex? _shaderPerms;
    private string? _shaderPermsDir;
    private Formats.Materials.ShaderPermutationIndex? ShaderPerms()
    {
        // M622: the SAME locator the shader cache and every other game-data path uses. This built the
        // path by hand, so a GameDirectory that FindFinalDirectory copes with but Path.Combine does not
        // returned null here while the cache opened fine - and a null index means no shader parameter
        // DEFAULTS, which leaves every unauthored parameter at zero. Zero is not "unspecified": it is a
        // value the shader multiplies by, and the result is a black model (M255).
        string? dir = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        if (dir is null || !Directory.Exists(dir)) return null;
        if (_shaderPerms is null || !string.Equals(_shaderPermsDir, dir, StringComparison.OrdinalIgnoreCase))
        {
            _shaderPerms = new Formats.Materials.ShaderPermutationIndex(dir);
            _shaderPermsDir = dir;
        }
        return _shaderPerms;
    }

    /// <summary>M164: clear NO_BAKED_LIGHTING from the materials that just received a lightmap layout.
    /// Without this the whole exercise is inert: the meshes point at an atlas, but both the game and our
    /// own bake skip them because the macro says "ignore baked lighting". This is the "remove
    /// incompatible shader macros" half of preparing a mesh for baking. Returns how many were cleared.</summary>
    private int ClearNoBakedLightingMacros(WadAssetEntry mapEntry, IReadOnlyCollection<string> materials,
        Formats.Baking.BakeSettings settings)
    {
        if (materials.Count == 0) return 0;
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry)) 
        { _log.Warn("Layout", "No materials .bin found — NO_BAKED_LIGHTING could not be cleared, so the new atlases will not be sampled."); return 0; }

        try
        {
            var binBytes = ReadAsset(binEntry.PathHash);
            var doc = Formats.Materials.MaterialDocument.Parse(binBytes, ResolveBinName, ResolveWadPath);
            var perms = ShaderPerms();
            bool canValidate = perms is not null && perms.IsAvailable;
            if (!canValidate)
                _log.Warn("Layout", "No shader cache found (set the game folder) — NO_BAKED_LIGHTING was left alone. " +
                                    "Clearing it blindly can ask the client for a shader permutation Riot never cooked.");

            int cleared = 0, refused = 0;
            foreach (var m in doc.Materials)
            {
                if (m.Name is not { } n || !materials.Contains(n)) continue;
                // M166: only clear where the resulting define set is one the game actually ships. On
                // Map11/base_srx 20 of 184 materials are NOT, and clearing them is what made the client
                // log "Unable to find correct hash for shader ... in wad" and fail to compile.
                if (!canValidate || !perms!.CanRemoveMacro(m, Formats.Materials.MaterialBinding.MacroNoBakedLighting)) { refused++; continue; }
                if (m.RemoveMacro(Formats.Materials.MaterialBinding.MacroNoBakedLighting)) cleared++;
            }
            if (refused > 0)
                _log.Info("Layout", $"{refused} material(s) keep NO_BAKED_LIGHTING — the game ships no shader " +
                                    "permutation for them without it, so they stay unlit rather than failing to render.");
            var binOut = cleared > 0 ? doc.Serialize() : binBytes;

            // M167: register the lightgrid we are about to bake. Without lightGridFileName nothing loads
            // it, so probe lighting for characters/effects/NO_BAKED_LIGHTING surfaces would stay dead.
            string gridPath = settings.ResolveOutputFolder(mapEntry.Path) + settings.LightGridFileName();
            var stamped = Formats.MapGeo.MapBakeProperties.Write(
                binOut, gridPath, settings.LightGridWidth, 0.5f, out var bakeResult);
            if (stamped is not null) { binOut = stamped; _log.Info("Layout", "MapBakeProperties: " + bakeResult.Detail); }
            else _log.Warn("Layout", "Could not write MapBakeProperties (" + bakeResult.Detail +
                                     ") — the baked lightgrid will not be loaded by the game.");

            if (cleared == 0 && stamped is null) return 0;
            WriteBakedAsset(binEntry.Path, binOut, ".bin");
            return cleared;
        }
        catch (Exception ex)
        {
            _log.Warn("Layout", $"Could not clear NO_BAKED_LIGHTING ({ex.Message}) — the new atlases will not be sampled until it is removed.");
            return 0;
        }
    }

    /// <summary>Build a bake service bound to the current project, or null when there is nowhere to write
    /// (an unsaved project). A folder project writes atlases to their real path; a saved single-WAD
    /// project uses the hashed override store.</summary>
    public Services.LightBakeService? MakeBakeService()
    {
        bool canWrite = (Project.IsFolderProject && Project.RootPath is not null)
                        || Project.OverridesDirectory is not null;
        return canWrite ? new Services.LightBakeService(WriteBakedAsset) : null;
    }

    /// <summary>M158: write a baked lightmap file where it BELONGS. For a folder project that means the
    /// asset's real path inside the map's project folder (…/Map12/assets/maps/lightmaps/…/0.tex) — the
    /// packer hashes folder files by their relative path, so this lands as the exact chunk the game
    /// reads, and it shows up in the project tree instead of as an opaque hash. A single-WAD project has
    /// no folder to place into, so it falls back to the hashed override store. Returns the path written.</summary>
    private string WriteBakedAsset(string assetPath, byte[] bytes, string ext)
    {
        ulong hash = HashAlgorithms.WadPath(assetPath);
        // M590: teach the dictionary this path as it is written. Riot's dictionary only knows Riot's
        // files, and since 16.17 a material stores a texture as the HASH of its path - so an asset this
        // editor invents (a legacy port's textures land under assets/maps/legacyimport/…) would show the
        // author a bare 0x… with an unresolved warning, for a file the project itself just created.
        // Registering here rather than at project open also covers assets made DURING a session, which
        // is exactly when a port runs.
        _resolver.Database.AddWad(hash, assetPath);
        if (Project.IsFolderProject && Project.RootPath is { } root && _currentMapEntry is { } mapEntry)
        {
            // Stage under the SAME WAD folder the map itself lives in (Map12.wad.client → "Map12"): the
            // game loads a map's lightmaps from that same wad.
            string folderName = RiotWadFolderName(mapEntry);
            // A newly generated atlas has no Riot source hash yet. More subtly, after a rewritten
            // mapgeo is remounted its source chain can temporarily lack the original WAD as well; using
            // "Overrides" then puts only the last new atlas into Overrides.wad.client while its siblings
            // land in Map11. The map path is authoritative about its home shipping WAD.
            if (folderName == "Overrides" && MapNameFromAssetPath(mapEntry.Path) is { } mapName)
                folderName = mapName;
            string dest = Path.Combine(root, folderName, assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            if (folderName != "Overrides")
            {
                // Repair output created by the old fallback above. The correct copy is durable before
                // the stale loose fallback is removed, so an interrupted bake never loses the atlas.
                string stale = Path.Combine(root, "Overrides", assetPath.Replace('/', Path.DirectorySeparatorChar));
                if (!string.Equals(stale, dest, StringComparison.OrdinalIgnoreCase) && File.Exists(stale))
                    File.Delete(stale);
            }
            if (!Project.ProjectFolders.Contains(folderName, StringComparer.OrdinalIgnoreCase))
                Project.ProjectFolders.Add(folderName);
            // A hashed override (ProjectOverride, priority 0) outranks a folder file (ProjectFolder,
            // priority 1) for the same hash — so an atlas left in the override store by an EARLIER bake
            // (before atlases landed in the folder) would shadow this one and the viewport would keep
            // showing the stale bake no matter how we re-bake. Clear it so the folder file wins.
            ClearShadowOverride(hash, ext);
            return dest;
        }

        var overrideFile = ProjectWorkspace.StoreOverrideBytes(Project, hash, bytes, ext);
        _overrides.Set(new ProjectAssetOverride
        {
            PathHash = hash,
            ResolvedPath = assetPath,
            OverrideFile = overrideFile,
            AddedUtc = DateTime.UtcNow.ToString("o"),
        });
        return overrideFile;
    }

    /// <summary>Delete a hashed override that would shadow a folder-placed baked file (and its record),
    /// so the fresh folder file wins. Lightweight — the caller rebuilds mounts once after the whole bake.</summary>
    private void ClearShadowOverride(ulong hash, string ext)
    {
        try
        {
            if (Project.OverridesDirectory is { } dir)
            {
                var f = Path.Combine(dir, $"{hash:x16}{ext}");
                if (File.Exists(f)) File.Delete(f);
            }
        }
        catch { /* best-effort — a locked/absent override just stays, BuildMounts still favours nothing worse */ }
        _overrides.Remove(hash);
    }

    public void OnLightBakeFinished(Services.LightBakeResult result)
    {
        if (result.AtlasCount == 0)
            _log.Error("Bake", $"Baked 0 of {result.ReferencedAtlasCount} referenced atlas(es); "
                + $"{result.SkippedAtlasCount} had no material-eligible triangles. "
                + "Use Enable Experimental Shader Lightmaps, wait for the map reload, then retry."
                + (result.WroteLightGrid ? " The lightgrid was written." : ""));
        else if (result.SkippedAtlasCount > 0)
            _log.Warn("Bake", result.OutputDescription
                + $" ({result.AtlasCount} baked, {result.SkippedAtlasCount} skipped). "
                + "Skipped atlases contain only materials or meshes that currently opt out of baked lighting.");
        else
            _log.Success("Bake", result.OutputDescription + $" ({result.AtlasCount} atlas(es)).");
        if (result.TotalBytes == 0) return;
        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        // Re-index: a folder project just gained new files on disk that the mounts don't know about yet;
        // without a remount ReadAsset would still serve Riot's atlases and the viewport wouldn't change.
        if (Project.IsFolderProject)
        {
            BuildMounts();
            BuildProjectTree();
        }
        UpdateTitle();
        // Switch the viewport to Baked so the user immediately sees the freshly baked lighting (atlas on,
        // dynamic lights off) instead of the live authoring view they baked from.
        LightingMode = LightingModeBaked;
        // Re-read the map so the viewport samples the freshly baked atlases instead of Riot's.
        if (_currentMapEntry is { } e) _ = LoadMapGeoAsync(e);
    }

    // ==================================================================== M171: recolour textures

    // ============================================================ M172a: closest-hit ray index

    private Rendering.MeshRayIndex? _rayIndex;
    private MapGeoAsset? _rayIndexMap;
    private int _rayIndexRevision = -1;
    private readonly object _rayIndexLock = new();

    /// <summary>A BVH over the open map's triangles, built on demand and rebuilt whenever the geometry
    /// moves. Replaces the brute-force picker, which scanned all 909,993 Summoner's Rift triangles per
    /// ray: measured 11.13 ms against 1.28 µs here, over 3,000 rays returning bit-identical hits.
    ///
    /// The build costs ~1.15 s on that map, so <see cref="PrebuildRayIndex"/> starts it in the background
    /// as soon as a map loads; this accessor only ever pays it if something asks before that finishes.
    /// Invalidated by <c>MeshVerticesRevision</c> because transform edits mutate MapGeoAsset.Positions in
    /// place — a stale tree would silently pick the geometry's old location.</summary>
    private Rendering.MeshRayIndex? RayIndex
    {
        get
        {
            if (_currentMap is not { } map || map.Groups.Count == 0) return null;
            lock (_rayIndexLock)
            {
                if (_rayIndex is not null && ReferenceEquals(_rayIndexMap, map) && _rayIndexRevision == MeshVerticesRevision)
                    return _rayIndex;
                _rayIndex = BuildRayIndex(map, out _);
                _rayIndexMap = map;
                _rayIndexRevision = MeshVerticesRevision;
                return _rayIndex;
            }
        }
    }

    private static Rendering.MeshRayIndex BuildRayIndex(MapGeoAsset map, out long ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var subs = map.Groups.Select(g => (g.StartIndex, g.IndexCount)).ToList();
        var index = new Rendering.MeshRayIndex(map.Positions, map.Uvs, map.Indices, subs);
        sw.Stop();
        ms = sw.ElapsedMilliseconds;
        return index;
    }

    /// <summary>Warm the ray index off the UI thread right after a map loads, so the first click doesn't
    /// wear the build cost.</summary>
    private void PrebuildRayIndex(MapGeoAsset map, int revision)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var index = BuildRayIndex(map, out long ms);
                lock (_rayIndexLock)
                {
                    // Only publish if nothing changed underneath us while we were building.
                    if (!ReferenceEquals(_currentMap, map) || MeshVerticesRevision != revision) return;
                    _rayIndex = index;
                    _rayIndexMap = map;
                    _rayIndexRevision = revision;
                }
                _log.Info("Viewport", $"Ray index: {index.TriangleCount:n0} triangles in {ms:n0} ms.");
            }
            catch (Exception ex) { _log.Warn("Viewport", $"Ray index build failed ({ex.Message}) — picking falls back to a rebuild on first click."); }
        });
    }

    private void InvalidateRayIndex()
    {
        lock (_rayIndexLock) { _rayIndex = null; _rayIndexMap = null; _rayIndexRevision = -1; }
        // The paint session caches the index and the texture set; both are about to be different.
        _paintSession = null;
        _paintStrokeActive = false;
    }

    // ============================================================ M172c: paint on meshes

    /// <summary>Set by the view: pushes a painted rectangle to the GPU without a reload.</summary>
    public Action<TextureImage, Avalonia.PixelRect>? PushTextureRegion { get; set; }

    /// <summary>M360: the same stroke, for hosts that identify a texture by its ASSET PATH rather than by
    /// the TextureImage instance. GL keeps an image-to-GL-id map so the path is redundant there; the D3D11
    /// renderer's texture pool is keyed by path, so without it a painted stroke has nothing to look up.
    /// Kept as a second hook rather than widening the first, so the GL path is untouched.</summary>
    public Action<string, TextureImage>? PushPaintedTextureByPath { get; set; }
    /// <summary>Set by the view: shows the brush footprint on the surface (null centre hides it).</summary>
    public Action<System.Numerics.Vector3?, System.Numerics.Vector3, float, float>? ShowBrushRing { get; set; }
    /// <summary>Set by the view: force a mip rebuild once a stroke has finished.</summary>
    public Action? RebuildTextureMips { get; set; }

    [ObservableProperty] private bool _isPaintMode;
    [ObservableProperty] private Avalonia.Media.Color _paintColor = Avalonia.Media.Color.FromRgb(200, 60, 40);
    /// <summary>Brush radius in WORLD units. Texel density varies by orders of magnitude between meshes,
    /// so a texel-sized brush would be a speck on the ground and swallow a prop whole.</summary>
    [ObservableProperty] private double _paintRadius = 120;
    [ObservableProperty] private double _paintHardness = 0.5;
    [ObservableProperty] private double _paintOpacity = 1.0;
    [ObservableProperty] private double _paintSeamBleed = 3;
    /// <summary>M173: mask rotation in DEGREES (radians in the brush itself — degrees is what a slider
    /// should show).</summary>
    [ObservableProperty] private double _paintMaskAngle;
    [ObservableProperty] private PaintBlendMode _paintBlendMode = PaintBlendMode.Normal;
    [ObservableProperty] private BrushMaskOption? _paintMask;
    [ObservableProperty] private string _paintStatus = "";
    [ObservableProperty] private string _paintHover = "";
    [ObservableProperty] private bool _paintHoverIsWarning;
    [ObservableProperty] private int _paintedTextureCount;

    /// <summary>Textures stacked over themselves badly enough that a stroke would show up in several
    /// unrelated places. Periph_Vista is the whole distant backdrop — 33.7% of the map's world area with
    /// its texture covered 5.04x over — so a single dab would appear five times across the horizon.</summary>
    private static readonly string[] PaintBlockedTextures = { "periph_vista" };

    /// <summary>M173: every blend mode, for the dropdown.</summary>
    public Array PaintBlendModes { get; } = Enum.GetValues<PaintBlendMode>();

    /// <summary>M173: the stencil library — "None" plus the generated built-ins, plus anything the user
    /// imports. Built-ins are procedural rather than bundled images: nothing to download, crisp at any
    /// size, and no third-party licence attached.</summary>
    public ObservableCollection<BrushMaskOption> BrushMasks { get; } = new(
        new[] { BrushMaskOption.None }.Concat(BrushMask.BuiltIn.Select(m => new BrushMaskOption(m))));

    /// <summary>Import any image as a brush stencil — PNG, TGA, DDS or a Riot .tex. Colour is folded to
    /// luminance and multiplied by alpha, so both a black-on-white stamp and an RGBA sprite work.</summary>
    [RelayCommand]
    private async Task LoadBrushMaskAsync()
    {
        var path = await Dialogs.OpenFileAsync("Load a brush mask",
            new Avalonia.Platform.Storage.FilePickerFileType("Image")
            { Patterns = new[] { "*.png", "*.tga", "*.dds", "*.tex", "*.jpg", "*.jpeg", "*.bmp" } });
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var img = await Task.Run(() => TextureDecoder.Decode(bytes));
            var mask = BrushMask.FromImage(Path.GetFileNameWithoutExtension(path), img);
            var option = new BrushMaskOption(mask);
            BrushMasks.Add(option);
            PaintMask = option;
            _log.Success("Paint", $"Brush mask '{mask.Name}' loaded ({mask.Width}x{mask.Height}).");
        }
        catch (Exception ex)
        {
            _log.Warn("Paint", $"Could not load that image as a mask: {ex.Message}");
            PaintStatus = "Could not load that image as a brush mask — see the console.";
        }
    }

    private MapPaintSession? _paintSession;
    private bool _paintStrokeActive;

    // Mouse moves arrive far faster than the screen refreshes — a gaming mouse can deliver 1000 events a
    // second, and each one used to run a full stroke step plus a GPU upload. Painting is coalesced to
    // roughly one step per frame instead; nothing is lost, because StrokeTo interpolates from wherever the
    // last dab landed, so a skipped event just becomes part of the next segment.
    private readonly System.Diagnostics.Stopwatch _paintClock = System.Diagnostics.Stopwatch.StartNew();
    private long _lastPaintMs;
    private const long PaintIntervalMs = 15;
    private (System.Numerics.Vector3 Origin, System.Numerics.Vector3 Dir)? _pendingPaintRay;
    private long _lastHoverMs;
    private const long HoverIntervalMs = 33;

    partial void OnIsPaintModeChanged(bool value)
    {
        if (!value)
        {
            _paintSession = null; PaintHover = "";
            ShowBrushRing?.Invoke(null, System.Numerics.Vector3.UnitY, 0f, 0f);
            return;
        }
        if (_currentMap is null)
        {
            IsPaintMode = false;
            _log.Warn("Paint", "Open a map (.mapgeo) first.");
            return;
        }
        PaintStatus = "Drag on the map to paint. Painting edits the texture, so every mesh that shares it changes too.";
    }

    /// <summary>Build (or reuse) the session bound to the current map and its live texture set.</summary>
    private MapPaintSession? EnsurePaintSession()
    {
        if (_currentMap is not { } map || RayIndex is not { } index) return null;
        if (CurrentModelTextures is not { } textures) return null;
        if (_paintSession is not null) return _paintSession;

        // Per-submesh texture path + material, alongside the per-submesh TextureImage the viewport already
        // uploaded. Painting through those instances is what makes a stroke appear without a reload.
        var paths = new string?[map.Groups.Count];
        var materials = new string[map.Groups.Count];
        for (int i = 0; i < map.Groups.Count; i++)
        {
            materials[i] = map.Groups[i].Material ?? "";
            paths[i] = _currentMaterialToTexture is { } m2t && m2t.TryGetValue(materials[i], out var p) ? p : null;
        }

        var blocked = paths.Where(p => p is not null)
            .Where(p => PaintBlockedTextures.Any(b => p!.Contains(b, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p!).Distinct().ToList();

        return _paintSession = new MapPaintSession(index, textures, paths, materials,
            CurrentModelSubmeshVisible, blocked);
    }

    /// <summary>Hover feedback: what would a stroke here change?</summary>
    public void PaintHoverAt(System.Numerics.Vector3 origin, System.Numerics.Vector3 dir)
    {
        if (!IsPaintMode || EnsurePaintSession() is not { } session)
        {
            PaintHover = "";
            ShowBrushRing?.Invoke(null, System.Numerics.Vector3.UnitY, 0f, 0f);
            return;
        }
        var d = System.Numerics.Vector3.Normalize(dir);

        // The ring follows the cursor every move — it is the cheap part (one BVH ray, ~1.3 us) and the
        // thing that has to feel instant. The badge text behind it is throttled, because updating a bound
        // string forces a layout pass and nobody reads it 500 times a second.
        var hit = session.Pick(origin, d);
        ShowBrushRing?.Invoke(hit?.Position, hit?.Normal ?? System.Numerics.Vector3.UnitY,
            (float)PaintRadius, (float)PaintHardness);

        long now = _paintClock.ElapsedMilliseconds;
        if (now - _lastHoverMs < HoverIntervalMs) return;
        _lastHoverMs = now;

        if (hit is null || session.Probe(origin, d) is not { } probe)
        {
            if (PaintHover.Length > 0) { PaintHover = ""; PaintHoverIsWarning = false; }
            return;
        }
        PaintHoverIsWarning = probe.Warning is not null;
        var text = $"{Path.GetFileName(probe.AssetPath)}  {probe.Width}x{probe.Height}"
                   + (probe.Warning is { } w ? "  —  " + w : "");
        if (text != PaintHover) PaintHover = text;
    }

    public void BeginPaintStroke(System.Numerics.Vector3 origin, System.Numerics.Vector3 dir)
    {
        if (!IsPaintMode || EnsurePaintSession() is not { } session) return;
        session.BeginStroke();
        _paintStrokeActive = true;
        _lastPaintMs = 0;                       // the first dab of a stroke always lands immediately
        _pendingPaintRay = null;
        PaintStrokeMove(origin, dir);
    }

    public void PaintStrokeMove(System.Numerics.Vector3 origin, System.Numerics.Vector3 dir)
    {
        if (!_paintStrokeActive) return;
        _pendingPaintRay = (origin, dir);
        long now = _paintClock.ElapsedMilliseconds;
        if (now - _lastPaintMs < PaintIntervalMs) return;   // coalesced — see _paintClock
        _lastPaintMs = now;
        FlushPaint();
    }

    private void FlushPaint()
    {
        if (_pendingPaintRay is not { } ray || _paintSession is not { } session) return;
        _pendingPaintRay = null;
        var (origin, dir) = ray;
        var d = System.Numerics.Vector3.Normalize(dir);
        if (session.Pick(origin, d) is not { } hit) return;
        ShowBrushRing?.Invoke(hit.Position, hit.Normal, (float)PaintRadius, (float)PaintHardness);

        var brush = CurrentBrush;

        foreach (var painted in session.StrokeTo(hit.Position, d, brush))
        {
            session.MarkPainted(painted.Image, painted.AssetPath);
            PushTextureRegion?.Invoke(painted.Image, new Avalonia.PixelRect(
                painted.Rect.MinX, painted.Rect.MinY, painted.Rect.Width, painted.Rect.Height));
            // M360: same stroke to the D3D11 viewport. Whole-texture re-upload, not the dirty rect: the
            // pool stores one SRV per path and re-creating it is the only update entry point the renderer
            // has today. If that proves too slow mid-stroke, the fix is a sub-region path via
            // UpdateSubresource - but that is an optimisation to make when measured, not up front.
            PushPaintedTextureByPath?.Invoke(painted.AssetPath, painted.Image);
        }
    }

    /// <summary>The brush the next dab will use, assembled from the palette.</summary>
    private ReyEngine.Core.Painting.PaintBrush CurrentBrush => new()
    {
        Color = new System.Numerics.Vector3(PaintColor.R / 255f, PaintColor.G / 255f, PaintColor.B / 255f),
        Radius = (float)PaintRadius,
        Hardness = (float)PaintHardness,
        Opacity = (float)PaintOpacity,
        SeamBleedTexels = (float)PaintSeamBleed,
        BlendMode = PaintBlendMode,
        Mask = PaintMask?.Mask,
        MaskAngle = (float)(PaintMaskAngle * Math.PI / 180.0),
    };

    public void EndPaintStroke()
    {
        if (!_paintStrokeActive || _paintSession is not { } session) return;
        FlushPaint();                 // the last mouse position may have been coalesced away
        _paintStrokeActive = false;
        RebuildTextureMips?.Invoke(); // mips are throttled mid-stroke; the finished result must be right
        if (session.EndStroke() is not { } record) return;

        UndoService.PushApplied(new PaintStrokeCommand(record, _currentMap!, RepaintAfterUndo));
        PaintedTextureCount = session.PaintedTextures.Count;
        HasUnsavedPaint = PaintedTextureCount > 0;
        PaintStatus = $"{PaintedTextureCount} texture(s) painted — not saved yet.";
    }

    /// <summary>Undo/redo changed texels behind the viewport's back; push the affected rectangles.</summary>
    private void RepaintAfterUndo(PaintStrokeRecord record)
    {
        foreach (var e in record.Entries)
            PushTextureRegion?.Invoke(e.Image, new Avalonia.PixelRect(
                e.Rect.MinX, e.Rect.MinY, e.Rect.Width, e.Rect.Height));
    }

    [ObservableProperty] private bool _hasUnsavedPaint;

    /// <summary>M172d: write every painted texture back as a real .tex.
    ///
    /// Encoding happens ONCE, here — never per stroke. The painted master is uncompressed RGBA the whole
    /// time it is being edited, so a session of hundreds of strokes still costs a single BC generation.
    /// The source pixel format is preserved: 160 of base_srx's 169 diffuse textures are BC1, and letting
    /// them default to BC3 would exactly double each one (2,796,228 -> 5,592,444 bytes).</summary>
    [RelayCommand]
    private async Task SavePaintedTextures()
    {
        if (_paintSession is not { } session) return;
        var painted = session.PaintedTextures;
        if (painted.Count == 0) { PaintStatus = "Nothing painted yet."; return; }
        if (!Project.IsFolderProject && Project.OverridesDirectory is null)
        {
            PaintStatus = "This project has nowhere to write to — save the project first.";
            return;
        }

        PaintStatus = $"Encoding {painted.Count} texture(s)…";
        int written = 0, failed = 0;
        long bytes = 0;
        var notes = new List<string>();

        await Task.Run(() =>
        {
            foreach (var (image, path) in painted)
            {
                try
                {
                    // Read the ORIGINAL only to learn its container shape — format and whether it had
                    // mips. The pixels come from the painted master, not from a re-decode.
                    var original = ReadRecolorBase(new RecolorTarget(HashAlgorithms.WadPath(path), path))
                                   ?? TryReadAssetBytes(HashAlgorithms.WadPath(path));
                    var format = original is not null ? TexWriter.DetectFormat(original) : null;
                    if (format is null)
                    {
                        failed++;
                        notes.Add($"{Path.GetFileName(path)}: could not read its original format — skipped rather than guessing.");
                        continue;
                    }
                    bool mips = original is not null && TextureRecolor.HasMips(original);
                    var texBytes = TexWriter.Write(image, format.Value, mips);
                    WriteRecoloredAsset(path, texBytes, ".tex");
                    written++;
                    bytes += texBytes.Length;
                }
                catch (Exception ex) { failed++; notes.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
            }
        });

        foreach (var n in notes) _log.Warn("Paint", n);
        _log.Success("Paint", $"{written:n0} painted texture(s) written ({bytes / 1048576.0:F1} MB)"
                              + (failed > 0 ? $", {failed} failed" : "") + ".");
        PaintStatus = $"Saved {written:n0} texture(s) ({bytes / 1048576.0:F1} MB)"
                      + (failed > 0 ? $", {failed} failed — see the console" : "") + ".";
        HasUnsavedPaint = failed > 0 && written == 0;

        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        // Re-index so the project tree and the packer see the new files. Deliberately NOT reloading the
        // map: the viewport already shows the painted pixels, and a reload would throw away the session.
        if (Project.IsFolderProject) { BuildMounts(); BuildProjectTree(); }
        UpdateTitle();
    }

    public Action? ShowTextureRecolorWindow { get; set; }
    public Action? ShowUvEditorWindow { get; set; }
    public Action<MaterialBrowserContext>? ShowMaterialBrowserWindow { get; set; }   // M503b

    /// <summary>M503b: open the map-wide material browser.</summary>
    [RelayCommand]
    private void OpenMaterialBrowser()
    {
        if (_currentMap is null) { _log.Warn("Materials", "Open a map (.mapgeo) first."); return; }
        if (BuildMaterialBrowserContext() is { } context) ShowMaterialBrowserWindow?.Invoke(context);
    }

    /// <summary>
    /// M503b: run the audit over the open map and package it for the browser.
    ///
    /// <para>Every optional lookup is supplied here, because the audit deliberately SKIPS a check it cannot
    /// answer — the app is the only layer that knows where textures live, which permutations the install
    /// cooked, and what each shader declares as its default tint.</para>
    /// </summary>
    private MaterialBrowserContext? BuildMaterialBrowserContext()
    {
        if (_currentMap is not { } map || _currentMapEntry is not { } mapEntry) return null;
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
        { _log.Warn("Materials", "No materials.bin alongside this mapgeo."); return null; }

        try
        {
            var doc = Formats.Materials.MaterialDocument.Parse(ReadAsset(binEntry.PathHash), ResolveBinName, ResolveWadPath);
            var usage = Formats.Materials.MapMaterialAudit.UsageFrom(
                map.Groups.Select(g => (g.Material, g.MeshIndex)));

            var perms = ShaderPerms();
            var catalog = MaterialEditor.Catalog;

            var context = new Formats.Materials.MaterialAuditContext(
                TextureExists: TextureExistsByPath,
                CanSetMacro: perms is { IsAvailable: true } ? (m, macro) => perms.CanSetMacro(m, macro, "1") : null,
                ShaderDeclaresMacro: perms is { IsAvailable: true } ? perms.DeclaresMacroAxis : null,
                SuggestFixes: perms is { IsAvailable: true } ? perms.SuggestFixes : null,   // M507
                ExactKey: perms is { IsAvailable: true }
                    ? m => perms.TryExactKey(m, out string key, out bool cooked, out string stage)
                        ? (true, cooked, stage.Length > 0 ? $"{stage}: {key}" : key)
                        : (false, false, "")
                    : null,
                LightmapUvCoverage: BuildLightmapUvCoverage(map),   // M509
                ShaderTintDefault: catalog is null ? null : shader =>
                {
                    var def = catalog.Find(shader);
                    var p = def?.Parameters?.FirstOrDefault(x =>
                        x.Name.Equals("TintColor", StringComparison.OrdinalIgnoreCase));
                    return p is null ? null : new System.Numerics.Vector4(p.X, p.Y, p.Z, p.W);
                });

            var rows = Formats.Materials.MapMaterialAudit.Audit(doc.Materials, usage, context);
            _log.Info("Materials", $"{rows.Count:n0} material(s), {rows.Count(r => r.HasIssues):n0} with findings.");

            return new MaterialBrowserContext(
                System.IO.Path.GetFileName(binEntry.Path),
                rows,
                OpenInEditor: name =>
                {
                    // The editor loads a bin through the asset-selection path, so this jumps to the
                    // material rather than trying to drive that from here. When the bin is not the one
                    // loaded, say so instead of silently selecting nothing.
                    var match = MaterialEditor.Materials
                        .FirstOrDefault(m => string.Equals(m.Model.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        _log.Info("Materials", $"Select {binEntry.DisplayName} in the browser first — "
                                             + $"the material editor is not holding it, so '{name}' cannot be shown.");
                        return;
                    }
                    MaterialEditor.SelectedMaterial = match;
                    InspectorTab = InspectorTabs.Materials;
                    AssetDataExpanded = true;
                },
                SelectMeshesUsing: names =>
                {
                    var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
                    var meshIndices = map.Groups.Where(g => wanted.Contains(g.Material) && g.MeshIndex >= 0)
                                               .Select(g => g.MeshIndex).ToHashSet();
                    var meshes = map.Meshes.Where(m => meshIndices.Contains(m.Index)).ToList();
                    if (meshes.Count == 0) { _log.Info("Materials", "No mesh uses those materials."); return; }
                    _selection.SetMany(meshes);
                    SetMapContentSelection(MapContent.AllMapPieces
                        .Where(p => meshIndices.Contains(p.MeshIndex)).ToList(), null);
                    _log.Success("Materials", $"Selected {meshes.Count:n0} mesh(es) using {wanted.Count} material(s).");
                },
                Refresh: () =>
                {
                    if (BuildMaterialBrowserContext() is { } fresh) ShowMaterialBrowserWindow?.Invoke(fresh);
                },
                Presets: BuildMaterialPresetService(binEntry),
                Repair: () => RepairMaterialWireForms(binEntry),
                RemoveUnused: names => RemoveUnusedMaterialsAsync(binEntry, map.Groups.Select(g => g.Material), names));
        }
        catch (Exception ex) { _log.Error("Materials", "Could not audit the materials: " + ex.Message); return null; }
    }

    /// <summary>
    /// M507: rewrite material containers whose wire form the client skips.
    ///
    /// <para>One byte per container, and it decides whether the game reads the field at all. This is a
    /// general, measured transformation rather than a hand-patch: the elements are moved untouched, and
    /// what Riot writes for each field was censused over the 18 shipped map wads.</para>
    /// </summary>
    private void RepairMaterialWireForms(WadAssetEntry binEntry)
    {
        if (!GuardEditable(binEntry)) return;
        if (Project.ProjectFilePath is null && Project.SourceWadPath is null)
        { _log.Warn("Materials", "Create or open a project before repairing."); return; }

        try
        {
            var tree = Formats.Meta.SafeBinTree.Parse(ReadAsset(binEntry.PathHash));
            var repaired = Formats.Materials.MaterialContainerShape.Repair(
                tree, ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a("StaticMaterialDef"));
            if (repaired.Count == 0)
            { _log.Info("Materials", "Every material container already has the wire form Riot ships."); return; }

            using var ms = new MemoryStream();
            tree.Write(ms);
            byte[] bytes = ms.ToArray();

            var issues = Formats.Meta.ModShapeValidator.ValidateBin(
                Formats.Meta.SafeBinTree.Parse(bytes), bytes, ResolveBinName);
            if (issues.Any(i => i.Category == "container-wire-form"))
            { _log.Error("Materials", "The repair did not take — not saved."); return; }

            string savedTo = SaveMaterialsBin(binEntry, bytes);

            foreach (string line in repaired.Take(10)) _log.Info("Materials", "  " + line);
            _log.Success("Materials", $"Repaired {repaired.Count:n0} material container(s) the client would "
                                    + $"have skipped. Saved to {savedTo}.");
        }
        catch (Exception ex) { _log.Error("Materials", "Repair failed: " + ex.Message); }
    }

    /// <summary>M675: one materials.bin write for every edit made from the Materials window - the project
    /// file when the bin is one, the override store otherwise - plus the bookkeeping that makes the change
    /// visible: node status, dirty flag, title, and the materials revision both viewports watch. Returns
    /// where the bytes went.</summary>
    private string SaveMaterialsBin(WadAssetEntry binEntry, byte[] bytes)
    {
        string savedTo;
        if (TryWriteToProjectFile(binEntry, bytes, out var projectFile)) savedTo = projectFile;
        else
        {
            savedTo = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, bytes, ".bin");
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = binEntry.PathHash,
                ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                OverrideFile = savedTo,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            _overrides.SaveTo(Project);
        }
        SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        UpdateTitle();
        NotifyMaterialsChanged();
        return savedTo;
    }

    /// <summary>
    /// M675: remove the StaticMaterialDefs no mesh uses from the map's materials.bin, from the Materials
    /// window.
    ///
    /// <para>The window's "unused" is "no mesh in this mapgeo draws it", which is necessary but not
    /// sufficient: a VFX system or a prop can still link a material by hash, and removing one of those is
    /// the classic way to make a map crash on load. So the decision belongs to
    /// <see cref="Formats.Materials.MapMaterialFactory.PlanUnusedStaticMaterials"/> - reachability from
    /// every non-material object plus the mapgeo's names - and the candidates the window hands over only
    /// NARROW it. The plan is shown and confirmed before anything is written, the bin as it was goes to
    /// <c>.reyengine/cleanup/</c> beside the file cleanups' own backups, and the removed names go to the
    /// log. Returns true when the bin changed.</para>
    /// </summary>
    private async Task<bool> RemoveUnusedMaterialsAsync(WadAssetEntry binEntry, IEnumerable<string> usedMaterials,
        IReadOnlyList<string> candidates)
    {
        if (!GuardEditable(binEntry)) return false;
        if (Project.ProjectFilePath is null && Project.SourceWadPath is null)
        { _log.Warn("Materials", "Create or open a project before removing materials."); return false; }

        static string Short(string s) { int i = s.LastIndexOf('/'); return i >= 0 ? s[(i + 1)..] : s; }
        try
        {
            byte[] before = ReadAsset(binEntry.PathHash);
            var used = usedMaterials.Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var plan = Formats.Materials.MapMaterialFactory.PlanUnusedStaticMaterials(before, used, candidates, out var planError);
            if (plan is null) { _log.Error("Materials", "Could not plan the cleanup: " + planError); return false; }

            string Name(uint h) => candidates.FirstOrDefault(c => Formats.Materials.MapMaterialFactory.MaterialHash(c) == h)
                                   ?? ResolveBinName(h) ?? $"0x{h:x8}";
            if (plan.Remove.Count == 0)
            {
                _log.Info("Materials", plan.KeptByLink.Count > 0
                    ? $"Nothing to remove: the {plan.KeptByLink.Count:n0} material(s) no mesh uses are still linked from "
                      + "other objects in the bin (VFX, props), and the game needs those."
                    : "Nothing to remove: every material in the bin is drawn by a mesh.");
                return false;
            }

            var names = plan.Remove.Select(Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            string listed = string.Join("\n", names.Take(12).Select(n => "  " + Short(n)))
                            + (names.Count > 12 ? $"\n  … and {names.Count - 12:n0} more" : "");
            string kept = plan.KeptByLink.Count > 0
                ? $" {plan.KeptByLink.Count:n0} other unused material(s) stay: a VFX system, prop or another object still links to them."
                : "";
            if (PromptOwner is not null && !await Views.PromptWindow.ConfirmAsync(PromptOwner, "Remove Unused Materials",
                    $"Remove {names.Count:n0} material(s) from {binEntry.DisplayName}?\n\n"
                    + "No mesh in this map uses them and nothing else in the bin links to them." + kept
                    + "\n\nA copy of the bin as it is now goes to .reyengine/cleanup/ first.\n\n" + listed,
                    "Remove"))
                return false;

            var after = Formats.Materials.MapMaterialFactory.RemoveUnusedStaticMaterials(before, used,
                out int removed, out var error, candidates);
            if (after is null) { _log.Error("Materials", "Cleanup failed: " + error); return false; }
            if (removed == 0) { _log.Info("Materials", "Nothing was removed."); return false; }

            string? backup = BackupMaterialsBin(binEntry, before, names);
            string savedTo = SaveMaterialsBin(binEntry, after);
            foreach (string n in names.Take(20)) _log.Info("Materials", "  removed " + Short(n));
            if (names.Count > 20) _log.Info("Materials", $"  … and {names.Count - 20:n0} more (all of them in the backup's removed.txt)");
            _log.Success("Materials", $"Removed {removed:n0} unused material(s) from {binEntry.DisplayName}. Saved to {savedTo}."
                                    + (backup is null ? "" : $" Backup: {backup}."));
            return true;
        }
        catch (Exception ex) { _log.Error("Materials", "Cleanup failed: " + ex.Message); return false; }
    }

    /// <summary>The bin as it was before a cleanup, under the same <c>.reyengine/cleanup/</c> root the file
    /// cleanups use, with the removed names beside it. Null when there is no project folder to keep it in
    /// or the copy fails - logged, not fatal, because the removal was confirmed.</summary>
    private string? BackupMaterialsBin(WadAssetEntry binEntry, byte[] before, IReadOnlyList<string> removedNames)
    {
        if (Project.RootPath is null) return null;
        try
        {
            string dir = System.IO.Path.Combine(ReyEngine.Core.Cleanup.CleanupExecutor.BackupRoot(Project.RootPath),
                "materials-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            string file = System.IO.Path.GetFileName(binEntry.IsResolved ? binEntry.Path : binEntry.DisplayName);
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) file = file.Replace(c, '_');
            File.WriteAllBytes(System.IO.Path.Combine(dir, file), before);
            File.WriteAllLines(System.IO.Path.Combine(dir, "removed.txt"), removedNames);
            return dir;
        }
        catch (Exception ex)
        {
            _log.Warn("Materials", "The bin could not be backed up first: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// M509: per material, how many of the meshes drawn with it carry Texcoord7.
    ///
    /// <para>The lightmap UV lives on the MESH and the decision to sample it lives in the MATERIAL, so
    /// neither file can answer this alone — which is why a map can be entirely valid on both sides and
    /// still render wrong. Measured on a real ported map: 81 of 384 submeshes were drawn by a
    /// baked-lighting shader on geometry with no Texcoord7, and the meshes that had it looked fine.</para>
    /// </summary>
    private static Func<Formats.Materials.MaterialBinding, (int With, int Without)> BuildLightmapUvCoverage(
        MapGeoAsset map)
    {
        var byMaterial = new Dictionary<string, (int With, int Without)>(StringComparer.OrdinalIgnoreCase);
        var counted = new HashSet<(string, int)>();
        foreach (var group in map.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Material) || group.MeshIndex < 0) continue;
            if (!counted.Add((group.Material, group.MeshIndex))) continue;   // one mesh counts once
            var mesh = map.Meshes.FirstOrDefault(x => x.Index == group.MeshIndex);
            if (mesh is null) continue;
            byMaterial.TryGetValue(group.Material, out var c);
            byMaterial[group.Material] = mesh.HasLightmapUv ? (c.With + 1, c.Without) : (c.With, c.Without + 1);
        }
        return m => byMaterial.TryGetValue(m.Name, out var c) ? c : (0, 0);
    }

    // ---- M504: material presets + bulk apply ------------------------------------------------------

    private Formats.Materials.MaterialPresetLibrary? _materialPresets;

    /// <summary>Saved next to the editor settings rather than inside a project: a preset is a way of
    /// working, and the point of one is to reuse it on the NEXT map too.</summary>
    private Formats.Materials.MaterialPresetLibrary MaterialPresets =>
        _materialPresets ??= Formats.Materials.MaterialPresetLibrary.Load();

    private MaterialPresetService BuildMaterialPresetService(WadAssetEntry binEntry) => new(
        MaterialPresets,
        () => MaterialPresets.Save(),
        (material, name) => CaptureMaterialPreset(binEntry, material, name),
        (preset, names, parts, write) => RunMaterialPreset(binEntry, preset, names, parts, write));

    private Formats.Materials.MaterialPreset? CaptureMaterialPreset(
        WadAssetEntry binEntry, string materialName, string presetName)
    {
        try
        {
            var doc = Formats.Materials.MaterialDocument.Parse(ReadAsset(binEntry.PathHash), ResolveBinName, ResolveWadPath);
            var source = doc.Materials.FirstOrDefault(m =>
                m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            { _log.Warn("Materials", $"'{materialName}' is not in {binEntry.DisplayName}."); return null; }

            var preset = Formats.Materials.MaterialPreset.Capture(source, presetName);
            _log.Success("Materials", $"Captured preset '{presetName}': {preset.Summary}");
            return preset;
        }
        catch (Exception ex) { _log.Error("Materials", "Could not capture the preset: " + ex.Message); return null; }
    }

    /// <summary>The shader-cache and texture lookups the applier needs. Anything missing SKIPS its check
    /// rather than guessing, and the applier says so in its notes.</summary>
    private Formats.Materials.MaterialPresetContext BuildMaterialPresetContext() => new(
        TextureExists: TextureExistsByPath,
        MacroSupport: CachedMacroSupport);

    /// <summary>
    /// M506: the one macro-safety oracle, shared by the preset applier and the material editor's inline
    /// badges. Memoised because each miss builds a probe material and re-parses a bin, and the editor asks
    /// about roughly a dozen defines every time a material is selected or its shader changes.
    ///
    /// <para>Returns Unknown — never a guess — when there is no shader cache or no catalogue. Callers show
    /// that as "unchecked".</para>
    /// </summary>
    private readonly Dictionary<string, LegacyMapPorter.MacroSupport> _macroSupportCache = new(StringComparer.Ordinal);

    private LegacyMapPorter.MacroSupport CachedMacroSupport(
        string shader, IReadOnlyDictionary<string, bool> switches, string macro, string value)
    {
        var perms = ShaderPerms();
        var catalog = MaterialEditor.Catalog;
        if (perms is not { IsAvailable: true } || catalog is null) return LegacyMapPorter.MacroSupport.Unknown;

        // The switch set is part of the key, not decoration: it is part of the permutation the client
        // resolves, so two materials on the same shader can get different answers.
        string key = shader + "" + macro + "" + value + ""
                   + string.Join(",", switches.Where(s => s.Value)
                       .Select(s => s.Key.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal));
        if (_macroSupportCache.TryGetValue(key, out var hit)) return hit;

        var support = MacroSupportFor(perms, catalog, shader, macro, switches, value);
        // Unknown is not cached: it usually means the catalogue has not finished loading, and caching it
        // would freeze "unchecked" onto the UI for the rest of the session.
        if (support != LegacyMapPorter.MacroSupport.Unknown) _macroSupportCache[key] = support;
        return support;
    }

    /// <summary>
    /// Preview or perform a preset application over a set of materials.
    ///
    /// <para>Both directions re-read the bin and run the same <see cref="Formats.Materials.MaterialPresetApplier"/>
    /// walk, so the diff the user approved is computed from the same bytes and the same rules as the edit
    /// that follows it. The save is the standard bin path: shape-validate, project file first, override
    /// store second (M417 — an override written for an asset the project ships is dropped at build).</para>
    /// </summary>
    private IReadOnlyList<Formats.Materials.MaterialPresetPlanRow> RunMaterialPreset(
        WadAssetEntry binEntry, Formats.Materials.MaterialPreset preset, IReadOnlyList<string> names,
        Formats.Materials.MaterialPresetParts parts, bool write)
    {
        var empty = Array.Empty<Formats.Materials.MaterialPresetPlanRow>();
        if (write && !GuardEditable(binEntry)) return empty;
        if (write && Project.ProjectFilePath is null && Project.SourceWadPath is null)
        { _log.Warn("Materials", "Create or open a project before applying a preset."); return empty; }

        try
        {
            var doc = Formats.Materials.MaterialDocument.Parse(ReadAsset(binEntry.PathHash), ResolveBinName, ResolveWadPath);
            var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var targets = doc.Materials.Where(m => wanted.Contains(m.Name)).ToList();
            var context = BuildMaterialPresetContext();

            var rows = (write
                ? Formats.Materials.MaterialPresetApplier.Apply(preset, targets, parts, context)
                : Formats.Materials.MaterialPresetApplier.Plan(preset, targets, parts, context)).ToList();

            // A name the browser listed that the bin does not contain is a MissingMaterial row — the mesh
            // asks for it and nothing provides it. Dropping it here would make the report claim a coverage
            // it does not have.
            foreach (string name in names)
                if (!targets.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    rows.Add(new Formats.Materials.MaterialPresetPlanRow(name,
                        Array.Empty<Formats.Materials.MaterialPresetChange>(),
                        new[] { "this material is not in the bin — a mesh names it, but there is nothing to edit" },
                        Array.Empty<string>()));

            if (!write) return rows;
            if (!rows.Any(r => r.HasChanges))
            { _log.Info("Materials", "Nothing to apply — every selected material already matches."); return rows; }

            byte[] bytes = doc.Serialize();
            var issues = Formats.Meta.ModShapeValidator.ValidateBin(
                Formats.Meta.SafeBinTree.Parse(bytes), bytes, ResolveBinName);
            if (issues.Count > 0)
            {
                foreach (var i in issues.Take(5)) _log.Error("Materials", $"[{i.Category}] {i.ObjectName}: {i.Detail}");
                _log.Error("Materials", $"{issues.Count} shape issue(s) — the preset was NOT saved.");
                return rows.Select(r => new Formats.Materials.MaterialPresetPlanRow(r.MaterialName,
                    Array.Empty<Formats.Materials.MaterialPresetChange>(),
                    new[] { "not saved — the resulting bin failed shape validation" },
                    r.Notes)).ToList();
            }

            string savedTo;
            if (TryWriteToProjectFile(binEntry, bytes, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, bytes, ".bin");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = binEntry.PathHash,
                    ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();
            NotifyMaterialsChanged();

            int changed = rows.Count(r => r.HasChanges);
            int refused = rows.Count(r => r.HasBlockers);
            _log.Success("Materials", $"Applied preset '{preset.Name}' to {changed:n0} material(s)"
                + (refused > 0 ? $", {refused:n0} with refusals" : "") + $". Saved to {savedTo}.");
            return rows;
        }
        catch (Exception ex)
        {
            _log.Error("Materials", $"Preset {(write ? "apply" : "preview")} failed: " + ex.Message);
            return empty;
        }
    }
    public Action<RitobinTarget>? ShowRitobinEditorWindow { get; set; }   // M498

    /// <summary>
    /// M498: open the selected .bin as ritobin text.
    ///
    /// <para>The conversion is byte-exact in both directions (M496/M497), so this is a real editing surface
    /// rather than a viewer: what comes back out is the file that went in, plus whatever was changed. The
    /// save goes through the same project-file-first path every other bin edit uses, because an override
    /// written for an asset the project ships is silently dropped at build.</para>
    /// </summary>
    [RelayCommand]
    private void OpenRitobinEditor()
    {
        var entry = ContextNode?.Entry ?? SelectedNode?.Entry;
        if (entry is null || entry.Type != AssetType.Bin)
        { _log.Warn("Ritobin", "Select a .bin first — this edits bins as ritobin text."); return; }
        if (!GuardEditable(entry)) return;

        ShowRitobinEditorWindow?.Invoke(new RitobinTarget(
            entry.DisplayName,
            () => ReadAsset(entry.PathHash),
            async bytes =>
            {
                if (!await EnsureProjectSavedAsync()) throw new InvalidOperationException("The project was not saved.");

                string savedTo;
                if (TryWriteToProjectFile(entry, bytes, out var projectFile)) savedTo = projectFile;
                else
                {
                    savedTo = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, ".bin");
                    _overrides.Set(new ProjectAssetOverride
                    {
                        PathHash = entry.PathHash,
                        ResolvedPath = entry.IsResolved ? entry.Path : null,
                        OverrideFile = savedTo,
                        AddedUtc = DateTime.UtcNow.ToString("o"),
                    });
                    _overrides.SaveTo(Project);
                }
                SetNodeStatus(entry.PathHash, AssetStatus.Modified);
                Project.IsDirty = true;
                if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
                UpdateTitle();
                return savedTo;
            },
            ResolveBinName,
            h => _resolver.Database.TryGetPath(h, out var p) ? p : null,
            m => _log.Info("Ritobin", m)));
    }

    /// <summary>M492: open the second-UV viewer/editor for the current map.</summary>
    [RelayCommand]
    private void OpenUvEditor()
    {
        if (_currentMap is null)
        { _log.Warn("MapGeo", "Open a map (.mapgeo) first — the UV editor works on its meshes."); return; }
        ShowUvEditorWindow?.Invoke();
    }

    /// <summary>
    /// M502: can the shader the porter is about to use actually take this macro?
    ///
    /// <para>Answered by BUILDING the material the porter would write and asking the real
    /// <see cref="Formats.Materials.ShaderPermutationIndex.CanSetMacro"/> about it — the same guard M491
    /// added for the Dynamic (unlit) button. The tempting shortcut, scanning the shader's TOC define pool
    /// for the macro at this value, was tried and thrown away: it reported DefaultEnv_Flat_AlphaTest as
    /// safe, which is precisely the material League refused in M486. The pool proves an axis value is
    /// cooked in SOME permutation; only the full resolve answers whether the exact key this material
    /// requests exists.</para>
    ///
    /// <para>The probe material is built in memory from an empty bin and thrown away.</para>
    ///
    /// <para>M504 added <paramref name="switches"/> and <paramref name="value"/>. The permutation key
    /// combines every axis at once, so a probe built without the switches the material will actually carry
    /// answers a NEIGHBOURING question — which is the shape of mistake M486 and M502 both made in turn.
    /// The porter passes null (its generated materials take the shader's own switch defaults); the preset
    /// applier passes the set the material will have after the preset lands.</para>
    /// </summary>
    private LegacyMapPorter.MacroSupport MacroSupportFor(
        Formats.Materials.ShaderPermutationIndex perms, Formats.Shaders.ShaderCatalog catalog,
        string shader, string macro,
        IReadOnlyDictionary<string, bool>? switches = null, string value = "1")
    {
        if (!perms.DeclaresMacroAxis(shader, macro)) return LegacyMapPorter.MacroSupport.NotDeclared;

        var def = catalog.Find(shader);
        if (def is null) return LegacyMapPorter.MacroSupport.Unknown;
        try
        {
            var seed = new LeagueToolkit.Core.Meta.BinTree(
                Array.Empty<LeagueToolkit.Core.Meta.BinTreeObject>(), Array.Empty<string>());
            using var ms = new MemoryStream();
            seed.Write(ms);

            var made = Formats.Materials.MapMaterialFactory.CreateFromShader(
                ms.ToArray(), "PortProbe/" + shader, def, out _, samplerOverrides: null,
                switches: switches,
                macros: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [macro] = true });
            if (made is null) return LegacyMapPorter.MacroSupport.Unknown;

            var probe = Formats.Materials.MaterialDocument.Parse(made, ResolveBinName, ResolveWadPath)
                .Materials.FirstOrDefault(m => m.Name == "PortProbe/" + shader);
            if (probe is null) return LegacyMapPorter.MacroSupport.Unknown;

            return perms.CanSetMacro(probe, macro, value)
                ? LegacyMapPorter.MacroSupport.Cooked
                : LegacyMapPorter.MacroSupport.NotCooked;
        }
        catch { return LegacyMapPorter.MacroSupport.Unknown; }
    }

    /// <summary>M492: everything the UV editor needs about the open map, so it never reaches into project
    /// or asset state itself.</summary>
    public UvEditorContext? GatherUvEditorContext()
    {
        if (_currentMap is not { } map || _currentMapBytes is null || _currentMapEntry is not { } entry)
            return null;
        return new UvEditorContext(
            map, _currentMapBytes, ExtendedChannelMaterialsFor(entry.Path),
            _selection.Items.Select(m => m.Index).ToList(),
            MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes)
                || MapContent.AddedMeshes.Count > 0,
            System.IO.Path.GetFileName(entry.Path));
    }

    /// <summary>
    /// M492: validate and save a mapgeo the UV editor rewrote.
    ///
    /// <para>Validation is the same shape AddTexcoord7 uses and for the same reason: our own writer and our
    /// own reader agree with each other by construction, so a rewrite that decodes cleanly has proved very
    /// little. Here the check is that the bytes decode AND the mesh count survives — an in-place UV write
    /// must not change either, and if it did, something touched the layout that had no business doing so.</para>
    /// </summary>
    public async Task SaveUvEditorResultAsync(byte[] bytes, Formats.MapGeo.UvEditResult result)
    {
        if (_currentMapEntry is not { } entry) throw new InvalidOperationException("No map is open.");
        if (!GuardEditable(entry)) throw new InvalidOperationException("This map is a read-only Riot asset.");
        if (!await EnsureProjectSavedAsync()) throw new InvalidOperationException("The project was not saved.");

        int meshesBefore = _currentMap?.Meshes.Count ?? -1;
        var extended = ExtendedChannelMaterialsFor(entry.Path);
        var check = await Task.Run(() => MapGeoDecoder.Decode(bytes, extended));
        if (meshesBefore >= 0 && check.Meshes.Count != meshesBefore)
            throw new InvalidDataException($"The rewritten mapgeo has {check.Meshes.Count} mesh(es) instead of "
                                         + $"{meshesBefore} — a UV write must not change the layout. Not saved.");

        string savedTo;
        if (TryWriteToProjectFile(entry, bytes, out var projectFile)) savedTo = projectFile;
        else
        {
            savedTo = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, ".mapgeo");
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = entry.PathHash,
                ResolvedPath = entry.IsResolved ? entry.Path : null,
                OverrideFile = savedTo,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            _overrides.SaveTo(Project);
        }
        SetNodeStatus(entry.PathHash, AssetStatus.Modified);
        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        UpdateTitle();

        foreach (var s in result.Skipped.Take(5)) _log.Warn("MapGeo", s);
        _log.Success("MapGeo", $"{result.Summary} Saved to {savedTo} ({bytes.Length:n0} bytes).");
        await LoadMapGeoAsync(entry);
    }

    [RelayCommand]
    private void OpenTextureRecolor()
    {
        if (_currentMap is null) { _log.Warn("Recolor", "Open a map (.mapgeo) first — the tool recolours its surfaces, placed mobs / props and lightmaps."); return; }
        ShowTextureRecolorWindow?.Invoke();
    }

    /// <summary>
    /// <para>Every colour-bearing texture used by the open map, its placed mobs / animated props and its
    /// baked LIGHTMAP atlases, ranked by how many scene references each one has. Normal maps, masks and
    /// gradients stay excluded: those are data rather than colour, and hue-shifting them would corrupt
    /// the lighting instead of recolouring the map. Prop textures are resolved from the same placed skin
    /// bins and champion material bindings as the viewport, so Baron, dragons, camps and shopkeepers do
    /// not silently keep their original diffuse colour.</para>
    ///
    /// <para>Recolouring one tints the map's baked lighting rather than its surfaces — warming or
    /// cooling shadowed areas, for instance — which is a genuinely different effect from recolouring a
    /// diffuse texture and is why lightmap rows carry their own badge in the picker rather than being
    /// silently mixed in.</para>
    /// </summary>
    public IReadOnlyList<RecolorTargetViewModel> GatherRecolorTargets()
    {
        if (_currentMap is not { } map || _currentMapEntry is not { } mapEntry)
            return Array.Empty<RecolorTargetViewModel>();

        var recolored = Project.TextureRecolors.Select(r => r.PathHash).ToHashSet();
        var candidates = new Dictionary<ulong, RecolorCandidate>();

        void Add(string? path, int mapUses = 0, int propUses = 0, int lightmapUses = 0)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string clean = path.Replace('\\', '/').Trim();
            ulong hash = HashAlgorithms.WadPath(clean);
            if (!candidates.TryGetValue(hash, out var candidate))
                candidates[hash] = candidate = new RecolorCandidate(clean);
            candidate.MapUses += mapUses;
            candidate.PropUses += propUses;
            candidate.LightmapUses += lightmapUses;
        }

        // ---- diffuse: every texture a material actually samples ----
        // Guarded rather than an early return for the whole method — a map whose materials.bin fails to
        // resolve should still offer its lightmaps rather than nothing at all.
        if (TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
        {
            var names = map.Groups.Select(g => g.Material).Where(m => m.Length > 0).Distinct().ToList();
            var (materialToTexture, _, _) = ResolveMapMaterials(binEntry, names);
            var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var tex in materialToTexture.Values)
                if (!string.IsNullOrEmpty(tex)) byPath[tex] = byPath.GetValueOrDefault(tex) + 1;

            foreach (var (path, uses) in byPath) Add(path, mapUses: uses);
        }

        // ---- mobs / animated props: diffuse textures from every placed character skin ----
        // Resolve each unique skin bin once. PropTextureCatalog also includes SkinMeshProperties.texture,
        // the fallback used by submeshes without a material override, and aggregates repeated placements.
        if (CurrentModelProps is { Count: > 0 } props)
        {
            var propTextures = PropTextureCatalog.Discover(
                props.GroupBy(prop => prop.Skin, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new PropSkinUsage(group.Key, group.Count())),
                skin => ReadAssetByPath($"data/{skin.ToLowerInvariant()}.bin"),
                ResolveBinName, ResolveWadPath);
            foreach (var texture in propTextures) Add(texture.AssetPath, propUses: texture.Placements);
        }

        // ---- lightmaps: every baked-light atlas the map references ----
        // The same enumeration LightBaker uses to decide what to re-bake, reused here so "every atlas
        // this map has" cannot drift into two different answers depending on which tool you opened.
        foreach (var atlas in Formats.Baking.LightBaker.EnumerateAtlases(map))
        {
            int uses = map.Groups.Count(g => string.Equals(g.LightmapTexture, atlas, StringComparison.OrdinalIgnoreCase));
            Add(atlas, lightmapUses: uses);
        }

        // Header triage happens once after all discovery passes. A texture shared by map geometry and a
        // prop becomes one row (MAP+PROP), one write and one project record rather than two competing rows.
        var list = new List<RecolorTargetViewModel>(candidates.Count);
        foreach (var (hash, candidate) in candidates)
        {
            var bytes = TryReadAssetBytes(hash);
            if (bytes is null || !TextureRecolor.IsSupported(bytes)) continue;
            var kind = candidate.LightmapUses > 0
                ? RecolorTargetKind.Lightmap
                : candidate.MapUses > 0 && candidate.PropUses > 0
                    ? RecolorTargetKind.MapAndPropDiffuse
                    : candidate.PropUses > 0 ? RecolorTargetKind.PropDiffuse : RecolorTargetKind.Diffuse;
            list.Add(new RecolorTargetViewModel
            {
                Target = new RecolorTarget(hash, candidate.Path),
                Name = Path.GetFileName(candidate.Path),
                Folder = Path.GetDirectoryName(candidate.Path)?.Replace('\\', '/') ?? "",
                Kind = kind,
                MapUses = candidate.MapUses,
                PropUses = candidate.PropUses,
                LightmapUses = candidate.LightmapUses,
                IsRecolored = recolored.Contains(hash),
            });
        }

        return list.OrderBy(t => t.Kind)
                   .ThenByDescending(t => t.UsedBy)
                   .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    private sealed class RecolorCandidate(string path)
    {
        public string Path { get; } = path;
        public int MapUses { get; set; }
        public int PropUses { get; set; }
        public int LightmapUses { get; set; }
    }

    private byte[]? TryReadAssetBytes(ulong hash)
    {
        try { return ReadAsset(hash); } catch { return null; }
    }

    /// <summary>The PRISTINE bytes to recolour from — Riot's original, never the project's own recoloured
    /// copy. This is the whole reason the tool is safe to re-run: BC compression is lossy, so recolouring
    /// an already-recoloured texture would add a generation of loss on every pass.
    ///
    /// Riot's reference WAD is the normal source and costs nothing. When a project has no reference
    /// mounted (so <see cref="AssetMountService.ReadFallback"/> has nothing to give) the first recolour
    /// stashed a snapshot instead; that snapshot is used from then on.</summary>
    public byte[]? ReadRecolorBase(RecolorTarget target)
    {
        var record = Project.TextureRecolors.FirstOrDefault(r => r.PathHash == target.PathHash);
        if (record?.BaseSnapshot is { } snap && ResolveSnapshotPath(snap) is { } snapPath && File.Exists(snapPath))
            return File.ReadAllBytes(snapPath);

        if (_mounts?.ReadFallback(target.PathHash) is { } riot) return riot;

        // No reference and no snapshot: the project's own copy is the closest thing to an original we
        // have. Valid as a base only until we recolour over it — see CheckOutRecolorBase.
        return record is null ? TryReadAssetBytes(target.PathHash) : null;
    }

    /// <summary>What the RUN reads from — the same bytes as <see cref="ReadRecolorBase"/>, except that in
    /// the no-Riot-reference case it also stashes them before handing them over.
    ///
    /// That has to happen HERE and not after the run: once the recoloured file is written, the project's
    /// copy is no longer an original, so a snapshot taken afterwards would preserve the edit instead of
    /// the source and every later re-tune would compound BC loss.</summary>
    private byte[]? CheckOutRecolorBase(RecolorTarget target)
    {
        var bytes = ReadRecolorBase(target);
        if (bytes is null) return null;

        bool needsSnapshot = _mounts?.ReadFallback(target.PathHash) is null
                             && !Project.TextureRecolors.Any(r => r.PathHash == target.PathHash);
        if (needsSnapshot) _pendingSnapshots[target.PathHash] = SnapshotOriginal(target, bytes);
        return bytes;
    }

    /// <summary>Snapshots taken during the current run, keyed by hash — folded into the project records
    /// by <see cref="PersistRecolors"/> once the run succeeds.</summary>
    private readonly Dictionary<ulong, string?> _pendingSnapshots = new();

    private string? ResolveSnapshotPath(string relative) =>
        Project.WorkspaceDirectory is { } ws ? Path.Combine(ws, relative) : null;

    public Services.TextureRecolorService? MakeRecolorService()
    {
        bool canWrite = (Project.IsFolderProject && Project.RootPath is not null)
                        || Project.OverridesDirectory is not null;
        return canWrite ? new Services.TextureRecolorService(CheckOutRecolorBase, WriteRecoloredAsset) : null;
    }

    /// <summary>Actionable source-health message shown by the recolour tool before a large run.</summary>
    public string? GetRecolorSourceWarning()
    {
        var status = GameReferenceLibrary.Inspect(Project.GameDirectory);
        string? problem = status.IsValid ? null : status.Message;
        if (status.IsValid && status.FinalDirectory is { } final
            && _currentMapEntry is { } entry
            && MapNameFromAssetPath(entry.Path) is { } mapName)
        {
            string mapWad = Path.Combine(final, "Maps", "Shipping", mapName + ".wad.client");
            if (!File.Exists(mapWad))
                problem = $"The configured League folder does not contain the required {mapName}.wad.client.";
        }
        if (problem is null) return null;
        return problem + " Recolouring may fail for textures that only exist in Riot's WADs."
               + Environment.NewLine + "1. Open Project > Set Game Folder...."
               + Environment.NewLine + "2. Select the League of Legends\\Game folder that contains DATA\\FINAL."
               + Environment.NewLine + "3. Reopen the map, press Refresh here, and retry.";
    }

    /// <summary>Write a recoloured texture where it belongs. Unlike a baked lightmap — a brand-new file
    /// with no home of its own — a recoloured texture already exists in a Riot WAD, so it is staged under
    /// THAT wad's folder and replaces the chunk the game actually reads.</summary>
    private string WriteRecoloredAsset(string assetPath, byte[] bytes, string ext)
    {
        ulong hash = HashAlgorithms.WadPath(assetPath);
        if (Project.IsFolderProject && Project.RootPath is { } root)
        {
            string folderName = RiotWadFolderNameForHash(hash);
            string dest = Path.Combine(root, folderName, assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            if (!Project.ProjectFolders.Contains(folderName, StringComparer.OrdinalIgnoreCase))
                Project.ProjectFolders.Add(folderName);
            ClearShadowOverride(hash, ext);   // a stale hashed override would outrank the folder file
            return dest;
        }

        var overrideFile = ProjectWorkspace.StoreOverrideBytes(Project, hash, bytes, ext);
        _overrides.Set(new ProjectAssetOverride
        {
            PathHash = hash,
            ResolvedPath = assetPath,
            OverrideFile = overrideFile,
            AddedUtc = DateTime.UtcNow.ToString("o"),
        });
        return overrideFile;
    }

    /// <summary>Remember the sliders (not the pixels) for each recoloured texture, so re-opening the tool
    /// shows what was done and a later edit re-derives from the original instead of stacking on top.</summary>
    public void PersistRecolors(TextureAdjustment adjustment, IReadOnlyList<RecolorTarget> targets)
    {
        foreach (var t in targets)
        {
            var record = Project.TextureRecolors.FirstOrDefault(r => r.PathHash == t.PathHash);
            if (record is null)
            {
                record = new TextureRecolorRecord { PathHash = t.PathHash, AssetPath = t.AssetPath };
                // Only set when the run actually had to keep its own copy (no Riot reference mounted);
                // in the normal case this stays null and the project stays small.
                record.BaseSnapshot = _pendingSnapshots.GetValueOrDefault(t.PathHash);
                Project.TextureRecolors.Add(record);
            }
            record.AssetPath = t.AssetPath;
            record.HueDegrees = adjustment.HueDegrees;
            record.Saturation = adjustment.Saturation;
            record.Brightness = adjustment.Brightness;
            record.Contrast = adjustment.Contrast;
            record.InputBlack = adjustment.InputBlack;
            record.InputWhite = adjustment.InputWhite;
            record.Gamma = adjustment.Gamma;
            record.TintR = adjustment.TintR;
            record.TintG = adjustment.TintG;
            record.TintB = adjustment.TintB;
            record.Strength = adjustment.Strength;
        }
        _pendingSnapshots.Clear();
    }

    /// <summary>Stash the untouched original under the workspace, returning its workspace-relative path.
    /// Only used when nothing else can supply a pristine base.</summary>
    private string? SnapshotOriginal(RecolorTarget target, byte[] bytes)
    {
        try
        {
            if (Project.WorkspaceDirectory is not { } ws) return null;
            string rel = Path.Combine("recolor-base", $"{target.PathHash:x16}.tex");
            string full = Path.Combine(ws, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            return rel;
        }
        catch (Exception ex)
        {
            _log.Warn("Recolor", $"Could not snapshot the original of {target.AssetPath} ({ex.Message}) — re-editing it will re-compress.");
            return null;
        }
    }

    /// <summary>Undo recolours: delete the project's copies so Riot's originals win again, and forget the
    /// saved sliders. Returns how many were restored.</summary>
    public int RevertRecolors(IReadOnlyList<RecolorTarget> targets)
    {
        int n = 0;
        foreach (var t in targets)
        {
            var record = Project.TextureRecolors.FirstOrDefault(r => r.PathHash == t.PathHash);
            try
            {
                if (Project.IsFolderProject && Project.RootPath is { } root)
                {
                    string dest = Path.Combine(root, RiotWadFolderNameForHash(t.PathHash),
                        t.AssetPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(dest)) { File.Delete(dest); n++; }
                }
                ClearShadowOverride(t.PathHash, ".tex");
                if (record?.BaseSnapshot is { } snap && ResolveSnapshotPath(snap) is { } p && File.Exists(p))
                    File.Delete(p);
            }
            catch (Exception ex) { _log.Warn("Recolor", $"Could not restore {t.AssetPath}: {ex.Message}"); }
            if (record is not null) Project.TextureRecolors.Remove(record);
        }
        OnRecolorFinished(null);
        return n;
    }

    public void OnRecolorFinished(Services.RecolorRunResult? result)
    {
        if (result is not null)
        {
            string summary = $"{result.Written:n0} texture(s) recoloured"
                + (result.Skipped > 0 ? $", {result.Skipped:n0} skipped" : "")
                + (result.Failed > 0 ? $", {result.Failed:n0} failed" : "")
                + $" ({result.BytesWritten / 1048576.0:F1} MB).";
            if (result.Failed > 0) _log.Error("Recolor", summary);
            else _log.Success("Recolor", summary);
            if (result.MissingSources > 0)
                _log.Error("Recolor",
                    $"{result.MissingSources:n0} original texture source(s) could not be read. "
                    + "Fix: Project > Set Game Folder..., select the League of Legends\\Game folder containing DATA\\FINAL, "
                    + "then reopen the map, refresh Recolor Textures, and retry.");
            foreach (var note in result.Notes) _log.Warn("Recolor", note);
        }

        if (result is not null && result.Written == 0) return;
        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        if (Project.IsFolderProject) { BuildMounts(); BuildProjectTree(); }
        UpdateTitle();
        // Re-read the map so the viewport paints with the new textures rather than the ones it cached.
        if (_currentMapEntry is { } e) _ = LoadMapGeoAsync(e);
    }
    [NotifyPropertyChangedFor(nameof(ActiveOverlayCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveOverlays))]
    [NotifyPropertyChangedFor(nameof(OverlayBadge))]
    [ObservableProperty] private bool _showLightMarkers = true;   // M71: show a glow icon at each light position
    // M71: manual lighting controls. Sun + sky feed the fallback lighting term (visible with lightmaps off or
    // on geometry without baked light); lightmap brightness scales the baked atlas. All initialise from the
    // map's MapSunProperties on load, then the user tweaks — darken the sky/lightmap so dynamic lights pop.
    [ObservableProperty] private double _sunIntensity = 1.0;
    [ObservableProperty] private double _sunColorR = 0.75;
    [ObservableProperty] private double _sunColorG = 0.75;
    [ObservableProperty] private double _sunColorB = 0.75;
    [ObservableProperty] private double _skyIntensity = 1.0;
    [ObservableProperty] private double _skyColorR = 0.35;
    [ObservableProperty] private double _skyColorG = 0.35;
    [ObservableProperty] private double _skyColorB = 0.35;

    // M463: the rest of MapSunProperties. Until now the panel edited 4 of the record's 10 fields, the
    // writer persisted all 10, and the other 6 could only ever be saved back exactly as loaded.
    //
    // NOT sliders, deliberately. Riot ships sunDirection NON-UNIT - lengths up to 8.775 (Map22
    // base_dragon_cloud is <2, 8, -3>) - so a -1..1 slider would silently clamp an authored vector and the
    // next save would write the clamped one. A wide NumericUpDown round-trips whatever the map holds.
    [ObservableProperty] private double _sunDirX = 0.4;
    [ObservableProperty] private double _sunDirY = 0.85;
    [ObservableProperty] private double _sunDirZ = 0.45;

    // Sky-gradient colours. SAVE-ONLY in this viewport - see the tooltips in LightingWindow.axaml and the
    // note on RebuildSun. They are carried through CurrentSunProperties so nothing downstream loses them.
    [ObservableProperty] private double _horizonColorR = 1.0;
    [ObservableProperty] private double _horizonColorG = 1.0;
    [ObservableProperty] private double _horizonColorB = 1.0;
    [ObservableProperty] private double _groundColorR = 1.0;
    [ObservableProperty] private double _groundColorG = 1.0;
    [ObservableProperty] private double _groundColorB = 1.0;

    // Fog. These DO drive the D3D11 viewport (ENV_FOG_COLOR / ENV_FOG_START_END_SCALE_EMISSIVE_REMAP),
    // gated on the Fog toggle exactly as Dx11ViewportSurface already gates them.
    [ObservableProperty] private double _fogColorR = 1.0;
    [ObservableProperty] private double _fogColorG = 1.0;
    [ObservableProperty] private double _fogColorB = 1.0;

    // RAW world HEIGHTS (M759), start above end: the fog begins below Start and is complete at End.
    // Summoner's Rift authors (0, -19000). Edited as the shaders read them.
    [ObservableProperty] private double _fogStartRaw;
    [ObservableProperty] private double _fogEndRaw;

    // M759: the four fog fields the panel could not reach. fogEnabled is the MAP's switch (153 of 201
    // shipped sun blocks turn it off); ShowFog is only the viewport's.
    [ObservableProperty] private bool _mapFogEnabled = true;
    [ObservableProperty] private double _fogAltColorR = 0.1;
    [ObservableProperty] private double _fogAltColorG = 0.1;
    [ObservableProperty] private double _fogAltColorB = 0.2;
    [ObservableProperty] private double _fogEmissiveRemap = 1.9;
    [ObservableProperty] private double _fogLowQualityEmissiveRemap = 0.02;

    // M467: MapSunProperties' four SHADOW fields. These are the only map-side shadow controls that exist —
    // a sweep of the meta database finds `castShadows` on SkinMeshDataProperties (characters) alone, so
    // there is no per-material or per-mesh cast flag to expose instead. They drive the GAME, not the
    // viewport: ReyEngine's own shadow map is fitted by SunShadowFit and has no use for a coverage radius
    // or a Riot bias, so wiring these into the preview would misreport what the client will do.
    [ObservableProperty] private double _sunRadiusForShadows;
    [ObservableProperty] private double _scaleSunShadowIntensity = 1.0;
    [ObservableProperty] private double _sunShadowBias = 0.0006;
    [ObservableProperty] private double _surfaceAreaToShadowMapScale = 0.05;
    [ObservableProperty] private bool _hasMaterialData;
    [ObservableProperty] private bool _hasInspectorBody;
    [ObservableProperty] private int _inspectorTab;

    /// <summary>
    /// M471: which top-level INSPECTOR section is showing. The panel used to be one scrolling column with
    /// every card in it — per-selection transform, map-wide graphics features, the selection's materials
    /// and the material editor all stacked — so finding anything meant scrolling past everything.
    ///
    /// <para>Distinct from <see cref="InspectorTab"/>, which selects Overview/Materials/Shaders INSIDE the
    /// Asset section. Both exist because they answer different questions ("what am I looking at" vs "which
    /// view of this asset"), and collapsing them into one index would have renumbered the six existing
    /// <c>InspectorTab</c> call sites — an off-by-one no test in this repo could catch.</para>
    /// </summary>
    [ObservableProperty] private int _inspectorSection;

    /// <summary>Named indices for <see cref="InspectorSection"/>. Constants rather than literals because
    /// the tab order lives in XAML and a bare "2" in C# is exactly how those two drift apart.</summary>
    public static class InspectorSections
    {
        public const int Object = 0;
        public const int Map = 1;
        public const int Asset = 2;
    }

    /// <summary>Named indices for <see cref="InspectorTab"/> — the tabs inside the Asset section.</summary>
    public static class InspectorTabs
    {
        public const int Overview = 0;
        public const int Materials = 1;
        public const int Shaders = 2;
    }

    /// <summary>
    /// M471: selecting an inner tab has to reveal the section that contains it.
    ///
    /// <para>Six places already set <c>InspectorTab</c> to jump the user somewhere useful — opening a
    /// .materials.bin lands on Materials, for instance. Once the panel became sectioned, every one of those
    /// would have selected a tab inside a section the user could not see, and the jump would silently do
    /// nothing. Routing it here means those callers keep working unchanged.</para>
    /// </summary>
    partial void OnInspectorTabChanged(int value) => InspectorSection = InspectorSections.Asset;
    [ObservableProperty] private int _previewMode; // 0 Basic · 1 RiotApprox · 2 Debug base · 3 Debug alpha · 4 Debug normal
    [ObservableProperty] private string _shaderDbStatus = "Riot shaders not scanned.";
    /// <summary>
    /// <para>M268: bumped whenever the open map is replaced or cleared. _currentMap is a plain field, so
    /// nothing observable fired on a map load - which is why the D3D11 viewport only ever built its scene
    /// on the toggle, and opening a different map while it was on left the PREVIOUS map on screen.</para>
    ///
    /// <para>A counter rather than exposing the map itself: the view needs to know THAT it changed, not
    /// what it changed to, and a counter cannot be accidentally held alive by a binding.</para>
    /// </summary>
    [ObservableProperty] private int _mapGeneration;

    /// <summary>
    /// M501: bumped whenever the open map's MATERIALS change — edited in the material editor, saved,
    /// rewritten by the legacy porter, or bulk-changed by a lighting command.
    ///
    /// <para>Separate from <see cref="MapGeneration"/>, which means "a different map is open". Material
    /// edits used to signal nothing at all: <c>ApplyMaterialToViewport</c> rebuilds
    /// <c>CurrentModelTextures</c>, which is the GL path's input, and the D3D11 scene is built once on
    /// toggle and once per MapGeneration. So a material change repainted GL and left DX11 showing whatever
    /// it had — the reported "after a restart the materials show, but on DX11 the mesh is still white",
    /// and the reason a fresh NVR port looked unchanged until the map was reloaded.</para>
    /// </summary>
    [ObservableProperty] private int _materialsRevision;

    /// <summary>M501: raise after anything that changes the open map's materials, so both viewports and the
    /// inspector see the same state without a reload.</summary>
    public void NotifyMaterialsChanged()
    {
        MaterialsRevision++;
        InvalidateMapMaterialNames();   // M516: a new material must show up in the pickers
    }

    /// <summary>
    /// <para>M269: the current selection as INDEX RANGES, for the D3D11 overlay.</para>
    ///
    /// <para>Ranges rather than material or slice indices, and that is not a style choice.
    /// Dx11SceneBuilder.MergeSlices sorts the map's groups by start index and merges adjacent ones, so the
    /// Nth D3D11 material is not the Nth mapgeo group. Handing the renderer material indices would
    /// highlight confidently and highlight the wrong mesh. A group's (StartIndex, IndexCount) is what
    /// mapgeo actually stores and survives the merge untouched.</para>
    /// </summary>
    /// <summary>
    /// <para>M270: the placement markers for the D3D11 viewport, colour-coded by type.</para>
    ///
    /// <para>Reads the same placement marker lists the GL viewport is bound to and applies the shared
    /// fitted-position formula to editable point lights.</para>
    ///
    /// <para>Size scales with camera distance because these mark a POSITION, not an object with a size -
    /// a fixed world size vanishes when you pull back over a 97,000-unit map and swallows the screen when
    /// you fly in.</para>
    /// </summary>
    private IReadOnlyList<System.Numerics.Vector3>? _dx11IconParticles, _dx11IconSounds, _dx11IconProps, _dx11IconProbes;
    private IReadOnlyList<PointLight>? _dx11IconLights;
    private float _dx11IconSize, _dx11IconSpread, _dx11IconScaleX, _dx11IconScaleZ, _dx11IconOffsetX, _dx11IconOffsetZ;
    private bool _dx11IconShowLights;
    private IReadOnlyList<(System.Numerics.Vector3 Pos, System.Numerics.Vector4 Color, float Size,
        ReyEngine.Core.Assets.ViewportIcon Glyph)> _dx11IconCache =
        Array.Empty<(System.Numerics.Vector3, System.Numerics.Vector4, float, ReyEngine.Core.Assets.ViewportIcon)>();

    public IReadOnlyList<(System.Numerics.Vector3 Pos, System.Numerics.Vector4 Color, float Size,
        ReyEngine.Core.Assets.ViewportIcon Glyph)> Dx11Icons(float cameraDistance)
    {
        float size = Math.Clamp(cameraDistance * 0.012f, 12f, 320f);
        float spread = (float)DynamicLightPositionScale;
        float scaleX = (float)DynamicLightScaleX, scaleZ = (float)DynamicLightScaleZ;
        float offsetX = (float)DynamicLightOffsetX, offsetZ = (float)DynamicLightOffsetZ;
        if (ReferenceEquals(_dx11IconParticles, ParticleMarkers)
            && ReferenceEquals(_dx11IconSounds, SoundMarkers)
            && ReferenceEquals(_dx11IconProps, PropMarkers)
            && ReferenceEquals(_dx11IconProbes, ProbeMarkers)
            && ReferenceEquals(_dx11IconLights, DynamicLights)
            && _dx11IconSize == size && _dx11IconShowLights == ShowLightMarkers
            && _dx11IconSpread == spread && _dx11IconScaleX == scaleX && _dx11IconScaleZ == scaleZ
            && _dx11IconOffsetX == offsetX && _dx11IconOffsetZ == offsetZ)
            return _dx11IconCache;

        _dx11IconParticles = ParticleMarkers; _dx11IconSounds = SoundMarkers;
        _dx11IconProps = PropMarkers; _dx11IconProbes = ProbeMarkers; _dx11IconLights = DynamicLights;
        _dx11IconSize = size; _dx11IconShowLights = ShowLightMarkers; _dx11IconSpread = spread;
        _dx11IconScaleX = scaleX; _dx11IconScaleZ = scaleZ; _dx11IconOffsetX = offsetX; _dx11IconOffsetZ = offsetZ;

        var outp = new List<(System.Numerics.Vector3, System.Numerics.Vector4, float,
            ReyEngine.Core.Assets.ViewportIcon)>();
        void Add(IReadOnlyList<System.Numerics.Vector3>? pts, System.Numerics.Vector4 colour,
                 ReyEngine.Core.Assets.ViewportIcon glyph)
        {
            if (pts is null) return;
            foreach (var p in pts) outp.Add((p, colour, size, glyph));
        }
        // M657: the painted icons carry their own colour, so these are opacity and nothing else - the
        // per-type tints went with the white glyphs they were colouring. Kept as a Vector4 because the
        // renderer batches by colour, and because a wash is still the way to say "highlighted".
        var plain = new System.Numerics.Vector4(1f, 1f, 1f, 0.95f);
        Add(ParticleMarkers, plain, ReyEngine.Core.Assets.ViewportIcon.Particle);
        Add(SoundMarkers, plain, ReyEngine.Core.Assets.ViewportIcon.Sound);
        Add(PropMarkers, plain, ReyEngine.Core.Assets.ViewportIcon.Prop);
        Add(ProbeMarkers, plain, ReyEngine.Core.Assets.ViewportIcon.Probe);
        if (ShowLightMarkers && DynamicLights is { Count: > 0 } lights)
        {
            var scaleXZ = new System.Numerics.Vector2(scaleX, scaleZ);
            var offset = new System.Numerics.Vector2(offsetX, offsetZ);
            foreach (var light in lights)
                outp.Add((Formats.Baking.BakeLighting.FitPosition(light.Position, spread, scaleXZ, offset),
                    plain, size * 1.2f,
                    ReyEngine.Core.Assets.ViewportIcon.Light));
        }
        return _dx11IconCache = outp;
    }

    private float[]? _dx11LightRangeCache;
    private bool _dx11LightRangeShown;
    private object? _dx11LightRangeSource;
    private (double Radius, double Spread, double ScaleX, double ScaleZ, double OffsetX, double OffsetZ) _dx11LightRangeKnobs;

    /// <summary>
    /// M659: the point-light radius rings for the D3D11 viewport, from the same builder and the same
    /// numbers the GL viewport uses - two viewports disagreeing about how far a light reaches would be
    /// worse than not drawing it at all.
    ///
    /// <para>Cached against the light list and the fit knobs, because this is called every frame and the
    /// rings are a few thousand floats.</para>
    /// </summary>
    public float[]? Dx11LightRangeLines()
    {
        bool shown = ShowLightRanges && ShowLightMarkers;
        var knobs = (DynamicLightRadiusScale, DynamicLightPositionScale, DynamicLightScaleX,
                     DynamicLightScaleZ, DynamicLightOffsetX, DynamicLightOffsetZ);
        if (shown == _dx11LightRangeShown && ReferenceEquals(_dx11LightRangeSource, DynamicLights)
            && knobs == _dx11LightRangeKnobs)
            return _dx11LightRangeCache;

        _dx11LightRangeShown = shown;
        _dx11LightRangeSource = DynamicLights;
        _dx11LightRangeKnobs = knobs;

        if (!shown || DynamicLights is not { Count: > 0 } lights) return _dx11LightRangeCache = null;

        float radiusScale = (float)DynamicLightRadiusScale;
        float spread = (float)DynamicLightPositionScale;
        var scaleXZ = new System.Numerics.Vector2((float)DynamicLightScaleX, (float)DynamicLightScaleZ);
        var offset = new System.Numerics.Vector2((float)DynamicLightOffsetX, (float)DynamicLightOffsetZ);
        var rings = new List<float>();
        foreach (var light in lights)
            rings.AddRange(Rendering.ViewportMeshRenderer.BuildLightRangeRings(
                Formats.Baking.BakeLighting.FitPosition(light.Position, spread, scaleXZ, offset),
                light.Radius * radiusScale));
        return _dx11LightRangeCache = rings.Count > 0 ? rings.ToArray() : null;
    }

    private IReadOnlyList<int>? _dx11HighlightSelection;
    private MapGeoAsset? _dx11HighlightMap;
    private IReadOnlyList<(int Start, int Count)> _dx11HighlightCache = Array.Empty<(int, int)>();

    public IReadOnlyList<(int Start, int Count)> Dx11HighlightRanges
    {
        get
        {
            if (SelectedSubmeshIndices is not { Count: > 0 } sel || _currentMap is not { } map)
                return Array.Empty<(int, int)>();
            if (ReferenceEquals(_dx11HighlightSelection, sel) && ReferenceEquals(_dx11HighlightMap, map))
                return _dx11HighlightCache;
            var ranges = new List<(int, int)>(sel.Count);
            foreach (int i in sel)
                if (i >= 0 && i < map.Groups.Count)
                    ranges.Add((map.Groups[i].StartIndex, map.Groups[i].IndexCount));
            _dx11HighlightSelection = sel; _dx11HighlightMap = map;
            return _dx11HighlightCache = ranges;
        }
    }

    private MapGeoAsset? _currentMap;
    private IReadOnlyDictionary<string, MaterialProfile>? _currentMapProfiles;
    private Dictionary<string, string>? _currentMaterialToTexture;   // M172c: material name -> diffuse .tex path // M34: material name → render-state profile
    // Map-only secondary layers. Flow water uses mask/gradient; terrain shader 0xe25b830f additionally reuses
    // emissive/matcap as top/extras. Keep them across ClearSecondaryTextures() just like baked lightmaps.
    private IReadOnlyList<TextureImage?>? _mapFlowMasks;
    private IReadOnlyList<TextureImage?>? _mapFlowGrads;
    private IReadOnlyList<TextureImage?>? _mapTerrainTops;
    private IReadOnlyList<TextureImage?>? _mapTerrainExtras;

    /// <summary>Republish map-only special material layers after ClearSecondaryTextures().</summary>
    private void PublishMapMaterialLayers()
    {
        CurrentModelMaskTextures = _mapFlowMasks;
        CurrentModelGradientTextures = _mapFlowGrads;
        CurrentModelEmissiveTextures = _mapTerrainTops;
        CurrentModelMatCapTextures = _mapTerrainExtras;
        HasFlowmapWater = CurrentModelSubmeshMaterials?.Any(m => m.IsFlowmap) == true;
    }
    private MapVisibilityControllers? _mapControllers;
    private MapVisibilityResolver? _visibilityResolver;
    private MapVisibilityDefinition _mapVisibility = MapVisibilityDefinition.Empty;
    private ShaderDatabase? _shaderDb;

    /// <summary>M666: the session log on disk. Held so Program.cs can append a crash to the same file,
    /// after the breadcrumbs that led to it.</summary>
    public static ReyEngine.Core.Diagnostics.SessionLogSink? SessionLog { get; private set; }

    public MainWindowViewModel()
    {
        _log.AddSink(Console);
        // M666: and to %AppData%/ReyEngine/session.log, flushed per line. A crash that is not a managed
        // exception - a driver fault, a stack overflow, a kill - leaves no crash.log and takes the
        // in-memory console with it, which is why "the tool crashed loading Locke" came with nothing to
        // read. This at least says which step it died on.
        SessionLog ??= new ReyEngine.Core.Diagnostics.SessionLogSink();
        if (SessionLog is not null) _log.AddSink(SessionLog);
        Console.EntryWritten += OnConsoleEntry;   // M519: badge the Console tab while it is behind
        // M367: deferred, not eager - LoadLocal reads and parses ~3.6 MB, and a session that never opens a
        // bin should not pay for it at startup. Lazy is thread-safe, which matters because the first
        // ResolveBinName usually arrives from a background parse.
        _meta = new Lazy<MetaClassDatabase>(() => _metaSync.LoadLocal(m => _log.Info("Meta", m)));
        _cullBackfaces = Settings.CullBackfacesDefault;   // M40: honor saved viewport default
        NewFeature.LastSeenVersion = Settings.LastSeenFeatureVersion;   // M593
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

        // M642: two material editors, ONE wiring. The map's lives in the inspector; the character's lives
        // in the character window, previews on both of its renderers there, and saves through the same
        // override path. See MainWindowViewModel.CharacterMaterials.cs.
        WireMaterialEditor(MaterialEditor, ApplyMaterialToViewport, SaveMaterialOverride);
        WireMaterialEditor(MeshPreview.MaterialEditor, ApplyCharacterMaterialsToPreview, SaveCharacterMaterialOverride);
        // M703/M704: the submesh row in the outliner and the offer in the Materials tab are the same step
        MeshPreview.AddSubmeshMaterial = row => AddCharacterSubmeshMaterialAsync(row.Name);
        MeshPreview.MaterialEditor.AddSubmeshMaterial = AddCharacterSubmeshMaterialAsync;
        MeshPreview.RequestDriverState = () => RebuildCharacterDx11Scene();   // M647: the state switch
        Inspector.CopyHandler = Dialogs.CopyAsync;   // M351c: copy button beside the asset path
        InitShaderEnvironments();

        // M46 Particle Editor wiring
        ParticleEditor.ResolveTextures = ResolveSystemTextures;
        ParticleEditor.ResolveMultTextures = ResolveSystemMultTextures;
        ParticleEditor.ResolveDistortionTextures = ResolveSystemDistortionTextures;
        ParticleEditor.ResolveColorTextures = ResolveSystemColorTextures;   // M68: particleColorTexture gradient
        // M175: erosion and palette. Erosion shipped in M174 but was only ever handed to the champion-VFX
        // path, so the Particle Editor - the one surface built for looking at VFX - never applied it.
        ParticleEditor.ResolveErosionTextures = ResolveSystemErosionTextures;
        ParticleEditor.ResolvePaletteTextures = ResolveSystemPaletteTextures;
        ParticleEditor.ResolveReflectionCubemaps = ResolveSystemReflectionCubemaps;   // M181 (2.12)
        ParticleEditor.ResolveMeshes = ResolveSystemMeshes;   // M47: .scb/.sco mesh primitives
        ParticleEditor.ResolveEmissionSurfaces = ResolveSystemEmissionSurfaces;   // M754
        // M755: the host is whatever the character window shows - it owns the skin, the WADs and the clips
        ParticleEditor.ResolveHost = () => MeshPreview.Mesh is { CanSkin: true } mesh && MeshPreview.Skeleton is { } skl
            ? new ParticleHostModel(string.IsNullOrWhiteSpace(MeshPreview.Title) ? "character" : MeshPreview.Title,
                mesh, skl, MeshPreview.Textures, MeshPreview.Materials, MeshPreview.CurrentAnimation)
            : null;

        // M55: model-preview window — its own animation clock (AnimationInspectorViewModel) + VFX resolvers
        MeshPreview.Animation.ClipLoader = DecodeAnimation;
        MeshPreview.LoadDummyMesh = () => Services.TargetDummyLoader.Get(Project.GameDirectory, _resolver,
            m => _log.Warn("Preview", m));   // M115: Riot's practice dummy from Map11.wad
        MeshPreview.LoadSkybox = LoadSkyboxAtAsync;   // M122: same catalogue, its own pick
        MeshPreview.ResolveTextures = ResolveSystemTextures;
        // M634: the multiplier / mask stage. Wired for the particle editor (above) and the map viewport
        // since M117, and never for the character window - the resolver existed, the delegate did not.
        MeshPreview.ResolveMultTextures = ResolveSystemMultTextures;
        MeshPreview.ResolveDistortionTextures = ResolveSystemDistortionTextures;
        MeshPreview.ResolveColorTextures = ResolveSystemColorTextures;   // M68
        MeshPreview.ResolveErosionTextures = ResolveSystemErosionTextures;   // M175 (see above)
        MeshPreview.ResolvePaletteTextures = ResolveSystemPaletteTextures;
        MeshPreview.ResolveReflectionCubemaps = ResolveSystemReflectionCubemaps;   // M181 (2.12)
        MeshPreview.ResolveMeshes = ResolveSystemMeshes;
        MeshPreview.ResolveEmissionSurfaces = ResolveSystemEmissionSurfaces;   // M754
        MeshPreview.BakeTangents = BakePreviewSkinTangentsAsync;               // M758
        MeshPreview.PlaySoundEvent = PlayPreviewSoundEvent;              // M90: clip SFX
        MeshPreview.StopSounds = () => Sound.StopTag("previewsfx");

        // M98: Map Bin Editor window
        MapBinEditor.Resolve = ResolveBinName;
        MapBinEditor.LoadThumbnail = LoadThumbnailByPath;   // M406: texture previews on texture rows
        // M408: the same meta-schema hooks the material and particle editors already receive, so the bin
        // editor can list a class's declared-but-absent fields and add one.
        MapBinEditor.DeclaredProperties = MaterialEditor.DeclaredProperties;
        MapBinEditor.ClassName = MaterialEditor.ClassName;
        MapBinEditor.Info = m => _log.Info("MapBin", m);
        MapBinEditor.Warn = m => _log.Warn("MapBin", m);
        MapBinEditor.PickOldOriginal = () => Dialogs.OpenFileAsync(
            "Pick the OLD original .bin (from the patch your mod was made for)",
            new Avalonia.Platform.Storage.FilePickerFileType("League .bin") { Patterns = new[] { "*.bin" } },
            DialogService.All);
        MapBinEditor.ReadRiotOriginal = ReadRiotOriginalBytes;
        MapBinEditor.SaveBytes = SaveMapBinBytesAsync;
        ParticleEditor.ResolveBinName = ResolveBinName;   // M187 (3.1): field names instead of raw hashes
        ParticleEditor.Info = m => _log.Info("Particle", m);
        ParticleEditor.Error = m => _log.Error("Particle", m);
        ParticleEditor.MarkDocumentDirty = () => { }; // window has its own dirty state via Document.IsDirty
        ParticleEditor.LoadThumbnail = LoadThumbnailByPath;
        // M368: the meta-class schema. Both are no-ops when it was never synced (empty database), so the
        // editor keeps working exactly as before and simply shows no schema panel.
        ParticleEditor.DeclaredProperties = h => Meta.PropertiesOf(h);
        ParticleEditor.ClassName = h => Meta.TryGetName(h, out var n) ? n : null;
        ParticleEditor.SaveOverrideAsync = SaveParticleOverride;
        ParticleEditor.OpenIssues = OpenParticleBinIssues;   // M125
        // M713: what the game says a system is for. Keyed by the system, like every other resolver this
        // view model hands over, so the editor never learns what a WAD is.
        ParticleEditor.ResolveRole = def =>
            _particleRoles.TryGetValue(def.PathHash, out var link) ? link : null;

        // M138: the wem encoder reuses vgmstream for input formats Media Foundation can't read
        Encoder.VgmstreamPath = Sound.VgmstreamPath;
        Encoder.ConsolePathSetting = Settings.WwiseConsolePath;
        Encoder.ProjectPathSetting = Settings.WwiseProjectPath;

        ContentBrowser.FileSelected = OpenAssetDocument;
        ContentBrowser.CanImportInto = f => TryComputeFolderDiskDir(f, out _);   // M107/M113: virtual folders materialize on write
        ContentBrowser.SelectionStateChanged = RaiseAssetCommandsCanExecute;          // M108
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
        MapContent.ItemStateChanged = OnMapContentItemStateChanged;
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
        // M642: a champion material lands in the character window's editor, not the inspector's.
        if (MeshPreview.MaterialEditor.BinEntry?.PathHash == material.SourceEntry.PathHash)
        {
            MeshPreview.MaterialEditor.Search = material.FullName;
            MeshPreview.ShowMaterials(true);
            ShowMeshPreviewWindow?.Invoke();
            return;
        }
        if (!HasMaterialData)
        {
            _log.Warn("Material", $"'{material.FullName}': no editable materials resolved from {material.SourceBin}.");
            return;
        }
        MaterialEditor.Search = material.FullName; // filter the editor to the clicked material
        InspectorTab = InspectorTabs.Materials;
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
        MapGeoAsset Map, byte[] MapBytes, WadAssetEntry Entry, MapVisibilityDefinition Visibility,
        MapVisibilityControllers? Controllers,
        MeshAsset Mesh, IReadOnlyList<TextureImage?>? Textures,
        IReadOnlyList<ViewportMeshRenderer.SubmeshMaterial>? Materials,
        IReadOnlyList<TextureImage?>? Lightmaps,
        IReadOnlyList<TextureImage?>? FlowMasks, IReadOnlyList<TextureImage?>? FlowGrads, // M44 flow-water
        IReadOnlyList<TextureImage?>? TerrainTops, IReadOnlyList<TextureImage?>? TerrainExtras,
        double LightmapScale, Formats.MapGeo.MapSunProperties? SunProps, // M45 sun properties
        IReadOnlyList<MapParticlePlacement>? Particles,
        IReadOnlyDictionary<uint, VfxSystemDefinition> VfxSystems,
        IReadOnlyList<MapCubemapProbe>? Probes, IReadOnlyList<MapAnimatedProp>? Props,
        IReadOnlyList<MapSoundPlacement>? Sounds,
        int[] VisibilityIndices, bool HasMoves, int[] SelectedMeshIndices,
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

    private async void OpenParticleEditorFor(WadAssetEntry entry)
    {
        try
        {
            var bytes = ReadAsset(entry.PathHash);
            bool editable = !entry.ReadOnly;
            // M197 (4.5): parse off the UI thread. The map VFX bins this milestone makes reachable are far
            // larger than a champion bin - map22.bin measures around 3 seconds - and that was a hard freeze.
            var resolveName = ParticleEditor.ResolveBinName;
            // M713: the roles are read off the champion's OTHER bins - the root bin for its spells and
            // every skin bin for its resource map - so they are built here, beside the parse, and not on
            // the UI thread. A champion with sixty skins is sixty small reads.
            string? binPath = entry.IsResolved ? entry.Path : null;
            var (doc, defs, roles) = await System.Threading.Tasks.Task.Run(
                () =>
                {
                    var (d, dd) = ParticleEditorViewModel.Parse(bytes, resolveName);
                    return (d, dd, BuildParticleRoles(binPath));
                });
            _particleRoles = roles;
            if (doc is null || !ParticleEditor.Load(entry, doc, defs, editable))
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

    /// <summary>M46: save the edited particle .bin — in place for folder-project files, to the
    /// override workspace for wad-backed assets (mirrors SaveMaterialOverride).</summary>
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

        // M126: one save path for project bins — folder-project files are written IN PLACE (and any
        // stale shadow override dissolves); only wad-backed assets go to the override workspace.
        await SaveMapBinBytesAsync(entry, bytes);
    }

    /// <summary>M121: the Model Preview window closed — its document tabs go with it. Mesh and
    /// Texture tabs are exactly the kinds whose content lives in that window (M50 meshes, M118
    /// static objects, M120 images); Map/Bin tabs belong to the main viewport and stay.</summary>
    public void OnPreviewWindowClosed()
    {
        MeshPreview.OnWindowClosed();
        foreach (var doc in Documents.Where(d => d.Kind is DocumentKind.Mesh or DocumentKind.Texture).ToList())
            CloseDocument(doc);
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
        if (value) ScheduleAutoSave();   // M503c
    }

    // ---- M503c: optional auto-save ---------------------------------------
    //
    // Saving a mesh move rewrites the WHOLE mapgeo — Map453's is 40 MB — so this can never fire per drag.
    // It waits for a quiet period and coalesces everything since the last save, and every edit restarts the
    // window. Off by default: someone who wants to decide when their project is written should not have
    // that taken away, and a surprise 40 MB write mid-drag is worse than no feature.
    private Avalonia.Threading.DispatcherTimer? _autoSaveTimer;
    private bool _autoSaving;

    /// <summary>Auto-save is on for this session. Exposed so the toolbar can show it.</summary>
    public bool AutoSaveEnabled => Settings.AutoSaveEdits;

    /// <summary>Restart the quiet window. Called from every edit that produces unsaved state.</summary>
    public void ScheduleAutoSave()
    {
        if (!Settings.AutoSaveEdits || _autoSaving) return;
        _autoSaveTimer ??= new Avalonia.Threading.DispatcherTimer();
        _autoSaveTimer.Interval = TimeSpan.FromSeconds(Settings.EffectiveAutoSaveDelaySeconds);
        _autoSaveTimer.Tick -= OnAutoSaveTick;
        _autoSaveTimer.Tick += OnAutoSaveTick;
        _autoSaveTimer.Stop();      // a burst of edits collapses into one save
        _autoSaveTimer.Start();
    }

    private async void OnAutoSaveTick(object? sender, EventArgs e)
    {
        _autoSaveTimer?.Stop();
        if (!Settings.AutoSaveEdits || _autoSaving) return;

        _autoSaving = true;
        try
        {
            // Guard against saving into a half-finished state: a project that was never saved has nowhere
            // to write, and EnsureProjectSavedAsync would pop a dialog the user did not ask for.
            if (Project.ProjectFilePath is null) return;

            bool savedAnything = false;
            if (HasPendingMapGeoWork)
            {
                await SaveMeshMoves();
                savedAnything = true;
            }
            if (HasParticleMoves) { await SaveParticleMoves(); savedAnything = true; }
            if (MaterialEditor.IsDirty && MaterialEditor.BinEntry is not null)
            { await SaveMaterialOverride(); savedAnything = true; }

            if (savedAnything) _log.Info("Auto-save", "Saved pending edits.");
        }
        catch (Exception ex) { _log.Warn("Auto-save", "Skipped: " + ex.Message); }
        finally { _autoSaving = false; }
    }

    private MapScene? CaptureMapScene()
    {
        if (_currentMap is not { } map || _currentMapBytes is null || _currentMapEntry is not { } entry || CurrentMesh is not { } mesh)
            return null;
        return new MapScene(map, _currentMapBytes, entry, _mapVisibility, _mapControllers, mesh,
            CurrentModelTextures, CurrentModelSubmeshMaterials, CurrentModelLightmapTextures,
            _mapFlowMasks, _mapFlowGrads, _mapTerrainTops, _mapTerrainExtras,
            CurrentLightmapScale, CurrentSunProperties,
            CurrentModelParticles, _vfxSystems, CurrentModelProbes, CurrentModelProps,
            CurrentModelSounds,
            VisibilityAxes.Select(a => a.SelectedIndex).ToArray(), HasMapMoves,
            _selection.Items.Select(m => m.Index).ToArray(),
            MapContent.LayerGroups.ToList(), MapContent.MapName, MapContent.Pieces.ToList());
    }

    private void RestoreMapScene(MapScene s)
    {
        CurrentSkeleton = null; ShowBones = false;
        _currentMap = s.Map; _currentMapBytes = s.MapBytes; _currentMapEntry = s.Entry;
        MapGeneration++;
        InvalidateRayIndex();
        PrebuildRayIndex(s.Map, MeshVerticesRevision);   // M172a
        HasMapGeo = true;   // M79
        _mapVisibility = s.Visibility;
        _mapControllers = s.Controllers;
        _visibilityResolver = new MapVisibilityResolver(s.Controllers, s.Visibility);
        RebuildVisibilityAxes(s.Visibility, s.VisibilityIndices);
        CurrentMesh = s.Mesh;
        CurrentModelTextures = s.Textures;
        ClearSecondaryTextures();
        CurrentModelLightmapTextures = s.Lightmaps;
        _mapFlowMasks = s.FlowMasks; _mapFlowGrads = s.FlowGrads;
        _mapTerrainTops = s.TerrainTops; _mapTerrainExtras = s.TerrainExtras;
        CurrentModelSubmeshMaterials = s.Materials;
        PublishMapMaterialLayers();
        CurrentLightmapScale = s.LightmapScale; CurrentSunProperties = s.SunProps;       // M45
        CurrentModelParticles = s.Particles;
        _vfxSystems = s.VfxSystems;
        CurrentModelProbes = s.Probes;
        CurrentModelProps = s.Props;
        CurrentModelSounds = s.Sounds;                 // M55
        MapContent.SetBucketGrids(s.Map.BucketGrids);  // M55
        HasBucketGrids = s.Map.BucketGrids.Count > 0;  // M77
        RebuildBucketGridLines();
        LoadNavGridForCurrentMap(s.Entry.Path);        // M562: the gameplay bush lives beside the mapgeo
        SelectedParticleTreeItem = null;
        MapGeoInspector.Show(s.Map, s.Entry.Path);
        MapContent.SetLayerGroups(s.LayerGroups);
        MapContent.ShowMap(s.MapName, s.Pieces);
        HasMapMoves = s.HasMoves;
        Inspector.ShowEntry(s.Entry);
        HasInspectorBody = true;
        InspectorTab = InspectorTabs.Overview;
        TryLoadMaterialBin(s.Entry, alsoRawBin: true);

        var meshes = s.SelectedMeshIndices
            .Select(i => s.Map.Meshes.FirstOrDefault(x => x.Index == i))
            .Where(m => m is not null).Select(m => m!).ToList();
        _selection.SetMany(meshes);
        ApplyMapVisibility();   // recompute the visibility array from the restored filters
        MeshVerticesRevision++; // re-upload possibly-edited vertices
        _log.Info("MapGeo", $"Restored map tab '{s.MapName}' ({s.Map.MeshCount:n0} meshes).");
    }

    /// <summary>Push the freshly-built asset tree into the Content Browser + Map Content panels.</summary>
    // ---- M122: skyboxes (map viewport + model preview share the catalogue) ----

    /// <summary>Combo labels: [None, Custom image..., ...discovered assets].</summary>
    public ObservableCollection<string> SkyboxOptions { get; } = new();
    private List<Services.SkyboxOption> _skyboxCatalog = new();
    [ObservableProperty] private int _selectedSkyboxIndex;
    [ObservableProperty] private Services.SkyboxSpec? _currentSkybox;

    /// <summary>M735: the chosen sky SURVIVES a rebuild.
    ///
    /// <para>This used to end with <c>SelectedSkyboxIndex = 0</c>, which drops the sky. It runs from
    /// <see cref="RefreshContentPanels"/>, i.e. from every browser refresh, and the project watcher
    /// schedules one of those on ANY write inside the project folder. Capture Sequence writes
    /// <c>.reyengine/cinematics.json</c> before its first frame (and the PNGs too, when the output folder
    /// is inside the project), so pressing it reset the skybox to "No skybox" a moment later and every
    /// captured frame came out with no sky - the bug this fixes. Saving a bin or adding a mesh did the
    /// same thing; the capture just made it obvious.</para>
    ///
    /// <para>Two rules. An unchanged catalogue does not touch the selection at all - the common case, and
    /// the whole of the capture case. A changed one restores the same option BY LABEL with the reload
    /// suppressed, because re-running the handler for index 1 would pop the "Custom image" file dialog
    /// mid-capture, and re-running it for a catalogue entry would decode a cubemap that is already
    /// loaded.</para></summary>
    private void RebuildSkyboxOptions()
    {
        var catalog = Services.SkyboxCatalog.Discover(AssetEntries);
        var labels = new List<string>(catalog.Count + 2) { "No skybox", "Custom image…" };
        foreach (var o in catalog) labels.Add(o.Label);

        bool same = labels.Count == SkyboxOptions.Count;
        for (int i = 0; same && i < labels.Count; i++) same = string.Equals(labels[i], SkyboxOptions[i], StringComparison.Ordinal);
        _skyboxCatalog = catalog;
        if (same) return;

        string? chosen = SelectedSkyboxIndex >= 0 && SelectedSkyboxIndex < SkyboxOptions.Count
            ? SkyboxOptions[SelectedSkyboxIndex] : null;

        _suppressSkyboxReload = true;
        try
        {
            SkyboxOptions.Clear();
            foreach (var l in labels) SkyboxOptions.Add(l);
            int restore = chosen is null ? 0 : labels.IndexOf(chosen);
            SelectedSkyboxIndex = restore >= 0 ? restore : 0;
        }
        finally { _suppressSkyboxReload = false; }

        // Only when the chosen sky is genuinely gone from the catalogue does the loaded one go with it.
        if (SelectedSkyboxIndex == 0 && chosen is not null) CurrentSkybox = null;

        MeshPreview.SetSkyboxOptions(SkyboxOptions);
        if (_skyboxCatalog.Count > 0)
            _log.Info("Skybox", $"{_skyboxCatalog.Count} skybox asset(s) discovered (cubemaps, domes, sky textures).");
    }

    /// <summary>M735: set while the options list is rebuilt, so restoring the selection does not re-run the
    /// loader (index 1 would open a file dialog).</summary>
    private bool _suppressSkyboxReload;

    partial void OnSelectedSkyboxIndexChanged(int value)
    {
        if (_suppressSkyboxReload) return;
        _ = ApplyMapSkyboxAsync(value);
    }

    private async Task ApplyMapSkyboxAsync(int index)
    {
        CurrentSkybox = await LoadSkyboxAtAsync(index);
    }

    /// <summary>Decode the skybox behind one combo index (shared by both viewports). Index 0 = none,
    /// 1 = pick a custom image file, 2+ = the discovered catalogue.</summary>
    private async Task<Services.SkyboxSpec?> LoadSkyboxAtAsync(int index)
    {
        try
        {
            if (index <= 0) return null;
            if (index == 1)
            {
                var file = await Dialogs.OpenFileAsync("Choose a skybox image (png/jpg/tex/dds)", DialogService.All);
                if (file is null) return null;
                var custom = await Task.Run(() => Services.SkyboxCatalog.LoadCustomFile(file));
                if (custom is null) _log.Warn("Skybox", $"{Path.GetFileName(file)}: not a decodable image.");
                return custom;
            }
            int ci = index - 2;
            if (ci < 0 || ci >= _skyboxCatalog.Count) return null;
            var opt = _skyboxCatalog[ci];
            return await Task.Run(() =>
            {
                var bytes = ReadAsset(opt.Main.PathHash);
                switch (opt.Kind)
                {
                    case Services.SkyboxSourceKind.Cubemap:
                        var cm = CubemapDecoder.TryDecodeDds(bytes);
                        if (cm is not null) return new Services.SkyboxSpec(Cubemap: cm);
                        return new Services.SkyboxSpec(Equirect: TextureDecoder.Decode(bytes));
                    case Services.SkyboxSourceKind.Texture:
                        return new Services.SkyboxSpec(Equirect: TextureDecoder.Decode(bytes));
                    default:
                        TextureImage? tex = opt.PairedTexture is { } pt
                            ? TextureDecoder.Decode(ReadAsset(pt.PathHash)) : null;
                        if (opt.Main.Path.EndsWith(".skn", StringComparison.OrdinalIgnoreCase))
                        {
                            var skn = SkinnedMeshDecoder.Decode(bytes);
                            return new Services.SkyboxSpec(MeshPositions: skn.Positions, MeshUvs: skn.Uvs,
                                MeshIndices: skn.Indices, MeshTexture: tex);
                        }
                        var so = Formats.Meshes.StaticObjectDecoder.Decode(bytes, opt.Main.Path);
                        if (so is null) return null;
                        return new Services.SkyboxSpec(MeshPositions: so.Positions, MeshUvs: so.Uvs,
                            MeshIndices: so.Indices, MeshTexture: tex);
                }
            });
        }
        catch (Exception ex)
        {
            _log.Error("Skybox", ex.Message);
            return null;
        }
    }

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
        // M123e: tree rebuilds fire on every project-file save (Add Mesh writes the materials bin,
        // the watcher fires on any disk change) - clearing the outliner then guts an OPEN map's
        // panel mid-session. Only clear when no map is actually loaded.
        if (_currentMap is null) MapContent.ClearMap();
        RebuildSkyboxOptions();   // M122
    }

    // ---- Material editor: asset access helpers --------------------------

    /// <summary>
    /// M420: while a Workshop preview is being built, assets are read from the whole-install catalog
    /// rather than the project VFS, which mounts only the current map and shared WADs. A converted
    /// legacy effect additionally points at staged paths that do not exist anywhere yet
    /// (<c>ASSETS/Legacy/...</c>), so those are aliased back to the original <c>DATA/...</c> chunk.
    ///
    /// <para>Hooking it here rather than writing a parallel resolver is deliberate: every existing
    /// per-emitter resolver - sprites, mult, distortion, colour ramps, erosion, palette, cubemaps and
    /// meshes - goes through this method, so all of them work for a template unchanged.</para>
    /// </summary>
    private IReadOnlyDictionary<string, string>? _workshopPreviewAliases;

    /// <param name="path">A texture REFERENCE: a real asset path, or the <c>0x…</c> form a WadChunkLink
    /// takes when the dictionary cannot name it. M592: this used to hash the string blindly, so a hex
    /// reference became <c>WadPath("0x…")</c> — a chunk that does not exist — and the texture silently
    /// failed to load, rendering the surface untextured. The hash the reference already carries is what
    /// addresses the chunk, so loading no longer depends on the dictionary knowing its name.</param>
    private byte[]? ReadAssetByPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (ContentLoaded)
        {
            var hash = Formats.Meta.BinTexturePath.HashOfReference(path);
            if (TryResolveEntry(hash, out _) && ReadAsset(hash) is { } bytes) return bytes;
        }
        if (_workshopPreviewAliases is null || _workshopCatalog is null) return null;
        string source = _workshopPreviewAliases.TryGetValue(path, out var alias) ? alias : path;
        try { return _workshopCatalog.ReadAsset(source); } catch { return null; }
    }

    private bool TextureExistsByPath(string path)
    {
        if (!ContentLoaded || string.IsNullOrEmpty(path)) return false;
        return TryResolveEntry(Formats.Meta.BinTexturePath.HashOfReference(path), out _);
    }

    private TextureImage? LoadTextureByPath(string path)
    {
        try
        {
            var bytes = ReadAssetByPath(path);
            if (bytes is null) return null;
            return TextureDecoder.Decode(bytes);
        }
        catch { return null; }   // subchunked/corrupt chunks throw inside the mount read — never propagate
    }

    private Avalonia.Media.Imaging.Bitmap? LoadThumbnailByPath(string path)
    {
        var img = LoadTextureByPath(path);
        return img is null ? null : BitmapFactory.FromRgba(img);
    }

    private void OpenTextureByPath(string path)
    {
        if (!ContentLoaded) return;
        // M592: same addressing rule as ReadAssetByPath, or a hex-referenced texture that the
        // viewport renders fine reports "not found" when you click it.
        var hash = Formats.Meta.BinTexturePath.HashOfReference(path);
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

    /// <summary>
    /// M85: gather the champion's submesh-visibility rules — initialSubmeshToHide from every skins/*.bin
    /// and per-clip show/hide lists from every animations/*.bin under the champ folder.
    ///
    /// <para>TWO views of the same clips, because they answer different questions (M663). <b>Clips</b> is
    /// keyed by .anm FILE NAME, which is what the preview needs: it looks up the visibility and sound
    /// rules for the animation currently playing, and an animation entry is a file. <b>AllClips</b> is
    /// every clip, de-duplicated by clip NAME, which is what the action list needs.</para>
    ///
    /// <para>They cannot be the same collection. Riot points several clips at ONE .anm file — Blitzcrank's
    /// base graph names <c>blitzcrank_spell1.anm</c> from Spell1, Spell2 and Spell2_BASE, and Aatrox's has
    /// 11 such files. Keyed by file, the second clip of each pair is dropped: the base graph's own Spell2
    /// disappeared, and the W action then matched a clip called Spell2 in some OTHER skin's graph, which
    /// is how a base-skin Blitzcrank came to play blitzcrank_skin20_spell1.</para>
    /// </summary>
    /// <param name="skinBin">M728: the skin bin the window was opened with - its initialSubmeshToHide and its own
    /// animation graph. The mesh's folder decides only when nobody chose one.</param>
    private (IReadOnlyList<string> InitialHide, IReadOnlyDictionary<string, Formats.Skeletons.AnimClipInfo>? Clips,
             IReadOnlySet<string> OwnAnms, IReadOnlyList<Formats.Skeletons.AnimClipInfo> AllClips)
        LoadSubmeshRules(WadAssetEntry skn, string? skinBin = null)
    {
        var ownAnms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Keyed by clip NAME and ordered: this skin's own graph goes in first, so where several graphs
        // define "Spell2" the one that wins is the one this skin actually plays.
        var byClipName = new Dictionary<string, Formats.Skeletons.AnimClipInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!skn.IsResolved) return (Array.Empty<string>(), null, ownAnms, Array.Empty<Formats.Skeletons.AnimClipInfo>());
            var parts = skn.Path.Split('/');
            int ci = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
            if (ci < 0 || ci + 1 >= parts.Length) return (Array.Empty<string>(), null, ownAnms, Array.Empty<Formats.Skeletons.AnimClipInfo>());
            string champ = parts[ci + 1];
            const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
            string animDir = $"characters/{champ}/animations/";
            string skinDir = $"characters/{champ}/skins/";

            var hide = new List<string>();
            var clips = new Dictionary<string, Formats.Skeletons.AnimClipInfo>(StringComparer.OrdinalIgnoreCase);

            // M86: this skin's OWN animation graph first (named in the skin bin's dependency list) —
            // clips merge first-wins, and other skins' graphs carry other skins' effect keys.
            var skinBinPath = SkinPaths.PreviewBinPath(skinBin, skn.Path);   // M728: the chosen skin, not the mesh's folder
            // M669: THIS skin's own initialSubmeshToHide, before anything else is consulted. The scan below
            // used to supply it - the first skins/*.bin in asset order that yielded a non-empty list, which
            // for Locke was a later skin's: it hid Recall_Page and Recall_Nail, submeshes his base skin does
            // not have, and never hid the VFX_Head, VFX_Hair and VFX_Smoke shells his base skin declares.
            // Drawn, those shells wrap the body in a second translucent copy of itself, which is what "the
            // material is not correct" looked like. The D3D11 path always read skin0's own list.
            if (skinBinPath is not null && TryResolveEntry(HashAlgorithms.WadPath(skinBinPath), out var ownSkinBin))
                hide.AddRange(Formats.Skeletons.ChampionAnimationData.ParseInitialHide(GetAssetBytes(ownSkinBin)));
            if (skinBinPath is not null && TryResolveEntry(HashAlgorithms.WadPath(skinBinPath), out var skinBinEntry)
                && VfxSystemResolver.ExtractDependencies(GetAssetBytes(skinBinEntry))
                    .FirstOrDefault(d => d.Contains("/animations/", OIC)) is { } graphPath
                && TryResolveEntry(HashAlgorithms.WadPath(graphPath), out var graphEntry))
                foreach (var c in Formats.Skeletons.ChampionAnimationData.ParseClips(GetAssetBytes(graphEntry), ResolveBinName, ResolveWadPath))
                {
                    var file = Path.GetFileName(c.AnmPath.Replace('\\', '/'));
                    if (file.Length > 0 && !clips.ContainsKey(file)) clips[file] = c;
                    if (file.Length > 0) ownAnms.Add(file);   // M115: THIS skin's animation set
                    if (c.Name.Length > 0) byClipName.TryAdd(c.Name, c);   // M663
                }

            foreach (var e in AssetEntries)
            {
                if (!e.IsResolved || !e.Path.EndsWith(".bin", OIC)) continue;
                if (e.Path.Contains(animDir, OIC))
                {
                    foreach (var c in Formats.Skeletons.ChampionAnimationData.ParseClips(GetAssetBytes(e), ResolveBinName, ResolveWadPath))
                    {
                        var file = Path.GetFileName(c.AnmPath.Replace('\\', '/'));
                        if (file.Length > 0 && !clips.ContainsKey(file)) clips[file] = c;
                        if (c.Name.Length > 0) byClipName.TryAdd(c.Name, c);   // M663
                    }
                }
                // M669: only a skin that declares no list of its own falls back to a sibling's. Kept for
                // the skins that name their submeshes the same way; it is a guess and is logged as one.
                else if (e.Path.Contains(skinDir, OIC) && hide.Count == 0)
                {
                    hide.AddRange(Formats.Skeletons.ChampionAnimationData.ParseInitialHide(GetAssetBytes(e)));
                    if (hide.Count > 0) _log.Info("Preview", $"{champ}: no initialSubmeshToHide of its own - borrowed {Path.GetFileName(e.Path)}'s.");
                }
            }
            if (clips.Count > 0)
                _log.Info("Preview", $"{champ}: {clips.Count} named clip(s) with visibility data, "
                    + $"{byClipName.Count} distinct clip name(s), initial-hide: {(hide.Count > 0 ? string.Join(' ', hide) : "(none)")}.");
            return (hide, clips.Count > 0 ? clips : null, ownAnms, byClipName.Values.ToList());
        }
        catch { return (Array.Empty<string>(), null, ownAnms, byClipName.Values.ToList()); }
    }

    private IEnumerable<AnimationEntryViewModel> FindAnimations(WadAssetEntry skn, IReadOnlySet<string>? currentSkinAnms = null)
    {
        if (!ContentLoaded || !skn.IsResolved) return Enumerable.Empty<AnimationEntryViewModel>();
        var parts = skn.Path.Split('/');
        int ci = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
        string champ = ci >= 0 && ci + 1 < parts.Length ? parts[ci + 1] : "";
        var marker = $"/characters/{champ}/";
        const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
        bool Match(string path, bool resolved) =>
            resolved && path.EndsWith(".anm", OIC) && (champ.Length == 0 || path.Contains(marker, OIC));

        // M115: which .anm files the LOADED skin's own animation graph references (green highlight);
        // when the graph didn't resolve, fall back to path matching against the skn's own skin folder.
        string sknGroup = AnimationEntryViewModel.GroupFromPath(skn.Path);
        bool IsCurrent(string path, string fileName) =>
            currentSkinAnms is { Count: > 0 }
                ? currentSkinAnms.Contains(fileName)
                : AnimationEntryViewModel.GroupFromPath(path) == sknGroup;

        AnimationEntryViewModel Make(WadAssetEntry e) => new(e)
        {
            SkinGroup = AnimationEntryViewModel.GroupFromPath(e.Path),
            IsCurrentSkin = IsCurrent(e.Path, Path.GetFileName(e.Path)),
        };

        var seen = new HashSet<ulong>();
        var list = new List<AnimationEntryViewModel>();
        foreach (var e in AssetEntries)
            if (Match(e.Path, e.IsResolved) && seen.Add(e.PathHash)) list.Add(Make(e));

        // If the mod doesn't ship this unit's animations, fall back to the original game WADs.
        if (list.Count == 0 && _mounts is not null)
            foreach (var fb in _mounts.Fallback)
                foreach (var a in fb.Enumerate())
                    if (Match(a.VirtualPath, a.IsResolved) && seen.Add(a.PathHash)) list.Add(Make(a.ToEntry()));

        // Loaded skin's clips first, then grouped by skin (Base, Skin 01…, Shared last), names within.
        static int GroupRank(string g) => g == "Base" ? 0 : g == "Shared" ? 999 : 1;
        return list
            .OrderByDescending(a => a.IsCurrentSkin)
            .ThenBy(a => GroupRank(a.SkinGroup))
            .ThenBy(a => a.SkinGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
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

        // M501: every path that writes an asset marks it Modified here, which makes this the one choke
        // point where "the open map's materials just changed" can be detected without hunting a dozen save
        // sites and missing one. It covers the legacy porter, the NO_BAKED_LIGHTING commands, sampler
        // address edits, the ritobin editor and the light bake alike.
        if (status == AssetStatus.Modified && _currentMapEntry is { } mapEntry
            && TryResolveMaterialsBin(mapEntry.Path, out var materialsBin)
            && materialsBin.PathHash == hash)
            NotifyMaterialsChanged();
    }

    /// <summary>Bytes for an asset — the project override if one exists, otherwise the WAD chunk.</summary>
    private byte[] GetAssetBytes(WadAssetEntry entry) => ReadAsset(entry.PathHash);

    [RelayCommand]
    private void ReloadWad()
    {
        if (_archive is null) { _log.Warn("WAD", "No archive is open."); return; }
        LoadWad(_archive.FilePath);
    }

    [RelayCommand(CanExecute = nameof(CanExportSelected))]
    private async Task ExportSelected()
    {
        var entry = ContextNode?.Entry;
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

    /// <summary>M731: one sync at a time. The automatic startup update and the three Tools commands write the
    /// same cache; two at once would race on the files and swap the resolver twice.</summary>
    private bool _hashSyncBusy;

    [RelayCommand]
    private async Task SyncHashes()
    {
        if (_hashSyncBusy) { _log.Info("Hashes", "A hash sync is already running."); return; }
        _hashSyncBusy = true;
        try
        {
            Status = "Syncing CommunityDragon hashes…";
            _log.Info("Hashes", "Downloading CommunityDragon hashes…");
            var db = await Task.Run(() => _sync.SyncAsync(m => _log.Info("Hashes", m)));
            if (db is null) { Status = "CommunityDragon hashes are current."; return; }
            _resolver.Swap(db);
            ApplyHashesToOpenWad();
            Status = $"Hashes synced — {db.WadCount:n0} WAD + {db.BinCount:n0} bin";
        }
        catch (Exception ex)
        {
            _log.Error("Hashes", $"Sync failed: {ex.Message}");
        }
        finally { _hashSyncBusy = false; }
    }

    /// <summary>
    /// M495: fetch Mimir's hash tables — the replacement for the CommunityDragon text lists.
    ///
    /// <para>Same tables, ~64 MB of memory-mapped binaries instead of ~554 MB of text plus a merged cache,
    /// and each one is verified against the manifest's SHA-256 before it is kept. Once these are present
    /// <see cref="Formats"/>-side lookups resolve through them and the old path is not loaded at all.</para>
    ///
    /// <para>Separate from Sync Hashes rather than replacing it: an existing install already has the merged
    /// cache and must keep working untouched if the user never runs this.</para>
    /// </summary>
    [RelayCommand]
    private async Task SyncMimirHashes()
    {
        if (_hashSyncBusy) { _log.Info("Mimir", "A hash sync is already running."); return; }
        _hashSyncBusy = true;
        try
        {
            Status = "Syncing Mimir hash tables…";
            var result = await Task.Run(() => _mimir.SyncAsync(m => _log.Info("Mimir", m)));
            var db = AdoptSyncedHashes();
            Status = result.Summary;
            _log.Success("Mimir", $"{result.Summary} {db.TableEntryCount:n0} entries reachable through "
                                + $"{db.TableCount} memory-mapped table(s).");
        }
        catch (Exception ex) { _log.Error("Mimir", $"Sync failed: {ex.Message}"); }
        finally { _hashSyncBusy = false; }
    }

    /// <summary>Re-open the synced tables through the normal path, so they are attached exactly as a fresh
    /// launch would attach them, then re-resolve whatever is open.</summary>
    private HashDatabase AdoptSyncedHashes()
    {
        var db = _sync.LoadLocal(m => _log.Info("Hashes", m));
        _resolver.Swap(db);
        ApplyHashesToOpenWad();
        return db;
    }

    /// <summary>
    /// M731: keep the hash tables and the meta-class database current without anyone pressing Sync.
    ///
    /// <para>Runs once per launch, a few seconds in, when Settings ▸ Updates leaves it on. It updates
    /// whichever source this install already uses - the Mimir tables when a manifest is present, else the
    /// CommunityDragon lists when they are - and never introduces one: an install with no hashes at all is the
    /// first-run wizard's job. Every check is one request; a newer release is fetched, adopted and applied to
    /// the open project. Failures are logged at info level: being offline is not an error.</para>
    /// </summary>
    public async Task AutoUpdateHashesAsync()
    {
        if (!Settings.AutoUpdateHashes) return;
        await Task.Delay(TimeSpan.FromSeconds(4));   // after the window has settled and the last project has opened
        if (_hashSyncBusy) return;
        _hashSyncBusy = true;
        try
        {
            if (_mimir.Local is not null)
            {
                var result = await Task.Run(() => _mimir.SyncAsync(m => _log.Info("Mimir", m)));
                if (result.UpToDate) _log.Info("Mimir", $"Hash tables are current ({result.ReleaseTag}).");
                else
                {
                    var db = AdoptSyncedHashes();
                    _log.Success("Mimir", $"Hash tables updated automatically: {result.Summary} "
                                        + $"{db.TableEntryCount:n0} entries through {db.TableCount} table(s).");
                }
            }
            else if (HashSyncService.HasLocalRaw)
            {
                var db = await Task.Run(() => _sync.SyncAsync(m => _log.Info("Hashes", m), onlyIfChanged: true));
                if (db is null) _log.Info("Hashes", "CommunityDragon hash lists are current.");
                else
                {
                    _resolver.Swap(db);
                    ApplyHashesToOpenWad();
                    _log.Success("Hashes", $"Hashes updated automatically: {db.WadCount:n0} WAD + {db.BinCount:n0} bin entries.");
                }
            }
        }
        catch (Exception ex) { _log.Info("Hashes", $"Automatic hash update skipped: {ex.Message}"); }
        finally { _hashSyncBusy = false; }

        try
        {
            if (!MetaClassSyncService.HasLocalCopy) return;
            var db = await _metaSync.SyncIfChangedAsync(m => _log.Info("Meta", m));
            if (db is null) { _log.Info("Meta", "Meta classes are current."); return; }
            _meta = new Lazy<MetaClassDatabase>(() => db);
            _log.Success("Meta", $"Meta classes updated automatically: {db.ClassCount:n0} class(es), build {db.ResolvedBuild}.");
        }
        catch (Exception ex) { _log.Info("Meta", $"Automatic meta-class update skipped: {ex.Message}"); }
    }

    /// <summary>M367: download the LeagueToolkit meta-class database. Companion to Sync Hashes, and
    /// deliberately a SEPARATE command - the hash lists are ~100 MB and this is ~3.6 MB, so bundling them
    /// would make a cheap refresh cost the expensive one.</summary>
    [RelayCommand]
    private async Task SyncMetaClasses()
    {
        try
        {
            Status = "Syncing meta classes…";
            var db = await _metaSync.SyncAsync(m => _log.Info("Meta", m));
            _meta = new Lazy<MetaClassDatabase>(() => db);
            Status = db.IsEmpty
                ? "Meta classes synced, but the database parsed empty"
                : $"Meta classes synced — {db.ClassCount:n0} classes, build {db.ResolvedBuild}";
            if (db.IsEmpty) _log.Warn("Meta", "The download parsed to zero classes - format may have changed.");
            else _log.Success("Meta", $"{db.ClassCount:n0} class(es) available for name and schema lookup.");
        }
        catch (Exception ex)
        {
            _log.Error("Meta", $"Sync failed: {ex.Message}");
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

        // M731: a project is MOUNTS, not an archive. This dereferenced _archive - null in every folder project -
        // so Sync Hashes, Sync Hash Tables and Reload Local Hashes all died with "Sync failed: Object reference
        // not set to an instance of an object" the moment a project was open, which is most of the time; the
        // sync itself had already finished. The WAD mounts re-resolve their entries in place; the folder and
        // override mounts name their files through the resolver on every enumeration, so rebuilding the index
        // and the tree is what applies the new names to them.
        if (_mounts is { } mounts)
        {
            int resolved = 0, total = 0;
            foreach (var wad in mounts.Mounts.OfType<WadMount>().Concat(mounts.Fallback.OfType<WadMount>()))
            {
                resolved += _resolver.RefreshArchive(wad.Archive);
                total += wad.Archive.Entries.Count;
            }
            mounts.Rebuild();
            BuildProjectTree();
            _log.Success("Hashes", $"Resolved {resolved:n0} / {total:n0} WAD paths across the project's mounts.");
            Status = $"{Project.Name} — {mounts.Count:n0} assets · {resolved:n0} WAD paths resolved";
            return;
        }

        if (_archive is null) return;
        int n = _resolver.RefreshArchive(_archive);
        RebuildTree();
        _log.Success("Hashes", $"Resolved {n:n0} / {_archive.Entries.Count:n0} WAD paths.");
        Status = $"{_archive.Name} — {_archive.Entries.Count:n0} entries · {n:n0} resolved";
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
        HasInspectorBody = entry.Type is AssetType.SkinnedMesh or AssetType.StaticMesh or AssetType.MapGeometry or AssetType.Bin;
        // M351g: the Raw BIN Tree tab is gone (index 2). A materials bin still lands on the Materials
        // tab via the dedicated path below; everything else opens on Overview.
        InspectorTab = InspectorTabs.Overview;
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
            case AssetType.StaticMesh:
                _ = LoadStaticMeshPreviewAsync(entry);   // M118: .scb/.sco in the model preview
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

    /// <summary>M118: open a static object (.scb binary / .sco ascii) in the Model Preview. These are
    /// the VFX mesh primitives (weapon swipes, rings, cylinders) — no skeleton, no textures of their
    /// own (the emitter supplies the sprite in a VFX context), and usually no normals, so normals are
    /// synthesized from the faces for lighting.</summary>
    private async Task LoadStaticMeshPreviewAsync(WadAssetEntry entry)
    {
        try
        {
            var mesh = await Task.Run(() =>
            {
                var data = StaticObjectDecoder.Decode(ReadAsset(entry.PathHash), entry.Path);
                if (data is null) return null;

                int vc = data.Positions.Length / 3;
                var normals = new float[data.Positions.Length];
                // accumulate face normals per vertex, then normalize — flat-ish but lightable
                for (int i = 0; i + 2 < data.Indices.Length; i += 3)
                {
                    int a = (int)data.Indices[i], b = (int)data.Indices[i + 1], d = (int)data.Indices[i + 2];
                    var pa = new System.Numerics.Vector3(data.Positions[a*3], data.Positions[a*3+1], data.Positions[a*3+2]);
                    var pb = new System.Numerics.Vector3(data.Positions[b*3], data.Positions[b*3+1], data.Positions[b*3+2]);
                    var pd = new System.Numerics.Vector3(data.Positions[d*3], data.Positions[d*3+1], data.Positions[d*3+2]);
                    var n = System.Numerics.Vector3.Cross(pb - pa, pd - pa);
                    foreach (var vi in new[] { a, b, d })
                    { normals[vi*3] += n.X; normals[vi*3+1] += n.Y; normals[vi*3+2] += n.Z; }
                }
                for (int i = 0; i < vc; i++)
                {
                    var n = new System.Numerics.Vector3(normals[i*3], normals[i*3+1], normals[i*3+2]);
                    if (n.LengthSquared() > 1e-12f) { n = System.Numerics.Vector3.Normalize(n); normals[i*3] = n.X; normals[i*3+1] = n.Y; normals[i*3+2] = n.Z; }
                    else normals[i*3+1] = 1f;   // degenerate vertex: point up
                }

                var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
                for (int i = 0; i < vc; i++)
                {
                    var v = new System.Numerics.Vector3(data.Positions[i*3], data.Positions[i*3+1], data.Positions[i*3+2]);
                    min = System.Numerics.Vector3.Min(min, v); max = System.Numerics.Vector3.Max(max, v);
                }

                return new MeshAsset
                {
                    Positions = data.Positions,
                    Normals = normals,
                    Uvs = data.Uvs,
                    Indices = data.Indices,
                    SubMeshes = new[] { new SubMeshInfo(string.IsNullOrEmpty(data.Name) ? "(static mesh)" : data.Name, 0, data.Indices.Length, vc) },
                    VertexCount = vc,
                    BoundsMin = min,
                    BoundsMax = max,
                };
            });
            if (mesh is null) { _log.Warn("Mesh", $"{entry.DisplayName}: not a readable .scb/.sco static object."); return; }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ForgetPreviewSkin();   // M642: a prop has no skin bin
                MeshPreview.Show(entry.DisplayName, mesh, skeleton: null, textures: null);
                MeshPreview.SetAnimations(Enumerable.Empty<AnimationEntryViewModel>());
                MeshPreview.SetVfx(new Dictionary<uint, ReyEngine.Formats.Vfx.VfxSystemDefinition>());
                MeshInspector.ShowMesh(mesh, null);
                ShowMeshPreviewWindow?.Invoke();
                _log.Success("Mesh", $"{entry.DisplayName}: {mesh.VertexCount:n0} verts, {mesh.TriangleCount:n0} tris (static object — untextured; VFX supply the sprite).");
            });
        }
        catch (Exception ex) { _log.Error("Mesh", $"{entry.DisplayName}: {ex.Message}"); }
    }

    // ---- Material editor: load + apply + save ---------------------------

    /// <summary>Hash to bin field/class name. M367 adds the LeagueToolkit meta database as a SECOND
    /// source, consulted only when CommunityDragon has no answer - the two have genuinely different
    /// coverage, and CDragon stays first so this can never change a name the app already resolved.
    /// This one line feeds every bin consumer (materials, particles, HUD, workshop), so widening it here
    /// widens all of them at once.</summary>
    private string? ResolveBinName(uint h)
    {
        if (_resolver.Database.TryGetBinName(h, out var n)) return n;
        return Meta.TryGetName(h, out var m) ? m : null;
    }

    /// <summary>
    /// M590: hash to WAD PATH — the 64-bit counterpart of <see cref="ResolveBinName"/>.
    ///
    /// <para>Patch 16.17 changed every material's <c>texturePath</c> from a String to a WadChunkLink
    /// (12,688 of 12,688 across the installed patch's 199 materials bins), so a texture reference is now
    /// a hash and needs this to become a path again. Measured on base_srx.materials.bin: 277 of 277
    /// resolve, so in practice nothing is lost — but an unknown hash stays as 0x…, which is honest and
    /// still round-trips through an edit.</para>
    /// </summary>
    private string? ResolveWadPath(ulong h) => _resolver.Database.TryGetPath(h, out var p) ? p : null;

    /// <summary>
    /// M590: teach the path dictionary the project's OWN asset paths.
    ///
    /// <para>Riot's dictionary knows Riot's files. A mod's custom texture — <c>assets/mymod/river.tex</c> —
    /// is in it nowhere, so once <c>texturePath</c> became a WadChunkLink the editor could only show the
    /// author their own texture as <c>0x…</c>. The paths are right there in the project folder and the
    /// hash is derived from them, so registering them costs one directory walk and makes a mod's own
    /// assets name themselves.</para>
    /// </summary>
    private int RegisterProjectAssetPaths()
    {
        if (Project.RootPath is null) return 0;
        int added = 0;
        foreach (string folder in Project.ProjectFolders)
        {
            string root = Path.Combine(Project.RootPath, folder);
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var (hash, path) in ReyEngine.Core.Build.WadPackService.EnumerateChunkFiles(root))
                {
                    string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                    // A hash-named loose chunk has no path to teach - its name IS the hash.
                    if (!rel.Contains('/') && Path.GetFileNameWithoutExtension(rel).Length == 16) continue;
                    _resolver.Database.AddWad(hash, rel);
                    added++;
                }
            }
            catch (Exception ex) { _log.Info("Project", $"Could not index {folder} for path names: {ex.Message}"); }
        }
        return added;
    }

    /// <param name="skinBin">M728: for a skinned mesh, the skin bin that was chosen with it - see
    /// <see cref="SkinPaths.PreviewBinPath"/>. Null falls back to the bin in the mesh's folder.</param>
    private WadAssetEntry? ResolveMaterialBin(WadAssetEntry entry, string? skinBin = null)
    {
        if (!ContentLoaded) return null;
        if (entry.Type == AssetType.Bin) return entry;
        if (!entry.IsResolved) return null;
        string? binPath = entry.Type switch
        {
            AssetType.SkinnedMesh => SkinPaths.PreviewBinPath(skinBin, entry.Path),
            AssetType.MapGeometry => MapGeoMaterialResolver.MaterialsBinPathFor(entry.Path),
            _ => null,
        };
        if (binPath is null) return null;
        return TryResolveEntry(HashAlgorithms.WadPath(binPath), out var be) ? be : null;
    }

    private void TryLoadMaterialBin(WadAssetEntry entry, bool alsoRawBin, string? skinBin = null)
    {
        var binEntry = ResolveMaterialBin(entry, skinBin);
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
            try { matDoc = MaterialDocument.Parse(bytes, ResolveBinName, ResolveWadPath); } catch { matDoc = null; }
            if (alsoRawBin) { try { binDoc = BinEditorDocument.Parse(bytes, ResolveBinName, ResolveWadPath); } catch { binDoc = null; } }
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (alsoRawBin && binDoc is not null) BinEditor.Load(binDoc, binEntry, bytes);
            if (matDoc is not null && matDoc.Materials.Count > 0 && matDoc.Kind == MaterialSourceKind.ChampionSkin)
            {
                // M642: a champion skin's materials belong to the character window - that is where the
                // model is, on both renderers. The inspector keeps whatever MAP document it holds. Before
                // this, opening a skin replaced the map's materials in the inspector with the skin's, a
                // window away from the character they belonged to.
                MeshPreview.MaterialEditor.Load(matDoc, binEntry, bytes);
                MeshPreview.ShowMaterials(true);
                if (MeshPreview.MaterialEditor.UnresolvedCount > 0)
                    _log.Warn("Material", $"{binEntry.DisplayName}: {matDoc.Materials.Count} material(s), {MeshPreview.MaterialEditor.UnresolvedCount} texture path(s) unresolved in this WAD - Character Editor.");
                else
                    _log.Info("Material", $"{binEntry.DisplayName}: {matDoc.Materials.Count} material(s) - Character Editor.");
                if (matDoc.Issues.Count > 0)   // M125
                    _log.Warn("Material", $"{binEntry.DisplayName}: {matDoc.Issues.Count} issue(s) repaired while reading - see the banner in the Character Editor's Material tab.");
            }
            else if (matDoc is not null && matDoc.Materials.Count > 0)
            {
                MaterialEditor.Load(matDoc, binEntry, bytes);
                HasMaterialData = true;
                // M50: the materials list lives in the Inspector's Materials tab now (the Content
                // Browser quick-list was removed) — jump straight to it for materials.bin selections.
                if (binEntry.Path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase))
                { InspectorTab = InspectorTabs.Materials; AssetDataExpanded = true; }
                if (MaterialEditor.UnresolvedCount > 0)
                    _log.Warn("Material", $"{binEntry.DisplayName}: {matDoc.Materials.Count} material(s), {MaterialEditor.UnresolvedCount} texture path(s) unresolved in this WAD.");
                else
                    _log.Info("Material", $"{binEntry.DisplayName}: {matDoc.Materials.Count} material(s).");
                if (matDoc.Issues.Count > 0)   // M125
                    _log.Warn("Material", $"{binEntry.DisplayName}: {matDoc.Issues.Count} issue(s) repaired while reading — see the ⚠ banner in the Materials tab (affected materials are marked red).");
            }
            else { MaterialEditor.Clear(); HasMaterialData = false; }
        });
    }

    private void ApplyMaterialToViewport()
    {
        var bytes = MaterialEditor.Serialize();
        if (bytes is null) return;
        // M505: arm auto-save BEFORE the viewport branch below, which returns early when the open .bin does
        // not match the loaded mesh. The edit is unsaved state whether or not anything can preview it.
        if (MaterialEditor.IsDirty) ScheduleAutoSave();
        try
        {
            if (MaterialEditor.Kind == MaterialSourceKind.ChampionSkin && CurrentMesh is { } mesh)
            {
                var resolved = ChampionMaterialResolver.Resolve(bytes, ResolveBinName, ResolveWadPath);
                CurrentModelTextures = BuildSubmeshTextures(mesh, resolved, "material preview");
            }
            else if (MaterialEditor.Kind == MaterialSourceKind.MapMaterials && _currentMap is { } map && CurrentMesh is not null)
            {
                var names = map.Groups.Select(g => g.Material).Where(m => m.Length > 0).Distinct().ToList();
                var m2t = MapGeoMaterialResolver.Resolve(bytes, names, ResolveWadPath);
                var profiles = MaterialProfiles.ForMapMaterials(bytes, names, ResolveBinName, ResolveWadPath);
                CurrentModelTextures = BuildMapTextures(map, m2t, profiles, names.Count, _currentMapEntry?.Path);
            }
            else { _log.Info("Material", "Nothing in the viewport to preview — select the matching .skn/.mapgeo."); return; }
            // M501: the assignment above only feeds GL. Tell DX11 too, or the two viewports show different
            // materials for the same edit.
            NotifyMaterialsChanged();
            _log.Success("Material", "Applied material edits to the viewport (live).");
        }
        catch (Exception ex) { _log.Error("Material", $"Apply failed: {ex.Message}"); }
    }

    private Task SaveMaterialOverride() => SaveMaterialOverrideFor(MaterialEditor, ApplyMaterialToViewport);

    /// <summary>M642: the one save path for both material editors - the inspector's and the character
    /// window's. The editor supplies the bin and the bytes; the caller supplies the preview to refresh.</summary>
    private async Task SaveMaterialOverrideFor(MaterialEditorViewModel editor, Action applyToViewport)
    {
        if (editor.BinEntry is not { } binEntry) { _log.Warn("Material", "No material .bin open."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!editor.IsDirty) { _log.Info("Material", "No material edits to save."); return; }
        if (!await EnsureProjectSavedAsync()) return;

        var bytes = editor.Serialize();
        if (bytes is null) return;
        bytes = RebaseOntoCurrent(binEntry, bytes, editor.BaseBytes, "Material");
        try { _ = new LeagueToolkit.Core.Meta.BinTree(new MemoryStream(bytes, false)); }
        catch (Exception ex) { _log.Error("Material", $"Edited material .bin failed to re-parse — NOT saved: {ex.Message}"); return; }

        // M126: one save path for project bins — folder-project files are written IN PLACE (and any
        // stale shadow override dissolves); only wad-backed assets go to the override workspace.
        if (!await SaveMapBinBytesAsync(binEntry, bytes)) return;
        applyToViewport();
        UndoService.MarkSaved();
    }

    private async Task ReplaceTextureForSlot(TextureSlotViewModel slot, Action applyToViewport)
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
            applyToViewport();
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
        InvalidateRayIndex();
        _currentMapProfiles = null;
        _mapVisibility = MapVisibilityDefinition.Empty;
        _mapControllers = null;
        _visibilityResolver = null;
        RebuildVisibilityAxes(_mapVisibility);
        VisibilityLayerBits.Clear();
        PlacementVisibilityLayerBits.Clear();
        HasPlacementLayerSelection = false;
        PlacementLayerSummary = "";
        LayerControllerChoices.Clear();
        _layerControllerHashes.Clear();
        _currentMapBytes = null;
        _currentMapEntry = null;
        MapGeneration++;
        OnPropertyChanged(nameof(CanBakeLighting));   // M158
        OnPropertyChanged(nameof(HasMapForLayout));  // M147
        OnPropertyChanged(nameof(MeshesWithoutLightmapUv));
        OnPropertyChanged(nameof(MeshesWithStrippedLightmap));   // M468
        OnPropertyChanged(nameof(HasStrippedLightmap));
        OnPropertyChanged(nameof(LegacyImportedMeshCount));      // M472
        OnPropertyChanged(nameof(HasLegacyImport));
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
        _mapTerrainTops = null;
        _mapTerrainExtras = null;
        CurrentLightmapScale = 1.0;
        CurrentSunProperties = null;
        CurrentModelParticles = null;
        SelectedParticleTreeItem = null;
        ParticleMarkers = null;
        CurrentModelProbes = null;
        CurrentModelProps = null;
        CurrentModelSounds = null;                                        // M55
        Sound.StopAll(); _activeAmbience.Clear(); _mapAudioBanks = null;   // M56
        SelectedSound = null; AmbienceEnabled = false;
        BucketGridLines = null;
        MapContent.SetBucketGrids(Array.Empty<MapBucketGridInfo>());
        HasBucketGrids = false;   // M77
        CurrentPropMeshes = null;
        ShowPropMeshes = false;
        MapContent.AddedMeshes.Clear();                                    // M79
        SetMapContentSelection(Array.Empty<MapOutlinerItemViewModel>(), null);
        SelectedAddedMesh = null;
        HasMapGeo = false;
        _propInstances = System.Array.Empty<PropInstanceData>();
        OnPropertyChanged(nameof(HasAddedMeshes));
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
        _vfxTextureCache.Clear(); _vfxTextureMultCache.Clear(); _vfxDistortionTextureCache.Clear(); _vfxColorTextureCache.Clear(); _vfxMeshCache.Clear();
        CurrentAnimation = null;
        AnimationTime = 0;
        Animation.Clear();
        MeshInspector.Clear();
        MapGeoInspector.Clear();
    }

    // ---- Data-driven map visibility layers ---------------------------------------------------------

    public sealed partial class VisibilityAxisViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _owner;
        public MapVisibilityAxis Axis { get; }
        public string Name => Axis.Name;
        public IReadOnlyList<string> Options { get; }
        [ObservableProperty] private int _selectedIndex;

        internal VisibilityAxisViewModel(MainWindowViewModel owner, MapVisibilityAxis axis)
        {
            _owner = owner;
            Axis = axis;
            Options = new[] { "All" }.Concat(axis.Layers.Select(l => l.Name)).ToList();
        }

        public int SelectedBit => SelectedIndex <= 0 || SelectedIndex > Axis.Layers.Count
            ? 0 : Axis.Layers[SelectedIndex - 1].Bit;

        partial void OnSelectedIndexChanged(int value)
        {
            if (!_owner._visibilityUiLoading) _owner.ApplyMapVisibility();
        }
    }

    public ObservableCollection<VisibilityAxisViewModel> VisibilityAxes { get; } = new();
    [ObservableProperty] private bool _hasVisibilityAxes;
    private bool _visibilityUiLoading;

    private IReadOnlyDictionary<uint, int> CurrentVisibilitySelections =>
        VisibilityAxes.ToDictionary(a => a.Axis.DefinitionFieldHash, a => a.SelectedBit);

    private int CurrentPrimaryVisibilityBit => VisibilityAxes.FirstOrDefault(a => a.Axis.IsPrimary)?.SelectedBit ?? 0;

    private void RebuildVisibilityAxes(MapVisibilityDefinition definition, IReadOnlyList<int>? selectedIndices = null)
    {
        _visibilityUiLoading = true;
        try
        {
            VisibilityAxes.Clear();
            for (int i = 0; i < definition.Axes.Count; i++)
            {
                var vm = new VisibilityAxisViewModel(this, definition.Axes[i]);
                vm.SelectedIndex = selectedIndices is not null && i < selectedIndices.Count ? selectedIndices[i] : 0;
                VisibilityAxes.Add(vm);
            }
            HasVisibilityAxes = VisibilityAxes.Count > 0;
        }
        finally { _visibilityUiLoading = false; }
    }

    /// <summary>Compute per-group visibility from the map-defined axes and push it to the viewport.</summary>
    // M104: render regions (mapgeo v18 renderRegionHash). Off hides every region-assigned mesh, leaving
    // the region-independent base geometry — the fastest way to see what a region is contributing.
    [ObservableProperty] private bool _renderRegionsEnabled = true;
    [ObservableProperty] private bool _hasRenderRegions;
    partial void OnRenderRegionsEnabledChanged(bool value) => ApplyMapVisibility();

    /// <summary>M661: which mapgeo GROUPS come from a mirrored (negative-determinant) mesh. The OpenGL
    /// viewport folds this into its per-submesh material; the D3D11 viewport has no per-mesh transform to
    /// read, so it is published here and both read the same array.</summary>
    public IReadOnlyList<bool>? CurrentModelSubmeshMirrored { get; private set; }

    private void ApplyMapVisibility()
    {
        if (_currentMap is not { } map)
        { CurrentModelSubmeshVisible = null; CurrentModelSubmeshMirrored = null; return; }
        var selections = CurrentVisibilitySelections;
        var resolver = _visibilityResolver ??= new MapVisibilityResolver(_mapControllers, _mapVisibility);
        var regionOf = map.Meshes.ToDictionary(m => m.Index, m => m.RegionHash);
        // M105: pending layer edits preview live — the group snapshot keeps the FILE's values, so the
        // check reads the mesh's effective (edited) mask/controller when there is one.
        var meshByIdx = map.Meshes.ToDictionary(m => m.Index);
        var hiddenByUser = MapContent.AllMapPieces
            .Where(p => !p.IsEditorVisible || p.IsDisabled || p.IsRemoved)
            .Select(p => p.MeshIndex).ToHashSet();
        HasRenderRegions = regionOf.Values.Any(r => r != 0);
        var vis = new bool[map.Groups.Count];
        for (int i = 0; i < vis.Length; i++)
        {
            var g = map.Groups[i];
            int flags = g.VisibilityFlags;
            uint ctrl = g.ControllerHash;
            if (g.MeshIndex >= 0 && meshByIdx.TryGetValue(g.MeshIndex, out var src))
            { flags = src.EffectiveVisibility; ctrl = src.EffectiveController; }
            vis[i] = resolver.IsVisible(flags, ctrl, selections);
            if (hiddenByUser.Contains(g.MeshIndex)) vis[i] = false;
            if (vis[i] && !RenderRegionsEnabled && g.MeshIndex >= 0
                && regionOf.TryGetValue(g.MeshIndex, out var region) && region != 0)
                vis[i] = false;
        }
        CurrentModelSubmeshVisible = vis;

        // M661: the same groups, by whether their source mesh is mirrored. Built here because this is
        // where the group -> mesh mapping already is.
        var mirroredMesh = map.Meshes.ToDictionary(m => m.Index, m => m.IsMirrored);
        var mirrored = new bool[map.Groups.Count];
        for (int i = 0; i < mirrored.Length; i++)
            mirrored[i] = map.Groups[i].MeshIndex >= 0
                && mirroredMesh.TryGetValue(map.Groups[i].MeshIndex, out var mir) && mir;
        CurrentModelSubmeshMirrored = mirrored;
        // M385: the grass tint is part of the map STATE, not the map build - Riot swaps it for the
        // mAlternateAssets entry whose visibility flag is active, so it has to follow this.
        RefreshGrassTint();
        UpdateParticleMarkers();
        UpdatePlaceableMarkers();
        RefreshMeshDetails();  // keep the inspector's mesh details + "why visible/hidden" in sync
        PruneSelectionToVisible(); // hidden (filtered-out) meshes must not stay selected/transformable
        if (PlayAllParticles) RebuildParticlePlayback();
        if (AmbienceEnabled) UpdateAmbience(_lastCamPosForAudio, force: true);
    }

    /// <summary>Visibility diagnostic for the primary-selected mesh under the current map filters.</summary>
    [ObservableProperty] private string _meshVisibilityReason = "";

    /// <summary>The full mesh-details inspector for the selected mapgeo mesh (M33).</summary>
    public MeshDetailsViewModel MeshDetails { get; } = new();

    // ---- M517: change the material of an EXISTING mesh -------------------------------------------

    /// <summary>The selected mesh's material, as the picker sees it. Setting it queues the swap; it is
    /// written into the mapgeo by Save Map Edits, like every other mesh edit.</summary>
    public string? SelectedMeshMaterial
    {
        get => _selection.Primary is { } mesh ? mesh.EffectiveMaterial : null;
        set
        {
            if (_selection.Primary is not { } mesh || value is not { Length: > 0 } name) return;
            if (string.Equals(mesh.EffectiveMaterial, name, StringComparison.Ordinal)) return;

            string? before = mesh.MaterialEdit;
            mesh.MaterialEdit = name;
            UndoService.PushApplied(new MeshMaterialCommand(_currentMap, mesh, before, name,
                AfterMeshMaterialEdit));
            AfterMeshMaterialEdit();
        }
    }

    /// <summary>How many submeshes the swap will cover. A mesh with several is being told to draw
    /// entirely in the chosen material, which is worth saying out loud before it happens.</summary>
    public int SelectedMeshSubmeshCount => _selection.Primary?.Materials.Count ?? 0;
    public bool SelectedMeshHasSeveralMaterials =>
        _selection.Primary is { } m && m.Materials.Distinct(StringComparer.Ordinal).Count() > 1;
    public bool SelectedMeshMaterialChanged => _selection.Primary?.HasMaterialEdit == true;
    public string SelectedMeshOriginalMaterial =>
        _selection.Primary is { Materials.Count: > 0 } m ? m.Materials[0] : "";

    private void AfterMeshMaterialEdit()
    {
        if (_currentMap is { } map)
            HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes)
                || MapGeoMaterialWriter.HasEdits(map.Meshes)
                || MapContent.AllMapPieces.Any(x => x.IsRemoved);
        RefreshSelectedMeshMaterial();

        // M518: bump the viewport revision WITHOUT invalidating MapMaterialNames.
        //
        // NotifyMaterialsChanged does both, and the second half made the picker unusable: replacing the
        // ComboBox's ItemsSource while it is processing a selection makes it re-resolve and snap back to
        // the previous value, so every pick reverted instantly. Measured on the headless platform - with
        // the ItemsSource replaced the view model stayed on the old material, without it the pick stuck.
        //
        // And the list has not changed anyway. Which material a MESH uses is not which materials EXIST.
        MaterialsRevision++;
        ScheduleAutoSave();
    }

    private void RefreshSelectedMeshMaterial()
    {
        OnPropertyChanged(nameof(SelectedMeshMaterial));
        OnPropertyChanged(nameof(SelectedMeshSubmeshCount));
        OnPropertyChanged(nameof(SelectedMeshHasSeveralMaterials));
        OnPropertyChanged(nameof(SelectedMeshMaterialChanged));
        OnPropertyChanged(nameof(SelectedMeshOriginalMaterial));
    }

    /// <summary>M517: put the mesh back on the material the file names.</summary>
    [RelayCommand]
    private void RevertSelectedMeshMaterial()
    {
        if (_selection.Primary is not { HasMaterialEdit: true } mesh) return;
        string? before = mesh.MaterialEdit;
        mesh.MaterialEdit = null;
        UndoService.PushApplied(new MeshMaterialCommand(_currentMap, mesh, before, null,
            AfterMeshMaterialEdit));
        AfterMeshMaterialEdit();
    }

    /// <summary>M101: scope the Materials tab to the selected mesh/meshes; empty selection = show all.</summary>
    private void RefreshMaterialMeshFilter()
    {
        if (_currentMap is not { } map || _selection.Count == 0) { MaterialEditor.SetMeshFilter(null); return; }
        var indices = _selection.Items.Select(m => m.Index).ToHashSet();
        MaterialEditor.SetMeshFilter(map.Groups
            .Where(g => indices.Contains(g.MeshIndex) && !string.IsNullOrEmpty(g.Material))
            .Select(g => g.Material));
    }

    private void RefreshMeshDetails()
    {
        RefreshMaterialMeshFilter();
        RefreshSelectedMeshMaterial();   // M517
        RefreshLayerEditor();   // M105
        if (_selection.Primary is not { } m || _visibilityResolver is null)
        { MeshVisibilityReason = ""; MeshDetails.Clear(); return; }
        // M105: diagnose the EFFECTIVE (edited) values so the details row matches what the viewport shows
        var d = _visibilityResolver.Resolve(m.EffectiveVisibility, m.EffectiveController, CurrentVisibilitySelections);
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

    // ---- M102/M105: editable layer system for the selected meshes ----

    public sealed partial class LayerBitViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _owner;
        public string Name { get; }
        public int Bit { get; }
        [ObservableProperty] private bool _isOn;
        internal bool Loading;

        public LayerBitViewModel(MainWindowViewModel owner, string name, int bit)
        { _owner = owner; Name = name; Bit = bit; }

        partial void OnIsOnChanged(bool value)
        {
            if (!Loading) _owner.SetLayerBitOnSelection(Bit, value);
        }
    }

    /// <summary>The primary map-axis checkboxes (state mirrors the primary selected mesh).</summary>
    public ObservableCollection<LayerBitViewModel> VisibilityLayerBits { get; } = new();

    public sealed partial class PlacementLayerBitViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _owner;
        public string Name { get; }
        public int Bit { get; }
        public bool Loading;
        [ObservableProperty] private bool _isOn;
        public PlacementLayerBitViewModel(MainWindowViewModel owner, string name, int bit)
        { _owner = owner; Name = name; Bit = bit; }
        partial void OnIsOnChanged(bool value)
        { if (!Loading) _owner.SetPlacementLayerBit(Bit, value); }
    }

    public ObservableCollection<PlacementLayerBitViewModel> PlacementVisibilityLayerBits { get; } = new();
    [ObservableProperty] private bool _hasPlacementLayerSelection;
    [ObservableProperty] private string _placementLayerSummary = "";

    private static int PlacementFlags(MapOutlinerItemViewModel item) => item switch
    {
        ParticlePlacementViewModel p => p.EffectiveVisibilityFlags,
        MapSoundViewModel s => s.EffectiveVisibilityFlags,
        AnimatedPropViewModel p => p.EffectiveVisibilityFlags,
        CubemapProbeViewModel p => p.EffectiveVisibilityFlags,
        _ => 255,
    };

    private IEnumerable<MapOutlinerItemViewModel> SelectedPlacementLeaves() => _mapContentSelection.Where(item =>
        item is ParticlePlacementViewModel or MapSoundViewModel or AnimatedPropViewModel or CubemapProbeViewModel);

    private static void SetPlacementFlags(MapOutlinerItemViewModel item, int? flags)
    {
        switch (item)
        {
            case ParticlePlacementViewModel p: p.EditedVisibilityFlags = flags; break;
            case MapSoundViewModel s: s.EditedVisibilityFlags = flags; break;
            case AnimatedPropViewModel p: p.EditedVisibilityFlags = flags; break;
            case CubemapProbeViewModel p: p.EditedVisibilityFlags = flags; break;
        }
    }

    private void RefreshPlacementLayerEditor()
    {
        // Some maps author mVisibilityFlags without a discoverable controller/layer-name table. Keep the
        // field editable there too: named bits come from the shipping map when available, raw bit names are
        // the lossless fallback rather than hiding the feature altogether.
        IReadOnlyList<VisibilityLayer> declared = _mapVisibility.Primary?.Layers is { Count: > 0 } named
            ? named
            : Enumerable.Range(0, 8).Select(i => new VisibilityLayer($"Bit {i}", 1 << i)).ToArray();
        if (!PlacementVisibilityLayerBits.Select(b => b.Bit).SequenceEqual(declared.Select(d => d.Bit)))
        {
            PlacementVisibilityLayerBits.Clear();
            foreach (var layer in declared)
                PlacementVisibilityLayerBits.Add(new PlacementLayerBitViewModel(this, layer.Name, layer.Bit));
        }
        var selected = SelectedPlacementLeaves().ToList();
        HasPlacementLayerSelection = selected.Count > 0;
        if (!HasPlacementLayerSelection) { PlacementLayerSummary = ""; return; }
        int flags = PlacementFlags(selected[^1]);
        foreach (var bit in PlacementVisibilityLayerBits)
        {
            bit.Loading = true;
            bit.IsOn = (flags & bit.Bit) != 0;
            bit.Loading = false;
        }
        PlacementLayerSummary = $"{MapVisibility.Label(flags, _mapVisibility.Primary)} · mask 0b{Convert.ToString(flags & 0xFF, 2).PadLeft(8, '0')}"
            + (selected.Count > 1 ? $" · applies to {selected.Count} selected objects" : "");
    }

    private void SetPlacementLayerBit(int bit, bool on)
    {
        foreach (var item in SelectedPlacementLeaves().ToList())
        {
            int flags = PlacementFlags(item);
            SetPlacementFlags(item, on ? flags | bit : flags & ~bit);
        }
        RefreshPlacementLayerEditor();
    }

    [RelayCommand]
    private void SetPlacementLayersAll()
    { foreach (var item in SelectedPlacementLeaves().ToList()) SetPlacementFlags(item, 255); RefreshPlacementLayerEditor(); }

    [RelayCommand]
    private void ResetPlacementLayerEdits()
    { foreach (var item in SelectedPlacementLeaves().ToList()) SetPlacementFlags(item, null); RefreshPlacementLayerEditor(); }

    /// <summary>Controller choices for the selected mesh — "None" + every controller in the map's bins.</summary>
    public ObservableCollection<string> LayerControllerChoices { get; } = new();
    private readonly List<uint> _layerControllerHashes = new();
    [ObservableProperty] private int _selectedLayerControllerIndex = -1;
    [ObservableProperty] private bool _meshBackfaceDisabled;
    [ObservableProperty] private bool _hasLayerSelection;
    [ObservableProperty] private string _layerSummary = "";
    private bool _layerUiLoading;

    /// <summary>Refill the layer card from the primary selection (called from RefreshMeshDetails).</summary>
    private void RefreshLayerEditor()
    {
        _layerUiLoading = true;
        try
        {
            var declared = _mapVisibility.Primary?.Layers ?? Array.Empty<VisibilityLayer>();
            if (!VisibilityLayerBits.Select(b => b.Bit).SequenceEqual(declared.Select(d => d.Bit)))
            {
                VisibilityLayerBits.Clear();
                foreach (var layer in declared)
                    VisibilityLayerBits.Add(new LayerBitViewModel(this, layer.Name, layer.Bit));
            }

            if (_selection.Primary is not { } m || _currentMap is null)
            { HasLayerSelection = false; LayerSummary = ""; return; }

            HasLayerSelection = true;
            int flags = m.EffectiveVisibility;
            foreach (var b in VisibilityLayerBits)
            {
                b.Loading = true;
                b.IsOn = (flags & b.Bit) != 0;
                b.Loading = false;
            }
            MeshBackfaceDisabled = m.EffectiveDisableBackface;

            // controller list (rebuilt when the map's controllers change)
            if (LayerControllerChoices.Count == 0 && _mapControllers is { } mc)
            {
                LayerControllerChoices.Add("None (always in layer system)");
                _layerControllerHashes.Clear();
                _layerControllerHashes.Add(0);
                foreach (var ci in mc.List())
                {
                    LayerControllerChoices.Add(ci.Label);
                    _layerControllerHashes.Add(ci.Hash);
                }
            }
            int idx = _layerControllerHashes.IndexOf(m.EffectiveController);
            SelectedLayerControllerIndex = idx;   // -1 = a controller the bins don't list; combo shows empty

            int selCount = _selection.Count;
            int edited = _currentMap.Meshes.Count(x => x.HasLayerEdit);
            LayerSummary = $"{MapVisibility.Label(flags, _mapVisibility.Primary)} · mask 0b{Convert.ToString(flags & 0xFF, 2).PadLeft(8, '0')}"
                           + (selCount > 1 ? $" · applies to {selCount} selected meshes" : "")
                           + (edited > 0 ? $" · {edited} unsaved layer edit(s)" : "");
        }
        finally { _layerUiLoading = false; }
    }

    /// <summary>Set/clear one primary visibility bit on every selected mesh (one undo step).</summary>
    internal void SetLayerBitOnSelection(int bit, bool on)
    {
        if (_layerUiLoading) return;
        string name = _mapVisibility.Primary?.Layers.FirstOrDefault(d => d.Bit == bit).Name ?? $"bit {bit}";
        ApplyLayerEdit($"{(on ? "Add to" : "Remove from")} {name} Layer", m =>
        {
            int flags = m.EffectiveVisibility;
            m.VisibilityEdit = on ? flags | bit : flags & ~bit;
        });
    }

    partial void OnSelectedLayerControllerIndexChanged(int value)
    {
        if (_layerUiLoading || value < 0 || value >= _layerControllerHashes.Count) return;
        uint hash = _layerControllerHashes[value];
        ApplyLayerEdit(hash == 0 ? "Clear Visibility Controller" : "Assign Visibility Controller",
            m => m.ControllerEdit = hash);
    }

    partial void OnMeshBackfaceDisabledChanged(bool value)
    {
        if (_layerUiLoading) return;
        ApplyLayerEdit(value ? "Disable Backface Culling" : "Enable Backface Culling",
            m => m.BackfaceEdit = value);
    }

    [RelayCommand]
    private void SetLayersAll() => ApplyLayerEdit("Show On All Layers", m => m.VisibilityEdit = 255);

    [RelayCommand]
    private void ResetLayerEdits() => ApplyLayerEdit("Reset Layer Edits", m =>
    { m.VisibilityEdit = null; m.ControllerEdit = null; m.BackfaceEdit = null; });

    /// <summary>Run one mutation over the selection as a single undoable command, then refresh.</summary>
    private void ApplyLayerEdit(string name, Action<MapGeoMesh> mutate)
    {
        if (_currentMap is not { } map || _selection.Count == 0) return;
        var entries = new List<(MapGeoMesh, MeshLayerCommand.State, MeshLayerCommand.State)>();
        foreach (var m in _selection.Items)
        {
            var before = MeshLayerCommand.State.Capture(m);
            mutate(m);
            entries.Add((m, before, MeshLayerCommand.State.Capture(m)));
        }
        UndoService.PushApplied(new MeshLayerCommand(name, map, entries, OnLayerEditApplied));
        OnLayerEditApplied();
    }

    private void OnLayerEditApplied()
    {
        if (_currentMap is { } map)
            HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
        ApplyMapVisibility();   // re-evaluates effective flags and refreshes the layer card via RefreshMeshDetails
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
    /// <summary>M170: verify every MapCubemapProbe points at a real DDS cubemap. A plain 2D texture
    /// bound where the engine expects 6 faces crashes the game at load — the fault that used to be
    /// blamed on wide WAD overlays (ltk-manager#305).</summary>
    [RelayCommand]
    private void CheckCubemapProbes()
    {
        if (CurrentModelProbes is not { Count: > 0 } probes)
        { _log.Warn("Cubemaps", "No cubemap probes in the loaded map (open a map's materials .bin first)."); return; }

        var issues = Formats.MapGeo.CubemapProbeValidator.Validate(probes, path =>
        {
            try { return ReadAssetByPath(path); } catch { return null; }
        });

        if (issues.Count == 0)
        { _log.Success("Cubemaps", $"All {probes.Count} cubemap probe(s) point at valid DDS cubemaps."); return; }

        _log.Error("Cubemaps", $"{issues.Count} of {probes.Count} cubemap probe(s) would fail to bind — this crashes the game at load:");
        foreach (var i in issues.Take(20))
            _log.Error("Cubemaps", $"   '{i.ProbeName}' -> {i.TexturePath}  {i.Problem}");
        if (issues.Count > 20) _log.Error("Cubemaps", $"   (+{issues.Count - 20} more)");
        _log.Info("Cubemaps", "Fix: re-export the texture as a DDS cubemap (6 square faces, DDSCAPS2_CUBEMAP set), " +
                              "or point the probe at one of Riot's existing cubemaps.");
    }

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

    /// <summary>Read the shipping map's visibility axes, then index controller graphs from sibling bins.</summary>
    private void BuildMapVisibility(string mapgeoPath, MapGeoAsset map)
    {
        var dir = mapgeoPath[..(mapgeoPath.LastIndexOf('/') + 1)];
        var bins = new List<byte[]>();
        WadAssetEntry? primaryMaterials = TryResolveMaterialsBin(mapgeoPath, out var resolvedMaterials)
            ? resolvedMaterials : null;
        foreach (var e in AssetEntries.Where(e => e.IsResolved
                     && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                     && e.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                     && (primaryMaterials is null || e.PathHash != primaryMaterials.PathHash))
                 .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            try { bins.Add(ReadAsset(e.PathHash)); } catch { /* skip unreadable bins */ }
        }
        // The mapgeo's own effective .materials.bin is authoritative for duplicate controller object
        // hashes. Add it last because MapVisibilityControllers deliberately uses later-bin-wins merging.
        // This matters for old/custom rifts whose controller graphs differ from current Riot Map11.
        if (primaryMaterials is not null)
            try { bins.Add(ReadAsset(primaryMaterials.PathHash)); } catch { /* no controller data */ }
        byte[]? shippingBin = null;
        var match = System.Text.RegularExpressions.Regex.Match(mapgeoPath, @"/mapgeometry/map(?<id>\d+)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
            shippingBin = ReadAssetByPath($"data/maps/shipping/map{match.Groups["id"].Value}/map{match.Groups["id"].Value}.bin");
        _mapVisibility = MapVisibility.Parse(shippingBin, ResolveBinName);
        if (!_mapVisibility.HasAxes) _mapVisibility = MapVisibility.Infer(map.Meshes.Select(m => m.VisibilityFlags));

        _mapControllers = MapVisibilityControllers.Build(bins, _mapVisibility);
        _visibilityResolver = new MapVisibilityResolver(_mapControllers, _mapVisibility);
        RebuildVisibilityAxes(_mapVisibility);
        LayerControllerChoices.Clear();
        _layerControllerHashes.Clear();
        VisibilityLayerBits.Clear();

        string axes = _mapVisibility.HasAxes
            ? string.Join(", ", _mapVisibility.Axes.Select(a => $"{a.Name} ({a.Layers.Count} states, initial {a.InitialMask})"))
            : "none";
        _log.Info("MapGeo", $"Visibility axes: {axes}; {_mapControllers.Count} controller(s) from {bins.Count} sibling bin(s).");
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
                    Name = $"{MapVisibility.Label(g.Key, _mapVisibility.Primary)} — {g.Count()} mesh(es)",
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
    [ObservableProperty] private bool _hasMapGeo;   // M79: a .mapgeo is loaded (enables Add Mesh to Map)

    // ---- Multi-selection + batch transform (M30) -----------------------
    private readonly SelectionSet<MapGeoMesh> _selection = new();
    private bool _syncingTreeSelection;   // reentrancy guard: tree<->selection sync must not recurse
    private readonly List<MapOutlinerItemViewModel> _mapContentSelection = new();
    private MapOutlinerItemViewModel? _mapContentAnchor;
    private bool _outlinerMultiSelecting;

    public IReadOnlyList<MapOutlinerItemViewModel> SelectedMapContentItems => _mapContentSelection;
    public bool HasMapContentSelection => _mapContentSelection.Count > 0;
    public string MapContentSelectionText => _mapContentSelection.Count switch
    {
        0 => "",
        1 => "1 selected",
        var count => $"{count} selected",
    };

    private void RaiseMapContentSelection()
    {
        OnPropertyChanged(nameof(SelectedMapContentItems));
        OnPropertyChanged(nameof(HasMapContentSelection));
        OnPropertyChanged(nameof(MapContentSelectionText));
        DeleteMapContentSelectionCommand.NotifyCanExecuteChanged();
        CopyMapContentSelectionCommand.NotifyCanExecuteChanged();
        CutMapContentSelectionCommand.NotifyCanExecuteChanged();
        PasteMapContentCommand.NotifyCanExecuteChanged();
        DisableMapContentSelectionCommand.NotifyCanExecuteChanged();
        EnableMapContentSelectionCommand.NotifyCanExecuteChanged();
        HideMapContentSelectionCommand.NotifyCanExecuteChanged();
        ShowMapContentSelectionCommand.NotifyCanExecuteChanged();
    }

    private void SetMapContentSelection(IEnumerable<MapOutlinerItemViewModel> items, MapOutlinerItemViewModel? anchor)
    {
        foreach (var old in _mapContentSelection) old.IsSelected = false;
        _mapContentSelection.Clear();
        foreach (var item in items.Distinct()) { item.IsSelected = true; _mapContentSelection.Add(item); }
        _mapContentAnchor = anchor;
        RaiseMapContentSelection();
        RefreshPlacementLayerEditor();
        RefreshParticleBatch();   // M644: two or more particles make a batch
    }

    private List<MapOutlinerItemViewModel> FlatMapContentItems() =>
        MapContent.AllMapPieces.Cast<MapOutlinerItemViewModel>()
            .Concat(MapContent.AllParticles)
            .Concat(MapContent.AllProps)
            .Concat(MapContent.Probes)
            .Concat(MapContent.Sounds)
            .Concat(MapContent.AddedMeshes)
            .ToList();

    /// <summary>Ctrl/Shift selection owned by the outliner because Avalonia TreeView itself is single-select.</summary>
    public void SelectMapContentFromTree(MapOutlinerItemViewModel item, bool toggle, bool range)
    {
        var next = _mapContentSelection.ToList();
        if (range)
        {
            var flat = FlatMapContentItems();
            int to = flat.IndexOf(item);
            int from = _mapContentAnchor is null ? to : flat.IndexOf(_mapContentAnchor);
            if (to >= 0)
            {
                if (from < 0) from = to;
                next = flat.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1).ToList();
            }
        }
        else if (toggle)
        {
            if (!next.Remove(item)) next.Add(item);
            _mapContentAnchor = item;
        }
        else next = new List<MapOutlinerItemViewModel> { item };

        SetMapContentSelection(next, _mapContentAnchor ?? item);
        var selectedMeshes = _mapContentSelection.OfType<MapPieceViewModel>()
            .Select(p => _currentMap?.Meshes.FirstOrDefault(m => m.Index == p.MeshIndex))
            .Where(m => m is not null).Cast<MapGeoMesh>().ToList();
        _outlinerMultiSelecting = true;
        try
        {
            _selection.SetMany(selectedMeshes);
            SelectedOutlinerItem = item;
        }
        finally { _outlinerMultiSelecting = false; }
    }

    private bool CanEditMapContentSelection() => _mapContentSelection.Count > 0;

    [RelayCommand(CanExecute = nameof(CanEditMapContentSelection))]
    private void HideMapContentSelection()
    { foreach (var item in _mapContentSelection.ToList()) item.IsEditorVisible = false; }

    [RelayCommand(CanExecute = nameof(CanEditMapContentSelection))]
    private void ShowMapContentSelection()
    { foreach (var item in _mapContentSelection.ToList()) item.IsEditorVisible = true; }

    [RelayCommand(CanExecute = nameof(CanEditMapContentSelection))]
    private void DisableMapContentSelection()
    { foreach (var item in _mapContentSelection.ToList()) item.IsDisabled = true; }

    [RelayCommand(CanExecute = nameof(CanEditMapContentSelection))]
    private void EnableMapContentSelection()
    { foreach (var item in _mapContentSelection.ToList()) item.IsDisabled = false; }

    // ---------------------------------------------------------------- M553: viewport clipboard
    /// <summary>
    /// One copied mesh, held as plain geometry rather than as a reference.
    ///
    /// <para>Copying a MAP PIECE and copying an ADDED mesh have to produce the same thing, because paste
    /// can only ever create an added mesh - the original map's meshes live in the mapgeo and are edited
    /// by flagging, not by insertion. So the clipboard stores the geometry itself and both sources
    /// normalise into it.</para>
    /// </summary>
    private sealed record CopiedMapMesh(
        string Name, string Material, float[] Positions, float[] Normals, float[] Uvs, int[] Indices,
        System.Numerics.Vector3 LocalCenter, System.Numerics.Vector3 Offset, System.Numerics.Vector3 RotationDegrees, System.Numerics.Vector3 Scale,
        int VisibilityMask, int EnabledVisibilityMask);

    private readonly List<CopiedMapMesh> _mapClipboard = new();

    public bool HasMapClipboard => _mapClipboard.Count > 0;

    /// <summary>Pull a selected outliner item into clipboard form, or null if it carries no geometry.</summary>
    private CopiedMapMesh? ToCopiedMesh(MapOutlinerItemViewModel item)
    {
        if (item is AddedMapMeshViewModel added)
            return new CopiedMapMesh(added.Name, added.Material,
                (float[])added.Positions.Clone(), (float[])added.Normals.Clone(),
                (float[])added.Uvs.Clone(), (int[])added.Indices.Clone(),
                added.LocalCenter, added.Offset, added.RotationDegrees, added.Scale,
                added.VisibilityMask, added.EnabledVisibilityMask);

        // A map piece's geometry lives in the loaded asset, indexed by the piece's mesh index.
        if (item is not MapPieceViewModel piece || piece.MeshIndex < 0) return null;
        if (_currentMap is not { } map || piece.MeshIndex >= map.Meshes.Count) return null;
        var group = map.Groups.FirstOrDefault(g => g.MeshIndex == piece.MeshIndex);
        if (group is null || group.IndexCount <= 0) return null;

        // One shared buffer holds every mesh, so the copy takes this group's index range and compacts the
        // vertices it actually touches.
        var remap = new Dictionary<uint, int>();
        var indices = new List<int>(group.IndexCount);
        for (int i = group.StartIndex; i < group.StartIndex + group.IndexCount; i++)
        {
            uint v = map.Indices[i];
            if (!remap.TryGetValue(v, out int local)) remap[v] = local = remap.Count;
            indices.Add(local);
        }
        var positions = new float[remap.Count * 3];
        var normals = new float[remap.Count * 3];
        var uvs = new float[remap.Count * 2];
        var lo = new System.Numerics.Vector3(float.MaxValue);
        var hi = new System.Numerics.Vector3(float.MinValue);
        foreach (var (source, local) in remap)
        {
            var p = new System.Numerics.Vector3(map.Positions[source * 3], map.Positions[source * 3 + 1],
                                map.Positions[source * 3 + 2]);
            positions[local * 3] = p.X; positions[local * 3 + 1] = p.Y; positions[local * 3 + 2] = p.Z;
            if (map.Normals.Length >= (source + 1) * 3)
            {
                normals[local * 3] = map.Normals[source * 3];
                normals[local * 3 + 1] = map.Normals[source * 3 + 1];
                normals[local * 3 + 2] = map.Normals[source * 3 + 2];
            }
            if (map.Uvs.Length >= (source + 1) * 2)
            { uvs[local * 2] = map.Uvs[source * 2]; uvs[local * 2 + 1] = map.Uvs[source * 2 + 1]; }
            lo = System.Numerics.Vector3.Min(lo, p); hi = System.Numerics.Vector3.Max(hi, p);
        }
        // Asset positions are already WORLD space, so the copy is recentred on its own bbox and carries
        // the difference as its offset. Pasting then lands it exactly where the original sits.
        var centre = (lo + hi) * 0.5f;
        for (int i = 0; i < positions.Length; i += 3)
        { positions[i] -= centre.X; positions[i + 1] -= centre.Y; positions[i + 2] -= centre.Z; }

        int mask = map.Meshes[piece.MeshIndex].VisibilityFlags;
        return new CopiedMapMesh(piece.Name, group.Material, positions, normals, uvs, indices.ToArray(),
            System.Numerics.Vector3.Zero, centre, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One, mask, mask);
    }

    [RelayCommand(CanExecute = nameof(CanEditMapContentSelection))]
    private void CopyMapContentSelection()
    {
        var copied = _mapContentSelection.Select(ToCopiedMesh).OfType<CopiedMapMesh>().ToList();
        if (copied.Count == 0)
        { _log.Info("Map Content", "Nothing in the selection carries geometry that can be copied."); return; }

        _mapClipboard.Clear();
        _mapClipboard.AddRange(copied);
        OnPropertyChanged(nameof(HasMapClipboard));
        PasteMapContentCommand.NotifyCanExecuteChanged();
        int skipped = _mapContentSelection.Count - copied.Count;
        _log.Info("Map Content", $"Copied {copied.Count} mesh(es)."
            + (skipped > 0 ? $" {skipped} selected item(s) carry no geometry and were left out." : ""));
    }

    [RelayCommand(CanExecute = nameof(CanEditMapContentSelection))]
    private async Task CutMapContentSelection()
    {
        CopyMapContentSelection();
        if (_mapClipboard.Count == 0) return;
        await DeleteMapContentSelection();
    }

    private bool CanPasteMapContent() => _mapClipboard.Count > 0 && _currentMap is not null;

    /// <summary>
    /// M553: paste in place, as added meshes.
    ///
    /// <para>In place rather than nudged: a decal or a prop is copied in order to be moved deliberately,
    /// and an arbitrary offset is just a second thing to undo. The paste lands selected, so the gizmo is
    /// already on it.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPasteMapContent))]
    private void PasteMapContent()
    {
        if (_mapClipboard.Count == 0) return;
        var pasted = new List<AddedMapMeshViewModel>(_mapClipboard.Count);
        foreach (var mesh in _mapClipboard)
            pasted.Add(new AddedMapMeshViewModel
            {
                Name = mesh.Name.EndsWith(" (copy)", StringComparison.Ordinal) ? mesh.Name : mesh.Name + " (copy)",
                Positions = (float[])mesh.Positions.Clone(), Normals = (float[])mesh.Normals.Clone(),
                Uvs = (float[])mesh.Uvs.Clone(), Indices = (int[])mesh.Indices.Clone(),
                LocalCenter = mesh.LocalCenter, Material = mesh.Material,
                Offset = mesh.Offset, RotationDegrees = mesh.RotationDegrees, Scale = mesh.Scale,
                VisibilityMask = mesh.VisibilityMask, EnabledVisibilityMask = mesh.EnabledVisibilityMask,
                StateChanged = OnMapContentItemStateChanged,
            });

        var command = new MapContentPasteCommand(pasted, MapContent.AddedMeshes, AfterMapContentDelete);
        command.Execute();
        UndoService.PushApplied(command);
        OnPropertyChanged(nameof(HasAddedMeshes));
        if (pasted.Count > 0) SelectedOutlinerItem = pasted[^1];
        _log.Success("Map Content", $"Pasted {pasted.Count} mesh(es) in place - undo (Ctrl+Z) removes them. "
                                  + "Save Map Content Edits to persist.");
    }

    /// <summary>Adding pasted meshes, and taking them back out again.</summary>
    private sealed class MapContentPasteCommand : ReyEngine.Core.Undo.IEditorCommand
    {
        private readonly List<AddedMapMeshViewModel> _meshes;
        private readonly ObservableCollection<AddedMapMeshViewModel> _target;
        private readonly Action _after;

        public MapContentPasteCommand(List<AddedMapMeshViewModel> meshes,
            ObservableCollection<AddedMapMeshViewModel> target, Action after)
        { _meshes = meshes; _target = target; _after = after; }

        public string Name => "Paste Map Objects";
        public object? Context => null;
        public void Execute() { foreach (var m in _meshes) if (!_target.Contains(m)) _target.Add(m); _after(); }
        public void Undo() { foreach (var m in _meshes) _target.Remove(m); _after(); }
        public bool CanMergeWith(ReyEngine.Core.Undo.IEditorCommand next) => false;
        public void MergeWith(ReyEngine.Core.Undo.IEditorCommand next) => throw new NotSupportedException();
    }

    /// <summary>
    /// M513: put deleted objects back.
    ///
    /// <para>The undo stack covers the misclick you notice straight away; this covers the one you notice
    /// twenty edits later, or after reopening the map. Deleted pieces never left the outliner — they carry
    /// a red DEL badge — so the only thing missing was a way to act on them.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditMapContentSelection))]
    private void RestoreMapContentSelection()
    {
        var flagged = _mapContentSelection.Where(item => item.IsRemoved).ToList();
        if (flagged.Count == 0)
        { _log.Info("Map Content", "Nothing in the selection is marked for deletion."); return; }

        // The same command, run backwards: restoring IS the undo of a delete, and writing it twice is how
        // the two drift apart.
        var command = new MapContentDeleteCommand(flagged,
            Array.Empty<(AddedMapMeshViewModel, int)>(), MapContent.AddedMeshes,
            _currentMap, AfterMapContentDelete);
        command.Undo();
        UndoService.PushApplied(new RestoreCommand(command));
        _log.Success("Map Content", $"Restored {flagged.Count} object(s).");
    }

    /// <summary>A delete command with its direction flipped, so undoing a restore deletes again.</summary>
    private sealed class RestoreCommand : ReyEngine.Core.Undo.IEditorCommand
    {
        private readonly MapContentDeleteCommand _inner;
        public RestoreCommand(MapContentDeleteCommand inner) { _inner = inner; }
        public string Name => "Restore Map Objects";
        public object? Context => _inner.Context;
        public void Execute() => _inner.Undo();
        public void Undo() => _inner.Execute();
        public bool CanMergeWith(ReyEngine.Core.Undo.IEditorCommand next) => false;
        public void MergeWith(ReyEngine.Core.Undo.IEditorCommand next) => throw new NotSupportedException();
    }

    [RelayCommand(CanExecute = nameof(CanEditMapContentSelection))]
    private async Task DeleteMapContentSelection()
    {
        var selected = _mapContentSelection.ToList();
        if (selected.Count == 0) return;
        if (PromptOwner is not null && !await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete Map Objects",
                $"Mark {selected.Count} selected object(s) for deletion?\n\nThe viewport updates immediately. The map files are changed only when you save map edits.", "Delete"))
            return;

        // M513: through the undo stack, like every other map edit. Deleting was already non-destructive on
        // disk - a map piece is only flagged and nothing is written until the map is saved - but there was
        // no way to un-flag it, so a misclick cost whatever it took to rebuild the object.
        var flagged = selected.Where(item => item is not AddedMapMeshViewModel).ToList();
        var added = selected.OfType<AddedMapMeshViewModel>()
            .Select(mesh => (Mesh: mesh, Index: MapContent.AddedMeshes.IndexOf(mesh)))
            .Where(entry => entry.Index >= 0)
            .ToList();

        var command = new MapContentDeleteCommand(flagged, added, MapContent.AddedMeshes,
            _currentMap, AfterMapContentDelete);
        command.Execute();
        UndoService.PushApplied(command);

        _log.Info("Map Content", $"Marked {selected.Count} object(s) for deletion — undo (Ctrl+Z) brings "
                               + "them back. Save Map Content Edits to persist.");
    }

    /// <summary>Everything the viewport and the outliner need after objects come or go. Shared by the
    /// delete and its undo so the two can never refresh different things.</summary>
    private void AfterMapContentDelete()
    {
        OnPropertyChanged(nameof(HasAddedMeshes));
        PublishAddedMeshPreview();
        ApplyMapVisibility();
        NotifyMaterialsChanged();     // the DX11 scene is built from the surviving pieces
    }

    [RelayCommand]
    private async Task SaveMapContentEdits()
    {
        if (HasPendingMapGeoWork) await SaveMeshMoves();
        if (HasParticleMoves) await SaveParticleMoves();
    }

    /// <summary>
    /// M572: is there anything for <see cref="SaveMeshMoves"/> to do?
    ///
    /// <para>This exists because there were TWO of these conditions and they disagreed. The gate that
    /// decides whether to call the save listed moves, deletions and added meshes; the guard inside it also
    /// knew about face edits. So face work passed the inner check it never reached, and saving quietly did
    /// nothing at all - which is precisely how it was reported.</para>
    ///
    /// <para>One property, used by the button and by auto-save, so the two cannot drift again.</para>
    /// </summary>
    public bool HasPendingMapGeoWork =>
        HasMapMoves
        || _faceEdits.Count > 0
        || _faceGrows.Count > 0
        || _blenderReshapes.Count > 0
        || MapContent.AllMapPieces.Any(p => p.IsRemoved)
        || MapContent.AddedMeshes.Count > 0;

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
        if (!_outlinerMultiSelecting && value is MapOutlinerItemViewModel leaf)
            SetMapContentSelection(new[] { leaf }, leaf);
        switch (value)
        {
            case MapPieceViewModel { MeshIndex: >= 0 } p when _currentMap is { } map
                && map.Meshes.FirstOrDefault(x => x.Index == p.MeshIndex) is { } m:
                if (!_outlinerMultiSelecting) _selection.SetSingle(m);
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
            case PointLightViewModel lt:   // M153: lights are scene objects now
                _selection.Clear();
                if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
                if (SelectedParticleNode is not null) SelectedParticleNode = null;
                if (SelectedPropTreeItem is not null) SelectedPropTreeItem = null;
                if (SelectedSound is not null) SelectedSound = null;
                SelectedLight = lt;
                break;
            case MapSoundViewModel snd:   // M55
                _selection.Clear();
                if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
                if (SelectedParticleNode is not null) SelectedParticleNode = null;   // M76: viewport picks bypass the tree item
                if (SelectedPropTreeItem is not null) SelectedPropTreeItem = null;
                if (SelectedProbe is not null) SelectedProbe = null;
                SelectedParticleMarker = snd.Position;   // M55b: highlight only — camera stays
                SelectedSound = snd;                      // M56: enables the SOUND card (Play button)
                SelectedPlaceableInfo = "";
                GizmoPivot = snd.Position;                // M75: sounds are gizmo-movable
                break;
            case AddedMapMeshViewModel am:   // M79: imported mesh queued for append — gizmo-movable
                _selection.Clear();
                if (SelectedParticleNode is not null) SelectedParticleNode = null;
                if (SelectedSound is not null) SelectedSound = null;
                SelectedAddedMesh = am;
                GizmoPivot = am.PivotWorld;
                SelectedPlaceableInfo = $"{am.Name}\n{am.Info}";
                OnPropertyChanged(nameof(MapMaterialNames));
                break;
            case BucketGridViewModel bg:  // M55/M77b: info only — the toolbar toggle controls visibility
                SelectedPlaceableInfo = $"{bg.Name}\n{bg.Info}";
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
    /// <summary>M55: click-select ANY scene object — meshes (triangles) or placeable icon markers
    /// (particles/props/probes, ray-vs-sphere at the marker's world size); nearest hit wins.</summary>
    /// <summary>M382: per-click picking trace, toggled by the PICK button on the viewport toolbar.
    ///
    /// Selection is a CPU raycast against matrices cached by whichever renderer drew last, and a wrong
    /// pick has three quite different causes that look identical on screen: the ray is offset (click
    /// pixel vs cached bounds disagree), the ray is right but the hit maps to the wrong group, or a
    /// placeable marker beat the mesh and swallowed the click. This logs enough to tell them apart in
    /// one click each. Seeded from REYENGINE_PICK_DIAG so it can be enabled before the window exists.</summary>
    [ObservableProperty]
    private bool _pickDiagnostics = Environment.GetEnvironmentVariable("REYENGINE_PICK_DIAG") == "1";

    /// <summary>The view half of the trace: the click pixel and the bounds the matrices were cached
    /// with. These live in the control, so the view calls this before handing the ray over — if they
    /// disagree, every ray is offset and nothing downstream can be right.</summary>
    public void LogPickClick(double clickX, double clickY, double boundsW, double boundsH)
    {
        if (!PickDiagnostics) return;
        _log.Info("Pick", $"click ({clickX:0.#},{clickY:0.#}) px in bounds {boundsW:0.#}x{boundsH:0.#} "
                          + $"renderer={(UseDx11Viewport ? "D3D11" : "GL")}");
    }

    private void LogPickRay(System.Numerics.Vector3 o, System.Numerics.Vector3 d, Rendering.MeshRayHit? hit)
    {
        _log.Info("Pick", $"  ray o=({o.X:0.#},{o.Y:0.#},{o.Z:0.#}) d=({d.X:0.###},{d.Y:0.###},{d.Z:0.###})");
        if (_currentMap is not { } map) { _log.Info("Pick", "  no map open"); return; }
        if (hit is not { } h)
        {
            _log.Info("Pick", $"  NO HIT over {map.Groups.Count} groups "
                              + $"(visible={CurrentModelSubmeshVisible?.Count(v => v).ToString() ?? "all"})");
            return;
        }
        int meshIndex = h.Submesh >= 0 && h.Submesh < map.Groups.Count ? map.Groups[h.Submesh].MeshIndex : -1;
        var mesh = map.Meshes.FirstOrDefault(x => x.Index == meshIndex);
        // Group count for the mesh: several groups sharing one MeshIndex is why clicking one visible
        // piece can correctly outline others - that is not a picking error, and this line shows it.
        int groupsOnMesh = map.Groups.Count(g => g.MeshIndex == meshIndex);
        _log.Info("Pick", $"  hit group={h.Submesh} tri={h.Triangle} dist={h.Distance:0.#} "
                          + $"at ({h.Position.X:0.#},{h.Position.Y:0.#},{h.Position.Z:0.#}) -> "
                          + $"mesh={meshIndex} '{(mesh?.Name is { Length: > 0 } n ? n : "?")}' "
                          + $"({groupsOnMesh} group(s) share this mesh)");
    }

    public void SelectAnyFromViewport(System.Numerics.Vector3 rayOrigin, System.Numerics.Vector3 rayDir, bool additive = false,
        Func<System.Numerics.Vector3, System.Numerics.Vector2?>? projectToScreen = null,
        System.Numerics.Vector2? clickScreenPx = null,
        bool doubleClick = false)
    {
        // M76 UE-style picking: placeable icons hit in SCREEN space first (within a pixel radius of the
        // drawn icon), so they're easy to click at ANY zoom — a distant marker no longer needs a
        // pixel-perfect ray. Nearest icon on screen wins; icons draw on top, so they beat mesh faces.
        if (!additive && projectToScreen is not null && clickScreenPx is { } px)
        {
            const float PickPixels = 18f;
            object? bestPx = null;
            float bestPxD = float.MaxValue;
            void TestPx(object node, System.Numerics.Vector3 pos)
            {
                if (projectToScreen(pos) is not { } s) return;
                float d = System.Numerics.Vector2.Distance(s, px);
                if (d <= PickPixels && d < bestPxD) { bestPxD = d; bestPx = node; }
            }
            if (ShowParticles && MapContent.HasParticles)
                foreach (var p in MapContent.AllParticles.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved
                    && IsParticleVisible(v.Placement, v.EffectiveVisibilityFlags))) TestPx(p, p.CurrentPosition);
            if (ShowPlaceables && MapContent.HasProps)
                foreach (var p in MapContent.AllProps.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved)) TestPx(p, p.Position);
            if (ShowPlaceables && MapContent.HasProbes)
                foreach (var p in MapContent.Probes.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved)) TestPx(p, p.Position);
            if (ShowPlaceables && MapContent.HasSounds)
                foreach (var s in MapContent.Sounds.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved
                    && IsSoundVisible(v.Sound, v.EffectiveVisibilityFlags))) TestPx(s, s.Position);
            // M123e: staged (not yet saved) meshes are click-selectable at their world center
            foreach (var a in MapContent.AddedMeshes.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved)) TestPx(a, a.LocalCenter + a.Offset);
            if (bestPx is not null) { SelectedOutlinerItem = bestPx; return; }
        }

        rayDir = System.Numerics.Vector3.Normalize(rayDir);   // same t units for mesh + marker tests
        // mesh hit distance (float.MaxValue when none)
        float meshT = float.MaxValue;
        Rendering.MeshRayHit? diagHit = null;   // M382: kept whole for the diagnostic, not just its distance
        if (_currentMap is { } map0 && map0.Groups.Count > 0
            && RayIndex?.ClosestHit(rayOrigin, rayDir, CurrentModelSubmeshVisible) is { } meshHit)
        { meshT = meshHit.Distance; diagHit = meshHit; }

        if (PickDiagnostics) LogPickRay(rayOrigin, rayDir, diagHit);

        // placeable markers: same size formula the viewport uses for the icons (Mesh.Radius-scaled)
        float radius = CurrentMesh is { } cm ? Math.Clamp(cm.Radius * 0.004f, 4f, 90f) * 1.6f : 40f;
        object? bestNode = null;
        float bestT = float.MaxValue;
        void Test(object node, System.Numerics.Vector3 pos)
        {
            var toC = pos - rayOrigin;
            float t = System.Numerics.Vector3.Dot(toC, rayDir);            // rayDir is normalized
            if (t <= 0f || t >= bestT) return;
            float d = (toC - rayDir * t).Length();                          // perpendicular distance
            if (d <= radius) { bestT = t; bestNode = node; }
        }
        // M383: these gates are the SAME properties the marker builders use, so nothing invisible can
        // take a click. See CanPickProps/CanPickSounds for what had drifted.
        if (CanPickParticles && !additive)
            foreach (var p in MapContent.AllParticles.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved
                && IsParticleVisible(v.Placement, v.EffectiveVisibilityFlags))) Test(p, p.CurrentPosition);
        if (CanPickProps && !additive)
            foreach (var p in MapContent.AllProps.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved)) Test(p, p.Position);
        if (CanPickProbes && !additive)
            foreach (var p in MapContent.Probes.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved)) Test(p, p.Position);
        if (CanPickSounds && !additive)
            foreach (var s in MapContent.Sounds.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved
                && IsSoundVisible(v.Sound, v.EffectiveVisibilityFlags))) Test(s, s.Position);   // M55/M60
        // M153: point lights pick like any other placement, so you can click one in the viewport.
        if (CanPickLights && !additive)
            foreach (var l in MapContent.Lights) Test(l, l.Position);

        // nearest placeable beats a farther mesh face (icons draw on top, so this matches what you see)
        if (PickDiagnostics)
            _log.Info("Pick", $"  gates: particles={CanPickParticles} props={CanPickProps} "
                              + $"probes={CanPickProbes} sounds={CanPickSounds} lights={CanPickLights}");
        if (PickDiagnostics)
            _log.Info("Pick", $"  placeables: radius={radius:0.#} best={(bestNode?.GetType().Name ?? "none")} "
                              + $"bestT={(bestT >= float.MaxValue ? "-" : bestT.ToString("0.#"))} "
                              + $"meshT={(meshT >= float.MaxValue ? "-" : meshT.ToString("0.#"))} -> "
                              + (bestNode is not null && bestT < meshT ? "PLACEABLE WINS (no mesh selected)" : "mesh"));
        if (bestNode is not null && bestT < meshT)
        {
            SelectedOutlinerItem = bestNode;   // routes by type + highlights the hierarchy
            return;
        }
        // M500: pass the click pixel through so repeated clicks at the same spot can cycle deeper.
        // M566: in face mode a click picks a TRIANGLE of the map, not a mesh. Routed here rather than in
        // the window so both viewports and any future input path get it from one place.
        if (FaceEditMode)
        {
            // M568: a double click takes the whole connected piece. A quad is two triangles, so a single
            // click on a flat surface selects half of it.
            if (doubleClick) SelectLinkedFacesFromViewport(rayOrigin, rayDir, additive);
            else SelectFaceFromViewport(rayOrigin, rayDir, additive);
            return;
        }
        SelectMeshFromViewport(rayOrigin, rayDir, additive, clickScreenPx);
    }

    // M500: Blender-style click-through. Clicking the same spot again steps to the next mesh along the
    // ray instead of re-selecting the nearest one forever.
    private System.Numerics.Vector2? _lastPickPx;
    private int _pickCycleCursor;

    /// <summary>How far the pointer may move and still count as "the same spot" for cycling, in DIP.</summary>
    private const float PickCycleSlopPx = 4f;

    /// <summary>
    /// M500: select the mesh under the ray, cycling through everything beneath it on repeated clicks.
    ///
    /// <para>Previously this took <c>ClosestHit</c>, which prunes the BVH with its running best distance —
    /// so anything behind the nearest surface was never even tested, and coincident geometry was arbitrated
    /// by a fixed "lowest submesh index wins" tie-break. On Summoner's Rift that is not an edge case: decals
    /// lie exactly on the terrain they decorate and the dragon pit floor exists seven times in one spot, so
    /// the losing copies were simply unreachable by clicking.</para>
    ///
    /// <para>Now the full hit list is collected and repeated clicks at the same pixel advance a cursor
    /// through it, wrapping at the end. The list is RECOMPUTED every click and only the cursor persists:
    /// that keeps the cycle correct across camera moves, visibility toggles and geometry edits with no
    /// invalidation logic to get wrong. Hits are collapsed to distinct meshes first, because one mesh can
    /// contribute several submeshes along the ray and the user is picking meshes, not triangles.</para>
    /// </summary>
    public void SelectMeshFromViewport(System.Numerics.Vector3 rayOrigin, System.Numerics.Vector3 rayDir,
        bool additive = false, System.Numerics.Vector2? clickScreenPx = null)
    {
        if (_currentMap is not { } map || map.Groups.Count == 0) return;

        var hits = RayIndex?.AllHits(rayOrigin, rayDir, CurrentModelSubmeshVisible);

        // Collapse to distinct meshes, nearest first. A mesh with several submeshes under the cursor must
        // occupy ONE step of the cycle, or clicking would appear to stall on it.
        var ordered = new List<int>();
        if (hits is not null)
            foreach (var h in hits)
            {
                if (h.Submesh < 0 || h.Submesh >= map.Groups.Count) continue;
                int mi = map.Groups[h.Submesh].MeshIndex;
                if (mi >= 0 && !ordered.Contains(mi)) ordered.Add(mi);
            }

        if (ordered.Count == 0)
        {
            _lastPickPx = null;
            _pickCycleCursor = 0;
            if (!additive)
            {
                _selection.Clear(); // empty click clears; Ctrl+empty keeps the set (UE/Blender)
                ClearPlaceableSelection(); // M76: an empty click also deselects particles/sounds/props/probes
            }
            return;
        }

        // Same spot as last time? Step deeper. Additive clicks never cycle — Ctrl+click is "add this one",
        // and advancing under the user there would add a different mesh than the one they aimed at.
        bool sameSpot = !additive && clickScreenPx is { } px && _lastPickPx is { } last
                        && System.Numerics.Vector2.Distance(px, last) <= PickCycleSlopPx;
        _pickCycleCursor = sameSpot ? (_pickCycleCursor + 1) % ordered.Count : 0;
        _lastPickPx = additive ? null : clickScreenPx;

        int meshIndex = ordered[_pickCycleCursor];
        if (ordered.Count > 1)
            _log.Info("MapGeo", $"Click-through {_pickCycleCursor + 1}/{ordered.Count} under the cursor "
                              + "(click again for the next one down).");
        var mesh = map.Meshes.FirstOrDefault(x => x.Index == meshIndex);
        if (mesh is null) return;
        if (additive) _selection.Toggle(mesh);
        else _selection.SetSingle(mesh);
        SetMapContentSelection(MapContent.AllMapPieces
            .Where(p => _selection.Items.Any(selected => selected.Index == p.MeshIndex)).ToList(),
            MapContent.AllMapPieces.FirstOrDefault(p => p.MeshIndex == _selection.Primary?.Index));
        var name = mesh.Name?.Length > 0 ? mesh.Name : $"#{meshIndex}";
        _log.Info("MapGeo", additive ? $"{(_selection.Contains(mesh) ? "Added" : "Removed")} '{name}' ({_selection.Count} selected)."
                                      : $"Selected '{name}' (viewport click).");
    }

    /// <summary>Ctrl+click on a Map Content tree row: toggle that mesh in/out of the selection.</summary>
    public void ToggleMeshSelectionFromTree(MapPieceViewModel piece)
    {
        if (_currentMap is not { } map || piece.MeshIndex < 0) return;
        if (map.Meshes.FirstOrDefault(x => x.Index == piece.MeshIndex) is { } m)
        {
            _selection.Toggle(m);
            SetMapContentSelection(MapContent.AllMapPieces
                .Where(p => _selection.Items.Any(selected => selected.Index == p.MeshIndex)).ToList(), piece);
        }
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
        OnPropertyChanged(nameof(CanAddTexcoord7));          // M432
        OnPropertyChanged(nameof(AddTexcoord7Label));
        OnPropertyChanged(nameof(CanAddTangents));           // M433
        OnPropertyChanged(nameof(AddTangentsLabel));
    }

    /// <summary>M76: deselect every placeable (particle/sound/prop/probe) — used when the user clicks
    /// empty space, so no stale placement keeps its gizmo/inspector alive (UE-style).</summary>
    private void ClearPlaceableSelection()
    {
        if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
        if (SelectedParticleNode is not null) SelectedParticleNode = null;
        if (SelectedPropTreeItem is not null) SelectedPropTreeItem = null;
        if (SelectedProbe is not null) SelectedProbe = null;
        if (SelectedSound is not null) SelectedSound = null;
        if (SelectedAddedMesh is not null) SelectedAddedMesh = null;   // M79
        if (SelectedLight is not null) SelectedLight = null;           // M153
        SelectedParticleMarker = null;
        SelectedPlaceableInfo = "";
        if (_selection.IsEmpty) GizmoPivot = null;
        _syncingTreeSelection = true;
        SelectedOutlinerItem = null;   // drop the outliner row highlight too
        _syncingTreeSelection = false;
        SetMapContentSelection(Array.Empty<MapOutlinerItemViewModel>(), null);
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
        if (!_outlinerMultiSelecting && _mapContentSelection.All(item => item is MapPieceViewModel))
            SetMapContentSelection(MapContent.AllMapPieces.Where(p => p.IsSelected).ToList(), primaryPiece);
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
        // M76: also clear the DIRECTLY-set particle node (viewport icon picks bypass the tree item, so
        // clearing only SelectedParticleTreeItem left the particle selected under a new mesh selection).
        if (SelectedParticleNode is not null) SelectedParticleNode = null;
        if (SelectedAddedMesh is not null) SelectedAddedMesh = null;   // M79
        SelectedParticleMarker = null;
        if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
        if (SelectedPropTreeItem is not null) SelectedPropTreeItem = null;
        if (SelectedProbe is not null) SelectedProbe = null;
        if (SelectedSound is not null) SelectedSound = null;   // M56
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
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
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
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
    };

    /// <summary>UI sync run after a BATCH transform command executes OR undoes: re-upload vertices, refresh
    /// the primary's fields, recompute all selection visuals, and update the dirty flag.</summary>
    private Action MakeBatchRefresh(MapGeoAsset map) => () =>
    {
        MeshVerticesRevision++;
        if (SelectedMapMesh is { } primary) RefreshMeshTransformFields(primary);
        RefreshSelectionVisuals();
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
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
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
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
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
        _log.Info("MapGeo", $"Transformed '{m.Name}': pos ({target.X:0.#}, {target.Y:0.#}, {target.Z:0.#}), " +
                            $"rot ({rotation.X:0.#}°, {rotation.Y:0.#}°, {rotation.Z:0.#}°), scale ({scale.X:0.##}, {scale.Y:0.##}, {scale.Z:0.##}).");
    }

    // ---- M472: fine-tune the legacy (NVR/WGEO) import offset ----------------------------------------

    /// <summary>M472: the nudge to apply to every imported legacy mesh, in world units. A DELTA, not a
    /// position — the whole point is to adjust an alignment that is already mostly right.</summary>
    [ObservableProperty] private string _legacyNudgeX = "0";
    [ObservableProperty] private string _legacyNudgeY = "0";
    [ObservableProperty] private string _legacyNudgeZ = "0";

    /// <summary>How many meshes in the open map came from a WGEO/NVR port, by material prefix.</summary>
    public int LegacyImportedMeshCount =>
        _currentMap is { } m ? Formats.MapGeo.LegacyMapPorter.ImportedMeshes(m).Count : 0;

    public bool HasLegacyImport => LegacyImportedMeshCount > 0;

    /// <summary>What the porter baked in, shown so the user can see the number they are adjusting away
    /// from. It is a constant measured in two passes and stored nowhere in the result, so this is the only
    /// place the value is visible at all.</summary>
    public string LegacyPortCorrectionText =>
        $"port baked in ({Formats.MapGeo.LegacyMapPorter.LegacyPositionCorrection.X:0.###}, "
        + $"{Formats.MapGeo.LegacyMapPorter.LegacyPositionCorrection.Y:0.###}, "
        + $"{Formats.MapGeo.LegacyMapPorter.LegacyPositionCorrection.Z:0.###})";

    /// <summary>
    /// M472: move every imported legacy mesh by a delta, as one undoable edit.
    ///
    /// <para>The per-mesh TRANSFORM box could already do this, but only one mesh at a time and only in
    /// ABSOLUTE world coordinates — so fine-tuning a port meant reading a number like 1030.787, adding the
    /// nudge by hand, and repeating it for every imported mesh. The port correction itself is a constant
    /// baked into the geometry at import time and recorded nowhere, so the alternative was re-porting the
    /// whole map to change one number.</para>
    ///
    /// <para>Additive on purpose: <c>TranslateMesh</c> ASSIGNS <c>Offset</c> rather than accumulating, so
    /// this passes <c>Offset + delta</c>. Nudging twice by 10 moves 20, which is what "fine-tune" means;
    /// passing the raw delta would instead have made the second nudge undo the first.</para>
    /// </summary>
    [RelayCommand]
    private void NudgeLegacyImport()
    {
        if (_currentMap is not { } map) { _log.Warn("MapGeo", "Open a map first."); return; }
        if (!TryParseVector3(LegacyNudgeX, LegacyNudgeY, LegacyNudgeZ, out var delta))
        { _log.Warn("MapGeo", "Enter valid nudge X/Y/Z numbers."); return; }
        if (delta == System.Numerics.Vector3.Zero) { _log.Info("MapGeo", "Nudge is zero — nothing to do."); return; }

        var meshes = Formats.MapGeo.LegacyMapPorter.ImportedMeshes(map);
        if (meshes.Count == 0)
        { _log.Warn("MapGeo", "No imported legacy geometry in this map (no LegacyPort/ materials)."); return; }

        var entries = new List<(MapGeoMesh, MeshTransformCommand.State, MeshTransformCommand.State)>(meshes.Count);
        foreach (var mesh in meshes)
        {
            var before = MeshTransformCommand.State.Capture(mesh);
            map.TranslateMesh(mesh, mesh.Offset + delta);
            entries.Add((mesh, before, MeshTransformCommand.State.Capture(mesh)));
        }

        var cmd = new BatchTransformCommand("Nudge Legacy Import", map, entries, MakeBatchRefresh(map));
        if (cmd.HasChange) UndoService.PushApplied(cmd);

        MeshVerticesRevision++;
        RefreshSelectionVisuals();
        if (SelectedMapMesh is { } sel) RefreshMeshTransformFields(sel);
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
        _log.Success("MapGeo", $"Nudged {meshes.Count} imported legacy mesh(es) by "
                             + $"({delta.X:0.###}, {delta.Y:0.###}, {delta.Z:0.###}). "
                             + "Undo restores them; Save to Mod writes it.");
    }

    /// <summary>M738: how far the decal lift raises the ported overlay, in world units.</summary>
    [ObservableProperty] private string _decalLift = "1";

    /// <summary>
    /// M738: raise the ported decal overlay off the terrain, as one undoable edit.
    ///
    /// <para>The porter welds decals flat onto the ground: measured on the Harrowing port, 15,843 of
    /// 15,845 decal vertices sit within 0.001 of the surface beneath them. Riot's own decals on the same
    /// map sit a median 6.37 units clear and are coplanar in only 9.3% of cases. A coplanar pair shares
    /// one depth plane, and so does anything else drawn at ground level - which is why a champion's ground
    /// projection and a turret shot's projected sprite look wrong over this map's decals and nowhere
    /// else.</para>
    ///
    /// <para>Only the PORTED decals move (<c>LegacyPort/.../Decal_*</c>). The destination map's own decals
    /// are placed correctly and are left alone. Additive, like the nudge above it: lifting twice by 1
    /// lifts 2.</para>
    /// </summary>
    [RelayCommand]
    private void LiftLegacyDecals()
    {
        if (_currentMap is not { } map) { _log.Warn("MapGeo", "Open a map first."); return; }
        if (!float.TryParse(DecalLift, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float lift) || lift == 0f)
        { _log.Warn("MapGeo", "Enter a non-zero decal lift."); return; }

        var meshes = Formats.MapGeo.LegacyMapPorter.ImportedDecalMeshes(map);
        if (meshes.Count == 0)
        { _log.Warn("MapGeo", "No ported decal overlay in this map (no LegacyPort/.../Decal_ materials)."); return; }

        var entries = new List<(MapGeoMesh, MeshTransformCommand.State, MeshTransformCommand.State)>(meshes.Count);
        foreach (var mesh in meshes)
        {
            var before = MeshTransformCommand.State.Capture(mesh);
            map.TranslateMesh(mesh, mesh.Offset + new System.Numerics.Vector3(0f, lift, 0f));
            entries.Add((mesh, before, MeshTransformCommand.State.Capture(mesh)));
        }

        var cmd = new BatchTransformCommand("Lift Legacy Decals", map, entries, MakeBatchRefresh(map));
        if (cmd.HasChange) UndoService.PushApplied(cmd);

        MeshVerticesRevision++;
        RefreshSelectionVisuals();
        if (SelectedMapMesh is { } sel) RefreshMeshTransformFields(sel);
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
        _log.Success("MapGeo", $"Lifted {meshes.Count} ported decal mesh(es) by {lift:0.###} unit(s). "
                             + "Undo restores them; Save to Mod writes it.");
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
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
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

    /// <summary>M432: can the selected meshes be given a Texcoord7 channel? False once they all have one.</summary>
    public bool CanAddTexcoord7 => _currentMap is not null && _currentMapEntry is not null && _currentMapBytes is not null
                                   && _selection.Items.Any(m => !m.HasLightmapUv);

    /// <summary>M432: how many of the selected meshes still lack the channel — drives the button label.</summary>
    public string AddTexcoord7Label => _selection.Count == 0
        ? "Add Texcoord7"
        : $"Add Texcoord7 ({_selection.Items.Count(m => !m.HasLightmapUv)})";

    /// <summary>
    /// M432: give the SELECTED meshes the baked UV set (Texcoord7) without baking a lightmap.
    ///
    /// <para>This is not a shortcut around the light baker — it serves a different need. A shader can want
    /// Texcoord7 as a vertex contract rather than to sample an atlas: <c>Mantis_Env_Baked_PBR</c> declares
    /// FEATURE_BAKED_PAINT and reads that UV set for BAKED_* samplers supplied by the MATERIAL. Measured:
    /// all 18 shipped meshes on the only other FEATURE_BAKED_PAINT shader carry Texcoord7, and 0 of the
    /// 586 in map11's base_srx do. Use "Generate Lightmap Layout" when you want real lightmap UVs.</para>
    ///
    /// <para>The UVs are COPIED FROM TEXCOORD0, which is stated rather than derived: valid, in range, and
    /// coherent with the diffuse. Charts overlap between meshes, so no atlas reference is written.</para>
    /// </summary>
    [RelayCommand]
    private async Task AddTexcoord7()
    {
        if (_currentMap is not { } map || _currentMapBytes is null || _currentMapEntry is not { } entry)
        { _log.Warn("MapGeo", "No map is open, so no mesh can be given a Texcoord7 channel."); return; }

        var targets = _selection.Items.Where(m => !m.HasLightmapUv).Select(m => m.Index).ToList();
        if (targets.Count == 0)
        {
            _log.Warn("MapGeo", _selection.Count == 0
                ? "Select a mesh first — this adds the channel to the selection."
                : "Every selected mesh already has a Texcoord7 channel.");
            return;
        }

        // Rewriting from _currentMapBytes would drop anything not yet saved, so say so instead.
        if (MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes) || MapContent.AddedMeshes.Count > 0)
        { _log.Warn("MapGeo", "Save your pending mesh edits first — this rewrites the mapgeo from the saved bytes."); return; }
        if (!GuardEditable(entry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            var source = _currentMapBytes;
            var extendedChannels = ExtendedChannelMaterialsFor(entry.Path);
            var (bytes, result) = await Task.Run(() =>
            {
                byte[] b = Formats.MapGeo.MeshUvChannelBuilder.AddTexcoord7(source, targets, out var r,
                    extendedChannels);
                return (b, r);
            });
            if (result.MeshesChanged == 0) { _log.Warn("MapGeo", result.Summary); return; }

            // Validate BEFORE saving: the rewrite has to decode again AND actually carry the channel.
            var check = await Task.Run(() => MapGeoDecoder.Decode(bytes, extendedChannels));
            var wanted = targets.ToHashSet();
            int carried = check.Meshes.Count(m => wanted.Contains(m.Index) && m.HasLightmapUv);
            if (carried != result.MeshesChanged)
            {
                _log.Error("MapGeo", $"The rewritten mapgeo decoded but only {carried} of {result.MeshesChanged} " +
                                     "mesh(es) carry the new channel — not saved.");
                return;
            }

            // M417: the project FILE wins when the project ships this mapgeo, or the build drops the edit.
            string savedTo;
            if (TryWriteToProjectFile(entry, bytes, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, ".mapgeo");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = entry.PathHash,
                    ResolvedPath = entry.IsResolved ? entry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(entry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            foreach (var s in result.Skipped.Take(5)) _log.Warn("MapGeo", s);
            _log.Success("MapGeo", $"{result.Summary} Copied from Texcoord0 — no lightmap texture was assigned. " +
                                   $"Saved to {savedTo} ({bytes.Length:n0} bytes).");
            await LoadMapGeoAsync(entry);
        }
        catch (Exception ex) { _log.Error("MapGeo", "Could not add the Texcoord7 channel: " + ex.Message); }
    }

    /// <summary>
    /// M444: bring the mapgeo's per-mesh data into agreement with the materials assigned to it — the
    /// editor equivalent of the byte patching that got Map11 loading.
    ///
    /// <para>Two things get fixed, both of which are invisible in the mapgeo itself and fatal when
    /// wrong:</para>
    /// <list type="number">
    ///   <item><b>The channel block.</b> A mesh whose material selects the 48-byte reader needs the extra
    ///   entry; one that no longer uses such a material must lose it. Either mismatch desyncs the client's
    ///   mesh parse and crashes map load with no diagnostic.</item>
    ///   <item><b>The baked-paint override.</b> A mesh with no override for the baked diffuse sampler makes
    ///   the client report <c>Missing shader constant "BAKED_PAINT_UV_SCALE_BIAS"</c> — the UV transform
    ///   has no sampler to attach to.</item>
    /// </list>
    ///
    /// <para>The override texture is taken from the material's OWN sampler of the same name, so mesh and
    /// material always agree. The transform is the identity: Riot's shipped values are sub-1 because their
    /// baked textures are atlas sub-rects, which does not apply to a standalone texture.</para>
    /// </summary>
    [RelayCommand]
    private async Task SyncMeshChannelsToMaterials()
    {
        if (_currentMap is not { } map || _currentMapBytes is null || _currentMapEntry is not { } entry)
        { _log.Warn("MapGeo", "No map is open, so mesh channels cannot be synced."); return; }

        if (MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes) || MapContent.AddedMeshes.Count > 0)
        { _log.Warn("MapGeo", "Save your pending mesh edits first — this rewrites the mapgeo from the saved bytes."); return; }

        var extended = ExtendedChannelMaterialsFor(entry.Path);
        if (extended.Count == 0)
        {
            _log.Info("MapGeo", "No material in this map uses the extended mesh channel block, so there is "
                              + "nothing to sync. (Only the Mantis shader family selects it.)");
            return;
        }
        if (!GuardEditable(entry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        // sampler name -> texture, per extended material, straight off the material itself
        var samplers = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        try
        {
            if (TryResolveMaterialsBin(entry.Path, out var binEntry))
            {
                var doc = Formats.Materials.MaterialDocument.Parse(ReadAsset(binEntry.PathHash), ResolveBinName, ResolveWadPath);
                foreach (var mat in doc.Materials.Where(m => extended.Contains(m.Name)))
                    samplers[mat.Name] = mat.Slots
                        .Where(s => !string.IsNullOrWhiteSpace(s.SamplerName) && !string.IsNullOrWhiteSpace(s.OriginalPath))
                        .GroupBy(s => s.SamplerName, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First().OriginalPath, StringComparer.Ordinal);
            }
        }
        catch (Exception ex) { _log.Warn("MapGeo", "Material samplers could not be read: " + ex.Message); }

        const string BakedDiffuse = "BAKED_DIFFUSE_TEXTURE";
        try
        {
            var source = _currentMapBytes;
            var (bytes, channelsChanged, overridesAdded, unresolved) = await Task.Run(() =>
            {
                if (!Formats.MapGeo.MapGeoBinary.TryReadEditable(source, out var edit, extended))
                    return ((byte[]?)null, 0, 0, (List<string>?)null);

                int channels = Formats.MapGeo.ExtendedChannelRule.Apply(edit, extended);
                int added = 0;
                var missing = new List<string>();
                foreach (var mesh in edit.Meshes)
                {
                    string? material = mesh.Submeshes.Select(s => s.Material).FirstOrDefault(extended.Contains);
                    if (material is null) continue;

                    int slot = edit.ShaderOverrideIndex(BakedDiffuse);
                    if (slot >= 0 && mesh.TextureOverrides.Any(o => o.Index == slot)) continue;
                    if (!samplers.TryGetValue(material, out var bySampler)
                        || !bySampler.TryGetValue(BakedDiffuse, out var texture))
                    { if (!missing.Contains(material)) missing.Add(material); continue; }

                    edit.SetTextureOverride(mesh, BakedDiffuse, texture,
                        System.Numerics.Vector2.One, System.Numerics.Vector2.Zero);
                    added++;
                }
                return (edit.Write(), channels, added, missing);
            });

            if (bytes is null)
            { _log.Error("MapGeo", "This mapgeo does not round-trip byte-exactly, so it was not rewritten."); return; }
            foreach (var m in unresolved ?? new List<string>())
                _log.Warn("MapGeo", $"{m} declares no {BakedDiffuse} sampler, so its meshes got no override.");
            if (channelsChanged == 0 && overridesAdded == 0)
            { _log.Info("MapGeo", "Already in sync — no mesh needed a channel entry or an override."); return; }

            // Validate BEFORE saving: it must re-read with the same material set AND still decode.
            if (!await Task.Run(() => Formats.MapGeo.MapGeoBinary.TryReadEditable(bytes, out _, extended)))
            { _log.Error("MapGeo", "The rewritten mapgeo did not re-read cleanly — not saved."); return; }
            await Task.Run(() => MapGeoDecoder.Decode(bytes, extended));

            string savedTo;
            if (TryWriteToProjectFile(entry, bytes, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, ".mapgeo");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = entry.PathHash,
                    ResolvedPath = entry.IsResolved ? entry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(entry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            _log.Success("MapGeo", $"Synced {channelsChanged:n0} channel block(s) and added {overridesAdded:n0} "
                                 + $"baked-paint override(s). Saved to {savedTo} ({bytes.Length:n0} bytes).");
            await LoadMapGeoAsync(entry);
        }
        catch (Exception ex) { _log.Error("MapGeo", "Mesh channels could not be synced: " + ex.Message); }
    }

    /// <summary>M433: can the selected meshes be given tangents? The decoder does not surface Texcoord6,
    /// so unlike <see cref="CanAddTexcoord7"/> this cannot pre-filter meshes that already have it — the
    /// builder skips those and reports them.</summary>
    public bool CanAddTangents => _currentMap is not null && _currentMapEntry is not null
                                  && _currentMapBytes is not null && _selection.Count > 0;

    public string AddTangentsLabel => _selection.Count == 0 ? "Add Tangents" : $"Add Tangents ({_selection.Count})";

    /// <summary>
    /// M433: give the SELECTED meshes the tangent channel (Texcoord6) that some shaders require.
    ///
    /// <para>Measured from Riot's compiled bytecode: <c>Mantis_Env_Baked_PBR</c>'s vertex shader reads
    /// TEXCOORD6 as a fully-used float4, alongside POSITION0/NORMAL0/TEXCOORD0/TEXCOORD7. A vertex shader
    /// input with no matching vertex element is an input-layout failure at load. The element named
    /// <c>Tangent</c> is a red herring — it appears in 0 of 40,512 shipped meshes; the tangent frame
    /// travels as Texcoord6, which only 1 of those 40,512 carries.</para>
    ///
    /// <para>The tangents are DERIVED from positions + Texcoord0 + normals, not authored.</para>
    /// </summary>
    [RelayCommand]
    private async Task AddTangents()
    {
        if (_currentMap is not { } map || _currentMapBytes is null || _currentMapEntry is not { } entry)
        { _log.Warn("MapGeo", "No map is open, so no mesh can be given tangents."); return; }

        var targets = _selection.Items.Select(m => m.Index).ToList();
        if (targets.Count == 0) { _log.Warn("MapGeo", "Select a mesh first — this adds tangents to the selection."); return; }

        if (MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes) || MapContent.AddedMeshes.Count > 0)
        { _log.Warn("MapGeo", "Save your pending mesh edits first — this rewrites the mapgeo from the saved bytes."); return; }
        if (!GuardEditable(entry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            var source = _currentMapBytes;
            var extendedChannels = ExtendedChannelMaterialsFor(entry.Path);
            var (bytes, result) = await Task.Run(() =>
            {
                byte[] b = Formats.MapGeo.MeshTangentBuilder.AddTangents(source, targets, out var r,
                    extendedChannels);
                return (b, r);
            });
            if (result.MeshesChanged == 0) { _log.Warn("MapGeo", result.Summary); return; }

            // Validate BEFORE saving: it must decode again with the geometry intact.
            var check = await Task.Run(() => MapGeoDecoder.Decode(bytes, extendedChannels));
            if (check.Meshes.Count != map.Meshes.Count)
            {
                _log.Error("MapGeo", $"The rewritten mapgeo decoded with {check.Meshes.Count} meshes " +
                                     $"instead of {map.Meshes.Count} — not saved.");
                return;
            }

            string savedTo;
            if (TryWriteToProjectFile(entry, bytes, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, ".mapgeo");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = entry.PathHash,
                    ResolvedPath = entry.IsResolved ? entry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(entry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            foreach (var s in result.Skipped.Take(5)) _log.Warn("MapGeo", s);
            _log.Success("MapGeo", $"{result.Summary} Derived from Position + Texcoord0 + Normal. " +
                                   $"Saved to {savedTo} ({bytes.Length:n0} bytes).");
            await LoadMapGeoAsync(entry);
        }
        catch (Exception ex) { _log.Error("MapGeo", "Could not add tangents: " + ex.Message); }
    }

    /// <summary>M434: the MapGraphicsFeature components the open map declares, one row per known feature.</summary>
    public ObservableCollection<MapGraphicsFeatureViewModel> MapGraphicsFeatures { get; } = new();

    public bool HasMapGraphicsFeatures => MapGraphicsFeatures.Count > 0;

    /// <summary>M434: re-read which graphics features the open map's materials.bin declares.</summary>
    private void RefreshMapGraphicsFeatures()
    {
        MapGraphicsFeatures.Clear();
        try
        {
            if (_currentMapEntry is not null && TryResolveMaterialsBin(_currentMapEntry.Path, out var binEntry))
            {
                var bytes = ReadAsset(binEntry.PathHash);
                var present = Formats.MapGeo.MapGraphicsFeatures.Read(bytes);
                foreach (var f in Formats.MapGeo.MapGraphicsFeatures.All)
                {
                    var row = new MapGraphicsFeatureViewModel(f, present.Contains(f));
                    // M436: only a DECLARED component has fields to edit.
                    if (row.IsPresent)
                        foreach (var field in Formats.MapGeo.MapGraphicsFeatureSettings.Describe(bytes, f, Meta))
                            row.Fields.Add(new MapFeatureFieldViewModel(f, field));
                    MapGraphicsFeatures.Add(row);
                }
            }
        }
        catch (Exception ex) { _log.Warn("Map", "Could not read the map's graphics features: " + ex.Message); }
        OnPropertyChanged(nameof(HasMapGraphicsFeatures));
        RefreshGameplayTextureStatus();   // M439
        RefreshLightGridStatus();         // M442
    }

    /// <summary>
    /// M434: add or remove a <c>MapGraphicsFeature</c> in the map's <c>MapContainer.components[]</c>.
    ///
    /// <para>These decide which shared rendering resources the engine builds, and map shaders bind them by
    /// name — measured against Mantis_Env_Baked_PBR's pixel shader: MapLightingV2 ↔ IBL_CUBEMAP,
    /// MapTerrainPaint ↔ TERRAIN_BLEND, MapSSAO ↔ SSAO_TEXTURE, and so on. 179 of 206 shipped map bins
    /// declare MapLightingV2; the 27 that do not are the map11 family, i.e. the legacy lighting path.</para>
    ///
    /// <para>Added as a bare marker, which is the shipped form (map12/jade's MapLightingV2 has zero
    /// properties).</para>
    /// </summary>
    [RelayCommand]
    private async Task ToggleMapGraphicsFeature(MapGraphicsFeatureViewModel? row)
    {
        if (row is null) return;
        if (_currentMapEntry is not { } mapEntry || !TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
        { _log.Warn("Map", "No map materials.bin is open."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            var source = ReadAsset(binEntry.PathHash);
            byte[]? updated = row.IsPresent
                ? Formats.MapGeo.MapGraphicsFeatures.Remove(source, row.Feature, out var result)
                : Formats.MapGeo.MapGraphicsFeatures.Add(source, row.Feature, null, out result);

            if (updated is null) { _log.Warn("Map", result.Detail); return; }

            // M435: say what the corpus says the feature still needs. Advisory, not a block - the
            // creator may be about to bake.
            if (!row.IsPresent
                && Formats.MapGeo.MapGraphicsFeatures.PrerequisiteWarning(updated, row.Feature) is { } advice)
                _log.Warn("Map", advice);

            // Validate before saving: it must reparse and report exactly the change we asked for.
            var after = Formats.MapGeo.MapGraphicsFeatures.Read(updated);
            if (after.Contains(row.Feature) == row.IsPresent)
            { _log.Error("Map", $"The rewritten bin does not reflect the {row.Feature.Name} change — not saved."); return; }

            string savedTo;
            if (TryWriteToProjectFile(binEntry, updated, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, updated, ".bin");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = binEntry.PathHash,
                    ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            _log.Success("Map", $"{result.Detail}. Saved to {savedTo} ({updated.Length:n0} bytes).");
            RefreshMapGraphicsFeatures();
        }
        catch (Exception ex) { _log.Error("Map", $"Could not change {row.Feature.Name}: {ex.Message}"); }
    }

    /// <summary>
    /// M436: write one field of a MapGraphicsFeature component.
    ///
    /// <para>Field names, types and defaults come from the meta-class dump, so nothing is guessed. An
    /// empty box CLEARS the field rather than zeroing it — an absent field takes the engine's default,
    /// and shipped components rely on that (map12/jade's MapLightingV2 sets none of its fields).</para>
    /// </summary>
    [RelayCommand]
    private async Task SetMapGraphicsField(MapFeatureFieldViewModel? row)
    {
        if (row is null) return;
        if (!row.IsEditable)
        { _log.Warn("Map", $"{row.Name} is a {row.TypeName} field — it needs structured content this editor will not invent."); return; }
        if (_currentMapEntry is not { } mapEntry || !TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
        { _log.Warn("Map", "No map materials.bin is open."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            var updated = Formats.MapGeo.MapGraphicsFeatureSettings.SetField(
                ReadAsset(binEntry.PathHash), row.Owner, row.Field.Hash, row.TypeName, row.Value, out var result);
            if (updated is null) { _log.Warn("Map", result.Detail); return; }

            string savedTo;
            if (TryWriteToProjectFile(binEntry, updated, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, updated, ".bin");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = binEntry.PathHash,
                    ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            _log.Success("Map", $"{row.Owner.Name}.{row.Name}: {result.Detail}. Saved to {savedTo} ({updated.Length:n0} bytes).");
            RefreshMapGraphicsFeatures();
        }
        catch (Exception ex) { _log.Error("Map", $"Could not set {row.Owner.Name}.{row.Name}: {ex.Message}"); }
    }

    /// <summary>
    /// M438: fill a graphics feature from a map Riot actually ships with it — values, textures and
    /// nested structs included.
    ///
    /// <para>This copies rather than invents. MapSSAO is an embedded renderer with a nested settings
    /// struct and MapClouds is a 3-element layer container plus a texture path; neither can be built from
    /// a type tuple, which is why <see cref="Formats.Meta.MetaDefaultProperty"/> refuses them. A
    /// component lifted whole out of a shipped bin is Riot's own working configuration.</para>
    ///
    /// <para>Adds the feature if it is absent, and overwrites the copied fields if it is present.</para>
    /// </summary>
    [RelayCommand]
    private async Task FillMapGraphicsPreset(MapGraphicsFeatureViewModel? row)
    {
        if (row is null) return;
        if (row.PresetSource is not { } sourcePath)
        { _log.Warn("Map", $"{row.Name} appears in no shipped map, so there is no configuration to copy."); return; }
        if (_currentMapEntry is not { } mapEntry || !TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
        { _log.Warn("Map", "No map materials.bin is open."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            var sourceBin = ReadAssetByPath(sourcePath);
            if (sourceBin is null)
            { _log.Warn("Map", $"Could not read {sourcePath} — is the game folder set?"); return; }

            var preset = Formats.MapGeo.MapGraphicsFeaturePresets.Extract(sourceBin, row.Feature, ResolveBinName);
            if (preset is null || preset.Fields.Count == 0)
            { _log.Warn("Map", $"{sourcePath} declares no usable {row.Name} fields."); return; }

            var updated = Formats.MapGeo.MapGraphicsFeatures.Add(
                ReadAsset(binEntry.PathHash), row.Feature, preset.Fields, out var result);
            if (updated is null) { _log.Warn("Map", result.Detail); return; }

            string savedTo;
            if (TryWriteToProjectFile(binEntry, updated, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, updated, ".bin");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = binEntry.PathHash,
                    ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            if (preset.Dropped.Count > 0)
                _log.Warn("Map", $"{row.Name}: {preset.Dropped.Count} link field(s) were NOT copied — an "
                                 + "ObjectLink points into its own bin, so the value would reference nothing here: "
                                 + string.Join(", ", preset.Dropped));
            _log.Success("Map", $"{row.Name}: copied {preset.Summary} from {sourcePath}. "
                                + $"Saved to {savedTo} ({updated.Length:n0} bytes).");
            RefreshMapGraphicsFeatures();
        }
        catch (Exception ex) { _log.Error("Map", $"Could not fill {row.Name}: {ex.Message}"); }
    }

    // ---- M439: MapGameplayTexture authoring (samplers + channel links) ----

    [ObservableProperty] private string _gameplayTextureSamplerName = "Base";
    [ObservableProperty] private string _gameplayTexturePath = "";
    [ObservableProperty] private string _gameplayChannelName = "";
    [ObservableProperty] private string _gameplayChannelSlot = "Alpha";

    /// <summary>M439: is MapGameplayTexture declared, so its sub-structures can be authored?</summary>
    public bool HasGameplayTexture =>
        MapGraphicsFeatures.Any(r => ReferenceEquals(r.Feature, Formats.MapGeo.MapGraphicsFeatures.GameplayTexture)
                                     && r.IsPresent);

    /// <summary>M439: what the component still lacks — the four colour slots start as null links, which
    /// is the state that makes the client fail with Missing sampler "AlphaMask".</summary>
    [ObservableProperty] private string _gameplayTextureStatus = "";

    public IReadOnlyList<string> GameplayChannelSlots { get; } = new[] { "Red", "Green", "Blue", "Alpha" };

    private void RefreshGameplayTextureStatus()
    {
        OnPropertyChanged(nameof(HasGameplayTexture));
        GameplayTextureStatus = "";
        try
        {
            if (!HasGameplayTexture) return;
            if (_currentMapEntry is null || !TryResolveMaterialsBin(_currentMapEntry.Path, out var binEntry)) return;
            var bytes = ReadAsset(binEntry.PathHash);
            var unlinked = Formats.MapGeo.MapGameplayTextureBuilder.UnlinkedSlots(bytes);
            var samplers = Formats.MapGeo.MapGameplayTextureBuilder.Samplers(bytes);
            GameplayTextureStatus =
                $"{samplers.Count} sampler(s): {(samplers.Count == 0 ? "none — no texture yet" : string.Join(", ", samplers.Select(s => $"{s.Name}={s.TexturePath}")))}"
                + Environment.NewLine
                + $"unlinked channels: {(unlinked.Count == 0 ? "none" : string.Join(", ", unlinked))}";
        }
        catch (Exception ex) { GameplayTextureStatus = "could not read: " + ex.Message; }
    }

    /// <summary>M439: give MapGameplayTexture its texture. The sampler list is an embedded struct of two
    /// strings, so unlike the channel slots it needs no object link.</summary>
    [RelayCommand]
    private Task SetGameplayTextureSampler() => EditGameplayTexture(bytes =>
        Formats.MapGeo.MapGameplayTextureBuilder.SetSampler(
            bytes, GameplayTextureSamplerName, GameplayTexturePath, out var r) is { } b ? (b, r) : (null, r));

    /// <summary>M439: create a GameplayTextureChannel object and link the chosen slot to it. A link
    /// stores the target's path hash, so the object has to exist in THIS bin.</summary>
    [RelayCommand]
    private Task SetGameplayTextureChannel() => EditGameplayTexture(bytes =>
    {
        if (!Enum.TryParse<Formats.MapGeo.MapGameplayTextureBuilder.Slot>(GameplayChannelSlot, out var slot))
            return (null, new Formats.MapGeo.MapGameplayTextureBuilder.Result(false, $"unknown slot '{GameplayChannelSlot}'"));
        var b = Formats.MapGeo.MapGameplayTextureBuilder.SetChannel(bytes, slot, GameplayChannelName, null, out var r);
        return (b, r);
    });

    /// <summary>The shared save path for both MapGameplayTexture edits.</summary>
    private async Task EditGameplayTexture(
        Func<byte[], (byte[]? Bytes, Formats.MapGeo.MapGameplayTextureBuilder.Result Result)> edit)
    {
        if (_currentMapEntry is not { } mapEntry || !TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
        { _log.Warn("Map", "No map materials.bin is open."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            var (updated, result) = edit(ReadAsset(binEntry.PathHash));
            if (updated is null) { _log.Warn("Map", result.Detail); return; }

            string savedTo;
            if (TryWriteToProjectFile(binEntry, updated, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, updated, ".bin");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = binEntry.PathHash,
                    ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            _log.Success("Map", $"MapGameplayTexture: {result.Detail}. Saved to {savedTo} ({updated.Length:n0} bytes).");
            var left = Formats.MapGeo.MapGameplayTextureBuilder.UnlinkedSlots(updated);
            if (left.Count > 0)
                _log.Warn("Map", $"Still unlinked: {string.Join(", ", left)}. A null channel link is what makes "
                                 + "the client fail with Missing sampler \"AlphaMask\".");
            RefreshMapGraphicsFeatures();
        }
        catch (Exception ex) { _log.Error("Map", "MapGameplayTexture edit failed: " + ex.Message); }
    }

    /// <summary>M442: is a standalone lightgrid meaningful here? Only needs an open mapgeo — unlike atlas
    /// baking it does not need a lightmap layout.</summary>
    public bool CanBakeLightGrid => _currentMap is not null && _currentMapEntry is not null;

    /// <summary>M442: what the map currently declares, so the button can say whether it would create or
    /// replace a link.</summary>
    [ObservableProperty] private string _lightGridStatus = "";

    private void RefreshLightGridStatus()
    {
        OnPropertyChanged(nameof(CanBakeLightGrid));
        LightGridStatus = "";
        try
        {
            if (_currentMapEntry is null || !TryResolveMaterialsBin(_currentMapEntry.Path, out var binEntry)) return;
            var current = Formats.MapGeo.MapBakeProperties.Read(ReadAsset(binEntry.PathHash));
            LightGridStatus = current is { File.Length: > 0 }
                ? $"linked: {current.Value.File} (size {current.Value.Size})"
                : "no lightGridFileName — MapLightingV2 has no probe data to read";
        }
        catch (Exception ex) { LightGridStatus = "could not read: " + ex.Message; }
    }

    /// <summary>
    /// M442: bake a lightgrid for the open map and link it, without baking a single atlas.
    ///
    /// <para>The probe volume is what lights everything a lightmap cannot cover — characters, effects, and
    /// any surface whose material sets NO_BAKED_LIGHTING. It is also the prerequisite MapLightingV2 has in
    /// every shipped map that declares it (180 of 180 carry MapBakeProperties.lightGridFileName), which is
    /// why a map can declare V2 and still have nothing to read.</para>
    ///
    /// <para>Deliberately separate from the full bake: <c>LightBakeService.BakeAsync</c> bakes every atlas
    /// first, which needs a lightmap layout the map may not have and costs minutes. The grid needs neither.</para>
    /// </summary>
    [RelayCommand]
    private async Task BakeLightGrid()
    {
        if (_currentMapEntry is not { } mapEntry)
        { _log.Warn("Bake", "No map is open."); return; }
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
        { _log.Warn("Bake", "No map materials.bin was found, so the grid could not be linked."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        if (_currentMap is not { } map) { _log.Warn("Bake", "No map geometry is loaded."); return; }

        try
        {
            Status = "Building lightgrid…";

            // M443: an ANALYTIC fill, not a probe bake. Measured on the real base_srx: the probe path
            // produces bit-identical output in 85.3% of cells (its only spatial term is a binary sun
            // shadow), its cube is 2.5x too flat because ProbeDirection adds sky light isotropically, and
            // with auto-exposure off every sample saturates to 255 - which the shader's mad_sat turns into
            // flat, unshaded characters. The corpus-derived constant is strictly better until
            // ProbeDirection grows real per-direction sky visibility.
            // M596: KEEP what the map already declares. This used to hardcode 0.25 - a value 0 of the 200
            // shipped MapBakeProperties author (129 say 0.5, 25 say 0.65, 6 say 1.0). Jade declares 1.0,
            // so baking over it quartered the self-illumination of every champion, minion and turret on
            // the ported map. It reaches them through LIGHTGRID_SCALE.y in LIT_UBER_PS, which is why the
            // terrain looked right and only the CHARACTERS came out dark.
            float fullBright = Formats.MapGeo.MapBakeProperties.Read(ReadAsset(binEntry.PathHash)) is
                { FullBright: > 0f } declared
                ? declared.FullBright
                : Formats.Lighting.NeutralLightGrid.CorpusCharacterFullBrightIntensity;
            var grid = Formats.Lighting.NeutralLightGrid.Build(map, characterFullBrightIntensity: fullBright);

            // Riot's authored casing when the bin has it; the derived form otherwise (which resolves to
            // the same chunk key either way, since WadPath lowercases before hashing).
            string mapPath = Formats.Lighting.NeutralLightGrid.MapPathFromBin(ReadAsset(binEntry.PathHash))
                             ?? Formats.Lighting.NeutralLightGrid.MapPathFromMapGeo(mapEntry.Path);
            string gridPath = Formats.Lighting.NeutralLightGrid.FileNameFor(mapPath);
            byte[] gridBytes = grid.Write();

            // Round-trip before shipping it: the header is fixed-size and the cell count must match, so a
            // grid that cannot re-read is one the client would choke on too.
            var reread = Formats.Lighting.LightGridFile.Read(gridBytes);
            if (reread.Width != grid.Width || reread.Height != grid.Height
                || reread.Samples.Length != grid.Samples.Length)
            { _log.Error("Bake", "The baked lightgrid did not round-trip — not saved."); return; }

            WriteBakedAsset(gridPath, gridBytes, ".dat");

            // Link it. MapBakeProperties.Write leaves any other fields the map already has untouched.
            // lightGridCharacterFullBrightIntensity must EQUAL the file's header[28] - they match in
            // 173/173 joinable shipped pairs, so this passes the grid's own value rather than a constant.
            var linked = Formats.MapGeo.MapBakeProperties.Write(
                ReadAsset(binEntry.PathHash), gridPath, grid.Width,
                grid.CharacterFullBrightIntensity, out var linkResult);
            if (linked is null)
            { _log.Error("Bake", "The grid was written but could not be linked: " + linkResult.Detail); return; }

            string savedTo;
            if (TryWriteToProjectFile(binEntry, linked, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, linked, ".bin");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = binEntry.PathHash,
                    ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            _log.Success("Bake", $"Lightgrid {grid.Width}x{grid.Height} world {grid.WorldSizeX:0}x{grid.WorldSizeZ:0} "
                                 + $"({gridBytes.Length:n0} B) → {gridPath}; "
                                 + $"MapBakeProperties: {linkResult.Detail}. Bin saved to {savedTo}.");
            Status = "Lightgrid baked and linked.";
            RefreshLightGridStatus();
            RefreshMapGraphicsFeatures();
        }
        catch (Exception ex) { _log.Error("Bake", "Lightgrid bake failed: " + ex.Message); Status = "Lightgrid bake failed."; }
    }

    [RelayCommand]
    private async Task SaveMeshMoves()
    {
        // M80: never fail silently — say exactly which precondition is missing.
        if (_currentMap is not { } map || _currentMapBytes is null || _currentMapEntry is not { } entry)
        {
            _log.Warn("MapGeo", $"Cannot save: map={( _currentMap is null ? "none" : "ok")}, " +
                $"bytes={(_currentMapBytes is null ? "none" : "ok")}, entry={(_currentMapEntry is null ? "none" : "ok")}. Reload the map and try again.");
            return;
        }
        // M566: face edits ride the same save. They are length-preserving patches into the index and
        // vertex buffers, so they compose with the transform moves below rather than fighting them.
        bool hasFaces = _faceEdits.Count > 0;
        bool hasGrows = _faceGrows.Count > 0;   // M570: extrude / inset, which ADD geometry
        // M583: reshapes from Blender. Absent from this gate until now, so a push with nothing else
        // pending fell straight out of the early return below with "No map edits to save" and the
        // reshape was silently dropped - the M572 bug again, in the one place that decides whether the
        // save runs at all rather than in the property the button reads.
        bool hasReshapes = _blenderReshapes.Count > 0;
        bool hasMoves = MapGeoWriter.HasMoves(map.Meshes);
        bool hasLayers = MapGeoLayerWriter.HasEdits(map.Meshes);
        bool hasMaterials = MapGeoMaterialWriter.HasEdits(map.Meshes);   // M517
        var added = MapContent.AddedMeshes.ToList();
        var removedIndices = MapContent.AllMapPieces.Where(p => p.IsRemoved).Select(p => p.MeshIndex).Distinct().ToList();
        if (!hasFaces && !hasGrows && !hasReshapes && !hasMoves && !hasLayers && !hasMaterials
            && added.Count == 0 && removedIndices.Count == 0)
        { _log.Info("MapGeo", "No map edits to save."); return; }
        if (!GuardEditable(entry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            byte[] bytes = _currentMapBytes;

            // M517: material edits before everything else. They REWRITE the file (a material name is a
            // length-prefixed string, so a longer one moves every byte after it), which invalidates any
            // byte offset computed earlier - so this has to happen while nothing has been patched yet.
            // Both passes below re-locate meshes by signature and are unaffected by the rewrite.
            if (hasMaterials)
            {
                var swapped = MapGeoMaterialWriter.TryWriteMaterialEdits(
                    bytes, map.Meshes, ExtendedChannelMaterialsFor(entry.Path), out var mErr);
                if (swapped is null) { _log.Error("MapGeo", mErr ?? "Material edits could not be written."); return; }
                bytes = swapped;
                int changed = map.Meshes.Count(m => m.HasMaterialEdit);
                _log.Success("MapGeo", $"Changed the material on {changed:n0} mesh(es).");
            }

            // M581: reshapes from Blender FIRST, and never alongside anything that addresses geometry by
            // index. A reshape replaces a mesh's vertex and index buffers outright, so every triangle
            // number a face edit or a grow was computed from stops meaning what it meant. Mixing them
            // would not fail - it would write edits onto whatever triangles now happen to hold those
            // indices, so the two are separated rather than ordered.
            if (hasReshapes)
            {
                if (hasFaces || hasGrows)
                {
                    _log.Error("MapGeo", "There are reshaped meshes from Blender AND face edits pending. A "
                        + "reshape replaces a mesh's triangles, so the face edits no longer refer to the same "
                        + "ones. Save them separately: clear or save the face edits first, then reload.");
                    return;
                }
                var reshaped = MapGeoMeshReshaper.TryApply(bytes, PendingBlenderReshapes, out string? reshapeError);
                if (reshaped is null)
                { _log.Error("MapGeo", reshapeError ?? "Blender reshapes could not be written."); return; }
                bytes = reshaped;
                _log.Success("MapGeo", $"Wrote {PendingBlenderReshapes.Count:n0} reshaped mesh(es) from Blender.");
            }

            // M566: face edits next, before anything that patches by byte offset. Every one is
            // length-preserving - a deleted face is degenerated rather than removed - so the offsets the
            // later passes compute are still valid, but they DO rewrite index and vertex bytes, so doing
            // them first keeps that rewriting away from freshly patched transforms.
            if (hasFaces)
            {
                var withFaces = MapGeoFaceWriter.TryApply(bytes, map, _faceEdits, out string? faceError);
                if (withFaces is null) { _log.Error("MapGeo", faceError ?? "Face edits could not be written."); return; }
                bytes = withFaces;
                _log.Success("MapGeo", $"Wrote {_faceEdits.Count:n0} face edit(s).");
            }

            // M570: extrude and inset AFTER the length-preserving face edits and before everything
            // else. They grow the index buffer and move every submesh offset after the insertion, so
            // anything that located a byte offset earlier would be reading the wrong place - and the
            // length-preserving edits above address triangles by their ORIGINAL index, which only holds
            // while nothing has been inserted yet.
            if (hasGrows)
            {
                var withGrows = MapGeoFaceGrower.TryApply(bytes, map, _faceGrows, out string? growError);
                if (withGrows is null) { _log.Error("MapGeo", growError ?? "Extrude/inset could not be written."); return; }
                bytes = withGrows;
                _log.Success("MapGeo", $"Wrote {_faceGrows.Count:n0} extrude/inset operation(s).");
            }

            // 0) M105: layer/controller/backface edits FIRST — they don't touch the [bbox][transform]
            //    signatures, so the move patching that follows still locates every mesh.
            if (hasLayers)
            {
                var layered = MapGeoLayerWriter.TryWriteLayerEdits(bytes, map.Meshes, out var lErr);
                if (layered is null) { _log.Error("MapGeo", $"Could not save layer edits: {lErr}"); return; }
                bytes = layered;
            }

            // 1) mesh moves (rebuilds bucket grids for the moved geometry)
            if (hasMoves)
            {
                var moved = MapGeoWriter.TryWriteWithMoves(bytes, map.Meshes, out var mErr);
                if (moved is null) { _log.Error("MapGeo", $"Could not save mesh moves: {mErr}"); return; }
                bytes = moved;
            }

            // 2) append the imported meshes (surgical splice), then regenerate bucket grids over ALL
            //    triangles (new geometry included) so the game culls the added meshes correctly.
            if (added.Count > 0)
            {
                var newMeshes = added.Select(a => new NewMapMesh(
                    a.Material, a.Positions, a.Normals, a.Uvs,
                    System.Array.ConvertAll(a.Indices, i => (ushort)i), a.Transform)).ToList();
                var appended = MapGeoMeshAppender.Append(bytes, newMeshes, out var aErr);
                if (appended is null) { _log.Error("MapGeo", $"Could not append meshes: {aErr}"); return; }

                var reMap = await Task.Run(() => MapGeoDecoder.Decode(appended));
                WarnIfAddedMeshesWillNotDrawInGame(added.Select(a => a.Material));

                // M123: the appended meshes are the LAST N — give them their chosen layer masks
                // before the grids bake per-face visibility from the mesh flags.
                bool anyMask = added.Any(a => a.VisibilityMask != 255);
                if (anyMask && reMap.Meshes.Count >= added.Count)
                {
                    for (int i = 0; i < added.Count; i++)
                        reMap.Meshes[reMap.Meshes.Count - added.Count + i].VisibilityEdit = added[i].VisibilityMask;
                    var layered = MapGeoLayerWriter.TryWriteLayerEdits(appended, reMap.Meshes, out var lErr);
                    if (layered is not null) { appended = layered; reMap = await Task.Run(() => MapGeoDecoder.Decode(appended)); }
                    else _log.Warn("MapGeo", $"Added-mesh layers not applied: {lErr}");
                }
                bytes = MapGeoWriter.WriteWithRegeneratedBucketGrids(appended, reMap, SaveBakeSize(), SaveBakeMin(), SaveBakeMax());
            }

            // 2b) M105: bucket grids bake per-face visibility masks from the mesh flags, so layer-only
            //     saves must regenerate them too (moves/appends above already did).
            //
            //     M585: and so must anything that changes GEOMETRY. A bucket grid carries its own baked
            //     copy of the map, and the game culls against that copy - so a reshape, an extrude or a
            //     face edit that is not followed by a rebuild leaves the game hiding meshes that have
            //     moved out from under the old cells. Measured on a real edited map: the grid claimed
            //     945,676 baked vertices where the geometry had 843,339, and the symptom was decals and
            //     meshes blinking out as the camera turned.
            if ((hasLayers || hasReshapes || hasFaces || hasGrows) && !hasMoves && added.Count == 0)
            {
                var reMap2 = await Task.Run(() => MapGeoDecoder.Decode(bytes));
                bytes = MapGeoWriter.WriteWithRegeneratedBucketGrids(bytes, reMap2, SaveBakeSize(), SaveBakeMin(), SaveBakeMax());
            }

            // 3) Blender-style pending deletion: remove only the selected mesh records. Their buffers stay
            // in the file unreferenced so no surviving mesh ID has to be rewritten; bucket grids are then
            // rebuilt from the remaining environment meshes.
            if (removedIndices.Count > 0)
            {
                var stripped = MapGeoMeshRemover.Remove(bytes, removedIndices, out var removeError);
                if (stripped is null) { _log.Error("MapGeo", $"Could not remove selected meshes: {removeError}"); return; }
                var remainingMap = await Task.Run(() => MapGeoDecoder.Decode(stripped));
                bytes = MapGeoWriter.WriteWithRegeneratedBucketGrids(stripped, remainingMap, SaveBakeSize(), SaveBakeMin(), SaveBakeMax());
            }

            // M417: same rule as the bin editor and placements - the project FILE wins when the project
            // ships this mapgeo, or the build discards the edit and the mod exports unchanged.
            string savedTo;
            if (TryWriteToProjectFile(entry, bytes, out var geoProjectFile)) savedTo = geoProjectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, ".mapgeo");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = entry.PathHash,
                    ResolvedPath = entry.IsResolved ? entry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(entry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            UpdateTitle();
            UndoService.MarkSaved();
            int moves = map.Meshes.Count(x => x.IsMoved);
            int layers = map.Meshes.Count(x => x.HasLayerEdit);
            // M566: the face edits are IN the file now. Replaying them on the next save would apply each
            // one twice - a second flip is the identity and a second move travels double - so the pending
            // list is emptied exactly here, where the bytes are known to have been written.
            int faceEdits = _faceEdits.Count + _faceGrows.Count;
            _faceEdits.Clear();
            _faceGrows.Clear();
            // M584: reshapes are cleared HERE too, not where they were applied. Clearing them earlier
            // would drop them if a later pass refused and returned, and the auto-apply reads this list
            // to tell "written" from "refused".
            ClearBlenderReshapes();
            OnPropertyChanged(nameof(HasFaceGrows));
            _faceUndoIndices.Clear();
            NotifyFaceState();
            _log.Success("MapGeo", $"Saved {moves} mesh move(s) + {layers} layer edit(s) + {faceEdits} face edit(s) + {added.Count} added + {removedIndices.Count} deleted mesh(es) to {savedTo} ({bytes.Length:n0} bytes). Build Package will include it. Reload the map to edit the resulting native geometry.");
        }
        catch (Exception ex) { _log.Error("MapGeo", ex.Message); }
    }

    /// <summary>
    /// M700: the one definition of "this map has placement edits to save".
    ///
    /// <para>It gates both the Save to Mod button and the two save-everything paths, and it was written
    /// out by hand in seven places - most of which counted only particles, or particles and sounds. A prop
    /// move was therefore saved by nothing at all: EndPlacementDrag recomputed the flag WITHOUT props at
    /// the end of every drag, so the button went straight back to disabled and the whole-project save
    /// skipped placements. The predicate below is the one SaveParticleMoves actually acts on, including
    /// the rule that a sound derived from a particle system is saved with that system rather than on its
    /// own, so the button cannot promise work the save will then refuse to do.</para>
    /// </summary>
    private void RefreshPlacementDirtyFlag() => HasParticleMoves = MapContent.HasPlacementEdits;

    /// <summary>Persist the moved particles into the map's .materials.bin override (M35).</summary>
    [RelayCommand]
    private async Task SaveParticleMoves()
    {
        if (_currentMapEntry is not { } mapEntry) return;
        var moved = MapContent.AllParticles.Where(v => v.HasEdits).ToList();
        // M199 (5.2): sounds derived FROM a particle system are no longer skipped. They used to be, because
        // they share the particle's transform BYTES and the old locator found placements by that signature -
        // so saving both collided. Identity is now (container, item key), which is unique, so the collision
        // cannot happen. A derived sound has no placement id of its own, though: it is a view of the
        // particle, so moving the particle is still what moves it, and only standalone MapAudio saves here.
        var movedSounds = MapContent.Sounds.Where(s => s.HasEdits && !s.Sound.FromParticleSystem).ToList();
        var editedProps = MapContent.AllProps.Where(p => p.HasEdits).ToList();
        var editedProbes = MapContent.Probes.Where(p => p.HasEdits).ToList();
        int derivedSounds = MapContent.Sounds.Count(s => s.HasEdits && s.Sound.FromParticleSystem);
        if (derivedSounds > 0)
            _log.Info("Sounds", $"{derivedSounds} moved sound(s) follow their particle system and are saved with it.");
        if (moved.Count == 0 && movedSounds.Count == 0 && editedProps.Count == 0 && editedProbes.Count == 0)
        { _log.Info("Map Content", "No placement edits to save."); return; }   // M700: MapContent.HasPlacementEdits is this set
        foreach (var prop in editedProps.Where(p => !string.IsNullOrWhiteSpace(p.EditedSkin)
                     && !p.EffectiveSkin.Equals(p.Prop.Skin, StringComparison.OrdinalIgnoreCase)))
        {
            string path = prop.EffectiveSkin.Replace('\\', '/').TrimStart('/');
            if (!path.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) path = "data/" + path;
            if (!path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) path += ".bin";
            if (ReadAssetByPath(path) is null)
            {
                _log.Error("Props", $"Skin '{prop.EffectiveSkin}' does not resolve to an installed/project skin bin. Nothing was saved.");
                return;
            }
        }
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry)) { _log.Error("Particles", "No materials .bin to save into."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        // M199 (5.2): edits are addressed by tree identity, not by a 64-byte transform signature. 1,450 of
        // 30,628 shipped placements share a matrix with a neighbour, so the old signature could patch the
        // wrong one; 2 have no transform and were unaddressable entirely.
        // M204: one edit per placement carrying every verb the user changed. Fields the user did not
        // touch stay null, which the writer reads as "leave this alone" - so a rename does not also
        // rewrite the transform.
        var placementEdits = moved
            .Where(v => v.Placement.Id.IsValid)
            .Select(v => new MapPlacementEdit(v.Placement.Id)
            {
                CloneOf = v.CloneSource,   // M206: null for an existing placement
                // A NEW placement always writes its transform - it has no authored one to preserve.
                Transform = v.IsMoved || v.IsNew ? v.CurrentTransform : null,
                Name = v.EditedName is { } n && n != v.Placement.Name ? n : null,
                ColorModulate = v.ParsedTint,
                SystemLink = v.EditedSystemHash != 0 ? v.EditedSystemHash : null,
                VisibilityFlags = v.EditedVisibilityFlags,
                Remove = v.IsRemoved,
            })
            .ToList();
        int unaddressable = moved.Count(v => !v.Placement.Id.IsValid);
        if (unaddressable > 0)
            _log.Warn("Particles", $"{unaddressable} moved particle(s) have no identity in the bin and were skipped.");


        // M202: sounds go through the SAME identity-addressed writer. Nothing uses the byte-signature
        // patcher any more, which matters because MapAudio placements are part of the same 1,450 that share
        // a transform with a neighbour - a standalone sound move could move the wrong thing too.
        placementEdits.AddRange(movedSounds
            .Where(s => s.Sound.Id.IsValid)
            .Select(s =>
            {
                var t = s.Sound.Transform;
                t.Translation = s.Position;
                return new MapPlacementEdit(s.Sound.Id)
                {
                    Transform = s.IsMoved ? t : null,
                    VisibilityFlags = s.EditedVisibilityFlags,
                    Remove = s.IsRemoved,
                };
            }));
        int unaddressableSounds = movedSounds.Count(s => !s.Sound.Id.IsValid);
        if (unaddressableSounds > 0)
            _log.Warn("Sounds", $"{unaddressableSounds} moved sound(s) have no identity in the bin and were skipped.");

        placementEdits.AddRange(editedProps.Where(p => p.Prop.Id.IsValid).Select(p => new MapPlacementEdit(p.Prop.Id)
        {
            Transform = p.IsMoved ? p.CurrentTransform : null,   // M699: null leaves the authored one alone
            Skin = !string.IsNullOrWhiteSpace(p.EditedSkin)
                && !p.EffectiveSkin.Equals(p.Prop.Skin, StringComparison.OrdinalIgnoreCase) ? p.EffectiveSkin : null,
            VisibilityFlags = p.EditedVisibilityFlags,
            // M750: change or remove the game-clock gate in place, without placing the prop again.
            AppearAfterSeconds = p.AppearAfterChanged ? (float)p.AppearAfterSeconds : null,
            Remove = p.IsRemoved,
        }));
        placementEdits.AddRange(editedProbes.Where(p => p.Probe.Id.IsValid).Select(p => new MapPlacementEdit(p.Probe.Id)
        {
            VisibilityFlags = p.EditedVisibilityFlags,
            Remove = p.IsRemoved,
        }));
        int unaddressableOthers = editedProps.Count(p => !p.Prop.Id.IsValid) + editedProbes.Count(p => !p.Probe.Id.IsValid);
        if (unaddressableOthers > 0)
            _log.Warn("Map Content", $"{unaddressableOthers} prop/probe placement(s) have no identity and were skipped.");

        var source = GetAssetBytes(binEntry);
        string? err = null;
        byte[]? bytes = placementEdits.Count > 0
            ? MapPlaceableWriter.WriteEdits(source, placementEdits, out err)
            : source;
        if (bytes is null) { _log.Error("Particles", $"Could not save placement edits: {err}"); return; }
        if (err is not null) _log.Warn("Particles", err);
        try
        {
            // M417: write the PROJECT FILE when the project ships this bin, exactly as SaveBinToOverride
            // has done since M98c. Placement edits went straight to the override store instead - and the
            // build refuses to let an override clobber a path a project folder provides (M126/M131), so
            // every deleted or moved particle was saved, reported as saved, and then dropped at package
            // time. TryWriteToProjectFile also dissolves the stale shadow, including a record-less orphan.
            if (TryWriteToProjectFile(binEntry, bytes, out var projectFile))
            {
                SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
                Project.IsDirty = true;
                UpdateTitle();
                HasParticleMoves = false;
                _log.Success("Map Content",
                    $"Saved {placementEdits.Count} placement edit(s) to {projectFile}. Build Package will include it.");
                return;
            }

            var dest = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, bytes, ".bin");
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = binEntry.PathHash,
                ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                OverrideFile = dest,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            _overrides.SaveTo(Project);   // M417: otherwise the record lives only in memory until some
                                          // unrelated command happens to persist it, and the build sees
                                          // an overrides list that does not mention this file
            SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            UpdateTitle();
            HasParticleMoves = false;
            _log.Success("Map Content", $"Saved {placementEdits.Count} placement edit(s) to the materials.bin override. Build Package will include it.");
        }
        catch (Exception ex) { _log.Error("Particles", ex.Message); }
    }

    private async Task LoadBinAsync(WadAssetEntry entry)
    {
        if (!ContentLoaded) return;
        try
        {
            byte[] sourceBytes = ReadAsset(entry.PathHash);
            var doc = await Task.Run(() =>
                BinEditorDocument.Parse(sourceBytes,
                    h => _resolver.Database.TryGetBinName(h, out var n) ? n : null));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                BinEditor.Load(doc, entry, sourceBytes);
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
        bytes = RebaseOntoCurrent(entry, bytes, BinEditor.BaseBytes, "Bin");

        // Validate the edited .bin re-parses before committing it to the override layer.
        try { _ = new LeagueToolkit.Core.Meta.BinTree(new MemoryStream(bytes, false)); }
        catch (Exception ex) { _log.Error("Bin", $"Edited .bin failed to re-parse — NOT saved: {ex.Message}"); return; }

        try
        {
            // M98c: folder-project files are edited in place — no shadow override
            if (TryWriteToProjectFile(entry, bytes, out var projectFile))
            {
                SetNodeStatus(entry.PathHash, AssetStatus.Modified);
                Project.IsDirty = true;
                UpdateTitle();
                UndoService.MarkSaved();
                _log.Success("Bin", $"Saved edited {entry.DisplayName} to {projectFile} ({bytes.Length:n0} bytes, re-parse OK).");
                return;
            }
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
                // M120: ONE preview surface - the image shows in the Model Preview window (replacing
                // whatever it showed) instead of stacking a second preview card into the inspector.
                MeshPreview.ShowImage(entry.DisplayName, BitmapFactory.FromRgba(img),
                    $"{img.Width}×{img.Height} · {entry.Compression}");
                ShowMeshPreviewWindow?.Invoke();
                _log.Info("Preview", $"Decoded {entry.DisplayName} ({img.Width}×{img.Height}).");
            });
        }
        catch (Exception ex) { _log.Error("Preview", $"{entry.DisplayName}: {ex.Message}"); }
    }

    // ---- M50: model preview window (separate viewport; main viewport stays on the map) ----
    public MeshPreviewViewModel MeshPreview { get; } = new();
    public Action? ShowMeshPreviewWindow;   // wired by MainWindow (owns the window instance)

    /// <summary>Convert an old room.nvr/room.wgeo into the currently open project's mapgeo. Ordinary
    /// destination geometry is replaced, while v18 render-region meshes remain part of the destination.</summary>
    [RelayCommand]
    private async Task PortLegacyMap()
    {
        if (!ProjectMode || _currentMapEntry is not { } initialMap || _currentMapBytes is null)
        { _log.Warn("Legacy Port", "Open a project mapgeo first."); return; }
        if (!await EnsureProjectSavedAsync()) return;
        if (!TryResolveMaterialsBin(initialMap.Path, out var initialBin))
        { _log.Error("Legacy Port", "The open map has no companion materials .bin."); return; }

        WadAssetEntry mapEntry = initialMap, binEntry = initialBin;

        var folder = await Dialogs.OpenFolderAsync("Select a legacy LEVELS/MapN folder containing Scene/room.nvr or room.wgeo");
        if (folder is null) return;

        if (MaterialEditor.Catalog is null && MaterialEditor.SelectedShaderEnvironment is { } environment)
            await LoadShaderCatalogAsync(environment);
        if (MaterialEditor.Catalog is not { } catalog)
        { _log.Error("Legacy Port", "No League shader catalogue is available. Set a valid game folder and retry."); return; }

        Status = "Converting legacy map...";
        LegacyMapPortResult result;
        LegacyDestinationContentSummary destinationSummary;
        HashSet<string> bushMaterials;
        byte[] originalMapBytes;
        byte[] originalBinBytes;
        try
        {
            originalMapBytes = GetAssetBytes(mapEntry);
            originalBinBytes = GetAssetBytes(binEntry);
            var shaderNames = catalog.Shaders.GroupBy(shader => HashAlgorithms.Fnv1a(shader.Name))
                .ToDictionary(group => group.Key, group => group.First().Name);
            string? ResolveLegacyName(uint hash) => ResolveBinName(hash)
                ?? shaderNames.GetValueOrDefault(hash)
                ?? (hash == HashAlgorithms.Fnv1a("StaticMaterialDef") ? "StaticMaterialDef" : null);
            var document = MaterialDocument.Parse(originalBinBytes, ResolveLegacyName);
            bushMaterials = document.Materials
                .Where(material => material.IsStaticMaterialDef
                    && (material.Profile.IsVertexDeform
                        || material.RenderShader?.Contains("VertexDeform", StringComparison.OrdinalIgnoreCase) == true))
                .Select(material => material.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (LegacyMapPorter.MapSpecificBushMaterials(mapEntry.Path) is { } mapSpecificBushMaterials)
                bushMaterials = mapSpecificBushMaterials.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var placements = MapPlaceableExtractor.Extract(originalBinBytes, ResolveWadPath);
            var particles = MapParticleExtractor.Extract(originalBinBytes, ResolveLegacyName);
            int bushCount = LegacyMapPorter.CountBushMeshes(originalMapBytes, bushMaterials);
            int previousImportCount = LegacyMapPorter.CountPreviousImportedMeshes(originalMapBytes);
            int ordinaryCount = MapGeoBinary.TryReadEditable(originalMapBytes, out var editable)
                ? Math.Max(0, editable.Meshes.Count(mesh => !mesh.HasRegionHash || mesh.RegionHash == 0)
                    - bushCount - previousImportCount)
                : 0;
            destinationSummary = new LegacyDestinationContentSummary(
                ordinaryCount, bushCount, previousImportCount, document.Materials.Count(material => material.IsStaticMaterialDef),
                particles.Count, placements.Props.Count, placements.Sounds.Count, placements.Probes.Count);
            result = await Task.Run(() => LegacyMapPorter.Port(folder, originalMapBytes, mapEntry.Path));
        }
        catch (Exception ex)
        { _log.Error("Legacy Port", ex.Message); Status = "Legacy map conversion failed."; return; }

        var shaderChoices = catalog.Shaders
            .Where(shader => shader.Category.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase))
            .Select(shader => shader.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var portPerms = ShaderPerms();
        Func<string, string, LegacyMapPorter.MacroSupport>? macroSupport =
            portPerms is { IsAvailable: true }
                ? (shader, macro) => MacroSupportFor(portPerms, catalog, shader, macro)
                : null;

        // M528: let the porter see each texture's ALPHA before it decides to alpha-test the surface.
        // The bytes are already in hand - the port carries every texture it is about to copy - so this
        // costs one decode per distinct texture and nothing on disk.
        var alphaCache = new Dictionary<string, LegacyAlphaKind>(StringComparer.OrdinalIgnoreCase);
        var portedBytes = result.Textures.ToDictionary(t => t.TargetPath, t => t.Bytes,
            StringComparer.OrdinalIgnoreCase);
        LegacyAlphaKind ClassifyPortedAlpha(string texture)
        {
            if (alphaCache.TryGetValue(texture, out var cached)) return cached;
            var kind = LegacyAlphaKind.Cutout;   // fail towards the previous behaviour
            if (portedBytes.TryGetValue(texture, out var bytes))
            {
                try { kind = LegacyAlphaClassifier.Classify(TextureDecoder.Decode(bytes).Rgba); }
                catch (Exception ex) { _log.Warn("Port", $"Could not read the alpha of {texture}: {ex.Message}"); }
            }
            alphaCache[texture] = kind;
            return kind;
        }

        // M534: decide the shaders BEFORE the dialog opens, so its rows show what the porter actually
        // chose rather than what it proposed before the alpha classifier ran. That is what makes a row
        // the user re-affirms a real choice instead of an untouched default - the M533 "only changed
        // rows" filter had no way to tell those apart, so wanting the default everywhere was unsayable.
        result = LegacyMapPorter.ApplyShaderOptions(result,
            LegacyPortShaderOptions.Defaults, macroSupport, note: null, ClassifyPortedAlpha);

        // M600: open the dialog on what this project's last completed port used. Nothing recorded these
        // before, so re-running a port to change ONE option meant re-deriving the other twelve from
        // memory - and getting one wrong produced a different map with nothing saying so.
        var remembered = Project?.LegacyPort;
        if (remembered is not null)
            _log.Info("Legacy Port", "Opening on this project's last port settings"
                + (remembered.SavedUtc is { } when ? $" (saved {when.ToLocalTime():yyyy-MM-dd HH:mm})" : "")
                + $" - decal planes {(remembered.GenerateDecalQuads ? "ON" : "off")}.");

        LegacyMapPortShaderSelection? selection = null;
        if (PromptOwner is not null)
        {
            selection = await Views.LegacyMapPortWindow.ShowAsync(PromptOwner, result, shaderChoices,
                destinationSummary, remembered);
            if (selection is null) { Status = "Legacy map port cancelled."; return; }
        }
        else if (remembered is not null)
        {
            // No dialog (headless/scripted): repeat the remembered port rather than silently falling back
            // to FullReplacement, which is a DIFFERENT port and the exact trap this milestone exists for.
            selection = LegacyMapPortWindowViewModel.Replay(remembered, shaderChoices);
        }
        // M550: rebuilding the decals as planes changes the GEOMETRY, and the port that produced `result`
        // ran before the dialog existed to ask. Re-run it with the option rather than trying to rebuild
        // meshes after the fact - the quad fit needs the per-vertex UVs the accumulator holds, and only
        // the porter has those. Costs a second port, and only when the user opted in.
        if (selection?.Decals is { GenerateQuads: true } decalOptions)
        {
            try
            {
                result = await Task.Run(() => LegacyMapPorter.Port(folder, originalMapBytes, mapEntry.Path, decalOptions));
                result = LegacyMapPorter.ApplyShaderOptions(result,
                    selection.RoleShaders, macroSupport, note: null, ClassifyPortedAlpha);
            }
            catch (Exception ex)
            {
                _log.Error("Legacy Port", $"Rebuilding the decals as planes failed: {ex.Message}");
                Status = "Legacy map conversion failed."; return;
            }
        }

        var cleanup = selection?.Cleanup ?? LegacyPortCleanupOptions.FullReplacement;
        // M502: let the porter ask the real shader cache before authoring NO_BAKED_LIGHTING, instead of
        // trusting a hardcoded role list. Measured: 4TextureBlend_WorldProjected does not declare the axis
        // at all (macro ignored by the client, so writing it only misleads later readers), while
        // DefaultEnv_Flat_AlphaTest declares it and never cooked it (writing it is the M486 crash).

        result = LegacyMapPorter.ApplyShaderOptions(result,
            selection?.RoleShaders ?? LegacyPortShaderOptions.Defaults,
            macroSupport, m => _log.Info("Port", m), ClassifyPortedAlpha,
            selection?.MaterialShaders);
        bool correctedLegacyPosition = selection?.FixImportedMapPosition == true;
        // M473: the correction the USER typed in the port window, not the constant.
        var legacyCorrection = selection?.PositionCorrection ?? LegacyMapPorter.LegacyPositionCorrection;
        if (correctedLegacyPosition)
        {
            try { result = LegacyMapPorter.ApplyImportedPositionCorrection(result, legacyCorrection); }
            catch (Exception ex)
            { _log.Error("Legacy Port", $"Imported map position correction failed: {ex.Message}"); return; }
        }

        LegacyMeshCleanupResult meshCleanup;
        try
        {
            meshCleanup = LegacyMapPorter.ApplyMeshCleanup(result, cleanup, bushMaterials);
            result = result with
            {
                MapGeoBytes = meshCleanup.MapGeoBytes,
                RemovedBaseMeshCount = meshCleanup.RemovedMeshCount + meshCleanup.RemovedBushMeshCount,
                PreservedRenderRegionMeshCount = meshCleanup.PreservedRenderRegionMeshCount,
            };
        }
        catch (Exception ex)
        { _log.Error("Legacy Port", $"Destination mesh cleanup failed: {ex.Message}"); return; }

        var missingShaders = result.Materials.Where(m => catalog.Find(m.Shader) is null)
            .Select(m => m.Shader).Distinct().ToList();
        if (missingShaders.Count > 0)
        { _log.Error("Legacy Port", "The selected client does not ship required shader(s): " + string.Join(", ", missingShaders)); return; }

        // Only now mutate project state. Copying references is part of the confirmed port; Riot's WAD
        // itself remains read-only, and cancelling above leaves the project completely unchanged.
        var copyEntries = new[] { initialMap, initialBin }
            .Where(e => e.SourceKind == AssetSourceKind.RiotReference).ToList();
        if (copyEntries.Count > 0)
        {
            bool copyFailed = false;
            _copyBatch = true;
            try
            {
                foreach (var entry in copyEntries)
                {
                    if (!_nodesByHash.TryGetValue(entry.PathHash, out var node)
                        || !await CopyOneAssetToProject(node, replaceExisting: false))
                    { _log.Error("Legacy Port", $"Could not copy '{entry.DisplayName}' into the project."); copyFailed = true; break; }
                }
            }
            finally { _copyBatch = false; }
            BuildMounts(); BuildProjectTree();
            if (copyFailed) return;
            if (!TryResolveEntry(initialMap.PathHash, out mapEntry)
                || !TryResolveEntry(initialBin.PathHash, out binEntry))
            { _log.Error("Legacy Port", "The project copies could not be remounted."); return; }
        }

        try
        {
            byte[] binBytes = GetAssetBytes(binEntry);
            var placementEdits = new List<MapPlacementEdit>();
            if (cleanup.RemoveOriginalParticles)
                placementEdits.AddRange(MapParticleExtractor.Extract(binBytes, ResolveBinName)
                    .Where(item => item.Id.IsValid).Select(item => new MapPlacementEdit(item.Id) { Remove = true }));
            var originalPlacements = MapPlaceableExtractor.Extract(binBytes, ResolveWadPath);
            if (cleanup.RemoveOriginalProps)
                placementEdits.AddRange(originalPlacements.Props.Where(item => item.Id.IsValid)
                    .Select(item => new MapPlacementEdit(item.Id) { Remove = true }));
            if (cleanup.RemoveOriginalSounds)
                placementEdits.AddRange(originalPlacements.Sounds.Where(item => item.Id.IsValid)
                    .Select(item => new MapPlacementEdit(item.Id) { Remove = true }));
            if (cleanup.RemoveOriginalProbes)
                placementEdits.AddRange(originalPlacements.Probes.Where(item => item.Id.IsValid)
                    .Select(item => new MapPlacementEdit(item.Id) { Remove = true }));
            placementEdits = placementEdits.DistinctBy(edit => edit.Id).ToList();
            int removedPlacements = placementEdits.Count;
            if (placementEdits.Count > 0)
            {
                var cleaned = MapPlaceableWriter.WriteEdits(binBytes, placementEdits, out var placementError);
                if (cleaned is null) throw new InvalidDataException($"Destination placement cleanup failed: {placementError}");
                if (!string.IsNullOrWhiteSpace(placementError))
                    _log.Warn("Legacy Port", placementError);
                binBytes = cleaned;
            }

            var resetBin = MapMaterialFactory.RemoveGeneratedMaterials(binBytes, "LegacyPort/",
                out int replacedGenerated, out var resetError);
            if (resetBin is null) throw new InvalidDataException($"Could not replace earlier legacy materials: {resetError}");
            binBytes = resetBin;

            int created = 0, updated = 0;
            foreach (var material in result.Materials)
            {
                bool existed = MapMaterialFactory.ContainsMaterial(binBytes, material.Name);
                var next = MapMaterialFactory.CreateFromShader(binBytes, material.Name, catalog.Find(material.Shader)!, out var error,
                    material.Samplers, material.Parameters, material.Switches, material.Macros,
                    replaceExisting: true, blendEnable: material.BlendEnabled,
                    sourceBlendFactor: material.SourceBlendFactor,
                    destinationBlendFactor: material.DestinationBlendFactor,
                    samplerAddressMode: material.SamplerAddressMode);
                if (next is null) throw new InvalidDataException($"Material '{material.Name}': {error}");
                binBytes = next;
                if (existed) updated++; else created++;
            }

            int removedMaterials = 0;
            if (cleanup.RemoveUnusedOriginalMaterials)
            {
                if (!MapGeoBinary.TryReadEditable(result.MapGeoBytes, out var finalMap))
                    throw new InvalidDataException("The final mapgeo could not be inspected for material cleanup.");
                var usedMaterials = finalMap.Meshes.SelectMany(mesh => mesh.Submeshes)
                    .Select(submesh => submesh.Material).Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var pruned = MapMaterialFactory.RemoveUnusedStaticMaterials(binBytes, usedMaterials,
                    out removedMaterials, out var materialError);
                if (pruned is null) throw new InvalidDataException($"Destination material cleanup failed: {materialError}");
                binBytes = pruned;
            }

            foreach (var texture in result.Textures)
                WriteBakedAsset(texture.TargetPath, texture.Bytes, Path.GetExtension(texture.TargetPath));

            // M531: the source map's own particles. Placed with the SAME translation the geometry got -
            // legacy particle coordinates share the NVR's space, so that shift is the whole correction.
            string particleSummary = "";
            if (selection?.ImportLegacyParticles == true)
            {
                var withParticles = ImportLegacyParticles(binBytes, result.SourceFile,
                    correctedLegacyPosition ? legacyCorrection : System.Numerics.Vector3.Zero,
                    binEntry, out particleSummary);
                if (withParticles is null)
                    _log.Error("Legacy Port", "Particles were not imported. If this map had particles from "
                        + "an earlier port, the cleanup pass has already removed their placements - re-run "
                        + "the port to restore them.");
                else binBytes = withParticles;
            }

            // M575: the source client's map audio. Repacked rather than copied - the legacy banks are a
            // bank version the current client will not read - and added to a bank this map already loads,
            // so the mod does not have to carry a modified map bin as well.
            string audioSummary = "";
            if (selection?.ImportLegacySounds == true)
            {
                var withAudio = ImportLegacyAudio(binBytes, result.SourceFile,
                    correctedLegacyPosition ? legacyCorrection : System.Numerics.Vector3.Zero,
                    mapEntry, out audioSummary);
                if (withAudio is not null) binBytes = withAudio;
            }

            if (!await SaveMapBinBytesAsync(binEntry, binBytes))
                throw new InvalidDataException("The companion materials bin could not be saved.");

            // M533: the material editor is still holding the document it parsed when the map was OPENED.
            // Nothing above replaced it, and three things read it: the Inspector's material panel shows
            // its rows, BuildDx11SceneAsync prefers its serialized bytes over disk while it is dirty
            // (M501), and the autosave tick would write that pre-port document straight back over the
            // port. So a re-port left the viewport and the Inspector showing the PREVIOUS port's
            // materials while the file on disk held the new ones.
            //
            // Reloaded BEFORE LoadMapGeoAsync below, because that is what bumps MapGeneration and
            // rebuilds the D3D11 scene - refreshing afterwards would rebuild from the stale document.
            if (MaterialEditor.Kind == MaterialSourceKind.MapMaterials
                && MaterialEditor.BinEntry is { } editing && editing.PathHash == binEntry.PathHash
                && TryResolveEntry(binEntry.PathHash, out var reloadedBin))
                await LoadMaterialBinAsync(reloadedBin, alsoRawBin: false);

            if (TryWriteToProjectFile(mapEntry, result.MapGeoBytes, out var mapFile))
                _log.Success("Legacy Port", $"Saved converted mapgeo to {mapFile}.");
            else
            {
                string dest = ProjectWorkspace.StoreOverrideBytes(Project, mapEntry.PathHash, result.MapGeoBytes, ".mapgeo");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = mapEntry.PathHash,
                    ResolvedPath = mapEntry.IsResolved ? mapEntry.Path : null,
                    OverrideFile = dest,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
            }

            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            BuildMounts(); BuildProjectTree(); UpdateTitle();
            foreach (string warning in result.Warnings.Take(12)) _log.Warn("Legacy Port", warning);
            if (result.Warnings.Count > 12) _log.Warn("Legacy Port", $"{result.Warnings.Count - 12:n0} additional warning(s) omitted.");
            _log.Success("Legacy Port", $"{result.SourceFormat} port complete: {result.ImportedMeshCount:n0} meshes, " +
                $"{result.Textures.Count:n0} unique textures, {created:n0} material(s) created, {updated:n0} updated; " +
                $"removed {meshCleanup.RemovedMeshCount:n0} destination meshes + {meshCleanup.RemovedBushMeshCount:n0} bushes, " +
                $"{meshCleanup.RemovedSubmeshCount:n0} material ranges, {removedPlacements:n0} placements, " +
                $"and {removedMaterials:n0} unused materials; " +
                $"rebuilt {replacedGenerated:n0} earlier legacy material(s), retained {meshCleanup.RetainedOriginalMeshCount:n0} " +
                $"selected destination meshes, and protected {result.PreservedRenderRegionMeshCount:n0} render-region meshes" +
                // M473: report the correction that was ACTUALLY used, not the default constant - they are
                // no longer the same thing now that the port window can override it.
                (correctedLegacyPosition ? $"; imported geometry moved by ({legacyCorrection.X:0.###}, " +
                    $"{legacyCorrection.Y:0.###}, {legacyCorrection.Z:0.###})." : ".")
                + (particleSummary.Length > 0 ? $" Imported {particleSummary}." : "")
                + (audioSummary.Length > 0 ? $" Imported {audioSummary}." : ""));
            // M474: stated explicitly because the number is legitimately small and looks like a failure.
            // Measured on two real rooms, only 0.3% (Map10) and 2% (Map8) of source geometry declares a
            // second UV at all - it is the four-blend terrain's mask UV, not a map-wide lightmap unwrap.
            _log.Info("Legacy Port", result.ImportedMeshesWithSecondUv > 0
                ? $"{result.ImportedMeshesWithSecondUv:n0} of {result.ImportedMeshCount:n0} imported mesh(es) carried a "
                  + "SECOND UV set from the source into Texcoord7. The rest is normally correct: legacy rooms "
                  + "only author that channel on four-blend terrain, so this is not a full lightmap unwrap. "
                  + "Use the lightmap layout generator if you need UVs on everything."
                : "No imported mesh had a second UV set — this source authors Texcoord7 nowhere.");
            // M600: record what actually ran, and only now - a cancelled dialog or a port that threw
            // must leave the previous settings alone, or a failed experiment would overwrite the run
            // that worked. Written from the SELECTION rather than the dialog, so a no-dialog replay
            // records the same thing it replayed.
            if (selection is not null && Project is { ProjectFilePath: { } portProjectFile })
            {
                try
                {
                    Project.LegacyPort = LegacyMapPortWindowViewModel.Remember(selection);
                    Core.Projects.ReyProjectService.Save(Project, portProjectFile);
                    _log.Info("Legacy Port", "Port settings remembered - the next port opens on these, "
                        + $"decal planes {(selection.Decals?.GenerateQuads == true ? "ON" : "off")}.");
                }
                catch (Exception ex)
                { _log.Warn("Legacy Port", $"The port succeeded but its settings were not saved: {ex.Message}"); }
            }

            if (TryResolveEntry(mapEntry.PathHash, out var reloaded)) await LoadMapGeoAsync(reloaded);
            Status = "Legacy map port complete.";
        }
        catch (Exception ex) { _log.Error("Legacy Port", ex.Message); Status = "Legacy map port failed."; }
    }

    /// <summary>M141: open a legacy (NVR) map folder — LEVELS/&lt;Map&gt; with Scene/room.nvr — as a
    /// standalone map in the Model Preview window. Reuses the M88/M89 NVR loader (mesh + per-submesh
    /// textures + lights); the preview auto-frames the whole map.</summary>
    [RelayCommand]
    private async Task OpenLegacyMap()
    {
        var folder = await Dialogs.OpenFolderAsync("Open a legacy NVR map folder (e.g. LEVELS/Map10)");
        if (folder is null) return;
        // Accept either the map folder or its Scene subfolder.
        if (!Services.MapPreviewLoader.IsNvrMapFolder(folder))
        {
            var parent = Path.GetDirectoryName(folder.TrimEnd('/', '\\'));
            if (parent is not null && Services.MapPreviewLoader.IsNvrMapFolder(parent)) folder = parent;
            else { _log.Warn("Map", $"No Scene/room.nvr in {folder} — pick a legacy LEVELS/MapN folder."); return; }
        }

        Status = "Loading legacy map…";
        try
        {
            var bg = await Task.Run(() => Services.MapPreviewLoader.Load(folder));
            ForgetPreviewSkin();   // M642: neither has a legacy map
            MeshPreview.Show($"{bg.MapName} (legacy NVR map)", bg.Mesh, skeleton: null, textures: bg.SubmeshTextures);
            // M142.8: a legacy map IS the subject — drop any character-preview backdrop still attached from
            // an earlier skin preview, or both maps render at once (Map8 backdrop behind the Map10 subject).
            MeshPreview.SetBackground(null);
            MeshPreview.Materials = bg.SubmeshMaterials;   // M142: double-sided + alpha cutout + ground flags
            // M148: NVR levels come in two flavours and need different ground + lighting models.
            //   height-blend (Twisted Treeline): BLEND_MAP is the null_black placeholder and the real
            //     ground is a baked composite atlas; its statics carry usable baked vertex lighting.
            //   mask-blend (Dominion) / plain (Map4): a real four-blend BLEND_MAP or no blend at all,
            //     and near-black vertex colours that are mask/AO data — NOT lighting. Applying the
            //     height-blend model to those flattened Dominion's ground and washed out its lighting.
            bool heightBlend = bg.SubmeshLightmap is not null;
            MeshPreview.IsLegacyMap = true;
            MeshPreview.NvrHeightBlend = heightBlend;
            MeshPreview.LightmapTextures = bg.SubmeshLightmap;   // composite atlas (height-blend only)
            MeshPreview.GradientTextures = bg.SubmeshColor1;     // COLOR_MAP_1/2/3 feed both models
            MeshPreview.EmissiveTextures = bg.SubmeshColor2;
            MeshPreview.MatCapTextures = bg.SubmeshColor3;
            // Mask slot: the height-scale map for height-blend, else the four-blend BLEND_MAP.
            MeshPreview.MaskTextures = heightBlend ? bg.SubmeshMask : bg.SubmeshBlend;
            MeshPreview.NvrFourBlend = !heightBlend;
            MeshPreview.UseVertexLightmap = heightBlend;
            MeshPreview.NvrVertexLight = 0;         // M89 default — the baked term is opt-in per map
            MeshPreview.NvrBrightness = 0.55;
            // M149: light it with the level's OWN sun/ambient when it ships one (terrain.inibin / sun.ini).
            MeshPreview.NvrSun = bg.Sun;
            MeshPreview.NvrUseMapSun = true;
            // M142.2: Light.dat loaded but OFF by default — the composite already bakes the light pools
            // in, so the runtime lights double them up. Toggleable later if a map needs them.
            MeshPreview.BackgroundLights = bg.Lights;
            MeshPreview.BackgroundLightsEnabled = false;
            ShowMeshPreviewWindow?.Invoke();
            Status = $"Legacy map {bg.MapName} loaded.";
            _log.Success("Map", $"Legacy NVR map {bg.MapName}: {bg.MeshCount:n0} meshes, {bg.Mesh.VertexCount:n0} verts"
                + (bg.MissingTextures > 0 ? $", {bg.MissingTextures} texture(s) unresolved." : "."));
        }
        catch (Exception ex) { _log.Error("Map", $"Legacy map load failed: {ex.Message}"); Status = "Legacy map load failed."; }
    }

    /// <param name="skinBin">M728: the skin bin that was CHOSEN with this mesh - the character browser's pick, or a
    /// placement's skin. Null only for a bare .skn from the asset tree, where the bin in the mesh's folder is the
    /// best there is. It cannot be worked out from the mesh in general: 11,304 of 14,749 shipped skin bins name a
    /// mesh in another skin's folder, every chroma among them, so the folder rule showed Lillia's skin 49 as 46.</param>
    private async Task LoadMeshPreviewAsync(WadAssetEntry entry, string? skinBin = null)
    {
        if (!ContentLoaded) return;
        _previewSkn = entry;   // M642: what a material edit rebuilds the D3D11 scene for
        // M728: and WHICH skin - every read below goes to this one bin
        string? binPath = _previewSkinBin = SkinPaths.PreviewBinPath(skinBin, entry.IsResolved ? entry.Path : null);
        EnsureCharacterBrowser();   // M643: the window's picker lists the install this skin came from
        try
        {
            var (mesh, skeleton, textures, vfx) = await Task.Run(() =>
            {
                var m = SkinnedMeshDecoder.Decode(ReadAsset(entry.PathHash));
                var s = TryPairSkeleton(entry);
                var t = TryLoadPreviewDiffuse(entry, m, binPath);
                var v = TryLoadChampionVfxWithResources(entry, binPath);   // M55/M86: skin VFX library + resource map
                return (m, s, t, v);
            });
            // M85: game-accurate submesh visibility — skin bin initial-hide + animation-graph clip lists.
            var (initialHide, clipsByAnm, ownAnms, allClips) = LoadSubmeshRules(entry, binPath);
            // M90: clip SFX banks. M728: still found from the mesh's folder, which for Lillia's skin 49 is right -
            // its bankUnits name skin 46's banks.
            await Task.Run(() => LoadChampionAudio(entry));
            // M618: and the D3D11 scene, off the UI thread - it decodes every texture the skin references.
            var dx11 = await Task.Run(() => BuildCharacterDx11Scene(entry, skinBin: binPath));
            // M726: the skin's own idle effects, off the UI thread with everything else.
            var idleEffects = await Task.Run(() => TryLoadIdleEffects(entry, binPath));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // M728: the skin in the header as well as the mesh. A chroma's mesh is its base skin's file, and
                // "Lillia_Skin46.skn" on its own reads exactly like the bug this fixed.
                MeshPreview.Show(skinBin is { Length: > 0 } ? $"{entry.DisplayName} · {Path.GetFileNameWithoutExtension(skinBin)}" : entry.DisplayName,
                    mesh, skeleton, textures.Textures);
                // M664: AFTER Show, which clears Materials. Without this a champion drew every submesh
                // opaque, and a blend-mode-only transparency like Aatrox's wings came out solid black.
                MeshPreview.Materials = textures.Materials;
                MeshPreview.SetSubmeshRules(initialHide, clipsByAnm, allClips);
                MeshPreview.SetAnimations(mesh.CanSkin && skeleton is not null
                    ? FindAnimations(entry, ownAnms)
                    : Enumerable.Empty<AnimationEntryViewModel>());
                MeshPreview.SetVfx(vfx.systems, vfx.resourceMap);
                // M726: AFTER SetVfx - an idle effect resolves through the resource map that call installs.
                MeshPreview.SetIdleEffects(idleEffects);
                // M715: and what the game says each of them is for - the same three-bin walk M713 does for
                // the particle editor, over the champion this window has just loaded.
                MeshPreview.SetVfxRoles(BuildParticleRoles(entry.IsResolved ? entry.Path : null));
                MeshPreview.SetVoiceEvents(TryLoadVoiceEvents(entry, binPath));   // M95c: authored VO lines
                // M663: the FULL clip list, not the by-file one - see LoadSubmeshRules for what the by-file
                // view drops.
                MeshPreview.SetActions(BuildCharacterActions(entry, allClips));   // M612: Q/W/E/R, move, recall
                MeshPreview.SetDx11Scene(dx11.Scene, dx11.Status);                  // M618: Riot's own shaders
                // M619: the D3D11 particle driver takes its shaders from here. Opened lazily with the
                // first scene build, so it is pushed on every load rather than once.
                MeshPreview.Dx11ShaderCache = _dx11ShaderCache;
                MeshPreview.LogDx11 ??= (cat, msg) => _log.Info(cat, msg);   // M625
                MeshInspector.ShowMesh(mesh, skeleton);
                ShowMeshPreviewWindow?.Invoke();
                _log.Success("Mesh", $"{entry.DisplayName}: {mesh.VertexCount:n0} verts, {mesh.TriangleCount:n0} tris — model preview window.");
            });
            _ = ApplyPreviewBackgroundAsync();   // M88: stream in the NVR map backdrop (non-blocking)
        }
        catch (Exception ex) { _log.Error("Mesh", $"{entry.DisplayName}: {ex.Message}"); }
    }

    // M88: cache the last-loaded backdrop so re-previewing skins doesn't re-read the ~60 MB room.nvr.
    private Services.MapPreviewBackground? _previewBackground;
    private string? _previewBackgroundFolder;
    /// <summary>M725: when the cached backdrop's room.nvr was last written. The cache was keyed on the
    /// FOLDER PATH alone and never invalidated, so re-downloading or repairing a pack into the same folder
    /// kept showing the map from before the repair until the app was restarted - and the user's evidence
    /// that the repair worked was precisely that the map would change.</summary>
    private DateTime _previewBackgroundStamp;

    /// <summary>M725: drop the cached backdrop so the next apply reloads it from disk.</summary>
    public void InvalidatePreviewBackground()
    {
        _previewBackground = null;
        _previewBackgroundFolder = null;
        _previewBackgroundStamp = default;
    }

    private static DateTime NvrStampOf(string folder)
    {
        try
        {
            string nvr = Path.Combine(folder, "Scene", "room.nvr");
            return File.Exists(nvr) ? File.GetLastWriteTimeUtc(nvr) : default;
        }
        catch { return default; }
    }

    /// <summary>Load (or reuse) the configured NVR map backdrop and attach it to the preview window.
    /// Silent no-op when the feature is off or the folder isn't a legacy map.</summary>
    private async Task ApplyPreviewBackgroundAsync()
    {
        try
        {
            // M665: an arena IS the backdrop. Streaming the NVR room in here - or clearing it, which is
            // what happens when the feature is off - would delete the map the character is standing on.
            if (MeshPreview.ArenaOwnsBackdrop) return;
            string folder = Settings.PreviewBackgroundMapFolder;
            if (!Settings.PreviewBackgroundEnabled || !Services.MapPreviewLoader.IsNvrMapFolder(folder))
            {
                await Dispatcher.UIThread.InvokeAsync(() => MeshPreview.SetBackground(null));
                return;
            }

            // M725: the folder AND the room's write time - a pack repaired or re-downloaded in place keeps
            // its path, so path-only caching served the old map for the rest of the session.
            var stamp = NvrStampOf(folder);
            if (_previewBackground is null
                || !string.Equals(_previewBackgroundFolder, folder, StringComparison.OrdinalIgnoreCase)
                || _previewBackgroundStamp != stamp)
            {
                _log.Info("Preview", $"Loading map backdrop from {Path.GetFileName(folder)}…");
                var bg = await Task.Run(() => Services.MapPreviewLoader.Load(folder));
                _previewBackground = bg;
                _previewBackgroundFolder = folder;
                _previewBackgroundStamp = stamp;
                _log.Success("Preview", $"Backdrop '{bg.MapName}': {bg.MeshCount:n0} meshes, {bg.Mesh.TriangleCount:n0} tris, {bg.Lights.Count} lights" +
                                        (bg.MissingTextures > 0 ? $" ({bg.MissingTextures} submesh(es) untextured)" : ""));
            }

            var loaded = _previewBackground;
            await Dispatcher.UIThread.InvokeAsync(() => MeshPreview.SetBackground(loaded));
        }
        catch (Exception ex) { _log.Error("Preview", $"Map backdrop: {ex.Message}"); }
    }

    /// <summary>
    /// M726: the loaded skin's own <c>idleParticlesEffects</c>.
    ///
    /// <para>From the ONE bin of the skin being shown, never from the dependency walk. Idle records name
    /// bones, and a champion's dependency closure carries other skins' <c>SkinCharacterDataProperties</c>,
    /// so a walk would mount another skin's effects on this skin's joints. M728: and that bin is the one the
    /// skin was opened with, not the bin in its mesh's folder.</para>
    /// </summary>
    private IReadOnlyList<ReyEngine.Formats.Characters.SkinIdleEffect> TryLoadIdleEffects(WadAssetEntry skn, string? skinBin = null)
    {
        try
        {
            if (!ContentLoaded || !skn.IsResolved) return Array.Empty<ReyEngine.Formats.Characters.SkinIdleEffect>();
            var binPath = SkinPaths.PreviewBinPath(skinBin, skn.Path);
            if (binPath is null || !TryResolveEntry(HashAlgorithms.WadPath(binPath), out var binEntry))
                return Array.Empty<ReyEngine.Formats.Characters.SkinIdleEffect>();
            return ReyEngine.Formats.Characters.SkinIdleEffects.Read(GetAssetBytes(binEntry));
        }
        catch { return Array.Empty<ReyEngine.Formats.Characters.SkinIdleEffect>(); }
    }

    /// <summary>Per-submesh diffuse textures for the model-preview window — NO side effects on the main
    /// viewport's texture/material state (unlike BuildSubmeshTextures, which publishes to it).
    ///
    /// <para>M664: the render state comes back with them, from the one resolve this already did. They were
    /// split only because the preview never asked for the second one.</para></summary>
    private (IReadOnlyList<TextureImage?>? Textures,
             IReadOnlyList<ViewportMeshRenderer.SubmeshMaterial>? Materials)
        TryLoadPreviewDiffuse(WadAssetEntry skn, MeshAsset mesh, string? skinBin = null)
    {
        if (!ContentLoaded || !skn.IsResolved) return (null, null);
        var binPath = SkinPaths.PreviewBinPath(skinBin, skn.Path);   // M728: the chosen skin's textures
        if (binPath is null || !TryResolveEntry(HashAlgorithms.WadPath(binPath), out var binEntry)) return (null, null);
        var resolved = ChampionMaterialResolver.Resolve(GetAssetBytes(binEntry), ResolveBinName, ResolveWadPath);
        // M642: shared with the character editor's live preview
        return (ResolveSubmeshDiffuse(mesh, resolved), ResolveSubmeshMaterials(mesh, resolved));
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

    /// <summary>M124: reload the open map from its current bytes — after Save Map Edits (appended
    /// meshes become native geometry) or a materials change (staged meshes pick up their textures).
    /// Unsaved transforms, layer edits and staged meshes are lost, so it confirms first.</summary>
    [RelayCommand]
    private async Task ReloadMap()
    {
        if (_currentMapEntry is not { } entry) { _log.Warn("MapGeo", "No map open to reload."); return; }
        bool hasEdits = (_currentMap is { } m && (MapGeoWriter.HasMoves(m.Meshes) || MapGeoLayerWriter.HasEdits(m.Meshes)))
                        || MapContent.AddedMeshes.Count > 0;
        if (hasEdits && PromptOwner is not null
            && !await Views.PromptWindow.ConfirmAsync(PromptOwner, "Reload Map",
                "Reload discards unsaved mesh moves, layer edits and staged meshes." + (char)10 + (char)10 + "Save Map Edits first if you want to keep them.", "Reload"))
            return;

        MapContent.AddedMeshes.Clear();
        OnPropertyChanged(nameof(HasAddedMeshes));
        // drop the tab's cached scene so switching tabs can't restore the stale state
        if (Documents.FirstOrDefault(d => d.Key == entry.PathHash) is { } doc) doc.Scene = null;
        _log.Info("MapGeo", $"Reloading {entry.DisplayName}…");
        await LoadMapGeoAsync(entry);
    }

    private async Task LoadMapGeoAsync(WadAssetEntry entry)
    {
        if (!ContentLoaded) return;
        // M515: suppress lighting capture across the WHOLE open, not just across ApplySunProperties.
        //
        // M287 guarded the reset itself, but the load publishes the map's point lights on the way in, and
        // RepublishLights captures — so the record was written from whatever the sun sliders happened to
        // hold at that moment, which is the fallback default, BEFORE the map's own MapSunProperties had
        // been read. Every later open then restored that default over the map's authored sun, and only
        // "Reset to map" (which reads _baseSunAuthored) put it back.
        bool wasApplyingLighting = _applyingLighting;
        _applyingLighting = true;
        try
        {
            _log.Info("MapGeo", $"Decoding {entry.DisplayName} …");
            var rawMapBytes = ReadAsset(entry.PathHash);
            // M445: the scene decoder is LeagueToolkit's and only knows the 40-byte channel block, so a map
            // using a Mantis material has to be normalized for it or it throws IndexOutOfRange. The
            // authoritative bytes (rawMapBytes) are untouched — this only feeds the viewer.
            var extendedChannels = ExtendedChannelMaterialsFor(entry.Path);
            var (map, mesh, textures, sunProperties) = await Task.Run(() =>
            {
                var m = MapGeoDecoder.Decode(rawMapBytes, extendedChannels);
                var meshAsset = new MeshAsset
                {
                    Positions = m.Positions,
                    Normals = m.Normals,
                    Uvs = m.Uvs,
                    Colors = m.Colors,
                    LightmapUvs = m.LightmapUvs,
                    BakedPaintUvs = m.BakedPaintUvs,
                    Indices = m.Indices,
                    VertexCount = m.VertexCount,
                    SubMeshes = m.Groups.Select(g => new SubMeshInfo(g.Material, g.StartIndex, g.IndexCount, 0)).ToList(),
                    BoundsMin = m.BoundsMin,
                    BoundsMax = m.BoundsMax,
                };
                var loaded = TryLoadMapTextures(entry, m);
                return (m, meshAsset, loaded.Textures, loaded.SunProperties);
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentSkeleton = null;
                ShowBones = false;
                CurrentMesh = mesh;
                if (_currentMap is { } replacedMap) UndoService.PurgeContext(replacedMap); // stale transform commands
                _currentMap = map;
                InvalidateRayIndex();
                PrebuildRayIndex(map, MeshVerticesRevision);   // M172a: warm it so the first click is instant
                _currentMapBytes = rawMapBytes;
                _currentMapEntry = entry;
                MapGeneration++;
                HasMapGeo = true;   // M79
                // M446 (D): show the map's OWN placed lights, not just the ones the editor happens to hold.
                ImportPlacedLightsIntoPanel(entry.Path);
                OnPropertyChanged(nameof(CanBakeLighting));   // M158
                OnPropertyChanged(nameof(HasMapForLayout));  // M147
                OnPropertyChanged(nameof(MeshesWithoutLightmapUv));
                OnPropertyChanged(nameof(MeshesWithStrippedLightmap));   // M468
                OnPropertyChanged(nameof(HasStrippedLightmap));
                // M468: say it out loud on load. A map in this state renders perfectly well and looks
                // merely "unlit", so nothing about the picture tells you the terrain's shadows are gone -
                // and they are, because in League a static mesh's shadow IS the lightmap.
                if (MeshesWithStrippedLightmap > 0)
                {
                    _log.Warn("Map", $"{MeshesWithStrippedLightmap} mesh(es) have lightmap UVs but NO lightmap "
                        + "texture bound. Baked light AND baked static shadows are both off for them - Riot's "
                        + "shader reads shadow = min(realtime PCF, lightmap alpha), and static map geometry "
                        + "only ever casts at BAKE time. Character and mob shadows are unaffected; they come "
                        + "from a separate skinned caster pass.");
                    // The half that is actually actionable. Stripping the bake and moving the materials to
                    // the dynamic path are two different actions, and doing only the first leaves the map
                    // sampling a lightmap that no longer exists.
                    int stillBaked = MaterialsStillOnBakedPath;
                    if (stillBaked > 0)
                        _log.Warn("Map", $"{stillBaked} material(s) still compile the BAKED path "
                            + "(no NO_BAKED_LIGHTING) while no lightmap is bound - so this map is not running "
                            + "dynamic lighting, it is running baked lighting with the bake deleted. Use the "
                            + "material lighting-mode buttons to set them Unlit/NO_BAKED_LIGHTING, or re-bake.");
                }
                RefreshMapGraphicsFeatures();   // M434
                _selection.Clear();
                CurrentModelTextures = textures;
                ApplySunProperties(sunProperties);
                // M287: and then put back whatever the user authored for THIS map. ApplySunProperties has
                // just overwritten sun/sky with the map's own values and forced SunIntensity to 1.0, which
                // is correct as a starting point and wrong as a final answer once the project holds edits.
                RestoreMapLighting(entry);
                // M515: from here on the user's edits are their own again.
                _applyingLighting = wasApplyingLighting;
                ClearSecondaryTextures(); // maps don't use champion secondary samplers
                PublishMapMaterialLayers(); // re-apply map special-material layers wiped above
                MapGeoInspector.Show(map, entry.Path);
                MapContent.SetBucketGrids(map.BucketGrids);   // M55: culling grid showcase
                HasBucketGrids = map.BucketGrids.Count > 0;   // M77
                RebuildBucketGridLines();
                LoadNavGridForCurrentMap(entry.Path);         // M564: the OTHER map-open path
                InvalidateMapMaterialNames();   // M516: a different map, a different materials.bin
                MapContent.ShowMap(entry.DisplayName, map.Groups
                    .Select((g, i) => new MapPieceViewModel { Name = string.IsNullOrEmpty(g.Material) ? $"Mesh {i}" : g.Material, Info = $"{g.IndexCount / 3:n0} tris" })
                    .ToList());
                BuildMapVisibility(entry.Path, map);
                BuildMapLayerGroups(map);
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
        // M515: a load that threw before the restore must not leave capture suppressed for the session -
        // every later slider move would then be silently dropped.
        finally { _applyingLighting = wasApplyingLighting; }
    }

    /// <summary>Resolve the map's materials .bin → per-group diffuse textures (shared instances for reuse).</summary>
    private (IReadOnlyList<TextureImage?>? Textures, Formats.MapGeo.MapSunProperties? SunProperties)
        TryLoadMapTextures(WadAssetEntry mapEntry, MapGeoAsset map)
    {
        if (!ContentLoaded || !mapEntry.IsResolved) return (null, null);

        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
        {
            _log.Info("MapGeo", $"No materials .bin found for {mapEntry.DisplayName} — rendering flat.");
            return (null, null);
        }

        // M35: placed particle systems live in the same materials.bin (MapPlaceableContainer.items).
        // M36: the VfxSystemDefinitions they reference live in the same bin too — parse them for playback.
        try
        {
            var binBytes = GetAssetBytes(binEntry);
            _vfxSystems = VfxSystemResolver.ExtractAll(binBytes);
            RebuildRelinkChoices();   // M205: the re-link picker's candidate list
            var particles = MapParticleExtractor.Extract(binBytes, hash =>
                _vfxSystems.TryGetValue(hash, out var system) ? system.ParticlePath : ResolveBinName(hash));
            CurrentModelParticles = particles.Count > 0 ? particles : null;
            if (particles.Count > 0) _log.Info("MapGeo", $"{particles.Count:n0} placed particle system(s) ({particles.Select(p => p.SystemPath).Distinct().Count()} unique, {_vfxSystems.Count} definitions).");

            // M38: cubemap reflection probes + animated props (placed characters) from the same bin.
            // M55: + MapAudio sound placements (Wwise events at world positions).
            var (probes, props, directSounds) = MapPlaceableExtractor.Extract(binBytes, ResolveWadPath);
            var particleSounds = MapParticleAudioExtractor.Extract(particles, _vfxSystems);
            var sounds = directSounds.Concat(particleSounds).ToList();
            CurrentModelProbes = probes.Count > 0 ? probes : null;
            CurrentModelProps = props.Count > 0 ? props : null;
            CurrentModelSounds = sounds.Count > 0 ? sounds : null;
            if (probes.Count > 0 || props.Count > 0 || sounds.Count > 0)
                _log.Info("MapGeo", $"{probes.Count} cubemap probe(s), {props.Count} animated prop(s) ({props.Select(p => p.CharacterName).Distinct().Count()} characters), {sounds.Count} sound placement(s).");
            LoadMapAudioBanks(binEntry.Path, sounds);   // M56/M60: direct MapAudio + VFX-carried map ambience
        }
        catch { CurrentModelParticles = null; _vfxSystems = EmptyVfx; CurrentModelProbes = null; CurrentModelProps = null; CurrentModelSounds = null; }

        var names = map.Groups.Select(g => g.Material).Where(m => m.Length > 0).Distinct().ToList();
        var (materialToTexture, profiles, sunProperties) = ResolveMapMaterials(binEntry, names);
        if (materialToTexture.Count == 0)
        {
            _log.Info("MapGeo", "Materials .bin didn't resolve any textures — rendering flat.");
            return (null, sunProperties);
        }
        _currentMaterialToTexture = materialToTexture;   // M172c: the paint session needs per-submesh paths
        return (BuildMapTextures(map, materialToTexture, profiles, names.Count, mapEntry.Path), sunProperties);
    }

    /// <summary>Resolve map material→texture (+ M32 profiles), falling back to the original game
    /// .materials.bin when the project's copy is broken (malformed .bin) or resolves nothing.</summary>
    private (Dictionary<string, string> textures, Dictionary<string, MaterialProfile> profiles,
        Formats.MapGeo.MapSunProperties? sunProperties) ResolveMapMaterials(WadAssetEntry binEntry, List<string> names)
    {
        try
        {
            var bytes = GetAssetBytes(binEntry);
            var r = MapGeoMaterialResolver.Resolve(bytes, names, ResolveWadPath);
            if (r.Count > 0)
            {
                return (r, MaterialProfiles.ForMapMaterials(bytes, names, ResolveBinName, ResolveWadPath),
                    Formats.MapGeo.MapLighting.EffectiveSun(bytes));
            }
        }
        catch (Exception ex) { _log.Warn("MapGeo", $"project materials.bin parse failed: {ex.Message}"); }

        var fb = _mounts?.ReadFallback(binEntry.PathHash);
        if (fb is not null)
        {
            try
            {
                var r = MapGeoMaterialResolver.Resolve(fb, names, ResolveWadPath);
                if (r.Count > 0)
                {
                    _log.Info("MapGeo", "Used the original game materials.bin (the project's copy was broken/empty).");
                    return (r, MaterialProfiles.ForMapMaterials(fb, names, ResolveBinName, ResolveWadPath),
                        Formats.MapGeo.MapLighting.EffectiveSun(fb));
                }
            }
            catch (Exception ex) { _log.Warn("MapGeo", $"game materials.bin parse failed: {ex.Message}"); }
        }
        return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, MaterialProfile>(StringComparer.OrdinalIgnoreCase), null);
    }

    /// <summary>M45: read the MapContainer's MapSunProperties component and publish what the renderer uses
    /// (lightMapColorScale — the game's baked-light multiplier, e.g. 2.0 on Map12 Bloom).</summary>
    private void ApplySunProperties(Formats.MapGeo.MapSunProperties? sun)
    {
        _baseSunAuthored = sun;   // M71: remembered so "Reset lighting" can restore the map's authored values
        // M71: keep the authored sun (direction + any HDR fields) as the base; the manual sliders replace only
        // colour/scale on top of it. When the map has no sun component, fall back to the renderer's own
        // defaults (dir/0.75 sun/0.35 sky) so nothing changes visually until the user touches a slider.
        _baseSun = sun ?? new MapSunProperties
        {
            SunDirection = new System.Numerics.Vector3(0.4f, 0.85f, 0.45f),
            SunColor = new System.Numerics.Vector4(0.75f, 0.75f, 0.75f, 1f),
            SkyLightColor = new System.Numerics.Vector4(0.35f, 0.35f, 0.35f, 1f),
            SkyLightScale = 1f,
        };
        // M287: this whole block RESETS the panel to the map's authored values. That is correct as a
        // starting point, but it must not be mistaken for something the user chose - so capture is
        // suppressed across it, or opening a map would immediately overwrite that map's saved record with
        // the reset and the restore below it would have nothing left to restore.
        _applyingLighting = true;
        try
        {
            _suppressSunRebuild = true;
            SunColorR = Clamp01(_baseSun.SunColor.X); SunColorG = Clamp01(_baseSun.SunColor.Y); SunColorB = Clamp01(_baseSun.SunColor.Z);
            // M451: the slider IS SunIntensityScale now. Resetting it to 1.0 was what made the viewport
            // ignore the strength the game applies (jade authors 0.5 - a 2x mismatch), and what made a
            // save report "stays at 1".
            SunIntensity = System.Math.Clamp(_baseSun.SunIntensityScale, 0f, 8f);
            SkyColorR = Clamp01(_baseSun.SkyLightColor.X); SkyColorG = Clamp01(_baseSun.SkyLightColor.Y); SkyColorB = Clamp01(_baseSun.SkyLightColor.Z);
            SkyIntensity = System.Math.Clamp(_baseSun.SkyLightScale, 0f, 8f);

            // M463: the six fields the panel could not previously reach. NOT Clamp01 for the direction -
            // Riot ships non-unit sun vectors and clamping one would corrupt it on the next save.
            SunDirX = _baseSun.SunDirection.X; SunDirY = _baseSun.SunDirection.Y; SunDirZ = _baseSun.SunDirection.Z;
            HorizonColorR = Clamp01(_baseSun.HorizonColor.X); HorizonColorG = Clamp01(_baseSun.HorizonColor.Y); HorizonColorB = Clamp01(_baseSun.HorizonColor.Z);
            GroundColorR = Clamp01(_baseSun.GroundColor.X); GroundColorG = Clamp01(_baseSun.GroundColor.Y); GroundColorB = Clamp01(_baseSun.GroundColor.Z);
            FogColorR = Clamp01(_baseSun.FogColor.X); FogColorG = Clamp01(_baseSun.FogColor.Y); FogColorB = Clamp01(_baseSun.FogColor.Z);
            FogStartRaw = _baseSun.FogStartAndEnd.X; FogEndRaw = _baseSun.FogStartAndEnd.Y;
            // M759
            MapFogEnabled = _baseSun.FogEnabled;
            FogAltColorR = Clamp01(_baseSun.FogAlternateColor.X); FogAltColorG = Clamp01(_baseSun.FogAlternateColor.Y); FogAltColorB = Clamp01(_baseSun.FogAlternateColor.Z);
            FogEmissiveRemap = _baseSun.FogEmissiveRemap;
            FogLowQualityEmissiveRemap = _baseSun.FogLowQualityModeEmissiveRemap;

            // M467: the shadow four. Loaded here so the panel shows what the MAP authors rather than the
            // schema default - the difference between the two is the whole finding on Summoner's Rift,
            // which authors none of them and therefore runs SunRadiusForShadows at 0.
            SunRadiusForShadows = _baseSun.SunRadiusForShadows;
            ScaleSunShadowIntensity = _baseSun.ScaleSunShadowIntensity;
            SunShadowBias = _baseSun.ShadowBias;
            SurfaceAreaToShadowMapScale = _baseSun.SurfaceAreaToShadowMapScale;

            _suppressSunRebuild = false;
            RebuildSun();
            CurrentLightmapScale = sun?.LightMapColorScale ?? 1.0;
        }
        finally { _applyingLighting = false; }
        if (sun is not null)
            _log.Info("Map", $"MapSunProperties: lightMapColorScale={sun.LightMapColorScale:0.##}, " +
                             $"skyLightScale={sun.SkyLightScale:0.##}, sunColor=({sun.SunColor.X:0.##}, {sun.SunColor.Y:0.##}, {sun.SunColor.Z:0.##}), " +
                             $"fog {sun.FogStartAndEnd.X:0}..{sun.FogStartAndEnd.Y:0}");
    }

    // M71: base sun (map-authored or default); the sliders replace colour/scale on top of it.
    private MapSunProperties _baseSun = new()
    {
        SunDirection = new System.Numerics.Vector3(0.4f, 0.85f, 0.45f),
        SunColor = new System.Numerics.Vector4(0.75f, 0.75f, 0.75f, 1f),
        SkyLightColor = new System.Numerics.Vector4(0.35f, 0.35f, 0.35f, 1f),
        SkyLightScale = 1f,
        // M759: explicit, now that the record's own defaults are the schema's (fog on, 0..-2000). With no
        // map there is no fog, and 0..0 is part of the signature MapLightingArtefact recognises.
        FogEnabled = false,
        FogStartAndEnd = System.Numerics.Vector2.Zero,
    };
    private bool _suppressSunRebuild;
    private static double Clamp01(double v) => System.Math.Clamp(v, 0.0, 1.0);

    /// <summary>M71: fold the manual sun/sky sliders into CurrentSunProperties (bound to the viewport). Sun
    /// colour is scaled by its intensity; sky scale carries the sky intensity — exactly the two knobs the
    /// renderer's fallback term uses (col = base * encode(sky + sun * NdotL)).</summary>
    private void RebuildSun()
    {
        if (_suppressSunRebuild) return;
        CurrentSunProperties = _baseSun with
        {
            // M451: this is the RENDER form - strength folded into the colour, scale normalised to 1 so
            // nothing can ever apply it twice. The SAVE form keeps them split (hue + SunIntensityScale),
            // which is how Riot authors it (jade: sunColor 0.87/0.75/0.6 with SunIntensityScale 0.5).
            SunColor = new System.Numerics.Vector4((float)(SunColorR * SunIntensity), (float)(SunColorG * SunIntensity), (float)(SunColorB * SunIntensity), 1f),
            SunIntensityScale = 1f,
            SkyLightColor = new System.Numerics.Vector4((float)SkyColorR, (float)SkyColorG, (float)SkyColorB, 1f),
            SkyLightScale = (float)SkyIntensity,

            // M463: the remaining six. They travel in the RENDER form whether or not this viewport can show
            // them, because CurrentSunProperties is also what the GL viewport and the per-map project record
            // read - a field dropped here would be a field the panel appears to edit and nothing remembers.
            //
            // Which of them a rendered frame actually reflects, measured rather than assumed:
            //   SunDirection    D3D11 SUN_LIGHT_DIRECTION + LightRegionInfo+32, and GL's own sun. VISIBLE.
            //   FogColor        D3D11 ENV_FOG_COLOR / ENV_FOG_ALT_COLOR, when the Fog toggle is on. VISIBLE.
            //   FogStartAndEnd  D3D11 ENV_FOG_START_END_SCALE_EMISSIVE_REMAP, likewise. VISIBLE.
            //   HorizonColor    SAVE-ONLY. Nothing in either viewport reads it: the sky is drawn from a
            //   GroundColor     cubemap/equirect/mesh TEXTURE (ShaderPreviewRenderer.Sky.cs), not from a
            //                   two-colour gradient, and no shader constant in PerFramePixelCB carries one.
            SunDirection = new System.Numerics.Vector3((float)SunDirX, (float)SunDirY, (float)SunDirZ),
            HorizonColor = new System.Numerics.Vector4((float)HorizonColorR, (float)HorizonColorG, (float)HorizonColorB, 1f),
            GroundColor = new System.Numerics.Vector4((float)GroundColorR, (float)GroundColorG, (float)GroundColorB, 1f),
            FogColor = new System.Numerics.Vector4((float)FogColorR, (float)FogColorG, (float)FogColorB, 1f),
            FogStartAndEnd = new System.Numerics.Vector2((float)FogStartRaw, (float)FogEndRaw),
            // M759
            FogEnabled = MapFogEnabled,
            FogAlternateColor = new System.Numerics.Vector4((float)FogAltColorR, (float)FogAltColorG, (float)FogAltColorB, 1f),
            FogEmissiveRemap = (float)FogEmissiveRemap,
            FogLowQualityModeEmissiveRemap = (float)FogLowQualityEmissiveRemap,
        };
        OnPropertyChanged(nameof(FogAltSwatch));
        OnPropertyChanged(nameof(FogAltColorPick));
        OnPropertyChanged(nameof(SunSwatch));
        OnPropertyChanged(nameof(SkySwatch));
        OnPropertyChanged(nameof(SunColorPick));   // M155
        OnPropertyChanged(nameof(SkyColorPick));
        OnPropertyChanged(nameof(HorizonSwatch));  // M463
        OnPropertyChanged(nameof(GroundSwatch));
        OnPropertyChanged(nameof(FogSwatch));
        OnPropertyChanged(nameof(HorizonColorPick));
        OnPropertyChanged(nameof(GroundColorPick));
        OnPropertyChanged(nameof(FogColorPick));
        // HasMapFog is NOT re-raised here: CurrentSunProperties is a record, so assigning a new one with a
        // different fog range already trips OnCurrentSunPropertiesChanged, which re-raises it.
        // M287: the sun/sky sliders all funnel through here, so one capture covers the panel.
        CaptureMapLighting();
    }

    public Avalonia.Media.IBrush SunSwatch => Swatch(SunColorR * SunIntensity, SunColorG * SunIntensity, SunColorB * SunIntensity);
    public Avalonia.Media.IBrush SkySwatch => Swatch(SkyColorR * SkyIntensity, SkyColorG * SkyIntensity, SkyColorB * SkyIntensity);

    /// <summary>M155: sun/sky colour as a real Color so the lighting panel can use the picker instead of
    /// three sliders. These are the UNSCALED hues - the Intensity sliders stay separate, which is what
    /// makes a colour picker usable here (picking a hue shouldn't also change the brightness).
    ///
    /// <para>M464: every one of these setters used to suppress the rebuild for R and G and rely on the B
    /// assignment to fire it. [ObservableProperty] only raises OnChanged when the value actually CHANGES,
    /// so picking any colour whose blue happened to match the current one wrote R and G under suppression
    /// and then fired nothing: the swatch, the viewport and the save form all kept the old colour while the
    /// picker showed the new one. That is the "colour sometimes not changing" bug, and it got one-in-256
    /// worse for every channel that happened to line up. Suppress all three, then rebuild unconditionally -
    /// the trigger no longer depends on which channels the user happened to move.</para></summary>
    public Avalonia.Media.Color SunColorPick
    {
        get => Col(SunColorR, SunColorG, SunColorB);
        set
        {
            _suppressSunRebuild = true;
            SunColorR = value.R / 255.0; SunColorG = value.G / 255.0; SunColorB = value.B / 255.0;
            _suppressSunRebuild = false;
            RebuildSun();   // unconditional: an unchanged channel must not swallow the edit
        }
    }

    public Avalonia.Media.Color SkyColorPick
    {
        get => Col(SkyColorR, SkyColorG, SkyColorB);
        set
        {
            _suppressSunRebuild = true;
            SkyColorR = value.R / 255.0; SkyColorG = value.G / 255.0; SkyColorB = value.B / 255.0;
            _suppressSunRebuild = false;
            RebuildSun();   // unconditional: an unchanged channel must not swallow the edit
        }
    }

    // M463: the three added pickers. One edit still fires exactly one RebuildSun - see the M464 note above.
    public Avalonia.Media.Color HorizonColorPick
    {
        get => Col(HorizonColorR, HorizonColorG, HorizonColorB);
        set
        {
            _suppressSunRebuild = true;
            HorizonColorR = value.R / 255.0; HorizonColorG = value.G / 255.0; HorizonColorB = value.B / 255.0;
            _suppressSunRebuild = false;
            RebuildSun();   // unconditional: an unchanged channel must not swallow the edit
        }
    }

    public Avalonia.Media.Color GroundColorPick
    {
        get => Col(GroundColorR, GroundColorG, GroundColorB);
        set
        {
            _suppressSunRebuild = true;
            GroundColorR = value.R / 255.0; GroundColorG = value.G / 255.0; GroundColorB = value.B / 255.0;
            _suppressSunRebuild = false;
            RebuildSun();   // unconditional: an unchanged channel must not swallow the edit
        }
    }

    public Avalonia.Media.Color FogColorPick
    {
        get => Col(FogColorR, FogColorG, FogColorB);
        set
        {
            _suppressSunRebuild = true;
            FogColorR = value.R / 255.0; FogColorG = value.G / 255.0; FogColorB = value.B / 255.0;
            _suppressSunRebuild = false;
            RebuildSun();   // unconditional: an unchanged channel must not swallow the edit
        }
    }

    public Avalonia.Media.IBrush HorizonSwatch => Swatch(HorizonColorR, HorizonColorG, HorizonColorB);
    public Avalonia.Media.IBrush GroundSwatch => Swatch(GroundColorR, GroundColorG, GroundColorB);
    public Avalonia.Media.IBrush FogSwatch => Swatch(FogColorR, FogColorG, FogColorB);
    public Avalonia.Media.IBrush FogAltSwatch => Swatch(FogAltColorR, FogAltColorG, FogAltColorB);   // M759

    public Avalonia.Media.Color FogAltColorPick
    {
        get => Col(FogAltColorR, FogAltColorG, FogAltColorB);
        set
        {
            _suppressSunRebuild = true;
            FogAltColorR = value.R / 255.0; FogAltColorG = value.G / 255.0; FogAltColorB = value.B / 255.0;
            _suppressSunRebuild = false;
            RebuildSun();
        }
    }

    private static Avalonia.Media.Color Col(double r, double g, double b) => Avalonia.Media.Color.FromRgb(
        (byte)Math.Clamp(Math.Round(r * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(g * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(b * 255), 0, 255));
    private static Avalonia.Media.IBrush Swatch(double r, double g, double b) =>
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(B(r), B(g), B(b)));
    private static byte B(double v) => (byte)System.Math.Clamp(v * 255.0, 0, 255);

    partial void OnSunIntensityChanged(double value) => RebuildSun();
    partial void OnSunColorRChanged(double value) => RebuildSun();
    partial void OnSunColorGChanged(double value) => RebuildSun();
    partial void OnSunColorBChanged(double value) => RebuildSun();
    partial void OnSkyIntensityChanged(double value) => RebuildSun();
    partial void OnSkyColorRChanged(double value) => RebuildSun();
    partial void OnSkyColorGChanged(double value) => RebuildSun();
    partial void OnSkyColorBChanged(double value) => RebuildSun();

    // M463: every added field funnels through the SAME republish, which is what makes it reach the viewport
    // and the project record. A property added without one of these binds fine, shows fine and does nothing
    // - the exact failure this milestone exists to fix.
    partial void OnSunDirXChanged(double value) => RebuildSun();
    partial void OnSunDirYChanged(double value) => RebuildSun();
    partial void OnSunDirZChanged(double value) => RebuildSun();
    partial void OnHorizonColorRChanged(double value) => RebuildSun();
    partial void OnHorizonColorGChanged(double value) => RebuildSun();
    partial void OnHorizonColorBChanged(double value) => RebuildSun();
    partial void OnGroundColorRChanged(double value) => RebuildSun();
    partial void OnGroundColorGChanged(double value) => RebuildSun();
    partial void OnGroundColorBChanged(double value) => RebuildSun();
    partial void OnFogColorRChanged(double value) => RebuildSun();
    partial void OnFogColorGChanged(double value) => RebuildSun();
    partial void OnFogColorBChanged(double value) => RebuildSun();
    partial void OnFogStartRawChanged(double value) => RebuildSun();
    partial void OnFogEndRawChanged(double value) => RebuildSun();
    partial void OnMapFogEnabledChanged(bool value) => RebuildSun();          // M759
    partial void OnFogAltColorRChanged(double value) => RebuildSun();
    partial void OnFogAltColorGChanged(double value) => RebuildSun();
    partial void OnFogAltColorBChanged(double value) => RebuildSun();
    partial void OnFogEmissiveRemapChanged(double value) => RebuildSun();
    partial void OnFogLowQualityEmissiveRemapChanged(double value) => RebuildSun();

    /// <summary>M71: restore sun/sky/lightmap to the loaded map's authored values.</summary>
    [RelayCommand]
    private void ResetLighting() => ApplySunProperties(_baseSunAuthored);
    private Formats.MapGeo.MapSunProperties? _baseSunAuthored;

    /// <summary>
    /// M450: persist the panel's sun/sky/fog into the map's materials.bin — the missing half of the
    /// lighting panel. Until now the sliders edited a copy the viewport rendered and nothing could save;
    /// "not saveable" was literally true, there was no writer.
    ///
    /// <para>Builds the SAVE form from <see cref="_baseSun"/> plus the raw panel values — NOT from
    /// <see cref="CurrentSunProperties"/>, which is the folded RENDER form. M451 split the two and this
    /// comment described the pre-M451 behaviour until M463 corrected it.</para>
    ///
    /// <para>M463: the panel now edits all ten of the record's fields, so all ten are written from it.
    /// Fields the record itself does not model (fogAlternateColor, CharacterSunLight*, …) are still left
    /// untouched by <see cref="Formats.MapGeo.MapSunProperties.Write"/>.</para>
    /// </summary>
    [RelayCommand]
    private async Task SaveSunToMap()
    {
        if (_currentMapEntry is not { } entry)
        { _log.Warn("Lighting", "No map is open, so there is nowhere to save the sun."); return; }
        if (!TryResolveMaterialsBin(entry.Path, out var binEntry))
        { _log.Error("Lighting", "No materials.bin was found alongside this mapgeo."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            // M451: the SAVE form splits colour and strength the way Riot authors them - sunColor is the
            // plain hue and SunIntensityScale carries the slider. Saving the folded render form instead
            // would zero out an authored scale and bake the strength into the hue, which is exactly the
            // lossy fold that made saved strength "stay at 1".
            var sun = _baseSun with
            {
                SunColor = new System.Numerics.Vector4((float)SunColorR, (float)SunColorG, (float)SunColorB, 1f),
                SunIntensityScale = (float)SunIntensity,
                SkyLightColor = new System.Numerics.Vector4((float)SkyColorR, (float)SkyColorG, (float)SkyColorB, 1f),
                SkyLightScale = (float)SkyIntensity,
                LightMapColorScale = (float)CurrentLightmapScale,

                // M463: the six fields the panel gained. Written from the panel values rather than left on
                // _baseSun, or the new controls would edit the viewport and be silently discarded on save -
                // which is the same class of defect as a control that edits nothing at all.
                SunDirection = new System.Numerics.Vector3((float)SunDirX, (float)SunDirY, (float)SunDirZ),
                HorizonColor = new System.Numerics.Vector4((float)HorizonColorR, (float)HorizonColorG, (float)HorizonColorB, 1f),
                GroundColor = new System.Numerics.Vector4((float)GroundColorR, (float)GroundColorG, (float)GroundColorB, 1f),
                FogColor = new System.Numerics.Vector4((float)FogColorR, (float)FogColorG, (float)FogColorB, 1f),
                FogStartAndEnd = new System.Numerics.Vector2((float)FogStartRaw, (float)FogEndRaw),
                // M759: the rest of the fog, on the same rule
                FogEnabled = MapFogEnabled,
                FogAlternateColor = new System.Numerics.Vector4((float)FogAltColorR, (float)FogAltColorG, (float)FogAltColorB, 1f),
                FogEmissiveRemap = (float)FogEmissiveRemap,
                FogLowQualityModeEmissiveRemap = (float)FogLowQualityEmissiveRemap,

                // M467: the shadow four, on the same rule as M463's six — written from the PANEL, because a
                // control that edits the viewport and is dropped on save is the same defect as a control
                // that does nothing. MapSunProperties.Write only ADDS a field whose value differs from the
                // schema default, so leaving these alone still produces a byte-identical bin.
                SunRadiusForShadows = (float)SunRadiusForShadows,
                ScaleSunShadowIntensity = (float)ScaleSunShadowIntensity,
                ShadowBias = (float)SunShadowBias,
                SurfaceAreaToShadowMapScale = (float)SurfaceAreaToShadowMapScale,
            };
            byte[] source = ReadAsset(binEntry.PathHash);
            var (bytes, result) = await Task.Run(() =>
            {
                var b = Formats.MapGeo.MapSunProperties.Write(source, sun, out var r);
                return (b, r);
            });
            if (bytes is null)
            { _log.Error("Lighting", "Sun could not be written: " + result.Detail); return; }

            // Validate BEFORE saving: shape rules, then read the sun back and require exact agreement.
            var issues = await Task.Run(() => Formats.Meta.ModShapeValidator.ValidateBin(
                Formats.Meta.SafeBinTree.Parse(bytes), bytes, ResolveBinName));
            if (issues.Count > 0)
            {
                foreach (var i in issues.Take(5)) _log.Error("Lighting", $"[{i.Category}] {i.ObjectName}: {i.Detail}");
                _log.Error("Lighting", $"{issues.Count} shape issue(s) — not saved."); return;
            }
            var back = Formats.MapGeo.MapSunProperties.Extract(bytes);
            if (back is null || back != sun)
            { _log.Error("Lighting", "The rewritten bin did not read back with the saved sun — not saved."); return; }

            string savedTo;
            if (TryWriteToProjectFile(binEntry, bytes, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, bytes, ".bin");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = binEntry.PathHash,
                    ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            // The saved values are now the map's authored values, so Reset should return HERE.
            _baseSunAuthored = sun;
            _baseSun = sun;

            _log.Success("Lighting", $"Saved sun & sky to the map ({result.Detail}). "
                + $"sunColor=({sun.SunColor.X:0.##}, {sun.SunColor.Y:0.##}, {sun.SunColor.Z:0.##}), "
                + $"skyScale={sun.SkyLightScale:0.##}, lightMapColorScale={sun.LightMapColorScale:0.##}. "
                + $"Saved to {savedTo}.");
        }
        catch (Exception ex) { _log.Error("Lighting", "Sun could not be saved: " + ex.Message); }
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
    /// Also publishes the per-group preview materials (UV transform + specular flag) from the profiles (M32).
    ///
    /// <para>M665: the resolving itself now lives in <see cref="Services.MapSubmeshResources"/>, which owns
    /// no state. What is left here is what was always view-model business: publishing the result into the
    /// main window's map fields, the grass tint, and the log line.</para></summary>
    private IReadOnlyList<TextureImage?> BuildMapTextures(MapGeoAsset map, Dictionary<string, string> materialToTexture,
        Dictionary<string, MaterialProfile> profilesByName, int materialCount, string? mapGeoPath)
    {
        _currentMapProfiles = profilesByName; // M34: cache for the mesh inspector's render-state rows

        var res = Services.MapSubmeshResources.Build(map, materialToTexture, profilesByName, mapGeoPath,
            LoadTextureByPath,
            onFlowGroup: (matName, prof, flowMap, flowNormal) =>
            {
                // M44 diagnostic: confirm detection + texture loads for the first few. Channel histogram of
                // the flow map (B = water mask, R = phase, G = flow) so the shader's channel mapping can be
                // sanity-checked against the real texture values.
                string gstat = "";
                if (flowMap is { } fmImg && fmImg.Rgba.Length >= 4)
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
                _log.Info("Water", $"flowmap '{matName}': flowMap={(flowMap is not null ? "OK" : "miss")} " +
                                   $"normal={(flowNormal is not null ? "OK" : "miss")} " +
                                   $"speed={prof.FlowSpeed:0.###} alpha={prof.WaterAlpha:0.##}{gstat}");
            });

        // Only the materials a group actually uses, which is what the loop this replaced logged.
        foreach (var g in map.Groups)
            if (profilesByName.TryGetValue(g.Material, out var gp)) LogUvTransform(gp, g.Material);

        CurrentModelSubmeshMaterials = res.Materials;
        CurrentModelLightmapTextures = res.HasLightmaps ? res.Lightmaps : null;

        // M78: any VertexDeform+USE_GRASS_TINT_MAP group → publish the map's world-space grass tint.
        int gtGroups = res.Materials.Count(m => m.UsesGrassTint);
        if (gtGroups > 0)
        {
            var gtPath = FindGrassTintTexturePath();
            CurrentGrassTint = gtPath is not null ? LoadTextureByPath(gtPath) : null;
            // M365b: the terrain-blend canvas, not a second one derived from the whole scene.
            //
            // This used to be (BoundsMin.X, BoundsMin.Z, 1/spanX, 1/spanZ), which was wrong three ways:
            // mapgeo bounds include sky domes and out-of-bounds decor rather than the playable canvas, the
            // X and Z spans were applied independently so a non-square bounding box STRETCHED the tint, and
            // it missed the edge snapping that recovers clean authored extents. TerrainBlendWorldTransformFor
            // was already measured and verified for the terrain mask (M322) and is square by construction;
            // D3D11 feeds the very same numbers into TERRAIN_XFORM, so the two viewports now place the tint
            // identically instead of merely being wrong in the same direction.
            //
            // Repacked, not reinterpreted: the transform is uv = world.xz * scale + bias, and this shader
            // wants uv = (world.xz - origin) * scale, so origin = -bias / scale.
            float gtScale = res.TerrainWorldTransform.X != 0f ? res.TerrainWorldTransform.X : 1f / 16000f;
            float gtOrigin = -res.TerrainWorldTransform.Z / gtScale;
            CurrentGrassTintRect = new System.Numerics.Vector4(gtOrigin, gtOrigin, gtScale, gtScale);
            _log.Info("GrassTint", gtPath is not null
                ? $"{gtGroups} grass-tint group(s) — {gtPath} — canvas origin {gtOrigin:0.#}, "
                  + $"extent {(gtScale > 0f ? 1f / gtScale : 0f):0.#}"
                : $"{gtGroups} grass-tint group(s), but no grasstint texture found in the mounts.");
        }
        else CurrentGrassTint = null;
        // Stash map-only secondary layers. A later ClearSecondaryTextures() on the load path wipes the channels,
        // so the UI-thread load code republishes them from these fields.
        _mapFlowMasks = res.HasFlowOrTerrain ? res.FlowMasks : null;
        _mapFlowGrads = res.HasFlowOrTerrain ? res.FlowGradients : null;
        _mapTerrainTops = res.TerrainGroups > 0 ? res.TerrainTops : null;
        _mapTerrainExtras = res.TerrainGroups > 0 ? res.TerrainExtras : null;
        PublishMapMaterialLayers();

        int spec = res.Materials.Count(m => m.UsesSpecular);
        _log.Success("MapGeo", $"Loaded {res.UniqueTextures} unique textures ({materialToTexture.Count}/{materialCount} materials resolved)" +
                               (spec > 0 ? $", {spec} group(s) with specular." : ".") +
                               (res.LightmapGroups > 0 ? $" {res.LightmapGroups} group(s) with baked lightmaps." : "") +
                               (res.FlowGroups > 0 ? $" {res.FlowGroups} flowmap-water group(s)." : "") +
                               (res.TerrainGroups > 0 ? $" {res.TerrainGroups} terrain-blend group(s)." : "") +
                               (res.BakedPaintGroups > 0 ? $" {res.BakedPaintGroups} baked-terrain group(s)." : ""));
        return res.Diffuse;
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
        var resolved = ChampionMaterialResolver.Resolve(GetAssetBytes(binEntry), ResolveBinName, ResolveWadPath);
        if (!resolved.HasAny)
        {
            _log.Info("Material", $"No skin material found for {skn.DisplayName} (flat shading).");
            return null;
        }
        return BuildSubmeshTextures(mesh, resolved, skn.DisplayName);
    }

    /// <summary>Parse the champion skin's VFX library from its .bin (M37). Empty when there's no skin bin.</summary>
    private IReadOnlyDictionary<uint, VfxSystemDefinition> TryLoadChampionVfx(WadAssetEntry skn)
        => TryLoadChampionVfxWithResources(skn).systems;

    // ---- M90: champion SFX for the model preview (clip SoundEventData -> Wwise banks) ----
    private Formats.Audio.AudioBankSet? _previewAudioBanks;

    /// <summary>Load the champion's SFX banks (base + the previewed skin's own folder when it has one):
    /// sounds/wwise2016/sfx/characters/&lt;champ&gt;/skins/&lt;base|skinNN&gt;/*.bnk|.wpk.</summary>
    private void LoadChampionAudio(WadAssetEntry skn)
    {
        _previewAudioBanks = null;
        try
        {
            if (!skn.IsResolved) return;
            var parts = skn.Path.Split('/');
            int ci = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
            if (ci < 0 || ci + 1 >= parts.Length) return;
            string champ = parts[ci + 1];
            // the skn's skin folder (skin03) — its banks override/extend base for newer skins
            string skinFolder = parts.FirstOrDefault(p => p.StartsWith("skin", StringComparison.OrdinalIgnoreCase)) ?? "";
            string marker = $"/sfx/characters/{champ}/skins/";
            const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

            var set = new Formats.Audio.AudioBankSet();
            int banks = 0, packs = 0;
            foreach (var e in AssetEntries)
            {
                if (!e.IsResolved) continue;
                var p = e.Path;
                int mi = p.IndexOf(marker, OIC);
                if (mi < 0) continue;
                string folder = p[(mi + marker.Length)..].Split('/')[0];
                if (!folder.Equals("base", OIC) && !folder.Equals(skinFolder, OIC)) continue;
                try
                {
                    if (p.EndsWith(".bnk", OIC))
                    { if (Formats.Audio.BnkFile.Parse(ReadAsset(e.PathHash)) is { } b) { set.AddBank(b, e.PathHash, p); banks++; } }
                    else if (p.EndsWith(".wpk", OIC))
                    { if (Formats.Audio.WpkFile.Parse(ReadAsset(e.PathHash)) is { } w) { set.AddPack(w, e.PathHash, p); packs++; } }
                }
                catch { /* skip broken banks */ }
            }
            // M95b: projects usually don't mount the champion's WAD at all (map projects, folder
            // projects), so the mount scan above finds nothing — fall back to the ORIGINAL
            // Champions/<Champ>.wad.client in the game install, like the mesh/texture fallback does.
            if (banks + packs == 0 && FindChampionWad(champ, locale: null) is { } mainWad)
            {
                int n = LoadBanksFromWadFile(set, mainWad, champ, skinFolder);
                if (n > 0) { banks += n; _log.Info("Audio", $"{champ}: SFX banks read from the original game WAD (not in project mounts)."); }
            }

            // M95: voice-over lives in the champion's LOCALE WAD (Aatrox.en_US.wad.client), which is
            // never mounted — open it directly from the game install and merge its VO banks so
            // Play_vo_ clip events (jokes, taunts, laughs) speak like in-game.
            int voBanks = FindChampionWad(champ, locale: "*") is { } voWad
                ? LoadBanksFromWadFile(set, voWad, champ, skinFolder) : 0;

            if (!set.IsEmpty)
            {
                _previewAudioBanks = set;
                _log.Info("Audio", $"{champ} SFX: {banks} bank(s) + {packs} pack(s)" +
                    (voBanks > 0 ? $", VO: {voBanks} bank(s)" : "") +
                    $" — {set.EventCount} event(s), {set.WemCount} wem(s).");
            }
        }
        catch { /* audio is optional */ }
    }

    /// <summary>M95c: the skin bin's authored VO event names (skinAudioProperties.bankUnits) — voice
    /// lines are triggered by game logic through these, never by animation clip events.</summary>
    private IReadOnlyList<string> TryLoadVoiceEvents(WadAssetEntry skn, string? skinBin = null)
    {
        try
        {
            if (!skn.IsResolved) return Array.Empty<string>();
            var binPath = SkinPaths.PreviewBinPath(skinBin, skn.Path);   // M728: the chosen skin's own bankUnits
            if (binPath is null || !TryResolveEntry(HashAlgorithms.WadPath(binPath), out var be)) return Array.Empty<string>();
            return Formats.Skeletons.ChampionAnimationData.ParseBankEvents(GetAssetBytes(be))
                .Where(e => e.StartsWith("Play_vo_", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>M95: locate a champion WAD in the game install. locale null → the main WAD;
    /// locale "*" → any locale companion (Aatrox.en_US.wad.client…), preferring en_US.</summary>
    private string? FindChampionWad(string champ, string? locale)
    {
        try
        {
            string? gameDir = !string.IsNullOrEmpty(Project.GameDirectory) && Directory.Exists(Project.GameDirectory)
                ? Project.GameDirectory
                : ReyEngine.Core.Projects.GameInstallLocator.Discover().FirstOrDefault()?.GameDirectory;
            if (gameDir is null) return null;
            string champsDir = Path.Combine(gameDir, "DATA", "FINAL", "Champions");
            if (!Directory.Exists(champsDir)) return null;

            if (locale is null)
            {
                string main = Path.Combine(champsDir, champ + ".wad.client");
                return File.Exists(main) ? main : null;
            }
            return Directory.EnumerateFiles(champsDir, $"{champ}.*.wad.client")
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                    Path.GetFileName(f), $@"^{System.Text.RegularExpressions.Regex.Escape(champ)}\.[a-z]{{2}}_[A-Z]{{2}}\.wad\.client$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .OrderByDescending(f => f.Contains(".en_US.", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>M95: merge the base + previewed skin's audio banks from a WAD file on disk into
    /// <paramref name="set"/>. Returns the number of banks/packs added.</summary>
    private int LoadBanksFromWadFile(Formats.Audio.AudioBankSet set, string wadPath, string champ, string skinFolder)
    {
        try
        {
            const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
            string marker = $"/characters/{champ}/skins/";
            int added = 0;
            using var wad = ReyEngine.Core.Wad.WadArchive.Open(wadPath, _resolver.Database);
            foreach (var e in wad.Entries)
            {
                if (!e.IsResolved) continue;
                var p = e.Path;
                int mi = p.IndexOf(marker, OIC);
                if (mi < 0) continue;
                string folder = p[(mi + marker.Length)..].Split('/')[0];
                if (!folder.Equals("base", OIC) && !folder.Equals(skinFolder, OIC)) continue;
                try
                {
                    if (p.EndsWith(".bnk", OIC))
                    { if (Formats.Audio.BnkFile.Parse(wad.Extract(e)) is { } b) { set.AddBank(b, e.PathHash, p); added++; } }
                    else if (p.EndsWith(".wpk", OIC))
                    { if (Formats.Audio.WpkFile.Parse(wad.Extract(e)) is { } w) { set.AddPack(w, e.PathHash, p); added++; } }
                }
                catch { /* skip broken banks */ }
            }
            return added;
        }
        catch { return 0; }
    }

    /// <summary>M90: play one clip sound event (e.g. Play_sfx_Aatrox_Death3D_cast) through the champion banks.</summary>
    private void PlayPreviewSoundEvent(string eventName)
    {
        try
        {
            if (_previewAudioBanks is null || !Sound.IsAvailable) return;
            var wems = _previewAudioBanks.ResolveEvent(eventName);
            if (wems.Count == 0) return;
            var wem = wems.Select(id => (Id: id, Data: _previewAudioBanks.GetWemData(id))).FirstOrDefault(x => x.Data is not null);
            if (wem.Data is null) return;
            if (Sound.DecodeToWav(wem.Id, wem.Data) is { } wav)
                Sound.PlayWav(wav, 1f, loop: false, tag: "previewsfx");
        }
        catch { /* never let SFX break the preview */ }
    }

    /// <summary>M86: the skin's VFX library + its ResourceResolver map (effect key → object hash), which
    /// is how animation clip particle events reference their effects. The skin bin itself holds almost no
    /// VFX — the systems live in its linked dependency bins (the multi-skin "longname" bins), so the
    /// whole link chain is followed and merged.</summary>
    private (IReadOnlyDictionary<uint, VfxSystemDefinition> systems, IReadOnlyDictionary<uint, uint>? resourceMap)
        TryLoadChampionVfxWithResources(WadAssetEntry skn, string? skinBin = null)
    {
        if (!ContentLoaded || !skn.IsResolved) return (EmptyVfx, null);
        // M728: the chosen skin's library and resolver - Lillia's skin 49 has a ResourceResolver of its own
        var binPath = SkinPaths.PreviewBinPath(skinBin, skn.Path);
        if (binPath is null || !TryResolveEntry(HashAlgorithms.WadPath(binPath), out var binEntry)) return (EmptyVfx, null);
        try
        {
            var systems = new Dictionary<uint, VfxSystemDefinition>();
            var resMap = new Dictionary<uint, uint>();
            var visited = new HashSet<ulong> { binEntry.PathHash };
            var queue = new Queue<WadAssetEntry>();
            queue.Enqueue(binEntry);
            int guard = 0;
            while (queue.Count > 0 && guard++ < 64)
            {
                byte[] bytes;
                try { bytes = GetAssetBytes(queue.Dequeue()); } catch { continue; }
                foreach (var (k, v) in VfxSystemResolver.ExtractAll(bytes)) systems.TryAdd(k, v);
                foreach (var (k, v) in VfxSystemResolver.ExtractResourceMap(bytes)) resMap.TryAdd(k, v);
                foreach (var dep in VfxSystemResolver.ExtractDependencies(bytes))
                {
                    var h = HashAlgorithms.WadPath(dep);
                    if (visited.Add(h) && TryResolveEntry(h, out var depEntry)) queue.Enqueue(depEntry);
                }
            }
            return (systems, resMap.Count > 0 ? resMap : null);
        }
        catch { return (EmptyVfx, null); }
    }

    /// <summary>Map a Formats <see cref="MaterialProfile"/> to the renderer's per-submesh material (M32).</summary>
    /// <summary>M78: locate the map's grass-tint texture (mGrassTintTexture — usually
    /// ASSETS/Maps/Info/&lt;map&gt;/GrassTint_*.tex). Mount glob, preferring the current map's folder and
    /// the base (shortest-named, no dragon suffix) texture — mirrors the MapgeoAddon fallback chain.</summary>
    // M385: the map's own state data, parsed once per opened map. Nulled by InvalidateMapState so a
    // reopen (or a project override appearing) re-reads it rather than serving a stale skin.
    private Formats.MapGeo.MapStateData? _mapState;
    private string? _mapStateFor;
    private Formats.MapGeo.MapSkinAssets? _mapSkin;

    private void InvalidateMapState() { _mapState = null; _mapStateFor = null; _mapSkin = null; }

    /// <summary>
    /// Riot's Map*.bin for the open mapgeo: "…/mapgeometry/map11/base_srx.mapgeo" -> the folder name
    /// "map11" -> "data/maps/shipping/map11/map11.bin". Read through ReadAssetByPath, so the normal mount
    /// priority applies and a project's own Map11.bin wins over Riot's.
    /// </summary>
    private Formats.MapGeo.MapStateData MapState()
    {
        string? mapPath = _currentMapEntry?.Path;
        if (mapPath is null) return Formats.MapGeo.MapStateData.Empty;
        if (_mapState is not null && _mapStateFor == mapPath) return _mapState;

        _mapStateFor = mapPath;
        _mapState = Formats.MapGeo.MapStateData.Empty;
        _mapSkin = null;

        string dir = Path.GetFileName(Path.GetDirectoryName(mapPath.Replace('\\', '/')) ?? "") ?? "";
        if (dir.Length == 0) return _mapState;

        try
        {
            var bytes = ReadAssetByPath($"data/maps/shipping/{dir.ToLowerInvariant()}/{dir.ToLowerInvariant()}.bin");
            if (bytes is null) return _mapState;
            _mapState = Formats.MapGeo.MapStateData.Parse(bytes, ResolveBinName);
            _mapSkin = _mapState.SkinForMapGeo(mapPath);
            _log.Info("MapGeo", $"map state: {_mapState.Skins.Count} skin(s), "
                              + $"{_mapState.FlagDefinitions.Count} visibility flag(s); skin for this mapgeo = "
                              + $"{_mapSkin?.SkinName ?? "(none matched)"}");
        }
        catch (Exception ex) { _log.Warn("MapGeo", $"map state could not be read ({ex.Message})."); }

        return _mapState;
    }

    /// <summary>M385: which grass tint the CURRENT visibility state selects, with the reasoning kept so
    /// the map/visibility inspector can show it without a debug window.</summary>
    public Formats.MapGeo.GrassTintChoice GrassTintChoice()
    {
        var state = MapState();
        // M387: CurrentPrimaryVisibilityBit is a MASK (VisibilityLayer.Bit is built as 1 << i and used as
        // one: `controllerBits & l.Bit`). Riot's MapVisibilityFlagDefinition.BitIndex is an INDEX. M385
        // compared the two directly, which shifted every state by one position: "Base" (mask 1) matched
        // BitIndex 1 = Fire and showed the Infernal tint, "Infernal" (mask 2) matched BitIndex 2 = earth
        // and showed Mountain, and so on. Convert explicitly.
        return state.ResolveGrassTintForMask(_mapSkin, CurrentPrimaryVisibilityBit);
    }

    /// <summary>
    /// M385: re-resolve the grass tint for the CURRENT visibility state and republish it.
    ///
    /// <para>Called whenever visibility changes, because the tint is part of the map state: Riot swaps
    /// mGrassTintTexture for the mAlternateAssets entry whose flag is active. Before this it was resolved
    /// once at map-build time and never again, so switching dragon kept the base tint.</para>
    ///
    /// <para>Cheap when nothing changed: the path is compared first and the texture is only reloaded when
    /// it actually differs, so this is safe to call from the visibility hook.</para>
    /// </summary>
    /// <summary>M396: the running environment crossfade. Renderer-agnostic; both viewports read
    /// <see cref="GrassInterp"/> and the same pair of paths.</summary>
    private readonly Formats.MapGeo.MapStateTransition _grassTransition = new();
    private readonly System.Diagnostics.Stopwatch _grassClock = System.Diagnostics.Stopwatch.StartNew();
    private double _grassLastTick;

    /// <summary>GRASS_INTERP for this frame: 0 = the state being left, 1 = the state being entered.</summary>
    public float GrassInterp => _grassTransition.Interp;

/// <summary>M397: the environment transition's INCOMING grass tint, for OpenGL's alternate sampler.
    /// Null whenever nothing is fading, which switches the shader's blend off.</summary>
    [ObservableProperty] private TextureImage? _currentGrassTintAlt;


    private void RefreshGrassTint()
    {
        if (_currentMap is null) return;

        var choice = GrassTintChoice();
        string? path = choice.ActivePath is not null ? FindGrassTintTexturePath() : null;

        // Riot authors TransitionTime on the state being ENTERED. Base is not a named state - its flag
        // definition carries no BitIndex, PublicName or TransitionTime - so returning to base is INSTANT.
        // That is what the data says, and it matches the game, which never leaves a dragon state
        // mid-match; the return trip only exists in an editor.
        float? duration = choice.FromAlternate ? MapState().TransitionTimeForBit(choice.BitIndex) : null;

        if (!_grassTransition.Begin(path, duration)) return;
        _grassLastTick = _grassClock.Elapsed.TotalSeconds;
        ApplyGrassTransition();
        // M397: ask the host to start pumping frames. Driven by the host rather than the D3D11 render
        // callback, because OpenGL has its own loop and a fade must run in whichever viewport is up.
        if (_grassTransition.IsRunning) GrassTransitionStarted?.Invoke();
        // M403: the transition bursts are gated on a RUNNING transition, so the playback set has to be
        // rebuilt at both edges - here to start them, and on settle to stop them.
        RebuildParticlePlayback();

        _log.Info("GrassTint", path is null
            ? "no grass tint resolved for this state."
            : $"{choice.SourceLabel}: {path}"
              + (choice.FromAlternate ? $" (visibility bit {choice.BitIndex})" : "")
              + (_grassTransition.IsRunning
                  ? $" - crossfading over {_grassTransition.Duration:0.##}s"
                  : " - instant (no TransitionTime authored)"));
        OnPropertyChanged(nameof(GrassTintStatus));
    }

    /// <summary>Push the transition's CURRENT pair to both renderers. Called when a fade starts and
    /// again when it lands, not per frame - only the interp factor changes in between.</summary>
    private void ApplyGrassTransition()
    {
        var from = _grassTransition.FromPath;
        var to = _grassTransition.ToPath;

        // M397: OpenGL now blends the same pair Riot does, so it gets BOTH ends - base on the primary
        // sampler, incoming on the alternate - and the shared GrassInterp. No approximation left here.
        CurrentGrassTint = from is not null ? LoadTextureByPath(from) : null;
        CurrentGrassTintAlt = _grassTransition.IsRunning && to is not null ? LoadTextureByPath(to) : null;

        var fromTex = from is not null ? LoadTextureByPath(from) : null;
        var toTex = to is not null ? LoadTextureByPath(to) : null;
        // Either end missing means there is nothing to blend between; fall back to whichever exists so a
        // half-resolved state still shows a tint instead of none.
        fromTex ??= toTex;
        toTex ??= fromTex;
        if (from is null || to is null || fromTex is null || toTex is null) return;

        Dx11RebindGrassTintPair?.Invoke(from, fromTex, to, toTex);
    }

    /// <summary>M397: raised when a fade begins, so the host can start ticking. Renderer-neutral on
    /// purpose - OpenGL and D3D11 have different frame loops and both must be able to run a transition.</summary>
    public Action? GrassTransitionStarted;

    /// <summary>M396: advance the crossfade. Called from the per-frame path of whichever viewport is
    /// presenting; returns true while it is still moving, so the host knows to keep asking for frames.</summary>
    public bool TickGrassTransition()
    {
        double now = _grassClock.Elapsed.TotalSeconds;
        float dt = (float)(now - _grassLastTick);
        _grassLastTick = now;

        if (!_grassTransition.IsRunning) return false;

        _grassTransition.Advance(dt);
        OnPropertyChanged(nameof(GrassInterp));

        if (!_grassTransition.IsRunning)
        {
            // Landed: collapse so the next fade starts from a clean pair, and rebind both slots to the
            // settled texture. Without this the alternate slot would keep the old incoming texture and
            // the next transition would blend from the wrong end.
            _grassTransition.Settle();
            ApplyGrassTransition();
            OnPropertyChanged(nameof(GrassInterp));
            OnPropertyChanged(nameof(GrassTintStatus));
            RebuildParticlePlayback();   // M403: drop the transition bursts now the fade has landed
            _log.Info("GrassTint", $"transition settled on {_grassTransition.SettledPath}.");
            return false;
        }
        return true;
    }

    /// <summary>M396: set by the view (which owns the D3D11 surface) — binds the two grass-tint slots to
    /// the transition's from/to textures. Null when D3D11 is not up, which is the normal case under
    /// OpenGL and not an error. Supersedes M386's single-texture callback, because a crossfade needs the
    /// two slots holding DIFFERENT textures.</summary>
    public Func<string, TextureImage, string, TextureImage, int>? Dx11RebindGrassTintPair;

    /// <summary>M385: item 7 — the grass tint state, for the existing map/visibility inspector rather
    /// than a standalone debug window.</summary>
    public string GrassTintStatus
    {
        get
        {
            var c = GrassTintChoice();
            if (c.DefaultPath is null && c.ActivePath is null) return "";
            var flag = c.FromAlternate ? MapState().FlagByBit(c.BitIndex) : null;
            return $"Default: {c.DefaultPath ?? "(none)"}\n"
                 + $"Active: {c.ActivePath ?? "(none)"}\n"
                 + $"Source: {c.SourceLabel}"
                 + (flag is not null
                     ? $"\nVisibility Flag: {flag.PublicName ?? $"bit {c.BitIndex}"}"
                       + (flag.TransitionTime is { } t ? $" (transition {t:0.##}s)" : " (no transition authored)")
                     : "");
        }
    }

    /// <summary>An "ASSETS/..." string from a bin, as a mounted virtual path — or null if nothing is
    /// mounted at it. WAD virtual paths are lowercase, which is the only transform needed.</summary>
    private string? ResolveAssetString(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath)) return null;
        string vp = assetPath.Replace('\\', '/').ToLowerInvariant();
        try { return ReadAssetByPath(vp) is not null ? vp : null; } catch { return null; }
    }

    private string? FindGrassTintTexturePath()
    {
        if (_mounts is null) return null;

        // M385: the authored answer first - mGrassTintTexture, or the mAlternateAssets entry whose
        // visibility flag is active. What follows is the pre-M385 filename scan, kept ONLY as a fallback
        // for maps with no Map*.bin (legacy NVR ports, hand-built projects); it guesses by name and was
        // never Riot's configuration.
        var choice = GrassTintChoice();
        if (ResolveAssetString(choice.ActivePath) is { } authored) return authored;
        // Authored but not mounted: say so, because silently scanning would hide a missing asset.
        if (choice.ActivePath is { } missing)
            _log.Warn("GrassTint", $"'{missing}' is authored but not mounted - falling back to a name scan.");

        var candidates = _mounts.Assets
            .Where(a => a.IsResolved && a.VirtualPath.Contains("grasstint", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.VirtualPath)
            .ToList();
        if (candidates.Count == 0) return null;
        string token = "";
        if (_currentMapEntry?.Path is { } mp)
            token = Path.GetFileName(Path.GetDirectoryName(mp.Replace('\\', '/')) ?? "") ?? "";
        return candidates
            .OrderByDescending(c => token.Length > 0 && c.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.Length)
            .First();
    }

    /// <summary>M665: moved to <see cref="Services.MapSubmeshResources"/> so the arena builds its floor's
    /// render state with the very same code the map viewport does. Kept as a forwarder because a dozen
    /// call sites read better without the namespace.</summary>
    private static ViewportMeshRenderer.SubmeshMaterial ToSubmeshMaterial(MaterialProfile p,
        System.Numerics.Vector4 terrainWorldMaskTransform = default) =>
        Services.MapSubmeshResources.ToSubmeshMaterial(p, terrainWorldMaskTransform);

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

    /// <summary>M73: the hash resolver, exposed so the New Project wizard can classify + extract WAD content.</summary>
    public WadPathResolver PathResolver => _resolver;

    /// <summary>M73: raised to open the template-based New Project wizard (handled by the window).</summary>
    public event Action? RequestNewProject;

    [RelayCommand]
    private void NewProject() => RequestNewProject?.Invoke();

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
        // A folder project (opened via Open Project Folder / the M73 wizard) is ALREADY a real saved project
        // with a workspace on disk — overrides land under its .reyengine folder. No WAD/quick-project needed.
        if (Project.IsFolderProject && Project.ProjectFilePath is not null) return true;

        if (Project.SourceWadPath is null)
        {
            if (_archive is null) { _log.Warn("Project", "Open a WAD and create a project first."); return false; }
            // Legacy quick-project: inspecting a bare WAD and making the first edit — wrap the open WAD in a
            // project inline (the M73 wizard is for deliberate new projects, not this save-on-first-edit path).
            var proj = ReyProjectService.NewFromWad(_archive.FilePath);
            proj.GameDirectory = Project.GameDirectory;
            Project = proj;
            _overrides.Clear();
            RebuildTree();
            _log.Info("Project", $"Created quick project '{proj.Name}' from {Path.GetFileName(_archive.FilePath)} to hold your edits.");
            UpdateTitle();
        }
        if (Project.ProjectFilePath is null) await SaveProjectAs();
        return Project.ProjectFilePath is not null;
    }

    // ---- Import / replace / revert --------------------------------------

    [RelayCommand(CanExecute = nameof(CanReplaceSelected))]
    private async Task ReplaceSelected()
    {
        var entry = ContextNode?.Entry;
        if (entry is null) { _log.Warn("Project", "Select an asset to replace."); return; }
        if (!GuardEditable(entry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        var file = await Dialogs.OpenFileAsync($"Replace {entry.DisplayName}", DialogService.All);
        if (file is null) return;

        // M393: replacing a .tex with a PNG has to CONVERT. Storing the PNG bytes under a .tex path
        // would produce an override the game cannot read, and nothing downstream would notice - the
        // override system is byte-agnostic on purpose.
        string source = file;
        string? temp = null;
        if (TextureImportViewModel.IsConvertibleImage(file)
            && entry.Path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
            && ShowTextureImportWindow is { } showImport)
        {
            var ivm = new TextureImportViewModel(new[] { file });
            var choice = await showImport(ivm);
            if (choice == Views.TextureImportResult.Cancel) return;
            if (choice == Views.TextureImportResult.Convert)
            {
                var encoded = ivm.Encode();
                if (encoded.Count == 0)
                { _log.Warn("Project", $"{Path.GetFileName(file)} could not be decoded - nothing replaced."); return; }
                temp = Path.Combine(Path.GetTempPath(), $"reyengine_{Guid.NewGuid():N}.tex");
                File.WriteAllBytes(temp, encoded[0].Bytes);
                source = temp;
            }
            // CopyAsIs falls through with the original file, which is a deliberate escape hatch: a user
            // replacing a .tex with an already-encoded blob that merely has the wrong extension.
        }

        try
        {
            var stored = ProjectWorkspace.StoreOverride(Project, entry.PathHash, source);
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
            _log.Success("Project", $"Replaced {entry.DisplayName} with {Path.GetFileName(file)}"
                                    + (temp is not null ? " (converted to .tex)." : "."));
        }
        catch (Exception ex) { _log.Error("Project", ex.Message); }
        finally
        {
            // The override was copied into the project by StoreOverride, so the scratch file has served
            // its purpose either way - including when StoreOverride threw.
            if (temp is not null) { try { File.Delete(temp); } catch { } }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevertSelected))]
    private void RevertSelected()
    {
        var entry = ContextNode?.Entry;
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
        var entry = ContextNode?.Entry;
        if (entry is null || !_overrides.TryGet(entry.PathHash, out var ov)) { _log.Warn("Export", "Selected asset has no override."); return; }
        var outPath = await Dialogs.SaveFileAsync("Export modified asset", Path.GetFileName(ov.OverrideFile));
        if (outPath is null) return;
        try { File.Copy(ov.OverrideFile, outPath, true); _log.Success("Export", $"Wrote {outPath}"); }
        catch (Exception ex) { _log.Error("Export", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(CanCopyEntryText))]
    private async Task CopyResolvedPath()
    {
        var entry = ContextNode?.Entry;
        if (entry is null) return;
        await Dialogs.CopyAsync(entry.Path);
        _log.Info("Clipboard", entry.Path);
    }

    [RelayCommand(CanExecute = nameof(CanCopyEntryText))]
    private async Task CopyHash()
    {
        var entry = ContextNode?.Entry;
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

    // ---- M98: Map Bin Editor window ----
    public MapBinEditorViewModel MapBinEditor { get; } = new();
    public Action? ShowMapBinEditorWindow;

    /// <summary>M140: open a HUD layout bin (ClientStates/…/UIBase) in the visual HUD Editor.</summary>
    [RelayCommand]
    private void OpenInHudEditor(AssetNodeViewModel? node)
    {
        var entry = node?.Entry ?? ContextNode?.Entry;
        if (entry is null) { _log.Warn("Hud", "Select a HUD layout .bin (ClientStates/…/UIBase) first."); return; }
        try
        {
            var bytes = GetAssetBytes(entry);
            var doc = Formats.Hud.HudDocument.Parse(bytes, ResolveBinName);
            if (doc is null || doc.AllElements.Count == 0)
            { _log.Warn("Hud", $"{entry.DisplayName} isn't a HUD layout bin (no UiElement objects)."); return; }

            var vm = new HudEditorViewModel
            {
                ResolveAtlas = LoadThumbnailByPath,
                Info = m => _log.Info("Hud", m),
            };
            vm.Load(entry, doc);
            var win = new Views.HudEditorWindow { DataContext = vm };
            if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
            _log.Info("Hud", $"HUD Editor: {entry.DisplayName} — {doc.AllElements.Count} element(s), {doc.AtlasPaths.Count} atlas(es), reference {doc.ReferenceWidth}×{doc.ReferenceHeight}.");
        }
        catch (Exception ex) { _log.Error("Hud", $"{entry.DisplayName}: {ex.Message}"); }
    }

    /// <summary>M137: open a Wwise .bnk/.wpk in the Audio Bank Editor — play, replace, add, rename,
    /// delete and copy/paste its embedded sounds.</summary>
    [RelayCommand]
    private void OpenInAudioEditor(AssetNodeViewModel? node)
    {
        var entry = node?.Entry ?? ContextNode?.Entry;
        if (entry is null) { _log.Warn("Audio", "Select a .bnk or .wpk asset first."); return; }
        if (!entry.DisplayName.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)
            && !entry.DisplayName.EndsWith(".wpk", StringComparison.OrdinalIgnoreCase))
        { _log.Warn("Audio", $"{entry.DisplayName} is not a Wwise bank or wem pack."); return; }

        try
        {
            var bytes = GetAssetBytes(entry);
            var vm = new AudioBankEditorViewModel
            {
                DecodeToWav = (id, data) => Sound.DecodeToWav(id, data),
                PlayWav = wav => Sound.PlayWav(wav, 1f, loop: false, tag: "bankedit"),
                StopAll = () => Sound.StopAll(),
                ClearDecodeCache = id => Sound.ClearCache(id),
                Info = m => _log.Info("Audio", m),
                Warn = m => _log.Warn("Audio", m),
                PickImportFile = title => Dialogs.OpenFileAsync(title,
                    new Avalonia.Platform.Storage.FilePickerFileType("Audio (wem / wav / mp3 / ogg)")
                    { Patterns = new[] { "*.wem", "*.wav", "*.mp3", "*.ogg", "*.flac", "*.m4a", "*.wma" } },
                    DialogService.All),
                ConvertToWem = path => { var d = Encoder.Convert(path, out var err); return (d, err); },
                ConverterAvailable = () => Encoder.IsAvailable,
                PickExportFile = suggested => Dialogs.SaveFileAsync("Export sound", suggested),
                PromptText = (title, initial) => PromptOwner is null
                    ? Task.FromResult<string?>(null)
                    : Views.PromptWindow.InputAsync(PromptOwner, title,
                        "Wwise identifies sounds by number — this id is what events reference.", initial),
                SaveAsync = async (doc, e, data) =>
                {
                    if (!GuardEditable(e)) return false;
                    if (!await EnsureProjectSavedAsync()) return false;
                    bool ok = await SaveMapBinBytesAsync(e, data);   // in-place for folder projects; override otherwise
                    if (ok) _log.Success("Audio", $"Saved {e.DisplayName} ({data.Length:n0} bytes, {doc.Entries.Count} sound(s)).");
                    return ok;
                },
            };

            if (!vm.Load(entry, bytes, null))
            { _log.Warn("Audio", $"{entry.DisplayName} isn't a readable Wwise bank/pack."); return; }
            if (vm.Document is { IsEditable: false, ReadOnlyReason: { } reason })
                _log.Warn("Audio", $"{entry.DisplayName}: {reason}");

            // "played by": reverse-resolve the sibling *_events.bnk so each sound shows what triggers it
            if (BuildUsedByLookup(entry) is { } lookup) vm.SetUsedByLookup(lookup);

            var win = new Views.AudioBankEditorWindow { DataContext = vm };
            if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
            _log.Info("Audio", $"Audio Bank Editor: {entry.DisplayName} — {vm.Document!.Entries.Count} sound(s)."
                + (Sound.IsAvailable ? "" : " vgmstream-cli NOT found — playback disabled."));
        }
        catch (Exception ex) { _log.Error("Audio", $"{entry.DisplayName}: {ex.Message}"); }
    }

    /// <summary>Map wem id → the event(s) that play it, read from the bank's sibling <c>*_events.bnk</c>
    /// (media and hierarchy ship as separate files). Event names aren't stored in the banks, so known
    /// names come from the loaded map's sound placements; anything else shows as its hex id.</summary>
    private Func<uint, string[]>? BuildUsedByLookup(WadAssetEntry mediaEntry)
    {
        try
        {
            var path = mediaEntry.Path;
            var eventsPath = path.Contains("_audio.", StringComparison.OrdinalIgnoreCase)
                ? System.Text.RegularExpressions.Regex.Replace(path, "_audio\\.(bnk|wpk)$", "_events.bnk",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                : null;
            if (eventsPath is null || !TryResolveEntry(HashAlgorithms.WadPath(eventsPath), out var evEntry)) return null;
            if (Formats.Audio.BnkFile.Parse(GetAssetBytes(evEntry)) is not { HasHirc: true } evBank) return null;

            var set = new Formats.Audio.AudioBankSet();
            set.AddBank(evBank, evEntry.PathHash, evEntry.Path);

            // known event names (hash -> name): the recovered index plus the map's placed sounds
            var names = new Dictionary<uint, string>();
            foreach (var s in MapContent.Sounds)
                names[Formats.Audio.WwiseHash.Fnv1(s.EventName)] = s.EventName;

            var reverse = new Dictionary<uint, List<string>>();
            foreach (var eventId in evBank.Events.Keys)
            {
                string label = names.TryGetValue(eventId, out var n) ? n : WwiseNames.Label(eventId);
                foreach (var wemId in set.ResolveEvent(eventId))
                {
                    if (!reverse.TryGetValue(wemId, out var list)) reverse[wemId] = list = new List<string>();
                    if (!list.Contains(label)) list.Add(label);
                }
            }
            _log.Info("Audio", $"Matched {System.IO.Path.GetFileName(eventsPath)}: {evBank.Events.Count} event(s) → {reverse.Count} sound(s).");
            return id => reverse.TryGetValue(id, out var l) ? l.ToArray() : Array.Empty<string>();
        }
        catch { return null; }
    }

    /// <summary>M98: right-click ▸ Open in Map Bin Editor — the fast structured editor for map*.bin.</summary>
    /// <summary>M197 (4.5): open any bin in the Particle Editor from the Content Browser. Until this, a map
    /// document only ever parsed VFX from the mapgeo's sibling materials.bin, so the systems in mapXX.bin and
    /// under maps/modespecificdata were unreachable in the app - thousands of them, with no placements to
    /// bring them into a scene.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInParticleEditor))]
    private void OpenInParticleEditor(AssetNodeViewModel? node)
    {
        var entry = node?.Entry ?? ContextNode?.Entry;
        if (entry is null) { _log.Warn("Particle", "Select an asset first."); return; }
        OpenParticleEditorFor(entry);
    }

    [RelayCommand(CanExecute = nameof(CanOpenInMapBinEditor))]
    private void OpenInMapBinEditor(AssetNodeViewModel? node)
    {
        if (node?.Entry is not { } entry) { _log.Warn("MapBin", "Select a .bin asset first."); return; }
        if (!entry.DisplayName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        { _log.Warn("MapBin", $"{entry.DisplayName} is not a .bin file."); return; }
        try
        {
            var bytes = GetAssetBytes(entry);
            MapBinEditor.Load(entry, bytes);
            ShowMapBinEditorWindow?.Invoke();
        }
        catch (Exception ex) { _log.Error("MapBin", $"{entry.DisplayName}: {ex.Message}"); }
    }

    /// <summary>M98: the UNTOUCHED Riot bytes for an entry — read from the project's reference WADs
    /// directly (never through the mounts, which would return the project's own override).</summary>
    private byte[]? ReadRiotOriginalBytes(WadAssetEntry entry)
    {
        // Prefer already-open reference/fallback mounts. The old wizard reopened a multi-hundred-MB WAD
        // for every bin, turning an update into repeated archive parsing when the exact source was mounted.
        if (_mounts is not null)
        {
            if (_mounts.TryGet(entry.PathHash, out var mounted))
                foreach (var source in new[] { mounted.Source }.Concat(mounted.AllSources).Distinct())
                    if (source.Kind == AssetSourceKind.RiotReference && source.Contains(entry.PathHash))
                        try { return source.Read(entry.PathHash); } catch { }
            try { if (_mounts.ReadFallback(entry.PathHash) is { } fallback) return fallback; } catch { }
        }
        foreach (var wadPath in Project.ReferenceWads)
        {
            try
            {
                if (!File.Exists(wadPath)) continue;
                using var w = ReyEngine.Core.Wad.WadArchive.Open(wadPath, _resolver.Database);
                if (w.TryGetEntry(entry.PathHash, out var e)) return w.Extract(e);
            }
            catch { /* try the next reference */ }
        }
        // single-WAD mode: the open archive IS the Riot file
        try { if (_archive is not null && _archive.TryGetEntry(entry.PathHash, out var ae)) return _archive.Extract(ae); }
        catch { }
        return null;
    }

    /// <summary>M98: save Map Bin Editor output through the same guarded override path as the raw editor
    /// (re-parse check, override store, status + dirty bookkeeping).</summary>
    /// <summary>
    /// M555: rebase an editor's whole-file snapshot onto whatever the file holds NOW.
    ///
    /// <para>The editors that own a parsed document - materials and the raw bin - serialise the ENTIRE
    /// file from a document parsed once when the map was opened. Writing that straight out discards every
    /// change made to the same file since, by any other surface. That is the reported bug exactly: the sun
    /// lives in the map's materials.bin, so saving a material rewound it. Autosave made it worse by firing
    /// the same stale write on a timer.</para>
    ///
    /// <para>So a save is a three-way merge, not an overwrite: <c>diff(base -> mine)</c> re-applied onto
    /// the current file, through the same engine M97 already uses to carry a mod across a patch. Untouched
    /// objects keep whatever the file has; only what the editor actually changed is carried over.</para>
    ///
    /// <para>Returns <paramref name="edited"/> unchanged when there is no base to compare against, or when
    /// the file has not moved underneath - the overwhelmingly common case, and one that must stay free.</para>
    /// </summary>
    private byte[] RebaseOntoCurrent(WadAssetEntry entry, byte[] edited, byte[]? baseBytes, string channel)
    {
        if (baseBytes is null || baseBytes.Length == 0) return edited;

        byte[] current;
        try { current = ReadAsset(entry.PathHash); }
        catch (Exception ex) { _log.Warn(channel, $"Could not re-read {entry.DisplayName} to merge onto: {ex.Message}"); return edited; }
        if (current.AsSpan().SequenceEqual(baseBytes)) return edited;   // nothing landed underneath us

        try
        {
            var (merged, report) = Formats.Meta.BinThreeWayMerge.Merge(baseBytes, edited, current, ResolveBinName);
            if (report.Conflicts > 0)
                foreach (string detail in report.ConflictDetails.Take(3))
                    _log.Warn(channel, $"Merge conflict, this editor's value kept: {detail}");
            _log.Info(channel, $"{entry.DisplayName} changed underneath this editor - merged "
                + $"{report.ModAdded + report.ModModified + report.ModRemoved} local edit(s) onto it "
                + "instead of overwriting.");
            return merged;
        }
        catch (Exception ex)
        {
            // Refusing is not an option here - it would lose the user's edit - but overwriting silently is
            // how the sun disappeared, so say so.
            _log.Warn(channel, $"{entry.DisplayName} changed underneath this editor and could not be merged "
                + $"({ex.Message}). Saving this editor's version; other changes to that file may be lost.");
            return edited;
        }
    }

    private async Task<bool> SaveMapBinBytesAsync(WadAssetEntry entry, byte[] bytes)
    {
        try { _ = Formats.Meta.SafeBinTree.Parse(bytes); }
        catch (Exception ex) { _log.Error("MapBin", $"Edited .bin failed to re-parse — NOT saved: {ex.Message}"); return false; }
        if (!await EnsureProjectSavedAsync()) return false;
        try
        {
            // M98c: folder-project files are edited in place — no shadow override
            if (TryWriteToProjectFile(entry, bytes, out var projectFile))
            {
                SetNodeStatus(entry.PathHash, AssetStatus.Modified);
                Project.IsDirty = true;
                UpdateTitle();
                _log.Success("MapBin", $"Saved {entry.DisplayName} to {projectFile} ({bytes.Length:n0} bytes, re-parse OK).");
                return true;
            }
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
            _log.Success("MapBin", $"Saved {entry.DisplayName} to project override ({bytes.Length:n0} bytes, re-parse OK).");
            return true;
        }
        catch (Exception ex) { _log.Error("MapBin", ex.Message); return false; }
    }

    // ---- M125: Bin Issues window — repairs the tolerant reader applied, navigable + fixable ----

    /// <summary>Open the Bin Issues window for the materials document (map/champion .bin).</summary>
    private void OpenMaterialBinIssues(MaterialEditorViewModel editor)
    {
        if (editor.BinEntry is not { } entry || editor.Issues.Count == 0) return;
        var vm = new BinIssuesWindowViewModel
        {
            BinName = entry.DisplayName,
            RepairAsync = entry.ReadOnly ? null : async () =>
            {
                // The tolerantly-parsed tree IS the healed form — re-saving it writes a clean file.
                var bytes = editor.Serialize();
                if (bytes is null || !await SaveMapBinBytesAsync(entry, bytes)) return false;
                await LoadMaterialBinAsync(entry, alsoRawBin: false);   // reload: the red marks clear
                return true;
            },
        };
        var group = new BinIssueGroupViewModel { BinName = entry.DisplayName };
        vm.Groups.Add(group);
        foreach (var i in editor.Issues)
        {
            var mat = editor.Materials.FirstOrDefault(m => m.Model.ObjectPathHash == i.ObjectPathHash);
            group.Rows.Add(new BinIssueRowViewModel
            {
                Kind = i.Kind,
                ObjectName = mat?.Name ?? ResolveBinName(i.ObjectPathHash) ?? $"0x{i.ObjectPathHash:x8}",
                ClassName = ResolveBinName(i.ObjectClassHash) ?? $"class 0x{i.ObjectClassHash:x8}",
                FieldName = i.FieldHash is { } fh ? ResolveBinName(fh) ?? $"0x{fh:x8}" : null,
                Message = i.Message,
                Suggestion = i.Suggestion,
                GoTo = mat is null ? null : () =>
                {
                    InspectorTab = InspectorTabs.Materials;
                    AssetDataExpanded = true;
                    MaterialEditor.SetMeshFilter(null);      // the filter must not hide the target
                    MaterialEditor.OnlyUnresolved = false;
                    MaterialEditor.Search = mat.Name;        // narrows the list to the affected material
                },
            });
        }
        ShowBinIssuesWindow(vm);
    }

    /// <summary>Open the Bin Issues window for the particle document.</summary>
    private void OpenParticleBinIssues()
    {
        if (ParticleEditor.Entry is not { } entry || ParticleEditor.Document is not { } doc || doc.Issues.Count == 0) return;
        var vm = new BinIssuesWindowViewModel
        {
            BinName = entry.DisplayName,
            RepairAsync = entry.ReadOnly ? null : async () =>
            {
                var bytes = doc.Serialize();
                if (!await SaveMapBinBytesAsync(entry, bytes)) return false;
                ParticleEditor.Load(entry, bytes, editable: true);   // reload from the healed bytes
                return true;
            },
        };
        var group = new BinIssueGroupViewModel { BinName = entry.DisplayName };
        vm.Groups.Add(group);
        foreach (var i in doc.Issues)
        {
            var node = ParticleEditor.Systems.FirstOrDefault(s => s.Entry.PathHash == i.ObjectPathHash);
            group.Rows.Add(new BinIssueRowViewModel
            {
                Kind = i.Kind,
                ObjectName = node?.Name ?? ResolveBinName(i.ObjectPathHash) ?? $"0x{i.ObjectPathHash:x8}",
                ClassName = ResolveBinName(i.ObjectClassHash) ?? $"class 0x{i.ObjectClassHash:x8}",
                FieldName = i.FieldHash is { } fh ? ResolveBinName(fh) ?? $"0x{fh:x8}" : null,
                Message = i.Message,
                Suggestion = i.Suggestion,
                GoTo = node is null ? null : () =>
                {
                    ParticleEditor.SelectedSystem = node;
                    ShowParticleEditorWindow?.Invoke();
                },
            });
        }
        ShowBinIssuesWindow(vm);
    }

    private void ShowBinIssuesWindow(BinIssuesWindowViewModel vm)
    {
        var win = new Views.BinIssuesWindow { DataContext = vm };
        if (PromptOwner is not null) win.Show(PromptOwner);
        else win.Show();
    }

    /// <summary>M97: emulated-injection check — validate every project .bin against the merged view
    /// (project overrides + Riot originals, exactly what the game would mount) and report broken object
    /// links and missing asset references. The classic "mod crashes after patch" causes, found offline.</summary>
    [RelayCommand]
    private async Task ValidateProjectBins()
    {
        if (!ContentLoaded) { _log.Warn("Validate", "Open a project (or WAD) first."); return; }
        if (Project.RootPath is null || Project.ProjectFolders.Count == 0)
        { _log.Warn("Validate", "No project folders to validate — open a folder project."); return; }

        _log.Info("Validate", "Checking project .bins against the injected view (project overrides + Riot originals)…");
        var results = await Task.Run(() =>
        {
            // M127: per missing asset, hunt for an existing replacement — the base variant of a dead
            // skin-suffixed path (Riot vaults map skins; X.HA_CREPE.scb dies, X.scb stays), else any
            // mounted file with the same filename. Powers the one-click Fix in the issues window.
            string? FindAlternative(string missing)
            {
                if (Formats.Meta.BinAssetRepointer.BaseVariant(missing) is { } baseVar
                    && TryResolveEntry(HashAlgorithms.WadPath(baseVar), out _))
                    return baseVar;
                string fileName = Path.GetFileName(missing);
                foreach (var e in AssetEntries)
                    if (e.IsResolved && Path.GetFileName(e.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        return e.Path;
                return null;
            }

            var list = new List<(string Rel, Formats.Meta.BinValidationReport Report, Dictionary<string, string> Alts)>();
            // M418: the geometry rules ask a question no single file can answer — "does the materials bin
            // define this material, and on which shader" — so every material bin read below is kept and
            // the .mapgeo pass runs once, afterwards, against all of them.
            var materialTrees = new List<LeagueToolkit.Core.Meta.BinTree>();
            uint materialClass = HashAlgorithms.Fnv1a("StaticMaterialDef");
            foreach (var folder in Project.ProjectFolders)
            {
                string root = Path.Combine(Project.RootPath!, folder);
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (!rel.Contains('/')) continue;   // loose unresolved-chunk dumps, not real bins
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(file); } catch { continue; }

                    // M126: validate what the game actually MOUNTS. A shadow override outranks the
                    // project file, so validating the raw file reported issues the user had already
                    // fixed (the fix lived in the override). Saves now dissolve shadows, but existing
                    // projects may still carry one — even record-less (the override mount scans its
                    // directory, project.json entries are optional) — prefer its bytes and say so.
                    string display = rel;
                    ulong relHash = HashAlgorithms.WadPath(rel);
                    string? shadowFile = null;
                    if (_overrides.TryGet(relHash, out var shadow) && File.Exists(shadow.OverrideFile))
                        shadowFile = shadow.OverrideFile;
                    else
                    {
                        try
                        {
                            var orphan = Path.Combine(ProjectWorkspace.OverridesDir(Project), $"{relHash:x16}.bin");
                            if (File.Exists(orphan)) shadowFile = orphan;
                        }
                        catch { }
                    }
                    if (shadowFile is not null && !string.Equals(shadowFile, file, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            bytes = File.ReadAllBytes(shadowFile);
                            display = $"{rel}  [an override shadows this file — save it once in ReyEngine to merge]";
                        }
                        catch { /* unreadable shadow: validate the project file */ }
                    }

                    // resolve this bin's dependency bins through the SAME merged view the game would see
                    var deps = new List<byte[]>();
                    foreach (var dep in Formats.Vfx.VfxSystemResolver.ExtractDependencies(bytes))
                        if (TryResolveEntry(HashAlgorithms.WadPath(dep), out var de))
                        { try { deps.Add(GetAssetBytes(de)); } catch { /* counted as missing-dependency */ } }

                    var report = Formats.Meta.BinValidator.Validate(display, bytes, deps,
                        p => TryResolveEntry(HashAlgorithms.WadPath(p), out _),
                        ResolveBinName,
                        h => ResolveBinName(h)?.StartsWith("Shaders/", StringComparison.OrdinalIgnoreCase) == true,
                        // M372: schema checks. All three fall back to "no schema" when the meta database
                        // was never synced, and the validator then reports exactly what it did before.
                        classKnown: h => Meta.TryGetClass(h, out _),
                        declaredType: (cls, prop) =>
                            Meta.TryGetProperty(cls, prop, out var mp) ? mp.FieldType : null,
                        declaredEver: Meta.DeclaredAtAnyBuild);

                    // M418: shape rules on top of the link/asset checks. Every one of these parses
                    // cleanly, round-trips byte-identically and diffs clean against Riot's file — and
                    // still crashes the game at map load or renders nothing. Additive, so both sets of
                    // findings land in the same report.
                    try
                    {
                        var tree = Formats.Meta.SafeBinTree.Parse(bytes);
                        var shape = Formats.Meta.ModShapeValidator.ValidateBin(tree, bytes, ResolveBinName);
                        if (shape.Count > 0)
                            report = report with { Issues = report.Issues.Concat(shape).ToList() };

                        // M430: the shader CONTRACT rules, when a shader catalog is loaded. These are the
                        // checks that would have caught the Mantis_Env_Baked_PBR crash - a shader Riot
                        // ships but has no material for, so there was no template to copy from.
                        if (MaterialEditor.Catalog is { } catalog)
                        {
                            var byHash = catalog.Shaders.ToDictionary(s => HashAlgorithms.Fnv1a(s.Name), s => s);
                            var contract = Formats.Meta.ModShapeValidator.ValidateAgainstShaders(
                                tree, h => byHash.GetValueOrDefault(h), ResolveBinName);
                            if (contract.Count > 0)
                                report = report with { Issues = report.Issues.Concat(contract).ToList() };
                        }
                        if (tree.Objects.Values.Any(o => o.ClassHash == materialClass))
                            materialTrees.Add(tree);
                    }
                    catch { /* unreadable: BinValidator already said so, above */ }

                    var alts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var i in report.Issues)
                        if (i.Category == "missing-asset" && !alts.ContainsKey(i.Detail)
                            && FindAlternative(i.Detail) is { } alt)
                            alts[i.Detail] = alt;
                    list.Add((rel, report, alts));
                }
            }

            // ---- M129: usage analysis — does the current game even LOAD each of these bins? ----
            // Old mods drag along skin*.bin files for characters the map no longer spawns, and
            // linked "skins_skin0_skin1_…" bins whose filename Riot has since changed (more skins
            // merged in). Those bins fail validation loudly but the game never requests them.
            var usage = new Dictionary<string, List<(string Kind, string Message, string Suggestion)>>(StringComparer.OrdinalIgnoreCase);

            // exact strings the CURRENT maps' shipping bins carry — spawn tables reference
            // characters by exact name string (verified on live map11.bin: 1,645 objects, all
            // character names present as plain strings)
            var mapNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rel, _, _) in list)
            {
                var segs = rel.Split('/');
                for (int i = 0; i + 1 < segs.Length; i++)
                    if ((segs[i].Equals("mapgeometry", StringComparison.OrdinalIgnoreCase)
                         || segs[i].Equals("shipping", StringComparison.OrdinalIgnoreCase))
                        && segs[i + 1].StartsWith("map", StringComparison.OrdinalIgnoreCase))
                        mapNames.Add(segs[i + 1]);
            }
            var mapExact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mapPathStrings = new List<string>();
            foreach (var map in mapNames)
            {
                string prefix = $"data/maps/shipping/{map}/";
                foreach (var e in AssetEntries)
                {
                    if (!e.IsResolved
                        || !e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || !e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var strings = new List<string>();
                        Formats.Meta.BinStringHarvester.Collect(
                            Formats.Meta.SafeBinTree.Parse(ReadAsset(e.PathHash)), strings);
                        foreach (var s in strings)
                        {
                            mapExact.Add(s);
                            if (s.Contains('/')) mapPathStrings.Add(s.ToLowerInvariant());
                        }
                    }
                    catch { /* a broken shipping bin only weakens the analysis */ }
                }
            }

            bool RiotHasPath(ulong h) =>
                _mounts is not null
                && (_mounts.Mounts.Any(m => m.Kind == AssetSourceKind.RiotReference && m.Contains(h))
                    || _mounts.Fallback.Any(f => f.Contains(h)));

            foreach (var (rel, _, _) in list)
            {
                var findings = new List<(string, string, string)>();

                bool riotHas = RiotHasPath(HashAlgorithms.WadPath(rel));
                bool referenced = list.Any(o => !o.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase)
                                                && o.Report.ReferencedPaths.Contains(rel));
                if (!riotHas && !referenced)
                    findings.Add(("unused-bin",
                        "The current game has no file at this path and nothing else in the project references it — the game never requests this bin.",
                        "Safe to delete (Delete .bin above). Typical for renamed linked bins: Riot merges more skins into 'skins_skin0_skin1_…' files and the filename changes each patch."));

                string? charName = null;
                var parts = rel.Split('/');
                for (int i = 0; i + 1 < parts.Length; i++)
                    if (parts[i].Equals("characters", StringComparison.OrdinalIgnoreCase)) { charName = parts[i + 1]; break; }
                if (charName is not null && mapExact.Count > 0
                    && !mapExact.Contains(charName)
                    && !mapPathStrings.Any(pth => pth.Contains($"characters/{charName.ToLowerInvariant()}/")))
                    findings.Add(("possibly-unused",
                        $"Character '{charName}' appears nowhere in the current map data ({string.Join(", ", mapNames)}) — the map no longer spawns it.",
                        "Probably a leftover from an older patch. If no other game mode needs it, Delete .bin above removes it from the mod."));

                if (findings.Count > 0) usage[rel] = findings;
            }

            // ---- M418: geometry against the materials that are supposed to define its surfaces ----
            // Appended AFTER the usage analysis on purpose: a custom map's .mapgeo has no Riot original
            // and nothing links to it by path, so that analysis would call it unused and offer to delete
            // it. Only files with findings are added, so a clean mapgeo stays out of the report.
            if (materialTrees.Count > 0)
            {
                var shaderOf = Formats.Meta.ModShapeValidator.MaterialShaderLookup(materialTrees, ResolveBinName);
                foreach (var folder in Project.ProjectFolders)
                {
                    string root = Path.Combine(Project.RootPath!, folder);
                    if (!Directory.Exists(root)) continue;
                    foreach (var file in Directory.EnumerateFiles(root, "*.mapgeo", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                        byte[] bytes;
                        try { bytes = File.ReadAllBytes(file); } catch { continue; }
                        var issues = Formats.Meta.ModShapeValidator.ValidateMapGeo(rel, bytes, shaderOf);
                        if (issues.Count == 0) continue;
                        list.Add((rel, new Formats.Meta.BinValidationReport(rel, 0, 0, 0, issues),
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
                    }
                }
            }

            return (list, usage);
        });
        var (reports, usage) = results;

        int bad = 0, issueCount = 0;
        foreach (var (_, r, _) in reports)
        {
            if (r.IsClean) continue;
            bad++; issueCount += r.Issues.Count;
            _log.Warn("Validate", $"{r.BinName}: {r.Issues.Count} issue(s)");
            foreach (var i in r.Issues.Take(8))
                _log.Warn("Validate", $"   [{i.Category}] {i.ObjectName} → {i.Detail}");
            if (r.Issues.Count > 8) _log.Warn("Validate", $"   … {r.Issues.Count - 8} more");
        }
        foreach (var (rel, findings) in usage)
            foreach (var f in findings)
                _log.Warn("Validate", $"{rel}: [{f.Kind}] {f.Message}");

        if (reports.Count == 0) { _log.Warn("Validate", "No .bin files found in the project folders."); return; }
        if (bad == 0 && usage.Count == 0)
        { _log.Success("Validate", $"All {reports.Count} project .bin(s) clean — every link and asset reference resolves in the injected view, and everything is still used by the current game."); return; }
        if (bad > 0)
            _log.Error("Validate", $"{bad}/{reports.Count} bin(s) have {issueCount} issue(s) — these would break in-game (details above).");
        if (usage.Count > 0)
            _log.Warn("Validate", $"{usage.Count} bin(s) look unused by the current game — see the issues window (they can be deleted there).");

        // M127: the issues are also a window now — navigable (Go To) and, where a replacement
        // exists, fixable in one click (repoint + save). No more hunting refs by hand.
        var vm = new BinIssuesWindowViewModel
        {
            BinName = $"Validate Project Bins — {bad} bin(s) with issues, {usage.Count} unused",
            Description = "Broken references the game would fail to load, checked against the injected view "
                + "(project overrides + Riot originals), plus shape checks for the defects that parse "
                + "cleanly and still break in-game — pointer instead of embedded container elements, empty "
                + "containers, half a blend equation, long-form null structs, geometry whose material the "
                + "bin never defines. Go To jumps to the object holding the reference; "
                + "where an existing replacement was found, Fix repoints every reference and saves the bin. "
                + "Bins marked unused are never requested by the current game — Delete .bin removes them. "
                + "Re-run Validate afterwards to confirm.",
        };
        foreach (var (rel, report, alts) in reports)
        {
            bool hasUsage = usage.TryGetValue(rel, out var findings);
            if (report.IsClean && !hasUsage) continue;
            bool haveEntry = TryResolveEntry(HashAlgorithms.WadPath(rel), out var binEntry);
            // M128: one group per bin, deletable — old mods often carry bins that are no longer
            // needed at all; dropping the file beats fixing its references one by one.
            var group = new BinIssueGroupViewModel
            {
                BinName = report.BinName,
                DeleteAsync = haveEntry ? async () =>
                {
                    if (PromptOwner is not null && !await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete Bin",
                        $"Delete {rel} from the project?\n\nThe mod stops overriding this file — the game will use Riot's original instead.", "Delete"))
                        return false;
                    return DeleteProjectBin(binEntry);
                } : null,
            };
            vm.Groups.Add(group);
            // M129: usage verdicts first — "this whole file is never loaded" outranks its detail issues
            if (hasUsage)
                foreach (var f in findings!)
                    group.Rows.Add(new BinIssueRowViewModel
                    {
                        Kind = f.Kind,
                        ObjectName = Path.GetFileName(rel),
                        ClassName = "file",
                        Message = f.Message,
                        Suggestion = f.Suggestion,
                    });
            foreach (var i in report.Issues)
            {
                string? alt = i.Category == "missing-asset" && alts.TryGetValue(i.Detail, out var a) ? a : null;
                group.Rows.Add(new BinIssueRowViewModel
                {
                    Kind = i.Category,
                    ObjectName = i.ObjectName,
                    ClassName = i.ObjectClassHash != 0
                        ? ResolveBinName(i.ObjectClassHash) ?? $"class 0x{i.ObjectClassHash:x8}"
                        : "file",
                    Message = i.Category switch
                    {
                        "missing-asset" => $"References {i.Detail} — it doesn't exist in the project or the game files; the game would fail to load it.",
                        "missing-dependency" => $"Dependency bin {i.Detail} doesn't exist in the injected view.",
                        _ => i.Detail,
                    },
                    Suggestion = i.Category switch
                    {
                        "missing-asset" when alt is not null =>
                            $"An existing file matches: {alt}",
                        "missing-asset" =>
                            "No replacement found automatically — bring the file into the project at exactly this path, or repoint the reference in the editor.",
                        "missing-link" =>
                            "The linked object exists in none of this bin's dependency bins — usually a stale link from an older patch.",
                        "missing-dependency" =>
                            "The game hard-requires listed dependencies. Bring the bin into the project, or remove the dependency entry.",
                        _ => "",
                    },
                    GoTo = haveEntry && i.ObjectPathHash != 0
                        ? () => _ = NavigateToBinObjectAsync(binEntry, i.ObjectPathHash, i.ObjectClassHash)
                        : null,
                    FixLabel = alt is not null ? $"🔧 Repoint to {alt}" : null,
                    FixAsync = haveEntry && alt is not null
                        ? () => RepointAssetRefAsync(binEntry, i.Detail, alt)
                        : null,
                });
            }
        }
        ShowBinIssuesWindow(vm);
    }

    /// <summary>M127: jump to a bin object in its natural editor — VFX systems open in the Particle
    /// Editor, everything else opens in the Map Bin Editor window with the object selected.
    /// M130: the Map Bin Editor replaced the old Materials-tab navigation — the issues window is owned
    /// by the main window, so the main window can never rise above it (the "Go To does nothing" bug);
    /// sibling windows can. It also handles EVERY object class, not just materials
    /// (SkinCharacterDataProperties in a skin bin has no material to search for).</summary>
    private Task NavigateToBinObjectAsync(WadAssetEntry entry, uint objHash, uint classHash)
    {
        if (classHash == HashAlgorithms.Fnv1a("VfxSystemDefinitionData"))
        {
            OpenParticleEditorFor(entry);
            if (ParticleEditor.Systems.FirstOrDefault(s => s.Entry.PathHash == objHash) is { } node)
                ParticleEditor.SelectedSystem = node;
            return Task.CompletedTask;
        }
        try
        {
            if (MapBinEditor.Entry?.PathHash != entry.PathHash)
                MapBinEditor.Load(entry, ReadAsset(entry.PathHash));
            ShowMapBinEditorWindow?.Invoke();
            if (!MapBinEditor.SelectObject(objHash))
                _log.Warn("Validate", $"Object 0x{objHash:x8} not found in {entry.DisplayName} — it may live in a dependency bin.");
        }
        catch (Exception ex) { _log.Error("Validate", $"{entry.DisplayName}: {ex.Message}"); }
        return Task.CompletedTask;
    }

    /// <summary>M127: replace every reference to a dead asset path with an existing one, then save the
    /// bin through the normal pipeline (in place for folder projects; shadows dissolve).</summary>
    private async Task<bool> RepointAssetRefAsync(WadAssetEntry entry, string fromPath, string toPath)
    {
        byte[] bytes;
        try { bytes = ReadAsset(entry.PathHash); }
        catch (Exception ex) { _log.Error("Validate", $"{entry.DisplayName}: {ex.Message}"); return false; }
        LeagueToolkit.Core.Meta.BinTree tree;
        try { tree = Formats.Meta.SafeBinTree.Parse(bytes); }
        catch (Exception ex) { _log.Error("Validate", $"{entry.DisplayName}: {ex.Message}"); return false; }

        int hits = Formats.Meta.BinAssetRepointer.Repoint(tree, fromPath, toPath);
        if (hits == 0) { _log.Warn("Validate", $"{entry.DisplayName}: no reference to {fromPath} found — already fixed?"); return false; }

        using var ms = new MemoryStream();
        tree.Write(ms);
        if (!await SaveMapBinBytesAsync(entry, ms.ToArray())) return false;
        _log.Success("Validate", $"{entry.DisplayName}: repointed {hits} reference(s) {fromPath} → {toPath}.");
        if (MaterialEditor.BinEntry?.PathHash == entry.PathHash)
            await LoadMaterialBinAsync(entry, alsoRawBin: false);   // refresh the open editor
        return true;
    }

    /// <summary>M128: remove a project bin entirely — the mod stops overriding it and the game falls
    /// back to Riot's original. Deletes the project file AND any shadow override, then rescans.</summary>
    private bool DeleteProjectBin(WadAssetEntry entry)
    {
        bool any = false;
        try
        {
            if (_mounts is not null && _mounts.TryGet(entry.PathHash, out var a))
                foreach (var src in new[] { a.Source }.Concat(a.AllSources).Distinct())
                    if (src is { Kind: AssetSourceKind.ProjectFolder or AssetSourceKind.ProjectOverride }
                        && src.TryGetFilePath(entry.PathHash, out var f) && File.Exists(f))
                    {
                        try { File.Delete(f); any = true; }
                        catch (Exception ex) { _log.Error("Validate", $"{f}: {ex.Message}"); }
                    }
            try
            {
                var orphan = Path.Combine(ProjectWorkspace.OverridesDir(Project), $"{entry.PathHash:x16}.bin");
                if (File.Exists(orphan)) { File.Delete(orphan); any = true; }
            }
            catch { }
            _overrides.Remove(entry.PathHash);
            if (!any) { _log.Warn("Validate", $"{entry.DisplayName}: no project file found to delete."); return false; }

            Project.IsDirty = true;
            if (MaterialEditor.BinEntry?.PathHash == entry.PathHash) { MaterialEditor.Clear(); HasMaterialData = false; }
            if (MeshPreview.MaterialEditor.BinEntry?.PathHash == entry.PathHash) ForgetPreviewSkin();   // M642
            RefreshBrowser();
            _log.Success("Validate", $"Deleted {entry.DisplayName} from the project — the game will use the original file.");
            return true;
        }
        catch (Exception ex) { _log.Error("Validate", ex.Message); return false; }
    }

    /// <summary>M134: Overlay Footprint — how many game WADs will loaders have to patch for this mod?
    /// Shared-path assets (characters/items) exist in dozens of WADs; a texture-heavy map mod can force
    /// 200+ patches. That is an install-time and merge-complexity cost, NOT a crash cause — the crash it
    /// was blamed for is a non-cubemap texture behind a MapCubemapProbe (see CubemapProbeValidator).</summary>
    [RelayCommand]
    private async Task OverlayFootprint()
    {
        if (!ContentLoaded || Project.RootPath is null || Project.ProjectFolders.Count == 0)
        { _log.Warn("Footprint", "Open a folder project first."); return; }
        string gameFinal = Path.Combine(
            (Project.GameDirectory ?? "").Replace('/', Path.DirectorySeparatorChar), "DATA", "FINAL");
        if (!Directory.Exists(gameFinal))
        { _log.Warn("Footprint", "Game folder not set (Project ▸ Set Game Folder) — the analysis scans the game's WADs."); return; }

        IsBuilding = true;
        var progress = BuildProgressSink();
        try
        {
            var fp = await Task.Run(() =>
            {
                var files = new List<(ulong Hash, string RelPath, long Bytes)>();
                foreach (var f in Project.ProjectFolders)
                {
                    var root = Project.ResolveProjectPath(f);
                    if (!Directory.Exists(root)) continue;
                    foreach (var (hash, path) in Core.Build.WadPackService.EnumerateChunkFiles(root))
                    {
                        var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                        files.Add((hash, rel, new FileInfo(path).Length));
                    }
                }
                return Core.Build.OverlayFootprintService.Analyze(files, gameFinal, progress);
            });
            _log.Info("Footprint", $"{fp.ProjectFiles:n0} file(s) → loaders patch {fp.TouchedWads} of {fp.GameWadsScanned} game WADs.");
            foreach (var s in fp.TopSources.Take(5))
                _log.Info("Footprint", $"   {s.Folder}: touches {s.WadsTouched} WAD(s) ({s.Files:n0} files, {s.Bytes / 1048576.0:0.0} MB)");
            var win = new Views.OverlayFootprintWindow
            { DataContext = OverlayFootprintWindowViewModel.From(fp) };
            if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
        }
        catch (Exception ex) { _log.Error("Footprint", ex.Message); }
        finally { IsBuilding = false; }
    }

    /// <summary>M136: Asset Usage — which project files can nothing ever load (dead: not shipped by
    /// any game wad, referenced by no project bin — deletable) and which belong to other content
    /// (outside-map: the wad fan-out drivers). Completes the trim workflow the Overlay Footprint opens.</summary>
    [RelayCommand]
    private async Task AssetUsage()
    {
        if (!ContentLoaded || Project.RootPath is null || Project.ProjectFolders.Count == 0)
        { _log.Warn("AssetUsage", "Open a folder project first."); return; }
        string gameFinal = Path.Combine(
            (Project.GameDirectory ?? "").Replace('/', Path.DirectorySeparatorChar), "DATA", "FINAL");
        if (!Directory.Exists(gameFinal))
        { _log.Warn("AssetUsage", "Game folder not set (Project ▸ Set Game Folder) — the analysis scans the game's WADs."); return; }

        IsBuilding = true;
        var progress = BuildProgressSink();
        try
        {
            var report = await Task.Run(() =>
            {
                var files = new List<(ulong Hash, string RelPath, string AbsPath, long Bytes)>();
                var mapNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Project.ProjectFolders)
                {
                    var root = Project.ResolveProjectPath(f);
                    if (!Directory.Exists(root)) continue;
                    // The mount folder is NAMED after the wad it targets (cslol/fantome convention:
                    // "Map11" -> Map11.wad.client) — the reliable home-wad source even for
                    // assets-only projects that carry no data/maps paths at all.
                    var folderName = Path.GetFileName(root.TrimEnd('/', '\\'));
                    if (folderName.Length > 0 && folderName != ".") mapNames.Add(folderName);
                    foreach (var (hash, path) in Core.Build.WadPackService.EnumerateChunkFiles(root))
                    {
                        var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                        files.Add((hash, rel, path, new FileInfo(path).Length));

                        var segs = rel.Split('/');
                        for (int i = 0; i + 1 < segs.Length; i++)
                            if ((segs[i].Equals("mapgeometry", StringComparison.OrdinalIgnoreCase)
                                 || segs[i].Equals("shipping", StringComparison.OrdinalIgnoreCase))
                                && segs[i + 1].StartsWith("map", StringComparison.OrdinalIgnoreCase))
                                mapNames.Add(segs[i + 1]);

                        if (rel.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && rel.Contains('/'))
                        {
                            try
                            {
                                var strings = new List<string>();
                                Formats.Meta.BinStringHarvester.Collect(
                                    Formats.Meta.SafeBinTree.Parse(File.ReadAllBytes(path)), strings);
                                foreach (var s in strings)
                                    if (s.Contains('/') || s.Contains('\\')) referenced.Add(s.Replace('\\', '/'));
                            }
                            catch { /* a broken bin just contributes no references */ }
                        }
                    }
                }
                return Core.Build.AssetUsageService.Analyze(files, gameFinal, mapNames,
                    rel => referenced.Contains(rel), progress);
            });

            _log.Info("AssetUsage", $"{report.TotalFiles:n0} file(s): {report.Dead.Count:n0} dead ({report.DeadBytes / 1048576.0:0.0} MB), "
                + $"{report.OutsideMapFiles:n0} outside the map ({report.OutsideMapBytes / 1048576.0:0.0} MB), {report.MapScopedFiles:n0} map-scoped.");

            var vm = AssetUsageWindowViewModel.Build(report, async () =>
            {
                if (PromptOwner is null) return 0;
                if (!await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete Dead Files",
                    $"Delete {report.Dead.Count:n0} dead file(s) ({report.DeadBytes / 1048576.0:0.0} MB)?\n\nNo game wad ships these paths and no project bin references them — nothing can ever load them.",
                    "Delete"))
                    return 0;
                int n = 0;
                foreach (var d in report.Dead)
                {
                    try { File.Delete(d.AbsPath); n++; }
                    catch (Exception ex) { _log.Warn("AssetUsage", $"{d.RelPath}: {ex.Message}"); }
                }
                _log.Success("AssetUsage", $"Deleted {n:n0} dead file(s).");
                RefreshBrowser();
                return n;
            });
            var win = new Views.AssetUsageWindow { DataContext = vm };
            if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
        }
        catch (Exception ex) { _log.Error("AssetUsage", ex.Message); }
        finally { IsBuilding = false; }
    }

    /// <summary>M309: route every normal-map MapSkin slot and alias through another registered skin's
    /// safe environment fields. Runtime/server data stays with each slot; Map22/TFT is blocked.</summary>
    [RelayCommand]
    private async Task OpenMapSkinSwitcher()
    {
        if (!ProjectMode || Project.RootPath is null || _mounts is null)
        {
            _log.Warn("MapSkin", "Open an editable project first. Riot reference files are never modified directly.");
            return;
        }

        Status = "Scanning registered map skins...";
        try
        {
            var entries = AssetEntries
                .Where(entry => entry.IsResolved && TryParseShippingMapBin(entry.Path, out _))
                .DistinctBy(entry => entry.PathHash)
                .ToList();
            var maps = await Task.Run(() =>
            {
                var found = new List<MapSkinMapViewModel>();
                foreach (var entry in entries)
                {
                    if (!TryParseShippingMapBin(entry.Path, out int mapId)) continue;
                    try
                    {
                        var catalog = MapSkinSwitcher.ReadCatalog(ReadAsset(entry.PathHash), ResolveBinName);
                        if (MapSkinSwitcher.BlockReason(mapId, catalog.MapStringId) is { } blocked)
                        {
                            _log.Info("MapSkin", $"Map{mapId} excluded: {blocked}");
                            continue;
                        }
                        found.Add(new MapSkinMapViewModel
                        {
                            MapId = mapId,
                            ShippingBinEntry = entry,
                            Catalog = catalog,
                        });
                    }
                    catch (Exception ex) { _log.Warn("MapSkin", $"Could not inspect {entry.Path}: {ex.Message}"); }
                }
                return found;
            });
            if (maps.Count == 0)
            {
                Status = "No eligible shipping-map bins are mounted in this project.";
                _log.Warn("MapSkin", Status);
                return;
            }

            var vm = new MapSkinSwitcherViewModel(maps) { ApplySwap = ApplyMapSkinSwapAsync };
            var window = new Views.MapSkinSwitcherWindow { DataContext = vm };
            if (PromptOwner is not null) window.Show(PromptOwner); else window.Show();
            Status = $"Map Skin Switcher: {maps.Count} eligible map(s). TFT / Map22 excluded.";
        }
        catch (Exception ex)
        {
            Status = "Map Skin Switcher could not open.";
            _log.Error("MapSkin", ex.Message);
        }
    }

    private static bool TryParseShippingMapBin(string path, out int mapId)
    {
        mapId = 0;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5
            || !parts[0].Equals("data", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals("maps", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("shipping", StringComparison.OrdinalIgnoreCase)
            || !parts[3].StartsWith("map", StringComparison.OrdinalIgnoreCase)
            || !parts[4].Equals(parts[3] + ".bin", StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(parts[3].AsSpan(3), out mapId);
    }

    private sealed record MapSkinPreflight(
        int SkinAssetCount,
        string? ContainerBin,
        int ShippingLinks,
        int ShippingAssets,
        int UnrelatedShippingIssues,
        int ContainerLinks,
        int ContainerAssets);

    private async Task<string> ApplyMapSkinSwapAsync(MapSkinApplyRequest request)
    {
        if (!ProjectMode || Project.RootPath is null || _mounts is null)
            throw new InvalidOperationException("The project was closed while the map-skin tool was open.");
        if (!TryResolveEntry(request.Map.ShippingBinEntry.PathHash, out var shippingEntry))
            throw new FileNotFoundException("The shipping map bin is no longer mounted.");
        if (!await EnsureProjectSavedAsync()) throw new InvalidOperationException("Save the project before creating the override.");

        byte[] original = ReadAsset(shippingEntry.PathHash);
        var swap = await Task.Run(() => MapSkinSwitcher.Switch(original, request.Map.MapId,
            request.Target.Info.PathHash, request.Source.Info.PathHash, ResolveBinName,
            request.CarryCharacterSkins));   // M649: the turret/minion/nexus skins, when asked for

        string sourceContainerPath = MapSkinSwitcher.ContainerBinPath(swap.Source.MapContainerLink)
            ?? throw new InvalidDataException("The selected source skin has no materials container.");
        if (!TryResolveEntry(HashAlgorithms.WadPath(sourceContainerPath), out var sourceContainerEntry))
            throw new FileNotFoundException($"The source skin's map-container bin is missing: {sourceContainerPath}");
        byte[] sourceContainerOriginal = ReadAsset(sourceContainerEntry.PathHash);

        MapSkinContainerCompatibilityResult compatibility;
        string? targetContainerPath = MapSkinSwitcher.ContainerBinPath(swap.Target.MapContainerLink);
        if (targetContainerPath is not null)
        {
            if (!TryResolveEntry(HashAlgorithms.WadPath(targetContainerPath), out var targetContainerEntry))
                throw new FileNotFoundException($"The current/base skin's map-container bin is missing: {targetContainerPath}");
            byte[] targetContainerBytes = ReadAsset(targetContainerEntry.PathHash);
            compatibility = await Task.Run(() => MapSkinSwitcher.BuildCompatibleContainer(
                targetContainerBytes, sourceContainerOriginal));
        }
        else compatibility = new MapSkinContainerCompatibilityResult(sourceContainerOriginal, 0, 0);

        var preflight = await Task.Run(() => ValidateMapSkinSwap(shippingEntry.Path,
            swap, sourceContainerPath, compatibility.Bytes));

        string safeRoute = SanitizeFileName($"Map{request.Map.MapId}-{swap.Target.Name}-to-{swap.Source.Name}");
        string backupDir = Path.Combine(Project.RootPath, ".reyengine", "backups",
            $"map-skin-{safeRoute}-{DateTime.Now:yyyyMMdd-HHmmss}");
        string backupFile = Path.Combine(backupDir,
            shippingEntry.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        File.WriteAllBytes(backupFile, original);
        string containerBackup = Path.Combine(backupDir,
            sourceContainerEntry.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(containerBackup)!);
        File.WriteAllBytes(containerBackup, sourceContainerOriginal);
        if (Project.ProjectFilePath is { } projectFile && File.Exists(projectFile))
            File.Copy(projectFile, Path.Combine(backupDir, "project.before-map-skin.json"), overwrite: true);

        if (!await SaveGeneratedMapSkinBinAsync(shippingEntry, swap.Bytes))
            throw new IOException("The validated shipping bin could not be saved to the project.");
        if (!await SaveGeneratedMapSkinBinAsync(sourceContainerEntry, compatibility.Bytes))
            throw new IOException("The gameplay-compatible source container could not be saved to the project.");
        // M730: remember WHAT was done, not only the result, so the next Riot patch does it again to the new
        // original - the slots that patch adds included. Recorded from the ORIGINAL bytes, so the snapshot is the
        // source slot as Riot shipped it.
        var recipe = MapSkinForceRecipe.Record(original, request.Map.MapId, request.Target.Info.PathHash,
            request.Source.Info.PathHash, request.CarryCharacterSkins, ResolveBinName);
        BinRecipeRecord.Upsert(Project.BinRecipes, recipe.ToRecord(shippingEntry.Path, Project.RiotPatchVersion, "switcher"));
        _overrides.SaveTo(Project);
        ReyProjectService.Save(Project, Project.ProjectFilePath!);
        BuildMounts();
        BuildProjectTree();
        UpdateTitle();

        string reportDir = ProjectWorkspace.ReportsDir(Project);
        string reportFile = Path.Combine(reportDir, $"map-skin-{safeRoute}.txt");
        var lines = new List<string>
        {
            $"Project: {Project.Name}",
            $"UTC: {DateTime.UtcNow:O}",
            $"Map: Map{request.Map.MapId} ({request.Map.Catalog.MapStringId})",
            $"Requested base skin: {swap.Target.Name} [{swap.Target.ObjectPath}]",
            $"Previous base container: {swap.Target.MapContainerLink ?? "legacy/default"}",
            $"Source skin: {swap.Source.Name} [{swap.Source.ObjectPath}]",
            $"Source container: {swap.Source.MapContainerLink ?? "legacy/default"}",
            $"MapSkin definitions rerouted (registered + aliases): {swap.RoutedSkinHashes.Count:n0}",
            $"Changed environment-route properties: {swap.ChangedRouteProperties:n0}",
            // M649: what happened to the turret/minion/nexus skins, named because it is the one
            // part of a swap a user notices immediately and the tool used to say nothing about.
            $"Character skins: {(request.CarryCharacterSkins
                ? swap.CarriedCharacterSkins.Count == 0
                    ? $"{swap.Source.Name} forces none, so nothing was carried"
                    : $"carried {swap.CarriedCharacterSkins.Count:n0} from {swap.Source.Name} "
                      + $"(replaced on {swap.CharacterSkinSlotsRouted:n0} slot(s), added to {swap.CharacterSkinSlotsAdded:n0})"
                : "left with each slot (turrets, minions and nexus stay as the base skin has them)")}",
            $"Audio profile: {(swap.RoutedAudioSourceHash is null
                ? "no dedicated source profile"
                : swap.ChangedAudioProperties > 0
                    ? $"routed ({swap.ChangedAudioProperties:n0} properties)"
                    : "already routed")}",
            $"Server gameplay placeables matched: {compatibility.MatchedServerPlaceables:n0}",
            $"Server gameplay keys remapped: {compatibility.RemappedServerPlaceableKeys:n0}",
            $"Verified skin-level files: {preflight.SkinAssetCount:n0}",
            $"Verified container bin: {preflight.ContainerBin ?? "not used by this legacy skin"}",
            $"Shipping bin validation: {preflight.ShippingLinks:n0} links, {preflight.ShippingAssets:n0} assets, 0 routed-slot issues",
            $"Unchanged Riot objects with validator warnings: {preflight.UnrelatedShippingIssues:n0}",
            $"Container validation: {preflight.ContainerLinks:n0} links, {preflight.ContainerAssets:n0} assets, 0 issues",
            $"Backup: {backupDir}",
            // M730
            $"Remembered for patch updates: {recipe.Describe()} - every Riot patch re-applies this switch to the new "
            + "original map bin, slots that patch adds included, instead of carrying today's values across.",
        };
        string? reportWarning = null;
        try { File.WriteAllLines(reportFile, lines); }
        catch (Exception ex)
        {
            reportWarning = $" The override is saved, but its report could not be written: {ex.Message}";
            _log.Warn("MapSkin", reportWarning);
        }

        string message = $"Ready: Map{request.Map.MapId}'s slots and aliases now use {swap.Source.Name}'s crash-safe environment"
            + (swap.RoutedAudioSourceHash is not null ? ", music and ambience. " : ". ")
            + $"Preserved {compatibility.MatchedServerPlaceables:n0} server gameplay identities. "
            + $"Verified {preflight.ShippingLinks + preflight.ContainerLinks:n0} links and "
            + $"{preflight.ShippingAssets + preflight.ContainerAssets:n0} asset references; backup written."
            + " Remembered for patch updates."
            + (reportWarning is null ? " Report written." : reportWarning);
        Status = message;
        _log.Success("MapSkin", message);
        return message;
    }

    private async Task<bool> SaveGeneratedMapSkinBinAsync(WadAssetEntry entry, byte[] bytes)
    {
        try { _ = SafeBinTree.Parse(bytes); }
        catch (Exception ex) { _log.Error("MapSkin", $"Generated {entry.DisplayName} failed to re-parse: {ex.Message}"); return false; }

        if (TryWriteToProjectFile(entry, bytes, out _))
        {
            Project.IsDirty = true;
            return true;
        }
        if (TryPlaceInProjectFolder(entry, bytes, out _))
        {
            Project.IsDirty = true;
            return true;
        }
        return await SaveMapBinBytesAsync(entry, bytes);
    }

    private MapSkinPreflight ValidateMapSkinSwap(string shippingPath, MapSkinSwapResult swap,
        string containerPath, byte[] containerBytes)
    {
        bool AssetExists(string path) => TryResolveEntry(HashAlgorithms.WadPath(path), out _);
        List<byte[]> Dependencies(byte[] bytes)
        {
            var result = new List<byte[]>();
            foreach (var dependency in VfxSystemResolver.ExtractDependencies(bytes))
            {
                if (!TryResolveEntry(HashAlgorithms.WadPath(dependency), out var entry))
                    throw new InvalidDataException($"Required dependency is not mounted: {dependency}");
                result.Add(ReadAsset(entry.PathHash));
            }
            return result;
        }
        bool LinkExempt(uint hash) => ResolveBinName(hash)?.StartsWith("Shaders/", StringComparison.OrdinalIgnoreCase) == true;

        var assetPaths = MapSkinSwitcher.AssetPaths(swap.ReferencedStrings);
        var missing = assetPaths.Where(path => !AssetExists(path)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"The selected skin is incomplete in this client: {missing.Count:n0} referenced file(s) are missing. First: {missing[0]}");

        var shippingReport = BinValidator.Validate(shippingPath, swap.Bytes, Dependencies(swap.Bytes),
            AssetExists, ResolveBinName, LinkExempt);
        var routedHashes = swap.RoutedSkinHashes.ToHashSet();
        if (swap.RoutedAudioTargetHash is { } audioHash) routedHashes.Add(audioHash);
        var routedIssues = shippingReport.Issues.Where(issue => routedHashes.Contains(issue.ObjectPathHash)).ToList();
        if (routedIssues.Count > 0)
            throw new InvalidDataException($"A routed map-skin slot failed injection validation: "
                + $"{routedIssues[0].Category}: {routedIssues[0].Detail}");

        var tree = SafeBinTree.Parse(containerBytes);
        uint containerHash = HashAlgorithms.Fnv1a(swap.Source.MapContainerLink!);
        if (!tree.Objects.TryGetValue(containerHash, out var container)
            || container.ClassHash != HashAlgorithms.Fnv1a("MapContainer"))
            throw new InvalidDataException($"{containerPath} does not contain the required MapContainer {swap.Source.MapContainerLink}.");

        var report = BinValidator.Validate(containerPath, containerBytes, Dependencies(containerBytes),
            AssetExists, ResolveBinName, LinkExempt);
        if (!report.IsClean)
            throw new InvalidDataException($"The source MapContainer failed injection validation: "
                + $"{report.Issues[0].Category}: {report.Issues[0].Detail}");
        int containerLinks = report.LinksChecked;
        int containerAssets = report.AssetRefsChecked;

        return new MapSkinPreflight(assetPaths.Count, containerPath, shippingReport.LinksChecked,
            shippingReport.AssetRefsChecked, shippingReport.Issues.Count, containerLinks, containerAssets);
    }

    /// <summary>M97c: rebase every project .bin from the patch the mod was built for onto the current
    /// patch (CommunityDragon old original + M97a three-way merge).</summary>
    [RelayCommand]
    private void OpenPatchUpdateWizard()
    {
        if (!ContentLoaded) { _log.Warn("PatchUpdate", "Open a project first."); return; }
        if (Project.RootPath is null || Project.ProjectFolders.Count + Project.ProjectWads.Count == 0)
        { _log.Warn("PatchUpdate", "The wizard needs an editable folder project or project WAD."); return; }

        var patchProject = Project;
        var installed = RiotPatchVersionDetector.Detect(patchProject.GameDirectory);

        var vm = CreatePatchUpdateViewModel(patchProject, installed?.Patch);

        var win = new Views.PatchUpdateWindow { DataContext = vm };
        if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
        _ = vm.InitAsync();
    }

    private PatchUpdateWindowViewModel CreatePatchUpdateViewModel(ReyProject patchProject, string? targetPatch)
    {
        bool Active() => ReferenceEquals(Project, patchProject);
        var vm = new PatchUpdateWindowViewModel
        {
            SelectedPatch = patchProject.RiotPatchVersion,
            TargetPatch = targetPatch,
            ListPatches = Services.CommunityDragonClient.ListPatchesAsync,
            DownloadOld = (patch, rel) => Services.CommunityDragonClient.DownloadBinAsync(
                patch, rel, Services.CommunityDragonClient.DefaultCacheDir),
            ReadCurrentOriginal = entry => Active() ? ReadRiotOriginalBytes(entry) : null,
            ReadProjectBytes = hash => Active() ? ReadAsset(hash)
                : throw new InvalidOperationException("A different project was opened while the patch update was running."),
            SaveBytes = (entry, bytes) => Active() ? SaveMapBinBytesAsync(entry, bytes) : Task.FromResult(false),
            RunValidate = () => Active() ? ValidateProjectBins() : Task.CompletedTask,
            Resolve = ResolveBinName,
            // M730: the bins the Map Skin Switcher made are re-done on the new original, not diffed across.
            RecipeFor = rel => BinRecipeRecord.Find(patchProject.BinRecipes, rel),
            RecipeInferred = (rel, record) =>
            {
                BinRecipeRecord.Upsert(patchProject.BinRecipes, record);
                patchProject.IsDirty = true;
                _log.Info("PatchUpdate", $"{rel}: no recipe was recorded for this forced map bin, so one was read out of the file "
                                         + $"({record.SourceSkin}{(record.CarryCharacterSkins ? ", character skins carried" : "")}) and remembered.");
            },
        };

        string route = $"{patchProject.RiotPatchVersion ?? "unknown"}-to-{targetPatch ?? "unknown"}";
        string backupDir = Path.Combine(patchProject.RootPath!, ".reyengine", "backups",
            $"patch-update-{route}-{DateTime.Now:yyyyMMdd-HHmmss}");
        vm.BackupDirectory = backupDir;
        vm.Backup = (row, bytes) =>
        {
            try
            {
                string rel = row.ProjectRel.Length > 0 ? row.ProjectRel : row.Rel;
                var dest = Path.Combine(backupDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, bytes);
                return dest;
            }
            catch (Exception ex)
            {
                _log.Warn("PatchUpdate", $"Backup of {row.Rel} failed: {ex.Message}");
                return null;
            }
        };

        int projectFiles = 0;
        var binHashes = new HashSet<ulong>();
        foreach (var folder in patchProject.ProjectFolders)
        {
            string root = Path.Combine(patchProject.RootPath!, folder);
            if (!Directory.Exists(root)) continue;
            string[] projectBins;
            try
            {
                projectFiles += Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count();
                projectBins = Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                _log.Warn("PatchUpdate", $"Could not scan {root}: {ex.Message}");
                continue;
            }
            foreach (var file in projectBins)
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!rel.Contains('/')) continue;
                ulong hash = HashAlgorithms.WadPath(rel);
                if (binHashes.Add(hash) && TryResolveEntry(hash, out var entry))
                    vm.Bins.Add(new PatchUpdateBinRowViewModel
                    { Rel = rel, ProjectRel = $"{folder}/{rel}", Entry = entry });
            }
        }

        // Packed editable WAD projects use hash overrides for rebased bins; BuildProjectCore applies
        // those overrides while repacking, so they can participate without destructively unpacking the WAD.
        if (_mounts is not null)
            foreach (var wad in _mounts.Mounts.OfType<WadMount>().Where(m => m.Kind == AssetSourceKind.ProjectWad))
            {
                projectFiles += wad.Archive.Entries.Count;
                foreach (var sourceEntry in wad.Archive.Entries)
                {
                    if (!sourceEntry.IsResolved || !sourceEntry.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                        || !sourceEntry.Path.Contains('/') || !binHashes.Add(sourceEntry.PathHash))
                        continue;
                    if (TryResolveEntry(sourceEntry.PathHash, out var entry))
                        vm.Bins.Add(new PatchUpdateBinRowViewModel
                        {
                            Rel = sourceEntry.Path,
                            ProjectRel = $"{wad.Name}/{sourceEntry.Path}",
                            Entry = entry,
                        });
                }
            }
        vm.RetainedAssetCount = Math.Max(0, projectFiles - vm.Bins.Count);
        vm.RunCompleted = result => CompletePatchUpdateAsync(patchProject, vm, result);
        return vm;
    }

    private async Task CompletePatchUpdateAsync(ReyProject patchProject, PatchUpdateWindowViewModel vm,
        PatchUpdateRunResult result)
    {
        if (!result.Success)
        {
            _log.Error("PatchUpdate", result.Summary);
            return;
        }
        if (vm.TargetPatch is not { } targetPatch)
        {
            _log.Warn("PatchUpdate", "The files were updated, but the installed Riot patch could not be detected; the project baseline was not changed.");
            return;
        }

        try
        {
            string backupDir = vm.BackupDirectory ?? Path.Combine(patchProject.RootPath!, ".reyengine", "backups",
                $"patch-update-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(backupDir);
            if (patchProject.ProjectFilePath is { } projectFile && File.Exists(projectFile))
                File.Copy(projectFile, Path.Combine(backupDir, "project.before-update.json"), overwrite: true);

            string? oldPatch = patchProject.RiotPatchVersion;
            patchProject.RiotPatchVersion = targetPatch;
            patchProject.LastPatchUpdateUtc = DateTime.UtcNow.ToString("O");
            patchProject.LastPatchUpdateSummary = result.Summary;
            patchProject.LastPatchBackupDirectory = backupDir;
            patchProject.PatchUpdateNeedsReview = result.NeedsReview;
            if (oldPatch is not null
                && RiotPatchVersionDetector.TryNormalize(patchProject.ModVersion, out var modPatch)
                && string.Equals(modPatch, oldPatch, StringComparison.Ordinal))
                patchProject.ModVersion = targetPatch + ".0";
            if (ReferenceEquals(Project, patchProject)) _overrides.SaveTo(patchProject);
            if (patchProject.ProjectFilePath is { } path) ReyProjectService.Save(patchProject, path);

            string reports = ProjectWorkspace.ReportsDir(patchProject);
            string reportFile = Path.Combine(reports, $"patch-update-{oldPatch ?? "unknown"}-to-{targetPatch}.txt");
            var reportLines = new List<string>
            {
                $"Project: {patchProject.Name}",
                $"Patch: {oldPatch ?? "unknown"} -> {targetPatch}",
                $"UTC: {patchProject.LastPatchUpdateUtc}",
                $"Backup: {backupDir}",
                $"Other retained assets: {vm.RetainedAssetCount:n0}",
                "",
                result.Summary,
                "",
            };
            foreach (var row in vm.Bins)
            {
                reportLines.Add($"[{row.Status}] {row.ProjectRel}");
                if (row.Detail.Length > 0)
                    reportLines.AddRange(row.Detail.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                        .Select(line => "  " + line));
            }
            File.WriteAllLines(reportFile, reportLines);

            _log.Success("PatchUpdate", $"{patchProject.Name}: {oldPatch ?? "unknown"} -> {targetPatch}. {result.Summary}");
            if (ReferenceEquals(Project, patchProject))
            {
                RefreshBrowser();
                UpdateTitle();
                if (patchProject.AutoBuildAfterPatchUpdate) await BuildUpdatedProjectArtifactsAsync(patchProject);
            }
        }
        catch (Exception ex) { _log.Error("PatchUpdate", $"Could not finalize patch tracking: {ex.Message}"); }
    }

    private async Task<bool> BuildUpdatedProjectArtifactsAsync(ReyProject patchProject)
    {
        if (!ReferenceEquals(Project, patchProject) || patchProject.RootPath is null) return false;
        var buildRoot = patchProject.OutputDirectory ?? Path.Combine(patchProject.RootPath, "Build");
        if (BuildSafety.IsInsideGameInstall(buildRoot))
        {
            patchProject.PatchUpdateNeedsReview = true;
            patchProject.LastPatchUpdateSummary += " Automatic build was blocked because its output folder is inside the Riot install.";
            if (patchProject.ProjectFilePath is { } path) ReyProjectService.Save(patchProject, path);
            _log.Error("PatchUpdate", "Automatic build blocked because the output folder is inside the Riot install.");
            return false;
        }

        string name = patchProject.EffectiveModName;
        string author = string.IsNullOrWhiteSpace(patchProject.ModAuthor) ? "Unknown" : patchProject.ModAuthor!;
        string fantomePath = Path.Combine(buildRoot, SanitizeFileName($"{name} by {author}.fantome"));
        var meta = new FantomeMeta
        {
            Name = name,
            Author = author,
            Version = string.IsNullOrWhiteSpace(patchProject.ModVersion) ? "1.0.0" : patchProject.ModVersion,
            Description = patchProject.ModDescription ?? "",
            Heart = patchProject.ModHeart,
            Home = patchProject.ModHome,
        };
        var thumbnail = LoadThumbnailPng(patchProject.ThumbnailPath);
        IsBuilding = true;
        Status = $"Rebuilding {name} for Riot {patchProject.RiotPatchVersion}...";
        var progress = BuildProgressSink();
        try
        {
            Directory.CreateDirectory(buildRoot);
            await Task.Run(() =>
            {
                var wads = BuildProjectCore(buildRoot, progress);
                if (wads.Count == 0) throw new InvalidOperationException("No WAD was produced from the updated project.");
                progress.Report((0.98, $"Packaging {Path.GetFileName(fantomePath)}..."));
                FantomeExporter.Export(meta, wads, thumbnail, fantomePath);
            });
            Status = $"Updated package ready: {Path.GetFileName(fantomePath)}";
            _log.Success("PatchUpdate", $"Automatically rebuilt WAD output and {fantomePath} ({new FileInfo(fantomePath).Length / 1048576.0:0.0} MB).");
            return true;
        }
        catch (Exception ex)
        {
            patchProject.PatchUpdateNeedsReview = true;
            patchProject.LastPatchUpdateSummary += $" Automatic build failed: {ex.Message}";
            if (patchProject.ProjectFilePath is { } path) ReyProjectService.Save(patchProject, path);
            _log.Error("PatchUpdate", $"The project update succeeded, but automatic packaging failed: {ex.Message}");
            return false;
        }
        finally { IsBuilding = false; }
    }

    private async Task CheckForAutomaticPatchUpdateAsync(ReyProject openedProject)
    {
        // M590: index the project's own asset paths BEFORE anything reads its bins, so a mod's custom
        // texture resolves from its WadChunkLink instead of showing the author a bare hash.
        try
        {
            int learned = RegisterProjectAssetPaths();
            if (learned > 0) _log.Info("Project", $"Indexed {learned:n0} project asset path(s) for hash lookup.");
        }
        catch (Exception ex) { _log.Info("Project", $"Project path indexing skipped: {ex.Message}"); }

        try { await CheckForAutomaticPatchUpdateCoreAsync(openedProject); }
        catch (Exception ex)
        {
            _log.Error("PatchUpdate", $"Automatic patch update stopped safely: {ex.Message}");
        }

        // M590: and AFTER any rebase, align asset wire forms with the installed patch.
        //
        // A rebase handles this for a project that is behind, but one already ON the current patch never
        // merges and so never gets it - which is most of them, since the flip happened mid-patch. Run
        // separately, and always, or the projects that most need it are exactly the ones skipped.
        try { await AlignProjectAssetWireFormsAsync(openedProject); }
        catch (Exception ex) { _log.Error("Project", $"Wire-form alignment stopped safely: {ex.Message}"); }
    }

    /// <summary>
    /// M590: rewrite a project's asset references onto the wire form the installed patch uses.
    ///
    /// <para>16.17 turned <c>texturePath</c> and friends from String into WadChunkLink. The client SKIPS a
    /// property whose form disagrees with the schema rather than reporting it (M507), so a mod authored
    /// before the flip loses its textures with nothing said — measured on real projects, 362 references in
    /// one map mod, 653 in another, 188 in a third.</para>
    ///
    /// <para>Which fields to convert is read from the patch's OWN copy of each bin: a field it writes only
    /// as a link gets converted, a field it writes both ways is left alone. Nothing is guessed, and the
    /// rule keeps working when Riot flips the next field. Every changed file is backed up first, and a
    /// result that fails shape validation is discarded rather than written.</para>
    /// </summary>
    /// <summary>M590: the shipped WAD a project folder shadows — the patch's own copy, and so the only
    /// honest source for "which wire form does this patch use". Null when the folder shadows nothing.</summary>
    private ReyEngine.Core.Wad.WadArchive? ReferenceWadFor(string folder)
    {
        string? game = Project.GameDirectory;
        if (string.IsNullOrEmpty(game)) return null;
        foreach (string sub in new[] { @"DATA\FINAL\Maps\Shipping", @"DATA\FINAL\Champions", @"DATA\FINAL" })
        {
            string path = Path.Combine(game, sub, folder + ".wad.client");
            if (!File.Exists(path)) continue;
            try { return ReyEngine.Core.Wad.WadArchive.Open(path, _resolver); } catch { return null; }
        }
        return null;
    }

    /// <summary>M590: the wad chunk hash a project file stands for — its relative path, or its own name
    /// when the file IS a hash (the cslol convention for a chunk whose path is unknown).</summary>
    private static ulong ChunkHashForProjectFile(string folderRoot, string file)
    {
        string rel = Path.GetRelativePath(folderRoot, file).Replace(Path.DirectorySeparatorChar, '/');
        string stem = Path.GetFileNameWithoutExtension(rel);
        if (!rel.Contains('/') && stem.Length == 16
            && ulong.TryParse(stem, System.Globalization.NumberStyles.HexNumber, null, out ulong direct))
            return direct;
        return HashAlgorithms.WadPath(rel.ToLowerInvariant());
    }

    private async Task AlignProjectAssetWireFormsAsync(ReyProject project)
    {
        if (project.RootPath is null || project.ProjectFolders.Count == 0) return;
        if (!ReferenceEquals(Project, project)) return;

        string backupRoot = Path.Combine(project.RootPath, ".reyengine", "backups",
            $"wire-form-{DateTime.Now:yyyyMMdd-HHmmss}");

        var (changedFiles, references, refused) = await Task.Run(() =>
        {
            int files = 0, refs = 0, bad = 0;
            foreach (string folder in project.ProjectFolders)
            {
                string dir = Path.Combine(project.RootPath, folder);
                if (!Directory.Exists(dir)) continue;
                using var reference = ReferenceWadFor(folder);
                if (reference is null) continue;

                foreach (string file in Directory.GetFiles(dir, "*.bin", SearchOption.AllDirectories))
                {
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(file); } catch { continue; }
                    if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'R' || bytes[2] != 'O' || bytes[3] != 'P') continue;

                    ulong hash = ChunkHashForProjectFile(dir, file);
                    if (!reference.TryGetEntry(hash, out var entry)) continue;   // nothing to calibrate against

                    int n;
                    byte[] outBytes;
                    try
                    {
                        var tree = Formats.Meta.SafeBinTree.Parse(bytes);
                        var patchCopy = Formats.Meta.SafeBinTree.Parse(reference.Extract(entry));
                        n = Formats.Meta.BinAssetLinkMigration.AlignWith(tree, patchCopy);
                        if (n == 0) continue;
                        using var ms = new MemoryStream();
                        tree.Write(ms);
                        outBytes = ms.ToArray();
                    }
                    catch { continue; }

                    var issues = Formats.Meta.ModShapeValidator.ValidateBin(
                        Formats.Meta.SafeBinTree.Parse(outBytes), outBytes, ResolveBinName);
                    if (issues.Count > 0) { bad++; continue; }

                    try
                    {
                        string dest = Path.Combine(backupRoot, Path.GetRelativePath(project.RootPath, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(file, dest, overwrite: true);
                        File.WriteAllBytes(file, outBytes);
                        files++; refs += n;
                    }
                    catch { bad++; }
                }
            }
            return (files, refs, bad);
        });

        if (changedFiles == 0 && refused == 0) return;
        if (changedFiles > 0)
            _log.Success("Project", $"Rewrote {references:n0} asset reference(s) in {changedFiles:n0} bin(s) onto the "
                + $"wire form this patch uses (String -> WadChunkLink). The client silently ignores the old form, "
                + $"so these would have lost their textures in game. Backup: {backupRoot}");
        if (refused > 0)
            _log.Warn("Project", $"{refused:n0} bin(s) were left alone - the rewritten form did not validate.");
    }

    private async Task CheckForAutomaticPatchUpdateCoreAsync(ReyProject openedProject)
    {
        var installed = RiotPatchVersionDetector.Detect(openedProject.GameDirectory);
        if (installed is null)
        {
            _log.Warn("PatchUpdate", "Could not detect the installed Riot patch; automatic updating is paused.");
            return;
        }

        string? baseline = openedProject.RiotPatchVersion;
        if (!RiotPatchVersionDetector.TryNormalize(baseline, out var normalizedBaseline))
        {
            baseline = RiotPatchVersionDetector.InferProjectBaseline(openedProject.ModVersion, installed.Patch);
            if (baseline is null)
            {
                openedProject.RiotPatchVersion = installed.Patch;
                if (openedProject.ProjectFilePath is { } path) ReyProjectService.Save(openedProject, path);
                _log.Info("PatchUpdate", $"Patch tracking enabled at {installed.Patch}. This legacy project has no patch-style version, so no historical base was guessed; set Project Base Patch once if it was built for an older client.");
                return;
            }
            openedProject.RiotPatchVersion = baseline;
            if (openedProject.ProjectFilePath is { } inferredPath) ReyProjectService.Save(openedProject, inferredPath);
            _log.Info("PatchUpdate", $"Inferred legacy project base patch {baseline} from mod version {openedProject.ModVersion}.");
        }
        else baseline = normalizedBaseline;

        int comparison = RiotPatchVersionDetector.Compare(baseline, installed.Patch);
        if (comparison == 0) return;
        if (comparison > 0)
        {
            _log.Warn("PatchUpdate", $"Project targets Riot {baseline}, but the selected game install is older ({installed.Patch}); automatic downgrade is blocked.");
            return;
        }
        if (!openedProject.AutoUpdateOnRiotPatch)
        {
            _log.Info("PatchUpdate", $"Riot {installed.Patch} is installed; this project still targets {baseline}. Automatic updating is disabled in Project Settings.");
            return;
        }
        if (!ReferenceEquals(Project, openedProject)) return;

        _log.Info("PatchUpdate", $"Riot patch changed {baseline} -> {installed.Patch}; preparing an automatic transactional rebase.");
        var vm = CreatePatchUpdateViewModel(openedProject, installed.Patch);
        await vm.InitAsync();
        if (!vm.CanRun)
        {
            _log.Error("PatchUpdate", vm.Status);
            if (ReferenceEquals(Project, openedProject)) ShowPatchUpdateWindow(vm);
            return;
        }

        var result = await vm.RunUpdateAsync();
        if (result is { NeedsReview: true } && ReferenceEquals(Project, openedProject)) ShowPatchUpdateWindow(vm);
    }

    private void ShowPatchUpdateWindow(PatchUpdateWindowViewModel vm)
    {
        var win = new Views.PatchUpdateWindow { DataContext = vm };
        if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
    }

    /// <summary>M94: convert a .fantome mod package into an editable folder project under
    /// Documents\ReyEngine Projects, then open it — lets users mod existing mods.</summary>
    [RelayCommand]
    private async Task ImportFantome()
    {
        var file = await Dialogs.OpenFileAsync("Import .fantome mod package",
            new Avalonia.Platform.Storage.FilePickerFileType("Fantome mod package") { Patterns = new[] { "*.fantome", "*.zip" } },
            DialogService.All);
        if (file is null) return;
        try
        {
            Status = "Importing .fantome…";
            Directory.CreateDirectory(ProjectsFolder);
            string? gameDir = !string.IsNullOrEmpty(Project.GameDirectory) && Directory.Exists(Project.GameDirectory)
                ? Project.GameDirectory
                : ReyEngine.Core.Projects.GameInstallLocator.Discover().FirstOrDefault()?.GameDirectory;
            var progress = new Progress<string>(m => Status = m);
            var result = await Task.Run(() => ReyEngine.Core.Projects.FantomeImporter.Import(
                file, ProjectsFolder, gameDir, _resolver, progress));
            _log.Success("Import", $"{result.ProjectName}: {result.Wads} WAD(s), {result.ExtractedFiles:n0} file(s) unpacked" +
                (result.RawFiles > 0 ? $" + {result.RawFiles} RAW file(s)" : "") +
                (result.FailedChunks > 0 ? $" ({result.FailedChunks} chunk(s) failed — usually subchunked textures)" : "") +
                $" → {result.RootPath}");
            OpenProjectAt(result.RootPath);   // also records it in Open Recent
        }
        catch (Exception ex) { _log.Error("Import", $"Fantome import failed: {ex.Message}"); }
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
            Documents.Clear(); ActiveDocument = null; // same path hash in another project is different content
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
            StartProjectWatchers();   // M100: auto-refresh the browser on external file changes
            _ = CheckForAutomaticPatchUpdateAsync(project);
            if (project.ReferenceWads.Count == 0)
                _log.Info("Project", "No Riot references yet — add one via Project ▸ Manage Riot References to preview/copy source assets.");
        }
        catch (Exception ex) { _log.Error("Project", ex.Message); }
    }

    /// <summary>M133: the projects folder — the configured one (Settings ▸ General) when set,
    /// else Documents\ReyEngine Projects (which OneDrive may redirect). Created on demand.</summary>
    public string ProjectsFolder =>
        !string.IsNullOrWhiteSpace(Settings.ProjectsDirectory) ? Settings.ProjectsDirectory : DefaultProjectsFolder;

    public static string DefaultProjectsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReyEngine Projects");

    private void LoadRecentProjects(IEnumerable<string> folders)
    {
        RecentProjectList.Clear();
        // M80: only list REAL project folders (a .reyengine/project.json inside) — the store accumulated
        // junk over time (unpacked-wad subfolders, the .reyengine dir itself, deleted paths).
        foreach (var f in folders)
            if (IsProjectFolder(f))
                RecentProjectList.Add(new RecentProjectViewModel(f, OpenRecentProject));

        // M80: also list everything in the canonical projects folder (wizard-created projects show up
        // even if they were never opened on this machine / the recents store was cleared).
        try
        {
            Directory.CreateDirectory(ProjectsFolder);
            foreach (var dir in Directory.EnumerateDirectories(ProjectsFolder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                if (IsProjectFolder(dir)
                    && !RecentProjectList.Any(r => string.Equals(r.Path, dir, StringComparison.OrdinalIgnoreCase)))
                    RecentProjectList.Add(new RecentProjectViewModel(dir, OpenRecentProject));
        }
        catch { /* projects folder unreadable — recents alone */ }

        OnPropertyChanged(nameof(HasRecentProjects));
    }

    private static bool IsProjectFolder(string dir) =>
        Directory.Exists(dir)
        && File.Exists(Path.Combine(dir, ReyProjectService.FolderMetaDir, ReyProjectService.FolderMetaFile))
        && !dir.TrimEnd('/', '\\').EndsWith(ReyProjectService.FolderMetaDir, StringComparison.OrdinalIgnoreCase);

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
        foreach (var r in Project.ReferenceWads) MountReference(r);

        AddGameFallback();
        _mounts.Rebuild();
    }

    /// <summary>
    /// M508: mount one Riot reference wad, with one retry.
    ///
    /// <para>Losing this mount is not cosmetic — every Riot asset the project reads through it disappears
    /// for the rest of the session, which is what a user saw as "the wad.client for the project is not
    /// there any more". The cause was a data race in the hash tables (fixed in ZstdSeekable), and the
    /// symptom was an IndexOutOfRangeException that said nothing about where it came from.</para>
    ///
    /// <para>So: name the exception TYPE, say what was lost, and try once more — a torn read is transient,
    /// and a second attempt costs milliseconds against losing the reference set.</para>
    /// </summary>
    private void MountReference(string path)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                _mounts!.Add(new WadMount(WadArchive.Open(path, _resolver), AssetSourceKind.RiotReference,
                    editable: false, name: Path.GetFileName(path)));
                if (attempt > 1) _log.Info("Project", $"reference {Path.GetFileName(path)}: mounted on retry.");
                return;
            }
            catch (Exception ex) when (attempt == 1)
            {
                _log.Warn("Project", $"reference {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message} "
                                   + "— retrying once.");
            }
            catch (Exception ex)
            {
                _log.Error("Project", $"reference {Path.GetFileName(path)} could not be mounted "
                                    + $"({ex.GetType().Name}: {ex.Message}). Riot assets from that wad are "
                                    + (File.Exists(path)
                                        ? "unavailable until the project is reopened — the file is still on disk."
                                        : "unavailable: the file is GONE from " + path));
            }
        }
    }

    /// <summary>Mount the original Riot game WADs as read-only fallback so missing assets resolve from the install.</summary>
    private void AddGameFallback()
    {
        if (_mounts is null) return;
        var mapNames = Project.ProjectFolders.Concat(Project.ProjectWads)
            .Select(p => p == "." ? Project.Name : Path.GetFileNameWithoutExtension(p).Replace(".wad", "", StringComparison.OrdinalIgnoreCase))
            .Append(Project.Name)
            // A project folder is not always named Map11. Recover the target WAD from resolved asset
            // paths such as data/maps/mapgeometry/map11/base_srx.mapgeo as well.
            .Concat(_mounts.Mounts
                .Where(m => m.Kind != AssetSourceKind.RiotReference)
                .SelectMany(m => m.Enumerate())
                .Select(a => MapNameFromAssetPath(a.VirtualPath))
                .OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var status = GameReferenceLibrary.Inspect(Project.GameDirectory);
        if (!status.IsValid)
        {
            LogGameFallbackOnce("error", status.Message
                + " Missing skins, props, materials and textures will not resolve. "
                + "Fix: Project > Set Game Folder..., select the League of Legends\\Game folder containing DATA\\FINAL, then reopen the map.");
            return;
        }

        int mounted = 0;
        var discovered = GameReferenceLibrary.Discover(status.GameDirectory, mapNames);
        foreach (var wad in discovered)
        {
            if (Project.ReferenceWads.Contains(wad, StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                _mounts.AddFallback(new WadMount(WadArchive.Open(wad), AssetSourceKind.RiotReference,
                    editable: false, name: Path.GetFileName(wad)));
                mounted++;
            }
            catch (Exception ex) { _log.Warn("Project", $"game fallback {Path.GetFileName(wad)}: {ex.Message}"); }
        }

        var missingMapWads = mapNames
            .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^map\d+$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Where(n => !File.Exists(Path.Combine(status.FinalDirectory!, "Maps", "Shipping", n + ".wad.client")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missingMapWads.Length > 0)
            LogGameFallbackOnce("error",
                $"The League folder was recognized, but required map WAD(s) are missing: {string.Join(", ", missingMapWads.Select(n => n + ".wad.client"))}. "
                + "The folder may point to an old or incomplete install; map recolouring and asset resolution can fail. "
                + "Update League or use Project > Set Game Folder... to select the active League of Legends\\Game folder, then reopen the map.");
        else if (discovered.Count > 0)
            LogGameFallbackOnce("info",
                $"Game asset folder verified: {status.GameDirectory}. {discovered.Count:n0} relevant WAD(s) found"
                + (mounted != discovered.Count
                    ? $", {mounted:n0} added as fallback (the rest are already explicit references)"
                    : " and mounted as read-only fallback") + ".");
        else
            LogGameFallbackOnce("error",
                $"The game folder is valid, but no relevant WADs were found under {status.FinalDirectory}. "
                + "Update League, then reopen the project. If the install moved, use Project > Set Game Folder....");
    }

    private static string? MapNameFromAssetPath(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < segments.Length; i++)
            if ((segments[i].Equals("mapgeometry", StringComparison.OrdinalIgnoreCase)
                 || segments[i].Equals("shipping", StringComparison.OrdinalIgnoreCase))
                && System.Text.RegularExpressions.Regex.IsMatch(segments[i + 1], @"^map\d+$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return segments[i + 1];
        return null;
    }

    private void LogGameFallbackOnce(string level, string message)
    {
        string key = level + "|" + message;
        if (string.Equals(_lastGameFallbackNotice, key, StringComparison.Ordinal)) return;
        _lastGameFallbackNotice = key;
        if (level == "error") _log.Error("Project", message);
        else _log.Info("Project", message);
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
            // M110: a folder mount stays listed even with no files — it may hold only empty folders.
            var dirs = mount is FolderMount fm ? fm.Directories : (IReadOnlyList<string>)Array.Empty<string>();
            if (entries.Count == 0 && dirs.Count == 0) continue;
            var subtree = AssetTree.Build(entries, mount.Name);
            if (dirs.Count > 0) AssetTree.EnsureFolders(subtree, dirs);
            projectGroup.Children.Add(subtree);
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
        var status = GameReferenceLibrary.Inspect(folder);
        if (!status.IsValid)
        {
            _log.Error("Project", status.Message
                + " Select the League of Legends\\Game folder containing DATA\\FINAL. The previous setting was kept.");
            return;
        }
        Project.GameDirectory = status.GameDirectory;
        var probe = GameReferenceLibrary.Discover(status.GameDirectory, Project.ProjectFolders.Append(Project.Name));
        _lastGameFallbackNotice = null;
        if (ProjectMode)
        {
            ReyProjectService.Save(Project, Project.ProjectFilePath!);
            BuildMounts(); BuildProjectTree();
        }
        _log.Success("Project", $"Game folder set and verified: {status.GameDirectory} — {probe.Count} reference WAD(s) available. Reopen the map before retrying Recolor Textures.");
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

    // ---- M103: League shader catalogue (Live / PBE) ---------------------

    private readonly Dictionary<string, string> _shaderEnvironmentDirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>List the installs whose shader list can be browsed: every discovered client, plus the
    /// project's own game directory when it isn't one of them.</summary>
    private void InitShaderEnvironments()
    {
        _shaderEnvironmentDirs.Clear();
        foreach (var editor in MaterialEditors) editor.ShaderEnvironments.Clear();   // M642: both editors
        foreach (var install in GameInstallLocator.Discover())
            if (_shaderEnvironmentDirs.TryAdd(install.Platform, install.GameDirectory))
                foreach (var editor in MaterialEditors) editor.ShaderEnvironments.Add(install.Platform);

        if (Project.GameDirectory is { Length: > 0 } gd
            && !_shaderEnvironmentDirs.Values.Any(d => string.Equals(d, gd, StringComparison.OrdinalIgnoreCase))
            && _shaderEnvironmentDirs.TryAdd("Project", gd))
            foreach (var editor in MaterialEditors) editor.ShaderEnvironments.Add("Project");

        // Prefer the install the project actually targets, else the first one found.
        var preferred = _shaderEnvironmentDirs.FirstOrDefault(kv =>
            string.Equals(kv.Value, Project.GameDirectory, StringComparison.OrdinalIgnoreCase)).Key
            ?? MaterialEditor.ShaderEnvironments.FirstOrDefault();
        if (preferred is not null) MaterialEditor.SelectedShaderEnvironment = preferred;
    }

    private static string ShaderCatalogCachePath(string environment) =>
        Path.Combine(ReyEngine.Core.ReyPaths.DataRoot, "shader_catalogs", $"{environment}.json");

    /// <summary>Scan (or load from cache) one install's shader definitions for the Material Editor.</summary>
    private async Task LoadShaderCatalogAsync(string environment)
    {
        if (!_shaderEnvironmentDirs.TryGetValue(environment, out var gameDir))
        {
            SetShaderCatalog(environment, null);
            return;
        }
        // M475: locate the WAD BEFORE consulting the cache — the cache is only valid for a specific build
        // of it. It used to be served on a game-directory match alone, and a directory path does not change
        // when Riot patches. Measured on this install: a Live catalogue written 2026-07-20 with 347 shaders
        // kept being served against a client whose shaders.bin now declares 351, so four shaders (including
        // 4TextureBlend_UVBased_baseMat) were unpickable in every dropdown, permanently, with no refresh
        // short of deleting the file by hand.
        var wad = GameReferenceLibrary.FindGlobalWad(gameDir);
        if (wad is null)
        {
            SetShaderCatalog(environment, null);
            _log.Warn("Shader", $"{environment}: Global.wad.client not found under {gameDir} — no shader list.");
            return;
        }

        var cachePath = ShaderCatalogCachePath(environment);
        string stamp = ShaderCatalogLoader.StampFor(wad);
        var cached = await Task.Run(() => ShaderCatalogCache.Load(cachePath, gameDir, stamp));
        if (cached is not null) { SetShaderCatalog(environment, cached); return; }
        if (File.Exists(cachePath))
            _log.Info("Shader", $"{environment}: the cached shader catalogue came from a different build of "
                              + "Global.wad — rescanning. A Riot patch used to leave it stale.");

        _log.Info("Shader", $"Reading {environment} shader definitions…");
        var catalog = await Task.Run(() =>
            ShaderCatalogLoader.Load(wad, gameDir, environment, _resolver, h => ResolveBinName(h)));
        if (catalog is not null)
        {
            await Task.Run(() => ShaderCatalogCache.Save(catalog, cachePath));
            _log.Success("Shader", $"{environment}: {catalog.Shaders.Count:n0} shader definitions loaded.");
        }
        else _log.Warn("Shader", $"{environment}: {ShaderCatalogLoader.ShaderBinPath} not readable.");
        SetShaderCatalog(environment, catalog);   // M642: every editor
    }

    [RelayCommand]
    private async Task ExportShaderDump()
    {
        var entry = SelectedNode?.Entry;
        if (entry is null || _archive is null && _mounts is null) { _log.Warn("Shader", "Select a shader (.dx11) asset first."); return; }
        // M277: the cache ships both ".dx11" and "-dx11" spellings (the 2026-07-29 patch renamed them all),
        // so testing only the dotted one rejects every shader asset in a current install.
        if (!entry.Path.Contains(".dx11", StringComparison.OrdinalIgnoreCase)
            && !entry.Path.Contains("-dx11", StringComparison.OrdinalIgnoreCase))
        { _log.Warn("Shader", "Selected asset isn't a shader (.dx11/-dx11)."); return; }
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

    // ---- new-feature discovery (M593) ----

    /// <summary>
    /// Bound from XAML as <c>Classes.newFeature="{Binding NewFeature[some-id]}"</c>. Seeded from the
    /// user's acknowledged release, so a fresh install and an updating install both see the highlights
    /// once and a returning user sees nothing.
    /// </summary>
    public NewFeatureLookup NewFeature { get; } = new();

    /// <summary>True while anything is unacknowledged — drives the "What's New" affordance.</summary>
    public bool HasNewFeatures => NewFeature.AnyUnseen;

    /// <summary>
    /// Mark this release's highlights as seen and put every glow out at once.
    ///
    /// <para>Tied to an explicit acknowledgement rather than to app start or to clicking any one control:
    /// dismissing on launch would mean a user who blinked never sees them, and dismissing per control
    /// would leave the rest glowing with no way to tell which were noticed. One deliberate action, one
    /// write to settings.</para>
    /// </summary>
    [RelayCommand]
    private void DismissNewFeatures()
    {
        if (!NewFeature.AnyUnseen) return;
        Settings.LastSeenFeatureVersion = ReyEngine.Core.Settings.NewFeatures.CurrentVersion;
        Settings.Save();
        NewFeature.LastSeenVersion = Settings.LastSeenFeatureVersion;
        OnPropertyChanged(nameof(HasNewFeatures));
        _log.Info("ReyEngine", $"New-feature highlights for {ReyEngine.Core.Settings.NewFeatures.CurrentVersion} dismissed.");
    }

    [RelayCommand]
    private void OpenSettings() => RequestSettings?.Invoke();

    /// <summary>M682: which section the next Settings window opens on; null is the first. Read and cleared
    /// by the window that shows it.</summary>
    public int? PendingSettingsSection { get; set; }

    /// <summary>M682: Tools ▸ Install Blender add-on… - Settings, on the Blender section.</summary>
    [RelayCommand]
    private void OpenBlenderAddonSetup()
    {
        PendingSettingsSection = SettingsViewModel.BlenderSection;
        RequestSettings?.Invoke();
    }

    /// <summary>Called by the view after the Preferences dialog is saved: persist + let the view re-apply.</summary>
    public void ApplyEditorSettings(SettingsViewModel vm)
    {
        bool wasAutoSaving = Settings.AutoSaveEdits;
        Settings.CopyFrom(vm.ToSettings());
        Settings.Save();
        CullBackfaces = Settings.CullBackfacesDefault;
        _log.Success("Settings", "Preferences saved.");
        // M505: auto-save has no other sign of life, and for two milestones it silently did nothing because
        // CopyFrom above dropped the flag. Say which state it is in whenever it changes.
        if (wasAutoSaving != Settings.AutoSaveEdits)
        {
            OnPropertyChanged(nameof(AutoSaveEnabled));
            _log.Info("Auto-save", Settings.AutoSaveEdits
                ? $"On — pending edits save after {Settings.EffectiveAutoSaveDelaySeconds}s of quiet."
                : "Off — edits are saved only when you ask.");
        }
        // M88: apply the preview backdrop change immediately if a model preview is already open.
        if (MeshPreview.Mesh is not null) _ = ApplyPreviewBackgroundAsync();
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
        var progress = BuildProgressSink();
        try
        {
            await Task.Run(() =>
            {
                var buildRoot = Project.OutputDirectory ?? Path.Combine(Project.RootPath, "Build");
                if (BuildSafety.IsInsideGameInstall(buildRoot))
                    throw new InvalidOperationException("Build output is inside the game install — change it in Project Settings.");
                Directory.CreateDirectory(buildRoot);
                // M131: fresh build + bundle EXACTLY what it produced — stale wads lying in the
                // build folder from earlier project layouts used to sneak into the package.
                var wads = BuildProjectCore(buildRoot, progress);
                if (wads.Count == 0) throw new InvalidOperationException("No WAD was produced — the project has no packable content.");
                progress.Report((0.98, $"Zipping {Path.GetFileName(outPath)}…"));
                FantomeExporter.Export(meta, wads, thumb, outPath);
            });
            _log.Success("Export", $"Wrote {outPath} ({new FileInfo(outPath).Length / 1048576.0:0.0} MB) — {meta.Name} v{meta.Version} by {meta.Author}.");
            Status = $"Exported {Path.GetFileName(outPath)}";
        }
        catch (Exception ex) { _log.Error("Export", ex.Message); }
        finally { IsBuilding = false; }
    }

    /// <summary>
    /// M470: send this project to LTK Manager's workshop folder, creating the mod or updating it in place.
    ///
    /// <para>Deliberately writes SOURCE files to the workshop, not a package to the library. LTK Manager's
    /// library lives in <c>%APPDATA%\dev.leaguetoolkit.manager</c> as library.json plus
    /// <c>archives\&lt;uuid&gt;.fantome</c>, and that json also holds the user's profiles, enabled set and
    /// mod order — minting uuids into someone else's database to save one import click is not a trade worth
    /// making. The manager already watches the workshop (<c>watcherEnabled: true</c> in its settings), so
    /// this uses the supported door.</para>
    ///
    /// <para>The workshop root is READ from the manager's own settings.json rather than guessed; the
    /// observed install points at <c>D:\Workshopmods</c>, which no default would have found.</para>
    /// </summary>
    /// <summary>M607: the object that owns the D3D11 surface and the camera. Set by MainWindow, exactly
    /// as PromptOwner is - the cinematic panel needs both and the view-model owns neither.</summary>
    public ICinematicHost? CinematicHost { get; set; }

    [RelayCommand]
    private void OpenCinematicCapture()
    {
        if (PromptOwner is null || CinematicHost is null)
        { _log.Error("Cinematic", "The viewport is not ready yet."); return; }
        if (!UseDx11Viewport)
        { _log.Error("Cinematic", "Cinematic capture renders through the Direct3D 11 viewport - switch to it first."); return; }
        if (!Project.IsFolderProject || Project.RootPath is not { } root)
        { _log.Error("Cinematic", "Open a folder project first - shots are saved beside it."); return; }

        Views.CinematicWindow.Show(PromptOwner, CinematicHost, root,
            (category, message) => _log.Info(category, message));
    }

    [RelayCommand]
    private async Task SendToLtkManager()
    {
        if (!ProjectMode || Project.RootPath is null)
        { _log.Warn("LTK", "Open a project folder first."); return; }

        if (!Core.Build.LtkManagerLocator.TryFindWorkshopRoot(out var root, out var why))
        { _log.Error("LTK", why); return; }

        // The manager watches this folder. Writing while it is mid-import is a race we can warn about but
        // not prevent, so say it rather than silently producing a half-imported mod.
        if (Core.Build.LtkManagerLocator.IsManagerRunning())
            _log.Warn("LTK", "LTK Manager is running — it may import while files are still being written. "
                           + "If the mod looks incomplete, re-send it with the manager closed.");

        string name = Project.EffectiveModName;
        string author = string.IsNullOrWhiteSpace(Project.ModAuthor) ? "Unknown" : Project.ModAuthor!;

        // The project's recorded target wins over a name-derived slug. A workshop slug is NOT derivable
        // from the mod name — the user's own "Old Summoner's Rift - Day" lives in a folder called
        // oldriftday — so without this, sending a long-shipped mod would create a duplicate rather than
        // update it, which is the one case this feature exists to handle.
        string slug = string.IsNullOrWhiteSpace(Project.LtkWorkshopSlug)
            ? Core.Build.LtkWorkshopExporter.Slugify(name)
            : Project.LtkWorkshopSlug!.Trim();

        var options = new Core.Build.LtkSendOptions(
            WorkshopRoot: root!,
            Slug: slug,
            DisplayName: name,
            Version: string.IsNullOrWhiteSpace(Project.ModVersion) ? "1.0.0" : Project.ModVersion,
            Description: Project.ModDescription ?? "",
            Author: author,
            ThumbnailPath: Project.ThumbnailPath)
        {
            // M742: the project's modpkg layers, so an optional half of a mod ships as something the
            // user can switch off in LTK Manager rather than as part of the map. "base" is always
            // declared, even when the project names no layers, because that is where unclaimed folders go.
            Layers = Core.Build.LtkProjectLayers.Of(Project),
        };

        bool declare = Project.ShipBinEditsAsDeclarations;
        var declarationReport = new List<(int Level, string Line)>();
        IsBuilding = true; Status = "Sending to LTK Manager…";
        try
        {
            var result = await Task.Run(() =>
            {
                // Same enumeration the WAD packer walks, so what LTK Manager gets is what a build would
                // have contained - one source of truth for "the project's files".
                var files = new List<(string Layer, string WadFolder, string RelPath, string AbsPath)>();
                foreach (var f in Project.ProjectFolders)
                {
                    var abs = Project.ResolveProjectPath(f);
                    if (!Directory.Exists(abs)) continue;
                    var folderName = Path.GetFileName(abs.TrimEnd('/', '\\'));
                    // M742: which modpkg layer this WAD folder ships in - "base" unless a layer claims it.
                    string layer = Project.LayerOf(folderName);
                    foreach (var (_, path) in Core.Build.WadPackService.EnumerateChunkFiles(abs))
                        files.Add((layer, folderName, Path.GetRelativePath(abs, path).Replace('\\', '/'), path));
                }
                if (files.Count == 0)
                    throw new InvalidOperationException("The project has no packable content to send.");
                // M757: game bins as declarations against the game's copy, when the project asks for it
                var sendOptions = options;
                if (declare)
                {
                    var declared = DeclareGameBins(files);
                    files = declared.Files;
                    sendOptions = options with { GameData = declared.GameData };
                    declarationReport = declared.Report;
                }
                return Core.Build.LtkWorkshopExporter.Send(sendOptions, files);
            });
            foreach (var (level, line) in declarationReport)
                if (level == 0) _log.Success("LTK", line); else _log.Info("LTK", line);

            _log.Success("LTK", $"{result.Detail} → {result.ModFolder}");

            // Remember what was targeted, so every later send updates this same mod even if the display
            // name changes. Recorded on create AND on update: an update proves the target is right.
            if (!string.Equals(Project.LtkWorkshopSlug, slug, StringComparison.Ordinal))
            {
                Project.LtkWorkshopSlug = slug;
                if (Project.ProjectFilePath is { } pf) ReyProjectService.Save(Project, pf);
            }

            if (result.Created)
            {
                // Creating is the risky outcome, not the safe one: if the user already ships this mod under
                // a slug that does not match its name, they have just gained a duplicate. Name the escape
                // hatch here rather than leaving them to find it.
                _log.Info("LTK", $"Created a NEW workshop mod '{slug}'. If you already ship this mod under a "
                               + "different folder name, set Project ▸ Project Settings ▸ LTK Manager workshop "
                               + "mod to that folder's name and send again to update it instead.");
                _log.Info("LTK", "LTK Manager should pick it up from the workshop; if it does not appear, "
                               + "refresh the workshop view in the manager.");
            }
            Status = $"{(result.Created ? "Created" : "Updated")} {slug} in LTK Manager";
        }
        catch (Exception ex) { _log.Error("LTK", $"Send failed: {ex.Message}"); }
        finally { IsBuilding = false; }
    }

    /// <summary>M757: plaintext for the hashes a declaration spells, from the loaded hash tables.</summary>
    private sealed class DeclarationNames(Core.Hashing.HashDatabase db) : Formats.Meta.IDeclarationNames
    {
        public string? Field(uint hash) => db.TryGetBinName(hash, out var n) ? n : null;
        public string? Class(uint hash) => Field(hash);
        public string? Entry(uint hash) => Field(hash);
        public string? File(ulong hash) => db.TryGetPath(hash, out var p) ? p : null;
    }

    /// <summary>
    /// M757: turn every project bin that overrides a GAME bin into declarations against the game's copy.
    /// A declared or unchanged bin leaves the file list; one that cannot be declared stays in it and the
    /// report says why. A bin with no game copy is new content and is sent as it always was.
    /// </summary>
    private (List<(string Layer, string WadFolder, string RelPath, string AbsPath)> Files,
             Dictionary<string, string> GameData, List<(int Level, string Line)> Report)
        DeclareGameBins(List<(string Layer, string WadFolder, string RelPath, string AbsPath)> files)
    {
        var names = new DeclarationNames(_resolver.Database);
        var kept = new List<(string, string, string, string)>();
        var modules = new Dictionary<string, List<Formats.Meta.DeclaredChunk>>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<(string Layer, string Target), byte[]>();
        var whole = new List<string>();
        int declared = 0, unchanged = 0, props = 0, added = 0, removed = 0;
        foreach (var f in files)
        {
            if (!f.RelPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) { kept.Add(f); continue; }
            string stem = Path.GetFileNameWithoutExtension(f.RelPath);
            ulong hash = !f.RelPath.Contains('/') && stem.Length == 16
                         && ulong.TryParse(stem, System.Globalization.NumberStyles.HexNumber, null, out var hex)
                ? hex : Core.Hashing.HashAlgorithms.WadPath(f.RelPath);
            byte[]? riot = null;
            try { riot = ReadRiotOriginalBytes(new WadAssetEntry { PathHash = hash, Path = f.RelPath }); } catch { }
            if (riot is null) { kept.Add(f); continue; }   // not a game bin: new content ships as a file

            byte[] mod = File.ReadAllBytes(f.AbsPath);
            string target = Formats.Meta.BinDeclarations.TargetOf(f.RelPath, hash);
            // the same bin in two WAD folders of one layer declares once; two different copies cannot
            if (seen.TryGetValue((f.Layer, target), out var first))
            {
                if (first.AsSpan().SequenceEqual(mod)) continue;
                whole.Add($"{f.WadFolder}/{f.RelPath}: two different copies in one layer");
                kept.Add(f);
                continue;
            }
            seen[(f.Layer, target)] = mod;

            var chunk = Formats.Meta.BinDeclarations.Convert(target, riot, mod, names);
            if (chunk.Unchanged) { unchanged++; continue; }
            if (!chunk.Declared) { whole.Add($"{f.WadFolder}/{f.RelPath}: {chunk.WhyNot}"); kept.Add(f); continue; }
            if (!modules.TryGetValue(f.Layer, out var list)) modules[f.Layer] = list = new();
            list.Add(chunk);
            declared++; props += chunk.Properties; added += chunk.ObjectsAdded; removed += chunk.ObjectsRemoved;
        }

        var report = new List<(int, string)>
        {
            (0, $"Declarations: {declared} game bin(s) sent as changes ({props} propert(ies), {added} object(s) added, "
                + $"{removed} removed), {unchanged} unchanged bin(s) not sent, {whole.Count} sent whole."),
        };
        foreach (var w in whole.Take(20)) report.Add((1, "  sent whole - " + w));
        if (whole.Count > 20) report.Add((1, $"  ... and {whole.Count - 20} more sent whole."));
        var gameData = modules.ToDictionary(kv => kv.Key, kv => Formats.Meta.BinDeclarations.Manifest(kv.Value),
            StringComparer.OrdinalIgnoreCase);
        return (kept.Select(k => (k.Item1, k.Item2, k.Item3, k.Item4)).ToList(), gameData, report);
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

    [RelayCommand(CanExecute = nameof(CanCopyAssetToProject))]
    private async Task CopyAssetToProject()
    {
        var nodes = ContextNodes.Where(n => !n.IsFolder && n.Entry is not null).ToList();
        if (nodes.Count == 0) { _log.Warn("Project", "Select an asset to copy."); return; }
        if (!ProjectMode || _mounts is null) { _log.Warn("Project", "Copy to Project needs an open project."); return; }

        // M107: one asset keeps the detailed per-file prompt.
        if (nodes.Count == 1) { await CopyOneAssetToProject(nodes[0], replaceExisting: null); return; }

        // A batch asks ONCE — a prompt per file is unusable on a large selection.
        int already = nodes.Count(HasProjectCopy);
        bool replaceExisting = false;
        if (already > 0)
        {
            if (PromptOwner is null) { _log.Info("Project", $"{already} of the selected asset(s) are already editable — skipping those."); }
            else
                replaceExisting = await Views.PromptWindow.ConfirmAsync(PromptOwner, "Replace Project Copies",
                    $"{already} of the {nodes.Count} selected asset(s) are already editable in the project.\n\n" +
                    $"Replace them with fresh copies of the ORIGINAL Riot files? Your edits in those files will be lost.\n\n" +
                    $"Cancel copies only the {nodes.Count - already} new one(s).", "Replace");
        }

        // The mount/tree rebuild is expensive, so it runs once for the whole batch, not per file.
        int copied = 0, skipped = 0;
        _copyBatch = true;
        try
        {
            foreach (var n in nodes)
                if (await CopyOneAssetToProject(n, replaceExisting)) copied++;
                else skipped++;
        }
        finally { _copyBatch = false; }

        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        BuildMounts();
        BuildProjectTree();
        UpdateTitle();
        _log.Success("Project", $"Copied {copied} of {nodes.Count} asset(s) into the project"
                                + (skipped > 0 ? $" — {skipped} skipped (already editable, or no original found)." : "."));
    }

    /// <summary>True when this asset already has an editable copy on disk in the project.</summary>
    private bool HasProjectCopy(AssetNodeViewModel node)
    {
        if (node.Entry is not { } e || e.SourceKind == AssetSourceKind.RiotReference) return false;
        if (TryGetNodeFile(node, out var f) && File.Exists(f)) return true;
        return _overrides.TryGet(e.PathHash, out var ov) && File.Exists(ov.OverrideFile);
    }

    /// <summary>M107: set while a multi-asset copy runs — <see cref="FinishProjectCopy"/> then skips the
    /// per-file mount/tree rebuild, which the batch does once at the end instead.</summary>
    private bool _copyBatch;

    /// <param name="replaceExisting">null = ask (single-asset path); true/false = the batch already decided.</param>
    private async Task<bool> CopyOneAssetToProject(AssetNodeViewModel? srcNode, bool? replaceExisting)
    {
        var entry = srcNode?.Entry;
        if (entry is null) return false;

        // M98b: don't trust the node's SourceKind — deleting the project copy from the browser leaves the
        // mount index stale. Check whether the project copy actually EXISTS on disk; if it does, offer to
        // replace it with a fresh copy of the Riot original instead of refusing.
        if (entry.SourceKind != AssetSourceKind.RiotReference)
        {
            string? projectCopy = null;
            if (TryGetNodeFile(srcNode, out var nodeFile) && File.Exists(nodeFile)) projectCopy = nodeFile;
            else if (_overrides.TryGet(entry.PathHash, out var ov) && File.Exists(ov.OverrideFile)) projectCopy = ov.OverrideFile;

            if (projectCopy is not null)
            {
                if (replaceExisting == false) return false;   // batch chose to skip existing copies
                if (replaceExisting is null)
                {
                    if (PromptOwner is null) { _log.Info("Project", "Asset is already editable in the project."); return false; }
                    if (!await Views.PromptWindow.ConfirmAsync(PromptOwner, "Replace Project Copy",
                        $"'{entry.DisplayName}' is already editable in the project.\n\nReplace it with a fresh copy of the ORIGINAL Riot file? Your edits in this file will be lost.\n\n{projectCopy}", "Replace"))
                        return false;
                }
                var riot = ReadRiotOriginalBytes(entry);
                if (riot is null)
                { _log.Error("Project", $"{entry.DisplayName}: original Riot bytes not found (no reference WAD has this asset)."); return false; }
                try
                {
                    // M98d: a legacy hash-named override in a folder project MIGRATES to its real path
                    // on replace — the hash file and its record are removed.
                    bool isLegacyOverride = _overrides.TryGet(entry.PathHash, out var ovRec)
                        && string.Equals(ovRec.OverrideFile, projectCopy, StringComparison.OrdinalIgnoreCase);
                    if (isLegacyOverride && TryPlaceInProjectFolder(entry, riot, out var migrated))
                    {
                        _overrides.Remove(entry.PathHash);
                        try { File.Delete(projectCopy); } catch { }
                        FinishProjectCopy(entry, $"Migrated {entry.DisplayName} from the hash-named override to {migrated} (fresh Riot original, {riot.Length:n0} bytes).");
                        return true;
                    }
                    File.WriteAllBytes(projectCopy, riot);
                    Project.IsDirty = true;
                    RefreshOverrideMount();
                    BuildProjectTree();
                    UpdateTitle();
                    _log.Success("Project", $"Replaced project copy of {entry.DisplayName} with the Riot original ({riot.Length:n0} bytes).");
                }
                catch (Exception ex) { _log.Error("Project", $"{entry.DisplayName}: {ex.Message}"); return false; }
                return true;
            }

            // stale: the project copy is gone from disk — clean the dead override record and re-copy below
            if (_overrides.Has(entry.PathHash))
            {
                _overrides.Remove(entry.PathHash);
                _log.Info("Project", $"Stale override record for {entry.DisplayName} removed (file was deleted) — copying fresh.");
            }
        }

        try
        {
            // prefer the untouched Riot original as the copy source (the mounts may still serve stale bytes)
            var bytes = ReadRiotOriginalBytes(entry) ?? ReadAsset(entry.PathHash);

            // M98c: folder projects get the copy at its REAL path inside the per-WAD folder (cslol
            // layout — human-findable, editable, picked up by Build Package like any project file).
            // The hashed overrides dir remains only for single-WAD projects and unresolved chunks.
            if (TryPlaceInProjectFolder(entry, bytes, out var placed))
            {
                FinishProjectCopy(entry, $"Copied {entry.DisplayName} into the project at {placed} ({bytes.Length:n0} bytes). It is now editable.");
                return true;
            }

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
            if (!_copyBatch)
            {
                RefreshOverrideMount();
                BuildProjectTree();
                if (_nodesByHash.TryGetValue(entry.PathHash, out var node)) SelectedNode = node;
                UpdateTitle();
            }
            _log.Success("Project", $"Copied {entry.DisplayName} into the project ({bytes.Length:n0} bytes). It is now editable.");
            return true;
        }
        catch (Exception ex) { _log.Error("Project", $"{entry.DisplayName}: {ex.Message}"); return false; }
    }

    /// <summary>M98c/d: the project-folder name a Riot asset should be staged under — the source WAD's
    /// base name (Map12.wad.client → "Map12"), or "Overrides" when the source WAD can't be determined.</summary>
    private string RiotWadFolderName(WadAssetEntry entry) => RiotWadFolderNameForHash(entry.PathHash);

    /// <summary>Which project folder does an asset belong in? The name of the Riot WAD it comes from
    /// (Map11.wad.client → "Map11"), so the packer puts it back into the same wad the game reads it from.
    /// "Overrides" when the asset has no Riot home at all.</summary>
    private string RiotWadFolderNameForHash(ulong pathHash)
    {
        if (_mounts is not null && _mounts.TryGet(pathHash, out var mounted))
        {
            var riotSrc = mounted.Source.Kind == AssetSourceKind.RiotReference ? mounted.Source
                : mounted.AllSources.FirstOrDefault(s => s.Kind == AssetSourceKind.RiotReference);
            if (riotSrc is not null)
            {
                var wadName = Path.GetFileName(riotSrc.Location);
                if (wadName.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                    wadName = wadName[..^".wad.client".Length];
                foreach (var c in Path.GetInvalidFileNameChars()) wadName = wadName.Replace(c, '_');
                if (wadName.Length > 0) return wadName;
            }
        }
        return "Overrides";
    }

    /// <summary>M98c/d: write bytes to the asset's REAL path inside the per-WAD project folder
    /// (Map11.wad.client → Map11/data/…). False when this isn't a folder project or the path is
    /// unresolved — the caller falls back to the hashed override store.</summary>
    private bool TryPlaceInProjectFolder(WadAssetEntry entry, byte[] bytes, out string placedRelative)
    {
        placedRelative = "";
        if (!Project.IsFolderProject || !entry.IsResolved || Project.RootPath is null || _mounts is null) return false;

        string folderName = RiotWadFolderName(entry);
        string destFile = Path.Combine(Project.RootPath, folderName, entry.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        File.WriteAllBytes(destFile, bytes);
        if (!Project.ProjectFolders.Contains(folderName, StringComparer.OrdinalIgnoreCase))
            Project.ProjectFolders.Add(folderName);
        placedRelative = $"{folderName}/{entry.Path}";
        return true;
    }

    /// <summary>M98c/d: shared bookkeeping after a folder-placement copy: persist, remount, reselect.</summary>
    private void FinishProjectCopy(WadAssetEntry entry, string successMessage)
    {
        Project.IsDirty = true;
        // M107: during a multi-asset copy the caller rebuilds once at the end.
        if (_copyBatch) { _log.Success("Project", successMessage); return; }
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        BuildMounts();
        BuildProjectTree();
        if (_nodesByHash.TryGetValue(entry.PathHash, out var node)) SelectedNode = node;
        UpdateTitle();
        _log.Success("Project", successMessage);
    }

    /// <summary>M98c: when the asset's editable source is a real project-folder file, write edits to THAT
    /// file — creating a hashed override would shadow the folder copy and confuse everyone. False →
    /// caller falls back to the override store (single-WAD projects, unresolved chunks).</summary>
    private bool TryWriteToProjectFile(WadAssetEntry entry, byte[] bytes, out string file)
    {
        file = "";
        if (_mounts is null || !_mounts.TryGet(entry.PathHash, out var a)) return false;
        // M126: prefer the real project FILE over a shadow override. Overrides outrank folder files in
        // the mount order, so writing "the first editable source" kept updating the shadow while the
        // project file went stale — and the validator (reading files from disk) reported issues the
        // user had already fixed. The project file is the single source of truth; once it's written,
        // any shadow override of it is deleted so it can never mask an edit again.
        var sources = new[] { a.Source }.Concat(a.AllSources).Where(s => s is not null).Distinct().ToList();
        foreach (var kind in new[] { AssetSourceKind.ProjectFolder, AssetSourceKind.ProjectOverride })
            foreach (var src in sources)
            {
                if (src!.Kind != kind) continue;
                if (!src.TryGetFilePath(entry.PathHash, out file) || !File.Exists(file)) continue;
                File.WriteAllBytes(file, bytes);
                if (kind == AssetSourceKind.ProjectFolder) RemoveShadowOverride(entry, file);
                return true;
            }
        return false;
    }

    /// <summary>M126: dissolve a shadow override that duplicates a project file we just wrote in place.
    /// Rebuilds the mounts afterwards — the override mount indexes its directory, so the deleted file
    /// would otherwise still win reads for this hash. Handles record-less orphans too: the override
    /// mount is directory-scanned, so a shadow can exist with no entry in project.json.</summary>
    private void RemoveShadowOverride(WadAssetEntry entry, string projectFile)
    {
        var candidates = new List<string>();
        if (_overrides.TryGet(entry.PathHash, out var ov)) candidates.Add(ov.OverrideFile);
        try { candidates.Add(Path.Combine(ProjectWorkspace.OverridesDir(Project), $"{entry.PathHash:x16}.bin")); }
        catch { }

        bool removed = false;
        foreach (var f in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(f, projectFile, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(f)) continue;
            try { File.Delete(f); removed = true; } catch { }
        }
        _overrides.Remove(entry.PathHash);
        if (!removed) return;
        Project.IsDirty = true;
        RefreshBrowser();
        _log.Info("Project", $"Removed the stale shadow override of {entry.DisplayName} — the project file is the single source of truth again.");
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

    // ---- M74: Explorer-style file operations (project folder mounts are real files on disk) ----

    /// <summary>The window that owns modal prompts (rename/confirm). Set by MainWindow.</summary>
    public Avalonia.Controls.Window? PromptOwner { get; set; }

    /// <summary>Re-scan the project's disk state (mounts are indexed once, so file ops re-run the scan).</summary>
    [RelayCommand]
    public void RefreshBrowser()
    {
        if (ProjectMode) { BuildMounts(); BuildProjectTree(); }
        else RebuildTree();
    }

    // ---- M100: auto-refresh — watch the project folder so external edits/adds/deletes show up ----
    private readonly List<FileSystemWatcher> _projectWatchers = new();
    private System.Threading.Timer? _watchDebounce;

    /// <summary>Watch the project root for file changes and refresh the browser automatically. Events are
    /// debounced (bulk copies fire hundreds) and marshalled to the UI thread.</summary>
    private void StartProjectWatchers()
    {
        StopProjectWatchers();
        if (!ProjectMode || Project.RootPath is null || !Directory.Exists(Project.RootPath)) return;
        try
        {
            var w = new FileSystemWatcher(Project.RootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler onChange = (_, _) => ScheduleBrowserRefresh();
            w.Created += onChange; w.Deleted += onChange; w.Changed += onChange;
            w.Renamed += (_, _) => ScheduleBrowserRefresh();
            w.EnableRaisingEvents = true;
            _projectWatchers.Add(w);
        }
        catch { /* watching is a convenience — never block the project */ }
    }

    private void StopProjectWatchers()
    {
        foreach (var w in _projectWatchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        _projectWatchers.Clear();
    }

    private void ScheduleBrowserRefresh()
    {
        // .reyengine/ churn (project.json saves, reports) must not loop back into a refresh storm
        _watchDebounce?.Dispose();
        _watchDebounce = new System.Threading.Timer(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                try { RefreshBrowser(); _log.Info("Files", "Project folder changed — browser refreshed."); }
                catch { }
            }), null, 600, System.Threading.Timeout.Infinite);
    }

    /// <summary>The real on-disk file behind a node (editable folder/override mounts only).</summary>
    private bool TryGetNodeFile(AssetNodeViewModel? node, out string filePath)
    {
        filePath = "";
        return node?.Entry is { ReadOnly: false } entry
            && _mounts is not null
            && _mounts.TryGetFilePath(entry.PathHash, out filePath, out _);
    }

    /// <summary>Map a Content Browser FOLDER node to its disk directory (editable FolderMounts only):
    /// climb to the mount subtree root under the "Project" group, then append the folder's path.</summary>
    private bool TryResolveFolderDiskDir(AssetNodeViewModel? folder, out string dir) =>
        TryComputeFolderDiskDir(folder, out dir) && Directory.Exists(dir);

    /// <summary>M113: map a folder node to its disk path under a project folder mount, whether or not it
    /// exists there yet. Walks ancestry NAMES instead of Model.FullPath, because the virtual material
    /// folders (ASSETS/… grafted from .materials.bin) have no Model — with the old check, creating a
    /// folder while standing in one silently fell back to the mount root.</summary>
    private bool TryComputeFolderDiskDir(AssetNodeViewModel? folder, out string dir)
    {
        dir = "";
        if (folder is not { IsFolder: true } || _mounts is null) return false;
        var parts = new List<string>();
        var node = folder;
        while (node.Parent is { } p && p.Parent is not null) { parts.Add(node.Name); node = p; }   // node = mount subtree root
        if (node.Parent is null || !string.Equals(node.Parent.Name, "Project", StringComparison.Ordinal)) return false;
        if (_mounts.Mounts.FirstOrDefault(m => m is FolderMount && m.Name == node.Name) is not FolderMount mount) return false;
        parts.Reverse();
        dir = parts.Count == 0 ? mount.Location : Path.Combine(mount.Location, Path.Combine(parts.ToArray()));
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanRenameAsset))]
    private async Task RenameAsset(AssetNodeViewModel? node)
    {
        if (node is null || PromptOwner is null) return;
        try
        {
            if (TryGetNodeFile(node, out var file))
            {
                var newName = await Views.PromptWindow.InputAsync(PromptOwner, "Rename",
                    $"Rename '{Path.GetFileName(file)}' — the asset's WAD path (and hash) changes with it.",
                    Path.GetFileName(file), "Rename");
                if (string.IsNullOrWhiteSpace(newName) || newName == Path.GetFileName(file)) return;
                var target = Path.Combine(Path.GetDirectoryName(file)!, newName.Trim());
                if (File.Exists(target)) { _log.Warn("Files", $"'{newName}' already exists here."); return; }
                File.Move(file, target);
                _log.Success("Files", $"Renamed {Path.GetFileName(file)} → {newName}.");
                RefreshBrowser();
            }
            else if (TryResolveFolderDiskDir(node, out var dir))
            {
                var newName = await Views.PromptWindow.InputAsync(PromptOwner, "Rename Folder",
                    $"Rename folder '{node.Name}' — every asset inside changes its WAD path (and hash).",
                    node.Name, "Rename");
                if (string.IsNullOrWhiteSpace(newName) || newName == node.Name) return;
                var target = Path.Combine(Path.GetDirectoryName(dir)!, newName.Trim());
                if (Directory.Exists(target)) { _log.Warn("Files", $"Folder '{newName}' already exists here."); return; }
                Directory.Move(dir, target);
                _log.Success("Files", $"Renamed folder {node.Name} → {newName}.");
                RefreshBrowser();
            }
            else _log.Warn("Files", "Only editable project files/folders can be renamed. Copy the asset to the project first.");
        }
        catch (Exception ex) { _log.Error("Files", ex.Message); }
    }

    // ---- M112: deleting like Explorer does ----
    // RemoveDirectory fails with ACCESS_DENIED when the directory — or anything inside it — carries the
    // ReadOnly attribute, which OneDrive-backed folders routinely do. Explorer strips it silently; .NET
    // does not, which is why "delete" failed here while Explorer succeeded on the same folder.

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch { /* best effort — the delete below reports the real problem */ }
    }

    private static async Task ForceDeleteDirectoryAsync(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) ClearReadOnly(f);
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)) ClearReadOnly(d);
        ClearReadOnly(dir);
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Cloud sync and virus scanners hold brief handles right after a write; one retry clears it.
            await Task.Delay(200);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task ForceDeleteFileAsync(string file)
    {
        ClearReadOnly(file);
        try { File.Delete(file); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            await Task.Delay(200);
            File.Delete(file);
        }
    }

    /// <summary>Explain a delete failure in terms the user can act on.</summary>
    private string DeleteFailureHint(string path, Exception ex) =>
        ex is UnauthorizedAccessException or IOException
            ? $"{Path.GetFileName(path)}: {ex.Message} — it may be open in another program, or OneDrive/antivirus is holding it. Close it and try again."
            : $"{Path.GetFileName(path)}: {ex.Message}";

    [RelayCommand(CanExecute = nameof(CanDeleteAsset))]
    private async Task DeleteAsset(AssetNodeViewModel? node)
    {
        if (node is null || PromptOwner is null) return;
        try
        {
            if (node.Entry is { SourceKind: AssetSourceKind.ProjectOverride } ov)
            {
                if (!await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete Override",
                    $"Delete the project override for '{ov.DisplayName}'? The asset reverts to its original source.", "Delete"))
                    return;
                RevertSelectedFor(node);
                return;
            }
            if (TryGetNodeFile(node, out var file))
            {
                if (!await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete File",
                    $"Permanently delete '{Path.GetFileName(file)}' from the project folder?\n\n{file}", "Delete"))
                    return;
                await ForceDeleteFileAsync(file);
                _log.Success("Files", $"Deleted {Path.GetFileName(file)}.");
                RefreshBrowser();
            }
            else if (TryResolveFolderDiskDir(node, out var dir))
            {
                int n = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
                if (!await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete Folder",
                    $"Permanently delete folder '{node.Name}' and the {n:n0} file(s) inside?\n\n{dir}", "Delete"))
                    return;
                await ForceDeleteDirectoryAsync(dir);
                _log.Success("Files", $"Deleted folder {node.Name} ({n:n0} file(s)).");
                RefreshBrowser();
            }
            else _log.Warn("Files", "Only editable project files/folders can be deleted. Riot references are read-only.");
        }
        catch (Exception ex) { _log.Error("Files", DeleteFailureHint(node.Name, ex)); }
    }

    /// <summary>Revert a specific node's override (Delete on an override = revert to original).</summary>
    private void RevertSelectedFor(AssetNodeViewModel node)
    {
        // M100: point the command at this node without re-selecting it (SelectedNode reloads the preview).
        _contextOverride = node;
        try { if (RevertSelectedCommand.CanExecute(null)) RevertSelectedCommand.Execute(null); }
        finally { _contextOverride = null; }
    }

    /// <summary>M74: open any asset's raw bytes in the system text editor. Editable files open in place
    /// (external saves show up after a browser refresh); read-only assets open as a temp copy.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInTextEditor))]
    private void OpenInTextEditor(AssetNodeViewModel? node)
    {
        if (node?.Entry is not { } entry) return;
        try
        {
            string file;
            if (TryGetNodeFile(node, out var real)) file = real;
            else
            {
                var bytes = GetAssetBytes(entry);
                if (bytes is null) { _log.Warn("Files", "Asset bytes not available."); return; }
                var dir = Path.Combine(Path.GetTempPath(), "ReyEngine", "TextView");
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, entry.DisplayName);
                File.WriteAllBytes(file, bytes);
                _log.Info("Files", $"'{entry.DisplayName}' is read-only — opened a temporary copy.");
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { _log.Error("Files", ex.Message); }
    }

    /// <summary>M74: show the asset's real file in Windows Explorer (editable file-backed assets).</summary>
    [RelayCommand(CanExecute = nameof(CanShowInExplorer))]
    private void ShowInExplorer(AssetNodeViewModel? node)
    {
        try
        {
            if (TryGetNodeFile(node, out var file))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            else if (TryResolveFolderDiskDir(node, out var dir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            else _log.Warn("Files", "This asset has no standalone file on disk (it lives inside a WAD archive).");
        }
        catch (Exception ex) { _log.Error("Files", ex.Message); }
    }

    /// <summary>M74: move an editable file node into a Content Browser folder (internal drag & drop).</summary>
    public void MoveAssetToFolder(AssetNodeViewModel item, AssetNodeViewModel targetFolder)
    {
        try
        {
            if (!TryGetNodeFile(item, out var file))
            { _log.Warn("Files", "Only editable project files can be moved. Copy the asset to the project first."); return; }
            if (!TryResolveFolderDiskDir(targetFolder, out var dir))
            { _log.Warn("Files", "Drop target must be a folder inside an editable project folder."); return; }
            var target = Path.Combine(dir, Path.GetFileName(file));
            if (string.Equals(target, file, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(target)) { _log.Warn("Files", $"'{Path.GetFileName(file)}' already exists in that folder."); return; }
            File.Move(file, target);
            _log.Success("Files", $"Moved {Path.GetFileName(file)} → {targetFolder.Name}/ (its WAD path changed with it).");
            RefreshBrowser();
        }
        catch (Exception ex) { _log.Error("Files", ex.Message); }
    }

    /// <summary>M74: import external files (Explorer drag-drop) into a Content Browser folder.</summary>
    public void ImportExternalFiles(IReadOnlyList<string> files, AssetNodeViewModel? targetFolder)
    {
        if (!TryResolveFolderDiskDir(targetFolder, out var dir))
        { _log.Warn("Files", "Drop files onto a folder inside an editable project folder (e.g. one of your extracted WAD folders)."); return; }
        ImportExternalFilesTo(files, dir);
    }

    /// <summary>M109: import into a resolved directory (the Import command already picked the target).</summary>
    public void ImportExternalFilesTo(IReadOnlyList<string> files, string dir)
    {
        int copied = 0;
        foreach (var f in files)
        {
            try
            {
                if (File.Exists(f)) { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true); copied++; }
                else if (Directory.Exists(f))
                {
                    foreach (var sub in Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(f, sub);
                        var target = Path.Combine(dir, Path.GetFileName(f), rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(sub, target, overwrite: true);
                        copied++;
                    }
                }
            }
            catch (Exception ex) { _log.Warn("Files", $"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        if (copied > 0)
        {
            _log.Success("Files", $"Imported {copied} file(s) into {dir}");
            RefreshBrowser();
        }
    }

    // ---- M100: bulk operations on the Content Browser selection ---------
    // Single click selects, double click opens — so the context menu and the toolbar act on the
    // browser's SelectedItems rather than on "whatever happens to be open in the editor".

    /// <summary>Forces <see cref="ContextNode"/> for the duration of one internal call (see
    /// <see cref="RevertSelectedFor"/>, which drives a command against a specific node).</summary>
    private AssetNodeViewModel? _contextOverride;

    /// <summary>The node the single-asset commands act on: the Content Browser selection when there
    /// is one (right-clicking a tile selects it), otherwise whatever is open in the editor.</summary>
    private AssetNodeViewModel? ContextNode =>
        _contextOverride ?? (ContentBrowser.SelectedItems.Count > 0 ? ContentBrowser.SelectedItems[0] : SelectedNode);

    /// <summary>Every node a bulk operation should touch.</summary>
    private List<AssetNodeViewModel> ContextNodes =>
        ContentBrowser.SelectedItems.Count > 0
            ? ContentBrowser.SelectedItems.ToList()
            : SelectedNode is { } n ? new List<AssetNodeViewModel> { n } : new List<AssetNodeViewModel>();

    // ---- M108: context-menu gating ----
    // Every asset command declares when it applies, so the menu greys out what can't work here
    // instead of accepting the click and logging a refusal.

    /// <summary>Re-query the selection-dependent commands (called whenever the selection or folder changes).</summary>
    private void RaiseAssetCommandsCanExecute()
    {
        CopyAssetToProjectCommand.NotifyCanExecuteChanged();
        ReplaceSelectedCommand.NotifyCanExecuteChanged();
        RevertSelectedCommand.NotifyCanExecuteChanged();
        CopySelectionToCommand.NotifyCanExecuteChanged();
        MoveSelectionToCommand.NotifyCanExecuteChanged();
        DeleteSelectionCommand.NotifyCanExecuteChanged();
        ExportSelectedCommand.NotifyCanExecuteChanged();
        CopyResolvedPathCommand.NotifyCanExecuteChanged();
        CopyHashCommand.NotifyCanExecuteChanged();
        ImportFilesCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A file on disk we're allowed to move/rename/delete.</summary>
    private bool IsEditableFile(AssetNodeViewModel? n) => n is not null && TryGetNodeFile(n, out _);
    private bool IsEditableFolder(AssetNodeViewModel? n) => n is not null && TryResolveFolderDiskDir(n, out _);
    private bool IsOverride(AssetNodeViewModel? n) => n?.Entry is { SourceKind: AssetSourceKind.ProjectOverride };

    private bool CanCopyAssetToProject() => ProjectMode && _mounts is not null && ContextNodes.Any(n => n.Entry is not null);
    private bool CanReplaceSelected() => ProjectMode && ContextNode?.Entry is not null;
    private bool CanRevertSelected() => ContextNode?.Entry is { } e && _overrides.Has(e.PathHash);
    private bool CanCopySelectionTo() => ContextNodes.Any(n => !n.IsFolder && n.Entry is not null);
    private bool CanMoveSelectionTo() => ContextNodes.Any(IsEditableFile);
    private bool CanDeleteSelection() => ContextNodes.Any(n => IsEditableFile(n) || IsEditableFolder(n) || IsOverride(n));
    private bool CanExportSelected() => ContentLoaded && ContextNode?.Entry is not null;
    private bool CanCopyEntryText() => ContextNode?.Entry is not null;
    /// <summary>M109: enabled anywhere in the project — the command resolves a writable target itself.</summary>
    private bool CanImportFiles(AssetNodeViewModel? target) =>
        ProjectMode && (TryComputeFolderDiskDir(target, out _) || ContentBrowser.CanImportHere || ProjectFolderMounts.Count > 0);

    private bool CanRenameAsset(AssetNodeViewModel? node) => IsEditableFile(node) || IsEditableFolder(node);
    private bool CanDeleteAsset(AssetNodeViewModel? node) => IsEditableFile(node) || IsEditableFolder(node) || IsOverride(node);
    private bool CanShowInExplorer(AssetNodeViewModel? node) => IsEditableFile(node) || IsEditableFolder(node);
    private bool CanOpenInTextEditor(AssetNodeViewModel? node) => node?.Entry is not null;
    private bool CanOpenInMapBinEditor(AssetNodeViewModel? node) =>
        node?.Entry is { } e && e.DisplayName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>M197 (4.5): deliberately NOT gated on a ".bin" suffix, unlike the Map Bin Editor's command.
    /// 207 of the map VFX bins this exists to reach sit at extensionless paths
    /// (maps/modespecificdata/&lt;mode&gt;/&lt;spell&gt;/loadable), so a suffix test would hide exactly the
    /// assets the milestone is for. A non-VFX pick falls through to the existing "contains no VFX systems"
    /// warning, which is a cheap and clear failure.</summary>
    private bool CanOpenInParticleEditor(AssetNodeViewModel? node) => node?.Entry is not null;

    /// <summary>Editable folder mounts in the open project (the places we're allowed to write).</summary>
    private List<FolderMount> ProjectFolderMounts =>
        _mounts?.Mounts.OfType<FolderMount>().ToList() ?? new List<FolderMount>();

    /// <summary>
    /// M109: where Import / New Folder should write. The folder in view when it's writable, otherwise a
    /// project folder mount — so both work from anywhere in the project, including the tree root and
    /// while browsing read-only Riot References, instead of only deep inside a mount.
    /// </summary>
    /// <param name="target">The folder the user pointed at (a right-clicked tree node). Right-clicking
    /// in the TreeView doesn't select, so without this the command only ever saw the browser's current
    /// folder and silently created things in the mount root instead.</param>
    private async Task<string?> ResolveWriteTargetAsync(string action, AssetNodeViewModel? target = null)
    {
        // M113: materialize the directory when it only exists virtually so far (ASSETS/… from a
        // .materials.bin) — Explorer semantics: creating inside a path makes that path real.
        if (TryComputeFolderDiskDir(target, out var picked))
        { try { Directory.CreateDirectory(picked); return picked; } catch (Exception ex) { _log.Error("Files", ex.Message); return null; } }
        if (TryComputeFolderDiskDir(ContentBrowser.CurrentFolder, out var here))
        { try { Directory.CreateDirectory(here); return here; } catch (Exception ex) { _log.Error("Files", ex.Message); return null; } }

        var folders = ProjectFolderMounts;
        if (folders.Count == 0)
        {
            _log.Warn("Files", $"{action} needs an editable project folder — this project has none. "
                             + "Create a folder project, or use Copy Asset To Project to make one editable first.");
            return null;
        }
        if (folders.Count == 1)
        {
            _log.Info("Files", $"Not inside a writable folder — using {folders[0].Name}/.");
            return folders[0].Location;
        }
        if (PromptOwner is null) return folders[0].Location;

        // Several folder mounts and no obvious one: let the user say which rather than guessing, since
        // the folder becomes part of the asset's WAD path.
        var names = string.Join(", ", folders.Select(f => f.Name));
        var pick = await Views.PromptWindow.InputAsync(PromptOwner, $"{action} — choose a folder",
            $"You're not inside a writable project folder, so pick which one to use.\n\nAvailable: {names}",
            folders[0].Name, "Use");
        if (string.IsNullOrWhiteSpace(pick)) return null;
        var m = folders.FirstOrDefault(f => string.Equals(f.Name, pick.Trim(), StringComparison.OrdinalIgnoreCase));
        if (m is null) { _log.Warn("Files", $"'{pick.Trim()}' isn't one of: {names}"); return null; }
        return m.Location;
    }

    /// <summary>M108: create a subfolder in the project folder the browser is showing.</summary>
    [RelayCommand(CanExecute = nameof(CanImportFiles))]
    private async Task NewFolder(AssetNodeViewModel? target)
    {
        if (PromptOwner is null) return;
        if (await ResolveWriteTargetAsync("New Folder", target) is not { } parent) return;
        var name = await Views.PromptWindow.InputAsync(PromptOwner, "New Folder",
            $"Create a folder inside:\n{parent}\n\nIt becomes part of the asset's WAD path, so name it the way the game expects.",
            "NewFolder", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;

        // Keep it a single folder name — a path here would silently create a tree somewhere else.
        name = name.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        { _log.Warn("Files", $"'{name}' contains characters a folder name can't have."); return; }

        var created = Path.Combine(parent, name);
        if (Directory.Exists(created)) { _log.Warn("Files", $"'{name}' already exists here."); return; }
        try
        {
            Directory.CreateDirectory(created);
            _log.Success("Files", $"Created {created}");
            RefreshBrowser();
        }
        catch (Exception ex) { _log.Error("Files", ex.Message); }
    }

    /// <summary>Import external files into the folder the browser is showing.</summary>
    [RelayCommand(CanExecute = nameof(CanImportFiles))]
    private async Task ImportFiles(AssetNodeViewModel? target)
    {
        if (await ResolveWriteTargetAsync("Import", target) is not { } into) return;
        var files = await Dialogs.OpenFilesAsync("Import files into the project", DialogService.All);
        if (files.Count == 0) return;

        // M392: images get the conversion dialog; everything else is copied as before. Splitting rather
        // than converting the whole selection means a mixed drop (a .tex, a .bin and three PNGs) still
        // does the right thing with each part.
        var images = files.Where(TextureImportViewModel.IsConvertibleImage).ToList();
        var rest = files.Where(f => !TextureImportViewModel.IsConvertibleImage(f)).ToList();

        if (images.Count > 0 && ShowTextureImportWindow is { } show)
        {
            var vm = new TextureImportViewModel(images);
            switch (await show(vm))
            {
                case Views.TextureImportResult.Cancel:
                    return;                                   // cancels the WHOLE import, images and rest
                case Views.TextureImportResult.Convert:
                    WriteConvertedTextures(vm, into);
                    break;
                case Views.TextureImportResult.CopyAsIs:
                    rest.AddRange(images);
                    break;
            }
        }
        else rest.AddRange(images);                            // no dialog host: behave exactly as before

        if (rest.Count > 0) ImportExternalFilesTo(rest, into);
    }

    /// <summary>M392: set by the view; shows the import dialog and reports how it closed.</summary>
    public Func<TextureImportViewModel, Task<Views.TextureImportResult>>? ShowTextureImportWindow;

    private void WriteConvertedTextures(TextureImportViewModel vm, string into)
    {
        int done = 0;
        long bytes = 0;
        foreach (var (name, data) in vm.Encode())
        {
            try
            {
                File.WriteAllBytes(Path.Combine(into, name), data);
                done++; bytes += data.LongLength;
            }
            catch (Exception ex) { _log.Warn("Files", $"{name}: {ex.Message}"); }
        }
        int skipped = vm.Items.Count - done;
        if (done > 0)
        {
            _log.Success("Files", $"Converted {done} image(s) to .tex ({bytes / 1024.0 / 1024.0:0.##} MB) "
                                  + $"in {into}" + (skipped > 0 ? $" - {skipped} skipped" : ""));
            RefreshBrowser();
        }
        else _log.Warn("Files", "Nothing was converted - see the rows in the import dialog for why.");
    }

    /// <summary>Copy the selected assets out to a folder on disk. Works for read-only Riot references
    /// too (the bytes are read through the mounts), so it doubles as a bulk export.</summary>
    [RelayCommand(CanExecute = nameof(CanCopySelectionTo))]
    private async Task CopySelectionTo()
    {
        var nodes = ContextNodes.Where(n => !n.IsFolder && n.Entry is not null).ToList();
        if (nodes.Count == 0) { _log.Warn("Files", "Select one or more files first."); return; }
        var dir = await Dialogs.OpenFolderAsync($"Copy {nodes.Count} asset(s) to…");
        if (dir is null) return;
        int done = 0;
        foreach (var n in nodes)
        {
            try
            {
                File.WriteAllBytes(Path.Combine(dir, n.Entry!.DisplayName), GetAssetBytes(n.Entry));
                done++;
            }
            catch (Exception ex) { _log.Warn("Files", $"{n.Name}: {ex.Message}"); }
        }
        _log.Success("Files", $"Copied {done}/{nodes.Count} asset(s) → {dir}");
    }

    /// <summary>Move the selected project files to another folder. Read-only references can't move —
    /// copy them into the project first.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveSelectionTo))]
    private async Task MoveSelectionTo()
    {
        var nodes = ContextNodes.Where(n => !n.IsFolder).ToList();
        if (nodes.Count == 0) { _log.Warn("Files", "Select one or more files first."); return; }
        var dir = await Dialogs.OpenFolderAsync($"Move {nodes.Count} file(s) to…");
        if (dir is null) return;
        int done = 0, skipped = 0;
        foreach (var n in nodes)
        {
            if (!TryGetNodeFile(n, out var file)) { skipped++; continue; }
            try
            {
                var target = Path.Combine(dir, Path.GetFileName(file));
                if (string.Equals(target, file, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(target)) { _log.Warn("Files", $"'{Path.GetFileName(file)}' already exists there — skipped."); skipped++; continue; }
                File.Move(file, target);
                done++;
            }
            catch (Exception ex) { _log.Warn("Files", $"{n.Name}: {ex.Message}"); skipped++; }
        }
        if (skipped > 0) _log.Warn("Files", $"{skipped} item(s) skipped — only editable project files can be moved.");
        if (done > 0) { _log.Success("Files", $"Moved {done} file(s) → {dir} (their WAD paths changed with them)."); RefreshBrowser(); }
    }

    /// <summary>Delete every selected asset — one confirmation for the whole batch. Overrides revert
    /// to their original instead of being removed from disk.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelection))]
    private async Task DeleteSelection()
    {
        var nodes = ContextNodes;
        if (nodes.Count == 0 || PromptOwner is null) { _log.Warn("Files", "Select something to delete first."); return; }
        if (nodes.Count == 1) { await DeleteAsset(nodes[0]); return; }   // single item keeps the detailed prompt

        if (!await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete Selection",
            $"Delete {nodes.Count} selected item(s)?\n\nProject files are removed from disk; overrides revert to their original.",
            "Delete"))
            return;

        int deleted = 0, reverted = 0, skipped = 0;
        foreach (var node in nodes)
        {
            try
            {
                if (node.Entry is { SourceKind: AssetSourceKind.ProjectOverride }) { RevertSelectedFor(node); reverted++; }
                else if (TryGetNodeFile(node, out var file)) { await ForceDeleteFileAsync(file); deleted++; }
                else if (TryResolveFolderDiskDir(node, out var dir)) { await ForceDeleteDirectoryAsync(dir); deleted++; }
                else skipped++;
            }
            catch (Exception ex) { _log.Warn("Files", DeleteFailureHint(node.Name, ex)); skipped++; }
        }
        if (skipped > 0) _log.Warn("Files", $"{skipped} item(s) skipped — Riot references are read-only.");
        _log.Success("Files", $"Deleted {deleted} item(s){(reverted > 0 ? $", reverted {reverted} override(s)" : "")}.");
        RefreshBrowser();
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
        var progress = BuildProgressSink();
        try
        {
            await Task.Run(() => BuildProjectCore(buildRoot, progress));
            Status = $"Built project to {buildRoot}";
            _log.Success("Build", $"Project build ready: {buildRoot}. Open it via File ▸ Open Project Folder to verify.");
        }
        catch (Exception ex) { _log.Error("Build", ex.Message); }
        finally { IsBuilding = false; }
    }

    /// <summary>M131: build the project into <paramref name="buildRoot"/>. Returns the wads this
    /// build produced — callers must bundle exactly these, never "whatever sits in the folder".</summary>
    private List<string> BuildProjectCore(string buildRoot, IProgress<(double Frac, string Stage)>? progress = null)
    {
        var overridesByHash = _overrides.All.ToDictionary(o => o.PathHash, o => o.OverrideFile);
        int wads = 0, staged = 0, files = 0, skipped = 0;
        var produced = new List<string>();

        // M131: a fresh build starts from a CLEAN slate — leftover staging from earlier builds kept
        // files the project has since deleted or renamed, and they leaked into every later package.
        var stagingRoot = Path.Combine(buildRoot, "staged");
        progress?.Report((0.02, "Cleaning old staging…"));
        if (Directory.Exists(stagingRoot))
        {
            try { Directory.Delete(stagingRoot, recursive: true); }
            catch (Exception ex) { _log.Warn("Build", $"Could not fully clean {stagingRoot}: {ex.Message} — close programs holding files there."); }
        }

        // Project WADs: safe in-place replace of existing chunks.
        foreach (var w in Project.ProjectWads)
        {
            var src = Project.ResolveProjectPath(w);
            if (!File.Exists(src)) { _log.Warn("Build", $"missing project WAD {w}"); continue; }
            progress?.Report((0.05, $"Repacking {Path.GetFileName(w)}…"));
            using var arc = WadArchive.Open(src, _resolver);
            var apply = new Dictionary<ulong, byte[]>();
            foreach (var (hash, file) in overridesByHash)
                if (arc.TryGetEntry(hash, out _) && File.Exists(file)) apply[hash] = File.ReadAllBytes(file);
            var outWad = Path.Combine(buildRoot, Path.GetFileName(w));
            var report = new BuildReport { OutputPath = outWad };
            WadRepackService.Repack(src, apply, outWad, report);
            foreach (var i in report.Issues) _log.Warn("Build", i.Message);
            _log.Info("Build", $"WAD {Path.GetFileName(w)}: {apply.Count} replaced → {Path.GetFileName(outWad)}");
            produced.Add(outWad);
            wads++;
        }

        // Project folders: stage (copy tree + apply overrides as files — new files are safe in folder format).
        var stagedFolders = new List<(string name, string dir)>();
        int folderIdx = 0;
        foreach (var f in Project.ProjectFolders)
        {
            var srcFolder = Project.ResolveProjectPath(f);
            var name = f == "." ? Project.Name : f.Replace('/', '_');
            var outFolder = Path.Combine(stagingRoot, name);
            folderIdx++;
            int idx = folderIdx;
            files += CopyTree(srcFolder, outFolder, buildRoot,
                n => progress?.Report((0.05 + 0.25 * (idx - 1 + Math.Min(1.0, n / 15000.0)) / Project.ProjectFolders.Count,
                    $"Staging {name}… ({n:n0} files)")));
            stagedFolders.Add((name, outFolder));
            staged++;
        }

        // Apply overrides into the first staged folder at their resolved path.
        // M131: the project FILE is the single source of truth (M126) — an override must never
        // clobber a file the project actually ships. Only overrides for paths the folder does NOT
        // provide (wad-source assets copied to the workspace) belong in the package.
        if (stagedFolders.Count > 0)
        {
            var outFolder = stagedFolders[0].dir;
            foreach (var ov in _overrides.All)
            {
                if (!File.Exists(ov.OverrideFile)) { skipped++; continue; }
                string rel = ov.ResolvedPath ?? $"0x{ov.PathHash:x16}{Path.GetExtension(ov.OverrideFile)}";
                var dest = Path.Combine(outFolder, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(dest))
                {
                    _log.Warn("Build", $"Stale shadow override IGNORED for {rel} — the project file wins. Save that asset once in ReyEngine to dissolve the shadow.");
                    skipped++;
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(ov.OverrideFile, dest, overwrite: false);
                files++;
            }
        }

        // Pack each staged folder into a distributable .wad.client.
        int packed = 0;
        int packIdx = 0;
        foreach (var (name, dir) in stagedFolders)
        {
            var outWad = Path.Combine(buildRoot, name + ".wad.client");
            packIdx++;
            int idx = packIdx;
            var packProgress = new Progress<float>(fr => progress?.Report(
                (0.30 + 0.65 * (idx - 1 + fr) / stagedFolders.Count, $"Packing {name}.wad.client… {fr:P0}")));
            WadPackReport pr;
            try { pr = WadPackService.Pack(dir, outWad, packProgress, knownTypesOnly: Project.PackKnownTypesOnly); }
            catch (Exception ex) { _log.Error("Build", $"Pack failed for {name}: {ex.Message}"); continue; }
            foreach (var w in pr.Warnings) _log.Warn("Build", w);
            if (pr.CleanedUnknown.Count > 0)   // M132
            {
                _log.Info("Build", $"{name}: cleaned {pr.CleanedUnknown.Count} unknown file(s) from the package (not game formats):");
                foreach (var c in pr.CleanedUnknown.Take(10)) _log.Info("Build", $"   skipped {c}");
                if (pr.CleanedUnknown.Count > 10) _log.Info("Build", $"   … {pr.CleanedUnknown.Count - 10} more");
            }
            if (pr.Success)
            {
                packed++;
                produced.Add(outWad);
                _log.Success("Build", $"Packed {name}.wad.client — {pr.Chunks:n0} chunks, {pr.InputBytes / 1048576.0:0.0}→{pr.OutputBytes / 1048576.0:0.0} MB. {pr.Validation}");
            }
            else _log.Error("Build", $"Pack didn't validate for {name} — the staged folder is at {dir}.");
        }

        // M131: stale wads from renamed/removed folders must not linger next to fresh output —
        // the fantome export used to bundle every *.wad.client it found.
        foreach (var w in Directory.GetFiles(buildRoot, "*.wad.client"))
            if (!produced.Contains(w, StringComparer.OrdinalIgnoreCase))
            {
                try { File.Delete(w); _log.Info("Build", $"Removed stale build output {Path.GetFileName(w)} (not part of this project anymore)."); }
                catch (Exception ex) { _log.Warn("Build", $"Stale {Path.GetFileName(w)} could not be removed: {ex.Message}"); }
            }

        progress?.Report((1.0, "Build finished."));
        _log.Info("Build", $"project WADs: {wads} · folders packed: {packed}/{staged} · files: {files:n0} · skipped: {skipped}");
        return produced;
    }

    private static int CopyTree(string src, string dst, string excludedRoot, Action<int>? onProgress = null)
    {
        int n = 0;
        string fullDst = Path.GetFullPath(dst);
        string fullExcluded = Path.GetFullPath(excludedRoot);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(src));

        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                string fullChild = Path.GetFullPath(child);
                if (Path.GetFileName(child).Equals(".reyengine", StringComparison.OrdinalIgnoreCase)
                    || IsSameOrChild(fullChild, fullDst)
                    || IsSameOrChild(fullChild, fullExcluded))
                    continue;
                pending.Push(fullChild);
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var rel = Path.GetRelativePath(src, file);
                var dest = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
                n++;
                if (n % 250 == 0) onProgress?.Invoke(n);
            }
        }
        onProgress?.Invoke(n);
        return n;
    }

    /// <summary>Upload only the mapgeo ranges affected by the current edit.</summary>
    public void UpdateDx11EditedMeshVertices(ReyEngine.Rendering.D3D11.ShaderPreviewRenderer renderer)
    {
        if (_currentMap is not { } map) return;
        if (_selection.Count == 0)
        {
            renderer.UpdateMeshVertices(map.Positions, map.Normals, 0, map.VertexCount);
            return;
        }
        foreach (var mesh in _selection.Items)
            renderer.UpdateMeshVertices(map.Positions, map.Normals, mesh.VertexStart, mesh.VertexCount);
    }

    private static bool IsSameOrChild(string path, string root)
    {
        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.Equals(fullRoot, comparison)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
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
        var sink = BuildProgressSink();
        var wadProgress = new Progress<float>(f => sink.Report((f, $"Repacking {Path.GetFileName(outPath)}… {f:P0}")));
        try
        {
            var report = await Task.Run(() => BuildPackageService.Build(Project, outPath, wadProgress, CancellationToken.None));
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

    /// <summary>M210: the experimental DX11 shader preview. Isolated on purpose - it loads Riot's own
    /// compiled shaders on a real Direct3D 11 device to find out whether that route is viable, and applies
    /// nothing it learns to a map. Integration into the Material Editor is a later decision that depends on
    /// what this turns up.</summary>
    [RelayCommand]
    private void ShaderPreview()
    {
        string? dir = string.IsNullOrEmpty(Project.GameDirectory) ? null
            : Path.Combine(Project.GameDirectory, "DATA", "FINAL");

        // M213: hand the window the asset mounts so a real material can be loaded end to end - its
        // textures come out of the same VFS everything else reads, so project overrides win over Riot's
        // originals exactly as they do in the rest of the editor.
        var bins = AssetEntries
            .Where(e => e.IsResolved && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            .Select(e => (e.Path, e.PathHash))
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // M214: .skn and .mapgeo, so a whole character or map can be drawn with its real materials
        var scenes = AssetEntries
            .Where(e => e.IsResolved
                        && (e.Path.EndsWith(".skn", StringComparison.OrdinalIgnoreCase)
                            || e.Path.EndsWith(".mapgeo", StringComparison.OrdinalIgnoreCase)))
            .Select(e => (e.Path, e.PathHash))
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var vm = new ShaderPreviewViewModel(dir, _resolver.Database,
            readAsset: h => { try { return ReadAsset(h); } catch { return null; } },
            binAssets: bins,
            resolveBinName: h => _resolver.Database.TryGetBinName(h, out var n) ? n : null,
            sceneAssets: scenes);

        if (bins.Count == 0)
            _log.Info("Shader", "No .bin assets are mounted, so the Material tab will be empty. "
                                + "Open a project or a WAD first.");
        if (!vm.CacheAvailable)
            _log.Warn("Shader", "No shader cache found - the window opens, but there is nothing to load. "
                                + "Point the project at the game folder (the one containing DATA/FINAL).");

        var win = new Views.ShaderPreviewWindow { DataContext = vm };
        if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
        _log.Info("Shader", "DX11 Shader Preview opened (experimental).");
    }
    [RelayCommand] private void ClearConsole() { Console.Clear(); UnseenConsoleProblems = 0; }

    [RelayCommand]
    private void Exit() =>
        (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
}

/// <summary>
/// M152: one editable Light.dat point light. Position/colour/radius are individually bindable so the
/// inspector can edit them, and every change republishes the render list so the viewport updates live.
/// Colour is edited as 0..255 (how the file stores it) while PointLight carries linear 0..1.
/// </summary>
public sealed partial class PointLightViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private bool _loading = true;

    [ObservableProperty] private double _x, _y, _z;
    [ObservableProperty] private double _r, _g, _b;      // 0..255, matching the file
    [ObservableProperty] private double _radius;
    [ObservableProperty] private double _intensity = 1;  // M153: this light's OWN strength
    [ObservableProperty] private string _name = "Light";

    public PointLightViewModel(PointLight light, MainWindowViewModel owner)
    {
        _owner = owner;
        _x = light.Position.X; _y = light.Position.Y; _z = light.Position.Z;
        _r = Math.Round(light.Color.X * 255); _g = Math.Round(light.Color.Y * 255); _b = Math.Round(light.Color.Z * 255);
        _radius = light.Radius;
        _intensity = light.Intensity;
        _loading = false;
    }

    public System.Numerics.Vector3 Position => new((float)X, (float)Y, (float)Z);

    public PointLight ToPointLight() => new(
        Position,
        new System.Numerics.Vector3((float)(R / 255.0), (float)(G / 255.0), (float)(B / 255.0)),
        (float)Math.Max(Radius, 0.01),    // Parse drops radius <= 0, so never produce one
        (float)Math.Max(Intensity, 0));

    /// <summary>M288: the exact INVERSE of <see cref="ToPointLight"/>, for rebuilding a light from the
    /// 0-255 components the project stores.
    ///
    /// <para>It exists because the constructor above takes a PointLight whose colour is 0-1 and scales it
    /// UP by 255. Handing that constructor stored 0-255 values therefore multiplies twice, and M287 did
    /// exactly that: every restored light came back 255x too bright, which only showed itself after a
    /// bake because that is what reloads the map and runs the restore. Placed next to its inverse so the
    /// two conversions cannot be read - or changed - separately again.</para></summary>
    public static PointLight FromStored(double x, double y, double z,
                                        double r, double g, double b, double radius, double intensity)
        => new(new System.Numerics.Vector3((float)x, (float)y, (float)z),
               new System.Numerics.Vector3((float)(r / 255.0), (float)(g / 255.0), (float)(b / 255.0)),
               (float)Math.Max(radius, 0.01),
               (float)Math.Max(intensity, 0));

    /// <summary>M154: the colour as a real Color, so the inspector can use a proper picker (spectrum +
    /// palette + hex) instead of three raw sliders. Backed by the same R/G/B the file stores.</summary>
    public Avalonia.Media.Color Color
    {
        get => Avalonia.Media.Color.FromRgb(Byte(R), Byte(G), Byte(B));
        set
        {
            if (value.R == Byte(R) && value.G == Byte(G) && value.B == Byte(B)) return;
            _loading = true;                      // one Changed() for the whole colour, not three
            R = value.R; G = value.G; B = value.B;
            _loading = false;
            Changed();
        }
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    /// <summary>Outliner label — position + radius, so lights are tellable apart at a glance.</summary>
    public string Label => $"{Name}  ({X:0}, {Y:0}, {Z:0})  r{Radius:0}";
    public string ColorHex => $"#{(int)Math.Clamp(R, 0, 255):X2}{(int)Math.Clamp(G, 0, 255):X2}{(int)Math.Clamp(B, 0, 255):X2}";
    public string Info => $"radius {Radius:0} · strength {Intensity:0.##}";

    /// <summary>Move from a viewport gizmo drag.</summary>
    public void MoveTo(System.Numerics.Vector3 p)
    {
        _loading = true;
        X = p.X; Y = p.Y; Z = p.Z;
        _loading = false;
        Changed();
    }

    partial void OnXChanged(double v) => Changed();
    partial void OnYChanged(double v) => Changed();
    partial void OnZChanged(double v) => Changed();
    partial void OnRChanged(double v) => Changed();
    partial void OnGChanged(double v) => Changed();
    partial void OnBChanged(double v) => Changed();
    partial void OnRadiusChanged(double v) => Changed();
    partial void OnIntensityChanged(double v) => Changed();
    partial void OnNameChanged(string v) => Changed();

    private void Changed()
    {
        if (_loading) return;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ColorHex));
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(Info));
        OnPropertyChanged(nameof(Position));
        _owner.RepublishLights();
    }
}
