using System.Numerics;
using ReyEngine.Core.Decoding;
using static ReyEngine.Formats.Tests.ColorTestImages;
using static ReyEngine.Formats.Tests.ColorTestMath;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M813: what <c>ApplyInPlace</c> reports. It returns the number of texels (or colours) that really changed, so a caller about
/// to BC re-encode a texture - lossy - or rewrite a bin field can skip both when the selection matched nothing. "Changed" is a
/// statement about the bytes (or the float bits) afterwards, not about which colours the transform looked at: a texel that was
/// transformed and rounded back to the very same bytes did not change, and is not counted.
/// </summary>
public sealed class ColorTransformChangeCountTests
{
    /// <summary>How many texels differ in red, green or blue (alpha is never touched, and is checked separately).</summary>
    private static int Differing(byte[] before, byte[] after)
    {
        int n = 0;
        for (int i = 0; i < before.Length; i += 4)
            if (before[i] != after[i] || before[i + 1] != after[i + 1] || before[i + 2] != after[i + 2]) n++;
        return n;
    }

    private static byte[] Texels(params (byte R, byte G, byte B, byte A)[] texels)
    {
        var px = new byte[texels.Length * 4];
        for (int i = 0; i < texels.Length; i++)
        {
            px[i * 4] = texels[i].R; px[i * 4 + 1] = texels[i].G; px[i * 4 + 2] = texels[i].B; px[i * 4 + 3] = texels[i].A;
        }
        return px;
    }

    // ===================================================================== 8-bit

    [Fact]
    public void AnIdentity_ReportsZero_AndLeavesTheBufferAsItCame()
    {
        var image = Golden();
        foreach (var t in new[]
        {
            new ColorTransform(),
            new ColorTransform { HueShiftDegrees = 360f },
            new ColorTransform { HueShiftDegrees = 90f, Saturation = 2f, Brightness = 0.5f, ColorizeHueDegrees = 40f, Strength = 0f },
            new ColorTransform { HueSelection = new HueRange(120f, 30f), GreyProtection = new GreyProtection(0.2f) },
        })
        {
            var copy = (byte[])image.Rgba.Clone();
            Assert.Equal(0, t.ApplyInPlace(copy));
            Assert.True(copy.AsSpan().SequenceEqual(image.Rgba), "an identity changed a byte");
        }
    }

    [Fact]
    public void ASelectionThatMatchesNothing_ReportsZero_AndLeavesTheBufferBitIdentical()
    {
        // reds through green-blues, and greys: nothing within 5 degrees of blue (240)
        var texels = new List<(byte, byte, byte, byte)>();
        for (float hue = 0f; hue <= 200f; hue += 10f)
        {
            var c = ToBytes(Colour(hue, 0.9f, 0.9f), 200);
            texels.Add((c[0], c[1], c[2], c[3]));
        }
        foreach (byte grey in new byte[] { 0, 64, 128, 255 }) texels.Add((grey, grey, grey, 7));
        var buffer = Texels(texels.ToArray());

        var blues = new ColorTransform { HueShiftDegrees = 120f, Brightness = 0.7f, HueSelection = new HueRange(240f, 10f) };
        var copy = (byte[])buffer.Clone();
        Assert.Equal(0, blues.ApplyInPlace(copy));
        Assert.True(copy.AsSpan().SequenceEqual(buffer), "a selection that matched nothing still changed a byte");

        // one blue texel in the same buffer, and the count is exactly one
        var withBlue = buffer.Concat(ToBytes(Colour(240f, 0.9f, 0.9f), 9)).ToArray();
        var after = (byte[])withBlue.Clone();
        Assert.Equal(1, blues.ApplyInPlace(after));
        Assert.Equal(1, Differing(withBlue, after));
        Assert.True(after.AsSpan(0, buffer.Length).SequenceEqual(buffer), "the texels outside the selection must be untouched");
        Assert.Equal(9, after[^1]);                                                // and the blue one's alpha too
    }

    [Fact]
    public void ASmallKnownBuffer_CountsExactlyTheTexelsThatMoved()
    {
        var reds = new ColorTransform { HueShiftDegrees = 120f, HueSelection = new HueRange(0f, 40f) };
        var buffer = Texels(
            (255, 0, 0, 11),         // red: selected, moves
            (128, 128, 128, 22),     // grey: never selected
            (0, 0, 255, 33),         // blue: outside
            (255, 255, 255, 44),     // white: a grey
            (200, 50, 50, 55));      // a dull red: selected, moves

        var after = (byte[])buffer.Clone();
        Assert.Equal(2, reds.ApplyInPlace(after));

        Assert.Equal(new byte[] { 128, 128, 128, 22 }, after[4..8]);
        Assert.Equal(new byte[] { 0, 0, 255, 33 }, after[8..12]);
        Assert.Equal(new byte[] { 255, 255, 255, 44 }, after[12..16]);
        Assert.NotEqual(new byte[] { 255, 0, 0 }, after[0..3]);
        Assert.NotEqual(new byte[] { 200, 50, 50 }, after[16..19]);
        Assert.Equal(11, after[3]);
        Assert.Equal(55, after[19]);
    }

    [Fact]
    public void ATexelThatIsTransformedAndRoundsBackToTheSameBytes_IsNotCounted()
    {
        // Colourising a red to hue 0 selects it (weight 1) and runs the whole HSV round trip, which hands back the same
        // bytes. Nothing changed, so nothing is reported - a re-encode would spend a generation of BC loss on an identical picture.
        var buffer = Texels((255, 0, 0, 1), (128, 0, 0, 2), (1, 0, 0, 3), (77, 0, 0, 4));
        var toRed = new ColorTransform { ColorizeHueDegrees = 0f };
        Assert.Equal(1f, toRed.SelectionWeight((byte)255, (byte)0, (byte)0));          // it WAS selected
        Assert.False(toRed.IsIdentity);

        var copy = (byte[])buffer.Clone();
        Assert.Equal(0, toRed.ApplyInPlace(copy));
        Assert.True(copy.AsSpan().SequenceEqual(buffer));

        // add one texel that does move and it is the only one counted
        var mixed = buffer.Concat(Texels((0, 255, 0, 5))).ToArray();
        Assert.Equal(1, toRed.ApplyInPlace(mixed));
        Assert.True(mixed.AsSpan(0, buffer.Length).SequenceEqual(buffer));
        Assert.Equal(new byte[] { 255, 0, 0, 5 }, mixed[^4..]);
    }

    [Fact]
    public void TheCountIsTheNumberOfTexelsThatDiffer_OnTheGoldenImage()
    {
        var image = Golden();
        int texels = image.Width * image.Height;
        foreach (var t in new[]
        {
            new ColorTransform { HueShiftDegrees = 90f },
            new ColorTransform { Saturation = 0f },
            new ColorTransform { Brightness = 0.5f },
            new ColorTransform { Brightness = 3f },                                                 // white is already at the ceiling
            new ColorTransform { ColorizeHueDegrees = 200f },
            new ColorTransform { HueShiftDegrees = 45f, HueSelection = new HueRange(30f, 90f, 20f) },
            new ColorTransform { HueShiftDegrees = 45f, GreyProtection = new GreyProtection(0.3f, 0.2f) },
            new ColorTransform { HueShiftDegrees = 120f, Strength = 0.3f },
            new ColorTransform { HueShiftDegrees = -33f, Saturation = 1.4f, Brightness = 0.9f, Strength = 0.8f, HueSelection = new HueRange(200f, 120f, 40f) },
        })
        {
            var after = (byte[])image.Rgba.Clone();
            int reported = t.ApplyInPlace(after);

            Assert.Equal(Differing(image.Rgba, after), reported);
            Assert.InRange(reported, 1, texels - 1);                                 // it did something, and not to everything
            for (int i = 3; i < after.Length; i += 4) Assert.Equal(image.Rgba[i], after[i]);
        }
    }

    [Fact]
    public void Apply_ReturningAnImage_AgreesWithTheCount()
    {
        // Apply(TextureImage) has no count of its own: the new image equals the source exactly when ApplyInPlace reports 0.
        var image = Golden();
        var nothing = new ColorTransform { HueShiftDegrees = 90f, HueSelection = new HueRange(float.NaN, 10f), Strength = 0f };
        Assert.True(nothing.Apply(image).Rgba.AsSpan().SequenceEqual(image.Rgba));
        Assert.Equal(0, nothing.ApplyInPlace((byte[])image.Rgba.Clone()));

        var something = new ColorTransform { HueShiftDegrees = 90f };
        Assert.False(something.Apply(image).Rgba.AsSpan().SequenceEqual(image.Rgba));
        Assert.True(something.ApplyInPlace((byte[])image.Rgba.Clone()) > 0);
    }

    // ===================================================================== float colours

    [Fact]
    public void Floats_CountTheColoursWhoseBitsChanged()
    {
        var reds = new ColorTransform { HueShiftDegrees = 120f, HueSelection = new HueRange(0f, 40f) };
        var colours = new[]
        {
            new Vector4(1f, 0f, 0f, 1f),               // a red: moves
            new Vector4(0.5f, 0.5f, 0.5f, 1f),         // a grey: never selected
            new Vector4(-0.5f, 0.2f, 0.3f, 1f),        // a colour it cannot read: left alone
            new Vector4(4f, 0f, 0f, 0.25f),            // an HDR red: moves
            new Vector4(0f, 0f, 1f, 1f),               // a blue: outside
        };
        var after = (Vector4[])colours.Clone();

        Assert.Equal(2, reds.ApplyInPlace(after));
        Assert.False(SameBits(colours[0], after[0]));
        Assert.False(SameBits(colours[3], after[3]));
        foreach (int unchanged in new[] { 1, 2, 4 }) Assert.True(SameBits(colours[unchanged], after[unchanged]), $"colour {unchanged} moved");
        Assert.Equal(0.25f, after[3].W);                                              // alpha is never part of a change
    }

    [Fact]
    public void Floats_ReportZero_WhenNothingChanged()
    {
        var rng = new Xorshift32(0xC0DE);
        var colours = new Vector4[300];
        for (int i = 0; i < colours.Length; i++)
        {
            float g = rng.NextUnit() * 6f;
            colours[i] = new Vector4(g, g, g, rng.NextUnit());                       // greys of every intensity
        }
        // a selection of blues over nothing but greys, a strength of zero, and a plain identity
        foreach (var t in new[]
        {
            new ColorTransform { HueShiftDegrees = 90f, Saturation = 2f, ColorizeHueDegrees = 40f },
            new ColorTransform { HueShiftDegrees = 90f, HueSelection = new HueRange(240f, 30f) },
            new ColorTransform { Brightness = 0.5f, GreyProtection = new GreyProtection(0.1f) },
            new ColorTransform { HueShiftDegrees = 90f, Strength = 0f },
            new ColorTransform(),
        })
        {
            var after = (Vector4[])colours.Clone();
            Assert.Equal(0, t.ApplyInPlace(after));
            for (int i = 0; i < colours.Length; i++)
                if (!SameBits(colours[i], after[i])) Assert.Fail($"{t}: colour {i} changed");
        }
    }

    [Fact]
    public void Floats_ColourisedToTheirOwnHue_ChangeNothing_AndAreNotCounted()
    {
        var colours = new[] { new Vector4(1f, 0f, 0f, 1f), new Vector4(4f, 0f, 0f, 0.5f), new Vector4(0.25f, 0f, 0f, 1f), new Vector4(255f, 0f, 0f, 1f) };
        var toRed = new ColorTransform { ColorizeHueDegrees = 0f };
        Assert.Equal(1f, toRed.SelectionWeight(colours[1]));

        var after = (Vector4[])colours.Clone();
        Assert.Equal(0, toRed.ApplyInPlace(after));
        for (int i = 0; i < colours.Length; i++) Assert.True(SameBits(colours[i], after[i]));
    }

    [Fact]
    public void Floats_TheCountIsTheNumberOfColoursThatDiffer()
    {
        var rng = new Xorshift32(0x5EED);
        var colours = new Vector4[2000];
        for (int i = 0; i < colours.Length; i++)
        {
            float k = rng.NextUnit() < 0.3f ? 1f + rng.NextUnit() * 20f : 1f;
            colours[i] = Colour(rng.NextUnit() * 360f, rng.NextUnit(), 0.2f + rng.NextUnit() * 0.8f) * k;
            colours[i].W = rng.NextUnit();
        }
        var t = new ColorTransform { HueShiftDegrees = 75f, Brightness = 0.9f, HueSelection = new HueRange(60f, 100f, 30f), Strength = 0.8f };

        var after = (Vector4[])colours.Clone();
        int reported = t.ApplyInPlace(after);

        int differing = 0;
        for (int i = 0; i < colours.Length; i++) if (!SameBits(colours[i], after[i])) differing++;
        Assert.Equal(differing, reported);
        Assert.InRange(reported, 100, colours.Length - 100);
    }
}
