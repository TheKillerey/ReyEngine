using System.Buffers.Binary;

namespace ReyEngine.Core.Decoding;

/// <summary>On-disk pixel formats a Riot .tex can carry. The values ARE the byte written at offset 9,
/// pinned empirically rather than from the widely-copied table that calls 11 "BC3" and 12 "BGRA8":
/// writing the same image as 8-byte BC1 blocks and as 16-byte BC3 blocks under each candidate byte and
/// reading it back through LeagueToolkit shows 10 and 11 both decode 8-byte blocks, and 12 decodes
/// 16-byte blocks (exact alpha). A real shipped Map12 lightmap agrees: format byte 12, and a length of
/// 5,592,444 that only balances as a 2048^2 BC3 chain.</summary>
public enum TexFormat : byte
{
    Bc1 = 10,
    Bc3 = 12,
}

/// <summary>M158: writes League .tex files. LeagueToolkit can read them but has no writer, and the
/// lightmap baker has to produce atlases the game will load, so the container and the BC3 encoder
/// both live here.
///
/// Container layout (12-byte header, measured — a 2048x2048 BC3 atlas with a full mip chain is
/// exactly 5,592,444 bytes, which only balances if every level down to 1x1 stores a whole 4x4 block):
///   0  u32  magic 'TEX\0'
///   4  u16  width
///   6  u16  height
///   8  u8   unused (Riot writes 1; the reader ignores it)
///   9  u8   format (see TexFormat)
///   10 u8   unused
///   11 u8   flags, bit 0 = has mip chain
///   12 ..   mip payloads, SMALLEST FIRST (1x1 ... full size).
/// </summary>
public static class TexWriter
{
    /// <summary>Encode an RGBA8 top-left-origin image as a .tex byte blob.</summary>
    /// <param name="mipmaps">Write the full chain down to 1x1. Riot's lightmap atlases always do.</param>
    public static byte[] Write(TextureImage image, TexFormat format = TexFormat.Bc3, bool mipmaps = true)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0) throw new ArgumentException("empty image", nameof(image));

        var levels = mipmaps ? BuildMipChain(image) : new List<TextureImage> { image };

        var ms = new MemoryStream();
        var header = new byte[12];
        header[0] = (byte)'T'; header[1] = (byte)'E'; header[2] = (byte)'X'; header[3] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)image.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)image.Height);
        header[8] = 1;
        header[9] = (byte)format;
        header[10] = 0;
        header[11] = (byte)(mipmaps && levels.Count > 1 ? 1 : 0);
        ms.Write(header);

        // Smallest first: the chain is built large -> small, so walk it backwards.
        for (int i = levels.Count - 1; i >= 0; i--)
            ms.Write(EncodeLevel(levels[i], format));

        return ms.ToArray();
    }

    /// <summary>Box-filtered mip chain, largest first, ending at 1x1.</summary>
    public static List<TextureImage> BuildMipChain(TextureImage image)
    {
        var levels = new List<TextureImage> { image };
        var cur = image;
        while (cur.Width > 1 || cur.Height > 1)
        {
            int w = Math.Max(1, cur.Width / 2), h = Math.Max(1, cur.Height / 2);
            var dst = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                int sy0 = Math.Min(y * 2, cur.Height - 1), sy1 = Math.Min(y * 2 + 1, cur.Height - 1);
                for (int x = 0; x < w; x++)
                {
                    int sx0 = Math.Min(x * 2, cur.Width - 1), sx1 = Math.Min(x * 2 + 1, cur.Width - 1);
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = cur.Rgba[(sy0 * cur.Width + sx0) * 4 + c]
                                + cur.Rgba[(sy0 * cur.Width + sx1) * 4 + c]
                                + cur.Rgba[(sy1 * cur.Width + sx0) * 4 + c]
                                + cur.Rgba[(sy1 * cur.Width + sx1) * 4 + c];
                        dst[(y * w + x) * 4 + c] = (byte)((sum + 2) / 4);
                    }
                }
            }
            cur = new TextureImage(w, h, dst);
            levels.Add(cur);
        }
        return levels;
    }

    private static byte[] EncodeLevel(TextureImage img, TexFormat format) => format switch
    {
        TexFormat.Bc1 => EncodeBlocks(img, bc1: true),
        TexFormat.Bc3 => EncodeBlocks(img, bc1: false),
        _ => throw new NotSupportedException($"unsupported .tex format {format}"),
    };

    /// <summary>BC1/BC3 block compression. Every level stores at least one whole 4x4 block, so a 1x1
    /// mip is still 8 (BC1) or 16 (BC3) bytes — that is what makes Riot's atlas sizes add up.</summary>
    private static byte[] EncodeBlocks(TextureImage img, bool bc1)
    {
        int bw = Math.Max(1, (img.Width + 3) / 4);
        int bh = Math.Max(1, (img.Height + 3) / 4);
        int blockBytes = bc1 ? 8 : 16;
        var dst = new byte[bw * bh * blockBytes];

        Span<byte> block = stackalloc byte[16 * 4];
        int o = 0;
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                // Gather the 4x4 texels, clamping at the edges so partial blocks replicate rather than
                // read zeros (a black fringe on any non-multiple-of-4 atlas otherwise).
                for (int y = 0; y < 4; y++)
                {
                    int sy = Math.Min(by * 4 + y, img.Height - 1);
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, img.Width - 1);
                        int s = (sy * img.Width + sx) * 4, d = (y * 4 + x) * 4;
                        block[d] = img.Rgba[s]; block[d + 1] = img.Rgba[s + 1];
                        block[d + 2] = img.Rgba[s + 2]; block[d + 3] = img.Rgba[s + 3];
                    }
                }
                if (!bc1) { EncodeAlphaBlock(block, dst.AsSpan(o)); o += 8; }
                EncodeColorBlock(block, dst.AsSpan(o), punchThrough: bc1);
                o += 8;
            }
        }
        return dst;
    }

    /// <summary>BC4-style alpha block: two endpoints + sixteen 3-bit indices.</summary>
    private static void EncodeAlphaBlock(ReadOnlySpan<byte> block, Span<byte> dst)
    {
        byte lo = 255, hi = 0;
        for (int i = 0; i < 16; i++)
        {
            byte a = block[i * 4 + 3];
            if (a < lo) lo = a;
            if (a > hi) hi = a;
        }
        dst[0] = hi; dst[1] = lo;                  // a0 > a1 selects the 8-value interpolation mode

        if (hi == lo)
        {
            dst[2] = dst[3] = dst[4] = dst[5] = dst[6] = dst[7] = 0;   // every index -> a0
            return;
        }

        ulong bits = 0;
        float scale = 7f / (hi - lo);
        for (int i = 0; i < 16; i++)
        {
            int q = (int)MathF.Round((block[i * 4 + 3] - lo) * scale);   // 0 = lo .. 7 = hi
            // BC4 index order: 0 -> a0(hi), 1 -> a1(lo), 2..7 -> interpolants from a0 down to a1.
            int idx = q == 7 ? 0 : q == 0 ? 1 : 8 - q;
            bits |= (ulong)(uint)idx << (i * 3);
        }
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(bits >> (i * 8));
    }

    /// <summary>BC1 colour block by range fit along the principal axis of the block's colours.</summary>
    private static void EncodeColorBlock(ReadOnlySpan<byte> block, Span<byte> dst, bool punchThrough)
    {
        // Mean and covariance-free principal axis: the bounding-box diagonal is a good enough axis for
        // lightmaps (smooth, low-chroma data) and costs a fraction of a proper PCA.
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = block[i * 4], g = block[i * 4 + 1], b = block[i * 4 + 2];
            if (r < minR) minR = r; if (r > maxR) maxR = r;
            if (g < minG) minG = g; if (g > maxG) maxG = g;
            if (b < minB) minB = b; if (b > maxB) maxB = b;
        }

        // Inset the box slightly: the endpoints are quantised to 5:6:5, and shrinking first keeps the
        // interpolants from over-shooting the real data (standard "inset by 1/16 of the range").
        int insR = (maxR - minR) >> 4, insG = (maxG - minG) >> 4, insB = (maxB - minB) >> 4;
        minR = Math.Min(minR + insR, 255); maxR = Math.Max(maxR - insR, 0);
        minG = Math.Min(minG + insG, 255); maxG = Math.Max(maxG - insG, 0);
        minB = Math.Min(minB + insB, 255); maxB = Math.Max(maxB - insB, 0);

        ushort c0 = Pack565(maxR, maxG, maxB);
        ushort c1 = Pack565(minR, minG, minB);

        // BC1 reads c0 <= c1 as the 3-colour + transparent mode. BC3's colour block is always
        // 4-colour, but keeping c0 > c1 is required for standalone BC1 and harmless for BC3.
        if (c0 < c1) (c0, c1) = (c1, c0);
        if (c0 == c1)
        {
            dst[0] = (byte)c0; dst[1] = (byte)(c0 >> 8);
            dst[2] = (byte)c1; dst[3] = (byte)(c1 >> 8);
            dst[4] = dst[5] = dst[6] = dst[7] = 0;    // all indices -> c0
            return;
        }
        if (punchThrough && c0 <= c1) (c0, c1) = (c1, c0);

        Unpack565(c0, out var e0);
        Unpack565(c1, out var e1);
        Span<(int r, int g, int b)> pal = stackalloc (int, int, int)[4];
        pal[0] = e0; pal[1] = e1;
        pal[2] = ((2 * e0.r + e1.r) / 3, (2 * e0.g + e1.g) / 3, (2 * e0.b + e1.b) / 3);
        pal[3] = ((e0.r + 2 * e1.r) / 3, (e0.g + 2 * e1.g) / 3, (e0.b + 2 * e1.b) / 3);

        uint indices = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = block[i * 4], g = block[i * 4 + 1], b = block[i * 4 + 2];
            int best = 0, bestD = int.MaxValue;
            for (int p = 0; p < 4; p++)
            {
                int dr = r - pal[p].r, dg = g - pal[p].g, db = b - pal[p].b;
                int d = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = p; }
            }
            indices |= (uint)best << (i * 2);
        }

        dst[0] = (byte)c0; dst[1] = (byte)(c0 >> 8);
        dst[2] = (byte)c1; dst[3] = (byte)(c1 >> 8);
        dst[4] = (byte)indices; dst[5] = (byte)(indices >> 8);
        dst[6] = (byte)(indices >> 16); dst[7] = (byte)(indices >> 24);
    }

    private static ushort Pack565(int r, int g, int b) =>
        (ushort)(((Math.Clamp(r, 0, 255) >> 3) << 11) | ((Math.Clamp(g, 0, 255) >> 2) << 5) | (Math.Clamp(b, 0, 255) >> 3));

    private static void Unpack565(ushort c, out (int r, int g, int b) rgb)
    {
        int r5 = (c >> 11) & 31, g6 = (c >> 5) & 63, b5 = c & 31;
        rgb = ((r5 << 3) | (r5 >> 2), (g6 << 2) | (g6 >> 4), (b5 << 3) | (b5 >> 2));
    }
}
