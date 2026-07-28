using System.Numerics;
using ReyEngine.Rendering;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M263: mesh selection under D3D11 is a CPU raycast against a view-projection the GL render used to be
/// the only writer of. Now that the D3D11 path supplies its own, the thing that can silently go wrong is
/// the CONVENTION - the X mirror, the aspect, the Y direction of screen space. A wrong matrix does not
/// throw; it just selects the wrong mesh, or nothing, which is indistinguishable from "picking is not
/// implemented".
///
/// So this pins the round trip rather than the code path: project a known world point to screen with the
/// same matrix the viewport caches, unproject that screen point back to a ray, and require the ray to pass
/// through the original point.
/// </summary>
public class ViewportPickRoundTripTests
{
    /// <summary>The mirror the viewport applies for both renderers: League data is authored in the
    /// opposite handedness. GL premultiplies it into the view-projection; D3D11 does the same thing inside
    /// the shader via PreviewSettings.MirrorX.</summary>
    private static Matrix4x4 ViewProj(OrbitCamera cam, double w, double h)
        => Matrix4x4.CreateScale(-1f, 1f, 1f) * cam.ViewProjection(h <= 0 ? 1f : (float)(w / h));

    [Theory]
    [InlineData(1600, 900)]
    [InlineData(900, 1600)]     // portrait: catches an aspect applied the wrong way round
    [InlineData(1024, 1024)]
    public void ProjectedPointUnprojectsToARayThroughIt(double w, double h)
    {
        var cam = new OrbitCamera { Distance = 9000f, Pitch = 0.6f, Yaw = 0.9f };
        var vp = ViewProj(cam, w, h);

        // Points spread around the origin, none of them symmetric, so a mirrored or transposed matrix
        // cannot round-trip by accident.
        foreach (var world in new[]
                 {
                     new Vector3(0f, 0f, 0f),
                     new Vector3(1200f, 300f, -800f),
                     new Vector3(-450f, -1100f, 2000f),
                     new Vector3(2500f, 60f, 2500f),
                 })
        {
            Assert.True(ViewportPicking.ProjectToScreen(world, vp, w, h, out var screen),
                $"{world} did not project at {w}x{h}");
            Assert.True(ViewportPicking.TryGetRay(screen, vp, w, h, out var origin, out var dir),
                $"{screen} did not unproject at {w}x{h}");

            // Distance from the world point to the ray. Scale-relative, because these are thousands of
            // units from the camera and an absolute epsilon would be meaningless at that range.
            var toPoint = world - origin;
            float along = Vector3.Dot(toPoint, Vector3.Normalize(dir));
            var closest = origin + Vector3.Normalize(dir) * along;
            float miss = Vector3.Distance(closest, world);

            Assert.True(along > 0f, $"{world} came back BEHIND the camera at {w}x{h} - the ray is inverted");
            Assert.True(miss < along * 1e-3f,
                $"{world} at {w}x{h}: ray missed by {miss:F2} units at range {along:F0}");
        }
    }

    /// <summary>The mirror has to be present. Without it every pick lands on the X-flipped mesh - which
    /// still selects something, so nothing fails loudly.</summary>
    [Fact]
    public void UnmirroredMatrixDoesNotRoundTrip()
    {
        var cam = new OrbitCamera { Distance = 9000f, Pitch = 0.6f, Yaw = 0.9f };
        const double w = 1600, h = 900;
        var world = new Vector3(1200f, 300f, -800f);

        Assert.True(ViewportPicking.ProjectToScreen(world, ViewProj(cam, w, h), w, h, out var screen));
        // Unproject with the un-mirrored matrix, as if the D3D11 path had forgotten MirrorX.
        Assert.True(ViewportPicking.TryGetRay(screen, cam.ViewProjection((float)(w / h)), w, h,
            out var origin, out var dir));

        var toPoint = world - origin;
        float along = Vector3.Dot(toPoint, Vector3.Normalize(dir));
        float miss = Vector3.Distance(origin + Vector3.Normalize(dir) * along, world);
        Assert.True(miss > along * 1e-3f, "dropping the mirror should NOT round-trip, but it did");
    }
}
