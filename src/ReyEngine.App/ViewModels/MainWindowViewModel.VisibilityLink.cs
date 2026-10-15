using System.Text.Json;
using ReyEngine.Formats.Interop;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M843 (protocol 4): the editor's map visibility for the Blender link. <c>visibility</c> serves the model the Visibility
/// Layers panel shows - the same axes, options, labels, events and defaults - with the current selection;
/// <c>visibility_eval</c> answers which meshes are drawn under a selection by <see cref="MapVisibilityEvaluator"/>, the function
/// <c>ApplyMapVisibility</c> draws with, so Blender can never show a mesh the editor hides.
///
/// <para>Not modelled: the editor's per-mesh eye / disable / delete state (editing state, not map visibility).</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    private IReadOnlyList<BridgeStageOption>? BlenderStageOptions() =>
        BoardStageSelectors.FirstOrDefault() is { } picker
            ? new[] { new BridgeStageOption("Start", null) }.Concat(picker.Set.Stages.Select(s => new BridgeStageOption(s.Label, s.Mask))).ToList()
            : null;

    private int CurrentStageIndex => BoardStageSelectors.FirstOrDefault()?.SelectedIndex ?? 0;

    /// <summary>Everything the editor's map-visibility decisions are asked with, right now.</summary>
    private VisibilitySelection CurrentVisibilitySelection() =>
        new(CurrentVisibilitySelections, CurrentEnabledEvents, CurrentStageMask, RenderRegionsEnabled);

    private IReadOnlyList<string> BlenderEventNames() => _mapControllers?.EventNames ?? Array.Empty<string>();

    private Dictionary<string, object?> VisibilityForBlender()
    {
        if (_currentMap is not { } map)
            return new Dictionary<string, object?> { ["found"] = false, ["message"] = "no map is open" };
        var model = BlenderLinkVisibility.Describe(_mapVisibility, BlenderEventNames(), map.Meshes.Any(m => m.RegionHash != 0),
            BlenderStageOptions(), CurrentStageIndex, CurrentVisibilitySelection());
        model["found"] = true;
        model["visible"] = VisibleMeshesNow(map);
        model["navgrid_overlays"] = NavOverlaysFor(CurrentVisibilitySelections);
        return model;
    }

    private IReadOnlyList<int> VisibleMeshesNow(MapGeoAsset map) =>
        MapVisibilityEvaluator.VisibleMeshIndices(map, _visibilityResolver ??= new MapVisibilityResolver(_mapControllers, _mapVisibility),
            CurrentVisibilitySelection());

    private List<string> NavOverlaysFor(IReadOnlyDictionary<uint, int>? axes) =>
        _navCatalog?.OverlaysFor(axes).Select(o => o.Id).ToList() ?? new List<string>();

    private Dictionary<string, object?> VisibilityEvalForBlender(JsonElement request)
    {
        if (_currentMap is not { } map)
            return new Dictionary<string, object?> { ["found"] = false, ["message"] = "no map is open" };
        if (!BlenderLinkVisibility.TryParseSelection(request, _mapVisibility, BlenderEventNames(), BlenderStageOptions(),
                out var selection, out int stageIndex, out string? error))
            return new Dictionary<string, object?> { ["found"] = false, ["message"] = error };

        var visible = MapVisibilityEvaluator.VisibleMeshIndices(
            map, _visibilityResolver ??= new MapVisibilityResolver(_mapControllers, _mapVisibility), selection);
        return new Dictionary<string, object?>
        {
            ["found"] = true,
            ["visible"] = visible,
            ["total"] = map.Meshes.Count,
            ["selection"] = BlenderLinkVisibility.SelectionToJson(selection, stageIndex),
            ["navgrid_overlays"] = NavOverlaysFor(selection.Axes),
        };
    }
}
