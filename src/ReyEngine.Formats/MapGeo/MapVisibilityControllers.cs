using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>The dragon + baron layer bits a mesh's visibility controller resolves to.</summary>
public sealed class BaronResolution
{
    public int DragonBits;
    public int BaronBits;
    public bool NotVisible;   // ParentMode 3: visible OUTSIDE the resolved bits rather than within them

    public static readonly BaronResolution Unconstrained = new();

    /// <summary>True if this mesh should show for the chosen baron state (bit 0 = "All").</summary>
    public bool VisibleForBaron(int baronBit)
    {
        if (baronBit == 0 || BaronBits == 0) return true; // no selection, or not baron-constrained
        bool inSet = (BaronBits & baronBit) != 0;
        return NotVisible ? !inSet : inSet;
    }
}

/// <summary>
/// Decodes League map visibility controllers (the baron-pit override system) out of a map's
/// <c>.materials.bin</c> objects, then resolves a mesh's <c>VisibilityControllerPathHash</c> into its
/// dragon + baron layer bits. Mirrors the MapgeoAddon baron-hash parser:
/// <list type="bullet">
/// <item>Dragon Layer Controller (0xc406a533): field 0x27639032 = dragon bit.</item>
/// <item>Baron Layer Controller (0xec733fe2): field 0x8bff8cdf = baron bit.</item>
/// <item>ChildMapVisibilityController (0xe21083b5): Parents (0x3044938a) links + ParentMode (0xc9d3f06a) — recurse.</item>
/// </list>
/// </summary>
public sealed class MapVisibilityControllers
{
    private const uint DragonController = 0xc406a533;
    private const uint BaronController = 0xec733fe2;
    private const uint ChildController = 0xe21083b5;
    private const uint FieldParents = 0x3044938a;
    private const uint FieldParentMode = 0xc9d3f06a;
    private const uint FieldDragonBit = 0x27639032;
    private const uint FieldBaronBit = 0x8bff8cdf;

    private readonly Dictionary<uint, BinTreeObject> _controllers = new();
    private readonly Dictionary<uint, BaronResolution> _cache = new();

    public int Count => _controllers.Count;
    public int BaronControllerCount => _controllers.Values.Count(o => o.ClassHash == BaronController);

    /// <summary>Index every controller object found across the given bin blobs (tolerant parse).</summary>
    public static MapVisibilityControllers Build(IEnumerable<byte[]> bins)
    {
        var m = new MapVisibilityControllers();
        foreach (var data in bins)
        {
            if (data is not { Length: > 0 }) continue;
            BinTree bin;
            try { bin = SafeBinTree.Parse(data); }
            catch { continue; }
            foreach (var o in bin.Objects.Values)
                if (o.ClassHash is DragonController or BaronController or ChildController)
                    m._controllers[o.PathHash] = o; // later bins win on duplicate hash
        }
        return m;
    }

    public BaronResolution Resolve(uint controllerHash)
    {
        if (controllerHash == 0) return BaronResolution.Unconstrained;
        if (_cache.TryGetValue(controllerHash, out var hit)) return hit;
        var r = new BaronResolution();
        Resolve(controllerHash, r, new HashSet<uint>());
        _cache[controllerHash] = r;
        return r;
    }

    private void Resolve(uint hash, BaronResolution r, HashSet<uint> visited)
    {
        if (hash == 0 || !visited.Add(hash) || !_controllers.TryGetValue(hash, out var o)) return;
        switch (o.ClassHash)
        {
            case DragonController:
                if (TryU8(o, FieldDragonBit, out var d)) r.DragonBits |= d;
                break;
            case BaronController:
                if (TryU8(o, FieldBaronBit, out var b)) r.BaronBits |= b;
                break;
            case ChildController:
                if (TryU32(o, FieldParentMode, out var pm) && pm == 3) r.NotVisible = true;
                if (o.Properties.TryGetValue(FieldParents, out var p) && p is BinTreeContainer c)
                    foreach (var el in c.Elements.OfType<BinTreeObjectLink>())
                        Resolve(el.Value, r, visited);
                break;
        }
    }

    private static bool TryU8(BinTreeObject o, uint field, out int value)
    {
        if (o.Properties.TryGetValue(field, out var p) && p is BinTreeU8 u) { value = u.Value; return true; }
        value = 0; return false;
    }

    private static bool TryU32(BinTreeObject o, uint field, out uint value)
    {
        if (o.Properties.TryGetValue(field, out var p) && p is BinTreeU32 u) { value = u.Value; return true; }
        value = 0; return false;
    }
}
