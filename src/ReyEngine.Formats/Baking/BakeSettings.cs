using System.Numerics;

namespace ReyEngine.Formats.Baking;

/// <summary>M158: how a lightmap bake is configured. Every default here is a MEASURED property of
/// Riot's shipping Map12 lightmaps, not a guess — see the constants' remarks.</summary>
public sealed class BakeSettings
{
    // ---- atlas ----

    /// <summary>Atlas edge length in texels. Riot ships 2048 for 126 of Map12's 128 atlases.</summary>
    public int AtlasResolution { get; set; } = 2048;

    /// <summary>Texels per world unit. Drives how large a mesh's chart is in the atlas: a chart
    /// covering W world units gets W * TexelDensity texels. Lower = coarser + fewer atlases.</summary>
    public float TexelDensity { get; set; } = 0.08f;

    /// <summary>Gutter between chart bounding boxes, in texels. Riot's shipped atlases keep >= 5.</summary>
    public int Padding { get; set; } = 5;

    /// <summary>Edge-replication ring painted around each chart so bilinear filtering can't sample the
    /// gutter. Riot uses exactly 2 texels (bit-identical neighbours at chart boundaries).</summary>
    public int Dilation { get; set; } = 2;

    /// <summary>Store atlases as BC3 (what Riot ships: format byte 12, 16-byte blocks). BC1 halves the
    /// file at the cost of the alpha channel.</summary>
    public bool CompressBc3 { get; set; } = true;

    /// <summary>Write each texel's alpha as its own luminance instead of a flat 255. Riot's shipped
    /// atlases DO carry varying alpha that correlates with brightness (r = 0.88, measured on Map12), but
    /// what consumes it is unverified — and 255 is the identity under any multiply, so it is the
    /// default. Our own viewport samples .rgb only, so this changes nothing in preview either way.</summary>
    public bool AlphaFromLuminance { get; set; }

    /// <summary>Write the full mip chain. Riot's atlases always carry one (12 levels at 2048).</summary>
    public bool GenerateMips { get; set; } = true;

    // ---- quality ----

    /// <summary>Shadow-ray samples per texel for the sun. 1 = hard shadows; higher softens the
    /// penumbra by jittering across the sun's angular size.</summary>
    public int SunSamples { get; set; } = 16;

    /// <summary>Samples per texel per point light, for soft shadows from a light's radius.</summary>
    public int PointLightSamples { get; set; } = 4;

    /// <summary>Ambient-occlusion rays per texel. 0 disables AO.</summary>
    public int AmbientOcclusionSamples { get; set; } = 32;

    /// <summary>How far a sky/AO ray travels, in world units. 0 = 1% of the scene diagonal (~570 on
    /// Summoner's Rift), which is the measured sweet spot and scales to other map sizes.
    ///
    /// The hemisphere integrator this feeds multiplies the SKY term only, so it IS sky visibility. Two
    /// things were measured on Map11 and both are worth knowing:
    ///  - Turning it ON is one of the two big contrast wins: texel-luminance stddev 35.7 -> 56.1.
    ///  - Making it LONGER is not. A quarter-diagonal radius scored 55.3 — no better — while dropping the
    ///    median from 191 to 128. Past room scale everything is occluded by something and the term
    ///    flattens out again, just darker.
    /// It is clipping-neutral either way: it only ever removes light, so auto-exposure's guarantee holds.</summary>
    public float AmbientOcclusionRadius { get; set; }

    /// <summary>Nudge along the normal before tracing, so a texel can't shadow its own surface.</summary>
    public float RayBias { get; set; } = 0.5f;

    /// <summary>M165: derive Exposure from the map itself instead of using the manual value — see
    /// BakeLighting.ComputeAutoExposure. It only ever LOWERS exposure to stop the atlas clipping, so a
    /// map with enough headroom (Map12, scale 2.0) is unaffected while one without (Map11, scale 0.6)
    /// stops baking to solid white. On by default because the manual default was wrong for such maps.</summary>
    public bool AutoExposure { get; set; } = true;

    /// <summary>Uniform brightness multiplier on the baked result. The Dynamic viewport preview is a
    /// shadowless, AO-less flat wash and so reads brighter than a real bake; raise this to bring the
    /// baked look back up to it. 1 = physically-matched (baked is darker by exactly its occlusion).</summary>
    public float Exposure { get; set; } = 1f;

    /// <summary>Shape of the point-light falloff: 0 = the classic (1-t)^2 (tight pool, visible rim),
    /// 1 = (1-t^2)^2 (wider pool that fades out gently). Applied identically by the bake and the
    /// viewport shader, so Baked and Dynamic always agree.</summary>
    public float FalloffSoftness { get; set; } = 0.6f;

    /// <summary>Smooth (area-average) vertex normals across coincident positions before lighting, for
    /// normals that meet within <see cref="SmoothingAngleDegrees"/>. Map geometry is largely flat-shaded
    /// — on this map 59.5% of shared positions carry split normals — so the N.L term steps at every
    /// triangle and a light pool bakes as visible polygonal facets. Averaging removes the faceting while
    /// the angle threshold keeps genuine hard corners (wall meets floor) hard.</summary>
    public bool SmoothNormals { get; set; } = true;

    /// <summary>Normals meeting at a shared position are averaged only if they are within this angle.
    /// Measured on real map geometry, the share of shared edges carrying a VISIBLE brightness step
    /// (the facet boundaries) falls off sharply with the threshold:
    ///   off 36.6%  |  60 deg 25.9%  |  90 deg 8.8%  |  120 deg 1.5%  |  150 deg 0.6%
    /// 120 is the default: it removes 96% of the faceting while still refusing to merge near-opposite
    /// normals (the two sides of a thin wall), which must stay independent.</summary>
    public float SmoothingAngleDegrees { get; set; } = 120f;

    /// <summary>Dither amplitude, in units of one 8-bit step, applied before quantising the atlas. A
    /// light's outer tail falls below one step long before it reaches the radius, so plain rounding snaps
    /// it flat and leaves a hard edge; dithering converts that contour into sub-step noise. 0 disables.</summary>
    public float DitherStrength { get; set; } = 1f;

    // ---- lightgrid ----

    /// <summary>Write the lightgrid alongside the atlases (probe lighting for meshes that can't take a
    /// baked lightmap). Riot's modern grids are 256x256 cells of 24 bytes.</summary>
    public bool BakeLightGrid { get; set; } = true;

    public int LightGridWidth { get; set; } = 256;
    public int LightGridHeight { get; set; } = 256;

    /// <summary>World-space volume the grid covers. Empty = derive it from the map bounds.</summary>
    public Vector3 LightGridMin { get; set; }
    public Vector3 LightGridMax { get; set; }
    public bool LightGridFromMapBounds { get; set; } = true;

    // ---- output ----

    /// <summary>Root the atlases are written under. Riot mirrors the mapgeo path beneath
    /// assets/maps/lightmaps/ — e.g. data/maps/mapgeometry/map12/crepe.mapgeo becomes
    /// assets/maps/lightmaps/maps/mapgeometry/map12/crepe/0.tex.</summary>
    public string OutputRoot { get; set; } = "assets/maps/lightmaps";

    /// <summary>Optional theme token inserted before the extension on every generated file, matching
    /// Riot's themed variants (0.kiwi16_9.tex, lightgrid.kiwi16_9.dat). Empty = untokenised.</summary>
    public string ThemeToken { get; set; } = "";

    /// <summary>Riot's own layout for a mapgeo path — the default output location.</summary>
    public string ResolveOutputFolder(string mapgeoPath)
    {
        string p = mapgeoPath.Replace('\\', '/').TrimStart('/');
        const string dataPrefix = "data/";
        if (p.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase)) p = p[dataPrefix.Length..];
        if (p.EndsWith(".mapgeo", StringComparison.OrdinalIgnoreCase)) p = p[..^".mapgeo".Length];
        return $"{OutputRoot.TrimEnd('/')}/{p}/";
    }

    /// <summary>Atlas file name — Riot uses a bare index with no prefix or padding.</summary>
    public string AtlasFileName(int index) =>
        ThemeToken.Length == 0 ? $"{index}.tex" : $"{index}.{ThemeToken}.tex";

    public string LightGridFileName() =>
        ThemeToken.Length == 0 ? "lightgrid.dat" : $"lightgrid.{ThemeToken}.dat";

    /// <summary>Bytes one atlas will occupy on disk, so the UI can show the real cost up front —
    /// lightmaps are the MAJORITY of a shipped map (Map12: 674 MiB of a 1067 MiB wad).</summary>
    public long EstimateAtlasBytes()
    {
        int r = Math.Max(1, AtlasResolution);
        long total = 0;
        int w = r, h = r;
        while (true)
        {
            // Every level stores whole 4x4 blocks, right down to 1x1 — that minimum is what makes a real
            // 2048 BC3 atlas come out at exactly 5,592,444 bytes.
            long blocks = (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4);
            total += blocks * (CompressBc3 ? 16 : 8);
            if (!GenerateMips || (w == 1 && h == 1)) break;
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        return total + 12;   // 12-byte .tex header
    }

    public BakeSettings Clone() => (BakeSettings)MemberwiseClone();
}
