using System.Buffers.Binary;
using System.Text;

namespace ReyEngine.Formats.Audio;

/// <summary>What a .wem's RIFF header says about it (for display; decoding stays with vgmstream).</summary>
public sealed record WemInfo(int Channels, int SampleRate, string Codec)
{
    public static readonly WemInfo Unknown = new(0, 0, "?");
    public override string ToString() =>
        Channels == 0 ? Codec : $"{Codec} · {SampleRate / 1000.0:0.#} kHz · {(Channels == 1 ? "mono" : Channels == 2 ? "stereo" : $"{Channels} ch")}";
}

/// <summary>One editable media entry in a bank/pack.</summary>
public sealed class AudioBankEntry
{
    public uint Id { get; internal set; }
    public byte[] Data { get; internal set; } = Array.Empty<byte>();
    /// <summary>The id this entry had when the file was opened (0 for entries added since).</summary>
    public uint OriginalId { get; internal init; }
    public bool IsAdded { get; internal set; }
    public bool IsReplaced { get; internal set; }
    public bool IsRenamed => !IsAdded && OriginalId != Id;
    public int Size => Data.Length;
    public WemInfo Info => WemHeader.Read(Data);
}

/// <summary>
/// M137: an editable view over a Wwise media container — a SoundBank (.bnk with DIDX/DATA) or a
/// League wem pack (.wpk). Entries can be replaced, added, removed, re-identified and duplicated;
/// <see cref="Serialize"/> writes the container back with every other section preserved verbatim.
/// Wwise media is keyed purely by numeric id (there are no file names inside), so "rename" means
/// changing an entry's id — which is exactly what re-points it at a different Sound object.
/// </summary>
public sealed class AudioBankDocument
{
    private readonly BnkFile? _bnk;
    private readonly WpkFile? _wpk;
    private readonly List<AudioBankEntry> _entries = new();

    public string Path { get; }
    public bool IsBnk => _bnk is not null;
    public string Kind => IsBnk ? "SoundBank (.bnk)" : "Wem pack (.wpk)";
    public uint BankVersion => _bnk?.Version ?? 0;
    public uint BankId => _bnk?.BankId ?? 0;
    /// <summary>The bank's own event hierarchy, when it has one (audio banks usually don't —
    /// their events live in the sibling <c>*_events.bnk</c>).</summary>
    public BnkFile? Hirc => _bnk is { HasHirc: true } ? _bnk : null;

    public IReadOnlyList<AudioBankEntry> Entries => _entries;
    public bool IsDirty { get; private set; }

    /// <summary>M137: false when the media table doesn't describe the file's actual payload — the
    /// document then opens for inspection only and refuses to serialize. Seen on shipped banks whose
    /// bytes were mangled by CR→CRLF expansion (every 0x0D gained a stray 0x0A), which shifts every
    /// DIDX offset: rewriting one of those would turn a recoverable file into a definitely-broken one.</summary>
    public bool IsEditable { get; private set; } = true;
    public string? ReadOnlyReason { get; private set; }

    private AudioBankDocument(string path, BnkFile? bnk, WpkFile? wpk)
    {
        Path = path; _bnk = bnk; _wpk = wpk;
    }

    /// <summary>Open .bnk/.wpk bytes for editing. Null when the data is neither (or a bank with no media).</summary>
    public static AudioBankDocument? Parse(byte[] data, string path)
    {
        if (WpkFile.Parse(data) is { } wpk)
        {
            var doc = new AudioBankDocument(path, null, wpk);
            foreach (var id in wpk.Order)
                doc._entries.Add(new AudioBankEntry { Id = id, OriginalId = id, Data = wpk.GetWemData(id) ?? Array.Empty<byte>() });
            return doc;
        }
        if (BnkFile.Parse(data) is { } bnk)
        {
            var doc = new AudioBankDocument(path, bnk, null);
            foreach (var id in bnk.DidxOrder())
                doc._entries.Add(new AudioBankEntry { Id = id, OriginalId = id, Data = bnk.GetWemData(id) ?? Array.Empty<byte>() });
            doc.ValidateMedia();
            return doc;   // a media-less events bank opens too (empty list; entries can be added)
        }
        return null;
    }

    /// <summary>Every entry the media table claims must actually be a RIFF payload of that length.
    /// Anything else means we do not understand this file's layout — mark it read-only.</summary>
    private void ValidateMedia()
    {
        int bad = 0;
        foreach (var e in _entries)
            if (e.Data.Length < 12
                || e.Data[0] != (byte)'R' || e.Data[1] != (byte)'I' || e.Data[2] != (byte)'F' || e.Data[3] != (byte)'F')
                bad++;
        if (bad == 0) return;
        IsEditable = false;
        ReadOnlyReason = $"{bad} of {_entries.Count} media entries don't point at valid audio — this file's "
            + "table doesn't match its data (a known corruption in a few shipped banks). Editing is disabled "
            + "so a rewrite can't make it worse.";
    }

    // ---- editing ----

    public AudioBankEntry? Find(uint id) => _entries.FirstOrDefault(e => e.Id == id);

    /// <summary>An id no entry uses yet (for Add/Duplicate). Wwise ids are arbitrary u32s.</summary>
    public uint SuggestFreeId()
    {
        uint candidate = _entries.Count > 0 ? _entries.Max(e => e.Id) + 1 : 1000000000u;
        while (_entries.Any(e => e.Id == candidate)) candidate++;
        return candidate;
    }

    public bool Replace(uint id, byte[] data)
    {
        if (Find(id) is not { } e) return false;
        e.Data = data; e.IsReplaced = true; IsDirty = true;
        return true;
    }

    /// <summary>Add a new media entry. Fails when the id is taken (ids are the only key Wwise has).</summary>
    public bool Add(uint id, byte[] data, out string? error)
    {
        error = null;
        if (Find(id) is not null) { error = $"Id {id} already exists in this file."; return false; }
        _entries.Add(new AudioBankEntry { Id = id, OriginalId = 0, Data = data, IsAdded = true });
        IsDirty = true;
        return true;
    }

    public bool Remove(uint id)
    {
        if (Find(id) is not { } e) return false;
        _entries.Remove(e); IsDirty = true;
        return true;
    }

    /// <summary>Change an entry's id — the Wwise equivalent of renaming a file.</summary>
    public bool ChangeId(uint oldId, uint newId, out string? error)
    {
        error = null;
        if (oldId == newId) return true;
        if (Find(oldId) is not { } e) { error = $"Id {oldId} not found."; return false; }
        if (Find(newId) is not null) { error = $"Id {newId} is already used in this file."; return false; }
        e.Id = newId; IsDirty = true;
        return true;
    }

    /// <summary>Copy an entry's audio into a new entry (paste-into-same-file / duplicate).</summary>
    public bool Duplicate(uint id, uint newId, out string? error)
    {
        error = null;
        if (Find(id) is not { } e) { error = $"Id {id} not found."; return false; }
        return Add(newId, (byte[])e.Data.Clone(), out error);
    }

    /// <summary>Write the container back. BNK keeps every non-media section verbatim; WPK is rebuilt.</summary>
    public byte[] Serialize()
    {
        if (!IsEditable)
            throw new InvalidOperationException(ReadOnlyReason ?? "This file's media layout isn't understood — refusing to rewrite it.");
        var list = _entries.Select(e => (e.Id, e.Data)).ToList();
        return IsBnk ? _bnk!.RebuildEntries(list) : WpkFile.RebuildEntries(list);
    }

    /// <summary>Round-trip check used before saving: the serialized file must re-open with the same ids.</summary>
    public bool Validate(byte[] serialized, out string? error)
    {
        error = null;
        var reopened = Parse(serialized, Path);
        if (reopened is null) { error = "the rebuilt file did not re-open"; return false; }
        var mine = _entries.Select(e => e.Id).OrderBy(x => x).ToList();
        var theirs = reopened.Entries.Select(e => e.Id).OrderBy(x => x).ToList();
        if (!mine.SequenceEqual(theirs)) { error = $"entry ids changed ({mine.Count} vs {theirs.Count})"; return false; }
        foreach (var e in _entries)
            if (reopened.Find(e.Id) is not { } o || o.Size != e.Size)
            { error = $"entry {e.Id} did not round-trip"; return false; }
        return true;
    }

    public void MarkSaved() => IsDirty = false;
}

/// <summary>Minimal RIFF/WAVE header read for display (Wwise wems are RIFF containers).</summary>
public static class WemHeader
{
    public static WemInfo Read(byte[] d)
    {
        try
        {
            if (d.Length < 44 || Encoding.ASCII.GetString(d, 0, 4) != "RIFF") return WemInfo.Unknown;
            int pos = 12;
            while (pos + 8 <= d.Length)
            {
                string id = Encoding.ASCII.GetString(d, pos, 4);
                int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos + 4));
                if (id == "fmt ")
                {
                    int p = pos + 8;
                    if (p + 16 > d.Length) break;
                    ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p));
                    ushort ch = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 2));
                    int rate = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4));
                    string codec = tag switch
                    {
                        0x0001 => "PCM",
                        0x0002 => "ADPCM",
                        0x0166 => "XMA2",
                        0xFFFF or 0xFFFE => "Vorbis",
                        0x3039 => "Opus",
                        _ => $"fmt 0x{tag:x4}",
                    };
                    return new WemInfo(ch, rate, codec);
                }
                pos += 8 + size + (size & 1);
            }
        }
        catch { /* display-only: never throw on a odd header */ }
        return WemInfo.Unknown;
    }
}
