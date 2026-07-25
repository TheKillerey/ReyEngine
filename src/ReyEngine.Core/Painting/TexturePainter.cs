using System.Numerics;
using ReyEngine.Core.Decoding;

namespace ReyEngine.Core.Painting;

/// <summary>The rectangle of texels a dab touched, so only that much is pushed to the GPU or snapshotted
/// for undo. Starts empty; <see cref="Add"/> grows it.</summary>
public struct PaintDirtyRect
{
    public int MinX, MinY, MaxX, MaxY;   // MaxX/MaxY are exclusive
    public bool IsEmpty => MaxX <= MinX || MaxY <= MinY;

    public static PaintDirtyRect Empty => new() { MinX = int.MaxValue, MinY = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue };

    public void Add(int x, int y)
    {
        if (x < MinX) MinX = x;
        if (y < MinY) MinY = y;
        if (x + 1 > MaxX) MaxX = x + 1;
        if (y + 1 > MaxY) MaxY = y + 1;
    }

    public void Union(in PaintDirtyRect other)
    {
        if (other.IsEmpty) return;
        if (other.MinX < MinX) MinX = other.MinX;
        if (other.MinY < MinY) MinY = other.MinY;
        if (other.MaxX > MaxX) MaxX = other.MaxX;
        if (other.MaxY > MaxY) MaxY = other.MaxY;
    }

    public int Width => IsEmpty ? 0 : MaxX - MinX;
    public int Height => IsEmpty ? 0 : MaxY - MinY;
}

/// <summary>Brush settings for one dab.</summary>
public sealed record PaintBrush
{
    /// <summary>Colour to lay down, 0..1 per channel.</summary>
    public Vector3 Color { get; init; } = new(1f, 0f, 0f);
    /// <summary>Radius in WORLD units, not texels. Texel density across Summoner's Rift varies by orders
    /// of magnitude between meshes, so a texel radius would give a brush that is a speck on one surface
    /// and covers a whole prop on the next.</summary>
    public float Radius { get; init; } = 100f;
    /// <summary>Fraction of the radius that gets full strength before the falloff starts. 1 = hard edge,
    /// 0 = soft all the way from the centre.</summary>
    public float Hardness { get; init; } = 0.5f;
    /// <summary>Strength of a single dab, 0..1.</summary>
    public float Opacity { get; init; } = 1f;
    /// <summary>How far past a triangle's UV edge to keep painting, in texels. UV islands are cut apart
    /// in texture space even where the surface is continuous, so a stroke crossing a seam leaves a hairline
    /// gap unless the paint bleeds past the edge — and bilinear filtering plus mipmapping widen that gap
    /// into a visible crack. 51 of base_srx's 159 single-mesh textures have more than 64 islands, so this
    /// is not an edge case.</summary>
    public float SeamBleedTexels { get; init; } = 3f;
    /// <summary>Skip triangles facing away from the camera. Without it a dab near a wall paints its far
    /// side too, because the brush is a sphere in world space and does not know what the user can see.</summary>
    public bool CullBackfaces { get; init; } = true;
}

/// <summary>M172: paints a brush dab into a texture.
///
/// Texel-centric, which is the shape Blender's projection painting uses and the reason it survives real
/// meshes: for each triangle the dab might touch, walk the TEXELS its UV triangle covers, map each texel
/// back to a world position, and test THAT against the brush sphere. The obvious alternative — splat a
/// disc in screen space and smear it into UV — produces gaps at grazing angles, doubles up where
/// triangles overlap in screen space, and changes brush size as you zoom.
///
/// Alpha is never written. League map textures carry cutout information there (609 of Map11's BC1 files
/// use punch-through alpha, median 38.8% of their texels transparent) and painting it would punch holes
/// in foliage or fill them in solid.</summary>
public static class TexturePainter
{
    /// <summary>The texel rectangle a dab on this triangle could touch — the same bound
    /// <see cref="DabTriangle"/> scans. Exposed so undo can snapshot exactly the tiles that are about to
    /// change: bounding it by the whole TRIANGLE instead would mean either capturing a 2048² texture per
    /// dab, or capping the capture and silently losing undo data for large triangles.
    ///
    /// Returns false when the brush cannot reach this triangle at all.</summary>
    public static bool TryGetDabBounds(
        int imageWidth, int imageHeight,
        in Vector3 p0, in Vector3 p1, in Vector3 p2,
        in Vector2 uv0, in Vector2 uv1, in Vector2 uv2,
        in Vector3 center, float radius, float seamBleedTexels,
        out int minX, out int minY, out int maxX, out int maxY)
    {
        minX = minY = maxX = maxY = 0;
        int w = imageWidth, h = imageHeight;
        if (w <= 0 || h <= 0 || radius <= 0f) return false;

        float a0x = uv0.X * w, a0y = uv0.Y * h;
        float a1x = uv1.X * w, a1y = uv1.Y * h;
        float a2x = uv2.X * w, a2y = uv2.Y * h;

        float bleed = MathF.Max(0f, seamBleedTexels);
        float lo = bleed + 1f;
        minX = (int)MathF.Floor(MathF.Min(a0x, MathF.Min(a1x, a2x)) - lo);
        maxX = (int)MathF.Ceiling(MathF.Max(a0x, MathF.Max(a1x, a2x)) + lo);
        minY = (int)MathF.Floor(MathF.Min(a0y, MathF.Min(a1y, a2y)) - lo);
        maxY = (int)MathF.Ceiling(MathF.Max(a0y, MathF.Max(a1y, a2y)) + lo);

        // Shrink to the brush's own footprint. Without this the cost — and the undo capture — is set by
        // how big the TRIANGLE is in UV rather than how big the brush is: a ground triangle can span a
        // million texels, so a small dab would scan all of them to paint three thousand. Measured on a
        // Summoner's Rift ground mesh, a radius-50 dab spent 2.67 ms doing exactly that.
        //
        // A UV-mapped triangle is affine in barycentric space, so the world -> UV stretch is CONSTANT
        // across it and the sphere's footprint can be bounded exactly. The bound is conservative (it uses
        // |a||d1| + |b||d2| rather than the true ellipse), so it can only ever be too generous — never
        // too tight, which would drop texels or lose undo data.
        var e1w = p1 - p0;
        var e2w = p2 - p0;
        var nrm = Vector3.Cross(e1w, e2w);
        float nLen = nrm.Length();
        if (nLen > 1e-12f)
        {
            var unit = nrm / nLen;
            var toC = center - p0;
            float planeDist = Vector3.Dot(toC, unit);
            if (MathF.Abs(planeDist) > radius) return false;   // sphere misses the plane entirely
            float rInPlane = MathF.Sqrt(MathF.Max(0f, radius * radius - planeDist * planeDist));

            float e11 = Vector3.Dot(e1w, e1w), e12 = Vector3.Dot(e1w, e2w), e22 = Vector3.Dot(e2w, e2w);
            float gram = e11 * e22 - e12 * e12;
            if (gram > 1e-12f)
            {
                var proj = toC - unit * planeDist;
                float c1 = Vector3.Dot(proj, e1w), c2 = Vector3.Dot(proj, e2w);
                float baryA = (e22 * c1 - e12 * c2) / gram;
                float baryB = (e11 * c2 - e12 * c1) / gram;

                float cxT = a0x + baryA * (a1x - a0x) + baryB * (a2x - a0x);
                float cyT = a0y + baryA * (a1y - a0y) + baryB * (a2y - a0y);

                float len1 = MathF.Sqrt(e11), len2 = MathF.Sqrt(e22), absE12 = MathF.Abs(e12);
                float maxA = rInPlane * (e22 * len1 + absE12 * len2) / gram;
                float maxB = rInPlane * (e11 * len2 + absE12 * len1) / gram;
                float du = maxA * MathF.Abs(a1x - a0x) + maxB * MathF.Abs(a2x - a0x) + lo;
                float dv = maxA * MathF.Abs(a1y - a0y) + maxB * MathF.Abs(a2y - a0y) + lo;

                minX = Math.Max(minX, (int)MathF.Floor(cxT - du));
                maxX = Math.Min(maxX, (int)MathF.Ceiling(cxT + du));
                minY = Math.Max(minY, (int)MathF.Floor(cyT - dv));
                maxY = Math.Min(maxY, (int)MathF.Ceiling(cyT + dv));
            }
        }

        minX = Math.Max(minX, 0); minY = Math.Max(minY, 0);
        maxX = Math.Min(maxX, w - 1); maxY = Math.Min(maxY, h - 1);
        return minX <= maxX && minY <= maxY;
    }

    /// <summary>Paint one triangle's contribution to a dab. Returns true if any texel changed.</summary>
    /// <param name="image">Mutated in place. Its <c>Rgba</c> is the same array the viewport uploaded.</param>
    /// <param name="viewDir">Direction the camera is looking, for backface culling. Pass
    /// <see cref="Vector3.Zero"/> to disable regardless of <see cref="PaintBrush.CullBackfaces"/>.</param>
    public static bool DabTriangle(
        TextureImage image,
        in Vector3 p0, in Vector3 p1, in Vector3 p2,
        in Vector2 uv0, in Vector2 uv1, in Vector2 uv2,
        in Vector3 center, in Vector3 viewDir, PaintBrush brush,
        ref PaintDirtyRect dirty)
    {
        int w = image.Width, h = image.Height;
        if (w <= 0 || h <= 0 || brush.Radius <= 0f) return false;

        if (brush.CullBackfaces && viewDir != Vector3.Zero)
        {
            var n = Vector3.Cross(p1 - p0, p2 - p0);
            // > 0 means the face points the same way the camera looks, i.e. we see its back.
            if (Vector3.Dot(n, viewDir) > 0f) return false;
        }

        // UV -> texel, matching AtlasRasterizer: v * Height with NO flip. That convention is the one the
        // shipped lightmap bake proves in game; introducing a flip here would paint mirrored.
        float a0x = uv0.X * w, a0y = uv0.Y * h;
        float a1x = uv1.X * w, a1y = uv1.Y * h;
        float a2x = uv2.X * w, a2y = uv2.Y * h;

        float bleed = MathF.Max(0f, brush.SeamBleedTexels);
        if (!TryGetDabBounds(w, h, p0, p1, p2, uv0, uv1, uv2, center, brush.Radius, bleed,
                out int minX, out int minY, out int maxX, out int maxY)) return false;

        // Barycentric setup in texel space (constant per triangle).
        float d00x = a1x - a0x, d00y = a1y - a0y;
        float d01x = a2x - a0x, d01y = a2y - a0y;
        float denom = d00x * d01y - d01x * d00y;
        if (MathF.Abs(denom) < 1e-9f) return false;   // degenerate in UV — nothing to paint
        float invDenom = 1f / denom;

        float r2 = brush.Radius * brush.Radius;
        float inner = Math.Clamp(brush.Hardness, 0f, 0.999f);
        float opacity = Math.Clamp(brush.Opacity, 0f, 1f);
        float cr = Math.Clamp(brush.Color.X, 0f, 1f) * 255f;
        float cg = Math.Clamp(brush.Color.Y, 0f, 1f) * 255f;
        float cb = Math.Clamp(brush.Color.Z, 0f, 1f) * 255f;

        var px = image.Rgba;
        bool touched = false;

        for (int ty = minY; ty <= maxY; ty++)
        {
            float py = ty + 0.5f;
            for (int tx = minX; tx <= maxX; tx++)
            {
                float pxc = tx + 0.5f;

                // Barycentrics of this texel centre within the UV triangle.
                float vx = pxc - a0x, vy = py - a0y;
                float bu = (vx * d01y - d01x * vy) * invDenom;
                float bv = (d00x * vy - vx * d00y) * invDenom;
                float bw = 1f - bu - bv;

                // Outside the triangle? Clamp onto it, then require the texel to be within the seam
                // bleed distance of where it landed. This is what carries a stroke across a UV seam.
                float cu = bu, cv = bv, cw = bw;
                if (bu < 0f || bv < 0f || bw < 0f)
                {
                    if (bleed <= 0f) continue;
                    ClampToTriangle(ref cu, ref cv, ref cw);
                    float ex = a0x * cw + a1x * cu + a2x * cv - pxc;
                    float ey = a0y * cw + a1y * cu + a2y * cv - py;
                    if (ex * ex + ey * ey > bleed * bleed) continue;
                }

                // Texel -> world, and the actual brush test.
                var world = p0 * cw + p1 * cu + p2 * cv;
                float dist2 = Vector3.DistanceSquared(world, center);
                if (dist2 > r2) continue;

                float t = MathF.Sqrt(dist2) / brush.Radius;
                float f = t <= inner ? 1f : 1f - (t - inner) / (1f - inner);
                f = f * f * (3f - 2f * f);                 // smoothstep, so the edge isn't a ring
                float alpha = f * opacity;
                if (alpha <= 0.0005f) continue;

                int o = (ty * w + tx) * 4;

                // Don't paint texels the texture cuts away. They are invisible in game, and on a BC1
                // cutout texture they are actively harmful: writing colour into them forces the encoder
                // to fit a wider colour range per block, which degrades the visible texels around them.
                // Measured on Order_MidRiver_B_1bitalpha, painting through the cutouts pushed the
                // save round-trip from ~1 to 20.15 RMSE.
                if (px[o + 3] < 8) continue;

                px[o] = (byte)(px[o] + (cr - px[o]) * alpha + 0.5f);
                px[o + 1] = (byte)(px[o + 1] + (cg - px[o + 1]) * alpha + 0.5f);
                px[o + 2] = (byte)(px[o + 2] + (cb - px[o + 2]) * alpha + 0.5f);
                // px[o + 3] deliberately untouched — see the type remarks.
                dirty.Add(tx, ty);
                touched = true;
            }
        }
        return touched;
    }

    /// <summary>Nearest point of the triangle, in barycentric form. Used only for seam bleed, where a
    /// texel just outside the island still needs a world position to distance-test against.</summary>
    private static void ClampToTriangle(ref float u, ref float v, ref float w)
    {
        u = MathF.Max(u, 0f); v = MathF.Max(v, 0f); w = MathF.Max(w, 0f);
        float sum = u + v + w;
        if (sum <= 1e-9f) { u = 0f; v = 0f; w = 1f; return; }
        float inv = 1f / sum;
        u *= inv; v *= inv; w *= inv;
    }
}
