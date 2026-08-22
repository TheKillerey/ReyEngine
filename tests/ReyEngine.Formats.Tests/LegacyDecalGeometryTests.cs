using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M547: three defects that all reached the reporter as "the decals go black".
///
/// <para>The port merged every decal sharing a texture into one map-spanning mesh, copied the legacy
/// source's non-finite UVs straight through, and clamped the sampler on materials that tile.</para>
/// </summary>
public sealed class LegacyDecalGeometryTests
{
    private const string Legacy = @"K:\LeagueSandbox\League_Sandbox_Client\LEVELS\Map2";
    private const string Wad =
        @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client";
    private const string GeoPath = "data/maps/mapgeometry/map453/jade_container.mapgeo";

    /// <summary>Porting Map2 takes tens of seconds; every test here asks about the same result, so it is
    /// done once for the class rather than once per fact.</summary>
    private static readonly Lazy<LegacyMapPortResult?> Port = new(() =>
    {
        if (!Directory.Exists(Legacy) || !File.Exists(Wad)) return null;
        using var wad = ReyEngine.Core.Wad.WadArchive.Open(Wad);
        ulong hash = ReyEngine.Core.Hashing.HashAlgorithms.WadPath(GeoPath);
        if (!wad.TryGetEntry(hash, out _)) return null;
        return LegacyMapPorter.Port(Legacy, wad.Extract(hash), GeoPath);
    });

    private static readonly Lazy<MapGeoAsset?> Asset =
        new(() => Port.Value is { } r ? MapGeoDecoder.Decode(r.MapGeoBytes) : null);

    private static LegacyMapPortResult? Ported() => Port.Value;

    private static IEnumerable<MapGeoGroup> Decals(MapGeoAsset a) =>
        a.Groups.Where(g => g.Material.Contains("LegacyPort/", StringComparison.OrdinalIgnoreCase)
                         && g.Material.Contains("/Decal_", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void NoPortedVertexCarriesAUvTheShaderCannotSample()
    {
        // room.nvr holds 38 non-finite UVs across 5 of its 4,373 meshes and they all landed on ONE
        // material - order_base_circle, which the reporter named. Interpolating across a NaN corner is NaN
        // over the whole triangle, so both the texel and the alpha it returns are undefined.
        if (Ported() is not { } result) return;
        var a = Asset.Value!;

        int bad = 0;
        foreach (var g in a.Groups.Where(g => g.Material.Contains("LegacyPort/", StringComparison.OrdinalIgnoreCase)))
            for (int i = g.StartIndex; i < g.StartIndex + g.IndexCount; i++)
            {
                uint v = a.Indices[i];
                if (!float.IsFinite(a.Uvs[v * 2]) || !float.IsFinite(a.Uvs[v * 2 + 1])) bad++;
            }
        Assert.Equal(0, bad);
    }

    [Fact]
    public void TheDropIsReportedRatherThanSilent()
    {
        // Geometry leaving the port has to say so. 24 triangles of ~800,000 is negligible, but a silent
        // drop is how a hole in a map becomes unexplainable later.
        if (Ported() is not { } result) return;
        Assert.Contains(result.Warnings, w => w.Contains("not finite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DecalsAreSplitOutOfTheirMapSpanningMerge()
    {
        // Before: 20 decal meshes, EVERY one spanning 55-96% of the map, the largest holding 452
        // disconnected islands across 96.3%. A blended mesh has one sort position, and no single position
        // sorts correctly against the ground across a whole map.
        if (Ported() is not { } result) return;
        var a = Asset.Value!;
        var decals = Decals(a).ToList();

        Assert.True(decals.Count > 900,
            $"expected the decals split into their own meshes, saw {decals.Count}");
        Assert.Contains(result.Warnings, w => w.Contains("Split", StringComparison.Ordinal)
                                           && w.Contains("decal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoDecalMeshCarriesOnlyPartOfItsTexture()
    {
        // The reporter's second finding, and the one that matters most: "I have a splitted mesh that halfs
        // or have a splitted texture not the full texture ... they are messed up and useless."
        //
        // Two finer units were tried and both broke decals apart. A 1,000-unit locality cell assigns cells
        // per TRIANGLE by centroid, so a quad on a boundary lost half its texture to another mesh. The
        // connected component came apart wherever the artist left seam vertices unwelded, leaving 6 meshes
        // under half a tile. Grouping by SOURCE MESH leaves none: the p10 mesh carries 1.35 tiles.
        if (Ported() is null) return;
        var a = Asset.Value!;

        var partial = new List<string>();
        foreach (var g in Decals(a))
        {
            var lo = new Vector2(float.MaxValue);
            var hi = new Vector2(float.MinValue);
            for (int i = g.StartIndex; i < g.StartIndex + g.IndexCount; i++)
            {
                uint v = a.Indices[i];
                var uv = new Vector2(a.Uvs[v * 2], a.Uvs[v * 2 + 1]);
                lo = Vector2.Min(lo, uv); hi = Vector2.Max(hi, uv);
            }
            if (Math.Max(hi.X - lo.X, hi.Y - lo.Y) < 0.5f)
                partial.Add($"{g.Material.Split('/').Last()} covers {Math.Max(hi.X - lo.X, hi.Y - lo.Y):n2} of a tile");
        }
        Assert.True(partial.Count == 0,
            "decal meshes carrying less than half a texture: " + string.Join(", ", partial.Take(5)));
    }

    [Fact]
    public void ADecalMeshIsOneObjectTheUserCanSelectAndMove()
    {
        // One ported mesh per SOURCE mesh. The legacy artist placed each decal as an object, so this is
        // what makes a decal selectable and movable as the plane it is - rather than one map-spanning mesh
        // holding hundreds of them, which is what the port produced before.
        if (Ported() is null) return;
        var a = Asset.Value!;
        var decals = Decals(a).ToList();

        Assert.InRange(decals.Count, 900, 1_200);       // 1,036 measured; 20 before the split
        Assert.True(decals.All(g => g.IndexCount >= 3), "a decal mesh with no triangle is a bug");
        // p50 is 4 triangles - two quads. A mesh holding hundreds means the merge came back.
        Assert.True(decals.Max(g => g.IndexCount / 3) < 200,
            $"largest decal mesh holds {decals.Max(g => g.IndexCount / 3)} triangles");
    }

    [Fact]
    public void TheAddressModeIsDecidedByTheUvsAndNotByTheRole()
    {
        // M490 gave the whole decal role Clamp and its own note says that "would be the wrong one for a
        // decal authored to tile". Measured, that is 19 of the 20 ported Map2 decal materials - including
        // both textures the reporter named. The one that genuinely stays in the unit square must still
        // clamp, or this trades one bug for the bug M490 fixed.
        if (Ported() is not { } result) return;
        var decals = result.Materials.Where(m => m.Role == LegacyMaterialRole.Decal).ToList();
        if (decals.Count == 0) return;

        string Tex(LegacyMaterialPlan m) => m.Samplers.Values.First().Split('/').Last();
        var tiling = decals.Where(m => m.SamplerAddressMode is null).Select(Tex).ToList();
        var clamped = decals.Where(m => m.SamplerAddressMode == LegacyMapPorter.ClampAddressMode)
                            .Select(Tex).ToList();

        Assert.Contains(tiling, t => t.StartsWith("order_base_circle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tiling, t => t.StartsWith("order_seam_", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(clamped, t => t.StartsWith("order_ground_moss_patch1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SplittingMovesMeshBoundariesAndNothingElse()
    {
        // The split must be a repartition, not a rewrite: same triangles, same places. Only the 24
        // non-finite ones are gone, and they are gone deliberately.
        if (Ported() is not { } result) return;
        var a = Asset.Value!;

        int decalTriangles = Decals(a).Sum(g => g.IndexCount) / 3;
        Assert.InRange(decalTriangles, 5_000, 5_400);   // 5,307 before the split, 5,283 after the UV drop
    }
}
