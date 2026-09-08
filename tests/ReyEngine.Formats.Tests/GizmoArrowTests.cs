using System;
using System.Numerics;
using ReyEngine.Core.Rendering;
using ReyEngine.Rendering;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M380/M658: the transform gizmo's geometry. BuildGizmoAxis is the single static builder BOTH viewports
/// call - GL binds its output directly, MainWindow hands the same arrays to _dx11.Renderer.SetGizmoGeometry -
/// so pinning it here pins it for both.
///
/// <para>M658 turned it from a line list into a TRIANGLE list: 9 floats per triangle. A wireframe gizmo
/// reads as a scribble over a busy map. What must NOT change is where the arm ends, because
/// <c>HitTestGizmoAxis</c> measures analytically against <c>pivot + axis * arm</c> and never looks at
/// this geometry - drift there is a handle that looks grabbable where it is not.</para>
/// </summary>
public class GizmoArrowTests
{
    private const int Move = 0, Rotate = 1, Scale = 2;
    private const float Arm = 100f;

    private static readonly Vector3 Pivot = Vector3.Zero;
    private static readonly Vector3 Axis = Vector3.UnitX;

    private static Vector3 Vert(float[] v, int i) => new(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);
    private static int VertCount(float[] v) => v.Length / 3;
    private static int TriCount(float[] v) => v.Length / 9;

    /// <summary>Distance along the axis, and distance from the axis line.</summary>
    private static (float Along, float Radial) Cyl(Vector3 p, Vector3 axis)
    {
        float along = Vector3.Dot(p - Pivot, axis);
        return (along, (p - Pivot - axis * along).Length());
    }

    [Theory]
    [InlineData(Move)]
    [InlineData(Rotate)]
    [InlineData(Scale)]
    public void EveryModeEmitsAWholeNumberOfTriangles(int mode)
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(mode, Pivot, Axis, Arm);
        Assert.Equal(0, v.Length % 9);
        Assert.True(TriCount(v) > 0);
    }

    /// <summary>The regression the M380 version existed for, restated for solids: move used to be one bare
    /// line with nothing on the end. A handful of triangles is not an arrow either.</summary>
    [Fact]
    public void MoveIsAShaftAndAConeAndNotAToken()
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Move, Pivot, Axis, Arm);
        // shaft: 2 per side quad + 1 base cap; cone: 1 side + 1 cap. Five per step.
        Assert.Equal(ViewportMeshRenderer.GizmoSegments * 5, TriCount(v));
    }

    /// <summary>
    /// The invariant the hit test depends on: the arm ENDS at pivot + axis * arm. Nothing may poke past
    /// it, or the drawn handle claims length the hit test does not accept.
    /// </summary>
    [Theory]
    [InlineData(Move)]
    [InlineData(Scale)]
    public void TheTipIsExactlyAtTheArmLength(int mode)
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(mode, Pivot, Axis, Arm);
        float furthest = float.MinValue;
        bool touchesTheTip = false;
        for (int i = 0; i < VertCount(v); i++)
        {
            var (along, _) = Cyl(Vert(v, i), Axis);
            furthest = MathF.Max(furthest, along);
            if (MathF.Abs(along - Arm) < 1e-3f) touchesTheTip = true;
        }
        Assert.True(touchesTheTip, "nothing reaches the arm's end");
        // scale's box is centred ON the tip, so it legitimately extends a little past it
        float allowance = mode == Scale ? Arm * 0.06f + 1e-3f : 1e-3f;
        Assert.True(furthest <= Arm + allowance, $"geometry runs {furthest - Arm:F2} past the tip");
    }

    /// <summary>The shaft starts at the pivot and is a tube around the axis, not a line and not a fan.</summary>
    [Fact]
    public void TheShaftIsATubeAroundTheAxisStartingAtThePivot()
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Move, Pivot, Axis, Arm);
        int atBase = 0;
        for (int i = 0; i < VertCount(v); i++)
        {
            var (along, radial) = Cyl(Vert(v, i), Axis);
            if (MathF.Abs(along) > 1e-3f) continue;
            // every vertex in the pivot plane is either the centre of the cap or on the shaft's rim
            Assert.True(radial < 1e-3f || MathF.Abs(radial - Arm * 0.018f) < 1e-2f,
                $"a base vertex sits {radial:F3} from the axis");
            if (radial > 1e-3f) atBase++;
        }
        Assert.True(atBase >= ViewportMeshRenderer.GizmoSegments, "the shaft has no rim at the pivot");
    }

    /// <summary>The cone's rim is a circle of radius arm*0.05 in the plane at arm*0.84 - conical, not a
    /// flat fan or a collapsed point.</summary>
    [Fact]
    public void TheConeRimIsACircleAtTheHeadBase()
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Move, Pivot, Axis, Arm);
        // Three things meet in that plane: the cap's centre, the SHAFT's rim (the shaft ends exactly
        // there) and the cone's own rim. Every vertex must be one of the three, and the cone rim must be
        // complete.
        int rim = 0;
        for (int i = 0; i < VertCount(v); i++)
        {
            var (along, radial) = Cyl(Vert(v, i), Axis);
            if (MathF.Abs(along - Arm * 0.84f) > 1e-2f) continue;
            if (radial < 1e-3f) continue;                            // the cap's centre vertex
            if (MathF.Abs(radial - Arm * 0.018f) < 1e-2f) continue;  // the shaft's rim
            Assert.Equal(Arm * 0.05f, radial, 2);
            rim++;
        }
        Assert.True(rim >= ViewportMeshRenderer.GizmoSegments, $"expected a full rim, saw {rim} points");
    }

    /// <summary>Scale keeps a full-length shaft and a solid box - 12 triangles for six faces.</summary>
    [Fact]
    public void ScaleIsAFullShaftPlusASolidBox()
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Scale, Pivot, Axis, Arm);
        Assert.Equal(ViewportMeshRenderer.GizmoSegments * 3 + 12, TriCount(v));

        // the shaft reaches the tip: some rim vertex sits at the arm's end
        bool rimAtTip = false;
        for (int i = 0; i < VertCount(v); i++)
        {
            var (along, radial) = Cyl(Vert(v, i), Axis);
            if (MathF.Abs(along - Arm) < 1e-3f && MathF.Abs(radial - Arm * 0.018f) < 1e-2f) rimAtTip = true;
        }
        Assert.True(rimAtTip, "scale's shaft must run all the way to the tip");
    }

    /// <summary>Rotate is a flat band CENTRED on the hit-test circle: the ring the user aims at is
    /// radius = arm, so drawing it off-centre would be a handle you grab beside where it looks.</summary>
    [Fact]
    public void RotateIsABandStraddlingTheHitTestCircle()
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Rotate, Pivot, Axis, Arm);
        Assert.Equal(ViewportMeshRenderer.GizmoRingSegments * 2, TriCount(v));

        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < VertCount(v); i++)
        {
            var (along, radial) = Cyl(Vert(v, i), Axis);
            Assert.Equal(0f, along, 3);                 // flat, in the plane perpendicular to the axis
            min = MathF.Min(min, radial); max = MathF.Max(max, radial);
        }
        Assert.True(min < Arm && max > Arm, $"the band {min:F1}..{max:F1} does not straddle {Arm}");
        Assert.Equal(Arm, (min + max) * 0.5f, 1);
    }

    /// <summary>The head scales with the arm, so it keeps its proportions at any camera distance.</summary>
    [Theory]
    [InlineData(10f)]
    [InlineData(500f)]
    [InlineData(5000f)]
    public void TheHeadScalesWithTheArm(float arm)
    {
        var v = ViewportMeshRenderer.BuildGizmoAxis(Move, Vector3.Zero, Axis, arm);
        float maxRadial = 0f;
        for (int i = 0; i < VertCount(v); i++)
            maxRadial = MathF.Max(maxRadial, (Vert(v, i) - Axis * Vector3.Dot(Vert(v, i), Axis)).Length());
        Assert.Equal(arm * 0.05f, maxRadial, 2);
    }

    /// <summary>Y and Z arms must get the same handle as X - the builder picks its perpendicular basis
    /// from the axis, and the near-parallel guard there is exactly the kind of thing that degenerates.</summary>
    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0f, -1f, 0f)]
    public void EveryAxisGetsTheSameHandle(float x, float y, float z)
    {
        var axis = Vector3.Normalize(new Vector3(x, y, z));
        var v = ViewportMeshRenderer.BuildGizmoAxis(Move, Pivot, axis, Arm);
        Assert.Equal(ViewportMeshRenderer.GizmoSegments * 5, TriCount(v));

        bool touchesTheTip = false;
        for (int i = 0; i < VertCount(v); i++)
            if (MathF.Abs(Vector3.Dot(Vert(v, i) - Pivot, axis) - Arm) < 1e-3f) touchesTheTip = true;
        Assert.True(touchesTheTip);

        foreach (float f in v) Assert.False(float.IsNaN(f), "degenerate basis produced NaN");
    }

    // ---- M658: the arm is a SCREEN length now ------------------------------------------------------

    private const int ViewportH = 1080;
    private const float Fov = 0.9f;

    private static Matrix4x4 ViewProjectionAt(float distance, out Vector3 up)
    {
        var eye = new Vector3(0f, 0f, distance);
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        up = Vector3.UnitY;
        return Matrix4x4.Multiply(view, Matrix4x4.CreatePerspectiveFieldOfView(Fov, 16f / 9f, 1f, 200000f));
    }

    /// <summary>
    /// The reported bug: "the arrows do not get smaller as I come closer". The old rule was
    /// <c>Clamp(distance * 0.15, 10, 5000)</c> - constant on screen only while NEITHER clamp bites, and
    /// both do. Below 67 units the 10-unit floor takes over, above 33,333 the 5,000 ceiling does, and in
    /// both regions closing in makes the gizmo grow.
    /// </summary>
    [Theory]
    [InlineData(20f)]        // inside the old floor
    [InlineData(600f)]       // the range the old rule handled correctly
    [InlineData(60000f)]     // past the old ceiling
    public void TheArmIsTheSameNumberOfPixelsAtEveryDistance(float distance)
    {
        var mvp = ViewProjectionAt(distance, out var up);
        float arm = ScreenSize.WorldSizeForPixels(Vector3.Zero, up, 110f, mvp, ViewportH);
        Assert.Equal(110f, ScreenSize.MeasurePixels(Vector3.Zero, up, arm, mvp, ViewportH), 1);
    }

    [Fact]
    public void TheOldRuleReallyDidGrowOnScreenAtBothEnds()
    {
        // Not a claim about the new code - a measurement of the rule it replaced, so the reason this
        // changed is on the record rather than in a commit message.
        static float Old(float d) => Math.Clamp(d * 0.15f, 10f, 5000f);
        static float Px(float distance)
        {
            var mvp = ViewProjectionAt(distance, out var up);
            return ScreenSize.MeasurePixels(Vector3.Zero, up, Old(distance), mvp, ViewportH);
        }

        // Both inside the floor, so the arm is a FIXED 10 world units at either distance - halving the
        // distance doubles it on screen. (60 vs 120 would not show it: at 120 the floor no longer bites.)
        Assert.True(Px(30f) > Px(60f) * 1.5f, "inside the floor, coming closer must have grown it");
        Assert.True(Px(40000f) > Px(80000f) * 1.5f, "past the ceiling, coming closer must have grown it");
        Assert.Equal(Px(600f), Px(1200f), 1);   // and in between it really was constant
    }

    [Fact]
    public void AnUnmeasurablePositionAsksTheCallerToFallBack()
    {
        // Behind the eye, and no viewport: zero, which ViewportControl reads as "use the old rule".
        var mvp = ViewProjectionAt(500f, out var up);
        Assert.Equal(0f, ScreenSize.WorldSizeForPixels(new Vector3(0f, 0f, 5000f), up, 110f, mvp, ViewportH));
        Assert.Equal(0f, ScreenSize.WorldSizeForPixels(Vector3.Zero, up, 110f, mvp, 0f));
    }
}
