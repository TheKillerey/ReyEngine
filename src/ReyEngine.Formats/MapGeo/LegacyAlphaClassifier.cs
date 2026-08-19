namespace ReyEngine.Formats.MapGeo;

/// <summary>What a diffuse texture's alpha channel actually holds.</summary>
public enum LegacyAlphaKind
{
    /// <summary>Effectively solid. An alpha test would discard nothing, so authoring one is pointless
    /// noise rather than harmful.</summary>
    Opaque,
    /// <summary>A genuine cutout mask - pixels sit at the ends of the range with little in between.
    /// Foliage, grilles, chain-link. This is the case an alpha test exists for.</summary>
    Cutout,
    /// <summary>A broad spread of mid values. In legacy League art this is almost always SPECULAR or
    /// GLOSS data packed into alpha, not transparency - alpha-testing it eats the surface.</summary>
    Gradient,
}

/// <summary>
/// Decides whether a legacy diffuse texture's alpha is a cutout mask or something else (M528).
///
/// <para><b>Why this exists.</b> M338 gave every ordinary ported surface
/// <c>DefaultEnv_Flat_AlphaTest</c> with <c>AlphaTestValue = 0.35</c>. That is correct for a cutout and
/// destructive for anything else, because old League textures routinely store gloss in the alpha
/// channel. Measured on a real ported map: of 86 textures only 14 are true cutouts, 52 are opaque, and
/// 20 are gradient - and an 0.35 cutoff discards 40-72% of those 20. The visible result is ground with
/// holes punched through it, which is exactly what a user reported.</para>
///
/// <para>M338's verification was structural - it checked that every generated binding kept its texture
/// and render state - so nothing in it could notice that the render state deletes the surface.</para>
/// </summary>
public static class LegacyAlphaClassifier
{
    /// <summary>At or below this, a pixel counts as fully transparent.</summary>
    private const byte Low = 16;

    /// <summary>At or above this, a pixel counts as fully opaque.</summary>
    private const byte High = 239;

    /// <summary>How much of the image must sit at the two ends before the channel reads as a mask
    /// rather than as a gradient. A real cutout is overwhelmingly on/off; anti-aliased mask edges are
    /// the only mid values it has, and they are a small minority of any sane texture.</summary>
    private const double CutoutEndShare = 0.90;

    /// <summary>Above this share of fully-opaque pixels there is nothing to test against.</summary>
    private const double OpaqueShare = 0.995;

    /// <summary>
    /// Classify a decoded RGBA8 image. <paramref name="rgba"/> is 4 bytes per pixel, alpha last.
    /// </summary>
    public static LegacyAlphaKind Classify(ReadOnlySpan<byte> rgba)
    {
        long total = 0, low = 0, high = 0;
        for (int i = 3; i < rgba.Length; i += 4)
        {
            byte a = rgba[i];
            total++;
            if (a <= Low) low++;
            else if (a >= High) high++;
        }

        if (total == 0) return LegacyAlphaKind.Opaque;
        if ((double)high / total >= OpaqueShare) return LegacyAlphaKind.Opaque;
        return (double)(low + high) / total >= CutoutEndShare
            ? LegacyAlphaKind.Cutout
            : LegacyAlphaKind.Gradient;
    }

    /// <summary>
    /// The share of an image an alpha test at <paramref name="cutoff"/> would discard. Reported so a
    /// port can say what it declined to do and why, rather than silently changing a shader.
    /// </summary>
    public static double DiscardedShare(ReadOnlySpan<byte> rgba, float cutoff)
    {
        byte threshold = (byte)Math.Clamp(cutoff * 255f, 0f, 255f);
        long total = 0, below = 0;
        for (int i = 3; i < rgba.Length; i += 4)
        {
            total++;
            if (rgba[i] < threshold) below++;
        }
        return total == 0 ? 0 : (double)below / total;
    }
}
