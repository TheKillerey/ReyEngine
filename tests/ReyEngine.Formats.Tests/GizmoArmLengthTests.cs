using System;
using System.Numerics;
using ReyEngine.App.Views;
using ReyEngine.Rendering;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M767: "When I move the gizmo is somehow getting bigger. But only when I move my camera." The arm length was
/// measured by projecting the camera's own up through the MIRRORED view-projection; its screen height is then
/// 1 - 2*ux^2 of the truth, so a tilted camera facing along X measured almost nothing and the arm exploded.
/// </summary>
public sealed class GizmoArmLengthTests
{
    private const float W = 1600f, H = 900f;

    // the orbit camera lives in the mirrored space, so it aims at the mirrored pivot - as the app's does
    private static Vector3 Mirrored(Vector3 p) => new(-p.X, p.Y, p.Z);

    /// <summary>The drawn arm's on-screen length, measured perpendicular to the view (along the camera's
    /// right, taken into world space) so foreshortening cannot hide an error.</summary>
    private static float ArmPixels(OrbitCamera cam, Vector3 pivot, float arm)
    {
        var vp = Matrix4x4.CreateScale(-1f, 1f, 1f) * cam.ViewProjectionGl(W / H);
        var right = new Vector3(-cam.Right.X, cam.Right.Y, cam.Right.Z);   // camera right in world space
        ViewportPicking.ProjectToScreen(pivot, vp, W, H, out var a);
        ViewportPicking.ProjectToScreen(pivot + right * arm, vp, W, H, out var b);
        return Vector2.Distance(a, b);
    }

    [Fact]
    public void The_arm_is_the_same_size_on_screen_from_every_camera()
    {
        var pivot = new Vector3(2710f, 119f, 4749f);
        float min = float.MaxValue, max = 0f;
        for (float yaw = 0f; yaw < MathF.Tau; yaw += 0.2f)
            for (float pitch = 0.1f; pitch < 1.5f; pitch += 0.2f)
                foreach (float dist in new[] { 400f, 1500f, 6000f })
                {
                    var cam = new OrbitCamera { Target = Mirrored(pivot), Distance = dist, Yaw = yaw, Pitch = pitch };
                    var vp = Matrix4x4.CreateScale(-1f, 1f, 1f) * cam.ViewProjectionGl(W / H);
                    float arm = ViewportControl.GizmoArmLength(pivot, cam.Up, vp, H);
                    float px = ArmPixels(cam, pivot, arm);
                    min = MathF.Min(min, px); max = MathF.Max(max, px);
                }
        Assert.InRange(min, ViewportControl.GizmoArmPixels * 0.9f, ViewportControl.GizmoArmPixels * 1.1f);
        Assert.InRange(max, ViewportControl.GizmoArmPixels * 0.9f, ViewportControl.GizmoArmPixels * 1.1f);
    }

    [Fact]
    public void The_old_probe_blew_up_on_a_tilted_camera_facing_along_X()
    {
        // the reported symptom, pinned so the test above is known to measure something
        var pivot = new Vector3(2710f, 119f, 4749f);
        var cam = new OrbitCamera { Target = Mirrored(pivot), Distance = 1500f, Yaw = MathF.PI / 2f, Pitch = MathF.PI / 4f };
        var vp = Matrix4x4.CreateScale(-1f, 1f, 1f) * cam.ViewProjectionGl(W / H);
        float old = ReyEngine.Core.Rendering.ScreenSize.WorldSizeForPixels(pivot, cam.Up, ViewportControl.GizmoArmPixels, vp, H);
        float now = ViewportControl.GizmoArmLength(pivot, cam.Up, vp, H);
        Assert.True(old == 0f || old > now * 100f, $"old probe gave {old:0}, fixed {now:0}");   // measured: 187,640 vs 152
    }
}
