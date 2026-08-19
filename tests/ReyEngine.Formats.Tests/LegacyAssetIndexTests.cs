using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M531: resolving legacy asset references, which lie about themselves three ways at once.
///
/// <para>Across the 16 particle systems Map2 uses, all 49 asset references end in <c>.tga</c> and NOT
/// ONE of them exists as a .tga - every one shipped as .dds, 30 under DATA/Particles and 19 under
/// DATA/Shared/Particles. So a resolver that trusts the extension finds nothing, and one that searches a
/// single folder finds at most 30 of 49.</para>
/// </summary>
public sealed class LegacyAssetIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-assetindex-" + Guid.NewGuid().ToString("N"));

    private string Touch(string relative)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Array.Empty<byte>());
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ATgaReferenceResolvesToTheDdsThatActuallyShipped()
    {
        // This one line is the difference between 49 textures and 0.
        Touch("Particles/flame.dds");
        var index = new LegacyAssetIndex(Path.Combine(_root, "Particles"));

        Assert.True(index.TryResolve("flame.tga", out string file));
        Assert.EndsWith("flame.dds", file);
    }

    [Fact]
    public void CaseAndPathSeparatorsDoNotMatter()
    {
        Touch("Particles/FlameThing.dds");
        var index = new LegacyAssetIndex(Path.Combine(_root, "Particles"));

        Assert.True(index.TryResolve(@"DATA\Particles\FLAMETHING.TGA", out _));
        Assert.True(index.TryResolve("data/particles/flamething.dds", out _));
    }

    [Fact]
    public void SeveralRootsAreSearchedAndTheFirstWins()
    {
        // DATA/Particles and DATA/Shared/Particles both hold art, and 19 of the 49 are only in the second.
        Touch("Particles/shared_only.dds");
        Touch("Shared/Particles/shared_only.dds");
        Touch("Shared/Particles/elsewhere.dds");

        var index = new LegacyAssetIndex(new[]
        {
            Path.Combine(_root, "Particles"),
            Path.Combine(_root, "Shared", "Particles"),
        });

        Assert.True(index.TryResolve("elsewhere.tga", out _));
        Assert.True(index.TryResolve("shared_only.tga", out string first));
        Assert.DoesNotContain("Shared", first);   // the earlier root wins
    }

    [Fact]
    public void MeshReferencesResolveOnlyWhenTheParticleExtensionsAreAsked()
    {
        // Mesh emitters point at .sco/.scb (insphere.sco, mundopaper1.sco). The map porter's image-only
        // set must not start matching them by accident, and the particle set must.
        Touch("Particles/insphere.sco");

        Assert.False(new LegacyAssetIndex(Path.Combine(_root, "Particles")).TryResolve("insphere.sco", out _));
        Assert.True(new LegacyAssetIndex(Path.Combine(_root, "Particles"),
            LegacyAssetIndex.ParticleExtensions).TryResolve("insphere.sco", out _));
    }

    [Fact]
    public void AMissingRootIsNotAnError()
    {
        // A legacy tree may simply not have DATA/Shared/Particles; that must degrade, not throw.
        var index = new LegacyAssetIndex(new[] { Path.Combine(_root, "nope"), Path.Combine(_root, "also-nope") });

        Assert.Equal(0, index.Count);
        Assert.False(index.TryResolve("anything.tga", out _));
    }

    [Fact]
    public void AnEmptyOrJunkReferenceResolvesToNothing()
    {
        Touch("Particles/real.dds");
        var index = new LegacyAssetIndex(Path.Combine(_root, "Particles"));

        Assert.False(index.TryResolve("", out _));
        Assert.False(index.TryResolve("   ", out _));
        Assert.False(index.TryResolve("not-there.tga", out _));
    }
}
