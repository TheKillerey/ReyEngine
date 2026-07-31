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

    /// <summary>Set when this archive was opened from a repaired copy rather than the file named by
    /// <see cref="FilePath"/>, with what was repaired. Null for the normal case. Callers that report to a
    /// user should surface it - silently accepting a malformed file teaches nobody anything.</summary>
    public string? RepairNote { get; private set; }

    /// <summary>A de-duplicated temp copy this archive owns and must delete.</summary>
    private string? _tempCopy;

    public static WadArchive Open(string path, IHashResolver? resolver = null)
    {
        WadFile wad;
        string? temp = null, note = null;
        try
        {
            wad = new WadFile(path);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            // M298: LeagueToolkit rejects a WAD whose table of contents lists one path hash twice, and it
            // rejects the WHOLE FILE - so a single redundant descriptor made an entire 382 MB mod
            // unimportable. The format itself tolerates this: a chunk descriptor carries an isDuplicate
            // byte, so duplicates are expressible by design and Riot's own packer emits them.
            //
            // Repairing a copy is only honest when the duplicate is REDUNDANT, so WadDeduplicator refuses
            // when two descriptors for one hash disagree about the data - that would be choosing which of
            // two answers the author meant, which is a guess, not a repair.
            temp = WadDeduplicator.TryRepair(path, out note);
            if (temp is null) throw;
            wad = new WadFile(temp);
        }

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

        var archive = new WadArchive(path, wad, list) { _tempCopy = temp, RepairNote = note };
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
        // M298: the de-duplicated copy is ours and only ours. Deleted after the WadFile above releases its
        // handle, or the delete silently fails and leaves a WAD-sized file in temp on every open.
        if (_tempCopy is not null)
        {
            try { System.IO.File.Delete(_tempCopy); } catch { /* temp cleanup is best-effort */ }
            _tempCopy = null;
        }
    }
}
