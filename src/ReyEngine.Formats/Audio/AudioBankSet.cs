namespace ReyEngine.Formats.Audio;

/// <summary>
/// M56: a loaded set of Wwise banks for one map/skin — the events bank (HIRC) plus any number of
/// wem sources (audio .bnk DIDX/DATA and/or .wpk packs). Resolves an event NAME to playable wem bytes:
/// FNV-1(name) → Event → Action(s) → Sound / RanSeqContainer / SwitchContainer (recursive) → wem ids.
/// </summary>
/// <summary>M57: a wem source keyed by its wad origin so an edit can be rebuilt + saved to the exact
/// file. Exactly one of Bnk/Wpk is set.</summary>
public sealed record AudioSource(ulong PathHash, string Path, BnkFile? Bnk, WpkFile? Wpk)
{
    public bool Owns(uint wemId) => Bnk?.Wems.ContainsKey(wemId) ?? Wpk!.Wems.ContainsKey(wemId);
}

public sealed class AudioBankSet
{
    private readonly List<BnkFile> _hircBanks = new();
    private readonly List<AudioSource> _sources = new();   // embedded bnk + wpk media sources

    public int EventCount => _hircBanks.Sum(b => b.Events.Count);
    public int WemCount => _sources.Sum(s => s.Bnk?.Wems.Count ?? s.Wpk!.Wems.Count);
    public bool IsEmpty => _hircBanks.Count == 0 && WemCount == 0;

    public void AddBank(BnkFile bnk, ulong pathHash = 0, string path = "")
    {
        if (bnk.HasHirc) _hircBanks.Add(bnk);
        if (bnk.HasEmbeddedWems) _sources.Add(new AudioSource(pathHash, path, bnk, null));
    }

    public void AddPack(WpkFile wpk, ulong pathHash = 0, string path = "")
        => _sources.Add(new AudioSource(pathHash, path, null, wpk));

    /// <summary>Find the source file that holds this wem (for a rebuild-and-save).</summary>
    public AudioSource? SourceOf(uint wemId) => _sources.FirstOrDefault(s => s.Owns(wemId));

    /// <summary>M57: rebuild the file owning <paramref name="wemId"/> with the wem replaced. Returns the
    /// origin path hash + new bank/pack bytes to store as an override; null when the wem isn't owned here.</summary>
    public (ulong PathHash, string Path, byte[] Bytes)? ReplaceWem(uint wemId, byte[] newData)
    {
        var src = SourceOf(wemId);
        if (src is null) return null;
        var map = new Dictionary<uint, byte[]> { [wemId] = newData };
        byte[]? bytes = src.Bnk is { } b ? b.Rebuild(map) : src.Wpk!.Rebuild(map);
        return bytes is null ? null : (src.PathHash, src.Path, bytes);
    }

    /// <summary>All wem ids an event resolves to (empty when the event isn't in the loaded banks).</summary>
    public IReadOnlyList<uint> ResolveEvent(string eventName) => ResolveEvent(WwiseHash.Fnv1(eventName));

    public IReadOnlyList<uint> ResolveEvent(uint eventId)
    {
        var wems = new List<uint>();
        var visited = new HashSet<uint>();
        foreach (var bank in _hircBanks)
        {
            if (!bank.Events.TryGetValue(eventId, out var actionIds)) continue;
            foreach (var actionId in actionIds)
                if (bank.ActionTargets.TryGetValue(actionId, out var target))
                    CollectWems(bank, target, wems, visited);
        }
        return wems;
    }

    private static void CollectWems(BnkFile bank, uint objectId, List<uint> wems, HashSet<uint> visited)
    {
        if (!visited.Add(objectId)) return;
        if (bank.Sounds.TryGetValue(objectId, out var wemId)) { wems.Add(wemId); return; }
        if (bank.RanSeqContainers.TryGetValue(objectId, out var children))
            foreach (var c in children) CollectWems(bank, c, wems, visited);
        if (bank.SwitchContainers.TryGetValue(objectId, out var switchChildren))
            foreach (var c in switchChildren) CollectWems(bank, c, wems, visited);
    }

    /// <summary>Raw wem bytes from any loaded source (embedded bnk DATA first, then wpk packs).</summary>
    public byte[]? GetWemData(uint wemId)
    {
        foreach (var s in _sources)
        {
            // A BNK source that does not own this id is still a valid source entry; do not
            // fall through to its null WPK half. Continue to the next BNK/WPK source instead.
            var d = s.Bnk is { } b ? b.GetWemData(wemId) : s.Wpk?.GetWemData(wemId);
            if (d is not null) return d;
        }
        return null;
    }

    /// <summary>Every event id in the loaded HIRC banks (for browsing when names are unknown).</summary>
    public IEnumerable<uint> AllEventIds() => _hircBanks.SelectMany(b => b.Events.Keys);
}
