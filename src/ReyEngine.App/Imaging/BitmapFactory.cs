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
}
