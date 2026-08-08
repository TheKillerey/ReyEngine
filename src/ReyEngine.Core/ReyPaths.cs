namespace ReyEngine.Core;

/// <summary>
/// Resolves the on-disk locations ReyEngine reads/writes. At dev time this walks up
/// from the executable to the repo root (the folder with the .sln) so it uses the
/// project's <c>data/</c> folder; otherwise it falls back to a local <c>data/</c>.
/// </summary>
public static class ReyPaths
{
    public static string DataRoot { get; } = ResolveDataRoot();

    public static string HashesDir => Path.Combine(DataRoot, "hashes");
    public static string CommunityDragonDir => Path.Combine(HashesDir, "communitydragon", "lol");
    public static string MergedCache => Path.Combine(HashesDir, "merged_hashes.cache");

    /// <summary>M367: the LeagueToolkit meta-class database. Downloaded and cached exactly like the
    /// CommunityDragon hashes above and gitignored for the same two reasons: it is third-party data this
    /// repo should not redistribute (lol-meta-classes ships no licence), and it is re-dumped every patch,
    /// so a committed copy would be stale the week after it landed.</summary>
    public static string MetaDir => Path.Combine(DataRoot, "meta");
    public static string MetaDbFile => Path.Combine(MetaDir, "meta.db.json");

    public static void EnsureHashDirs()
    {
        Directory.CreateDirectory(HashesDir);
        Directory.CreateDirectory(CommunityDragonDir);
    }

    public static void EnsureMetaDir() => Directory.CreateDirectory(MetaDir);

    private static string ResolveDataRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "hashes")) ||
                Directory.GetFiles(dir.FullName, "*.sln").Length > 0 ||
                Directory.GetFiles(dir.FullName, "*.slnx").Length > 0)
                return Path.Combine(dir.FullName, "data");
            dir = dir.Parent;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "data");
    }
}
