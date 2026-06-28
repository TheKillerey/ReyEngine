using ReyEngine.Core.Wad;

namespace ReyEngine.Core.Hashing;

/// <summary>
/// Façade the app holds: wraps the active <see cref="HashDatabase"/> and re-resolves an
/// already-open archive's entries (0x… → readable paths) when the dictionary changes.
/// </summary>
public sealed class WadPathResolver : IHashResolver
{
    public HashDatabase Database { get; private set; }

    public WadPathResolver(HashDatabase database) => Database = database;

    public void Swap(HashDatabase database) => Database = database;

    public bool TryGetPath(ulong hash, out string path) => Database.TryGetPath(hash, out path);
    public string ResolvePath(ulong hash) => Database.ResolvePath(hash);

    /// <summary>Re-resolve every entry in the archive in place. Returns the resolved count.</summary>
    public int RefreshArchive(WadArchive archive) => archive.ReResolve(this);
}
