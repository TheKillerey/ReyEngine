using System.Globalization;
using System.Text;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>M817: why a string is not a property path (<c>PropertyPathErrorKind</c>).</summary>
public enum PropertyPathErrorKind
{
    EmptySegment,
    UnexpectedCharacter,
    UnbalancedBracket,
    DoubleSubscript,
    InvalidIndex,
    InvalidKey,
    TooLong,
}

/// <summary>M817: the first place a string stopped being a property path. <see cref="Offset"/> counts UTF-8 bytes, as ltk_meta's does,
/// and <see cref="Message"/> is its <c>Display</c>.</summary>
public sealed record PropertyPathError(int Offset, PropertyPathErrorKind Kind, int Character = 0, int Length = 0)
{
    public string Message => $"{KindText} at byte {Offset}";

    private string KindText => Kind switch
    {
        PropertyPathErrorKind.EmptySegment => "expected a property name",
        PropertyPathErrorKind.UnexpectedCharacter => $"unexpected character {RustChar.Debug(Character)}",
        PropertyPathErrorKind.UnbalancedBracket => "unbalanced bracket",
        PropertyPathErrorKind.DoubleSubscript => "a segment can only have one subscript",
        PropertyPathErrorKind.InvalidIndex => "expected a non-negative integer index",
        PropertyPathErrorKind.InvalidKey => "expected a JSON number, string or boolean key",
        _ => $"path is {Length} bytes, the limit is 65535",
    };
}

/// <summary>The JSON scalar inside a <c>{...}</c> subscript (<c>KeyLiteral</c>): <c>true</c> or <c>false</c>, a number kept as the text
/// written, or a string, unescaped.</summary>
public sealed record KeyLiteral
{
    private KeyLiteral(KeyLiteralKind kind, bool flag, string text) { Kind = kind; Flag = flag; Text = text; }

    public KeyLiteralKind Kind { get; }
    public bool Flag { get; }
    /// <summary>The number's text, or the string unescaped.</summary>
    public string Text { get; }

    public static KeyLiteral Bool(bool value) => new(KeyLiteralKind.Bool, value, "");
    public static KeyLiteral Number(string text) => new(KeyLiteralKind.Number, false, text);
    public static KeyLiteral String(string text) => new(KeyLiteralKind.String, false, text);

    /// <summary>The <c>Display</c>, which a group's identity is built from: a number as written, a string re-escaped
    /// (<c>\" \\ \b \f \n \r \t</c>, other characters below 0x20 as <c>\u00xx</c>) between quotes.</summary>
    public override string ToString()
    {
        switch (Kind)
        {
            case KeyLiteralKind.Bool: return Flag ? "true" : "false";
            case KeyLiteralKind.Number: return Text;
        }
        var text = new StringBuilder(Text.Length + 2).Append('"');
        foreach (char c in Text)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case < ' ': text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: text.Append(c); break;
            }
        }
        return text.Append('"').ToString();
    }
}

public enum KeyLiteralKind { Bool, Number, String }

/// <summary>One <c>name[subscript]</c> piece of a property path.</summary>
public sealed class PathSegment
{
    public PathSegment(string name, uint? index, KeyLiteral? key)
    {
        Name = name;
        Index = index;
        Key = key;
    }

    /// <summary>The property name, exactly as written.</summary>
    public string Name { get; }
    /// <summary><c>[i]</c>: an element of a container, or the value inside an option.</summary>
    public uint? Index { get; }
    /// <summary><c>{k}</c>: an entry of a map.</summary>
    public KeyLiteral? Key { get; }

    public bool HasSubscript => Index is not null || Key is not null;

    /// <summary>The <c>Display</c>: the name, then the subscript in its canonical spelling.</summary>
    public override string ToString() => Index is { } i ? Name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]"
        : Key is { } k ? Name + "{" + k + "}" : Name;
}

/// <summary>
/// M817: a validated property path (<c>ltk_meta::path::PropertyPath</c>), the language a patch record and a declaration key name a
/// property in: <c>Position.UIRect.Size</c>, <c>Elements[3]</c>, <c>Lookup{"weapon"}</c>. The text is kept as written, so the
/// spelling a diagnostic reports is the author's. The grammar (<c>path/parse.rs</c>) is walked over the UTF-8 bytes, which is
/// what makes an error's offset the one ltk_meta reports.
/// </summary>
public sealed class PropertyPath
{
    /// <summary>The longest path that can be written to a file: <c>pathLen</c> on the wire is a <c>u16</c>.</summary>
    public const int MaxLength = ushort.MaxValue;

    private PropertyPath(string text, IReadOnlyList<PathSegment> segments)
    {
        Text = text;
        Segments = segments;
    }

    public string Text { get; }
    public IReadOnlyList<PathSegment> Segments { get; }

    public override string ToString() => Text;

    public static bool TryParse(string text, out PropertyPath? path, out PropertyPathError? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        var segments = new List<PathSegment>(4);
        error = Parse(utf8, segments);
        path = error is null ? new PropertyPath(text, segments) : null;
        return error is null;
    }

    public static PropertyPath Parse(string text) =>
        TryParse(text, out var path, out var error) ? path! : throw new FormatException(error!.Message);

    /// <summary>The first <paramref name="count"/> segments, as the path <c>a.b</c> that names them. The text is the segments'
    /// <c>Display</c> joined by <c>.</c>.</summary>
    public PropertyPath Prefix(int count)
    {
        if (count == Segments.Count) return this;
        var segments = new List<PathSegment>(count);
        for (int i = 0; i < count; i++) segments.Add(Segments[i]);
        return new PropertyPath(string.Join('.', segments), segments);
    }

    /// <summary><c>format!("{prefix}.{path}")</c> read back as a path: the segments of both, and the text joined at a dot.</summary>
    /// <exception cref="GameDataException">The joined text is longer than <see cref="MaxLength"/> bytes. The crate reads the joined text back with an
    /// <c>expect</c> that panics there; this is the same end without the panic, a <see cref="GameDataErrorKind.Bin"/>: a path that long cannot be written to a record.</exception>
    public static PropertyPath Join(PropertyPath prefix, PropertyPath path)
    {
        // a character is at most three bytes in a UTF-16 string, so the bytes are counted only for a path that could be too long
        if ((long)(prefix.Text.Length + path.Text.Length + 1) * 3 > MaxLength
            && (long)Encoding.UTF8.GetByteCount(prefix.Text) + 1 + Encoding.UTF8.GetByteCount(path.Text) > MaxLength)
            throw new GameDataException(GameDataErrorKind.Bin, $"A property path of more than {MaxLength} bytes cannot be written: {prefix.Text.Length + path.Text.Length + 1} characters joined");
        var segments = new List<PathSegment>(prefix.Segments.Count + path.Segments.Count);
        segments.AddRange(prefix.Segments);
        segments.AddRange(path.Segments);
        return new PropertyPath(prefix.Text + "." + path.Text, segments);
    }

    // ------------------------------------------------------------------------------------------ the grammar (path/parse.rs)

    private static PropertyPathError? Parse(byte[] src, List<PathSegment> segments)
    {
        if (src.Length > MaxLength) return new PropertyPathError(MaxLength, PropertyPathErrorKind.TooLong, 0, src.Length);
        int pos = 0;
        while (true)
        {
            var error = ParseSegment(src, ref pos, out bool done, out PathSegment? segment);
            if (error is not null) return error;
            segments.Add(segment!);
            if (done) return null;
        }
    }

    private static PropertyPathError? ParseSegment(byte[] src, ref int pos, out bool done, out PathSegment? segment)
    {
        done = false;
        segment = null;
        int start = pos, end = start;
        while (end < src.Length)
        {
            Rune.DecodeFromUtf8(src.AsSpan(end), out Rune c, out int used);
            if (!IsNameChar(c)) break;
            end += used;
        }
        if (end == start) return new PropertyPathError(start, PropertyPathErrorKind.EmptySegment);
        string name = Encoding.UTF8.GetString(src, start, end - start);

        uint? index = null;
        KeyLiteral? key = null;
        bool subscripted = false;
        if (end < src.Length && src[end] == '[')
        {
            var error = ParseIndex(src, end, out uint value, out int next);
            if (error is not null) return error;
            index = value;
            subscripted = true;
            end = next;
        }
        else if (end < src.Length && src[end] == '{')
        {
            var error = ParseKey(src, end, out KeyLiteral? literal, out int next);
            if (error is not null) return error;
            key = literal;
            subscripted = true;
            end = next;
        }

        if (end >= src.Length)
        {
            pos = end;
            done = true;
        }
        else if (src[end] == '.') pos = end + 1;
        else if (subscripted && (src[end] == '[' || src[end] == '{')) return new PropertyPathError(end, PropertyPathErrorKind.DoubleSubscript);
        else
        {
            Rune.DecodeFromUtf8(src.AsSpan(end), out Rune c, out _);
            return new PropertyPathError(end, PropertyPathErrorKind.UnexpectedCharacter, c.Value);
        }

        segment = new PathSegment(name, index, key);
        return null;
    }

    /// <summary>A name is any character but <c>. [ ] { } ( )</c> and a control character.</summary>
    private static bool IsNameChar(Rune c) =>
        c.Value is not ('.' or '[' or ']' or '{' or '}' or '(' or ')') && !Rune.IsControl(c);

    private static PropertyPathError? ParseIndex(byte[] src, int open, out uint index, out int next)
    {
        index = 0;
        next = 0;
        int close = Array.IndexOf(src, (byte)']', open + 1);
        if (close < 0) return new PropertyPathError(open, PropertyPathErrorKind.UnbalancedBracket);
        if (!TryParseInt(src.AsSpan(open + 1, close - open - 1), out index))
            return new PropertyPathError(open + 1, PropertyPathErrorKind.InvalidIndex);
        next = close + 1;
        return null;
    }

    /// <summary><c>strtol</c> with base 0, restricted to non-negative values that fill the whole text: <c>0x</c> hex, a leading
    /// <c>0</c> octal, else decimal; the value fits a <c>u32</c>.</summary>
    private static bool TryParseInt(ReadOnlySpan<byte> text, out uint value)
    {
        value = 0;
        int radix = 10;
        ReadOnlySpan<byte> digits = text;
        if (text.Length >= 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X')) { radix = 16; digits = text[2..]; }
        else if (text.Length > 1 && text[0] == '0') { radix = 8; digits = text[1..]; }
        if (digits.IsEmpty) return false;
        ulong acc = 0;
        foreach (byte b in digits)
        {
            int digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'z' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'Z' => b - 'A' + 10,
                _ => 99,
            };
            if (digit >= radix) return false;
            acc = acc * (uint)radix + (uint)digit;
            if (acc > uint.MaxValue) return false;
        }
        value = (uint)acc;
        return true;
    }

    private static PropertyPathError? ParseKey(byte[] src, int open, out KeyLiteral? key, out int next)
    {
        key = null;
        next = 0;
        int start = SkipWhitespace(src, open + 1);
        int after;
        if (start >= src.Length) return new PropertyPathError(open, PropertyPathErrorKind.UnbalancedBracket);
        byte lead = src[start];
        if (lead == '"')
        {
            var error = ParseJsonString(src, start, out key, out after);
            if (error is not null) return error;
        }
        else if (lead == 't' && StartsWith(src, start, "true")) { key = KeyLiteral.Bool(true); after = start + 4; }
        else if (lead == 'f' && StartsWith(src, start, "false")) { key = KeyLiteral.Bool(false); after = start + 5; }
        else if (lead == '-' || lead is >= (byte)'0' and <= (byte)'9')
        {
            var error = ParseJsonNumber(src, start, out key, out after);
            if (error is not null) return error;
        }
        else return new PropertyPathError(start, PropertyPathErrorKind.InvalidKey);

        int close = SkipWhitespace(src, after);
        if (close >= src.Length) return new PropertyPathError(open, PropertyPathErrorKind.UnbalancedBracket);
        if (src[close] != '}') return new PropertyPathError(close, PropertyPathErrorKind.InvalidKey);
        next = close + 1;
        return null;
    }

    private static bool StartsWith(byte[] src, int at, string word)
    {
        if (at + word.Length > src.Length) return false;
        for (int i = 0; i < word.Length; i++)
            if (src[at + i] != word[i]) return false;
        return true;
    }

    private static int SkipWhitespace(byte[] src, int at)
    {
        while (at < src.Length && src[at] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r') at++;
        return at;
    }

    private static PropertyPathError? ParseJsonString(byte[] src, int start, out KeyLiteral? key, out int after)
    {
        key = null;
        after = 0;
        int at = start + 1;
        bool escaped = false;
        while (true)
        {
            if (at >= src.Length) return new PropertyPathError(start, PropertyPathErrorKind.UnbalancedBracket);
            byte b = src[at];
            if (b == '"') break;
            if (b == '\\') { escaped = true; at += 2; }
            else if (b < 0x20) return new PropertyPathError(at, PropertyPathErrorKind.InvalidKey);
            else at++;
        }
        int bodyLength = at - (start + 1);
        if (escaped)
        {
            var error = Unescape(src, start + 1, bodyLength, out string text);
            if (error is not null) return error;
            key = KeyLiteral.String(text);
        }
        else key = KeyLiteral.String(Encoding.UTF8.GetString(src, start + 1, bodyLength));
        after = at + 1;
        return null;
    }

    private static PropertyPathError? Unescape(byte[] src, int origin, int length, out string result)
    {
        result = "";
        var text = new StringBuilder(length);
        int at = 0;
        while (at < length)
        {
            if (src[origin + at] != '\\')
            {
                Rune.DecodeFromUtf8(src.AsSpan(origin + at, length - at), out Rune c, out int used);
                text.Append(c.ToString());
                at += used;
                continue;
            }

            int escape = at;
            at++;
            if (at >= length) return new PropertyPathError(origin + escape, PropertyPathErrorKind.InvalidKey);
            byte kind = src[origin + at];
            at++;
            switch (kind)
            {
                case (byte)'"': text.Append('"'); break;
                case (byte)'\\': text.Append('\\'); break;
                case (byte)'/': text.Append('/'); break;
                case (byte)'b': text.Append('\b'); break;
                case (byte)'f': text.Append('\f'); break;
                case (byte)'n': text.Append('\n'); break;
                case (byte)'r': text.Append('\r'); break;
                case (byte)'t': text.Append('\t'); break;
                case (byte)'u':
                {
                    if (!TryHex4(src, origin, length, at, out uint high)) return new PropertyPathError(origin + at, PropertyPathErrorKind.InvalidKey);
                    at += 4;
                    if (high is >= 0xD800 and <= 0xDBFF)
                    {
                        if (!(at + 1 < length && src[origin + at] == '\\' && src[origin + at + 1] == 'u'))
                            return new PropertyPathError(origin + at, PropertyPathErrorKind.InvalidKey);
                        at += 2;
                        if (!TryHex4(src, origin, length, at, out uint low)) return new PropertyPathError(origin + at, PropertyPathErrorKind.InvalidKey);
                        at += 4;
                        if (low is < 0xDC00 or > 0xDFFF) return new PropertyPathError(origin + at, PropertyPathErrorKind.InvalidKey);
                        text.Append((char)high).Append((char)low);
                    }
                    else if (high is >= 0xDC00 and <= 0xDFFF) return new PropertyPathError(origin + at, PropertyPathErrorKind.InvalidKey);
                    else text.Append((char)high);
                    break;
                }
                default:
                    return new PropertyPathError(origin + escape, PropertyPathErrorKind.InvalidKey);
            }
        }
        result = text.ToString();
        return null;
    }

    /// <summary><c>u32::from_str_radix(text.get(at..at + 4), 16)</c>: four bytes that are hexadecimal digits, or a <c>+</c> and three
    /// (Rust's integer parser takes a leading plus).</summary>
    private static bool TryHex4(byte[] src, int origin, int length, int at, out uint value)
    {
        value = 0;
        if (at + 4 > length) return false;
        int i = 0;
        if (src[origin + at] == '+') i = 1;
        uint acc = 0;
        for (; i < 4; i++)
        {
            byte b = src[origin + at + i];
            int digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => -1,
            };
            if (digit < 0) return false;
            acc = (acc << 4) | (uint)digit;
        }
        value = acc;
        return true;
    }

    private static PropertyPathError? ParseJsonNumber(byte[] src, int start, out KeyLiteral? key, out int after)
    {
        key = null;
        after = 0;
        int at = start;
        if (at < src.Length && src[at] == '-') at++;
        if (at < src.Length && src[at] == '0') at++;
        else if (at < src.Length && src[at] is >= (byte)'1' and <= (byte)'9') at = SkipDigits(src, at);
        else return new PropertyPathError(start, PropertyPathErrorKind.InvalidKey);

        if (at < src.Length && src[at] == '.')
        {
            at++;
            int end = SkipDigits(src, at);
            if (end == at) return new PropertyPathError(at, PropertyPathErrorKind.InvalidKey);
            at = end;
        }
        if (at < src.Length && src[at] is (byte)'e' or (byte)'E')
        {
            at++;
            if (at < src.Length && src[at] is (byte)'+' or (byte)'-') at++;
            int end = SkipDigits(src, at);
            if (end == at) return new PropertyPathError(at, PropertyPathErrorKind.InvalidKey);
            at = end;
        }
        key = KeyLiteral.Number(Encoding.ASCII.GetString(src, start, at - start));
        after = at;
        return null;
    }

    private static int SkipDigits(byte[] src, int at)
    {
        while (at < src.Length && src[at] is >= (byte)'0' and <= (byte)'9') at++;
        return at;
    }
}

/// <summary>Rust's <c>{:?}</c> of a <c>char</c>, for the error texts that print one.</summary>
internal static class RustChar
{
    public static string Debug(int scalar)
    {
        switch (scalar)
        {
            case 0: return "'\\0'";
            case '\t': return "'\\t'";
            case '\r': return "'\\r'";
            case '\n': return "'\\n'";
            case '\\': return "'\\\\'";
            case '\'': return "'\\''";
        }
        if (scalar < 0x20 || scalar == 0x7F || scalar is >= 0x80 and <= 0x9F) return "'\\u{" + scalar.ToString("x", CultureInfo.InvariantCulture) + "}'";
        return "'" + char.ConvertFromUtf32(scalar) + "'";
    }
}
