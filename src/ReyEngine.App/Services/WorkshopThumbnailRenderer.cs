using Avalonia.Media.Imaging;
using ReyEngine.App.Imaging;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;

namespace ReyEngine.App.Services;

/// <summary>Large, deterministic Workshop hero thumbnails. Material previews use an edge-to-edge authored
/// texture with studio lighting/vignette; particle previews composite the real sprite into a small glow
/// scene. The cards remain recognizable even when a template has no usable texture.</summary>
public static class WorkshopThumbnailRenderer
{
    private const int Width = 480;
    private const int Height = 270;

    public static Bitmap Render(TextureImage? source, string identity, bool particle)
    {
        uint hash = HashAlgorithms.Fnv1a(identity);
        var rgba = new byte[Width * Height * 4];
        var a = Color(hash, 0.16f, 0.35f);
        var b = Color(hash * 1664525u + 1013904223u, 0.25f, 0.62f);
        FillBackground(rgba, a, b, particle);

        if (source is not null)
        {
            if (particle) DrawParticle(rgba, source, b);
            else DrawMaterial(rgba, source);
        }

        Finish(rgba, particle);
        return BitmapFactory.FromRgba(new TextureImage(Width, Height, rgba));
    }

    private static void FillBackground(byte[] dst, (float R, float G, float B) a,
        (float R, float G, float B) b, bool particle)
    {
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            float u = x / (float)(Width - 1), v = y / (float)(Height - 1);
            float mix = Math.Clamp(u * .65f + (1f - v) * .35f, 0, 1);
            float glow = MathF.Exp(-((u - .52f) * (u - .52f) + (v - .48f) * (v - .48f)) * (particle ? 8f : 3f));
            int i = (y * Width + x) * 4;
            dst[i] = B((a.R * (1 - mix) + b.R * mix) * (.48f + glow * .35f));
            dst[i + 1] = B((a.G * (1 - mix) + b.G * mix) * (.48f + glow * .35f));
            dst[i + 2] = B((a.B * (1 - mix) + b.B * mix) * (.48f + glow * .35f));
            dst[i + 3] = 255;
        }
    }

    private static void DrawMaterial(byte[] dst, TextureImage src)
    {
        float srcAspect = src.Width / (float)src.Height;
        float dstAspect = Width / (float)Height;
        float cropW = srcAspect > dstAspect ? src.Height * dstAspect : src.Width;
        float cropH = srcAspect > dstAspect ? src.Height : src.Width / dstAspect;
        float ox = (src.Width - cropW) * .5f, oy = (src.Height - cropH) * .5f;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int sx = Math.Clamp((int)(ox + x / (float)Width * cropW), 0, src.Width - 1);
            int sy = Math.Clamp((int)(oy + y / (float)Height * cropH), 0, src.Height - 1);
            int si = (sy * src.Width + sx) * 4, di = (y * Width + x) * 4;
            float alpha = src.Rgba[si + 3] / 255f;
            float studio = .72f + .28f * MathF.Max(0, 1f - MathF.Abs(x / (float)Width - .37f) * 2.8f);
            Blend(dst, di, src.Rgba[si], src.Rgba[si + 1], src.Rgba[si + 2], alpha * .88f, studio);
        }
    }

    private static void DrawParticle(byte[] dst, TextureImage src, (float R, float G, float B) tint)
    {
        // A hero sprite plus two soft echoes gives a representative effect card without pretending to
        // simulate the full system. The source sprite and alpha are always the real authored texture.
        DrawGlow(dst, .50f, .48f, .44f, tint, .55f);
        DrawSprite(dst, src, .50f, .48f, .72f, 1f);
        DrawSprite(dst, src, .19f, .68f, .28f, .34f);
        DrawSprite(dst, src, .82f, .27f, .22f, .25f);
    }

    private static void DrawGlow(byte[] dst, float cx, float cy, float radius,
        (float R, float G, float B) color, float strength)
    {
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            float dx = x / (float)Width - cx, dy = y / (float)Height - cy;
            float amount = MathF.Exp(-(dx * dx + dy * dy) / (radius * radius) * 4f) * strength;
            int i = (y * Width + x) * 4;
            dst[i] = B(dst[i] / 255f + color.R * amount);
            dst[i + 1] = B(dst[i + 1] / 255f + color.G * amount);
            dst[i + 2] = B(dst[i + 2] / 255f + color.B * amount);
        }
    }

    private static void DrawSprite(byte[] dst, TextureImage src, float cx, float cy, float scale, float opacity)
    {
        float size = Math.Min(Width, Height) * scale;
        int left = (int)(cx * Width - size / 2), top = (int)(cy * Height - size / 2);
        int right = (int)(cx * Width + size / 2), bottom = (int)(cy * Height + size / 2);
        for (int y = Math.Max(0, top); y < Math.Min(Height, bottom); y++)
        for (int x = Math.Max(0, left); x < Math.Min(Width, right); x++)
        {
            int sx = Math.Clamp((int)((x - left) / size * src.Width), 0, src.Width - 1);
            int sy = Math.Clamp((int)((y - top) / size * src.Height), 0, src.Height - 1);
            int si = (sy * src.Width + sx) * 4, di = (y * Width + x) * 4;
            float alpha = src.Rgba[si + 3] / 255f * opacity;
            Blend(dst, di, src.Rgba[si], src.Rgba[si + 1], src.Rgba[si + 2], alpha, 1.15f);
        }
    }

    private static void Finish(byte[] dst, bool particle)
    {
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            float nx = x / (float)Width * 2 - 1, ny = y / (float)Height * 2 - 1;
            float vignette = Math.Clamp(1f - (nx * nx + ny * ny) * (particle ? .24f : .16f), .55f, 1f);
            float topShine = MathF.Max(0, 1f - MathF.Abs((x + y * .7f) / Width - .38f) * 13f) * .08f;
            int i = (y * Width + x) * 4;
            dst[i] = B(dst[i] / 255f * vignette + topShine);
            dst[i + 1] = B(dst[i + 1] / 255f * vignette + topShine);
            dst[i + 2] = B(dst[i + 2] / 255f * vignette + topShine);
        }
    }

    private static void Blend(byte[] dst, int i, byte r, byte g, byte b, float alpha, float light)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        dst[i] = B(dst[i] / 255f * (1 - alpha) + r / 255f * light * alpha);
        dst[i + 1] = B(dst[i + 1] / 255f * (1 - alpha) + g / 255f * light * alpha);
        dst[i + 2] = B(dst[i + 2] / 255f * (1 - alpha) + b / 255f * light * alpha);
    }

    private static (float R, float G, float B) Color(uint hash, float saturation, float value)
    {
        float h = (hash % 360) / 60f;
        float c = value * saturation, x = c * (1 - MathF.Abs(h % 2 - 1)), m = value - c;
        var rgb = h switch
        {
            < 1 => (c, x, 0f), < 2 => (x, c, 0f), < 3 => (0f, c, x),
            < 4 => (0f, x, c), < 5 => (x, 0f, c), _ => (c, 0f, x),
        };
        return (rgb.Item1 + m, rgb.Item2 + m, rgb.Item3 + m);
    }

    private static byte B(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
}
