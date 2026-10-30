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

    public bool IsVisible(int flags, uint controllerHash, IReadOnlyDictionary<uint, int>? selections, int? stageMask = null,
        IReadOnlySet<string>? enabledEvents = null)
        => Resolve(flags, controllerHash, selections, stageMask, enabledEvents).Visible;

    /// <summary>
    /// M802: does the EVENT part of a controller let its content show? True for everything no event gates (no
    /// controller, a layer controller, <paramref name="enabledEvents"/> null). For content the editor has no other
    /// visibility rule for - a particle's sound follows its particle - where the whole of <see cref="Resolve"/> would
    /// also start applying the dragon and baron layers to it.
    /// </summary>
    public bool EventsAllow(uint controllerHash, IReadOnlySet<string>? enabledEvents)
    {
        if (controllerHash == 0 || _controllers is null) return true;
        return EventGate(_controllers.Resolve(controllerHash), enabledEvents) is not { Blocks: true };
    }

    /// <summary>What the events in a controller graph say about it, or null when it reaches none (or events are not
    /// being evaluated). A mutator is ON when its name is in the set. The graph's leaves join as a union, so ANY event
    /// on satisfies it, and ParentMode 3 - the existing "none of the parents" inversion - flips it.
    /// <para><b>Blocks</b>: the event alone hides the content. <b>Satisfies</b>: the graph also reaches layer bits, and
    /// the event being on is enough for the union, so the layer rules are not consulted. With the event off a mixed
    /// graph is left to its layer rules. No shipped graph is mixed - every mutator is a direct controller, and no Child
    /// in Map11's 26 bins has one among its parents (M802 census) - so this is the existing union rule carried over, not
    /// a measured one.</para></summary>
    private static (bool Blocks, bool Satisfies, bool On)? EventGate(VisibilityControllerResolution controller, IReadOnlySet<string>? enabledEvents)
    {
        if (enabledEvents is null || controller.Mutators.Count == 0) return null;
        bool on = controller.Mutators.Any(enabledEvents.Contains);
        bool mixed = controller.HasAxisBits;
        bool blocks = on ? controller.NotVisible : !mixed && !controller.NotVisible;
        return (blocks, on && !controller.NotVisible && mixed, on);
    }

    /// <param name="stageMask">M797: a TFT board stage's exact visibility mask. When set it IS the primary axis's
    /// state - <see cref="MapVisibility.VisibleForStage"/> for content, the mask itself (no initial mask added)
    /// for a controller - and the primary axis's own selection is not consulted. Null leaves every rule as it was.</param>
    /// <param name="enabledEvents">M802: the events switched on (a normal game has none). Content whose controller is an
    /// event shows only while its event is in the set. Null means events are not evaluated and event content shows -
    /// the behaviour before M802, for a caller that has no event state.</param>
    public VisibilityDiagnostic Resolve(int flags, uint controllerHash, IReadOnlyDictionary<uint, int>? selections, int? stageMask = null,
        IReadOnlySet<string>? enabledEvents = null)
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

        // M849: a controller graph is a boolean tree, evaluated the way the client does (the old per-axis union stays behind
        // MapVisibilityControllers.LenientEvaluation). A hash this map has no controller object for is still unconstrained.
        if (_controllers is { LenientEvaluation: false } tree && tree.Contains(controllerHash))
            return ResolveTree(result, tree, controller, flags, controllerHash, selections, stageMask, enabledEvents);

        var filterParts = new List<string>();
        var controllerParts = new List<string>();
        var reasons = new List<string>();
        bool visible = true;

        // M802: event-only content (a MutatorMapVisibilityController) is decided by which events are on, not by a layer.
        // It is checked before the layer axes so it also works on a map that declares none.
        var gate = EventGate(controller, enabledEvents);
        string eventNames = gate is null ? "" : string.Join(", ", controller.Mutators);
        if (gate is { } eventGate)
        {
            controllerParts.Add($"Event: {eventNames} ({(eventGate.On ? "on" : "off")})");
            if (eventGate.Blocks)
            {
                visible = false;
                reasons.Add(eventGate.On
                    ? $"event '{eventNames}' is on, and this content shows only while it is off"
                    : $"event '{eventNames}' is off (tick it under Visibility Layers > Events)");
            }
        }
        bool eventSatisfies = gate is { Satisfies: true };

        foreach (var axis in _definition.Axes)
        {
            // M797: a board stage stands in for the primary axis's selection (see the parameter's remark).
            bool staged = axis.IsPrimary && stageMask is not null;
            int selected = staged ? stageMask!.Value : selections?.GetValueOrDefault(axis.DefinitionFieldHash) ?? 0;
            string selectedName = SelectionName(axis, staged, selected);
            if (selected < 0) selected = MapVisibility.MaskSelection(axis, selected);   // M849: "no terraform yet" is the initial state to a mesh mask
            filterParts.Add($"{axis.Name}: {selectedName}");

            int controllerBits = controller.BitsFor(axis.DefinitionFieldHash);
            if (controllerBits != 0)
            {
                var names = axis.Layers.Where(l => (controllerBits & l.Bit) != 0).Select(l => l.Name).ToList();
                controllerParts.Add($"{axis.Name}: {(names.Count > 0 ? string.Join(", ", names) : controllerBits.ToString())}");
            }
            if (selected == 0 && !staged) continue;
            if (eventSatisfies) continue;   // M802: a union the event being on already satisfies (see EventGate)

            bool axisVisible;
            if (controllerBits != 0)
            {
                // InitialVisibilityMask is the permanent foundation for the PRIMARY mapgeo mask. Secondary
                // controller axes are exclusive states: selecting Cup/Tunnel/Upgraded must not also match
                // their initial Base controller. This is the same split used by Riot's MapgeoAddon.
                // M797: a board stage's mask is the whole state, so no initial mask is folded in.
                int activeMask = staged ? selected : selected | (axis.IsPrimary ? axis.InitialMask : 0);
                bool inSet = (controllerBits & activeMask) != 0;
                axisVisible = controller.NotVisible ? !inSet : inSet;
                if (!axisVisible) reasons.Add($"controller hides {axis.Name} '{selectedName}'");
            }
            else if (axis.IsPrimary)
            {
                axisVisible = staged ? MapVisibility.VisibleForStage(flags, selected) : MapVisibility.VisibleForMask(flags, axis, selected);
                if (!axisVisible)
                    reasons.Add(staged ? $"mesh mask {flags} shares no bit with the board stage's mask {selected}"
                        : $"mesh mask {flags} does not include {axis.Name} '{selectedName}' or initial mask {axis.InitialMask}");
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
              + (gate is { On: true } ? $"; event '{eventNames}' is on" : "")
            : "hidden: " + string.Join("; ", reasons);
        return result;
    }

    private static string SelectionName(MapVisibilityAxis axis, bool staged, int selected) =>
        staged ? $"board stage, mask {selected}"
        : selected < 0 ? MapVisibility.NoTerraformLabel
        : selected == 0 ? "All"
        : axis.Layers.FirstOrDefault(l => l.Bit == selected).Name ?? $"bit {selected}";

    /// <summary>
    /// M849: a controller graph evaluated as the client does (<see cref="MapVisibilityControllers.Evaluate"/>). Each axis the graph
    /// reaches is at the state picked for it: a terrain leaf holds only while its mask contains the rift in play (the base state
    /// and the axis's initial mask do NOT count for a controller), a baron-stage leaf while the stage is in its mask. An axis picked
    /// as All stands for every state it can take, so the content shows if SOME state shows it; with every reached axis on All the
    /// picker means "show everything", so a graph that no state of the axes could ever show still shows (the diagnostics name it),
    /// while an event still gates it. A graph that does not reach the primary axis leaves that axis to the mesh's own mask (M771).
    /// </summary>
    private VisibilityDiagnostic ResolveTree(VisibilityDiagnostic result, MapVisibilityControllers tree, VisibilityControllerResolution flat,
        int flags, uint controllerHash, IReadOnlyDictionary<uint, int>? selections, int? stageMask, IReadOnlySet<string>? enabledEvents)
    {
        var info = tree.Analyze(controllerHash);
        var filterParts = new List<string>();
        var controllerParts = new List<string>();
        var reasons = new List<string>();
        var fixedMasks = new Dictionary<uint, int>();
        var pickedNames = new List<string>();
        bool maskVisible = true;
        bool primaryReached = info.Axes.ContainsKey(MapVisibility.PrimaryAxisHash);

        foreach (var axis in _definition.Axes)
        {
            bool staged = axis.IsPrimary && stageMask is not null;
            int selected = staged ? stageMask!.Value : selections?.GetValueOrDefault(axis.DefinitionFieldHash) ?? 0;
            string selectedName = SelectionName(axis, staged, selected);
            filterParts.Add($"{axis.Name}: {selectedName}");

            int controllerBits = flat.BitsFor(axis.DefinitionFieldHash);
            if (controllerBits != 0)
            {
                var names = axis.Layers.Where(l => (controllerBits & l.Bit) != 0).Select(l => l.Name).ToList();
                controllerParts.Add($"{axis.Name}: {(names.Count > 0 ? string.Join(", ", names) : controllerBits.ToString())}");
            }
            if (selected == 0 && !staged) continue;

            if (info.Axes.ContainsKey(axis.DefinitionFieldHash))
            {
                // a board stage's mask is the whole state (M797), whatever the axis's initial mask is
                fixedMasks[axis.DefinitionFieldHash] = staged ? selected : MapVisibility.ControllerStateMask(axis, selected);
                pickedNames.Add($"{axis.Name} '{selectedName}'");
            }
            else if (axis.IsPrimary)
            {
                // the graph says nothing about the primary axis: the mesh's own mask does (M771 / M797)
                int mask = MapVisibility.MaskSelection(axis, selected);
                bool v = staged ? MapVisibility.VisibleForStage(flags, selected) : MapVisibility.VisibleForMask(flags, axis, mask);
                if (!v)
                    reasons.Add(staged ? $"mesh mask {flags} shares no bit with the board stage's mask {selected}"
                        : $"mesh mask {flags} does not include {axis.Name} '{selectedName}' or initial mask {axis.InitialMask}");
                maskVisible &= v;
            }
        }

        bool allFree = fixedMasks.Count == 0;
        bool treeVisible = tree.CanShow(controllerHash, fixedMasks, enabledEvents);
        string graph = $"controller 0x{controllerHash:x8}";
        if (!treeVisible && allFree && info.Axes.Count > 0)
        {
            // everything on All shows everything - unless an event, not an axis, is what hides it
            treeVisible = !tree.CanShow(controllerHash, fixedMasks, enabledEvents, freeEvents: true);
            if (treeVisible) reasons.Clear();
        }
        string eventNames = string.Join(", ", info.Events);
        bool eventsOn = enabledEvents is not null && info.Events.Any(enabledEvents.Contains);
        if (info.Events.Count > 0 && enabledEvents is not null)
            controllerParts.Add($"Event: {eventNames} ({(eventsOn ? "on" : "off")})");
        if (!treeVisible)
        {
            if (info.Events.Count > 0 && enabledEvents is not null && tree.CanShow(controllerHash, fixedMasks, enabledEvents, freeEvents: true))
                reasons.Add(eventsOn
                    ? $"event '{eventNames}' is on, and this content shows only while it is off"
                    : $"event '{eventNames}' is off (tick it under Visibility Layers > Events)");
            else
                reasons.Add(fixedMasks.Count > 0
                    ? $"{graph} does not show with {string.Join(", ", pickedNames)}"
                    : $"{graph} shows in no state or event");
        }

        bool visible = treeVisible && maskVisible;
        result.Visible = visible;
        result.FilterSummary = filterParts.Count == 0 ? "No map visibility layers" : string.Join(" / ", filterParts);
        result.ControllerSummary = controllerParts.Count == 0 ? "none" : string.Join(" / ", controllerParts)
            + (flat.NotVisible ? " / inverted (ParentMode 3)" : "");
        result.Reason = visible
            ? $"visible: {(filterParts.Count == 0 ? "map declares no visibility filter" : string.Join("; ", filterParts))}"
              + (eventsOn ? $"; event '{eventNames}' is on" : "")
            : "hidden: " + string.Join("; ", reasons);
        return result;
    }
}
