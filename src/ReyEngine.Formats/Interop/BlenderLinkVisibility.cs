using System.Globalization;
using System.Text.Json;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Interop;

/// <summary>M843: one TFT board stage choice (index 0 is "Start": the map as it loads, no stage mask).</summary>
public sealed record BridgeStageOption(string Label, int? Mask);

/// <summary>
/// M843 (protocol 4): ReyEngine's map-visibility model, as the Blender link serves it - the same option lists with the same
/// labels as the editor's Visibility Layers panel (Map Visibility, Baron Pit, Events, Render regions, Board stage) and the
/// editor's CURRENT selection - and the reader of a selection sent back for <c>visibility_eval</c>. The evaluation itself is
/// <see cref="MapVisibilityEvaluator"/>, the function the editor draws with.
/// </summary>
public static class BlenderLinkVisibility
{
    public static string AxisId(uint fieldHash) => "0x" + fieldHash.ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>The model, for the <c>visibility</c> reply (every field but <c>op</c>).</summary>
    public static Dictionary<string, object?> Describe(MapVisibilityDefinition definition, IReadOnlyList<string> eventNames,
        bool hasRegions, IReadOnlyList<BridgeStageOption>? stages, int stageIndex, VisibilitySelection current)
    {
        var axes = definition.Axes.Select(a => new Dictionary<string, object?>
        {
            ["id"] = AxisId(a.DefinitionFieldHash),
            ["name"] = a.Name,
            ["primary"] = a.IsPrimary,
            ["initial_mask"] = a.InitialMask,
            // "All" first, exactly as the editor's combo lists it
            ["options"] = new[] { new Dictionary<string, object?> { ["label"] = "All", ["bit"] = 0 } }
                .Concat(a.Layers.Select(l => new Dictionary<string, object?> { ["label"] = l.Name, ["bit"] = l.Bit })).ToList(),
            ["selected"] = current.Axes.GetValueOrDefault(a.DefinitionFieldHash),
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["axes"] = axes,
            ["events"] = eventNames.Select(n => new Dictionary<string, object?>
            {
                ["name"] = n, ["label"] = MapEventNames.Label(n), ["on"] = current.Events.Contains(n),
            }).ToList(),
            ["render_regions"] = new Dictionary<string, object?> { ["available"] = hasRegions, ["enabled"] = current.RenderRegions },
            ["stages"] = stages is { Count: > 1 }
                ? new Dictionary<string, object?>
                {
                    ["options"] = stages.Select((s, i) => new Dictionary<string, object?> { ["index"] = i, ["label"] = s.Label, ["mask"] = s.Mask }).ToList(),
                    ["selected"] = stageIndex,
                }
                : null,
            ["selection"] = SelectionToJson(current, stageIndex),
        };
    }

    public static Dictionary<string, object?> SelectionToJson(VisibilitySelection s, int stageIndex)
    {
        var axes = new Dictionary<string, object?>();
        foreach (var (hash, bit) in s.Axes.OrderBy(k => k.Key)) axes[AxisId(hash)] = bit;
        return new Dictionary<string, object?>
        {
            ["axes"] = axes,
            ["events"] = s.Events.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            ["render_regions"] = s.RenderRegions,
            ["stage"] = stageIndex,
        };
    }

    /// <summary>
    /// Read a selection. Everything omitted keeps the editor's default on a freshly opened map: every axis on All, no event
    /// on, render regions off, no board stage. A value the map cannot take (an axis it does not have, a bit that is not one of
    /// the axis's layers, an unknown event or stage) is refused with the reason - never guessed.
    /// </summary>
    public static bool TryParseSelection(JsonElement request, MapVisibilityDefinition definition, IReadOnlyList<string> eventNames,
        IReadOnlyList<BridgeStageOption>? stages, out VisibilitySelection selection, out int stageIndex, out string? error)
    {
        error = null;
        stageIndex = 0;
        var axes = new Dictionary<uint, int>();
        foreach (var a in definition.Axes) axes[a.DefinitionFieldHash] = 0;
        var events = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool regions = false;
        selection = new VisibilitySelection(axes, events, null, regions);

        JsonElement sel = request;
        if (request.ValueKind == JsonValueKind.Object && request.TryGetProperty("selection", out var inner)) sel = inner;
        if (sel.ValueKind == JsonValueKind.Undefined || sel.ValueKind == JsonValueKind.Null) return true;
        if (sel.ValueKind != JsonValueKind.Object) { error = "selection must be an object"; return false; }

        if (sel.TryGetProperty("axes", out var axesJson) && axesJson.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in axesJson.EnumerateObject())
            {
                var axis = definition.Axes.FirstOrDefault(a => string.Equals(AxisId(a.DefinitionFieldHash), p.Name, StringComparison.OrdinalIgnoreCase)
                                                               || string.Equals(a.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (axis is null) { error = $"this map has no visibility axis '{p.Name}'"; return false; }
                if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out int bit))
                { error = $"axis '{axis.Name}': the selected layer must be a bit number (0 = All)"; return false; }
                if (bit != 0 && !axis.Layers.Any(l => l.Bit == bit))
                { error = $"axis '{axis.Name}' has no layer with bit {bit}"; return false; }
                axes[axis.DefinitionFieldHash] = bit;
            }
        }
        if (sel.TryGetProperty("events", out var eventsJson) && eventsJson.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in eventsJson.EnumerateArray())
            {
                string? name = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                var known = name is null ? null : eventNames.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                if (known is null) { error = $"this map has no event '{name}'"; return false; }
                events.Add(known);
            }
        }
        if (sel.TryGetProperty("render_regions", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False) regions = r.GetBoolean();

        int? stageMask = null;
        if (sel.TryGetProperty("stage", out var st) && st.ValueKind == JsonValueKind.Number)
        {
            if (!st.TryGetInt32(out int idx) || idx < 0 || (idx > 0 && (stages is null || idx >= stages.Count)))
            { error = $"this map has no board stage {st}"; return false; }
            stageIndex = idx;
            if (idx > 0) stageMask = stages![idx].Mask;
        }
        selection = new VisibilitySelection(axes, events, stageMask, regions);
        return true;
    }
}
