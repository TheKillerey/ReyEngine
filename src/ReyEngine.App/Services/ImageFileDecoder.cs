using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReyEngine.Core.Decoding;

namespace ReyEngine.App.Services;

/// <summary>
/// M393: decode the image formats ReyEngine.Core cannot.
///
/// <para>TextureDecoder handles Riot's own containers - TGA, .tex (including the BC5 extension) and .dds
/// through LeagueToolkit - and nothing else. PNG, JPEG and BMP have no decoder there because Core carries
/// no image library, which is why M392's import dialog reported "Cannot load unknown texture file format"
/// for the exact files it exists to convert.</para>
///
/// <para>Avalonia already ships a decoder for those, so this bridges to it. It lives in the App layer
/// because that is where the Avalonia dependency belongs; Core stays free of it.</para>
/// </summary>
public static class ImageFileDecoder
{
    /// <summary>
    /// Decode any supported image to RGBA8. Riot containers are tried FIRST so a .tex or .dds keeps the
    /// exact path it had before this existed - Avalonia would either refuse them or decode them by a
    /// different route, and neither is worth risking for formats that already worked.
    /// </summary>
    public static TextureImage Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Exception? riotError = null;
        try { return TextureDecoder.Decode(data); }
        catch (Exception ex) { riotError = ex; }

        if (TryDecodeWithAvalonia(data, out var img, out var avaloniaError)) return img!;

        // Report the RIOT failure, not Avalonia's: for a .tex that failed to parse, "unknown file format"
        // from the PNG decoder would be a misleading second opinion.
        throw new NotSupportedException(
            riotError?.Message ?? avaloniaError ?? "the image could not be decoded");
    }

    /// <summary>True when Avalonia could decode it. Never throws; the reason comes back in
    /// <paramref name="error"/>.</summary>
    public static bool TryDecodeWithAvalonia(byte[] data, out TextureImage? image, out string? error)
    {
        image = null; error = null;
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            using var bmp = new Bitmap(ms);
            int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
            if (w <= 0 || h <= 0) { error = "image has no pixels"; return false; }

            // Unpremultiplied: the encoders expect straight alpha, and premultiplied source would darken
            // every semi-transparent texel against black.
            using var wb = new WriteableBitmap(bmp.PixelSize, new Vector(96, 96),
                PixelFormat.Rgba8888, AlphaFormat.Unpremul);

            var rgba = new byte[w * h * 4];
            using (var fb = wb.Lock())
            {
                bmp.CopyPixels(new PixelRect(0, 0, w, h), fb.Address, fb.RowBytes * h, fb.RowBytes);
                // Copy row by row: RowBytes is the framebuffer's stride and is NOT guaranteed to equal
                // w*4, so a single block copy would shear any image whose rows are padded.
                for (int y = 0; y < h; y++)
                    Marshal.Copy(fb.Address + y * fb.RowBytes, rgba, y * w * 4, w * 4);
            }

            image = new TextureImage(w, h, rgba);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
