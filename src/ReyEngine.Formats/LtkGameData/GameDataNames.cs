using System.Globalization;
using System.Text;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>The keys a body mapping reserves for a binding rather than for an entry name or a property path (<c>BindingKeyword</c>).</summary>
internal enum BindingKeyword { Overrides, Objects, AddLinks, RemoveLinks }

internal static class BindingKeywords
{
    public static BindingKeyword? Of(string key) => key switch
    {
        "overrides" => BindingKeyword.Overrides,
        "objects" => BindingKeyword.Objects,
        "links" or "+links" => BindingKeyword.AddLinks,
        "-links" => BindingKeyword.RemoveLinks,
        _ => null,
    };
}

/// <summary>
/// M817: a bin object named in a declaration: a path, or its hash as <c>0x</c> and exactly 8 hexadecimal digits (<c>EntryName</c>).
/// The spelling is kept as written, and two spellings of one hash are two names. A binding keyword is not one: a target body
/// carries its entry names beside its bindings, so the name would be read as the binding.
/// </summary>
public sealed class EntryName : IEquatable<EntryName>
{
    private EntryName(string text, bool isHash, uint hash)
    {
        Text = text;
        IsHash = isHash;
        ObjectHash = hash;
    }

    public string Text { get; }

    /// <summary>Whether the name is spelled as a hash: <c>0x</c> and 8 hexadecimal digits.</summary>
    public bool IsHash { get; }

    /// <summary>The bin object hash: FNV-1a over the lowercased path, or the spelled hash.</summary>
    public uint ObjectHash { get; }

    public override string ToString() => Text;

    public static bool TryCreate(string value, out EntryName? name, out GameDataErrorKind? error)
    {
        name = null;
        if (value.Length == 0) { error = GameDataErrorKind.EmptyEntryName; return false; }
        if (BindingKeywords.Of(value) is not null) { error = GameDataErrorKind.ReservedBindingKey; return false; }
        error = null;
        name = LtkHash.TryHex32(value, out uint hash)
            ? new EntryName(value, true, hash)
            : new EntryName(value, false, LtkHash.Fnv1aLower(value));
        return true;
    }

    /// <summary>The name of <paramref name="value"/>; throws <see cref="GameDataException"/> for an empty one or a binding keyword.</summary>
    public static EntryName Create(string value) =>
        TryCreate(value, out var name, out var error) ? name! : throw Refusal(error!.Value, value);

    /// <summary>The refusal of a spelling that is no entry name, located where <c>ltk_game_data</c> locates it: at <c>entries</c> for an empty one, and at
    /// the spelling for a binding keyword. <paramref name="entry"/> is the entry the spelling is read inside, when it is read inside one.</summary>
    internal static GameDataException Refusal(GameDataErrorKind kind, string value, string? entry = null)
    {
        string key = kind == GameDataErrorKind.EmptyEntryName ? "entries" : value;
        return new GameDataException(kind, value, entry is null ? key : $"entry {entry}: {key}");
    }

    public bool Equals(EntryName? other) => other is not null && IsHash == other.IsHash && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as EntryName);

    public override int GetHashCode() => HashCode.Combine(IsHash, Text);
}

/// <summary>A bin class: a name, or its hash as <c>0x</c> and 8 hexadecimal digits (<c>ClassName</c>).</summary>
public sealed class ClassName
{
    public ClassName(string text)
    {
        if (text.Length == 0) throw new GameDataException(GameDataErrorKind.EmptyClassName, null, "class");
        Text = text;
    }

    public string Text { get; }

    /// <summary>The spelled hash, or FNV-1a over the lowercased name.</summary>
    public uint ClassHash => LtkHash.Hash32Of(Text);

    public override string ToString() => Text;
}

/// <summary>A nonempty game lookup path, or a bare 16-digit hexadecimal chunk hash (<c>Target</c>).</summary>
public sealed class TargetName
{
    public TargetName(string text)
    {
        if (text.Length == 0) throw new GameDataException(GameDataErrorKind.EmptyTarget, null, "target");
        Text = text;
        IsHash = text.Length == 16 && text.All(char.IsAsciiHexDigit);
    }

    public string Text { get; }

    public bool IsHash { get; }

    /// <summary>The game's WAD chunk identifier: the hash the 16 digits spell, or the XXH64 of the lowercased path.</summary>
    public ulong ChunkHash => IsHash ? ulong.Parse(Text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) : LtkHash.Xxh64Path(Text);

    public override string ToString() => Text;
}

/// <summary>A dependency path of 1 to 65535 UTF-8 bytes (<c>LinkPath</c>).</summary>
public sealed class LinkPath
{
    public LinkPath(string text)
    {
        if (text.Length == 0 || Encoding.UTF8.GetByteCount(text) > ushort.MaxValue) throw new GameDataException(GameDataErrorKind.LinkPathLength, null, "links");
        Text = text;
    }

    public string Text { get; }

    public override string ToString() => Text;
}

/// <summary>
/// The layer-relative, forward-slash path of a <c>.ptch</c> override file (<c>OverridePath</c>). It is nonempty, relative, has no
/// backslash, no drive prefix, no empty, <c>.</c> or <c>..</c> segment, and a <c>.ptch</c> extension compared ASCII
/// case-insensitively. A <c>.rito</c> path is refused with an error naming the extension.
/// </summary>
public sealed class OverridePath : IEquatable<OverridePath>
{
    public OverridePath(string text)
    {
        if (text.Length == 0) throw new GameDataException(GameDataErrorKind.EmptyOverridePath, null, "overrides");
        if (text.Contains('\\')) throw new GameDataException(GameDataErrorKind.OverridePathBackslash, null, "overrides");
        if (text[0] == '/') throw new GameDataException(GameDataErrorKind.OverridePathAbsolute, null, "overrides");
        string[] parts = text.Split('/');
        if (parts[0].Contains(':')) throw new GameDataException(GameDataErrorKind.OverridePathAbsolute, null, "overrides");
        foreach (string part in parts)
            if (part is "" or "." or "..") throw new GameDataException(GameDataErrorKind.OverridePathSegment, null, "overrides");
        string file = parts[^1];
        int dot = file.LastIndexOf('.');
        if (dot >= 0)
        {
            string extension = file[(dot + 1)..];
            if (dot > 0 && extension.Equals("ptch", StringComparison.OrdinalIgnoreCase)) { Text = text; return; }
            if (extension.Equals("rito", StringComparison.OrdinalIgnoreCase)) throw new GameDataException(GameDataErrorKind.OverridePathRito, null, "overrides");
        }
        throw new GameDataException(GameDataErrorKind.OverridePathExtension, null, "overrides");
    }

    public string Text { get; }

    public override string ToString() => Text;

    public bool Equals(OverridePath? other) => other is not null && Text == other.Text;

    public override bool Equals(object? obj) => Equals(obj as OverridePath);

    public override int GetHashCode() => Text.GetHashCode();
}

/// <summary>
/// A value of the installed game, named by entry and path (<c>Reference</c>): <c>&lt;entry name&gt;:&lt;property path&gt;</c>, split
/// at the first <c>:</c>, so the entry holds none and the path may.
/// </summary>
public sealed class GameDataReference
{
    private GameDataReference(EntryName entry, PropertyPath path) { Entry = entry; Path = path; }

    public EntryName Entry { get; }

    public PropertyPath Path { get; }

    /// <summary>The spelling: the entry, a colon, and the path as written.</summary>
    public override string ToString() => Entry.Text + ":" + Path.Text;

    /// <summary>The reference <paramref name="text"/> spells; null for text with no <c>:</c>, an entry name the name rule refuses, or a
    /// path the path rule refuses.</summary>
    public static GameDataReference? TryParse(string text)
    {
        int colon = text.IndexOf(':');
        if (colon < 0) return null;
        if (!EntryName.TryCreate(text[..colon], out var entry, out _)) return null;
        if (!PropertyPath.TryParse(text[(colon + 1)..], out var path, out _)) return null;
        return new GameDataReference(entry!, path!);
    }
}

/// <summary>The operation of a property edit: the sign in front of its key.</summary>
public enum Sign { Set, Add, Remove }

public static class Signs
{
    /// <summary>The sign of a key and the key without it.</summary>
    public static (Sign Sign, string Path) Of(string key)
    {
        if (key.StartsWith('+')) return (Sign.Add, key[1..]);
        if (key.StartsWith('-')) return (Sign.Remove, key[1..]);
        return (Sign.Set, key);
    }

    public static string Text(this Sign sign) => sign switch { Sign.Add => "+", Sign.Remove => "-", _ => "" };
}

/// <summary>One signed property path with its value (<c>PropertyEdit</c>). A one-key mapping keyed by a type name is a pin on every
/// property; any other mapping on a struct-typed property descends into it at apply time.</summary>
public sealed class PropertyEdit
{
    public PropertyEdit(PropertyPath path, Sign sign, GameDataLiteral value)
    {
        Path = path;
        Sign = sign;
        Value = value;
    }

    public PropertyPath Path { get; }

    public Sign Sign { get; }

    public GameDataLiteral Value { get; }

    /// <summary>The signed key as spelled.</summary>
    public string Key => Sign.Text() + Path.Text;

    /// <summary>The edit of the key <paramref name="key"/> with <paramref name="value"/>: its path must parse, and every struct pin and
    /// reference in the value must be well formed.</summary>
    public static PropertyEdit Parse(string key, GameDataLiteral value)
    {
        var (sign, spelled) = Signs.Of(key);
        if (!PropertyPath.TryParse(spelled, out var path, out var error))
            throw new GameDataException(GameDataErrorKind.InvalidPropertyPath, error!.Message, key);
        if (value.CheckPins() is { } bad) throw new GameDataException(bad, null, key);
        return new PropertyEdit(path!, sign, value);
    }

    /// <summary><see cref="Parse"/> without the exception: whether <paramref name="key"/> and <paramref name="value"/> make an edit. Whatever is wrong with
    /// either is the same answer, which is all a block's inner key needs, and a block of a hundred thousand bad keys does not raise a hundred thousand exceptions.</summary>
    public static bool TryParse(string key, GameDataLiteral value, out PropertyEdit? edit)
    {
        edit = null;
        var (sign, spelled) = Signs.Of(key);
        if (!PropertyPath.TryParse(spelled, out var path, out _)) return false;
        if (value.CheckPins() is not null) return false;
        edit = new PropertyEdit(path!, sign, value);
        return true;
    }
}
