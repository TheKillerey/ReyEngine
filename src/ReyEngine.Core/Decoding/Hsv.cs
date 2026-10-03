namespace ReyEngine.Core.Decoding;

/// <summary>
/// M813: the editor's one RGB / HSV conversion. <see cref="TextureAdjustment"/> (Recolor Textures) and
/// <see cref="ColorTransform"/> (the Chroma Studio) both call it, so a hue means the same thing in a recoloured map
/// texture and in a recoloured particle colour.
///
/// <para><b>Moved, not rewritten.</b> These are the two private functions <see cref="TextureAdjustment"/> has carried since
/// M171a, with every operation in the same order, so Recolor Textures' output is byte-identical to what it was (pinned by
/// <c>RecolorGoldenTests</c>, whose hashes were recorded before the move).</para>
///
/// <para><b>They do not clamp.</b> Hue is in TURNS (0..1: red 0, green 1/3, blue 2/3), saturation is
/// <c>(max - min) / max</c> and value is <c>max</c>. A value above 1 is fine in both directions: <see cref="ToRgb"/> is
/// linear in <c>v</c>, so an HDR colour round-trips. Clamping is the caller's decision, and the two callers decide
/// differently: <see cref="TextureAdjustment"/> clamps its inputs and results to 0..1 around these calls (a texel is a byte),
/// while <see cref="ColorTransform"/> always clamps saturation but clamps value only for 8-bit texels (a particle colour
/// above 1 is intensity, not an error).</para>
///
/// <para>Hue is undefined for a grey. By convention it reads 0 there, and <see cref="FromRgb"/> says so through its return
/// value; callers that must tell "red" from "no hue" (the hue-range selection) use that, not the hue.</para>
/// </summary>
public static class Hsv
{
    /// <summary>Chroma (<c>max - min</c>) at or below which a colour has no hue, and saturation at or below which
    /// <see cref="ToRgb"/> returns a grey. It is far below one 8-bit step (1/255 = 0.0039), so no byte colour is ever
    /// read as grey that is not, and only float noise is.</summary>
    public const float Epsilon = 1e-6f;

    /// <summary>RGB to HSV. Returns false when the colour has no hue (a grey, to within <see cref="Epsilon"/>), in which case
    /// <paramref name="h"/> is 0; saturation and value are always meaningful.</summary>
    public static bool FromRgb(float r, float g, float b, out float h, out float s, out float v)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float d = max - min;
        v = max;
        s = max <= Epsilon ? 0f : d / max;
        if (d <= Epsilon) { h = 0f; return false; }
        if (max == r) h = (g - b) / d / 6f + (g < b ? 1f : 0f);
        else if (max == g) h = ((b - r) / d + 2f) / 6f;
        else h = ((r - g) / d + 4f) / 6f;
        return true;
    }

    /// <summary>HSV to RGB. Hue is in turns and may be any value (it is wrapped); saturation is expected in 0..1 and value is
    /// not limited. A saturation at or below <see cref="Epsilon"/> returns the grey <c>(v, v, v)</c> exactly.</summary>
    public static void ToRgb(float h, float s, float v, out float r, out float g, out float b)
    {
        if (s <= Epsilon) { r = g = b = v; return; }
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
