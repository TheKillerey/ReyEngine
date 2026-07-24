namespace ReyEngine.Core.Decoding;

/// <summary>
/// M144: minimal Targa (.tga) reader for OLD NVR maps. Pre-DDS League levels (e.g. the 0.0.0.8-era
/// Map4) reference their textures as <c>foo.tga</c>, which LeagueToolkit's Texture.Load cannot read —
/// so every surface came out untextured. Handles the true-colour and greyscale types League's levels
/// actually ship: 2/10 (BGR(A) 24/32-bit, raw + RLE) and 3/11 (grey 8-bit, raw + RLE).
/// </summary>
public static class TgaDecoder
{
    /// <summary>True when the bytes plausibly are a TGA we can read. TGA has no leading magic, so this
    /// validates the 18-byte header instead (image type, bit depth, and the size against the payload).</summary>
    public static bool LooksLikeTga(byte[] data)
    {
        if (data.Length < 18) return false;
        int colorMapType = data[1], type = data[2], bpp = data[16];
        if (colorMapType != 0) return false;                       // palettised: not used by League levels
        if (type is not (2 or 3 or 10 or 11)) return false;
        if (bpp is not (8 or 24 or 32)) return false;
        if ((type is 2 or 10) && bpp == 8) return false;           // true-colour is never 8-bit
        if ((type is 3 or 11) && bpp != 8) return false;           // greyscale is always 8-bit
        int w = data[12] | (data[13] << 8), h = data[14] | (data[15] << 8);
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return false;
        // uncompressed payload must actually fit; RLE is variable so only the header is checked
        if (type is 2 or 3 && 18 + data[0] + (long)w * h * (bpp / 8) > data.Length) return false;
        return true;
    }

    /// <summary>Decode to RGBA8, top-left origin. Null when the bytes aren't a TGA we support.</summary>
    public static TextureImage? TryDecode(byte[] data)
    {
        if (!LooksLikeTga(data)) return null;
        try
        {
            int idLength = data[0], type = data[2], bpp = data[16], descriptor = data[17];
            int w = data[12] | (data[13] << 8), h = data[14] | (data[15] << 8);
            int bytes = bpp / 8;
            int p = 18 + idLength;

            var rgba = new byte[w * h * 4];
            bool rle = type is 10 or 11;
            bool grey = type is 3 or 11;
            int pixels = w * h, done = 0;
            var px = new byte[4];

            while (done < pixels)
            {
                int run = 1;
                bool raw = true;
                if (rle)
                {
                    if (p >= data.Length) break;
                    int packet = data[p++];
                    run = (packet & 0x7F) + 1;
                    raw = (packet & 0x80) == 0;
                }
                for (int k = 0; k < run && done < pixels; k++)
                {
                    if (k == 0 || raw)
                    {
                        if (p + bytes > data.Length) { done = pixels; break; }
                        if (grey) { px[0] = px[1] = px[2] = data[p]; px[3] = 255; }
                        else
                        {
                            px[0] = data[p + 2]; px[1] = data[p + 1]; px[2] = data[p];   // BGR(A) -> RGB(A)
                            px[3] = bytes == 4 ? data[p + 3] : (byte)255;
                        }
                        p += bytes;
                    }
                    int o = done * 4;
                    rgba[o] = px[0]; rgba[o + 1] = px[1]; rgba[o + 2] = px[2]; rgba[o + 3] = px[3];
                    done++;
                }
            }

            // Descriptor bit 5 set = rows already run top-to-bottom; otherwise the file is bottom-up.
            if ((descriptor & 0x20) == 0) FlipVertical(rgba, w, h);

            // Some exporters write 32-bit pixels but leave the alpha-depth field at 0 and the alpha
            // bytes at 0 — taken literally that makes the whole texture invisible. Treat it as opaque.
            if (bytes == 4 && IsFullyTransparent(rgba))
                for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;

            return new TextureImage(w, h, rgba);
        }
        catch { return null; }
    }

    private static void FlipVertical(byte[] rgba, int w, int h)
    {
        int stride = w * 4;
        var tmp = new byte[stride];
        for (int y = 0; y < h / 2; y++)
        {
            int top = y * stride, bottom = (h - 1 - y) * stride;
            Buffer.BlockCopy(rgba, top, tmp, 0, stride);
            Buffer.BlockCopy(rgba, bottom, rgba, top, stride);
            Buffer.BlockCopy(tmp, 0, rgba, bottom, stride);
        }
    }

    private static bool IsFullyTransparent(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 0) return false;
        return true;
    }
}
