namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M817: Rust's <c>char::to_lowercase</c>, as a table. <c>ltk_hash</c> hashes a name that holds a non-ASCII character
/// by lowercasing every <c>char</c> with it (<c>impls/fnv1a.rs</c>), and the hash a bin stores for such a name is that
/// one. .NET's <c>ToLowerInvariant</c> is a different function: it leaves U+0130 alone, where Rust's gives
/// <c>i</c> and U+0307, and its Unicode version is its own.
///
/// <para>The table is dumped from rustc 1.92.0 (Unicode 17.0.0): every scalar value whose lowercase is not itself, 1,488
/// of them, written as runs <c>(first code point, count, step, delta)</c> where each code point <c>first + k * step</c>
/// lowercases to itself plus <c>delta</c>. The one character with a longer lowercase, U+0130, is
/// <see cref="TryMultiple"/>. A newer Rust may lowercase a newer character; regenerate it with
/// <c>gdcheck lower-table</c> (<c>.codex_tmp/M817</c>).</para>
/// </summary>
internal static class RustLowercase
{
    private static readonly (int Start, int Count, int Step, int Delta)[] Runs =
    {
        (0x41, 26, 1, 32), (0xC0, 23, 1, 32), (0xD8, 7, 1, 32), (0x100, 24, 2, 1), (0x132, 3, 2, 1),
        (0x139, 8, 2, 1), (0x14A, 23, 2, 1), (0x178, 1, 1, -121), (0x179, 3, 2, 1), (0x181, 1, 1, 210),
        (0x182, 2, 2, 1), (0x186, 1, 1, 206), (0x187, 1, 1, 1), (0x189, 2, 1, 205), (0x18B, 1, 1, 1),
        (0x18E, 1, 1, 79), (0x18F, 1, 1, 202), (0x190, 1, 1, 203), (0x191, 1, 1, 1), (0x193, 1, 1, 205),
        (0x194, 1, 1, 207), (0x196, 1, 1, 211), (0x197, 1, 1, 209), (0x198, 1, 1, 1), (0x19C, 1, 1, 211),
        (0x19D, 1, 1, 213), (0x19F, 1, 1, 214), (0x1A0, 3, 2, 1), (0x1A6, 1, 1, 218), (0x1A7, 1, 1, 1),
        (0x1A9, 1, 1, 218), (0x1AC, 1, 1, 1), (0x1AE, 1, 1, 218), (0x1AF, 1, 1, 1), (0x1B1, 2, 1, 217),
        (0x1B3, 2, 2, 1), (0x1B7, 1, 1, 219), (0x1B8, 1, 1, 1), (0x1BC, 1, 1, 1), (0x1C4, 1, 1, 2),
        (0x1C5, 1, 1, 1), (0x1C7, 1, 1, 2), (0x1C8, 1, 1, 1), (0x1CA, 1, 1, 2), (0x1CB, 9, 2, 1),
        (0x1DE, 9, 2, 1), (0x1F1, 1, 1, 2), (0x1F2, 2, 2, 1), (0x1F6, 1, 1, -97), (0x1F7, 1, 1, -56),
        (0x1F8, 20, 2, 1), (0x220, 1, 1, -130), (0x222, 9, 2, 1), (0x23A, 1, 1, 10795), (0x23B, 1, 1, 1),
        (0x23D, 1, 1, -163), (0x23E, 1, 1, 10792), (0x241, 1, 1, 1), (0x243, 1, 1, -195), (0x244, 1, 1, 69),
        (0x245, 1, 1, 71), (0x246, 5, 2, 1), (0x370, 2, 2, 1), (0x376, 1, 1, 1), (0x37F, 1, 1, 116),
        (0x386, 1, 1, 38), (0x388, 3, 1, 37), (0x38C, 1, 1, 64), (0x38E, 2, 1, 63), (0x391, 17, 1, 32),
        (0x3A3, 9, 1, 32), (0x3CF, 1, 1, 8), (0x3D8, 12, 2, 1), (0x3F4, 1, 1, -60), (0x3F7, 1, 1, 1),
        (0x3F9, 1, 1, -7), (0x3FA, 1, 1, 1), (0x3FD, 3, 1, -130), (0x400, 16, 1, 80), (0x410, 32, 1, 32),
        (0x460, 17, 2, 1), (0x48A, 27, 2, 1), (0x4C0, 1, 1, 15), (0x4C1, 7, 2, 1), (0x4D0, 48, 2, 1),
        (0x531, 38, 1, 48), (0x10A0, 38, 1, 7264), (0x10C7, 1, 1, 7264), (0x10CD, 1, 1, 7264), (0x13A0, 80, 1, 38864),
        (0x13F0, 6, 1, 8), (0x1C89, 1, 1, 1), (0x1C90, 43, 1, -3008), (0x1CBD, 3, 1, -3008), (0x1E00, 75, 2, 1),
        (0x1E9E, 1, 1, -7615), (0x1EA0, 48, 2, 1), (0x1F08, 8, 1, -8), (0x1F18, 6, 1, -8), (0x1F28, 8, 1, -8),
        (0x1F38, 8, 1, -8), (0x1F48, 6, 1, -8), (0x1F59, 4, 2, -8), (0x1F68, 8, 1, -8), (0x1F88, 8, 1, -8),
        (0x1F98, 8, 1, -8), (0x1FA8, 8, 1, -8), (0x1FB8, 2, 1, -8), (0x1FBA, 2, 1, -74), (0x1FBC, 1, 1, -9),
        (0x1FC8, 4, 1, -86), (0x1FCC, 1, 1, -9), (0x1FD8, 2, 1, -8), (0x1FDA, 2, 1, -100), (0x1FE8, 2, 1, -8),
        (0x1FEA, 2, 1, -112), (0x1FEC, 1, 1, -7), (0x1FF8, 2, 1, -128), (0x1FFA, 2, 1, -126), (0x1FFC, 1, 1, -9),
        (0x2126, 1, 1, -7517), (0x212A, 1, 1, -8383), (0x212B, 1, 1, -8262), (0x2132, 1, 1, 28), (0x2160, 16, 1, 16),
        (0x2183, 1, 1, 1), (0x24B6, 26, 1, 26), (0x2C00, 48, 1, 48), (0x2C60, 1, 1, 1), (0x2C62, 1, 1, -10743),
        (0x2C63, 1, 1, -3814), (0x2C64, 1, 1, -10727), (0x2C67, 3, 2, 1), (0x2C6D, 1, 1, -10780), (0x2C6E, 1, 1, -10749),
        (0x2C6F, 1, 1, -10783), (0x2C70, 1, 1, -10782), (0x2C72, 1, 1, 1), (0x2C75, 1, 1, 1), (0x2C7E, 2, 1, -10815),
        (0x2C80, 50, 2, 1), (0x2CEB, 2, 2, 1), (0x2CF2, 1, 1, 1), (0xA640, 23, 2, 1), (0xA680, 14, 2, 1),
        (0xA722, 7, 2, 1), (0xA732, 31, 2, 1), (0xA779, 2, 2, 1), (0xA77D, 1, 1, -35332), (0xA77E, 5, 2, 1),
        (0xA78B, 1, 1, 1), (0xA78D, 1, 1, -42280), (0xA790, 2, 2, 1), (0xA796, 10, 2, 1), (0xA7AA, 1, 1, -42308),
        (0xA7AB, 1, 1, -42319), (0xA7AC, 1, 1, -42315), (0xA7AD, 1, 1, -42305), (0xA7AE, 1, 1, -42308), (0xA7B0, 1, 1, -42258),
        (0xA7B1, 1, 1, -42282), (0xA7B2, 1, 1, -42261), (0xA7B3, 1, 1, 928), (0xA7B4, 8, 2, 1), (0xA7C4, 1, 1, -48),
        (0xA7C5, 1, 1, -42307), (0xA7C6, 1, 1, -35384), (0xA7C7, 2, 2, 1), (0xA7CB, 1, 1, -42343), (0xA7CC, 8, 2, 1),
        (0xA7DC, 1, 1, -42561), (0xA7F5, 1, 1, 1), (0xFF21, 26, 1, 32), (0x10400, 40, 1, 40), (0x104B0, 36, 1, 40),
        (0x10570, 11, 1, 39), (0x1057C, 15, 1, 39), (0x1058C, 7, 1, 39), (0x10594, 2, 1, 39), (0x10C80, 51, 1, 64),
        (0x10D50, 22, 1, 32), (0x118A0, 32, 1, 32), (0x16E40, 32, 1, 32), (0x16EA0, 25, 1, 27), (0x1E900, 34, 1, 34),
    };

    private static readonly int[] Starts = Array.ConvertAll(Runs, r => r.Start);

    /// <summary>The single character <paramref name="scalar"/> lowercases to, or itself.</summary>
    public static int Single(int scalar)
    {
        if (scalar < 0x41) return scalar;
        int at = Array.BinarySearch(Starts, scalar);
        if (at < 0) at = ~at - 1;
        if (at < 0) return scalar;
        var (start, count, step, delta) = Runs[at];
        int offset = scalar - start;
        if (offset < 0 || offset % step != 0 || offset / step >= count) return scalar;
        return scalar + delta;
    }

    /// <summary>U+0130 (I with dot above) lowercases to <c>i</c> and U+0307; no other scalar value lowercases to more than one.</summary>
    public static bool TryMultiple(int scalar, out int first, out int second)
    {
        first = second = 0;
        if (scalar != 0x130) return false;
        first = 0x69;
        second = 0x307;
        return true;
    }
}
