using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ReyEngine.Core;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M790: runs once per test process, before any test in this assembly, and keeps what the process writes as a
/// side effect out of the repo.
///
/// <para>The shader catalogue cache is the write that bit (M784, M789). Every <c>new MainWindowViewModel()</c>
/// picks a shader environment in its constructor, picking one loads that install's catalogue, and after any Riot
/// patch the committed stamp no longer matches Global.wad - so the load rescanned and saved over
/// <c>data/shader_catalogs/&lt;env&gt;.json</c> in whichever checkout the test binary sits under. Here the cache
/// goes to a temporary folder of this process's own, deleted when it exits. The app is untouched: it still
/// writes the tracked folder (M687).</para>
/// </summary>
internal static class TestRunIsolation
{
    /// <summary>This process's shader catalogue cache. Not created here: the first save creates it, so a run
    /// that never builds an editor leaves nothing behind.</summary>
    public static string ShaderCatalogsDir { get; } =
        Path.Combine(Path.GetTempPath(), $"reyengine_shader_catalogs_{Guid.NewGuid():N}");

    /// <summary>The committed catalogues as this process found them before any test ran, path to SHA-256.</summary>
    private static Dictionary<string, string> _atStart = new(StringComparer.OrdinalIgnoreCase);

    [ModuleInitializer]
    internal static void Isolate()
    {
        // The redirect first: it is the part that protects the files, and nothing below may stop it.
        ReyPaths.RedirectShaderCatalogs(ShaderCatalogsDir);
        _atStart = HashCommittedCatalogues();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { if (Directory.Exists(ShaderCatalogsDir)) Directory.Delete(ShaderCatalogsDir, recursive: true); }
            catch { /* a load still writing at exit; it is under %TEMP% */ }
        };
    }

    /// <summary>The checkout the test binary was built in, found by its ReyEngine.slnx.</summary>
    public static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>Where committed catalogues live: the folder the app resolves from <see cref="ReyPaths.DataRoot"/>
    /// and this checkout's own. One folder, unless the binary was built under another <c>data/hashes</c>.</summary>
    public static IReadOnlyList<string> CommittedCatalogueDirs()
    {
        var dirs = new List<string> { Path.Combine(ReyPaths.DataRoot, "shader_catalogs") };
        if (RepoRoot() is { } root) dirs.Add(Path.Combine(root, "data", "shader_catalogs"));
        return dirs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every committed catalogue that is not byte-identical to how the process found it: rewritten,
    /// added or deleted since the start of the run - by any test, not only the one asking.</summary>
    public static List<string> ChangedSinceStart()
    {
        var now = HashCommittedCatalogues();
        return now.Keys.Union(_atStart.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(path => !now.TryGetValue(path, out var hash) || !_atStart.TryGetValue(path, out var before)
                           || hash != before)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Never throws: it runs inside the module initializer, where a throw fails every test.</summary>
    private static Dictionary<string, string> HashCommittedCatalogues()
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string dir in CommittedCatalogueDirs())
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.json"))
                {
                    try { hashes[file] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))); }
                    catch (Exception ex) { hashes[file] = "unreadable: " + ex.Message; }
                }
            }
        }
        catch { /* no checkout to protect */ }
        return hashes;
    }
}
