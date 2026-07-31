using System.Text;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M301: recovering chunk names from the archive's own .bin files.</summary>
public class WadPathRecoveryTests
{
    /// <summary>Wrap path strings in something that looks like a bin: "PROP" magic, strings separated by
    /// non-printable bytes the way a length-prefixed format naturally separates them.</summary>
    private static byte[] FakeBin(params string[] strings)
    {
        var b = new List<byte>(Encoding.ASCII.GetBytes("PROP"));
        foreach (var s in strings)
        {
            b.AddRange(new byte[] { 0x00, 0x01, 0x00 });
            b.AddRange(Encoding.ASCII.GetBytes(s));
        }
        b.AddRange(new byte[] { 0x00, 0x00 });
        return b.ToArray();
    }

    [Fact]
    public void RecoversTheHashOfAPathNamedInsideABin()
    {
        const string real = "assets/characters/custom/skins/base/particles/my_texture.dds";
        ulong hash = HashAlgorithms.WadPath(real);
        var bin = FakeBin("SomeClassName", real, "another/thing.tex");

        // 1 = the bin itself (name unknown, as a custom bin usually is), 2 = the texture it references.
        var found = WadPathRecovery.Recover(
            allChunks: new ulong[] { 1, hash },
            unknown: new HashSet<ulong> { hash },
            readChunk: h => h == 1 ? bin : null);

        Assert.Equal(real, Assert.Contains(hash, found));
    }

    [Fact]
    public void IgnoresChunksThatAreNotBins()
    {
        const string real = "assets/x/y.dds";
        ulong hash = HashAlgorithms.WadPath(real);
        // Same string, but the chunk is a DDS - scanning arbitrary binary for paths would invite noise.
        var notABin = new List<byte>(Encoding.ASCII.GetBytes("DDS "));
        notABin.AddRange(new byte[] { 0 });
        notABin.AddRange(Encoding.ASCII.GetBytes(real));

        var found = WadPathRecovery.Recover(
            new ulong[] { 1 }, new HashSet<ulong> { hash }, _ => notABin.ToArray());

        Assert.Empty(found);
    }

    [Fact]
    public void NeverNamesAChunkTheDatabaseAlreadyResolved()
    {
        const string real = "assets/x/y.dds";
        ulong hash = HashAlgorithms.WadPath(real);
        // The chunk is NOT in the unknown set, so recovery must leave it alone rather than racing the
        // database and possibly overwriting a correct name with a same-hashing variant.
        var found = WadPathRecovery.Recover(
            new ulong[] { 1 }, new HashSet<ulong>(), _ => FakeBin(real));

        Assert.Empty(found);
    }

    [Fact]
    public void NormalisesBackslashesBeforeHashing()
    {
        const string real = "assets/x/y.dds";
        ulong hash = HashAlgorithms.WadPath(real);
        // Bins are authored on Windows; the WAD key is always forward slash.
        var found = WadPathRecovery.Recover(
            new ulong[] { 1 }, new HashSet<ulong> { hash }, _ => FakeBin(@"assets\x\y.dds"));

        Assert.Equal(real, Assert.Contains(hash, found));
    }

    [Fact]
    public void SurvivesAnUnreadableChunk()
    {
        var found = WadPathRecovery.Recover(
            new ulong[] { 1, 2 }, new HashSet<ulong> { 3 },
            h => h == 1 ? throw new InvalidDataException("corrupt") : FakeBin("a/b.dds"));

        Assert.Empty(found);   // and, crucially, did not throw
    }
}
