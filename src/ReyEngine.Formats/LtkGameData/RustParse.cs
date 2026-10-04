using System.Globalization;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M817: Rust's <c>str::parse</c> for the numbers a declaration's keys spell, with Rust's grammar rather than .NET's. A key
/// <c>{k}</c> is parsed as the map's key type (<c>text.parse::&lt;T&gt;()</c>) and a map key in a value as <c>i128</c> or <c>f64</c>
/// (<c>coerce.rs</c> <c>key</c>): no whitespace, no thousands separator, an optional <c>+</c> everywhere and a <c>-</c> only on a signed
/// type, and for floats <c>inf</c>, <c>infinity</c> and <c>nan</c> in any case, <c>1.</c> and <c>.5</c>.
/// </summary>
public static class RustParse
{
    /// <summary><c>str::parse::&lt;i128&gt;</c>.</summary>
    public static bool TryParseInt128(string text, out Int128 value)
    {
        value = 0;
        if (!TryMagnitude(text, signedType: true, out bool negative, out UInt128 magnitude)) return false;
        if (negative)
        {
            if (magnitude > (UInt128)Int128.MaxValue + 1) return false;
            value = magnitude == (UInt128)Int128.MaxValue + 1 ? Int128.MinValue : -(Int128)magnitude;
        }
        else
        {
            if (magnitude > (UInt128)Int128.MaxValue) return false;
            value = (Int128)magnitude;
        }
        return true;
    }

    /// <summary><c>str::parse::&lt;T&gt;</c> for one of the integer kinds; the value as 64 bits, sign-extended for a signed kind.</summary>
    public static bool TryParseInteger(string text, PropKind kind, out ulong bits)
    {
        bits = 0;
        bool signed = kind is PropKind.I8 or PropKind.I16 or PropKind.I32 or PropKind.I64;
        if (!TryMagnitude(text, signed, out bool negative, out UInt128 magnitude)) return false;
        (UInt128 maxPositive, UInt128 maxNegative) = kind switch
        {
            PropKind.I8 => ((UInt128)sbyte.MaxValue, (UInt128)sbyte.MaxValue + 1),
            PropKind.U8 => ((UInt128)byte.MaxValue, (UInt128)0),
            PropKind.I16 => ((UInt128)short.MaxValue, (UInt128)short.MaxValue + 1),
            PropKind.U16 => ((UInt128)ushort.MaxValue, (UInt128)0),
            PropKind.I32 => ((UInt128)int.MaxValue, (UInt128)int.MaxValue + 1),
            PropKind.U32 => ((UInt128)uint.MaxValue, (UInt128)0),
            PropKind.I64 => ((UInt128)long.MaxValue, (UInt128)long.MaxValue + 1),
            _ => ((UInt128)ulong.MaxValue, (UInt128)0),
        };
        if (negative)
        {
            if (magnitude > maxNegative) return false;
            bits = unchecked((ulong)(-(long)(ulong)magnitude));
            if (magnitude == 0) bits = 0;
        }
        else
        {
            if (magnitude > maxPositive) return false;
            bits = (ulong)magnitude;
        }
        return true;
    }

    /// <summary>The sign and the digits of an integer: an optional <c>+</c> or (on a signed type) <c>-</c>, then ASCII digits; false for
    /// anything else, a lone sign and an empty string included, and for a magnitude past 128 bits.</summary>
    private static bool TryMagnitude(string text, bool signedType, out bool negative, out UInt128 magnitude)
    {
        negative = false;
        magnitude = 0;
        int at = 0;
        if (text.Length == 0) return false;
        if (text[0] == '+') at = 1;
        else if (text[0] == '-' && signedType) { negative = true; at = 1; }
        if (at == text.Length) return false;
        for (; at < text.Length; at++)
        {
            char c = text[at];
            if (c is < '0' or > '9') return false;
            UInt128 next = magnitude * 10 + (uint)(c - '0');
            if (magnitude > UInt128.MaxValue / 10 || next < magnitude * 10) return false;
            magnitude = next;
        }
        return true;
    }

    /// <summary><c>str::parse::&lt;f64&gt;</c> (<c>dec2flt</c>): the nearest double, or an infinity for a value too large.</summary>
    public static bool TryParseDouble(string text, out double value)
    {
        value = 0;
        if (text.Length == 0) return false;
        bool negative = text[0] == '-';
        string body = text[0] is '-' or '+' ? text[1..] : text;
        if (body.Length == 0) return false;

        if (IsDecimal(body))
        {
            value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            return true;
        }
        if (body.Equals("inf", StringComparison.OrdinalIgnoreCase) || body.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            value = negative ? double.NegativeInfinity : double.PositiveInfinity;
            return true;
        }
        if (body.Equals("nan", StringComparison.OrdinalIgnoreCase))
        {
            // Rust's NaN has the sign bit clear; .NET's double.NaN has it set
            value = BitConverter.Int64BitsToDouble(negative ? unchecked((long)0xFFF8000000000000UL) : 0x7FF8000000000000L);
            return true;
        }
        return false;
    }

    /// <summary><c>str::parse::&lt;f32&gt;</c> of a JSON number's text: rounded once, directly to single precision.</summary>
    public static bool TryParseSingle(string text, out float value)
    {
        value = 0;
        string body = text.Length > 0 && text[0] is '-' or '+' ? text[1..] : text;
        if (!IsDecimal(body)) return false;
        value = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Digits with an optional point and an optional exponent, at least one digit before the exponent: <c>1</c>, <c>1.</c>,
    /// <c>.5</c>, <c>1e5</c>, <c>1.5E-3</c>.</summary>
    private static bool IsDecimal(string body)
    {
        int at = 0, digits = 0;
        while (at < body.Length && body[at] is >= '0' and <= '9') { at++; digits++; }
        if (at < body.Length && body[at] == '.')
        {
            at++;
            while (at < body.Length && body[at] is >= '0' and <= '9') { at++; digits++; }
        }
        if (digits == 0) return false;
        if (at < body.Length && body[at] is 'e' or 'E')
        {
            at++;
            if (at < body.Length && body[at] is '+' or '-') at++;
            int exponent = 0;
            while (at < body.Length && body[at] is >= '0' and <= '9') { at++; exponent++; }
            if (exponent == 0) return false;
        }
        return at == body.Length;
    }
}
