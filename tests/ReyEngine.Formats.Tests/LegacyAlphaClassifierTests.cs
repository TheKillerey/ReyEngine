using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M528: an ordinary ported surface is alpha-tested only when its alpha is a real cutout mask.
///
/// <para>M338 gave every normal surface <c>DefaultEnv_Flat_AlphaTest</c> with a 0.35 cutoff. That is
/// right for a cutout and destructive for anything else, because legacy League art routinely packs
/// gloss into the diffuse alpha channel - so the test discards the surface instead of shaping it.
/// Measured on a real ported map: 86 textures, 14 genuine cutouts, 52 opaque, 20 gradient, and the
/// cutoff eats 40-72% of those 20. The user-visible result was ground with holes in it.</para>
///
/// <para>M338's own verification was structural - every generated binding kept its texture and render
/// state - so nothing in it could notice that the render state deletes the surface.</para>
/// </summary>
public sealed class LegacyAlphaClassifierTests
{
    /// <summary>An RGBA8 image whose alpha follows <paramref name="alpha"/> over <paramref name="count"/>
    /// pixels. Colour is irrelevant here; only the alpha channel is read.</summary>
    private static byte[] Image(int count, Func<int, byte> alpha)
    {
        var rgba = new byte[count * 4];
        for (int i = 0; i < count; i++) rgba[i * 4 + 3] = alpha(i);
        return rgba;
    }

    [Fact]
    public void AFullyOpaqueTextureHasNothingToTest()
    {
        Assert.Equal(LegacyAlphaKind.Opaque,
            LegacyAlphaClassifier.Classify(Image(1000, _ => 255)));

        // a handful of stray non-opaque pixels is still opaque - real art is never perfectly clean
        Assert.Equal(LegacyAlphaKind.Opaque,
            LegacyAlphaClassifier.Classify(Image(1000, i => i < 3 ? (byte)200 : (byte)255)));
    }

    [Fact]
    public void AMaskIsACutout()
    {
        // hard on/off with a thin anti-aliased edge, which is what a foliage mask looks like
        Assert.Equal(LegacyAlphaKind.Cutout, LegacyAlphaClassifier.Classify(
            Image(1000, i => i < 450 ? (byte)0 : i < 480 ? (byte)128 : (byte)255)));
    }

    [Fact]
    public void ASmoothRampIsAGradientAndMustNotBeAlphaTested()
    {
        // gloss packed into alpha: a broad spread with most pixels in the middle. This is the case that
        // was eating the ground.
        Assert.Equal(LegacyAlphaKind.Gradient,
            LegacyAlphaClassifier.Classify(Image(1000, i => (byte)(i * 255 / 1000))));
    }

    [Fact]
    public void TheDiscardedShareIsReportedAtTheCutoffThatWouldBeUsed()
    {
        // half the pixels sit below the porter's 0.35 (89/255) cutoff
        var image = Image(1000, i => i < 500 ? (byte)10 : (byte)255);
        Assert.Equal(0.5, LegacyAlphaClassifier.DiscardedShare(image, 0.35f), 3);
        Assert.Equal(0.0, LegacyAlphaClassifier.DiscardedShare(image, 0.0f), 3);
    }

    [Fact]
    public void AnEmptyImageIsTreatedAsOpaqueRatherThanCrashing()
    {
        Assert.Equal(LegacyAlphaKind.Opaque, LegacyAlphaClassifier.Classify(Array.Empty<byte>()));
        Assert.Equal(0.0, LegacyAlphaClassifier.DiscardedShare(Array.Empty<byte>(), 0.35f));
    }

    [Fact]
    public void TheTwoNormalShadersAreTheAlphaTestedOneAndThePlainOne()
    {
        // Riot's own shipped Map453 splits the same way round - 57 DefaultEnv_Flat against 31
        // DefaultEnv_Flat_AlphaTest - so the plain shader is the majority case, not an exception.
        Assert.Equal("Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest", LegacyMapPorter.NormalShader);
        Assert.Equal("Shaders/StaticMesh/DefaultEnv_Flat", LegacyMapPorter.SolidShader);
        Assert.NotEqual(LegacyMapPorter.NormalShader, LegacyMapPorter.SolidShader);
    }
}
