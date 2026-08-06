using ReyEngine.Core.Decoding;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M353: encrypted esports banner art is reported as encrypted, not as a broken texture.</summary>
public class EncryptedAssetTests
{
    // The header shared by all 192 encrypted banners in Map11.wad.client.
    private static byte[] EncryptedBanner(int length = 1048592)
    {
        var b = new byte[length];
        new byte[] { 0xc9, 0xe3, 0x44, 0x26, 0x64, 0xbb, 0x01, 0x61 }.CopyTo(b, 0);
        return b;
    }

    [Fact]
    public void AnEncryptedBannerIsRecognised()
        => Assert.True(TextureDecoder.IsEncryptedAsset(EncryptedBanner()));

    [Fact]
    public void DecodingOneSaysItIsEncryptedRatherThanUnknown()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TextureDecoder.Decode(EncryptedBanner()));
        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(new byte[] { 0x44, 0x44, 0x53, 0x20 })]              // "DDS "
    [InlineData(new byte[] { 0x54, 0x45, 0x58, 0x00 })]              // "TEX\0"
    [InlineData(new byte[] { 0xc9, 0xe3, 0x44, 0x26, 0x00, 0x00 })]  // shares a prefix, diverges by byte 5
    public void RealTexturesAreNotMistakenForEncrypted(byte[] head)
        => Assert.False(TextureDecoder.IsEncryptedAsset(head));

    [Fact]
    public void AShortBufferIsNotMistakenForEncrypted()
        => Assert.False(TextureDecoder.IsEncryptedAsset(new byte[] { 0xc9, 0xe3 }));
}
