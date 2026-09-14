using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Meta;

/// <summary>What one replay of a recipe did to one original bin.</summary>
/// <param name="UsedSnapshot">The patch no longer ships the source slot, so the recorded copy was applied. Worth
/// a look in game: the environment it forces must still ship, or be part of the mod.</param>
/// <param name="Notes">Everything a reader should know about how the recipe was resolved on this patch.</param>
public sealed record MapSkinReplayResult(
    byte[] Bytes,
    MapSkinSwapResult Swap,
    MapSkinCatalog Catalog,
    bool UsedSnapshot,
    string TargetName,
    string SourceName,
    IReadOnlyList<string> Notes);

/// <summary>
/// M730: a Map Skin Switcher run as a RULE that can be run again - the recipe behind a forced map bin.
///
/// <para><b>The problem it solves.</b> A forced <c>mapNN.bin</c> is the switcher's output: every MapSkin slot's
/// environment fields overwritten with one slot's. The patch updater carried that output across patches as a
/// diff, which is faithful to the slots that existed and blind to the rule. Measured on the user's Map Forcer:
/// 36 slots routed on 16.15; 16.17 added Hall_Of_Legends, the merge left it as Riot shipped it, and the game -
/// which picks the seasonal slot - loaded Hall of Legends. 16.18 then removed the slot again, and a merge of the
/// other forced project restored a stale copy of it as an unregistered alias. Replaying the recipe on each new
/// original forces whatever slots that original has and nothing else.</para>
///
/// <para><b>How a replay finds its slots.</b> The source by name, then by container link (Riot has renamed
/// slots), then the recorded <see cref="Snapshot"/> of it (Riot vaults seasonal slots; the mod usually ships
/// their assets). When the live slot is used, Riot's current values for it flow through - exactly what re-running
/// the switcher by hand would give. The target names the slot whose audio profile takes the source's; when it is
/// gone the replay falls back to Default and says so.</para>
/// </summary>
public sealed record MapSkinForceRecipe(
    int MapId,
    /// <summary>Null for a recipe inferred from a bin whose audio was never routed: route every slot, touch no
    /// audio profile, so the replay reproduces the bin rather than adding an edit it never had.</summary>
    string? TargetName,
    string SourceName,
    string? SourceContainerLink,
    bool CarryCharacterSkins,
    MapSkinSourceSnapshot? Snapshot)
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    public string Describe() =>
        $"{(TargetName ?? "every slot")} <- {SourceName}"
        + (SourceContainerLink is { Length: > 0 } link ? $" ({link})" : "")
        + (CarryCharacterSkins ? ", character skins carried" : "");

    /// <summary><c>data/maps/shipping/mapNN/mapNN.bin</c> - the only bins a recipe of this kind describes.</summary>
    public static bool IsShippingMapBinPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5
            && parts[0].Equals("data", OIC) && parts[1].Equals("maps", OIC) && parts[2].Equals("shipping", OIC)
            && parts[3].StartsWith("map", OIC) && parts[4].Equals(parts[3] + ".bin", OIC)
            && int.TryParse(parts[3].AsSpan(3), out _);
    }

    // ===================================================== record

    /// <summary>The recipe for the switch the Map Skin Switcher is about to make, with a snapshot of the source.</summary>
    public static MapSkinForceRecipe Record(byte[] shippingBin, int mapId, uint targetSkinHash, uint sourceSkinHash,
        bool carryCharacterSkins, Func<uint, string?>? resolve)
    {
        var catalog = MapSkinSwitcher.ReadCatalog(shippingBin, resolve);
        var target = catalog.Skins.FirstOrDefault(s => s.PathHash == targetSkinHash)
            ?? throw new InvalidOperationException("The target is not a registered MapSkin slot.");
        var source = catalog.Skins.FirstOrDefault(s => s.PathHash == sourceSkinHash)
            ?? throw new InvalidOperationException("The source is not a registered MapSkin slot.");
        var tree = SafeBinTree.Parse(shippingBin);
        return new MapSkinForceRecipe(mapId, target.Name, source.Name, source.MapContainerLink, carryCharacterSkins,
            TakeSnapshot(tree, source));
    }

    private static MapSkinSourceSnapshot TakeSnapshot(BinTree tree, MapSkinInfo source)
    {
        var skin = tree.Objects[source.PathHash];
        var audio = FindFeatureAudio(tree, source.Name);
        return new MapSkinSourceSnapshot(source.Name, source.MapContainerLink, CloneObject(skin),
            audio is null ? null : CloneObject(audio));
    }

    private static BinTreeObject? FindFeatureAudio(BinTree tree, string skinName)
    {
        uint feature = HashAlgorithms.Fnv1a(skinName);
        return tree.Objects.Values.FirstOrDefault(o => o.ClassHash == MapSkinSwitcher.FeatureAudioClassHash
            && o.Properties.TryGetValue(MapSkinSwitcher.FeatureFieldHash, out var f)
            && f is BinTreeHash h && h.Value == feature);
    }

    private static BinTreeObject CloneObject(BinTreeObject o) =>
        new(o.PathHash, o.ClassHash, o.Properties.Select(kv => BinTreeCloner.Clone(kv.Value, kv.Key)));

    // ===================================================== the project record

    public BinRecipeRecord ToRecord(string path, string? patch, string origin) => new()
    {
        Path = path,
        Kind = BinRecipeRecord.ForceMapSkinKind,
        MapId = MapId,
        TargetSkin = TargetName,
        SourceSkin = SourceName,
        SourceContainerLink = SourceContainerLink,
        CarryCharacterSkins = CarryCharacterSkins,
        SourceSnapshot = Snapshot is null ? null : Convert.ToBase64String(SnapshotBytes(Snapshot)),
        Origin = origin,
        RecordedUtc = DateTime.UtcNow.ToString("O"),
        RecordedOnPatch = patch,
    };

    public static MapSkinForceRecipe FromRecord(BinRecipeRecord record)
    {
        if (!string.Equals(record.Kind, BinRecipeRecord.ForceMapSkinKind, StringComparison.Ordinal))
            throw new InvalidDataException($"Not a {BinRecipeRecord.ForceMapSkinKind} recipe: {record.Kind}");
        if (string.IsNullOrWhiteSpace(record.SourceSkin))
            throw new InvalidDataException("The recipe names no source skin.");
        MapSkinSourceSnapshot? snapshot = null;
        if (record.SourceSnapshot is { Length: > 0 } b64)
            snapshot = ParseSnapshot(Convert.FromBase64String(b64), record.SourceSkin, record.SourceContainerLink);
        return new MapSkinForceRecipe(record.MapId, record.TargetSkin, record.SourceSkin, record.SourceContainerLink,
            record.CarryCharacterSkins, snapshot);
    }

    /// <summary>The snapshot as a small bin of its own: the source MapSkin object and, when it has one, its
    /// FeatureAudio object. A bin rather than JSON so every property keeps its exact wire form.</summary>
    public static byte[] SnapshotBytes(MapSkinSourceSnapshot snapshot)
    {
        var objects = new List<BinTreeObject> { snapshot.Skin };
        if (snapshot.Audio is not null) objects.Add(snapshot.Audio);
        using var ms = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    public static MapSkinSourceSnapshot ParseSnapshot(byte[] bytes, string name, string? containerLink)
    {
        var tree = SafeBinTree.Parse(bytes);
        var skin = tree.Objects.Values.FirstOrDefault(o => o.ClassHash == MapSkinSwitcher.MapSkinClassHash)
            ?? throw new InvalidDataException("The recorded snapshot holds no MapSkin object.");
        var audio = tree.Objects.Values.FirstOrDefault(o => o.ClassHash == MapSkinSwitcher.FeatureAudioClassHash);
        return new MapSkinSourceSnapshot(name, containerLink, skin, audio);
    }

    // ===================================================== replay

    /// <summary>Do the switch again on <paramref name="baseBin"/> - any patch's original of the map bin.</summary>
    public MapSkinReplayResult Replay(byte[] baseBin, Func<uint, string?>? resolve)
    {
        var catalog = MapSkinSwitcher.ReadCatalog(baseBin, resolve);
        var notes = new List<string>();

        var source = catalog.Skins.FirstOrDefault(s => s.Name.Equals(SourceName, OIC) && s.MapContainerLink is not null);
        if (source is null && SourceContainerLink is { Length: > 0 })
        {
            source = catalog.Skins.FirstOrDefault(s => string.Equals(s.MapContainerLink, SourceContainerLink, OIC));
            if (source is not null)
                notes.Add($"the source slot {SourceName} is not in this patch; {source.Name} loads the same container "
                          + $"({SourceContainerLink}) and was used instead");
        }
        bool usedSnapshot = false;
        if (source is null)
        {
            if (Snapshot is null)
                throw new InvalidOperationException($"The source slot {SourceName} is not in this patch and no copy of it was recorded.");
            usedSnapshot = true;
            notes.Add($"the source slot {SourceName} is not in this patch - the recorded copy of its environment was applied "
                      + "instead. Check the map in game: what it forces must still ship, or be part of this mod.");
        }
        else if (Snapshot is not null)
        {
            // Riot may have changed the slot since the recipe was recorded. The live values win - that is what
            // re-running the switcher by hand gives - but say which fields moved.
            var live = SafeBinTree.Parse(baseBin).Objects[source.PathHash];
            var moved = MapSkinSwitcher.EnvironmentRouteFieldHashes
                .Where(f => Snapshot.Skin.Properties.TryGetValue(f, out var was) && live.Properties.TryGetValue(f, out var now)
                            && !BinPropEquality.PropsEqual(was, now))
                .Select(f => resolve?.Invoke(f) ?? $"0x{f:x8}")
                .ToList();
            if (moved.Count > 0)
                notes.Add($"Riot changed {source.Name}'s {string.Join(", ", moved)} since the recipe was recorded; "
                          + "its current values were forced");
        }

        bool routeAudio = TargetName is not null;
        var target = TargetName is null ? null : catalog.Skins.FirstOrDefault(s => s.Name.Equals(TargetName, OIC));
        if (target is null)
        {
            target = catalog.Skins.FirstOrDefault(s => s.Name.Equals("Default", OIC) && s.PathHash != source?.PathHash)
                ?? catalog.Skins.FirstOrDefault(s => s.PathHash != source?.PathHash)
                ?? throw new InvalidOperationException("This patch registers no map skin slot other than the source.");
            if (TargetName is not null)
                notes.Add($"the base slot {TargetName} is not in this patch; {target.Name} took its place as the base "
                          + "whose audio profile is routed");
        }

        var swap = usedSnapshot
            ? MapSkinSwitcher.SwitchToSnapshot(baseBin, MapId, target.PathHash, Snapshot!, resolve, CarryCharacterSkins, routeAudio)
            : MapSkinSwitcher.Switch(baseBin, MapId, target.PathHash, source!.PathHash, resolve, CarryCharacterSkins, routeAudio);

        byte[] bytes = swap.Bytes;
        if (usedSnapshot)
        {
            // M590: the snapshot carries the wire forms of the patch it was recorded on. A String texture path on
            // a patch that writes WadChunkLinks is silently skipped by the client, so align with this original.
            var tree = SafeBinTree.Parse(bytes);
            int relinked = BinAssetLinkMigration.AlignWith(tree, SafeBinTree.Parse(baseBin));
            if (relinked > 0)
            {
                using var ms = new MemoryStream();
                tree.Write(ms);
                bytes = ms.ToArray();
                notes.Add($"{relinked} recorded asset reference(s) rewritten to the wire form this patch uses");
            }
        }

        return new MapSkinReplayResult(bytes, swap, catalog, usedSnapshot, target.Name, source?.Name ?? SourceName, notes);
    }

    // ===================================================== infer

    /// <summary>
    /// Read the recipe back out of a forced bin made before recipes existed, by comparing it with the original it
    /// was made from. Null when the bin does not look like a switch: it is not a shipping map bin, no slot was
    /// routed, or the routed slots do not all agree with one untouched slot.
    /// </summary>
    public static MapSkinForceRecipe? Infer(byte[] modBin, byte[] baseBin, Func<uint, string?>? resolve)
    {
        BinTree mod, bas;
        MapSkinCatalog modCatalog;
        try
        {
            mod = SafeBinTree.Parse(modBin);
            bas = SafeBinTree.Parse(baseBin);
            modCatalog = MapSkinSwitcher.ReadCatalog(modBin, resolve);
        }
        catch (Exception)
        {
            return null;   // not a shipping map bin, or one this build cannot read - nothing to infer
        }

        var routeFields = MapSkinSwitcher.EnvironmentRouteFieldHashes;
        var modSkins = mod.Objects.Values.Where(o => o.ClassHash == MapSkinSwitcher.MapSkinClassHash).ToList();

        // Which slots did the mod route? Route fields present in both copies and different.
        var routed = modSkins.Where(m => bas.Objects.TryGetValue(m.PathHash, out var b) && routeFields.Any(f =>
                m.Properties.TryGetValue(f, out var mv) && b.Properties.TryGetValue(f, out var bv) && !BinPropEquality.PropsEqual(mv, bv)))
            .ToList();
        if (routed.Count == 0) return null;

        // The source: a registered slot with a container, untouched by the mod, whose route values every MapSkin
        // object in the bin agrees with on the fields they share. Several may qualify with identical values
        // (Default and its aliases); the registered order decides, Default first.
        static bool AgreesWith(BinTreeObject m, BinTreeObject s, IReadOnlyList<uint> fields) =>
            fields.All(f => !m.Properties.TryGetValue(f, out var mv) || !s.Properties.TryGetValue(f, out var sv)
                            || BinPropEquality.PropsEqual(mv, sv));
        var candidates = modCatalog.Skins
            .Where(s => s.MapContainerLink is not null
                        && bas.Objects.TryGetValue(s.PathHash, out var b) && BinPropEquality.ObjectsEqual(mod.Objects[s.PathHash], b)
                        && modSkins.All(m => AgreesWith(m, mod.Objects[s.PathHash], routeFields)))
            .OrderBy(s => s.Name.Equals("Default", OIC) ? 0 : 1)
            .ThenBy(s => s.Index)
            .ToList();
        if (candidates.Count == 0) return null;
        var source = candidates[0];
        var sourceObject = mod.Objects[source.PathHash];

        // M649's carry: a routed slot whose character-skin field equals the source's where the original's did not.
        bool carry = MapSkinSwitcher.CharacterSkinFieldHashes.Any(f =>
            sourceObject.Properties.TryGetValue(f, out var sv) && routed.Any(m =>
                m.Properties.TryGetValue(f, out var mv) && BinPropEquality.PropsEqual(mv, sv)
                && !(bas.Objects[m.PathHash].Properties.TryGetValue(f, out var bv) && BinPropEquality.PropsEqual(bv, sv))));

        // The target: the one slot whose audio profile the mod changed, when there is exactly one.
        string? targetName = null;
        var audioChanged = mod.Objects.Values
            .Where(o => o.ClassHash == MapSkinSwitcher.FeatureAudioClassHash
                        && bas.Objects.TryGetValue(o.PathHash, out var b) && !BinPropEquality.ObjectsEqual(o, b))
            .ToList();
        if (audioChanged.Count == 1
            && audioChanged[0].Properties.TryGetValue(MapSkinSwitcher.FeatureFieldHash, out var feature) && feature is BinTreeHash fh)
            targetName = modCatalog.Skins.FirstOrDefault(s => HashAlgorithms.Fnv1a(s.Name) == fh.Value && s.PathHash != source.PathHash)?.Name;

        var recipe = new MapSkinForceRecipe(modCatalog.MapId(), targetName, source.Name, source.MapContainerLink, carry,
            TakeSnapshot(mod, source));
        try { recipe.Replay(baseBin, resolve); }
        catch (Exception) { return null; }   // a recipe that cannot even run on its own original is no recipe
        return recipe;
    }
}

internal static class MapSkinCatalogExtensions
{
    /// <summary>The map number from the shipping bin's own <c>Maps/Shipping/MapNN</c> object path.</summary>
    public static int MapId(this MapSkinCatalog catalog)
    {
        // The Map object's hash is FNV-1a of "Maps/Shipping/MapNN"; the number is recovered by trying the ids
        // the client ships rather than by inverting the hash.
        for (int id = 1; id <= 99; id++)
            if (HashAlgorithms.Fnv1a($"Maps/Shipping/Map{id}") == catalog.MapObjectHash) return id;
        return 0;
    }
}
