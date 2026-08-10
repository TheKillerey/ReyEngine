using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M417. A project FOLDER file is what ships. The packer stages folders and an override may not clobber
/// a path a folder provides (M126/M131), so an edit written to the override store for an asset the
/// project already ships is silently dropped at package time - the save succeeds, the bytes are on disk,
/// and the exported mod is unchanged. That is how deleting or moving a particle appeared to do nothing.
///
/// <para><b>What this pins and what it does not.</b> The save paths themselves live in
/// <c>MainWindowViewModel</c>, which has no test project, so the choice of destination cannot be asserted
/// here. What is pinned is the RULE that makes the choice matter: a file in a project folder is packed
/// under exactly the hash the override store would have used, so the two genuinely collide and the folder
/// genuinely wins. If that ever stopped being true the fix would quietly stop working, because
/// <c>TryWriteToProjectFile</c> would never match and every edit would fall back to an override again.</para>
/// </summary>
public class ProjectFileWinsOverOverrideTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rey-m417-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void AProjectFolderFileIsPackedUnderTheHashTheOverrideStoreWouldUse()
    {
        string folder = TempDir();
        try
        {
            const string rel = "data/maps/mapgeometry/map453/jade_container.materials.bin";
            string full = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            byte[] projectBytes = BuildBin("PROJECT");
            File.WriteAllBytes(full, projectBytes);

            // The packer's view of "which chunk is this file"...
            var enumerated = WadPackService.EnumerateChunkFiles(folder).ToList();
            var (hash, path) = Assert.Single(enumerated);
            Assert.Equal(full, path);

            // ...must be the same hash the override store names its file by, or they never collide and
            // the whole precedence rule is moot.
            Assert.Equal(HashAlgorithms.WadPath(rel), hash);

            string outWad = Path.Combine(folder, "out.wad.client");
            var report = WadPackService.Pack(folder, outWad, null, default, knownTypesOnly: true);
            Assert.True(report.Success);

            using var wad = WadArchive.Open(outWad);
            Assert.True(wad.TryGetEntry(HashAlgorithms.WadPath(rel), out _));
            Assert.Equal(projectBytes, wad.Extract(HashAlgorithms.WadPath(rel)));
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch { } }
    }

    /// <summary>The packed chunk carries the PROJECT file's bytes, not some other copy - stated explicitly
    /// because "the edit was saved somewhere" was exactly the failure mode.</summary>
    [Fact]
    public void EditingTheProjectFileChangesWhatGetsPacked()
    {
        string folder = TempDir();
        try
        {
            const string rel = "data/test/asset.bin";
            string full = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, BuildBin("BEFORE"));

            string outWad = Path.Combine(folder, "before.wad.client");
            Assert.True(WadPackService.Pack(folder, outWad, null, default, knownTypesOnly: true).Success);

            // the save path writes the project file in place - this is what M417 made it do
            File.WriteAllBytes(full, BuildBin("AFTER"));
            string outWad2 = Path.Combine(folder, "after.wad.client");
            Assert.True(WadPackService.Pack(folder, outWad2, null, default, knownTypesOnly: true).Success);

            using var wad = WadArchive.Open(outWad2);
            Assert.Equal(BuildBin("AFTER"), wad.Extract(HashAlgorithms.WadPath(rel)));
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch { } }
    }

    /// <summary>A minimal valid PROP bin, so the packer's known-type filter accepts it.</summary>
    private static byte[] BuildBin(string marker)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(new[] { 'P', 'R', 'O', 'P' });
        w.Write(3u);                      // version
        w.Write(0u);                      // dependencies
        w.Write(0u);                      // objects
        w.Write(System.Text.Encoding.ASCII.GetBytes(marker));   // makes the two versions differ
        w.Flush();
        return ms.ToArray();
    }
}
