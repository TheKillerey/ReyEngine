using System.Buffers.Binary;
using ReyEngine.Core.Decoding;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M594: a block-compressed texture must sit on the 4x4 block grid, or the client will not load the map.
///
/// <para>BC1/BC3 store 4x4 blocks, and D3D refuses to create a texture whose TOP mip is not a multiple of
/// 4 in both dimensions. The client reports it as <c>ALE-D0D00020</c> / <c>E_INVALIDARG</c>,
/// "A texture could not be created", and stops the game during load — with no indication of WHICH texture.</para>
///
/// <para>A legacy map port produced exactly one: <c>assets/Legacy/black.tex</c> at <b>1x1 BC3</b>, 28
/// bytes, referenced by the map's materials bin. That single file kept the whole map from loading.</para>
///
/// <para>The convention is Riot's own, measured rather than invented: of <b>9,955</b> block-compressed
/// .tex in Map453, <b>zero</b> are off the block grid, and their tiny utility textures are authored at
/// 4x4 (<c>ASSETS/Shared/Materials/white.tex</c> is 4x4, 36 bytes).</para>
/// </summary>
public sealed class BlockCompressedTextureSizeTests
{
    private static TextureImage Solid(int w, int h, byte r = 0, byte g = 0, byte b = 0, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a; }
        return new TextureImage(w, h, px);
    }

    private static (int Width, int Height, byte Format) Header(byte[] tex)
    {
        Assert.True(tex.Length >= 12);
        Assert.Equal((byte)'T', tex[0]);
        return (BinaryPrimitives.ReadUInt16LittleEndian(tex.AsSpan(4)),
                BinaryPrimitives.ReadUInt16LittleEndian(tex.AsSpan(6)),
                tex[9]);
    }

    [Fact]
    public void TheOneByOneCaseThatStoppedTheGameIsGrownToFourByFour()
    {
        // Exactly what the legacy port produced for black.tex.
        var tex = TexWriter.Write(Solid(1, 1), TexFormat.Bc3, mipmaps: false);
        var (w, h, fmt) = Header(tex);

        Assert.Equal(4, w);
        Assert.Equal(4, h);
        Assert.Equal((byte)TexFormat.Bc3, fmt);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(30, 30)]
    [InlineData(62, 100)]
    [InlineData(1, 64)]
    public void AnyOffGridSizeIsGrownOntoTheBlockGrid(int w, int h)
    {
        var tex = TexWriter.Write(Solid(w, h), TexFormat.Bc3);
        var (outW, outH, _) = Header(tex);

        Assert.True(outW % 4 == 0 && outH % 4 == 0, $"{w}x{h} became {outW}x{outH}");
        Assert.True(outW >= w && outH >= h, "growing must never shrink the image");
        Assert.True(outW >= 4 && outH >= 4);
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(64, 64)]
    [InlineData(256, 128)]
    public void AnAlreadyValidSizeIsLeftExactlyAlone(int w, int h)
    {
        var tex = TexWriter.Write(Solid(w, h), TexFormat.Bc3);
        var (outW, outH, _) = Header(tex);
        Assert.Equal(w, outW);
        Assert.Equal(h, outH);
    }

    [Fact]
    public void GrowingAOneByOneKeepsItsColour()
    {
        // The case in the wild is a solid colour, so growing must be invisible - not merely legal.
        var grown = TexWriter.PadToBlockMultiple(Solid(1, 1, r: 12, g: 34, b: 56, a: 200));
        Assert.Equal(4, grown.Width);
        Assert.Equal(4, grown.Height);
        for (int i = 0; i < grown.Width * grown.Height; i++)
        {
            Assert.Equal(12, grown.Rgba[i * 4]);
            Assert.Equal(34, grown.Rgba[i * 4 + 1]);
            Assert.Equal(56, grown.Rgba[i * 4 + 2]);
            Assert.Equal(200, grown.Rgba[i * 4 + 3]);
        }
    }

    [Fact]
    public void PaddingIsIdentityForAValidImage()
    {
        var image = Solid(8, 8);
        Assert.Same(image, TexWriter.PadToBlockMultiple(image));
    }

    [Fact]
    public void WrappingRefusesADdsThatIsOffTheBlockGrid()
    {
        // The second door into the same bug: wrapping copies blocks through untouched, so a malformed
        // legacy DDS would become a .tex the client cannot create. Refusing sends the caller to re-encode.
        var dds = new byte[256];
        dds[0] = (byte)'D'; dds[1] = (byte)'D'; dds[2] = (byte)'S'; dds[3] = (byte)' ';
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(12), 30);   // height
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(16), 30);   // width
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(28), 1);    // mips
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(84), 0x35545844); // DXT5

        Assert.False(TexWriter.TryWrapDds(dds, out _));
    }

    [Fact]
    public void TheBlockGridRuleIsStatedOnceAndAgrees()
    {
        Assert.True(TexWriter.IsBlockCompressed(TexFormat.Bc1));
        Assert.True(TexWriter.IsBlockCompressed(TexFormat.Bc3));
        Assert.True(TexWriter.FitsBlockGrid(4, 4));
        Assert.False(TexWriter.FitsBlockGrid(1, 1));
        Assert.False(TexWriter.FitsBlockGrid(30, 32));
        Assert.False(TexWriter.FitsBlockGrid(0, 4));
    }
}
