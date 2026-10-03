using ReyEngine.Core.Decoding;
using static ReyEngine.Formats.Tests.ColorTestImages;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M813: Recolor Textures' output, pinned byte for byte.
///
/// <para>These were written BEFORE the HSV maths moved out of <see cref="TextureAdjustment"/> into a shared helper, and
/// their hashes were recorded from the unmodified code. They must pass unchanged afterwards: Recolor Textures rewrites a
/// whole map's textures, and a one-byte drift in what it writes is a drift in every map a user already shipped.</para>
///
/// <para>What is and is not pinned, and why:</para>
/// <list type="bullet">
/// <item><b>The adjustment stack</b> (<see cref="Adjustment_IsByteIdentical"/>): hue, saturation, brightness, contrast,
/// levels, tint, strength and a .cube grade, each pinned as the SHA-256 of the whole 32,768-texel output. Every operation
/// in those paths is exact IEEE arithmetic (+ - * / floor round), so the hashes do not depend on the CPU. <b>Gamma is left
/// out on purpose</b>: it is a <c>pow</c>, and the C runtime's <c>pow</c> is allowed to differ in the last bit between
/// CPUs, which would fail this test on another machine for a reason that has nothing to do with the code. Gamma is not on
/// the path the refactor touched.</item>
/// <item><b>The .tex round trip</b> (<see cref="Recolor_Bc1WithMips_IsByteIdentical"/>,
/// <see cref="Recolor_Bc3_IsByteIdentical"/>): the same, through decode, adjust and the BC1/BC3 encoder. Its input is built
/// by hand from fixed block bytes, so no encoder produced the INPUT. If one of these fails while the adjustment hashes pass,
/// the encoder (BCnEncoder) changed, not the adjustment; the failure says so.</item>
/// </list>
/// </summary>
public sealed class RecolorGoldenTests
{
    /// <summary>Pinned: the input every adjustment case starts from. If this moves, nothing below means anything.</summary>
    private const string GoldenInputHash = "13d3084cb984025ff80d37ed276748f485bcd3eec7f6a8b752203470b5672c31";

    private static CubeLut WarmGrade() => CubeLut.Parse(new[]
    {
        "TITLE \"warm\"",
        "LUT_3D_SIZE 2",
        "0.00 0.00 0.05",
        "0.95 0.05 0.00",
        "0.05 0.90 0.10",
        "1.00 0.95 0.05",
        "0.00 0.10 0.90",
        "0.90 0.00 0.95",
        "0.05 0.85 1.00",
        "1.00 1.00 0.95",
    });

    /// <summary>name -> (the adjustment, the SHA-256 of TextureAdjustment.Apply(Golden()).Rgba).</summary>
    private static readonly Dictionary<string, (Func<TextureAdjustment> Make, string Hash)> Cases = new()
    {
        // An identity adjustment, and a strength of zero, hand back the input: their hash IS the input's.
        ["identity"] = (() => new TextureAdjustment(), "13d3084cb984025ff80d37ed276748f485bcd3eec7f6a8b752203470b5672c31"),
        ["strength-zero"] = (() => new TextureAdjustment { HueDegrees = 120, Strength = 0f }, "13d3084cb984025ff80d37ed276748f485bcd3eec7f6a8b752203470b5672c31"),
        ["hue+90"] = (() => new TextureAdjustment { HueDegrees = 90 }, "0accb9f7c31a5342ffe2a36e20232d89194cd89c7e51fc6a730ec183e58a81c1"),
        ["hue-45"] = (() => new TextureAdjustment { HueDegrees = -45 }, "737f0fb3751389ee6b8b5f6370abb9017bc0134a68e434f85396d8288e874e78"),
        ["hue+180"] = (() => new TextureAdjustment { HueDegrees = 180 }, "2a0340118f2605f5b3f3a1faaa9730a10aa09b37770a68910f414552f927cbb6"),
        ["saturation0.35"] = (() => new TextureAdjustment { Saturation = 0.35f }, "0410c4f14ead55b6f3ca966268d436d3e2684a06ee3ac0b71e0b09eb292c6f1f"),
        ["saturation1.8"] = (() => new TextureAdjustment { Saturation = 1.8f }, "2dce4bbbc66e15b70c5a4879cc0f9b02677211726a70d7ac0a1a554ed04f7f2b"),
        ["brightness0.6"] = (() => new TextureAdjustment { Brightness = 0.6f }, "1ea44ad28bc2e8675f9544215387abc4ebfdc4ac9d9ca048eb6536ee0acf85c8"),
        ["brightness1.4"] = (() => new TextureAdjustment { Brightness = 1.4f }, "4cdb8c8d4bc8e738f88a42bd89e71d33cb9313962fc8e6719384e0d0b8745c6b"),
        ["hsv-combo"] = (() => new TextureAdjustment { HueDegrees = 33, Saturation = 1.25f, Brightness = 0.9f }, "f1928f71e881226f98e3b38c75f46ced99581afe3a08260560995087b2c54325"),
        ["levels-contrast"] = (() => new TextureAdjustment { Contrast = 1.3f, InputBlack = 0.1f, InputWhite = 0.9f }, "9a4c2e028d3f3c94d8922b83ec52da961327f997f2a4f386b73b82169df1f9e7"),
        ["tint"] = (() => new TextureAdjustment { TintR = 0.9f, TintG = 1f, TintB = 1.1f }, "a1226d5e413ed08d87649af0d82d581a428a681246b280ca51875bd5898bf88c"),
        ["strength-half"] = (() => new TextureAdjustment { HueDegrees = 120, Strength = 0.5f }, "85bfd281ecefb9c2e9d686b82a487b736f2a8ebd1232f874d662611b20ca16c1"),
        ["hsv-strength"] = (() => new TextureAdjustment { HueDegrees = -70, Saturation = 0.8f, Brightness = 1.1f, Strength = 0.35f }, "daeaff0a72a8cd2121b427d458d9818eb702c4b322fb6830b2889256bcb0cd41"),
        ["lut"] = (() => new TextureAdjustment { Lut = WarmGrade(), LutStrength = 0.7f }, "77721b8db332243f6dfb091d62246720d12d7e5a3b42a5724a446d1b0a19f532"),
        ["full-stack"] = (() => new TextureAdjustment
        {
            HueDegrees = 25, Saturation = 1.1f, Brightness = 1.05f, Contrast = 1.1f,
            InputBlack = 0.05f, InputWhite = 0.95f, TintR = 1f, TintG = 0.95f, TintB = 0.9f,
            Lut = WarmGrade(), LutStrength = 0.4f, Strength = 0.8f,
        }, "5cfeda9c06b10b8cf38c1084b8a8ee5cd0494b0af9df76c051ab259d54100040"),
    };

    public static IEnumerable<object[]> CaseNames => Cases.Keys.Select(k => new object[] { k });

    [Fact]
    public void TheGoldenInputIsPinned()
    {
        string actual = Sha256Hex(Golden().Rgba);
        Assert.True(GoldenInputHash == actual,
            Inv($"the golden input moved: pinned {GoldenInputHash} but it hashes to {actual}"));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Adjustment_IsByteIdentical(string name)
    {
        var (make, pinned) = Cases[name];
        var input = Golden();
        var before = (byte[])input.Rgba.Clone();

        var output = make().Apply(input);
        string actual = Sha256Hex(output.Rgba);

        Assert.True(pinned == actual,
            Inv($"{name}: pinned {pinned} but TextureAdjustment.Apply now hashes to {actual}"));
        Assert.Equal(before, input.Rgba);                       // the source is never modified
        Assert.Equal(input.Width, output.Width);
        Assert.Equal(input.Height, output.Height);
    }

    [Fact]
    public void Adjustment_NeverTouchesAlpha()
    {
        var input = Golden();
        var output = new TextureAdjustment { HueDegrees = 90, Saturation = 1.5f, Brightness = 0.7f, Contrast = 1.2f }.Apply(input);
        for (int i = 3; i < input.Rgba.Length; i += 4)
            Assert.Equal(input.Rgba[i], output.Rgba[i]);
    }

    // ===================================================================== the .tex round trip

    /// <summary>
    /// A valid BC1 (format 10) or BC3 (format 12) .tex built from fixed pseudo-random blocks, so the INPUT of the round
    /// trip does not depend on any encoder. Colour endpoints are ordered c0 &gt; c1 (the four-colour mode) in both
    /// formats. With <paramref name="mips"/> the chain is written smallest first down to 1x1, each level a whole number of
    /// blocks, which is the layout <see cref="TexWriter"/> documents.
    /// </summary>
    private static byte[] HandBuiltTex(int width, int height, TexFormat format, bool mips, uint seed)
    {
        var rng = new Xorshift32(seed);

        var body = new List<byte>();
        void Level(int w, int h)
        {
            int blocks = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4);
            for (int i = 0; i < blocks; i++)
            {
                if (format == TexFormat.Bc3)
                    for (int k = 0; k < 8; k++) body.Add((byte)rng.NextUInt());
                ushort c0 = (ushort)rng.NextUInt(), c1 = (ushort)rng.NextUInt();
                if (c0 == c1) c1 = (ushort)(c0 ^ 0x0841);
                if (c0 < c1) (c0, c1) = (c1, c0);
                body.Add((byte)c0); body.Add((byte)(c0 >> 8));
                body.Add((byte)c1); body.Add((byte)(c1 >> 8));
                uint idx = rng.NextUInt();
                body.Add((byte)idx); body.Add((byte)(idx >> 8)); body.Add((byte)(idx >> 16)); body.Add((byte)(idx >> 24));
            }
        }

        if (mips)
        {
            var sizes = new List<(int W, int H)>();
            for (int w = width, h = height; ; w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
            {
                sizes.Add((w, h));
                if (w == 1 && h == 1) break;
            }
            for (int i = sizes.Count - 1; i >= 0; i--) Level(sizes[i].W, sizes[i].H);
        }
        else Level(width, height);

        var tex = new byte[12 + body.Count];
        tex[0] = (byte)'T'; tex[1] = (byte)'E'; tex[2] = (byte)'X';
        tex[4] = (byte)width; tex[5] = (byte)(width >> 8);
        tex[6] = (byte)height; tex[7] = (byte)(height >> 8);
        tex[8] = 1; tex[9] = (byte)format; tex[11] = (byte)(mips ? 1 : 0);
        body.CopyTo(tex, 12);
        return tex;
    }

    private static readonly TextureAdjustment RecolorAdjustment =
        new() { HueDegrees = 40, Saturation = 1.2f, Brightness = 0.95f, Strength = 0.85f };

    private const string Bc1WithMipsHash = "e697f135b1cc8545d4a2a94304b6225b8ad228d2d5f3e4f56bae9b6f8895225c";
    private const string Bc3Hash = "cf74578334b4d77eb2ce681587209b209f8b21932ec6099d4200c111b5551590";

    private const string EncoderNote =
        " (if the adjustment hashes still pass, BCnEncoder changed, not TextureAdjustment)";

    [Fact]
    public void Recolor_Bc1WithMips_IsByteIdentical()
    {
        var source = HandBuiltTex(32, 32, TexFormat.Bc1, mips: true, seed: 0xB10C0001);
        var outcome = TextureRecolor.Apply(source, RecolorAdjustment);
        Assert.True(outcome.Ok, outcome.Detail);
        string actual = Sha256Hex(outcome.Bytes!);
        Assert.True(Bc1WithMipsHash == actual,
            Inv($"BC1 with a mip chain: pinned {Bc1WithMipsHash} but it hashes to {actual}{EncoderNote}"));
    }

    [Fact]
    public void Recolor_Bc3_IsByteIdentical()
    {
        var source = HandBuiltTex(32, 16, TexFormat.Bc3, mips: false, seed: 0xB10C0003);
        var outcome = TextureRecolor.Apply(source, RecolorAdjustment);
        Assert.True(outcome.Ok, outcome.Detail);
        string actual = Sha256Hex(outcome.Bytes!);
        Assert.True(Bc3Hash == actual,
            Inv($"BC3 without mips: pinned {Bc3Hash} but it hashes to {actual}{EncoderNote}"));
    }

    /// <summary>
    /// The pinned hashes above name the bytes; this names the RULE, with no hash that could go stale: a recolored .tex is
    /// exactly decode, then <see cref="TextureAdjustment.Apply"/>, then <see cref="TexWriter.Write"/> in the format the
    /// file arrived in, with a mip chain only if it had one.
    /// </summary>
    [Theory]
    [InlineData(TexFormat.Bc1, true, 32, 32)]
    [InlineData(TexFormat.Bc1, false, 16, 8)]
    [InlineData(TexFormat.Bc3, true, 16, 16)]
    [InlineData(TexFormat.Bc3, false, 32, 16)]
    public void Recolor_IsDecodeAdjustWrite(TexFormat format, bool mips, int width, int height)
    {
        var source = HandBuiltTex(width, height, format, mips, seed: 0xC0105);
        var outcome = TextureRecolor.Apply(source, RecolorAdjustment);
        Assert.True(outcome.Ok, outcome.Detail);

        var expected = TexWriter.Write(RecolorAdjustment.Apply(TextureDecoder.Decode(source)), format, mips);
        Assert.Equal(expected, outcome.Bytes);
        Assert.Equal(format, outcome.Format);
    }

    [Fact]
    public void Recolor_SkipsAnIdentityAdjustment()
    {
        var source = HandBuiltTex(16, 16, TexFormat.Bc1, mips: false, seed: 7);
        var outcome = TextureRecolor.Apply(source, new TextureAdjustment());
        Assert.False(outcome.Ok);
        Assert.Equal(RecolorSkip.NoChange, outcome.Skip);
    }
}
