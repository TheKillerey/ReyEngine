using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// A bin the codec refuses. <see cref="Exception.Message"/> is the text <c>ltk_meta</c>'s error prints for the same bytes. This is the one
/// exception the readers of this class (<see cref="PropCodec.ReadProp"/>, <see cref="PropCodec.Mount"/>, <see cref="PropMount"/>, <see cref="PropCodec.ReadObject"/>,
/// <see cref="PropCodec.ReadPtch"/>) throw for bytes they do not accept, whatever is wrong with them: a short file, a count the bytes cannot hold, a kind that
/// does not exist, invalid UTF-8, a nesting past <see cref="PropCodec"/>'s depth, more entries of a kind that takes no bytes than a decode may declare. A caller
/// that reads untrusted bytes catches this and nothing else (a null argument is the caller's own mistake and stays an <see cref="ArgumentNullException"/>).
/// </summary>
public sealed class PropDecodeException : Exception
{
    public PropDecodeException(string message, bool invalidKind = false) : base(message) { InvalidKind = invalidKind; }

    /// <summary>Whether a kind byte did not decode (<c>InvalidPropertyTypePrimitive</c>), which is what makes a reader try the legacy numbering.</summary>
    public bool InvalidKind { get; }
}

/// <summary>
/// M817: the engine's own PROP and PTCH codec, mirroring <c>ltk_meta</c> (<c>stream/layout.rs</c>, <c>stream/owned.rs</c>,
/// <c>tree/write.rs</c>, <c>data_override/read.rs</c> and <c>write.rs</c>) so that what is written back is under this crate's control
/// and equals what <c>ltk_meta</c> writes for the same tree.
///
/// <para><b>What reading normalises, and writing therefore changes.</b> Version 1, 2 and 3 are read and version 3 is written. The first
/// of two objects with one path hash keeps its slot and takes the later object's class and value; two properties of one name are
/// one property the same way. A boolean byte of 2 or more is written as 1. A bin numbered before <c>WadChunkLink</c> existed is read
/// with the legacy kind numbering and written in the current one. Every size is recomputed, and bytes after the last object are
/// dropped. A map keeps its entries in order, duplicates included. Invalid UTF-8, a container inside a container, and a map keyed by
/// a kind that cannot key one are errors.</para>
///
/// <para><b>What it refuses that <c>ltk_meta</c> reads.</b> The crate takes a tree of any depth and a container of any number of entries of a kind
/// that takes no bytes; this codec is for untrusted bytes, so it reads a value nested at most 256 containers deep, and a decode may declare 65,536
/// entries of a kind that takes no bytes in all (Riot writes none: the installed game's 40,643 bins hold 289 million values, nest at most 34
/// containers deep, and hold no such entry). It never reserves more than 1 MiB ahead of the bytes that fill it, and it writes only what it
/// could read back: a string, a path or a property count that a 16-bit field cannot hold is a <see cref="GameDataException"/> of kind
/// <see cref="GameDataErrorKind.Bin"/>, where <c>ltk_meta</c> truncates the field and writes a bin nothing reads.</para>
/// </summary>
public static class PropCodec
{
    private const uint PropMagic = 0x504F5250;   // "PROP"
    private const uint PtchMagic = 0x48435450;   // "PTCH"

    /// <summary>What one decode keeps beside the bytes: the meter of the application it belongs to, when there is one, and the budget of entries that take no bytes.</summary>
    internal sealed class DecodeState
    {
        public DecodeState(WorkMeter? meter) { Meter = meter; }

        public WorkMeter? Meter { get; }

        /// <summary>How many entries of a kind that takes no bytes the decode may still declare.</summary>
        public int ZeroWidthLeft = PropLimits.ZeroWidthBudget;
    }

    // ================================================================================================ reading PROP

    /// <summary><c>Bin::from_reader</c> through the stream: the whole file as a tree.</summary>
    /// <exception cref="PropDecodeException">The bytes are not a PROP the codec reads.</exception>
    public static PropBin ReadProp(byte[] bytes) => Mount(bytes).Read();

    /// <summary><c>BinStream::mount</c>: the header of a PROP (version, dependencies and class table), read before anything of the objects is.
    /// A caller that refuses a version does so here, without decoding the objects.</summary>
    /// <exception cref="PropDecodeException">The bytes are not a PROP the codec reads.</exception>
    public static PropMount Mount(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var header = new StreamReader(bytes);
        uint magic = header.U32();
        if (magic == PtchMagic) throw new PropDecodeException("Expected a PROP bin, found a PTCH bin");
        if (magic != PropMagic) throw new PropDecodeException("Invalid file signature");

        uint version = header.U32();
        if (version is < 1 or > 3) throw new PropDecodeException($"Invalid file version '{version}'");

        var dependencies = new List<string>();
        if (version >= 2)
        {
            uint count = header.U32();
            for (uint i = 0; i < count; i++) dependencies.Add(header.SizedString());
        }
        uint objects = header.U32();
        // every class hash takes four bytes, so a count the file cannot hold is a short read
        if (objects > (uint)(bytes.Length - header.Position) / 4) throw ShortRead();
        var classes = new uint[objects];
        for (uint i = 0; i < objects; i++) classes[i] = header.U32();
        return new PropMount(bytes, version, dependencies, classes, header.Position);
    }

    internal static int HeaderLength(uint version, List<string> dependencies)
    {
        long length = version >= 2 ? 12 : 8;
        foreach (string dependency in dependencies) length += 2 + Encoding.UTF8.GetByteCount(dependency);
        return checked((int)length);
    }

    internal static (uint Path, long Offset, uint Size) HarvestRow(byte[] bytes, long offset)
    {
        if (offset < 0 || offset + 8 > bytes.Length) throw ShortRead();
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset));
        uint path = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset + 4));
        return (path, offset, size);
    }

    /// <summary><c>BinStream::read_object</c>: the object of a row, retried under the legacy numbering when a kind byte only makes sense
    /// there. The retry's success latches <paramref name="legacy"/>; its failure leaves the first complaint.</summary>
    internal static PropObject ReadRow(byte[] bytes, (uint Path, long Offset, uint Size) row, uint classHash, ref bool legacy, DecodeState state)
    {
        long end = row.Offset + 4 + row.Size;
        if (end > bytes.Length) throw new PropDecodeException("IO Error - unexpected end of file");
        int start = (int)row.Offset, stop = (int)end;
        int budget = state.ZeroWidthLeft;
        try
        {
            return DecodeObject(bytes, start, stop, classHash, legacy, state);
        }
        catch (PropDecodeException first) when (first.InvalidKind && !legacy)
        {
            // the first reading is thrown away, and so is what it spent
            state.ZeroWidthLeft = budget;
            try
            {
                var obj = DecodeObject(bytes, start, stop, classHash, true, state);
                legacy = true;
                return obj;
            }
            catch (PropDecodeException)
            {
                throw first;
            }
        }
    }

    /// <summary><c>read_object</c>: one whole object, its size field included, from <paramref name="start"/> to <paramref name="stop"/>.</summary>
    private static PropObject DecodeObject(byte[] bytes, int start, int stop, uint classHash, bool legacy, DecodeState state)
    {
        var cursor = new Cursor(bytes, start, stop, legacy, state);
        uint declared = cursor.U32();
        int bodyStart = cursor.Position;
        var obj = ReadObjectBody(cursor, classHash);
        long consumed = cursor.Position - bodyStart;
        if (consumed != declared) throw InvalidSize(declared, consumed);
        return obj;
    }

    private static PropObject ReadObjectBody(Cursor cursor, uint classHash)
    {
        uint path = cursor.U32();
        ushort count = cursor.U16();
        return new PropObject(path, classHash, ReadProperties(cursor, count, 0));
    }

    private static OrderedMap<PropValue> ReadProperties(Cursor cursor, ushort count, int depth)
    {
        // a property takes five bytes at the least, its name and its kind, so a count the bytes left cannot hold is not reserved for
        var properties = new OrderedMap<PropValue>(Math.Min(count, cursor.Remaining / 5));
        var meter = cursor.State.Meter;
        for (int i = 0; i < count; i++)
        {
            uint name = cursor.U32();
            var kind = cursor.Kind();
            meter?.Charge(1);
            properties.Set(name, ReadValue(cursor, kind, depth));
        }
        return properties;
    }

    private static PropValue ReadValue(Cursor cursor, PropKind kind, int depth)
    {
        switch (kind)
        {
            case PropKind.None: return PropNone.Instance;
            case PropKind.Bool:
            case PropKind.BitBool: return new PropBool(kind, cursor.U8() != 0);
            case PropKind.I8: return new PropInt(kind, unchecked((ulong)(long)(sbyte)cursor.U8()));
            case PropKind.U8: return new PropInt(kind, cursor.U8());
            case PropKind.I16: return new PropInt(kind, unchecked((ulong)(long)(short)cursor.U16()));
            case PropKind.U16: return new PropInt(kind, cursor.U16());
            case PropKind.I32: return new PropInt(kind, unchecked((ulong)(long)(int)cursor.U32()));
            case PropKind.U32: return new PropInt(kind, cursor.U32());
            case PropKind.I64:
            case PropKind.U64: return new PropInt(kind, cursor.U64());
            case PropKind.F32: return new PropF32(cursor.F32());
            case PropKind.Vector2: return new PropVector(kind, cursor.Floats(2));
            case PropKind.Vector3: return new PropVector(kind, cursor.Floats(3));
            case PropKind.Vector4: return new PropVector(kind, cursor.Floats(4));
            case PropKind.Matrix44: return new PropVector(kind, cursor.Floats(16));
            case PropKind.Color: return new PropColor(cursor.U32());
            case PropKind.String: return new PropString(cursor.String(utf8Error: "UTF-8 Error - "));
            case PropKind.Hash:
            case PropKind.ObjectLink: return new PropHash(kind, cursor.U32());
            case PropKind.WadChunkLink: return new PropFile(cursor.U64());
            case PropKind.Container:
            case PropKind.UnorderedContainer: return ReadContainer(cursor, kind, depth);
            case PropKind.Optional: return ReadOptional(cursor, depth);
            case PropKind.Map: return ReadMap(cursor, depth);
            case PropKind.Struct:
            case PropKind.Embedded: return ReadStruct(cursor, kind, depth);
            default: throw new PropDecodeException("unreachable kind");
        }
    }

    private static PropValue ReadContainer(Cursor cursor, PropKind kind, int depth)
    {
        if (depth >= PropLimits.MaxDepth) throw TooDeep();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var itemKind = cursor.ItemKind();
        uint declared = cursor.U32();
        int start = cursor.Position;
        uint count = cursor.U32();
        var items = new List<PropValue>(cursor.Capacity(count, MinWidth(itemKind), 8));
        var meter = cursor.State.Meter;
        for (uint i = 0; i < count; i++)
        {
            meter?.Charge(1);
            items.Add(ReadValue(cursor, itemKind, depth + 1));
        }
        long consumed = cursor.Position - start;
        if (consumed != declared) throw InvalidSize(declared, consumed);
        return new PropList(kind, itemKind, items);
    }

    private static PropValue ReadOptional(Cursor cursor, int depth)
    {
        if (depth >= PropLimits.MaxDepth) throw TooDeep();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var itemKind = cursor.ItemKind();
        bool present = cursor.U8() != 0;
        return new PropOption(itemKind, present ? ReadValue(cursor, itemKind, depth + 1) : null);
    }

    private static PropValue ReadMap(Cursor cursor, int depth)
    {
        if (depth >= PropLimits.MaxDepth) throw TooDeep();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var keyKind = cursor.KeyKind();
        var valueKind = cursor.ItemKind();
        uint declared = cursor.U32();
        int start = cursor.Position;
        uint count = cursor.U32();
        var entries = new List<KeyValuePair<PropValue, PropValue>>(cursor.Capacity(count, MinWidth(keyKind) + MinWidth(valueKind), 16));
        var meter = cursor.State.Meter;
        for (uint i = 0; i < count; i++)
        {
            meter?.Charge(1);
            var key = ReadValue(cursor, keyKind, depth + 1);
            var value = ReadValue(cursor, valueKind, depth + 1);
            entries.Add(new(key, value));
        }
        long consumed = cursor.Position - start;
        if (consumed != declared) throw InvalidSize(declared, consumed);
        return new PropMapValue(keyKind, valueKind, entries);
    }

    private static PropValue ReadStruct(Cursor cursor, PropKind kind, int depth)
    {
        if (depth >= PropLimits.MaxDepth) throw TooDeep();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        uint classHash = cursor.U32();
        if (classHash == 0) return new PropStruct(kind, 0, new OrderedMap<PropValue>());
        uint declared = cursor.U32();
        int start = cursor.Position;
        ushort count = cursor.U16();
        var properties = ReadProperties(cursor, count, depth + 1);
        long consumed = cursor.Position - start;
        if (consumed != declared) throw InvalidSize(declared, consumed);
        return new PropStruct(kind, classHash, properties);
    }

    // ================================================================================================ reading one object

    /// <summary>The object of path hash <paramref name="pathHash"/> in a PROP, or null when the file holds none: the table of contents is
    /// harvested from the objects' size fields, the last row of that hash is decoded, and no other object is. This is what the overlay
    /// does to answer a reference (<c>stream.object(hash)?.read()</c>).</summary>
    public static PropObject? ReadObject(byte[] bytes, uint pathHash) => Mount(bytes).ReadObject(pathHash);

    // ================================================================================================ reading PTCH

    /// <summary><c>BinOverride::from_reader</c>.</summary>
    /// <exception cref="PropDecodeException">The bytes are not a PTCH the codec reads.</exception>
    public static PtchBin ReadPtch(byte[] bytes) => ReadPtch(bytes, null);

    internal static PtchBin ReadPtch(byte[] bytes, WorkMeter? meter)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            return ReadPtchOf(bytes, new DecodeState(meter));
        }
        catch (InsufficientExecutionStackException)
        {
            throw new PropDecodeException("Container nesting is too deep for the stack");
        }
    }

    private static PtchBin ReadPtchOf(byte[] bytes, DecodeState state)
    {
        var reader = new StreamReader(bytes);
        uint magic = reader.U32();
        if (magic == PropMagic) throw new PropDecodeException("Expected a PTCH bin, found a PROP bin");
        if (magic != PtchMagic) throw new PropDecodeException("Invalid file signature");
        uint version = reader.U32();
        if (version != 1) throw new PropDecodeException($"Invalid PTCH version '{version}' - the client only accepts 1");

        var patch = new PtchBin();
        uint deleted = reader.U32();
        for (uint i = 0; i < deleted; i++)
        {
            state.Meter?.Charge(1);
            patch.Deleted.Add(reader.U32());
        }

        if (reader.U32() != PropMagic) throw new PropDecodeException("Invalid file signature");
        uint inner = reader.U32();
        if (inner is < 1 or > 3) throw new PropDecodeException($"Invalid file version '{inner}'");
        if (inner >= 2)
        {
            // the client does not support dependencies, so a patch that declares any is rejected
            uint dependencies = reader.U32();
            if (dependencies != 0)
                throw new PropDecodeException($"The PTCH declares {dependencies} dependencies - a patch that has any cannot be loaded");
        }

        patch.Objects = ReadObjects(reader, state);
        if (inner >= 3)
        {
            uint records = reader.U32();
            for (uint i = 0; i < records; i++)
            {
                state.Meter?.Charge(1);
                patch.Patches.Add(ReadRecord(reader, (int)i, state));
            }
        }
        return patch;
    }

    /// <summary><c>read_objects</c>: the class table and the objects, tried in the current numbering and then, on a kind byte that does
    /// not decode, in the legacy one.</summary>
    private static OrderedMap<PropObject> ReadObjects(StreamReader reader, DecodeState state)
    {
        uint count = reader.U32();
        if (count > (uint)(reader.Remaining / 4)) throw ShortRead();
        var classes = new uint[count];
        for (uint i = 0; i < count; i++) classes[i] = reader.U32();
        int start = reader.Position;
        int budget = state.ZeroWidthLeft;
        try
        {
            return ReadObjectTable(reader, classes, false, state);
        }
        catch (PropDecodeException e) when (e.InvalidKind)
        {
            reader.Position = start;
            state.ZeroWidthLeft = budget;
            return ReadObjectTable(reader, classes, true, state);
        }
    }

    private static OrderedMap<PropObject> ReadObjectTable(StreamReader reader, uint[] classes, bool legacy, DecodeState state)
    {
        var objects = new OrderedMap<PropObject>(classes.Length);
        foreach (uint classHash in classes)
        {
            // read_object_from: the size field bounds the object, so its bytes are taken in one read, short ones included
            uint declared = reader.U32();
            int available = (int)Math.Min(declared, (uint)reader.Remaining);
            int start = reader.Position;
            reader.Position = start + available;
            var cursor = new Cursor(reader.Buffer, start, start + available, legacy, state);
            var obj = ReadObjectBody(cursor, classHash);
            long consumed = cursor.Position - start;
            if (consumed != declared) throw InvalidSize(declared, consumed);
            objects.Set(obj.PathHash, obj);
        }
        return objects;
    }

    private static PatchRecord ReadRecord(StreamReader reader, int index, DecodeState state)
    {
        uint objectHash = reader.U32();
        uint size = reader.U32();
        int start = reader.Position;
        byte raw = reader.U8();
        var kind = PropKinds.Unpack(raw, false) ?? throw InvalidKind(raw);
        string text = reader.SizedString();
        if (!PropertyPath.TryParse(text, out var path, out var pathError))
            throw new PropDecodeException($"Invalid property path in patch record {index} (object {objectHash:x8}) - {pathError!.Message}");

        // the value is read from what the file holds past the path; it ends where its own counts end
        var cursor = new Cursor(reader.Buffer, reader.Position, reader.Buffer.Length, false, state);
        var value = ReadValue(cursor, kind, 0);
        reader.Position = cursor.Position;
        long real = reader.Position - start;
        if (real != size) throw InvalidSize(size, real);
        return new PatchRecord(objectHash, path!, value);
    }

    // ================================================================================================ errors

    /// <summary>The fewest bytes a value of <paramref name="kind"/> takes: its width, or what its length prefix takes.</summary>
    private static int MinWidth(PropKind kind) => PropKinds.FixedWidth(kind) switch
    {
        >= 0 and var width => width,
        _ => kind == PropKind.String ? 2 : 4,
    };

    private static PropDecodeException ShortRead() => new("IO Error - failed to fill whole buffer");

    private static PropDecodeException InvalidSize(long declared, long consumed) => new($"Invalid size - expected {declared}, got {consumed} bytes");

    private static PropDecodeException InvalidKind(byte raw) =>
        new($"Invalid property kind - No discriminant in enum `Kind` matches the value `{raw}`", invalidKind: true);

    private static PropDecodeException TooDeep() => new("Container nesting is too deep");

    /// <summary>The statement of <c>Utf8Error</c>, after the valid prefix <paramref name="validUpTo"/>.</summary>
    internal static string Utf8Statement(int validUpTo, int errorLength) => errorLength > 0
        ? $"invalid utf-8 sequence of {errorLength} bytes from index {validUpTo}"
        : $"incomplete utf-8 byte sequence from index {validUpTo}";

    /// <summary><c>core::str::from_utf8</c>'s validation: the length of the valid prefix and the length of the invalid sequence after
    /// it (0 for a sequence that is cut short), or -1 when every byte is valid.</summary>
    internal static int ValidateUtf8(ReadOnlySpan<byte> data, out int validUpTo)
    {
        int i = 0;
        validUpTo = 0;
        while (i < data.Length)
        {
            byte first = data[i];
            if (first < 0x80) { i++; continue; }
            validUpTo = i;
            int width = first switch
            {
                >= 0xC2 and <= 0xDF => 2,
                >= 0xE0 and <= 0xEF => 3,
                >= 0xF0 and <= 0xF4 => 4,
                _ => 0,
            };
            if (width == 0) return 1;
            if (i + 1 >= data.Length) return 0;
            byte second = data[i + 1];
            if (width == 2)
            {
                if (second is < 0x80 or > 0xBF) return 1;
            }
            else if (width == 3)
            {
                bool ok = (first, second) switch
                {
                    (0xE0, >= 0xA0 and <= 0xBF) => true,
                    (>= 0xE1 and <= 0xEC, >= 0x80 and <= 0xBF) => true,
                    (0xED, >= 0x80 and <= 0x9F) => true,
                    (>= 0xEE and <= 0xEF, >= 0x80 and <= 0xBF) => true,
                    _ => false,
                };
                if (!ok) return 1;
                if (i + 2 >= data.Length) return 0;
                if (data[i + 2] is < 0x80 or > 0xBF) return 2;
            }
            else
            {
                bool ok = (first, second) switch
                {
                    (0xF0, >= 0x90 and <= 0xBF) => true,
                    (>= 0xF1 and <= 0xF3, >= 0x80 and <= 0xBF) => true,
                    (0xF4, >= 0x80 and <= 0x8F) => true,
                    _ => false,
                };
                if (!ok) return 1;
                if (i + 2 >= data.Length) return 0;
                if (data[i + 2] is < 0x80 or > 0xBF) return 2;
                if (i + 3 >= data.Length) return 0;
                if (data[i + 3] is < 0x80 or > 0xBF) return 3;
            }
            i += width;
        }
        validUpTo = data.Length;
        return -1;
    }

    // ================================================================================================ readers

    /// <summary>A sequential reader over a file, with the short-read errors <c>std::io</c> gives (<c>read_exact</c>).</summary>
    private sealed class StreamReader
    {
        public StreamReader(byte[] buffer) { Buffer = buffer; }

        public byte[] Buffer { get; }

        public int Position { get; set; }

        public int Remaining => Buffer.Length - Position;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > Buffer.Length - Position) throw ShortRead();
            var span = Buffer.AsSpan(Position, count);
            Position += count;
            return span;
        }

        public byte U8() => Take(1)[0];
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        /// <summary><c>read_sized_string_u16</c>: a <c>u16</c> length and that many bytes of UTF-8.</summary>
        public string SizedString()
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            var bytes = Take(length);
            int bad = ValidateUtf8(bytes, out int validUpTo);
            if (bad >= 0) throw new PropDecodeException("From UTF-8 Error - " + Utf8Statement(validUpTo, bad));
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>A reading position over one object's bytes and the kind numbering they were written under (<c>layout::Cursor</c>). Reading
    /// past the end is the error <c>Cursor::take</c> raises.</summary>
    private sealed class Cursor
    {
        private readonly byte[] _buf;
        private readonly int _end;
        private readonly bool _legacy;

        public Cursor(byte[] buf, int start, int end, bool legacy, DecodeState state)
        {
            _buf = buf;
            Position = start;
            _end = end;
            _legacy = legacy;
            State = state;
        }

        public DecodeState State { get; }

        public int Position { get; private set; }

        /// <summary>The bytes this object has left.</summary>
        public int Remaining => _end - Position;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > _end - Position) throw new PropDecodeException("IO Error - unexpected end of file");
            var span = _buf.AsSpan(Position, count);
            Position += count;
            return span;
        }

        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public float F32() => BitConverter.UInt32BitsToSingle(U32());

        public float[] Floats(int count)
        {
            var floats = new float[count];
            var bytes = Take(count * 4);
            for (int i = 0; i < count; i++) floats[i] = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]));
            return floats;
        }

        public string String(string utf8Error)
        {
            int length = U16();
            var bytes = Take(length);
            int bad = ValidateUtf8(bytes, out int validUpTo);
            if (bad >= 0) throw new PropDecodeException(utf8Error + Utf8Statement(validUpTo, bad));
            return Encoding.UTF8.GetString(bytes);
        }

        public PropKind Kind()
        {
            byte raw = U8();
            return PropKinds.Unpack(raw, _legacy) ?? throw InvalidKind(raw);
        }

        /// <summary>The kind a container or an option declares for what it holds: none of the container kinds.</summary>
        public PropKind ItemKind()
        {
            var kind = Kind();
            if (PropKinds.IsContainer(kind)) throw new PropDecodeException($"Container type {kind} cannot be nested!");
            return kind;
        }

        /// <summary>The kind a map declares for its keys: one that can key a map.</summary>
        public PropKind KeyKind()
        {
            var kind = Kind();
            if (!PropKinds.IsValidMapKey(kind)) throw new PropDecodeException($"Invalid map key type {kind}, only primitive types can be used as keys.");
            return kind;
        }

        /// <summary>How many entries to reserve for a declared <paramref name="count"/>: no more than the bytes left can hold, and no more than
        /// <see cref="PropLimits.MaxReserveBytes"/> of <paramref name="elementBytes"/> each, whatever the bytes could hold; a list that is longer
        /// than that grows as it is read. A count the file cannot hold is read until the bytes end, which is the error. Entries that take no
        /// bytes at all are bounded by nothing but a budget for the whole decode, which is spent here.</summary>
        public int Capacity(uint count, int minimumWidth, int elementBytes)
        {
            int reserve = PropLimits.MaxReserveBytes / elementBytes;
            if (minimumWidth <= 0)
            {
                if (count > (uint)State.ZeroWidthLeft)
                    throw new PropDecodeException($"Container declares {count} entries of a kind that takes no bytes");
                State.ZeroWidthLeft -= (int)count;
                return (int)Math.Min(count, (uint)reserve);
            }
            long room = (_end - Position) / minimumWidth;
            return (int)Math.Min(Math.Min(count, Math.Max(room, 0)), reserve);
        }
    }

    // ================================================================================================ writing

    private sealed class ByteSink
    {
        private readonly WorkMeter? _meter;
        private readonly long _max;
        private byte[] _data = new byte[4096];
        private int _length;

        public ByteSink(WorkMeter? meter)
        {
            _meter = meter;
            _max = meter?.MaxOutput ?? long.MaxValue;
        }

        public int Length => _length;

        /// <summary>Counts work done writing: a property, an entry, an element.</summary>
        public void Charge(long units) => _meter?.Charge(units);

        /// <summary>Makes room for <paramref name="extra"/> bytes. The limit is looked at for every reservation and not only when the buffer
        /// has to grow: the buffer doubles, so it can hold more than the limit allows, and what it holds is not what may be written.</summary>
        private void Grow(int extra)
        {
            long needed = (long)_length + extra;
            if (needed > _max) throw _meter!.WriteExceeded();
            if (needed <= _data.Length) return;
            if (needed > Array.MaxLength) throw new GameDataException(GameDataErrorKind.LimitExceeded, "output exceeds the two gigabytes an array holds");
            Array.Resize(ref _data, (int)Math.Min(Math.Max(_data.Length * 2L, needed), Array.MaxLength));
        }

        public void U8(byte value) { Grow(1); _data[_length++] = value; }
        public void U16(ushort value) { Grow(2); BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(_length), value); _length += 2; }
        public void U32(uint value) { Grow(4); BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(_length), value); _length += 4; }
        public void U64(ulong value) { Grow(8); BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(_length), value); _length += 8; }
        public void F32(float value) => U32(BitConverter.SingleToUInt32Bits(value));

        public void Bytes(ReadOnlySpan<byte> bytes)
        {
            Grow(bytes.Length);
            bytes.CopyTo(_data.AsSpan(_length));
            _length += bytes.Length;
        }

        /// <summary><c>write_len_prefixed_string</c>: a <c>u16</c> length, then the bytes. <c>ltk_meta</c> casts the length and writes every byte, so a
        /// string past 65,535 bytes leaves a bin that nothing reads; this writer refuses it.</summary>
        public void LengthPrefixed(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > ushort.MaxValue)
                throw new GameDataException(GameDataErrorKind.Bin, $"A string of {bytes.Length} bytes does not fit the 16-bit length a bin writes it with (at most {ushort.MaxValue})");
            U16((ushort)bytes.Length);
            Bytes(bytes);
        }

        /// <summary>Reserves a <c>u32</c> and returns where it is.</summary>
        public int Placeholder() { U32(0); return _length - 4; }

        /// <summary>Fills the <c>u32</c> at <paramref name="at"/> with the bytes written since it.</summary>
        public void Fill(int at) => BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(at), unchecked((uint)(_length - at - 4)));

        public byte[] ToArray() => _data.AsSpan(0, _length).ToArray();
    }

    /// <summary><c>Bin::to_writer</c>: version 3, the dependencies, the class table, then each object.</summary>
    /// <exception cref="GameDataException">Of kind <see cref="GameDataErrorKind.Bin"/> for a tree that nests more than 256 containers or holds a
    /// string, a path or a property count that a 16-bit field cannot hold: a bin the codec would not read back.</exception>
    public static byte[] WriteProp(PropBin bin) => WriteProp(bin, null);

    internal static byte[] WriteProp(PropBin bin, WorkMeter? meter)
    {
        ArgumentNullException.ThrowIfNull(bin);
        try
        {
            var sink = new ByteSink(meter);
            sink.U32(PropMagic);
            sink.U32(3);
            sink.U32(unchecked((uint)bin.Dependencies.Count));
            foreach (string dependency in bin.Dependencies) sink.LengthPrefixed(dependency);
            WriteObjects(sink, bin.Objects);
            return sink.ToArray();
        }
        catch (InsufficientExecutionStackException)
        {
            throw new GameDataException(GameDataErrorKind.Bin, "Container nesting is too deep for the stack");
        }
    }

    private static void WriteObjects(ByteSink sink, OrderedMap<PropObject> objects)
    {
        sink.U32(unchecked((uint)objects.Count));
        sink.Charge(objects.Count);
        for (int i = 0; i < objects.Count; i++) sink.U32(objects.ValueAt(i).ClassHash);
        for (int i = 0; i < objects.Count; i++) WriteObject(sink, objects.ValueAt(i));
    }

    private static void WriteObject(ByteSink sink, PropObject obj)
    {
        int size = sink.Placeholder();
        sink.U32(obj.PathHash);
        WriteProperties(sink, obj.Properties, 0);
        sink.Fill(size);
    }

    /// <summary>The properties of an object (<paramref name="depth"/> 0) or of a struct, each a value <paramref name="depth"/> containers down.</summary>
    private static void WriteProperties(ByteSink sink, OrderedMap<PropValue> properties, int depth)
    {
        if (properties.Count > ushort.MaxValue)
            throw new GameDataException(GameDataErrorKind.Bin, $"{properties.Count} properties do not fit the 16-bit count a bin writes them with (at most {ushort.MaxValue})");
        sink.U16((ushort)properties.Count);
        sink.Charge(properties.Count + 1);
        for (int i = 0; i < properties.Count; i++)
        {
            var value = properties.ValueAt(i);
            sink.U32(properties.KeyAt(i));
            sink.U8((byte)value.Kind);
            WriteValue(sink, value, depth);
        }
    }

    /// <summary>A container, an option, a map or a struct is about to be written <paramref name="depth"/> containers down: past the depth the codec reads
    /// is a tree it could not read back, and past the stack is one this recursion could not finish.</summary>
    private static void Nest(int depth)
    {
        if (depth >= PropLimits.MaxDepth) throw PropLimits.TooDeep();
        RuntimeHelpers.EnsureSufficientExecutionStack();
    }

    private static void WriteValue(ByteSink sink, PropValue value, int depth)
    {
        switch (value)
        {
            case PropNone: break;
            case PropBool b: sink.U8(b.Value ? (byte)1 : (byte)0); break;
            case PropInt n:
                switch (n.Kind)
                {
                    case PropKind.I8: case PropKind.U8: sink.U8(unchecked((byte)n.Bits)); break;
                    case PropKind.I16: case PropKind.U16: sink.U16(unchecked((ushort)n.Bits)); break;
                    case PropKind.I32: case PropKind.U32: sink.U32(unchecked((uint)n.Bits)); break;
                    default: sink.U64(n.Bits); break;
                }
                break;
            case PropF32 f: sink.F32(f.Value); break;
            case PropVector v: foreach (float component in v.Components) sink.F32(component); break;
            case PropColor c: sink.U32(c.Packed); break;
            case PropString s: sink.LengthPrefixed(s.Value); break;
            case PropHash h: sink.U32(h.Value); break;
            case PropFile file: sink.U64(file.Value); break;
            case PropList list:
            {
                Nest(depth);
                sink.U8((byte)list.ItemKind);
                int size = sink.Placeholder();
                sink.U32(unchecked((uint)list.Items.Count));
                sink.Charge(list.Items.Count + 1);
                foreach (var item in list.Items) WriteValue(sink, item, depth + 1);
                sink.Fill(size);
                break;
            }
            case PropOption option:
                Nest(depth);
                sink.U8((byte)option.ItemKind);
                sink.U8(option.Value is null ? (byte)0 : (byte)1);
                if (option.Value is not null) WriteValue(sink, option.Value, depth + 1);
                break;
            case PropMapValue map:
            {
                Nest(depth);
                sink.U8((byte)map.KeyKind);
                sink.U8((byte)map.ValueKind);
                int size = sink.Placeholder();
                sink.U32(unchecked((uint)map.Entries.Count));
                sink.Charge(map.Entries.Count + 1);
                foreach (var entry in map.Entries)
                {
                    WriteValue(sink, entry.Key, depth + 1);
                    WriteValue(sink, entry.Value, depth + 1);
                }
                sink.Fill(size);
                break;
            }
            case PropStruct s:
            {
                Nest(depth);
                sink.U32(s.ClassHash);
                if (s.ClassHash == 0) break;
                int size = sink.Placeholder();
                WriteProperties(sink, s.Properties, depth + 1);
                sink.Fill(size);
                break;
            }
            default: throw new InvalidOperationException("unknown value");
        }
    }

    /// <summary><c>BinOverride::to_writer</c>: PTCH version 1 around a PROP version 3 with no dependencies.</summary>
    /// <exception cref="GameDataException">As for <see cref="WriteProp(PropBin)"/>.</exception>
    public static byte[] WritePtch(PtchBin patch) => WritePtch(patch, null);

    internal static byte[] WritePtch(PtchBin patch, WorkMeter? meter)
    {
        ArgumentNullException.ThrowIfNull(patch);
        try
        {
            var sink = new ByteSink(meter);
            sink.U32(PtchMagic);
            sink.U32(1);
            sink.U32(unchecked((uint)patch.Deleted.Count));
            sink.Charge(patch.Deleted.Count);
            foreach (uint hash in patch.Deleted) sink.U32(hash);
            sink.U32(PropMagic);
            sink.U32(3);
            sink.U32(0);
            WriteObjects(sink, patch.Objects);
            sink.U32(unchecked((uint)patch.Patches.Count));
            foreach (var record in patch.Patches)
            {
                sink.Charge(1);
                sink.U32(record.ObjectHash);
                int size = sink.Placeholder();
                sink.U8((byte)record.Value.Kind);
                sink.LengthPrefixed(record.Path.Text);
                WriteValue(sink, record.Value, 0);
                sink.Fill(size);
            }
            return sink.ToArray();
        }
        catch (InsufficientExecutionStackException)
        {
            throw new GameDataException(GameDataErrorKind.Bin, "Container nesting is too deep for the stack");
        }
    }
}

/// <summary>
/// A mounted PROP (<c>BinStream</c>): its header read, its objects not yet. <see cref="BodyOffset"/> is where the object table starts when
/// the dependencies are the ones the file lists, which is what a header-only rewrite copies from.
/// </summary>
public sealed class PropMount
{
    private readonly byte[] _bytes;
    private readonly uint[] _classes;
    private readonly int _objectsStart;

    internal PropMount(byte[] bytes, uint version, List<string> dependencies, uint[] classes, int objectsStart)
    {
        _bytes = bytes;
        Version = version;
        Dependencies = dependencies;
        _classes = classes;
        _objectsStart = objectsStart;
        BodyOffset = PropCodec.HeaderLength(version, dependencies);
    }

    public uint Version { get; }

    /// <summary>The dependencies as the file lists them, duplicates included.</summary>
    public List<string> Dependencies { get; }

    /// <summary>The offset of the object count: the magic, the version, the dependency count and the dependencies in front of it.</summary>
    public int BodyOffset { get; }

    public int ObjectCount => _classes.Length;

    /// <summary>The path hash and class of every object, in file order, read from the size fields without decoding a value
    /// (<c>BinStream::entries</c>). This is how an index of a game's bins is built.</summary>
    /// <exception cref="PropDecodeException">A size field the file cannot hold.</exception>
    public List<(uint PathHash, uint ClassHash)> Declarations()
    {
        var found = new List<(uint, uint)>(Math.Min(_classes.Length, PropLimits.MaxReserveBytes / 8));
        long offset = _objectsStart;
        for (int i = 0; i < _classes.Length; i++)
        {
            var row = PropCodec.HarvestRow(_bytes, offset);
            found.Add((row.Path, _classes[i]));
            offset = row.Offset + 4 + row.Size;
        }
        return found;
    }

    /// <summary>The object of <paramref name="pathHash"/>, or null: see <see cref="PropCodec.ReadObject"/>.</summary>
    public PropObject? ReadObject(uint pathHash) => ReadObject(pathHash, null);

    internal PropObject? ReadObject(uint pathHash, WorkMeter? meter)
    {
        try
        {
            long offset = _objectsStart;
            int found = -1;
            var rows = new List<(uint Path, long Offset, uint Size)>(Math.Min(_classes.Length, PropLimits.MaxReserveBytes / 16));
            for (int i = 0; i < _classes.Length; i++)
            {
                var row = PropCodec.HarvestRow(_bytes, offset);
                rows.Add(row);
                if (row.Path == pathHash) found = i;
                offset = row.Offset + 4 + row.Size;
            }
            if (found < 0) return null;
            bool legacy = false;
            return PropCodec.ReadRow(_bytes, rows[found], _classes[found], ref legacy, new PropCodec.DecodeState(meter));
        }
        catch (InsufficientExecutionStackException)
        {
            throw new PropDecodeException("Container nesting is too deep for the stack");
        }
    }

    /// <summary><c>BinStream::into_bin</c>: every object, from the top. The first of two objects with one path hash keeps its slot and takes
    /// the later one's value. An object whose kind bytes only make sense in the legacy numbering is retried in it, and a retry that works
    /// latches the numbering for the whole file, so the objects read so far are read again.</summary>
    /// <exception cref="PropDecodeException">An object the codec refuses.</exception>
    public PropBin Read() => Read(null);

    internal PropBin Read(WorkMeter? meter)
    {
        try
        {
            var bin = new PropBin { Version = Version };
            bin.Dependencies.AddRange(Dependencies);
            var state = new PropCodec.DecodeState(meter);

            // the table of contents: each object's offset and declared size, harvested one row at a time
            var rows = new List<(uint Path, long Offset, uint Size)>(Math.Min(_classes.Length, PropLimits.MaxReserveBytes / 16));
            bool legacy = false;
            int index = 0;
            while (index < _classes.Length)
            {
                if (index == rows.Count)
                {
                    long offset = rows.Count == 0 ? _objectsStart : rows[^1].Offset + 4 + rows[^1].Size;
                    rows.Add(PropCodec.HarvestRow(_bytes, offset));
                }
                bool before = legacy;
                meter?.Charge(1);
                var obj = PropCodec.ReadRow(_bytes, rows[index], _classes[index], ref legacy, state);
                if (legacy != before)
                {
                    // a latch onto the legacy numbering invalidates what was read, so the drain starts over
                    bin.Objects.Clear();
                    state.ZeroWidthLeft = PropLimits.ZeroWidthBudget;
                    index = 0;
                    continue;
                }
                bin.Objects.Set(obj.PathHash, obj);
                index++;
            }
            return bin;
        }
        catch (InsufficientExecutionStackException)
        {
            throw new PropDecodeException("Container nesting is too deep for the stack");
        }
    }
}
