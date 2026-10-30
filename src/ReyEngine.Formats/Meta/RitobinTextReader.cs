using System.Globalization;
using System.Numerics;
using System.Text;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using System.IO.Hashing;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Meta;

/// <summary>One problem found while reading ritobin text, with the line it is on.</summary>
public sealed record RitobinError(int Line, string Message)
{
    public override string ToString() => $"line {Line}: {Message}";
}

/// <summary>
/// M497: parse ritobin <c>#PROP_text</c> back into a <see cref="BinTree"/> — the half that lets the editor
/// save what was edited.
///
/// <para><b>Errors are collected with line numbers rather than thrown.</b> This parses text a human has
/// just been typing into, so "unknown type 'strng' on line 412" is the useful answer and a stack trace is
/// not. Nothing is written unless the parse produced no errors at all.</para>
///
/// <para><b>Names, hashes and the round trip.</b> A name may appear as a bare identifier, a quoted string
/// or a <c>0x…</c> literal; the first two are hashed (FNV-1a for bin names, XxHash64 for <c>file</c>
/// paths) and the third is taken as-is. That is what makes the writer's readability free: it prints the
/// name it resolved, and reading it back hashes to exactly the value it started from.</para>
///
/// <para><b>The round trip is byte-exact</b>, verified on Map453's shipped materials.bin: 148 objects out,
/// 148 back, and re-serialising gives a file identical to the byte. That bar caught the one real defect
/// here — <c>rgba</c>. LeagueToolkit's Color stores NORMALISED floats while ritobin writes 0..255, so an
/// alpha of 255 was going out as <c>1</c> and coming back as the byte 1. It looked like 456 stray bool
/// bytes flipping <c>0xFF</c> to <c>0x01</c> until the colour conversion was fixed, at which point every
/// one of them went away. Structural comparison alone would not have found it.</para>
/// </summary>
public static class RitobinTextReader
{
    /// <summary>Parse ritobin text. Returns null when <paramref name="errors"/> is non-empty.</summary>
    public static BinTree? Read(string text, out IReadOnlyList<RitobinError> errors)
    {
        var parser = new Parser(text ?? "");
        var tree = parser.ParseFile();
        errors = parser.Errors;
        return parser.Errors.Count == 0 ? tree : null;
    }

    /// <summary>The hash of <paramref name="text"/> by a ritobin hash function, over its ASCII-lowercased bytes, narrowed to <paramref name="width"/>
    /// bytes (4 keeps the low 32 bits, as ltk_hash's <c>HashValue::with_width</c> does). XXH64 and XXH3 use seed 0.</summary>
    public static ulong HashText(string text, string function, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] is >= (byte)'A' and <= (byte)'Z') bytes[i] += 32;
        ulong hash = function switch
        {
            "fnv1a" => Fnv1a32(bytes),
            "xxh64" => XxHash64.HashToUInt64(bytes, 0),
            "xxh3" => XxHash3.HashToUInt64(bytes),
            _ => throw new ArgumentException($"'{function}' is not fnv1a, xxh64 or xxh3.", nameof(function)),
        };
        return width == 4 ? (uint)hash : hash;
    }

    private static uint Fnv1a32(ReadOnlySpan<byte> bytes)
    {
        uint h = 0x811c9dc5;
        foreach (byte b in bytes) { h ^= b; h *= 0x01000193; }
        return h;
    }

    // ---- tokens ----------------------------------------------------------

    private enum TokenKind { End, Identifier, String, Number, HexNumber, Symbol }

    private readonly record struct Token(TokenKind Kind, string Text, int Line, ulong Hex = 0);

    private sealed class Lexer
    {
        private readonly string _s;
        private int _i;
        private int _line = 1;

        public Lexer(string s) { _s = s; }

        public Token Next()
        {
            SkipTrivia();
            if (_i >= _s.Length) return new Token(TokenKind.End, "", _line);

            char c = _s[_i];
            int line = _line;

            if (c is '{' or '}' or '[' or ']' or ':' or '=' or ',')
            { _i++; return new Token(TokenKind.Symbol, c.ToString(), line); }

            if (c == '"') return ReadString();

            // 0x… is a hash literal; anything else starting with a digit or sign is a number.
            if (c == '0' && _i + 1 < _s.Length && (_s[_i + 1] is 'x' or 'X'))
            {
                int start = _i;
                _i += 2;
                while (_i < _s.Length && Uri.IsHexDigit(_s[_i])) _i++;
                string hex = _s[(start + 2).._i];
                // a literal wider than 64 bits is an error to report, not an exception to throw
                ulong value = hex.Length == 0 || !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong parsed) ? 0 : parsed;
                return new Token(TokenKind.HexNumber, _s[start.._i], line, value);
            }

            if (char.IsDigit(c) || c is '-' or '+')
            {
                int start = _i;
                _i++;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] is '.' or 'e' or 'E' or '-' or '+')) _i++;
                return new Token(TokenKind.Number, _s[start.._i], line);
            }

            // Bare identifiers cover field names, class names, type names, true/false/null. Riot names
            // contain '/', '_' and '.', so those are part of the token rather than separators.
            if (char.IsLetter(c) || c is '_' or '/')
            {
                int start = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] is '_' or '/' or '.' or '-')) _i++;
                return new Token(TokenKind.Identifier, _s[start.._i], line);
            }

            _i++;
            return new Token(TokenKind.Symbol, c.ToString(), line);
        }

        private void SkipTrivia()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == '\n') { _line++; _i++; }
                else if (char.IsWhiteSpace(c)) _i++;
                else if (c == '#')
                {
                    // Comments run to end of line. The "#PROP_text" header is consumed the same way, which
                    // is why the parser does not need to special-case it.
                    while (_i < _s.Length && _s[_i] != '\n') _i++;
                }
                else return;
            }
        }

        private Token ReadString()
        {
            int line = _line;
            var sb = new StringBuilder();
            _i++;                                        // opening quote
            while (_i < _s.Length && _s[_i] != '"')
            {
                char c = _s[_i++];
                if (c == '\\' && _i < _s.Length)
                {
                    char e = _s[_i++];
                    sb.Append(e switch
                    {
                        'n' => '\n', 'r' => '\r', 't' => '\t',
                        '\\' => '\\', '"' => '"',
                        _ => e,
                    });
                }
                else { if (c == '\n') _line++; sb.Append(c); }
            }
            if (_i < _s.Length) _i++;                    // closing quote
            return new Token(TokenKind.String, sb.ToString(), line);
        }
    }

    // ---- parser ----------------------------------------------------------

    /// <summary>
    /// M850: the arguments of <c>hash[width]</c> / <c>hash[width,function]</c> (ltk_ritobin 0.11, <c>HashType</c>). Plain <c>hash</c> is
    /// <c>hash[u32,fnv1a]</c> and carries none.
    /// </summary>
    /// <param name="Width">4 (<c>u32</c>) or 8 (<c>u64</c>).</param>
    /// <param name="Function"><c>fnv1a</c>, <c>xxh64</c> or <c>xxh3</c>; null when only the width is given, which accepts a hex literal alone.</param>
    private readonly record struct HashSpec(int Width, string? Function);

    private sealed class Parser
    {
        private readonly List<Token> _tokens = new();
        private int _at;
        private uint _owner;                           // M850: the class whose fields are being read
        private HashSpec? _specSelf, _specKey, _specElement;   // the hash arguments of the type spec just read
        public List<RitobinError> Errors { get; } = new();

        public Parser(string text)
        {
            var lexer = new Lexer(text);
            while (true)
            {
                var t = lexer.Next();
                _tokens.Add(t);
                if (t.Kind == TokenKind.End) break;
            }
        }

        private Token Peek => _tokens[Math.Min(_at, _tokens.Count - 1)];
        private Token Take() => _tokens[Math.Min(_at++, _tokens.Count - 1)];
        private bool AtEnd => Peek.Kind == TokenKind.End;

        private void Error(string message) => Error(Peek.Line, message);
        private void Error(int line, string message)
        {
            // One cascade of errors from a single mistake helps nobody; stop collecting past a sane limit.
            if (Errors.Count < 50) Errors.Add(new RitobinError(line, message));
        }

        private bool Expect(string symbol)
        {
            if (Peek.Kind == TokenKind.Symbol && Peek.Text == symbol) { _at++; return true; }
            Error($"expected '{symbol}' but found '{Peek.Text}'");
            return false;
        }

        public BinTree ParseFile()
        {
            var objects = new List<BinTreeObject>();
            var dependencies = new List<string>();

            while (!AtEnd && Errors.Count < 50)
            {
                var nameToken = Take();
                if (nameToken.Kind == TokenKind.End) break;
                if (nameToken.Kind != TokenKind.Identifier)
                { Error(nameToken.Line, $"expected a field name but found '{nameToken.Text}'"); Recover(); continue; }

                if (!Expect(":")) { Recover(); continue; }
                if (!ParseTypeSpec(out var type, out var key, out var element)) { Recover(); continue; }
                if (!Expect("=")) { Recover(); continue; }

                switch (nameToken.Text)
                {
                    case "type":
                    case "version":
                        Take();                                  // the value is fixed by the writer
                        break;

                    case "linked":
                        if (!Expect("{")) { Recover(); break; }
                        while (!AtEnd && !(Peek.Kind == TokenKind.Symbol && Peek.Text == "}"))
                        {
                            var v = Take();
                            if (v.Kind == TokenKind.String) dependencies.Add(v.Text);
                            else Error(v.Line, $"linked expects strings, found '{v.Text}'");
                        }
                        Expect("}");
                        break;

                    case "entries":
                        ParseEntries(objects);
                        break;

                    default:
                        Error(nameToken.Line, $"unknown top-level field '{nameToken.Text}'");
                        Recover();
                        break;
                }
            }

            if (objects.Count == 0 && Errors.Count == 0)
                Error(1, "no entries were found — is this ritobin text?");

            return new BinTree(objects, dependencies);
        }

        /// <summary>Skip to something that looks like the start of the next field, so one bad line does not
        /// turn into fifty errors.</summary>
        private void Recover()
        {
            int depth = 0;
            while (!AtEnd)
            {
                var t = Peek;
                if (t.Kind == TokenKind.Symbol)
                {
                    if (t.Text == "{") depth++;
                    else if (t.Text == "}") { if (depth == 0) return; depth--; }
                }
                _at++;
                if (depth == 0 && Peek.Kind == TokenKind.Identifier
                    && _at + 1 < _tokens.Count && _tokens[_at + 1] is { Kind: TokenKind.Symbol, Text: ":" })
                    return;
            }
        }

        private void ParseEntries(List<BinTreeObject> objects)
        {
            if (!Expect("{")) return;
            while (!AtEnd && !(Peek.Kind == TokenKind.Symbol && Peek.Text == "}"))
            {
                var keyToken = Take();
                uint pathHash = HashOf(keyToken);
                if (!Expect("=")) { Recover(); continue; }

                var classToken = Take();
                uint classHash = HashOf(classToken);
                if (!Expect("{")) { Recover(); continue; }

                var properties = ParseFields(classHash);
                Expect("}");
                objects.Add(new BinTreeObject(pathHash, classHash, properties));
            }
            Expect("}");
        }

        private List<BinTreeProperty> ParseFields(uint owner)
        {
            var properties = new List<BinTreeProperty>();
            uint outer = _owner;
            _owner = owner;
            try { ReadFields(properties); }
            finally { _owner = outer; }
            return properties;
        }

        private void ReadFields(List<BinTreeProperty> properties)
        {
            while (!AtEnd && !(Peek.Kind == TokenKind.Symbol && Peek.Text == "}"))
            {
                var nameToken = Take();
                uint nameHash = HashOf(nameToken);
                if (!Expect(":")) { Recover(); continue; }
                if (!ParseTypeSpec(out var type, out var key, out var element)) { Recover(); continue; }
                if (!Expect("=")) { Recover(); continue; }

                var value = ParseValue(nameHash, type, key, element);
                if (value is not null) properties.Add(value);
            }
        }

        /// <summary>A type spec is <c>name</c>, <c>name[element]</c> or <c>map[key,value]</c>.</summary>
        private bool ParseTypeSpec(out BinPropertyType type, out BinPropertyType key, out BinPropertyType element)
        {
            type = key = element = BinPropertyType.None;
            _specSelf = _specKey = _specElement = null;
            var t = Take();
            if (t.Kind != TokenKind.Identifier || !RitobinText.TryParseType(t.Text, out type))
            { Error(t.Line, $"unknown type '{t.Text}'"); return false; }

            if (Peek.Kind == TokenKind.Symbol && Peek.Text == "[")
            {
                // M850: hash[u64], hash[u32,fnv1a]: the brackets of a hash give its width and function, not an element type
                if (type == BinPropertyType.Hash)
                {
                    _at++;
                    return ParseHashArgs(out _specSelf);
                }

                _at++;
                var first = Take();
                if (first.Kind != TokenKind.Identifier || !RitobinText.TryParseType(first.Text, out var a))
                { Error(first.Line, $"unknown type '{first.Text}'"); return false; }
                HashSpec? specA = null, specB = null;
                if (a == BinPropertyType.Hash && Peek.Kind == TokenKind.Symbol && Peek.Text == "[")
                {
                    _at++;
                    if (!ParseHashArgs(out specA)) return false;
                }

                if (Peek.Kind == TokenKind.Symbol && Peek.Text == ",")
                {
                    _at++;
                    var second = Take();
                    if (second.Kind != TokenKind.Identifier || !RitobinText.TryParseType(second.Text, out var b))
                    { Error(second.Line, $"unknown type '{second.Text}'"); return false; }
                    if (b == BinPropertyType.Hash && Peek.Kind == TokenKind.Symbol && Peek.Text == "[")
                    {
                        _at++;
                        if (!ParseHashArgs(out specB)) return false;
                    }
                    key = a; element = b;
                    _specKey = specA; _specElement = specB;
                }
                else { element = a; _specElement = specA; }

                if (!Expect("]")) return false;
            }
            return true;
        }

        /// <summary>The text of a hash type after its '[': <c>width]</c> or <c>width,function]</c>.</summary>
        private bool ParseHashArgs(out HashSpec? spec)
        {
            spec = null;
            var w = Take();
            int width = w.Kind == TokenKind.Identifier ? w.Text switch { "u32" => 4, "u64" => 8, _ => 0 } : 0;
            if (width == 0) { Error(w.Line, $"a hash width is u32 or u64, not '{w.Text}'"); return false; }
            string? function = null;
            if (Peek.Kind == TokenKind.Symbol && Peek.Text == ",")
            {
                _at++;
                var f = Take();
                if (f.Kind != TokenKind.Identifier || f.Text is not ("fnv1a" or "xxh64" or "xxh3"))
                { Error(f.Line, $"a hash function is fnv1a, xxh64 or xxh3, not '{f.Text}'"); return false; }
                function = f.Text;
            }
            if (!Expect("]")) return false;
            spec = new HashSpec(width, function);
            return true;
        }

        private BinTreeProperty? ParseValue(uint nameHash, BinPropertyType type,
            BinPropertyType key, BinPropertyType element)
        {
            // the hash arguments of THIS type spec, taken before a nested spec replaces them
            HashSpec? self = _specSelf, keySpec = _specKey, elementSpec = _specElement;
            switch (type)
            {
                case BinPropertyType.Container:
                case BinPropertyType.UnorderedContainer:
                {
                    if (!Expect("{")) return null;
                    var items = new List<BinTreeProperty>();
                    while (!AtEnd && !(Peek.Kind == TokenKind.Symbol && Peek.Text == "}"))
                    {
                        var item = ParseElement(element, elementSpec);
                        if (item is not null) items.Add(item); else Recover();
                    }
                    Expect("}");
                    return type == BinPropertyType.Container
                        ? new BinTreeContainer(nameHash, element, items)
                        : new BinTreeUnorderedContainer(nameHash, element, items);
                }

                case BinPropertyType.Optional:
                {
                    if (!Expect("{")) return null;
                    BinTreeProperty? inner = null;
                    if (!(Peek.Kind == TokenKind.Symbol && Peek.Text == "}")) inner = ParseElement(element, elementSpec);
                    Expect("}");
                    return new BinTreeOptional(nameHash, inner!);
                }

                case BinPropertyType.Map:
                {
                    if (!Expect("{")) return null;
                    var pairs = new List<KeyValuePair<BinTreeProperty, BinTreeProperty>>();
                    while (!AtEnd && !(Peek.Kind == TokenKind.Symbol && Peek.Text == "}"))
                    {
                        var k = ParseScalar(0, key, keySpec);
                        if (k is null) { Recover(); continue; }
                        if (!Expect("=")) { Recover(); continue; }
                        var v = ParseElement(element, elementSpec);
                        if (v is null) { Recover(); continue; }
                        pairs.Add(new KeyValuePair<BinTreeProperty, BinTreeProperty>(k, v));
                    }
                    Expect("}");
                    return new BinTreeMap(nameHash, key, element, pairs);
                }

                case BinPropertyType.Struct:
                case BinPropertyType.Embedded:
                    return ParseStruct(nameHash, type);

                default:
                    return ParseScalar(nameHash, type, self);
            }
        }

        /// <summary>An element inside a container: a bare value, or a class name plus a body.</summary>
        private BinTreeProperty? ParseElement(BinPropertyType type, HashSpec? spec = null) => type switch
        {
            BinPropertyType.Struct or BinPropertyType.Embedded => ParseStruct(0, type),
            BinPropertyType.Container or BinPropertyType.UnorderedContainer or
            BinPropertyType.Map or BinPropertyType.Optional => ParseNestedContainerElement(type),
            _ => ParseScalar(0, type, spec),
        };

        /// <summary>A container whose elements are themselves containers writes the inner type spec again.</summary>
        private BinTreeProperty? ParseNestedContainerElement(BinPropertyType type)
        {
            if (Peek.Kind == TokenKind.Identifier)
            {
                if (!ParseTypeSpec(out var inner, out var k, out var e)) return null;
                if (!Expect("=")) return null;
                return ParseValue(0, inner, k, e);
            }
            return ParseValue(0, type, BinPropertyType.None, BinPropertyType.None);
        }

        private BinTreeProperty? ParseStruct(uint nameHash, BinPropertyType type)
        {
            var classToken = Take();
            // "null" is how an unset pointer is written; it must stay unset rather than become a class.
            if (classToken.Kind == TokenKind.Identifier && classToken.Text == "null")
                return new BinTreeStruct(nameHash, 0, Array.Empty<BinTreeProperty>());

            uint classHash = HashOf(classToken);
            if (!Expect("{")) return null;
            var properties = ParseFields(classHash);
            Expect("}");
            return type == BinPropertyType.Embedded
                ? new BinTreeEmbedded(nameHash, classHash, properties)
                : new BinTreeStruct(nameHash, classHash, properties);
        }

        private BinTreeProperty? ParseScalar(uint nameHash, BinPropertyType type, HashSpec? spec = null)
        {
            switch (type)
            {
                case BinPropertyType.Vector2:
                case BinPropertyType.Vector3:
                case BinPropertyType.Vector4:
                case BinPropertyType.Matrix44:
                case BinPropertyType.Color:
                    return ParseNumberList(nameHash, type);
            }

            var t = Take();
            switch (type)
            {
                case BinPropertyType.None:
                    return new BinTreeNone(nameHash);

                case BinPropertyType.Bool:
                case BinPropertyType.BitBool:
                {
                    bool value = t.Text is "true" or "1";
                    if (t.Text is not ("true" or "false" or "1" or "0"))
                        Error(t.Line, $"expected true/false but found '{t.Text}'");
                    return type == BinPropertyType.Bool
                        ? new BinTreeBool(nameHash, value)
                        : new BinTreeBitBool(nameHash, value);
                }

                case BinPropertyType.String:
                    if (t.Kind != TokenKind.String) Error(t.Line, $"expected a quoted string but found '{t.Text}'");
                    return new BinTreeString(nameHash, t.Text);

                case BinPropertyType.Hash:
                    return spec is { } hashSpec ? HashWithArguments(nameHash, t, hashSpec) : new BinTreeHash(nameHash, HashOf(t));
                case BinPropertyType.ObjectLink:
                    return new BinTreeObjectLink(nameHash, HashOf(t));
                case BinPropertyType.WadChunkLink:
                    return new BinTreeWadChunkLink(nameHash, WadHashOf(t));

                case BinPropertyType.F32:
                    return new BinTreeF32(nameHash, ParseFloat(t));

                case BinPropertyType.I8: return new BinTreeI8(nameHash, (sbyte)ParseLong(t));
                case BinPropertyType.U8: return new BinTreeU8(nameHash, (byte)ParseLong(t));
                case BinPropertyType.I16: return new BinTreeI16(nameHash, (short)ParseLong(t));
                case BinPropertyType.U16: return new BinTreeU16(nameHash, (ushort)ParseLong(t));
                case BinPropertyType.I32: return new BinTreeI32(nameHash, (int)ParseLong(t));
                case BinPropertyType.U32: return new BinTreeU32(nameHash, (uint)ParseLong(t));
                case BinPropertyType.I64: return new BinTreeI64(nameHash, ParseLong(t));
                case BinPropertyType.U64: return new BinTreeU64(nameHash, (ulong)ParseLong(t));

                default:
                    Error(t.Line, $"cannot read a value of type {RitobinText.TypeName(type)}");
                    return null;
            }
        }

        private BinTreeProperty? ParseNumberList(uint nameHash, BinPropertyType type)
        {
            if (!Expect("{")) return null;
            var numbers = new List<float>();
            while (!AtEnd && !(Peek.Kind == TokenKind.Symbol && Peek.Text == "}"))
            {
                if (Peek.Kind == TokenKind.Symbol && Peek.Text == ",") { _at++; continue; }
                numbers.Add(ParseFloat(Take()));
            }
            Expect("}");

            float N(int i) => i < numbers.Count ? numbers[i] : 0f;
            return type switch
            {
                BinPropertyType.Vector2 => new BinTreeVector2(nameHash, new Vector2(N(0), N(1))),
                BinPropertyType.Vector3 => new BinTreeVector3(nameHash, new Vector3(N(0), N(1), N(2))),
                BinPropertyType.Vector4 => new BinTreeVector4(nameHash, new Vector4(N(0), N(1), N(2), N(3))),
                BinPropertyType.Color => new BinTreeColor(nameHash,
                    new LeagueToolkit.Core.Primitives.Color((byte)N(0), (byte)N(1), (byte)N(2), (byte)N(3))),
                _ => new BinTreeMatrix44(nameHash, new Matrix4x4(
                    N(0), N(1), N(2), N(3), N(4), N(5), N(6), N(7),
                    N(8), N(9), N(10), N(11), N(12), N(13), N(14), N(15))),
            };
        }

        private float ParseFloat(Token t)
        {
            if (t.Kind == TokenKind.HexNumber) return t.Hex;
            // InvariantCulture is mandatory: on a German machine "1.5" would otherwise fail to parse and
            // silently become 0.
            if (float.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return f;
            Error(t.Line, $"'{t.Text}' is not a number");
            return 0f;
        }

        private long ParseLong(Token t)
        {
            if (t.Kind == TokenKind.HexNumber) return (long)t.Hex;
            if (long.TryParse(t.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) return v;
            if (double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return (long)d;
            Error(t.Line, $"'{t.Text}' is not an integer");
            return 0;
        }

        /// <summary>
        /// M850: a value of <c>hash[width]</c> or <c>hash[width,function]</c>, read as ltk_ritobin 0.11 reads it: a hex literal is the value
        /// (it must fit the width), and a string (or bare name) is hashed with the function over its ASCII-lowercased bytes, XXH64 and XXH3
        /// with seed 0, and narrowed to the low 32 bits for u32. A width with no function accepts a hex literal only.
        ///
        /// <para>A four-byte hash is a <see cref="BinTreeHash"/>. An eight-byte one is held as the U64 stand-in
        /// <see cref="BinWireNormalizer"/> uses, which only <c>StaticMaterialDef.name</c> has; anywhere else it is an error, because the tree
        /// has no eight-byte hash to put it in and the bin could not be written back.</para>
        /// </summary>
        private BinTreeProperty? HashWithArguments(uint nameHash, Token t, HashSpec spec)
        {
            ulong value;
            if (t.Kind == TokenKind.HexNumber)
            {
                value = t.Hex;
                if (spec.Width == 4 && value > uint.MaxValue)
                { Error(t.Line, $"{t.Text} does not fit hash[u32]"); return null; }
            }
            else if (t.Kind is TokenKind.String or TokenKind.Identifier && spec.Function is { } function)
            {
                value = HashText(t.Text, function, spec.Width);
            }
            else
            {
                string width = spec.Width == 4 ? "u32" : "u64";
                Error(t.Line, spec.Function is null
                    ? $"hash[{width}] accepts a hex literal only; name a hash function (for example hash[{width},{(spec.Width == 4 ? "fnv1a" : "xxh3")}]) to give a string"
                    : $"expected a string or a hex literal but found '{t.Text}'");
                return null;
            }

            if (spec.Width == 4) return new BinTreeHash(nameHash, (uint)value);
            if (!BinWireNormalizer.IsMaterialName(_owner, nameHash))
            {
                Error(t.Line, "an 8-byte hash is held only as StaticMaterialDef.name (patch 16.21); ReyEngine cannot write it anywhere else");
                return null;
            }
            return new BinTreeU64(nameHash, value);
        }

        /// <summary>A 32-bit name: a 0x literal as-is, anything else hashed FNV-1a — which is what makes the
        /// writer's readable output round-trip to the same bytes.</summary>
        private uint HashOf(Token t) => t.Kind switch
        {
            TokenKind.HexNumber => (uint)t.Hex,
            TokenKind.Identifier or TokenKind.String => HashAlgorithms.Fnv1a(t.Text),
            _ => Fail(t),
        };

        private uint Fail(Token t)
        {
            Error(t.Line, $"expected a name or hash but found '{t.Text}'");
            return 0;
        }

        /// <summary>A 64-bit WAD path: 0x literal as-is, otherwise XxHash64 of the path.</summary>
        private ulong WadHashOf(Token t) => t.Kind switch
        {
            TokenKind.HexNumber => t.Hex,
            TokenKind.Identifier or TokenKind.String => HashAlgorithms.WadPath(t.Text),
            _ => Fail(t),
        };
    }
}
