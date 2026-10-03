using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using ReyEngine.Core.Decoding;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M813: the deterministic pixels the colour tests share. <see cref="Golden"/> is PINNED by hash in
/// <see cref="RecolorGoldenTests"/>: change one byte of how it is built and every golden hash moves with it, which is
/// the point - a golden image that quietly drifts proves nothing about the code under it.
///
/// <para>Integer arithmetic only. No <see cref="Random"/> (its sequence is an implementation detail), no floating
/// point, no clock, so the same bytes come out on every machine.</para>
/// </summary>
internal static class ColorTestImages
{
    public const int GoldenWidth = 256;
    public const int GoldenHeight = 128;

    /// <summary>
    /// 256 x 128 RGBA8 = 32,768 texels, in three runs:
    /// <list type="number">
    /// <item>the 16 x 16 x 16 lattice of every combination of 0, 17, 34 ... 255 (4,096 texels: all eight cube corners,
    /// every primary and secondary at every level, and the whole grey diagonal);</item>
    /// <item>every grey 0..255 (256 texels);</item>
    /// <item>pseudo-random texels from a fixed xorshift32 stream (the other 28,416), which is where the odd values and
    /// the near-greys live.</item>
    /// </list>
    /// Alpha is varied everywhere, so "alpha is never touched" is something a hash can notice.
    /// </summary>
    public static TextureImage Golden()
    {
        var px = new byte[GoldenWidth * GoldenHeight * 4];
        int n = 0;

        void Put(byte r, byte g, byte b, byte a)
        {
            px[n++] = r; px[n++] = g; px[n++] = b; px[n++] = a;
        }

        for (int bi = 0; bi < 16; bi++)
            for (int gi = 0; gi < 16; gi++)
                for (int ri = 0; ri < 16; ri++)
                    Put((byte)(ri * 17), (byte)(gi * 17), (byte)(bi * 17), (byte)(255 - (ri + gi + bi) * 5));

        for (int v = 0; v < 256; v++)
            Put((byte)v, (byte)v, (byte)v, (byte)(v ^ 0x5A));

        uint s = 0x9E3779B9u;
        while (n < px.Length)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            Put((byte)s, (byte)(s >> 8), (byte)(s >> 16), (byte)(s >> 24));
        }
        return new TextureImage(GoldenWidth, GoldenHeight, px);
    }

    /// <summary>A fixed xorshift32 stream: the same numbers on every machine, which <see cref="Random"/> does not promise.</summary>
    public sealed class Xorshift32
    {
        private uint _s;
        public Xorshift32(uint seed) => _s = seed == 0 ? 0x9E3779B9u : seed;

        public uint NextUInt()
        {
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            return _s;
        }

        /// <summary>Uniform in [0, 1): the top 24 bits, so every value is exactly representable.</summary>
        public float NextUnit() => (NextUInt() >> 8) / 16777216f;
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Format a message with the invariant culture, so a failing assertion reads the same on every machine.</summary>
    public static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>M813: small colour helpers the ColorTransform tests share.</summary>
internal static class ColorTestMath
{
    public static bool SameBits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    public static bool SameBits(Vector4 a, Vector4 b) =>
        SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z) && SameBits(a.W, b.W);

    /// <summary>The colour at a hue (degrees), saturation and value. Alpha 1.</summary>
    public static Vector4 Colour(float hueDegrees, float saturation = 1f, float value = 1f)
    {
        Hsv.ToRgb(hueDegrees / 360f, saturation, value, out float r, out float g, out float b);
        return new Vector4(r, g, b, 1f);
    }

    /// <summary>Hue (degrees 0..360), saturation and value of a float colour.</summary>
    public static (float Hue, float Sat, float Val) HsvOf(Vector4 c)
    {
        Hsv.FromRgb(c.X, c.Y, c.Z, out float h, out float s, out float v);
        return (h * 360f, s, v);
    }

    /// <summary>Distance between two hues round the wheel, 0..180 degrees.</summary>
    public static float HueGap(float a, float b)
    {
        float d = MathF.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    public static Vector4 FromBytes(byte r, byte g, byte b, byte a = 255) =>
        new(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>One RGBA8 texel from a float colour, rounded as the transform rounds.</summary>
    public static byte[] ToBytes(Vector4 c, byte a = 255) => new[] { Byte(c.X), Byte(c.Y), Byte(c.Z), a };

    public static byte Byte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);

    public static bool Near(Vector4 a, Vector4 b, float tolerance) =>
        MathF.Abs(a.X - b.X) <= tolerance && MathF.Abs(a.Y - b.Y) <= tolerance &&
        MathF.Abs(a.Z - b.Z) <= tolerance && MathF.Abs(a.W - b.W) <= tolerance;

    /// <summary>Run a transform over one RGBA8 texel and return the result, leaving the input alone.</summary>
    public static byte[] Run(ColorTransform t, params byte[] rgba)
    {
        var copy = (byte[])rgba.Clone();
        t.ApplyInPlace(copy);
        return copy;
    }
}
