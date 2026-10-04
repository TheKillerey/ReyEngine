using System.Runtime.CompilerServices;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M817: the literal a property edit carries (<c>ltk_game_data::Value</c>): null, boolean, integer, float, string, list, or
/// mapping, in spelled order. The build reads it by the property's type.
///
/// <para>Two one-key mappings mean more than a mapping. A type name pins the type the value reads as (<see cref="Pinned"/>), and
/// <c>ref</c> names a value of the installed game to read instead of a literal (<see cref="ReferenceText"/>).</para>
/// </summary>
public abstract class GameDataLiteral
{
    /// <summary>The type names a pin spells, each with the kind it names (<c>TYPE_NAMES</c>).</summary>
    private static readonly Dictionary<string, PropKind> PinKinds = new(StringComparer.Ordinal)
    {
        ["bool"] = PropKind.Bool, ["i8"] = PropKind.I8, ["i16"] = PropKind.I16, ["i32"] = PropKind.I32, ["i64"] = PropKind.I64,
        ["u8"] = PropKind.U8, ["u16"] = PropKind.U16, ["u32"] = PropKind.U32, ["u64"] = PropKind.U64, ["f32"] = PropKind.F32,
        ["vec2"] = PropKind.Vector2, ["vec3"] = PropKind.Vector3, ["vec4"] = PropKind.Vector4, ["mtx44"] = PropKind.Matrix44,
        ["rgba"] = PropKind.Color, ["string"] = PropKind.String, ["hash"] = PropKind.Hash, ["file"] = PropKind.WadChunkLink,
        ["link"] = PropKind.ObjectLink, ["flag"] = PropKind.BitBool, ["option"] = PropKind.Optional, ["pointer"] = PropKind.Struct,
        ["embed"] = PropKind.Embedded,
    };

    /// <summary>The one-key mapping key a reference spells. Not a type name, so not a pin.</summary>
    public const string ReferenceKey = "ref";

    /// <summary><c>kind_named</c>: the kind a type name names, or null for a name that is not a type name.</summary>
    public static PropKind? KindNamed(string name) => PinKinds.TryGetValue(name, out var kind) ? kind : null;

    /// <summary>The type name and the pinned value of a one-key mapping whose key is a type name, or null.</summary>
    public (string Name, GameDataLiteral Inner)? Pinned()
    {
        if (this is GdMapping { Count: 1 } mapping && PinKinds.ContainsKey(mapping.Entries[0].Key))
            return (mapping.Entries[0].Key, mapping.Entries[0].Value);
        return null;
    }

    /// <summary>The text of a one-key mapping keyed <c>ref</c> whose value is a string, or null.</summary>
    public string? ReferenceText() =>
        this is GdMapping { Count: 1 } mapping && mapping.Entries[0].Key == ReferenceKey && mapping.Entries[0].Value is GdString text ? text.Value : null;

    /// <summary>Whether the value is a one-key mapping keyed <c>ref</c>, whatever its value.</summary>
    private bool IsReferenceKey() => this is GdMapping { Count: 1 } mapping && mapping.Entries[0].Key == ReferenceKey;

    /// <summary>Every reference in the value, in spelled order, duplicates included. A <c>ref</c> whose text does not parse is not one.</summary>
    public List<GameDataReference> References()
    {
        var found = new List<GameDataReference>();
        CollectReferences(found);
        return found;
    }

    internal void CollectReferences(List<GameDataReference> found)
    {
        if (ReferenceText() is { } text)
        {
            if (GameDataReference.TryParse(text) is { } reference) found.Add(reference);
            return;
        }
        switch (this)
        {
            case GdList list:
                RuntimeHelpers.EnsureSufficientExecutionStack();
                foreach (var item in list.Items) item.CollectReferences(found);
                break;
            case GdMapping mapping:
                RuntimeHelpers.EnsureSufficientExecutionStack();
                foreach (var entry in mapping.Entries) entry.Value.CollectReferences(found);
                break;
        }
    }

    /// <summary>
    /// Checks every struct pin and every reference in the value. A <c>pointer</c> pin's value is null, the empty mapping, or a mapping;
    /// an <c>embed</c> pin's value is a mapping. The mapping holds <c>class</c>, a string, or <c>set</c>, a mapping, or both, and
    /// nothing else. A <c>ref</c> key's value is a string a reference parses from. Null when the value is well formed, else the code.
    /// </summary>
    /// <exception cref="InsufficientExecutionStackException">The value is nested past what the stack holds, which a literal read from JSON never is
    /// (the reader stops at 127 containers) but one built by hand may be. <see cref="GameDataDocument.FromLiteral"/> turns it into a <see cref="GameDataException"/>.</exception>
    public GameDataErrorKind? CheckPins()
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (IsReferenceKey())
            return ReferenceText() is { } text
                ? GameDataReference.TryParse(text) is null ? GameDataErrorKind.ReferenceShape : null
                : GameDataErrorKind.ReferenceShape;
        switch (this)
        {
            case GdList list:
                foreach (var item in list.Items)
                    if (item.CheckPins() is { } bad) return bad;
                return null;
            case GdMapping mapping:
                if (Pinned() is { } pin && pin.Name is "pointer" or "embed")
                {
                    switch (pin.Inner)
                    {
                        case GdNull when pin.Name == "pointer":
                            return null;
                        case GdMapping fields:
                            if ((fields.Count == 0 && pin.Name == "embed")
                                || fields.Entries.Any(e => e.Key is not ("class" or "set"))
                                || (fields.Get("class") is { } cls && cls is not GdString))
                                return GameDataErrorKind.StructPinShape;
                            switch (fields.Get("set"))
                            {
                                case null: return null;
                                case GdMapping set:
                                    foreach (var entry in set.Entries)
                                        if (entry.Value.CheckPins() is { } bad) return bad;
                                    return null;
                                default: return GameDataErrorKind.StructPinShape;
                            }
                        default:
                            return GameDataErrorKind.StructPinShape;
                    }
                }
                foreach (var entry in mapping.Entries)
                    if (entry.Value.CheckPins() is { } bad) return bad;
                return null;
            default:
                return null;
        }
    }
}

public sealed class GdNull : GameDataLiteral
{
    public static readonly GdNull Instance = new();
    private GdNull() { }
}

public sealed class GdBool : GameDataLiteral
{
    public static readonly GdBool True = new(true);
    public static readonly GdBool False = new(false);
    private GdBool(bool value) { Value = value; }
    public bool Value { get; }
    public static GdBool Of(bool value) => value ? True : False;
}

/// <summary>An integer of the union of the <c>i64</c> and <c>u64</c> ranges.</summary>
public sealed class GdInteger : GameDataLiteral
{
    public GdInteger(Int128 value) { Value = value; }
    public Int128 Value { get; }
}

/// <summary>A float, or an integer past the integer ranges, or <c>-0</c>.</summary>
public sealed class GdFloat : GameDataLiteral
{
    public GdFloat(double value) { Value = value; }
    public double Value { get; }
}

public sealed class GdString : GameDataLiteral
{
    public GdString(string value) { Value = value; }
    public string Value { get; }
}

public sealed class GdList : GameDataLiteral
{
    public GdList(List<GameDataLiteral> items) { Items = items; }
    public List<GameDataLiteral> Items { get; }
}

/// <summary>A mapping in spelled order. A duplicate key does not load.</summary>
public sealed class GdMapping : GameDataLiteral
{
    public GdMapping(List<KeyValuePair<string, GameDataLiteral>> entries) { Entries = entries; }

    public List<KeyValuePair<string, GameDataLiteral>> Entries { get; }

    public int Count => Entries.Count;

    public GameDataLiteral? Get(string key)
    {
        foreach (var entry in Entries)
            if (string.Equals(entry.Key, key, StringComparison.Ordinal)) return entry.Value;
        return null;
    }
}
