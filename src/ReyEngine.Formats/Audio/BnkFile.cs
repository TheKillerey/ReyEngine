using System.Text;

namespace ReyEngine.Formats.Audio;

/// <summary>
/// M56: Wwise SoundBank (.bnk) reader — section walk (BKHD/DIDX/DATA/HIRC) plus the minimal HIRC
/// object set needed to resolve an event to its .wem sources: Sound, Action, Event,
/// RandomOrSequenceContainer, SwitchContainer. Everything else is skipped by its declared size.
/// Faithful port of LtMAO's pyRitoFile/bnk.py (which League modding tools use in production);
/// version quirks (v88 sounds, v58 events, base-params layout by version) are kept.
/// </summary>
public sealed class BnkFile
{
    public uint Version { get; private set; }
    public uint BankId { get; private set; }

    /// <summary>Embedded wem directory (DIDX): id → (offset into DATA, size).</summary>
    public IReadOnlyDictionary<uint, (int Offset, int Size)> Wems => _wems;
    private readonly Dictionary<uint, (int, int)> _wems = new();
    private int _dataStart = -1;
    private byte[] _raw = System.Array.Empty<byte>();

    // HIRC (only what event resolution needs)
    public IReadOnlyDictionary<uint, uint[]> Events => _events;                 // event id -> action ids
    public IReadOnlyDictionary<uint, uint> ActionTargets => _actionTargets;     // action id -> target object id
    public IReadOnlyDictionary<uint, uint> Sounds => _sounds;                   // sound object id -> wem id
    public IReadOnlyDictionary<uint, uint[]> RanSeqContainers => _ranSeq;       // container id -> child sound ids
    public IReadOnlyDictionary<uint, uint[]> SwitchContainers => _switch;       // container id -> child ids
    private readonly Dictionary<uint, uint[]> _events = new();
    private readonly Dictionary<uint, uint> _actionTargets = new();
    private readonly Dictionary<uint, uint> _sounds = new();
    private readonly Dictionary<uint, uint[]> _ranSeq = new();
    private readonly Dictionary<uint, uint[]> _switch = new();

    public bool HasHirc => _events.Count > 0 || _sounds.Count > 0;
    public bool HasEmbeddedWems => _wems.Count > 0 && _dataStart >= 0;

    /// <summary>Slice an embedded wem's bytes out of the DATA section (null when not embedded here).</summary>
    public byte[]? GetWemData(uint wemId)
    {
        if (_dataStart < 0 || !_wems.TryGetValue(wemId, out var e)) return null;
        int start = _dataStart + e.Item1;
        if (start < 0 || start + e.Item2 > _raw.Length) return null;
        var data = new byte[e.Item2];
        System.Array.Copy(_raw, start, data, 0, e.Item2);
        return data;
    }

    public static BnkFile? Parse(byte[] data)
    {
        try { return ParseInner(data); }
        catch { return null; }   // malformed/unknown-version bank: never throw
    }

    /// <summary>M57: rewrite this bank with some embedded wems replaced (id → new .wem bytes). Every section
    /// except DIDX/DATA is copied verbatim; DIDX offsets/sizes + DATA are regenerated (contiguous, matching
    /// the layout LtMAO produces — proven in-game). Returns null when the bank has no embedded wems.</summary>
    public byte[]? Rebuild(IReadOnlyDictionary<uint, byte[]> replacements)
    {
        if (!HasEmbeddedWems) return null;
        var entries = new List<(uint Id, byte[] Data)>();
        foreach (var id in DidxOrder())
            entries.Add((id, replacements.TryGetValue(id, out var rep) ? rep : (GetWemData(id) ?? System.Array.Empty<byte>())));
        return RebuildEntries(entries);
    }

    /// <summary>The wem ids in DIDX order (the order the bank stores them in).</summary>
    public IReadOnlyList<uint> DidxOrder()
    {
        var order = new List<uint>();
        using var ms = new MemoryStream(_raw, writable: false);
        using var r = new BinaryReader(ms);
        while (ms.Position + 8 <= ms.Length)
        {
            string sig = Encoding.ASCII.GetString(r.ReadBytes(4));
            uint size = r.ReadUInt32();
            long end = ms.Position + size;
            if (sig == "DIDX")
                for (uint i = 0; i < size / 12; i++)
                { uint id = r.ReadUInt32(); r.ReadInt32(); r.ReadInt32(); order.Add(id); }
            ms.Position = end;
        }
        return order;
    }

    /// <summary>
    /// M137: rewrite the bank so its embedded media is EXACTLY <paramref name="entries"/> — supports
    /// added, removed, re-identified and reordered wems, not just in-place replacement. Every other
    /// section (BKHD/HIRC/STID/…) is copied verbatim; DIDX/DATA are regenerated contiguously.
    /// A bank with no DIDX/DATA yet gets both inserted right after BKHD (standard Wwise order).
    /// </summary>
    /// <summary>Riot ships embedded media 16-byte aligned inside DATA (verified on shipped banks:
    /// every DIDX offset % 16 == 0, with zero padding between entries). Wwise wants aligned media for
    /// memory-mapped playback, so rebuilds reproduce that layout rather than packing contiguously.</summary>
    private const int MediaAlignment = 16;

    public byte[] RebuildEntries(IReadOnlyList<(uint Id, byte[] Data)> entries)
    {
        // offsets first: each entry starts on the next 16-byte boundary
        var offsets = new int[entries.Count];
        int cursor = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            offsets[i] = cursor;
            cursor += entries[i].Data.Length;
            if (i + 1 < entries.Count && cursor % MediaAlignment != 0)
                cursor += MediaAlignment - (cursor % MediaAlignment);
        }
        int dataLength = cursor;   // no trailing padding, matching the shipped layout

        void WriteMedia(BinaryWriter w)
        {
            w.Write(Encoding.ASCII.GetBytes("DIDX"));
            w.Write((uint)(entries.Count * 12));
            for (int i = 0; i < entries.Count; i++)
            {
                w.Write(entries[i].Id); w.Write(offsets[i]); w.Write(entries[i].Data.Length);
            }
            w.Write(Encoding.ASCII.GetBytes("DATA"));
            w.Write((uint)dataLength);
            for (int i = 0; i < entries.Count; i++)
            {
                w.Write(entries[i].Data);
                int end = offsets[i] + entries[i].Data.Length;
                int next = i + 1 < entries.Count ? offsets[i + 1] : end;
                for (int p = end; p < next; p++) w.Write((byte)0);
            }
        }

        using var outMs = new MemoryStream();
        using var w = new BinaryWriter(outMs);
        bool wroteMedia = false;
        bool hadMediaSections = false;

        using (var ms = new MemoryStream(_raw, writable: false))
        using (var r = new BinaryReader(ms))
        {
            while (ms.Position + 8 <= ms.Length)
            {
                long secStart = ms.Position;
                string sig = Encoding.ASCII.GetString(r.ReadBytes(4));
                uint size = r.ReadUInt32();
                long end = ms.Position + size;
                if (end > ms.Length) break;

                if (sig is "DIDX" or "DATA")
                {
                    hadMediaSections = true;
                    // both are replaced by one regenerated pair, emitted at the DIDX slot
                    if (!wroteMedia && entries.Count > 0) { WriteMedia(w); wroteMedia = true; }
                }
                else
                {
                    ms.Position = secStart;
                    w.Write(r.ReadBytes((int)(end - secStart)));
                    // no media sections in this bank: insert the pair right after BKHD
                    if (!hadMediaSections && !wroteMedia && sig == "BKHD" && entries.Count > 0)
                    { WriteMedia(w); wroteMedia = true; }
                }
                ms.Position = end;
            }
        }
        return outMs.ToArray();
    }

    private static BnkFile? ParseInner(byte[] data)
    {
        var bnk = new BnkFile { _raw = data };
        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinaryReader(ms);

        while (ms.Position + 8 <= ms.Length)
        {
            string sig = Encoding.ASCII.GetString(r.ReadBytes(4));
            uint size = r.ReadUInt32();
            long end = ms.Position + size;
            if (end > ms.Length) break;

            switch (sig)
            {
                case "BKHD":
                    bnk.Version = r.ReadUInt32();
                    bnk.BankId = r.ReadUInt32();
                    break;
                case "DIDX":
                    for (uint i = 0; i < size / 12; i++)
                    {
                        uint id = r.ReadUInt32(); int off = r.ReadInt32(); int len = r.ReadInt32();
                        bnk._wems[id] = (off, len);
                    }
                    break;
                case "DATA":
                    bnk._dataStart = (int)ms.Position;
                    break;
                case "HIRC":
                    bnk.ReadHirc(r, ms, end);
                    break;
            }
            ms.Position = end;
        }
        return bnk.Version == 0 ? null : bnk;
    }

    private void ReadHirc(BinaryReader r, MemoryStream ms, long sectionEnd)
    {
        uint count = r.ReadUInt32();
        for (uint i = 0; i < count && ms.Position < sectionEnd; i++)
        {
            byte type = r.ReadByte();
            uint size = r.ReadUInt32();
            uint id = r.ReadUInt32();
            long end = ms.Position + size - 4;   // size includes the id
            if (end > sectionEnd) break;
            try
            {
                switch (type)
                {
                    case 2:   // Sound
                    {
                        r.ReadUInt32();                                     // plugin/unknown
                        _ = Version == 88 ? r.ReadUInt32() : r.ReadByte();  // stream type
                        uint wemId = r.ReadUInt32();
                        _sounds[id] = wemId;
                        break;
                    }
                    case 3:   // Action
                    {
                        r.ReadByte();                 // scope
                        byte actionType = r.ReadByte();
                        if (actionType != 25)         // 25 = set-switch (no direct target)
                            _actionTargets[id] = r.ReadUInt32();
                        break;
                    }
                    case 4:   // Event
                    {
                        uint n = Version == 58 ? r.ReadUInt32() : r.ReadByte();
                        var ids = new uint[n];
                        for (int k = 0; k < n; k++) ids[k] = r.ReadUInt32();
                        _events[id] = ids;
                        break;
                    }
                    case 5:   // RandomOrSequenceContainer
                    {
                        // The NodeBaseParams prefix varies subtly per bank version (v145 drifted from the
                        // classic layout), but the TAIL is stable: [u32 childCount][ids][u16 playlistCount]
                        // [{u32 id, u32 weight}] — so parse the children backwards from the end.
                        var payload = r.ReadBytes((int)(size - 4));
                        if (ParseRanSeqChildren(payload) is { } ids) _ranSeq[id] = ids;
                        break;
                    }
                    case 6:   // SwitchContainer
                    {
                        SkipBaseParams(r);
                        r.ReadByte();                                   // group type
                        if (Version <= 0x59) ms.Position += 3;
                        r.ReadUInt32();                                 // group id
                        ms.Position += 5;
                        uint n = r.ReadUInt32();
                        var ids = new uint[n];
                        for (int k = 0; k < n; k++) ids[k] = r.ReadUInt32();
                        _switch[id] = ids;
                        break;
                    }
                }
            }
            catch { /* object layout drifted: fall through to the size-based resync below */ }
            ms.Position = end;   // resync — object sizes are authoritative
        }
    }

    /// <summary>Backward tail parse of a RanSeq container payload: the playlist ([u16 count][{id,weight}*])
    /// sits at the very end, preceded by the child list ([u32 count][u32 ids]). Weights are validated
    /// (1..100000, default 50000) to reject false matches.</summary>
    private static uint[]? ParseRanSeqChildren(byte[] p)
    {
        for (int pc = 0; pc <= 64; pc++)
        {
            int pcPos = p.Length - pc * 8 - 2;
            if (pcPos < 4) break;
            if (System.BitConverter.ToUInt16(p, pcPos) != pc) continue;
            bool weightsOk = true;
            for (int k = 0; k < pc && weightsOk; k++)
            {
                uint w = System.BitConverter.ToUInt32(p, pcPos + 2 + k * 8 + 4);
                if (w is 0 or > 100_000) weightsOk = false;
            }
            if (!weightsOk) continue;
            for (int c = 0; c <= 256; c++)
            {
                int cPos = pcPos - c * 4 - 4;
                if (cPos < 0) break;
                if (System.BitConverter.ToUInt32(p, cPos) != (uint)c) continue;
                if (c == 0) return System.Array.Empty<uint>();
                var ids = new uint[c];
                bool sane = true;
                for (int k = 0; k < c; k++)
                {
                    ids[k] = System.BitConverter.ToUInt32(p, cPos + 4 + k * 4);
                    if (ids[k] == 0) sane = false;
                }
                if (sane) return ids;
            }
        }
        return null;
    }

    // ---- base-params skipping (needed to reach container child lists) — port of BNKHelper ----

    private void SkipBaseParams(BinaryReader r)
    {
        SkipFx(r);
        r.ReadUInt32(); r.ReadUInt32();                 // bus id, parent id
        r.BaseStream.Position += Version <= 89 ? 2 : 1;
        SkipInitParams(r);
        SkipPosParams(r);
        SkipAux(r);
        SkipStateGroups(r);
        SkipRtpc(r);
    }

    private void SkipFx(BinaryReader r)
    {
        r.BaseStream.Position += 1;
        byte fx = r.ReadByte();
        if (fx > 0) r.BaseStream.Position += 1 + fx * (Version <= 145 ? 7 : 6);
        if (Version > 136)
        {
            r.BaseStream.Position += 1;
            byte fx2 = r.ReadByte();
            r.BaseStream.Position += fx2 * 6;
        }
        if (Version > 89 && Version <= 145) r.BaseStream.Position += 1;
    }

    private static void SkipInitParams(BinaryReader r)
    {
        r.BaseStream.Position += r.ReadByte() * 5;
        r.BaseStream.Position += r.ReadByte() * 9;
    }

    private void SkipPosParams(BinaryReader r)
    {
        byte bits = r.ReadByte();
        bool hasPos = (bits & 1) != 0;
        bool has3d = false;
        int hasAutomation = 0;
        if (hasPos)
        {
            if (Version <= 89)
            {
                bool has2d = r.ReadBoolean(); has3d = r.ReadBoolean();
                if (has2d) r.BaseStream.Position += 1;
            }
            else has3d = (bits & 2) != 0;
        }
        if (hasPos && has3d)
        {
            if (Version <= 89)
            {
                hasAutomation = (r.ReadByte() & 3) != 1 ? 1 : 0;
                r.BaseStream.Position += 8;
            }
            else
            {
                hasAutomation = (bits >> 5) & 3;
                r.BaseStream.Position += 1;
            }
        }
        if (hasAutomation != 0)
        {
            r.BaseStream.Position += Version <= 89 ? 9 : 5;
            r.BaseStream.Position += 16 * r.ReadUInt32();
            r.BaseStream.Position += (Version <= 89 ? 16 : 20) * r.ReadUInt32();
        }
        else if (Version <= 89) r.BaseStream.Position += 1;
    }

    private void SkipAux(BinaryReader r)
    {
        bool hasAux = ((r.ReadByte() >> 3) & 1) != 0;
        if (hasAux) r.BaseStream.Position += 16;
        if (Version > 135) r.BaseStream.Position += 4;
    }

    private static void SkipStateGroups(BinaryReader r)
    {
        r.BaseStream.Position += 6;
        r.BaseStream.Position += 3 * r.ReadByte();
        int n = r.ReadByte();
        for (int i = 0; i < n; i++)
        {
            r.BaseStream.Position += 5;
            r.BaseStream.Position += 8 * r.ReadByte();
        }
    }

    private void SkipRtpc(BinaryReader r)
    {
        ushort n = r.ReadUInt16();
        for (int i = 0; i < n; i++)
        {
            r.BaseStream.Position += Version <= 89 ? 13 : 12;
            r.BaseStream.Position += 12 * r.ReadUInt16();
        }
    }
}
