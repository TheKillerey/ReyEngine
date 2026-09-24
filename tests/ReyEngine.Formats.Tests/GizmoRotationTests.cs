using System;
using System.Numerics;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Rendering;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M766: a rotate ring turns the object about exactly the axis it draws - for meshes (X, Y, Z Euler), added
/// meshes (yaw-pitch-roll) and particles/props (yaw-pitch-roll inside their authored frame) - in World and
/// Local mode, and the angles written back stay continuous.
/// </summary>
public sealed class GizmoRotationTests
{
    private static void Near(Matrix4x4 expected, Matrix4x4 actual, float tol = 2e-4f)
    {
        float[] e = { expected.M11, expected.M12, expected.M13, expected.M21, expected.M22, expected.M23, expected.M31, expected.M32, expected.M33 };
        float[] a = { actual.M11, actual.M12, actual.M13, actual.M21, actual.M22, actual.M23, actual.M31, actual.M32, actual.M33 };
        for (int i = 0; i < 9; i++)
            Assert.True(MathF.Abs(e[i] - a[i]) < tol, $"element {i}: expected {e[i]:0.00000}, got {a[i]:0.00000}");
    }

    public static TheoryData<float, float, float> Angles => new()
    {
        { 0f, 0f, 0f }, { 30f, -45f, 60f }, { 170f, 20f, -130f }, { -75f, 89.9f, 10f },
        { 12f, -89.99f, 44f }, { 0f, 90f, 0f }, { 200f, -10f, 355f },
    };

    [Theory]
    [MemberData(nameof(Angles))]
    public void Decompose_recovers_the_same_rotation_in_both_orders(float x, float y, float z)
    {
        foreach (var order in new[] { GizmoEulerOrder.Xyz, GizmoEulerOrder.YawPitchRoll })
        {
            // for yaw-pitch-roll, X is the one that can lock; swap so the near-90 cases hit it too
            var deg = order == GizmoEulerOrder.Xyz ? new Vector3(x, y, z) : new Vector3(y, x, z);
            var m = GizmoRotation.Compose(deg, order);
            Near(m, GizmoRotation.Compose(GizmoRotation.Decompose(m, order, deg), order), tol: 1e-3f);   // asin loses digits beside the lock
        }
    }

    [Fact]
    public void Xyz_is_the_mesh_convention() =>
        Near(new MapGeoMesh { Index = 0, Name = "m", VertexStart = 0, VertexCount = 0, Transform = Matrix4x4.Identity, Pivot = Vector3.Zero, RotationDegrees = new(30f, -45f, 60f) }.ScaleRotationMatrix,
             GizmoRotation.Compose(new(30f, -45f, 60f), GizmoEulerOrder.Xyz));

    public static TheoryData<int, int> KindsAndAxes()
    {
        var d = new TheoryData<int, int>();
        for (int kind = 0; kind < 3; kind++) for (int axis = 0; axis < 3; axis++) d.Add(kind, axis);
        return d;
    }

    /// <summary>kind 0 = mesh, 1 = added mesh, 2 = particle/prop with an authored rotation.</summary>
    [Theory]
    [MemberData(nameof(KindsAndAxes))]
    public void A_ring_turns_the_object_about_exactly_its_world_axis(int kind, int axis)
    {
        var order = kind == 0 ? GizmoEulerOrder.Xyz : GizmoEulerOrder.YawPitchRoll;
        var authored = kind == 2
            ? Matrix4x4.CreateScale(1.3f) * Matrix4x4.CreateFromYawPitchRoll(0.9f, -0.4f, 0.25f) * Matrix4x4.CreateTranslation(2701f, 95f, 4752f)
            : Matrix4x4.Identity;
        var b = GizmoRotation.RotationOnly(authored);
        var start = new Vector3(25f, -60f, 110f);
        var d = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;

        var after = GizmoRotation.RotateAbout(start, order, authored, d, 37f);

        // the object's full orientation turned by 37 degrees about the world axis, nothing else
        var expected = GizmoRotation.Compose(start, order) * b * Matrix4x4.CreateFromAxisAngle(d, 37f * MathF.PI / 180f);
        Near(expected, GizmoRotation.Compose(after, order) * b);
    }

    [Theory]
    [MemberData(nameof(KindsAndAxes))]
    public void In_local_mode_a_ring_turns_about_the_objects_own_axis(int kind, int axis)
    {
        var order = kind == 0 ? GizmoEulerOrder.Xyz : GizmoEulerOrder.YawPitchRoll;
        var authored = kind == 2 ? Matrix4x4.CreateFromYawPitchRoll(-1.2f, 0.3f, 0.7f) : Matrix4x4.Identity;
        var start = new Vector3(-40f, 75f, 15f);
        var (lx, ly, lz) = GizmoRotation.LocalAxes(start, order, authored);
        var d = axis == 0 ? lx : axis == 1 ? ly : lz;

        var after = GizmoRotation.RotateAbout(start, order, authored, d, -52f);

        // turning about the object's own axis leaves that axis where it was, and moves the other two
        var (ax, ay, az) = GizmoRotation.LocalAxes(after, order, authored);
        var kept = axis == 0 ? ax : axis == 1 ? ay : az;
        Assert.True(Vector3.Distance(d, kept) < 1e-3f, $"the local {"XYZ"[axis]} axis moved: {d} -> {kept}");
        var moved = axis == 0 ? ay : ax;
        var before = axis == 0 ? ly : lx;
        Assert.True(Vector3.Distance(before, moved) > 0.5f, "a -52 degree turn left the other axes where they were");
    }

    [Fact]
    public void Local_axes_follow_each_objects_own_convention()
    {
        // the mesh axes used to be drawn with the placement's yaw-pitch-roll; for a mesh they are the rows of X*Y*Z
        var deg = new Vector3(30f, -45f, 60f);
        var (x, _, _) = GizmoRotation.LocalAxes(deg, GizmoEulerOrder.Xyz, Matrix4x4.Identity);
        var mesh = GizmoRotation.Compose(deg, GizmoEulerOrder.Xyz);
        Assert.True(Vector3.Distance(x, new Vector3(mesh.M11, mesh.M12, mesh.M13)) < 1e-4f);
        var (px, _, _) = GizmoRotation.LocalAxes(deg, GizmoEulerOrder.YawPitchRoll, Matrix4x4.Identity);
        Assert.True(Vector3.Distance(x, px) > 0.1f, "the two conventions give the same axes here, so this test measures nothing");
    }

    [Fact]
    public void A_drag_moves_the_angles_continuously()
    {
        // a whole turn in 5 degree steps from the drag start, as a drag recomputes every frame: the written
        // angles never jump by the 180/360 an unconstrained decomposition would produce. (Beside gimbal lock two
        // angles legitimately swing fast - 14 degrees in a 5 degree step on the X turn below - so the bound is
        // set against a flip, not against that.)
        foreach (var order in new[] { GizmoEulerOrder.Xyz, GizmoEulerOrder.YawPitchRoll })
            foreach (var d in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            {
                var start = new Vector3(10f, 20f, 30f);
                var prev = start;
                for (float t = 5f; t <= 360f; t += 5f)
                {
                    // recomputed from the start every frame, near the angles written last frame - as the drag does
                    var now = GizmoRotation.RotateAbout(start, order, Matrix4x4.Identity, d, t, near: prev);
                    Assert.True(Vector3.Distance(now, prev) < 60f, $"{order} about {d} at {t}: {prev} -> {now}");
                    prev = now;
                }
            }
    }

    [Fact]
    public void A_mirrored_placement_keeps_its_mirror_in_the_frame()
    {
        // the frame must be the one that RENDERS: rows of the authored matrix, scale removed, handedness kept
        var mirrored = Matrix4x4.CreateScale(-1.5f, 2f, 0.7f) * Matrix4x4.CreateFromYawPitchRoll(0.5f, -0.3f, 1.1f);
        var r = GizmoRotation.RotationOnly(mirrored);
        Assert.Equal(-1f, r.GetDeterminant(), 3);
        Assert.True(Vector3.Distance(new Vector3(r.M31, r.M32, r.M33), Vector3.Normalize(new Vector3(mirrored.M31, mirrored.M32, mirrored.M33))) < 1e-4f,
            "the Z row was rebuilt right-handed instead of kept");

        // and a drag on it still turns the RENDERED object about exactly the world axis. A mirror with equal
        // magnitudes - a non-uniform authored scale sits BETWEEN the extra angles and the authored rotation, so
        // no change to those angles can give a rigid world-axis turn there (see GizmoRotation.RotateAbout).
        mirrored = Matrix4x4.CreateScale(-1.3f, 1.3f, 1.3f) * Matrix4x4.CreateFromYawPitchRoll(0.5f, -0.3f, 1.1f);
        var start = new Vector3(15f, 40f, -70f);
        foreach (var d in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            var after = GizmoRotation.RotateAbout(start, GizmoEulerOrder.YawPitchRoll, mirrored, d, 33f);
            var rendered0 = GizmoRotation.Compose(start, GizmoEulerOrder.YawPitchRoll) * mirrored;
            var rendered1 = GizmoRotation.Compose(after, GizmoEulerOrder.YawPitchRoll) * mirrored;
            // an independent oracle: every rendered point p (a row-vector image) moved by exactly Rot(d, 33)
            var turn = Matrix4x4.CreateFromAxisAngle(d, 33f * MathF.PI / 180f);
            foreach (var p in new[] { new Vector3(1f, 2f, 3f), new Vector3(-4f, 0.5f, 1f) })
            {
                var before = Vector3.TransformNormal(p, rendered0);
                var now = Vector3.TransformNormal(p, rendered1);
                Assert.True(Vector3.Distance(Vector3.TransformNormal(before, turn), now) < 1e-3f,
                    $"about {d}: expected {Vector3.TransformNormal(before, turn)}, rendered {now}");
            }
        }
    }
}
