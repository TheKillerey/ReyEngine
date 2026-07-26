using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReyEngine.Core.Painting;

namespace ReyEngine.App.ViewModels;

/// <summary>M173: one entry in the brush-mask picker — the stencil plus a thumbnail of it.
///
/// The thumbnail is drawn as white-on-transparent rather than as the raw greyscale, because that is what
/// the mask actually does: it is a coverage map, and showing it as opacity reads correctly against the
/// dark panel while a greyscale swatch would make a soft brush look like a grey blob.</summary>
public sealed class BrushMaskOption
{
    /// <summary>Null for the "no stencil" entry — a plain round brush.</summary>
    public BrushMask? Mask { get; }
    public string Name { get; }
    public Bitmap? Thumbnail { get; }

    public static BrushMaskOption None { get; } = new();

    private BrushMaskOption()
    {
        Mask = null;
        Name = "Round";
        Thumbnail = null;
    }

    public BrushMaskOption(BrushMask mask)
    {
        Mask = mask;
        Name = mask.Name;
        Thumbnail = Render(mask, 48);
    }

    private static Bitmap? Render(BrushMask mask, int size)
    {
        try
        {
            var bmp = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96),
                PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            using var fb = bmp.Lock();
            var row = new byte[size * 4];
            for (int y = 0; y < size; y++)
            {
                // Sample in the mask's own -1..1 space so the thumbnail matches the dab exactly.
                float sy = y / (size - 1f) * 2f - 1f;
                for (int x = 0; x < size; x++)
                {
                    float sx = x / (size - 1f) * 2f - 1f;
                    byte a = (byte)Math.Clamp(mask.Sample(sx, sy) * 255f + 0.5f, 0f, 255f);
                    int o = x * 4;
                    row[o] = 255; row[o + 1] = 255; row[o + 2] = 255; row[o + 3] = a;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
            }
            return bmp;
        }
        catch { return null; }   // a missing thumbnail must never stop the mask being usable
    }
}
