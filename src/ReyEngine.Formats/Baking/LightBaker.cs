using System.Numerics;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Baking;

/// <summary>One finished atlas: the image, the encoded .tex bytes, and where it belongs.</summary>
public sealed class BakedAtlas
{
    public required string Texture { get; init; }       // atlas path as the mapgeo references it
    public required string OutputPath { get; init; }    // project-relative path the bytes go to
    public required TextureImage Image { get; init; }
    public required byte[] TexBytes { get; init; }
    public int CoveredTexels { get; init; }
    public int Triangles { get; init; }
}

public sealed record BakeProgress(int AtlasIndex, int AtlasCount, string Texture, string Stage);

/// <summary>M158: bakes lighting into a map's EXISTING lightmap atlas layout.
///
/// Scope note, deliberately: this rewrites atlas IMAGES only. It does not unwrap UVs, pack charts, or
/// touch the .mapgeo. That is not a shortcut, it is the only part that is safe to ship today — a map's
/// lightmapped meshes are INSTANCES that share one vertex buffer and therefore one uv7 layout (Map11
/// has a single buffer shared by 302 meshes), so generating new UV2s means rewriting shared buffers,
/// and LeagueToolkit's mapgeo writer cannot round-trip a file at any version. Re-lighting the layout
/// Riot already authored needs neither.</summary>
public static class LightBaker
{
    /// <summary>Does this map have a lightmap layout to bake into?</summary>
    public static bool CanBakeExistingLayout(MapGeoAsset asset) =>
        asset.HasLightmap && asset.LightmapUvs is not null &&
        asset.Groups.Any(g => !string.IsNullOrEmpty(g.LightmapTexture));

    /// <summary>Every distinct atlas the map references, in a stable order.</summary>
    public static IReadOnlyList<string> EnumerateAtlases(MapGeoAsset asset) =>
        asset.Groups.Select(g => g.LightmapTexture)
             .Where(t => !string.IsNullOrEmpty(t))
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// <summary>Bake every atlas the map uses, handing each one to <paramref name="onAtlas"/> as soon as
    /// it is done.
    ///
    /// Streaming is not a nicety: a 2048 atlas is 16 MB of RGBA plus 24 MB of intermediate vectors, and
    /// the biggest Map12 mapgeo (crepe) references 85 of them — holding them all would cost multiple
    /// gigabytes. The caller writes each atlas to disk and lets it go.</summary>
    /// <param name="groupLightmapEnabled">Per-group (index-aligned with <c>asset.Groups</c>) flag: false
    /// where the material sets NO_BAKED_LIGHTING. Those groups are skipped so the baked coverage matches
    /// what the viewport will actually sample — 20 Map12 materials set it, and baking them anyway puts
    /// light into texels nothing ever reads while the meshes stay unlit.</param>
    public static async Task<int> BakeExistingLayoutAsync(
        MapGeoAsset asset,
        IReadOnlyList<bool>? groupLightmapEnabled,
        BakeLighting lighting,
        BakeSettings settings,
        string mapgeoPath,
        Func<BakedAtlas, Task> onAtlas,
        IProgress<BakeProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(onAtlas);
        if (asset.LightmapUvs is null) return 0;

        var atlases = EnumerateAtlases(asset);
        if (atlases.Count == 0) return 0;

        // Occluders: the whole map, including meshes whose own materials opt out of baked lighting —
        // a NO_BAKED_LIGHTING wall still casts a shadow.
        progress?.Report(new BakeProgress(0, atlases.Count, "", "Building ray-tracing scene"));
        var scene = NeedsRays(lighting, settings) ? new BakeScene(asset.Positions, asset.Indices) : null;

        string folder = settings.ResolveOutputFolder(mapgeoPath);
        int done = 0;

        for (int ai = 0; ai < atlases.Count; ai++)
        {
            ct.ThrowIfCancellationRequested();
            string texture = atlases[ai];
            progress?.Report(new BakeProgress(ai, atlases.Count, texture, "Rasterizing"));

            var tris = CollectTriangles(asset, groupLightmapEnabled, texture);
            if (tris.Count == 0) continue;

            progress?.Report(new BakeProgress(ai, atlases.Count, texture, $"Shading {tris.Count} triangles"));
            var surface = AtlasRasterizer.Rasterize(
                texture, settings.AtlasResolution, settings.AtlasResolution, tris, lighting, scene, settings, ct);

            progress?.Report(new BakeProgress(ai, atlases.Count, texture, "Encoding"));
            var image = Encode(surface, settings);
            var bytes = TexWriter.Write(image, settings.CompressBc3 ? TexFormat.Bc3 : TexFormat.Bc1, settings.GenerateMips);

            await onAtlas(new BakedAtlas
            {
                Texture = texture,
                OutputPath = ResolveOutputPath(texture, folder, settings, ai),
                Image = image,
                TexBytes = bytes,
                CoveredTexels = surface.CoveredCount,
                Triangles = tris.Count,
            }).ConfigureAwait(false);
            done++;
        }

        progress?.Report(new BakeProgress(atlases.Count, atlases.Count, "", "Done"));
        return done;
    }

    /// <summary>Bake the probe volume: for every cell, shoot the six ambient-cube directions from a
    /// point just above the ground and gather what each one sees. This is the lighting used by
    /// everything a lightmap cannot cover — characters, effects, and meshes whose material sets
    /// NO_BAKED_LIGHTING.</summary>
    /// <param name="probeHeight">How far above the sampled ground a probe sits, in world units.</param>
    public static LightGridFile BakeLightGrid(
        MapGeoAsset asset, BakeLighting lighting, BakeSettings settings,
        float probeHeight = 150f, IProgress<BakeProgress>? progress = null, CancellationToken ct = default)
    {
        var min = Bounds(asset, out var boundsMax);

        int w = Math.Max(1, settings.LightGridWidth), h = Math.Max(1, settings.LightGridHeight);
        // No origin field exists in the format, so the grid is anchored at the world origin and sized to
        // reach the far edge of the map — matching how every shipped grid is laid out.
        float sizeX = settings.LightGridFromMapBounds ? MathF.Max(boundsMax.X, 1f) : MathF.Max(settings.LightGridMax.X, 1f);
        float sizeZ = settings.LightGridFromMapBounds ? MathF.Max(boundsMax.Z, 1f) : MathF.Max(settings.LightGridMax.Z, 1f);

        var grid = LightGridFile.Create(w, h, sizeX, sizeZ);
        var scene = new BakeScene(asset.Positions, asset.Indices);
        float groundY = min.Y;
        float far = (boundsMax - min).Length() + 1f;

        progress?.Report(new BakeProgress(0, 1, "lightgrid", $"Baking {w}x{h} probes"));
        var opts = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, h, opts, cz =>
        {
            for (int cx = 0; cx < w; cx++)
            {
                float wx = (cx + 0.5f) / w * sizeX;
                float wz = (cz + 0.5f) / h * sizeZ;

                // Drop onto the geometry so the probe sits above the floor rather than in the air (or
                // buried). Fall back to the map's lowest point where nothing is below.
                float y = groundY + probeHeight;
                var down = new Vector3(0, -1, 0);
                var top = new Vector3(wx, boundsMax.Y + 1f, wz);
                if (scene.Occluded(top, down, far))
                {
                    // Occluded() is any-hit, so walk down in steps to find roughly where the ground is.
                    float step = (boundsMax.Y - min.Y) / 64f;
                    for (float probe = boundsMax.Y; probe > min.Y; probe -= step)
                        if (scene.Occluded(new Vector3(wx, probe, wz), down, step * 1.5f)) { y = probe + probeHeight; break; }
                }

                var p = new Vector3(wx, y, wz);
                int cell = (cz * w + cx) * LightGridFile.Directions;
                for (int d = 0; d < LightGridFile.Directions; d++)
                    grid.Samples[cell + d] = ProbeDirection(p, LightGridFile.DirectionVectors[d], lighting, scene, settings, cell + d);
            }
        });

        grid.FullBrightScale = 1f;
        progress?.Report(new BakeProgress(1, 1, "lightgrid", "Done"));
        return grid;
    }

    /// <summary>One ambient-cube face: the light a surface facing <paramref name="dir"/> would receive.</summary>
    private static Vector3 ProbeDirection(Vector3 p, Vector3 dir, BakeLighting lighting, BakeScene scene,
        BakeSettings settings, int seed)
    {
        float ndl = MathF.Max(Vector3.Dot(dir, lighting.DirectionToSun), 0f);
        float far = (scene.BoundsMax - scene.BoundsMin).Length() + 1f;
        float sunVis = ndl > 0f && lighting.SunShadows && !scene.Occluded(p, lighting.DirectionToSun, far) ? 1f : ndl > 0f && !lighting.SunShadows ? 1f : 0f;
        var lit = lighting.SkyLight + lighting.SunColor * (ndl * sunVis);

        foreach (var l in lighting.PointLights)
        {
            var lp = lighting.ResolvePosition(l);
            float radius = lighting.ResolveRadius(l);
            var toLight = lp - p;
            float dist = toLight.Length();
            if (radius <= 0f || dist >= radius) continue;
            float atten = lighting.Attenuation(dist, radius);   // shared curve — identical to the shader
            var ld = toLight / MathF.Max(dist, 1e-4f);
            float nl = MathF.Max(Vector3.Dot(dir, ld), 0f);
            if (lighting.PointLightShadows && scene.Occluded(p, ld, dist)) continue;
            lit += l.Color * (Math.Clamp(l.Intensity, 0f, 64f) * atten * (0.35f + 0.65f * nl)) * lighting.LightIntensity;
        }
        return lit * settings.Exposure;   // same brightness lever the atlas bake uses
    }

    private static Vector3 Bounds(MapGeoAsset asset, out Vector3 max)
    {
        var min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        var p = asset.Positions;
        for (int i = 0; i + 2 < p.Length; i += 3)
        {
            var v = new Vector3(p[i], p[i + 1], p[i + 2]);
            min = Vector3.Min(min, v); max = Vector3.Max(max, v);
        }
        if (p.Length < 3) { min = Vector3.Zero; max = Vector3.Zero; }
        return min;
    }

    /// <summary>Where a re-baked atlas is written. The mapgeo already names the atlas it samples, so the
    /// bake goes back to THAT path and drops straight in — no bin edit, no mapgeo edit. Deriving the
    /// name from the mapgeo instead would be wrong twice over: the atlas folder need not match the
    /// mapgeo name (Map12's base_srx.mapgeo samples .../Map12/Crepe/), and the enumeration is sorted as
    /// text, so "10.tex" would be handed the index 2 and overwrite the wrong file.
    /// The settings folder is only the fallback for an atlas that has no path of its own.</summary>
    private static string ResolveOutputPath(string texture, string fallbackFolder, BakeSettings settings, int index)
    {
        if (string.IsNullOrWhiteSpace(texture)) return fallbackFolder + settings.AtlasFileName(index);
        string p = texture.Replace('\\', '/').TrimStart('/');
        return settings.ThemeToken.Length == 0 || p.EndsWith($".{settings.ThemeToken}.tex", StringComparison.OrdinalIgnoreCase)
            ? p
            : p[..^".tex".Length] + $".{settings.ThemeToken}.tex";
    }

    private static bool NeedsRays(BakeLighting lighting, BakeSettings settings) =>
        (lighting.SunShadows && settings.SunSamples > 0)
        || (lighting.PointLightShadows && lighting.PointLights.Count > 0 && settings.PointLightSamples > 0)
        || settings.AmbientOcclusionSamples > 0;

    /// <summary>Gather every triangle that lands in one atlas, in atlas UV space.</summary>
    private static List<AtlasRasterizer.Tri> CollectTriangles(
        MapGeoAsset asset, IReadOnlyList<bool>? groupLightmapEnabled, string texture)
    {
        var pos = asset.Positions;
        var nrm = asset.Normals;
        var uv = asset.LightmapUvs!;
        var idx = asset.Indices;
        var tris = new List<AtlasRasterizer.Tri>();

        for (int gi = 0; gi < asset.Groups.Count; gi++)
        {
            var g = asset.Groups[gi];
            if (!string.Equals(g.LightmapTexture, texture, StringComparison.OrdinalIgnoreCase)) continue;
            if (groupLightmapEnabled is not null && gi < groupLightmapEnabled.Count && !groupLightmapEnabled[gi]) continue;

            int end = Math.Min(g.StartIndex + g.IndexCount, idx.Length);
            for (int k = g.StartIndex; k + 2 < end; k += 3)
            {
                int i0 = (int)idx[k], i1 = (int)idx[k + 1], i2 = (int)idx[k + 2];
                if ((i0 + 1) * 3 > pos.Length || (i1 + 1) * 3 > pos.Length || (i2 + 1) * 3 > pos.Length) continue;
                tris.Add(new AtlasRasterizer.Tri(
                    new Vector2(uv[i0 * 2], uv[i0 * 2 + 1]),
                    new Vector2(uv[i1 * 2], uv[i1 * 2 + 1]),
                    new Vector2(uv[i2 * 2], uv[i2 * 2 + 1]),
                    new Vector3(pos[i0 * 3], pos[i0 * 3 + 1], pos[i0 * 3 + 2]),
                    new Vector3(pos[i1 * 3], pos[i1 * 3 + 1], pos[i1 * 3 + 2]),
                    new Vector3(pos[i2 * 3], pos[i2 * 3 + 1], pos[i2 * 3 + 2]),
                    new Vector3(nrm[i0 * 3], nrm[i0 * 3 + 1], nrm[i0 * 3 + 2]),
                    new Vector3(nrm[i1 * 3], nrm[i1 * 3 + 1], nrm[i1 * 3 + 2]),
                    new Vector3(nrm[i2 * 3], nrm[i2 * 3 + 1], nrm[i2 * 3 + 2])));
            }
        }
        return tris;
    }

    /// <summary>Linear illumination -> the 8-bit atlas. Stored LINEAR, because that is what the shader
    /// expects: it applies pow(1/2.2) to the sampled value itself (bakedLightColour), so encoding here
    /// too would gamma the map twice.</summary>
    private static TextureImage Encode(BakedAtlasSurface surface, BakeSettings settings)
    {
        var rgba = new byte[surface.Width * surface.Height * 4];
        for (int i = 0; i < surface.Linear.Length; i++)
        {
            var c = surface.Linear[i];
            // Ordered (Bayer) dither before the 8-bit round. A light's smooth outer tail spans values far
            // below one quantisation step (at d/r=0.95 the term is ~0.0025, which is 0.3/255 and would
            // round flat to 0) — so plain rounding SNAPS the whole tail to zero and the pool ends on a
            // hard, texel-aligned border. That is the "sharp edge in the bake but not in Dynamic": the
            // dynamic path keeps the tail in float. Dithering trades that contour for sub-LSB noise, which
            // is how any renderer stores a smooth gradient in 8 bits.
            float d = settings.DitherStrength * (Bayer8(i % surface.Width, i / surface.Width) - 0.5f);
            byte r = ToByte(c.X, d), g = ToByte(c.Y, d), b = ToByte(c.Z, d);
            int o = i * 4;
            rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b;
            rgba[o + 3] = settings.AlphaFromLuminance
                ? (byte)((r * 77 + g * 150 + b * 29) >> 8)
                : (byte)255;
        }
        return new TextureImage(surface.Width, surface.Height, rgba);
    }

    /// <summary>8x8 Bayer threshold in [0,1) — a fixed pattern, so a re-bake stays deterministic.</summary>
    private static float Bayer8(int x, int y)
    {
        int v = 0, mask = 4, xc = x ^ y, yc = y;
        for (int bit = 0; bit < 6; mask >>= 1)
        {
            v |= ((yc & mask) != 0 ? 1 : 0) << bit++;
            v |= ((xc & mask) != 0 ? 1 : 0) << bit++;
        }
        return v / 64f;
    }

    /// <summary>Quantise to 8 bits with a sub-LSB dither offset (in units of one step).</summary>
    private static byte ToByte(float v, float dither) =>
        (byte)Math.Clamp(MathF.Round(v * 255f + dither), 0f, 255f);
}
