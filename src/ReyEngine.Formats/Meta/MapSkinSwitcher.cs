using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using System.Numerics;

namespace ReyEngine.Formats.Meta;

/// <summary>M649: one character whose skin a map skin forces - a turret, a minion, the nexus.</summary>
/// <param name="Character">The character record, e.g. <c>Characters/Turret</c>.</param>
/// <param name="SkinId">The skin index that character is given while this map skin is selected.</param>
/// <param name="FromFallbackMap">True when it came from the older <c>mObjectSkinFallbacks</c> map rather
/// than the newer per-character list. Both are live: 11 of Map11's slots use the map, 2 use the list.</param>
public sealed record MapSkinCharacterOverride(string Character, int SkinId, bool FromFallbackMap)
{
    public string DisplayName => $"{Character.Split('/').Last()} = skin {SkinId}";
}

/// <summary>One registered skin slot from a shipping map bin.</summary>
public sealed record MapSkinInfo(
    int Index,
    uint PathHash,
    string ObjectPath,
    string Name,
    string? MapContainerLink,
    int PropertyCount)
{
    /// <summary>M649: the turret/minion/nexus skins this slot forces. Empty for most slots.</summary>
    public IReadOnlyList<MapSkinCharacterOverride> CharacterSkins { get; init; } = Array.Empty<MapSkinCharacterOverride>();

    public string DisplayName => MapContainerLink is { Length: > 0 }
        ? $"{Name}  -  {MapContainerLink.Split('/').Last()}"
        : $"{Name}  -  legacy/default geometry";

    /// <summary>What this slot does to characters, for the row under its name.</summary>
    public string CharacterSummary => CharacterSkins.Count == 0
        ? "no character skins"
        : $"{CharacterSkins.Count} character skin(s): " + string.Join(", ", CharacterSkins.Take(4).Select(o => o.DisplayName))
          + (CharacterSkins.Count > 4 ? $", +{CharacterSkins.Count - 4} more" : "");
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
    IReadOnlyList<string> ReferencedStrings)
{
    /// <summary>M649: slots whose character-skin overrides were replaced by the source's.</summary>
    public int CharacterSkinSlotsRouted { get; init; }
    /// <summary>M649: slots that shipped with NO character-skin field and were given one. Reported
    /// separately because it is the part of this that Riot's own data has no example of.</summary>
    public int CharacterSkinSlotsAdded { get; init; }
    public IReadOnlyList<MapSkinCharacterOverride> CarriedCharacterSkins { get; init; } = Array.Empty<MapSkinCharacterOverride>();
}

/// <summary>A source map-container rewritten to retain the server-addressed gameplay identities
/// from the current/base container while keeping the source skin's authored values and visuals.</summary>
public sealed record MapSkinContainerCompatibilityResult(
    byte[] Bytes,
    int MatchedServerPlaceables,
    int RemappedServerPlaceableKeys);

/// <summary>
/// M730: a source environment given by VALUE rather than by slot - the recorded copy of a MapSkin object (and
/// its FeatureAudio object, when it has one) taken when a recipe was recorded. A switch from a snapshot routes
/// the same fields the live switch routes, from an object that need not be in the bin at all, which is what a
/// recipe needs once Riot vaults the seasonal slot it was recorded from.
/// </summary>
public sealed record MapSkinSourceSnapshot(string Name, string? MapContainerLink, BinTreeObject Skin, BinTreeObject? Audio);

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

    /// <summary>
    /// M649: the two fields that decide which SKIN a turret, minion or nexus wears while a map skin is
    /// selected - the thing a user means by "swapping the turrets".
    ///
    /// <para>There are two of them because Riot moved: <c>mObjectSkinFallbacks</c> is a
    /// <c>map[hash,i32]</c> and is what every older slot uses (Odyssey, Arcade, URF, Project…), while
    /// 0x2d3285eb is a <c>list2</c> of <c>{Character, SkinID}</c> added in schema revision 7231955 and so
    /// far used only by the two newest Map11 slots - Milkshake_SRS (24 entries, Turret = 4) and
    /// Sodapop_SRS (6 entries, Turret = 48). Neither field is named in Riot's public hash list for the
    /// second one, which is why it shows in an editor as a bare 0x2d3285eb.</para>
    ///
    /// <para>They are NOT part of <see cref="EnvironmentRouteFields"/> and must not become part of it:
    /// routing an environment is safe because the values describe a place, while these describe units the
    /// server spawns. Carrying them is opt-in for that reason.</para>
    /// </summary>
    private const uint CharacterSkinListField = 0x2d3285eb;
    private static readonly uint ObjectSkinFallbacksField = H("mObjectSkinFallbacks");
    private static readonly uint CharacterField = H("Character");
    private static readonly uint SkinIdField = H("SkinID");
    private static readonly uint[] CharacterSkinFields = { CharacterSkinListField, ObjectSkinFallbacksField };

    // M730: the recipe machinery (MapSkinForceRecipe) reads a bin the way this switch writes it, so it needs the
    // same classes and fields. Read-only views; the arrays above stay the single definition.
    public static uint MapSkinClassHash => MapSkinClass;
    public static uint FeatureAudioClassHash => FeatureAudioClass;
    public static uint FeatureFieldHash => FeatureField;
    public static IReadOnlyList<uint> EnvironmentRouteFieldHashes => EnvironmentRouteFields;
    public static IReadOnlyList<uint> CharacterSkinFieldHashes => CharacterSkinFields;

    /// <summary>Read what a MapSkin forces onto characters, from either mechanism.</summary>
    internal static IReadOnlyList<MapSkinCharacterOverride> ReadCharacterSkins(
        BinTreeObject skin, Func<uint, string?>? resolve)
    {
        var result = new List<MapSkinCharacterOverride>();
        if (skin.Properties.TryGetValue(CharacterSkinListField, out var listProp) && listProp is BinTreeContainer list)
            foreach (var element in list.Elements.OfType<BinTreeStruct>())
            {
                if (element.Properties.TryGetValue(CharacterField, out var c) && c is not BinTreeHash) continue;
                uint characterHash = element.Properties.TryGetValue(CharacterField, out var ch) && ch is BinTreeHash h ? h.Value : 0;
                int skinId = element.Properties.TryGetValue(SkinIdField, out var s) && s is BinTreeU32 u ? (int)u.Value : 0;
                if (characterHash == 0) continue;
                result.Add(new MapSkinCharacterOverride(NameOf(resolve, characterHash), skinId, false));
            }
        if (skin.Properties.TryGetValue(ObjectSkinFallbacksField, out var mapProp) && mapProp is BinTreeMap map)
            foreach (var pair in map)
                if (pair.Key is BinTreeHash key)
                    result.Add(new MapSkinCharacterOverride(NameOf(resolve, key.Value),
                        pair.Value is BinTreeI32 i ? i.Value : 0, true));
        return result;
    }

    private static string NameOf(Func<uint, string?>? resolve, uint hash) =>
        resolve?.Invoke(hash) is { Length: > 0 } n ? n : $"0x{hash:x8}";

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
                name.Value, container, skin.Properties.Count)
            {
                CharacterSkins = ReadCharacterSkins(skin, resolve),   // M649
            });
        }
        return new MapSkinCatalog(mapStringId, mapHash, skins);
    }

    /// <param name="carryCharacterSkins">M649: also give every rewritten slot the SOURCE skin's
    /// turret/minion/nexus skins. Off by default - see <see cref="CharacterSkinFields"/> for why these are
    /// not environment fields. A slot that shipped without the field is GIVEN one, which is the one part of
    /// this that Riot's own data has no example of; the result counts those separately so a caller can say so.</param>
    /// <param name="routeAudio">M730: also give the target's FeatureAudio profile the source's banks and music
    /// (the default, and what the switcher has always done). A recipe inferred from a bin whose audio was never
    /// routed replays with this off, so it reproduces that bin rather than adding an edit it never had.</param>
    public static MapSkinSwapResult Switch(
        byte[] shippingBin,
        int mapId,
        uint targetSkinHash,
        uint sourceSkinHash,
        Func<uint, string?>? resolve = null,
        bool carryCharacterSkins = false,
        bool routeAudio = true)
        => SwitchCore(shippingBin, mapId, targetSkinHash, sourceSkinHash, null, resolve, carryCharacterSkins, routeAudio);

    /// <summary>
    /// M730: the same switch from a recorded copy of the source slot instead of a slot in the bin. Every MapSkin
    /// object in the bin is routed exactly as <see cref="Switch"/> routes it; the only difference is where the
    /// values come from. For a recipe whose source slot the patch no longer ships.
    /// </summary>
    public static MapSkinSwapResult SwitchToSnapshot(
        byte[] shippingBin,
        int mapId,
        uint targetSkinHash,
        MapSkinSourceSnapshot snapshot,
        Func<uint, string?>? resolve = null,
        bool carryCharacterSkins = false,
        bool routeAudio = true)
        => SwitchCore(shippingBin, mapId, targetSkinHash, null, snapshot, resolve, carryCharacterSkins, routeAudio);

    private static MapSkinSwapResult SwitchCore(
        byte[] shippingBin,
        int mapId,
        uint targetSkinHash,
        uint? sourceSkinHash,
        MapSkinSourceSnapshot? snapshot,
        Func<uint, string?>? resolve,
        bool carryCharacterSkins,
        bool routeAudio)
    {
        var catalog = ReadCatalog(shippingBin, resolve);
        if (BlockReason(mapId, catalog.MapStringId) is { } blocked) throw new InvalidOperationException(blocked);
        if (sourceSkinHash is { } same && targetSkinHash == same) throw new InvalidOperationException("Choose two different map skins.");

        var targetInfo = catalog.Skins.FirstOrDefault(s => s.PathHash == targetSkinHash)
            ?? throw new InvalidOperationException("The target is not a registered MapSkin slot.");

        var tree = SafeBinTree.Parse(shippingBin);
        BinTreeObject source;
        MapSkinInfo sourceInfo;
        if (sourceSkinHash is { } liveSource)
        {
            sourceInfo = catalog.Skins.FirstOrDefault(s => s.PathHash == liveSource)
                ?? throw new InvalidOperationException("The source is not a registered MapSkin slot.");
            if (sourceInfo.MapContainerLink is null)
                throw new InvalidOperationException("The source slot has no map container and cannot be forced as an environment.");
            source = tree.Objects[liveSource];
        }
        else
        {
            // M730: a snapshot stands in for the slot. It carries the slot's own hash, so a stale unregistered
            // copy of the vaulted slot left in a bin by an earlier merge is routed like every other MapSkin object.
            var recorded = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            if (recorded.MapContainerLink is null)
                throw new InvalidOperationException("The recorded source environment has no map container and cannot be forced.");
            source = recorded.Skin;
            sourceInfo = new MapSkinInfo(-1, recorded.Skin.PathHash,
                resolve?.Invoke(recorded.Skin.PathHash) ?? $"0x{recorded.Skin.PathHash:x8}",
                recorded.Name, recorded.MapContainerLink, recorded.Skin.Properties.Count)
            {
                CharacterSkins = ReadCharacterSkins(recorded.Skin, resolve),
            };
        }
        int changedRouteProperties = 0;
        int characterSkinSlotsRouted = 0, characterSkinSlotsAdded = 0;   // M649
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

            // M649: the character skins, when asked for. Unlike the environment fields these are ADDED to a
            // slot that lacks them - which is the whole point, since 35 of Map11's 37 slots carry neither
            // field and a user swapping to one of the two that do would otherwise keep default turrets.
            var addHere = new List<uint>();
            int routedHere = 0;
            if (carryCharacterSkins && skinHash != sourceSkinHash)
                foreach (uint field in CharacterSkinFields)
                {
                    if (!source.Properties.TryGetValue(field, out var sourceValue)) continue;
                    if (skin.Properties.TryGetValue(field, out var mine))
                    {
                        if (BinPropEquality.PropsEqual(mine, sourceValue)) continue;
                        routedHere++;
                    }
                    else addHere.Add(field);
                }

            if (changedForSkin == 0 && routedHere == 0 && addHere.Count == 0) continue;

            var routeFields = carryCharacterSkins
                ? EnvironmentRouteFields.Concat(CharacterSkinFields).ToArray()
                : EnvironmentRouteFields;
            var properties = skin.Properties
                .Select(pair => routeFields.Contains(pair.Key)
                    && source.Properties.TryGetValue(pair.Key, out var sourceRoute)
                        ? BinTreeCloner.Clone(sourceRoute, pair.Key)
                        : BinTreeCloner.Clone(pair.Value, pair.Key))
                .ToList();
            foreach (uint field in addHere)
                properties.Add(BinTreeCloner.Clone(source.Properties[field], field));

            tree.Objects[skinHash] = new BinTreeObject(skinHash, MapSkinClass, properties);
            routedSkinHashes.Add(skinHash);
            changedRouteProperties += changedForSkin;
            if (routedHere > 0) characterSkinSlotsRouted++;
            if (addHere.Count > 0) characterSkinSlotsAdded++;
        }
        var audio = routeAudio
            ? RouteFeatureAudio(tree, targetInfo, sourceInfo, snapshot?.Audio)
            : (ChangedProperties: 0, TargetHash: (uint?)null, SourceHash: (uint?)null, SourceAudio: (BinTreeObject?)null);

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
        if (sourceSkinHash is { } liveSourceHash
            && !BinPropEquality.ObjectsEqual(original.Objects[liveSourceHash], verified.Objects[liveSourceHash]))
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
        if (audio.SourceAudio is { } sourceAudio)
            foreach (uint field in new[] { BankUnitsField, MusicField })
                if (sourceAudio.Properties.TryGetValue(field, out var property))
                    CollectStrings(property, strings);
        return new MapSkinSwapResult(bytes, targetInfo, sourceInfo, changedRouteProperties,
            routedSkinHashes, audio.ChangedProperties, audio.TargetHash, audio.SourceHash,
            strings.Order().ToList())
        {
            CharacterSkinSlotsRouted = characterSkinSlotsRouted,
            CharacterSkinSlotsAdded = characterSkinSlotsAdded,
            CarriedCharacterSkins = carryCharacterSkins ? sourceInfo.CharacterSkins : Array.Empty<MapSkinCharacterOverride>(),
        };
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

    /// <param name="sourceAudioOverride">M730: the recorded source audio profile, when the switch runs from a
    /// snapshot. Its hash is not in the tree, so <c>SourceHash</c> comes back null and the source-unchanged
    /// verification is skipped for it; <c>SourceAudio</c> is what the asset preflight reads either way.</param>
    private static (int ChangedProperties, uint? TargetHash, uint? SourceHash, BinTreeObject? SourceAudio) RouteFeatureAudio(
        BinTree tree, MapSkinInfo target, MapSkinInfo source, BinTreeObject? sourceAudioOverride)
    {
        var audioObjects = tree.Objects.Where(pair => pair.Value.ClassHash == FeatureAudioClass).ToList();
        var targetAudio = audioObjects.FirstOrDefault(pair =>
            pair.Value.Properties.TryGetValue(FeatureField, out var feature)
            && feature is BinTreeHash hash && hash.Value == H(target.Name));
        if (targetAudio.Value is null) return (0, null, null, null);

        uint? sourceKey;
        BinTreeObject sourceAudio;
        if (sourceAudioOverride is not null)
        {
            sourceKey = null;
            sourceAudio = sourceAudioOverride;
        }
        else
        {
            var found = audioObjects.FirstOrDefault(pair =>
                pair.Value.Properties.TryGetValue(FeatureField, out var feature)
                && feature is BinTreeHash hash && hash.Value == H(source.Name));
            if (found.Value is null)
            {
                string[] tokens = AudioIdentityTokens(source).ToArray();
                var scored = audioObjects
                    .Where(pair => pair.Key != targetAudio.Key)
                    .Select(pair => (Pair: pair, Score: AudioScore(pair.Value, tokens)))
                    .Where(candidate => candidate.Score > 0)
                    .OrderByDescending(candidate => candidate.Score)
                    .ToList();
                if (scored.Count == 0 || scored.Count > 1 && scored[0].Score == scored[1].Score)
                    return (0, null, null, null);
                found = scored[0].Pair;
            }
            if (found.Key == targetAudio.Key) return (0, null, null, null);
            sourceKey = found.Key;
            sourceAudio = found.Value;
        }

        int changed = 0;
        foreach (uint field in new[] { BankUnitsField, MusicField })
        {
            bool targetHas = targetAudio.Value.Properties.TryGetValue(field, out var targetProperty);
            bool sourceHas = sourceAudio.Properties.TryGetValue(field, out var sourceProperty);
            if (targetHas != sourceHas || targetHas && !BinPropEquality.PropsEqual(targetProperty!, sourceProperty!)) changed++;
        }
        // Keep reporting the selected source on a repeat application. Besides making the operation
        // idempotent, this retains its referenced Wwise banks in the normal asset preflight.
        if (changed == 0) return (0, targetAudio.Key, sourceKey, sourceAudio);

        var properties = sourceAudio.Properties.Select(pair => pair.Key == FeatureField
            ? BinTreeCloner.Clone(targetAudio.Value.Properties[FeatureField], FeatureField)
            : BinTreeCloner.Clone(pair.Value, pair.Key));
        tree.Objects[targetAudio.Key] = new BinTreeObject(targetAudio.Key, FeatureAudioClass, properties);
        return (changed, targetAudio.Key, sourceKey, sourceAudio);
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
