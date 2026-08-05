using System.Buffers.Binary;

using BCnEncoder.Decoder;
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

/// <summary>Decodes League .tex and .dds textures (via LeagueToolkit) to RGBA8, plus .tga (M144 —
/// old NVR levels reference Targa files, which LeagueToolkit cannot read).</summary>
public static class TextureDecoder
{
    public static TextureImage Decode(byte[] data)
    {
        // M144: checked first — TGA has no leading magic, so LeagueToolkit would misparse it rather
        // than fail cleanly. LooksLikeTga validates the header, so .tex/.dds are never taken by mistake.
        if (TgaDecoder.LooksLikeTga(data) && TgaDecoder.TryDecode(data) is { } tga) return tga;

        // Riot extended format 14 is BC5/ATI2: two independently compressed channels used by
        // normal maps. LeagueToolkit currently rejects it before exposing a mip, so decode the
        // top mip directly while preserving the TEX container's smallest-first mip ordering.
        if (TryDecodeExtendedTex(data) is { } extended) return extended;

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

    private static TextureImage? TryDecodeExtendedTex(byte[] data)
    {
        const int HeaderSize = 12;
        const byte Bc5Format = 14;

        if (data.Length < HeaderSize ||
            data[0] != 'T' || data[1] != 'E' || data[2] != 'X' || data[3] != 0 ||
            data[9] != Bc5Format)
            return null;

        int width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2));
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Extended BC5 texture has invalid dimensions.");

        static int Bc5LevelSize(int width, int height) =>
            checked(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16);

        int topLevelSize = Bc5LevelSize(width, height);
        int mipPayloadSize = topLevelSize;
        if ((data[11] & 1) != 0)
        {
            for (int mipWidth = Math.Max(1, width / 2), mipHeight = Math.Max(1, height / 2);
                 mipWidth != width || mipHeight != height;
                 mipWidth = Math.Max(1, mipWidth / 2), mipHeight = Math.Max(1, mipHeight / 2))
            {
                mipPayloadSize = checked(mipPayloadSize + Bc5LevelSize(mipWidth, mipHeight));
                if (mipWidth == 1 && mipHeight == 1) break;
            }
        }

        if (data.Length < HeaderSize + mipPayloadSize)
            throw new InvalidDataException(
                $"Extended BC5 texture payload is truncated: expected {mipPayloadSize:N0} bytes, " +
                $"found {Math.Max(0, data.Length - HeaderSize):N0}.");

        // TEX mip chains are smallest first, therefore the full-resolution level is last.
        int topLevelOffset = HeaderSize + mipPayloadSize - topLevelSize;
        byte[] topLevel = data.AsSpan(topLevelOffset, topLevelSize).ToArray();
        ColorRgba32[] pixels = new BcDecoder().DecodeRaw(topLevel, width, height, CompressionFormat.Bc5);
        var rgba = new byte[checked(width * height * 4)];
        for (int i = 0; i < pixels.Length; i++)
        {
            rgba[i * 4] = pixels[i].r;
            rgba[i * 4 + 1] = pixels[i].g;
            rgba[i * 4 + 2] = pixels[i].b;
            rgba[i * 4 + 3] = pixels[i].a;
        }

        return new TextureImage(width, height, rgba);
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
