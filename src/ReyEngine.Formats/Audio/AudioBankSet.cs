namespace ReyEngine.Formats.Audio;

/// <summary>
/// M56: a loaded set of Wwise banks for one map/skin — the events bank (HIRC) plus any number of
/// wem sources (audio .bnk DIDX/DATA and/or .wpk packs). Resolves an event NAME to playable wem bytes:
/// FNV-1(name) → Event → Action(s) → Sound / RanSeqContainer / SwitchContainer (recursive) → wem ids.
/// </summary>
public sealed class AudioBankSet
{
    private readonly List<BnkFile> _hircBanks = new();
    private readonly List<BnkFile> _embeddedSources = new();
    private readonly List<WpkFile> _wpkSources = new();

    public int EventCount => _hircBanks.Sum(b => b.Events.Count);
    public int WemCount => _embeddedSources.Sum(b => b.Wems.Count) + _wpkSources.Sum(w => w.Wems.Count);
    public bool IsEmpty => _hircBanks.Count == 0 && WemCount == 0;

    public void AddBank(BnkFile bnk)
    {
        if (bnk.HasHirc) _hircBanks.Add(bnk);
        if (bnk.HasEmbeddedWems) _embeddedSources.Add(bnk);
    }

    public void AddPack(WpkFile wpk) => _wpkSources.Add(wpk);

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
        foreach (var b in _embeddedSources)
            if (b.GetWemData(wemId) is { } d) return d;
        foreach (var w in _wpkSources)
            if (w.GetWemData(wemId) is { } d) return d;
        return null;
    }

    /// <summary>Every event id in the loaded HIRC banks (for browsing when names are unknown).</summary>
    public IEnumerable<uint> AllEventIds() => _hircBanks.SelectMany(b => b.Events.Keys);
}
