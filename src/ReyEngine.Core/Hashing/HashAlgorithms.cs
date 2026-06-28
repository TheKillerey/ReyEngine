using System.IO.Hashing;
using System.Text;

namespace ReyEngine.Core.Hashing;

/// <summary>
/// League uses several string hashes depending on the file type:
///   - XxHash64 (lowercased path) for WAD chunk keys
///   - FNV-1a 32 (lowercased) for .bin entry/field/class names
///   - ELF / SDBM for some legacy lookups
/// All operate on the lowercased string.
/// </summary>
public static class HashAlgorithms
{
    public static uint Fnv1a(string input)
    {
        const uint prime = 16777619u;
        uint hash = 2166136261u;
        foreach (char c in input)
        {
            byte b = (byte)((c >= 'A' && c <= 'Z') ? c + 32 : c);
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }

    public static uint Elf(string input)
    {
        uint hash = 0;
        foreach (char c in input)
        {
            byte b = (byte)((c >= 'A' && c <= 'Z') ? c + 32 : c);
            hash = (hash << 4) + b;
            uint high = hash & 0xF0000000u;
            if (high != 0)
                hash ^= high >> 24;
            hash &= ~high;
        }
        return hash;
    }

    public static ulong Sdbm(string input)
    {
        ulong hash = 0;
        foreach (char c in input)
        {
            byte b = (byte)((c >= 'A' && c <= 'Z') ? c + 32 : c);
            hash = b + (hash << 6) + (hash << 16) - hash;
        }
        return hash;
    }

    /// <summary>WAD chunk key: XxHash64 of the lowercased UTF-8 path.</summary>
    public static ulong WadPath(string path)
    {
        ReadOnlySpan<byte> bytes = Encoding.UTF8.GetBytes(path.ToLowerInvariant());
        return XxHash64.HashToUInt64(bytes);
    }
}
