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

/// <summary>M122: a decoded cubemap - six RGBA8 faces in DDS order (+X, -X, +Y, -Y, +Z, -Z).</summary>
public sealed class CubemapImage
{
    public required int FaceSize { get; init; }
    /// <summary>Six faces, each FaceSize*FaceSize*4 bytes RGBA.</summary>
    public required byte[][] Faces { get; init; }
}

public static class CubemapDecoder
{
    /// <summary>
    /// M122: decode a DDS cubemap (League's skybox format - riots_sru_skybox_cubemap.dds is six
    /// DXT1 faces back-to-back after the 128-byte header, no mips). Null when the file isn't a
    /// cubemap DDS or uses a compression we don't handle.
    /// </summary>
    public static CubemapImage? TryDecodeDds(byte[] data)
    {
        try
        {
            if (data.Length < 128 || data[0] != 'D' || data[1] != 'D' || data[2] != 'S' || data[3] != ' ') return null;
            int height = BitConverter.ToInt32(data, 12);
            int width = BitConverter.ToInt32(data, 16);
            int mipCount = Math.Max(1, BitConverter.ToInt32(data, 28));
            string fourCC = System.Text.Encoding.ASCII.GetString(data, 84, 4);
            uint caps2 = BitConverter.ToUInt32(data, 112);
            if ((caps2 & 0x200) == 0 || width != height || width <= 0) return null;   // not a cubemap

            var format = fourCC switch
            {
                "DXT1" => CompressionFormat.Bc1,
                "DXT3" => CompressionFormat.Bc2,
                "DXT5" => CompressionFormat.Bc3,
                _ => CompressionFormat.Unknown,
            };
            if (format == CompressionFormat.Unknown) return null;

            int bytesPerBlock = format == CompressionFormat.Bc1 ? 8 : 16;
            long FaceBytes(int w, int h) => Math.Max(1, (w + 3) / 4) * (long)Math.Max(1, (h + 3) / 4) * bytesPerBlock;

            // Each face stores its full mip chain before the next face starts.
            long mipChain = 0;
            for (int m = 0, w = width, h = height; m < mipCount; m++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
                mipChain += FaceBytes(w, h);

            var decoder = new BCnEncoder.Decoder.BcDecoder();
            var faces = new byte[6][];
            long offset = 128;
            for (int f = 0; f < 6; f++)
            {
                var block = new byte[FaceBytes(width, height)];
                Array.Copy(data, offset, block, 0, block.Length);
                ColorRgba32[] pixels = decoder.DecodeRaw(block, width, height, format);
                var rgba = new byte[width * height * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    rgba[i * 4] = pixels[i].r; rgba[i * 4 + 1] = pixels[i].g;
                    rgba[i * 4 + 2] = pixels[i].b; rgba[i * 4 + 3] = pixels[i].a;
                }
                faces[f] = rgba;
                offset += mipChain;   // skip this face's smaller mips
            }
            return new CubemapImage { FaceSize = width, Faces = faces };
        }
        catch { return null; }
    }
}
