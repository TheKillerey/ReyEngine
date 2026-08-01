using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>A named state declared by a map visibility definition.</summary>
public readonly record struct VisibilityLayer(string Name, int Bit, uint NameHash = 0);

/// <summary>One independent visibility axis from the shipping map bin.</summary>
public sealed record MapVisibilityAxis(
    uint DefinitionFieldHash,
    string Name,
    int InitialMask,
    bool IsPrimary,
    IReadOnlyList<VisibilityLayer> Layers);

/// <summary>The visibility axes declared by a map's <c>Map</c> object.</summary>
public sealed class MapVisibilityDefinition
{
    public static readonly MapVisibilityDefinition Empty = new(Array.Empty<MapVisibilityAxis>());

    public MapVisibilityDefinition(IReadOnlyList<MapVisibilityAxis> axes) => Axes = axes;
    public IReadOnlyList<MapVisibilityAxis> Axes { get; }
    public MapVisibilityAxis? Primary => Axes.FirstOrDefault(a => a.IsPrimary);
    public bool HasAxes => Axes.Count > 0;
}

/// <summary>Reads and evaluates League's data-driven map visibility layer declarations.</summary>
public static class MapVisibility
{
    public const uint PrimaryAxisHash = 0x9e019715;       // VisibilityFlagDefines
    public const uint BaronPitAxisHash = 0xd31ac6ce;      // recovered: baronpitflagdefines

    private const uint MapClass = 0xdfa2efb1;
    private const uint DefinitionClass = 0x587ed3b7;      // MapVisibilityFlagDefinitions
    private const uint InitialMaskField = 0x6744e6e3;     // InitialVisibilityMask
    private const uint InitialBaronPitMaskField = 0x30eafcaa; // recovered: initialbaronpitmask
    private const uint FlagDefinitionsField = 0x4e08731b;
    private const uint NameField = 0x8d39bde6;
    private const uint PublicNameField = 0x12afd903;
    private const uint BitIndexField = 0x091fa8ec;

    private static readonly IReadOnlyDictionary<uint, string> RecoveredNames = new Dictionary<uint, string>
    {
        [0x33dbd490] = "Base",
        [0x55f641e9] = "Upgraded",
    };

    /// <summary>Parse every visibility definition embedded in the map object. Unknown axes and states remain usable.</summary>
    public static MapVisibilityDefinition Parse(byte[]? mapBin, Func<uint, string?>? resolveName = null)
    {
        if (mapBin is not { Length: > 0 }) return MapVisibilityDefinition.Empty;
        BinTree tree;
        try { tree = SafeBinTree.Parse(mapBin); }
        catch { return MapVisibilityDefinition.Empty; }

        var map = tree.Objects.Values.FirstOrDefault(o => o.ClassHash == MapClass);
        if (map is null) return MapVisibilityDefinition.Empty;

        int primaryInitial = ReadInteger(map.Properties.GetValueOrDefault(InitialMaskField), 0);
        int secondaryInitial = ReadInteger(map.Properties.GetValueOrDefault(InitialBaronPitMaskField), 0);
        var axes = new List<MapVisibilityAxis>();
        foreach (var (fieldHash, property) in map.Properties)
        {
            if (property is not BinTreeStruct definition || definition.ClassHash != DefinitionClass) continue;
            var layers = ReadLayers(definition, resolveName);
            if (layers.Count == 0) continue;
            bool primary = fieldHash == PrimaryAxisHash;
            string name = AxisName(fieldHash, primary, resolveName);
            int initial = primary ? primaryInitial : fieldHash == BaronPitAxisHash ? secondaryInitial : 0;
            axes.Add(new MapVisibilityAxis(fieldHash, name, initial, primary, layers));
        }

        return new MapVisibilityDefinition(axes
            .OrderByDescending(a => a.IsPrimary)
            .ThenBy(a => a.DefinitionFieldHash)
            .ToList());
    }

    /// <summary>Fallback for custom maps that use mapgeo flags but omit the shipping map definition.</summary>
    public static MapVisibilityDefinition Infer(IEnumerable<int> meshFlags)
    {
        int used = meshFlags.Where(f => f is not 0 and not 255).Aggregate(0, (mask, f) => mask | f);
        if (used == 0) return MapVisibilityDefinition.Empty;
        var layers = Enumerable.Range(0, 8)
            .Where(i => (used & (1 << i)) != 0)
            .Select(i => new VisibilityLayer($"Layer {i + 1}", 1 << i))
            .ToList();
        return new MapVisibilityDefinition(new[]
        {
            new MapVisibilityAxis(PrimaryAxisHash, "Visibility", (used & 1) != 0 ? 1 : 0, true, layers),
        });
    }

    /// <summary>Evaluate a mapgeo mask using the map's permanent initial mask plus the selected state.</summary>
    public static bool VisibleForMask(int flags, MapVisibilityAxis? axis, int selectedBit)
    {
        if (selectedBit == 0 || flags is 0 or 255) return true;
        int activeMask = (axis?.InitialMask ?? 0) | selectedBit;
        return (flags & activeMask) != 0;
    }

    public static string Label(int flags, MapVisibilityAxis? axis)
    {
        if (flags is 0 or 255) return "All Layers";
        var names = axis?.Layers.Where(l => (flags & l.Bit) != 0).Select(l => l.Name).ToList() ?? new();
        return names.Count == 0 ? $"Mask {flags}" : string.Join(" / ", names);
    }

    private static List<VisibilityLayer> ReadLayers(BinTreeStruct definition, Func<uint, string?>? resolveName)
    {
        var result = new List<VisibilityLayer>();
        if (definition.Properties.GetValueOrDefault(FlagDefinitionsField) is not BinTreeContainer entries) return result;
        foreach (var entry in entries.Elements.OfType<BinTreeStruct>())
        {
            int index = ReadInteger(entry.Properties.GetValueOrDefault(BitIndexField), 0);
            if (index is < 0 or > 30) continue;
            uint hash = 0;
            string? name = entry.Properties.GetValueOrDefault(PublicNameField) is BinTreeString publicName
                && !string.IsNullOrWhiteSpace(publicName.Value) ? publicName.Value : null;
            if (entry.Properties.GetValueOrDefault(NameField) is BinTreeString rawString && !string.IsNullOrWhiteSpace(rawString.Value))
                name ??= rawString.Value;
            else if (entry.Properties.GetValueOrDefault(NameField) is BinTreeHash rawHash)
            {
                hash = rawHash.Value;
                name ??= resolveName?.Invoke(hash) ?? RecoveredNames.GetValueOrDefault(hash);
            }
            name ??= hash != 0 ? $"Layer {index + 1} [0x{hash:x8}]" : $"Layer {index + 1}";
            result.Add(new VisibilityLayer(name, 1 << index, hash));
        }
        return result.OrderBy(l => l.Bit).ToList();
    }

    private static string AxisName(uint hash, bool primary, Func<uint, string?>? resolveName)
    {
        if (primary) return "Map Visibility";
        if (hash == BaronPitAxisHash) return "Baron Pit";
        string? resolved = resolveName?.Invoke(hash);
        return string.IsNullOrWhiteSpace(resolved) ? $"Visibility 0x{hash:x8}" : SplitName(resolved);
    }

    private static string SplitName(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ");

    private static int ReadInteger(BinTreeProperty? p, int fallback) => p switch
    {
        BinTreeU8 v => v.Value,
        BinTreeU16 v => v.Value,
        BinTreeU32 v when v.Value <= int.MaxValue => (int)v.Value,
        BinTreeI8 v => v.Value,
        BinTreeI16 v => v.Value,
        BinTreeI32 v => v.Value,
        _ => fallback,
    };
}
