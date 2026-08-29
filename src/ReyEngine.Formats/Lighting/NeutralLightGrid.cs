using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Lighting;

/// <summary>
/// M443: build a lightgrid analytically, from measurements of Riot's 180 shipped grids.
///
/// <para><b>Why not a probe bake.</b> Running <see cref="Baking.LightBaker.BakeLightGrid"/> on the real
/// shipped base_srx was measured: 85.3% of cells come out BIT-IDENTICAL (97.8% when sized to the SR rect)
/// — the only spatial variation is a binary sun-shadow ray, so the probe bake on this map already IS a
/// constant fill. Worse, its cube shape is wrong (+Y/−Y = 1.8–1.9 against a shipped median of 4.5) because
/// <c>ProbeDirection</c> adds sky light isotropically to all six directions, and with auto-exposure off
/// every sample saturates to 255 — which the shader's <c>mad_sat</c> turns into flat, unshaded
/// characters. The bake buys ~2% spatial variation for a wrong shape and a fragile level.</para>
///
/// <para><b>The safety rule that shapes this class.</b> A MISSING grid is completely graceful — the
/// loader returns false and every consumer null-checks. A MALFORMED grid is a silent crash: on
/// <c>version != 3</c> or a zero dimension the loader still returns TRUE, appends an entry with a null
/// data pointer, and the first <c>ReadCell</c> dereferences it with no null test. There is no middle
/// ground, so <see cref="Build"/> refuses rather than emitting anything questionable.</para>
/// </summary>
public static class NeutralLightGrid
{
    /// <summary>Per-direction bytes, in the engine's sample order (+X, −X, +Y, −Y, +Z, −Z).
    ///
    /// <para>Derived from the corpus, not invented: median per-direction luminance across 178 readable
    /// grids gives +Y : horizontal : −Y = 1 : 0.30 : 0.20. The LEVEL is anchored on the three grids at
    /// Summoner's-Rift world scale, whose effective sphere-average multipliers converge at 0.300, 0.305
    /// and 0.330. mean6 = 80 gives 80/255 = 0.314, inside that band, and the peak (200/255 = 0.784) leaves
    /// 22% headroom so nothing saturates.</para></summary>
    public static readonly byte[] DirectionBytes = { 60, 60, 200, 40, 60, 60 };

    /// <summary>The corpus rule for grid extent, measured. NOT the mapgeo bounds: sizing from
    /// <c>BoundsMax</c> matches <b>0 of 178</b> shipped grids and would be ~2x too large for base_srx.
    /// Riot's grids are square (173/178) and a multiple of 100 (172/178), and only 1 of 177 is smaller
    /// than its own primary bucket rect — so round the primary culling rect up.</summary>
    public static float WorldSizeFor(MapGeoAsset map)
    {
        ArgumentNullException.ThrowIfNull(map);
        float raw = PrimaryBucketExtent(map);
        if (raw < 1f) throw new InvalidOperationException(
            "this mapgeo has no usable bucket grid, and mapgeo bounds are the one convention Riot never "
          + "uses for grid extent — refusing to guess a world size");
        return MathF.Ceiling(raw / 1000f) * 1000f;
    }

    /// <summary>The far corner of the largest bucket grid — the map's real culling rect.</summary>
    private static float PrimaryBucketExtent(MapGeoAsset map)
    {
        float best = 0f;
        foreach (var g in map.BucketGrids)
        {
            float extent = MathF.Max(g.MaxX, g.MaxZ);
            float area = (g.MaxX - g.MinX) * (g.MaxZ - g.MinZ);
            if (area > 0f && extent > best) best = extent;
        }
        return best;
    }

    /// <summary>M596: what a map should declare for character self-illumination when it does not already
    /// say. Censused over every shipped map wad, 200 MapBakeProperties: <b>129 author 0.5</b>, 25 author
    /// 0.65, 29 leave it out, and the rest scatter between 0.4 and 1.22.
    ///
    /// <para><b>0 of 200 author 0.25</b>, which is what this used to hardcode. On a Jade port that
    /// replaced the destination's own 1.0, so every champion, minion and turret on the map rendered at a
    /// QUARTER of the self-illumination Riot gave it - reported as "the tower looks darker" on a map
    /// whose terrain was otherwise fine. The value reaches characters through LIGHTGRID_SCALE.y in
    /// LIT_UBER_PS, so nothing about the map geometry shows it going wrong.</para></summary>
    public const float CorpusCharacterFullBrightIntensity = 0.5f;

    /// <summary>
    /// A complete, valid grid for a map that has never been baked.
    /// </summary>
    /// <param name="characterFullBrightIntensity">Must equal what is written to the bin's
    /// <c>lightGridCharacterFullBrightIntensity</c>; they match in 173/173 shipped pairs. Prefer the
    /// value the destination map already declares - see <see cref="CorpusCharacterFullBrightIntensity"/>
    /// for why a hardcoded constant is the wrong default.</param>
    public static LightGridFile Build(MapGeoAsset map, int width = 256, int height = 256,
        float characterFullBrightIntensity = CorpusCharacterFullBrightIntensity)
    {
        ArgumentNullException.ThrowIfNull(map);
        float size = WorldSizeFor(map);
        return Build(size, size, width, height, characterFullBrightIntensity);
    }

    /// <summary>The explicit-extent form, so tests and callers with their own measurements can bypass the
    /// bucket-grid rule.</summary>
    public static LightGridFile Build(float worldSizeX, float worldSizeZ, int width, int height,
        float characterFullBrightIntensity)
    {
        // The loader accepts version!=3, w<1, h<1 and worldSize<1 by RETURNING TRUE with a null data
        // pointer, which crashes on first sample. Refuse here instead.
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "grid dimensions must be >= 1");
        if (!(worldSizeX >= 1f) || !(worldSizeZ >= 1f))   // NaN fails this, and NaN PASSES the client's check
            throw new ArgumentOutOfRangeException(nameof(worldSizeX), "world size must be >= 1 and not NaN");

        var grid = LightGridFile.Create(width, height, worldSizeX, worldSizeZ);
        grid.Version = 3;
        grid.FullBrightScale = 0.25f;                    // LIGHTGRID_SCALE.x = 1.0
        grid.CharacterFullBrightIntensity = characterFullBrightIntensity;

        for (int cell = 0; cell < width * height; cell++)
            for (int d = 0; d < LightGridFile.Directions; d++)
            {
                float v = DirectionBytes[d] / 255f;
                grid.Samples[cell * LightGridFile.Directions + d] = new Vector3(v, v, v);   // neutral grey
            }
        return grid;
    }

    /// <summary>
    /// The exact <c>lightGridFileName</c> string. Rule measured byte-for-byte on <b>179 of 181</b> shipped
    /// bins (the 2 exceptions are variant maps deliberately pointing at a sibling's grid):
    /// <c>"ASSETS/Maps/Lightmaps/" + mapPath + "/LightGrid.dat"</c>.
    ///
    /// <para>Casing is cosmetic — <c>WadPath</c> lowercases before hashing, so all spellings resolve to the
    /// same chunk — but Riot's spelling is free. The basename is <c>LightGrid.dat</c> in 181 of 181; no
    /// theme-token variant exists anywhere in the install.</para>
    /// </summary>
    public static string FileNameFor(string mapPath)
    {
        string p = (mapPath ?? "").Replace('\\', '/').Trim('/');
        if (p.Length == 0) throw new ArgumentException("mapPath is required", nameof(mapPath));
        return $"ASSETS/Maps/Lightmaps/{p}/LightGrid.dat";
    }

    /// <summary>
    /// The map path as Riot AUTHORED it, read from <c>MapContainer.mapPath</c> (0xcc5e808a). This is the
    /// only way to get the exact casing — <c>Maps/MapGeometry/Map11/Base_SRX</c> cannot be derived from
    /// the all-lowercase asset path, because neither the internal capital in "MapGeometry" nor the
    /// all-caps "SRX" is recoverable.
    ///
    /// <para>Casing is cosmetic for RESOLUTION — <c>WadPath</c> lowercases before hashing, so every
    /// spelling maps to the same chunk key — but it is free to get right, and it is what every shipped bin
    /// contains.</para>
    /// </summary>
    public static string? MapPathFromBin(byte[] materialsBin)
    {
        ArgumentNullException.ThrowIfNull(materialsBin);
        try
        {
            var tree = new LeagueToolkit.Core.Meta.BinTree(new MemoryStream(materialsBin, false));
            uint container = Core.Hashing.HashAlgorithms.Fnv1a("MapContainer");
            uint field = Core.Hashing.HashAlgorithms.Fnv1a("mapPath");
            foreach (var (_, obj) in tree.Objects)
            {
                if (obj.ClassHash != container) continue;
                if (obj.Properties.TryGetValue(field, out var p)
                    && p is LeagueToolkit.Core.Meta.Properties.BinTreeString s
                    && s.Value.Length > 0)
                    return s.Value.Replace('\\', '/').Trim('/');
            }
        }
        catch { /* a bin we cannot parse just means no authored casing */ }
        return null;
    }

    /// <summary>Fallback when the bin carries no <c>mapPath</c>: derive from the mapgeo asset path,
    /// <c>data/maps/mapgeometry/map11/base_srx.mapgeo</c> → <c>maps/mapgeometry/map11/base_srx</c>.
    ///
    /// <para>The CASING will not match Riot's — it is not recoverable from a lowercase path — but the
    /// resulting name resolves to the identical chunk key, because WadPath lowercases before hashing.
    /// Prefer <see cref="MapPathFromBin"/> whenever the bin is to hand.</para>
    /// </summary>
    public static string MapPathFromMapGeo(string mapGeoPath)
    {
        string p = (mapGeoPath ?? "").Replace('\\', '/').TrimStart('/');
        if (p.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) p = p["data/".Length..];
        if (p.EndsWith(".mapgeo", StringComparison.OrdinalIgnoreCase)) p = p[..^".mapgeo".Length];
        return p.Trim('/');
    }
}
