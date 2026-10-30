using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.MapGeo;

/// <summary>M849: what one node of a visibility controller graph is.</summary>
public enum ControllerKind
{
    /// <summary><c>c406a533</c>: visible while a layer of its mask is the active terraform (the rift).</summary>
    Terrain,
    /// <summary><c>ec733fe2</c>: visible while a stage of its mask is the active baron-pit stage.</summary>
    BaronStage,
    /// <summary><c>MutatorMapVisibilityController</c>: visible while the game event is on.</summary>
    Event,
    /// <summary><c>e07edfa4</c>: a switch the server sets by name; before any server call it shows its DefaultVisible (class default true).</summary>
    Named,
    /// <summary><c>ChildMapVisibilityController</c>: a boolean combination of its parents, by ParentMode.</summary>
    Child,
    /// <summary>A parent link to a class this reader does not index (or to nothing at all): visible, as before M849, but named in the diagnostics.</summary>
    Unknown,
}

/// <summary>M849: one controller object as a node of the boolean graph.</summary>
public sealed class ControllerNode
{
    public uint Hash { get; init; }
    public ControllerKind Kind { get; init; }
    public uint ClassHash { get; init; }
    /// <summary>Terrain / baron-stage leaves: the mask field.</summary>
    public int Bits { get; init; }
    public string? EventName { get; init; }
    /// <summary>Named leaves: the DefaultVisible field; absent = the class default, true.</summary>
    public bool DefaultVisible { get; init; } = true;
    public IReadOnlyList<uint> Parents { get; init; } = Array.Empty<uint>();
    /// <summary>Child: the ParentMode value (absent = 0, ALL).</summary>
    public uint ParentMode { get; init; }
    /// <summary>Unknown: no loaded bin holds the object at all (otherwise <see cref="ClassHash"/> names its class).</summary>
    public bool Dangling { get; init; }

    public bool ModeIsKnown => ParentMode <= 3;
    public bool IsAll => Kind == ControllerKind.Child && ParentMode == 0;
}

/// <summary>M849: the static facts of the graph under one controller.</summary>
public sealed class ControllerGraphInfo
{
    /// <summary>Axis field hash -> the union of the mask bits of every leaf of that axis.</summary>
    public Dictionary<uint, int> Axes { get; } = new();
    public HashSet<string> Events { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<uint> Nodes { get; } = new();
    public List<ControllerNode> Unknowns { get; } = new();
    public List<ControllerNode> ManyParentAlls { get; } = new();
    public List<ControllerNode> InvalidModes { get; } = new();
    public bool HasCycle { get; set; }
}

public sealed partial class MapVisibilityControllers
{
    private const uint NamedController = 0xe07edfa4;
    private static readonly uint FieldDefaultVisible = HashAlgorithms.Fnv1a("DefaultVisible");

    /// <summary>M849: evaluate controller graphs as the old, lenient per-axis union (the pre-M849 reading: only ParentMode 3,
    /// as a global inversion; the primary axis's InitialMask always counts) instead of as the game's boolean tree. Off by
    /// default: the editor must not be kinder than the game.</summary>
    public bool LenientEvaluation { get; set; }

    private readonly Dictionary<uint, ControllerNode> _nodes = new();
    private readonly Dictionary<uint, ControllerGraphInfo> _infos = new();

    /// <summary>Is this hash a controller object the reader indexes (a graph it can evaluate)?</summary>
    public bool Contains(uint hash) => hash != 0 && _controllers.ContainsKey(hash);

    public IReadOnlyCollection<uint> ControllerHashes => _controllers.Keys;

    /// <summary>The primary (terraform) axis of the definition these controllers were built with, if any.</summary>
    public MapVisibilityAxis? PrimaryAxis => _definition.Primary;

    public ControllerNode Node(uint hash)
    {
        if (_nodes.TryGetValue(hash, out var hit)) return hit;
        ControllerNode node;
        if (_controllers.TryGetValue(hash, out var o)) node = MakeNode(o);
        else if (_otherClasses.TryGetValue(hash, out uint cls))
            node = new ControllerNode { Hash = hash, Kind = ControllerKind.Unknown, ClassHash = cls };
        else node = new ControllerNode { Hash = hash, Kind = ControllerKind.Unknown, Dangling = true };
        _nodes[hash] = node;
        return node;
    }

    private static ControllerNode MakeNode(BinTreeObject o)
    {
        switch (o.ClassHash)
        {
            case PrimaryController:
                return new ControllerNode { Hash = o.PathHash, Kind = ControllerKind.Terrain, ClassHash = o.ClassHash, Bits = TryU8(o, FieldPrimaryBits, out int pb) ? pb : 0 };
            case BaronPitController:
                return new ControllerNode { Hash = o.PathHash, Kind = ControllerKind.BaronStage, ClassHash = o.ClassHash, Bits = TryU8(o, FieldBaronPitBits, out int bb) ? bb : 0 };
            case MutatorController:
                return new ControllerNode { Hash = o.PathHash, Kind = ControllerKind.Event, ClassHash = o.ClassHash, EventName = MutatorNameOf(o) };
            case NamedController:
            {
                bool visible = true;
                if (o.Properties.TryGetValue(FieldDefaultVisible, out var p))
                    visible = p switch { BinTreeBool b => b.Value, BinTreeBitBool bit => bit.Value, _ => true };
                return new ControllerNode { Hash = o.PathHash, Kind = ControllerKind.Named, ClassHash = o.ClassHash, DefaultVisible = visible };
            }
            default:
            {
                var parents = new List<uint>();
                if (o.Properties.TryGetValue(FieldParents, out var pp) && pp is BinTreeContainer c)
                    foreach (var link in c.Elements.OfType<BinTreeObjectLink>()) parents.Add(link.Value);
                o.Properties.TryGetValue(FieldParentMode, out var mp);
                uint mode = mp switch { BinTreeU32 u => u.Value, BinTreeU8 u8 => u8.Value, BinTreeU16 u16 => u16.Value, _ => 0u };
                return new ControllerNode { Hash = o.PathHash, Kind = ControllerKind.Child, ClassHash = o.ClassHash, Parents = parents, ParentMode = mode };
            }
        }
    }

    /// <summary>The static facts of the graph under <paramref name="hash"/>: the axes and events it reaches, and what in it is unusual. Cached.</summary>
    public ControllerGraphInfo Analyze(uint hash)
    {
        if (_infos.TryGetValue(hash, out var hit)) return hit;
        var info = new ControllerGraphInfo();
        Walk(hash, info, new HashSet<uint>());
        _infos[hash] = info;
        return info;
    }

    private void Walk(uint hash, ControllerGraphInfo info, HashSet<uint> path)
    {
        var node = Node(hash);
        if (!path.Add(hash)) { info.HasCycle = true; return; }
        info.Nodes.Add(hash);
        switch (node.Kind)
        {
            case ControllerKind.Terrain:
                info.Axes[MapVisibility.PrimaryAxisHash] = info.Axes.GetValueOrDefault(MapVisibility.PrimaryAxisHash) | node.Bits;
                break;
            case ControllerKind.BaronStage:
                info.Axes[MapVisibility.BaronPitAxisHash] = info.Axes.GetValueOrDefault(MapVisibility.BaronPitAxisHash) | node.Bits;
                break;
            case ControllerKind.Event:
                if (node.EventName is { Length: > 0 } name) info.Events.Add(name);
                break;
            case ControllerKind.Unknown: info.Unknowns.Add(node); break;
            case ControllerKind.Child:
                if (!node.ModeIsKnown) info.InvalidModes.Add(node);
                if (node.IsAll && node.Parents.Count > 2) info.ManyParentAlls.Add(node);
                foreach (uint parent in node.Parents) Walk(parent, info, path);
                break;
        }
        path.Remove(hash);
    }

    /// <summary>
    /// M849: does the graph under <paramref name="hash"/> show, with each axis at <paramref name="axisMask"/> (the mask of the
    /// active state: the terraform for the primary axis, the pit stage for the baron axis; 0 = none) and the events in
    /// <paramref name="events"/> on (null: events are not evaluated, an event shows)? The client's rules (LeagueToolkit
    /// ADR-0064): ParentMode 0 = all parents, 1 = any, 2 = exactly one, 3 = none; a child with no parents shows only in
    /// 0 and 3; a terrain or stage leaf shows while its mask holds the active state; a named controller shows its
    /// DefaultVisible; an event shows while it is on. A parent this reader does not know is visible (as before M849).
    /// A cycle is cut where it closes, the repeated node counting as visible.
    /// </summary>
    public bool Evaluate(uint hash, Func<uint, int> axisMask, IReadOnlySet<string>? events)
        => EvaluateNode(hash, axisMask, events, new HashSet<uint>());

    private bool EvaluateNode(uint hash, Func<uint, int> axisMask, IReadOnlySet<string>? events, HashSet<uint> path)
    {
        var node = Node(hash);
        switch (node.Kind)
        {
            case ControllerKind.Terrain: return (node.Bits & axisMask(MapVisibility.PrimaryAxisHash)) != 0;
            case ControllerKind.BaronStage: return (node.Bits & axisMask(MapVisibility.BaronPitAxisHash)) != 0;
            case ControllerKind.Event:
                return events is null || node.EventName is not { Length: > 0 } || events.Contains(node.EventName);
            case ControllerKind.Named: return node.DefaultVisible;
            case ControllerKind.Unknown: return true;
        }
        if (!path.Add(hash)) return true;
        int total = node.Parents.Count, shown = 0;
        foreach (uint parent in node.Parents)
            if (EvaluateNode(parent, axisMask, events, path)) shown++;
        path.Remove(hash);
        return node.ParentMode switch
        {
            1 => shown > 0,
            2 => shown == 1,
            3 => shown == 0,
            _ => shown == total,   // 0 (and a mode the client does not define: reported by the diagnostics, evaluated as ALL)
        };
    }

    /// <summary>The state masks one axis can take for a graph: each single layer bit, plus (primary axis only) 0 = no terraform yet.</summary>
    internal IReadOnlyList<int> StateMasks(uint axisHash, int leafBits)
    {
        var axis = _definition.Axes.FirstOrDefault(a => a.DefinitionFieldHash == axisHash);
        int bits = leafBits;
        if (axis is not null) foreach (var l in axis.Layers) bits |= l.Bit;
        bool primary = axisHash == MapVisibility.PrimaryAxisHash;
        // the base state IS "no terraform yet": its bit is the mask 0, not a rift
        if (primary && MapVisibility.IsSingleStateInitial(axis)) bits &= ~axis!.InitialMask;
        var list = new List<int>();
        if (primary) list.Add(0);
        for (int i = 0; i < 31; i++) if ((bits & (1 << i)) != 0) list.Add(1 << i);
        if (list.Count == 0) list.Add(0);
        return list;
    }

    /// <summary>
    /// M849: can the graph show in SOME state? <paramref name="fixedMasks"/> pins axes; every other axis the graph reaches is tried
    /// over all its states (<see cref="StateMasks"/>). <paramref name="events"/> are the events that are on (null: events are not
    /// evaluated, an event shows); with <paramref name="freeEvents"/> every event of the graph is also tried both ways.
    /// </summary>
    public bool CanShow(uint hash, IReadOnlyDictionary<uint, int> fixedMasks, IReadOnlySet<string>? events, bool freeEvents = false)
    {
        var info = Analyze(hash);
        var free = info.Axes.Where(a => !fixedMasks.ContainsKey(a.Key))
            .Select(a => (Axis: a.Key, States: StateMasks(a.Key, a.Value))).ToList();
        var eventNames = freeEvents ? info.Events.ToList() : new List<string>();
        var masks = new Dictionary<uint, int>(fixedMasks);
        var on = new HashSet<string>(events ?? (IEnumerable<string>)Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return Search(hash, free, 0, eventNames, 0, masks, on, events is null && !freeEvents);
    }

    private bool Search(uint hash, List<(uint Axis, IReadOnlyList<int> States)> free, int axisIndex, List<string> eventNames, int eventIndex,
        Dictionary<uint, int> masks, HashSet<string> on, bool eventsNotEvaluated)
    {
        if (axisIndex < free.Count)
        {
            foreach (int state in free[axisIndex].States)
            {
                masks[free[axisIndex].Axis] = state;
                if (Search(hash, free, axisIndex + 1, eventNames, eventIndex, masks, on, eventsNotEvaluated)) return true;
            }
            return false;
        }
        if (eventIndex < eventNames.Count)
        {
            foreach (bool value in new[] { false, true })
            {
                if (value) on.Add(eventNames[eventIndex]); else on.Remove(eventNames[eventIndex]);
                if (Search(hash, free, axisIndex, eventNames, eventIndex + 1, masks, on, eventsNotEvaluated)) return true;
            }
            on.Remove(eventNames[eventIndex]);
            return false;
        }
        return Evaluate(hash, a => masks.GetValueOrDefault(a), eventsNotEvaluated ? null : on);
    }
}
