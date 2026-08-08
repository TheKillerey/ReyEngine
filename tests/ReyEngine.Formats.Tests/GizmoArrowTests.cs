using System;
using System.Numerics;
using ReyEngine.Rendering;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M380: the translate gizmo's cone arrowhead. BuildGizmoAxis is the single static builder BOTH viewports
/// call - GL binds its output directly, MainWindow hands the same arrays to _dx11.Renderer.SetGizmoLines -
/// so pinning the geometry here pins it for both. It emits a flat line list: 6 floats per segment
/// (x,y,z, x,y,z).
/// </summary>
public class GizmoArrowTests
{
    private const int Move = 0, Rotate = 1, Scale = 2;
    private const float Arm = 100f;

    private static readonly Vector3 Pivot = Vector3.Zero;
    private static readonly Vector3 Axis = Vector3.UnitX;

    /// <summary>Segment i as (start, end).</summary>
    private static (Vector3 A, Vector3 B) Seg(float[] v, int i) =>
        (new Vector3(v[i * 6], v[i * 6 + 1], v[i * 6 + 2]),
         new Vector3(v[i * 6 + 3], v[i * 6 + 4], v[i * 6 + 5]));

    private static int SegCount(float[] v) => v.Length / 6;

    [Fact]
    public void MoveEmitsAWholeNumberOfSegments()
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Move, Pivot, Axis, Arm);
        Assert.Equal(0, v.Length % 6);
    }

    /// <summary>The regression this exists for: move used to emit ONE segment - a bare line with nothing
    /// on the end. Anything that collapses the head back to that fails here.</summary>
    [Fact]
    public void MoveIsNoLongerASingleBareLine()
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Move, Pivot, Axis, Arm);
        Assert.True(SegCount(v) > 1, $"move gizmo emitted {SegCount(v)} segment(s) - the arrowhead is gone");
    }

    /// <summary>Shaft + 12 rim + 12 slant. Pinned exactly: a silently doubled or halved cone would still
    /// "look like an arrow" in a screenshot and drift the two viewports' vertex budgets apart.</summary>
    [Fact]
    public void MoveEmitsShaftPlusTwelveRimAndTwelveSlantSegments()
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Move, Pivot, Axis, Arm);
        Assert.Equal(1 + 12 + 12, SegCount(v));
    }

    /// <summary>The shaft stops at the cone base, NOT at the tip - the head is wireframe, so a shaft run
    /// to the tip is visible through it.</summary>
    [Fact]
    public void ShaftStopsAtTheConeBase()
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Move, Pivot, Axis, Arm);
        var (a, b) = Seg(v, 0);

        Assert.Equal(Pivot.X, a.X, 3);
        // headLen = arm * 0.16 => base at 84 along the axis, tip at 100.
        Assert.Equal(84f, b.X, 3);
        Assert.True(b.X < Arm, "shaft must not reach the tip");
    }

    /// <summary>Every distinct rim point gets exactly one slant to the tip - no point left unconnected and
    /// none connected twice. This is what the "step N lands back on step 0" wrap in the builder buys.</summary>
    [Fact]
    public void EveryRimPointHasExactlyOneSlantToTheTip()
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Move, Pivot, Axis, Arm);
        var tip = Pivot + Axis * Arm;

        int slants = 0;
        for (int i = 0; i < SegCount(v); i++)
            if (Vector3.Distance(Seg(v, i).B, tip) < 1e-3f) slants++;

        Assert.Equal(12, slants);
    }

    /// <summary>The rim is a circle of radius arm*0.05 about the axis, in the plane at the cone base.
    /// Checks the cone is actually conical rather than a flat fan or a collapsed point.</summary>
    [Fact]
    public void RimPointsLieOnACircleAboutTheAxisAtTheConeBase()
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Move, Pivot, Axis, Arm);
        var tip = Pivot + Axis * Arm;
        int checkedPoints = 0;

        for (int i = 1; i < SegCount(v); i++)
        {
            var (a, _) = Seg(v, i);
            if (Vector3.Distance(a, tip) < 1e-3f) continue;   // that is the tip end of a slant

            // distance from the axis line, and position along it
            float along = Vector3.Dot(a - Pivot, Axis);
            float radial = (a - Pivot - Axis * along).Length();
            Assert.Equal(84f, along, 2);        // in the base plane
            Assert.Equal(5f, radial, 2);        // arm * 0.05
            checkedPoints++;
        }

        Assert.True(checkedPoints >= 12, $"expected at least 12 rim points, saw {checkedPoints}");
    }

    /// <summary>Scale keeps its box and its full-length shaft — M380 restructured that branch, so this
    /// pins that it came through unchanged.</summary>
    [Fact]
    public void ScaleStillEmitsAFullShaftPlusTwelveBoxEdges()
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Scale, Pivot, Axis, Arm);
        Assert.Equal(1 + 12, SegCount(v));

        var (a, b) = Seg(v, 0);
        Assert.Equal(Pivot.X, a.X, 3);
        Assert.Equal(Arm, b.X, 3);   // scale's shaft DOES run to the tip
    }

    [Fact]
    public void RotateRingIsUnchanged()
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Rotate, Pivot, Axis, Arm);
        Assert.Equal(48, SegCount(v));
    }

    /// <summary>The head scales with the arm, so it stays the same size on screen at any camera distance
    /// (the arm is itself distance-scaled by GizmoArmLength).</summary>
    [Theory]
    [InlineData(10f)]
    [InlineData(500f)]
    [InlineData(5000f)]
    public void HeadScalesWithTheArm(float arm)
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Move, Pivot, Axis, arm);
        var (_, b) = Seg(v, 0);
        Assert.Equal(arm * 0.84f, b.X, 2);
    }

    /// <summary>Y and Z arms must get the same head as X — the builder picks its perpendicular basis from
    /// the axis, and the near-parallel guard there is exactly the kind of thing that silently degenerates.</summary>
    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0f, -1f, 0f)]
    public void EveryAxisGetsTheSameHead(float x, float y, float z)
    {
        var axis = new Vector3(x, y, z);
        var v = ViewportMeshRenderer.BuildGizmoAxis(Move, Pivot, axis, Arm);
        Assert.Equal(1 + 12 + 12, SegCount(v));

        var tip = Pivot + axis * Arm;
        int slants = 0;
        for (int i = 0; i < SegCount(v); i++)
            if (Vector3.Distance(Seg(v, i).B, tip) < 1e-3f) slants++;
        Assert.Equal(12, slants);

        foreach (float f in v) Assert.False(float.IsNaN(f), "degenerate basis produced NaN");
    }
}
