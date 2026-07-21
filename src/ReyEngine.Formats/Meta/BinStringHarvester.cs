using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M129: collect every string value in a bin — spawn tables, character names, asset paths, all of it.
/// Used to answer "does the current map's data mention X anywhere?" for the unused-bin analysis
/// (map bins reference characters by exact name strings, not by dependency lists).
/// </summary>
public static class BinStringHarvester
{
    public static void Collect(BinTree tree, ICollection<string> into)
    {
        foreach (var o in tree.Objects.Values)
            foreach (var p in o.Properties.Values)
                Walk(p, into);
        foreach (var dep in tree.Dependencies) into.Add(dep);
    }

    private static void Walk(BinTreeProperty p, ICollection<string> into)
    {
        switch (p)
        {
            case BinTreeString s when s.Value.Length > 0: into.Add(s.Value); break;
            case BinTreeContainer c: foreach (var el in c.Elements) Walk(el, into); break;
            case BinTreeStruct st: foreach (var v in st.Properties.Values) Walk(v, into); break;
            case BinTreeOptional { Value: { } inner }: Walk(inner, into); break;
            case BinTreeMap m: foreach (var (k, v) in m) { Walk(k, into); Walk(v, into); } break;
        }
    }
}
