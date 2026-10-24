using System.Buffers.Binary;
using System.IO.Hashing;
using LeagueToolkit.Core.Meta;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// Patch 16.21 wire change: <c>StaticMaterialDef.name</c> (field 0x8d39bde6 of class 0xff9d3409) is no longer a
/// String. It is type byte 0x11 (Hash) followed by an EIGHT-byte little-endian XXH3-64 of the lowercased name, where
/// every other Hash is four bytes. Nothing in the file marks the new form; only the (class, field) pair does.
///
/// <para>LeagueToolkit 4.1.0-beta.53 reads four bytes for a Hash and desyncs. So every read goes through
/// <see cref="Normalize"/> first: it rewrites that one type byte to U64 (0x09, also eight bytes, so no size changes) and
/// LeagueToolkit then parses the bin strictly. U64 - not WadChunkLink - because the rest of ReyEngine treats a
/// WadChunkLink as an asset path. Every write goes through <see cref="Denormalize"/>, which turns the U64 stand-in back
/// into 0x11 + the same eight bytes, and a legacy String name (a 16.20 bin) into 0x11 + XXH3-64 of the lowercased
/// name: ReyEngine targets the current patch, so a material is always written in the 16.21 form.</para>
///
/// <para>Both directions walk the PROP framing and never guess: when the bytes do not frame consistently,
/// <see cref="Normalize"/> hands back the original array untouched.</para>
/// </summary>
public static class BinWireNormalizer
{
    public const uint StaticMaterialDefClass = 0xff9d3409;
    public const uint NameField = 0x8d39bde6;
    private const byte TypeU64 = 0x09, TypeString = 0x10, TypeHash = 0x11;

    /// <summary>XXH3-64 of the lowercased (ASCII) name: the value 16.21 stores in a material name.</summary>
    public static ulong NameHash(string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        LowerAscii(bytes);
        return XxHash3.HashToUInt64(bytes);
    }

    /// <summary>Is <paramref name="field"/> of a <paramref name="ownerClass"/> the 8-byte hashed material name?</summary>
    public static bool IsMaterialName(uint ownerClass, uint field) => ownerClass == StaticMaterialDefClass && field == NameField;

    /// <summary>Bytes in which the 16.21 name is a U64 stand-in. Returns <paramref name="data"/> itself when nothing changed.</summary>
    public static byte[] Normalize(byte[] data)
    {
        if (!MightContain(data)) return data;
        try
        {
            var result = Rewrite(data, toWire: false, out int changed);
            return changed == 0 ? data : result;
        }
        catch { return data; }   // not a walkable PROP: leave it to the readers (and their repairs) exactly as before
    }

    /// <summary>The on-disk form of LeagueToolkit-written bytes: stand-in and String names become 0x11 + eight bytes. Bytes that already hold
    /// the wire form are returned as they are (the call is idempotent); bytes that do not frame as a PROP throw.</summary>
    public static byte[] Denormalize(byte[] data)
    {
        if (!MightContain(data)) return data;
        var result = Rewrite(data, toWire: true, out int changed);
        return changed == 0 ? data : result;
    }

    /// <summary>Cheap pre-filter: both the class hash and the field hash must occur somewhere in the bytes.</summary>
    private static bool MightContain(byte[] data)
    {
        if (data.Length < 16) return false;
        ReadOnlySpan<byte> field = [0xe6, 0xbd, 0x39, 0x8d];
        ReadOnlySpan<byte> cls = [0x09, 0x34, 0x9d, 0xff];
        return data.AsSpan().IndexOf(field) >= 0 && data.AsSpan().IndexOf(cls) >= 0;
    }

    // Only A-Z fold, like the lowercase hash of every other path ReyEngine writes. All 31,653 top-level 16.21 material names checked
    // against xxh3_64(lower(entry path)) were ASCII, so what Riot does with a non-ASCII letter is unobserved.
    private static void LowerAscii(byte[] b) { for (int i = 0; i < b.Length; i++) if (b[i] >= 'A' && b[i] <= 'Z') b[i] += 32; }

    private static byte[] Rewrite(byte[] data, bool toWire, out int changed)
    {
        var w = new Walker(data, toWire);
        int p = 0;
        if (data.Length < 12) throw new InvalidDataException("short");
        if (data[0] == 'P' && data[1] == 'T' && data[2] == 'C' && data[3] == 'H') p = 12;
        if (p + 4 > data.Length || data[p] != 'P' || data[p + 1] != 'R' || data[p + 2] != 'O' || data[p + 3] != 'P')
            throw new InvalidDataException("not PROP");
        p += 4;
        uint version = U32(data, ref p);
        if (version >= 2)
        {
            uint deps = U32(data, ref p);
            for (uint i = 0; i < deps; i++) { ushort len = U16(data, ref p); p = checked(p + len); }
        }
        uint count = U32(data, ref p);
        int classTable = p;
        p = checked(p + (int)count * 4);
        if (p > data.Length) throw new InvalidDataException("class table");

        var output = new MemoryStream(data.Length + 64);
        output.Write(data, 0, p);
        var body = new MemoryStream(1024);
        for (uint i = 0; i < count; i++)
        {
            int objectStart = p;
            uint size = U32(data, ref p);
            int end = checked(objectStart + 4 + (int)size);
            if (end > data.Length || end < p) throw new InvalidDataException("object size");
            int pathAt = p;
            p += 4;
            ushort props = U16(data, ref p);
            uint cls = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(classTable + (int)i * 4, 4));
            body.SetLength(0);
            int q = p;
            for (int k = 0; k < props; k++) q = w.Property(q, cls, body);
            if (q != end) throw new InvalidDataException("object framing");
            WriteU32(output, checked((uint)(body.Length + 6)));
            output.Write(data, pathAt, 4);
            WriteU16(output, props);
            body.Position = 0;
            body.CopyTo(output);
            p = end;
        }
        if (p < data.Length) output.Write(data, p, data.Length - p);   // PTCH patch section, if any
        changed = w.Changed;
        return output.ToArray();
    }

    private sealed class Walker(byte[] d, bool toWire)
    {
        public int Changed;

        public int Property(int p, uint ownerClass, Stream output)
        {
            uint field = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4));
            byte type = d[p + 4];
            if (IsMaterialName(ownerClass, field))
            {
                if (!toWire && type == TypeHash)
                {
                    if (p + 13 > d.Length) throw new InvalidDataException("name");
                    output.Write(d, p, 4);
                    output.WriteByte(TypeU64);
                    output.Write(d, p + 5, 8);
                    Changed++;
                    return p + 13;
                }
                if (toWire && type == TypeU64)
                {
                    output.Write(d, p, 4);
                    output.WriteByte(TypeHash);
                    output.Write(d, p + 5, 8);
                    Changed++;
                    return p + 13;
                }
                if (toWire && type == TypeHash)
                {
                    // already the wire form (bytes that were denormalised before): copy it, change nothing
                    if (p + 13 > d.Length) throw new InvalidDataException("name");
                    output.Write(d, p, 13);
                    return p + 13;
                }
                if (toWire && type == TypeString)
                {
                    int len = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 5, 2));
                    var text = d.AsSpan(p + 7, len).ToArray();
                    LowerAscii(text);
                    output.Write(d, p, 4);
                    output.WriteByte(TypeHash);
                    Span<byte> h = stackalloc byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(h, XxHash3.HashToUInt64(text));
                    output.Write(h);
                    Changed++;
                    return p + 7 + len;
                }
            }
            output.Write(d, p, 5);
            return Value(p + 5, type, output);
        }

        private int Value(int p, byte type, Stream output)
        {
            switch (type)
            {
                case <= 18 and not 16:
                {
                    int n = PrimitiveSize(type);
                    if (p + n > d.Length) throw new InvalidDataException("eof");
                    output.Write(d, p, n);
                    return p + n;
                }
                case 16:
                {
                    int n = 2 + BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p, 2));
                    if (p + n > d.Length) throw new InvalidDataException("eof");
                    output.Write(d, p, n);
                    return p + n;
                }
                case 0x87: output.Write(d, p, 1); return p + 1;
                case 0x84: output.Write(d, p, 4); return p + 4;
                case 0x82 or 0x83:
                {
                    uint classHash = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4));
                    if (classHash == 0 && type == 0x82) { output.Write(d, p, 4); return p + 4; }
                    int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 4, 4)));
                    ushort n = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 8, 2));
                    var inner = new MemoryStream(Math.Max(16, size));
                    int q = p + 10;
                    for (int i = 0; i < n; i++) q = Property(q, classHash, inner);
                    if (q - (p + 8) != size) throw new InvalidDataException("struct size");
                    WriteU32(output, classHash);
                    WriteU32(output, checked((uint)(inner.Length + 2)));
                    WriteU16(output, n);
                    inner.Position = 0;
                    inner.CopyTo(output);
                    return q;
                }
                case 0x80 or 0x81:
                {
                    byte elementType = d[p];
                    int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 1, 4)));
                    uint n = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 5, 4));
                    var inner = new MemoryStream(Math.Max(16, size));
                    int q = p + 9;
                    for (uint i = 0; i < n; i++) q = Value(q, elementType, inner);
                    if (q - (p + 5) != size) throw new InvalidDataException("container size");
                    output.WriteByte(elementType);
                    WriteU32(output, checked((uint)(inner.Length + 4)));
                    WriteU32(output, n);
                    inner.Position = 0;
                    inner.CopyTo(output);
                    return q;
                }
                case 0x85:
                {
                    byte valueType = d[p];
                    bool has = d[p + 1] != 0;
                    output.Write(d, p, 2);
                    return has ? Value(p + 2, valueType, output) : p + 2;
                }
                case 0x86:
                {
                    byte keyType = d[p], valueType = d[p + 1];
                    int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 2, 4)));
                    uint n = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 6, 4));
                    var inner = new MemoryStream(Math.Max(16, size));
                    int q = p + 10;
                    for (uint i = 0; i < n; i++)
                    {
                        q = Value(q, keyType, inner);
                        q = Value(q, valueType, inner);
                    }
                    if (q - (p + 6) != size) throw new InvalidDataException("map size");
                    output.WriteByte(keyType);
                    output.WriteByte(valueType);
                    WriteU32(output, checked((uint)(inner.Length + 4)));
                    WriteU32(output, n);
                    inner.Position = 0;
                    inner.CopyTo(output);
                    return q;
                }
                default: throw new InvalidDataException($"unknown property type 0x{type:x2}");
            }
        }

        private static int PrimitiveSize(byte type) => type switch
        {
            0 => 0, 1 or 2 or 3 => 1, 4 or 5 => 2,
            6 or 7 or 10 or 15 or 17 => 4,
            8 or 9 or 11 or 18 => 8, 12 => 12, 13 => 16, 14 => 64,
            _ => throw new InvalidDataException($"unknown primitive 0x{type:x2}"),
        };
    }

    private static uint U32(byte[] d, ref int p) { uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4)); p += 4; return v; }
    private static ushort U16(byte[] d, ref int p) { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p, 2)); p += 2; return v; }
    private static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
    private static void WriteU16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); s.Write(b); }
}

/// <summary>The one door in and out of LeagueToolkit's <see cref="BinTree"/> for ReyEngine code:
/// reads normalise the 16.21 material-name form, writes restore it (see <see cref="BinWireNormalizer"/>).</summary>
public static class BinTreeIo
{
    /// <summary>Strict read (throws on a malformed file, like <c>new BinTree(stream)</c>) of a 16.20 or 16.21 bin.</summary>
    public static BinTree Read(byte[] data)
        => new(new MemoryStream(BinWireNormalizer.Normalize(data), writable: false));

    public static byte[] Serialize(BinTree tree)
    {
        using var ms = new MemoryStream();
        tree.Write(ms);
        return BinWireNormalizer.Denormalize(ms.ToArray());
    }

    public static void Write(BinTree tree, Stream destination)
    {
        var bytes = Serialize(tree);
        destination.Write(bytes, 0, bytes.Length);
    }
}
