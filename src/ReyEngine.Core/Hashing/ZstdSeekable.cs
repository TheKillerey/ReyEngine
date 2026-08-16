using System.Buffers.Binary;

namespace ReyEngine.Core.Hashing;

/// <summary>
/// M494: reader for the zstd <b>Seekable Format</b> — a normal zstd stream split into independent frames,
/// with a trailing index that says where each frame starts and how big it decompresses to.
///
/// <para>Mimir's <c>.hashdb</c> stores its string arena this way, which is the whole reason the format is
/// cheap to query: pulling one path out of a 44 MB arena decompresses the single ~1 MB frame that contains
/// it rather than the lot. Without this the reader would have to inflate the entire arena on open, which
/// is exactly the memory cost the format exists to avoid.</para>
///
/// <para><b>Layout, measured against Mimir's shipped bintypes-2026-08-14.lhdb.</b> The stream is a run of
/// standard zstd frames (magic <c>0xFD2FB528</c>) followed by ONE skippable frame holding the seek table.
/// That frame is <c>[magic u32 0x184D2A5E][frameSize u32][entries...][footer]</c>, each entry
/// <c>[compressedSize u32][decompressedSize u32]</c> plus an optional <c>[checksum u32]</c> when bit 7 of
/// the descriptor is set, and the 9-byte footer is
/// <c>[numFrames u32][descriptor u8][magic u32 0x8F92EAB1]</c>. That file: 6 frames, descriptor 0, so
/// 6*8 + 9 = 57 bytes — exactly the skippable frame's declared size.</para>
///
/// <para>Only reading is implemented. Nothing in ReyEngine writes this format, and a writer that was never
/// exercised against the real thing would be a liability rather than a feature.</para>
/// </summary>
public sealed class ZstdSeekable
{
    /// <summary>Skippable-frame magic range is 0x184D2A50..0x184D2A5F; the seek table uses the last one.</summary>
    private const uint SeekTableMagic = 0x184D2A5E;
    private const uint SeekFooterMagic = 0x8F92EAB1;
    private const uint ZstdFrameMagic = 0xFD2FB528;
    private const int FooterSize = 9;

    /// <summary>One frame's place in both the compressed stream and the decompressed arena.</summary>
    public readonly record struct Frame(long CompressedOffset, int CompressedSize,
                                        long DecompressedOffset, int DecompressedSize);

    private readonly Frame[] _frames;

    public IReadOnlyList<Frame> Frames => _frames;
    public long DecompressedLength { get; }

    private ZstdSeekable(Frame[] frames, long decompressedLength)
    {
        _frames = frames;
        DecompressedLength = decompressedLength;
    }

    /// <summary>Read the seek table off the end of a seekable stream. Returns false for anything that is not
    /// one — including a plain zstd stream, which is a legitimate arena form and not an error.</summary>
    public static bool TryRead(ReadOnlySpan<byte> stream, out ZstdSeekable? table)
    {
        table = null;
        if (stream.Length < FooterSize + 8) return false;

        var footer = stream[^FooterSize..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[5..]) != SeekFooterMagic) return false;

        uint frameCount = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        byte descriptor = footer[4];
        bool hasChecksums = (descriptor & 0x80) != 0;
        int entrySize = hasChecksums ? 12 : 8;

        // Reject a frame count the file cannot possibly hold before allocating for it.
        long tableBytes = (long)frameCount * entrySize + FooterSize;
        if (frameCount == 0 || tableBytes > stream.Length) return false;

        long skippableStart = stream.Length - tableBytes - 8;
        if (skippableStart < 0) return false;
        var skippable = stream[(int)skippableStart..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(skippable) != SeekTableMagic) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(skippable[4..]) != tableBytes) return false;

        var frames = new Frame[frameCount];
        int at = (int)skippableStart + 8;
        long compressed = 0, decompressed = 0;
        for (int i = 0; i < frameCount; i++)
        {
            uint cSize = BinaryPrimitives.ReadUInt32LittleEndian(stream[at..]);
            uint dSize = BinaryPrimitives.ReadUInt32LittleEndian(stream[(at + 4)..]);
            at += entrySize;

            // A frame that runs past the seek table itself would read another frame's bytes as its own.
            if (compressed + cSize > skippableStart) return false;
            frames[i] = new Frame(compressed, (int)cSize, decompressed, (int)dSize);
            compressed += cSize;
            decompressed += dSize;
        }

        table = new ZstdSeekable(frames, decompressed);
        return true;
    }

    /// <summary>Is this a zstd stream at all? Used to tell "raw arena" from "compressed arena" without
    /// relying solely on the header flag.</summary>
    public static bool LooksLikeZstd(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == ZstdFrameMagic;

    /// <summary>The frames covering <paramref name="length"/> decompressed bytes from
    /// <paramref name="offset"/>, as an inclusive index range. Returns false when the range falls outside
    /// the arena.</summary>
    public bool TryFindFrames(long offset, int length, out int first, out int last)
    {
        first = last = -1;
        if (offset < 0 || length < 0 || offset + length > DecompressedLength) return false;
        if (length == 0) { first = last = 0; return true; }

        first = IndexOf(offset);
        last = IndexOf(offset + length - 1);
        return first >= 0 && last >= 0;
    }

    private int IndexOf(long decompressedOffset)
    {
        int lo = 0, hi = _frames.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var f = _frames[mid];
            if (decompressedOffset < f.DecompressedOffset) hi = mid - 1;
            else if (decompressedOffset >= f.DecompressedOffset + f.DecompressedSize) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    // Decompressed frames, kept so clustered lookups do not re-inflate the same frame. Measured on Mimir's
    // game-2026-08-14.lhdb (2.29M entries, 187 MB arena): without this, 5,003 scattered lookups cost 382 ms
    // because every one decompresses a whole ~1 MB frame. Paths in the arena are stored lexicographically,
    // so real lookups cluster by directory far more than a scattered sample does and this hits often.
    private readonly Dictionary<int, byte[]> _cache = new();
    private readonly Queue<int> _cacheOrder = new();
    private long _cacheBytes;

    /// <summary>Cap on retained decompressed frames. Bounded in BYTES rather than frames because frame size
    /// is a writer choice we do not control.</summary>
    public long CacheLimitBytes { get; set; } = 48L * 1024 * 1024;

    private byte[] FrameBytes(ReadOnlySpan<byte> stream, int index)
    {
        if (_cache.TryGetValue(index, out var hit)) return hit;

        var frame = _frames[index];
        var compressed = stream.Slice((int)frame.CompressedOffset, frame.CompressedSize).ToArray();
        var buffer = new byte[frame.DecompressedSize];
        using (var source = new MemoryStream(compressed, writable: false))
        using (var decoder = new ZstdSharp.DecompressionStream(source))
        {
            int got = 0;
            while (got < buffer.Length)
            {
                int n = decoder.Read(buffer, got, buffer.Length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got != buffer.Length)
                throw new InvalidDataException(
                    $"seekable frame {index} decompressed to {got} bytes, but the seek table says {frame.DecompressedSize}");
        }

        // FIFO rather than true LRU: the access pattern is a scan through sorted keys, where the oldest
        // frame really is the least likely to be wanted again, and this costs no per-hit bookkeeping.
        _cache[index] = buffer;
        _cacheOrder.Enqueue(index);
        _cacheBytes += buffer.Length;
        while (_cacheBytes > CacheLimitBytes && _cacheOrder.Count > 1)
        {
            int evict = _cacheOrder.Dequeue();
            if (_cache.Remove(evict, out var gone)) _cacheBytes -= gone.Length;
        }
        return buffer;
    }

    /// <summary>Drop every cached frame.</summary>
    public void ClearCache() { _cache.Clear(); _cacheOrder.Clear(); _cacheBytes = 0; }

    /// <summary>Decompress exactly the frames spanning the requested range and return that slice.</summary>
    public byte[] Read(ReadOnlySpan<byte> stream, long offset, int length)
    {
        if (!TryFindFrames(offset, length, out int first, out int last))
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"range {offset}..{offset + length} lies outside the {DecompressedLength}-byte arena");
        if (length == 0) return Array.Empty<byte>();

        // The common case by far: the whole string sits inside one frame, so it is copied straight out.
        if (first == last)
        {
            var only = FrameBytes(stream, first);
            return only.AsSpan((int)(offset - _frames[first].DecompressedOffset), length).ToArray();
        }

        var result = new byte[length];
        int written = 0;
        for (int i = first; i <= last && written < length; i++)
        {
            var frame = _frames[i];
            var bytes = FrameBytes(stream, i);
            long from = Math.Max(offset, frame.DecompressedOffset);
            long to = Math.Min(offset + length, frame.DecompressedOffset + frame.DecompressedSize);
            int take = checked((int)(to - from));
            bytes.AsSpan((int)(from - frame.DecompressedOffset), take).CopyTo(result.AsSpan(written));
            written += take;
        }
        return result;
    }
}
