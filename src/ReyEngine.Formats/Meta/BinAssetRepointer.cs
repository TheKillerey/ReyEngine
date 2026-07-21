using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M127: repoint asset-path strings on a live BinTree — every string property equal to
/// <c>fromPath</c> (case-insensitive, anywhere in the tree) is set to <c>toPath</c>.
/// The standard fix for dead skin-variant references (Riot vaults map skins, the suffixed
/// file disappears, the base variant stays).
/// </summary>
public static class BinAssetRepointer
{
    /// <summary>Returns how many references were changed.</summary>
    public static int Repoint(BinTree tree, string fromPath, string toPath)
    {
        int hits = 0;
        foreach (var o in tree.Objects.Values)
            foreach (var p in o.Properties.Values)
                Walk(p, fromPath, toPath, ref hits);
        return hits;
    }

    private static void Walk(BinTreeProperty p, string from, string to, ref int hits)
    {
        switch (p)
        {
            case BinTreeString s when string.Equals(s.Value, from, StringComparison.OrdinalIgnoreCase):
                s.Value = to; hits++; break;
            case BinTreeContainer c:            // covers BinTreeUnorderedContainer
                foreach (var el in c.Elements) Walk(el, from, to, ref hits); break;
            case BinTreeStruct st:              // covers BinTreeEmbedded
                foreach (var v in st.Properties.Values) Walk(v, from, to, ref hits); break;
            case BinTreeOptional { Value: { } inner }:
                Walk(inner, from, to, ref hits); break;
            case BinTreeMap m:
                foreach (var (k, v) in m) { Walk(k, from, to, ref hits); Walk(v, from, to, ref hits); }
                break;
        }
    }

    /// <summary>The base-variant candidate for a skin-suffixed asset path:
    /// <c>Dir/Name.SUFFIX.ext</c> → <c>Dir/Name.ext</c>. Null when the filename has no middle token.</summary>
    public static string? BaseVariant(string path)
    {
        int lastDot = path.LastIndexOf('.');
        if (lastDot <= 0) return null;
        int prevDot = path.LastIndexOf('.', lastDot - 1);
        int lastSlash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        if (prevDot <= lastSlash) return null;
        return path[..prevDot] + path[lastDot..];
    }
}
