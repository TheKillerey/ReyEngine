using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>The per-axis masks produced by one map visibility controller graph.</summary>
public sealed class VisibilityControllerResolution
{
    public Dictionary<uint, int> AxisBits { get; } = new();
    public bool NotVisible;

    public int BitsFor(uint axisHash) => AxisBits.GetValueOrDefault(axisHash);
    public void Add(uint axisHash, int bits) => AxisBits[axisHash] = BitsFor(axisHash) | bits;
    public static readonly VisibilityControllerResolution Unconstrained = new();
}

/// <summary>
/// Decodes visibility-controller graphs from a map's sibling bins. The state names and available bits come
/// from the shipping map bin; these hidden controller classes only connect a graph leaf to one of those axes.
/// </summary>
public sealed class MapVisibilityControllers
{
    private const uint PrimaryController = 0xc406a533;
    private const uint BaronPitController = 0xec733fe2;
    private const uint ChildController = 0xe21083b5;       // ChildMapVisibilityController
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
            string kind = o.ClassHash == ChildController ? "Combined" : "Layer";
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
                if (o.ClassHash is PrimaryController or BaronPitController or ChildController)
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
