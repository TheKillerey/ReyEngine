using ReyEngine.Core.Hashing;

namespace ReyEngine.Core.Assets;

/// <summary>M819: one chunk an <see cref="IAssetOverlay"/> serves.</summary>
/// <param name="PathHash">The chunk's path hash.</param>
/// <param name="Path">The path the overlay itself knows the chunk by (the module's target, for LTK GameData), or null. The hash dictionary is asked first.</param>
/// <param name="Size">The size of the bytes the overlay serves.</param>
/// <param name="Identity">A cheap fingerprint of those bytes: it changes when the declarations, or the game state they were applied to, change. It names the asset in a cache key.</param>
public sealed record AssetOverlayEntry(ulong PathHash, string? Path, long Size, string Identity);

/// <summary>
/// M819: bytes served in place of what the mounts hold for a chunk. The seam LTK GameData comes through - <c>Core</c> knows nothing of projects, layers or declarations, only that something may answer a
/// read for a chunk before the mounts do, and that what it answers is read-only.
///
/// <para>An overlay is attached to <see cref="AssetMountService"/> (<see cref="AssetMountService.SetOverlay"/>) and merged into its index by <see cref="AssetMountService.Rebuild"/> and
/// <see cref="AssetMountService.RefreshOverlay"/>, so a served chunk is listed, found by <see cref="AssetMountService.TryGet"/> and read by <see cref="AssetMountService.Read"/> like any other - with an
/// <see cref="OverlayMount"/> as its source and the mounts that hold their own copy of it as shadowed ones. Every member may be called from any thread.</para>
///
/// <para>An overlay that is not ready serves nothing: <see cref="Entries"/> is empty and <see cref="TryRead"/> is false, so a read in the meantime answers what the mounts hold, and the owner
/// refreshes once the overlay is ready.</para>
/// </summary>
public interface IAssetOverlay
{
    /// <summary>What a source of this overlay is called where one is named ("LTK GameData").</summary>
    string Name { get; }

    /// <summary>The chunks the overlay serves now. Empty until it is ready. A snapshot: it does not change under the caller.</summary>
    IReadOnlyList<AssetOverlayEntry> Entries { get; }

    /// <summary>The bytes the overlay serves for <paramref name="pathHash"/>; false when it serves none (the chunk is not one it changes, or it is not ready yet).</summary>
    bool TryRead(ulong pathHash, out byte[] bytes);

    /// <summary>Whether the overlay's declarations name this chunk - changed or not. A chunk they name is one an edit must not be written back to: the declarations would apply to the edit as well.
    /// May answer true for a chunk <see cref="Entries"/> does not list. While <see cref="TargetsKnown"/> is false it answers only for the chunks it can name so far.</summary>
    bool IsTarget(ulong pathHash);

    /// <summary>Whether the overlay is still preparing what it serves: until it is done, <see cref="Entries"/> is empty and a read answers what the mounts hold, which is not final.</summary>
    bool IsPending => false;

    /// <summary>
    /// Whether <see cref="IsTarget"/> can say, for every chunk, if the declarations name it. False while the overlay has not yet bound them to what they apply to: some declarations name their chunks only
    /// once the game's index has been read, so a chunk <see cref="IsTarget"/> answers false for may still turn out to be one. A caller that guards a write must treat a bin as a target until this is true.
    /// </summary>
    bool TargetsKnown => true;
}

/// <summary>
/// M819: the read-only source of the chunks an <see cref="IAssetOverlay"/> serves - what <see cref="AssetMountService"/> lists and reads them from. It is a snapshot of the overlay's
/// <see cref="IAssetOverlay.Entries"/> taken when it was made. Not a mount of the project: it is never in <see cref="AssetMountService.Mounts"/>, so what walks the project's mounts (the
/// browser's project folders, Cleanup, the asset usage report, a build) never meets it.
/// </summary>
public sealed class OverlayMount : IAssetMount
{
    private readonly IAssetOverlay _overlay;
    private readonly IHashResolver? _resolver;
    private readonly Dictionary<ulong, AssetOverlayEntry> _entries = new();

    public OverlayMount(IAssetOverlay overlay, IHashResolver? resolver, IReadOnlyList<AssetOverlayEntry> entries)
    {
        _overlay = overlay;
        _resolver = resolver;
        foreach (var entry in entries) _entries[entry.PathHash] = entry;
    }

    public string Name => _overlay.Name;
    public string Location => _overlay.Name;
    public AssetSourceKind Kind => AssetSourceKind.LtkGameData;
    public bool IsEditable => false;

    /// <summary>The overlay's own fingerprint of the bytes it serves for a chunk, or null when it serves none.</summary>
    public string? IdentityOf(ulong pathHash) => _entries.TryGetValue(pathHash, out var entry) ? entry.Identity : null;

    public IEnumerable<MountedAsset> Enumerate()
    {
        foreach (var entry in _entries.Values) yield return Build(entry);
    }

    public bool Contains(ulong pathHash) => _entries.ContainsKey(pathHash);

    public MountedAsset? Get(ulong pathHash) => _entries.TryGetValue(pathHash, out var entry) ? Build(entry) : null;

    /// <exception cref="FileNotFoundException">The overlay serves nothing for the chunk (it was replaced, or it is not a chunk it changes).</exception>
    public byte[] Read(ulong pathHash)
    {
        if (_overlay.TryRead(pathHash, out var bytes)) return bytes;
        throw new FileNotFoundException($"{_overlay.Name} does not serve 0x{pathHash:x16}.");
    }

    public bool TryGetFilePath(ulong pathHash, out string filePath) { filePath = ""; return false; }

    public void Dispose() { }

    private MountedAsset Build(AssetOverlayEntry entry)
    {
        string path;
        bool resolved;
        // a spelling is listed only when it is a plain relative path: the module's own text, and the dictionary's, can come from a package, and become a file name in the browser's copy and export commands
        if (_resolver is not null && _resolver.TryGetPath(entry.PathHash, out var known) && AssetPathSafety.IsSafeRelativePath(known)) { path = known; resolved = true; }
        else if (entry.Path is { Length: > 0 } own && AssetPathSafety.IsSafeRelativePath(own)) { path = own; resolved = true; }
        else { path = $"0x{entry.PathHash:x16}.bin"; resolved = false; }
        return new MountedAsset
        {
            PathHash = entry.PathHash,
            VirtualPath = path,
            IsResolved = resolved,
            Type = AssetTypeDetector.FromPath(path),
            Size = entry.Size,
            Source = this,
        };
    }
}
