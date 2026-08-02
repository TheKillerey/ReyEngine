using System.Numerics;
using ReyEngine.Formats.Baking;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

public sealed class LightBakeCoverageTests
{
    [Fact]
    public void Coverage_DistinguishesReferencedFromMaterialEligibleAtlases()
    {
        var map = MapWithGroups(
            new MapGeoGroup("dynamic", 0, 3, LightmapTexture: "lightmaps/0.tex"),
            new MapGeoGroup("static", 3, 3, LightmapTexture: "lightmaps/1.tex"));

        var coverage = LightBaker.AnalyzeCoverage(map, [false, true]);

        Assert.Equal(2, coverage.ReferencedAtlases);
        Assert.Equal(1, coverage.BakeableAtlases);
        Assert.Equal(new[] { "lightmaps/0.tex" }, coverage.SkippedAtlases);
    }

    [Fact]
    public async Task ZeroRaySamples_StillBakeSunAndSkyAtlas()
    {
        var map = MapWithGroups(new MapGeoGroup("dynamic", 0, 3, LightmapTexture: "lightmaps/0.tex"));
        var settings = new BakeSettings
        {
            AtlasResolution = 8,
            SunSamples = 0,
            PointLightSamples = 0,
            AmbientOcclusionSamples = 0,
            Dilation = 0,
            GenerateMips = false,
            SmoothNormals = false,
            AutoExposure = false,
            BakeLightGrid = false,
        };
        BakedAtlas? written = null;

        var result = await LightBaker.BakeExistingLayoutAsync(
            map, [true], null, new BakeLighting(), settings, "data/maps/mapgeometry/map11/test.mapgeo",
            atlas => { written = atlas; return Task.CompletedTask; });

        Assert.Equal(1, result.ReferencedAtlases);
        Assert.Equal(1, result.BakedAtlases);
        Assert.Equal(0, result.SkippedAtlases);
        Assert.NotNull(written);
        Assert.True(written!.CoveredTexels > 0);
        Assert.NotEmpty(written.TexBytes);
    }

    private static MapGeoAsset MapWithGroups(params MapGeoGroup[] groups) => new()
    {
        Positions =
        [
            0, 0, 0,  1, 0, 0,  0, 0, 1,
            0, 0, 0,  1, 0, 0,  0, 0, 1,
        ],
        Normals =
        [
            0, 1, 0,  0, 1, 0,  0, 1, 0,
            0, 1, 0,  0, 1, 0,  0, 1, 0,
        ],
        Uvs = new float[12],
        LightmapUvs =
        [
            0.1f, 0.1f,  0.9f, 0.1f,  0.1f, 0.9f,
            0.1f, 0.1f,  0.9f, 0.1f,  0.1f, 0.9f,
        ],
        HasLightmap = true,
        Indices = [0, 1, 2, 3, 4, 5],
        Groups = groups,
    };
}
