using ReyEngine.Core.Hashing;

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

    // M819: where each mount ranks among the others OF ITS KIND (lower first; 0 for every mount unless the caller said otherwise). The order of _mounts stays the order they were added in
    // - the browser lists them so - and only the winner of a hash (Rebuild) follows the rank, which is how a project's layers shadow one another the way LTK installs them.
    private readonly List<int> _precedence = new();

    // M812 (round three): the index is replaced as a whole, never edited in place. Rebuild() used to Clear() it and refill it
    // while a colour scan or a map thumbnail was reading through the same service from a worker - Sync Hashes does exactly that to
    // the LIVE service - and a Dictionary read during a Clear or an Add can miss a key that is there, so a file read as absent
    // and a scan came out short without saying so. Readers take the reference once per call; Rebuild publishes the finished one.
    private Dictionary<ulong, MountedAsset> _index = new();

    // M819: the index as the mounts alone make it. With no overlay it IS _index (the same object, so a project that has none reads exactly as it always did); with one, _index is this
    // plus what the overlay serves, and the overlay's readiness is a Publish() away rather than a Rebuild() of every mount. Touched on the thread that rebuilds, never by a reader.
    private Dictionary<ulong, MountedAsset> _baseIndex = new();
    private IAssetOverlay? _overlay;
    private IHashResolver? _overlayResolver;

    // Whether what the overlay serves is in the index: set by Publish once the overlay is no longer pending - after the index it merged is written, never before - and cleared by SetOverlay. A read that finds an overlay
    // attached and this still false is answered by the mounts alone, so it is told to ReadWhileOverlayPending. The overlay's own IsPending is not that: between the moment it becomes ready and the moment its owner merges it
    // (the continuation on the UI thread) it says it is not pending, and a read in that window is not final either.
    private bool _overlayPublished;

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
    public void Add(IAssetMount mount) => Add(mount, 0);

    /// <summary>
    /// M819: add a mount with a rank among the mounts of its kind: of two that hold a hash, the lower rank wins, and mounts of one rank win in the order they were added (so every
    /// call that passes no rank behaves as before). A project of layers adds each folder with the rank its layer's turn in LTK's apply order gives it - the layer applied last first.
    /// </summary>
    public void Add(IAssetMount mount, int precedence)
    {
        _mounts.Add(mount);
        _precedence.Add(precedence);
    }

    /// <summary>
    /// M819: attach an overlay (or none), whose chunks <see cref="Rebuild"/> and <see cref="RefreshOverlay"/> merge into the index. It serves nothing until one of them has run, and not
    /// before the overlay says it is ready.
    /// </summary>
    /// <param name="overlay">What serves the chunks, or null for none.</param>
    /// <param name="resolver">Names the chunks it lists, when the overlay does not know them itself.</param>
    public void SetOverlay(IAssetOverlay? overlay, IHashResolver? resolver = null)
    {
        Volatile.Write(ref _overlayPublished, false);   // before the overlay: a reader that finds the new one finds it unpublished
        Volatile.Write(ref _overlayResolver, resolver);
        Volatile.Write(ref _overlay, overlay);
    }

    /// <summary>The overlay attached, or null.</summary>
    public IAssetOverlay? Overlay => Volatile.Read(ref _overlay);

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
        _precedence.Clear();
        SetOverlay(null);
        ReadWhileOverlayPending = null;
        _baseIndex = new Dictionary<ulong, MountedAsset>();
        Volatile.Write(ref _index, _baseIndex);
    }

    private static void DisposeQuietly(IAssetMount mount)
    {
        try { mount.Dispose(); } catch { /* a mount that will not close must not keep the others open */ }
    }

    /// <summary>Rebuild the unified index. Mounts are merged in the order they were added (M819: within a kind, by rank first), then the overlay's chunks are merged over them.</summary>
    public void Rebuild()
    {
        var index = new Dictionary<ulong, MountedAsset>();
        // Sort by priority (override first), preserving add-order within a kind.
        var ordered = _mounts
            .Select((m, i) => (m, i))
            .OrderBy(t => (int)t.m.Kind).ThenBy(t => _precedence[t.i]).ThenBy(t => t.i)
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
        _baseIndex = index;
        Publish();
    }

    /// <summary>
    /// M819: merge what the overlay serves NOW into the index again, without enumerating the mounts - the overlay became ready, or changed. Call on the thread that calls
    /// <see cref="Rebuild"/>. With no overlay it republishes the index the mounts made.
    /// </summary>
    public void RefreshOverlay() => Publish();

    /// <summary>Publishes the index: the one the mounts made, with the overlay's chunks over it. A chunk a mount also holds is listed with the overlay as its source and that mount shadowed
    /// (a copy of the asset - the mount's own object, which readers may hold, is never changed); a chunk no mount holds is listed on its own.</summary>
    private void Publish()
    {
        var baseIndex = _baseIndex;
        var overlay = Volatile.Read(ref _overlay);
        // asked BEFORE the entries are read: an overlay that is not pending now has its final entries, so what is merged below is what it serves. One that becomes ready after this was asked is merged as it was
        // (nothing, or what it had) and stays unpublished, until the refresh that follows its readiness.
        bool ready = overlay is not null && !overlay.IsPending;
        var entries = overlay?.Entries;
        if (overlay is null || entries is null || entries.Count == 0)
        {
            Volatile.Write(ref _index, baseIndex);   // published whole: a reader sees the old index or this one, never half of either
            MarkPublished(overlay, ready);
            return;
        }

        var source = new OverlayMount(overlay, Volatile.Read(ref _overlayResolver), entries);
        var merged = new Dictionary<ulong, MountedAsset>(baseIndex);
        foreach (var served in source.Enumerate())
        {
            if (baseIndex.TryGetValue(served.PathHash, out var held))
            {
                // the chunk keeps the name and type the mounts resolved; what is served is the bytes
                var over = new MountedAsset
                {
                    PathHash = held.PathHash,
                    VirtualPath = held.IsResolved ? held.VirtualPath : served.VirtualPath,
                    IsResolved = held.IsResolved || served.IsResolved,
                    Type = held.IsResolved ? held.Type : served.Type,
                    Size = served.Size,
                    Source = source,
                };
                over.AllSources.Add(source);
                over.AllSources.AddRange(held.AllSources);
                merged[served.PathHash] = over;
            }
            else
            {
                served.AllSources.Add(source);
                merged[served.PathHash] = served;
            }
        }
        Volatile.Write(ref _index, merged);
        MarkPublished(overlay, ready);
    }

    /// <summary>The flag follows the index and never goes before it: a reader that finds it set finds the merged index. It is set only for the overlay that was merged, and only once that overlay is ready.</summary>
    private void MarkPublished(IAssetOverlay? overlay, bool ready)
    {
        if (ready && ReferenceEquals(Volatile.Read(ref _overlay), overlay)) Volatile.Write(ref _overlayPublished, true);
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
        if (ReadWhileOverlayPending is { } told && t_quietReads == 0 && Volatile.Read(ref _overlay) is not null && !Volatile.Read(ref _overlayPublished)) told(pathHash);   // M819
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

    /// <summary>
    /// M819: the bytes of a chunk as the PROJECT and the game hold them, never as the overlay serves them: the winning mount's copy, or the fallbacks' when no mount holds it. What a caller
    /// that writes bytes back into the project reads, so that what it saves is the project's own file and not the declarations' result (saving that would have LTK apply the declarations twice).
    /// Without an overlay this is <see cref="Read"/>.
    /// </summary>
    public byte[]? ReadRaw(ulong pathHash)
    {
        if (Volatile.Read(ref _index).TryGetValue(pathHash, out var a))
        {
            if (a.Source is not OverlayMount) return a.Source.Read(pathHash);
            foreach (var shadowed in a.AllSources)
                if (shadowed is not OverlayMount) return shadowed.Read(pathHash);
        }
        foreach (var f in Volatile.Read(ref _fallback))
            if (f.Contains(pathHash)) return f.Read(pathHash);
        return null;
    }

    /// <summary>M819: whether a read of this chunk answers what the overlay serves.</summary>
    public bool IsOverlaid(ulong pathHash) =>
        Volatile.Read(ref _index).TryGetValue(pathHash, out var a) && a.Source is OverlayMount;

    /// <summary>M819: whether the overlay's declarations name this chunk - served or not. See <see cref="IAssetOverlay.IsTarget"/>.</summary>
    public bool IsOverlayTarget(ulong pathHash) => Volatile.Read(ref _overlay)?.IsTarget(pathHash) ?? false;

    /// <summary>M819: whether <see cref="IsOverlayTarget"/> can answer for every chunk (see <see cref="IAssetOverlay.TargetsKnown"/>). True when there is no overlay: there is nothing it could still turn out to name.</summary>
    public bool OverlayTargetsKnown => Volatile.Read(ref _overlay)?.TargetsKnown ?? true;

    /// <summary>
    /// M819: told, on the thread that reads, of each chunk <see cref="Read"/> answers while an overlay is attached and what it serves is not yet in the index: it is still preparing (<see cref="IAssetOverlay.IsPending"/>), or it
    /// has become ready and its owner has not merged it yet (<see cref="RefreshOverlay"/>). Such a read answered what the mounts hold, which is not what the overlay serves, so whoever kept the bytes - an editor, a cache -
    /// may have to be refreshed when it is published. Null for a project with no overlay: nothing is checked, nothing is told.
    /// </summary>
    public Action<ulong>? ReadWhileOverlayPending { get; set; }

    [ThreadStatic] private static int t_quietReads;

    /// <summary>The reads made on this thread until the scope is disposed are not told to <see cref="ReadWhileOverlayPending"/>: for a reader whose product is made again when the overlay is ready (the browser's tree).</summary>
    public static QuietReadScope QuietReads()
    {
        t_quietReads++;
        return default;
    }

    public readonly struct QuietReadScope : IDisposable
    {
        public void Dispose() => t_quietReads--;
    }

    /// <summary>M819: the overlay's own fingerprint of what it serves for this chunk, or null when it serves none.</summary>
    public string? OverlayIdentityOf(ulong pathHash) =>
        Volatile.Read(ref _index).TryGetValue(pathHash, out var a) && a.Source is OverlayMount overlaid ? overlaid.IdentityOf(pathHash) : null;

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
