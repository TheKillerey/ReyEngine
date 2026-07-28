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

        Assert.Equal(2_000f, cam.EffectiveNear, 3);      // at range, unchanged

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
        Assert.True(cam.EffectiveNear >= 0.02f, $"near floor breached: {cam.EffectiveNear}");
        Assert.True(cam.EffectiveNear < cam.Far);
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
