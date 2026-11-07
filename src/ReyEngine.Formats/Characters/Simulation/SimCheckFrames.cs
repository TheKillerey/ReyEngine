using System.Numerics;

namespace ReyEngine.Formats.Characters.Simulation;

/// <summary>An axis-aligned pixel rectangle in a top-down frame.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>This rectangle cut to a <paramref name="width"/> x <paramref name="height"/> frame (empty when it lies outside).</summary>
    public PixelRect Clamp(int width, int height)
    {
        int x0 = Math.Clamp(X, 0, width), y0 = Math.Clamp(Y, 0, height);
        int x1 = Math.Clamp(Right, 0, width), y1 = Math.Clamp(Bottom, 0, height);
        return new PixelRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }
}

/// <summary>
/// M871: the pure pixel arithmetic of <c>--simcheck</c>. The frames are what the Character window's D3D11 surface read back: BGRA,
/// top-down, the 3D view only (no UI around it).
/// </summary>
public static class SimCheckFrames
{
    /// <summary>Per-channel difference (0..255) above which a pixel counts as changed. Below it is dithering and filtering noise.</summary>
    public const int DefaultChannelThreshold = 24;

    /// <summary>
    /// Where a world point lands in the frame, in pixels. False when the point is behind the camera. The viewport's camera lives in a space
    /// whose X is the League world's X negated (the surface renders with <c>MirrorX</c>): <paramref name="mirrorX"/> true flips the point's X
    /// into that space first, exactly as the renderer does, so a League world position can be passed as it is. The frame is top-down,
    /// so +Y world is up.
    /// </summary>
    public static bool ProjectToPixel(Matrix4x4 view, Matrix4x4 projection, Vector3 world, int width, int height, bool mirrorX,
        out float x, out float y)
    {
        if (mirrorX) world = new Vector3(-world.X, world.Y, world.Z);
        var clip = Vector4.Transform(new Vector4(world, 1f), view * projection);
        x = y = 0f;
        if (!(clip.W > 1e-6f)) return false;
        float nx = clip.X / clip.W, ny = clip.Y / clip.W;
        x = (nx * 0.5f + 0.5f) * width;
        y = (1f - (ny * 0.5f + 0.5f)) * height;
        return !float.IsNaN(x) && !float.IsNaN(y);
    }

    /// <summary>
    /// The region the cast is judged in: the box around the given screen points, grown by a fraction of the frame on each side
    /// (more above, where the champion's body and tall effects are), cut to the frame. With no usable point it is the whole frame -
    /// a camera that cannot see the stage is still judged, not skipped.
    /// </summary>
    public static PixelRect RegionAround(IEnumerable<(float X, float Y)> points, int width, int height,
        float marginX = 0.07f, float marginTop = 0.10f, float marginBottom = 0.05f)
    {
        if (width <= 0 || height <= 0) return default;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        int n = 0;
        foreach (var (px, py) in points)
        {
            if (float.IsNaN(px) || float.IsNaN(py) || float.IsInfinity(px) || float.IsInfinity(py)) continue;
            minX = MathF.Min(minX, px); maxX = MathF.Max(maxX, px);
            minY = MathF.Min(minY, py); maxY = MathF.Max(maxY, py);
            n++;
        }
        if (n == 0) return new PixelRect(0, 0, width, height);
        var rect = new PixelRect(
            (int)MathF.Floor(minX - marginX * width), (int)MathF.Floor(minY - marginTop * height),
            (int)MathF.Ceiling(maxX - minX + 2 * marginX * width), (int)MathF.Ceiling(maxY - minY + (marginTop + marginBottom) * height))
            .Clamp(width, height);
        return rect.IsEmpty ? new PixelRect(0, 0, width, height) : rect;
    }

    /// <summary>A rectangle of <paramref name="halfWidth"/> either side of a screen point, from <paramref name="above"/> above to <paramref name="below"/> below it.</summary>
    public static PixelRect Around(float x, float y, float halfWidth, float above, float below, int width, int height) =>
        new PixelRect((int)MathF.Floor(x - halfWidth), (int)MathF.Floor(y - above), (int)MathF.Ceiling(2 * halfWidth),
            (int)MathF.Ceiling(above + below)).Clamp(width, height);

    /// <summary>
    /// The fraction of the pixels of <paramref name="region"/> (minus <paramref name="exclude"/>) whose largest B/G/R difference between the two
    /// frames exceeds <paramref name="channelThreshold"/>. Alpha is ignored: the readback's alpha is whatever the shaders last wrote.
    /// Zero for an empty region or frames of different size.
    /// </summary>
    public static double ChangedRatio(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height, PixelRect region,
        PixelRect? exclude = null, int channelThreshold = DefaultChannelThreshold)
    {
        long need = (long)width * height * 4;
        if (width <= 0 || height <= 0 || a.Length < need || b.Length < need) return 0.0;
        region = region.Clamp(width, height);
        if (region.IsEmpty) return 0.0;
        var ex = exclude?.Clamp(width, height) ?? default;
        long total = 0, changed = 0;
        for (int y = region.Y; y < region.Bottom; y++)
        {
            bool rowExcluded = !ex.IsEmpty && y >= ex.Y && y < ex.Bottom;
            int row = y * width * 4;
            for (int x = region.X; x < region.Right; x++)
            {
                if (rowExcluded && x >= ex.X && x < ex.Right) { x = ex.Right - 1; continue; }
                int o = row + x * 4;
                int d = Math.Max(Math.Abs(a[o] - b[o]), Math.Max(Math.Abs(a[o + 1] - b[o + 1]), Math.Abs(a[o + 2] - b[o + 2])));
                total++;
                if (d > channelThreshold) changed++;
            }
        }
        return total == 0 ? 0.0 : (double)changed / total;
    }

    /// <summary>A box-filter reduction of a BGRA frame to <paramref name="targetWidth"/> x <paramref name="targetHeight"/>, opaque.</summary>
    public static byte[] Downscale(ReadOnlySpan<byte> bgra, int width, int height, int targetWidth, int targetHeight)
    {
        var result = new byte[Math.Max(0, targetWidth) * Math.Max(0, targetHeight) * 4];
        if (width <= 0 || height <= 0 || targetWidth <= 0 || targetHeight <= 0 || bgra.Length < (long)width * height * 4) return result;
        for (int ty = 0; ty < targetHeight; ty++)
        {
            int y0 = (int)((long)ty * height / targetHeight), y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * height / targetHeight));
            for (int tx = 0; tx < targetWidth; tx++)
            {
                int x0 = (int)((long)tx * width / targetWidth), x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * width / targetWidth));
                int b = 0, g = 0, r = 0, n = 0;
                for (int y = y0; y < y1 && y < height; y++)
                    for (int x = x0; x < x1 && x < width; x++)
                    {
                        int o = (y * width + x) * 4;
                        b += bgra[o]; g += bgra[o + 1]; r += bgra[o + 2]; n++;
                    }
                int t = (ty * targetWidth + tx) * 4;
                if (n > 0) { result[t] = (byte)(b / n); result[t + 1] = (byte)(g / n); result[t + 2] = (byte)(r / n); }
                result[t + 3] = 255;
            }
        }
        return result;
    }

    /// <summary>M873: the bounding rectangle of the non-empty rectangles (null when there are none). The caster's boxes along a dash, as one region to leave out.</summary>
    public static PixelRect? Union(IEnumerable<PixelRect> rects)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue; bool any = false;
        foreach (var r in rects)
        {
            if (r.IsEmpty) continue;
            any = true;
            x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y); x1 = Math.Max(x1, r.Right); y1 = Math.Max(y1, r.Bottom);
        }
        return any ? new PixelRect(x0, y0, x1 - x0, y1 - y0) : null;
    }

    /// <summary>
    /// M873: the part of the frame a contact-sheet cell shows. The judged <paramref name="region"/> grown by <paramref name="pad"/> on each side, widened or
    /// heightened to the cell's <paramref name="aspect"/> (width / height) around its centre, then slid back inside the frame (never cut, so the aspect
    /// holds): the actors fill the cell instead of sitting small in a 2200-unit camera view. A region that is empty or larger than the frame gives the whole frame.
    /// </summary>
    public static PixelRect CropFor(PixelRect region, int width, int height, double aspect, double pad = 0.08)
    {
        var whole = new PixelRect(0, 0, Math.Max(0, width), Math.Max(0, height));
        if (width <= 0 || height <= 0 || region.IsEmpty || !(aspect > 0.05) || double.IsInfinity(aspect)) return whole;
        double cx = region.X + region.Width / 2.0, cy = region.Y + region.Height / 2.0;
        double w = region.Width * (1 + 2 * pad), h = region.Height * (1 + 2 * pad);
        if (w / h < aspect) w = h * aspect; else h = w / aspect;
        if (w >= width || h >= height)
        {
            // the cell's aspect at the largest size the frame allows
            w = Math.Min(width, height * aspect); h = w / aspect;
            if (region.Width * (1 + 2 * pad) >= width && region.Height * (1 + 2 * pad) >= height) return whole;
        }
        int cw = (int)Math.Ceiling(w), ch = (int)Math.Ceiling(h);
        cw = Math.Min(cw, width); ch = Math.Min(ch, height);
        int x = (int)Math.Round(cx - cw / 2.0), y = (int)Math.Round(cy - ch / 2.0);
        x = Math.Clamp(x, 0, width - cw); y = Math.Clamp(y, 0, height - ch);
        return new PixelRect(x, y, cw, ch);
    }

    /// <summary>A bilinear resample of <paramref name="crop"/> of a BGRA frame to <paramref name="targetWidth"/> x <paramref name="targetHeight"/>, opaque. Up- and downscales.</summary>
    public static byte[] ResampleCrop(ReadOnlySpan<byte> bgra, int width, int height, PixelRect crop, int targetWidth, int targetHeight)
    {
        var result = new byte[Math.Max(0, targetWidth) * Math.Max(0, targetHeight) * 4];
        crop = crop.Clamp(width, height);
        if (width <= 0 || height <= 0 || targetWidth <= 0 || targetHeight <= 0 || crop.IsEmpty || bgra.Length < (long)width * height * 4) return result;
        for (int ty = 0; ty < targetHeight; ty++)
        {
            double sy = crop.Y + (ty + 0.5) * crop.Height / targetHeight - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), crop.Y, crop.Bottom - 1), y1 = Math.Min(y0 + 1, crop.Bottom - 1);
            double fy = Math.Clamp(sy - y0, 0.0, 1.0);
            for (int tx = 0; tx < targetWidth; tx++)
            {
                double sx = crop.X + (tx + 0.5) * crop.Width / targetWidth - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), crop.X, crop.Right - 1), x1 = Math.Min(x0 + 1, crop.Right - 1);
                double fx = Math.Clamp(sx - x0, 0.0, 1.0);
                int t = (ty * targetWidth + tx) * 4;
                for (int c = 0; c < 3; c++)
                {
                    double top = bgra[(y0 * width + x0) * 4 + c] * (1 - fx) + bgra[(y0 * width + x1) * 4 + c] * fx;
                    double bottom = bgra[(y1 * width + x0) * 4 + c] * (1 - fx) + bgra[(y1 * width + x1) * 4 + c] * fx;
                    result[t + c] = (byte)Math.Clamp((int)Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
                }
                result[t + 3] = 255;
            }
        }
        return result;
    }
}
