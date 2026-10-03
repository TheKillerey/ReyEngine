using System.Diagnostics;
using System.Numerics;
using ReyEngine.Core.Decoding;
using Xunit.Abstractions;
using static ReyEngine.Formats.Tests.ColorTestImages;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M813: the cost of the transform on a large texture. The Chroma Studio recolours a skin's textures while a slider is dragged,
/// so "how long does a big texture take" decides whether it can run on the full texture or must preview a reduced copy.
///
/// <para><b>What was measured</b> - 2048 x 2048 random texels (the worst case: no flat runs), on the development machine, best
/// of 7 after a warm-up, by a scratch probe and not by this test:</para>
/// <list type="bullet">
/// <item><b>Release</b>: hue + saturation + brightness in place 131 ms (31 ns a texel); range + protection + strength 100 ms;
/// colourise 73 ms; <see cref="ColorTransform.Apply(TextureImage)"/> with its clone 118 ms; <see cref="TextureAdjustment"/>'s
/// same HSV step 164 ms. A smooth texture with a flat black border: 36 ms against 75 ms for <see cref="TextureAdjustment"/>.
/// 100,000 float colours: 3.4 ms (34 ns each).</item>
/// <item><b>Debug</b> (how the suite is built), these cases alone in a run: 381, 323, 329 and 388 ms, and
/// <see cref="TextureAdjustment"/> 439 ms.</item>
/// </list>
///
/// <para><b>What this test does</b>: it runs a QUARTER of that - 1024 x 1024, three cases over a million texels, each run once to
/// warm up and once timed - where it first ran twelve passes over four million texels, about three seconds of Debug-build CPU
/// in the default suite for a number already recorded above. It prints the time and the cost per texel, and it asserts only a
/// deliberately generous ceiling: ten seconds, about a hundred times what Debug takes. This test shares the machine with the
/// whole suite, and a tight timing assertion that fails under load teaches nothing; it is there to catch an accidental
/// quadratic loop, not to measure.</para>
/// </summary>
public sealed class ColorTransformPerformanceTests
{
    private readonly ITestOutputHelper _output;
    public ColorTransformPerformanceTests(ITestOutputHelper output) => _output = output;

    /// <summary>Worst case for the transform: every texel a different pseudo-random colour, no flat runs.</summary>
    private static byte[] RandomRgba(int texels, uint seed)
    {
        var rng = new Xorshift32(seed);
        var px = new byte[texels * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            uint s = rng.NextUInt();
            px[i] = (byte)s; px[i + 1] = (byte)(s >> 8); px[i + 2] = (byte)(s >> 16); px[i + 3] = (byte)(s >> 24);
        }
        return px;
    }

    /// <summary>One warm-up, one timed run; prints the milliseconds and the cost per item.</summary>
    private double Time(string what, int items, string unit, Action run)
    {
        run();                                   // warm-up: JIT, caches
        var sw = Stopwatch.StartNew();
        run();
        sw.Stop();
        double ms = sw.Elapsed.TotalMilliseconds;
        _output.WriteLine(Inv($"{what}: {ms:F1} ms, {ms * 1e6 / items:F1} ns per {unit}"));
        return ms;
    }

    private const double CeilingMilliseconds = 10_000;

    [Fact]
    public void A1024SquareImage_IsTransformedWithinTenSeconds()
    {
        const int Side = 1024;
        int texels = Side * Side;
        var pixels = RandomRgba(texels, 0x1024);
        var image = new TextureImage(Side, Side, pixels);
        _output.WriteLine(Inv($"{Side}x{Side} = {texels:N0} texels, random content (the 2048x2048 figures are in the class remarks)"));

        var hsv = new ColorTransform { HueShiftDegrees = 40f, Saturation = 1.2f, Brightness = 0.95f };
        var everything = new ColorTransform
        {
            HueShiftDegrees = 40f, Saturation = 1.2f, Brightness = 0.95f,
            HueSelection = new HueRange(30f, 120f, 30f), GreyProtection = new GreyProtection(0.1f, 0.1f), Strength = 0.8f,
        };

        double plain = Time("hue + saturation + brightness (in place, clone included)", texels, "texel", () => hsv.ApplyInPlace((byte[])pixels.Clone()));
        double full = Time("range + protection + strength (in place, clone included)", texels, "texel", () => everything.ApplyInPlace((byte[])pixels.Clone()));
        Time("for comparison, TextureAdjustment hue + saturation + brightness", texels, "texel", () =>
            new TextureAdjustment { HueDegrees = 40f, Saturation = 1.2f, Brightness = 0.95f }.Apply(image));

        Assert.True(plain < CeilingMilliseconds && full < CeilingMilliseconds, Inv($"plain {plain} ms, full {full} ms"));
    }

    [Fact]
    public void FiveThousandFloatColours_AreTransformedWithinFiveSeconds()
    {
        // The effects recolour's workload: the colour keys of a skin's systems - a few thousand floats, not millions of texels.
        var rng = new Xorshift32(0x1000);
        var keys = new Vector4[5000];
        for (int i = 0; i < keys.Length; i++)
            keys[i] = new Vector4(rng.NextUnit() * 4f, rng.NextUnit() * 4f, rng.NextUnit() * 4f, 1f);
        var t = new ColorTransform { HueShiftDegrees = 90f, Saturation = 1.1f, HueSelection = new HueRange(10f, 100f, 20f) };

        double ms = Time("5,000 float colours", keys.Length, "colour", () => t.ApplyInPlace((Vector4[])keys.Clone()));
        Assert.True(ms < 5_000, Inv($"{ms} ms"));
    }
}
