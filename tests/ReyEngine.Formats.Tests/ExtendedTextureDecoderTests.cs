using System.Buffers.Binary;

using ReyEngine.Core.Decoding;

namespace ReyEngine.Formats.Tests;

/// <summary>M329: Riot extended TEX format 14 is BC5/ATI2 and stores mip chains smallest first.</summary>
public class ExtendedTextureDecoderTests
{
    [Fact]
    public void Decodes_format_14_as_two_channel_bc5()
    {
        byte[] tex = MakeTex(4, 4, mipmaps: false, Bc5Block(31, 219));

        TextureImage image = TextureDecoder.Decode(tex);

        Assert.Equal(4, image.Width);
        Assert.Equal(4, image.Height);
        for (int i = 0; i < image.Rgba.Length; i += 4)
        {
            Assert.Equal(31, image.Rgba[i]);
            Assert.Equal(219, image.Rgba[i + 1]);
            Assert.Equal(255, image.Rgba[i + 3]);
        }
    }

    [Fact]
    public void Selects_full_resolution_level_from_smallest_first_mip_chain()
    {
        byte[] smallest = Bc5Block(11, 12); // 1x1
        byte[] two = Bc5Block(21, 22);      // 2x2
        byte[] four = Bc5Block(31, 32);     // 4x4
        byte[] eight = Enumerable.Range(0, 4).SelectMany(_ => Bc5Block(201, 202)).ToArray();
        byte[] tex = MakeTex(8, 8, mipmaps: true, smallest, two, four, eight);

        TextureImage image = TextureDecoder.Decode(tex);

        Assert.Equal(8, image.Width);
        Assert.Equal(8, image.Height);
        Assert.Equal(201, image.Rgba[0]);
        Assert.Equal(202, image.Rgba[1]);
    }

    [Fact]
    public void Rejects_truncated_format_14_payload_with_an_actionable_error()
    {
        byte[] tex = MakeTex(8, 8, mipmaps: false, Bc5Block(1, 2));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => TextureDecoder.Decode(tex));

        Assert.Contains("BC5", error.Message);
        Assert.Contains("truncated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] MakeTex(int width, int height, bool mipmaps, params byte[][] payloads)
    {
        byte[] payload = payloads.SelectMany(static p => p).ToArray();
        byte[] tex = new byte[12 + payload.Length];
        tex[0] = (byte)'T'; tex[1] = (byte)'E'; tex[2] = (byte)'X';
        BinaryPrimitives.WriteUInt16LittleEndian(tex.AsSpan(4, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(tex.AsSpan(6, 2), (ushort)height);
        tex[8] = 1;
        tex[9] = 14;
        tex[11] = (byte)(mipmaps ? 1 : 0);
        payload.CopyTo(tex, 12);
        return tex;
    }

    private static byte[] Bc5Block(byte red, byte green)
    {
        var block = new byte[16];
        block[0] = block[1] = red;
        block[8] = block[9] = green;
        return block;
    }
}
