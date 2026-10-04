using System.IO.Hashing;
using System.Text;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M817: the hashes <c>ltk_game_data</c> and <c>ltk_meta</c> compute, bit for bit. A name hashes by FNV-1a over its lowercase
/// UTF-8 (a bin's property, class and object names) or by XXH64 over its ASCII-lowercased UTF-8 (a WAD chunk path), and a name
/// spelled <c>0x</c> and the hash's hex digits IS that hash.
///
/// <para><b>Why this is not <see cref="ReyEngine.Core.Hashing.HashAlgorithms"/>.</b> The two lowercase differently. Rust's
/// <c>BinHash::hash_str</c> lowercases a name that holds a non-ASCII character with <c>char::to_lowercase</c> (<c>fnv1a.rs</c>:
/// the whole Unicode table, and U+0130 becomes two characters), while an XXH64 path hash lowercases ASCII only
/// (<c>WadHash::hash_str</c>, <c>path_hash</c>). <c>HashAlgorithms.WadPath</c> uses <c>ToLowerInvariant</c>, which lowercases
/// non-ASCII too, and other callers depend on it; this class gives the apply engine its own exact functions.</para>
/// </summary>
public static class LtkHash
{
    /// <summary><c>BinHash::hash_str</c>: FNV-1a over the lowercase UTF-8 of <paramref name="text"/>.</summary>
    public static uint Fnv1aLower(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        uint hash = 0x811c9dc5;
        bool ascii = true;
        foreach (char c in text)
            if (c >= 0x80) { ascii = false; break; }
        if (ascii)
        {
            foreach (char c in text)
            {
                hash ^= c is >= 'A' and <= 'Z' ? (uint)(c + 32) : c;
                hash *= 0x01000193;
            }
            return hash;
        }

        Span<byte> utf8 = stackalloc byte[4];
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (RustLowercase.TryMultiple(rune.Value, out int first, out int second))
            {
                hash = Mix(hash, new Rune(first), utf8);
                hash = Mix(hash, new Rune(second), utf8);
            }
            else hash = Mix(hash, new Rune(RustLowercase.Single(rune.Value)), utf8);
        }
        return hash;
    }

    private static uint Mix(uint hash, Rune rune, Span<byte> buffer)
    {
        int length = rune.EncodeToUtf8(buffer);
        for (int i = 0; i < length; i++)
        {
            hash ^= buffer[i];
            hash *= 0x01000193;
        }
        return hash;
    }

    /// <summary><c>path_hash</c> / <c>WadHash::hash_str</c>: XXH64 (seed 0) over the ASCII-lowercased UTF-8 of
    /// <paramref name="path"/>. Characters above U+007F are not lowercased.</summary>
    public static ulong Xxh64Path(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] utf8 = Encoding.UTF8.GetBytes(path);
        for (int i = 0; i < utf8.Length; i++)
            if (utf8[i] is >= (byte)'A' and <= (byte)'Z') utf8[i] += 32;
        return XxHash64.HashToUInt64(utf8);
    }

    /// <summary>The hash <c>0x</c> and exactly 8 hexadecimal digits spell (<c>hex32</c>). The prefix is a lowercase <c>x</c>: <c>0X</c>
    /// is a name. The digits may be of either case.</summary>
    public static bool TryHex32(string text, out uint value)
    {
        value = 0;
        if (text.Length != 10 || text[0] != '0' || text[1] != 'x') return false;
        uint parsed = 0;
        for (int i = 2; i < 10; i++)
        {
            int digit = HexDigit(text[i]);
            if (digit < 0) return false;
            parsed = (parsed << 4) | (uint)digit;
        }
        value = parsed;
        return true;
    }

    /// <summary>The hash <c>0x</c> and exactly 16 hexadecimal digits spell (<c>hex</c> with 16 digits).</summary>
    public static bool TryHex64(string text, out ulong value)
    {
        value = 0;
        if (text.Length != 18 || text[0] != '0' || text[1] != 'x') return false;
        ulong parsed = 0;
        for (int i = 2; i < 18; i++)
        {
            int digit = HexDigit(text[i]);
            if (digit < 0) return false;
            parsed = (parsed << 4) | (uint)digit;
        }
        value = parsed;
        return true;
    }

    /// <summary>Whether <paramref name="text"/> is hash-form: <c>0x</c> and exactly 8 hexadecimal digits (<c>is_hash_form</c>).</summary>
    public static bool IsHashForm(string text) => TryHex32(text, out _);

    /// <summary><c>hash32_of</c>: the hash a hash-form name spells, else the hash of the name.</summary>
    public static uint Hash32Of(string name) => TryHex32(name, out uint hash) ? hash : Fnv1aLower(name);

    /// <summary><c>hash64_of</c>: the hash <c>0x</c> and 16 hexadecimal digits spell, else the hash of the path.</summary>
    public static ulong Hash64Of(string path) => TryHex64(path, out ulong hash) ? hash : Xxh64Path(path);

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
