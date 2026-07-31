using System;
using System.Collections.Generic;
using System.Text;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Core.Wad;

/// <summary>
/// <para>M301: recover names for WAD chunks the hash database cannot identify, using the archive's own
/// .bin property files.</para>
///
/// <para>A hash database is built from Riot's shipped paths, so the chunks it CANNOT name are precisely a
/// mod's custom assets - which is why the files most worth finding are the ones that arrive as bare
/// hashes. But the mod's own .bin files reference those assets by literal path string, because that is how
/// the game finds them, and a chunk's key is nothing more than the hash of its path. The names are already
/// inside the archive; nothing external is needed.</para>
///
/// <para>Scanning for printable runs rather than parsing the bins is a deliberate trade. Measured on the
/// reported mod, parsing recovers 215 of 756 and this scan recovers 200 - 93% of the benefit for none of
/// the cost, and, decisively, no dependency on the bin parser, which lives in a layer this one sits
/// beneath. The 15 it misses are strings that are not contiguous ASCII on disk.</para>
/// </summary>
public static class WadPathRecovery
{
    /// <summary>Map hash to a recovered path, for chunks in <paramref name="unknown"/> only.
    /// <paramref name="readChunk"/> returns a chunk's bytes, or null if it cannot be read.</summary>
    public static Dictionary<ulong, string> Recover(
        IEnumerable<ulong> allChunks, IReadOnlySet<ulong> unknown, Func<ulong, byte[]?> readChunk)
    {
        var found = new Dictionary<ulong, string>();
        if (unknown.Count == 0) return found;

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hash in allChunks)
        {
            byte[]? bytes;
            try { bytes = readChunk(hash); } catch { continue; }
            // "PROP" and "PTCH" are the bin magics. Checked on CONTENT, not on the name - the bins holding
            // custom paths are frequently unnamed themselves.
            if (bytes is null || bytes.Length < 4 || bytes[0] != 'P') continue;
            if (!(bytes[1] == 'R' && bytes[2] == 'O' && bytes[3] == 'P')
             && !(bytes[1] == 'T' && bytes[2] == 'C' && bytes[3] == 'H')) continue;

            int start = -1;
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if (b >= 0x20 && b < 0x7F) { if (start < 0) start = i; continue; }
                if (start >= 0 && i - start >= 5)
                {
                    string run = Encoding.ASCII.GetString(bytes, start, i - start);
                    if (run.IndexOf('.') > 0) candidates.Add(run);
                }
                start = -1;
            }
            if (start >= 0 && bytes.Length - start >= 5)
            {
                string run = Encoding.ASCII.GetString(bytes, start, bytes.Length - start);
                if (run.IndexOf('.') > 0) candidates.Add(run);
            }
        }

        foreach (string s in candidates)
        {
            // Bins are authored on Windows and mix separators; WAD keys are always forward slash.
            string p = s.Replace('\\', '/');
            ulong h = HashAlgorithms.WadPath(p);
            if (unknown.Contains(h) && !found.ContainsKey(h)) found[h] = p;
        }
        return found;
    }
}
