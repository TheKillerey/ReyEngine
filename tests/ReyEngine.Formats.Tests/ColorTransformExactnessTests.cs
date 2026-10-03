using System.Numerics;
using ReyEngine.Core.Decoding;
using static ReyEngine.Formats.Tests.ColorTestImages;
using static ReyEngine.Formats.Tests.ColorTestMath;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M813: the guarantees M815's writer builds on, each one a bit-for-bit statement:
/// <list type="number">
/// <item>identity parameters return the input unchanged;</item>
/// <item>a colour whose weight is 0 is returned unchanged (no HSV round-trip error);</item>
/// <item>white, and every grey, is unchanged under EVERY parameter set except an explicit brightness change;</item>
/// <item>alpha is never touched;</item>
/// </list>
/// plus the two cross-checks that keep the maths honest: it agrees with <see cref="TextureAdjustment"/> wherever the two
/// overlap, and the byte and float paths agree with each other (the latter is in <see cref="ColorTransformHdrTests"/>).
/// </summary>
public sealed class ColorTransformExactnessTests
{
    /// <summary>Float colours that cover the HDR range, none of them a grey.</summary>
    private static List<Vector4> RandomColours(int count, uint seed, float maxIntensity = 255f)
    {
        var rng = new Xorshift32(seed);
        var list = new List<Vector4>(count);
        for (int i = 0; i < count; i++)
        {
            float intensity = rng.NextUnit() < 0.5f ? rng.NextUnit() : 1f + rng.NextUnit() * (maxIntensity - 1f);
            var c = Colour(rng.NextUnit() * 360f, rng.NextUnit(), 1f) * intensity;
            c.W = rng.NextUnit() < 0.1f ? float.NaN : rng.NextUnit();
            list.Add(c);
        }
        return list;
    }

    // ===================================================================== 1. identity

    public static IEnumerable<object[]> IdentitySets() => new[]
    {
        new object[] { "default", new ColorTransform() },
        new object[] { "a whole turn", new ColorTransform { HueShiftDegrees = 360f } },
        new object[] { "minus two turns", new ColorTransform { HueShiftDegrees = -720f } },
        new object[] { "strength zero", new ColorTransform { HueShiftDegrees = 90f, Saturation = 2f, Brightness = 0.5f, ColorizeHueDegrees = 40f, Strength = 0f } },
        new object[] { "strength below zero", new ColorTransform { HueShiftDegrees = 90f, Strength = -1f } },
        new object[] { "only NaNs", new ColorTransform { HueShiftDegrees = float.NaN, Saturation = float.NaN, Brightness = float.NaN, Strength = float.NaN } },
        new object[] { "a selection alone", new ColorTransform { HueSelection = new HueRange(120f, 30f, 10f) } },
        new object[] { "a protection alone", new ColorTransform { GreyProtection = new GreyProtection(0.3f, 0.1f) } },
        new object[] { "both, neutral", new ColorTransform { HueSelection = new HueRange(0f, 60f), GreyProtection = new GreyProtection(0.1f), Strength = 0.5f } },
    };

    [Theory]
    [MemberData(nameof(IdentitySets))]
    public void IdentityParameters_ReturnTheInputBitForBit(string name, ColorTransform t)
    {
        Assert.True(t.IsIdentity, name);

        var image = Golden();
        Assert.Equal(image.Rgba, t.Apply(image).Rgba);

        var inPlace = (byte[])image.Rgba.Clone();
        t.ApplyInPlace(inPlace);
        Assert.True(inPlace.AsSpan().SequenceEqual(image.Rgba), name);

        foreach (var c in RandomColours(2000, 0xD00D))
            if (!SameBits(c, t.Apply(c))) Assert.Fail(Inv($"{name}: {c} came back as {t.Apply(c)}"));

        var span = RandomColours(200, 0xF00D).ToArray();
        var copy = (Vector4[])span.Clone();
        t.ApplyInPlace(copy);
        for (int i = 0; i < span.Length; i++)
            if (!SameBits(span[i], copy[i])) Assert.Fail($"{name}: colour {i} changed in place");
    }

    // ===================================================================== 2. weight zero

    [Fact]
    public void AColourWithWeightZero_ComesBackBitForBit_Floats()
    {
        // blues only, and nothing nearly grey: most random colours fall outside
        var t = new ColorTransform
        {
            HueShiftDegrees = 133f, Saturation = 1.7f, Brightness = 0.6f, ColorizeHueDegrees = null,
            HueSelection = new HueRange(240f, 50f, 20f), GreyProtection = new GreyProtection(0.2f, 0.1f),
        };
        int untouched = 0, touched = 0;
        foreach (var c in RandomColours(5000, 0xBEEF))
        {
            var got = t.Apply(c);
            if (t.SelectionWeight(c) == 0f)
            {
                Assert.True(SameBits(c, got), Inv($"{c} has weight 0 but came back as {got}"));
                untouched++;
            }
            else
            {
                Assert.False(SameBits(c, got) && t.SelectionWeight(c) == 1f, Inv($"{c} has weight 1 but did not change"));
                touched++;
            }
        }
        Assert.True(untouched > 2000 && touched > 300, Inv($"untouched {untouched}, touched {touched}: both sides must be exercised"));
    }

    [Fact]
    public void AColourWithWeightZero_ComesBackBitForBit_Bytes()
    {
        var t = new ColorTransform
        {
            HueShiftDegrees = 133f, Saturation = 1.7f, Brightness = 0.6f,
            HueSelection = new HueRange(240f, 50f, 20f), GreyProtection = new GreyProtection(0.2f, 0.1f),
        };
        var image = Golden();
        var got = t.Apply(image).Rgba;
        int untouched = 0, touched = 0;
        for (int i = 0; i < image.Rgba.Length; i += 4)
        {
            if (t.SelectionWeight(image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2]) == 0f)
            {
                for (int c = 0; c < 4; c++) Assert.Equal(image.Rgba[i + c], got[i + c]);
                untouched++;
            }
            else touched++;
        }
        Assert.True(untouched > 15000 && touched > 1000, Inv($"untouched {untouched}, touched {touched}"));
    }

    // ===================================================================== 3. white and every grey

    /// <summary>Greys of every kind: all 256 byte levels, and float greys from nothing to the float maximum.</summary>
    private static Vector4[] AllGreys()
    {
        var list = new List<Vector4>();
        for (int v = 0; v < 256; v++) list.Add(FromBytes((byte)v, (byte)v, (byte)v, (byte)(255 - v)));
        foreach (float c in new[] { 1e-7f, 0.123456789f, 0.99999994f, 1.0000001f, 1.5f, 2f, 3.75f, 100f, 255f, 1e9f, 3e38f })
            list.Add(new Vector4(c, c, c, 0.25f));
        return list.ToArray();
    }

    [Fact]
    public void WhiteAndEveryGrey_AreUnchanged_UnderEveryParameterSet()
    {
        float[] shifts = { -540f, -180f, -90f, -1f, 0f, 37f, 90f, 180f, 359f, 360f, 725f };
        float[] saturations = { 0f, 0.3f, 1f, 2.5f, 100f };
        float?[] colourise = { null, 0f, 120f, 270f };
        HueRange?[] ranges =
        {
            null, new HueRange(350f, 40f, 10f), new HueRange(120f, 0f, 0f), new HueRange(0f, 360f, 0f),
            new HueRange(180f, 359.9f, 0f), new HueRange(60f, 100f, 50f),
        };
        GreyProtection?[] guards = { null, new GreyProtection(0f), new GreyProtection(0.2f, 0.1f) };
        float[] strengths = { 1f, 0.5f, 0.001f, 0f };

        var greys = AllGreys();
        var greyBytes = new byte[256 * 4];
        for (int v = 0; v < 256; v++)
        {
            greyBytes[v * 4] = greyBytes[v * 4 + 1] = greyBytes[v * 4 + 2] = (byte)v;
            greyBytes[v * 4 + 3] = (byte)(255 - v);
        }

        int sets = 0;
        foreach (float shift in shifts)
        foreach (float sat in saturations)
        foreach (float? colour in colourise)
        foreach (HueRange? range in ranges)
        foreach (GreyProtection? guard in guards)
        foreach (float strength in strengths)
        {
            // brightness stays 1: that is the one exception
            var t = new ColorTransform
            {
                HueShiftDegrees = shift, Saturation = sat, Brightness = 1f, ColorizeHueDegrees = colour,
                HueSelection = range, GreyProtection = guard, Strength = strength,
            };

            // (the failure messages are built only on failure: this loop runs 15,840 x 268 times)
            var bytes = (byte[])greyBytes.Clone();
            t.ApplyInPlace(bytes);
            if (!bytes.AsSpan().SequenceEqual(greyBytes)) Assert.Fail($"byte greys changed under {t}");

            var floats = (Vector4[])greys.Clone();
            t.ApplyInPlace(floats);
            for (int i = 0; i < greys.Length; i++)
                if (!SameBits(greys[i], floats[i])) Assert.Fail(Inv($"grey {greys[i]} became {floats[i]} under {t}"));
            sets++;
        }
        Assert.Equal(shifts.Length * saturations.Length * colourise.Length * ranges.Length * guards.Length * strengths.Length, sets);
    }

    [Fact]
    public void EveryByteGreyIsAlsoExact_ThroughTheFloatKernel_NotJustBecauseTheByteLoopSkipsIt()
    {
        // ApplyInPlace(byte) skips greys outright when brightness is 1. The float API has no such skip: it runs the whole
        // HSV round trip, so exactness here is the maths itself.
        var t = new ColorTransform
        {
            HueShiftDegrees = 211f, Saturation = 2f, ColorizeHueDegrees = 40f, Strength = 0.7f,
            HueSelection = new HueRange(0f, 360f), GreyProtection = new GreyProtection(0f),
        };
        // (a protection with threshold 0 gives a grey weight 0, so it never reaches the kernel: also run without it, at
        // strength 0.7 with colourise, and at strength 1 with a plain hue shift)
        var variants = new[]
        {
            t,
            t with { GreyProtection = null },
            t with { GreyProtection = null, Strength = 1f, ColorizeHueDegrees = null },
        };
        foreach (var variant in variants)
            for (int v = 0; v < 256; v++)
            {
                var grey = FromBytes((byte)v, (byte)v, (byte)v);
                if (!SameBits(grey, variant.Apply(grey))) Assert.Fail(Inv($"grey {v} became {variant.Apply(grey)}"));
            }
    }

    [Fact]
    public void AGreyChangesOnlyWithAnExplicitBrightness()
    {
        var dim = new ColorTransform { Brightness = 0.5f };
        Assert.True(Near(dim.Apply(Vector4.One), new Vector4(0.5f, 0.5f, 0.5f, 1f), 0f));
        Assert.True(Near(dim.Apply(new Vector4(4f, 4f, 4f, 1f)), new Vector4(2f, 2f, 2f, 1f), 0f));
        Assert.Equal(new byte[] { 128, 128, 128, 9 }, Run(dim, 255, 255, 255, 9));
        Assert.Equal(new byte[] { 0, 0, 0, 9 }, Run(dim, 0, 0, 0, 9));                 // black stays black

        var bright = new ColorTransform { Brightness = 2f };
        Assert.Equal(new byte[] { 200, 200, 200, 9 }, Run(bright, 100, 100, 100, 9));
        Assert.Equal(new byte[] { 255, 255, 255, 9 }, Run(bright, 200, 200, 200, 9));  // white is the ceiling for a byte
        Assert.True(Near(bright.Apply(Vector4.One), new Vector4(2f, 2f, 2f, 1f), 0f)); // but not for a float

        // brightness is not an exception under a partial selection or a protection: the grey is simply not selected
        var guarded = dim with { GreyProtection = new GreyProtection(0.05f) };
        var restricted = dim with { HueSelection = new HueRange(90f, 120f) };
        foreach (var t in new[] { guarded, restricted })
        {
            Assert.Equal(new byte[] { 255, 255, 255, 9 }, Run(t, 255, 255, 255, 9));
            Assert.True(SameBits(Vector4.One, t.Apply(Vector4.One)));
        }

        // and a strength of zero is the identity, brightness or no brightness
        Assert.Equal(new byte[] { 255, 255, 255, 9 }, Run(dim with { Strength = 0f }, 255, 255, 255, 9));
    }

    // ===================================================================== 4. alpha

    [Fact]
    public void Alpha_IsNeverTouched_ByAnyParameterSet()
    {
        var image = Golden();
        foreach (var t in new[]
        {
            new ColorTransform { HueShiftDegrees = 90f },
            new ColorTransform { Saturation = 0f },
            new ColorTransform { Brightness = 0f },
            new ColorTransform { Brightness = 3f, Saturation = 3f },
            new ColorTransform { ColorizeHueDegrees = 300f },
            new ColorTransform { HueShiftDegrees = 45f, Strength = 0.3f },
            new ColorTransform { HueShiftDegrees = 45f, HueSelection = new HueRange(10f, 60f, 20f) },
            new ColorTransform { HueShiftDegrees = 45f, GreyProtection = new GreyProtection(0.3f, 0.2f) },
            new ColorTransform { HueShiftDegrees = 180f, Saturation = 2f, Brightness = 0.5f, HueSelection = new HueRange(0f, 200f), GreyProtection = new GreyProtection(0.05f), Strength = 0.9f },
        })
        {
            var got = t.Apply(image).Rgba;
            for (int i = 3; i < image.Rgba.Length; i += 4)
                Assert.Equal(image.Rgba[i], got[i]);
        }
    }

    // ===================================================================== cross-check: TextureAdjustment

    public static IEnumerable<object[]> PlainHsvSets() => new[]
    {
        new object[] { 90f, 1f, 1f, 1f },
        new object[] { -45f, 1f, 1f, 1f },
        new object[] { 180f, 1f, 1f, 1f },
        new object[] { 0f, 0.35f, 1f, 1f },
        new object[] { 0f, 1.8f, 1f, 1f },
        new object[] { 0f, 1f, 0.6f, 1f },
        new object[] { 0f, 1f, 1.4f, 1f },
        new object[] { 33f, 1.25f, 0.9f, 1f },
        new object[] { 120f, 1f, 1f, 0.5f },
        new object[] { -70f, 0.8f, 1.1f, 0.35f },
        new object[] { 10f, 2f, 0.5f, 0.9f },
        new object[] { 170f, 0f, 1f, 1f },
        new object[] { -179f, 3f, 1.7f, 0.2f },
    };

    [Theory]
    [MemberData(nameof(PlainHsvSets))]
    public void WhereTheyOverlap_ItIsByteForByteTheMathsRecolorTexturesUses(float hue, float saturation, float brightness, float strength)
    {
        // The same HSV maths in the same order: with no selection and no colourise, ColorTransform IS the HSV step of
        // TextureAdjustment (levels, contrast, tint and LUT at their neutral values do not change a byte).
        var image = Golden();
        var old = new TextureAdjustment { HueDegrees = hue, Saturation = saturation, Brightness = brightness, Strength = strength }.Apply(image).Rgba;
        var now = new ColorTransform { HueShiftDegrees = hue, Saturation = saturation, Brightness = brightness, Strength = strength }.Apply(image).Rgba;

        for (int i = 0; i < old.Length; i++)
            if (old[i] != now[i])
                Assert.Fail(Inv($"texel {i / 4} channel {i % 4}: TextureAdjustment {old[i]}, ColorTransform {now[i]}"));
    }

    // ===================================================================== allocation

    [Fact]
    public void ApplyInPlace_AllocatesNothing()
    {
        var t = new ColorTransform
        {
            HueShiftDegrees = 40f, Saturation = 1.2f, Brightness = 0.9f,
            HueSelection = new HueRange(30f, 120f, 30f), GreyProtection = new GreyProtection(0.1f, 0.1f), Strength = 0.8f,
        };
        var pixels = Golden().Rgba;
        t.ApplyInPlace(pixels);                         // warm up the JIT and the lookup table
        var floats = new[] { new Vector4(2f, 1f, 0.5f, 1f) };
        t.ApplyInPlace(floats);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4; i++) t.ApplyInPlace(pixels);
        for (int i = 0; i < 1000; i++) t.ApplyInPlace(floats);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 512, Inv($"{allocated} bytes allocated"));
    }

    [Fact]
    public void ApplyInPlace_IsDeterministic()
    {
        var t = new ColorTransform { HueShiftDegrees = 91f, Saturation = 0.7f, HueSelection = new HueRange(200f, 90f, 25f) };
        var a = Golden().Rgba;
        var b = Golden().Rgba;
        t.ApplyInPlace(a);
        t.ApplyInPlace(b);
        Assert.True(a.AsSpan().SequenceEqual(b));
    }
}
