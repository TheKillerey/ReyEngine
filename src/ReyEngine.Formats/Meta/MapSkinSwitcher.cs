using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Meta;

/// <summary>One registered skin slot from a shipping map bin.</summary>
public sealed record MapSkinInfo(
    int Index,
    uint PathHash,
    string ObjectPath,
    string Name,
    string? MapContainerLink,
    int PropertyCount)
{
    public string DisplayName => MapContainerLink is { Length: > 0 }
        ? $"{Name}  -  {MapContainerLink.Split('/').Last()}"
        : $"{Name}  -  legacy/default geometry";
}

/// <summary>The Map object and only the MapSkin objects it actually registers.</summary>
public sealed record MapSkinCatalog(
    string MapStringId,
    uint MapObjectHash,
    IReadOnlyList<MapSkinInfo> Skins);

public sealed record MapSkinSwapResult(
    byte[] Bytes,
    MapSkinInfo Target,
    MapSkinInfo Source,
    int CopiedProperties,
    IReadOnlyList<string> ReferencedStrings);

/// <summary>
/// Reads and safely rewires Riot's shipping-map skin slots. A swap replaces the complete target
/// <c>MapSkin</c> definition with a deep clone of the source definition while retaining the target's
/// object hash and <c>name</c>. The server can therefore keep selecting the original slot (normally
/// <c>Default</c>) while the client receives the source skin's map container, minimap, navigation,
/// alternate terrain/fog assets, audio banks, particles, gamma, grass tint and resource resolvers.
/// </summary>
public static class MapSkinSwitcher
{
    public const int TftMapId = 22;

    private static readonly uint MapClass = H("Map");
    private static readonly uint MapSkinClass = H("MapSkin");
    private static readonly uint MapStringIdField = H("mapStringId");
    private static readonly uint MapSkinsField = H("mapSkins");
    private static readonly uint NameField = H("name");
    private static readonly uint MapContainerLinkField = H("mMapContainerLink");

    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    /// <summary>Map22 and any bin that identifies its mode as TFT are deliberately unavailable.</summary>
    public static bool IsTftMap(int mapId, string? mapStringId) =>
        mapId == TftMapId || string.Equals(mapStringId, "TFT", StringComparison.OrdinalIgnoreCase);

    public static string? BlockReason(int mapId, string? mapStringId) => IsTftMap(mapId, mapStringId)
        ? "TFT arenas (Map22) are paid cosmetics and are intentionally unavailable in this tool."
        : null;

    public static MapSkinCatalog ReadCatalog(byte[] shippingBin, Func<uint, string?>? resolve = null)
    {
        var tree = SafeBinTree.Parse(shippingBin);
        var maps = tree.Objects.Where(pair => pair.Value.ClassHash == MapClass).ToList();
        if (maps.Count != 1)
            throw new InvalidDataException($"Expected one Map object, found {maps.Count:n0}.");

        var (mapHash, map) = maps[0];
        if (!map.Properties.TryGetValue(MapSkinsField, out var listProperty) || listProperty is not BinTreeContainer list)
            throw new InvalidDataException("The Map object has no mapSkins container.");

        string mapStringId = map.Properties.TryGetValue(MapStringIdField, out var idProperty)
            && idProperty is BinTreeString id ? id.Value : "";
        var skins = new List<MapSkinInfo>(list.Elements.Count);
        var seen = new HashSet<uint>();
        for (int i = 0; i < list.Elements.Count; i++)
        {
            if (list.Elements[i] is not BinTreeObjectLink link || link.Value == 0)
                throw new InvalidDataException($"mapSkins[{i}] is not a valid object link.");
            if (!seen.Add(link.Value))
                throw new InvalidDataException($"mapSkins contains duplicate link 0x{link.Value:x8}.");
            if (!tree.Objects.TryGetValue(link.Value, out var skin) || skin.ClassHash != MapSkinClass)
                throw new InvalidDataException($"mapSkins[{i}] points to a missing or non-MapSkin object 0x{link.Value:x8}.");
            if (!skin.Properties.TryGetValue(NameField, out var nameProperty) || nameProperty is not BinTreeString name
                || string.IsNullOrWhiteSpace(name.Value))
                throw new InvalidDataException($"MapSkin 0x{link.Value:x8} has no name.");

            string? container = skin.Properties.TryGetValue(MapContainerLinkField, out var containerProperty)
                && containerProperty is BinTreeString containerString && containerString.Value.Length > 0
                ? containerString.Value : null;
            skins.Add(new MapSkinInfo(i, link.Value, resolve?.Invoke(link.Value) ?? $"0x{link.Value:x8}",
                name.Value, container, skin.Properties.Count));
        }
        return new MapSkinCatalog(mapStringId, mapHash, skins);
    }

    public static MapSkinSwapResult Switch(
        byte[] shippingBin,
        int mapId,
        uint targetSkinHash,
        uint sourceSkinHash,
        Func<uint, string?>? resolve = null)
    {
        var catalog = ReadCatalog(shippingBin, resolve);
        if (BlockReason(mapId, catalog.MapStringId) is { } blocked) throw new InvalidOperationException(blocked);
        if (targetSkinHash == sourceSkinHash) throw new InvalidOperationException("Choose two different map skins.");

        var targetInfo = catalog.Skins.FirstOrDefault(s => s.PathHash == targetSkinHash)
            ?? throw new InvalidOperationException("The target is not a registered MapSkin slot.");
        var sourceInfo = catalog.Skins.FirstOrDefault(s => s.PathHash == sourceSkinHash)
            ?? throw new InvalidOperationException("The source is not a registered MapSkin slot.");

        var tree = SafeBinTree.Parse(shippingBin);
        var target = tree.Objects[targetSkinHash];
        var source = tree.Objects[sourceSkinHash];
        var cloned = source.Properties.Select(pair => BinTreeCloner.Clone(pair.Value, pair.Key)).ToList();

        // The path hash is the slot identity and the name is its human-readable counterpart. Everything
        // else is behavior owned by the source skin and must travel together to avoid half-swapped maps.
        int nameIndex = cloned.FindIndex(p => p.NameHash == NameField);
        if (nameIndex < 0 || target.Properties[NameField] is not BinTreeString targetName)
            throw new InvalidDataException("The source or target MapSkin has no identity name.");
        cloned[nameIndex] = new BinTreeString(NameField, targetName.Value);
        tree.Objects[targetSkinHash] = new BinTreeObject(targetSkinHash, MapSkinClass, cloned);

        using var output = new MemoryStream(shippingBin.Length);
        tree.Write(output);
        byte[] bytes = output.ToArray();

        // Strict reparse plus semantic invariants. The mapSkins selection table and source definition
        // must remain untouched, and the target must be the complete source clone except for its name.
        var verified = new BinTree(new MemoryStream(bytes, writable: false));
        if (verified.Objects.Count != tree.Objects.Count)
            throw new InvalidDataException("The rewritten shipping bin changed its object count.");
        var original = SafeBinTree.Parse(shippingBin);
        if (!BinPropEquality.PropsEqual(original.Objects[catalog.MapObjectHash].Properties[MapSkinsField],
                verified.Objects[catalog.MapObjectHash].Properties[MapSkinsField]))
            throw new InvalidDataException("The rewrite changed the mapSkins selection table.");
        if (!BinPropEquality.ObjectsEqual(original.Objects[sourceSkinHash], verified.Objects[sourceSkinHash]))
            throw new InvalidDataException("The rewrite changed the source MapSkin.");
        if (!BinPropEquality.DictsEqual(tree.Objects[targetSkinHash].Properties,
                verified.Objects[targetSkinHash].Properties))
            throw new InvalidDataException("The target MapSkin did not survive strict round-trip serialization.");

        var strings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in source.Properties.Values) CollectStrings(property, strings);
        return new MapSkinSwapResult(bytes, targetInfo, sourceInfo, cloned.Count, strings.Order().ToList());
    }

    /// <summary>Convert a map-container object path to its companion materials bin.</summary>
    public static string? ContainerBinPath(string? mapContainerLink)
    {
        if (string.IsNullOrWhiteSpace(mapContainerLink)) return null;
        string clean = mapContainerLink.Replace('\\', '/').Trim('/');
        if (clean.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) clean = clean[5..];
        return $"data/{clean}.materials.bin";
    }

    /// <summary>Strings with a path separator and a filename suffix are files the selected skin expects.</summary>
    public static IReadOnlyList<string> AssetPaths(IEnumerable<string> strings) => strings
        .Where(value =>
        {
            int slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
            return slash >= 0 && value.LastIndexOf('.') > slash + 1;
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static void CollectStrings(BinTreeProperty property, ISet<string> strings)
    {
        switch (property)
        {
            case BinTreeString value when value.Value.Length > 0:
                strings.Add(value.Value);
                break;
            case BinTreeContainer container:
                foreach (var element in container.Elements) CollectStrings(element, strings);
                break;
            case BinTreeStruct structure:
                foreach (var child in structure.Properties.Values) CollectStrings(child, strings);
                break;
            case BinTreeOptional { Value: { } inner }:
                CollectStrings(inner, strings);
                break;
            case BinTreeMap map:
                foreach (var (key, value) in map)
                {
                    CollectStrings(key, strings);
                    CollectStrings(value, strings);
                }
                break;
        }
    }
}
