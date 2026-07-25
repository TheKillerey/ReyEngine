namespace ReyEngine.Core.Decoding;

/// <summary>Why a texture was left alone by a recolor.</summary>
public enum RecolorSkip
{
    None,
    /// <summary>Not a Riot .tex container at all. Map WADs carry a few of these under .tex names
    /// (Map11 has 187 such chunks under assets/esports/sponsoredbanners/); rewriting them would
    /// destroy whatever they really are.</summary>
    NotATexture,
    /// <summary>A .tex whose pixel format we can read but not write back — Map11 ships three
    /// format-14 normal maps. Converting them to something we can write would change the data the
    /// shader samples, so they are skipped instead.</summary>
    UnsupportedFormat,
    DecodeFailed,
    EncodeFailed,
    /// <summary>The adjustment is the identity — re-encoding would spend a generation of BC loss to
    /// produce the same picture.</summary>
    NoChange,
}

/// <summary>Result of recolouring one texture. <see cref="Bytes"/> is non-null only when Ok.</summary>
public sealed record RecolorOutcome(
    bool Ok,
    byte[]? Bytes,
    RecolorSkip Skip,
    string Detail,
    int Width = 0,
    int Height = 0,
    TexFormat? Format = null);

/// <summary>M171: recolour a League texture in place — decode, apply a <see cref="TextureAdjustment"/>,
/// re-encode into the SAME container and the SAME pixel format it arrived in.
///
/// Two rules make this safe to run over a whole map:
///  - Never change what a file IS. The source format is preserved (a BC1 texture stays BC1 — 37.9% of
///    Map11 is BC1 and promoting it to BC3 would roughly double the mod), the mip chain is kept only if
///    the source had one, and anything we cannot faithfully rewrite is reported as a skip rather than
///    converted.
///  - Never edit our own output. The caller must hand in a PRISTINE base (Riot's original bytes), not
///    the previously-recoloured file. BC compression is lossy, so re-editing an edited texture compounds
///    the loss every time: ten successive slider nudges applied destructively measure 28.6 dB, while the
///    same ten re-derived from the original measure 40.0 dB. TextureAdjustment is a value precisely so
///    the caller can keep re-deriving.</summary>
public static class TextureRecolor
{
    /// <summary>Can this blob be recoloured? Cheap header check — no decode.</summary>
    public static bool IsSupported(byte[]? source) => Classify(source).Skip == RecolorSkip.None;

    /// <summary>Header-only triage: what would happen if we tried. Lets a UI list show which textures
    /// are in scope before any of them is decoded.</summary>
    public static RecolorOutcome Classify(byte[]? source)
    {
        if (source is null || source.Length < 12 || source[0] != 'T' || source[1] != 'E' || source[2] != 'X' || source[3] != 0)
            return new RecolorOutcome(false, null, RecolorSkip.NotATexture, "not a .tex container");

        int w = source[4] | (source[5] << 8);
        int h = source[6] | (source[7] << 8);
        var fmt = TexWriter.DetectFormat(source);
        if (fmt is null)
            return new RecolorOutcome(false, null, RecolorSkip.UnsupportedFormat,
                $"pixel format {source[9]} can be read but not written", w, h);

        return new RecolorOutcome(true, null, RecolorSkip.None, "", w, h, fmt);
    }

    /// <summary>Does this .tex carry a mip chain? Bit 0 of the flags byte.</summary>
    public static bool HasMips(byte[] tex) => tex.Length >= 12 && (tex[11] & 1) != 0;

    /// <summary>Recolour one texture. <paramref name="source"/> must be the PRISTINE original — see the
    /// type remarks. Returns a skip outcome (never throws) when the file is out of scope.</summary>
    public static RecolorOutcome Apply(byte[]? source, TextureAdjustment adjustment)
    {
        ArgumentNullException.ThrowIfNull(adjustment);

        var triage = Classify(source);
        if (!triage.Ok) return triage;
        if (adjustment.IsIdentity || adjustment.Strength <= 0f)
            return triage with { Ok = false, Skip = RecolorSkip.NoChange, Detail = "no adjustment set" };

        TextureImage decoded;
        try { decoded = TextureDecoder.Decode(source!); }
        catch (Exception ex)
        {
            return triage with { Ok = false, Skip = RecolorSkip.DecodeFailed, Detail = ex.Message };
        }

        try
        {
            var adjusted = adjustment.Apply(decoded);
            var bytes = TexWriter.Write(adjusted, triage.Format!.Value, HasMips(source!));
            return triage with { Bytes = bytes, Detail = $"{triage.Width}x{triage.Height} {triage.Format}" };
        }
        catch (Exception ex)
        {
            return triage with { Ok = false, Skip = RecolorSkip.EncodeFailed, Detail = ex.Message };
        }
    }

    /// <summary>Decode for on-screen preview, box-downscaled to at most <paramref name="maxDim"/> on its
    /// long edge. A map's textures are 2048^2, and a list of thumbnails at full size would be gigabytes
    /// of RGBA; the adjustment pass is also the expensive half of an edit (80 ms at 2048^2 versus 61 ms
    /// to encode), so previewing small is what keeps the sliders live.</summary>
    public static TextureImage? TryDecodePreview(byte[]? source, int maxDim = 256)
    {
        if (source is null) return null;
        TextureImage img;
        try { img = TextureDecoder.Decode(source); }
        catch { return null; }
        return Downscale(img, maxDim);
    }

    /// <summary>Box-filtered downscale to fit a square of <paramref name="maxDim"/>, preserving aspect.
    /// Returns the input untouched when it already fits.</summary>
    public static TextureImage Downscale(TextureImage img, int maxDim)
    {
        if (maxDim <= 0 || (img.Width <= maxDim && img.Height <= maxDim)) return img;

        float scale = maxDim / (float)Math.Max(img.Width, img.Height);
        int w = Math.Max(1, (int)MathF.Round(img.Width * scale));
        int h = Math.Max(1, (int)MathF.Round(img.Height * scale));
        var dst = new byte[w * h * 4];

        for (int y = 0; y < h; y++)
        {
            int sy0 = y * img.Height / h, sy1 = Math.Max(sy0 + 1, (y + 1) * img.Height / h);
            for (int x = 0; x < w; x++)
            {
                int sx0 = x * img.Width / w, sx1 = Math.Max(sx0 + 1, (x + 1) * img.Width / w);
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    int row = sy * img.Width;
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int i = (row + sx) * 4;
                        r += img.Rgba[i]; g += img.Rgba[i + 1]; b += img.Rgba[i + 2]; a += img.Rgba[i + 3];
                        n++;
                    }
                }
                int o = (y * w + x) * 4;
                dst[o] = (byte)(r / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(b / n); dst[o + 3] = (byte)(a / n);
            }
        }
        return new TextureImage(w, h, dst);
    }
}
