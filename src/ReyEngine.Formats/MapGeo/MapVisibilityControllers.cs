using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>The per-axis masks produced by one map visibility controller graph.</summary>
public sealed class VisibilityControllerResolution
{
    public Dictionary<uint, int> AxisBits { get; } = new();
    public bool NotVisible;

    /// <summary>M802: the events (<c>MutatorMapVisibilityController.MutatorName</c>) the graph reaches. A mutator is
    /// not a layer bit - it is a yes/no the game sets for the whole match - so it is kept apart from
    /// <see cref="AxisBits"/>. Shared and cached: read it, never change it.</summary>
    public HashSet<string> Mutators { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>M802: does the graph reach a real layer bit (a dragon or baron-pit state) as well?</summary>
    public bool HasAxisBits => AxisBits.Values.Any(bits => bits != 0);

    public int BitsFor(uint axisHash) => AxisBits.GetValueOrDefault(axisHash);
    public void Add(uint axisHash, int bits) => AxisBits[axisHash] = BitsFor(axisHash) | bits;
    public static readonly VisibilityControllerResolution Unconstrained = new();
}

/// <summary>
/// Decodes visibility-controller graphs from a map's sibling bins. The state names and available bits come
/// from the shipping map bin; these hidden controller classes only connect a graph leaf to one of those axes.
/// <para>M802: a <c>MutatorMapVisibilityController</c> is the third kind of leaf. It is not a layer bit but a named
/// event the game switches on for a whole match (Summoner's Rift has three), so it resolves into
/// <see cref="VisibilityControllerResolution.Mutators"/> and <see cref="MapVisibilityResolver"/> decides it from the set
/// of events that are on. Any other controller class (a named script switch, the legacy flag controller, the Map22
/// carousel's provider controller) is still not indexed, and content that names one resolves as always visible.</para>
/// </summary>
public sealed class MapVisibilityControllers
{
    private const uint PrimaryController = 0xc406a533;
    private const uint BaronPitController = 0xec733fe2;
    private const uint ChildController = 0xe21083b5;       // ChildMapVisibilityController
    private const uint MutatorController = 0x4275b121;     // M802: FNV-1a("MutatorMapVisibilityController")
    private const uint FieldMutatorName = 0xcb0a7522;      // M802: FNV-1a("MutatorName")
    private const uint FieldParents = 0x3044938a;
    private const uint FieldParentMode = 0xc9d3f06a;
    private const uint FieldPrimaryBits = 0x27639032;
    private const uint FieldBaronPitBits = 0x8bff8cdf;     // recovered: baronpitflags

    private readonly Dictionary<uint, BinTreeObject> _controllers = new();
    private readonly Dictionary<uint, VisibilityControllerResolution> _cache = new();
    private readonly MapVisibilityDefinition _definition;

    private MapVisibilityControllers(MapVisibilityDefinition definition) => _definition = definition;

    public int Count => _controllers.Count;
    public int LeafControllerCount => _controllers.Values.Count(o => o.ClassHash is PrimaryController or BaronPitController);

    private IReadOnlyList<string>? _eventNames;

    /// <summary>
    /// M802: the distinct events this map's <c>MutatorMapVisibilityController</c>s name, in name order. A mutator is
    /// ON only when the game match enables its name, so event-only content (Summoner's Rift's Hall of Legends, MSI
    /// trophies and esports banners) is hidden in an ordinary game - and here until the user switches the event on.
    /// </summary>
    public IReadOnlyList<string> EventNames => _eventNames ??= _controllers.Values
        .Where(o => o.ClassHash == MutatorController)
        .Select(MutatorNameOf)
        .Where(name => name is { Length: > 0 })
        .Select(name => name!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>M802: the name of the event a <c>MutatorMapVisibilityController</c> stands for; null for any other
    /// controller, or a mutator that names nothing (which stays unhandled, i.e. visible, like any unknown class).</summary>
    private static string? MutatorNameOf(BinTreeObject o) =>
        o.ClassHash == MutatorController && o.Properties.TryGetValue(FieldMutatorName, out var p) && p is BinTreeString s
            ? s.Value : null;

    public readonly record struct ControllerInfo(uint Hash, string Kind, IReadOnlyList<string> States, bool NotVisible)
    {
        public string Label => string.Join(" / ", new[] { $"{Kind} 0x{Hash:x8}" }
            .Concat(States)
            .Concat(NotVisible ? new[] { "inverted" } : Array.Empty<string>()));
    }

    public IReadOnlyList<ControllerInfo> List() => _controllers.Values
        .Select(o =>
        {
            var resolution = Resolve(o.PathHash);
            var states = new List<string>();
            foreach (var axis in _definition.Axes)
            {
                int bits = resolution.BitsFor(axis.DefinitionFieldHash);
                var names = axis.Layers.Where(l => (bits & l.Bit) != 0).Select(l => l.Name).ToList();
                if (names.Count > 0) states.Add($"{axis.Name}: {string.Join(", ", names)}");
            }
            if (resolution.Mutators.Count > 0) states.Add($"event {string.Join(", ", resolution.Mutators)}");   // M802
            string kind = o.ClassHash == ChildController ? "Combined" : o.ClassHash == MutatorController ? "Event" : "Layer";
            return new ControllerInfo(o.PathHash, kind, states, resolution.NotVisible);
        })
        .OrderBy(ci => ci.Kind)
        .ThenBy(ci => ci.Hash)
        .ToList();

    public static MapVisibilityControllers Build(IEnumerable<byte[]> bins, MapVisibilityDefinition? definition = null)
    {
        var result = new MapVisibilityControllers(definition ?? MapVisibilityDefinition.Empty);
        foreach (var data in bins)
        {
            if (data is not { Length: > 0 }) continue;
            BinTree bin;
            try { bin = SafeBinTree.Parse(data); }
            catch { continue; }
            foreach (var o in bin.Objects.Values)
                if (o.ClassHash is PrimaryController or BaronPitController or ChildController or MutatorController)
                    result._controllers[o.PathHash] = o;
        }
        return result;
    }

    public VisibilityControllerResolution Resolve(uint controllerHash)
    {
        if (controllerHash == 0) return VisibilityControllerResolution.Unconstrained;
        if (_cache.TryGetValue(controllerHash, out var hit)) return hit;
        var result = new VisibilityControllerResolution();
        Resolve(controllerHash, result, new HashSet<uint>());
        _cache[controllerHash] = result;
        return result;
    }

    private void Resolve(uint hash, VisibilityControllerResolution result, HashSet<uint> visited)
    {
        if (hash == 0 || !visited.Add(hash) || !_controllers.TryGetValue(hash, out var o)) return;
        switch (o.ClassHash)
        {
            case PrimaryController:
                if (TryU8(o, FieldPrimaryBits, out var primary)) result.Add(MapVisibility.PrimaryAxisHash, primary);
                break;
            case BaronPitController:
                if (TryU8(o, FieldBaronPitBits, out var secondary)) result.Add(MapVisibility.BaronPitAxisHash, secondary);
                break;
            case MutatorController:
                // M802: a leaf like the two above - inside a Child graph it joins the union (ParentMode 3 still inverts).
                if (MutatorNameOf(o) is { Length: > 0 } eventName) result.Mutators.Add(eventName);
                break;
            case ChildController:
                if (TryU32(o, FieldParentMode, out var mode) && mode == 3) result.NotVisible = true;
                if (o.Properties.TryGetValue(FieldParents, out var p) && p is BinTreeContainer parents)
                    foreach (var link in parents.Elements.OfType<BinTreeObjectLink>())
                        Resolve(link.Value, result, visited);
                break;
        }
    }

    private static bool TryU8(BinTreeObject o, uint field, out int value)
    {
        if (o.Properties.TryGetValue(field, out var p) && p is BinTreeU8 u) { value = u.Value; return true; }
        value = 0;
        return false;
    }

    private static bool TryU32(BinTreeObject o, uint field, out uint value)
    {
        if (o.Properties.TryGetValue(field, out var p) && p is BinTreeU32 u) { value = u.Value; return true; }
        value = 0;
        return false;
    }
}
