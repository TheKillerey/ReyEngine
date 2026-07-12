namespace ReyEngine.Formats.Audio;

/// <summary>M56: Wwise object-name hashing — FNV-1 (NOT 1a: multiply then xor), 32-bit, lowercase ASCII.
/// Event names like "Play_sfx_Env_Map12_Bloom_bridge_ambience" hash to the HIRC Event object id.</summary>
public static class WwiseHash
{
    public static uint Fnv1(string s)
    {
        uint h = 0x811c9dc5;
        foreach (char c in s)
        {
            uint b = c is >= 'A' and <= 'Z' ? (uint)(c + 32) : c;
            h = unchecked(h * 0x01000193) ^ b;
        }
        return h;
    }
}
