using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Interop;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M843: navgrid VARIANTS. A map ships several navigation grids (Map11 alone has 16 whole grids) and eight state overlays
/// (<c>.ngrid_overlay</c>: the cells a dragon terrain, a baron pit shape or an Atakhan pit changes). The overlay shows the
/// grid the open geometry plays on, with the overlay the Map Visibility selection implies on top of it; two pickers next to
/// the layer list choose another grid or another overlay. View only, as before.
///
/// <para><b>Riot's own handling is not emulated.</b> The data names the grid per geometry set (<c>NavGridPath</c> of its
/// map container) and per map skin (<c>mNavigationMesh</c>), and lists the overlays by a hashed name; nothing in the bins
/// says when the game switches an overlay on. The editor reads the overlay named <c>NavGrid_&lt;layer&gt;</c> as that layer's
/// (see <see cref="NavGridVariantCatalog"/>) and shows an overlay by replacing the base cells' flag words in its blocks
/// (<see cref="NavGridOverlay"/>).</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    private NavGridVariantCatalog? _navCatalog;
    private NavGridDeclarations _navDecl = NavGridDeclarations.Empty;
    private string _navMapFolder = "";
    private string? _navStem;
    private List<string> _navWadPaths = new();
    private bool _navSkinsLoaded;
    private bool _navUiLoading;
    private readonly Dictionary<string, (NavGrid? Grid, string? Error)> _navGridCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (NavGridOverlay? Overlay, string? Error)> _navOverlayCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The grid the user picked (a catalog id), "" = the catalog's default for the open geometry.</summary>
    private string _navGridChoice = "";
    /// <summary>"auto" (follow Map Visibility), "none", or an overlay id.</summary>
    private string _navOverlayChoice = "auto";
    /// <summary>The overlay ids the grid on screen has applied.</summary>
    private List<string> _navAppliedOverlays = new();
    /// <summary>The overlay ids the last selection ASKED for (some may be unavailable, so they differ from the applied ones).</summary>
    private List<string> _navRequestedOverlays = new();

    public ObservableCollection<string> NavGridGridLabels { get; } = new();
    public ObservableCollection<string> NavGridOverlayLabels { get; } = new();

    [ObservableProperty] private int _navGridGridIndex;
    [ObservableProperty] private int _navGridOverlayIndex;
    /// <summary>One line under the pickers: which grid and overlays are on screen.</summary>
    [ObservableProperty] private string _navGridVariantSummary = "";

    /// <summary>True when the open map offers a choice (more than one grid, or any overlay).</summary>
    public bool HasNavGridVariants => _navCatalog is { } c && (c.Grids.Count > 1 || c.Overlays.Count > 0);

    partial void OnNavGridGridIndexChanged(int value)
    {
        if (_navUiLoading || _navCatalog is null || value < 0 || value >= _navCatalog.Grids.Count) return;
        _navGridChoice = _navCatalog.Grids[value].Id;
        ApplyNavGridSelection();
    }

    partial void OnNavGridOverlayIndexChanged(int value)
    {
        if (_navUiLoading || _navCatalog is null || value < 0) return;
        _navOverlayChoice = value == 0 ? "auto" : value == 1 ? "none"
            : value - 2 < _navCatalog.Overlays.Count ? _navCatalog.Overlays[value - 2].Id : "auto";
        ApplyNavGridSelection();
    }

    private static List<string> NavGridPathsOf(IEnumerable<string> all) =>
        all.Where(p => p.StartsWith("assets/maps/navgrid/", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Read the geometry's declarations (its own materials.bin) and build the variant lists. Called when a map opens.</summary>
    private void InitNavGridVariants(string mapFolder, string mapGeoPath)
    {
        _navMapFolder = mapFolder;
        string file = mapGeoPath.Replace('\\', '/');
        file = file[(file.LastIndexOf('/') + 1)..];
        _navStem = file.EndsWith(".mapgeo", StringComparison.OrdinalIgnoreCase) ? file[..^".mapgeo".Length] : file;
        _navWadPaths = NavGridPathsOf(AssetEntries.Where(e => e.IsResolved).Select(e => e.Path));
        _navSkinsLoaded = false;
        _navGridCache.Clear();
        _navOverlayCache.Clear();
        _navGridChoice = "";
        _navOverlayChoice = "auto";

        var bins = new List<byte[]>();
        try { if (TryResolveMaterialsBin(mapGeoPath, out var matEntry)) bins.Add(ReadAsset(matEntry.PathHash)); }
        catch (Exception ex) { _log.Info("NavGrid", "Could not read the map's materials.bin for its navgrid declaration: " + ex.Message); }
        _navDecl = NavGridDeclarations.Read(bins);
        RebuildNavGridCatalog();

        // which map skins name each grid is wanted only for the picker's tooltips: parsed once the open has finished
        // (the shipping bin is several MB), never inside it
        int generation = ++_navGeneration;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (generation == _navGeneration) EnsureNavGridSkins(); },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private int _navGeneration;

    /// <summary>The catalog again, with the current Map Visibility definition (it is read after the navgrid on the first
    /// open path) and, once loaded, the map skins' own choices.</summary>
    private void RebuildNavGridCatalog()
    {
        if (_navMapFolder.Length == 0) return;
        _navCatalog = NavGridVariantCatalog.Build(_navMapFolder, _navStem, _navWadPaths, _navDecl,
            _mapVisibility.HasAxes ? _mapVisibility : null, ResolveBinName);

        _navUiLoading = true;
        try
        {
            NavGridGridLabels.Clear();
            foreach (var g in _navCatalog.Grids) NavGridGridLabels.Add(NavGridLabel(g));
            NavGridOverlayLabels.Clear();
            NavGridOverlayLabels.Add("Auto (follows Map Visibility)");
            NavGridOverlayLabels.Add("None");
            foreach (var o in _navCatalog.Overlays) NavGridOverlayLabels.Add(o.Label + (o.InGame ? "" : " (file missing)"));

            string gridId = _navGridChoice.Length > 0 && _navCatalog.Find(_navGridChoice) is not null ? _navGridChoice : _navCatalog.DefaultGridId;
            NavGridGridIndex = Math.Max(0, _navCatalog.Grids.ToList().FindIndex(g => string.Equals(g.Id, gridId, StringComparison.OrdinalIgnoreCase)));
            int ov = _navOverlayChoice == "auto" ? 0 : _navOverlayChoice == "none" ? 1
                : 2 + _navCatalog.Overlays.ToList().FindIndex(o => string.Equals(o.Id, _navOverlayChoice, StringComparison.OrdinalIgnoreCase));
            NavGridOverlayIndex = ov < 2 && _navOverlayChoice is not ("auto" or "none") ? 0 : ov;
            if (NavGridOverlayIndex == 0) _navOverlayChoice = "auto";
        }
        finally { _navUiLoading = false; }
        OnPropertyChanged(nameof(HasNavGridVariants));
    }

    private string NavGridLabel(NavGridVariant g) =>
        _navGridCache.TryGetValue(g.Path, out var c) && c.Grid is null && c.Error is { } why ? g.Label + "  [" + why + "]" : g.Label;

    /// <summary>The map skins' own grid choices (the shipping bin): parsed when first wanted, never on a map open.</summary>
    private void EnsureNavGridSkins()
    {
        if (_navSkinsLoaded || _navMapFolder.Length == 0) return;
        _navSkinsLoaded = true;
        try
        {
            string id = _navMapFolder.StartsWith("map", StringComparison.OrdinalIgnoreCase) ? _navMapFolder[3..] : _navMapFolder;
            if (ReadAssetByPath($"data/maps/shipping/map{id}/map{id}.bin") is { } shipping)
            {
                var skins = NavGridDeclarations.Read(new[] { shipping });
                _navDecl = new NavGridDeclarations
                {
                    ContainerGrids = _navDecl.ContainerGrids,
                    SkinGrids = skins.SkinGrids,
                    Overlays = _navDecl.Overlays.Count > 0 ? _navDecl.Overlays : skins.Overlays,
                };
                RebuildNavGridCatalog();
            }
        }
        catch (Exception ex) { _log.Info("NavGrid", "Could not read the map skins' navgrid choices: " + ex.Message); }
    }

    /// <summary>The overlay ids implied by the picker and a Map Visibility selection.</summary>
    private List<string> EffectiveNavOverlays(string overlayChoice, IReadOnlyDictionary<uint, int>? selections)
    {
        if (_navCatalog is null || overlayChoice == "none") return new List<string>();
        if (overlayChoice == "auto") return _navCatalog.OverlaysFor(selections).Select(o => o.Id).ToList();
        return _navCatalog.Find(overlayChoice) is { Kind: NavGridVariant.KindOverlay } o ? new List<string> { o.Id } : new List<string>();
    }

    /// <summary>What <see cref="ResolveNavGrid"/> found.</summary>
    private sealed record NavGridResolved(NavGrid? Grid, WadAssetEntry? Entry, NavGridVariant? Base,
        IReadOnlyList<(NavGridVariant Overlay, int Changed, int Outside)> Overlays, string? Error)
    {
        /// <summary>The composite id the link serves: the grid, then each overlay.</summary>
        public string Id => Base is null ? "" : string.Join("+", new[] { Base.Id }.Concat(Overlays.Select(o => o.Overlay.Id)));
    }

    private (NavGrid? Grid, string? Error) LoadNavGridFile(string path)
    {
        if (_navGridCache.TryGetValue(path, out var cached)) return cached;
        (NavGrid?, string?) result;
        try
        {
            ulong hash = HashAlgorithms.WadPath(path);
            if (!TryResolveEntry(hash, out _)) result = (null, "not in the game files");
            else if (NavGrid.TryParse(ReadAsset(hash), out var grid, out string? why) && grid is not null) result = (grid, null);
            else result = (null, why is { } w && w.StartsWith("navgrid version") ? "unsupported version " + w["navgrid version ".Length..].Split(' ')[0] : why ?? "unreadable");
        }
        catch (Exception ex) { result = (null, ex.Message); }
        _navGridCache[path] = result;
        return result;
    }

    private (NavGridOverlay? Overlay, string? Error) LoadNavOverlayFile(string path)
    {
        if (_navOverlayCache.TryGetValue(path, out var cached)) return cached;
        (NavGridOverlay?, string?) result;
        try
        {
            ulong hash = HashAlgorithms.WadPath(path);
            if (!TryResolveEntry(hash, out _)) result = (null, "not in the game files");
            else if (NavGridOverlay.TryParse(ReadAsset(hash), out var o, out string? why) && o is not null) result = (o, null);
            else result = (null, why ?? "unreadable");
        }
        catch (Exception ex) { result = (null, ex.Message); }
        _navOverlayCache[path] = result;
        return result;
    }

    /// <summary>A grid and overlays turned into the grid to show. Touches no UI state.</summary>
    private NavGridResolved ResolveNavGrid(string gridId, IReadOnlyList<string> overlayIds)
    {
        var none = Array.Empty<(NavGridVariant, int, int)>();
        if (_navCatalog is null) return new NavGridResolved(null, null, null, none, "no map is open");
        var baseVariant = _navCatalog.Find(gridId.Length > 0 ? gridId : _navCatalog.DefaultGridId);
        if (baseVariant is not { Kind: NavGridVariant.KindGrid })
            return new NavGridResolved(null, null, null, none, "this map ships no navgrid");
        TryResolveEntry(HashAlgorithms.WadPath(baseVariant.Path), out var entry);
        var (grid, error) = LoadNavGridFile(baseVariant.Path);
        if (grid is null) return new NavGridResolved(null, entry, baseVariant, none, $"{baseVariant.Id}: {error}");

        var applied = new List<(NavGridVariant, int, int)>();
        foreach (var id in overlayIds)
        {
            if (_navCatalog.Find(id) is not { Kind: NavGridVariant.KindOverlay } ov) continue;
            var (overlay, why) = LoadNavOverlayFile(ov.Path);
            if (overlay is null) { _log.Info("NavGrid", $"Overlay {ov.Id} not applied: {why}"); continue; }
            try
            {
                grid = overlay.ApplyTo(grid, out int changed, out int outside);
                applied.Add((ov, changed, outside));
            }
            catch (Exception ex) { _log.Info("NavGrid", $"Overlay {ov.Id} not applied: {ex.Message}"); }
        }
        return new NavGridResolved(grid, entry, baseVariant, applied, null);
    }

    /// <summary>Show the picked grid with the overlays the picker and Map Visibility imply: recompute the layers and the overlay.</summary>
    private void ApplyNavGridSelection()
    {
        if (_navCatalog is null) return;
        var overlayIds = EffectiveNavOverlays(_navOverlayChoice, CurrentVisibilitySelections);
        var resolved = ResolveNavGrid(_navGridChoice, overlayIds);
        _navAppliedOverlays = resolved.Overlays.Select(o => o.Overlay.Id).ToList();
        _navRequestedOverlays = overlayIds;

        // remember which flags were switched on, by mask, so changing variant keeps the user's layer choice
        var wasVisible = NavGridLayers.Where(l => l.IsVisible).Select(l => l.Mask).ToHashSet();
        _navGrid = resolved.Grid;
        _navGridEntry = resolved.Entry;
        FillNavGridLayers(resolved.Grid, wasVisible);

        NavGridVariantSummary = resolved.Grid is null
            ? (resolved.Error ?? "This map ships no navgrid.")
            : $"{resolved.Base!.Id.Replace("grid:", "")}" + (resolved.Overlays.Count == 0 ? "" : " + " + string.Join(" + ", resolved.Overlays.Select(o => $"{o.Overlay.Label} ({o.Changed:n0} cells)")));
        if (resolved.Grid is { } g)
            _log.Info("NavGrid", $"Showing {resolved.Id}: v{g.VersionMajor}.{g.VersionMinor}, {g.CountX}x{g.CountZ} cells, {g.PresentFlags().Count} distinct flag(s)"
                + string.Concat(resolved.Overlays.Select(o => $"; overlay {o.Overlay.Id} changed {o.Changed:n0} cell(s)" + (o.Outside > 0 ? $" ({o.Outside} outside the lattice)" : ""))));
        else _log.Info("NavGrid", NavGridVariantSummary);
        if (!_navUiLoading) RefreshNavGridLabels();
    }

    private void RefreshNavGridLabels()
    {
        if (_navCatalog is null) return;
        bool was = _navUiLoading;
        _navUiLoading = true;
        try { for (int i = 0; i < _navCatalog.Grids.Count && i < NavGridGridLabels.Count; i++) NavGridGridLabels[i] = NavGridLabel(_navCatalog.Grids[i]); }
        finally { _navUiLoading = was; }
    }

    private bool _navLayersLoading;

    private void FillNavGridLayers(NavGrid? grid, ISet<ushort> wasVisible)
    {
        _navLayersLoading = true;
        try
        {
            NavGridLayers.Clear();
            if (grid is not null)
            {
                var present = grid.PresentFlags();
                for (int i = 0; i < present.Count; i++)
                {
                    var (mask, cells) = present[i];
                    NavGridLayers.Add(new NavGridLayerViewModel
                    {
                        Mask = mask,
                        Cells = cells,
                        Share = grid.CellCount > 0 ? cells / (double)grid.CellCount : 0,
                        Color = LayerColor(i),
                        Changed = () => { if (!_navLayersLoading) RebuildBushCellLines(); },
                        IsVisible = wasVisible.Contains(mask),
                    });
                }
            }
        }
        finally { _navLayersLoading = false; }
        OnPropertyChanged(nameof(HasNavGrid));
        if (ShowBushAreas) RebuildBushCellLines();
    }

    /// <summary>Called whenever the map's visibility is recomputed: with the overlay picker on Auto, a new dragon or baron
    /// selection brings its overlay along.</summary>
    private void RefreshNavGridForVisibility()
    {
        if (_navCatalog is null || _navOverlayChoice != "auto") return;
        var wanted = EffectiveNavOverlays("auto", CurrentVisibilitySelections);
        if (!wanted.SequenceEqual(_navRequestedOverlays, StringComparer.OrdinalIgnoreCase)) ApplyNavGridSelection();
    }

    // ---- the Blender link ------------------------------------------------------------------------

    /// <summary>The header version of a grid file, "7.1", or why it cannot be shown.</summary>
    private (string Version, bool Supported, string? Reason) NavGridVersionOf(NavGridVariant g)
    {
        var (grid, error) = LoadNavGridFile(g.Path);
        if (grid is not null) return ($"{grid.VersionMajor}.{grid.VersionMinor}", true, null);
        return ("", false, error);
    }

    /// <summary>The <c>navgrid_variants</c> reply: every grid and overlay of the open map, and what the editor has chosen.</summary>
    private Dictionary<string, object?> NavGridVariantsForBlender()
    {
        if (_currentMap is null || _navCatalog is null)
            return new Dictionary<string, object?> { ["found"] = false, ["variants"] = new List<object>(), ["message"] = "no map is open" };
        EnsureNavGridSkins();
        var catalog = _navCatalog;
        var variants = catalog.All.Select(v =>
        {
            var d = new Dictionary<string, object?>
            {
                ["id"] = v.Id, ["label"] = v.Label, ["path"] = v.Path, ["kind"] = v.Kind, ["group"] = v.Group,
                ["detail"] = v.Detail, ["in_game"] = v.InGame,
            };
            if (v.Kind == NavGridVariant.KindGrid)
            {
                var (version, supported, reason) = NavGridVersionOf(v);
                d["version"] = version;
                d["supported"] = supported;
                if (!supported) { d["reason"] = reason; d["label"] = v.Label + "  [" + reason + "]"; }
                d["default"] = string.Equals(v.Id, catalog.DefaultGridId, StringComparison.OrdinalIgnoreCase);
            }
            else if (v.AxisBit != 0)
            {
                d["axis"] = BlenderLinkVisibility.AxisId(v.AxisHash);
                d["bit"] = v.AxisBit;
            }
            return d;
        }).ToList();
        return new Dictionary<string, object?>
        {
            ["found"] = true,
            ["map"] = _navMapFolder,
            ["default_grid"] = catalog.DefaultGridId,
            ["default_reason"] = catalog.DefaultReason,
            ["current_grid"] = _navGridChoice.Length > 0 ? _navGridChoice : catalog.DefaultGridId,
            ["current_overlay"] = _navOverlayChoice,
            ["current_overlays"] = _navAppliedOverlays,
            ["variants"] = variants,
        };
    }

    /// <summary>
    /// The <c>navgrid</c> reply for a request that may name a variant. <c>variant</c> is <c>grid:x</c> and/or overlay tokens joined
    /// with <c>+</c> (<c>overlay:y</c>, <c>auto</c> = the overlays the Map Visibility selection implies, <c>none</c>); a missing grid
    /// token means the grid the editor shows, a missing overlay token the editor's own overlay choice. The optional
    /// <c>selection</c> (the one <c>visibility_eval</c> takes) is what <c>auto</c> is evaluated against; without it, the editor's.
    /// </summary>
    private BridgeNavGrid NavGridForBlender(System.Text.Json.JsonElement request)
    {
        var emptyLayers = Array.Empty<BridgeNavLayer>();
        if (_currentMap is null) return new(false, "", "", null, emptyLayers, "no map is open");
        if (_navCatalog is null) return new(false, "", "", null, emptyLayers, "this map ships no navgrid");

        string gridId = _navGridChoice;
        string? overlayToken = null;
        var overlayIds = new List<string>();
        IReadOnlyDictionary<uint, int>? axes = null;
        if (request.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (request.TryGetProperty("selection", out var selJson) && selJson.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (!BlenderLinkVisibility.TryParseSelection(selJson, _mapVisibility, _mapControllers?.EventNames ?? Array.Empty<string>(),
                        BlenderStageOptions(), out var sel, out _, out string? why))
                    return new(false, "", "", null, emptyLayers, "selection: " + why);
                axes = sel.Axes;
            }
            if (request.TryGetProperty("variant", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String && v.GetString() is { Length: > 0 } spec)
            {
                foreach (string raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string token = raw.ToLowerInvariant();
                    if (token == "auto") { overlayToken = "auto"; }
                    else if (token == "none") { overlayToken ??= "none"; }
                    else if (_navCatalog.Find(token) is { } found)
                    {
                        if (found.Kind == NavGridVariant.KindGrid) gridId = found.Id;
                        else { overlayToken = "list"; overlayIds.Add(found.Id); }
                    }
                    else return new(false, "", "", null, emptyLayers, $"no navgrid variant '{raw}' in this map (ask navgrid_variants)");
                }
            }
        }
        List<string> overlays = overlayToken switch
        {
            "list" => overlayIds,
            "auto" => EffectiveNavOverlays("auto", axes ?? CurrentVisibilitySelections),
            "none" => new List<string>(),
            _ => EffectiveNavOverlays(_navOverlayChoice, axes ?? CurrentVisibilitySelections),
        };

        var resolved = ResolveNavGrid(gridId, overlays);
        if (resolved.Grid is not { } grid || resolved.Entry is not { } entry)
            return new(false, resolved.Base?.Path ?? "", "", null, emptyLayers, resolved.Error ?? "this map ships no navgrid (or it is not a version 7 grid)")
            { Variant = resolved.Id };
        string source = entry.SourceKind switch
        {
            AssetSourceKind.RiotReference => "game",
            AssetSourceKind.LtkGameData => "gamedata",
            _ => "project",
        };
        // the layers of THIS grid, with the editor's switches where the editor has the same flag on
        var present = grid.PresentFlags();
        var layers = present.Select((p, i) =>
        {
            var editor = NavGridLayers.FirstOrDefault(l => l.Mask == p.Mask);
            return new BridgeNavLayer(p.Mask, NavGridLayerViewModel.LabelOf(p.Mask), LayerColor(i), p.Cells,
                ShowBushAreas && editor is { IsVisible: true }, i == 0);
        }).ToList();
        return new(true, resolved.Base!.Path, source, grid, layers, null)
        {
            Variant = resolved.Id,
            BaseId = resolved.Base.Id,
            Overlays = resolved.Overlays.Select(o => new BridgeNavOverlay(o.Overlay.Id, o.Overlay.Label, o.Overlay.Path, o.Changed, o.Outside)).ToList(),
        };
    }
}
