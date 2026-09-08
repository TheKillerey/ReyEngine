using System;
using System.Numerics;
using ReyEngine.Rendering;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M659: the wire ball that shows how far a dynamic point light reaches.
///
/// <para>Built by one static builder BOTH viewports call, for the same reason
/// <see cref="ViewportMeshRenderer.BuildGizmoAxis"/> is shared (M296): two viewports disagreeing about
/// where a light stops would be worse than not drawing it.</para>
/// </summary>
public class LightRangeRingTests
{
    private static readonly Vector3 Centre = new(120f, -40f, 900f);
    private const float Radius = 500f;

    private static Vector3 Vert(float[] v, int i) => new(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);
    private static int VertCount(float[] v) => v.Length / 3;

    [Fact]
    public void ThreeClosedCirclesOfTheGivenRadius()
    {
        const int segments = 48;
        var v = ViewportMeshRenderer.BuildLightRangeRings(Centre, Radius, segments);

        // three circles, one line segment per step, two points per segment
        Assert.Equal(3 * segments * 2, VertCount(v));
        Assert.All(Enumerable(v), p => Assert.Equal(Radius, (p - Centre).Length(), 2));
    }

    /// <summary>One circle per plane, so the ball reads as a ball from any angle rather than as a disc
    /// that vanishes edge-on.</summary>
    [Fact]
    public void OneCircleLiesInEachOfTheThreePlanes()
    {
        var v = ViewportMeshRenderer.BuildLightRangeRings(Centre, Radius, 16);
        int flatX = 0, flatY = 0, flatZ = 0;
        foreach (var p in Enumerable(v))
        {
            var d = p - Centre;
            if (MathF.Abs(d.X) < 1e-3f) flatX++;
            if (MathF.Abs(d.Y) < 1e-3f) flatY++;
            if (MathF.Abs(d.Z) < 1e-3f) flatZ++;
        }
        Assert.True(flatX > 0, "no circle in the YZ plane");
        Assert.True(flatY > 0, "no circle in the XZ plane");
        Assert.True(flatZ > 0, "no circle in the XY plane");
    }

    /// <summary>The ring closes: the last point of a circle lands back on its first. An open ring shows
    /// as a gap that looks like missing geometry.</summary>
    [Fact]
    public void EachCircleClosesOnItself()
    {
        const int segments = 24;
        var v = ViewportMeshRenderer.BuildLightRangeRings(Centre, Radius, segments);
        int perCircle = segments * 2;
        for (int c = 0; c < 3; c++)
        {
            var first = Vert(v, c * perCircle);
            var last = Vert(v, c * perCircle + perCircle - 1);
            Assert.True((first - last).Length() < 1e-2f, $"circle {c} does not close");
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-10f)]
    public void ANonPositiveRadiusDrawsNothingRatherThanADegenerateRing(float radius)
    {
        Assert.Empty(ViewportMeshRenderer.BuildLightRangeRings(Centre, radius));
    }

    [Fact]
    public void TooFewSegmentsDrawsNothing()
    {
        Assert.Empty(ViewportMeshRenderer.BuildLightRangeRings(Centre, Radius, 2));
    }

    /// <summary>The indicator is the light's own radius, so it must track it exactly - this is the number
    /// the map is lit with once the global multiplier is folded in by the caller.</summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(250f)]
    [InlineData(12000f)]
    public void TheRingIsExactlyTheRadiusAsked(float radius)
    {
        var v = ViewportMeshRenderer.BuildLightRangeRings(Vector3.Zero, radius, 12);
        Assert.All(Enumerable(v), p => Assert.Equal(radius, p.Length(), 2));
    }

    private static System.Collections.Generic.IEnumerable<Vector3> Enumerable(float[] v)
    {
        for (int i = 0; i < VertCount(v); i++) yield return Vert(v, i);
    }
}
