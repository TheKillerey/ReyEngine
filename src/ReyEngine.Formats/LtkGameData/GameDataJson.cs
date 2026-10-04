using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>A JSON text the reader refuses, with the line and column <c>serde_json</c> would name.</summary>
public sealed class GameDataJsonException : FormatException
{
    public GameDataJsonException(string message, int line, int column)
        : base($"{message} at line {line} column {column}")
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }

    public int Column { get; }
}

/// <summary>
/// M817: a JSON reader that reproduces <c>serde_json</c> 1.0.150 as <c>ltk_game_data</c> builds it (<c>preserve_order</c>, no
/// <c>float_roundtrip</c>, no <c>arbitrary_precision</c>), reading into the literal a declaration carries
/// (<see cref="GameDataLiteral"/>). The points where it differs from .NET's reader, and that a declaration's meaning rests on:
///
/// <list type="bullet">
/// <item><b>A duplicate key anywhere refuses the document</b> (<c>DeclarationDocument</c>'s visitor does; <c>JsonDocument</c> keeps
/// the last).</item>
/// <item><b>Integer or float.</b> A non-negative integer up to <c>u64::MAX</c> and a negative one down to <c>i64::MIN</c> is an
/// integer. <c>-0</c>, an integer past those ranges, and anything with a point or an exponent is a float, so <c>1.0</c> is a float.</item>
/// <item><b>The float itself</b> is <c>significand as f64</c> times or divided by a power of ten from a table
/// (<c>de.rs</c> <c>f64_from_parts</c>): correctly rounded only for up to 15 or 16 digits and small exponents, and it can round twice
/// beyond that. It is ported, not replaced by <c>double.Parse</c>, because a value one bit apart is a different f32 on a rare input.</item>
/// <item>Key order is kept, a lone surrogate escape is an error, a raw control character in a string is an error, and nesting
/// stops at <paramref name="depthLimit"/> containers.</item>
/// <item><b>Positions are bytes.</b> The reader works on the UTF-8 of the text, as <c>serde_json</c> does, so the column an error names
/// counts bytes (a character of two bytes is two columns) and an error inside a <c>\u</c> escape names the byte after the four it read.</item>
/// </list>
/// </summary>
public static class GameDataJson
{
    /// <summary>The nesting a <c>serde_json</c> deserializer starts with: 128 containers deep stops it, so a document may be 127 deep.</summary>
    public const int DefaultDepthLimit = 128;

    /// <summary>Reads <paramref name="text"/>, which must be exactly one JSON value.</summary>
    /// <param name="depthLimit">The <c>remaining_depth</c> the reader starts with. A document read on its own starts with 128; one read
    /// as the <c>GameData</c> of a .fantome's info.json starts four containers deeper, so 124. A value above 128 is read as 128: the reader
    /// recurses a container deep for each of them, and 128 is what <c>serde_json</c> allows however much stack a caller has.</param>
    /// <exception cref="GameDataJsonException">The text is not JSON <c>serde_json</c> accepts, or holds a duplicate key.</exception>
    public static GameDataLiteral Parse(string text, int depthLimit = DefaultDepthLimit)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reader = new Reader(Encoding.UTF8.GetBytes(text), Math.Min(depthLimit, DefaultDepthLimit));
        var value = reader.ParseValue();
        reader.SkipWhitespace();
        if (reader.Position < reader.Length) throw reader.Error("trailing characters", reader.Position + 1);
        return value;
    }

    // POW10[i] = 1e{i} for i in 0..=308, the literals serde_json's table holds.
    private static readonly double[] Pow10 = BuildPow10();

    private static double[] BuildPow10()
    {
        var table = new double[309];
        for (int i = 0; i < table.Length; i++) table[i] = double.Parse("1e" + i.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        return table;
    }

    /// <summary><c>significand as f64</c>: round to nearest, ties to even.</summary>
    private static double UInt64ToDouble(ulong value)
    {
        if (value <= (1UL << 53)) return (double)(long)value;
        int drop = 64 - BitOperations.LeadingZeroCount(value) - 53;
        ulong mantissa = value >> drop;
        ulong rest = value & ((1UL << drop) - 1);
        ulong half = 1UL << (drop - 1);
        if (rest > half || (rest == half && (mantissa & 1) == 1)) mantissa++;
        return Math.ScaleB((double)(long)mantissa, drop);
    }

    private sealed class Reader
    {
        private readonly byte[] _bytes;
        private int _depth;

        public Reader(byte[] bytes, int depthLimit)
        {
            _bytes = bytes;
            _depth = depthLimit;
        }

        /// <summary>The bytes read so far: the index of the next one.</summary>
        public int Position { get; private set; }

        public int Length => _bytes.Length;

        /// <summary>The error at the position after <paramref name="index"/> bytes (<c>position_of_index</c>): an error raised on the byte at <c>Position</c> names
        /// <c>Position + 1</c>, which at the end of the text is the end.</summary>
        public GameDataJsonException Error(string message, int index)
        {
            int line = 1, column = 0;
            for (int i = 0; i < index && i < _bytes.Length; i++)
            {
                if (_bytes[i] == (byte)'\n') { line++; column = 0; }
                else column++;
            }
            return new GameDataJsonException(message, line, column);
        }

        private int Peek() => Position < _bytes.Length ? _bytes[Position] : -1;

        public void SkipWhitespace()
        {
            while (Position < _bytes.Length && _bytes[Position] is (byte)' ' or (byte)'\n' or (byte)'\t' or (byte)'\r') Position++;
        }

        public GameDataLiteral ParseValue()
        {
            SkipWhitespace();
            int c = Peek();
            switch (c)
            {
                case -1: throw Error("EOF while parsing a value", Position);
                case 'n': Position++; ParseIdent("ull"); return GdNull.Instance;
                case 't': Position++; ParseIdent("rue"); return GdBool.True;
                case 'f': Position++; ParseIdent("alse"); return GdBool.False;
                case '-': Position++; return ParseNumber(false);
                case >= '0' and <= '9': return ParseNumber(true);
                case '"': Position++; return new GdString(ParseString());
                case '[': return ParseList();
                case '{': return ParseMapping();
                default: throw Error("expected value", Position + 1);
            }
        }

        private void ParseIdent(string rest)
        {
            foreach (char expected in rest)
            {
                if (Position >= _bytes.Length) throw Error("EOF while parsing a value", Position);
                if (_bytes[Position] != expected) throw Error("expected ident", Position + 1);
                Position++;
            }
        }

        private void Enter()
        {
            _depth--;
            if (_depth <= 0) throw Error("recursion limit exceeded", Position + 1);
            // 128 containers are a few kilobytes of stack; a thread that has less left than that is refused as the limit is
            if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) throw Error("recursion limit exceeded", Position + 1);
        }

        private GameDataLiteral ParseList()
        {
            Enter();
            Position++;
            var items = new List<GameDataLiteral>();
            bool first = true;
            while (true)
            {
                SkipWhitespace();
                int c = Peek();
                if (c == ']') { Position++; break; }
                if (c == -1) throw Error("EOF while parsing a list", Position);
                if (!first)
                {
                    if (c != ',') throw Error("expected `,` or `]`", Position + 1);
                    Position++;
                    SkipWhitespace();
                    if (Peek() == ']') throw Error("trailing comma", Position + 1);
                }
                items.Add(ParseValue());
                first = false;
            }
            _depth++;
            return new GdList(items);
        }

        private GameDataLiteral ParseMapping()
        {
            Enter();
            Position++;
            var entries = new List<KeyValuePair<string, GameDataLiteral>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool first = true;
            while (true)
            {
                SkipWhitespace();
                int c = Peek();
                if (c == '}') { Position++; break; }
                if (c == -1) throw Error("EOF while parsing an object", Position);
                if (!first)
                {
                    if (c != ',') throw Error("expected `,` or `}`", Position + 1);
                    Position++;
                    SkipWhitespace();
                    if (Peek() == '}') throw Error("trailing comma", Position + 1);
                    c = Peek();
                }
                // after a comma the next key is a value that is not there yet
                if (c == -1) throw Error("EOF while parsing a value", Position);
                if (c != '"') throw Error("key must be a string", Position + 1);
                Position++;
                string key = ParseString();
                SkipWhitespace();
                if (Peek() != ':') throw Error(Peek() == -1 ? "EOF while parsing an object" : "expected `:`", Position + 1);
                Position++;
                var value = ParseValue();
                if (!seen.Add(key))
                {
                    // the deserializer closes the object it is reading before it names the position of an error the visitor raised
                    SkipWhitespace();
                    if (Peek() == '}') Position++;
                    throw Error($"duplicate key `{key}`", Position);
                }
                entries.Add(new(key, value));
                first = false;
            }
            _depth++;
            return new GdMapping(entries);
        }

        // ------------------------------------------------------------------------------------------ strings (read.rs)

        private static bool Escape(byte b) => b == (byte)'"' || b == (byte)'\\' || b < 0x20;

        private string ParseString()
        {
            int start = Position;
            int scan = start;
            while (scan < _bytes.Length && !Escape(_bytes[scan])) scan++;
            if (scan < _bytes.Length && _bytes[scan] == (byte)'"')
            {
                // the common case: no escape, no control character
                Position = scan + 1;
                return Encoding.UTF8.GetString(_bytes, start, scan - start);
            }

            var buffer = new List<byte>(scan - start + 16);
            while (true)
            {
                while (Position < _bytes.Length && !Escape(_bytes[Position])) buffer.Add(_bytes[Position++]);
                if (Position >= _bytes.Length) throw Error("EOF while parsing a string", Position);
                byte b = _bytes[Position];
                if (b == (byte)'"')
                {
                    Position++;
                    return Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(buffer));
                }
                Position++;
                if (b == (byte)'\\') { ParseEscape(buffer); continue; }
                throw Error("control character (\\u0000-\\u001F) found while parsing a string", Position);
            }
        }

        private void ParseEscape(List<byte> buffer)
        {
            if (Position >= _bytes.Length) throw Error("EOF while parsing a string", Position);
            byte c = _bytes[Position++];
            switch (c)
            {
                case (byte)'"': buffer.Add((byte)'"'); break;
                case (byte)'\\': buffer.Add((byte)'\\'); break;
                case (byte)'/': buffer.Add((byte)'/'); break;
                case (byte)'b': buffer.Add(0x08); break;
                case (byte)'f': buffer.Add(0x0C); break;
                case (byte)'n': buffer.Add(0x0A); break;
                case (byte)'r': buffer.Add(0x0D); break;
                case (byte)'t': buffer.Add(0x09); break;
                case (byte)'u': ParseUnicodeEscape(buffer); break;
                default: throw Error("invalid escape", Position);
            }
        }

        private void ParseUnicodeEscape(List<byte> buffer)
        {
            int n = DecodeHex();
            // a trailing surrogate on its own is the half of a pair that is missing its first
            if (n is >= 0xDC00 and <= 0xDFFF) throw Error("lone leading surrogate in hex escape", Position);
            if (n is < 0xD800 or > 0xDBFF) { Append(buffer, n); return; }

            // a leading surrogate: a second \u escape must follow, and each byte that is not the one wanted is consumed before the error is named
            if (Position >= _bytes.Length) throw Error("EOF while parsing a string", Position);
            if (_bytes[Position] != (byte)'\\') { Position++; throw Error("unexpected end of hex escape", Position); }
            Position++;
            if (Position >= _bytes.Length) throw Error("EOF while parsing a string", Position);
            if (_bytes[Position] != (byte)'u') { Position++; throw Error("unexpected end of hex escape", Position); }
            Position++;
            int n2 = DecodeHex();
            if (n2 is < 0xDC00 or > 0xDFFF) throw Error("lone leading surrogate in hex escape", Position);
            Append(buffer, (((n - 0xD800) << 10) | (n2 - 0xDC00)) + 0x10000);
        }

        private static void Append(List<byte> buffer, int scalar)
        {
            Span<byte> utf8 = stackalloc byte[4];
            int length = new Rune(scalar).EncodeToUtf8(utf8);
            for (int i = 0; i < length; i++) buffer.Add(utf8[i]);
        }

        /// <summary><c>decode_hex_escape</c>: the four bytes after <c>\u</c> are consumed before they are checked, so an error names the byte after them.</summary>
        private int DecodeHex()
        {
            if (Position + 4 > _bytes.Length)
            {
                Position = _bytes.Length;
                throw Error("EOF while parsing a string", Position);
            }
            int value = 0;
            bool valid = true;
            for (int i = 0; i < 4; i++)
            {
                byte c = _bytes[Position + i];
                int digit = c switch
                {
                    >= (byte)'0' and <= (byte)'9' => c - '0',
                    >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                    >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                    _ => -1,
                };
                if (digit < 0) valid = false;
                value = (value << 4) | (digit < 0 ? 0 : digit);
            }
            Position += 4;
            if (!valid) throw Error("invalid escape", Position);
            return value;
        }

        // ------------------------------------------------------------------------------------------ numbers (de.rs)

        private static bool Overflows(ulong significand, ulong digit) =>
            significand >= ulong.MaxValue / 10 && (significand > ulong.MaxValue / 10 || digit > ulong.MaxValue % 10);

        private bool PeekDigit() => Position < _bytes.Length && _bytes[Position] is >= (byte)'0' and <= (byte)'9';

        /// <summary><c>parse_integer</c>: the number from its first digit. <paramref name="positive"/> is false when a minus sign was read.</summary>
        private GameDataLiteral ParseNumber(bool positive)
        {
            if (Position >= _bytes.Length) throw Error("EOF while parsing a value", Position);
            byte first = _bytes[Position++];
            switch (first)
            {
                case (byte)'0':
                    if (PeekDigit()) throw Error("invalid number", Position + 1);
                    return ParseNumberTail(positive, 0);
                case >= (byte)'1' and <= (byte)'9':
                {
                    ulong significand = (ulong)(first - '0');
                    while (true)
                    {
                        if (!PeekDigit()) return ParseNumberTail(positive, significand);
                        ulong digit = (ulong)(_bytes[Position] - '0');
                        // keep the number a u64 until it grows too large; then it is read as a float
                        if (Overflows(significand, digit)) return new GdFloat(ParseLongInteger(positive, significand));
                        Position++;
                        significand = significand * 10 + digit;
                    }
                }
                default:
                    throw Error("invalid number", Position);
            }
        }

        private GameDataLiteral ParseNumberTail(bool positive, ulong significand)
        {
            int c = Peek();
            if (c == '.') return new GdFloat(ParseDecimal(positive, significand, 0));
            if (c == 'e' || c == 'E') return new GdFloat(ParseExponent(positive, significand, 0));
            if (positive) return new GdInteger(significand);
            long neg = unchecked(-(long)significand);
            // a float if it underflows, or on -0
            return neg >= 0 ? new GdFloat(-UInt64ToDouble(significand)) : new GdInteger(neg);
        }

        private double ParseLongInteger(bool positive, ulong significand)
        {
            int exponent = 0;
            while (true)
            {
                int c = Peek();
                if (c is >= '0' and <= '9') { Position++; exponent++; }
                else if (c == '.') return ParseDecimal(positive, significand, exponent);
                else if (c == 'e' || c == 'E') return ParseExponent(positive, significand, exponent);
                else return FromParts(positive, significand, exponent);
            }
        }

        private double ParseDecimal(bool positive, ulong significand, int exponentBeforeDecimalPoint)
        {
            Position++;
            int exponentAfterDecimalPoint = 0;
            while (PeekDigit())
            {
                ulong digit = (ulong)(_bytes[Position] - '0');
                if (Overflows(significand, digit))
                    return ParseDecimalOverflow(positive, significand, exponentBeforeDecimalPoint + exponentAfterDecimalPoint);
                Position++;
                significand = significand * 10 + digit;
                exponentAfterDecimalPoint--;
            }

            // at least one digit follows the decimal point
            if (exponentAfterDecimalPoint == 0)
                throw Error(Position < _bytes.Length ? "invalid number" : "EOF while parsing a value", Position + 1);

            int exponent = exponentBeforeDecimalPoint + exponentAfterDecimalPoint;
            int c = Peek();
            return c == 'e' || c == 'E' ? ParseExponent(positive, significand, exponent) : FromParts(positive, significand, exponent);
        }

        private double ParseDecimalOverflow(bool positive, ulong significand, int exponent)
        {
            // the next multiply-add would overflow, so every further digit is ignored
            while (PeekDigit()) Position++;
            int c = Peek();
            return c == 'e' || c == 'E' ? ParseExponent(positive, significand, exponent) : FromParts(positive, significand, exponent);
        }

        private double ParseExponent(bool positive, ulong significand, int startingExponent)
        {
            Position++;
            bool positiveExponent = true;
            int sign = Peek();
            if (sign == '+') Position++;
            else if (sign == '-') { Position++; positiveExponent = false; }

            if (Position >= _bytes.Length) throw Error("EOF while parsing a value", Position);
            byte next = _bytes[Position++];
            if (next is < (byte)'0' or > (byte)'9') throw Error("invalid number", Position);
            int exp = next - '0';
            while (PeekDigit())
            {
                int digit = _bytes[Position++] - '0';
                if (exp >= int.MaxValue / 10 && (exp > int.MaxValue / 10 || digit > int.MaxValue % 10))
                    return ParseExponentOverflow(positive, significand == 0, positiveExponent);
                exp = exp * 10 + digit;
            }

            int final = positiveExponent ? SaturatingAdd(startingExponent, exp) : SaturatingAdd(startingExponent, -exp);
            return FromParts(positive, significand, final);
        }

        private static int SaturatingAdd(int a, int b)
        {
            long sum = (long)a + b;
            return sum > int.MaxValue ? int.MaxValue : sum < int.MinValue ? int.MinValue : (int)sum;
        }

        private double ParseExponentOverflow(bool positive, bool zeroSignificand, bool positiveExponent)
        {
            // an error instead of an infinity
            if (!zeroSignificand && positiveExponent) throw Error("number out of range", Position);
            while (PeekDigit()) Position++;
            return positive ? 0.0 : -0.0;
        }

        private double FromParts(bool positive, ulong significand, int exponent)
        {
            double f = UInt64ToDouble(significand);
            while (true)
            {
                int magnitude = exponent == int.MinValue ? -1 : Math.Abs(exponent);
                if (magnitude >= 0 && magnitude < Pow10.Length)
                {
                    double power = Pow10[magnitude];
                    if (exponent >= 0)
                    {
                        f *= power;
                        if (double.IsInfinity(f)) throw Error("number out of range", Position);
                    }
                    else f /= power;
                    break;
                }
                if (f == 0.0) break;
                if (exponent >= 0) throw Error("number out of range", Position);
                f /= 1e308;
                exponent += 308;
            }
            return positive ? f : -f;
        }
    }
}
