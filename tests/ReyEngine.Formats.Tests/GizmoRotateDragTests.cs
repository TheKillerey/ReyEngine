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
}
