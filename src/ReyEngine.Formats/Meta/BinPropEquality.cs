using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LtProp = LeagueToolkit.Core.Meta.BinTreeProperty;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M97: deep structural equality for bin properties/objects. LeagueToolkit's own Equals throws
/// NullReferenceException on empty <see cref="BinTreeOptional"/>s (live map11 bins contain them), so the
/// merge/diff machinery routes ALL comparisons through this instead. Leaf primitives still use the
/// library's value equality (safe there); every composite type is compared structurally.
/// </summary>
public static class BinPropEquality
{
    public static bool ObjectsEqual(BinTreeObject a, BinTreeObject b) =>
        a.ClassHash == b.ClassHash && DictsEqual(a.Properties, b.Properties, a.ClassHash);

    public static bool DictsEqual(IReadOnlyDictionary<uint, LtProp> a, IReadOnlyDictionary<uint, LtProp> b) => DictsEqual(a, b, 0);

    /// <param name="ownerClass">The class the properties belong to. Patch 16.21 stores a StaticMaterialDef name as the XXH3 of the
    /// lowercased text where older bins hold the text, so those two forms of one name are the same value, not an edit.</param>
    public static bool DictsEqual(IReadOnlyDictionary<uint, LtProp> a, IReadOnlyDictionary<uint, LtProp> b, uint ownerClass)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
        {
            if (!b.TryGetValue(k, out var other)) return false;
            if (PropsEqual(v, other)) continue;
            if (BinWireNormalizer.IsMaterialName(ownerClass, k) && SameMaterialName(v, other)) continue;
            return false;
        }
        return true;
    }

    private static bool SameMaterialName(LtProp a, LtProp b)
    {
        if (a is BinTreeString s1 && b is BinTreeU64 h1) return BinWireNormalizer.NameHash(s1.Value) == h1.Value;
        if (a is BinTreeU64 h2 && b is BinTreeString s2) return BinWireNormalizer.NameHash(s2.Value) == h2.Value;
        return false;
    }


    public static bool PropsEqual(LtProp? a, LtProp? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        // a property compared on its own (the three-way merge walks an object's properties one by one) has no class around it; a
        // "name" that is a String on one side and exactly its own XXH3 on the other is the 16.20 / 16.21 forms of one value
        if (a.NameHash == b.NameHash && a.NameHash == BinWireNormalizer.NameField && SameMaterialName(a, b)) return true;
        if (a.GetType() != b.GetType() || a.NameHash != b.NameHash) return false;

        switch (a)
        {
            case BinTreeOptional oa:
                return PropsEqual(oa.Value, ((BinTreeOptional)b).Value);   // the library NREs here

            case BinTreeStruct sa:   // also covers BinTreeEmbedded (same runtime type enforced above)
            {
                var sb = (BinTreeStruct)b;
                return sa.ClassHash == sb.ClassHash && DictsEqual(sa.Properties, sb.Properties, sa.ClassHash);
            }

            case BinTreeContainer ca:
            {
                var cb = (BinTreeContainer)b;
                if (ca.Elements.Count != cb.Elements.Count) return false;
                for (int i = 0; i < ca.Elements.Count; i++)
                    if (!PropsEqual(ca.Elements[i], cb.Elements[i])) return false;
                return true;
            }

            case BinTreeMap ma:
            {
                var mb = (BinTreeMap)b;
                if (ma.Count != mb.Count) return false;
                // map keys are primitives (hash/int/string) whose library equality is safe
                var bPairs = mb.ToList();
                var used = new bool[bPairs.Count];
                foreach (var pair in ma)
                {
                    int hit = -1;
                    for (int i = 0; i < bPairs.Count; i++)
                        if (!used[i] && PropsEqual(pair.Key, bPairs[i].Key) && PropsEqual(pair.Value, bPairs[i].Value))
                        { hit = i; break; }
                    if (hit < 0) return false;
                    used[hit] = true;
                }
                return true;
            }

            default:
                return a.Equals(b);   // primitives / leaves — library value equality is safe here
        }
    }
}
