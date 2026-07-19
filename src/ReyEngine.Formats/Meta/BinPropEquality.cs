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
        a.ClassHash == b.ClassHash && DictsEqual(a.Properties, b.Properties);

    public static bool DictsEqual(IReadOnlyDictionary<uint, LtProp> a, IReadOnlyDictionary<uint, LtProp> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var other) || !PropsEqual(v, other)) return false;
        return true;
    }

    public static bool PropsEqual(LtProp? a, LtProp? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.GetType() != b.GetType() || a.NameHash != b.NameHash) return false;

        switch (a)
        {
            case BinTreeOptional oa:
                return PropsEqual(oa.Value, ((BinTreeOptional)b).Value);   // the library NREs here

            case BinTreeStruct sa:   // also covers BinTreeEmbedded (same runtime type enforced above)
            {
                var sb = (BinTreeStruct)b;
                return sa.ClassHash == sb.ClassHash && DictsEqual(sa.Properties, sb.Properties);
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
