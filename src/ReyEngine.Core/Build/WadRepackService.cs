using System.IO.Hashing;

namespace ReyEngine.Core.Build;

/// <summary>
/// Non-destructive WAD repack: copies the source WAD, appends each modified chunk's data at the
/// end (stored uncompressed), and patches that chunk's 32-byte table-of-contents entry to point
/// at it. Everything else — including the subchunk table and unresolved chunks — stays byte-exact.
/// Only supports replacing existing chunks (adding new chunks needs a TOC rebuild; see TODO).
/// </summary>
public static class WadRepackService
{
    private const long HeaderChunkCountOffset = 268; // 2+1+1+256+8
    private const long TocStart = 272;
    private const int TocEntrySize = 32;

    public static void Repack(string sourceWadPath, IReadOnlyDictionary<ulong, byte[]> overrides,
        string outputPath, BuildReport report, IProgress<float>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(sourceWadPath, outputPath, overwrite: true);

        using var fs = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite);
        using var br = new BinaryReader(fs);
        using var bw = new BinaryWriter(fs);

        fs.Seek(HeaderChunkCountOffset, SeekOrigin.Begin);
        int chunkCount = br.ReadInt32();
        report.ChunksTotal = chunkCount;

        // map path hash -> TOC entry position
        var tocOffset = new Dictionary<ulong, long>(chunkCount);
        for (int i = 0; i < chunkCount; i++)
        {
            long pos = TocStart + (long)i * TocEntrySize;
            fs.Seek(pos, SeekOrigin.Begin);
            tocOffset[br.ReadUInt64()] = pos;
        }

        int replaced = 0, done = 0;
        foreach (var (hash, data) in overrides)
        {
            ct.ThrowIfCancellationRequested();
            if (!tocOffset.TryGetValue(hash, out var entryPos))
            {
                report.Add(BuildSeverity.Warning, $"0x{hash:x16}: not in source WAD — adding new chunks isn't supported yet (skipped).");
                report.ChunksFailed++;
                continue;
            }

            fs.Seek(0, SeekOrigin.End);
            long dataOffset = fs.Position;
            if (dataOffset + data.Length > uint.MaxValue)
            {
                report.Add(BuildSeverity.Error, "Output would exceed the 4 GB WAD offset limit.");
                break;
            }

            bw.Write(data);

            fs.Seek(entryPos, SeekOrigin.Begin);
            bw.Write(hash);                          // pathHash       u64
            bw.Write((uint)dataOffset);              // dataOffset     u32
            bw.Write(data.Length);                   // compressedSize i32
            bw.Write(data.Length);                   // uncompressed   i32
            bw.Write((byte)0);                       // None, 0 subchunks
            bw.Write(false);                         // isDuplicated
            bw.Write((ushort)0);                     // startSubChunk
            bw.Write(XxHash64.HashToUInt64(data));   // checksum       u64
            replaced++;
            progress?.Report((float)(++done) / Math.Max(1, overrides.Count));
        }

        bw.Flush();
        report.ChunksReplaced = replaced;
        report.ChunksCopied = chunkCount - replaced;
        report.OutputSize = fs.Length;
    }
}
