using System.Text;

namespace ReyEngine.Formats.Audio;

/// <summary>M56: League .wpk wem-pack reader ("r3d2"): a table of u32 entry offsets; each entry is
/// { u32 dataOffset, u32 size, u32 nameLen, UTF-16LE "123456.wem" } — the numeric name is the wem id.
/// Port of LtMAO's pyRitoFile/wpk.py.</summary>
public sealed class WpkFile
{
    public IReadOnlyDictionary<uint, (int Offset, int Size)> Wems => _wems;
    private readonly Dictionary<uint, (int, int)> _wems = new();
    private byte[] _raw = System.Array.Empty<byte>();

    public byte[]? GetWemData(uint wemId)
    {
        if (!_wems.TryGetValue(wemId, out var e)) return null;
        if (e.Item1 < 0 || e.Item1 + e.Item2 > _raw.Length) return null;
        var data = new byte[e.Item2];
        System.Array.Copy(_raw, e.Item1, data, 0, e.Item2);
        return data;
    }

    /// <summary>Order of wem ids as they appear in the pack (preserved for rebuild).</summary>
    private readonly List<uint> _order = new();

    /// <summary>M57: rewrite the pack with some wems replaced (id → new .wem bytes).</summary>
    public byte[] Rebuild(IReadOnlyDictionary<uint, byte[]> replacements)
    {
        var entries = new List<(uint Id, byte[] Data)>();
        foreach (var id in _order)
            entries.Add((id, replacements.TryGetValue(id, out var rep) ? rep : (GetWemData(id) ?? System.Array.Empty<byte>())));
        return RebuildEntries(entries);
    }

    /// <summary>The wem ids in pack order (preserved across a rebuild).</summary>
    public IReadOnlyList<uint> Order => _order;

    /// <summary>
    /// M137: rewrite the pack so its contents are EXACTLY <paramref name="entries"/> — added, removed,
    /// re-identified and reordered wems included. Layout matches LtMAO's wpk.write: r3d2 / version 1 /
    /// count / offset table / entries {u32 dataOffset, u32 size, u32 nameLen, UTF-16 "id.wem"} / data.
    /// </summary>
    /// <summary>Shipped packs 8-align both the entry headers and the payloads (verified across Riot
    /// .wpk files — the stride only reveals it when entry name lengths differ, e.g. 8- vs 9-digit ids).</summary>
    private const int EntryAlignment = 8;
    private const int DataAlignment = 8;

    public static byte[] RebuildEntries(IReadOnlyList<(uint Id, byte[] Data)> entries)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Pad(int alignment)
        {
            while (ms.Position % alignment != 0) w.Write((byte)0);
        }

        w.Write(Encoding.ASCII.GetBytes("r3d2"));
        w.Write(1u);                              // version
        w.Write((uint)entries.Count);
        long offsetTablePos = ms.Position;
        foreach (var _ in entries) w.Write(0u);   // entry offsets, filled below

        var entryOffsets = new long[entries.Count];
        var dataPatchPos = new long[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            Pad(EntryAlignment);
            entryOffsets[i] = ms.Position;
            var name = $"{entries[i].Id}.wem";
            dataPatchPos[i] = ms.Position;
            w.Write(0u);                          // dataOffset (patched after data written)
            w.Write((uint)entries[i].Data.Length);
            w.Write((uint)name.Length);
            w.Write(Encoding.Unicode.GetBytes(name));
        }
        for (int i = 0; i < entries.Count; i++)
        {
            Pad(DataAlignment);
            long dataStart = ms.Position;
            w.Write(entries[i].Data);
            long resume = ms.Position;
            ms.Position = dataPatchPos[i]; w.Write((uint)dataStart);
            ms.Position = resume;
        }
        ms.Position = offsetTablePos;
        foreach (var off in entryOffsets) w.Write((uint)off);
        return ms.ToArray();
    }

    public static WpkFile? Parse(byte[] data)
    {
        try
        {
            var wpk = new WpkFile { _raw = data };
            using var ms = new MemoryStream(data, writable: false);
            using var r = new BinaryReader(ms);
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "r3d2") return null;
            r.ReadUInt32();   // version
            uint count = r.ReadUInt32();
            var offsets = new uint[count];
            for (int i = 0; i < count; i++) offsets[i] = r.ReadUInt32();
            foreach (var off in offsets)
            {
                if (off == 0 || off + 12 > data.Length) continue;
                ms.Position = off;
                int dataOffset = r.ReadInt32();
                int size = r.ReadInt32();
                int nameLen = r.ReadInt32();
                var name = Encoding.Unicode.GetString(r.ReadBytes(nameLen * 2));
                if (uint.TryParse(name.Replace(".wem", ""), out var id))
                { wpk._wems[id] = (dataOffset, size); wpk._order.Add(id); }
            }
            return wpk;
        }
        catch { return null; }
    }
}
