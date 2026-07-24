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

    /// <summary>How far an AO ray travels, in world units.</summary>
    public float AmbientOcclusionRadius { get; set; } = 400f;

    /// <summary>Nudge along the normal before tracing, so a texel can't shadow its own surface.</summary>
    public float RayBias { get; set; } = 0.5f;

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
