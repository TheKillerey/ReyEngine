using System.Runtime.CompilerServices;
using System.Text;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M817: the property kinds of <c>ltk_meta</c> (<c>property/kind.rs</c>), under the names <c>ltk_meta</c> gives them and the byte
/// values the current numbering writes. <see cref="object.ToString"/> is the name Rust's <c>{:?}</c> prints, which the apply
/// engine's error texts use.
/// </summary>
public enum PropKind : byte
{
    None = 0,
    Bool = 1,
    I8 = 2,
    U8 = 3,
    I16 = 4,
    U16 = 5,
    I32 = 6,
    U32 = 7,
    I64 = 8,
    U64 = 9,
    F32 = 10,
    Vector2 = 11,
    Vector3 = 12,
    Vector4 = 13,
    Matrix44 = 14,
    Color = 15,
    String = 16,
    Hash = 17,
    WadChunkLink = 18,
    Container = 0x80,
    UnorderedContainer = 0x81,
    Struct = 0x82,
    Embedded = 0x83,
    ObjectLink = 0x84,
    Optional = 0x85,
    Map = 0x86,
    BitBool = 0x87,
}

/// <summary>
/// The bounds the engine keeps on a tree so that nothing it does with one can exhaust the stack or the memory. They are not <c>ltk_meta</c>'s:
/// the crate reads and writes a tree of any depth and reserves what a count asks for. They bite only on input nobody ships: the deepest value in
/// the installed game's 40,643 bins is 34 levels, and none of them holds an entry that takes no bytes.
/// </summary>
internal static class PropLimits
{
    /// <summary>How many containers may nest: a value is read, cloned and written at most this deep, and an insertion that would leave the tree deeper is refused.</summary>
    public const int MaxDepth = 256;

    /// <summary><c>ltk_io_ext::MAX_RESERVE</c>: no more than this many bytes of an allocation are made ahead of the data that fills it.</summary>
    public const int MaxReserveBytes = 1 << 20;

    /// <summary>How many entries of a kind that takes no bytes (a list of <c>None</c>) one decode may declare in all: nothing bounds them but this.</summary>
    public const int ZeroWidthBudget = 1 << 16;

    public static GameDataException TooDeep() => new(GameDataErrorKind.Bin, "Container nesting is too deep");
}

/// <summary>
/// The hash table behind every collection the engine keys by a 32-bit hash that a file or a document chose: an object's path, a property's
/// name, a class. A hash is its own hash code, and a table that finds a bucket by the remainder against a prime can be made to put every key
/// in one bucket by choosing keys that share a remainder (multiples of the prime): fifty-seven thousand properties of about 340 KB chain
/// twenty thousand inserts in a bucket, which is seconds, and every copy of the map pays it again. The hash code here is salted for the
/// process (<see cref="HashCode"/> is seeded at random when the process starts), so what a file chooses does not choose the buckets. The order of a
/// map lives in its key list and never in a table, so what is written is the same in every process.
/// </summary>
internal static class KeyHash
{
    /// <summary>The comparer of every table the engine keys by a hash of its own input.</summary>
    public static readonly IEqualityComparer<uint> Comparer = new Salted();

    private sealed class Salted : IEqualityComparer<uint>
    {
        public bool Equals(uint x, uint y) => x == y;

        public int GetHashCode(uint key) => HashCode.Combine(key);
    }

    public static HashSet<uint> Set() => new(Comparer);

    public static HashSet<uint> Set(IEnumerable<uint> keys) => new(keys, Comparer);

    public static Dictionary<uint, T> Map<T>(int capacity = 0) => new(capacity, Comparer);
}

/// <summary>The rules <c>ltk_meta</c> keeps about kinds.</summary>
public static class PropKinds
{
    /// <summary>Whether a byte is a kind in the current numbering.</summary>
    public static bool IsDefined(byte raw) => raw <= 18 || raw is >= 0x80 and <= 0x87;

    /// <summary>A container, an unordered container, an option or a map: a kind that may not hold one of those.</summary>
    public static bool IsContainer(PropKind kind) => kind is PropKind.Container or PropKind.UnorderedContainer or PropKind.Optional or PropKind.Map;

    /// <summary>The kinds a map may be keyed by: every leaf kind up to <see cref="PropKind.WadChunkLink"/>.</summary>
    public static bool IsValidMapKey(PropKind kind) => (byte)kind <= 18;

    /// <summary>The width of a value that has one; -1 for a string, a struct and the containers.</summary>
    public static int FixedWidth(PropKind kind) => kind switch
    {
        PropKind.None => 0,
        PropKind.Bool or PropKind.I8 or PropKind.U8 or PropKind.BitBool => 1,
        PropKind.I16 or PropKind.U16 => 2,
        PropKind.I32 or PropKind.U32 or PropKind.F32 or PropKind.Color or PropKind.Hash or PropKind.ObjectLink => 4,
        PropKind.I64 or PropKind.U64 or PropKind.Vector2 or PropKind.WadChunkLink => 8,
        PropKind.Vector3 => 12,
        PropKind.Vector4 => 16,
        PropKind.Matrix44 => 64,
        _ => -1,
    };

    /// <summary>Whether a kind is one of the integer kinds <c>I8</c> to <c>U64</c>.</summary>
    public static bool IsInteger(PropKind kind) => kind is >= PropKind.I8 and <= PropKind.U64;

    /// <summary><c>Kind::unpack</c>: the kind a byte names under the current numbering, or under the legacy one (written before
    /// <c>WadChunkLink</c> existed, so the complex kinds sit lower). Null when the byte names none.</summary>
    public static PropKind? Unpack(byte raw, bool legacy)
    {
        if (!legacy) return IsDefined(raw) ? (PropKind)raw : null;
        byte fudged = raw;
        if (fudged >= (byte)PropKind.WadChunkLink && fudged < (byte)PropKind.Container)
        {
            fudged -= (byte)PropKind.WadChunkLink;
            fudged |= (byte)PropKind.Container;
        }
        if (fudged >= (byte)PropKind.UnorderedContainer) fudged = unchecked((byte)(fudged + 1));
        return IsDefined(fudged) ? (PropKind)fudged : null;
    }
}

/// <summary>
/// M817: an insertion-ordered map from a 32-bit hash to a value, <c>IndexMap&lt;BinHash, T&gt;</c> as <c>ltk_meta</c> uses it for
/// an object table and for a struct's properties: setting a key that is held replaces the value in the key's slot, a new key goes
/// at the end, and removing a key keeps the order of the rest (<c>shift_remove</c>). Small maps are searched, larger ones indexed.
///
/// <para><b>Concurrency.</b> A map nobody is changing may be read by any number of threads at once, its lazily built index included: the
/// index is built apart and published whole. A map that is being changed belongs to one thread.</para>
///
/// <para><b>Removal</b> costs two shifts and no rebuild: the entries after the removed one move up, and the removed key's number goes into a
/// sorted list, which moves the numbers after its place. The index keeps every key's sequence number (the order the keys were added in) and that
/// list of the numbers removed since, so a position is a number less the removals before it, and no removal rebuilds the index. Both shifts are
/// what <c>Remove</c> reports, so that the caller can charge them.</para>
/// </summary>
public sealed class OrderedMap<T>
{
    private const int IndexThreshold = 12;

    /// <summary>A key and a value are twelve bytes of a reservation; a map reserves no more than <see cref="PropLimits.MaxReserveBytes"/> of them ahead of the data.</summary>
    private const int MaxReserve = PropLimits.MaxReserveBytes / 12;

    private readonly List<uint> _keys;
    private readonly List<T> _values;
    private Slots? _slots;

    public OrderedMap() { _keys = new(); _values = new(); }

    public OrderedMap(int capacity)
    {
        capacity = Math.Clamp(capacity, 0, MaxReserve);
        _keys = new(capacity);
        _values = new(capacity);
    }

    /// <summary>The index of a large map.</summary>
    private sealed class Slots
    {
        public readonly Dictionary<uint, int> Of;
        public readonly List<int> Removed = new();
        public int Next;

        public Slots(List<uint> keys)
        {
            Of = KeyHash.Map<int>(keys.Count);
            for (int i = 0; i < keys.Count; i++) Of[keys[i]] = i;
            Next = keys.Count;
        }

        /// <summary>Where a key is now: its number less the numbers removed before it.</summary>
        public int Position(int slot)
        {
            if (Removed.Count == 0) return slot;
            int at = Removed.BinarySearch(slot);
            return slot - (at < 0 ? ~at : at);
        }
    }

    public int Count => _keys.Count;

    public uint KeyAt(int index) => _keys[index];

    public T ValueAt(int index) => _values[index];

    public void SetValueAt(int index, T value) => _values[index] = value;

    /// <summary>The position of <paramref name="key"/>, or -1.</summary>
    public int IndexOf(uint key)
    {
        if (_keys.Count <= IndexThreshold)
        {
            for (int i = 0; i < _keys.Count; i++)
                if (_keys[i] == key) return i;
            return -1;
        }
        var slots = Volatile.Read(ref _slots) ?? Publish();
        return slots.Of.TryGetValue(key, out int slot) ? slots.Position(slot) : -1;
    }

    /// <summary>Builds the index apart and publishes it whole; a reader that loses the race uses the winner's.</summary>
    private Slots Publish()
    {
        var built = new Slots(_keys);
        return Interlocked.CompareExchange(ref _slots, built, null) ?? built;
    }

    public bool ContainsKey(uint key) => IndexOf(key) >= 0;

    public bool TryGetValue(uint key, out T value)
    {
        int at = IndexOf(key);
        if (at < 0) { value = default!; return false; }
        value = _values[at];
        return true;
    }

    /// <summary><c>IndexMap::insert</c>: a held key keeps its slot and takes the new value; returns whether the key was held.</summary>
    public bool Set(uint key, T value)
    {
        int at = IndexOf(key);
        if (at >= 0) { _values[at] = value; return true; }
        _keys.Add(key);
        _values.Add(value);
        var slots = _slots;
        if (slots is not null) slots.Of.Add(key, slots.Next++);
        return false;
    }

    /// <summary><c>IndexMap::shift_remove</c>.</summary>
    public bool Remove(uint key) => Remove(key, out _);

    /// <summary>Removes <paramref name="key"/>; <paramref name="shifted"/> is how many entries moved: those that moved up to close the gap, and
    /// those of the list of removed numbers that moved over to make room for this one (deleting the newest first puts every number at the front).</summary>
    internal bool Remove(uint key, out int shifted)
    {
        shifted = 0;
        int at = IndexOf(key);
        if (at < 0) return false;
        shifted = _keys.Count - at - 1;
        _keys.RemoveAt(at);
        _values.RemoveAt(at);
        var slots = _slots;
        if (slots is not null)
        {
            int slot = slots.Of[key];
            slots.Of.Remove(key);
            int insert = slots.Removed.BinarySearch(slot);
            if (insert < 0) insert = ~insert;
            shifted += slots.Removed.Count - insert;
            slots.Removed.Insert(insert, slot);
            // a map that has shrunk to a handful is searched again, and one that has lost half its numbers is numbered anew when it is next asked
            if (_keys.Count <= IndexThreshold || (slots.Removed.Count > 1024 && slots.Removed.Count > _keys.Count / 2)) _slots = null;
        }
        return true;
    }

    public void Clear()
    {
        _keys.Clear();
        _values.Clear();
        _slots = null;
    }

    public IEnumerable<uint> Keys => _keys;

    public IEnumerable<T> Values => _values;
}

/// <summary>What the patch type rule compares (<c>ValueShape</c>): the kind, a container's item and a map's key kind, and the class of
/// an embed. A pointer's class is not part of it.</summary>
public readonly record struct PropShape(PropKind Kind, PropKind? Item, PropKind? Key, uint? Class)
{
    /// <summary>The Rust <c>Display</c>: the kind, its item kinds in brackets, and an embed's class.</summary>
    public override string ToString()
    {
        var text = new StringBuilder(Kind.ToString());
        if (Key is { } key && Item is { } item) text.Append('[').Append(key).Append(", ").Append(item).Append(']');
        else if (Item is { } only) text.Append('[').Append(only).Append(']');
        if (Class is { } cls) text.Append(' ').Append(cls.ToString("x8"));
        return text.ToString();
    }
}

/// <summary>
/// M817: one property value of a bin, the variants of <c>PropertyValueEnum</c>. The model is the engine's own, mirroring
/// <c>ltk_meta</c>'s kinds, so that what is decoded and what is written back is under this crate's control.
/// </summary>
public abstract class PropValue
{
    public abstract PropKind Kind { get; }

    /// <summary>A deep copy (<c>Clone</c>). A value the engine puts into a tree it also keeps elsewhere is copied first, because
    /// a struct is edited in place. A value that nests more than 256 containers is not copied: the copy throws a <see cref="GameDataException"/> of
    /// kind <see cref="GameDataErrorKind.Bin"/>, as the codec refuses to read or write one.</summary>
    public PropValue Clone() => Clone(0, null);

    /// <summary>The copy of a value that stands <paramref name="depth"/> containers down, charged to <paramref name="meter"/> when it has one.</summary>
    internal abstract PropValue Clone(int depth, WorkMeter? meter);

    /// <summary><c>ValueShape::of</c>.</summary>
    public virtual PropShape Shape => new(Kind, null, null, null);

    /// <summary>The derived <c>PartialEq</c> of <c>PropertyValueEnum</c>: the same variant and equal fields. Floats compare as IEEE
    /// numbers (<c>NaN</c> is not equal to itself, <c>-0</c> equals <c>0</c>), a struct compares its properties as a set.</summary>
    public static bool Equal(PropValue? a, PropValue? b) => Equal(a, b, 0);

    private static bool Equal(PropValue? a, PropValue? b, int depth)
    {
        if (a is null || b is null || a.Kind != b.Kind) return false;
        switch (a)
        {
            case PropNone: return true;
            case PropBool x: return x.Value == ((PropBool)b).Value;
            case PropInt x: return x.Bits == ((PropInt)b).Bits;
            case PropF32 x: return x.Value == ((PropF32)b).Value;
            case PropVector x:
            {
                var y = (PropVector)b;
                for (int i = 0; i < x.Components.Length; i++)
                    if (x.Components[i] != y.Components[i]) return false;
                return true;
            }
            case PropColor x: return x.Packed == ((PropColor)b).Packed;
            case PropString x: return string.Equals(x.Value, ((PropString)b).Value, StringComparison.Ordinal);
            case PropHash x: return x.Value == ((PropHash)b).Value;
            case PropFile x: return x.Value == ((PropFile)b).Value;
        }

        // only a container, an option, a map or a struct is held to the depth, as the codec holds them: a leaf may sit at the 256th level
        if (depth >= PropLimits.MaxDepth) throw PropLimits.TooDeep();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (a)
        {
            case PropList x:
            {
                var y = (PropList)b;
                if (x.ItemKind != y.ItemKind || x.Items.Count != y.Items.Count) return false;
                for (int i = 0; i < x.Items.Count; i++)
                    if (!Equal(x.Items[i], y.Items[i], depth + 1)) return false;
                return true;
            }
            case PropOption x:
            {
                var y = (PropOption)b;
                return x.ItemKind == y.ItemKind && (x.Value is null ? y.Value is null : y.Value is not null && Equal(x.Value, y.Value, depth + 1));
            }
            case PropMapValue x:
            {
                var y = (PropMapValue)b;
                if (x.KeyKind != y.KeyKind || x.ValueKind != y.ValueKind || x.Entries.Count != y.Entries.Count) return false;
                for (int i = 0; i < x.Entries.Count; i++)
                    if (!Equal(x.Entries[i].Key, y.Entries[i].Key, depth + 1) || !Equal(x.Entries[i].Value, y.Entries[i].Value, depth + 1)) return false;
                return true;
            }
            case PropStruct x:
            {
                var y = (PropStruct)b;
                if (x.ClassHash != y.ClassHash || x.Properties.Count != y.Properties.Count) return false;
                for (int i = 0; i < x.Properties.Count; i++)
                    if (!y.Properties.TryGetValue(x.Properties.KeyAt(i), out var other) || !Equal(x.Properties.ValueAt(i), other, depth + 1)) return false;
                return true;
            }
        }
        return false;
    }
}

public sealed class PropNone : PropValue
{
    public static readonly PropNone Instance = new();
    public override PropKind Kind => PropKind.None;
    internal override PropValue Clone(int depth, WorkMeter? meter) => this;
}

/// <summary>A <see cref="PropKind.Bool"/> or a <see cref="PropKind.BitBool"/> (the flag): one byte, 0 or 1.</summary>
public sealed class PropBool : PropValue
{
    private readonly PropKind _kind;
    public PropBool(PropKind kind, bool value) { _kind = kind; Value = value; }
    public bool Value { get; }
    public override PropKind Kind => _kind;
    internal override PropValue Clone(int depth, WorkMeter? meter) => this;
}

/// <summary>An integer of one of the kinds <c>I8</c> to <c>U64</c>. <see cref="Bits"/> holds the value sign-extended (a signed kind) or
/// zero-extended (an unsigned one) to 64 bits.</summary>
public sealed class PropInt : PropValue
{
    private readonly PropKind _kind;
    public PropInt(PropKind kind, ulong bits) { _kind = kind; Bits = bits; }
    public ulong Bits { get; }
    public long Signed => unchecked((long)Bits);
    public override PropKind Kind => _kind;
    internal override PropValue Clone(int depth, WorkMeter? meter) => this;
}

public sealed class PropF32 : PropValue
{
    public PropF32(float value) { Value = value; }
    public float Value { get; }
    public override PropKind Kind => PropKind.F32;
    internal override PropValue Clone(int depth, WorkMeter? meter) => this;
}

/// <summary>A vector or a matrix: 2, 3, 4 or 16 floats. A matrix holds its sixteen floats in wire (row) order, which is also the
/// order a declaration lists them in.</summary>
public sealed class PropVector : PropValue
{
    private readonly PropKind _kind;
    public PropVector(PropKind kind, float[] components) { _kind = kind; Components = components; }
    public float[] Components { get; }
    public override PropKind Kind => _kind;
    internal override PropValue Clone(int depth, WorkMeter? meter) => new PropVector(_kind, (float[])Components.Clone());
}

/// <summary>An RGBA colour of four bytes, red first.</summary>
public sealed class PropColor : PropValue
{
    public PropColor(uint packed) { Packed = packed; }
    /// <summary>The four bytes as they lie in the file: red is the lowest byte.</summary>
    public uint Packed { get; }
    public override PropKind Kind => PropKind.Color;
    internal override PropValue Clone(int depth, WorkMeter? meter) => this;
}

public sealed class PropString : PropValue
{
    public PropString(string value) { Value = value; }
    public string Value { get; }
    public override PropKind Kind => PropKind.String;
    internal override PropValue Clone(int depth, WorkMeter? meter) => this;
}

/// <summary>A <see cref="PropKind.Hash"/> or an <see cref="PropKind.ObjectLink"/>: one 32-bit hash.</summary>
public sealed class PropHash : PropValue
{
    private readonly PropKind _kind;
    public PropHash(PropKind kind, uint value) { _kind = kind; Value = value; }
    public uint Value { get; }
    public override PropKind Kind => _kind;
    internal override PropValue Clone(int depth, WorkMeter? meter) => this;
}

/// <summary>A <see cref="PropKind.WadChunkLink"/>: one 64-bit hash.</summary>
public sealed class PropFile : PropValue
{
    public PropFile(ulong value) { Value = value; }
    public ulong Value { get; }
    public override PropKind Kind => PropKind.WadChunkLink;
    internal override PropValue Clone(int depth, WorkMeter? meter) => this;
}

/// <summary>A <see cref="PropKind.Container"/> or an <see cref="PropKind.UnorderedContainer"/>: items of one declared kind.</summary>
public sealed class PropList : PropValue
{
    private readonly PropKind _kind;

    public PropList(PropKind kind, PropKind itemKind, List<PropValue> items) { _kind = kind; ItemKind = itemKind; Items = items; }

    public PropKind ItemKind { get; }
    public List<PropValue> Items { get; }
    public override PropKind Kind => _kind;
    public override PropShape Shape => new(_kind, ItemKind, null, null);

    internal override PropValue Clone(int depth, WorkMeter? meter)
    {
        if (depth >= PropLimits.MaxDepth) throw PropLimits.TooDeep();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        meter?.Charge(Items.Count + 1);
        var items = new List<PropValue>(Math.Min(Items.Count, PropLimits.MaxReserveBytes / 8));
        foreach (var item in Items) items.Add(item.Clone(depth + 1, meter));
        return new PropList(_kind, ItemKind, items);
    }

    /// <summary><c>Container::new</c>: a container kind may not nest, and every item is of the item kind.</summary>
    public static bool TryCreate(PropKind kind, PropKind itemKind, List<PropValue> items, out PropList? list)
    {
        list = null;
        if (PropKinds.IsContainer(itemKind)) return false;
        foreach (var item in items)
            if (item.Kind != itemKind) return false;
        list = new PropList(kind, itemKind, items);
        return true;
    }
}

/// <summary>An <see cref="PropKind.Optional"/>: at most one value of the declared item kind, which an empty option declares too.</summary>
public sealed class PropOption : PropValue
{
    public PropOption(PropKind itemKind, PropValue? value) { ItemKind = itemKind; Value = value; }
    public PropKind ItemKind { get; }
    public PropValue? Value { get; private set; }

    /// <summary>Puts <paramref name="value"/> in the place of the held value (a patch replaces a value of its own shape).</summary>
    internal void Replace(PropValue value) => Value = value;
    public override PropKind Kind => PropKind.Optional;
    public override PropShape Shape => new(PropKind.Optional, ItemKind, null, null);
    internal override PropValue Clone(int depth, WorkMeter? meter)
    {
        if (depth >= PropLimits.MaxDepth) throw PropLimits.TooDeep();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        meter?.Charge(1);
        return new PropOption(ItemKind, Value?.Clone(depth + 1, meter));
    }

    /// <summary><c>Optional::new</c>.</summary>
    public static bool TryCreate(PropKind itemKind, PropValue? value, out PropOption? option)
    {
        option = null;
        if (PropKinds.IsContainer(itemKind) || (value is not null && value.Kind != itemKind)) return false;
        option = new PropOption(itemKind, value);
        return true;
    }
}

/// <summary>A <see cref="PropKind.Map"/>: entries in file order, keys of one declared leaf kind and values of another. Duplicate keys
/// are kept.</summary>
public sealed class PropMapValue : PropValue
{
    public PropMapValue(PropKind keyKind, PropKind valueKind, List<KeyValuePair<PropValue, PropValue>> entries)
    {
        KeyKind = keyKind;
        ValueKind = valueKind;
        Entries = entries;
    }

    public PropKind KeyKind { get; }
    public PropKind ValueKind { get; }
    public List<KeyValuePair<PropValue, PropValue>> Entries { get; }
    public override PropKind Kind => PropKind.Map;
    public override PropShape Shape => new(PropKind.Map, ValueKind, KeyKind, null);

    internal override PropValue Clone(int depth, WorkMeter? meter)
    {
        if (depth >= PropLimits.MaxDepth) throw PropLimits.TooDeep();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        meter?.Charge(Entries.Count + 1);
        var entries = new List<KeyValuePair<PropValue, PropValue>>(Math.Min(Entries.Count, PropLimits.MaxReserveBytes / 16));
        foreach (var entry in Entries) entries.Add(new(entry.Key.Clone(depth + 1, meter), entry.Value.Clone(depth + 1, meter)));
        return new PropMapValue(KeyKind, ValueKind, entries);
    }

    /// <summary><c>Map::new</c>.</summary>
    public static bool TryCreate(PropKind keyKind, PropKind valueKind, List<KeyValuePair<PropValue, PropValue>> entries, out PropMapValue? map)
    {
        map = null;
        if (!PropKinds.IsValidMapKey(keyKind) || PropKinds.IsContainer(valueKind)) return false;
        foreach (var entry in entries)
            if (entry.Key.Kind != keyKind || entry.Value.Kind != valueKind) return false;
        map = new PropMapValue(keyKind, valueKind, entries);
        return true;
    }
}

/// <summary>A <see cref="PropKind.Struct"/> (a pointer) or an <see cref="PropKind.Embedded"/>: a class and properties. A pointer of
/// class 0 is the null pointer; it holds no properties and takes four bytes on the wire, an embed of class 0 as well.</summary>
public sealed class PropStruct : PropValue
{
    private readonly PropKind _kind;

    public PropStruct(PropKind kind, uint classHash, OrderedMap<PropValue> properties)
    {
        _kind = kind;
        ClassHash = classHash;
        Properties = properties;
    }

    public static PropStruct NullPointer() => new(PropKind.Struct, 0, new OrderedMap<PropValue>());

    public uint ClassHash { get; }
    public OrderedMap<PropValue> Properties { get; }
    public override PropKind Kind => _kind;
    public override PropShape Shape => new(_kind, null, null, _kind == PropKind.Embedded ? ClassHash : null);

    internal override PropValue Clone(int depth, WorkMeter? meter)
    {
        if (depth >= PropLimits.MaxDepth) throw PropLimits.TooDeep();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return new PropStruct(_kind, ClassHash, CloneProperties(Properties, depth + 1, meter));
    }

    /// <summary>The properties of a struct (or of an object, at depth 0), each copied at <paramref name="depth"/>.</summary>
    internal static OrderedMap<PropValue> CloneProperties(OrderedMap<PropValue> properties, int depth, WorkMeter? meter)
    {
        meter?.Charge(properties.Count + 1);
        var copy = new OrderedMap<PropValue>(properties.Count);
        for (int i = 0; i < properties.Count; i++) copy.Set(properties.KeyAt(i), properties.ValueAt(i).Clone(depth, meter));
        return copy;
    }
}

/// <summary>One object of a bin: its path hash, its class, and its properties in file order.</summary>
public sealed class PropObject
{
    public PropObject(uint pathHash, uint classHash, OrderedMap<PropValue>? properties = null)
    {
        PathHash = pathHash;
        ClassHash = classHash;
        Properties = properties ?? new OrderedMap<PropValue>();
    }

    public uint PathHash { get; set; }
    public uint ClassHash { get; }
    public OrderedMap<PropValue> Properties { get; }

    public PropObject Clone() => Clone(null);

    internal PropObject Clone(WorkMeter? meter) => new(PathHash, ClassHash, PropStruct.CloneProperties(Properties, 0, meter));
}

/// <summary>A decoded PROP: its version as read, its dependencies, and its objects in file order. Writing always gives version 3.</summary>
public sealed class PropBin
{
    public uint Version { get; set; } = 3;
    public List<string> Dependencies { get; } = new();
    public OrderedMap<PropObject> Objects { get; } = new();
}

/// <summary>One record of a PTCH: set the property at <see cref="Path"/> inside object <see cref="ObjectHash"/> to <see cref="Value"/>.</summary>
public sealed class PatchRecord
{
    public PatchRecord(uint objectHash, PropertyPath path, PropValue value)
    {
        ObjectHash = objectHash;
        Path = path;
        Value = value;
    }

    public uint ObjectHash { get; }
    public PropertyPath Path { get; }
    public PropValue Value { get; }

    private string? _identity;

    /// <summary>The property the path names, however it is spelled (<see cref="EntryPhase.Identity"/>), worked out once.</summary>
    internal string Identity => _identity ??= EntryPhase.Identity(Path);
}

/// <summary>A decoded PTCH (<c>BinOverride</c>): the objects it deletes, the objects it adds, and its property records in file order.</summary>
public sealed class PtchBin
{
    public List<uint> Deleted { get; set; } = new();
    public OrderedMap<PropObject> Objects { get; set; } = new();
    public List<PatchRecord> Patches { get; set; } = new();
}
