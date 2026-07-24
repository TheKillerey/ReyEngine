using System.Numerics;

namespace ReyEngine.Formats.Baking;

/// <summary>One atlas' worth of baked light: linear RGB per texel plus the coverage mask, before any
/// encoding. Width/Height are the atlas resolution.</summary>
public sealed class BakedAtlasSurface
{
    public required string Texture { get; init; }     // the atlas path this belongs to
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required Vector3[] Linear { get; init; }   // Width*Height linear illumination
    public required bool[] Covered { get; init; }     // texels an actual triangle landed on
    public int CoveredCount { get; init; }
}

/// <summary>M158: rasterizes map triangles into lightmap-atlas texel space and shades each covered
/// texel. This is the core of a bake into an EXISTING atlas layout: the mapgeo already carries the
/// per-vertex atlas UVs (Texcoord7 * BakedLight.Scale + Bias), so no unwrapping or packing is needed
/// and no geometry has to be rewritten — only the atlas image changes.</summary>
public static class AtlasRasterizer
{
    /// <summary>A triangle to rasterize, in atlas UV space plus its world attributes.</summary>
    public readonly struct Tri
    {
        public readonly Vector2 Uv0, Uv1, Uv2;
        public readonly Vector3 P0, P1, P2;
        public readonly Vector3 N0, N1, N2;
        public Tri(Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector3 p0, Vector3 p1, Vector3 p2,
                   Vector3 n0, Vector3 n1, Vector3 n2)
        { Uv0 = uv0; Uv1 = uv1; Uv2 = uv2; P0 = p0; P1 = p1; P2 = p2; N0 = n0; N1 = n1; N2 = n2; }
    }

    /// <summary>Shade one atlas.
    ///
    /// UV orientation: the surface buffer is TOP-LEFT origin (same as TextureImage), and the viewport
    /// uploads that buffer straight to GL, where row 0 is v = 0. So the texel row for a UV is
    /// v * Height with no flip — inserting one here would mirror every bake against the geometry.</summary>
    public static BakedAtlasSurface Rasterize(
        string texture, int width, int height, IReadOnlyList<Tri> triangles,
        BakeLighting lighting, BakeScene? scene, BakeSettings settings,
        CancellationToken ct = default)
    {
        var linear = new Vector3[width * height];
        var covered = new bool[width * height];
        var worldPos = new Vector3[width * height];
        var worldNrm = new Vector3[width * height];

        // Pass 1: rasterize geometry into the atlas (cheap, single-threaded, no ray tracing).
        foreach (var t in triangles)
        {
            ct.ThrowIfCancellationRequested();
            RasterizeTriangle(t, width, height, covered, worldPos, worldNrm);
        }

        int coveredCount = 0;
        for (int i = 0; i < covered.Length; i++) if (covered[i]) coveredCount++;

        // Pass 2: shade the covered texels. This is where every ray is traced, so it is the part worth
        // parallelising; each texel is independent, and BakeScene queries are read-only.
        var opts = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, height, opts, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (!covered[i]) continue;
                linear[i] = Shade(worldPos[i], worldNrm[i], lighting, scene, settings, i);
            }
        });

        // Pass 3: bleed the covered texels outward so bilinear filtering at chart edges cannot pick up
        // an empty gutter texel (a dark seam along every chart boundary otherwise).
        Dilate(linear, covered, width, height, Math.Max(0, settings.Dilation));

        return new BakedAtlasSurface
        {
            Texture = texture, Width = width, Height = height,
            Linear = linear, Covered = covered, CoveredCount = coveredCount,
        };
    }

    private static void RasterizeTriangle(in Tri t, int width, int height,
        bool[] covered, Vector3[] worldPos, Vector3[] worldNrm)
    {
        // Atlas UV -> texel centres. Texel (x,y) covers u in [x/W, (x+1)/W), so its centre is (x+0.5)/W.
        var a = new Vector2(t.Uv0.X * width - 0.5f, t.Uv0.Y * height - 0.5f);
        var b = new Vector2(t.Uv1.X * width - 0.5f, t.Uv1.Y * height - 0.5f);
        var c = new Vector2(t.Uv2.X * width - 0.5f, t.Uv2.Y * height - 0.5f);

        float area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
        if (MathF.Abs(area) < 1e-9f) return;      // degenerate in UV space: contributes no texels
        float invArea = 1f / area;

        // Expand the bounds by one texel: a chart's boundary texel centre often sits just outside the
        // triangle, and leaving it uncovered is exactly the seam the dilation pass has to repair.
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))) - 1);
        int maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))) + 1);
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))) - 1);
        int maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))) + 1);
        if (minX > maxX || minY > maxY) return;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int i = y * width + x;
                if (covered[i]) continue;      // first triangle wins; overlapping charts are a UV bug

                var p = new Vector2(x, y);
                float w0 = ((b.X - p.X) * (c.Y - p.Y) - (c.X - p.X) * (b.Y - p.Y)) * invArea;
                float w1 = ((c.X - p.X) * (a.Y - p.Y) - (a.X - p.X) * (c.Y - p.Y)) * invArea;
                float w2 = 1f - w0 - w1;

                // Snap texels that fall just outside onto the nearest point of the triangle instead of
                // dropping them: their world position stays on the surface, which is what matters.
                const float Edge = -0.35f;      // ~1/3 texel of slack, in barycentric units
                if (w0 < Edge || w1 < Edge || w2 < Edge) continue;
                w0 = Math.Clamp(w0, 0f, 1f); w1 = Math.Clamp(w1, 0f, 1f); w2 = Math.Clamp(w2, 0f, 1f);
                float sum = w0 + w1 + w2;
                if (sum < 1e-6f) continue;
                w0 /= sum; w1 /= sum; w2 /= sum;

                var pos = t.P0 * w0 + t.P1 * w1 + t.P2 * w2;
                var nrm = t.N0 * w0 + t.N1 * w1 + t.N2 * w2;
                float len = nrm.Length();
                nrm = len > 1e-6f ? nrm / len : Vector3.UnitY;

                covered[i] = true;
                worldPos[i] = pos;
                worldNrm[i] = nrm;
            }
        }
    }

    /// <summary>Evaluate the light model at one surface point. Deliberately mirrors the viewport
    /// fragment shader term for term (see BakeLighting) so baked output matches the preview.</summary>
    private static Vector3 Shade(Vector3 pos, Vector3 nrm, BakeLighting lighting, BakeScene? scene,
        BakeSettings settings, int texelSeed)
    {
        var origin = pos + nrm * settings.RayBias;

        // --- sun: sky term + N.L, optionally shadowed --- (linear illumination)
        float ndl = MathF.Max(Vector3.Dot(nrm, lighting.DirectionToSun), 0f);
        float sunVis = 1f;
        if (ndl > 0f && lighting.SunShadows && scene is not null)
            sunVis = SunVisibility(origin, nrm, lighting, scene, settings, texelSeed);

        // Ambient occlusion darkens the sky term only (the sun already carries its own shadow).
        float ao = settings.AmbientOcclusionSamples > 0 && scene is not null
            ? AmbientOcclusion(origin, nrm, scene, settings, texelSeed) : 1f;
        var ambient = lighting.SkyLight * ao + lighting.SunColor * (ndl * sunVis);

        // --- point lights: identical falloff to the shader's, including the 0.35/0.65 N.L wrap ---
        var pointLight = Vector3.Zero;
        var lights = lighting.PointLights;
        for (int i = 0; i < lights.Count; i++)
        {
            var l = lights[i];
            var lp = lighting.ResolvePosition(l);
            float radius = lighting.ResolveRadius(l);
            if (radius <= 0f) continue;
            var toLight = lp - pos;
            float dist = toLight.Length();
            if (dist >= radius) continue;

            float atten = 1f - dist / radius;
            atten *= atten;
            var dir = toLight / MathF.Max(dist, 1e-4f);
            float nl = MathF.Max(Vector3.Dot(nrm, dir), 0f);
            float vis = 1f;
            if (lighting.PointLightShadows && scene is not null)
                vis = PointVisibility(origin, lp, l, radius, scene, settings, texelSeed + i);
            pointLight += l.Color * (Math.Clamp(l.Intensity, 0f, 64f) * atten * (0.35f + 0.65f * nl) * vis);
        }
        pointLight *= lighting.LightIntensity;

        // Store LINEAR irradiance — the standard lightmap convention, and specifically what Riot's own
        // atlases store (so the GAME renders ours the same way it renders theirs: correctly). The map
        // multiplies the atlas by lightMapColorScale, so divide it out; the sum is ambient + point lights,
        // all linear, which is what the in-game lightmap shader expects.
        //
        // NOTE this deliberately does NOT pre-invert the ReyEngine viewport's pow(1/2.2) display curve.
        // That curve is a preview-only approximation; baking to cancel it made the atlas correct in the
        // editor but too bright in-game, because the game applies a weaker transform. Matching Riot's
        // linear storage is what makes the shipped result right. The trade-off is that the editor preview
        // renders point lights a touch softer than in-game (its pow compresses them) — a preview
        // limitation, not a bake error.
        var lit = (ambient + pointLight) * settings.Exposure;
        return lit / MathF.Max(lighting.LightMapColorScale, 1e-3f);
    }

    private static float SunVisibility(Vector3 origin, Vector3 nrm, BakeLighting lighting,
        BakeScene scene, BakeSettings settings, int seed)
    {
        int samples = Math.Max(1, settings.SunSamples);
        float far = (scene.BoundsMax - scene.BoundsMin).Length() + 1f;
        if (samples == 1)
            return scene.Occluded(origin, lighting.DirectionToSun, far) ? 0f : 1f;

        // Jitter inside a small cone around the sun direction: real sunlight has an angular size, and a
        // single ray gives the hard stencil-shadow look Riot's bakes do not have.
        BuildBasis(lighting.DirectionToSun, out var tangent, out var bitangent);
        const float ConeRadius = 0.035f;   // ~2 degrees
        int hits = 0;
        for (int s = 0; s < samples; s++)
        {
            var (jx, jy) = DiskSample(s, samples, seed);
            var d = Vector3.Normalize(lighting.DirectionToSun + tangent * (jx * ConeRadius) + bitangent * (jy * ConeRadius));
            if (Vector3.Dot(d, nrm) <= 0f) { hits++; continue; }   // sample fell below the horizon
            if (scene.Occluded(origin, d, far)) hits++;
        }
        return 1f - hits / (float)samples;
    }

    private static float PointVisibility(Vector3 origin, Vector3 lightPos, in BakePointLight light,
        float radius, BakeScene scene, BakeSettings settings, int seed)
    {
        int samples = Math.Max(1, settings.PointLightSamples);
        if (samples == 1)
        {
            var d = lightPos - origin;
            float dist = d.Length();
            return dist < 1e-4f || !scene.Occluded(origin, d / dist, dist) ? 1f : 0f;
        }

        // Treat the light as a small sphere so its shadows have a penumbra that widens with distance.
        float lightRadius = MathF.Max(radius * 0.03f, 1f);
        int visible = 0;
        for (int s = 0; s < samples; s++)
        {
            var (jx, jy) = DiskSample(s, samples, seed);
            var dirToLight = Vector3.Normalize(lightPos - origin);
            BuildBasis(dirToLight, out var tangent, out var bitangent);
            var target = lightPos + tangent * (jx * lightRadius) + bitangent * (jy * lightRadius);
            var d = target - origin;
            float dist = d.Length();
            if (dist < 1e-4f || !scene.Occluded(origin, d / dist, dist)) visible++;
        }
        return visible / (float)samples;
    }

    private static float AmbientOcclusion(Vector3 origin, Vector3 nrm, BakeScene scene,
        BakeSettings settings, int seed)
    {
        int samples = Math.Max(1, settings.AmbientOcclusionSamples);
        float radius = MathF.Max(settings.AmbientOcclusionRadius, 1f);
        BuildBasis(nrm, out var tangent, out var bitangent);
        int open = 0;
        for (int s = 0; s < samples; s++)
        {
            // Cosine-weighted hemisphere: concentric-disk sample lifted onto the hemisphere, which
            // matches the cosine term a diffuse surface integrates anyway.
            var (dx, dy) = DiskSample(s, samples, seed);
            float r2 = dx * dx + dy * dy;
            float z = MathF.Sqrt(MathF.Max(0f, 1f - r2));
            var d = Vector3.Normalize(tangent * dx + bitangent * dy + nrm * z);
            if (!scene.Occluded(origin, d, radius)) open++;
        }
        return open / (float)samples;
    }

    /// <summary>Stratified sample on the unit disk. Deterministic in (index, seed) so a re-bake with the
    /// same settings produces the same atlas byte for byte — scripts are unavailable in this layer, and
    /// a reproducible bake is worth more than true randomness anyway.</summary>
    private static (float x, float y) DiskSample(int index, int count, int seed)
    {
        // Golden-angle spiral, rotated per texel so neighbouring texels do not share a pattern.
        float t = (index + 0.5f) / count;
        float r = MathF.Sqrt(t);
        float phase = (seed * 0.6180339887f) % 1f;
        float angle = (index * 2.39996323f) + phase * MathF.Tau;
        return (r * MathF.Cos(angle), r * MathF.Sin(angle));
    }

    private static void BuildBasis(Vector3 n, out Vector3 tangent, out Vector3 bitangent)
    {
        var up = MathF.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        tangent = Vector3.Normalize(Vector3.Cross(up, n));
        bitangent = Vector3.Cross(n, tangent);
    }

    /// <summary>Push covered colours outward by <paramref name="rings"/> texels. Riot's atlases show a
    /// 2-texel replicated border around every chart; anything less and a bilinear tap at a chart edge
    /// mixes in empty gutter.</summary>
    private static void Dilate(Vector3[] linear, bool[] covered, int width, int height, int rings)
    {
        if (rings <= 0) return;
        var filled = (bool[])covered.Clone();
        for (int pass = 0; pass < rings; pass++)
        {
            var next = (bool[])filled.Clone();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (filled[i]) continue;
                    var sum = Vector3.Zero;
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int sy = y + dy;
                        if (sy < 0 || sy >= height) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int sx = x + dx;
                            if (sx < 0 || sx >= width) continue;
                            int si = sy * width + sx;
                            if (!filled[si]) continue;
                            sum += linear[si]; n++;
                        }
                    }
                    if (n == 0) continue;
                    linear[i] = sum / n;
                    next[i] = true;
                }
            }
            filled = next;
        }
    }
}
