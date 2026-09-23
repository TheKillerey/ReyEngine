using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// Deep-copies a LeagueToolkit BinTree property, so a container element (or struct) can be
/// duplicated and then edited. Used by array/struct element editing (M10) — cloning an existing
/// element guarantees the new one matches the schema (class hashes, field names, address modes…).
/// </summary>
public static class BinTreeCloner
{
    public static BinTreeProperty Clone(BinTreeProperty p, uint nameHash) => p switch
    {
        BinTreeBool v => new BinTreeBool(nameHash, v.Value),
        BinTreeBitBool v => new BinTreeBitBool(nameHash, v.Value),
        BinTreeI8 v => new BinTreeI8(nameHash, v.Value),
        BinTreeU8 v => new BinTreeU8(nameHash, v.Value),
        BinTreeI16 v => new BinTreeI16(nameHash, v.Value),
        BinTreeU16 v => new BinTreeU16(nameHash, v.Value),
        BinTreeI32 v => new BinTreeI32(nameHash, v.Value),
        BinTreeU32 v => new BinTreeU32(nameHash, v.Value),
        BinTreeI64 v => new BinTreeI64(nameHash, v.Value),
        BinTreeU64 v => new BinTreeU64(nameHash, v.Value),
        BinTreeF32 v => new BinTreeF32(nameHash, v.Value),
        BinTreeVector2 v => new BinTreeVector2(nameHash, v.Value),
        BinTreeVector3 v => new BinTreeVector3(nameHash, v.Value),
        BinTreeVector4 v => new BinTreeVector4(nameHash, v.Value),
        BinTreeColor v => new BinTreeColor(nameHash, v.Value),
        BinTreeString v => new BinTreeString(nameHash, v.Value),
        BinTreeHash v => new BinTreeHash(nameHash, v.Value),
        // M206: every map placeable carries a transform, so without this NO placement could be cloned -
        // the first clone test failed on it. Matrix4x4 is a value type, so the copy is inherently deep.
        BinTreeMatrix44 v => new BinTreeMatrix44(nameHash, v.Value),
        BinTreeObjectLink v => new BinTreeObjectLink(nameHash, v.Value),
        BinTreeWadChunkLink v => new BinTreeWadChunkLink(nameHash, v.Value),
        // Derived types must precede their base (UnorderedContainer : Container, Embedded : Struct).
        BinTreeUnorderedContainer v => new BinTreeUnorderedContainer(nameHash, v.ElementType, v.Elements.Select(e => Clone(e, 0))),
        BinTreeContainer v => new BinTreeContainer(nameHash, v.ElementType, v.Elements.Select(e => Clone(e, 0))),
        BinTreeEmbedded v => new BinTreeEmbedded(nameHash, v.ClassHash, v.Properties.Select(kv => Clone(kv.Value, kv.Key))),
        BinTreeStruct v => new BinTreeStruct(nameHash, v.ClassHash, v.Properties.Select(kv => Clone(kv.Value, kv.Key))),
        BinTreeOptional v => v.Value is null ? EmptyOptional(nameHash, v.ValueType) : new BinTreeOptional(nameHash, Clone(v.Value, 0)),
        // M123: maps (shaderMacros on every StaticMaterialDef) — keys and values cloned pairwise
        BinTreeMap v => new BinTreeMap(nameHash, v.KeyType, v.ValueType,
            v.Select(kv => new KeyValuePair<BinTreeProperty, BinTreeProperty>(Clone(kv.Key, 0), Clone(kv.Value, 0)))),
        _ => throw new NotSupportedException($"Cannot duplicate a {p.Type} property yet."),
    };

    private static readonly System.Reflection.FieldInfo? OptionalValueType =
        typeof(BinTreeOptional).GetField("_valueType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>
    /// M756: an EMPTY option that keeps its element type. LeagueToolkit's only public constructor infers the
    /// type from the value, so an empty clone was written with element type None where Riot writes the
    /// real one (emitterLinger is option&lt;f32&gt; whether or not it holds a value) - every duplicated emitter
    /// shipped that divergence. The type is set on the private field the reader fills; should a
    /// LeagueToolkit update rename it, the clone is refused rather than silently retyped.
    /// </summary>
    private static BinTreeOptional EmptyOptional(uint nameHash, BinPropertyType valueType)
    {
        var o = new BinTreeOptional(nameHash, null);
        if (o.ValueType == valueType) return o;
        if (OptionalValueType is null)
            throw new NotSupportedException("Cannot duplicate an empty option with its element type on this LeagueToolkit version.");
        OptionalValueType.SetValue(o, valueType);
        return o;
    }

    /// <summary>Can every part of this property be deep-copied?</summary>
    public static bool CanClone(BinTreeProperty p)
    {
        try { Clone(p, 0); return true; }
        catch (NotSupportedException) { return false; }
    }
}
