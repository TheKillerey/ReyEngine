namespace ReyEngine.Core.Decoding;

/// <summary>M171: a colour adjustment stack for a texture.
///
/// Deliberately a pure VALUE applied to SOURCE pixels, never to the previously-adjusted result. League
/// textures are BC-compressed, so every decode/encode cycle is lossy; re-editing an already-written
/// texture would compound that loss with each nudge of a slider. Keeping this a description of the edit
/// (rather than the edit itself) means a recolor can be re-tuned any number of times and still cost
/// exactly one encode from the original pixels.
///
/// Alpha is never touched by the colour operations — it carries cutout and blend information, and
/// hue-shifting it would silently change which pixels the game treats as transparent.</summary>
/// NOTE this is a record CLASS, not a struct, and deliberately so: with a positional record struct the
/// parameter defaults below apply only to an explicit constructor call, so `default` / `new()` would
/// silently yield Saturation=0 and Strength=0 — a greyscale no-op that reports itself as neutral. As a
/// class, `new TextureAdjustment()` really is the identity.
public sealed record TextureAdjustment(
    /// <summary>Hue rotation in degrees, -180..180.</summary>
    float HueDegrees = 0f,
    /// <summary>Saturation multiplier. 0 = greyscale, 1 = unchanged, >1 = more saturated.</summary>
    float Saturation = 1f,
    /// <summary>Value/brightness multiplier applied in HSV. 1 = unchanged.</summary>
    float Brightness = 1f,
    /// <summary>Contrast around mid-grey. 1 = unchanged.</summary>
    float Contrast = 1f,
    /// <summary>Input black point, 0..1 — everything at or below becomes black.</summary>
    float InputBlack = 0f,
    /// <summary>Input white point, 0..1 — everything at or above becomes white.</summary>
    float InputWhite = 1f,
    /// <summary>Midtone gamma. 1 = linear.</summary>
    float Gamma = 1f,
    /// <summary>Multiplicative tint, per channel. White = unchanged.</summary>
    float TintR = 1f, float TintG = 1f, float TintB = 1f,
    /// <summary>Blend of the whole adjustment against the original, 0..1. Lets a strong recolor be
    /// dialled back without re-deriving every slider.</summary>
    float Strength = 1f)
{
    /// <summary>M173: an optional .cube colour grade applied after the slider stack. Not a positional
    /// parameter because a LUT is a loaded object, not a number the UI scrubs — and because adding it to
    /// the record's primary constructor would silently change every existing call site's argument order.</summary>
    public CubeLut? Lut { get; init; }

    /// <summary>How much of the LUT to mix in, 0..1. Separate from <see cref="Strength"/> so a grade can
    /// be dialled back without also weakening the hue/levels work underneath it.</summary>
    public float LutStrength { get; init; } = 1f;

    public static TextureAdjustment Identity { get; } = new();

    /// <summary>True when this would leave every pixel untouched — lets a caller skip the re-encode
    /// entirely, which is the difference between a lossless no-op and a needless generation of BC loss.</summary>
    public bool IsIdentity =>
        HueDegrees == 0f && Saturation == 1f && Brightness == 1f && Contrast == 1f
        && InputBlack == 0f && InputWhite == 1f && Gamma == 1f
        && TintR == 1f && TintG == 1f && TintB == 1f
        && (Lut is null || LutStrength <= 0f);

    /// <summary>Apply to a decoded image, returning a new image. The source is never modified, so the
    /// caller can keep it as the pristine base for the next adjustment.</summary>
    public TextureImage Apply(TextureImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (IsIdentity || Strength <= 0f)
            return new TextureImage(source.Width, source.Height, (byte[])source.Rgba.Clone());

        var src = source.Rgba;
        var dst = new byte[src.Length];
        float strength = Math.Clamp(Strength, 0f, 1f);

        // Levels denominator; guard a degenerate black==white range rather than dividing by zero.
        float lo = Math.Clamp(InputBlack, 0f, 1f);
        float hi = Math.Clamp(InputWhite, 0f, 1f);
        float span = MathF.Abs(hi - lo) < 1e-4f ? 1e-4f : hi - lo;
        float invGamma = Gamma > 1e-4f ? 1f / Gamma : 1f;

        for (int i = 0; i < src.Length; i += 4)
        {
            float r = src[i] / 255f, g = src[i + 1] / 255f, b = src[i + 2] / 255f;
            float or_ = r, og = g, ob = b;

            // 1. levels + gamma, per channel
            r = MathF.Pow(Math.Clamp((r - lo) / span, 0f, 1f), invGamma);
            g = MathF.Pow(Math.Clamp((g - lo) / span, 0f, 1f), invGamma);
            b = MathF.Pow(Math.Clamp((b - lo) / span, 0f, 1f), invGamma);

            // 2. contrast about mid-grey
            if (Contrast != 1f)
            {
                r = (r - 0.5f) * Contrast + 0.5f;
                g = (g - 0.5f) * Contrast + 0.5f;
                b = (b - 0.5f) * Contrast + 0.5f;
            }

            // 3. hue / saturation / value, in HSV
            if (HueDegrees != 0f || Saturation != 1f || Brightness != 1f)
            {
                RgbToHsv(Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f),
                         out float h, out float s, out float v);
                h += HueDegrees / 360f;
                h -= MathF.Floor(h);                       // wrap into [0,1)
                s = Math.Clamp(s * Saturation, 0f, 1f);
                v = Math.Clamp(v * Brightness, 0f, 1f);
                HsvToRgb(h, s, v, out r, out g, out b);
            }

            // 4. tint
            r *= TintR; g *= TintG; b *= TintB;

            // 5. colour grade. Last, because a .cube is authored as a FINAL look — it expects to see the
            // image as it would be delivered, so running it before the levels/hue work would grade
            // something the colourist never saw.
            if (Lut is { } lut && LutStrength > 0f)
            {
                var graded = lut.Sample(new System.Numerics.Vector3(
                    Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f)));
                float lm = Math.Clamp(LutStrength, 0f, 1f);
                r += (graded.X - r) * lm;
                g += (graded.Y - g) * lm;
                b += (graded.Z - b) * lm;
            }

            // 6. blend the whole stack back toward the original
            if (strength < 1f)
            {
                r = or_ + (r - or_) * strength;
                g = og + (g - og) * strength;
                b = ob + (b - ob) * strength;
            }

            dst[i] = ToByte(r); dst[i + 1] = ToByte(g); dst[i + 2] = ToByte(b);
            dst[i + 3] = src[i + 3];   // alpha is cutout/blend data — never recoloured
        }
        return new TextureImage(source.Width, source.Height, dst);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);

    private static void RgbToHsv(float r, float g, float b, out float h, out float s, out float v)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float d = max - min;
        v = max;
        s = max <= 1e-6f ? 0f : d / max;
        if (d <= 1e-6f) { h = 0f; return; }
        if (max == r) h = (g - b) / d / 6f + (g < b ? 1f : 0f);
        else if (max == g) h = ((b - r) / d + 2f) / 6f;
        else h = ((r - g) / d + 4f) / 6f;
    }

    private static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
    {
        if (s <= 1e-6f) { r = g = b = v; return; }
        float sector = h * 6f;
        int i = (int)MathF.Floor(sector) % 6;
        if (i < 0) i += 6;
        float f = sector - MathF.Floor(sector);
        float p = v * (1f - s), q = v * (1f - s * f), t = v * (1f - s * (1f - f));
        (r, g, b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }
}
