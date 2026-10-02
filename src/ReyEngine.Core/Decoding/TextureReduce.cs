namespace ReyEngine.Core.Decoding;

/// <summary>
/// M807: shrink a decoded texture by whole box-filtered halvings - what the Content Browser's map thumbnails do to every map
/// texture before it reaches the GPU. A 384 by 268 picture of a map needs nothing near a 4096 square terrain atlas, and a
/// Summoner's Rift scene would otherwise hold hundreds of megabytes of texels it can never show.
/// </summary>
public static class TextureReduce
{
    /// <summary>
    /// <paramref name="image"/> averaged down by the smallest power of two that brings its longer side to
    /// <paramref name="maxSize"/> or below. The same instance when it already fits or <paramref name="maxSize"/> is not
    /// positive, so a caller that opts out pays nothing. Each output texel is the straight-alpha mean of the block of source
    /// texels it covers (an edge block covers what is left), so a non-power-of-two size keeps every source texel.
    /// </summary>
    public static TextureImage ToMaxSize(TextureImage image, int maxSize)
    {
        ArgumentNullException.ThrowIfNull(image);
        int w = image.Width, h = image.Height;
        if (maxSize <= 0 || w <= 0 || h <= 0 || Math.Max(w, h) <= maxSize) return image;
        if (image.Rgba.Length < (long)w * h * 4) return image;   // not a texture this knows how to read: leave it alone

        int f = 1;
        while (Math.Max(w, h) / f > maxSize && f < 1024) f <<= 1;
        if (f == 1) return image;

        int nw = (w + f - 1) / f, nh = (h + f - 1) / f;
        var src = image.Rgba;
        var dst = new byte[checked(nw * nh * 4)];
        for (int y = 0; y < nh; y++)
        {
            int y0 = y * f, y1 = Math.Min(h, y0 + f);
            for (int x = 0; x < nw; x++)
            {
                int x0 = x * f, x1 = Math.Min(w, x0 + f);
                long r = 0, g = 0, b = 0, a = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int i = (sy * w + x0) * 4;
                    for (int sx = x0; sx < x1; sx++, i += 4)
                    {
                        r += src[i]; g += src[i + 1]; b += src[i + 2]; a += src[i + 3];
                    }
                }
                int n = (y1 - y0) * (x1 - x0);
                int o = (y * nw + x) * 4;
                dst[o] = (byte)((r + n / 2) / n);
                dst[o + 1] = (byte)((g + n / 2) / n);
                dst[o + 2] = (byte)((b + n / 2) / n);
                dst[o + 3] = (byte)((a + n / 2) / n);
            }
        }
        return new TextureImage(nw, nh, dst);
    }
}
