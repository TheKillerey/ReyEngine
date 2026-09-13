using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M722: registers characters in a map's own bin (<c>data/maps/shipping/mapNNN/mapNNN.bin</c>) so the game
/// preloads them.
///
/// <para><b>Why a placement is not enough.</b> A scenery character placed in the materials bin names its
/// record and skin, and that is all the editor wrote. The game only preloads characters its <c>Map</c> object
/// lists: <c>characterLists</c> links a handful of <c>MapCharacterList</c> objects, and each one's
/// <c>characters</c> links <c>Characters/&lt;Name&gt;</c>. A placed character missing from every list is
/// drawn through the static path - on a skinned material that asks for a vertex shader without
/// <c>NUM_BLEND_WEIGHTS</c>, which no skinned permutation has, and raises "Missing shader constant
/// WORLD_MATRIX", which no skinned shader declares. The URF prop on the Halloween Map453 did exactly that
/// and never appeared.</para>
///
/// <para><b>The shape, measured on Map453.</b> Nine lists, 155 links, 126 distinct characters, and every one
/// of the 126 has its own <c>"Characters/&lt;Name&gt;" = Character { name }</c> object in the same bin - no
/// listed character lacks one and no such object is unlisted. All 19 scenery characters placed on the map
/// are in one list (<c>0x2bf6fd19</c>, 23 entries). So a character is registered with both halves: the
/// link, appended to the list that already holds most of the characters placed on the map, and the
/// <c>Character</c> object when the bin does not have it.</para>
/// </summary>
public static class MapCharacterListWriter
{
    private static readonly uint MapClass = HashAlgorithms.Fnv1a("Map");
    private static readonly uint ListClass = HashAlgorithms.Fnv1a("MapCharacterList");
    private static readonly uint CharacterClass = HashAlgorithms.Fnv1a("Character");
    private static readonly uint F_characterLists = HashAlgorithms.Fnv1a("characterLists");
    private static readonly uint F_characters = HashAlgorithms.Fnv1a("characters");
    private static readonly uint F_name = HashAlgorithms.Fnv1a("name");

    /// <summary>The object a character is listed by: <c>Characters/&lt;Name&gt;</c>.</summary>
    public static uint CharacterHash(string character) => HashAlgorithms.Fnv1a("Characters/" + character);

    /// <summary>Every character the map's lists preload, as <see cref="CharacterHash"/> values.</summary>
    public static HashSet<uint> Listed(byte[] mapBin)
    {
        var tree = SafeBinTree.Parse(mapBin);
        return Lists(tree).SelectMany(l => Links(l.Characters)).ToHashSet();
    }

    /// <summary>
    /// Make sure each of <paramref name="characters"/> is preloaded by the map. <paramref name="placedOnMap"/>
    /// is every character name the map places; the list holding most of them receives the new links (ties go
    /// to the longer list, then to the earlier one). Returns the bin unchanged when every character is already
    /// listed, the new bytes when some were added, or null with <paramref name="error"/> when the bin has no
    /// Map object with character lists to add to.
    /// </summary>
    public static byte[]? Register(byte[] mapBin, IEnumerable<string> characters, IEnumerable<string> placedOnMap,
        out IReadOnlyList<string> added, out uint listHash, out string? error)
    {
        added = Array.Empty<string>();
        listHash = 0;
        error = null;

        BinTree tree;
        try
        {
            tree = SafeBinTree.Parse(mapBin);
            // a lossy read would drop whatever failed to parse when the file is written back
            SafeBinTree.ThrowIfLossy(tree, "character list edits");
        }
        catch (Exception ex) { error = $"could not parse the map bin: {ex.Message}"; return null; }

        var lists = Lists(tree);
        if (lists.Count == 0)
        {
            error = "the map bin has no Map object whose characterLists name a MapCharacterList with characters.";
            return null;
        }

        var listed = lists.SelectMany(l => Links(l.Characters)).ToHashSet();
        var missing = characters
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .GroupBy(CharacterHash).Select(g => g.First())
            .Where(c => !listed.Contains(CharacterHash(c)))
            .ToList();
        if (missing.Count == 0) return mapBin;

        var placed = placedOnMap.Select(CharacterHash).ToHashSet();
        var target = lists
            .Select((l, order) => (List: l, Order: order, Overlap: Links(l.Characters).Count(placed.Contains)))
            .OrderByDescending(x => x.Overlap)
            .ThenByDescending(x => x.List.Characters.Elements.Count)
            .ThenBy(x => x.Order)
            .First().List;

        var elements = target.Characters.Elements.ToList();
        elements.AddRange(missing.Select(c => (BinTreeProperty)new BinTreeObjectLink(0, CharacterHash(c))));
        // keep the wire form the list already has: list2 is an unordered container (0x81), and a wrong tag is
        // silently dropped by the client
        target.Object.Properties[F_characters] = target.Characters is BinTreeUnorderedContainer
            ? new BinTreeUnorderedContainer(F_characters, BinPropertyType.ObjectLink, elements)
            : new BinTreeContainer(F_characters, BinPropertyType.ObjectLink, elements);

        foreach (var c in missing)
        {
            uint hash = CharacterHash(c);
            if (tree.Objects.ContainsKey(hash)) continue;
            tree.Objects[hash] = new BinTreeObject(hash, CharacterClass, new BinTreeProperty[]
            {
                new BinTreeString(F_name, c),
            });
        }

        byte[] result;
        try
        {
            using var ms = new MemoryStream();
            tree.Write(ms);
            result = ms.ToArray();
        }
        catch (Exception ex) { error = $"could not write the map bin: {ex.Message}"; return null; }

        // prove the result reads back with every character listed
        try
        {
            var reread = Listed(result);
            if (missing.FirstOrDefault(c => !reread.Contains(CharacterHash(c))) is { } lost)
            { error = $"'{lost}' is not listed in the rewritten map bin."; return null; }
        }
        catch (Exception ex) { error = $"the rewritten map bin no longer parses: {ex.Message}"; return null; }

        added = missing;
        listHash = target.Object.PathHash;
        return result;
    }

    private sealed record CharacterList(BinTreeObject Object, BinTreeContainer Characters);

    private static List<CharacterList> Lists(BinTree tree)
    {
        var result = new List<CharacterList>();
        var seen = new HashSet<uint>();
        foreach (var map in tree.Objects.Values.Where(o => o.ClassHash == MapClass))
        {
            if (map.Properties.GetValueOrDefault(F_characterLists) is not BinTreeContainer links) continue;
            foreach (uint link in Links(links))
            {
                if (!seen.Add(link)) continue;
                if (!tree.Objects.TryGetValue(link, out var list) || list.ClassHash != ListClass) continue;
                if (list.Properties.GetValueOrDefault(F_characters) is BinTreeContainer chars)
                    result.Add(new CharacterList(list, chars));
            }
        }
        return result;
    }

    private static IEnumerable<uint> Links(BinTreeContainer container) =>
        container.Elements.OfType<BinTreeObjectLink>().Select(l => l.Value);
}
