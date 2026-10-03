using System.Numerics;
using System.Text.Json;
using ReyEngine.Core.Decoding;
using static ReyEngine.Formats.Tests.ColorTestImages;
using static ReyEngine.Formats.Tests.ColorTestMath;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M813: what each slider of <see cref="ColorTransform"/> does, one at a time, on bytes and on floats. The selection (hue
/// range, grey protection) is in <see cref="ColorTransformSelectionTests"/>, HDR colours in
/// <see cref="ColorTransformHdrTests"/>, and the bit-for-bit promises M815's writer relies on in
/// <see cref="ColorTransformExactnessTests"/>.
///
/// <para>Float tests use random colours from a fixed stream, so they cover the colour cube without a flaky run.</para>
/// </summary>
public sealed class ColorTransformTests
{
    /// <summary>Random float colours with enough saturation and value that their hue is well defined.</summary>
    private static List<(float H, float S, float V)> RandomHsv(int count, uint seed, float minS = 0.1f, float minV = 0.1f)
    {
        var rng = new Xorshift32(seed);
        var list = new List<(float, float, float)>(count);
        for (int i = 0; i < count; i++)
            list.Add((rng.NextUnit() * 360f, minS + rng.NextUnit() * (1f - minS), minV + rng.NextUnit() * (1f - minV)));
        return list;
    }

    // ===================================================================== hue

    [Fact]
    public void HueShift_PureRedPlus120_IsPureGreen()
    {
        var t = new ColorTransform { HueShiftDegrees = 120 };

        var bytes = Run(t, 255, 0, 0, 77);
        Assert.InRange(bytes[0], 0, 1);
        Assert.InRange(bytes[1], 254, 255);
        Assert.InRange(bytes[2], 0, 1);
        Assert.Equal(77, bytes[3]);

        var f = t.Apply(new Vector4(1f, 0f, 0f, 0.25f));
        Assert.True(Near(f, new Vector4(0f, 1f, 0f, 0.25f), 1e-6f), Inv($"got {f}"));
    }

    [Theory]
    [InlineData(255, 0, 0, -120, 0, 0, 255)]       // red - 120 = blue
    [InlineData(0, 255, 0, 120, 0, 0, 255)]        // green + 120 = blue
    [InlineData(0, 0, 255, 120, 255, 0, 0)]        // blue + 120 = red (wraps)
    [InlineData(0, 0, 255, -240, 255, 0, 0)]       // -240 is +120
    [InlineData(255, 255, 0, 60, 0, 255, 0)]       // yellow + 60 = green
    [InlineData(255, 0, 0, 60, 255, 255, 0)]       // red + 60 = yellow
    [InlineData(255, 0, 0, 180, 0, 255, 255)]      // red + 180 = cyan
    public void HueShift_RotatesThePrimaries(int r, int g, int b, float degrees, int er, int eg, int eb)
    {
        var t = new ColorTransform { HueShiftDegrees = degrees };
        var got = Run(t, (byte)r, (byte)g, (byte)b, 200);
        Assert.InRange(got[0], Math.Max(0, er - 1), Math.Min(255, er + 1));
        Assert.InRange(got[1], Math.Max(0, eg - 1), Math.Min(255, eg + 1));
        Assert.InRange(got[2], Math.Max(0, eb - 1), Math.Min(255, eb + 1));
        Assert.Equal(200, got[3]);
    }

    [Theory]
    [InlineData(360f)]
    [InlineData(-360f)]
    [InlineData(720f)]
    [InlineData(-1080f)]
    public void HueShift_AWholeTurn_IsTheIdentity_BitForBit(float degrees)
    {
        var t = new ColorTransform { HueShiftDegrees = degrees };
        Assert.True(t.IsIdentity);

        var image = Golden();
        Assert.Equal(image.Rgba, t.Apply(image).Rgba);

        foreach (var c in new[] { new Vector4(1f, 0f, 0f, 1f), new Vector4(0.3f, 0.6f, 0.1f, 0.5f), new Vector4(7f, 2f, 0.5f, 1f) })
            Assert.True(SameBits(c, t.Apply(c)), Inv($"{c} changed under a hue shift of {degrees}"));
    }

    [Fact]
    public void HueShift_MoreThanATurn_LosesWholeTurnsOnly()
    {
        var image = Golden();
        var plain = new ColorTransform { HueShiftDegrees = 120 }.Apply(image).Rgba;
        // 480 = 120 + 360 and 840 = 120 + 720: the reduction is an exact fmod, so the very same bytes come out.
        Assert.Equal(plain, new ColorTransform { HueShiftDegrees = 480 }.Apply(image).Rgba);
        Assert.Equal(plain, new ColorTransform { HueShiftDegrees = 840 }.Apply(image).Rgba);

        // -240 is the same rotation as +120, reached from the other side of the circle: the same colours to within a byte.
        var other = new ColorTransform { HueShiftDegrees = -240 }.Apply(image).Rgba;
        for (int i = 0; i < plain.Length; i++)
            Assert.InRange(other[i], plain[i] - 1, plain[i] + 1);
    }

    [Fact]
    public void HueShift_NegativeUndoesPositive()
    {
        var t = new ColorTransform { HueShiftDegrees = 77f };
        var back = new ColorTransform { HueShiftDegrees = -77f };
        foreach (var (h, s, v) in RandomHsv(500, 0x1111))
        {
            var c = Colour(h, s, v);
            Assert.True(Near(back.Apply(t.Apply(c)), c, 3e-6f), Inv($"{c} did not come back"));
        }
    }

    [Fact]
    public void HueShift_MovesOnlyTheHue()
    {
        var rng = new Xorshift32(0x2222);
        foreach (var (h, s, v) in RandomHsv(1000, 0x3333))
        {
            float degrees = (rng.NextUnit() - 0.5f) * 800f;         // -400..400: past a turn both ways
            var shifted = new ColorTransform { HueShiftDegrees = degrees }.Apply(Colour(h, s, v));
            var (h2, s2, v2) = HsvOf(shifted);

            Assert.True(HueGap(h2, h + degrees) < 0.02f, Inv($"hue {h} + {degrees} gave {h2}"));
            Assert.InRange(s2, s - 2e-5f, s + 2e-5f);
            Assert.InRange(v2, v - 2e-5f, v + 2e-5f);
        }
    }

    [Fact]
    public void HueShift_ByteTexelsKeepTheirSaturationAndValueToAByte()
    {
        var image = Golden();
        var t = new ColorTransform { HueShiftDegrees = 137f };
        var shifted = t.Apply(image).Rgba;

        for (int i = 0; i < image.Rgba.Length; i += 4)
        {
            Assert.Equal(image.Rgba[i + 3], shifted[i + 3]);
            int maxBefore = Math.Max(image.Rgba[i], Math.Max(image.Rgba[i + 1], image.Rgba[i + 2]));
            int maxAfter = Math.Max(shifted[i], Math.Max(shifted[i + 1], shifted[i + 2]));
            int minBefore = Math.Min(image.Rgba[i], Math.Min(image.Rgba[i + 1], image.Rgba[i + 2]));
            int minAfter = Math.Min(shifted[i], Math.Min(shifted[i + 1], shifted[i + 2]));
            // value is the largest channel and the smallest channel is value x (1 - saturation): neither moves by more than rounding.
            Assert.InRange(maxAfter, maxBefore - 1, maxBefore + 1);
            Assert.InRange(minAfter, minBefore - 1, minBefore + 1);
        }
    }

    // ===================================================================== saturation

    [Fact]
    public void Saturation_Zero_IsTheGreyOfTheSameValue_Bytes()
    {
        var image = Golden();
        var grey = new ColorTransform { Saturation = 0f }.Apply(image).Rgba;
        for (int i = 0; i < image.Rgba.Length; i += 4)
        {
            byte max = Math.Max(image.Rgba[i], Math.Max(image.Rgba[i + 1], image.Rgba[i + 2]));
            Assert.Equal(max, grey[i]);
            Assert.Equal(max, grey[i + 1]);
            Assert.Equal(max, grey[i + 2]);
            Assert.Equal(image.Rgba[i + 3], grey[i + 3]);
        }
    }

    [Fact]
    public void Saturation_Zero_IsTheGreyOfTheSameValue_Floats()
    {
        var t = new ColorTransform { Saturation = 0f };
        foreach (var (h, s, v) in RandomHsv(500, 0x4444, minS: 0f, minV: 0f))
        {
            var c = Colour(h, s, v);
            var g = t.Apply(c);
            float max = Math.Max(c.X, Math.Max(c.Y, c.Z));
            Assert.True(SameBits(max, g.X) && SameBits(max, g.Y) && SameBits(max, g.Z), Inv($"{c} -> {g}, expected a grey of {max}"));
        }
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1.5f)]
    [InlineData(3f)]
    public void Saturation_ScalesAndClampsAtOne(float factor)
    {
        var t = new ColorTransform { Saturation = factor };
        foreach (var (h, s, v) in RandomHsv(500, 0x5555, minS: 0.1f, minV: 0.1f))
        {
            var (h2, s2, v2) = HsvOf(t.Apply(Colour(h, s, v)));
            Assert.InRange(s2, MathF.Min(1f, s * factor) - 3e-5f, MathF.Min(1f, s * factor) + 3e-5f);
            Assert.InRange(v2, v - 2e-5f, v + 2e-5f);
            if (s2 > 0.05f) Assert.True(HueGap(h2, h) < 0.05f, Inv($"hue {h} became {h2}"));
        }
    }

    // ===================================================================== brightness

    [Fact]
    public void Brightness_ScalesTheValue_Bytes()
    {
        var t = new ColorTransform { Brightness = 2f };
        var got = Run(t, 100, 50, 25, 9);
        Assert.InRange(got[0], 199, 201);
        Assert.InRange(got[1], 99, 101);
        Assert.InRange(got[2], 49, 51);
        Assert.Equal(9, got[3]);

        var dim = Run(new ColorTransform { Brightness = 0.5f }, 255, 128, 0, 255);
        Assert.InRange(dim[0], 127, 128);
        Assert.InRange(dim[1], 63, 65);
        Assert.Equal(0, dim[2]);
    }

    [Fact]
    public void Brightness_ClampsTheValueAtWhite_NotEachChannel_Bytes()
    {
        // (200,100,50) is hue 20, saturation 0.75, value 0.78. Doubling the value clamps at 1, and a clamp of the VALUE keeps
        // the hue and saturation; clamping each channel separately would give (255,200,100), a different colour.
        var got = Run(new ColorTransform { Brightness = 2f }, 200, 100, 50, 255);
        Assert.Equal(255, Math.Max(got[0], Math.Max(got[1], got[2])));
        var (h0, s0, _) = HsvOf(FromBytes(200, 100, 50));
        var (h1, s1, v1) = HsvOf(FromBytes(got[0], got[1], got[2]));
        Assert.True(HueGap(h0, h1) < 1.5f, Inv($"hue {h0} became {h1}"));
        Assert.InRange(s1, s0 - 0.01f, s0 + 0.01f);
        Assert.Equal(1f, v1);
    }

    [Fact]
    public void Brightness_Zero_IsBlack_AndKeepsAlpha()
    {
        var got = Run(new ColorTransform { Brightness = 0f }, 200, 100, 50, 33);
        Assert.Equal(new byte[] { 0, 0, 0, 33 }, got);
    }

    // ===================================================================== colourise

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    [InlineData(200f)]
    [InlineData(359.5f)]
    [InlineData(-90f)]
    [InlineData(560f)]
    public void Colorize_EverySelectedColourTakesTheTargetHue_Floats(float target)
    {
        var t = new ColorTransform { ColorizeHueDegrees = target };
        foreach (var (h, s, v) in RandomHsv(500, 0x6666))
        {
            var (h2, s2, v2) = HsvOf(t.Apply(Colour(h, s, v)));
            Assert.True(HueGap(h2, target) < 0.02f, Inv($"colour at hue {h} became hue {h2}, wanted {target}"));
            Assert.InRange(s2, s - 2e-5f, s + 2e-5f);       // its own saturation
            Assert.InRange(v2, v - 2e-5f, v + 2e-5f);       // its own value
        }
    }

    [Fact]
    public void Colorize_EverySelectedColourTakesTheTargetHue_Bytes()
    {
        // Bytes quantise the hue; colours with a chroma of a quarter or more pin it to about half a degree.
        var image = Golden();
        var shifted = new ColorTransform { ColorizeHueDegrees = 200f }.Apply(image).Rgba;
        int checkedTexels = 0;
        for (int i = 0; i < image.Rgba.Length; i += 4)
        {
            int max = Math.Max(image.Rgba[i], Math.Max(image.Rgba[i + 1], image.Rgba[i + 2]));
            int min = Math.Min(image.Rgba[i], Math.Min(image.Rgba[i + 1], image.Rgba[i + 2]));
            if (max - min < 64) continue;
            var (h, _, _) = HsvOf(FromBytes(shifted[i], shifted[i + 1], shifted[i + 2]));
            Assert.True(HueGap(h, 200f) < 1.5f, Inv($"texel {i / 4} came out at hue {h}"));
            checkedTexels++;
        }
        Assert.True(checkedTexels > 5000, "the check must actually look at a lot of texels");
    }

    [Fact]
    public void Colorize_KeepsEachColoursOwnSaturation_ScaledByTheSaturationMultiplier()
    {
        var t = new ColorTransform { ColorizeHueDegrees = 280f, Saturation = 0.5f };
        foreach (var (h, s, v) in RandomHsv(300, 0x7777))
        {
            var (h2, s2, v2) = HsvOf(t.Apply(Colour(h, s, v)));
            Assert.InRange(s2, s * 0.5f - 3e-5f, s * 0.5f + 3e-5f);
            Assert.InRange(v2, v - 2e-5f, v + 2e-5f);
            if (s2 > 0.05f) Assert.True(HueGap(h2, 280f) < 0.05f);
        }
    }

    [Fact]
    public void Colorize_LeavesGreysAndWhiteExactlyAlone()
    {
        var t = new ColorTransform { ColorizeHueDegrees = 123f };
        for (int v = 0; v < 256; v++)
            Assert.Equal(new byte[] { (byte)v, (byte)v, (byte)v, 9 }, Run(t, (byte)v, (byte)v, (byte)v, 9));

        foreach (var white in new[] { new Vector4(1f, 1f, 1f, 1f), new Vector4(0.5f, 0.5f, 0.5f, 1f), new Vector4(4f, 4f, 4f, 0.2f), Vector4.Zero })
            Assert.True(SameBits(white, t.Apply(white)), Inv($"{white} was tinted"));
    }

    [Fact]
    public void Colorize_ReplacesTheHueShift_NotAddsToIt()
    {
        var image = Golden();
        var plain = new ColorTransform { ColorizeHueDegrees = 200f }.Apply(image).Rgba;
        var withShift = new ColorTransform { ColorizeHueDegrees = 200f, HueShiftDegrees = 77f }.Apply(image).Rgba;
        Assert.Equal(plain, withShift);
    }

    [Fact]
    public void Colorize_AloneIsNotTheIdentity_AndNeitherIsAnyOtherEffectiveChange()
    {
        Assert.False(new ColorTransform { ColorizeHueDegrees = 0f }.IsIdentity);
        Assert.False(new ColorTransform { HueShiftDegrees = 1f }.IsIdentity);
        Assert.False(new ColorTransform { Saturation = 1.01f }.IsIdentity);
        Assert.False(new ColorTransform { Brightness = 0.99f }.IsIdentity);

        Assert.True(new ColorTransform().IsIdentity);
        Assert.True(ColorTransform.Identity.IsIdentity);
        Assert.True(new ColorTransform { Strength = 0f, HueShiftDegrees = 90f, ColorizeHueDegrees = 10f }.IsIdentity);
        // a selection or a protection cannot change a colour by itself
        Assert.True(new ColorTransform { HueSelection = new HueRange(0, 30), GreyProtection = new GreyProtection(0.5f) }.IsIdentity);
    }

    // ===================================================================== strength

    [Fact]
    public void Strength_Half_BlendsTheOriginalAndTheFullResultInRgb()
    {
        var full = new ColorTransform { HueShiftDegrees = 120f, Brightness = 0.8f };
        var half = full with { Strength = 0.5f };
        var image = Golden();

        var fullBytes = full.Apply(image).Rgba;
        var halfBytes = half.Apply(image).Rgba;
        for (int i = 0; i < image.Rgba.Length; i += 4)
            for (int c = 0; c < 3; c++)
            {
                float expected = (image.Rgba[i + c] + fullBytes[i + c]) / 2f;
                Assert.InRange(halfBytes[i + c], expected - 1.01f, expected + 1.01f);   // both ends are rounded bytes
            }
    }

    [Fact]
    public void Strength_IsDialledBetweenNothingAndEverything_Monotonically()
    {
        var c = new Vector4(0.9f, 0.2f, 0.1f, 1f);
        var full = new ColorTransform { HueShiftDegrees = 150f }.Apply(c);
        float previous = 0f;
        for (float s = 0f; s <= 1.0001f; s += 0.05f)
        {
            var got = new ColorTransform { HueShiftDegrees = 150f, Strength = s }.Apply(c);
            float distance = Vector4.Distance(got, c);
            Assert.True(distance >= previous - 1e-6f, Inv($"strength {s} moved the colour less than a smaller strength did"));
            previous = distance;
        }
        Assert.True(Near(new ColorTransform { HueShiftDegrees = 150f, Strength = 1f }.Apply(c), full, 0f));
        Assert.True(SameBits(c, new ColorTransform { HueShiftDegrees = 150f, Strength = 0f }.Apply(c)));
    }

    [Fact]
    public void Strength_PastTheEnds_IsClamped()
    {
        var image = Golden();
        var one = new ColorTransform { HueShiftDegrees = 50f, Strength = 1f }.Apply(image).Rgba;
        Assert.Equal(one, new ColorTransform { HueShiftDegrees = 50f, Strength = 7f }.Apply(image).Rgba);
        Assert.Equal(image.Rgba, new ColorTransform { HueShiftDegrees = 50f, Strength = -3f }.Apply(image).Rgba);
    }

    // ===================================================================== the parameters themselves

    [Fact]
    public void Parameters_NaNIsNeutral_AndNothingNeverCrashes()
    {
        var nan = new ColorTransform
        {
            HueShiftDegrees = float.NaN, Saturation = float.NaN, Brightness = float.NaN, Strength = float.NaN,
            ColorizeHueDegrees = float.NaN,
        };
        Assert.True(nan.IsIdentity);
        var image = Golden();
        Assert.Equal(image.Rgba, nan.Apply(image).Rgba);

        // Infinite and negative factors are clamped, and none of them can turn a texel into NaN or an out-of-range byte.
        foreach (var t in new[]
        {
            new ColorTransform { Saturation = float.PositiveInfinity, Brightness = float.PositiveInfinity },
            new ColorTransform { Saturation = float.NegativeInfinity, Brightness = float.NegativeInfinity },
            new ColorTransform { Saturation = -5f, Brightness = -5f, HueShiftDegrees = float.PositiveInfinity },
            new ColorTransform { HueSelection = new HueRange(float.NaN, float.NaN, float.NaN), HueShiftDegrees = 30f },
            new ColorTransform { GreyProtection = new GreyProtection(float.NaN, float.NaN), HueShiftDegrees = 30f },
        })
        {
            var result = t.Apply(image);
            Assert.Equal(image.Rgba.Length, result.Rgba.Length);
            var f = t.Apply(new Vector4(0.7f, 0.3f, 0.2f, 1f));
            Assert.True(float.IsFinite(f.X) && float.IsFinite(f.Y) && float.IsFinite(f.Z), Inv($"{t} gave {f}"));
            Assert.True(float.IsFinite(t.SelectionWeight(0.7f, 0.3f, 0.2f)));
        }
    }

    [Fact]
    public void Parameters_AreAnImmutableValue()
    {
        var a = new ColorTransform { HueShiftDegrees = 30f, HueSelection = new HueRange(10, 20, 5) };
        var b = new ColorTransform { HueShiftDegrees = 30f, HueSelection = new HueRange(10, 20, 5) };
        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { Strength = 0.5f });
        Assert.Equal(1f, new ColorTransform().Strength);
        Assert.Equal(1f, new ColorTransform().Saturation);
        Assert.Equal(1f, new ColorTransform().Brightness);
        Assert.Null(new ColorTransform().ColorizeHueDegrees);
    }

    private static string[] Keys(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Parameters_SurviveAJsonRoundTrip()
    {
        // The Chroma Studio's recipe saves the transform in the project: it must come back equal.
        var t = new ColorTransform
        {
            HueShiftDegrees = 40.5f, Saturation = 1.2f, Brightness = 0.9f, ColorizeHueDegrees = 200f,
            HueSelection = new HueRange(350f, 40f, 10f), GreyProtection = new GreyProtection(0.1f, 0.05f), Strength = 0.75f,
        };
        string json = JsonSerializer.Serialize(t);
        var back = JsonSerializer.Deserialize<ColorTransform>(json);
        Assert.Equal(t, back);

        var plain = JsonSerializer.Deserialize<ColorTransform>(JsonSerializer.Serialize(new ColorTransform { HueShiftDegrees = 12f }));
        Assert.Equal(new ColorTransform { HueShiftDegrees = 12f }, plain);
        Assert.Equal(ColorTransform.Identity, JsonSerializer.Deserialize<ColorTransform>(JsonSerializer.Serialize(ColorTransform.Identity)));
    }

    [Fact]
    public void Json_IsExactlyTheRecipeSchema_AndNeverCarriesTheDerivedIsIdentity()
    {
        // These names ARE the persisted recipe: renaming one silently drops a user's saved setting. IsIdentity is derived from
        // them, so writing it would only bake a stale answer into every recipe.
        var t = new ColorTransform
        {
            HueShiftDegrees = 40.5f, Saturation = 1.2f, Brightness = 0.9f, ColorizeHueDegrees = 200f,
            HueSelection = new HueRange(350f, 40f, 10f), GreyProtection = new GreyProtection(0.1f, 0.05f), Strength = 0.75f,
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(t));
        var root = doc.RootElement;

        Assert.Equal(
            new[] { "Brightness", "ColorizeHueDegrees", "GreyProtection", "HueSelection", "HueShiftDegrees", "Saturation", "Strength" },
            Keys(root));
        Assert.False(root.TryGetProperty("IsIdentity", out _), "IsIdentity must not be written");
        Assert.Equal(new[] { "CenterDegrees", "FeatherDegrees", "WidthDegrees" }, Keys(root.GetProperty("HueSelection")));
        Assert.Equal(new[] { "Feather", "Threshold" }, Keys(root.GetProperty("GreyProtection")));

        // the same for an identity (which would have written "IsIdentity": true) and for one with the optional parts unset
        foreach (var plain in new[] { ColorTransform.Identity, new ColorTransform { HueShiftDegrees = 360f }, new ColorTransform { Strength = 0f } })
        {
            using var plainDoc = JsonDocument.Parse(JsonSerializer.Serialize(plain));
            Assert.False(plainDoc.RootElement.TryGetProperty("IsIdentity", out _));
            Assert.Equal(
                new[] { "Brightness", "ColorizeHueDegrees", "GreyProtection", "HueSelection", "HueShiftDegrees", "Saturation", "Strength" },
                Keys(plainDoc.RootElement));
        }
    }

    [Fact]
    public void Json_AnOlderRecipeThatDidCarryIsIdentity_StillLoads_AndTheValueIsRecomputed()
    {
        // IsIdentity is derived, so whatever a file claims for it is ignored: a stale "true" must not make a real change an identity.
        var loaded = JsonSerializer.Deserialize<ColorTransform>("{\"HueShiftDegrees\":120,\"IsIdentity\":true,\"Unknown\":1}");
        Assert.NotNull(loaded);
        Assert.Equal(new ColorTransform { HueShiftDegrees = 120f }, loaded);
        Assert.False(loaded!.IsIdentity);
    }

    [Fact]
    public void Json_RefusesANonFiniteNumber_RatherThanWritingIt()
    {
        // A recipe holds finite values (a slider never produces any other). The serializer refuses a NaN or infinite float by
        // default, so a bad value fails loudly at save time instead of being written as something that cannot be read back.
        // Apply() would treat the same NaN as neutral (see Parameters_NaNIsNeutral...); persisting it is a different matter.
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Serialize(new ColorTransform { HueShiftDegrees = float.NaN }));
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Serialize(new ColorTransform { HueSelection = new HueRange(float.PositiveInfinity, 10f) }));
    }

    // ===================================================================== the entry points

    [Fact]
    public void ApplyToAnImage_ReturnsANewImage_AndNeverModifiesTheSource()
    {
        var image = Golden();
        var before = (byte[])image.Rgba.Clone();
        var result = new ColorTransform { HueShiftDegrees = 90f }.Apply(image);

        Assert.NotSame(image, result);
        Assert.NotSame(image.Rgba, result.Rgba);
        Assert.Equal(before, image.Rgba);
        Assert.NotEqual(before, result.Rgba);
        Assert.Equal(image.Width, result.Width);
        Assert.Equal(image.Height, result.Height);

        // even an identity hands back a copy, as TextureAdjustment does
        var same = ColorTransform.Identity.Apply(image);
        Assert.NotSame(image.Rgba, same.Rgba);
        Assert.Equal(before, same.Rgba);
    }

    [Fact]
    public void ApplyInPlace_RejectsARaggedBuffer_AndAcceptsAnEmptyOne()
    {
        var t = new ColorTransform { HueShiftDegrees = 90f };
        Assert.Throws<ArgumentException>(() => t.ApplyInPlace(new byte[7]));
        Assert.Throws<ArgumentException>(() => t.ApplyInPlace(new byte[3]));
        Assert.Equal(0, t.ApplyInPlace(Array.Empty<byte>()));
        Assert.Equal(0, t.ApplyInPlace(Array.Empty<Vector4>()));
        Assert.Throws<ArgumentNullException>(() => t.Apply((TextureImage)null!));
    }

    [Fact]
    public void ApplyInPlace_OverFloatColours_IsApplyOneByOne()
    {
        var t = new ColorTransform { HueShiftDegrees = 33f, Saturation = 1.3f, Brightness = 1.2f, HueSelection = new HueRange(30, 120, 30) };
        var rng = new Xorshift32(0x8888);
        var colours = new Vector4[200];
        for (int i = 0; i < colours.Length; i++)
            colours[i] = new Vector4(rng.NextUnit() * 3f, rng.NextUnit() * 3f, rng.NextUnit() * 3f, rng.NextUnit());

        var expected = colours.Select(c => t.Apply(c)).ToArray();
        var actual = (Vector4[])colours.Clone();
        t.ApplyInPlace(actual);
        for (int i = 0; i < colours.Length; i++)
            Assert.True(SameBits(expected[i], actual[i]), Inv($"colour {i}: {expected[i]} vs {actual[i]}"));
    }
}
