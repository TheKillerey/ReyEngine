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
    /// <summary>M589: chunks that reuse another entry's data region instead of storing a second copy.</summary>
    public int SharedChunks { get; set; }
    public int Skipped { get; set; }
    public long InputBytes { get; set; }
    public long OutputBytes { get; set; }
    public bool Reopened { get; set; }
    public string Validation { get; set; } = "";
    public List<string> Warnings { get; } = new();
    /// <summary>M132: files excluded by the known-game-types filter (relative paths).</summary>
    public List<string> CleanedUnknown { get; } = new();
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

    /// <summary>M132: every file type the game's wads actually carry. Anything else in a staged
    /// folder is editor leftovers (notes, sources, thumbnails) — the game never requests it, and it
    /// bloats/pollutes the package.</summary>
    private static readonly HashSet<string> KnownGameExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bin", ".bnk", ".wpk", ".dds", ".tex", ".skn", ".skl", ".anm", ".scb", ".sco",
        ".mapgeo", ".nvr", ".preload", ".stringtable", ".luaobj", ".troybin", ".dat", ".cfg",
        ".png", ".jpg", ".jpeg", ".webm", ".ogg", ".svg", ".ttf", ".otf", ".subchunktoc",
    };

    public static WadPackReport Pack(string folder, string outputWad, IProgress<float>? progress = null,
        CancellationToken ct = default, bool knownTypesOnly = false)
    {
        var report = new WadPackReport { OutputPath = outputWad };

        // Resolve files → hash (last file wins on a hash clash).
        var byHash = new Dictionary<ulong, string>();
        foreach (var (hash, path) in EnumerateChunkFiles(folder))
        {
            if (knownTypesOnly)
            {
                var rel = Path.GetRelativePath(folder, path).Replace('\\', '/');
                // hash-named loose chunks carry no extension info worth trusting — always packed
                bool loose = !rel.Contains('/') && Path.GetFileNameWithoutExtension(rel).Length == 16;
                if (!loose && !KnownGameExtensions.Contains(Path.GetExtension(rel)) && !IsDx11ShaderCacheEntry(rel))
                {
                    report.CleanedUnknown.Add(rel);
                    report.Skipped++;
                    continue;
                }
            }
            byHash[hash] = path;
        }

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

        // M589: STORE EACH DISTINCT BLOB ONCE. Two paths holding identical bytes get two TOC entries
        // pointing at one data region - which is what Riot does and what the packer never did.
        //
        // Measured on a real 3,600-file map mod: 686 MB packed one-blob-per-entry against 419 MB for the
        // author's own build of the same content, and the whole difference was this. Riot's shipped
        // Map11.wad shares a region between 11,551 of its 28,858 entries, avoiding 8,786 redundant copies.
        //
        // isDuplicated is left FALSE, which is Riot's v3.4 convention rather than a guess: of those 11,551
        // shared entries, ZERO carry the flag. (A v3.3 wad built by an older tool flags all of its 1,191 -
        // the two conventions disagree, and we write v3.4.)
        var offsetOfBlob = new Dictionary<BlobKey, long>();
        var blobOrder = new List<byte[]>();
        long dataStart = TocStart + (long)entries.Count * TocEntrySize;
        long cursor = dataStart;
        var entryOffset = new long[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var key = new BlobKey(entries[i].data);
            if (offsetOfBlob.TryGetValue(key, out long existing)) { entryOffset[i] = existing; continue; }
            offsetOfBlob[key] = cursor;
            entryOffset[i] = cursor;
            blobOrder.Add(entries[i].data);
            cursor += entries[i].data.Length;
        }
        int shared = entries.Count - blobOrder.Count;

        var dir = Path.GetDirectoryName(outputWad);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (cursor > uint.MaxValue)
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

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                bw.Write(e.hash);                          // pathHash       u64
                bw.Write((uint)entryOffset[i]);            // dataOffset     u32 (shared between identical blobs)
                bw.Write(e.data.Length);                   // compressedSize i32
                bw.Write(e.uncompressed);                  // uncompressed   i32
                bw.Write(e.compression);                   // compression, 0 subchunks
                bw.Write(false);                           // isDuplicated — 0 of Riot's 11,551 shared entries set it
                bw.Write((ushort)0);                       // startSubChunk
                bw.Write(XxHash3.HashToUInt64(e.data));    // checksum: XXH3-64 of stored data (NOT XxHash64 - the game validates this)
            }
            foreach (var blob in blobOrder) bw.Write(blob);
            report.OutputBytes = fs.Length;
        }

        report.Chunks = entries.Count;
        report.SharedChunks = shared;
        try
        {
            using var wad = new WadFile(outputWad);
            report.Reopened = true;
            report.Validation = $"Reopened OK — {wad.Chunks.Count:n0} chunks.";
        }
        catch (Exception ex) { report.Warnings.Add($"Built WAD failed to reopen: {ex.Message}"); }

        return report;
    }

    /// <summary>M312: generated shader-cache TOCs and containers do not have conventional extensions:
    /// <c>foo.vs-dx11</c> and <c>foo.ps-dx11_400</c>. They are real game assets and must survive the
    /// known-types cleanup when the explicit ShaderCache project folder is built.</summary>
    public static bool IsDx11ShaderCacheEntry(string relativePath)
    {
        string path = relativePath.Replace('\\', '/');
        if (!path.StartsWith("assets/shaders/generated/", StringComparison.OrdinalIgnoreCase)) return false;
        int token = path.LastIndexOf("-dx11", StringComparison.OrdinalIgnoreCase);
        if (token < 0) token = path.LastIndexOf(".dx11", StringComparison.OrdinalIgnoreCase);
        if (token < 3) return false;
        string stage = path.Substring(token - 3, 3);
        if (!stage.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            && !stage.Equals(".ps", StringComparison.OrdinalIgnoreCase)) return false;
        string tail = path[(token + 5)..];
        return tail.Length == 0 || (tail[0] == '_' && tail.Length > 1 && tail[1..].All(char.IsDigit));
    }

    /// <summary>Public since M134 — the Overlay Footprint analysis walks the same file set the packer would.</summary>
    public static IEnumerable<(ulong hash, string path)> EnumerateChunkFiles(string folder)
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

/// <summary>
/// M589: identity of a stored blob, for the dedup map — hashed for lookup, compared BYTE FOR BYTE.
///
/// <para>Hashing alone would be a silent corruption waiting to happen: a 64-bit collision between two
/// different assets would point one entry at the other's bytes, and the mod would ship the wrong file with
/// nothing to show for it. The hash only narrows the candidates; equality is what decides.</para>
/// </summary>
internal readonly struct BlobKey : IEquatable<BlobKey>
{
    private readonly byte[] _data;
    private readonly int _hash;

    public BlobKey(byte[] data)
    {
        _data = data;
        _hash = HashCode.Combine(data.Length, XxHash3.HashToUInt64(data));
    }

    public bool Equals(BlobKey other) =>
        _hash == other._hash && _data.AsSpan().SequenceEqual(other._data);

    public override bool Equals(object? obj) => obj is BlobKey other && Equals(other);
    public override int GetHashCode() => _hash;
}
