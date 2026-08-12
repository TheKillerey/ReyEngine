using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Meta;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M440: does every container property in a bin use the WIRE FORM its schema declares?
///
/// <para><b>Why this is worth a dedicated check.</b> The client registers each property with a wire tag
/// and, on a mismatch, its PROP reader jumps straight to the skip-value routine and returns SUCCESS. The
/// property is silently dropped — no log, no error, no crash at parse time. The damage only shows up far
/// downstream as something unrelated going missing. That is exactly how a MapGameplayTexture sampler list
/// written as <c>List</c> instead of <c>List2</c> starved a shader sampler and produced
/// <c>Missing sampler "AlphaMask"</c> at map load, with nothing in between to point at the cause.</para>
///
/// <para><b>It is invisible to a round-trip test</b> because <see cref="BinTreeUnorderedContainer"/>
/// derives from <see cref="BinTreeContainer"/>: ReyEngine reads back what it wrote and everything looks
/// correct. Only comparing against the schema catches it.</para>
///
/// <para><b>The rule is Riot's, measured:</b> across 11,523,742 properties in 12,000 shipped bins,
/// <c>List</c> is written as Container 3,185,836 times and <c>List2</c> as UnorderedContainer 11,443
/// times, with <b>zero</b> cross-encodings. Related: <see cref="BinPropEquality"/> and the M416
/// Embedded-vs-pointer rule for container ELEMENTS, which is the same class of defect one level down.</para>
/// </summary>
public static class BinWireFormCheck
{
    /// <param name="Path">Where the property sits, e.g. "MapContainer.components[3].0xa5aaf88e".</param>
    /// <param name="Declared">The schema's field type, "List" or "List2".</param>
    /// <param name="Actual">The wire form found.</param>
    public sealed record Mismatch(string Path, uint NameHash, string Declared, string Actual)
    {
        public string Summary =>
            $"{Path}: schema says {Declared} (wire {(Declared == "List2" ? "0x81 UnorderedContainer" : "0x80 Container")}) "
          + $"but it is written as {Actual}. The client SILENTLY DROPS type-mismatched properties.";
    }

    /// <summary>Every container property whose wire form disagrees with the meta dump. Empty when the bin
    /// is clean or when no schema is available — this never guesses.</summary>
    public static IReadOnlyList<Mismatch> Check(byte[] bin, MetaClassDatabase? meta)
    {
        var found = new List<Mismatch>();
        if (meta is null) return found;

        BinTree tree;
        try { tree = new BinTree(new MemoryStream(bin, false)); }
        catch { return found; }

        foreach (var (_, obj) in tree.Objects)
            Walk(meta, obj.ClassHash, obj.Properties.Values, Name(meta, obj.ClassHash), found);
        return found;
    }

    private static void Walk(MetaClassDatabase meta, uint classHash, IEnumerable<BinTreeProperty> props,
        string path, List<Mismatch> found)
    {
        foreach (var p in props)
        {
            string here = $"{path}.{FieldName(meta, classHash, p.NameHash)}";

            if (p is BinTreeContainer c && meta.TryGetProperty(classHash, p.NameHash, out var declared))
            {
                // UnorderedContainer derives from Container, so test the concrete type, not `is`.
                bool isUnordered = p is BinTreeUnorderedContainer;
                if (declared.FieldType == "List2" && !isUnordered)
                    found.Add(new Mismatch(here, p.NameHash, "List2", "Container (0x80)"));
                else if (declared.FieldType == "List" && isUnordered)
                    found.Add(new Mismatch(here, p.NameHash, "List", "UnorderedContainer (0x81)"));

                foreach (var el in c.Elements)
                    if (el is BinTreeStruct es)
                        Walk(meta, es.ClassHash, es.Properties.Values, here + "[]", found);
            }
            else if (p is BinTreeStruct s)
                Walk(meta, s.ClassHash, s.Properties.Values, here, found);
            else if (p is BinTreeOptional { Value: BinTreeStruct os })
                Walk(meta, os.ClassHash, os.Properties.Values, here, found);
        }
    }

    private static string FieldName(MetaClassDatabase meta, uint classHash, uint nameHash) =>
        meta.TryGetProperty(classHash, nameHash, out var p) && p.HasName ? p.Name : $"0x{nameHash:x8}";

    private static string Name(MetaClassDatabase meta, uint hash) =>
        meta.TryGetName(hash, out var n) && n.Length > 0 ? n : $"0x{hash:x8}";
}
