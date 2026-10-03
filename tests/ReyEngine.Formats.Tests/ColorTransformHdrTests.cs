using System.Numerics;
using ReyEngine.Core.Decoding;
using static ReyEngine.Formats.Tests.ColorTestImages;
using static ReyEngine.Formats.Tests.ColorTestMath;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M813: float colours - what a bin <c>ValueColor</c> holds. 808 shipped components exceed 1.0 (up to 255), so a colour is
/// intensity as well as hue, and the transform must move the hue without clamping the intensity to 1.
/// </summary>
public sealed class ColorTransformHdrTests
{
    private static readonly ColorTransform Plus120 = new() { HueShiftDegrees = 120f };

    [Fact]
    public void Red2_Plus120_IsGreen2()
    {
        var got = Plus120.Apply(new Vector4(2f, 0f, 0f, 1f));
        Assert.True(Near(got, new Vector4(0f, 2f, 0f, 1f), 1e-6f), Inv($"got {got}"));
    }

    [Fact]
    public void Orange255_Plus120_KeepsItsMaximum()
    {
        // (255, 128, 0) is hue 30.1; plus 120 it is hue 150.1, a spring green at the same intensity: (0, 255, 128).
        var got = Plus120.Apply(new Vector4(255f, 128f, 0f, 1f));
        Assert.True(Near(got, new Vector4(0f, 255f, 128f, 1f), 1e-3f), Inv($"got {got}"));
        Assert.InRange(Math.Max(got.X, Math.Max(got.Y, got.Z)), 255f - 1e-3f, 255f + 1e-3f);
    }

    [Fact]
    public void HueShift_KeepsTheIntensityOfAnyHdrColour()
    {
        // Build a colour as (hue, saturation) x intensity, shift it, and it must be the same intensity at the shifted hue.
        var rng = new Xorshift32(0xABCD);
        for (int i = 0; i < 2000; i++)
        {
            float hue = rng.NextUnit() * 360f, sat = 0.2f + rng.NextUnit() * 0.8f;
            float intensity = 1.01f + rng.NextUnit() * 253.99f;          // above 1, up to the largest value shipped
            float degrees = (rng.NextUnit() - 0.5f) * 720f;

            var source = Colour(hue, sat, 1f) * intensity;
            source.W = 0.5f;
            var expected = Colour(hue + degrees, sat, 1f) * intensity;
            var got = new ColorTransform { HueShiftDegrees = degrees }.Apply(source);

            float tolerance = 2e-5f * intensity;
            Assert.True(MathF.Abs(got.X - expected.X) <= tolerance && MathF.Abs(got.Y - expected.Y) <= tolerance
                        && MathF.Abs(got.Z - expected.Z) <= tolerance,
                Inv($"{source} shifted by {degrees} gave {got}, expected {expected}"));
            Assert.InRange(Math.Max(got.X, Math.Max(got.Y, got.Z)), intensity * (1f - 2e-5f), intensity * (1f + 2e-5f));
        }
    }

    [Fact]
    public void NothingClampsToOne()
    {
        // yellow at 3.0, rotated 60 degrees, is green at 3.0
        var green = new ColorTransform { HueShiftDegrees = 60f }.Apply(new Vector4(3f, 3f, 0f, 1f));
        Assert.True(Near(green, new Vector4(0f, 3f, 0f, 1f), 1e-5f), Inv($"got {green}"));

        // saturating a dull HDR colour cannot push it past its own intensity either
        var vivid = new ColorTransform { Saturation = 5f }.Apply(new Vector4(40f, 30f, 20f, 1f));
        Assert.InRange(vivid.X, 40f - 1e-4f, 40f + 1e-4f);
        Assert.True(vivid.Z < 20f, "more saturation lowers the smallest channel");
        Assert.True(vivid.Z >= 0f);
    }

    [Fact]
    public void Brightness_ScalesTheWholeHdrColour_AndFloatsAreNeverClamped()
    {
        var a = new ColorTransform { Brightness = 1.5f }.Apply(new Vector4(2f, 0f, 0f, 1f));
        Assert.True(Near(a, new Vector4(3f, 0f, 0f, 1f), 1e-6f), Inv($"got {a}"));

        var b = new ColorTransform { Brightness = 0.5f }.Apply(new Vector4(255f, 128f, 0f, 1f));
        Assert.True(Near(b, new Vector4(127.5f, 64f, 0f, 1f), 1e-3f), Inv($"got {b}"));

        // a colour at or below 1 may be brightened past 1: a float is an intensity, not a byte
        var c = new ColorTransform { Brightness = 2f }.Apply(new Vector4(0.8f, 0.4f, 0f, 1f));
        Assert.True(Near(c, new Vector4(1.6f, 0.8f, 0f, 1f), 1e-6f), Inv($"got {c}"));

        // while a byte stops at white
        var d = Run(new ColorTransform { Brightness = 2f }, 200, 100, 0, 9);
        Assert.Equal(255, d.Take(3).Max());
    }

    [Fact]
    public void Strength_Half_BlendsAnHdrColourExactly_InFloatRgbAtItsOwnScale()
    {
        // (4,0,0) rotated 120 degrees is (0,4,0). Half way back toward the original, in float RGB at the colour's own scale, is
        // (2,2,0): the blend is original + (transformed - original) x 0.5, so the intensity of the pair (4 -> 0, 0 -> 4) is
        // linear, not an "intermediate hue at full intensity" (which would be (4,4,0)-ish). Every step is exact here.
        var got = new ColorTransform { HueShiftDegrees = 120f, Strength = 0.5f }.Apply(new Vector4(4f, 0f, 0f, 1f));
        Assert.True(SameBits(new Vector4(2f, 2f, 0f, 1f), got), Inv($"got {got}"));

        // the same at the other end of the shipped range, scaled: (255,0,0) -> (127.5, 127.5, 0)
        var big = new ColorTransform { HueShiftDegrees = 120f, Strength = 0.5f }.Apply(new Vector4(255f, 0f, 0f, 1f));
        Assert.True(SameBits(new Vector4(127.5f, 127.5f, 0f, 1f), big), Inv($"got {big}"));

        // and with brightness: (4,0,0) x 0.5 is (2,0,0); half way back toward (4,0,0) is (3,0,0)
        var dim = new ColorTransform { Brightness = 0.5f, Strength = 0.5f }.Apply(new Vector4(4f, 0f, 0f, 1f));
        Assert.True(SameBits(new Vector4(3f, 0f, 0f, 1f), dim), Inv($"got {dim}"));

        // a quarter strength: 4 + (0 - 4) x 0.25 = 3 and 0 + (4 - 0) x 0.25 = 1
        var quarter = new ColorTransform { HueShiftDegrees = 120f, Strength = 0.25f }.Apply(new Vector4(4f, 0f, 0f, 1f));
        Assert.True(SameBits(new Vector4(3f, 1f, 0f, 1f), quarter), Inv($"got {quarter}"));
    }

    [Fact]
    public void APartialWeight_BlendsAnHdrColour_BetweenTheOriginalAndTheFullResult()
    {
        // As PartialWeight_BlendsTheOriginalAndTheFullResult, but with the largest channel above 1, where the colour is
        // normalised, transformed and scaled back before it is blended.
        var t = new ColorTransform { HueShiftDegrees = 70f, Brightness = 0.8f, HueSelection = new HueRange(120f, 40f, 30f) };
        foreach (float intensity in new[] { 2f, 6f, 40f, 255f })
        {
            var c = Colour(155f, 0.9f, 1f) * intensity;
            c.W = 1f;
            float w = t.SelectionWeight(c);
            Assert.InRange(w, 0.499f, 0.501f);                          // 35 degrees into a 30 degree fade of a 40 degree range

            var full = (t with { HueSelection = null }).Apply(c);
            var expected = c + (full - c) * w;
            var got = t.Apply(c);
            Assert.True(Near(got, expected, 1e-6f * intensity), Inv($"intensity {intensity}: got {got}, expected {expected}"));
            // and it really is between them, channel by channel
            Assert.InRange(got.X, MathF.Min(c.X, full.X) - 1e-4f * intensity, MathF.Max(c.X, full.X) + 1e-4f * intensity);
            Assert.InRange(got.Y, MathF.Min(c.Y, full.Y) - 1e-4f * intensity, MathF.Max(c.Y, full.Y) + 1e-4f * intensity);
            Assert.InRange(got.Z, MathF.Min(c.Z, full.Z) - 1e-4f * intensity, MathF.Max(c.Z, full.Z) + 1e-4f * intensity);
        }
    }

    [Fact]
    public void Colorize_KeepsIntensity()
    {
        var got = new ColorTransform { ColorizeHueDegrees = 240f }.Apply(new Vector4(20f, 10f, 0f, 1f));
        var (h, s, v) = HsvOf(got / 20f);
        Assert.True(HueGap(h, 240f) < 0.02f, Inv($"hue {h}"));
        Assert.InRange(s, 1f - 1e-5f, 1f);
        Assert.InRange(got.Z, 20f - 1e-4f, 20f + 1e-4f);
        Assert.True(v > 0.9999f);
    }

    [Fact]
    public void Greys_AtAnyIntensity_AreExactlyThemselves()
    {
        var t = new ColorTransform { HueShiftDegrees = 77f, Saturation = 3f, ColorizeHueDegrees = 200f, Strength = 0.6f };
        foreach (float c in new[] { 0f, 1e-7f, 0.123456789f, 0.5f, 1f, 2f, 3.75f, 100f, 255f, 3e38f })
        {
            var grey = new Vector4(c, c, c, 0.5f);
            Assert.True(SameBits(grey, t.Apply(grey)), Inv($"grey {c} changed to {t.Apply(grey)}"));
        }
    }

    [Fact]
    public void ANearGrey_IsLeftAlone_NotSnappedToGrey_WhenBrightnessIsUntouched()
    {
        // Within a millionth of grey a float colour has no hue to move. Rebuilding it from (v, v, v) would change its bits
        // for nothing, so it is returned as it came - and brightness, the one thing that can act on it, still does.
        var t = new ColorTransform { HueShiftDegrees = 77f, Saturation = 3f, ColorizeHueDegrees = 200f, Strength = 0.9f };
        foreach (var c in new[]
        {
            new Vector4(1f, 1f, 0.99999994f, 1f),            // one step under white
            new Vector4(255f, 255f, 254.99998f, 1f),         // the same at the top of the shipped range
            new Vector4(0.5f, 0.5000001f, 0.5f, 1f),
            new Vector4(100f, 100f, 99.99999f, 1f),
            new Vector4(3e-7f, 0f, 0f, 1f),                  // so dark that its hue is noise
        })
            Assert.True(SameBits(c, t.Apply(c)), Inv($"{c} was rebuilt as {t.Apply(c)}"));

        var dim = new ColorTransform { Brightness = 0.5f }.Apply(new Vector4(1f, 1f, 0.99999994f, 1f));
        Assert.True(Near(dim, new Vector4(0.5f, 0.5f, 0.5f, 1f), 1e-6f), Inv($"got {dim}"));
    }

    [Fact]
    public void Alpha_IsNeverTouched_NotEvenWhenItIsNaN()
    {
        var t = new ColorTransform { HueShiftDegrees = 90f, Brightness = 0.7f };
        foreach (float w in new[] { 0f, 0.37f, 1f, -1f, 1000f, float.NaN, float.PositiveInfinity })
        {
            Assert.True(SameBits(w, t.Apply(new Vector4(1f, 0.2f, 0.1f, w)).W), Inv($"alpha {w} came back changed"));
            Assert.True(SameBits(w, t.Apply(new Vector4(5f, 2f, 0f, w)).W));
            Assert.True(SameBits(w, t.Apply(new Vector4(0.5f, 0.5f, 0.5f, w)).W));
            Assert.True(SameBits(w, t.Apply(new Vector4(-1f, 0.5f, 0.5f, w)).W));    // a colour it cannot read
        }
    }

    // ===================================================================== colours it will not touch

    public static IEnumerable<object[]> Unreadable() => new[]
    {
        new object[] { new Vector4(-0.5f, 0.2f, 0.3f, 1f) },
        new object[] { new Vector4(0.2f, -1e-30f, 0.3f, 1f) },
        new object[] { new Vector4(0.2f, 0.3f, -255f, 1f) },
        new object[] { new Vector4(float.NaN, 0f, 0f, 1f) },
        new object[] { new Vector4(0f, float.NaN, 0f, 1f) },
        new object[] { new Vector4(0f, 0f, float.NaN, 1f) },
        new object[] { new Vector4(float.PositiveInfinity, 0f, 0f, 1f) },
        new object[] { new Vector4(0f, float.NegativeInfinity, 0f, 1f) },
        new object[] { new Vector4(1f, 1f, float.PositiveInfinity, 1f) },

        // Shipped, read by the M813 probe over every champion WAD (5 distinct negative vectors in 438,263): a rim that darkens
        // is a negative reflectionDefinition.fresnelColor, and Miss Fortune's smoke has a negative green and alpha in a plain color.
        new object[] { new Vector4(0.545098f, -0.129412f, 0.937255f, -0.129412f) },   // MissFortune_Skin15_R_mis_child_B / SmokeTrail_AB6 / color
        new object[] { new Vector4(-0.15f, -0.15f, -0.05f, 1f) },                     // Singed_Base_W_Root_01 / Mesh / fresnelColor
        new object[] { new Vector4(-0.2f, -0.2f, -0.2f, -1f) },                       // Velkoz_Skin11_R_Beam_Eye / puffs / fresnelColor
        new object[] { new Vector4(-1f, -1f, -1f, 0f) },                              // JarvanIV_Skin07_W_buf_01 / caustic_lines2 / fresnelColor
    };

    /// <summary>HDR colours exactly as shipped (same probe), with where each was read.</summary>
    public static IEnumerable<object[]> ShippedHdr() => new[]
    {
        new object[] { new Vector4(255f, 255f, 255f, 255f) },                         // Kalista_Skin14_BA_Spear_missed / dust2 / color: a grey of 255
        new object[] { new Vector4(5f, 1f, 1f, 1f) },                                 // Aurora_Skin20_R_tar / EnergyPulses / color
        new object[] { new Vector4(0.5f, 0.5f, 5f, 0f) },                             // DoomBots_Brand_Base_W_POF_tar / FireUpMesh_Aoe / color
        new object[] { new Vector4(3f, 1.75f, 1f, 1f) },                              // AurelionSol_Skin01_Death / AvatarFakeBody / color
        new object[] { new Vector4(0f, 3f, 1f, 1f) },                                 // Soraka_Skin06_Z_IdlePulse / StaffStaggered1 / fresnelColor
        new object[] { new Vector4(3f, 3f, 3f, 0.6f) },                               // Heimerdinger_Base_Death_Explosion_01 / Explosion_Add / color: a grey of 3
        new object[] { new Vector4(1.85f, 0.2f, 0.13333334f, 0.7529412f) },           // Zilean_Base_Q_Indicator_Red / Ring_Ally / color
        new object[] { new Vector4(1f, 0.44705883f, 1.7882353f, 0f) },                // Varus_Skin07_Q_Channel / Blinkies / color
        new object[] { new Vector4(1.1666666f, 1.3137255f, 1.5392157f, 0f) },         // Jinx_Skin20_Q_Rocket_Cas / Explosion_Smoke / color
        new object[] { new Vector4(1.2450975f, 1.2450975f, 1.2450975f, 0f) },         // Jinx_Skin03_Q_Rocket_Trail_02 / Smoke_Trail / color: a grey of 1.245
    };

    [Theory]
    [MemberData(nameof(ShippedHdr))]
    public void ShippedHdrColours_KeepTheirIntensity_AndTheirGreysStayExact(Vector4 c)
    {
        float max = Math.Max(c.X, Math.Max(c.Y, c.Z));
        bool grey = c.X == c.Y && c.Y == c.Z;
        foreach (var t in new[]
        {
            new ColorTransform { HueShiftDegrees = 40f },
            new ColorTransform { HueShiftDegrees = -137f, Saturation = 1.2f },
            new ColorTransform { ColorizeHueDegrees = 200f },
            new ColorTransform { HueShiftDegrees = 90f, Strength = 0.5f, HueSelection = new HueRange(0f, 359f) },
        })
        {
            var got = t.Apply(c);
            Assert.True(SameBits(c.W, got.W), "alpha moved");
            if (grey) { Assert.True(SameBits(c, got), Inv($"grey {c} became {got}")); continue; }
            Assert.True(float.IsFinite(got.X) && float.IsFinite(got.Y) && float.IsFinite(got.Z));
            if (t.Strength == 1f && t.Saturation == 1f)
                Assert.InRange(Math.Max(got.X, Math.Max(got.Y, got.Z)), max * (1f - 2e-5f), max * (1f + 2e-5f));
        }
    }

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void NegativeNaNOrInfiniteComponents_AreReturnedUnchanged_AndNeverCrash(Vector4 c)
    {
        var aggressive = new ColorTransform
        {
            HueShiftDegrees = 90f, Saturation = 2f, Brightness = 3f, ColorizeHueDegrees = 10f, Strength = 0.5f,
        };
        Assert.False(ColorTransform.CanTransform(c));
        Assert.False(ColorTransform.CanTransform(c.X, c.Y, c.Z));
        Assert.True(SameBits(c, aggressive.Apply(c)), Inv($"{c} was changed to {aggressive.Apply(c)}"));
        Assert.Equal(0f, aggressive.SelectionWeight(c));
        Assert.Equal(0f, aggressive.SelectionWeight(c.X, c.Y, c.Z));

        var inPlace = new[] { c };
        aggressive.ApplyInPlace(inPlace);
        Assert.True(SameBits(c, inPlace[0]));
    }

    [Fact]
    public void NegativeZero_CountsAsZero()
    {
        Assert.True(ColorTransform.CanTransform(new Vector4(-0f, 0.5f, 0.5f, 1f)));
        Assert.True(ColorTransform.CanTransform(Vector4.Zero));
        Assert.True(ColorTransform.CanTransform(new Vector4(255f, 255f, 255f, float.NaN)));        // alpha is not examined
        var got = Plus120.Apply(new Vector4(-0f, 0.5f, 0.5f, 1f));
        Assert.True(float.IsFinite(got.X) && float.IsFinite(got.Y) && float.IsFinite(got.Z));
    }

    [Fact]
    public void AnOverflowingBrightness_LeavesTheColourAloneInsteadOfWritingInfinity()
    {
        var c = new Vector4(3e38f, 1e38f, 0f, 1f);
        var got = new ColorTransform { Brightness = 100f }.Apply(c);
        Assert.True(SameBits(c, got), Inv($"got {got}"));
        // the same colour at a brightness that fits is transformed
        var fits = new ColorTransform { HueShiftDegrees = 120f }.Apply(c);
        Assert.True(float.IsFinite(fits.X) && float.IsFinite(fits.Y) && float.IsFinite(fits.Z));
        Assert.False(SameBits(c, fits));
    }

    // ===================================================================== selection on HDR

    [Fact]
    public void TheSelectionWeight_DoesNotDependOnIntensity()
    {
        var t = new ColorTransform
        {
            HueSelection = new HueRange(60f, 50f, 30f), GreyProtection = new GreyProtection(0.1f, 0.2f), Strength = 0.9f,
        };
        var rng = new Xorshift32(0x5150);
        for (int i = 0; i < 500; i++)
        {
            var c = Colour(rng.NextUnit() * 360f, 0.05f + rng.NextUnit() * 0.95f, 0.1f + rng.NextUnit() * 0.9f);
            float baseline = t.SelectionWeight(c);
            // dark colours recover their hue less precisely (a chroma of 5e-5 at k = 0.01), hence 2e-4 rather than float noise
            foreach (float k in new[] { 0.01f, 1f, 7f, 255f })
                Assert.InRange(t.SelectionWeight(c * k), baseline - 2e-4f, baseline + 2e-4f);
        }
    }

    [Fact]
    public void ASelectedHdrColour_IsFullyTransformed_AndAnUnselectedOneIsBitIdentical()
    {
        var t = new ColorTransform { HueShiftDegrees = 60f, HueSelection = new HueRange(0f, 60f) };       // the reds
        var red = new Vector4(6f, 0.5f, 0.2f, 1f);
        var blue = new Vector4(0.5f, 0.6f, 9f, 1f);

        Assert.Equal(1f, t.SelectionWeight(red));
        Assert.Equal(0f, t.SelectionWeight(blue));
        Assert.True(SameBits(blue, t.Apply(blue)));
        Assert.True(SameBits((t with { HueSelection = null }).Apply(red), t.Apply(red)));
        Assert.False(SameBits(red, t.Apply(red)));
    }

    [Fact]
    public void AMidAndAHighIntensityOfTheSameHue_MoveByTheSameAngle()
    {
        var t = new ColorTransform { HueShiftDegrees = 100f };
        foreach (float k in new[] { 0.5f, 1f, 2f, 40f, 255f })
        {
            var (h, s, v) = HsvOf(t.Apply(Colour(30f, 0.8f, 1f) * k) / k);
            Assert.True(HueGap(h, 130f) < 0.02f, Inv($"at intensity {k}: hue {h}"));
            Assert.InRange(s, 0.8f - 2e-5f, 0.8f + 2e-5f);
            Assert.InRange(v, 1f - 2e-5f, 1f + 2e-5f);
        }
    }

    // ===================================================================== both paths agree

    [Fact]
    public void TheByteAndFloatPaths_AgreeOnEveryTexelOfTheGoldenImage()
    {
        // Same kernel, so the byte result is the float result rounded - for every brightness at or below 1, where a byte does
        // not clamp. The byte path also skips greys outright, which must not change that.
        foreach (var t in new[]
        {
            new ColorTransform { HueShiftDegrees = 75f, Saturation = 1.3f, Brightness = 0.9f },
            new ColorTransform { ColorizeHueDegrees = 210f, Saturation = 0.8f, HueSelection = new HueRange(30f, 90f, 40f) },
            new ColorTransform { HueShiftDegrees = -33f, GreyProtection = new GreyProtection(0.1f, 0.2f), Strength = 0.6f },
            new ColorTransform { Brightness = 0.4f, HueSelection = new HueRange(200f, 120f, 20f) },
        })
        {
            var image = Golden();
            var bytes = t.Apply(image).Rgba;
            for (int i = 0; i < image.Rgba.Length; i += 4)
            {
                var f = t.Apply(FromBytes(image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2], image.Rgba[i + 3]));
                Assert.Equal(Byte(f.X), bytes[i]);
                Assert.Equal(Byte(f.Y), bytes[i + 1]);
                Assert.Equal(Byte(f.Z), bytes[i + 2]);
                Assert.Equal(image.Rgba[i + 3], bytes[i + 3]);
            }
        }
    }
}
