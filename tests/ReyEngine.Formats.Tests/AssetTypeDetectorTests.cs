using ReyEngine.Core.Assets;

namespace ReyEngine.Formats.Tests;

public class AssetTypeDetectorTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, "png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpg")]
    public void ImageMagicKeepsItsActualContainer(byte[] bytes, string extension)
        => Assert.Equal(extension, AssetTypeDetector.FileExtensionFromMagic(bytes));
}
