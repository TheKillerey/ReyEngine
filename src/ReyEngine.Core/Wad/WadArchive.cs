using CommunityToolkit.HighPerformance.Buffers;
using LeagueToolkit.Core.Wad;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Core.Wad;

/// <summary>
/// Thin, ReyEngine-friendly wrapper over LeagueToolkit's <see cref="WadFile"/>:
/// opens an archive, exposes resolved entries, and extracts chunk bytes on demand.
/// </summary>
public sealed class WadArchive : IDisposable
{
    private readonly WadFile _wad;

    public string FilePath { get; }
    public string Name => System.IO.Path.GetFileName(FilePath);
    public IReadOnlyList<WadAssetEntry> Entries { get; }
    public int ResolvedCount { get; }

    private WadArchive(string path, WadFile wad, List<WadAssetEntry> entries, int resolved)
    {
        FilePath = path;
        _wad = wad;
        Entries = entries;
        ResolvedCount = resolved;
    }

    public static WadArchive Open(string path, HashDictionary? hashes = null)
    {
        var wad = new WadFile(path);
        var list = new List<WadAssetEntry>(wad.Chunks.Count);
        int resolved = 0;

        foreach (var (hash, chunk) in wad.Chunks)
        {
            bool isResolved = hashes is not null && hashes.TryGetPath(hash, out var known);
            string p = isResolved ? hashes!.ResolvePath(hash) : $"0x{hash:x16}.unknown";
            if (isResolved) resolved++;

            list.Add(new WadAssetEntry
            {
                PathHash = hash,
                Path = p,
                IsResolved = isResolved,
                CompressedSize = chunk.CompressedSize,
                UncompressedSize = chunk.UncompressedSize,
                Compression = chunk.Compression.ToString(),
                Type = isResolved ? AssetTypeDetector.FromPath(p) : AssetType.Unknown,
            });
        }

        return new WadArchive(path, wad, list, resolved);
    }

    /// <summary>Extract and decompress a chunk to a managed byte array.</summary>
    public byte[] Extract(WadAssetEntry entry) => Extract(entry.PathHash);

    public byte[] Extract(ulong pathHash)
    {
        var chunk = _wad.Chunks[pathHash];
        using MemoryOwner<byte> owner = _wad.LoadChunkDecompressed(chunk);
        return owner.Span.ToArray();
    }

    public void ExtractToFile(WadAssetEntry entry, string outPath)
    {
        var dir = System.IO.Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(outPath, Extract(entry));
    }

    public void Dispose() => _wad.Dispose();
}
