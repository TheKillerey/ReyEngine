using System.Runtime.CompilerServices;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>A value coerced to a shape, or the reason it does not coerce.</summary>
internal readonly struct Coerced
{
    private Coerced(PropValue? value, PropertySkipReason reason) { Value = value; Reason = reason; }

    public PropValue? Value { get; }

    public PropertySkipReason Reason { get; }

    public bool IsOk => Value is not null;

    public static implicit operator Coerced(PropValue value) => new(value, default);

    public static implicit operator Coerced(PropertySkipReason reason) => new(null, reason);
}

/// <summary>
/// M817: coercion, the reading of a literal as the shape of its property (<c>apply/coerce.rs</c>). A value that no row accepts is a
/// <see cref="PropertySkipReason"/>, never a wrapped or narrowed value. A reference, a pin, then the bare row of the shape's kind.
/// </summary>
internal sealed class Coercer
{
    public Coercer(IGameDataSchema schema, Dictionary<uint, PropObject> references, WorkMeter? meter = null)
    {
        Schema = schema;
        References = references;
        Meter = meter;
    }

    public IGameDataSchema Schema { get; }

    /// <summary>The meter of the application the coercion belongs to; every value coerced is a unit of its work.</summary>
    public WorkMeter? Meter { get; }

    /// <summary>The game's copy of each entry the edits reference, read once before any edit applies, by object hash.</summary>
    public Dictionary<uint, PropObject> References { get; }

    /// <summary>Set when a struct pin's <c>set</c> field is typed through the schema's fallback.</summary>
    public bool FellBack { get; set; }

    /// <summary>Reads <paramref name="value"/> as <paramref name="shape"/>. <paramref name="base"/> is the property's base value, the class
    /// source of a struct pin without <c>class</c>. Every element, key, field and operand reaches its own value through here, so the
    /// reference row is read once and holds everywhere a value is.</summary>
    public Coerced Coerce(GameDataLiteral value, GameDataShape shape, PropValue? @base)
    {
        Meter?.Charge(1);
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (value.ReferenceText() is { } text) return Referenced(text, shape);
        return value.Pinned() is { } pin ? Pinned(pin.Name, pin.Inner, shape, @base) : Bare(value, shape);
    }

    /// <summary>The game's copy of the value <paramref name="text"/> names. It carries its own kinds, so it is read as it is rather than
    /// coerced; what the property asks of it is that the two shapes agree.</summary>
    private Coerced Referenced(string text, GameDataShape shape)
    {
        // loading parses every reference it reads, so this only refuses one built by hand
        if (GameDataReference.TryParse(text) is not { } reference) return PropertySkipReason.ReferenceUnresolved;
        if (!References.TryGetValue(reference.Entry.ObjectHash, out var entry)) return PropertySkipReason.ReferenceMissingEntry;
        if (PropWalk.Resolve(entry, reference.Path, out var value, Meter) is not null) return PropertySkipReason.ReferenceUnresolved;
        return GameDataShape.Of(value!) == shape ? value!.Clone(0, Meter) : PropertySkipReason.KindMismatch;
    }

    /// <summary>Reads a pinned value: the pin fixes the kind, then the bare rules apply.</summary>
    private Coerced Pinned(string name, GameDataLiteral inner, GameDataShape shape, PropValue? @base)
    {
        // a reference has no literal spelling and carries the game's own kinds, so a pin over one asks for nothing and is refused
        if (inner.ReferenceText() is not null) return PropertySkipReason.PinMismatch;
        var pin = GameDataLiteral.KindNamed(name)!.Value;
        switch (shape.Kind)
        {
            case PropKind.Struct or PropKind.Embedded:
                return name is "pointer" or "embed" && pin == shape.Kind ? StructPin(inner, shape.Kind, @base) : PropertySkipReason.PinMismatch;
            case PropKind.Container or PropKind.UnorderedContainer or PropKind.Map:
                return pin == shape.Item ? Bare(inner, shape) : PropertySkipReason.PinMismatch;
            case PropKind.Optional when name == "option":
                return inner is GdMapping { Count: 0 } ? Optional(GdNull.Instance, shape) : Optional(inner, shape);
            case PropKind.Optional when pin == shape.Item:
            {
                // a struct pin on an option of structs is the element's own spelling, so the element keeps it
                var element = pin is PropKind.Struct or PropKind.Embedded
                    ? new GdMapping(new List<KeyValuePair<string, GameDataLiteral>> { new(name, inner) })
                    : inner;
                return Optional(new GdList(new List<GameDataLiteral> { element }), shape);
            }
            case PropKind.Optional:
                return PropertySkipReason.PinMismatch;
            default:
                return pin == shape.Kind ? Bare(inner, shape) : PropertySkipReason.PinMismatch;
        }
    }

    /// <summary>Reads an unpinned value by the row of its shape's kind.</summary>
    private Coerced Bare(GameDataLiteral value, GameDataShape shape)
    {
        switch (shape.Kind)
        {
            case PropKind.None:
                return PropertySkipReason.KindMismatch;
            case PropKind.Bool:
                return value is GdBool flag ? new PropBool(PropKind.Bool, flag.Value) : PropertySkipReason.KindMismatch;
            case PropKind.BitBool:
                return value switch
                {
                    GdBool b => new PropBool(PropKind.BitBool, b.Value),
                    GdInteger { Value: var n } when n == 0 => new PropBool(PropKind.BitBool, false),
                    GdInteger { Value: var n } when n == 1 => new PropBool(PropKind.BitBool, true),
                    GdInteger => PropertySkipReason.OutOfRange,
                    _ => PropertySkipReason.KindMismatch,
                };
            case PropKind.I8 or PropKind.U8 or PropKind.I16 or PropKind.U16 or PropKind.I32 or PropKind.U32 or PropKind.I64 or PropKind.U64:
            {
                var integer = Integer(value, shape.Kind, out ulong bits);
                return integer is null ? new PropInt(shape.Kind, bits) : integer.Value;
            }
            case PropKind.F32:
            {
                var single = Single(value, out float number);
                return single is null ? new PropF32(number) : single.Value;
            }
            case PropKind.Vector2: return Singles(value, 2, PropKind.Vector2);
            case PropKind.Vector3: return Singles(value, 3, PropKind.Vector3);
            case PropKind.Vector4: return Singles(value, 4, PropKind.Vector4);
            case PropKind.Matrix44: return Singles(value, 16, PropKind.Matrix44);
            case PropKind.Color:
            {
                if (value is not GdList items) return PropertySkipReason.KindMismatch;
                var bytes = new ulong[items.Items.Count];
                for (int i = 0; i < bytes.Length; i++)
                {
                    var error = Integer(items.Items[i], PropKind.U8, out bytes[i]);
                    if (error is not null) return error.Value;
                }
                if (bytes.Length != 4) return PropertySkipReason.ArityMismatch;
                return new PropColor((uint)bytes[0] | ((uint)bytes[1] << 8) | ((uint)bytes[2] << 16) | ((uint)bytes[3] << 24));
            }
            case PropKind.String:
                return value is GdString text ? new PropString(text.Value) : PropertySkipReason.KindMismatch;
            case PropKind.Hash:
            case PropKind.ObjectLink:
            {
                var error = Hash32(value, out uint hash);
                return error is null ? new PropHash(shape.Kind, hash) : error.Value;
            }
            case PropKind.WadChunkLink:
            {
                var error = Hash64(value, out ulong hash);
                return error is null ? new PropFile(hash) : error.Value;
            }
            case PropKind.Container or PropKind.UnorderedContainer:
                return List(value, shape);
            case PropKind.Optional:
                return Optional(value, shape);
            case PropKind.Map:
                return Map(value, shape);
            case PropKind.Struct:
                return value is GdNull ? PropStruct.NullPointer() : PropertySkipReason.KindMismatch;
            default:
                return PropertySkipReason.KindMismatch;
        }
    }

    /// <summary>Reads a list as a container of the shape's item kind.</summary>
    private Coerced List(GameDataLiteral value, GameDataShape shape)
    {
        if (shape.Item is not { } item) return PropertySkipReason.Untypable;
        if (value is not GdList list) return PropertySkipReason.KindMismatch;
        var items = new List<PropValue>(list.Items.Count);
        foreach (var element in list.Items)
        {
            var coerced = Coerce(element, GameDataShape.Bare(item), null);
            if (!coerced.IsOk) return coerced;
            items.Add(coerced.Value!);
        }
        return PropList.TryCreate(shape.Kind, item, items, out var container) ? container! : PropertySkipReason.KindMismatch;
    }

    /// <summary>Reads null, a list of zero or one element, or one value as an option.</summary>
    private Coerced Optional(GameDataLiteral value, GameDataShape shape)
    {
        if (shape.Item is not { } item) return PropertySkipReason.Untypable;
        PropValue? content = null;
        switch (value)
        {
            case GdNull:
                break;
            case GdList { Items.Count: 0 }:
                break;
            case GdList { Items.Count: 1 } one:
            {
                var coerced = Coerce(one.Items[0], GameDataShape.Bare(item), null);
                if (!coerced.IsOk) return coerced;
                content = coerced.Value;
                break;
            }
            case GdList:
                return PropertySkipReason.ArityMismatch;
            default:
            {
                var coerced = Coerce(value, GameDataShape.Bare(item), null);
                if (!coerced.IsOk) return coerced;
                content = coerced.Value;
                break;
            }
        }
        return PropOption.TryCreate(item, content, out var option) ? option! : PropertySkipReason.KindMismatch;
    }

    /// <summary>Reads a mapping as a map of the shape's key and value kinds. Entries keep their order and no key is deduplicated.</summary>
    private Coerced Map(GameDataLiteral value, GameDataShape shape)
    {
        if (shape.Key is not { } keyKind || shape.Item is not { } item) return PropertySkipReason.Untypable;
        if (value is not GdMapping mapping) return PropertySkipReason.KindMismatch;
        var entries = new List<KeyValuePair<PropValue, PropValue>>(mapping.Count);
        foreach (var (text, element) in mapping.Entries)
        {
            var key = Key(text, keyKind);
            if (!key.IsOk) return key;
            var coerced = Coerce(element, GameDataShape.Bare(item), null);
            if (!coerced.IsOk) return coerced;
            entries.Add(new(key.Value!, coerced.Value!));
        }
        return PropMapValue.TryCreate(keyKind, item, entries, out var map) ? map! : PropertySkipReason.KindMismatch;
    }

    /// <summary>Reads a map key, spelled as text, as the key kind.</summary>
    public Coerced Key(string text, PropKind kind)
    {
        GameDataLiteral value;
        switch (kind)
        {
            case PropKind.Bool:
                value = text switch { "true" => GdBool.True, "false" => GdBool.False, _ => null! };
                if (value is null) return PropertySkipReason.InvalidKey;
                break;
            case PropKind.I8 or PropKind.U8 or PropKind.I16 or PropKind.U16 or PropKind.I32 or PropKind.U32 or PropKind.I64 or PropKind.U64:
                if (!RustParse.TryParseInt128(text, out var integer)) return PropertySkipReason.InvalidKey;
                value = new GdInteger(integer);
                break;
            case PropKind.F32:
                if (!RustParse.TryParseDouble(text, out double number)) return PropertySkipReason.InvalidKey;
                value = new GdFloat(number);
                break;
            case PropKind.String or PropKind.Hash or PropKind.WadChunkLink:
                value = new GdString(text);
                break;
            default:
                return PropertySkipReason.InvalidKey;
        }
        return Bare(value, GameDataShape.Bare(kind));
    }

    /// <summary>Constructs the struct of a <c>pointer</c> or <c>embed</c> pin from <c>class</c> and <c>set</c>. The pin replaces the whole struct.</summary>
    private Coerced StructPin(GameDataLiteral inner, PropKind kind, PropValue? @base)
    {
        GdMapping fields;
        switch (inner)
        {
            case GdNull when kind == PropKind.Struct:
                return PropStruct.NullPointer();
            case GdMapping { Count: 0 } when kind == PropKind.Struct:
                return PropStruct.NullPointer();
            case GdMapping mapping:
                fields = mapping;
                break;
            default:
                return PropertySkipReason.KindMismatch;
        }

        uint? baseClass = @base switch
        {
            PropStruct { Kind: PropKind.Struct, ClassHash: not 0 } pointer => pointer.ClassHash,
            PropStruct { Kind: PropKind.Embedded } embed => embed.ClassHash,
            _ => null,
        };
        uint classHash;
        switch (fields.Get("class"))
        {
            case GdString name: classHash = LtkHash.Hash32Of(name.Value); break;
            case not null: return PropertySkipReason.KindMismatch;
            default:
                if (baseClass is not { } inherited) return PropertySkipReason.Untypable;
                classHash = inherited;
                break;
        }
        if (kind == PropKind.Embedded && baseClass is { } held && held != classHash) return PropertySkipReason.PinMismatch;
        // the shipped bin attests the class it already carries, whether the pin names that class or leaves it out; the schema answers
        // for every other class, and refusing one it does not know keeps a typo out of the written object
        if (baseClass != classHash && !Schema.HasClass(classHash)) return PropertySkipReason.UnknownClass;

        var properties = new OrderedMap<PropValue>();
        if (fields.Get("set") is GdMapping set)
        {
            foreach (var (key, element) in set.Entries)
            {
                if (!PropertyPath.TryParse(key, out var path, out _)) return PropertySkipReason.InvalidPath;
                if (path!.Segments.Count != 1 || path.Segments[0].HasSubscript) return PropertySkipReason.InvalidPath;
                uint field = LtkHash.Hash32Of(path.Segments[0].Name);
                GameDataShape fieldShape;
                if (Schema.Expected(classHash, field) is { } expected) fieldShape = expected;
                else
                {
                    if (Schema.Fallback(classHash, field) is not { } fallback) return PropertySkipReason.Untypable;
                    FellBack = true;
                    fieldShape = fallback;
                }
                var coerced = Coerce(element, fieldShape, null);
                if (!coerced.IsOk) return coerced;
                properties.Set(field, coerced.Value!);
            }
        }
        return new PropStruct(kind, classHash, properties);
    }

    // ------------------------------------------------------------------------------------------ leaves

    private static PropertySkipReason? Integer(GameDataLiteral value, PropKind kind, out ulong bits)
    {
        bits = 0;
        if (value is not GdInteger integer) return PropertySkipReason.KindMismatch;
        Int128 n = integer.Value;
        (Int128 min, Int128 max) = kind switch
        {
            PropKind.I8 => ((Int128)sbyte.MinValue, (Int128)sbyte.MaxValue),
            PropKind.U8 => ((Int128)0, (Int128)byte.MaxValue),
            PropKind.I16 => ((Int128)short.MinValue, (Int128)short.MaxValue),
            PropKind.U16 => ((Int128)0, (Int128)ushort.MaxValue),
            PropKind.I32 => ((Int128)int.MinValue, (Int128)int.MaxValue),
            PropKind.U32 => ((Int128)0, (Int128)uint.MaxValue),
            PropKind.I64 => ((Int128)long.MinValue, (Int128)long.MaxValue),
            _ => ((Int128)0, (Int128)ulong.MaxValue),
        };
        if (n < min || n > max) return PropertySkipReason.OutOfRange;
        bits = n < 0 ? unchecked((ulong)(long)n) : (ulong)n;
        return null;
    }

    /// <summary>A single-precision float: a float rounded, or an integer represented exactly.</summary>
    private static PropertySkipReason? Single(GameDataLiteral value, out float result)
    {
        result = 0;
        switch (value)
        {
            case GdFloat f:
                result = (float)f.Value;
                return null;
            case GdInteger i:
                if (!ExactSingle(i.Value, out result)) return PropertySkipReason.PrecisionLoss;
                return null;
            default:
                return PropertySkipReason.KindMismatch;
        }
    }

    /// <summary>Whether an integer is exactly an f32 (<c>integer as f32</c> read back is the integer): the odd part of its magnitude
    /// fits the 24 bits of a single's significand.</summary>
    private static bool ExactSingle(Int128 integer, out float single)
    {
        single = 0;
        if (integer == 0) return true;
        UInt128 magnitude = integer < 0 ? (UInt128)(-integer) : (UInt128)integer;
        int zeros = (int)UInt128.TrailingZeroCount(magnitude);
        if ((magnitude >> zeros) >= (UInt128)(1 << 24)) return false;
        single = (float)(double)integer;
        return true;
    }

    private Coerced Singles(GameDataLiteral value, int count, PropKind kind)
    {
        if (value is not GdList list) return PropertySkipReason.KindMismatch;
        var floats = new float[list.Items.Count];
        for (int i = 0; i < floats.Length; i++)
        {
            var error = Single(list.Items[i], out floats[i]);
            if (error is not null) return error.Value;
        }
        return floats.Length == count ? new PropVector(kind, floats) : PropertySkipReason.ArityMismatch;
    }

    /// <summary>A 32-bit hash: FNV-1a of a lowercased string, a spelled hash, or zero for null and <c>""</c>.</summary>
    private static PropertySkipReason? Hash32(GameDataLiteral value, out uint hash)
    {
        hash = 0;
        switch (value)
        {
            case GdNull: return null;
            case GdString { Value.Length: 0 }: return null;
            case GdString text:
                hash = LtkHash.Hash32Of(text.Value);
                return null;
            default: return PropertySkipReason.KindMismatch;
        }
    }

    /// <summary>A 64-bit hash: XXH64 of a lowercased path, a spelled hash, or zero for null and <c>""</c>.</summary>
    private static PropertySkipReason? Hash64(GameDataLiteral value, out ulong hash)
    {
        hash = 0;
        switch (value)
        {
            case GdNull: return null;
            case GdString { Value.Length: 0 }: return null;
            case GdString text:
                hash = LtkHash.Hash64Of(text.Value);
                return null;
            default: return PropertySkipReason.KindMismatch;
        }
    }
}
