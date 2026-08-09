using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M414: an EMPTY container or map property is not the same as an absent one, and Riot never writes one.
///
/// <para>Measured across every <c>*.wad.client</c> the game ships - <b>33,645 StaticMaterialDef objects</b> -
/// there are <b>zero</b> empty <c>switches</c>, <c>paramValues</c>, <c>samplerValues</c>, <c>techniques</c>
/// or <c>shaderMacros</c>. Riot omits the field instead. That is a 0-in-33,645 convention, not a
/// coincidence.</para>
///
/// <para>It is also a confirmed crash. The Map453 mod shipped exactly one empty container - <c>switches</c>
/// on <c>LegacyPort/map11/Grass_31861292fa7a</c> - and the game died on a null dereference 0.66 s into
/// loading, with that material's name the ONLY asset string still resident in the crash dump. Removing it
/// is the difference between a map that loads and one that does not.</para>
///
/// <para>So this is not cosmetic tidying: writing a shape the engine is never given is how a mod crashes.
/// Applied on save rather than at every construction site, because the sites are many and a save is the
/// one place everything passes through.</para>
/// </summary>
public static class BinEmptyProperty
{
    /// <summary>Remove every empty container/map property, at any depth. Returns how many were removed.</summary>
    public static int Strip(BinTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        int removed = 0;
        foreach (var (_, o) in tree.Objects)
        {
            foreach (uint key in o.Properties.Where(kv => IsEmpty(kv.Value)).Select(kv => kv.Key).ToList())
            {
                o.Properties.Remove(key);
                removed++;
            }
            foreach (var (_, p) in o.Properties) removed += Descend(p, 0);
        }
        return removed;
    }

    /// <summary>Is this an empty container or map? Optionals are NOT stripped - an authored "no value"
    /// optional is a real, distinguishable state that Riot does ship.</summary>
    public static bool IsEmpty(BinTreeProperty p) => p switch
    {
        BinTreeContainer c => c.Elements.Count == 0,   // covers BinTreeUnorderedContainer
        BinTreeMap m => !m.Any(),
        _ => false,
    };

    private static int Descend(BinTreeProperty p, int depth)
    {
        if (depth > 12) return 0;
        int removed = 0;
        switch (p)
        {
            case BinTreeStruct s:      // covers BinTreeEmbedded
                foreach (uint key in s.Properties.Where(kv => IsEmpty(kv.Value)).Select(kv => kv.Key).ToList())
                {
                    s.Properties.Remove(key);
                    removed++;
                }
                foreach (var (_, v) in s.Properties) removed += Descend(v, depth + 1);
                break;
            case BinTreeContainer c:
                foreach (var e in c.Elements) removed += Descend(e, depth + 1);
                break;
            case BinTreeOptional o:
                if (o.Value is { } ov) removed += Descend(ov, depth + 1);
                break;
            case BinTreeMap m:
                foreach (var kv in m) removed += Descend(kv.Value, depth + 1);
                break;
        }
        return removed;
    }
}
