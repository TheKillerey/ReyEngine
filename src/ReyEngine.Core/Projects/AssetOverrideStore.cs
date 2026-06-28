namespace ReyEngine.Core.Projects;

/// <summary>In-memory index of a project's overrides for fast status lookup by path hash.</summary>
public sealed class AssetOverrideStore
{
    private readonly Dictionary<ulong, ProjectAssetOverride> _map = new();

    public int Count => _map.Count;
    public IReadOnlyCollection<ProjectAssetOverride> All => _map.Values;

    public bool Has(ulong pathHash) => _map.ContainsKey(pathHash);
    public bool TryGet(ulong pathHash, out ProjectAssetOverride ov) => _map.TryGetValue(pathHash, out ov!);
    public void Set(ProjectAssetOverride ov) => _map[ov.PathHash] = ov;
    public void Remove(ulong pathHash) => _map.Remove(pathHash);
    public void Clear() => _map.Clear();

    public void LoadFrom(ReyProject project)
    {
        _map.Clear();
        foreach (var ov in project.Overrides) _map[ov.PathHash] = ov;
    }

    public void SaveTo(ReyProject project) => project.Overrides = _map.Values.ToList();
}
