namespace ReyEngine.Formats.MapGeo;

/// <summary>Explains final map visibility for a mesh or placement.</summary>
public sealed class VisibilityDiagnostic
{
    public int Flags;
    public string FlagLabel = "";
    public uint ControllerHash;
    public bool HasController;
    public bool ControllerNotVisible;
    public string ControllerSummary = "none";
    public string FilterSummary = "All";
    public bool Visible;
    public string Reason = "";
}

/// <summary>Shared data-driven map visibility evaluation used by both renderers and every placeable type.</summary>
public sealed class MapVisibilityResolver
{
    private readonly MapVisibilityControllers? _controllers;
    private readonly MapVisibilityDefinition _definition;

    public MapVisibilityResolver(MapVisibilityControllers? controllers, MapVisibilityDefinition? definition = null)
    {
        _controllers = controllers;
        _definition = definition ?? MapVisibilityDefinition.Empty;
    }

    public bool IsVisible(int flags, uint controllerHash, IReadOnlyDictionary<uint, int>? selections)
        => Resolve(flags, controllerHash, selections).Visible;

    public VisibilityDiagnostic Resolve(int flags, uint controllerHash, IReadOnlyDictionary<uint, int>? selections)
    {
        var primary = _definition.Primary;
        var result = new VisibilityDiagnostic
        {
            Flags = flags,
            FlagLabel = MapVisibility.Label(flags, primary),
            ControllerHash = controllerHash,
            HasController = controllerHash != 0,
        };
        var controller = controllerHash != 0
            ? _controllers?.Resolve(controllerHash) ?? VisibilityControllerResolution.Unconstrained
            : VisibilityControllerResolution.Unconstrained;
        result.ControllerNotVisible = controller.NotVisible;

        var filterParts = new List<string>();
        var controllerParts = new List<string>();
        var reasons = new List<string>();
        bool visible = true;
        foreach (var axis in _definition.Axes)
        {
            int selected = selections?.GetValueOrDefault(axis.DefinitionFieldHash) ?? 0;
            string selectedName = selected == 0 ? "All" : axis.Layers.FirstOrDefault(l => l.Bit == selected).Name ?? $"bit {selected}";
            filterParts.Add($"{axis.Name}: {selectedName}");

            int controllerBits = controller.BitsFor(axis.DefinitionFieldHash);
            if (controllerBits != 0)
            {
                var names = axis.Layers.Where(l => (controllerBits & l.Bit) != 0).Select(l => l.Name).ToList();
                controllerParts.Add($"{axis.Name}: {(names.Count > 0 ? string.Join(", ", names) : controllerBits.ToString())}");
            }
            if (selected == 0) continue;

            bool axisVisible;
            if (controllerBits != 0)
            {
                int activeMask = selected | axis.InitialMask;
                bool inSet = (controllerBits & activeMask) != 0;
                axisVisible = controller.NotVisible ? !inSet : inSet;
                if (!axisVisible) reasons.Add($"controller hides {axis.Name} '{selectedName}'");
            }
            else if (axis.IsPrimary)
            {
                axisVisible = MapVisibility.VisibleForMask(flags, axis, selected);
                if (!axisVisible) reasons.Add($"mesh mask {flags} does not include {axis.Name} '{selectedName}' or initial mask {axis.InitialMask}");
            }
            else axisVisible = true;
            visible &= axisVisible;
        }

        result.Visible = visible;
        result.FilterSummary = filterParts.Count == 0 ? "No map visibility layers" : string.Join(" / ", filterParts);
        result.ControllerSummary = controllerParts.Count == 0 ? "none" : string.Join(" / ", controllerParts)
            + (controller.NotVisible ? " / inverted (ParentMode 3)" : "");
        result.Reason = visible
            ? $"visible: {(filterParts.Count == 0 ? "map declares no visibility filter" : string.Join("; ", filterParts))}"
            : "hidden: " + string.Join("; ", reasons);
        return result;
    }
}
