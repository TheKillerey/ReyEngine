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

    /// <summary>M495: Mimir's .hashdb tables and the manifest they were published with. Gitignored for the
    /// same reasons as the CommunityDragon lists: third-party data this repo should not redistribute, and
    /// re-generated per patch, so a committed copy would be stale immediately.</summary>
    public static string MimirDir => Path.Combine(HashesDir, "mimir");
    public static string MimirManifestFile => Path.Combine(MimirDir, "manifest.json");

    /// <summary>M367: the LeagueToolkit meta-class database. Downloaded and cached exactly like the
    /// CommunityDragon hashes above and gitignored for the same two reasons: it is third-party data this
    /// repo should not redistribute (lol-meta-classes ships no licence), and it is re-dumped every patch,
    /// so a committed copy would be stale the week after it landed.</summary>
    public static string MetaDir => Path.Combine(DataRoot, "meta");
    public static string MetaDbFile => Path.Combine(MetaDir, "meta.db.json");

    /// <summary>M103/M475: the shader catalogue cache, one <c>&lt;environment&gt;.json</c> per install. The Material
    /// Editor serves a file while its stamp matches that install's Global.wad and otherwise rescans and writes it
    /// back. The Live and PBE files are committed (M687), so in the app this is deliberately the tracked folder.</summary>
    public static string ShaderCatalogsDir => _shaderCatalogsDir ?? Path.Combine(DataRoot, "shader_catalogs");

    private static string? _shaderCatalogsDir;

    /// <summary>M790: keep the shader catalogue cache somewhere else for the rest of this process. Exists so a
    /// test run never rewrites the committed catalogues: every headless MainWindowViewModel loads one, and after
    /// any Riot patch the committed stamp is stale, so the load rescans and saves over the repo's file (M784,
    /// M789). The test assembly calls this before its first test.</summary>
    public static void RedirectShaderCatalogs(string directory) => _shaderCatalogsDir = directory;

    public static void EnsureHashDirs()
    {
        Directory.CreateDirectory(HashesDir);
        Directory.CreateDirectory(CommunityDragonDir);
    }

    public static void EnsureMimirDir() => Directory.CreateDirectory(MimirDir);

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
