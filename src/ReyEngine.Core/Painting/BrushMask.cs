using ReyEngine.Core.Decoding;

namespace ReyEngine.Core.Painting;

/// <summary>M173: a greyscale stencil that shapes a brush dab.
///
/// Without one, every dab is a smooth disc and a stroke is a uniform tube — fine for blocking colour in,
/// useless for anything that should read as a material. A mask multiplies the radial falloff, so the same
/// brush can lay down speckle, cracks, or a hard-edged stamp.
///
/// Stored as one byte per texel, sampled in brush-local space: the dab's disc maps to the mask's unit
/// square, so a mask is resolution-independent and the same one works at any brush size.</summary>
public sealed class BrushMask
{
    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Width*Height coverage values, 0 = no paint, 255 = full.</summary>
    public byte[] Alpha { get; }

    public BrushMask(string name, int width, int height, byte[] alpha)
    {
        Name = name; Width = width; Height = height; Alpha = alpha;
    }

    /// <summary>Sample at brush-local coordinates, both in -1..1 with 0 at the dab centre. Bilinear, so a
    /// 256² mask does not go blocky when the brush covers a few hundred texels. Outside the square reads
    /// as zero, which keeps a rotated mask from wrapping around its own edge.</summary>
    public float Sample(float x, float y)
    {
        // -1..1 -> 0..1 -> texel space.
        float u = (x * 0.5f + 0.5f) * (Width - 1);
        float v = (y * 0.5f + 0.5f) * (Height - 1);
        if (u < 0f || v < 0f || u > Width - 1 || v > Height - 1) return 0f;

        int x0 = (int)u, y0 = (int)v;
        int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        float fx = u - x0, fy = v - y0;

        float a00 = Alpha[y0 * Width + x0], a10 = Alpha[y0 * Width + x1];
        float a01 = Alpha[y1 * Width + x0], a11 = Alpha[y1 * Width + x1];
        float top = a00 + (a10 - a00) * fx;
        float bot = a01 + (a11 - a01) * fx;
        return (top + (bot - top) * fy) * (1f / 255f);
    }

    /// <summary>Adopt any decoded image as a mask. Colour is folded to luminance and multiplied by alpha,
    /// so both a black-on-white PNG stamp and an RGBA sprite with a real alpha channel behave sensibly —
    /// which of the two a downloaded brush pack uses is never predictable.</summary>
    public static BrushMask FromImage(string name, TextureImage image, bool invert = false)
    {
        int w = image.Width, h = image.Height;
        var a = new byte[w * h];
        var px = image.Rgba;
        // Does the image carry real transparency? If so it IS the shape and luminance is decoration.
        bool hasAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] < 250) { hasAlpha = true; break; }

        for (int i = 0, o = 0; i < a.Length; i++, o += 4)
        {
            float lum = (px[o] * 0.299f + px[o + 1] * 0.587f + px[o + 2] * 0.114f) / 255f;
            float v = hasAlpha ? px[o + 3] / 255f * lum : lum;
            if (invert) v = 1f - v;
            a[i] = (byte)Math.Clamp(v * 255f + 0.5f, 0f, 255f);
        }
        return new BrushMask(name, w, h, a);
    }

    // ------------------------------------------------------------------ built-ins

    private const int Res = 256;

    /// <summary>The masks that ship with the editor. Generated rather than bundled as image files: they
    /// stay crisp at any resolution, add nothing to the download, and carry no third-party licence.
    /// Anything more elaborate belongs in a user-imported PNG.</summary>
    public static IReadOnlyList<BrushMask> BuiltIn { get; } = new[]
    {
        Generate("Soft",     (dx, dy, r, rnd) => r >= 1f ? 0f : Smooth(1f - r)),
        Generate("Hard",     (dx, dy, r, rnd) => r >= 1f ? 0f : Smooth(Math.Clamp((1f - r) * 6f, 0f, 1f))),
        Generate("Square",   (dx, dy, r, rnd) => MathF.Max(MathF.Abs(dx), MathF.Abs(dy)) >= 1f ? 0f : 1f),
        Generate("Speckle",  (dx, dy, r, rnd) => r >= 1f ? 0f : (rnd < 0.30f ? Smooth(1f - r) : 0f)),
        Generate("Grain",    (dx, dy, r, rnd) => r >= 1f ? 0f : Smooth(1f - r) * (0.35f + 0.65f * rnd)),
        Generate("Chalk",    (dx, dy, r, rnd) => r >= 1f ? 0f : Smooth(1f - r) * (rnd < 0.55f ? 1f : 0.15f)),
        Generate("Spatter",  Spatter),
        Generate("Ring",     (dx, dy, r, rnd) => r >= 1f ? 0f : Smooth(1f - MathF.Abs(r - 0.75f) * 5f)),
    };

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Clumped dots rather than per-texel noise — real spatter has structure, and independent
    /// noise just reads as grain at any distance.</summary>
    private static float Spatter(float dx, float dy, float r, float rnd)
    {
        if (r >= 1f) return 0f;
        float v = 0f;
        // A few octaves of coarse value noise, thresholded. Deterministic in (dx, dy) so the mask is
        // stable — regenerating it must not produce a different brush.
        for (int o = 1; o <= 3; o++)
        {
            float f = o * 5.5f;
            float n = Hash(MathF.Floor(dx * f + 31.7f), MathF.Floor(dy * f + 11.3f));
            if (n > 0.62f) v = MathF.Max(v, 1f / o);
        }
        return v * Smooth(1f - r);
    }

    private static float Hash(float x, float y)
    {
        // Cheap deterministic hash — no Random, so the built-ins are identical every run.
        float s = MathF.Sin(x * 127.1f + y * 311.7f) * 43758.5453f;
        return s - MathF.Floor(s);
    }

    private static BrushMask Generate(string name, Func<float, float, float, float, float> shape)
    {
        var a = new byte[Res * Res];
        for (int y = 0; y < Res; y++)
        {
            float dy = y / (Res - 1f) * 2f - 1f;
            for (int x = 0; x < Res; x++)
            {
                float dx = x / (Res - 1f) * 2f - 1f;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                float rnd = Hash(x * 1.7f, y * 2.3f);
                a[y * Res + x] = (byte)Math.Clamp(shape(dx, dy, r, rnd) * 255f + 0.5f, 0f, 255f);
            }
        }
        return new BrushMask(name, Res, Res, a);
    }
}
