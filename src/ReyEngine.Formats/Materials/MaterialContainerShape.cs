using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Materials;

/// <summary>
/// M507: which WIRE FORM each StaticMaterialDef container field takes.
///
/// <para>A .bin container is tagged either <c>Container</c> (0x80, "list") or <c>UnorderedContainer</c>
/// (0x81, "list2"). The two parse identically here and round-trip identically — and the CLIENT silently
/// drops a property whose tag disagrees with the schema. Dropped is not "renders oddly": the material
/// simply behaves as though the field were never authored.</para>
///
/// <para>That is how a ported Map453 crashed on load. The editor's Add Switch wrote <c>switches</c> as a
/// plain Container, the client skipped it, MULTIPLY_ALPHA fell back to the shader's default of 0, and the
/// material's PREMULTIPLIED_ALPHA=1 macro then asked DefaultEnv_Flat_AlphaTest for a permutation Riot
/// never cooked:</para>
/// <code>
/// Unable to find correct hash for shader 'ASSETS/.../DefaultEnv_Flat_AlphaTest.ps-dx11' in wad.
/// Pass Defines: DISABLE_DEPTH_FOG=1 FEATURE_MASKED=1 MULTIPLY_ALPHA=0 NO_BAKED_LIGHTING=1 PREMULTIPLIED_ALPHA=1
/// </code>
/// <para>Every one of the 256 cooked permutations of that shader that carries PREMULTIPLIED_ALPHA=1 also
/// carries MULTIPLY_ALPHA=1. Nothing in the material was wrong as WE read it; the tag on one container
/// meant the client read a different material.</para>
///
/// <para>Censused over the 18 shipped map wads (12,955 bins), where Riot is perfectly consistent:</para>
/// <list type="table">
///   <item><term>switches</term><description>UnorderedContainer — 6,561 of 6,561</description></item>
///   <item><term>samplerValues</term><description>UnorderedContainer — 10,917 of 10,917</description></item>
///   <item><term>techniques</term><description>Container — 10,982 of 10,982</description></item>
/// </list>
/// </summary>
public static class MaterialContainerShape
{
    /// <summary>Field name -> whether Riot tags it UnorderedContainer (list2) rather than Container (list).</summary>
    private static readonly (string Field, bool Unordered, string Evidence)[] Fields =
    {
        ("switches",      true,  "6,561 of 6,561 shipped"),
        ("samplerValues", true,  "10,917 of 10,917 shipped"),
        ("paramValues",   true,  "authored beside samplerValues in the same form"),
        ("techniques",    false, "10,982 of 10,982 shipped"),
    };

    /// <summary>True when this field must be an <see cref="BinTreeUnorderedContainer"/>; false when it must
    /// be a plain <see cref="BinTreeContainer"/>; null when we have no measurement for it and therefore say
    /// nothing.</summary>
    public static bool? IsUnordered(string field)
    {
        foreach (var (name, unordered, _) in Fields)
            if (name.Equals(field, StringComparison.OrdinalIgnoreCase)) return unordered;
        return null;
    }

    public static string? Evidence(string field)
    {
        foreach (var (name, _, evidence) in Fields)
            if (name.Equals(field, StringComparison.OrdinalIgnoreCase)) return evidence;
        return null;
    }

    /// <summary>Create the container Riot would write for this field.</summary>
    public static BinTreeContainer Create(string field, BinPropertyType elementType,
        IEnumerable<BinTreeProperty> elements)
    {
        uint hash = HashAlgorithms.Fnv1a(field);
        return IsUnordered(field) == true
            ? new BinTreeUnorderedContainer(hash, elementType, elements)
            : new BinTreeContainer(hash, elementType, elements);
    }

    /// <summary>Does this property carry the wire form Riot uses for that field? Null = no opinion.</summary>
    public static bool? Matches(string field, BinTreeProperty property)
    {
        if (IsUnordered(field) is not { } unordered) return null;
        if (property is not BinTreeContainer container) return null;
        // BinTreeUnorderedContainer derives from BinTreeContainer, so the test is on the exact type.
        return (container is BinTreeUnorderedContainer) == unordered;
    }

    /// <summary>Every material container field we have a measured form for.</summary>
    public static IEnumerable<string> KnownFields => Fields.Select(f => f.Field);

    /// <summary>
    /// Rewrite mis-tagged material containers in place, returning what was changed.
    ///
    /// <para>The elements are MOVED, not rebuilt: the values, their order and their own wire forms are
    /// untouched, so this only changes the one byte per container that decides whether the client reads
    /// the field at all.</para>
    /// </summary>
    public static IReadOnlyList<string> Repair(BinTree tree, uint materialClassHash)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var fixedUp = new List<string>();

        foreach (var (_, obj) in tree.Objects)
        {
            if (obj.ClassHash != materialClassHash) continue;
            foreach (string field in KnownFields)
            {
                uint hash = HashAlgorithms.Fnv1a(field);
                if (!obj.Properties.TryGetValue(hash, out var prop)) continue;
                if (prop is not BinTreeContainer container) continue;
                if (Matches(field, prop) != false) continue;

                var elements = container.Elements.ToList();
                obj.Properties[hash] = Create(field, container.ElementType, elements);
                fixedUp.Add($"{Name(obj)}: {field} was {prop.GetType().Name}");
            }
        }
        return fixedUp;
    }

    private static string Name(BinTreeObject obj)
    {
        uint nameHash = HashAlgorithms.Fnv1a("name");
        return obj.Properties.TryGetValue(nameHash, out var p) && p is BinTreeString s
            ? s.Value
            : $"0x{obj.PathHash:x8}";
    }
}
