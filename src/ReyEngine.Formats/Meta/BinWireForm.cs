using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace ReyEngine.Formats.Meta;

/// <summary>One property that kept its name and value shape but changed its WIRE TAG.</summary>
/// <param name="Path">Where it sits, e.g. <c>Maps/Shipping/Map11/MapSkins/Default.mGrassTintTexture</c>.</param>
/// <param name="Before">The tag Riot shipped.</param>
/// <param name="After">The tag the edit produced.</param>
public readonly record struct BinWireFormChange(string Path, string Before, string After)
{
    public override string ToString() => $"{Path}: {Before} -> {After}";
}

/// <summary>
/// M649: does an edited bin still describe its properties the way the client reads them?
///
/// <para>A .bin property carries a one-byte type tag, and the client dispatches on it. Give a field the
/// wrong tag and the client does not complain - it SKIPS the property, and the map loads with the field
/// silently absent. This project has now been bitten by that three separate times, each looking like a
/// different bug: a List2 written as an ordinary container made the client drop the property whole; a
/// container of Embedded (0x83) elements written as pointers (0x82) loaded everywhere and rendered
/// nothing; and 16.17 turned texture paths into WadChunkLink, so a path re-typed as a String returned
/// nothing at all. All three are the same mistake and none of them is visible in a diff of the values.</para>
///
/// <para>So this compares the tags rather than the values. It is deliberately structural: no schema is
/// consulted, because the authority on what a field should be is the file Riot shipped, and comparing
/// against that catches every tag the client can skip - including fields whose declared type this build
/// has never heard of.</para>
/// </summary>
public static class BinWireForm
{
    /// <summary>Every property whose wire tag differs between the two trees. Objects, properties and
    /// container elements that exist in only one side are NOT reported: adding or removing a field is a
    /// visible edit, while re-tagging one is the invisible mistake this looks for.</summary>
    /// <param name="limit">Stop after this many, so a wholesale re-tag cannot flood a status line.</param>
    public static IReadOnlyList<BinWireFormChange> Compare(BinTree before, BinTree after,
        Func<uint, string?>? resolve = null, int limit = 50)
    {
        var found = new List<BinWireFormChange>();
        foreach (var (hash, original) in before.Objects)
        {
            if (found.Count >= limit) break;
            if (!after.Objects.TryGetValue(hash, out var edited)) continue;
            string path = Name(resolve, hash);
            foreach (var (fieldHash, was) in original.Properties)
            {
                if (found.Count >= limit) break;
                if (edited.Properties.TryGetValue(fieldHash, out var now))
                    Walk($"{path}.{Name(resolve, fieldHash)}", was, now, found, resolve, limit);
            }
        }
        return found;
    }

    private static void Walk(string path, BinTreeProperty before, BinTreeProperty after,
        List<BinWireFormChange> found, Func<uint, string?>? resolve, int limit)
    {
        if (found.Count >= limit) return;
        if (before.Type != after.Type)
        {
            found.Add(new BinWireFormChange(path, before.Type.ToString(), after.Type.ToString()));
            return;   // the shapes have diverged; anything below would be noise
        }

        switch (before)
        {
            // A container's ELEMENT type is part of its own tag pair, and an element re-tagged inside an
            // otherwise identical container is exactly the Embedded-vs-pointer case.
            case BinTreeContainer b when after is BinTreeContainer a:
                if (b.ElementType != a.ElementType)
                {
                    found.Add(new BinWireFormChange(path + "[]", b.ElementType.ToString(), a.ElementType.ToString()));
                    return;
                }
                for (int i = 0; i < Math.Min(b.Elements.Count, a.Elements.Count); i++)
                    Walk($"{path}[{i}]", b.Elements[i], a.Elements[i], found, resolve, limit);
                return;

            case BinTreeStruct b2 when after is BinTreeStruct a2:
                if (b2.ClassHash != a2.ClassHash)
                {
                    found.Add(new BinWireFormChange(path, "class " + Name(resolve, b2.ClassHash), "class " + Name(resolve, a2.ClassHash)));
                    return;
                }
                foreach (var (fieldHash, was) in b2.Properties)
                    if (a2.Properties.TryGetValue(fieldHash, out var now))
                        Walk($"{path}.{Name(resolve, fieldHash)}", was, now, found, resolve, limit);
                return;

            case BinTreeOptional b3 when after is BinTreeOptional a3:
                if (b3.Value is { } bv && a3.Value is { } av) Walk(path, bv, av, found, resolve, limit);
                return;

            case BinTreeMap b4 when after is BinTreeMap a4:
                if (b4.KeyType != a4.KeyType)
                    found.Add(new BinWireFormChange(path + " key", b4.KeyType.ToString(), a4.KeyType.ToString()));
                else if (b4.ValueType != a4.ValueType)
                    found.Add(new BinWireFormChange(path + " value", b4.ValueType.ToString(), a4.ValueType.ToString()));
                return;
        }
    }

    private static string Name(Func<uint, string?>? resolve, uint hash) =>
        resolve?.Invoke(hash) is { Length: > 0 } n ? n : $"0x{hash:x8}";

    /// <summary>A sentence for a status line, or null when nothing was re-tagged.</summary>
    public static string? Describe(IReadOnlyList<BinWireFormChange> changes, int show = 4)
    {
        if (changes.Count == 0) return null;
        string list = string.Join("; ", changes.Take(show));
        string more = changes.Count > show ? $" (+{changes.Count - show} more)" : "";
        return $"{changes.Count} propert{(changes.Count == 1 ? "y" : "ies")} changed wire type: {list}{more}. "
             + "The client SKIPS a property whose tag it does not expect - it will load and the field will "
             + "simply be missing.";
    }
}
