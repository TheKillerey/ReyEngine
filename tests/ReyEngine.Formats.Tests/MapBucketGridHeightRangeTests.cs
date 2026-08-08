using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ReyEngine.Formats.MapGeo;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M410: the bake height range. The name invites a wrong model, so these tests state what it IS.
///
/// <para>The bucket grid has NO height — MapBucketGridData carries MinX/MinZ/MaxX/MaxZ and
/// BucketsPerSide and nothing about Y, and the shipped header has no height field. The range is a
/// triangle REJECT FILTER applied before the grid is built. These tests assert exactly that: changing
/// it changes WHICH GEOMETRY is baked, never the grid's shape.</para>
/// </summary>
public class MapBucketGridHeightRangeTests
{
    /// <summary>One triangle sitting flat at world height <paramref name="y"/>, spanning 1000 units in XZ
    /// so it lands in a sane number of buckets.</summary>
    private static MapGeoAsset AssetAt(params float[] heights)
    {
        var pos = new List<float>();
        var idx = new List<uint>();
        var groups = new List<MapGeoGroup>();
        var meshes = new List<MapGeoMesh>();

        for (int t = 0; t < heights.Length; t++)
        {
            float y = heights[t];
            uint b = (uint)(t * 3);
            pos.AddRange(new[] { 0f, y, 0f, 1000f, y, 0f, 0f, y, 1000f });
            idx.AddRange(new[] { b, b + 1, b + 2 });
            groups.Add(new MapGeoGroup($"mat{t}", t * 3, 3, MeshIndex: t));
            meshes.Add(new MapGeoMesh
            {
                Index = t, Name = $"mesh{t}", VertexStart = t * 3, VertexCount = 3,
                Transform = Matrix4x4.Identity, Pivot = Vector3.Zero,
            });
        }

        return new MapGeoAsset
        {
            Positions = pos.ToArray(),
            Normals = new float[pos.Count],
            Uvs = new float[pos.Count / 3 * 2],
            Indices = idx.ToArray(),
            // Not derived from Positions.Length - PositionAt bounds-checks against this.
            VertexCount = pos.Count / 3,
            Groups = groups,
            Meshes = meshes,
        };
    }

    private static int TotalFaces(IReadOnlyList<MapBucketGridData> grids) =>
        grids.Sum(g => g.Vertices?.Count ?? 0);

    // ---- the filter ----

    /// <summary>A triangle above the range is DROPPED. This is the whole feature: raising the max is how
    /// you get tall geometry into the grid.</summary>
    [Fact]
    public void GeometryAboveTheRangeIsExcluded()
    {
        var asset = AssetAt(10_000f);
        Assert.Empty(MapBucketGridBuilder.Rebuild(asset, heightMin: -120f, heightMax: 5000f));
    }

    [Fact]
    public void RaisingTheMaxIncludesGeometryThatWasExcluded()
    {
        var asset = AssetAt(10_000f);
        var grids = MapBucketGridBuilder.Rebuild(asset, heightMin: -120f, heightMax: 20_000f);
        Assert.NotEmpty(grids);
        Assert.True(TotalFaces(grids) > 0);
    }

    [Fact]
    public void GeometryBelowTheRangeIsExcluded()
    {
        var asset = AssetAt(-5_000f);
        Assert.Empty(MapBucketGridBuilder.Rebuild(asset, heightMin: -120f, heightMax: 5000f));
        Assert.NotEmpty(MapBucketGridBuilder.Rebuild(asset, heightMin: -10_000f, heightMax: 5000f));
    }

    /// <summary>The defaults are Riot's shipped values and must not drift.</summary>
    [Fact]
    public void DefaultsAreTheShippedRange()
    {
        Assert.Equal(-120f, MapBucketGridBuilder.HeightRangeMin);
        Assert.Equal(5000f, MapBucketGridBuilder.HeightRangeMax);
        var asset = AssetAt(0f);
        Assert.Equal(
            MapBucketGridBuilder.Rebuild(asset).Count,
            MapBucketGridBuilder.Rebuild(asset, heightMin: -120f, heightMax: 5000f).Count);
    }

    // ---- what it does NOT do ----

    /// <summary>The grid's SHAPE is derived from X/Z only. Widening the height range so that the SAME
    /// triangles survive must not move a single bucket bound - proof this is a filter, not a volume.</summary>
    [Fact]
    public void HeightRangeDoesNotChangeTheGridsExtentWhenTheSameGeometrySurvives()
    {
        var asset = AssetAt(0f);
        var tight = MapBucketGridBuilder.Rebuild(asset, heightMin: -120f, heightMax: 5000f).Single();
        var wide = MapBucketGridBuilder.Rebuild(asset, heightMin: -50_000f, heightMax: 50_000f).Single();

        Assert.Equal(tight.MinX, wide.MinX);
        Assert.Equal(tight.MinZ, wide.MinZ);
        Assert.Equal(tight.MaxX, wide.MaxX);
        Assert.Equal(tight.MaxZ, wide.MaxZ);
        Assert.Equal(tight.BucketsPerSide, wide.BucketsPerSide);
    }

    // ---- validation ----

    /// <summary>An inverted range bakes NOTHING, and an empty grid is indistinguishable on screen from a
    /// failed build - so it throws rather than silently producing one.</summary>
    [Fact]
    public void AnInvertedRangeThrowsRatherThanBakingNothing()
    {
        var asset = AssetAt(0f);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MapBucketGridBuilder.Rebuild(asset, heightMin: 5000f, heightMax: -120f));
        Assert.Contains("inverted", ex.Message);
    }

    [Theory]
    [InlineData(float.NaN, 5000f)]
    [InlineData(-120f, float.PositiveInfinity)]
    public void NonFiniteBoundsAreRejected(float min, float max)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => MapBucketGridBuilder.Rebuild(AssetAt(0f), heightMin: min, heightMax: max));
}
