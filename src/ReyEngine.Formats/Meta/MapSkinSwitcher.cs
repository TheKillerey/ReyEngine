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
    int ChangedRouteProperties,
    IReadOnlyList<uint> RoutedSkinHashes,
    IReadOnlyList<string> ReferencedStrings);

/// <summary>
/// Reads and safely rewires Riot's shipping-map skin slots. A switch copies only the source skin's
/// environment-loading route to the selected target slot. The target retains its identity and runtime
/// data (especially its character-skin overrides), while loading the chosen map container, object
/// configuration, world particles and grass tint.
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
    private static readonly uint MapObjectsCfgField = H("mMapObjectsCFG");
    private static readonly uint WorldParticlesField = H("mWorldParticlesINI");
    private static readonly uint GrassTintField = H("mGrassTintTexture");

    // These are the four fields changed by the long-standing, in-game-proven Map Forcer. Copying an
    // entire MapSkin is unsafe: seasonal skins carry spawn-time character skin overrides (currently
    // unresolved field 0x2d3285eb on Map11) that are only valid when the server selected that skin.
    private static readonly uint[] EnvironmentRouteFields =
    {
        MapContainerLinkField,
        MapObjectsCfgField,
        WorldParticlesField,
        GrassTintField,
    };

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
        var properties = target.Properties
            .Where(pair => !EnvironmentRouteFields.Contains(pair.Key))
            .Select(pair => BinTreeCloner.Clone(pair.Value, pair.Key))
            .ToList();
        int changedRouteProperties = 0;
        foreach (uint field in EnvironmentRouteFields)
        {
            bool hadTarget = target.Properties.TryGetValue(field, out var targetRoute);
            bool hasSource = source.Properties.TryGetValue(field, out var sourceRoute);
            if (hasSource) properties.Add(BinTreeCloner.Clone(sourceRoute!, field));
            if (hadTarget != hasSource || (hadTarget && !BinPropEquality.PropsEqual(targetRoute!, sourceRoute!)))
                changedRouteProperties++;
        }
        if (changedRouteProperties == 0)
            throw new InvalidOperationException($"{targetInfo.Name} already uses {sourceInfo.Name}'s environment route.");
        tree.Objects[targetSkinHash] = new BinTreeObject(targetSkinHash, MapSkinClass, properties);

        using var output = new MemoryStream(shippingBin.Length);
        tree.Write(output);
        byte[] bytes = output.ToArray();

        // Strict reparse plus semantic invariants. The selection table and source definition stay
        // untouched, and every rewritten slot must survive exact semantic round-trip serialization.
        var verified = new BinTree(new MemoryStream(bytes, writable: false));
        if (verified.Objects.Count != tree.Objects.Count)
            throw new InvalidDataException("The rewritten shipping bin changed its object count.");
        var original = SafeBinTree.Parse(shippingBin);
        if (!BinPropEquality.PropsEqual(original.Objects[catalog.MapObjectHash].Properties[MapSkinsField],
                verified.Objects[catalog.MapObjectHash].Properties[MapSkinsField]))
            throw new InvalidDataException("The rewrite changed the mapSkins selection table.");
        if (!BinPropEquality.ObjectsEqual(original.Objects[sourceSkinHash], verified.Objects[sourceSkinHash]))
            throw new InvalidDataException("The rewrite changed the source MapSkin.");
        if (!BinPropEquality.ObjectsEqual(tree.Objects[targetSkinHash], verified.Objects[targetSkinHash]))
            throw new InvalidDataException($"Routed MapSkin 0x{targetSkinHash:x8} did not survive strict round-trip serialization.");

        var strings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (uint field in EnvironmentRouteFields)
            if (source.Properties.TryGetValue(field, out var property)) CollectStrings(property, strings);
        return new MapSkinSwapResult(bytes, targetInfo, sourceInfo, changedRouteProperties,
            new[] { targetSkinHash }, strings.Order().ToList());
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
