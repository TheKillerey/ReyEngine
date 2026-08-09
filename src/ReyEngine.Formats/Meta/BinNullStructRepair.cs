using System.Buffers.Binary;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M413: repairs a .bin whose NULLABLE structs were serialised in the long form.
///
/// <para>The PROP format has two struct-shaped property types that are easy to confuse:
/// <c>0x82</c> ("struct"/pointer) is NULLABLE - a class hash of 0 means null and <b>nothing follows
/// it</b>, so the value is exactly four bytes - while <c>0x83</c> ("embedded") is never null and always
/// carries <c>classHash, size, propCount, props</c>. A writer that emits a null <c>0x82</c> in the long
/// form adds six stray bytes (<c>size=2, propCount=0</c>). Riot's reader and LeagueToolkit both stop
/// after the four-byte hash, so every byte after the first null in that container is misread. In the
/// game this is an instant crash at map load, before anything renders.</para>
///
/// <para>Measured on <c>TEstmode1\Map453\jade_container.materials.bin</c> (2026-08-09): 1080 nulls across
/// six objects - five <c>MapPlaceableContainer</c>s and the <c>mapContainer</c> - for 6480 stray bytes.
/// Riot's own Map453 bin holds the same 1080 nulls in the short form, so the null COUNT is a good
/// cross-check that a repair restored the file rather than merely making it parse.</para>
///
/// <para><b>The form is never guessed.</b> Each object is parsed twice, once assuming a four-byte null
/// and once assuming ten, and the reading that consumes exactly the object's own declared size wins. An
/// object that parses under neither is copied through untouched - it has some other defect, and the
/// tolerant reader is a better place to lose that argument than a rewriter is.</para>
/// </summary>
public static class BinNullStructRepair
{
    /// <summary>What a repair found and changed. All zero when the file was already canonical.</summary>
    public readonly record struct Result(int NullsFixed, int ObjectsFixed, int ObjectsUnreadable)
    {
        public bool ChangedAnything => NullsFixed > 0;
    }

    /// <summary>
    /// Rewrite long-form nulls back to the four-byte form, correcting every enclosing size field.
    /// Returns false (and leaves <paramref name="repaired"/> null) when there was nothing to fix or the
    /// file is not a walkable PROP - callers then keep their original bytes.
    /// </summary>
    public static bool TryRepair(byte[] data, out byte[]? repaired, out Result result)
    {
        repaired = null;
        result = default;
        try { return Repair(data, out repaired, out result); }
        catch { return false; }   // any surprise means "not the known defect"; never rewrite on a guess
    }

    private static bool Repair(byte[] data, out byte[]? repaired, out Result result)
    {
        repaired = null;
        result = default;
        int p = 0;
        if (data.Length < 12) return false;
        if (data[0] == 'P' && data[1] == 'T' && data[2] == 'C' && data[3] == 'H') p = 12;
        if (p + 4 > data.Length || data[p] != 'P' || data[p + 1] != 'R' || data[p + 2] != 'O' || data[p + 3] != 'P')
            return false;
        p += 4;
        uint version = ReadU32(data, ref p);
        if (version >= 2)
        {
            uint deps = ReadU32(data, ref p);
            for (uint i = 0; i < deps; i++)
            {
                ushort len = ReadU16(data, ref p);
                p = checked(p + len);
            }
        }
        uint count = ReadU32(data, ref p);
        p = checked(p + (int)count * 4);
        if (p > data.Length) return false;

        // Header + class table are byte-identical; only object bodies can change length.
        var output = new MemoryStream(data.Length);
        output.Write(data, 0, p);

        int nullsFixed = 0, objectsFixed = 0, unreadable = 0;
        var body = new MemoryStream(1024);
        for (uint i = 0; i < count; i++)
        {
            int objectStart = p;
            uint size = ReadU32(data, ref p);
            int end = checked(objectStart + 4 + (int)size);
            if (end > data.Length || end < p) return false;
            int pathHashAt = p;
            p += 4;
            ushort props = ReadU16(data, ref p);

            var reader = new Rewriter(data);
            bool ok = false;
            foreach (int nullLength in stackalloc[] { 4, 10 })
            {
                reader.Reset(nullLength);
                body.SetLength(0);
                int q = p;
                try
                {
                    for (int k = 0; k < props; k++) q = reader.Property(q, body);
                }
                catch { continue; }
                if (q != end) continue;
                ok = true;
                break;
            }

            if (!ok)
            {
                // Unrepairable by this rule. Copy it through verbatim so the rest of the file is still
                // fixed; the strict re-parse decides whether the result is usable.
                unreadable++;
                output.Write(data, objectStart, end - objectStart);
                p = end;
                continue;
            }

            if (reader.NullsRewritten > 0) { objectsFixed++; nullsFixed += reader.NullsRewritten; }
            Span<byte> header = stackalloc byte[10];
            BinaryPrimitives.WriteUInt32LittleEndian(header[..4], checked((uint)(body.Length + 6)));
            data.AsSpan(pathHashAt, 4).CopyTo(header[4..8]);
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..10], props);
            output.Write(header);
            body.Position = 0;
            body.CopyTo(output);
            p = end;
        }

        result = new Result(nullsFixed, objectsFixed, unreadable);
        if (nullsFixed == 0) return false;          // already canonical - do not hand back a rewrite
        repaired = output.ToArray();
        return true;
    }

    /// <summary>Copies a property tree, emitting nulls in the short form and recomputing every size.</summary>
    private struct Rewriter(byte[] data)
    {
        private readonly byte[] _d = data;
        private int _nullLength;
        public int NullsRewritten { get; private set; }

        public void Reset(int nullLength) { _nullLength = nullLength; NullsRewritten = 0; }

        public int Property(int p, Stream output)
        {
            byte type = _d[p + 4];
            output.Write(_d, p, 5);                 // name hash + type byte are unchanged
            return Value(p + 5, type, output);
        }

        public int Value(int p, byte type, Stream output)
        {
            switch (type)
            {
                case <= 18 and not 16:
                {
                    int n = PrimitiveSize(type);
                    output.Write(_d, p, n);
                    return p + n;
                }
                case 16:                            // String: u16 length prefix
                {
                    int n = 2 + BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(p, 2));
                    output.Write(_d, p, n);
                    return p + n;
                }
                case 0x87: output.Write(_d, p, 1); return p + 1;   // BitBool
                case 0x84: output.Write(_d, p, 4); return p + 4;   // ObjectLink
                case 0x82 or 0x83:
                {
                    uint classHash = BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(p, 4));
                    if (classHash == 0 && type == 0x82)
                    {
                        // The whole point: emit the canonical four bytes whatever the source used.
                        output.Write(_d, p, 4);
                        if (_nullLength == 10) NullsRewritten++;
                        return p + _nullLength;
                    }
                    int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(p + 4, 4)));
                    ushort n = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(p + 8, 2));
                    var inner = new MemoryStream(Math.Max(16, size));
                    int q = p + 10;
                    for (int i = 0; i < n; i++) q = Property(q, inner);
                    if (q - (p + 8) != size) throw new InvalidDataException("struct size mismatch");
                    WriteU32(output, classHash);
                    WriteU32(output, checked((uint)(inner.Length + 2)));
                    WriteU16(output, n);
                    inner.Position = 0;
                    inner.CopyTo(output);
                    return q;
                }
                case 0x80 or 0x81:                  // Container / UnorderedContainer
                {
                    byte elementType = _d[p];
                    int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(p + 1, 4)));
                    uint n = BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(p + 5, 4));
                    var inner = new MemoryStream(Math.Max(16, size));
                    int q = p + 9;
                    for (uint i = 0; i < n; i++) q = Value(q, elementType, inner);
                    if (q - (p + 5) != size) throw new InvalidDataException("container size mismatch");
                    output.WriteByte(elementType);
                    WriteU32(output, checked((uint)(inner.Length + 4)));
                    WriteU32(output, n);
                    inner.Position = 0;
                    inner.CopyTo(output);
                    return q;
                }
                case 0x85:                          // Optional
                {
                    byte valueType = _d[p];
                    bool has = _d[p + 1] != 0;
                    output.Write(_d, p, 2);
                    return has ? Value(p + 2, valueType, output) : p + 2;
                }
                case 0x86:                          // Map
                {
                    byte keyType = _d[p], valueType = _d[p + 1];
                    int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(p + 2, 4)));
                    uint n = BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(p + 6, 4));
                    var inner = new MemoryStream(Math.Max(16, size));
                    int q = p + 10;
                    for (uint i = 0; i < n; i++)
                    {
                        q = Value(q, keyType, inner);
                        q = Value(q, valueType, inner);
                    }
                    if (q - (p + 6) != size) throw new InvalidDataException("map size mismatch");
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

    private static uint ReadU32(byte[] d, ref int p) { uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4)); p += 4; return v; }
    private static ushort ReadU16(byte[] d, ref int p) { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p, 2)); p += 2; return v; }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteU16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        s.Write(b);
    }
}
