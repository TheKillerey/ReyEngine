using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>Where a placement lives in the bin: which <c>MapPlaceableContainer</c> object, and its key
/// inside that container's <c>items</c> map. Measured over the shipping maps, this pair is unique -
/// 151,457 items, every key a <c>BinTreeHash</c>, and ZERO duplicate keys within a container.</summary>
public readonly record struct MapPlacementId(uint ContainerHash, uint ItemKey)
{
    public bool IsValid => ContainerHash != 0 && ItemKey != 0;
}

/// <summary>One placement edit. Every field is optional; null means "leave this alone".</summary>
public sealed record MapPlacementEdit(MapPlacementId Id)
{
    public Matrix4x4? Transform { get; init; }
    public Vector4? ColorModulate { get; init; }
    /// <summary>Re-point the placement at a different VFX system (the <c>system</c> object link).</summary>
    public uint? SystemLink { get; init; }
    public string? Name { get; init; }
    /// <summary>Replacement map visibility mask. Zero disables the placement without deleting it.</summary>
    public int? VisibilityFlags { get; init; }
    /// <summary>Replacement character skin path on an animated prop / mob placement.</summary>
    public string? Skin { get; init; }
    /// <summary>Delete the placement from its container.</summary>
    public bool Remove { get; init; }

    /// <summary>M206: insert a COPY of this placement under <see cref="Id"/>, rather than editing an
    /// existing one. The source is deep-cloned so the new placement carries every field the original had -
    /// including the ones ReyEngine does not model - and the edit's other verbs are then applied on top.
    /// <see cref="Id"/> must name a key that does not already exist in the container.</summary>
    public MapPlacementId? CloneOf { get; init; }
}

/// <summary>
/// M199 (tier 5.2): persists placement edits into a map's .materials.bin by editing the parsed TREE and
/// rewriting it, replacing the byte-signature patcher in <see cref="MapParticleWriter"/>.
///
/// <para><b>Why the old locator had to go.</b> It found each placement by scanning the raw file for the
/// exact 64 bytes of its original transform. Measured over the 8 shipping map WADs, <b>1,450 of 30,628
/// placements (365 groups) share an identical transform with another placement in the same bin</b> - so
/// editing one could silently patch the other. In Map11's <c>base.materials.bin</c>,
/// <c>SRUAP_Chaos_Inhibitor_runeTimer_mid1</c> and <c>SRUAP_Chaos_Inhibitor_Rubble_dust2</c> are one such
/// pair. The app already knew: it refused to save moved sounds derived from particle systems, precisely
/// because they share the particle's transform bytes. A further 2 placements carry no transform at all and
/// were therefore unaddressable. Identity by (container, item key) has none of those problems, and it is
/// what makes re-tinting, re-linking and removing possible at all - none of which has a byte signature.</para>
///
/// <para><b>The safety net.</b> A tree rewrite touches the whole file, where the old patcher touched 64
/// bytes. So the result is not trusted: it is re-parsed and compared object-by-object and
/// property-by-property against the original, and the write is REFUSED unless the only differences are the
/// ones that were asked for. That check is what makes replacing a proven byte-patcher defensible.</para>
/// </summary>
public static class MapPlaceableWriter
{
    private static readonly uint ContainerClass = HashAlgorithms.Fnv1a("MapPlaceableContainer");
    private static readonly uint F_items = HashAlgorithms.Fnv1a("items");
    private static readonly uint F_transform = HashAlgorithms.Fnv1a("transform");
    private static readonly uint F_colorModulate = HashAlgorithms.Fnv1a("colorModulate");
    private static readonly uint F_system = HashAlgorithms.Fnv1a("system");
    private static readonly uint F_name = HashAlgorithms.Fnv1a("name");
    private static readonly uint F_visibilityFlags = HashAlgorithms.Fnv1a("mVisibilityFlags");
    private static readonly uint F_characterRecord = HashAlgorithms.Fnv1a("characterRecord");
    private static readonly uint F_skin = HashAlgorithms.Fnv1a("skin");

    /// <summary>Apply the edits. Returns the new bytes, or null with a reason.</summary>
    public static byte[]? WriteEdits(byte[] materialsBin, IReadOnlyList<MapPlacementEdit> edits, out string? error)
    {
        error = null;
        if (edits.Count == 0) return materialsBin;

        BinTree original, tree;
        try
        {
            original = SafeBinTree.Parse(materialsBin);
            tree = SafeBinTree.Parse(materialsBin);   // a second, independent parse to diff against
        }
        catch (Exception ex) { error = $"could not parse the .bin: {ex.Message}"; return null; }

        int applied = 0;
        var missing = new List<MapPlacementId>();
        foreach (var edit in edits)
        {
            if (!TryApply(tree, edit)) { missing.Add(edit.Id); continue; }
            applied++;
        }

        if (applied == 0)
        {
            error = "none of the edited placements could be located in the .bin.";
            return null;
        }
        if (missing.Count > 0)
            error = $"{missing.Count} of {edits.Count} placement(s) could not be located (applied {applied}).";

        byte[] result;
        try
        {
            using var ms = new MemoryStream();
            tree.Write(ms);
            result = ms.ToArray();
        }
        catch (Exception ex) { error = $"could not write the .bin: {ex.Message}"; return null; }

        // Re-parse and prove that nothing beyond the requested edits moved.
        BinTree reparsed;
        try { reparsed = SafeBinTree.Parse(result); }
        catch (Exception ex) { error = $"the rewritten .bin no longer parses: {ex.Message}"; return null; }

        if (UnintendedChange(original, reparsed, edits) is { } bad)
        {
            error = $"refusing to save: the rewrite changed something that was not edited ({bad}).";
            return null;
        }
        return result;
    }

    /// <summary>M206: a key no placement in this container is using. Derived from the source key so a
    /// given clone lands on the same key every time (a save must not churn), then walked forward on the
    /// vanishingly unlikely collision.
    ///
    /// <para>Any unused value is safe here: measured over 151,457 shipped items, the key is NOT derived
    /// from the placement's name - 0 match FNV-1a of it in either casing, and 92,079 items have no name at
    /// all - so nothing reconstructs it, and a newly added placement is by definition not referenced by
    /// anything else yet.</para></summary>
    public static uint NewItemKey(BinTree tree, MapPlacementId source)
    {
        var used = new HashSet<uint>();
        if (tree.Objects.TryGetValue(source.ContainerHash, out var c)
            && c.Properties.TryGetValue(F_items, out var ip) && ip is BinTreeMap m)
            foreach (var e in m)
                if (e.Key is BinTreeHash kh) used.Add(kh.Value);

        uint candidate = source.ItemKey * 2654435761u + 0x9E3779B9u;   // Knuth mix, so clones scatter
        while (candidate == 0 || used.Contains(candidate)) candidate++;
        return candidate;
    }

    private static bool TryApply(BinTree tree, MapPlacementEdit edit)
    {
        if (!edit.Id.IsValid) return false;
        if (!tree.Objects.TryGetValue(edit.Id.ContainerHash, out var container)) return false;
        if (container.ClassHash != ContainerClass) return false;
        if (!container.Properties.TryGetValue(F_items, out var itemsProp) || itemsProp is not BinTreeMap items) return false;

        // M206: a clone inserts a deep copy of its source under this edit's (new) key first; everything
        // below then treats it as an ordinary placement, so the other verbs apply to the copy for free.
        if (edit.CloneOf is { } src)
        {
            if (items.Any(e => e.Key is BinTreeHash k && k.Value == edit.Id.ItemKey)) return false;  // key taken
            BinTreeProperty? sourceValue = null;
            foreach (var e in items)
                if (e.Key is BinTreeHash sk && sk.Value == src.ItemKey) { sourceValue = e.Value; break; }
            if (sourceValue is not BinTreeStruct sourceStruct) return false;

            var copy = BinTreeCloner.Clone(sourceStruct, 0);
            var withClone = items
                .Select(e => new KeyValuePair<BinTreeProperty, BinTreeProperty>(e.Key, e.Value))
                .Append(new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, edit.Id.ItemKey), copy));
            items = new BinTreeMap(F_items, items.KeyType, items.ValueType, withClone);
            container.Properties[F_items] = items;
        }

        BinTreeProperty? key = null, value = null;
        foreach (var e in items)
            if (e.Key is BinTreeHash kh && kh.Value == edit.Id.ItemKey) { key = e.Key; value = e.Value; break; }
        if (key is null || value is not BinTreeStruct s) return false;

        if (edit.Remove)
        {
            // Rebuild the map without this entry - BinTreeMap exposes no Remove.
            var kept = items.Where(e => !(e.Key is BinTreeHash h && h.Value == edit.Id.ItemKey))
                            .Select(e => new KeyValuePair<BinTreeProperty, BinTreeProperty>(e.Key, e.Value));
            container.Properties[F_items] = new BinTreeMap(F_items, items.KeyType, items.ValueType, kept);
            return true;
        }

        if (edit.Transform is { } m) s.Properties[F_transform] = new BinTreeMatrix44(F_transform, m);
        if (edit.ColorModulate is { } c) s.Properties[F_colorModulate] = new BinTreeVector4(F_colorModulate, c);
        if (edit.SystemLink is { } link) s.Properties[F_system] = new BinTreeObjectLink(F_system, link);
        if (edit.Name is { } n) s.Properties[F_name] = new BinTreeString(F_name, n);
        if (edit.VisibilityFlags is { } visibility)
        {
            int maskValue = Math.Clamp(visibility, 0, 255);
            s.Properties[F_visibilityFlags] = s.Properties.GetValueOrDefault(F_visibilityFlags) switch
            {
                BinTreeU16 => new BinTreeU16(F_visibilityFlags, (ushort)maskValue),
                BinTreeU32 => new BinTreeU32(F_visibilityFlags, (uint)maskValue),
                _ => new BinTreeU8(F_visibilityFlags, (byte)maskValue),
            };
        }
        if (edit.Skin is { } skin)
        {
            var characterData = s.Properties.Values.OfType<BinTreeStruct>()
                .FirstOrDefault(x => x.Properties.ContainsKey(F_characterRecord));
            if (characterData is null) return false;
            characterData.Properties[F_skin] = new BinTreeString(F_skin, skin);
        }
        return true;
    }

    /// <summary>Null when the rewrite changed only what was asked for; otherwise a description of the first
    /// unintended difference. Objects are compared by path hash, then property-by-property; the containers
    /// holding edited placements are compared entry-by-entry so an edit does not excuse its neighbours.</summary>
    private static string? UnintendedChange(BinTree before, BinTree after, IReadOnlyList<MapPlacementEdit> edits)
    {
        var editedByContainer = edits.GroupBy(e => e.Id.ContainerHash)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Id.ItemKey).ToHashSet());
        // M206: a clone's key is absent from `before` on purpose, so it must not read as an intruder.
        var addedByContainer = edits.Where(e => e.CloneOf is not null).GroupBy(e => e.Id.ContainerHash)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Id.ItemKey).ToHashSet());

        if (before.Objects.Count != after.Objects.Count)
            return $"object count {before.Objects.Count} -> {after.Objects.Count}";

        foreach (var (hash, a) in before.Objects)
        {
            if (!after.Objects.TryGetValue(hash, out var b)) return $"object 0x{hash:x8} disappeared";
            if (a.ClassHash != b.ClassHash) return $"object 0x{hash:x8} changed class";

            if (!editedByContainer.TryGetValue(hash, out var editedKeys))
            {
                if (!BinPropEquality.DictsEqual(a.Properties, b.Properties))
                    return $"untouched object 0x{hash:x8} changed";
                continue;
            }

            // An edited container: every property except `items` must be untouched, and inside `items`
            // every entry except the edited keys must be untouched.
            foreach (var (ph, pa) in a.Properties)
            {
                if (ph == F_items) continue;
                if (!b.Properties.TryGetValue(ph, out var pb) || !BinPropEquality.PropsEqual(pa, pb))
                    return $"container 0x{hash:x8} property 0x{ph:x8} changed";
            }
            if (a.Properties[F_items] is not BinTreeMap ma || b.Properties[F_items] is not BinTreeMap mb)
                return $"container 0x{hash:x8} items is no longer a map";

            var afterByKey = mb.Where(e => e.Key is BinTreeHash)
                               .ToDictionary(e => ((BinTreeHash)e.Key).Value, e => e.Value);
            var added = addedByContainer.GetValueOrDefault(hash) ?? new HashSet<uint>();
            var beforeKeys = ma.Where(e => e.Key is BinTreeHash).Select(e => ((BinTreeHash)e.Key).Value).ToHashSet();
            foreach (var k in afterByKey.Keys)
                if (!beforeKeys.Contains(k) && !added.Contains(k))
                    return $"placement 0x{k:x8} appeared without being asked for";
            foreach (var e in ma)
            {
                if (e.Key is not BinTreeHash kh) continue;
                bool wasEdited = editedKeys.Contains(kh.Value);
                bool stillThere = afterByKey.TryGetValue(kh.Value, out var vb);
                if (!stillThere)
                {
                    if (!wasEdited) return $"placement 0x{kh.Value:x8} vanished without being edited";
                    continue;   // a requested Remove
                }
                if (!wasEdited && !BinPropEquality.PropsEqual(e.Value, vb))
                    return $"untouched placement 0x{kh.Value:x8} changed";
            }
        }
        return null;
    }
}
