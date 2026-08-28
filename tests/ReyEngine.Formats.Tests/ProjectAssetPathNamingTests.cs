using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M590: a project's own asset paths must name themselves.
///
/// <para>Since 16.17 a material stores a texture as the HASH of its path, and Riot's dictionary only
/// knows Riot's files. A legacy port writes its textures to <c>assets/maps/legacyimport/…</c> — paths the
/// porter invents — so the editor showed the author a bare <c>0x…</c> with an unresolved warning for a
/// file the project had just created. Measured on a real ported map: of 112 chunk links, 21 were nameable
/// from Riot's dictionary and <b>91 only from the project itself</b>.</para>
///
/// <para>Both halves of the fix rest on one invariant — that the hash a project file is addressed by is
/// <c>WadPath(its relative path)</c>. That is what this pins; without it, registering paths would name
/// them wrongly, which is worse than not naming them.</para>
/// </summary>
public sealed class ProjectAssetPathNamingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-paths-" + Guid.NewGuid().ToString("n")[..8]);

    public ProjectAssetPathNamingTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private void Write(string rel, byte[]? bytes = null)
    {
        string p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes ?? new byte[] { 1, 2, 3, 4 });
    }

    [Fact]
    public void AProjectFileIsAddressedByTheHashOfItsRelativePath()
    {
        // The exact shape a legacy port produces.
        const string Rel = "assets/maps/legacyimport/map1/textures/orderpantheon.tex";
        Write(Rel);

        var found = WadPackService.EnumerateChunkFiles(_root).ToList();
        var entry = Assert.Single(found);
        Assert.Equal(HashAlgorithms.WadPath(Rel), entry.hash);
    }

    [Fact]
    public void CaseInTheFolderDoesNotChangeTheAddress()
    {
        // The porter writes lowercase; a material may have been authored with Riot's mixed case. Both
        // must address the same chunk or a texture silently points nowhere.
        Write("ASSETS/Maps/LegacyImport/Map1/Textures/OrderPantheon.tex");

        var entry = Assert.Single(WadPackService.EnumerateChunkFiles(_root));
        Assert.Equal(HashAlgorithms.WadPath("assets/maps/legacyimport/map1/textures/orderpantheon.tex"),
            entry.hash);
    }

    [Fact]
    public void AHashNamedLooseChunkKeepsItsOwnHash()
    {
        // cslol's convention for a chunk whose path is unknown: the NAME is the hash. Hashing that name
        // as though it were a path would invent a second, wrong address for it.
        const ulong Known = 0x84541dd835d2c63aUL;
        Write($"{Known:x16}.bin");

        var entry = Assert.Single(WadPackService.EnumerateChunkFiles(_root));
        Assert.Equal(Known, entry.hash);
        Assert.NotEqual(HashAlgorithms.WadPath($"{Known:x16}.bin"), entry.hash);
    }

    [Fact]
    public void EveryPathNamedFileRoundTripsToItsOwnPath()
    {
        string[] rels =
        {
            "assets/maps/legacyimport/map1/textures/a.tex",
            "assets/maps/legacyimport/map1/textures/b.tex",
            "data/maps/mapgeometry/map453/jade_container.materials.bin",
        };
        foreach (string r in rels) Write(r);

        // What RegisterProjectAssetPaths does: hash -> relative path. Every one must come back to itself.
        var index = new Dictionary<ulong, string>();
        foreach (var (hash, path) in WadPackService.EnumerateChunkFiles(_root))
        {
            string rel = Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
            index[hash] = rel;
        }

        foreach (string r in rels)
            Assert.Equal(r, index[HashAlgorithms.WadPath(r)]);
    }
}
