namespace ReyEngine.Core.Assets;

/// <summary>
/// The virtual file system: aggregates project-override, project-WAD/-folder and Riot-reference
/// mounts into one asset view. Mounts are consulted in priority order (override &gt; loose project folder
/// &gt; project WAD &gt;
/// Riot reference); the highest-priority mount that holds a path hash wins, and lower ones are
/// recorded as shadowed sources (conflicts). Reading always goes to the winning mount.
/// </summary>
public sealed class AssetMountService : IDisposable
{
    private readonly List<IAssetMount> _mounts = new();

    // M812 (round three): the index is replaced as a whole, never edited in place. Rebuild() used to Clear() it and refill it
    // while a colour scan or a map thumbnail was reading through the same service from a worker - Sync Hashes does exactly that to
    // the LIVE service - and a Dictionary read during a Clear or an Add can miss a key that is there, so a file read as absent
    // and a scan came out short without saying so. Readers take the reference once per call; Rebuild publishes the finished one.
    private Dictionary<ulong, MountedAsset> _index = new();

    // M812: the fallback list is copy-on-write. A Character window opened from the game folder adds a champion's WAD
    // here (AddFallback) on the UI thread while a worker - the CHROMA card's colour scan, a map thumbnail - is reading
    // through this service; a List<T> enumerated during an Add throws or hands out a half-written slot. Readers take the
    // array once per call and never see it change; the one writer publishes a longer copy. Adding is rare, reading is not.
    private readonly object _fallbackGate = new();
    private IAssetMount[] _fallback = Array.Empty<IAssetMount>();

    public IReadOnlyList<IAssetMount> Mounts => _mounts;
    public IReadOnlyList<IAssetMount> Fallback => Volatile.Read(ref _fallback);
    public IReadOnlyCollection<MountedAsset> Assets => Volatile.Read(ref _index).Values;
    public int Count => Volatile.Read(ref _index).Count;

    /// <summary>Add a mount. Order matters: add highest-priority (overrides) first.</summary>
    public void Add(IAssetMount mount) => _mounts.Add(mount);

    /// <summary>
    /// Add a read-only fallback source (e.g. an original Riot game WAD). Fallbacks are consulted only
    /// when no mounted source holds a hash, and are NOT enumerated into the asset tree — so missing
    /// skin bins / textures resolve from the original game files without bloating the browser.
    /// Safe to call while another thread is reading (see the field remarks).
    /// </summary>
    public void AddFallback(IAssetMount mount)
    {
        lock (_fallbackGate)
        {
            var current = _fallback;
            var grown = new IAssetMount[current.Length + 1];
            Array.Copy(current, grown, current.Length);
            grown[^1] = mount;
            Volatile.Write(ref _fallback, grown);
        }
    }

    /// <summary>Dispose every mount and empty the service. A mount whose Dispose throws is not allowed to leave the ones after it
    /// open (M812): each is disposed on its own and the failure of one is not rethrown - it is released with the process, as the
    /// thumbnail service treats the readers it retires.</summary>
    public void Clear()
    {
        IAssetMount[] fallback;
        lock (_fallbackGate)
        {
            fallback = _fallback;
            Volatile.Write(ref _fallback, Array.Empty<IAssetMount>());
        }
        foreach (var m in _mounts.ToArray()) DisposeQuietly(m);
        foreach (var m in fallback) DisposeQuietly(m);
        _mounts.Clear();
        Volatile.Write(ref _index, new Dictionary<ulong, MountedAsset>());
    }

    private static void DisposeQuietly(IAssetMount mount)
    {
        try { mount.Dispose(); } catch { /* a mount that will not close must not keep the others open */ }
    }

    /// <summary>Rebuild the unified index. Mounts are merged in the order they were added.</summary>
    public void Rebuild()
    {
        var index = new Dictionary<ulong, MountedAsset>();
        // Sort by priority (override first), preserving add-order within a kind.
        var ordered = _mounts
            .Select((m, i) => (m, i))
            .OrderBy(t => (int)t.m.Kind).ThenBy(t => t.i)
            .Select(t => t.m);

        foreach (var mount in ordered)
        {
            foreach (var asset in mount.Enumerate())
            {
                if (index.TryGetValue(asset.PathHash, out var existing))
                {
                    existing.AllSources.Add(mount);
                    // Keep a resolved path/type if the winner didn't have one.
                    if (!existing.IsResolved && asset.IsResolved)
                    {
                        existing.VirtualPath = asset.VirtualPath;
                        existing.IsResolved = true;
                        existing.Type = asset.Type;
                    }
                }
                else
                {
                    asset.AllSources.Add(mount);
                    index[asset.PathHash] = asset;
                }
            }
        }
        Volatile.Write(ref _index, index);   // published whole: a reader sees the old index or this one, never half of either
    }

    public bool TryGet(ulong pathHash, out MountedAsset asset)
    {
        if (Volatile.Read(ref _index).TryGetValue(pathHash, out asset!)) return true;
        foreach (var f in Volatile.Read(ref _fallback))
            if (f.Get(pathHash) is { } a) { asset = a; return true; }
        asset = null!;
        return false;
    }

    public byte[]? Read(ulong pathHash)
    {
        if (Volatile.Read(ref _index).TryGetValue(pathHash, out var a)) return a.Source.Read(pathHash);
        foreach (var f in Volatile.Read(ref _fallback))
            if (f.Contains(pathHash)) return f.Read(pathHash);
        return null;
    }

    /// <summary>Does any mount or fallback hold this hash?</summary>
    public bool Has(ulong pathHash)
    {
        if (Volatile.Read(ref _index).ContainsKey(pathHash)) return true;
        foreach (var f in Volatile.Read(ref _fallback))
            if (f.Contains(pathHash)) return true;
        return false;
    }

    /// <summary>Read ONLY from the game fallback (bypassing project/override) — for when a mod's copy
    /// of a shared file is broken and we want the original game version.</summary>
    public byte[]? ReadFallback(ulong pathHash)
    {
        foreach (var f in Volatile.Read(ref _fallback))
            if (f.Contains(pathHash)) return f.Read(pathHash);
        return null;
    }

    /// <summary>All mounts (in priority order) that hold a given hash — for "show all sources".</summary>
    public IReadOnlyList<IAssetMount> SourcesOf(ulong pathHash) =>
        Volatile.Read(ref _index).TryGetValue(pathHash, out var a) ? a.AllSources : Array.Empty<IAssetMount>();

    /// <summary>M74: the real on-disk file behind an asset's WINNING source, when that source is
    /// file-backed (folder/override mount). False for archive-backed or fallback-only assets —
    /// Explorer-style file operations (rename/delete/move) need a standalone file.</summary>
    public bool TryGetFilePath(ulong pathHash, out string filePath, out IAssetMount mount)
    {
        if (Volatile.Read(ref _index).TryGetValue(pathHash, out var a) && a.Source.TryGetFilePath(pathHash, out filePath!))
        {
            mount = a.Source;
            return true;
        }
        filePath = ""; mount = null!;
        return false;
    }

    public void Dispose() => Clear();
}
