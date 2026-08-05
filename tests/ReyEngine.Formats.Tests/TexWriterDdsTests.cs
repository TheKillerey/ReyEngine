using System.Buffers.Binary;
using ReyEngine.Core.Decoding;

namespace ReyEngine.Formats.Tests;

public sealed class TexWriterDdsTests
{
    [Fact]
    public void WrapsFullDxt1MipChainAsSmallestFirstBc1Tex()
    {
        byte[] dds = MakeDds(8, 8, "DXT1", new[] { 32, 8, 8, 8 });

        Assert.True(TexWriter.TryWrapDds(dds, out byte[] tex));

        Assert.Equal(new byte[] { (byte)'T', (byte)'E', (byte)'X', 0 }, tex[..4]);
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(tex.AsSpan(4, 2)));
        Assert.Equal((byte)TexFormat.Bc1, tex[9]);
        Assert.Equal(1, tex[11]);
        Assert.All(tex.AsSpan(12, 8).ToArray(), value => Assert.Equal(0x13, value));
        Assert.All(tex.AsSpan(20, 8).ToArray(), value => Assert.Equal(0x12, value));
        Assert.All(tex.AsSpan(28, 8).ToArray(), value => Assert.Equal(0x11, value));
        Assert.All(tex.AsSpan(36, 32).ToArray(), value => Assert.Equal(0x10, value));
    }

    [Fact]
    public void WrapsSingleDxt5LevelAndRejectsPartialMipChains()
    {
        Assert.True(TexWriter.TryWrapDds(MakeDds(4, 4, "DXT5", new[] { 16 }), out byte[] tex));
        Assert.Equal((byte)TexFormat.Bc3, tex[9]);
        Assert.Equal(0, tex[11]);
        Assert.All(tex.AsSpan(12, 16).ToArray(), value => Assert.Equal(0x10, value));

        Assert.False(TexWriter.TryWrapDds(MakeDds(8, 8, "DXT1", new[] { 32, 8 }), out _));
    }

    private static byte[] MakeDds(int width, int height, string fourCc, IReadOnlyList<int> levels)
    {
        byte[] dds = new byte[128 + levels.Sum()];
        "DDS "u8.CopyTo(dds);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(12), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(28), (uint)levels.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(76), 32);
        System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(dds, 84);
        int offset = 128;
        for (int level = 0; level < levels.Count; level++)
        {
            dds.AsSpan(offset, levels[level]).Fill((byte)(0x10 + level));
            offset += levels[level];
        }
        return dds;
    }
}
