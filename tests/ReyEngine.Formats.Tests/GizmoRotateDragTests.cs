using System;
using System.Numerics;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Rendering;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M764: "When I want to rotate right it goes left." The rotate drag was a fixed horizontal mapping that
/// ignored the viewport's X mirror. These tests run the real camera, the real mirrored pick matrix and the
/// real mesh and placement rotations, and require that the point the user grabbed moves WITH the mouse.
/// </summary>
public sealed class GizmoRotateDragTests
{
    private const float W = 1600f, H = 900f, D2R = MathF.PI / 180f;

    private static Matrix4x4 MeshRot(Vector3 deg)
    {
        var m = new MapGeoMesh { Index = 0, Name = "p", VertexStart = 0, VertexCount = 0, Transform = Matrix4x4.Identity, Pivot = Vector3.Zero };
        m.RotationDegrees = deg;
        return m.ScaleRotationMatrix;
    }

    // MapContentViewModel's placement rotation
    private static Matrix4x4 PlacementRot(Vector3 deg) => Matrix4x4.CreateFromYawPitchRoll(deg.Y * D2R, deg.X * D2R, deg.Z * D2R);

    private static float Comp(Vector3 v, int c) => c == 0 ? v.X : c == 1 ? v.Y : v.Z;
    private static Vector3 With(Vector3 v, int c, float x) => c == 0 ? v with { X = x } : c == 1 ? v with { Y = x } : v with { Z = x };

    /// <summary>For every front-half grab point on each ring: press there, drag 2 px along the ring as it is
    /// drawn, and count the grabs whose grabbed point moves the way the mouse went.</summary>
    private static (int ok, int n) Sweep(float yaw, float pitch, int comp, bool mesh, bool fixedMapping)
    {
        var cam = new OrbitCamera { Target = Vector3.Zero, Distance = 1000f, Yaw = yaw, Pitch = pitch };
        var vp = Matrix4x4.CreateScale(-1f, 1f, 1f) * cam.ViewProjectionGl(W / H);   // the map views' mirror
        var eye = new Vector3(-cam.Position.X, cam.Position.Y, cam.Position.Z);
        Vector2? Project(Vector3 p) => ViewportPicking.ProjectToScreen(p, vp, W, H, out var s) ? s : null;

        var axis = comp == 0 ? Vector3.UnitX : comp == 1 ? Vector3.UnitY : Vector3.UnitZ;
        var u = Vector3.Normalize(MathF.Abs(axis.Y) < 0.99f ? Vector3.Cross(axis, Vector3.UnitY) : Vector3.Cross(axis, Vector3.UnitX));
        var w = Vector3.Cross(axis, u);
        int ok = 0, n = 0;
        for (int i = 0; i < 360; i += 5)
        {
            var g = (u * MathF.Cos(i * D2R) + w * MathF.Sin(i * D2R)) * 100f;
            if (Vector3.Dot(g, eye) <= 0) continue;   // front half: the part of the ring a user grabs
            if (Project(g) is not { } s0 || Project(g + Vector3.Cross(axis, g) * 0.01f) is not { } ahead) continue;
            var along = ahead - s0;
            // the ring's silhouette: a 0.01 rad step moves the point under 0.1 px, and any finite drag carries it
            // over the edge and back - no mapping can "follow the mouse" there, and a grab there is ambiguous
            if (along.Length() < 0.1f) continue;
            var mouse = Vector2.Normalize(along) * 2f;   // the first 2 px of a drag along the visible ring, either way round
            foreach (var m in new[] { mouse, -mouse })
            {
                var tangent = GizmoRotateDrag.ScreenTangent(Vector3.Zero, axis, 100f, s0, vp, W, H);
                float deg = fixedMapping ? GizmoRotateDrag.Degrees(m, tangent) : m.X * GizmoRotateDrag.DegreesPerPixel;
                var start = Vector3.Zero;
                var r1 = mesh ? MeshRot(With(start, comp, Comp(start, comp) + deg)) : PlacementRot(With(start, comp, Comp(start, comp) + deg));
                if (Project(Vector3.Transform(g, r1)) is not { } s1) continue;
                n++;
                if (Vector2.Dot(s1 - s0, m) > 0) ok++;
            }
        }
        return (ok, n);
    }

    public static TheoryData<float, float> Cameras => new()
    {
        { 0.7f, 0.5f },          // the default view
        { 0.0f, 1.45f },         // near top-down
        { MathF.PI, 1.45f },     // top-down, turned round
        { 1.9f, 0.15f },         // low, from the side
    };

    [Theory]
    [MemberData(nameof(Cameras))]
    public void The_grabbed_point_follows_the_mouse_on_every_ring(float yaw, float pitch)
    {
        for (int comp = 0; comp < 3; comp++)
            foreach (bool mesh in new[] { true, false })
            {
                var (ok, n) = Sweep(yaw, pitch, comp, mesh, fixedMapping: true);
                Assert.True(n > 0);
                Assert.True(ok == n, $"{"XYZ"[comp]} ring, {(mesh ? "mesh" : "placement")}: {ok}/{n} grabs follow the mouse");
            }
    }

    [Fact]
    public void The_old_horizontal_mapping_turned_the_Y_ring_backwards()
    {
        // the reported symptom, pinned so the test above is known to measure something
        var (ok, n) = Sweep(0.7f, 0.5f, comp: 1, mesh: true, fixedMapping: false);
        Assert.True(ok < n / 2, $"old mapping: {ok}/{n} follow");
    }

    [Fact]
    public void With_no_tangent_the_drag_is_horizontal_as_before()
    {
        // a matrix that projects nothing (W = 0 everywhere): no grab point, no tangent
        Assert.Null(GizmoRotateDrag.ScreenTangent(Vector3.Zero, Vector3.UnitY, 100f, Vector2.Zero, new Matrix4x4(), W, H));
        Assert.Equal(5f, GizmoRotateDrag.Degrees(new Vector2(10f, 40f), null));
    }

    // ---------------------------------------------------------------- M767: the angle swept in the ring's plane

    /// <summary>Follow the drawn ring round with the cursor, casting the real pick ray through the mirrored
    /// matrix each step as the drag does, and return the total accumulated turn (degrees) - or null when the
    /// ring is edge-on from this camera (the drag then uses the tangent fallback).</summary>
    private static float? FollowRing(float yaw, float pitch, Vector3 axis, float followDegrees)
    {
        var cam = new OrbitCamera { Target = Vector3.Zero, Distance = 1000f, Yaw = yaw, Pitch = pitch };
        var vp = Matrix4x4.CreateScale(-1f, 1f, 1f) * cam.ViewProjectionGl(W / H);
        var eye = new Vector3(-cam.Position.X, cam.Position.Y, cam.Position.Z);
        var u = Vector3.Normalize(MathF.Abs(axis.Y) < 0.99f ? Vector3.Cross(axis, Vector3.UnitY) : Vector3.Cross(axis, Vector3.UnitX));
        var w = Vector3.Cross(axis, u);
        // grab the front of the ring
        Vector3 g = u * 100f; float best = float.MaxValue;
        for (int i = 0; i < 360; i++)
        {
            var q = (u * MathF.Cos(i * D2R) + w * MathF.Sin(i * D2R)) * 100f;
            float dist = Vector3.Distance(q, eye);
            if (dist < best) { best = dist; g = q; }
        }
        bool Ray(Vector3 world, out Vector3 o, out Vector3 d)
        {
            o = d = default;
            return ViewportPicking.ProjectToScreen(world, vp, W, H, out var s)
                && ViewportPicking.TryGetRay(s, vp, W, H, out o, out d);
        }
        if (!Ray(g, out var o0, out var d0) || GizmoRotateDrag.IsEdgeOn(d0, axis)) return null;
        Assert.True(GizmoRotateDrag.RingAngle(o0, d0, Vector3.Zero, axis, out float prev));
        float turned = 0f;
        for (float phi = 5f; phi <= followDegrees + 1e-3f; phi += 5f)
        {
            // where the grabbed point is after a POSITIVE turn of phi - the path the cursor follows
            var at = Vector3.Transform(g, Matrix4x4.CreateFromAxisAngle(axis, phi * D2R));
            if (!Ray(at, out var o, out var d) || !GizmoRotateDrag.RingAngle(o, d, Vector3.Zero, axis, out float now)) continue;
            turned += GizmoRotateDrag.AngleStep(prev, now);
            prev = now;
        }
        return turned / D2R;
    }

    [Theory]
    [MemberData(nameof(Cameras))]
    public void Following_the_ring_turns_by_exactly_the_angle_followed(float yaw, float pitch)
    {
        int measured = 0;
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            foreach (float follow in new[] { 45f, 90f, 180f, 270f, 360f, 720f })
            {
                if (FollowRing(yaw, pitch, axis, follow) is not { } turned) continue;   // edge-on: the fallback's job
                measured++;
                // M764's tangent mapping peaked near +55 and returned to 0 at a half turn; this must track exactly
                Assert.True(MathF.Abs(turned - follow) < 2f, $"axis {axis}, followed {follow}: turned {turned:0.0}");
            }
        Assert.True(measured > 0, "every ring was edge-on from this camera, so nothing was measured");
    }

    [Fact]
    public void A_ring_seen_edge_on_uses_the_tangent_fallback()
    {
        // looking straight along the ring's plane: no usable intersection, so the drag must not use it
        Assert.True(GizmoRotateDrag.IsEdgeOn(new Vector3(1f, 0f, 0f), Vector3.UnitY));
        Assert.False(GizmoRotateDrag.IsEdgeOn(new Vector3(0f, -1f, 0.2f), Vector3.UnitY));
        Assert.Equal(MathF.PI / 2f, GizmoRotateDrag.AngleStep(3f * MathF.PI / 4f, -3f * MathF.PI / 4f), 4);   // 135 -> -135 is +90 the short way
    }

    [Theory]
    [InlineData(0.2f, 200f, 0f)]      // a low camera: the Y ring seen steeply, |cos| ~ 0.2, just above the edge-on cut
    [InlineData(0.2f, 0f, 120f)]
    [InlineData(0.25f, 350f, 60f)]
    [InlineData(0.7f, -300f, 40f)]
    [InlineData(0.6f, float.NaN, 12f)]   // flicked past the pivot to the far side: the fastest sweep there is
    [InlineData(1.2f, float.NaN, -12f)]
    public void A_fast_straight_drag_sweeps_what_a_slow_one_does(float pitch, float dx, float dy)
    {
        // raised in review: could one mouse event move the plane angle past 180 degrees on a steeply-seen ring,
        // so the short-way step reads it backwards? A straight screen segment is a straight line in the plane,
        // which sweeps under 180 degrees about the pivot - so no. Pinned: ONE event carrying the whole path gives
        // what the same path drawn a quarter pixel at a time gives.
        var cam = new OrbitCamera { Target = Vector3.Zero, Distance = 1000f, Yaw = 0.4f, Pitch = pitch };
        var vp = Matrix4x4.CreateScale(-1f, 1f, 1f) * cam.ViewProjectionGl(W / H);
        (Vector3 Origin, Vector3 Dir)? Ray(Vector2 q) =>
            ViewportPicking.TryGetRay(q, vp, W, H, out var o, out var d) ? (o, d) : null;
        var axis = Vector3.UnitY;
        var eye = new Vector3(-cam.Position.X, cam.Position.Y, cam.Position.Z);
        var grab = Vector3.Normalize(new Vector3(eye.X, 0f, eye.Z)) * 100f;   // the front of the ring
        Assert.True(ViewportPicking.ProjectToScreen(grab, vp, W, H, out var press));
        var r0 = Ray(press)!.Value;
        Assert.False(GizmoRotateDrag.IsEdgeOn(r0.Dir, axis), "the press must be on the plane path for this test");
        Assert.True(GizmoRotateDrag.RingAngle(r0.Origin, r0.Dir, Vector3.Zero, axis, out float start));
        Assert.True(ViewportPicking.ProjectToScreen(Vector3.Zero, vp, W, H, out var pivotOnScreen));
        var across = pivotOnScreen - press;
        var end = float.IsNaN(dx)
            ? pivotOnScreen + across + Vector2.Normalize(new Vector2(-across.Y, across.X)) * dy   // past the pivot, dy to one side
            : press + new Vector2(dx, dy);

        var r1 = Ray(end)!.Value;
        Assert.True(GizmoRotateDrag.RingAngle(r1.Origin, r1.Dir, Vector3.Zero, axis, out float last));
        float swept = GizmoRotateDrag.AngleStep(start, last);   // one event, as the drag does it

        float slow = start, truth = 0f;
        int n = (int)(Vector2.Distance(press, end) * 4f);
        for (int k = 1; k <= n; k++)
            if (Ray(Vector2.Lerp(press, end, k / (float)n)) is { } r && GizmoRotateDrag.RingAngle(r.Origin, r.Dir, Vector3.Zero, axis, out float a))
            { truth += GizmoRotateDrag.AngleStep(slow, a); slow = a; }

        Assert.True(MathF.Abs(swept - truth) / D2R < 1f, $"swept {swept / D2R:0.0} deg, the path itself {truth / D2R:0.0}");
    }
}
