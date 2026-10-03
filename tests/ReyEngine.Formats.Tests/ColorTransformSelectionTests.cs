using System.Numerics;
using ReyEngine.Core.Decoding;
using static ReyEngine.Formats.Tests.ColorTestImages;
using static ReyEngine.Formats.Tests.ColorTestMath;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M813: the selection half of <see cref="ColorTransform"/> - the hue range (which wraps), grey protection, and the weight
/// that combines them with the strength. M817 draws that weight over the preview, so its shape is tested as a shape:
/// plateaus, monotonic fades, symmetry, the half-way value.
///
/// <para>Test colours are built from a hue/saturation/value with <see cref="ColorTestMath.Colour"/>, so a colour's hue is the
/// one asked for to within about 0.0001 degrees. Edges are therefore tested one degree either side, never on the line.</para>
/// </summary>
public sealed class ColorTransformSelectionTests
{
    // ===================================================================== the hue range

    private static readonly ColorTransform Wrap = new() { HueShiftDegrees = 90f, HueSelection = new HueRange(350f, 40f) };

    [Theory]
    [InlineData(333f)]
    [InlineData(340f)]
    [InlineData(350f)]
    [InlineData(358f)]
    [InlineData(2f)]
    [InlineData(7f)]
    public void Range_WrapAround_SelectsAcrossZero_AndInsideIsFullyTransformed(float hue)
    {
        // centre 350, width 40: 330 through 10, through zero.
        var c = Colour(hue);
        Assert.Equal(1f, Wrap.SelectionWeight(c));

        var unrestricted = Wrap with { HueSelection = null };
        Assert.True(SameBits(unrestricted.Apply(c), Wrap.Apply(c)), "a selected float colour must get exactly the full transform");
        Assert.NotEqual(c, Wrap.Apply(c));

        var texel = ToBytes(c);
        Assert.Equal(1f, Wrap.SelectionWeight(texel[0], texel[1], texel[2]));
        Assert.Equal(Run(unrestricted, texel), Run(Wrap, texel));
        Assert.NotEqual(texel, Run(Wrap, texel));
    }

    [Theory]
    [InlineData(327f)]
    [InlineData(300f)]
    [InlineData(13f)]
    [InlineData(20f)]
    [InlineData(60f)]
    [InlineData(120f)]
    [InlineData(180f)]
    [InlineData(270f)]
    public void Range_WrapAround_OutsideIsBitIdentical(float hue)
    {
        var c = Colour(hue);
        Assert.Equal(0f, Wrap.SelectionWeight(c));
        Assert.True(SameBits(c, Wrap.Apply(c)), Inv($"hue {hue} was touched"));

        var texel = ToBytes(c, 77);
        Assert.Equal(texel, Run(Wrap, texel));
    }

    [Fact]
    public void Range_HardEdge_OnlyEverGivesZeroOrOne()
    {
        var rng = new Xorshift32(0x9A9A);
        int zero = 0, one = 0;
        for (int i = 0; i < 2000; i++)
        {
            float w = Wrap.SelectionWeight(Colour(rng.NextUnit() * 360f, 0.3f + rng.NextUnit() * 0.7f, 0.3f + rng.NextUnit() * 0.7f));
            Assert.True(w == 0f || w == 1f, Inv($"weight {w} on a hard edge"));
            if (w == 0f) zero++; else one++;
        }
        Assert.InRange(one, 100, 400);           // 40 of 360 degrees is 11%
        Assert.True(zero > one);
    }

    [Fact]
    public void Range_OutsideIsBitIdentical_InsideFullyTransformed_OnTheGoldenImage()
    {
        var restricted = new ColorTransform { HueShiftDegrees = 60f, Saturation = 1.2f, HueSelection = new HueRange(350f, 40f) };
        var unrestricted = restricted with { HueSelection = null };
        var image = Golden();
        var got = restricted.Apply(image).Rgba;
        var full = unrestricted.Apply(image).Rgba;

        int inside = 0, outside = 0;
        for (int i = 0; i < image.Rgba.Length; i += 4)
        {
            float w = restricted.SelectionWeight(image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2]);
            for (int c = 0; c < 4; c++)
            {
                if (w == 0f) Assert.Equal(image.Rgba[i + c], got[i + c]);
                else Assert.Equal(full[i + c], got[i + c]);
            }
            if (w == 0f) outside++; else inside++;
        }
        Assert.True(inside > 500 && outside > 5000, Inv($"inside {inside}, outside {outside}: the image must exercise both"));
    }

    [Fact]
    public void Range_Feathered_PartialTexelsLieBetweenTheOriginalAndTheFullResult()
    {
        var feathered = new ColorTransform { HueShiftDegrees = 60f, HueSelection = new HueRange(350f, 40f, 30f) };
        var full = feathered with { HueSelection = null };
        var image = Golden();
        var got = feathered.Apply(image).Rgba;
        var fullBytes = full.Apply(image).Rgba;

        int partial = 0;
        for (int i = 0; i < image.Rgba.Length; i += 4)
        {
            float w = feathered.SelectionWeight(image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2]);
            for (int c = 0; c < 4; c++)
            {
                if (w == 0f) Assert.Equal(image.Rgba[i + c], got[i + c]);
                else if (w == 1f) Assert.Equal(fullBytes[i + c], got[i + c]);
                else if (c < 3)
                {
                    int lo = Math.Min(image.Rgba[i + c], fullBytes[i + c]), hi = Math.Max(image.Rgba[i + c], fullBytes[i + c]);
                    Assert.InRange(got[i + c], lo - 1, hi + 1);
                }
                else Assert.Equal(image.Rgba[i + c], got[i + c]);
            }
            if (w is > 0f and < 1f) partial++;
        }
        Assert.True(partial > 1000, Inv($"only {partial} partial texels"));
    }

    [Theory]
    [InlineData(120f)]     // away from zero
    [InlineData(350f)]     // the fade crosses 0/360
    [InlineData(0f)]       // centred on it
    [InlineData(10f)]
    public void Range_Feather_FallsMonotonicallyAndSymmetrically(float centre)
    {
        // plateau out to 20 degrees (half of 40), fade over the next 30, nothing beyond 50.
        var t = new ColorTransform { HueShiftDegrees = 30f, HueSelection = new HueRange(centre, 40f, 30f) };
        float previousUp = 1f, previousDown = 1f;
        for (float d = 0f; d <= 100f; d += 0.05f)
        {
            float up = t.SelectionWeight(Colour(centre + d));
            float down = t.SelectionWeight(Colour(centre - d));
            Assert.InRange(up, 0f, 1f);
            Assert.InRange(down, 0f, 1f);
            // the hue of a synthesized colour is recovered to ~1e-4 degrees, which the fade turns into ~5e-6 of weight
            Assert.True(up <= previousUp + 1e-5f, Inv($"weight rose from {previousUp} to {up} at +{d}"));
            Assert.True(down <= previousDown + 1e-5f, Inv($"weight rose from {previousDown} to {down} at -{d}"));
            Assert.InRange(up - down, -2e-5f, 2e-5f);
            previousUp = up; previousDown = down;

            if (d <= 19f) { Assert.Equal(1f, up); Assert.Equal(1f, down); }
            if (d >= 51f) { Assert.Equal(0f, up); Assert.Equal(0f, down); }
        }
        Assert.InRange(t.SelectionWeight(Colour(centre + 35f)), 0.499f, 0.501f);    // half way through the fade is half weight
        Assert.InRange(t.SelectionWeight(Colour(centre - 35f)), 0.499f, 0.501f);

        // The fade is a smoothstep, not a ramp (M817 draws this curve): 1 - (3t^2 - 2t^3) is 0.84375 a quarter of the way
        // down (t = 0.25) and 0.15625 three quarters of the way. A straight line would give 0.75 and 0.25.
        Assert.InRange(t.SelectionWeight(Colour(centre + 27.5f)), 0.84375f - 1e-3f, 0.84375f + 1e-3f);
        Assert.InRange(t.SelectionWeight(Colour(centre - 42.5f)), 0.15625f - 1e-3f, 0.15625f + 1e-3f);
    }

    [Fact]
    public void Range_CentreIsWrapped()
    {
        var a = new ColorTransform { HueSelection = new HueRange(-10f, 40f, 20f) };
        var b = new ColorTransform { HueSelection = new HueRange(350f, 40f, 20f) };
        var c = new ColorTransform { HueSelection = new HueRange(710f, 40f, 20f) };
        for (float hue = 0f; hue < 360f; hue += 7.3f)
        {
            var colour = Colour(hue);
            Assert.Equal(b.SelectionWeight(colour), a.SelectionWeight(colour));
            Assert.Equal(b.SelectionWeight(colour), c.SelectionWeight(colour));
        }
    }

    [Fact]
    public void Range_ZeroWidthWithAFeather_IsAPeakAtTheCentre()
    {
        var t = new ColorTransform { HueSelection = new HueRange(120f, 0f, 20f) };
        Assert.InRange(t.SelectionWeight(Colour(120.5f)), 0.99f, 1f);
        Assert.InRange(t.SelectionWeight(Colour(130f)), 0.499f, 0.501f);
        Assert.InRange(t.SelectionWeight(Colour(110f)), 0.499f, 0.501f);
        Assert.Equal(0f, t.SelectionWeight(Colour(141f)));
        Assert.Equal(0f, t.SelectionWeight(Colour(99f)));
    }

    [Theory]
    [InlineData(360f)]
    [InlineData(500f)]
    public void Range_FullWidth_IsNoRestriction_GreysIncluded(float width)
    {
        var t = new ColorTransform { Brightness = 0.5f, HueSelection = new HueRange(123f, width, 0f) };
        foreach (var c in new[] { Colour(0f), Colour(200f), new Vector4(0.5f, 0.5f, 0.5f, 1f), Vector4.One, Vector4.UnitW })
            Assert.Equal(1f, t.SelectionWeight(c));

        // so the whole image is transformed, exactly as with no selection at all
        var image = Golden();
        Assert.Equal((t with { HueSelection = null }).Apply(image).Rgba, t.Apply(image).Rgba);
        Assert.NotEqual(new byte[] { 255, 255, 255, 9 }, Run(t, 255, 255, 255, 9));
    }

    [Fact]
    public void Range_Partial_NeverSelectsAGrey_SoBrightnessLeavesWhiteAlone()
    {
        var t = new ColorTransform { Brightness = 0.5f, HueSelection = new HueRange(0f, 359f, 0f) };
        foreach (var grey in new[] { new Vector4(0.5f, 0.5f, 0.5f, 1f), Vector4.One, new Vector4(0f, 0f, 0f, 1f), new Vector4(9f, 9f, 9f, 1f) })
        {
            Assert.Equal(0f, t.SelectionWeight(grey));
            Assert.True(SameBits(grey, t.Apply(grey)));
        }
        Assert.Equal(new byte[] { 255, 255, 255, 9 }, Run(t, 255, 255, 255, 9));
        Assert.Equal(new byte[] { 100, 100, 100, 9 }, Run(t, 100, 100, 100, 9));

        // a colour inside the (almost full) range is still brightened
        var red = Run(t, 255, 0, 0, 9);
        Assert.InRange(red[0], 127, 128);
    }

    public static IEnumerable<object[]> RangesWithNoUsableCentreOrWidth() => new[]
    {
        new object[] { new HueRange(float.NaN, float.NaN, float.NaN) },
        new object[] { new HueRange(float.NaN, 40f, 10f) },                     // no centre
        new object[] { new HueRange(120f, float.NaN, 10f) },                    // no width
        new object[] { new HueRange(float.PositiveInfinity, 40f, 10f) },        // an infinite centre has no hue to be centred on either
        new object[] { new HueRange(float.NegativeInfinity, 40f, 10f) },
        new object[] { new HueRange(120f, float.PositiveInfinity, 0f) },        // an infinite width is clamped to 360: every hue
    };

    [Theory]
    [MemberData(nameof(RangesWithNoUsableCentreOrWidth))]
    public void Range_WithNoUsableCentreOrWidth_IsNoRestriction_NotAHueOfZero(HueRange range)
    {
        // "NaN is neutral": a selection that is not a number selects nothing in particular, which is no restriction - it must
        // not quietly become "width 0 round hue 0", i.e. pure red only.
        var t = new ColorTransform { HueShiftDegrees = 90f, Brightness = 0.8f, HueSelection = range };
        var none = t with { HueSelection = null };

        foreach (var c in new[] { Colour(0f), Colour(120f), Colour(250f), new Vector4(0.5f, 0.5f, 0.5f, 1f), Vector4.One })
            Assert.Equal(1f, t.SelectionWeight(c));                     // everything, greys included

        var image = Golden();
        Assert.Equal(none.Apply(image).Rgba, t.Apply(image).Rgba);
        foreach (var c in new[] { Colour(30f, 0.7f, 0.9f), Colour(200f, 0.4f, 0.5f) * 5f, new Vector4(2f, 2f, 2f, 1f) })
            Assert.True(SameBits(none.Apply(c), t.Apply(c)), Inv($"{c} was treated differently from no selection"));

        // white is dimmed, which a restricting range would never do to a grey
        Assert.NotEqual(new byte[] { 255, 255, 255, 9 }, Run(t, 255, 255, 255, 9));
        Assert.Equal(Run(none, 255, 255, 255, 9), Run(t, 255, 255, 255, 9));
    }

    [Fact]
    public void Range_OfWidthZeroAtZero_IsPureRedOnly_UnlikeANaNRange()
    {
        // the contrast that makes the previous test mean something: the SAME numbers, as real zeros, are a real selection
        var literal = new ColorTransform { HueSelection = new HueRange(0f, 0f, 0f) };
        Assert.Equal(1f, literal.SelectionWeight(1f, 0f, 0f));          // exact red
        Assert.Equal(0f, literal.SelectionWeight(Colour(120f)));
        Assert.Equal(0f, literal.SelectionWeight(0.5f, 0.5f, 0.5f));    // and a grey is outside every partial range

        var notANumber = new ColorTransform { HueSelection = new HueRange(float.NaN, float.NaN, 0f) };
        Assert.Equal(1f, notANumber.SelectionWeight(Colour(120f)));
        Assert.Equal(1f, notANumber.SelectionWeight(0.5f, 0.5f, 0.5f));
    }

    [Fact]
    public void Range_NaNFeather_IsAHardEdge_NotNoSelection()
    {
        var nan = new ColorTransform { HueShiftDegrees = 60f, HueSelection = new HueRange(350f, 40f, float.NaN) };
        var hard = new ColorTransform { HueShiftDegrees = 60f, HueSelection = new HueRange(350f, 40f, 0f) };

        foreach (float hue in new[] { 300f, 328f, 335f, 355f, 8f, 13f, 60f, 180f })
            Assert.Equal(hard.SelectionWeight(Colour(hue)), nan.SelectionWeight(Colour(hue)));
        Assert.Equal(0f, nan.SelectionWeight(Colour(180f)));            // it still selects: it is not "everything"
        Assert.Equal(1f, nan.SelectionWeight(Colour(355f)));
        Assert.Equal(hard.Apply(Golden()).Rgba, nan.Apply(Golden()).Rgba);
    }

    // ===================================================================== grey protection

    [Fact]
    public void GreyProtection_NaNThreshold_IsNoProtection_NotAProtectionOfExactGreys()
    {
        // The neutral of a protection is none at all. A threshold read as 0 would protect every exact grey - brightness would
        // then leave white alone - which is a protection, and the opposite of neutral.
        var t = new ColorTransform { Brightness = 0.5f, GreyProtection = new GreyProtection(float.NaN, 0.2f) };
        var none = t with { GreyProtection = null };

        Assert.NotEqual(new byte[] { 255, 255, 255, 9 }, Run(t, 255, 255, 255, 9));              // the grey is dimmed...
        Assert.Equal(Run(none, 255, 255, 255, 9), Run(t, 255, 255, 255, 9));                      // ...exactly as with no protection
        Assert.Equal(new byte[] { 255, 255, 255, 9 }, Run(t with { GreyProtection = new GreyProtection(0f, 0.2f) }, 255, 255, 255, 9));

        foreach (var c in new[] { Colour(40f, 0f, 0.8f), Colour(40f, 0.05f, 0.8f), Colour(40f, 0.5f, 0.8f), Vector4.One })
            Assert.Equal(none.SelectionWeight(c), t.SelectionWeight(c));
        Assert.Equal(none.Apply(Golden()).Rgba, t.Apply(Golden()).Rgba);
    }

    [Fact]
    public void GreyProtection_NaNFeather_IsAHardEdge()
    {
        var nan = new ColorTransform { HueShiftDegrees = 60f, GreyProtection = new GreyProtection(0.1f, float.NaN) };
        var hard = nan with { GreyProtection = new GreyProtection(0.1f, 0f) };

        foreach (float s in new[] { 0f, 0.05f, 0.09f, 0.11f, 0.2f, 1f })
            Assert.Equal(hard.SelectionWeight(Colour(40f, s, 0.8f)), nan.SelectionWeight(Colour(40f, s, 0.8f)));
        Assert.Equal(0f, nan.SelectionWeight(Colour(40f, 0.09f, 0.8f)));    // still a protection
        Assert.Equal(1f, nan.SelectionWeight(Colour(40f, 0.2f, 0.8f)));
        Assert.Equal(hard.Apply(Golden()).Rgba, nan.Apply(Golden()).Rgba);
    }

    [Fact]
    public void GreyProtection_WeightRisesMonotonicallyWithSaturation()
    {
        var t = new ColorTransform { GreyProtection = new GreyProtection(0.10f, 0.20f) };
        float previous = 0f;
        for (int i = 0; i <= 1000; i++)
        {
            float s = i / 1000f;
            float w = t.SelectionWeight(Colour(40f, s, 0.8f));
            Assert.InRange(w, 0f, 1f);
            Assert.True(w >= previous - 1e-5f, Inv($"weight fell from {previous} to {w} at saturation {s}"));
            previous = w;
            if (s <= 0.10f) Assert.InRange(w, 0f, 1e-5f);              // up to the threshold: not touched
            if (s >= 0.30f) Assert.InRange(w, 0.99999f, 1f);           // threshold + feather: fully touched
        }
        Assert.InRange(t.SelectionWeight(Colour(40f, 0.2f, 0.8f)), 0.499f, 0.501f);     // half way up the fade is half weight

        // a smoothstep, like the hue fade: 3t^2 - 2t^3 is 0.15625 a quarter of the way up and 0.84375 three quarters of the way
        Assert.InRange(t.SelectionWeight(Colour(40f, 0.15f, 0.8f)), 0.15625f - 1e-3f, 0.15625f + 1e-3f);
        Assert.InRange(t.SelectionWeight(Colour(40f, 0.25f, 0.8f)), 0.84375f - 1e-3f, 0.84375f + 1e-3f);
    }

    [Fact]
    public void GreyProtection_HardEdge_ProtectsUpToAndIncludingTheThreshold()
    {
        var t = new ColorTransform { GreyProtection = new GreyProtection(0.10f, 0f) };
        Assert.Equal(0f, t.SelectionWeight(Colour(40f, 0.00f, 0.8f)));
        Assert.Equal(0f, t.SelectionWeight(Colour(40f, 0.09f, 0.8f)));
        Assert.Equal(1f, t.SelectionWeight(Colour(40f, 0.11f, 0.8f)));
        Assert.Equal(1f, t.SelectionWeight(Colour(40f, 1.00f, 0.8f)));
    }

    [Fact]
    public void GreyProtection_ThresholdZero_ProtectsExactGreysOnly()
    {
        var t = new ColorTransform { Brightness = 0.5f, GreyProtection = new GreyProtection(0f) };
        Assert.Equal(0f, t.SelectionWeight(0.5f, 0.5f, 0.5f));
        Assert.Equal(0f, t.SelectionWeight((byte)255, (byte)255, (byte)255));
        Assert.Equal(1f, t.SelectionWeight((byte)255, (byte)254, (byte)254));         // saturation 1/255: not a grey
        Assert.Equal(new byte[] { 200, 200, 200, 9 }, Run(t, 200, 200, 200, 9));
        Assert.NotEqual(new byte[] { 200, 199, 199, 9 }, Run(t, 200, 199, 199, 9));
    }

    [Fact]
    public void GreyProtection_LeavesGreysWhiteAndBlackAlone_EvenUnderBrightness()
    {
        var t = new ColorTransform { HueShiftDegrees = 90f, Brightness = 0.5f, GreyProtection = new GreyProtection(0.1f, 0.1f) };
        for (int v = 0; v < 256; v++)
            Assert.Equal(new byte[] { (byte)v, (byte)v, (byte)v, 9 }, Run(t, (byte)v, (byte)v, (byte)v, 9));

        foreach (var grey in new[] { Vector4.One, Vector4.UnitW, new Vector4(0.5f, 0.5f, 0.5f, 1f), new Vector4(4f, 4f, 4f, 1f) })
            Assert.True(SameBits(grey, t.Apply(grey)), Inv($"{grey} moved"));

        // ... and without protection that very brightness does move white
        Assert.NotEqual(new byte[] { 255, 255, 255, 9 }, Run(t with { GreyProtection = null }, 255, 255, 255, 9));
    }

    [Fact]
    public void GreyProtection_NearGreysAreProtectedBelowTheThreshold_AndShiftedAbove()
    {
        var t = new ColorTransform { HueShiftDegrees = 120f, GreyProtection = new GreyProtection(0.10f) };
        Assert.Equal(new byte[] { 140, 128, 128, 255 }, Run(t, 140, 128, 128, 255));    // saturation 12/140 = 0.086
        Assert.NotEqual(new byte[] { 150, 128, 128, 255 }, Run(t, 150, 128, 128, 255)); // saturation 22/150 = 0.147
        Assert.NotEqual(new byte[] { 255, 0, 0, 255 }, Run(t, 255, 0, 0, 255));
    }

    // ===================================================================== the weight

    [Fact]
    public void Weight_IsHueTimesGreyTimesStrength()
    {
        var t = new ColorTransform
        {
            HueShiftDegrees = 30f,
            HueSelection = new HueRange(120f, 40f, 30f),
            GreyProtection = new GreyProtection(0.1f, 0.2f),
            Strength = 0.8f,
        };
        // 35 degrees from the centre is half way down the hue fade (0.5); saturation 0.2 is half way up the grey fade (0.5).
        Assert.InRange(t.SelectionWeight(Colour(155f, 0.2f, 0.8f)), 0.8f * 0.25f - 2e-3f, 0.8f * 0.25f + 2e-3f);
        // dead centre, well saturated: only the strength is left
        Assert.InRange(t.SelectionWeight(Colour(120f, 0.9f, 0.8f)), 0.8f - 1e-6f, 0.8f + 1e-6f);
        // outside the hue range: nothing, whatever the rest says
        Assert.Equal(0f, t.SelectionWeight(Colour(200f, 0.9f, 0.8f)));
        // a protected grey: nothing
        Assert.Equal(0f, t.SelectionWeight(Colour(120f, 0.05f, 0.8f)));
    }

    [Fact]
    public void Weight_WithNoSelection_IsJustTheStrength()
    {
        foreach (float strength in new[] { 0f, 0.25f, 1f })
        {
            var t = new ColorTransform { HueShiftDegrees = 30f, Strength = strength };
            Assert.Equal(strength, t.SelectionWeight(Colour(77f, 0.5f, 0.5f)));
            Assert.Equal(strength, t.SelectionWeight(0.5f, 0.5f, 0.5f));             // a grey is selected when nothing excludes it
        }
    }

    [Fact]
    public void PartialWeight_BlendsTheOriginalAndTheFullResult()
    {
        var t = new ColorTransform { HueShiftDegrees = 70f, Brightness = 0.8f, HueSelection = new HueRange(120f, 40f, 30f) };
        var c = Colour(155f, 0.9f, 0.9f);
        float w = t.SelectionWeight(c);
        Assert.InRange(w, 0.499f, 0.501f);

        var full = (t with { HueSelection = null }).Apply(c);
        var expected = c + (full - c) * w;
        Assert.True(Near(t.Apply(c), expected, 1e-6f), Inv($"got {t.Apply(c)}, expected {expected}"));
    }

    [Fact]
    public void ByteAndFloatWeights_AreTheSameNumber()
    {
        var t = new ColorTransform
        {
            HueSelection = new HueRange(200f, 60f, 40f), GreyProtection = new GreyProtection(0.1f, 0.15f), Strength = 0.9f,
        };
        var image = Golden();
        for (int i = 0; i < image.Rgba.Length; i += 4)
        {
            byte r = image.Rgba[i], g = image.Rgba[i + 1], b = image.Rgba[i + 2];
            Assert.Equal(t.SelectionWeight(r / 255f, g / 255f, b / 255f), t.SelectionWeight(r, g, b));
        }
    }
}
