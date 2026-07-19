using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using LeagueToolkit.Core.Wad;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Core.Build;

public sealed class WadPackReport
{
    public string OutputPath { get; set; } = "";
    public int Chunks { get; set; }
    public int Skipped { get; set; }
    public long InputBytes { get; set; }
    public long OutputBytes { get; set; }
    public bool Reopened { get; set; }
    public string Validation { get; set; } = "";
    public List<string> Warnings { get; } = new();
    public bool Success => Reopened;
}

/// <summary>
/// Packs an unpacked-WAD folder (cslol style: files at their resolved path + loose &lt;hash&gt;.bin
/// chunks) into a fresh, distributable <c>.wad.client</c>. Writes a v3.4 header, a hash-sorted TOC and
/// Zstd-compressed chunk data (no subchunks — so it sidesteps the v3.4 subchunk-relocation problem of
/// editing an existing WAD), then reopens it to validate.
/// </summary>
public static class WadPackService
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    private const long TocStart = 272;
    private const int TocEntrySize = 32;

    public static WadPackReport Pack(string folder, string outputWad, IProgress<float>? progress = null, CancellationToken ct = default)
    {
        var report = new WadPackReport { OutputPath = outputWad };

        // Resolve files → hash (last file wins on a hash clash).
        var byHash = new Dictionary<ulong, string>();
        foreach (var (hash, path) in EnumerateChunkFiles(folder)) byHash[hash] = path;

        var entries = new List<(ulong hash, byte[] data, int uncompressed, byte compression)>(byHash.Count);
        using var compressor = new ZstdSharp.Compressor(level: 6);
        int done = 0;
        foreach (var (hash, path) in byHash)
        {
            ct.ThrowIfCancellationRequested();
            byte[] raw;
            try { raw = File.ReadAllBytes(path); }
            catch (Exception ex) { report.Warnings.Add($"skip {Path.GetFileName(path)}: {ex.Message}"); report.Skipped++; continue; }
            report.InputBytes += raw.Length;

            byte[] data; byte comp;
            if (raw.Length == 0) { data = raw; comp = 0; }
            else
            {
                var z = compressor.Wrap(raw).ToArray();
                if (z.Length < raw.Length) { data = z; comp = 3; }   // Zstd
                else { data = raw; comp = 0; }                       // None (no gain)
            }
            entries.Add((hash, data, raw.Length, comp));
            progress?.Report((float)(++done) / Math.Max(1, byHash.Count));
        }

        entries.Sort((a, b) => a.hash.CompareTo(b.hash));

        var dir = Path.GetDirectoryName(outputWad);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        long dataStart = TocStart + (long)entries.Count * TocEntrySize;
        if (dataStart + entries.Sum(e => (long)e.data.Length) > uint.MaxValue)
        {
            report.Warnings.Add("Packed WAD would exceed the 4 GB offset limit — split the mod into smaller WADs.");
            return report;
        }

        using (var fs = new FileStream(outputWad, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            var header = new byte[TocStart];
            header[0] = (byte)'R'; header[1] = (byte)'W'; header[2] = 3; header[3] = 4; // RW v3.4, zeroed signature+checksum
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(268), entries.Count);
            bw.Write(header);

            long off = dataStart;
            foreach (var e in entries)
            {
                bw.Write(e.hash);                          // pathHash       u64
                bw.Write((uint)off);                       // dataOffset     u32
                bw.Write(e.data.Length);                   // compressedSize i32
                bw.Write(e.uncompressed);                  // uncompressed   i32
                bw.Write(e.compression);                   // compression, 0 subchunks
                bw.Write(false);                           // isDuplicated
                bw.Write((ushort)0);                       // startSubChunk
                bw.Write(XxHash3.HashToUInt64(e.data));    // checksum: XXH3-64 of stored data (NOT XxHash64 - the game validates this)
                off += e.data.Length;
            }
            foreach (var e in entries) bw.Write(e.data);
            report.OutputBytes = fs.Length;
        }

        report.Chunks = entries.Count;
        try
        {
            using var wad = new WadFile(outputWad);
            report.Reopened = true;
            report.Validation = $"Reopened OK — {wad.Chunks.Count:n0} chunks.";
        }
        catch (Exception ex) { report.Warnings.Add($"Built WAD failed to reopen: {ex.Message}"); }

        return report;
    }

    private static IEnumerable<(ulong hash, string path)> EnumerateChunkFiles(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (rel.Equals("hashed_bins.json", OIC)) continue;
            if (rel.StartsWith(".reyengine/", OIC)) continue;

            ulong hash;
            string name = Path.GetFileNameWithoutExtension(rel);
            if (!rel.Contains('/') && name.Length == 16 && ulong.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
                hash = h;
            else
                hash = HashAlgorithms.WadPath(rel);
            yield return (hash, file);
        }
    }
}
