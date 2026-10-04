using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>An archive or one of its chunks that cannot be read. <see cref="Exception.Message"/> is the statement <c>ltk_wad</c>'s <c>WadError</c> gives for the same bytes
/// where there is one (<c>invalid version 3.9</c>), and the platform's where there is not.</summary>
internal sealed class GameWadException : Exception
{
    public GameWadException(string message) : base(message) { }

    public GameWadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>How an archive stores a chunk (<c>WadChunkCompression</c>).</summary>
internal enum GameWadCompression : byte
{
    None = 0,
    GZip = 1,
    /// <summary>A chunk whose bytes live in another archive: it holds none.</summary>
    Satellite = 2,
    Zstd = 3,
    /// <summary>Zstd frames laid end to end, some of them raw, described by the archive's subchunk table.</summary>
    ZstdMulti = 4,
}

/// <summary>One row of an archive's table of contents (<c>WadChunk</c>). The sizes are the file's unsigned words: a version 3.1 table writes them as <c>i32</c>
/// and the crate reads a negative one as an enormous size, which no read can satisfy, as it cannot here.</summary>
internal readonly record struct GameWadChunk(
    ulong PathHash,
    uint DataOffset,
    uint CompressedSize,
    uint UncompressedSize,
    GameWadCompression Compression,
    byte FrameCount,
    uint StartFrame,
    ulong Checksum,
    int TocIndex);

/// <summary>
/// M818: a game archive's table of contents, read the way <c>ltk_wad::Wad::mount</c> reads it, and the chunk reads <c>ltk_game_index</c> and the overlay make over
/// it: a chunk's raw bytes, its first bytes decoded (the sniff), and the whole chunk.
///
/// <para><b>What mounting accepts.</b> The magic <c>RW</c>; a major version of at most 3. A version 3 table (minor 0 to 3, or 4 and later) holds one 32-byte
/// descriptor per chunk; a version 1 or 2 table is read to its chunk count and fails at its first descriptor, as it does in the crate, so an empty one mounts. A
/// count the file cannot hold, a compression the format does not name and a short table are refusals. The rows are held sorted by path hash, rows of
/// one hash in table order, and a lookup answers the LAST of them (the crate keeps the last in its index); iterating gives every row.</para>
///
/// <para><b>What it does not do.</b> The subchunk table (<c>SubchunkToc</c>) is not detected: a <see cref="GameWadCompression.ZstdMulti"/> chunk is decoded by scanning for its first
/// Zstd frame, which is the crate's own fallback when it finds no table. Textures are the only chunks the installed game stores that way, and the index
/// never reads them past their first bytes.</para>
/// </summary>
internal sealed class GameWad
{
    public const int DescriptorSize = 32;

    /// <summary>The most a whole chunk may decode to: a limit on one allocation, far above any bin the game ships (Map22's is 33 MB).</summary>
    public const long MaxChunkBytes = 1L << 30;

    private readonly GameWadChunk[] _chunks;

    private GameWad(string path, long length, GameWadChunk[] chunks)
    {
        Path = path;
        Length = length;
        _chunks = chunks;
    }

    public string Path { get; }

    /// <summary>The file's length when it was mounted.</summary>
    public long Length { get; }

    /// <summary>Every row, in path hash order, rows of one hash in table order.</summary>
    public ReadOnlySpan<GameWadChunk> Chunks => _chunks;

    public int Count => _chunks.Length;

    /// <summary>The row of <paramref name="pathHash"/>; the last one the table lists when it lists the hash twice.</summary>
    public bool TryGet(ulong pathHash, out GameWadChunk chunk)
    {
        int low = 0, high = _chunks.Length;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (_chunks[mid].PathHash <= pathHash) low = mid + 1;
            else high = mid;
        }
        if (low > 0 && _chunks[low - 1].PathHash == pathHash)
        {
            chunk = _chunks[low - 1];
            return true;
        }
        chunk = default;
        return false;
    }

    /// <summary>Opens <paramref name="path"/> and reads its table of contents.</summary>
    /// <exception cref="IOException">The file does not open.</exception>
    /// <exception cref="UnauthorizedAccessException">The file does not open.</exception>
    /// <exception cref="GameWadException">The file is not an archive the crate mounts.</exception>
    public static GameWad Mount(string path)
    {
        using var handle = OpenHandle(path);
        return Mount(path, handle);
    }

    internal static SafeFileHandle OpenHandle(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);

    internal static GameWad Mount(string path, SafeFileHandle handle)
    {
        long length = RandomAccess.GetLength(handle);
        Span<byte> head = stackalloc byte[4];
        int have = ReadUpTo(handle, head, 0);
        if (have < 2) throw ShortRead();

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(head);
        if (magic != 0x5752) throw new GameWadException("invalid header");   // "RW"
        if (have < 4) throw ShortRead();
        byte major = head[2], minor = head[3];
        if (major > 3) throw new GameWadException($"invalid version {major}.{minor}");

        // the signature and the checksum of the data: 84 bytes of ECDSA in a version 2 file, 256 of PKCS#1 in a version 3
        int at = 4;
        if (major == 2) at += 84 + 8;
        else if (major == 3) at += 256 + 8;
        if (major is 1 or 2) at += 4;   // the offset and the size of the table, which the crate skips

        Span<byte> countBytes = stackalloc byte[4];
        if (ReadUpTo(handle, countBytes, at) < 4) throw ShortRead();
        int count = BinaryPrimitives.ReadInt32LittleEndian(countBytes);
        at += 4;
        if (count < 0) throw new GameWadException("invalid chunk count " + count);
        // a table that is not version 3 fails at its first descriptor, before a byte of it is read
        if (count > 0 && major != 3) throw new GameWadException($"invalid version {major}.{minor}");
        if ((long)count * DescriptorSize > length - at) throw ShortRead();
        if ((long)count * DescriptorSize > Array.MaxLength) throw new GameWadException("the table of contents is too large to hold");

        var table = new byte[count * DescriptorSize];
        if (ReadUpTo(handle, table, at) < table.Length) throw ShortRead();

        var chunks = new GameWadChunk[count];
        for (int i = 0; i < count; i++)
        {
            var row = table.AsSpan(i * DescriptorSize, DescriptorSize);
            byte typeAndFrames = row[20];
            byte type = (byte)(typeAndFrames & 0xF);
            if (type > 4) throw new GameWadException($"invalid chunk compression: {type}");
            // 3.0 to 3.3 spell the start frame as a u16 after a duplicated flag; 3.4 and later as 24 bits, the high byte first
            uint startFrame = minor <= 3
                ? BinaryPrimitives.ReadUInt16LittleEndian(row[22..])
                : (uint)(row[21] << 16 | row[23] << 8 | row[22]);
            chunks[i] = new GameWadChunk(
                BinaryPrimitives.ReadUInt64LittleEndian(row),
                BinaryPrimitives.ReadUInt32LittleEndian(row[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(row[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(row[16..]),
                (GameWadCompression)type,
                (byte)(typeAndFrames >> 4),
                startFrame,
                BinaryPrimitives.ReadUInt64LittleEndian(row[24..]),
                i);
        }
        // stable by table order: a sort that is not would let the last of two rows of one hash be either
        Array.Sort(chunks, static (a, b) => a.PathHash != b.PathHash ? a.PathHash.CompareTo(b.PathHash) : a.TocIndex.CompareTo(b.TocIndex));
        return new GameWad(path, length, chunks);
    }

    private static GameWadException ShortRead() => new("io error: failed to fill whole buffer");

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes at <paramref name="offset"/>; fewer only where the file ends.</summary>
    internal static int ReadUpTo(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = RandomAccess.Read(handle, buffer[total..], offset + total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    /// <summary>Opens the archive's file for reads, which a caller makes one after another and closes.</summary>
    public GameWadReader OpenReader() => new(this, OpenHandle(Path));
}

/// <summary>
/// An open archive: the chunk reads of <see cref="GameWad"/> over one handle, with the decoder kept between them (<c>ChunkDecoder</c>: a Zstd context is a few
/// hundred kilobytes, and building one costs about as much as decoding a small chunk). One reader serves one thread at a time.
/// </summary>
internal sealed class GameWadReader : IDisposable
{
    /// <summary>Raw bytes a sniff reads from a chunk first: the first block of nearly every chunk fits (<c>HEAD_FIRST_RAW</c>).</summary>
    private const int HeadFirstRaw = 16 * 1024;

    /// <summary>The most raw bytes a sniff reads from one chunk. A Zstd block decodes to at most 128 KiB and an incompressible block is no larger, so the
    /// first block and its headers always fit (<c>HEAD_MAX_RAW</c>).</summary>
    private const int HeadMaxRaw = 256 * 1024;

    private static readonly byte[] ZstdMagic = { 0x28, 0xB5, 0x2F, 0xFD };

    private readonly SafeFileHandle _handle;
    private Decompressor? _zstd;

    // the raw bytes of a sniff are read into one buffer the reader keeps: a build sniffs every chunk of an installation, and a buffer for each is gigabytes of garbage
    private byte[] _scratch = Array.Empty<byte>();

    internal GameWadReader(GameWad wad, SafeFileHandle handle)
    {
        Wad = wad;
        _handle = handle;
    }

    public GameWad Wad { get; }

    public void Dispose()
    {
        _zstd?.Dispose();
        _handle.Dispose();
    }

    /// <summary>The first <paramref name="maxLength"/> raw (still compressed) bytes of a chunk (<c>load_chunk_raw_prefix</c>).</summary>
    /// <exception cref="GameWadException">The file ends inside the chunk.</exception>
    public byte[] ReadRawPrefix(in GameWadChunk chunk, int maxLength)
    {
        var data = new byte[(int)Math.Min((long)chunk.CompressedSize, maxLength)];
        if (GameWad.ReadUpTo(_handle, data, chunk.DataOffset) < data.Length) throw new GameWadException("io error: failed to fill whole buffer");
        return data;
    }

    /// <summary>At most the first <paramref name="want"/> decoded bytes of a chunk, decoding no further (<c>chunk_head</c>): the sniff. The raw bytes are read in two steps, the
    /// first block's worth and then, where that cut the first block short, enough for any first block, which is the crate's own escalation.</summary>
    /// <exception cref="GameWadException">The chunk's bytes do not decode as far as <paramref name="want"/> asks, or its file ends inside it.</exception>
    public byte[] ReadHead(in GameWadChunk chunk, int want)
    {
        want = (int)Math.Min((long)want, chunk.UncompressedSize);
        // stored bytes are the prefix itself: reading more of them than are asked for decodes nothing the sniff needs
        if (chunk.Compression == GameWadCompression.None)
        {
            var stored = ReadRawPrefix(chunk, want);
            return stored;
        }
        int ceiling = Math.Max(HeadMaxRaw, want);
        int rawLimit = Math.Max(HeadFirstRaw, want);
        while (true)
        {
            int length = (int)Math.Min((long)chunk.CompressedSize, rawLimit);
            if (_scratch.Length < length) _scratch = new byte[Math.Max(length, 2 * _scratch.Length)];
            var raw = _scratch.AsSpan(0, length);
            if (GameWad.ReadUpTo(_handle, raw, chunk.DataOffset) < raw.Length) throw new GameWadException("io error: failed to fill whole buffer");
            bool cutShort = raw.Length == rawLimit && rawLimit < ceiling;
            try
            {
                var head = DecodePrefix(raw, chunk.Compression, want);
                if (head.Length >= want || !cutShort) return head;
            }
            catch (GameWadException) when (cutShort) { }
            rawLimit = ceiling;
        }
    }

    /// <summary>The whole chunk, decoded to its stated size (<c>load_chunk_decompressed</c>). What the table of contents says of the chunk is checked before a byte is allocated: a
    /// descriptor that claims more than <paramref name="maxBytes"/> stored or decoded, or bytes past the end of the file, is a refusal, so a corrupt table cannot make the reader allocate a gigabyte.</summary>
    /// <param name="chunk">The chunk's row.</param>
    /// <param name="maxBytes">The most the chunk may be stored in and may decode to; never more than <see cref="GameWad.MaxChunkBytes"/>.</param>
    /// <param name="cancellationToken">Checked between the read and the decode.</param>
    /// <exception cref="GameWadException">The chunk is larger than the limit, lies outside the file, its file ends inside it, or its bytes do not decode to its size.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public byte[] ReadChunk(in GameWadChunk chunk, long maxBytes = GameWad.MaxChunkBytes, CancellationToken cancellationToken = default)
    {
        long limit = Math.Min(maxBytes, GameWad.MaxChunkBytes);
        if (chunk.UncompressedSize > limit)
            throw new GameWadException($"chunk decodes to {Digits(chunk.UncompressedSize)} bytes, more than the {Digits(limit)} the read allows");
        if (chunk.CompressedSize > limit)
            throw new GameWadException($"chunk is stored in {Digits(chunk.CompressedSize)} bytes, more than the {Digits(limit)} the read allows");
        long end = (long)chunk.DataOffset + chunk.CompressedSize;
        if (end > Wad.Length)
            throw new GameWadException($"chunk lies outside its archive: it ends at byte {Digits(end)} of a file of {Digits(Wad.Length)}");

        cancellationToken.ThrowIfCancellationRequested();
        var raw = ReadRawPrefix(chunk, int.MaxValue);
        cancellationToken.ThrowIfCancellationRequested();
        return Decode(raw, chunk);
    }

    private static string Digits(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    // ================================================================================================ decoding

    private byte[] Decode(byte[] raw, in GameWadChunk chunk)
    {
        switch (chunk.Compression)
        {
            case GameWadCompression.None:
                return raw;
            case GameWadCompression.GZip:
            {
                var data = new byte[chunk.UncompressedSize];
                using var gzip = new GZipStream(new MemoryStream(raw, writable: false), CompressionMode.Decompress);
                if (!TryReadFully(gzip, data)) throw new GameWadException("io error: failed to fill whole buffer");
                return data;
            }
            case GameWadCompression.Satellite:
                throw new GameWadException("error: `satellite chunks are not supported`");
            case GameWadCompression.Zstd:
                return DecodeZstd(raw, 0, (int)chunk.UncompressedSize);
            default:
            {
                int frameAt = raw.AsSpan().IndexOf(ZstdMagic);
                if (frameAt < 0) throw new GameWadException("failed to decompress chunk: failed to find zstd magic");
                return DecodeZstd(raw, frameAt, (int)chunk.UncompressedSize);
            }
        }
    }

    /// <summary>The bytes before <paramref name="frameAt"/> are stored raw, and the frames from it on decode into the rest.</summary>
    private byte[] DecodeZstd(byte[] raw, int frameAt, int size)
    {
        var data = new byte[size];
        int copied = Math.Min(frameAt, size);
        raw.AsSpan(0, copied).CopyTo(data);
        int written = StreamInto(raw.AsSpan(frameAt), data.AsSpan(copied));
        if (copied + written != size)
            throw new GameWadException($"failed to decompress chunk: decompressed {copied + written} bytes, expected {size}");
        return data;
    }

    /// <summary>The first <paramref name="want"/> bytes a chunk decodes to from <paramref name="raw"/>, which may be a prefix of its bytes (<c>decompress_prefix</c>). A Zstd
    /// prefix that cuts the first block short comes back shorter, which is how the caller knows to read more.</summary>
    private byte[] DecodePrefix(ReadOnlySpan<byte> raw, GameWadCompression compression, int want)
    {
        switch (compression)
        {
            case GameWadCompression.None:
                return raw[..Math.Min(raw.Length, want)].ToArray();
            case GameWadCompression.GZip:
            {
                var buffer = new byte[want];
                using var gzip = new GZipStream(new MemoryStream(raw.ToArray(), writable: false), CompressionMode.Decompress);
                int filled = 0;
                try
                {
                    while (filled < want)
                    {
                        int read = gzip.Read(buffer, filled, want - filled);
                        if (read == 0) break;
                        filled += read;
                    }
                }
                catch (InvalidDataException e) { throw new GameWadException("io error: " + e.Message, e); }
                return buffer.AsSpan(0, filled).ToArray();
            }
            case GameWadCompression.Satellite:
                throw new GameWadException("error: `satellite chunks are not supported`");
            case GameWadCompression.Zstd:
                return DecodeZstdPrefix(raw, 0, want);
            default:
            {
                int frameAt = raw.IndexOf(ZstdMagic);
                return DecodeZstdPrefix(raw, frameAt < 0 ? raw.Length : frameAt, want);
            }
        }
    }

    private byte[] DecodeZstdPrefix(ReadOnlySpan<byte> raw, int frameAt, int want)
    {
        var data = new byte[want];
        int copied = Math.Min(frameAt, want);
        raw[..copied].CopyTo(data);
        int written = frameAt < raw.Length ? StreamInto(raw[frameAt..], data.AsSpan(copied)) : 0;
        return written + copied == want ? data : data.AsSpan(0, copied + written).ToArray();
    }

    /// <summary>Decodes frames from <paramref name="input"/> into <paramref name="output"/> until the output is full or the input is spent (<c>stream_into</c>), and
    /// answers the bytes written. Input that ends inside a block is not an error: a prefix read does that on purpose.</summary>
    private int StreamInto(ReadOnlySpan<byte> input, Span<byte> output)
    {
        _zstd ??= new Decompressor();
        _zstd.ResetStream();
        int read = 0, written = 0;
        while (written < output.Length && read < input.Length)
        {
            OperationStatus status;
            int consumed, produced;
            try { status = _zstd.UnwrapStream(input[read..], output[written..], out consumed, out produced); }
            catch (ZstdException e) { throw new GameWadException("failed to decompress chunk: " + e.Message, e); }
            read += consumed;
            written += produced;
            if (status == OperationStatus.InvalidData) throw new GameWadException("failed to decompress chunk: corrupted block detected");
            if (consumed == 0 && produced == 0) break;
        }
        return written;
    }

    private static bool TryReadFully(Stream stream, byte[] buffer)
    {
        int filled = 0;
        try
        {
            while (filled < buffer.Length)
            {
                int read = stream.Read(buffer, filled, buffer.Length - filled);
                if (read == 0) return false;
                filled += read;
            }
        }
        catch (InvalidDataException) { return false; }
        return true;
    }
}
