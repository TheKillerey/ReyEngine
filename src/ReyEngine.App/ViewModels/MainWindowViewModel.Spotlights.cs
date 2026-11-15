using System.Numerics;
using LeagueToolkit.Core.Meta;
using ReyEngine.Core.Hashing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meta;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M900: the League spotlights (<c>MapDynamicSpotlight</c>) - the sibling of the dynamic point lights. They are map placements,
/// so they load with the map's other placements, appear in the outliner's Spotlights folder, move and turn with the viewport
/// gizmo (undoable), and save through the placement writer on Save to Mod.
///
/// <para>The viewport shows each as the spotlight icon, and the selected one as its frustum wireframe along local -Z. On
/// Direct3D 11 they light the map through Riot's own spot loop (the cluster grid, see ClusterLightBuilder); the OpenGL viewport
/// shows the icons and the wireframe but does not light with them.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    [ObservableProperty] private SpotlightViewModel? _selectedSpotlight;

    /// <summary>World positions of the spotlight icons the viewport draws (editor-visible, enabled, not deleted).</summary>
    [ObservableProperty] private IReadOnlyList<Vector3>? _spotlightMarkers;

    /// <summary>Line list (xyz per vertex) of the SELECTED spotlight's frustum, for the OpenGL viewport; D3D11 reads
    /// <see cref="Dx11SpotlightFrustumLines"/>.</summary>
    [ObservableProperty] private float[]? _spotlightFrustumLines;

    /// <summary>What the D3D11 viewport lights the map with: the enabled spotlights packed for Riot's spot loop. A NEW list on
    /// every republish (the renderer's change signal is the reference).</summary>
    public IReadOnlyList<ClusterSpotLight>? Dx11SpotLights { get; private set; }
    public SpotCookieAtlas? Dx11SpotCookieAtlas { get; private set; }

    /// <summary>Raised whenever the selection or an edit changes the wireframe, so cached overlay data refreshes.</summary>
    private int _spotlightRevision;
    public int SpotlightRevision => _spotlightRevision;

    public bool HasSpotlights => MapContent.Spotlights.Count > 0;
    public bool HasSelectedSpotlight => SelectedSpotlight is not null;

    private bool CanPickSpotlights => ShowLightMarkers && MapContent.HasSpotlights;

    // ---- loading ----------------------------------------------------------------------------------------------------

    /// <summary>Read the map bin's spotlights into the outliner. Called with the other placements, before the D3D11 scene is built.</summary>
    private void LoadSpotlights(byte[] materialsBin)
    {
        // its own guard: a failure here must not blank the particles, probes, props and sounds read beside it
        try
        {
            var spots = DynamicSpotlights.Read(materialsBin, ResolveWadPath);
            MapContent.SetSpotlights(spots);
            _spotCookieCache.Clear(); _spotCookieMiss.Clear();
            if (spots.Count > 0)
                _log.Info("MapGeo", $"{spots.Count} spotlight(s) (MapDynamicSpotlight). They light the D3D11 viewport while the Dynamic lighting mode is on.");
            RepublishSpotlights();
        }
        catch (Exception ex) { _log.Error("Spotlights", $"The map's spotlights could not be loaded: {ex.Message}"); }
    }

    // ---- publish ----------------------------------------------------------------------------------------------------

    private readonly Dictionary<ulong, SpotCookieImage?> _spotCookieCache = new();
    /// <summary>Cookies that failed to decode, with when: retried after a couple of seconds rather than never (an override may be
    /// added) and rather than on every keystroke while a path is being typed.</summary>
    private readonly Dictionary<ulong, long> _spotCookieMiss = new();
    private const long CookieRetryMs = 2000;
    private string _spotAtlasKey = "";
    private SpotCookieAtlas? _spotAtlas;

    /// <summary>Rebuild everything the viewports read from the spotlight list. Cheap enough to run on every gizmo tick: the cookie
    /// atlas is only rebuilt when the set of cookies changes.</summary>
    public void RepublishSpotlights()
    {
        var all = MapContent.Spotlights;
        SpotlightMarkers = CanPickSpotlights
            ? all.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved).Select(v => v.Position).ToList() : null;

        var lit = all.Where(v => !v.IsDisabled && !v.IsRemoved).Select(v => v.Current).ToList();
        if (lit.Count == 0)
        {
            Dx11SpotLights = null;
        }
        else
        {
            // decode each referenced cookie once
            long now = Environment.TickCount64;
            foreach (var h in lit.Select(l => l.CookieHash).Where(h => h != 0).Distinct())
            {
                if (_spotCookieCache.ContainsKey(h)) continue;
                if (_spotCookieMiss.TryGetValue(h, out long at) && now - at < CookieRetryMs) continue;
                var image = DecodeCookie(h);
                if (image is not null) { _spotCookieCache[h] = image; _spotCookieMiss.Remove(h); }
                else _spotCookieMiss[h] = now;   // a failure is not remembered as a cookie
            }
            var packed = SpotlightPacking.Pack(lit, _spotCookieCache);
            string key = string.Join(",", packed.CookieOrder) + "|" + packed.AtlasWidth;
            if (key != _spotAtlasKey || (packed.AtlasRgba is not null && _spotAtlas is null))
            {
                _spotAtlasKey = key;
                _spotAtlas = packed.AtlasRgba is null ? null : new SpotCookieAtlas(packed.AtlasRgba, packed.AtlasWidth, packed.AtlasHeight);
            }
            Dx11SpotLights = packed.Lights.ToList();
            Dx11SpotCookieAtlas = _spotAtlas;
        }
        if (lit.Count == 0) { Dx11SpotCookieAtlas = null; _spotAtlas = null; _spotAtlasKey = ""; }

        _spotlightRevision++;
        SpotlightFrustumLines = SelectedSpotlight is { IsRemoved: false } sel
            ? DynamicSpotlights.BuildFrustumLines(sel.CurrentTransform, (float)sel.Fov, (float)sel.Far, (float)sel.Near, (float)sel.Aspect)
            : null;

        OnPropertyChanged(nameof(HasSpotlights));
        // A map whose FIRST spotlight arrives after the D3D11 scene was built needs the in-shader light loop pinned (M844).
        OnPropertyChanged(nameof(HasDynamicLights));
    }

    private SpotCookieImage? DecodeCookie(ulong hash)
    {
        try
        {
            var bytes = TryReadAssetBytes(hash);
            if (bytes is null) return null;
            var img = TextureDecoder.TryDecodeTopMip(bytes) ?? TextureDecoder.Decode(bytes);
            return new SpotCookieImage(img.Rgba, img.Width, img.Height);
        }
        catch { return null; }   // a missing or subchunked cookie is a white one, never a failed frame
    }

    /// <summary>The selected spotlight's frustum for the D3D11 viewport (the SAME builder GL uses), or null.</summary>
    public float[]? Dx11SpotlightFrustumLines => SpotlightFrustumLines;

    partial void OnShowLightMarkersChanged(bool value) => RepublishSpotlights();   // the icons follow the same switch as the point lights

    // ---- selection --------------------------------------------------------------------------------------------------

    partial void OnSelectedSpotlightChanged(SpotlightViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedSpotlight));
        if (value is { } s)
        {
            // exclusive selection, as for every other placement
            _selection.Clear();
            if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
            if (SelectedParticleNode is not null) SelectedParticleNode = null;
            if (SelectedPropTreeItem is not null) SelectedPropTreeItem = null;
            if (SelectedPropNode is not null) SelectedPropNode = null;
            if (SelectedSound is not null) SelectedSound = null;
            if (SelectedProbe is not null) SelectedProbe = null;
            if (SelectedAddedMesh is not null) SelectedAddedMesh = null;
            if (SelectedLight is not null) SelectedLight = null;
            SelectedParticleMarker = s.Position;
            GizmoPivot = s.Position;
            SelectedPlaceableInfo = "";
        }
        else if (_selection.IsEmpty && SelectedParticleNode is null && SelectedSound is null && SelectedAddedMesh is null
                 && SelectedLight is null && SelectedPropNode is null)
            GizmoPivot = null;
        RepublishSpotlights();
    }

    /// <summary>A spotlight's state changed (an inspector field, the eye, Disable, Delete): everything it feeds follows.</summary>
    private void OnSpotlightStateChanged(SpotlightViewModel s)
    {
        // runtime Disable writes a zero visibility mask and can be restored (as for every other placement)
        if (s.IsDisabled != (s.EffectiveVisibilityFlags == 0))
            s.EditedVisibilityFlags = s.IsDisabled ? 0 : (s.Spot.HasVisibilityFlags && s.Spot.VisibilityFlags == 0 ? 255 : null);
        if (s.IsRemoved && ReferenceEquals(SelectedSpotlight, s)) SelectedSpotlight = null;
        RepublishSpotlights();
    }

    // ---- commands ---------------------------------------------------------------------------------------------------

    private static Matrix4x4 PointingDown(Vector3 at) =>
        new(1, 0, 0, 0,
            0, 0, -1, 0,      // local Y = world -Z
            0, 1, 0, 0,       // local Z = world +Y, so the light (along local -Z) shines DOWN
            at.X, at.Y, at.Z, 1);

    /// <summary>Add a spotlight above the gizmo (or the last marker, or the origin), aimed straight down so its pool lands on the
    /// thing you were looking at. Saved on Save to Mod.</summary>
    [RelayCommand]
    private void AddSpotlight()
    {
        if (_currentMap is null) { _log.Warn("Spotlights", "Open a map first."); return; }
        var at = GizmoPivot ?? SelectedParticleMarker ?? Vector3.Zero;
        var spot = new DynamicSpotlight(default, $"Spotlight {MapContent.Spotlights.Count + 1}", PointingDown(at + new Vector3(0, 300f, 0)),
            new Vector4(1f, 0.9f, 0.7f, 1f), DynamicSpotlights.DefaultFov, 800f, DynamicSpotlights.DefaultNear,
            DynamicSpotlights.DefaultAspect, 0, "", 0, 255, false);
        var vm = SpotlightViewModel.Create(spot, true, OnMapContentItemStateChanged);
        MapContent.AddSpotlight(vm);
        ShowLightMarkers = true;
        ShowDynamicLights = true;
        SelectedOutlinerItem = vm;
        RefreshPlacementDirtyFlag();
        RepublishSpotlights();
        _log.Info("Spotlights", $"Added '{vm.Name}' above ({at.X:0}, {at.Y:0}, {at.Z:0}). Drag the gizmo to place it, rotate it to aim it; Save to Mod writes it.");
    }

    [RelayCommand]
    private void DuplicateSpotlight()
    {
        if (SelectedSpotlight is not { } src) return;
        var now = src.Current with { Id = default, Name = src.Name + " copy" };
        var vm = SpotlightViewModel.Create(now, true, OnMapContentItemStateChanged);
        vm.Offset = new Vector3(100f, 0f, 0f);
        MapContent.AddSpotlight(vm);
        SelectedOutlinerItem = vm;
        RefreshPlacementDirtyFlag();
        RepublishSpotlights();
        _log.Info("Spotlights", $"Duplicated '{src.Name}'. Save to Mod writes the copy.");
    }

    /// <summary>Delete the selected spotlight: one that was never saved just goes away, a saved one is removed from the bin on Save to Mod.</summary>
    [RelayCommand]
    private void DeleteSpotlight()
    {
        if (SelectedSpotlight is not { } s) return;
        SelectedSpotlight = null;
        if (s.IsNew) MapContent.RemoveSpotlight(s);
        else s.IsRemoved = true;
        RefreshPlacementDirtyFlag();
        RepublishSpotlights();
    }

    [RelayCommand]
    private void ResetSpotlightEdits()
    {
        if (SelectedSpotlight is not { } s) return;
        s.ResetEdits();
        GizmoPivot = s.Position;
        SelectedParticleMarker = s.Position;
        RefreshPlacementDirtyFlag();
        RepublishSpotlights();
    }

    // ---- saving -----------------------------------------------------------------------------------------------------

    /// <summary>The placement edits for every changed spotlight. A new light is given its identity HERE, in the container that already
    /// holds the most spotlights, so a repeated save lands on the same key.</summary>
    private List<MapPlacementEdit> BuildSpotlightEdits(byte[] source, IReadOnlyList<SpotlightViewModel> edited)
    {
        var edits = new List<MapPlacementEdit>();
        BinTree? tree = null;
        BinTreeObject? container = null;
        var allocated = new HashSet<uint>();
        foreach (var v in edited)
        {
            if (v.IsNew && v.IsRemoved) continue;
            if (v.IsNew)
            {
                if (!v.Id.IsValid)
                {
                    tree ??= SafeBinTree.Parse(source);
                    container ??= DynamicSpotlights.TargetContainer(tree);
                    // not skipped: a save that quietly dropped the light would then reload the list and delete it
                    if (container is null) throw new InvalidOperationException(
                        $"'{v.Name}' cannot be saved: the map's materials.bin has no placeable container to put it in. Nothing was written.");
                    uint seed = HashAlgorithms.Fnv1a(v.Name ?? "spot") + (uint)allocated.Count;
                    uint key;
                    do { key = MapPlaceableWriter.NewItemKey(tree, new MapPlacementId(container.PathHash, seed++)); }
                    while (!allocated.Add(key));
                    v.Id = new MapPlacementId(container.PathHash, key);
                }
                edits.Add(DynamicSpotlights.CreateEdit(v.Id, v.Current));
            }
            else if (!v.Id.IsValid) _log.Warn("Spotlights", $"'{v.Name}' has no identity in the bin and was skipped.");
            else if (v.IsRemoved) edits.Add(new MapPlacementEdit(v.Id) { Remove = true });
            else if (DynamicSpotlights.EditFor(v.Spot, v.Current) is { } e) edits.Add(e);
        }
        return edits;
    }

    /// <summary>After a successful save the saved bin IS the authored state: reload the list from it so a second save does not try to
    /// create or remove the same thing twice. The selection is kept by identity.</summary>
    private void ReloadSpotlightsAfterSave(byte[] savedBin)
    {
        var selectedId = SelectedSpotlight?.Id ?? default;
        SelectedSpotlight = null;
        // undo entries hold the objects about to be replaced; undoing a move across a save would move a ghost
        foreach (var old in MapContent.Spotlights.ToList()) UndoService.PurgeContext(old);
        MapContent.SetSpotlights(DynamicSpotlights.Read(savedBin, ResolveWadPath));
        if (selectedId.IsValid && MapContent.Spotlights.FirstOrDefault(s => s.Id == selectedId) is { } again)
            SelectedOutlinerItem = again;
        RepublishSpotlights();
    }
}
