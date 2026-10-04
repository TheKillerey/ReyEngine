using System.Runtime.CompilerServices;
using System.Text;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>The kinds of <c>ResolveError</c>: why a path does not name a value in the tree it was walked through.</summary>
public enum ResolveErrorKind
{
    MissingObject,
    MissingProperty,
    NullPointer,
    CannotDescend,
    NotIndexable,
    IndexOutOfRange,
    InvalidKey,
    KeyNotFound,
    FieldExpected,
}

/// <summary>Why a path does not name a value (<c>ResolveError</c>): the segment that could not be applied, counting from 0, and the kind.</summary>
public sealed class ResolveError
{
    public ResolveError(int segment, ResolveErrorKind kind, uint hash = 0, PropKind valueKind = PropKind.None, uint index = 0, int length = 0)
    {
        Segment = segment;
        Kind = kind;
        Hash = hash;
        ValueKind = valueKind;
        Index = index;
        Length = length;
    }

    public int Segment { get; }
    public ResolveErrorKind Kind { get; }
    public uint Hash { get; }
    public PropKind ValueKind { get; }
    public uint Index { get; }
    public int Length { get; }

    /// <summary>The <c>Display</c>: the kind's statement and the segment.</summary>
    public string Message => Kind switch
    {
        ResolveErrorKind.MissingObject => $"no object {Hash:x8}",
        ResolveErrorKind.MissingProperty => $"no property {Hash:x8}",
        ResolveErrorKind.NullPointer => "the pointer is null",
        ResolveErrorKind.CannotDescend => $"cannot descend into a {ValueKind}",
        ResolveErrorKind.NotIndexable => $"a {ValueKind} cannot be subscripted that way",
        ResolveErrorKind.IndexOutOfRange => $"index {Index} is out of range, the length is {Length}",
        ResolveErrorKind.InvalidKey => $"the key does not convert to {ValueKind}",
        ResolveErrorKind.KeyNotFound => "no entry has that key",
        _ => "a path into an object starts with a field",
    } + $" (segment {Segment})";

    /// <summary>The record-skip code the kind maps to; every property-skip reason of a path is the same name.</summary>
    public RecordSkipReason RecordReason => Kind switch
    {
        ResolveErrorKind.MissingObject => RecordSkipReason.MissingObject,
        ResolveErrorKind.MissingProperty => RecordSkipReason.MissingProperty,
        ResolveErrorKind.NullPointer => RecordSkipReason.NullPointer,
        ResolveErrorKind.CannotDescend => RecordSkipReason.CannotDescend,
        ResolveErrorKind.NotIndexable => RecordSkipReason.NotIndexable,
        ResolveErrorKind.IndexOutOfRange => RecordSkipReason.IndexOutOfRange,
        ResolveErrorKind.InvalidKey => RecordSkipReason.InvalidKey,
        ResolveErrorKind.KeyNotFound => RecordSkipReason.KeyNotFound,
        _ => RecordSkipReason.Unknown,
    };

    public PropertySkipReason PropertyReason => Kind switch
    {
        ResolveErrorKind.MissingObject => PropertySkipReason.MissingObject,
        ResolveErrorKind.MissingProperty => PropertySkipReason.MissingProperty,
        ResolveErrorKind.NullPointer => PropertySkipReason.NullPointer,
        ResolveErrorKind.CannotDescend => PropertySkipReason.CannotDescend,
        ResolveErrorKind.NotIndexable => PropertySkipReason.NotIndexable,
        ResolveErrorKind.IndexOutOfRange => PropertySkipReason.IndexOutOfRange,
        ResolveErrorKind.InvalidKey => PropertySkipReason.InvalidKey,
        ResolveErrorKind.KeyNotFound => PropertySkipReason.KeyNotFound,
        _ => PropertySkipReason.Unknown,
    };
}

/// <summary>Why a patch does not apply (<c>PatchError</c>): the path does not name a value, or it names one of another shape.</summary>
public sealed class PatchFailure
{
    private PatchFailure(ResolveError? resolve, PropShape expected, PropShape found)
    {
        Resolve = resolve;
        Expected = expected;
        Found = found;
    }

    public static PatchFailure Unresolved(ResolveError error) => new(error, default, default);

    public static PatchFailure Mismatch(PropShape expected, PropShape found) => new(null, expected, found);

    public ResolveError? Resolve { get; }
    public PropShape Expected { get; }
    public PropShape Found { get; }

    public string Message => Resolve is { } error ? error.Message
        : $"type mismatch: the property is {Expected}, the patch carries {Found}";

    public RecordSkipReason RecordReason => Resolve?.RecordReason ?? RecordSkipReason.TypeMismatch;

    public PropertySkipReason PropertyReason => Resolve?.PropertyReason ?? PropertySkipReason.TypeMismatch;
}

/// <summary>One segment of a <see cref="ValuePath"/>: a field by its hash, an element by its position, or a map entry by its key.</summary>
public readonly struct ValueSegment
{
    private ValueSegment(byte kind, uint field, int index, PropValue? key) { Kind = kind; Field = field; Index = index; Key = key; }

    public static ValueSegment OfField(uint field) => new(0, field, 0, null);
    public static ValueSegment OfIndex(int index) => new(1, 0, index, null);
    public static ValueSegment OfKey(PropValue key) => new(2, 0, 0, key);

    /// <summary>0 for a field, 1 for an index, 2 for a key.</summary>
    public byte Kind { get; }
    public uint Field { get; }
    public int Index { get; }
    public PropValue? Key { get; }
    public bool IsField => Kind == 0;
}

/// <summary>
/// M817: walking a path through a value tree, and the type rule that governs a patch (<c>ltk_meta</c> <c>path/resolve.rs</c>,
/// <c>path/value.rs</c>, and the declaration engine's <c>address.rs</c>). There are two languages here and they differ:
///
/// <list type="bullet">
/// <item><b>A <see cref="PropertyPath"/> as the client reads it</b> (a PTCH record): every name is hashed as text, with no <c>0x</c>
/// escape, and a <c>{k}</c> key matches by IEEE equality.</item>
/// <item><b>A declaration path as a <see cref="ValueSegment"/> list</b> (<c>ValuePath</c>): a name spelled <c>0x</c> and 8 hex digits is the
/// field hash itself, a key is converted to the map's key kind first, and matching is bitwise, so <c>NaN</c> equals <c>NaN</c> and
/// <c>-0</c> differs from <c>0</c>.</item>
/// </list>
/// </summary>
public static class PropWalk
{
    // ============================================================================================== keys

    /// <summary><c>key_as</c>: the key a <c>{k}</c> literal selects, as a value of <paramref name="kind"/>; null when it does not convert.
    /// A hash key takes a string as text to hash, or a number as the raw value; there is no <c>0x</c> escape here.</summary>
    public static PropValue? KeyAs(PropKind kind, KeyLiteral literal)
    {
        switch (kind, literal.Kind)
        {
            case (PropKind.Bool, KeyLiteralKind.Bool): return new PropBool(PropKind.Bool, literal.Flag);
            case (PropKind.BitBool, KeyLiteralKind.Bool): return new PropBool(PropKind.BitBool, literal.Flag);
            case (PropKind.I8 or PropKind.U8 or PropKind.I16 or PropKind.U16 or PropKind.I32 or PropKind.U32 or PropKind.I64 or PropKind.U64, KeyLiteralKind.Number):
                return RustParse.TryParseInteger(literal.Text, kind, out ulong bits) ? new PropInt(kind, bits) : null;
            case (PropKind.F32, KeyLiteralKind.Number):
                return RustParse.TryParseSingle(literal.Text, out float single) ? new PropF32(single) : null;
            case (PropKind.String, KeyLiteralKind.String): return new PropString(literal.Text);
            case (PropKind.Hash, KeyLiteralKind.String): return new PropHash(PropKind.Hash, LtkHash.Fnv1aLower(literal.Text));
            case (PropKind.Hash, KeyLiteralKind.Number):
                return RustParse.TryParseInteger(literal.Text, PropKind.U32, out ulong hash) ? new PropHash(PropKind.Hash, (uint)hash) : null;
            case (PropKind.WadChunkLink, KeyLiteralKind.String): return new PropFile(LtkHash.Xxh64Path(literal.Text));
            case (PropKind.WadChunkLink, KeyLiteralKind.Number):
                return RustParse.TryParseInteger(literal.Text, PropKind.U64, out ulong file) ? new PropFile(file) : null;
            default: return null;
        }
    }

    /// <summary><c>MapKey::from_literal</c>: <see cref="KeyAs"/>, as a key a map can hold (a flag cannot key one).</summary>
    public static PropValue? MapKeyFromLiteral(KeyLiteral literal, PropKind kind)
    {
        var key = KeyAs(kind, literal);
        return key is null || key.Kind == PropKind.BitBool ? null : key;
    }

    /// <summary><c>key_eq</c>: whether a map key equals the value <see cref="KeyAs"/> produced, comparing floats as IEEE numbers.</summary>
    private static bool ClientKeyEquals(PropValue key, PropValue wanted)
    {
        if (key.Kind != wanted.Kind) return false;
        return key switch
        {
            PropBool b => b.Value == ((PropBool)wanted).Value,
            PropInt n => n.Bits == ((PropInt)wanted).Bits,
            PropF32 f => f.Value == ((PropF32)wanted).Value,
            PropString s => string.Equals(s.Value, ((PropString)wanted).Value, StringComparison.Ordinal),
            PropHash h => h.Value == ((PropHash)wanted).Value,
            PropFile f => f.Value == ((PropFile)wanted).Value,
            _ => false,
        };
    }

    /// <summary>Whether two map keys are equal as <c>MapKey</c>s: the same kind, and a float by its bits.</summary>
    public static bool MapKeyEquals(PropValue key, PropValue wanted)
    {
        if (key.Kind != wanted.Kind) return false;
        return key switch
        {
            PropF32 f => BitConverter.SingleToUInt32Bits(f.Value) == BitConverter.SingleToUInt32Bits(((PropF32)wanted).Value),
            PropVector v => VectorBitsEqual(v, (PropVector)wanted),
            PropColor c => c.Packed == ((PropColor)wanted).Packed,
            PropHash or PropFile or PropString or PropBool or PropInt => ClientKeyEquals(key, wanted),
            PropNone => true,
            _ => false,
        };
    }

    private static bool VectorBitsEqual(PropVector a, PropVector b)
    {
        for (int i = 0; i < a.Components.Length; i++)
            if (BitConverter.SingleToUInt32Bits(a.Components[i]) != BitConverter.SingleToUInt32Bits(b.Components[i])) return false;
        return true;
    }

    // ============================================================================================== steps shared by both languages

    private static ResolveError? DescendCheck(PropValue value, int segment)
    {
        if (value is PropStruct { Kind: PropKind.Struct, ClassHash: 0 }) return new ResolveError(segment, ResolveErrorKind.NullPointer);
        if (value is PropStruct) return null;
        return new ResolveError(segment, ResolveErrorKind.CannotDescend, valueKind: value.Kind);
    }

    /// <summary>Where a value sits, so that it can be replaced: a property of an object or a struct, an item of a list, the value of an
    /// option, or the value of a map entry.</summary>
    private readonly struct Slot
    {
        private readonly OrderedMap<PropValue>? _properties;
        private readonly uint _field;
        private readonly PropList? _list;
        private readonly PropOption? _option;
        private readonly PropMapValue? _map;
        private readonly int _index;

        public Slot(PropValue value, OrderedMap<PropValue>? properties, uint field, PropList? list, PropOption? option, PropMapValue? map, int index)
        {
            Value = value;
            _properties = properties;
            _field = field;
            _list = list;
            _option = option;
            _map = map;
            _index = index;
        }

        public PropValue Value { get; }

        public void Set(PropValue replacement)
        {
            if (_properties is not null) _properties.Set(_field, replacement);
            else if (_list is not null) _list.Items[_index] = replacement;
            else if (_option is not null) _option.Replace(replacement);
            else if (_map is not null) _map.Entries[_index] = new(_map.Entries[_index].Key, replacement);
        }
    }

    private static Slot OfProperty(OrderedMap<PropValue> properties, uint field, PropValue value) => new(value, properties, field, null, null, null, 0);

    // ============================================================================================== declaration paths (ValuePath)

    /// <summary><c>slot_at</c>: which slot an index or a key selects inside <paramref name="value"/>.</summary>
    private static ResolveError? SlotAt(PropValue value, in ValueSegment segment, int at, out Slot slot, WorkMeter? meter)
    {
        slot = default;
        switch (value)
        {
            case PropList list when segment.Kind == 1:
                if (segment.Index < list.Items.Count)
                {
                    slot = new Slot(list.Items[segment.Index], null, 0, list, null, null, segment.Index);
                    return null;
                }
                return new ResolveError(at, ResolveErrorKind.IndexOutOfRange, index: ClampIndex(segment.Index), length: list.Items.Count);
            case PropOption option when segment.Kind == 1:
                if (segment.Index == 0 && option.Value is not null)
                {
                    slot = new Slot(option.Value, null, 0, null, option, null, 0);
                    return null;
                }
                return new ResolveError(at, ResolveErrorKind.IndexOutOfRange, index: ClampIndex(segment.Index), length: option.Value is null ? 0 : 1);
            case PropMapValue map when segment.Kind == 2:
            {
                var key = segment.Key!;
                if (key.Kind != map.KeyKind) return new ResolveError(at, ResolveErrorKind.InvalidKey, valueKind: map.KeyKind);
                meter?.Charge(map.Entries.Count);
                for (int i = 0; i < map.Entries.Count; i++)
                {
                    if (!MapKeyEquals(map.Entries[i].Key, key)) continue;
                    slot = new Slot(map.Entries[i].Value, null, 0, null, null, map, i);
                    return null;
                }
                return new ResolveError(at, ResolveErrorKind.KeyNotFound);
            }
            default:
                return new ResolveError(at, ResolveErrorKind.NotIndexable, valueKind: value.Kind);
        }
    }

    private static uint ClampIndex(int index) => index < 0 ? uint.MaxValue : (uint)index;

    /// <summary>
    /// <c>walk_at</c>: applies the first <paramref name="count"/> segments from the properties of an object. Returns the slot the last
    /// segment lands in, or none for zero segments, which is the properties themselves. A field descends into an object, a pointer or an
    /// embed; an index or a key subscripts the value reached.
    /// </summary>
    private static ResolveError? Walk(OrderedMap<PropValue> root, IReadOnlyList<ValueSegment> segments, int count, out Slot? slot, WorkMeter? meter)
    {
        slot = null;
        meter?.Charge(count + 1);
        OrderedMap<PropValue> properties = root;
        PropValue? current = null;
        Slot last = default;
        for (int i = 0; i < count; i++)
        {
            var segment = segments[i];
            if (current is null)
            {
                // the object's own properties: only a field can start a path
                if (!segment.IsField) return new ResolveError(i, ResolveErrorKind.FieldExpected);
                if (!properties.TryGetValue(segment.Field, out var found)) return new ResolveError(i, ResolveErrorKind.MissingProperty, segment.Field);
                last = OfProperty(properties, segment.Field, found);
                current = found;
            }
            else if (segment.IsField)
            {
                if (DescendCheck(current, i) is { } error) return error;
                properties = ((PropStruct)current).Properties;
                if (!properties.TryGetValue(segment.Field, out var found)) return new ResolveError(i, ResolveErrorKind.MissingProperty, segment.Field);
                last = OfProperty(properties, segment.Field, found);
                current = found;
            }
            else
            {
                if (SlotAt(current, segment, i, out var next, meter) is { } error) return error;
                last = next;
                current = next.Value;
            }
        }
        if (current is not null) slot = last;
        return null;
    }

    /// <summary><c>BinObject::resolve_at</c>: the value a declaration path names inside an object.</summary>
    public static ResolveError? ResolveAt(PropObject obj, IReadOnlyList<ValueSegment> path, out PropValue? value) => ResolveAt(obj, path, out value, null);

    internal static ResolveError? ResolveAt(PropObject obj, IReadOnlyList<ValueSegment> path, out PropValue? value, WorkMeter? meter)
    {
        value = null;
        var error = Walk(obj.Properties, path, path.Count, out var slot, meter);
        if (error is not null) return error;
        if (slot is null) return new ResolveError(0, ResolveErrorKind.FieldExpected);
        value = slot.Value.Value;
        return null;
    }

    /// <summary><c>PropertyValueEnum::resolve_at</c>: the value a path names relative to <paramref name="start"/>, where a field descends
    /// into a pointer or an embed and an index or a key subscripts a list, an option or a map.</summary>
    public static ResolveError? ResolveFromValue(PropValue start, IReadOnlyList<ValueSegment> path, out PropValue? value)
    {
        value = null;
        PropValue current = start;
        for (int i = 0; i < path.Count; i++)
        {
            var segment = path[i];
            if (segment.IsField)
            {
                if (DescendCheck(current, i) is { } error) return error;
                var properties = ((PropStruct)current).Properties;
                if (!properties.TryGetValue(segment.Field, out var found)) return new ResolveError(i, ResolveErrorKind.MissingProperty, segment.Field);
                current = found;
            }
            else
            {
                if (SlotAt(current, segment, i, out var next, null) is { } error) return error;
                current = next.Value;
            }
        }
        value = current;
        return null;
    }

    /// <summary>
    /// <c>BinObject::patch_at</c>: sets the property a declaration path names, under the client's type rule. A leaf the path names by a
    /// field is created when it is absent (appended); a leaf named by a subscript never is, and neither is a step on the way down.
    /// A value of another shape than the one it replaces changes nothing. Null when the patch landed.
    ///
    /// <para>A patch that would leave the tree deeper than the codec reads (256 containers) is refused with a <see cref="GameDataException"/> of
    /// kind <see cref="GameDataErrorKind.Bin"/>. <c>ltk_meta</c> inserts at any depth and the crate's writer then recurses to match, so the
    /// first tree it cannot write is one of a few thousand levels; this engine stops at the depth it reads, which no bin of the game comes within
    /// seven times of.</para>
    /// </summary>
    /// <param name="inserted">Whether the leaf was created rather than replaced.</param>
    public static PatchFailure? PatchAt(PropObject obj, IReadOnlyList<ValueSegment> path, PropValue value, out bool inserted) => PatchAt(obj, path, value, out inserted, null);

    internal static PatchFailure? PatchAt(PropObject obj, IReadOnlyList<ValueSegment> path, PropValue value, out bool inserted, WorkMeter? meter)
    {
        inserted = false;
        if (path.Count > 0 && path[^1].IsField)
        {
            uint field = path[^1].Field;
            int parents = path.Count - 1;
            var error = Walk(obj.Properties, path, parents, out var parent, meter);
            if (error is not null) return PatchFailure.Unresolved(error);
            OrderedMap<PropValue> properties = obj.Properties;
            if (parent is { } slot)
            {
                if (DescendCheck(slot.Value, parents) is { } descend) return PatchFailure.Unresolved(descend);
                properties = ((PropStruct)slot.Value).Properties;
            }
            if (!properties.TryGetValue(field, out var existing))
            {
                // the leaf the path names outright is created
                Admit(value, parents, created: true, meter);
                properties.Set(field, value);
                inserted = true;
                return null;
            }
            return Replace(OfProperty(properties, field, existing), value, parents, meter);
        }

        var walkError = Walk(obj.Properties, path, path.Count, out var target, meter);
        if (walkError is not null) return PatchFailure.Unresolved(walkError);
        if (target is null) return PatchFailure.Unresolved(new ResolveError(0, ResolveErrorKind.FieldExpected));
        return Replace(target.Value, value, path.Count - 1, meter);
    }

    /// <summary>Replaces the value in <paramref name="slot"/> under the type rule: the shapes must be equal. <paramref name="depth"/> is how many
    /// containers the slot lies down.</summary>
    private static PatchFailure? Replace(Slot slot, PropValue value, int depth, WorkMeter? meter)
    {
        var expected = slot.Value.Shape;
        var found = value.Shape;
        if (expected != found) return PatchFailure.Mismatch(expected, found);
        Admit(value, depth, created: false, meter);
        slot.Set(value);
        return null;
    }

    /// <summary>
    /// The insertion rule: a value put into the tree at <paramref name="depth"/> containers down must leave every one of its own within 256
    /// (<see cref="PropLimits.MaxDepth"/>), and its bytes count toward the output the tree is allowed to grow to. A value is measured once, here, and
    /// refused whole: nothing of it has been put in when the refusal comes.
    /// </summary>
    private static void Admit(PropValue value, int depth, bool created, WorkMeter? meter)
    {
        long bytes = created ? 5 : 0;   // a property carries its name and its kind
        long nodes = 0;
        if (!Measure(value, PropLimits.MaxDepth - depth, ref bytes, ref nodes))
            throw new GameDataException(GameDataErrorKind.Bin,
                $"Container nesting is too deep: a value inserted {depth} containers down has room for {Math.Max(PropLimits.MaxDepth - depth, 0)} levels of its own, the limit being {PropLimits.MaxDepth}");
        if (meter is null) return;
        meter.Charge(nodes);
        meter.Grow(bytes);
    }

    /// <summary>What it takes to write an object (<see cref="PropCodec"/>'s class, size, path and property count, then each property): the bytes a clone of it adds
    /// to the tree. An object nested past the depth the codec writes is the same refusal as inserting one.</summary>
    internal static long MeasureObject(PropObject obj)
    {
        long bytes = 14, nodes = 0;
        for (int i = 0; i < obj.Properties.Count; i++)
        {
            bytes += 5;
            if (!Measure(obj.Properties.ValueAt(i), PropLimits.MaxDepth, ref bytes, ref nodes)) throw PropLimits.TooDeep();
        }
        return bytes;
    }

    /// <summary>
    /// Whether <paramref name="value"/> nests no more than <paramref name="remaining"/> containers (a value of no container, a leaf, may stand at 0),
    /// adding what it takes to write it to <paramref name="bytes"/> and the values it holds to <paramref name="nodes"/>. Stops at the first
    /// container past the limit, so its own recursion is no deeper than the limit.
    /// </summary>
    internal static bool Measure(PropValue value, int remaining, ref long bytes, ref long nodes)
    {
        if (remaining < 0) return false;
        nodes++;
        switch (value)
        {
            case PropList list:
                if (remaining < 1) return false;
                RuntimeHelpers.EnsureSufficientExecutionStack();
                bytes += 9;
                foreach (var item in list.Items)
                    if (!Measure(item, remaining - 1, ref bytes, ref nodes)) return false;
                return true;
            case PropOption option:
                if (remaining < 1) return false;
                RuntimeHelpers.EnsureSufficientExecutionStack();
                bytes += 2;
                return option.Value is null || Measure(option.Value, remaining - 1, ref bytes, ref nodes);
            case PropMapValue map:
                if (remaining < 1) return false;
                RuntimeHelpers.EnsureSufficientExecutionStack();
                bytes += 10;
                foreach (var entry in map.Entries)
                    if (!Measure(entry.Key, remaining - 1, ref bytes, ref nodes) || !Measure(entry.Value, remaining - 1, ref bytes, ref nodes)) return false;
                return true;
            case PropStruct st:
            {
                if (remaining < 1) return false;
                RuntimeHelpers.EnsureSufficientExecutionStack();
                bytes += 4;
                if (st.ClassHash == 0) return true;
                bytes += 6;
                for (int i = 0; i < st.Properties.Count; i++)
                {
                    bytes += 5;
                    nodes++;
                    if (!Measure(st.Properties.ValueAt(i), remaining - 1, ref bytes, ref nodes)) return false;
                }
                return true;
            }
            case PropString text:
                bytes += 2 + Encoding.UTF8.GetByteCount(text.Value);
                return true;
            default:
                bytes += Math.Max(PropKinds.FixedWidth(value.Kind), 0);
                return true;
        }
    }

    /// <summary><c>address::value_path</c>: the declaration path <paramref name="path"/> names inside <paramref name="obj"/>. A field
    /// is its hash (a hash-form name is the hash itself), an index its position, and a key is converted to the key kind of the map it
    /// subscripts, which is found in the object as it stands.</summary>
    public static ResolveError? ValuePathOf(PropObject obj, PropertyPath path, out List<ValueSegment> segments) => ValuePathOf(obj, path, out segments, null);

    internal static ResolveError? ValuePathOf(PropObject obj, PropertyPath path, out List<ValueSegment> segments, WorkMeter? meter)
    {
        segments = new List<ValueSegment>(path.Segments.Count * 2);
        foreach (var segment in path.Segments)
        {
            segments.Add(ValueSegment.OfField(LtkHash.Hash32Of(segment.Name)));
            if (segment.Index is { } index)
            {
                segments.Add(ValueSegment.OfIndex(index > int.MaxValue ? int.MaxValue : (int)index));
            }
            else if (segment.Key is { } literal)
            {
                var error = ResolveAt(obj, segments, out var container, meter);
                if (error is not null) return error;
                if (container is not PropMapValue map) return new ResolveError(0, ResolveErrorKind.NotIndexable, valueKind: container!.Kind);
                var key = MapKeyFromLiteral(literal, map.KeyKind);
                if (key is null) return new ResolveError(0, ResolveErrorKind.InvalidKey, valueKind: map.KeyKind);
                segments.Add(ValueSegment.OfKey(key));
            }
        }
        return null;
    }

    /// <summary><c>address::resolve</c>: the value a declaration path names inside an object.</summary>
    public static ResolveError? Resolve(PropObject obj, PropertyPath path, out PropValue? value) => Resolve(obj, path, out value, null);

    internal static ResolveError? Resolve(PropObject obj, PropertyPath path, out PropValue? value, WorkMeter? meter)
    {
        value = null;
        var error = ValuePathOf(obj, path, out var segments, meter);
        return error ?? ResolveAt(obj, segments, out value, meter);
    }

    // ============================================================================================== client paths (PropertyPath)

    private static ResolveError? ClientSlot(PropValue value, PathSegment segment, int at, out Slot slot, WorkMeter? meter)
    {
        slot = default;
        switch (value)
        {
            case PropList list when segment.Index is { } index:
                if (index < list.Items.Count)
                {
                    slot = new Slot(list.Items[(int)index], null, 0, list, null, null, (int)index);
                    return null;
                }
                return new ResolveError(at, ResolveErrorKind.IndexOutOfRange, index: index, length: list.Items.Count);
            case PropOption option when segment.Index is { } index:
                if (index == 0 && option.Value is not null)
                {
                    slot = new Slot(option.Value, null, 0, null, option, null, 0);
                    return null;
                }
                return new ResolveError(at, ResolveErrorKind.IndexOutOfRange, index: index, length: option.Value is null ? 0 : 1);
            case PropMapValue map when segment.Key is { } literal:
            {
                var wanted = KeyAs(map.KeyKind, literal);
                if (wanted is null) return new ResolveError(at, ResolveErrorKind.InvalidKey, valueKind: map.KeyKind);
                meter?.Charge(map.Entries.Count);
                for (int i = 0; i < map.Entries.Count; i++)
                {
                    if (!ClientKeyEquals(map.Entries[i].Key, wanted)) continue;
                    slot = new Slot(map.Entries[i].Value, null, 0, null, null, map, i);
                    return null;
                }
                return new ResolveError(at, ResolveErrorKind.KeyNotFound);
            }
            default:
                return new ResolveError(at, ResolveErrorKind.NotIndexable, valueKind: value.Kind);
        }
    }

    /// <summary><c>step</c>: looks a segment's name up by the hash of its text and applies its subscript.</summary>
    private static ResolveError? ClientStep(OrderedMap<PropValue> properties, PathSegment segment, int at, out Slot slot, WorkMeter? meter)
    {
        uint name = LtkHash.Fnv1aLower(segment.Name);
        if (!properties.TryGetValue(name, out var value))
        {
            slot = default;
            return new ResolveError(at, ResolveErrorKind.MissingProperty, name);
        }
        if (!segment.HasSubscript)
        {
            slot = OfProperty(properties, name, value);
            return null;
        }
        return ClientSlot(value, segment, at, out slot, meter);
    }

    /// <summary>
    /// <c>BinObject::patch</c>: sets the property a path names the way a PTCH record does. Every name is hashed as text. A leaf that
    /// is not there is created when the last segment names it outright; a step on the way down never is. Null when the record applied.
    /// A record that would leave the tree deeper than 256 containers is refused as <see cref="PatchAt(PropObject, IReadOnlyList{ValueSegment}, PropValue, out bool)"/> refuses one.
    /// </summary>
    public static PatchFailure? PatchClient(PropObject obj, PropertyPath path, PropValue value, out bool inserted) => PatchClient(obj, path, value, out inserted, null);

    internal static PatchFailure? PatchClient(PropObject obj, PropertyPath path, PropValue value, out bool inserted, WorkMeter? meter)
    {
        inserted = false;
        OrderedMap<PropValue> properties = obj.Properties;
        var segments = path.Segments;
        meter?.Charge(segments.Count + 1);
        // how many containers the leaf lies down: a field is one level, and so is a subscript
        int depth = -1;
        foreach (var segment in segments) depth += segment.HasSubscript ? 2 : 1;
        for (int i = 0; i < segments.Count - 1; i++)
        {
            var error = ClientStep(properties, segments[i], i, out var slot, meter);
            if (error is not null) return PatchFailure.Unresolved(error);
            if (DescendCheck(slot.Value, i + 1) is { } descend) return PatchFailure.Unresolved(descend);
            properties = ((PropStruct)slot.Value).Properties;
        }

        var last = segments[^1];
        uint name = LtkHash.Fnv1aLower(last.Name);
        if (!properties.ContainsKey(name))
        {
            if (last.HasSubscript) return PatchFailure.Unresolved(new ResolveError(segments.Count - 1, ResolveErrorKind.MissingProperty, name));
            Admit(value, depth, created: true, meter);
            properties.Set(name, value);
            inserted = true;
            return null;
        }
        var lastError = ClientStep(properties, last, segments.Count - 1, out var target, meter);
        if (lastError is not null) return PatchFailure.Unresolved(lastError);
        return Replace(target, value, depth, meter);
    }
}
