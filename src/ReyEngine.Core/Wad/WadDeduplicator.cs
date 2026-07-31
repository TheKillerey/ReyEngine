using System;
using System.Collections.Generic;
using System.IO;

namespace ReyEngine.Core.Wad;

/// <summary>
/// <para>M298: rewrite a WAD whose table of contents lists one path hash twice, so LeagueToolkit will open
/// it.</para>
///
/// <para>LeagueToolkit builds its chunk map into a dictionary and throws on the first collision, rejecting
/// the ENTIRE file - a single redundant descriptor made a 382 MB mod unimportable, which is how this was
/// found. The WAD format itself does not agree that duplicates are fatal: every descriptor carries an
/// <c>isDuplicate</c> byte, so they are expressible by design.</para>
///
/// <para><b>This repairs only what is provably redundant.</b> If two descriptors for one hash disagree
/// about where the data is or how big it is, the file genuinely says two different things about one asset
/// and choosing between them would be a guess dressed as a fix - so the repair is refused and the original
/// error stands. Silently picking one would be the worse failure: an import that appears to succeed while
/// quietly containing the wrong asset.</para>
/// </summary>
public static class WadDeduplicator
{
    // v3 layout: "RW", major, minor, signature[256], checksum[8], u32 chunkCount, then 32-byte descriptors.
    private const int HeaderSize = 2 + 1 + 1 + 256 + 8;   // up to, not including, the count
    private const int DescriptorSize = 32;

    /// <summary>Write a de-duplicated copy to a temp file and return its path, or null when the file cannot
    /// be repaired honestly (not a v3 WAD, no duplicates, or duplicates that disagree).</summary>
    public static string? TryRepair(string path, out string? note)
    {
        note = null;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            if (fs.Length < HeaderSize + 4) return null;
            if (br.ReadByte() != (byte)'R' || br.ReadByte() != (byte)'W') return null;
            byte major = br.ReadByte();
            br.ReadByte();                                   // minor
            if (major != 3) return null;                     // only v3 is understood here

            fs.Position = HeaderSize;
            uint count = br.ReadUInt32();
            long tocStart = fs.Position;
            long tocEnd = tocStart + (long)count * DescriptorSize;
            if (count == 0 || tocEnd > fs.Length) return null;

            var raw = new byte[count * DescriptorSize];
            if (fs.Read(raw, 0, raw.Length) != raw.Length) return null;

            var firstFor = new Dictionary<ulong, int>((int)count);
            var keep = new List<int>((int)count);
            int dropped = 0;

            for (int i = 0; i < count; i++)
            {
                int o = i * DescriptorSize;
                ulong hash = BitConverter.ToUInt64(raw, o);
                if (!firstFor.TryGetValue(hash, out int first)) { firstFor[hash] = i; keep.Add(i); continue; }

                // Redundant only if it describes the SAME data: offset, compressed size, uncompressed size
                // and the flags byte. Anything else and we refuse rather than choose.
                int f = first * DescriptorSize;
                bool identical = true;
                for (int b = 8; b < 21 && identical; b++) identical = raw[o + b] == raw[f + b];
                if (!identical)
                {
                    note = $"chunk {hash} appears twice with DIFFERENT data - not repaired";
                    return null;
                }
                dropped++;
            }

            if (dropped == 0) return null;                   // the open failed for some other reason

            string temp = Path.Combine(Path.GetTempPath(), $"reywad-{Guid.NewGuid():N}.wad.client");
            using (var outFs = File.Create(temp))
            {
                // Header verbatim, then the surviving descriptor count.
                fs.Position = 0;
                var head = new byte[HeaderSize];
                fs.Read(head, 0, head.Length);
                outFs.Write(head, 0, head.Length);
                outFs.Write(BitConverter.GetBytes((uint)keep.Count), 0, 4);

                foreach (int i in keep) outFs.Write(raw, i * DescriptorSize, DescriptorSize);

                // Chunk offsets are ABSOLUTE, so the data must stay exactly where it was. A shorter TOC is
                // padded back up to the original data start rather than closing the gap - moving the data
                // would invalidate every offset we just copied.
                long pad = tocEnd - outFs.Position;
                if (pad < 0) { File.Delete(temp); return null; }
                if (pad > 0) outFs.Write(new byte[pad], 0, (int)pad);

                fs.Position = tocEnd;
                fs.CopyTo(outFs);
            }

            note = $"{dropped} duplicate chunk descriptor(s) dropped - each pointed at the same data as the "
                 + "entry it collided with, so nothing was lost";
            return temp;
        }
        catch
        {
            // A repair that itself fails must leave the original error to speak. Never mask it.
            return null;
        }
    }
}
