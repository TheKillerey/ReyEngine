using System.Globalization;
using System.Numerics;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M817: what <c>serde_json</c>'s <c>Value</c> deserializer says when a value is not the type a declaration field takes, in its words. A declaration document
/// is read from a <c>serde_json::Value</c> by derived <c>Deserialize</c> impls (<c>DeclarationDocument::parse</c>), so a refused document names what it found and
/// what it expected the way <c>serde</c> writes it: <c>invalid type: string "1", expected u32</c>, <c>invalid value: integer `-1`, expected usize</c>,
/// <c>invalid length 0, expected struct Origin with 3 elements</c>. There is no line and column: the error is raised from a value, not from text.
/// </summary>
internal static class SerdeValue
{
    private static GameDataException Syntax(string text) => new(GameDataErrorKind.Syntax, text);

    /// <summary><c>serde_json</c>'s <c>JsonUnexpected</c>: a null is <c>null</c>, a float is written by <c>zmij</c>, a string as Rust's Debug.</summary>
    public static string Unexpected(GameDataLiteral value) => value switch
    {
        GdNull => "null",
        GdBool b => $"boolean `{(b.Value ? "true" : "false")}`",
        GdInteger i => $"integer `{i.Value.ToString(CultureInfo.InvariantCulture)}`",
        GdFloat f => $"floating point `{FloatText(f.Value)}`",
        GdString s => "string " + RustDebug.Quote(s.Value),
        GdList => "sequence",
        GdMapping => "map",
        _ => "value",
    };

    public static GameDataException InvalidType(GameDataLiteral actual, string expected) => Syntax($"invalid type: {Unexpected(actual)}, expected {expected}");

    public static GameDataException InvalidValue(GameDataLiteral actual, string expected) => Syntax($"invalid value: {Unexpected(actual)}, expected {expected}");

    public static GameDataException InvalidLength(int length, string expected) => Syntax($"invalid length {length}, expected {expected}");

    public static GameDataException MissingField(string name) => Syntax($"missing field `{name}`");

    /// <summary><c>unknown field `x`, expected `a` or `b`</c>, as a struct with <c>deny_unknown_fields</c> says it.</summary>
    public static GameDataException UnknownField(string name, IReadOnlyList<string> fields)
    {
        string expected = fields.Count switch
        {
            0 => "nothing",
            1 => $"`{fields[0]}`",
            2 => $"`{fields[0]}` or `{fields[1]}`",
            _ => "one of " + string.Join(", ", fields.Select(f => $"`{f}`")),
        };
        return Syntax($"unknown field `{name}`, expected {expected}");
    }

    public static string ReadString(GameDataLiteral value) => value is GdString text ? text.Value : throw InvalidType(value, "a string");

    public static uint ReadU32(GameDataLiteral value) => value switch
    {
        GdInteger i when i.Value >= 0 && i.Value <= uint.MaxValue => (uint)i.Value,
        GdInteger => throw InvalidValue(value, "u32"),
        _ => throw InvalidType(value, "u32"),
    };

    public static ulong ReadUsize(GameDataLiteral value) => value switch
    {
        GdInteger i when i.Value >= 0 && i.Value <= ulong.MaxValue => (ulong)i.Value,
        GdInteger => throw InvalidValue(value, "usize"),
        _ => throw InvalidType(value, "usize"),
    };

    public static List<GameDataLiteral> ReadSequence(GameDataLiteral value) => value is GdList list ? list.Items : throw InvalidType(value, "a sequence");

    // ------------------------------------------------------------------------------------------ floats (zmij)

    /// <summary>
    /// <c>zmij::Buffer::format</c> of a finite <c>f64</c>: the shortest digits that read back as the value, laid out in fixed notation from 1e-5 up to 1e16
    /// (<c>1.0</c>, <c>0.00001</c>, <c>544431068535091.6</c>, <c>12340000000.0</c>) and as <c>1.234e+33</c> or <c>6.62607015e-34</c> outside it: a sign on every
    /// exponent, no padding. The digits are .NET's own shortest round trip.
    /// </summary>
    public static string FloatText(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        bool negative = double.IsNegative(value);
        if (value == 0) return negative ? "-0.0" : "0.0";

        double magnitude = Math.Abs(value);
        string text = magnitude.ToString("R", CultureInfo.InvariantCulture);
        string digits;
        int scientific;
        if (double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) == magnitude)
        {
            int exponent = 0;
            int e = text.IndexOf('E');
            if (e >= 0)
            {
                exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                text = text[..e];
            }
            int dot = text.IndexOf('.');
            digits = dot < 0 ? text : text[..dot] + text[(dot + 1)..];
            int point = dot < 0 ? text.Length : dot;   // the digits before the decimal point
            int lead = 0;
            while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
            digits = digits[lead..].TrimEnd('0');
            point -= lead;
            if (digits.Length == 0) digits = "0";
            scientific = point + exponent - 1;         // the exponent of the first digit
        }
        else
        {
            // a power of two has a rounding interval half as wide below it as above, and .NET's shortest digits can land outside it (2^-25 gives
            // 2.980232238769531e-8, which reads back as the next double down); zmij writes the 17 digits the exact value rounds to
            (digits, scientific) = SeventeenDigits(magnitude);
        }
        int count = digits.Length;

        string body;
        if (scientific is >= -5 and <= 15)
        {
            if (count - 1 <= scientific) body = digits + new string('0', scientific + 1 - count) + ".0";
            else if (scientific >= 0) body = digits[..(scientific + 1)] + "." + digits[(scientific + 1)..];
            else body = "0." + new string('0', -scientific - 1) + digits;
        }
        else
        {
            body = digits[..1] + (count > 1 ? "." + digits[1..] : "") + "e" + (scientific >= 0 ? "+" : "-") + Math.Abs(scientific).ToString(CultureInfo.InvariantCulture);
        }
        return negative ? "-" + body : body;
    }

    /// <summary>The 17 significant digits the exact value of a positive finite double rounds to (a tie to the even digit), without trailing zeros, and the
    /// exponent of the first.</summary>
    private static (string Digits, int Exponent) SeventeenDigits(double magnitude)
    {
        long bits = BitConverter.DoubleToInt64Bits(magnitude);
        int biased = (int)((bits >> 52) & 0x7FF);
        ulong fraction = (ulong)bits & 0xFFFFFFFFFFFFFUL;
        var significand = new BigInteger(biased == 0 ? fraction : fraction | (1UL << 52));
        int binary = biased == 0 ? -1074 : biased - 1075;     // value = significand * 2^binary

        // value = whole * 10^decimalExponent exactly
        BigInteger whole;
        int decimalExponent;
        if (binary >= 0) { whole = significand << binary; decimalExponent = 0; }
        else { whole = significand * BigInteger.Pow(5, -binary); decimalExponent = binary; }

        string all = whole.ToString(CultureInfo.InvariantCulture);
        int exponent = all.Length + decimalExponent - 1;
        if (all.Length <= 17) return (all.TrimEnd('0'), exponent);

        string kept = all[..17];
        string rest = all[17..];
        bool up = rest[0] > '5' || (rest[0] == '5' && rest.AsSpan(1).ContainsAnyExcept('0')) || (rest[0] == '5' && (kept[^1] - '0') % 2 == 1);
        if (up)
        {
            var rounded = BigInteger.Parse(kept, CultureInfo.InvariantCulture) + 1;
            kept = rounded.ToString(CultureInfo.InvariantCulture);
            if (kept.Length > 17) { kept = kept[..17]; exponent++; }
        }
        return (kept.TrimEnd('0'), exponent);
    }
}
