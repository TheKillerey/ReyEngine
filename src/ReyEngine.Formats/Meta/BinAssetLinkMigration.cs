using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M590: bring a mod's asset references onto the wire form the installed patch uses.
///
/// <para>Patch 16.17 changed asset paths from <c>String</c> to <c>WadChunkLink</c>. Measured on the
/// installed patch: <c>texturePath</c> is 11,695 links and <b>zero</b> strings, <c>mAnimationFilePath</c>
/// 8,983 and zero, <c>oldAsset</c> 3,432 and zero — while <c>texture</c> (2,863 / 70,402) and
/// <c>TextureName</c> (119 / 11,962) are genuinely BOTH and must not be touched.</para>
///
/// <para><b>Why a rebase alone does not fix it.</b> A three-way merge carries the mod's own objects
/// forward verbatim, so after rebasing a 16.10 map onto 16.17 its materials still held 1,029 String
/// texturePaths against 254 links inherited from Riot's untouched objects — one bin, two eras. The client
/// skips a property whose wire form disagrees with the schema rather than reporting it (M507, where a
/// mistagged container made it miss MULTIPLY_ALPHA entirely), so those materials lose their textures with
/// nothing said.</para>
///
/// <para><b>The field list is not hardcoded.</b> It is derived from the patch's OWN copy of the same bin:
/// a field that appears only as a link there is one this file must write as a link, and a field that
/// appears both ways is left alone. That calibrates per bin, needs no corpus survey at run time, and keeps
/// working when Riot flips the next field.</para>
///
/// <para>The conversion is meaning-preserving: the stored hash is <c>WadPath(the old string)</c>, which is
/// exactly what the loader derived from that string anyway.</para>
/// </summary>
public static class BinAssetLinkMigration
{
    /// <summary>Which field name hashes <paramref name="reference"/> writes ONLY as a WadChunkLink.</summary>
    public static HashSet<uint> LinkOnlyFields(BinTree reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var asLink = new HashSet<uint>();
        var asString = new HashSet<uint>();
        foreach (var o in reference.Objects.Values) Survey(o.Properties.Values, asLink, asString);
        asLink.ExceptWith(asString);
        return asLink;
    }

    /// <summary>
    /// Rewrite every String on a link-only field into the WadChunkLink form.
    /// </summary>
    /// <returns>How many properties were converted.</returns>
    public static int Apply(BinTree tree, IReadOnlySet<uint> linkOnlyFields)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(linkOnlyFields);
        if (linkOnlyFields.Count == 0) return 0;

        int converted = 0;
        foreach (var o in tree.Objects.Values) Convert(o.Properties, linkOnlyFields, ref converted);
        return converted;
    }

    /// <summary>Align <paramref name="tree"/> with the wire forms <paramref name="reference"/> uses.</summary>
    public static int AlignWith(BinTree tree, BinTree reference) =>
        Apply(tree, LinkOnlyFields(reference));

    // ---- internals --------------------------------------------------------------------------------

    private static void Survey(IEnumerable<BinTreeProperty> props, HashSet<uint> asLink, HashSet<uint> asString)
    {
        foreach (var p in props)
        {
            if (p.NameHash != 0)
            {
                if (p is BinTreeWadChunkLink) asLink.Add(p.NameHash);
                else if (p is BinTreeString) asString.Add(p.NameHash);
            }
            switch (p)
            {
                case BinTreeStruct s: Survey(s.Properties.Values, asLink, asString); break;
                case BinTreeContainer c: Survey(c.Elements, asLink, asString); break;
                case BinTreeOptional o when o.Value is not null: Survey(new[] { o.Value }, asLink, asString); break;
                case BinTreeMap m: Survey(m.Values, asLink, asString); break;
            }
        }
    }

    /// <summary>A property dictionary: entries can be replaced in place, keyed by name hash.</summary>
    private static void Convert(IDictionary<uint, BinTreeProperty> props, IReadOnlySet<uint> fields, ref int converted)
    {
        foreach (uint key in props.Keys.ToList())
        {
            var p = props[key];
            if (p is BinTreeString s && fields.Contains(p.NameHash))
            {
                props[key] = Link(p.NameHash, s.Value);
                converted++;
                continue;
            }
            Descend(p, fields, ref converted);
        }
    }

    /// <summary>A container's elements carry NO name hash of their own, so an element is only ever
    /// descended into — never converted here. The named property that owns them is handled above.</summary>
    private static void Descend(BinTreeProperty p, IReadOnlySet<uint> fields, ref int converted)
    {
        switch (p)
        {
            case BinTreeStruct s:
                Convert(s.Properties, fields, ref converted);
                break;
            case BinTreeContainer c:
                for (int i = 0; i < c.Elements.Count; i++) Descend(c.Elements[i], fields, ref converted);
                break;
            case BinTreeOptional o when o.Value is not null:
                Descend(o.Value, fields, ref converted);
                break;
            case BinTreeMap m:
                foreach (var v in m.Values) Descend(v, fields, ref converted);
                break;
        }
    }

    private static BinTreeWadChunkLink Link(uint nameHash, string value) =>
        new(nameHash, string.IsNullOrWhiteSpace(value) ? 0UL
            : BinTexturePath.TryParseHex(value, out ulong raw) ? raw
            : HashAlgorithms.WadPath(value));
}
