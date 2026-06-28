using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
using LeagueToolkit.Core.Renderer;

namespace ReyEngine.Core.Decoding;

/// <summary>Decoded image: tightly packed RGBA8, top-left origin.</summary>
public sealed class TextureImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    public TextureImage(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }
}

/// <summary>Decodes League .tex and .dds textures (via LeagueToolkit) to RGBA8.</summary>
public static class TextureDecoder
{
    public static TextureImage Decode(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        Texture texture = Texture.Load(ms);

        var mip = texture.Mips[0];
        int w = mip.Width;
        int h = mip.Height;
        var rgba = new byte[w * h * 4];

        Span2D<ColorRgba32> pixels = mip.Span;
        int i = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                ColorRgba32 c = pixels[y, x];
                rgba[i++] = c.r;
                rgba[i++] = c.g;
                rgba[i++] = c.b;
                rgba[i++] = c.a;
            }
        }

        return new TextureImage(w, h, rgba);
    }
}
