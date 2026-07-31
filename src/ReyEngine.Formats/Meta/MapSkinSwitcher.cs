using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using System.Numerics;

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
    int ChangedAudioProperties,
    uint? RoutedAudioTargetHash,
    uint? RoutedAudioSourceHash,
    IReadOnlyList<string> ReferencedStrings);

/// <summary>A source map-container rewritten to retain the server-addressed gameplay identities
/// from the current/base container while keeping the source skin's authored values and visuals.</summary>
public sealed record MapSkinContainerCompatibilityResult(
    byte[] Bytes,
    int MatchedServerPlaceables,
    int RemappedServerPlaceableKeys);

/// <summary>
/// Reads and safely rewires Riot's shipping-map skin slots. A switch copies only the source skin's
/// environment-loading values to every MapSkin definition, including unregistered aliases, without
/// adding route fields that the definition did not originally contain. Each definition retains its
/// identity, optional-field shape and runtime data
/// (especially its character-skin overrides), while every possible server selection uses the chosen
/// map container, object configuration, world particles and grass tint where that field is supported.
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
    private static readonly uint FeatureAudioClass = H("FeatureAudioDataProperties");
    private static readonly uint FeatureField = H("feature");
    private static readonly uint BankUnitsField = H("bankUnits");
    private static readonly uint MusicField = H("music");
    private static readonly uint PlaceableContainerClass = H("MapPlaceableContainer");
    private static readonly uint ItemsField = H("items");
    private static readonly uint TransformField = H("transform");
    private static readonly uint CharacterRecordField = H("CharacterRecord");

    // Verified from the Map11 StartSpawn crash at League RVA 0x1246fb0: the server requested
    // Base_SRX item 0x1e1e8b6b (north shopkeeper), but Milkshake stored the equivalent object as
    // 0x4241132a. The class is unresolved in Riot's public hash list, so keep the measured value.
    private const uint ServerCharacterPlaceableClass = 0x25e3f5d0;

    // These are the four fields changed across ALL MapSkin objects by the long-standing,
    // in-game-proven Map Forcer. Updating only the logged server slot is insufficient: the client
    // resolves additional registered slots during StartSpawn. The forcer also preserves missing fields
    // (legacy slots intentionally omit a container, config, or particle property); synthesizing those
    // fields flattens incompatible slot shapes and crashes at the same point. Copying an entire MapSkin
    // is also unsafe:
    // seasonal skins carry spawn-time character overrides (currently unresolved field 0x2d3285eb on
    // Map11) that are only valid when the server selected that skin.
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
        if (sourceInfo.MapContainerLink is null)
            throw new InvalidOperationException("The source slot has no map container and cannot be forced as an environment.");

        var tree = SafeBinTree.Parse(shippingBin);
        var source = tree.Objects[sourceSkinHash];
        int changedRouteProperties = 0;
        var routedSkinHashes = new List<uint>();
        var allMapSkins = tree.Objects
            .Where(pair => pair.Value.ClassHash == MapSkinClass)
            .Select(pair => pair.Key)
            .ToList();
        foreach (uint skinHash in allMapSkins)
        {
            var skin = tree.Objects[skinHash];
            int changedForSkin = 0;
            foreach (uint field in EnvironmentRouteFields)
            {
                bool hadSkin = skin.Properties.TryGetValue(field, out var skinRoute);
                bool hasSource = source.Properties.TryGetValue(field, out var sourceRoute);
                if (hadSkin && hasSource && !BinPropEquality.PropsEqual(skinRoute!, sourceRoute!))
                    changedForSkin++;
            }
            if (changedForSkin == 0) continue;

            var properties = skin.Properties
                .Select(pair => EnvironmentRouteFields.Contains(pair.Key)
                    && source.Properties.TryGetValue(pair.Key, out var sourceRoute)
                        ? BinTreeCloner.Clone(sourceRoute, pair.Key)
                        : BinTreeCloner.Clone(pair.Value, pair.Key))
                .ToList();

            tree.Objects[skinHash] = new BinTreeObject(skinHash, MapSkinClass, properties);
            routedSkinHashes.Add(skinHash);
            changedRouteProperties += changedForSkin;
        }
        var audio = RouteFeatureAudio(tree, targetInfo, sourceInfo);

        using var output = new MemoryStream(shippingBin.Length);
        tree.Write(output);
        byte[] bytes = output.ToArray();

        // Strict reparse plus semantic invariants. The selection table and source definition stay
        // untouched, and every rewritten registered slot or alias must survive semantic round-trip exactly.
        var verified = new BinTree(new MemoryStream(bytes, writable: false));
        if (verified.Objects.Count != tree.Objects.Count)
            throw new InvalidDataException("The rewritten shipping bin changed its object count.");
        var original = SafeBinTree.Parse(shippingBin);
        if (!BinPropEquality.PropsEqual(original.Objects[catalog.MapObjectHash].Properties[MapSkinsField],
                verified.Objects[catalog.MapObjectHash].Properties[MapSkinsField]))
            throw new InvalidDataException("The rewrite changed the mapSkins selection table.");
        if (!BinPropEquality.ObjectsEqual(original.Objects[sourceSkinHash], verified.Objects[sourceSkinHash]))
            throw new InvalidDataException("The rewrite changed the source MapSkin.");
        foreach (uint skinHash in routedSkinHashes)
            if (!BinPropEquality.ObjectsEqual(tree.Objects[skinHash], verified.Objects[skinHash]))
                throw new InvalidDataException($"Routed MapSkin 0x{skinHash:x8} did not survive strict round-trip serialization.");
        if (audio.TargetHash is { } audioTarget
            && !BinPropEquality.ObjectsEqual(tree.Objects[audioTarget], verified.Objects[audioTarget]))
            throw new InvalidDataException($"Routed audio object 0x{audioTarget:x8} did not survive strict round-trip serialization.");
        if (audio.SourceHash is { } audioSource
            && !BinPropEquality.ObjectsEqual(original.Objects[audioSource], verified.Objects[audioSource]))
            throw new InvalidDataException($"The source audio object 0x{audioSource:x8} changed during routing.");

        var strings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (uint field in EnvironmentRouteFields)
            if (source.Properties.TryGetValue(field, out var property)) CollectStrings(property, strings);
        if (audio.SourceHash is { } sourceAudioHash)
            foreach (uint field in new[] { BankUnitsField, MusicField })
                if (original.Objects[sourceAudioHash].Properties.TryGetValue(field, out var property))
                    CollectStrings(property, strings);
        return new MapSkinSwapResult(bytes, targetInfo, sourceInfo, changedRouteProperties,
            routedSkinHashes, audio.ChangedProperties, audio.TargetHash, audio.SourceHash,
            strings.Order().ToList());
    }

    /// <summary>
    /// Make a source skin's materials container compatible with the gameplay identities selected by
    /// the server for the current/base skin. Values remain source-authored; only the opaque item keys
    /// of semantically identical server character placeables are changed.
    /// </summary>
    public static MapSkinContainerCompatibilityResult BuildCompatibleContainer(
        byte[] targetMaterialsBin, byte[] sourceMaterialsBin)
    {
        var target = SafeBinTree.Parse(targetMaterialsBin);
        var source = SafeBinTree.Parse(sourceMaterialsBin);
        var targetItems = ServerItems(target).ToList();
        var sourceItems = ServerItems(source).ToList();

        if (targetItems.Count == 0)
            return new MapSkinContainerCompatibilityResult(sourceMaterialsBin, 0, 0);

        var sourceByIdentity = sourceItems.GroupBy(item => item.Identity).ToDictionary(group => group.Key, group => group.ToList());
        var targetByIdentity = targetItems.GroupBy(item => item.Identity).ToDictionary(group => group.Key, group => group.ToList());
        // The runtime registry is assembled from every MapPlaceableContainer, not only the
        // server-character subset. Refuse a replacement if the desired key belongs to any other
        // source object; silently producing duplicate opaque IDs is another StartSpawn crash.
        var allSourceKeys = MapItems(source).Select(item => item.ItemKey).ToHashSet();
        var replacements = new Dictionary<(uint ContainerHash, uint ItemKey), uint>();
        int matched = 0;

        foreach (var (identity, targetMatches) in targetByIdentity)
        {
            if (targetMatches.Count != 1 || !sourceByIdentity.TryGetValue(identity, out var sourceMatches)
                || sourceMatches.Count != 1)
                continue;

            var targetItem = targetMatches[0];
            var sourceItem = sourceMatches[0];
            matched++;
            if (targetItem.ItemKey == sourceItem.ItemKey) continue;
            if (allSourceKeys.Contains(targetItem.ItemKey))
                throw new InvalidDataException($"Cannot preserve server placeable 0x{targetItem.ItemKey:x8}: "
                    + "the source container already uses that key for a different gameplay object.");
            replacements[(sourceItem.ContainerHash, sourceItem.ItemKey)] = targetItem.ItemKey;
            allSourceKeys.Remove(sourceItem.ItemKey);
            allSourceKeys.Add(targetItem.ItemKey);
        }

        if (matched != targetItems.Count)
            throw new InvalidDataException($"The source map container is not gameplay-compatible with the selected base skin: "
                + $"matched {matched:n0} of {targetItems.Count:n0} server character placeables.");
        if (replacements.Count == 0)
            return new MapSkinContainerCompatibilityResult(sourceMaterialsBin, matched, 0);

        foreach (var (containerHash, container) in source.Objects)
        {
            if (container.ClassHash != PlaceableContainerClass
                || !container.Properties.TryGetValue(ItemsField, out var property) || property is not BinTreeMap items)
                continue;
            var entries = new List<KeyValuePair<BinTreeProperty, BinTreeProperty>>();
            foreach (var entry in items)
            {
                if (entry.Key is BinTreeHash key
                    && replacements.TryGetValue((containerHash, key.Value), out uint replacement))
                    entries.Add(new(new BinTreeHash(0, replacement), entry.Value));
                else
                    entries.Add(new(entry.Key, entry.Value));
            }
            container.Properties[ItemsField] = new BinTreeMap(ItemsField, items.KeyType, items.ValueType, entries);
        }

        using var output = new MemoryStream(sourceMaterialsBin.Length);
        source.Write(output);
        byte[] bytes = output.ToArray();
        var verified = SafeBinTree.Parse(bytes);
        if (verified.Objects.Count != source.Objects.Count)
            throw new InvalidDataException("The compatible source container changed its object count.");
        foreach (var (objectHash, desiredObject) in source.Objects)
            if (!verified.Objects.TryGetValue(objectHash, out var verifiedObject)
                || !BinPropEquality.ObjectsEqual(desiredObject, verifiedObject))
                throw new InvalidDataException($"Compatible container object 0x{objectHash:x8} "
                    + "did not survive strict round-trip serialization.");

        ValidateContainerRewrite(SafeBinTree.Parse(sourceMaterialsBin), verified, replacements);
        var verifiedItems = ServerItems(verified).ToList();
        foreach (var replacement in replacements.Values)
            if (verifiedItems.Count(item => item.ItemKey == replacement) != 1)
                throw new InvalidDataException($"Server placeable 0x{replacement:x8} did not survive container round-trip.");
        foreach (var oldKey in replacements.Keys.Select(key => key.ItemKey))
            if (verifiedItems.Any(item => item.ItemKey == oldKey))
                throw new InvalidDataException($"Old source placeable key 0x{oldKey:x8} remained after compatibility rewrite.");

        return new MapSkinContainerCompatibilityResult(bytes, matched, replacements.Count);
    }

    private static (int ChangedProperties, uint? TargetHash, uint? SourceHash) RouteFeatureAudio(
        BinTree tree, MapSkinInfo target, MapSkinInfo source)
    {
        var audioObjects = tree.Objects.Where(pair => pair.Value.ClassHash == FeatureAudioClass).ToList();
        var targetAudio = audioObjects.FirstOrDefault(pair =>
            pair.Value.Properties.TryGetValue(FeatureField, out var feature)
            && feature is BinTreeHash hash && hash.Value == H(target.Name));
        if (targetAudio.Value is null) return (0, null, null);

        var sourceAudio = audioObjects.FirstOrDefault(pair =>
            pair.Value.Properties.TryGetValue(FeatureField, out var feature)
            && feature is BinTreeHash hash && hash.Value == H(source.Name));
        if (sourceAudio.Value is null)
        {
            string[] tokens = AudioIdentityTokens(source).ToArray();
            var scored = audioObjects
                .Where(pair => pair.Key != targetAudio.Key)
                .Select(pair => (Pair: pair, Score: AudioScore(pair.Value, tokens)))
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ToList();
            if (scored.Count == 0 || scored.Count > 1 && scored[0].Score == scored[1].Score)
                return (0, null, null);
            sourceAudio = scored[0].Pair;
        }
        if (sourceAudio.Key == targetAudio.Key) return (0, null, null);

        int changed = 0;
        foreach (uint field in new[] { BankUnitsField, MusicField })
        {
            bool targetHas = targetAudio.Value.Properties.TryGetValue(field, out var targetProperty);
            bool sourceHas = sourceAudio.Value.Properties.TryGetValue(field, out var sourceProperty);
            if (targetHas != sourceHas || targetHas && !BinPropEquality.PropsEqual(targetProperty!, sourceProperty!)) changed++;
        }
        // Keep reporting the selected source on a repeat application. Besides making the operation
        // idempotent, this retains its referenced Wwise banks in the normal asset preflight.
        if (changed == 0) return (0, targetAudio.Key, sourceAudio.Key);

        var properties = sourceAudio.Value.Properties.Select(pair => pair.Key == FeatureField
            ? BinTreeCloner.Clone(targetAudio.Value.Properties[FeatureField], FeatureField)
            : BinTreeCloner.Clone(pair.Value, pair.Key));
        tree.Objects[targetAudio.Key] = new BinTreeObject(targetAudio.Key, FeatureAudioClass, properties);
        return (changed, targetAudio.Key, sourceAudio.Key);
    }

    private static IEnumerable<string> AudioIdentityTokens(MapSkinInfo skin)
    {
        string identity = skin.MapContainerLink?.Split('/').LastOrDefault() ?? skin.Name;
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "map", "maps", "skin", "skins", "srs", "srx", "seasonal", "default", "base" };
        return identity.Split(new[] { '_', '-', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 4 && !ignored.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static int AudioScore(BinTreeObject audio, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return 0;
        var strings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in audio.Properties.Values) CollectStrings(property, strings);
        return tokens.Sum(token => strings.Count(value => value.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private readonly record struct ServerItemIdentity(string CharacterRecord, Matrix4x4 Transform);
    private sealed record ServerItem(uint ContainerHash, uint ItemKey, ServerItemIdentity Identity);
    private sealed record MapItem(uint ContainerHash, uint ItemKey, BinTreeProperty Value);

    private static IEnumerable<MapItem> MapItems(BinTree tree)
    {
        foreach (var (containerHash, container) in tree.Objects)
        {
            if (container.ClassHash != PlaceableContainerClass
                || !container.Properties.TryGetValue(ItemsField, out var property) || property is not BinTreeMap items)
                continue;
            foreach (var entry in items)
                if (entry.Key is BinTreeHash key)
                    yield return new MapItem(containerHash, key.Value, entry.Value);
        }
    }

    private static IEnumerable<ServerItem> ServerItems(BinTree tree)
    {
        foreach (var (containerHash, container) in tree.Objects)
        {
            if (container.ClassHash != PlaceableContainerClass
                || !container.Properties.TryGetValue(ItemsField, out var property) || property is not BinTreeMap items)
                continue;
            foreach (var entry in items)
            {
                if (entry.Key is not BinTreeHash key || entry.Value is not BinTreeStruct value
                    || value.ClassHash != ServerCharacterPlaceableClass
                    || !value.Properties.TryGetValue(TransformField, out var transformProperty)
                    || transformProperty is not BinTreeMatrix44 transform)
                    continue;
                string? record = FindString(value, CharacterRecordField);
                if (string.IsNullOrWhiteSpace(record)) continue;
                yield return new ServerItem(containerHash, key.Value,
                    new ServerItemIdentity(record.ToLowerInvariant(), transform.Value));
            }
        }
    }

    private static void ValidateContainerRewrite(BinTree original, BinTree rewritten,
        IReadOnlyDictionary<(uint ContainerHash, uint ItemKey), uint> replacements)
    {
        foreach (var (objectHash, originalObject) in original.Objects)
        {
            if (!rewritten.Objects.TryGetValue(objectHash, out var rewrittenObject)
                || originalObject.ClassHash != rewrittenObject.ClassHash
                || originalObject.Properties.Count != rewrittenObject.Properties.Count)
                throw new InvalidDataException($"Compatibility rewrite changed object 0x{objectHash:x8} shape.");

            foreach (var (propertyHash, originalProperty) in originalObject.Properties)
            {
                if (!rewrittenObject.Properties.TryGetValue(propertyHash, out var rewrittenProperty))
                    throw new InvalidDataException($"Compatibility rewrite removed property 0x{propertyHash:x8}.");
                if (propertyHash != ItemsField || originalProperty is not BinTreeMap originalItems
                    || rewrittenProperty is not BinTreeMap rewrittenItems)
                {
                    if (!BinPropEquality.PropsEqual(originalProperty, rewrittenProperty))
                        throw new InvalidDataException($"Compatibility rewrite changed non-item property 0x{propertyHash:x8}.");
                    continue;
                }

                if (originalItems.Count != rewrittenItems.Count)
                    throw new InvalidDataException("Compatibility rewrite changed a placeable item count.");
                var remaining = rewrittenItems.ToList();
                foreach (var originalEntry in originalItems)
                {
                    uint? replacement = originalEntry.Key is BinTreeHash oldKey
                        && replacements.TryGetValue((objectHash, oldKey.Value), out uint newKey)
                        ? newKey : null;
                    int match = remaining.FindIndex(candidate =>
                        (replacement is { } expected
                            ? candidate.Key is BinTreeHash candidateKey && candidateKey.Value == expected
                            : BinPropEquality.PropsEqual(originalEntry.Key, candidate.Key))
                        && BinPropEquality.PropsEqual(originalEntry.Value, candidate.Value));
                    if (match < 0)
                        throw new InvalidDataException("Compatibility rewrite changed a placeable value or unexpected key.");
                    remaining.RemoveAt(match);
                }
                if (remaining.Count != 0)
                    throw new InvalidDataException("Compatibility rewrite introduced an unexpected placeable item.");
            }
        }
    }

    private static string? FindString(BinTreeProperty property, uint fieldHash)
    {
        if (property.NameHash == fieldHash && property is BinTreeString text) return text.Value;
        return property switch
        {
            BinTreeStruct structure => structure.Properties.Values.Select(child => FindString(child, fieldHash))
                .FirstOrDefault(value => value is not null),
            BinTreeContainer container => container.Elements.Select(child => FindString(child, fieldHash))
                .FirstOrDefault(value => value is not null),
            BinTreeOptional { Value: { } inner } => FindString(inner, fieldHash),
            _ => null,
        };
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
