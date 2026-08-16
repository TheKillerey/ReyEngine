using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace ReyEngine.Core.Hashing;

/// <summary>
/// M494: reader for Mimir's <c>.hashdb</c> / <c>.lhdb</c> hash-to-path tables.
///
/// <para><b>Why.</b> ReyEngine currently carries 554 MB of hash data — ~300 MB of CommunityDragon text
/// plus a 257 MB merged_hashes.cache — and loads it into dictionaries at startup. Mimir publishes the same
/// tables as memory-mapped binaries totalling ~64 MB: binentries, binfields, binhashes, bintypes, game,
/// lcu, rst, rst-xxh3. Lookups binary-search a sorted key array in the mapped file and decompress only the
/// arena frame holding the answer, so nothing is read until it is asked for.</para>
///
/// <para><b>The format, measured against the shipped bintypes-2026-08-14.lhdb</b> rather than assumed. An
/// 80-byte little-endian header:</para>
/// <list type="table">
///   <item><term>0</term><description>magic <c>"HASHDB\0\0"</c></description></item>
///   <item><term>8</term><description>version u16 (1)</description></item>
///   <item><term>10</term><description>hash kind u8</description></item>
///   <item><term>11</term><description>flags u8 — bit 0 arena compressed, bit 1 case-insensitive</description></item>
///   <item><term>12</term><description>key width u8 (4 or 8)</description></item>
///   <item><term>13</term><description>offset width u8 (4 or 8)</description></item>
///   <item><term>16</term><description>entry count u64</description></item>
///   <item><term>24/32/40</term><description>keys / offsets / arena file offsets, u64</description></item>
///   <item><term>48/56</term><description>arena decompressed and compressed sizes, u64</description></item>
///   <item><term>64</term><description>xxh3-64 checksum u64</description></item>
/// </list>
///
/// <para>Then the sorted keys (<c>entryCount * keyWidth</c>), then the offsets
/// (<c>entryCount * offsetWidth</c>) IMMEDIATELY followed by the lengths, which are <b>always 2 bytes</b>
/// each regardless of offset width. That last detail is the one worth stating: in the sample file the
/// region between offsetsOffset and arenaOffset is 22,266 bytes for 3,711 entries, which is 4+2 per entry
/// and not the 8 a pair of same-width fields would give.</para>
///
/// <para><b>Case-insensitive tables.</b> The flag records how the generator hashed its input; it does NOT
/// mean the reader should fold anything. Callers already hold the hash — this type never hashes a string —
/// so the flag is exposed for diagnostics and layering checks and deliberately changes no behaviour here.</para>
/// </summary>
public sealed class HashDbFile : IDisposable
{
    private static readonly byte[] Magic = "HASHDB\0\0"u8.ToArray();
    public const int HeaderSize = 80;
    public const ushort SupportedVersion = 1;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly unsafe byte* _base;
    private readonly long _length;
    private readonly ZstdSeekable? _seek;

    public string Path { get; }
    public long EntryCount { get; }
    public int KeyWidth { get; }
    public int OffsetWidth { get; }
    public byte HashKind { get; }
    public bool ArenaCompressed { get; }
    public bool CaseInsensitive { get; }
    public ulong Checksum { get; }
    public long ArenaDecompressedSize { get; }

    private readonly long _keysOffset, _offsetsOffset, _lengthsOffset, _arenaOffset, _arenaCompressedSize;

    private unsafe HashDbFile(string path, MemoryMappedFile file, MemoryMappedViewAccessor view, long length)
    {
        Path = path;
        _file = file;
        _view = view;
        _length = length;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);
        // The pointer is acquired before anything is validated, so a REJECTED file would otherwise keep the
        // mapping pinned and the file could not be deleted or replaced for the life of the process. Found by
        // the test that feeds Open() a PROP file: it could not clean up its own temp file afterwards.
        try
        {
        var header = new ReadOnlySpan<byte>(_base, HeaderSize);
        if (!header[..8].SequenceEqual(Magic))
            throw new InvalidDataException($"'{path}' is not a hashdb file (magic mismatch).");

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        if (version != SupportedVersion)
            throw new InvalidDataException($"'{path}' is hashdb format version {version}; this build reads {SupportedVersion}.");

        HashKind = header[10];
        byte flags = header[11];
        ArenaCompressed = (flags & 0x01) != 0;
        CaseInsensitive = (flags & 0x02) != 0;
        KeyWidth = header[12];
        OffsetWidth = header[13];
        if (KeyWidth is not (4 or 8)) throw new InvalidDataException($"'{path}' has key width {KeyWidth}; expected 4 or 8.");
        if (OffsetWidth is not (2 or 4 or 8)) throw new InvalidDataException($"'{path}' has offset width {OffsetWidth}; expected 2, 4 or 8.");

        EntryCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        _keysOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[24..]);
        _offsetsOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
        _arenaOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[40..]);
        ArenaDecompressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[48..]);
        _arenaCompressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[56..]);
        Checksum = BinaryPrimitives.ReadUInt64LittleEndian(header[64..]);

        // Lengths sit directly after the offsets and are always 2 bytes wide.
        _lengthsOffset = _offsetsOffset + EntryCount * OffsetWidth;

        long need = Math.Max(_lengthsOffset + EntryCount * 2, _arenaOffset + _arenaCompressedSize);
        if (EntryCount < 0 || need > length)
            throw new InvalidDataException($"'{path}' declares {EntryCount:n0} entries but is only {length:n0} bytes.");

        if (ArenaCompressed)
        {
            var arena = new ReadOnlySpan<byte>(_base + _arenaOffset, checked((int)_arenaCompressedSize));
            if (ZstdSeekable.TryRead(arena, out var table)) _seek = table;
            else if (!ZstdSeekable.LooksLikeZstd(arena))
                throw new InvalidDataException($"'{path}' claims a compressed arena that is not zstd.");
            // A plain (non-seekable) zstd arena stays valid: _seek is null and Read inflates it whole.
        }
        }
        catch
        {
            try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { /* not acquired */ }
            throw;
        }
    }

    /// <summary>Map a table. The file stays mapped until disposed; nothing is copied up front.</summary>
    public static HashDbFile Open(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("hashdb not found", path);
        if (info.Length < HeaderSize) throw new InvalidDataException($"'{path}' is too small to be a hashdb.");

        var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, 0, MemoryMappedFileAccess.Read);
        MemoryMappedViewAccessor? view = null;
        try
        {
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return new HashDbFile(path, file, view, info.Length);
        }
        catch
        {
            view?.Dispose();
            file.Dispose();
            throw;
        }
    }

    /// <summary>The key at an index, widened to u64 so both table widths share one search.</summary>
    private unsafe ulong KeyAt(long index)
    {
        byte* at = _base + _keysOffset + index * KeyWidth;
        return KeyWidth == 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(at, 4))
            : BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(at, 8));
    }

    private unsafe long OffsetAt(long index)
    {
        byte* at = _base + _offsetsOffset + index * OffsetWidth;
        return OffsetWidth switch
        {
            2 => BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(at, 2)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(at, 4)),
            _ => (long)BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(at, 8)),
        };
    }

    private unsafe int LengthAt(long index) =>
        BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(_base + _lengthsOffset + index * 2, 2));

    /// <summary>Index of <paramref name="key"/>, or -1. Binary search over the sorted key array — the
    /// property the format is built around.</summary>
    public long IndexOf(ulong key)
    {
        long lo = 0, hi = EntryCount - 1;
        while (lo <= hi)
        {
            long mid = lo + (hi - lo) / 2;
            ulong probe = KeyAt(mid);
            if (probe == key) return mid;
            if (probe < key) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>Look up a path by hash.</summary>
    public bool TryGet(ulong key, out string value)
    {
        long index = IndexOf(key);
        if (index < 0) { value = ""; return false; }
        value = StringAt(index);
        return true;
    }

    /// <summary>The key at an index, for enumeration.</summary>
    public ulong KeyOf(long index) =>
        index >= 0 && index < EntryCount ? KeyAt(index)
        : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>The string at an index, pulled out of the arena.</summary>
    public unsafe string StringAt(long index)
    {
        if (index < 0 || index >= EntryCount) throw new ArgumentOutOfRangeException(nameof(index));
        long offset = OffsetAt(index);
        int length = LengthAt(index);
        if (length == 0) return "";
        if (offset < 0 || offset + length > ArenaDecompressedSize)
            throw new InvalidDataException($"entry {index} points outside the arena ({offset}+{length} of {ArenaDecompressedSize}).");

        if (!ArenaCompressed)
            return Encoding.UTF8.GetString(_base + _arenaOffset + offset, length);

        var arena = new ReadOnlySpan<byte>(_base + _arenaOffset, checked((int)_arenaCompressedSize));
        if (_seek is not null) return Encoding.UTF8.GetString(_seek.Read(arena, offset, length));

        // Non-seekable zstd arena: inflate once and keep it, since there is no way to seek into it.
        // M508: under the lock. Resolution runs on several threads at once, and the unguarded '??=' let
        // one thread read a half-published buffer.
        byte[] whole;
        lock (_arenaLock) whole = _wholeArena ??= InflateWholeArena(arena);
        return Encoding.UTF8.GetString(whole, (int)offset, length);
    }

    private byte[]? _wholeArena;
    private readonly object _arenaLock = new();

    /// <summary>M508: shrink the seekable frame cache, so a test can force the eviction path — the branch
    /// that moves the dictionary, the queue and the byte counter together, and the one a second thread
    /// corrupted. No effect on a table whose arena is not seekable.</summary>
    public void SetFrameCacheLimit(long bytes)
    {
        if (_seek is not null) _seek.CacheLimitBytes = bytes;
    }

    private byte[] InflateWholeArena(ReadOnlySpan<byte> arena)
    {
        using var source = new MemoryStream(arena.ToArray(), writable: false);
        using var decoder = new ZstdSharp.DecompressionStream(source);
        var buffer = new byte[ArenaDecompressedSize];
        int got = 0;
        while (got < buffer.Length)
        {
            int n = decoder.Read(buffer, got, buffer.Length - got);
            if (n <= 0) break;
            got += n;
        }
        if (got != buffer.Length)
            throw new InvalidDataException($"arena decompressed to {got} bytes, but the header says {ArenaDecompressedSize}.");
        return buffer;
    }

    /// <summary>Every (key, value) pair, in key order. Streams — it never materialises the whole table.</summary>
    public IEnumerable<(ulong Key, string Value)> Entries()
    {
        for (long i = 0; i < EntryCount; i++) yield return (KeyAt(i), StringAt(i));
    }

    /// <summary>Verify the sorted-key invariant the binary search depends on. Not run on open: it is O(n)
    /// over the key array, and a table that fails it is a generator bug rather than something to guard
    /// every startup against.</summary>
    public bool KeysAreSorted()
    {
        for (long i = 1; i < EntryCount; i++)
            if (KeyAt(i - 1) >= KeyAt(i)) return false;
        return true;
    }

    public void Dispose()
    {
        lock (_arenaLock) _wholeArena = null;
        try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { /* already released */ }
        _view.Dispose();
        _file.Dispose();
    }
}
