using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReyEngine.Core.Decoding;

namespace ReyEngine.App.Imaging;

public static class BitmapFactory
{
    /// <summary>Builds an Avalonia bitmap from a decoded RGBA8 texture.</summary>
    public static WriteableBitmap FromRgba(TextureImage img)
    {
        var bmp = new WriteableBitmap(
            new PixelSize(img.Width, img.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        var bgra = new byte[img.Rgba.Length];
        for (int i = 0; i < img.Rgba.Length; i += 4)
        {
            bgra[i + 0] = img.Rgba[i + 2]; // B
            bgra[i + 1] = img.Rgba[i + 1]; // G
            bgra[i + 2] = img.Rgba[i + 0]; // R
            bgra[i + 3] = img.Rgba[i + 3]; // A
        }

        using var fb = bmp.Lock();
        int srcStride = img.Width * 4;
        for (int y = 0; y < img.Height; y++)
            Marshal.Copy(bgra, y * srcStride, IntPtr.Add(fb.Address, y * fb.RowBytes), srcStride);

        return bmp;
    }

    /// <summary>A small (≤<paramref name="maxSize"/>px) nearest-neighbour thumbnail — bounds memory for the
    /// Content Browser where many tiles are visible at once (M33).</summary>
    public static WriteableBitmap FromRgbaThumbnail(TextureImage img, int maxSize = 64)
    {
        int w = img.Width, h = img.Height;
        if (Math.Max(w, h) <= maxSize) return FromRgba(img);

        float scale = (float)maxSize / Math.Max(w, h);
        int tw = Math.Max(1, (int)(w * scale));
        int th = Math.Max(1, (int)(h * scale));
        var bgra = new byte[tw * th * 4];
        for (int y = 0; y < th; y++)
        {
            int sy = Math.Min(h - 1, (int)(y / scale));
            for (int x = 0; x < tw; x++)
            {
                int sx = Math.Min(w - 1, (int)(x / scale));
                int si = (sy * w + sx) * 4;
                int di = (y * tw + x) * 4;
                bgra[di + 0] = img.Rgba[si + 2]; // B
                bgra[di + 1] = img.Rgba[si + 1]; // G
                bgra[di + 2] = img.Rgba[si + 0]; // R
                bgra[di + 3] = img.Rgba[si + 3]; // A
            }
        }

        var bmp = new WriteableBitmap(new PixelSize(tw, th), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        int stride = tw * 4;
        for (int y = 0; y < th; y++)
            Marshal.Copy(bgra, y * stride, IntPtr.Add(fb.Address, y * fb.RowBytes), stride);
        return bmp;
    }
}
