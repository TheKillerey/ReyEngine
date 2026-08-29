using System;
using ReyEngine.Core.Decoding;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M391: the import settings layer. The round-trip assertions matter most — PredictBytes drives the
/// dialog's size preview, and a preview that disagrees with the file actually written is worse than no
/// preview, so every prediction here is checked against a real encode rather than against itself.
/// </summary>
public class TexEncodeOptionsTests
{
    /// <param name="alpha">255 = fully opaque, &lt;128 = punch-through.</param>
    private static TextureImage Img(int w, int h, byte alpha = 255)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 4] = (byte)(i * 7); px[i * 4 + 1] = (byte)(i * 13); px[i * 4 + 2] = (byte)(i * 29);
            px[i * 4 + 3] = alpha;
        }
        return new TextureImage(w, h, px);
    }

    // ---- format choice ----

    [Fact]
    public void AutoPicksBc1WhenThereIsNoTransparency()
        => Assert.Equal(TexFormat.Bc1, TexEncodeOptions.Default.Resolve(Img(16, 16)));

    /// <summary>BC1 carries at most one bit of alpha, so a graded alpha must not land there.</summary>
    [Fact]
    public void AutoPicksBc3WhenPixelsAreTransparent()
        => Assert.Equal(TexFormat.Bc3, TexEncodeOptions.Default.Resolve(Img(16, 16, alpha: 10)));

    [Theory]
    [InlineData(TexFormatChoice.Bc1, TexFormat.Bc1)]
    [InlineData(TexFormatChoice.Bc3, TexFormat.Bc3)]
    public void AnExplicitChoiceOverridesTheAlphaHeuristic(TexFormatChoice choice, TexFormat expected)
    {
        // transparent image, so Auto would say BC3 - the explicit pick must win either way
        var o = new TexEncodeOptions { Format = choice };
        Assert.Equal(expected, o.Resolve(Img(16, 16, alpha: 10)));
    }

    // ---- the survey's conclusions, pinned ----

    [Fact]
    public void MipmapsAreOnByDefault()
        => Assert.True(TexEncodeOptions.Default.Mipmaps);

    /// <summary>7,620 shipped BC1 textures are non-power-of-two, so NPOT must encode, not be "corrected".
    /// 4x4 blocks are the real constraint and the writer already pads to them.</summary>
    [Theory]
    [InlineData(24, 40)]
    [InlineData(100, 60)]
    [InlineData(17, 5)]     // not even a multiple of 4
    [InlineData(4, 4)]      // the smallest whole block
    public void NonPowerOfTwoSizesEncodeRatherThanBeingRejected(int w, int h)
    {
        var tex = TexEncodeOptions.Default.Encode(Img(w, h));
        Assert.True(tex.Length > 12);
        Assert.Equal((byte)'T', tex[0]);
        // M594: a block-compressed image is grown onto the 4x4 grid first - D3D cannot create one that
        // is off it (a 1x1 BC3 stopped a map from loading). So the written size is the input ROUNDED UP,
        // never smaller and never off-grid.
        int outW = tex[4] | (tex[5] << 8), outH = tex[6] | (tex[7] << 8);
        Assert.Equal(Math.Max(4, (w + 3) / 4 * 4), outW);
        Assert.Equal(Math.Max(4, (h + 3) / 4 * 4), outH);
        Assert.True(outW >= w && outH >= h);
    }

    // ---- prediction vs reality ----

    [Theory]
    [InlineData(64, 64, true)]
    [InlineData(64, 64, false)]
    [InlineData(256, 128, true)]
    [InlineData(24, 40, true)]      // NPOT, mipped
    [InlineData(17, 5, true)]       // not a multiple of 4, mipped
    [InlineData(1, 1, true)]
    public void PredictedSizeMatchesTheBytesActuallyWritten(int w, int h, bool mips)
    {
        foreach (var choice in new[] { TexFormatChoice.Bc1, TexFormatChoice.Bc3 })
        {
            var o = new TexEncodeOptions { Format = choice, Mipmaps = mips };
            var img = Img(w, h);
            Assert.Equal(o.Encode(img).LongLength, o.PredictBytes(img));
        }
    }

    [Fact]
    public void Bc1IsHalfTheSizeOfBc3ForTheSameImage()
    {
        var img = Img(64, 64);
        long bc1 = new TexEncodeOptions { Format = TexFormatChoice.Bc1 }.PredictBytes(img);
        long bc3 = new TexEncodeOptions { Format = TexFormatChoice.Bc3 }.PredictBytes(img);
        Assert.Equal(12 + (bc1 - 12) * 2, bc3);   // header is shared, payload doubles
    }

    [Fact]
    public void MipmapsCostRoughlyAThirdMore()
    {
        var img = Img(256, 256);
        long flat = new TexEncodeOptions { Format = TexFormatChoice.Bc3, Mipmaps = false }.PredictBytes(img);
        long mipped = new TexEncodeOptions { Format = TexFormatChoice.Bc3, Mipmaps = true }.PredictBytes(img);
        Assert.True(mipped > flat);
        Assert.InRange(mipped / (double)flat, 1.3, 1.4);
    }

    // ---- header round-trip ----

    [Theory]
    [InlineData(TexFormatChoice.Bc1, TexFormat.Bc1)]
    [InlineData(TexFormatChoice.Bc3, TexFormat.Bc3)]
    public void TheWrittenFormatByteIsReadBack(TexFormatChoice choice, TexFormat expected)
    {
        var tex = new TexEncodeOptions { Format = choice }.Encode(Img(32, 32));
        Assert.Equal(expected, TexWriter.DetectFormat(tex));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void TheMipFlagReflectsTheSetting(bool mips, int expectedBit)
    {
        var tex = new TexEncodeOptions { Mipmaps = mips }.Encode(Img(32, 32));
        Assert.Equal(expectedBit, tex[11] & 1);
    }

    // ---- validation ----

    [Fact]
    public void AValidImagePassesValidation()
        => Assert.Null(TexEncodeOptions.Validate(Img(8, 8)));

    /// <summary>Width and height are u16 in the header, so anything larger would wrap to a wrong size
    /// instead of failing. Caught up front rather than written wrong.</summary>
    [Fact]
    public void OversizedImagesAreRejectedBecauseTheHeaderIs16Bit()
    {
        var bogus = new TextureImage(70000, 4, new byte[16]);
        Assert.Contains("16-bit", TexEncodeOptions.Validate(bogus));
    }

    [Fact]
    public void AShortPixelBufferIsRejected()
    {
        var bogus = new TextureImage(64, 64, new byte[16]);
        Assert.Contains("needs", TexEncodeOptions.Validate(bogus));
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(8, 0)]
    public void EmptyImagesAreRejected(int w, int h)
        => Assert.Equal("image has no pixels", TexEncodeOptions.Validate(new TextureImage(w, h, Array.Empty<byte>())));

    [Fact]
    public void NullIsRejectedRatherThanThrowing()
        => Assert.Equal("no image", TexEncodeOptions.Validate(null));
}
