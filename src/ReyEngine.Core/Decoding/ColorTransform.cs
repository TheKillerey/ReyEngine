using System.Numerics;
using System.Text.Json.Serialization;

namespace ReyEngine.Core.Decoding;

/// <summary>
/// M813: a hue-range selection for <see cref="ColorTransform"/> - "the reds", "the blues", whatever hue the user picked.
/// Degrees on the HSV hue wheel (red 0, green 120, blue 240). The range WRAPS: centre 350 with width 40 selects 330
/// through 10.
/// </summary>
/// <param name="CenterDegrees">Middle of the range. Any finite value; it is wrapped into 0..360 (-10, 350 and 710 all mean
/// the same hue). A NaN or infinite centre has no hue to be centred on, so the whole selection counts as not set: no
/// restriction at all, the same neutral a null <see cref="ColorTransform.HueSelection"/> is.</param>
/// <param name="WidthDegrees">Full width of the part that is selected completely: the range runs from centre - width/2 to
/// centre + width/2, both ends included (a colour that sits exactly on an edge may still fall either side: a float colour's
/// hue is read back from RGB to about a ten-thousandth of a degree, and a byte colour's is only as precise as its bytes -
/// a degree or worse for a pale one). 0 selects a single hue. 360 or more selects EVERY hue, which is no restriction at all
/// (greys included, since nothing is being excluded). Clamped to 0..360. NaN is no restriction too: a width that is not a
/// number must not quietly become "pure red only".</param>
/// <param name="FeatherDegrees">How far OUTSIDE the width the selection fades to nothing, on each side. 0 is a hard edge.
/// The fade is a smoothstep, so the weight falls monotonically from 1 at the edge of the width to 0 at width/2 + feather
/// from the centre (and is exactly 0.5 half way). Clamped to 0..180; NaN is a hard edge.</param>
public readonly record struct HueRange(float CenterDegrees, float WidthDegrees, float FeatherDegrees = 0f);

/// <summary>
/// M813: grey protection for <see cref="ColorTransform"/>. A colour whose HSV saturation is low is left alone, so a hue
/// shift or a brightness change does not tint or move the neutral parts of a texture (steel, bone, cloth, highlights).
/// </summary>
/// <param name="Threshold">Saturation (0..1) up to and including which a colour is not touched at all. 0 protects only
/// exact greys (r = g = b), which have saturation 0. Clamped to 0..1. NaN protects nothing: the protection counts as not
/// set, the same neutral a null <see cref="ColorTransform.GreyProtection"/> is (reading it as 0 would protect every exact
/// grey, which is not "no protection").</param>
/// <param name="Feather">How far ABOVE the threshold the protection fades out: the weight rises (smoothstep) from 0 at the
/// threshold to 1 at threshold + feather. 0 is a hard edge. Clamped to 0..1; NaN is a hard edge.</param>
public readonly record struct GreyProtection(float Threshold, float Feather = 0f);

/// <summary>
/// M813: the Chroma Studio's ONE colour transform - hue rotate, saturation, brightness, colourise, a hue-range selection
/// and grey protection - as a single pure function over 8-bit texels (<see cref="ApplyInPlace(Span{byte})"/>) and over
/// float colours (<see cref="Apply(Vector4)"/>). M814 (body textures) and M815 (effect colours) both call it, so one set of
/// sliders moves a skin's body and its effects the same way, and M817 draws <see cref="SelectionWeight(float, float, float)"/>
/// over the preview.
///
/// <para><b>What each parameter does</b> (all in HSV, the same maths <see cref="TextureAdjustment"/> uses, via <see cref="Hsv"/>):</para>
/// <list type="bullet">
/// <item><see cref="HueShiftDegrees"/> rotates the hue: +120 turns pure red into pure green. 360 and its multiples are the
/// identity.</item>
/// <item><see cref="Saturation"/> multiplies HSV saturation, clamped to 0..1: 0 gives the grey of the same value.</item>
/// <item><see cref="Brightness"/> multiplies HSV value, the largest channel. 8-bit texels clamp the value at 255; float
/// colours do not clamp at all (see below).</item>
/// <item><see cref="ColorizeHueDegrees"/> REPLACES the hue and nothing else: every selected colour takes that hue, keeps
/// its own saturation (scaled by <see cref="Saturation"/>) and value. Greys and white have no saturation to carry, so they
/// stay grey and white. While it is set <see cref="HueShiftDegrees"/> has no effect.</item>
/// <item><see cref="HueSelection"/> restricts the change to a range of hues (<see cref="HueRange"/>); a grey has no hue and is
/// never inside a partial range.</item>
/// <item><see cref="GreyProtection"/> leaves low-saturation colours alone (<see cref="Decoding.GreyProtection"/>).</item>
/// <item><see cref="Strength"/> blends the result back toward the original, in RGB, as an overall dial.</item>
/// </list>
///
/// <para><b>The selection weight.</b> Each colour gets a weight 0..1 = hue-range weight x grey-protection weight x strength,
/// from the colour's own hue and saturation only. The transformed colour is <c>original + (transformed - original) x
/// weight</c>. Weight 0 returns the colour untouched; weight 1 is the full transform.</para>
///
/// <para><b>Exactness (M815's writer relies on these, each has a test).</b></para>
/// <list type="number">
/// <item>When <see cref="IsIdentity"/> every colour comes back unchanged, bit for bit: no HSV round trip is run at all.</item>
/// <item>A colour whose weight is 0 comes back unchanged, bit for bit, for the same reason.</item>
/// <item>White and every grey (r = g = b) are unchanged under every parameter set - hue, saturation, colourise, selection,
/// protection, strength - EXCEPT an explicit <see cref="Brightness"/> other than 1. (Under a partial hue range or grey
/// protection a grey is outside the selection, so even brightness leaves it alone: "brighten the reds" does not brighten the
/// whites.) The same holds for any colour with no hue to move: a float colour within a millionth of grey, after
/// normalising, counts as a grey and is not rebuilt. This matters because a bin field the file leaves out reads as white,
/// and such a field must never need writing.</item>
/// <item>Alpha is never read or written. A float colour's W comes back with the same bits it went in with.</item>
/// </list>
///
/// <para><b>Colour space.</b> Encoded values as stored: a byte over 255, a float as authored. No linearisation, the same
/// choice <see cref="TextureAdjustment"/> makes. The engine does none either: M352 disassembled the map pixel shaders and
/// found plain <c>mad</c> into the output with no sRGB encode or decode anywhere ("League is gamma-space end to end"), so
/// the numbers in a texture or a bin colour are exactly what the shader multiplies. Particle shaders were not disassembled
/// for this milestone; if one ever turns out to linearise, only the float path would need to change. Using the same space
/// for both keeps a body texture and an effect colour that were one colour before one colour after.</para>
///
/// <para><b>Float colours (bin <c>ValueColor</c>).</b> They are HDR: the plan counts 808 shipped components above 1.0, up to
/// 255. (A probe over every champion WAD, 2026-10-03 - 23,021 bins, 191,119 systems, 438,263 distinct constant / first / last
/// colour vectors - found 26 vectors with a component above 1, 20 of them below 2; the largest is the (255, 255, 255, 255)
/// of a Kalista dust emitter.) A colour whose largest channel m is above 1 is transformed as c / m (a colour with a maximum
/// of exactly 1) and scaled back by m, so its intensity survives and nothing clamps to 1; <see cref="Brightness"/> scales
/// the result. A colour with m of 1 or less is transformed as it stands, and brightness may carry it above 1 - there is no
/// clamp on a float. A grey (c, c, c) normalises to exactly white and scales back to exactly (c, c, c). Hue and saturation do
/// not depend on scale, so a selection picks the same colours at any intensity.</para>
///
/// <para><b>Colours it will not touch.</b> A float colour with a negative, NaN or infinite red/green/blue component has no
/// meaningful hue, and it is returned unchanged, bit for bit, with selection weight 0; <see cref="CanTransform(Vector4)"/>
/// says which colours that is, so a writer can count them. Shipped data does contain a few: the same probe found 5 distinct
/// vectors with a negative component and none that is NaN or infinite. Four are <c>reflectionDefinition.fresnelColor</c>
/// values that darken a rim (Singed (-0.15, -0.15, -0.05), Vel'Koz (-0.2, -0.2, -0.2), Jarvan IV and Jayce (-1, -1, -1)) and
/// one is a <c>color</c> (Miss Fortune, (0.545, -0.129, 0.937, -0.129)). Clamping them to zero and recolouring that would be
/// inventing a colour the author never wrote. A transform that would overflow a float (a brightness boost on a component
/// near <see cref="float.MaxValue"/>) leaves that colour unchanged too, rather than writing an infinity. Negative zero counts
/// as zero. Alpha is not examined.</para>
///
/// <para><b>Parameters are sanitised, never trusted.</b> NaN is the neutral value of every parameter. A NaN hue shift,
/// saturation, brightness or strength is the identity value; a non-finite hue shift or colourise hue counts as not set. A hue
/// range with a NaN width, or a NaN or infinite centre, is no restriction (as a null selection is), and a grey protection with
/// a NaN threshold is no protection (as a null protection is); in both a NaN feather is a hard edge. Saturation and brightness
/// are clamped to 0..1,000,000 (a negative factor is 0, and an infinite one cannot turn a texel into NaN) and strength to
/// 0..1; see <see cref="HueRange"/> and <see cref="Decoding.GreyProtection"/> for the rest.</para>
///
/// <para><b>Persistence.</b> The Chroma Studio's recipe saves a transform as JSON with System.Text.Json. The public property names -
/// <c>HueShiftDegrees</c>, <c>Saturation</c>, <c>Brightness</c>, <c>ColorizeHueDegrees</c>, <c>HueSelection</c>
/// (<c>CenterDegrees</c>, <c>WidthDegrees</c>, <c>FeatherDegrees</c>), <c>GreyProtection</c> (<c>Threshold</c>,
/// <c>Feather</c>) and <c>Strength</c> - are the recipe's schema and are not to be renamed; a test pins them.
/// <see cref="IsIdentity"/> is derived, so it is <c>[JsonIgnore]</c>d and never written. The serializer refuses a NaN or
/// infinite number by default, so a recipe holds finite values (a slider never produces any).</para>
///
/// <para><b>Limits.</b> HSV is not perceptual: a hue shift keeps HSV value, not perceived brightness (yellow looks brighter
/// than blue at the same value), exactly as Recolor Textures does. Strength and the selection feather blend in RGB, so a
/// half-strength shift of red to cyan passes through a darker, duller colour rather than through an intermediate hue.
/// Results are rounded to the nearest byte, and a BC1/BC3 re-encode after that is lossy (see <see cref="TextureRecolor"/>:
/// always re-derive from the pristine original). Dark texels have noisy hue, which is invisible at their brightness.</para>
///
/// <para><b>Shape.</b> An immutable record, a class on purpose for the reason <see cref="TextureAdjustment"/> gives:
/// <c>new ColorTransform()</c> really is the identity. Every method is pure and thread-safe; <see cref="ApplyInPlace(Span{byte})"/>
/// allocates nothing.</para>
///
/// <para><b>Cost</b> (measured, 2048 x 2048 random texels, the worst case, on the development machine): hue + saturation +
/// brightness in place takes about 130 ms in a Release build and 380 ms in Debug, against 165 ms (Release) for the same
/// step of <see cref="TextureAdjustment"/>; a texture with flat areas is 3-4x cheaper, because greys and unselected texels
/// are skipped. A float colour costs about 35 ns. M814's live preview should still work on a reduced copy while a slider
/// moves and run the full-size pass once on release.</para>
/// </summary>
public sealed record ColorTransform
{
    /// <summary>Hue rotation in degrees. Positive turns red toward green (+120: red becomes green), negative the other way.
    /// 360 and its multiples are the identity. Ignored while <see cref="ColorizeHueDegrees"/> is set.</summary>
    public float HueShiftDegrees { get; init; }

    /// <summary>Saturation multiplier. 0 = the grey of the same value, 1 = unchanged, above 1 = more saturated (clamped to a
    /// saturation of 1).</summary>
    public float Saturation { get; init; } = 1f;

    /// <summary>Value (largest channel) multiplier. 1 = unchanged. Bytes clamp at 255; float colours are not clamped.</summary>
    public float Brightness { get; init; } = 1f;

    /// <summary>Colourise: when set, replaces the hue with this one (degrees, wrapped), keeping each colour's own saturation
    /// and value. Greys and white are unchanged. While set, <see cref="HueShiftDegrees"/> has no effect.</summary>
    public float? ColorizeHueDegrees { get; init; }

    /// <summary>Restrict the change to a range of hues. Null: every colour that is not a grey-protected one.</summary>
    public HueRange? HueSelection { get; init; }

    /// <summary>Leave low-saturation colours alone. Null: no protection.</summary>
    public GreyProtection? GreyProtection { get; init; }

    /// <summary>Overall strength, 0..1: a blend of the whole transform back toward the original. 0 changes nothing.</summary>
    public float Strength { get; init; } = 1f;

    /// <summary>The transform that changes nothing: what <c>new ColorTransform()</c> already is.</summary>
    public static ColorTransform Identity { get; } = new();

    /// <summary>True when this would return every colour unchanged, bit for bit: strength 0, or no colourise and a hue shift
    /// that is a multiple of 360 degrees and saturation 1 and brightness 1. A selection or grey protection alone cannot
    /// change anything, so it does not count. Lets a caller skip a re-encode entirely. Derived from the other properties, so it
    /// is not part of the persisted recipe (<c>[JsonIgnore]</c>).</summary>
    [JsonIgnore]
    public bool IsIdentity => new Plan(this).Identity;

    // ===================================================================== the selection weight

    /// <summary>
    /// The selection weight of one colour, 0..1: hue-range weight x grey-protection weight x strength. This is the number the
    /// transform blends by, and the one M817 draws over the preview. It depends only on the colour's hue and saturation (not
    /// on how bright it is, even above 1), and ignores whether the other parameters would change anything.
    /// A colour that cannot be transformed (<see cref="CanTransform(float, float, float)"/>) has weight 0.
    /// </summary>
    public float SelectionWeight(float r, float g, float b)
    {
        if (!CanTransform(r, g, b)) return 0f;
        var plan = new Plan(this);
        bool hasHue = Plan.Analyse(r, g, b, hdr: true, out _, out float h, out float s, out _);
        return plan.Weight(h, s, hasHue);
    }

    /// <summary>The selection weight of an 8-bit colour. Equal to the float overload on the same colour, which is what
    /// <see cref="ApplyInPlace(Span{byte})"/> blends by.</summary>
    public float SelectionWeight(byte r, byte g, byte b)
    {
        float[] unit = Tables.ByteToUnit;
        return SelectionWeight(unit[r], unit[g], unit[b]);
    }

    /// <summary>The selection weight of a float colour (alpha is ignored).</summary>
    public float SelectionWeight(Vector4 color) => SelectionWeight(color.X, color.Y, color.Z);

    // ===================================================================== 8-bit texels

    /// <summary>
    /// Transform an RGBA8 buffer (tightly packed, four bytes a texel, the layout of <see cref="TextureImage.Rgba"/>) in
    /// place. Alpha is never read or written; a texel the transform leaves as it was is not written either. Allocates nothing.
    /// </summary>
    /// <returns>The number of texels whose colour changed (each counts once; a texel the transform touched but rounded back to
    /// the very same bytes does not count). <b>0 means the buffer is exactly as it came</b>, so a caller about to re-encode it
    /// - BC1 and BC3 are lossy, see <see cref="TextureRecolor"/> - can skip the encode and the write for a texture whose
    /// selection matched nothing.</returns>
    /// <exception cref="ArgumentException">The length is not a multiple of 4.</exception>
    public int ApplyInPlace(Span<byte> rgba)
    {
        if ((rgba.Length & 3) != 0)
            throw new ArgumentException("an RGBA8 buffer is a whole number of 4-byte texels", nameof(rgba));
        var plan = new Plan(this);
        if (plan.Identity) return 0;

        float[] unit = Tables.ByteToUnit;
        // A grey has no saturation for anything but brightness to act on, so with brightness 1 it is skipped outright.
        // Not an optimisation alone: this is what makes "white and every grey are unchanged" true by construction.
        bool greysAreUntouched = plan.Brightness == 1f;
        int changed = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            byte rb = rgba[i], gb = rgba[i + 1], bb = rgba[i + 2];
            if (greysAreUntouched && rb == gb && gb == bb) continue;
            if (!plan.Transform(unit[rb], unit[gb], unit[bb], hdr: false, out float r, out float g, out float b)) continue;
            byte nr = ToByte(r), ng = ToByte(g), nb = ToByte(b);
            if (nr == rb && ng == gb && nb == bb) continue;          // transformed, and rounded back to the same bytes
            rgba[i] = nr; rgba[i + 1] = ng; rgba[i + 2] = nb;
            changed++;
        }
        return changed;
    }

    /// <summary>Transform an image, returning a new one. The source is never modified, so a caller can keep it as the
    /// pristine base for the next adjustment (re-derive, never edit your own output: see <see cref="TextureRecolor"/>).</summary>
    public TextureImage Apply(TextureImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = (byte[])source.Rgba.Clone();
        ApplyInPlace(copy);
        return new TextureImage(source.Width, source.Height, copy);
    }

    // ===================================================================== float colours

    /// <summary>
    /// Transform one float colour (a bin <c>ValueColor</c> value, or one key of its curve), HDR-aware as the type remarks
    /// describe. W (alpha) comes back with the bits it went in with. A colour this leaves alone is returned as it came.
    /// </summary>
    public Vector4 Apply(Vector4 color)
    {
        var plan = new Plan(this);
        return plan.Apply(color);
    }

    /// <summary>Transform float colours in place (a curve's keys, say). Same rules as <see cref="Apply(Vector4)"/>.</summary>
    /// <returns>The number of colours that changed: any of red, green or blue differs in its bits (alpha never does). 0 means
    /// every colour is exactly as it came, so a writer need not rewrite the field it read them from.</returns>
    public int ApplyInPlace(Span<Vector4> colors)
    {
        var plan = new Plan(this);
        if (plan.Identity) return 0;
        int changed = 0;
        for (int i = 0; i < colors.Length; i++)
        {
            Vector4 before = colors[i], after = plan.Apply(before);
            if (SameColorBits(before, after)) continue;
            colors[i] = after;
            changed++;
        }
        return changed;
    }

    /// <summary>Can this colour be transformed at all? False for a negative, NaN or infinite red, green or blue component;
    /// such a colour is returned unchanged and has selection weight 0. Alpha is not examined.</summary>
    public static bool CanTransform(Vector4 color) => CanTransform(color.X, color.Y, color.Z);

    /// <inheritdoc cref="CanTransform(Vector4)"/>
    public static bool CanTransform(float r, float g, float b) =>
        r >= 0f && g >= 0f && b >= 0f && float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b);

    // ===================================================================== internals

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);

    /// <summary>Same red, green and blue, bit for bit (so -0 differs from +0, and a NaN equals itself). Alpha is never changed
    /// by this type, so it is not compared.</summary>
    private static bool SameColorBits(Vector4 a, Vector4 b) =>
        BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
        && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);

    /// <summary>Largest saturation or brightness factor honoured. Finite on purpose: zero times infinity is NaN.</summary>
    private const float MaxFactor = 1_000_000f;

    private static float Sane(float v, float neutral, float min, float max) =>
        float.IsNaN(v) ? neutral : Math.Clamp(v, min, max);

    /// <summary>byte / 255f, precomputed: the very same values <see cref="TextureAdjustment"/> divides out, without 3 divisions a texel.</summary>
    private static class Tables
    {
        public static readonly float[] ByteToUnit = Build();

        private static float[] Build()
        {
            var t = new float[256];
            for (int i = 0; i < t.Length; i++) t[i] = i / 255f;
            return t;
        }
    }

    /// <summary>The parameters, sanitised and turned into what the per-colour maths wants. A struct on the stack: a call
    /// builds one, a loop builds one for the whole buffer.</summary>
    private readonly struct Plan
    {
        public readonly bool Identity;
        public readonly float Strength, Saturation, Brightness;
        /// <summary>Hue shift in turns (1 = 360 degrees), |x| &lt; 1.</summary>
        public readonly float ShiftTurns;
        public readonly bool Colorize;
        public readonly float ColorizeTurns;
        public readonly bool Selects;
        public readonly float CenterDegrees, HalfWidthDegrees, FeatherDegrees;
        public readonly bool Guards;
        public readonly float GreyThreshold, GreyFeather;

        public Plan(ColorTransform t)
        {
            Strength = Sane(t.Strength, 1f, 0f, 1f);
            Saturation = Sane(t.Saturation, 1f, 0f, MaxFactor);
            Brightness = Sane(t.Brightness, 1f, 0f, MaxFactor);

            // % keeps the sign and is exact, so a shift inside +-360 is untouched and a larger one loses whole turns only.
            float shift = float.IsFinite(t.HueShiftDegrees) ? t.HueShiftDegrees % 360f : 0f;
            ShiftTurns = shift / 360f;

            if (t.ColorizeHueDegrees is { } target && float.IsFinite(target))
            {
                Colorize = true;
                float turns = target / 360f;
                ColorizeTurns = turns - MathF.Floor(turns);
            }
            else
            {
                Colorize = false;
                ColorizeTurns = 0f;
            }

            // A selection with no usable centre or width (NaN, or an infinite centre) is no restriction, the neutral a null
            // selection is. Reading it as "width 0 round hue 0" would select only pure red - the opposite of neutral.
            if (t.HueSelection is { } range && float.IsFinite(range.CenterDegrees) && !float.IsNaN(range.WidthDegrees))
            {
                float width = Math.Clamp(range.WidthDegrees, 0f, 360f);
                Selects = width < 360f;                    // 360 degrees is every hue: no restriction
                float center = range.CenterDegrees % 360f;
                if (center < 0f) center += 360f;
                CenterDegrees = center;
                HalfWidthDegrees = width * 0.5f;
                FeatherDegrees = Sane(range.FeatherDegrees, 0f, 0f, 180f);        // a NaN feather is a hard edge
            }
            else
            {
                Selects = false;
                CenterDegrees = HalfWidthDegrees = FeatherDegrees = 0f;
            }

            // Likewise a protection with no threshold protects nothing. A threshold of 0 would protect every exact grey, which
            // is a protection (brightness would then leave greys alone), not the absence of one.
            if (t.GreyProtection is { } grey && !float.IsNaN(grey.Threshold))
            {
                Guards = true;
                GreyThreshold = Math.Clamp(grey.Threshold, 0f, 1f);
                GreyFeather = Sane(grey.Feather, 0f, 0f, 1f);                    // a NaN feather is a hard edge
            }
            else
            {
                Guards = false;
                GreyThreshold = GreyFeather = 0f;
            }

            bool neutral = !Colorize && ShiftTurns == 0f && Saturation == 1f && Brightness == 1f;
            Identity = Strength <= 0f || neutral;
        }

        /// <summary>Normalise an HDR colour to a maximum of 1 and take its HSV. <paramref name="scale"/> is what to multiply
        /// back by (1 unless the largest channel exceeds 1). Returns whether the colour has a hue.</summary>
        public static bool Analyse(float r, float g, float b, bool hdr, out float scale, out float h, out float s, out float v)
        {
            scale = 1f;
            if (hdr)
            {
                float m = MathF.Max(r, MathF.Max(g, b));
                if (m > 1f) { scale = m; r /= m; g /= m; b /= m; }
            }
            return Hsv.FromRgb(r, g, b, out h, out s, out v);
        }

        /// <summary>hue-range weight x grey-protection weight x strength.</summary>
        public float Weight(float hueTurns, float saturation, bool hasHue)
        {
            float w = Strength;
            if (Selects)
            {
                if (!hasHue) return 0f;          // a grey has no hue, so it is outside every partial range
                w *= HueWeight(hueTurns);
            }
            if (Guards) w *= GreyWeight(saturation);
            return w;
        }

        private float HueWeight(float hueTurns)
        {
            float d = MathF.Abs(hueTurns * 360f - CenterDegrees);
            d = MathF.Min(d, 360f - d);                                  // distance round the wheel, 0..180
            if (d <= HalfWidthDegrees) return 1f;
            if (d >= HalfWidthDegrees + FeatherDegrees) return 0f;       // also every d past a hard edge
            return 1f - Smooth(MathF.Min((d - HalfWidthDegrees) / FeatherDegrees, 1f));
        }

        private float GreyWeight(float saturation)
        {
            if (GreyFeather <= 0f) return saturation > GreyThreshold ? 1f : 0f;
            float t = (saturation - GreyThreshold) / GreyFeather;
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return Smooth(t);
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);

        public Vector4 Apply(Vector4 c)
        {
            if (Identity || !CanTransform(c.X, c.Y, c.Z)) return c;
            return Transform(c.X, c.Y, c.Z, hdr: true, out float r, out float g, out float b) ? new Vector4(r, g, b, c.W) : c;
        }

        /// <summary>
        /// The one per-colour kernel. <paramref name="hdr"/> is the float path: normalise by the largest channel when it
        /// exceeds 1, do not clamp the value. Returns false (and the input, untouched) when the weight is 0 or the result
        /// would not be finite; the caller then writes nothing.
        /// </summary>
        public bool Transform(float r, float g, float b, bool hdr, out float tr, out float tg, out float tb)
        {
            bool hasHue = Analyse(r, g, b, hdr, out float scale, out float h, out float s, out float v);

            // No hue: hue, saturation and colourise have nothing to act on, so only brightness can change this colour. With
            // brightness at 1 it is left exactly as it is - a grey, or a float within a millionth of one, which rebuilding
            // from (v, v, v) would otherwise snap to grey by a few bits.
            if (!hasHue && Brightness == 1f) { tr = r; tg = g; tb = b; return false; }

            float w = Weight(h, s, hasHue);
            if (w <= 0f) { tr = r; tg = g; tb = b; return false; }

            // The same sequence of operations as TextureAdjustment's HSV step, so the two agree where they overlap.
            h = Colorize ? ColorizeTurns : h + ShiftTurns;
            h -= MathF.Floor(h);                                         // wrap into [0,1)
            s = Math.Clamp(s * Saturation, 0f, 1f);
            v *= Brightness;
            if (!hdr && v > 1f) v = 1f;                                  // a byte cannot exceed white; a float can
            Hsv.ToRgb(h, s, v, out tr, out tg, out tb);
            if (scale != 1f) { tr *= scale; tg *= scale; tb *= scale; }

            if (hdr && !(float.IsFinite(tr) && float.IsFinite(tg) && float.IsFinite(tb)))
            {
                tr = r; tg = g; tb = b;
                return false;
            }

            if (w < 1f)
            {
                tr = r + (tr - r) * w;
                tg = g + (tg - g) * w;
                tb = b + (tb - b) * w;
            }
            return true;
        }
    }
}
