using System;
using System.Numerics;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M245: the frustum cull. The asymmetry here is the whole point — keeping a box the frustum does not
/// really touch costs one wasted draw, but rejecting one that IS visible puts a hole in the image and
/// looks like a loading bug. So these test the false-reject rate specifically, not just "does it reject".
/// </summary>
public class FrustumCullTests
{
    private static Matrix4x4 Vp()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 300, 800), Vector3.Zero, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 16f / 9f, 1f, 5000f);
        return Matrix4x4.Multiply(view, proj);
    }

    [Fact]
    public void A_box_at_the_focus_point_is_kept()
    {
        Assert.True(ShaderPreviewRenderer.TestFrustumForTests(Vp(), new Vector3(-50), new Vector3(50)));
    }

    [Fact]
    public void A_box_far_behind_the_camera_is_rejected()
    {
        Assert.False(ShaderPreviewRenderer.TestFrustumForTests(
            Vp(), new Vector3(-50, -50, 3000), new Vector3(50, 50, 3100)));
    }

    [Fact]
    public void A_box_enclosing_the_whole_frustum_is_kept()
    {
        // No corner of it is inside the clip volume, but it still covers the screen. A naive
        // corner-in-frustum test gets this wrong; the plane test does not.
        Assert.True(ShaderPreviewRenderer.TestFrustumForTests(
            Vp(), new Vector3(-100000), new Vector3(100000)));
    }

    [Fact]
    public void Never_rejects_a_box_with_a_corner_inside_the_clip_volume()
    {
        // 50,000 random boxes against a brute-force reference. A single false reject fails this.
        var vp = Vp();
        var rng = new Random(11);
        int falseRejects = 0, rejected = 0;

        for (int i = 0; i < 50_000; i++)
        {
            var c = new Vector3(rng.NextSingle() * 4000 - 2000, rng.NextSingle() * 1000 - 500,
                                rng.NextSingle() * 4000 - 2000);
            var h = new Vector3(rng.NextSingle() * 200 + 5);
            Vector3 min = c - h, max = c + h;

            bool ours = ShaderPreviewRenderer.TestFrustumForTests(vp, min, max);
            if (!ours) rejected++;

            bool anyCornerInside = false;
            for (int k = 0; k < 8 && !anyCornerInside; k++)
            {
                var p = new Vector3((k & 1) == 0 ? min.X : max.X, (k & 2) == 0 ? min.Y : max.Y,
                                    (k & 4) == 0 ? min.Z : max.Z);
                var cp = Vector4.Transform(new Vector4(p, 1f), vp);
                if (cp.W > 0 && MathF.Abs(cp.X) <= cp.W && MathF.Abs(cp.Y) <= cp.W && cp.Z >= 0 && cp.Z <= cp.W)
                    anyCornerInside = true;
            }
            if (anyCornerInside && !ours) falseRejects++;
        }

        Assert.Equal(0, falseRejects);
        // and it must actually be doing something - a culler that keeps everything also has 0 false rejects
        Assert.True(rejected > 20_000, $"only {rejected} of 50,000 rejected; the cull is not working");
    }
}
