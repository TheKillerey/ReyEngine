using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M807: a map as it stands when a game on it starts - the state the Content Browser's map thumbnails draw.</para>
///
/// <para><b>Why not the viewport's "Start".</b> The editor opens a map on <i>All</i> layers: every dragon pit, every terraform
/// and, on a TFT board, every stage at once. That is the right default for editing and the wrong picture for a tile. The game
/// starts the map on the shipping map bin's <c>InitialVisibilityMask</c> (and <c>initialbaronpitmask</c>), so that is the state
/// drawn here:</para>
/// <list type="bullet">
/// <item><b>An axis that starts on exactly one of its own states</b> (Summoner's Rift's Base, Map12's Default) is selected at
/// that state - the same selection a user makes in the Map Visibility combo, through the same
/// <see cref="MapVisibilityResolver"/>.</item>
/// <item><b>A TFT board</b> (a primary axis that starts on several bits at once - Map22's 67 - and board stages of its own,
/// M797) is drawn as the stage it is played in: <b>Board ready (level 1)</b>, the first stage, by the exact rule a board
/// stage uses (<see cref="MapVisibility.VisibleForStage"/>), with the placed props and level props following it as they follow
/// a stage (M798). Not the bare initial mask: that is the board BEFORE it is ready, and measured on Map22's 159 boards it
/// leaves Lux with 2 of 38 materials, Lunar Revel 2025 with 1 of 23 and Soul Fighter 2026 with 8 of 21 - the board's own
/// content is flagged for the stage, not for the start.</item>
/// <item><b>A multi-bit start with no stage behaviour</b> (Map22's neondj): the initial mask itself, by the same exact rule.</item>
/// <item><b>No axis, or an inferred one</b> (a map without a shipping bin): nothing is selected, as in the editor.</item>
/// <item><b>Every event is off</b> (M802), as in an ordinary match, so event-only content - Summoner's Rift's Hall of Legends,
/// MSI trophies and esports banners - is not drawn.</item>
/// </list>
/// </summary>
public sealed class MapStartState
{
    private static readonly IReadOnlySet<string> NoEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public MapVisibilityDefinition Definition { get; }
    public MapVisibilityControllers? Controllers { get; }
    public MapVisibilityResolver Resolver { get; }

    /// <summary>The axes selected at their single initial state, by axis field hash. Empty when none.</summary>
    public IReadOnlyDictionary<uint, int> Selections { get; }

    /// <summary>The staged start's exact mask - a TFT board's first stage (lux: 20), or a multi-bit initial mask with no stage to
    /// name (Map22's 67) - or null for every map that starts on a single state.</summary>
    public int? StageMask { get; }

    /// <summary>Per mapgeo group, whether the start state draws it.</summary>
    public IReadOnlyList<bool> GroupVisible { get; }

    private MapStartState(MapVisibilityDefinition definition, MapVisibilityControllers? controllers,
        MapVisibilityResolver resolver, IReadOnlyDictionary<uint, int> selections, int? stageMask, IReadOnlyList<bool> groupVisible)
    {
        Definition = definition;
        Controllers = controllers;
        Resolver = resolver;
        Selections = selections;
        StageMask = stageMask;
        GroupVisible = groupVisible;
    }

    /// <param name="shippingBin">The map's shipping <c>mapNN.bin</c> (null when it has none).</param>
    /// <param name="controllerBins">The bins visibility controllers are read from, the map's own materials bin LAST (later
    /// bins win on a repeated controller hash, as <c>MainWindowViewModel.BuildMapVisibility</c> orders them).</param>
    /// <param name="boardStage">The board's first stage (<see cref="MapBoardStageSet.Stages"/>[0]), or null for a map that has none.</param>
    public static MapStartState Resolve(MapGeoAsset map, byte[]? shippingBin, IEnumerable<byte[]> controllerBins,
        Func<uint, string?> resolveBinName, MapBoardStage? boardStage) =>
        // an inferred axis would invent its initial mask, so none is inferred: a map without a shipping bin has no start state
        Resolve(map, MapVisibility.Parse(shippingBin, resolveBinName), controllerBins, boardStage);

    /// <param name="definition">The map's visibility axes as <c>MapVisibility.Parse</c> reads them from its shipping bin (the
    /// parse of a multi-megabyte bin is what a caller with many skins of one map keeps).</param>
    /// <param name="boardStage">The board's first stage, or null. Honoured only on a map with a primary axis.</param>
    public static MapStartState Resolve(MapGeoAsset map, MapVisibilityDefinition definition, IEnumerable<byte[]> controllerBins,
        MapBoardStage? boardStage)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(definition);
        var controllers = MapVisibilityControllers.Build(controllerBins, definition);
        var resolver = new MapVisibilityResolver(controllers, definition);

        var selections = new Dictionary<uint, int>();
        int? stage = null;
        foreach (var axis in definition.Axes)
        {
            if (axis.InitialMask == 0) continue;
            if (MapVisibility.IsSingleStateInitial(axis)) selections[axis.DefinitionFieldHash] = axis.InitialMask;
            else if (axis.IsPrimary) stage = axis.InitialMask;
        }
        // a board plays in its first stage, whatever bits it happened to start on
        if (boardStage is not null && definition.Primary is not null) stage = boardStage.Mask;

        // the loop of MainWindowViewModel.ApplyMapVisibility, with nothing hidden by the user and render regions on
        var meshByIndex = new Dictionary<int, MapGeoMesh>();
        foreach (var m in map.Meshes) meshByIndex[m.Index] = m;
        var visible = new bool[map.Groups.Count];
        for (int i = 0; i < visible.Length; i++)
        {
            var g = map.Groups[i];
            int flags = g.VisibilityFlags;
            uint ctrl = g.ControllerHash;
            if (g.MeshIndex >= 0 && meshByIndex.TryGetValue(g.MeshIndex, out var src))
            { flags = src.EffectiveVisibility; ctrl = src.EffectiveController; }
            visible[i] = resolver.IsVisible(flags, ctrl, selections, stage, NoEvents);
        }

        return new MapStartState(definition, controllers, resolver, selections, stage, visible);
    }

    /// <summary>A placed prop at the start: not disabled (flags 0), and under a staged start the stage's exact rule - the
    /// M798 gate. The Map Visibility layer never filtered a prop and still does not.</summary>
    public bool PropShown(MapAnimatedProp prop) =>
        prop.VisibilityFlags != 0
        && (StageMask is not { } stage || MapVisibility.VisibleForStage(prop.VisibilityFlags, stage));

    /// <summary>A level prop at the start (M806): the placed prop's rule, plus the event gate on its own or its chunk's
    /// controller - which, with every event off, hides a gated one. Without the map's controllers a gated one is not shown.</summary>
    public bool LevelPropShown(MapLevelProp prop) =>
        prop.VisibilityFlags != 0
        && (StageMask is not { } stage || MapVisibility.VisibleForStage(prop.VisibilityFlags, stage))
        && (prop.VisibilityControllerHash == 0 || Resolver.EventsAllow(prop.VisibilityControllerHash, NoEvents));
}
