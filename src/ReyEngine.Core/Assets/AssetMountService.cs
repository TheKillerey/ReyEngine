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
    private readonly List<IAssetMount> _fallback = new();
    private readonly Dictionary<ulong, MountedAsset> _index = new();

    public IReadOnlyList<IAssetMount> Mounts => _mounts;
    public IReadOnlyList<IAssetMount> Fallback => _fallback;
    public IReadOnlyCollection<MountedAsset> Assets => _index.Values;
    public int Count => _index.Count;

    /// <summary>Add a mount. Order matters: add highest-priority (overrides) first.</summary>
    public void Add(IAssetMount mount) => _mounts.Add(mount);

    /// <summary>
    /// Add a read-only fallback source (e.g. an original Riot game WAD). Fallbacks are consulted only
    /// when no mounted source holds a hash, and are NOT enumerated into the asset tree — so missing
    /// skin bins / textures resolve from the original game files without bloating the browser.
    /// </summary>
    public void AddFallback(IAssetMount mount) => _fallback.Add(mount);

    public void Clear()
    {
        foreach (var m in _mounts) m.Dispose();
        foreach (var m in _fallback) m.Dispose();
        _mounts.Clear();
        _fallback.Clear();
        _index.Clear();
    }

    /// <summary>Rebuild the unified index. Mounts are merged in the order they were added.</summary>
    public void Rebuild()
    {
        _index.Clear();
        // Sort by priority (override first), preserving add-order within a kind.
        var ordered = _mounts
            .Select((m, i) => (m, i))
            .OrderBy(t => (int)t.m.Kind).ThenBy(t => t.i)
            .Select(t => t.m);

        foreach (var mount in ordered)
        {
            foreach (var asset in mount.Enumerate())
            {
                if (_index.TryGetValue(asset.PathHash, out var existing))
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
                    _index[asset.PathHash] = asset;
                }
            }
        }
    }

    public bool TryGet(ulong pathHash, out MountedAsset asset)
    {
        if (_index.TryGetValue(pathHash, out asset!)) return true;
        foreach (var f in _fallback)
            if (f.Get(pathHash) is { } a) { asset = a; return true; }
        asset = null!;
        return false;
    }

    public byte[]? Read(ulong pathHash)
    {
        if (_index.TryGetValue(pathHash, out var a)) return a.Source.Read(pathHash);
        foreach (var f in _fallback)
            if (f.Contains(pathHash)) return f.Read(pathHash);
        return null;
    }

    /// <summary>Does any mount or fallback hold this hash?</summary>
    public bool Has(ulong pathHash) => _index.ContainsKey(pathHash) || _fallback.Any(f => f.Contains(pathHash));

    /// <summary>Read ONLY from the game fallback (bypassing project/override) — for when a mod's copy
    /// of a shared file is broken and we want the original game version.</summary>
    public byte[]? ReadFallback(ulong pathHash)
    {
        foreach (var f in _fallback)
            if (f.Contains(pathHash)) return f.Read(pathHash);
        return null;
    }

    /// <summary>All mounts (in priority order) that hold a given hash — for "show all sources".</summary>
    public IReadOnlyList<IAssetMount> SourcesOf(ulong pathHash) =>
        _index.TryGetValue(pathHash, out var a) ? a.AllSources : Array.Empty<IAssetMount>();

    /// <summary>M74: the real on-disk file behind an asset's WINNING source, when that source is
    /// file-backed (folder/override mount). False for archive-backed or fallback-only assets —
    /// Explorer-style file operations (rename/delete/move) need a standalone file.</summary>
    public bool TryGetFilePath(ulong pathHash, out string filePath, out IAssetMount mount)
    {
        if (_index.TryGetValue(pathHash, out var a) && a.Source.TryGetFilePath(pathHash, out filePath!))
        {
            mount = a.Source;
            return true;
        }
        filePath = ""; mount = null!;
        return false;
    }

    public void Dispose() => Clear();
}
