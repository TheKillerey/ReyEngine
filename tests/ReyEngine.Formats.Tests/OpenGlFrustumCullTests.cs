using System.Numerics;
using ReyEngine.Rendering;

namespace ReyEngine.Formats.Tests;

public class OpenGlFrustumCullTests
{
    private static Matrix4x4 Vp()
    {
        var camera = new OrbitCamera
        {
            Target = Vector3.Zero,
            Distance = 850f,
            Yaw = 0f,
            Pitch = MathF.Asin(300f / 850f),
            Near = 1f,
            Far = 5000f,
            FieldOfView = 0.9f,
        };
        return camera.ViewProjectionGl(16f / 9f);
    }

    [Fact]
    public void Keeps_focus_and_rejects_geometry_behind_the_camera()
    {
        var f = ViewFrustum.FromOpenGl(Vp());
        Assert.True(f.Intersects(new Vector3(-50), new Vector3(50)));
        Assert.False(f.Intersects(new Vector3(-50, -50, 3000), new Vector3(50, 50, 3100)));
    }

    [Fact]
    public void Keeps_a_box_that_encloses_the_frustum()
        => Assert.True(ViewFrustum.FromOpenGl(Vp()).Intersects(new Vector3(-100000), new Vector3(100000)));

    [Fact]
    public void Never_rejects_a_box_with_an_OpenGL_clip_space_corner_inside()
    {
        var vp = Vp();
        var f = ViewFrustum.FromOpenGl(vp);
        var rng = new Random(307);
        int falseRejects = 0, rejected = 0;
        for (int i = 0; i < 50_000; i++)
        {
            var center = new Vector3(rng.NextSingle() * 4000f - 2000f,
                rng.NextSingle() * 1000f - 500f, rng.NextSingle() * 4000f - 2000f);
            var half = new Vector3(rng.NextSingle() * 200f + 5f);
            var min = center - half; var max = center + half;
            bool ours = f.Intersects(min, max);
            if (!ours) rejected++;

            bool cornerInside = false;
            for (int k = 0; k < 8 && !cornerInside; k++)
            {
                var point = new Vector3((k & 1) == 0 ? min.X : max.X,
                    (k & 2) == 0 ? min.Y : max.Y, (k & 4) == 0 ? min.Z : max.Z);
                var clip = Vector4.Transform(new Vector4(point, 1f), vp);
                cornerInside = clip.W > 0f && MathF.Abs(clip.X) <= clip.W && MathF.Abs(clip.Y) <= clip.W
                    && MathF.Abs(clip.Z) <= clip.W;
            }
            if (cornerInside && !ours) falseRejects++;
        }

        Assert.Equal(0, falseRejects);
        Assert.True(rejected > 20_000, $"only {rejected} of 50,000 rejected; the cull is not working");
    }
}
