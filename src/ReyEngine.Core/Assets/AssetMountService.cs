namespace ReyEngine.Core.Assets;

/// <summary>
/// The virtual file system: aggregates project-override, project-WAD/-folder and Riot-reference
/// mounts into one asset view. Mounts are consulted in priority order (override &gt; project &gt;
/// Riot reference); the highest-priority mount that holds a path hash wins, and lower ones are
/// recorded as shadowed sources (conflicts). Reading always goes to the winning mount.
/// </summary>
public sealed class AssetMountService : IDisposable
{
    private readonly List<IAssetMount> _mounts = new();
    private readonly Dictionary<ulong, MountedAsset> _index = new();

    public IReadOnlyList<IAssetMount> Mounts => _mounts;
    public IReadOnlyCollection<MountedAsset> Assets => _index.Values;
    public int Count => _index.Count;

    /// <summary>Add a mount. Order matters: add highest-priority (overrides) first.</summary>
    public void Add(IAssetMount mount) => _mounts.Add(mount);

    public void Clear()
    {
        foreach (var m in _mounts) m.Dispose();
        _mounts.Clear();
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

    public bool TryGet(ulong pathHash, out MountedAsset asset) => _index.TryGetValue(pathHash, out asset!);

    public byte[]? Read(ulong pathHash) => _index.TryGetValue(pathHash, out var a) ? a.Source.Read(pathHash) : null;

    /// <summary>All mounts (in priority order) that hold a given hash — for "show all sources".</summary>
    public IReadOnlyList<IAssetMount> SourcesOf(ulong pathHash) =>
        _index.TryGetValue(pathHash, out var a) ? a.AllSources : Array.Empty<IAssetMount>();

    public void Dispose() => Clear();
}
