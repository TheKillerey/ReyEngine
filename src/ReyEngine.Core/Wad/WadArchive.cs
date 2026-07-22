using CommunityToolkit.HighPerformance.Buffers;
using LeagueToolkit.Core.Wad;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Core.Wad;

/// <summary>
/// Thin, ReyEngine-friendly wrapper over LeagueToolkit's <see cref="WadFile"/>:
/// opens an archive, exposes resolved entries, extracts chunk bytes, and supports
/// re-resolving paths after the hash dictionary changes.
/// </summary>
public sealed class WadArchive : IDisposable
{
    private readonly WadFile _wad;
    private readonly Dictionary<ulong, WadAssetEntry> _byHash;
    /// <summary>M119: LeagueToolkit's WadFile seeks ONE shared FileStream — concurrent extracts from
    /// different tasks interleave seeks and return garbage ("Invalid file signature" on files that are
    /// provably fine). The preview pipeline reads mesh/skl/textures, audio banks and the backdrop from
    /// the same archive in parallel, so every read takes this lock.</summary>
    private readonly object _readLock = new();

    public string FilePath { get; }
    public string Name => System.IO.Path.GetFileName(FilePath);
    public IReadOnlyList<WadAssetEntry> Entries { get; }
    public int ResolvedCount { get; private set; }

    private WadArchive(string path, WadFile wad, List<WadAssetEntry> entries)
    {
        FilePath = path;
        _wad = wad;
        Entries = entries;
        _byHash = entries.ToDictionary(e => e.PathHash);
    }

    public static WadArchive Open(string path, IHashResolver? resolver = null)
    {
        var wad = new WadFile(path);
        var list = new List<WadAssetEntry>(wad.Chunks.Count);

        foreach (var (hash, chunk) in wad.Chunks)
        {
            list.Add(new WadAssetEntry
            {
                PathHash = hash,
                Path = $"0x{hash:x16}.unknown",
                IsResolved = false,
                CompressedSize = chunk.CompressedSize,
                UncompressedSize = chunk.UncompressedSize,
                Compression = chunk.Compression.ToString(),
                Type = AssetType.Unknown,
            });
        }

        var archive = new WadArchive(path, wad, list);
        if (resolver is not null) archive.ReResolve(resolver);
        return archive;
    }

    /// <summary>Re-apply a resolver to all entries (path / resolved flag / type). Returns resolved count.</summary>
    public int ReResolve(IHashResolver resolver)
    {
        int resolved = 0;
        foreach (var e in Entries)
        {
            if (resolver.TryGetPath(e.PathHash, out var path))
            {
                e.Path = path;
                e.IsResolved = true;
                e.Type = AssetTypeDetector.FromPath(path);
                resolved++;
            }
            else
            {
                e.Path = $"0x{e.PathHash:x16}.unknown";
                e.IsResolved = false;
                e.Type = AssetType.Unknown;
            }
        }
        ResolvedCount = resolved;
        return resolved;
    }

    public bool TryGetEntry(ulong pathHash, out WadAssetEntry entry) => _byHash.TryGetValue(pathHash, out entry!);

    /// <summary>Extract and decompress a chunk to a managed byte array.</summary>
    public byte[] Extract(WadAssetEntry entry) => Extract(entry.PathHash);

    public byte[] Extract(ulong pathHash)
    {
        lock (_readLock)
        {
            var chunk = _wad.Chunks[pathHash];
            try
            {
                using MemoryOwner<byte> owner = _wad.LoadChunkDecompressed(chunk);
                return owner.Span.ToArray();
            }
            catch (Exception) when (chunk.Compression == WadChunkCompression.ZstdChunked)
            {
                // M135: LeagueToolkit can only decode ZstdChunked entries when it found the wad's
                // subchunk TOC — mod-built and overlay wads carry the entries WITHOUT a TOC LT can
                // locate, and LT dies with the (in)famous NullReferenceException (the M44 gap that
                // made fantome imports drop "failed chunks — usually subchunked textures").
                // A TOC is not actually needed to decode: the stored bytes are the subchunks'
                // zstd frames back-to-back, and a streaming decoder walks concatenated frames.
                // Verified byte-identical to LT's TOC-driven output on 400 riot Map12 entries.
                return ExtractSubchunkedWithoutToc(chunk);
            }
        }
    }

    private FileStream? _rawStream;   // fallback reads; guarded by _readLock, disposed with the archive

    private byte[] ExtractSubchunkedWithoutToc(WadChunk chunk)
    {
        _rawStream ??= File.OpenRead(FilePath);
        var stored = new byte[chunk.CompressedSize];
        _rawStream.Position = chunk.DataOffset;
        _rawStream.ReadExactly(stored, 0, stored.Length);

        using var ds = new ZstdSharp.DecompressionStream(new MemoryStream(stored, writable: false));
        var result = new byte[chunk.UncompressedSize];
        int total = 0;
        while (total < result.Length)
        {
            int n = ds.Read(result, total, result.Length - total);
            if (n <= 0) break;
            total += n;
        }
        if (total != result.Length)
            throw new InvalidDataException($"Subchunked chunk 0x{chunk.PathHash:x16}: only {total:n0} of {result.Length:n0} bytes decoded.");
        return result;
    }

    public void ExtractToFile(WadAssetEntry entry, string outPath)
    {
        var dir = System.IO.Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(outPath, Extract(entry));
    }

    public void Dispose()
    {
        _rawStream?.Dispose();
        _wad.Dispose();
    }
}
