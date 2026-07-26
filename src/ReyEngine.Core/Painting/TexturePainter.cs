using System.Numerics;
using System.Threading.Tasks;
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

/// <summary>A precomputed texel rectangle for one triangle's dab (inclusive bounds), so the caller can
/// derive it once and share it between the undo snapshot and the paint.</summary>
public readonly record struct DabBounds(int MinX, int MinY, int MaxX, int MaxY);

/// <summary>M173: how a dab's colour combines with what is already on the texture. Each one is the
/// standard compositing formula, computed per channel in 0..1 and then mixed by the brush's coverage —
/// so Strength still fades any mode smoothly, rather than every mode being all-or-nothing.</summary>
public enum PaintBlendMode
{
    /// <summary>Replace. What a paint program calls Normal.</summary>
    Normal,
    /// <summary>a*b — only ever darkens. Good for shadowing and grime.</summary>
    Multiply,
    /// <summary>1-(1-a)(1-b) — only ever lightens. Good for dust, snow, bleaching.</summary>
    Screen,
    /// <summary>Multiply the darks, screen the lights, keyed off the EXISTING pixel. Adds contrast while
    /// keeping the underlying detail, which is why it is the usual choice for tinting terrain.</summary>
    Overlay,
    /// <summary>Like Overlay but keyed off the BRUSH colour, so a mid-grey brush leaves the image alone
    /// and the result never clips as hard.</summary>
    SoftLight,
    Add,
    Subtract,
    Darken,
    Lighten,
    /// <summary>Take the brush's hue and saturation, keep the texture's brightness. Recolours a surface
    /// without flattening the detail painted into it — the mode you want for restyling stone or foliage.</summary>
    Color,
    /// <summary>The complement: keep the texture's colour, take the brush's brightness.</summary>
    Luminosity,
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

    /// <summary>M173: how the colour combines with what is already there.</summary>
    public PaintBlendMode BlendMode { get; init; } = PaintBlendMode.Normal;

    /// <summary>M173: optional stencil shaping the dab. Null = plain radial falloff.</summary>
    public BrushMask? Mask { get; init; }

    /// <summary>Mask rotation in radians, about the dab centre in the surface plane.</summary>
    public float MaskAngle { get; init; }
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
    /// <summary>How far a dab actually reaches, which is NOT always the brush radius.
    ///
    /// An unmasked brush is a disc of exactly Radius. A masked one is a SQUARE stencil of half-width
    /// Radius — Photoshop's model, and the only one under which a square or ring stencil means anything —
    /// so once rotated its corners reach Radius * sqrt(2). Clipping a mask to the disc was a real bug:
    /// every stencil came out circular, and rotating a square one changed 17 texels out of 30,000.</summary>
    public static float EffectiveRadius(PaintBrush brush)
        => brush.Mask is null ? brush.Radius : brush.Radius * 1.41421356f;


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

                // Exact half-extents of the footprint. The brush disc maps to an ELLIPSE in texel space,
                // and the axis-aligned extent of {M v : |v| <= r} along an axis is r * ||row of M||.
                // Taking the gradient of each texel coordinate with respect to in-plane world position
                // gives those rows directly.
                //
                // The previous bound summed the two barycentric contributions in absolute value, which is
                // an L1 over-estimate of an L2 quantity — up to sqrt(2) too wide per axis, so up to 2x the
                // area. That matters more than it looks: the loop is memory-bound (a radius-150 dab walks
                // ~900,000 texels scattered across several 2048^2 images), so scanned area translates
                // almost linearly into time, and no amount of threading buys it back.
                var gradA = (e1w * e22 - e2w * e12) / gram;
                var gradB = (e2w * e11 - e1w * e12) / gram;
                var gradX = gradA * (a1x - a0x) + gradB * (a2x - a0x);
                var gradY = gradA * (a1y - a0y) + gradB * (a2y - a0y);
                float du = rInPlane * gradX.Length() + lo;
                float dv = rInPlane * gradY.Length() + lo;

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
        ref PaintDirtyRect dirty, DabBounds? bounds = null)
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

        float reach = EffectiveRadius(brush);
        float bleed = MathF.Max(0f, brush.SeamBleedTexels);
        // Bounds may be supplied by the caller. The undo snapshot needs the same rectangle, and deriving
        // it twice per triangle was pure duplicated setup on the hot path.
        int minX, minY, maxX, maxY;
        if (bounds is { } b) { minX = b.MinX; minY = b.MinY; maxX = b.MaxX; maxY = b.MaxY; }
        else if (!TryGetDabBounds(w, h, p0, p1, p2, uv0, uv1, uv2, center, reach, bleed,
                out minX, out minY, out maxX, out maxY)) return false;

        // Barycentric setup in texel space (constant per triangle).
        float d00x = a1x - a0x, d00y = a1y - a0y;
        float d01x = a2x - a0x, d01y = a2y - a0y;
        float denom = d00x * d01y - d01x * d00y;
        if (MathF.Abs(denom) < 1e-9f) return false;   // degenerate in UV — nothing to paint
        float invDenom = 1f / denom;

        float r2 = brush.Radius * brush.Radius;
        float reach2 = reach * reach;
        float inner = Math.Clamp(brush.Hardness, 0f, 0.999f);
        float opacity = Math.Clamp(brush.Opacity, 0f, 1f);
        float nr = Math.Clamp(brush.Color.X, 0f, 1f);
        float ng = Math.Clamp(brush.Color.Y, 0f, 1f);
        float nb = Math.Clamp(brush.Color.Z, 0f, 1f);
        float cr = nr * 255f, cg = ng * 255f, cb = nb * 255f;
        var blend = brush.BlendMode;

        var px = image.Rgba;

        // Everything the inner loop needs is AFFINE in texel coordinates, so it can be stepped instead of
        // recomputed. Barycentrics are affine by construction, and the world position is
        // p0 + bu*e1 + bv*e2 — affine in bu/bv, hence affine in (x, y). Recomputing all of it per texel
        // cost about 63 ns/texel, which is what made a big brush feel sluggish; stepping leaves three
        // adds for the barycentrics and one vector add for the position.
        // Copied out of the `in` parameters: a local function cannot capture those, and copying three
        // vectors once per triangle is nothing against a scan of hundreds of thousands of texels.
        Vector3 v0 = p0, v1 = p1, v2 = p2, ctr = center;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        float buDx = d01y * invDenom, buDy = -d01x * invDenom;
        float bvDx = -d00y * invDenom, bvDy = d00x * invDenom;
        var wDx = e1 * buDx + e2 * bvDx;

        // A mask needs an orientation in the surface plane. Built per triangle from its own normal so
        // the stencil lies flat on the geometry instead of being projected from a fixed world axis, which
        // would smear it into a streak on anything that is not a floor.
        var mask = brush.Mask;
        Vector3 maskU = default, maskV = default;
        if (mask is not null)
        {
            var mn = Vector3.Cross(e1, e2);
            mn = mn.LengthSquared() > 1e-12f ? Vector3.Normalize(mn) : Vector3.UnitY;
            var seed = MathF.Abs(mn.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
            var bu = Vector3.Normalize(Vector3.Cross(seed, mn));
            var bv = Vector3.Cross(mn, bu);
            float ca = MathF.Cos(brush.MaskAngle), sa = MathF.Sin(brush.MaskAngle);
            maskU = (bu * ca + bv * sa) / brush.Radius;   // fold the -1..1 normalisation into the basis
            maskV = (bv * ca - bu * sa) / brush.Radius;
        }

        float startX = minX + 0.5f;
        float bleed2 = bleed * bleed;
        float innerR2 = (inner * brush.Radius) * (inner * brush.Radius);
        float invRadius = 1f / brush.Radius;
        float invSoft = 1f / (1f - inner);

        // Row bands are disjoint by construction — no two of them can write the same texel — so this
        // parallelises with no locking and no lost updates. It matters because the cost of a dab is
        // dominated by one or two big ground triangles: a radius-150 dab measured 907,753 texels scanned,
        // and 425,000 of those were in two triangles of the same two textures. Splitting by TEXTURE
        // cannot help there; splitting by row can.
        int rows = maxY - minY + 1;
        long area = (long)rows * (maxX - minX + 1);
        int bands = area >= ParallelTexelThreshold ? Math.Min(Environment.ProcessorCount, rows / 32) : 1;

        if (bands <= 1)
        {
            var single = PaintDirtyRect.Empty;
            bool hit = PaintBand(minY, maxY, ref single);
            if (hit) dirty.Union(single);
            return hit;
        }

        var bandRects = new PaintDirtyRect[bands];
        var bandHit = new bool[bands];
        Parallel.For(0, bands, bi =>
        {
            int y0 = minY + (int)((long)rows * bi / bands);
            int y1 = minY + (int)((long)rows * (bi + 1) / bands) - 1;
            var r = PaintDirtyRect.Empty;
            bandHit[bi] = PaintBand(y0, y1, ref r);
            bandRects[bi] = r;
        });

        bool any = false;
        for (int i = 0; i < bands; i++)
        {
            if (!bandHit[i]) continue;
            any = true;
            dirty.Union(bandRects[i]);
        }
        return any;

        bool PaintBand(int y0, int y1, ref PaintDirtyRect rect)
        {
            bool touched = false;
            for (int ty = y0; ty <= y1; ty++)
            {
                float py = ty + 0.5f;
                float vx0 = startX - a0x, vy0 = py - a0y;
                float bu = (vx0 * d01y - d01x * vy0) * invDenom;
                float bv = (d00x * vy0 - vx0 * d00y) * invDenom;
                var world = v0 + e1 * bu + e2 * bv;
                int rowBase = ty * w;

                for (int tx = minX; tx <= maxX; tx++, bu += buDx, bv += bvDx, world += wDx)
                {
                    float bw = 1f - bu - bv;

                    // Outside the triangle? Clamp onto it, then require the texel to be within the seam
                    // bleed distance of where it landed. This is what carries a stroke across a UV seam.
                    // Rare branch, so it re-derives rather than complicating the stepped fast path.
                    Vector3 sample = world;
                    if (bu < 0f || bv < 0f || bw < 0f)
                    {
                        if (bleed <= 0f) continue;
                        float cu = bu, cv = bv, cw = bw;
                        ClampToTriangle(ref cu, ref cv, ref cw);
                        float pxc = tx + 0.5f;
                        float ex = a0x * cw + a1x * cu + a2x * cv - pxc;
                        float ey = a0y * cw + a1y * cu + a2y * cv - py;
                        if (ex * ex + ey * ey > bleed2) continue;
                        sample = v0 * cw + v1 * cu + v2 * cv;
                    }

                    float dx = sample.X - ctr.X, dy = sample.Y - ctr.Y, dz = sample.Z - ctr.Z;
                    float dist2 = dx * dx + dy * dy + dz * dz;
                    if (dist2 > reach2) continue;

                    int o = (rowBase + tx) * 4;

                    // Don't paint texels the texture cuts away. They are invisible in game, and on a BC1
                    // cutout texture they are actively harmful: writing colour into them forces the encoder
                    // to fit a wider colour range per block, which degrades the visible texels around them.
                    // Measured on Order_MidRiver_B_1bitalpha, painting through the cutouts pushed the
                    // save round-trip from 20.15 RMSE down to 1.20. Checked before the falloff maths so a
                    // cut-away texel costs one byte compare, not a square root.
                    if (px[o + 3] < 8) continue;

                    // Coverage. A stencil REPLACES the radial falloff rather than scaling it — the mask
                    // is the brush's shape, exactly as a brush tip is in a paint program, and multiplying
                    // a disc into it would round off every stencil's corners. Hardness therefore applies
                    // only to the plain round brush, which is also how Photoshop behaves.
                    float alpha;
                    if (mask is not null)
                    {
                        float rel = dx * maskU.X + dy * maskU.Y + dz * maskU.Z;
                        float rev = dx * maskV.X + dy * maskV.Y + dz * maskV.Z;
                        float cov = mask.Sample(rel, rev);   // zero outside its own square
                        if (cov <= 0.002f) continue;
                        alpha = cov * opacity;
                    }
                    else if (dist2 <= innerR2) alpha = opacity;
                    else
                    {
                        // Inside the hard core the falloff is flat, so the root is only paid on the rim.
                        float f = 1f - (MathF.Sqrt(dist2) * invRadius - inner) * invSoft;
                        alpha = f * f * (3f - 2f * f) * opacity;   // smoothstep, so the edge isn't a ring
                    }
                    if (alpha <= 0.0005f) continue;

                    if (blend == PaintBlendMode.Normal)
                    {
                        px[o] = (byte)(px[o] + (cr - px[o]) * alpha + 0.5f);
                        px[o + 1] = (byte)(px[o + 1] + (cg - px[o + 1]) * alpha + 0.5f);
                        px[o + 2] = (byte)(px[o + 2] + (cb - px[o + 2]) * alpha + 0.5f);
                        rect.Add(tx, ty);
                        touched = true;
                        continue;
                    }

                    Blend(blend, px[o] * Inv255, px[o + 1] * Inv255, px[o + 2] * Inv255,
                          nr, ng, nb, out float br2, out float bg2, out float bb2);
                    px[o] = (byte)(px[o] + (br2 * 255f - px[o]) * alpha + 0.5f);
                    px[o + 1] = (byte)(px[o + 1] + (bg2 * 255f - px[o + 1]) * alpha + 0.5f);
                    px[o + 2] = (byte)(px[o + 2] + (bb2 * 255f - px[o + 2]) * alpha + 0.5f);
                    // px[o + 3] deliberately untouched — see the type remarks.
                    rect.Add(tx, ty);
                    touched = true;
                }
            }
            return touched;
        }
    }

    /// <summary>Below this many texels a dab is not worth splitting across threads.</summary>
    private const long ParallelTexelThreshold = 24_000;

    private const float Inv255 = 1f / 255f;

    /// <summary>The compositing formulas, per channel in 0..1. <paramref name="d"/> is what is already on
    /// the texture (destination), <paramref name="s"/> is the brush colour (source).
    ///
    /// Color and Luminosity are the two that are not per-channel: they decompose into luma and chroma, so
    /// they get their own branch. Luma uses the Rec.601 weights, which is what Photoshop's equivalents
    /// use — swapping in Rec.709 would shift every recolour slightly against what an artist expects.</summary>
    private static void Blend(PaintBlendMode mode,
        float dr, float dg, float dbl, float sr, float sg, float sb,
        out float r, out float g, out float b)
    {
        switch (mode)
        {
            case PaintBlendMode.Multiply: r = dr * sr; g = dg * sg; b = dbl * sb; break;
            case PaintBlendMode.Screen:   r = Scr(dr, sr); g = Scr(dg, sg); b = Scr(dbl, sb); break;
            case PaintBlendMode.Overlay:  r = Ovl(dr, sr); g = Ovl(dg, sg); b = Ovl(dbl, sb); break;
            case PaintBlendMode.SoftLight: r = Soft(dr, sr); g = Soft(dg, sg); b = Soft(dbl, sb); break;
            case PaintBlendMode.Add:      r = dr + sr; g = dg + sg; b = dbl + sb; break;
            case PaintBlendMode.Subtract: r = dr - sr; g = dg - sg; b = dbl - sb; break;
            case PaintBlendMode.Darken:   r = MathF.Min(dr, sr); g = MathF.Min(dg, sg); b = MathF.Min(dbl, sb); break;
            case PaintBlendMode.Lighten:  r = MathF.Max(dr, sr); g = MathF.Max(dg, sg); b = MathF.Max(dbl, sb); break;

            case PaintBlendMode.Color:
            {
                // Brush chroma, texture luma: keeps every scratch and crack in the surface while changing
                // what colour it is.
                float dl = Luma(dr, dg, dbl), sl = Luma(sr, sg, sb);
                float k = dl - sl;
                r = sr + k; g = sg + k; b = sb + k;
                ClipToGamut(dl, ref r, ref g, ref b);
                break;
            }
            case PaintBlendMode.Luminosity:
            {
                float dl = Luma(dr, dg, dbl), sl = Luma(sr, sg, sb);
                float k = sl - dl;
                r = dr + k; g = dg + k; b = dbl + k;
                ClipToGamut(sl, ref r, ref g, ref b);
                break;
            }
            default: r = sr; g = sg; b = sb; break;
        }
        r = Math.Clamp(r, 0f, 1f); g = Math.Clamp(g, 0f, 1f); b = Math.Clamp(b, 0f, 1f);
    }

    private static float Scr(float d, float s) => 1f - (1f - d) * (1f - s);
    private static float Ovl(float d, float s) => d < 0.5f ? 2f * d * s : 1f - 2f * (1f - d) * (1f - s);
    private static float Soft(float d, float s) => s < 0.5f
        ? 2f * d * s + d * d * (1f - 2f * s)
        : 2f * d * (1f - s) + MathF.Sqrt(d) * (2f * s - 1f);

    private static float Luma(float r, float g, float b) => 0.299f * r + 0.587f * g + 0.114f * b;

    /// <summary>Pull a luma-shifted colour back inside 0..1 WITHOUT changing its luma. Plain clamping
    /// would shift the brightness the mode just set, which is the whole point of Color/Luminosity.</summary>
    private static void ClipToGamut(float lum, ref float r, ref float g, ref float b)
    {
        float lo = MathF.Min(r, MathF.Min(g, b));
        float hi = MathF.Max(r, MathF.Max(g, b));
        if (lo < 0f && lum - lo > 1e-6f)
        {
            float t = lum / (lum - lo);
            r = lum + (r - lum) * t; g = lum + (g - lum) * t; b = lum + (b - lum) * t;
        }
        if (hi > 1f && hi - lum > 1e-6f)
        {
            float t = (1f - lum) / (hi - lum);
            r = lum + (r - lum) * t; g = lum + (g - lum) * t; b = lum + (b - lum) * t;
        }
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
