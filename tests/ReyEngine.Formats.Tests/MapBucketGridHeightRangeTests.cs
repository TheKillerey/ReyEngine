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

    // ---- the preview box (M411) ----

    /// <summary>The preview's X/Z must be the BAKE's own snapped extent, not a separate calculation -
    /// a preview that disagrees with what gets baked is worse than no preview.</summary>
    [Fact]
    public void BakeBoundsXzMatchesTheGridTheBakeProduces()
    {
        var asset = AssetAt(0f);
        var grid = MapBucketGridBuilder.Rebuild(asset).Single();
        var box = MapBucketGridBuilder.ComputeBakeBounds(asset)!.Value;

        Assert.Equal(grid.MinX, box.Min.X);
        Assert.Equal(grid.MinZ, box.Min.Z);
        Assert.Equal(grid.MaxX, box.Max.X);
        Assert.Equal(grid.MaxZ, box.Max.Z);
    }

    /// <summary>Y is the FILTER range, authored - it is the one part of the box the user sets.</summary>
    [Fact]
    public void BakeBoundsYIsTheAuthoredHeightRange()
    {
        var box = MapBucketGridBuilder.ComputeBakeBounds(AssetAt(0f), heightMin: -300f, heightMax: 900f)!.Value;
        Assert.Equal(-300f, box.Min.Y);
        Assert.Equal(900f, box.Max.Y);
    }

    /// <summary>Nothing survives the filter -> no box. That is the useful answer, not an empty one at
    /// the origin, which would draw a degenerate rectangle in the middle of the map.</summary>
    [Fact]
    public void BakeBoundsIsNullWhenNothingSurvivesTheFilter()
        => Assert.Null(MapBucketGridBuilder.ComputeBakeBounds(AssetAt(10_000f), heightMax: 5000f));

    /// <summary>
    /// targetBucketSize changes the CELL COUNT; it changes the extent only when the span does not divide
    /// evenly, because maxX is snapped to minX + bucketSize * side.
    ///
    /// <para>Written the other way round first, asserting the extent always moves - it does not. A
    /// 1000-unit span snaps to 1000 at both 100 (side 10) and 5000 (side clamped to 4, cell 250), so the
    /// box is identical while the grid inside it is completely different. Worth pinning: a preview that
    /// only redraws when the BOX changes would look frozen while the user changes the thing that
    /// matters.</para>
    /// </summary>
    [Fact]
    public void TargetBucketSizeChangesTheCellCountEvenWhenTheExtentIsUnchanged()
    {
        var asset = AssetAt(0f);
        var small = MapBucketGridBuilder.Rebuild(asset, targetBucketSize: 100f).Single();
        var large = MapBucketGridBuilder.Rebuild(asset, targetBucketSize: 5000f).Single();

        Assert.NotEqual(small.BucketsPerSide, large.BucketsPerSide);
        Assert.Equal(4, large.BucketsPerSide);   // clamped to MinimumBucketsPerSide

        var boxS = MapBucketGridBuilder.ComputeBakeBounds(asset, targetBucketSize: 100f)!.Value;
        var boxL = MapBucketGridBuilder.ComputeBakeBounds(asset, targetBucketSize: 5000f)!.Value;
        Assert.Equal(boxS.Max.X, boxL.Max.X);    // evenly divisible here, so the extent does NOT move
    }

    /// <summary>And when the span does NOT divide evenly, the extent really is snapped outward.</summary>
    [Fact]
    public void AnUnevenSpanIsSnappedOutward()
    {
        var asset = AssetAt(0f);
        var box = MapBucketGridBuilder.ComputeBakeBounds(asset, targetBucketSize: 300f)!.Value;
        var grid = MapBucketGridBuilder.Rebuild(asset, targetBucketSize: 300f).Single();
        Assert.Equal(grid.MinX + grid.BucketSizeX * grid.BucketsPerSide, box.Max.X, 3);
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
