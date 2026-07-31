using System;

namespace ReyEngine.Rendering.D3D11;

/// <summary>The placement types the viewport marks.</summary>
public enum IconGlyph { Particle = 0, Sound = 1, Prop = 2, Probe = 3, Light = 4 }

/// <summary>
/// <para>M271: the marker glyphs, rasterised in code rather than loaded from files.</para>
///
/// <para>There is no icon art in the repository - the OpenGL viewport draws a soft dot and nothing else -
/// so shipping textured markers meant either introducing an art pipeline or drawing them. Drawing them
/// has no missing-file failure mode, no packaging step, and no way for a build to ship with the icons
/// silently absent, which for five small symbols is worth more than the fidelity a painted sprite would
/// add.</para>
///
/// <para>Each glyph is a white RGBA image whose ALPHA carries the shape; the colour comes from the
/// overlay's constant buffer, so one glyph serves every tint and the same five textures cover both the
/// normal and the selected state.</para>
/// </summary>
public static class IconGlyphs
{
    public const int Size = 64;

    public static byte[] Build(IconGlyph glyph)
    {
        var px = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            // Centred [-1, 1] coordinates, y up, so the shapes read the way they are described.
            float u = (x + 0.5f) / Size * 2f - 1f;
            float v = 1f - (y + 0.5f) / Size * 2f;
            float a = glyph switch
            {
                IconGlyph.Particle => Spark(u, v),
                IconGlyph.Sound => Speaker(u, v),
                IconGlyph.Prop => Cube(u, v),
                IconGlyph.Probe => Probe(u, v),
                _ => Light(u, v),
            };
            int o = (y * Size + x) * 4;
            px[o] = 255; px[o + 1] = 255; px[o + 2] = 255;
            px[o + 3] = (byte)Math.Clamp(a * 255f, 0f, 255f);
        }
        return px;
    }

    // A four-point spark: bright core, four tapering arms. Reads as "emitter" at a dozen pixels, which is
    // the size these are actually seen at.
    private static float Spark(float u, float v)
    {
        float r = MathF.Sqrt(u * u + v * v);
        float core = Smooth(0.22f, 0.05f, r);
        float arms = MathF.Max(Arm(u, v), Arm(v, u));
        return MathF.Min(1f, core + arms * 0.9f);
    }

    private static float Arm(float along, float across)
    {
        float d = MathF.Abs(along);
        if (d > 0.92f) return 0f;
        float halfWidth = 0.12f * (1f - d / 0.92f);          // tapers to a point
        return Smooth(halfWidth, halfWidth * 0.5f + 0.01f, MathF.Abs(across));
    }

    // A speaker: solid body, a cone, and two arcs to the right.
    private static float Speaker(float u, float v)
    {
        float body = Box(u + 0.45f, v, 0.16f, 0.22f);
        float cone = 0f;
        if (u > -0.62f && u < -0.05f)
        {
            float t = (u + 0.62f) / 0.57f;                    // 0 at the back, 1 at the mouth
            cone = MathF.Abs(v) <= 0.12f + t * 0.5f ? 1f : 0f;
        }
        float arcs = MathF.Max(Arc(u, v, 0.42f), Arc(u, v, 0.68f));
        return MathF.Min(1f, MathF.Max(MathF.Max(body, cone), arcs * 0.95f));
    }

    private static float Arc(float u, float v, float radius)
    {
        if (u < 0.02f) return 0f;                            // right-hand side only
        float r = MathF.Sqrt(u * u + v * v);
        if (MathF.Abs(v) > r * 0.78f) return 0f;             // keep it a wedge, not a ring
        return Smooth(radius, 0.055f, r) - Smooth(radius - 0.075f, 0.055f, r);
    }

    // An isometric cube: top rhombus plus two side faces, drawn as edges.
    private static float Cube(float u, float v)
    {
        const float w = 0.055f;
        float e = 0f;
        e = MathF.Max(e, Seg(u, v, 0f, 0.72f, -0.62f, 0.36f, w));   // top-left
        e = MathF.Max(e, Seg(u, v, 0f, 0.72f, 0.62f, 0.36f, w));    // top-right
        e = MathF.Max(e, Seg(u, v, -0.62f, 0.36f, 0f, 0f, w));      // mid-left
        e = MathF.Max(e, Seg(u, v, 0.62f, 0.36f, 0f, 0f, w));       // mid-right
        e = MathF.Max(e, Seg(u, v, -0.62f, 0.36f, -0.62f, -0.36f, w));
        e = MathF.Max(e, Seg(u, v, 0.62f, 0.36f, 0.62f, -0.36f, w));
        e = MathF.Max(e, Seg(u, v, 0f, 0f, 0f, -0.72f, w));
        e = MathF.Max(e, Seg(u, v, -0.62f, -0.36f, 0f, -0.72f, w));
        e = MathF.Max(e, Seg(u, v, 0.62f, -0.36f, 0f, -0.72f, w));
        return e;
    }

    // A reflection probe: a ring with a specular dot, i.e. a sphere read at icon size.
    private static float Probe(float u, float v)
    {
        float r = MathF.Sqrt(u * u + v * v);
        float ring = Smooth(0.74f, 0.06f, r) - Smooth(0.58f, 0.06f, r);
        float dot = Smooth(0.17f, 0.06f, Dist(u, v, -0.24f, 0.26f));
        return MathF.Min(1f, MathF.Max(ring, dot * 0.9f));
    }

    // A compact sun/bulb mark: solid centre with eight short rays.
    private static float Light(float u, float v)
    {
        float r = MathF.Sqrt(u * u + v * v);
        float core = Smooth(0.28f, 0.05f, r);
        float rays = 0f;
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.PI / 4f;
            rays = MathF.Max(rays, Seg(u, v,
                MathF.Cos(a) * 0.43f, MathF.Sin(a) * 0.43f,
                MathF.Cos(a) * 0.82f, MathF.Sin(a) * 0.82f, 0.055f));
        }
        return MathF.Max(core, rays);
    }

    private static float Box(float u, float v, float hw, float hh)
        => MathF.Abs(u) <= hw && MathF.Abs(v) <= hh ? 1f : 0f;

    private static float Dist(float u, float v, float cx, float cy)
        => MathF.Sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy));

    /// <summary>1 inside <paramref name="edge"/>, fading to 0 over <paramref name="feather"/>.</summary>
    private static float Smooth(float edge, float feather, float d)
    {
        if (d <= edge - feather) return 1f;
        if (d >= edge + feather) return 0f;
        float t = (d - (edge - feather)) / (2f * feather);
        return 1f - (t * t * (3f - 2f * t));
    }

    /// <summary>Anti-aliased line segment, for the glyphs made of edges.</summary>
    private static float Seg(float u, float v, float x0, float y0, float x1, float y1, float w)
    {
        float dx = x1 - x0, dy = y1 - y0;
        float len2 = dx * dx + dy * dy;
        float t = len2 <= 0f ? 0f : Math.Clamp(((u - x0) * dx + (v - y0) * dy) / len2, 0f, 1f);
        return Smooth(w, w * 0.6f, Dist(u, v, x0 + dx * t, y0 + dy * t));
    }
}
