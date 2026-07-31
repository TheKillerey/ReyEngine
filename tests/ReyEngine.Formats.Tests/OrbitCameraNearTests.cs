using ReyEngine.Rendering;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M267: the near plane must follow how close the camera is, not how far away it was when the scene was
/// framed. Framing a 97,000-unit map used to latch it around 2,000 units, so flying in clipped away
/// everything within 2,000 units of the eye - the camera could orbit a map and then refuse to approach
/// anything in it.
///
/// Worth a test rather than a look, because the symptom is indistinguishable from a dozen other things:
/// geometry vanishes as you get close, which reads equally well as culling, a broken projection, or a
/// depth bug. Nothing about it says "near plane".
/// </summary>
public class OrbitCameraNearTests
{
    /// <summary>The regression, stated as the user hit it: frame a whole map, then zoom in on a prop.</summary>
    [Fact]
    public void ZoomingInAfterFramingALargeMapTightensTheNearPlane()
    {
        var cam = new OrbitCamera();
        // What FrameCamera does for a map-sized mesh: a huge framing distance, and a near plane sized
        // from it as a ceiling.
        cam.Distance = 200_000f;
        cam.Near = 2_000f;

        // M291 lowered the coefficient from 0.01 to 0.0025, so the near plane now starts shrinking four
        // times further out: at this range it is 200_000 * 0.0025 = 500 rather than the caller's 2_000
        // ceiling. Still far below Far, so distant rendering is unaffected; the ceiling remains an upper
        // bound and is asserted as such rather than as an exact value.
        Assert.Equal(500f, cam.EffectiveNear, 3);
        Assert.True(cam.EffectiveNear <= cam.Near, "the caller's ceiling must remain an upper bound");

        cam.Distance = 500f;                              // fly in to inspect a prop
        Assert.True(cam.EffectiveNear <= 5f,
            $"near plane stayed at {cam.EffectiveNear} after closing to 500 units - geometry within that "
            + "distance of the eye is clipped, which is the bug");
    }

    [Fact]
    public void TheNearPlaneNeverExceedsTheCeilingACallerSet()
    {
        var cam = new OrbitCamera { Distance = 600f, Near = 1f };
        // Distance * 0.01 would be 6; the ceiling wins, so the default framing behaviour is unchanged.
        Assert.Equal(1f, cam.EffectiveNear, 4);
    }

    [Fact]
    public void TheNearPlaneHasAFloorSoTheDepthRangeStaysFinite()
    {
        var cam = new OrbitCamera { Near = 1f };
        cam.Zoom(0.0001f);                                // slam into the distance floor
        Assert.True(cam.Distance >= 1f, $"distance floor breached: {cam.Distance}");
        Assert.True(cam.EffectiveNear >= 0.01f, $"near floor breached: {cam.EffectiveNear}");   // M291: 0.02 -> 0.01
        Assert.True(cam.EffectiveNear < cam.Far);
    }

    /// <summary>
    /// M291: each graphics API gets the depth range it actually expects.
    ///
    /// <para>System.Numerics builds Direct3D-convention projections (clip z in [0, w]). Handing that to
    /// OpenGL, whose clip and glDepthRange both assume [-w, w], does not clip anything away - so nothing
    /// LOOKS wrong - it just squeezes every visible fragment into the upper half of the depth buffer and
    /// doubles z-fighting. A silent one-bit precision loss is exactly the kind of thing that needs a test
    /// rather than an eye.</para>
    /// </summary>
    [Fact]
    public void EachProjectionUsesTheDepthRangeItsApiExpects()
    {
        var cam = new OrbitCamera { Distance = 1_000f, Near = 1f };
        const float aspect = 16f / 9f;
        float n = cam.EffectiveNear, f = cam.Far;

        var d3d = cam.Projection(aspect);
        var gl = cam.ProjectionGl(aspect);

        // Depth of a view-space point after projection, as normalised device z. The camera looks down -Z.
        static float Ndc(System.Numerics.Matrix4x4 m, float viewZ)
        {
            var c = System.Numerics.Vector4.Transform(
                new System.Numerics.Vector4(0f, 0f, -viewZ, 1f), m);
            return c.Z / c.W;
        }

        Assert.Equal(0f, Ndc(d3d, n), 3);     // D3D: near -> 0
        Assert.Equal(1f, Ndc(d3d, f), 3);     //      far  -> 1
        Assert.Equal(-1f, Ndc(gl, n), 3);     // GL:   near -> -1
        Assert.Equal(1f, Ndc(gl, f), 3);      //       far  -> +1

        // Only the depth rows may differ - if the horizontal or vertical scale drifted, the two viewports
        // would disagree about field of view and every screen-space comparison between them would be void.
        Assert.Equal(d3d.M11, gl.M11, 5);
        Assert.Equal(d3d.M22, gl.M22, 5);
        Assert.Equal(-1f, gl.M34, 5);
    }

    /// <summary>The point of the change: you can get closer than before, on any mesh.</summary>
    [Fact]
    public void ZoomReachesACloserStandoffThanTheOldFloor()
    {
        var cam = new OrbitCamera { Distance = 600f };
        for (int i = 0; i < 200; i++) cam.Zoom(0.9f);
        Assert.True(cam.Distance <= 1.001f, $"could only reach {cam.Distance} units");
    }
}
