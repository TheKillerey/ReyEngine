using System.Numerics;
using ReyEngine.Core.Cinematics;
using ReyEngine.Rendering;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M605: a captured frame has to clip exactly as the preview it was framed in.
///
/// <para>The editor camera does not use a fixed near plane. <see cref="OrbitCamera.EffectiveNear"/>
/// derives one from the camera's distance so close-up framing does not clip, which means a capture that
/// assumed a constant would clip differently from the viewport for exactly the shots most likely to want
/// it — a push-in that ends near a surface. It would look correct while framing and wrong only in the
/// exported sequence, which is the failure this whole mode exists to avoid.</para>
///
/// <para>So the pose takes near and far rather than assuming them, and the capture passes the live
/// camera's own values.</para>
/// </summary>
public sealed class CinematicProjectionTests
{
    private static CinematicPose Pose(float fov = 0.9f) =>
        new(new Vector3(0, 100, 300), Quaternion.Identity, fov, 0f);

    [Fact]
    public void ThePoseFrustumMatchesTheEditorCamerasForTheSameSettings()
    {
        // The property the capture depends on: given the camera's own near/far, the two projections are
        // the same matrix. If this drifts, exports stop matching the viewport.
        var camera = new OrbitCamera { Distance = 600f, FieldOfView = 0.9f };
        const float aspect = 16f / 9f;

        var fromCamera = camera.Projection(aspect);
        var fromPose = Pose().Projection(aspect, camera.EffectiveNear, camera.Far);

        Assert.Equal(fromCamera.M11, fromPose.M11, 5);
        Assert.Equal(fromCamera.M22, fromPose.M22, 5);
        Assert.Equal(fromCamera.M33, fromPose.M33, 4);
        Assert.Equal(fromCamera.M43, fromPose.M43, 3);
    }

    [Fact]
    public void ACloseCameraGetsTheNearerPlaneRatherThanAFixedOne()
    {
        // EffectiveNear = min(Near, max(0.01, Distance * 0.0025)). At 100 units that is 0.25, not 1 - and
        // a hardcoded 1 would clip a quarter of a unit of geometry the preview shows.
        var close = new OrbitCamera { Distance = 100f };
        Assert.True(close.EffectiveNear < 1f);

        // M43 is near*far/(near-far) - the element that actually carries the near plane. M33 is
        // far/(near-far), which is ~-1.000005 either way and would hide the difference entirely.
        var assumed = Pose().Projection(1.6f);                                   // the old fixed default
        var actual = Pose().Projection(1.6f, close.EffectiveNear, close.Far);
        Assert.Equal(-1.000005f, assumed.M43, 4);
        Assert.Equal(-0.25f, actual.M43, 3);
    }

    [Fact]
    public void AFarCameraStillGetsTheCamerasOwnNearPlane()
    {
        var far = new OrbitCamera { Distance = 20000f };
        var projection = Pose().Projection(1.6f, far.EffectiveNear, far.Far);

        Assert.False(float.IsNaN(projection.M11));
        Assert.Equal(far.Projection(1.6f).M33, projection.M33, 4);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void ADegenerateAspectDoesNotProduceANaNFrustum(float aspect)
    {
        // The panel can ask for a projection before the capture size is known.
        var projection = Pose().Projection(aspect);
        Assert.False(float.IsNaN(projection.M11) || float.IsNaN(projection.M22));
    }

    [Fact]
    public void ANonsenseNearFarPairIsCorrectedRatherThanThrowing()
    {
        // CreatePerspectiveFieldOfView throws when near >= far or near <= 0, and a capture must not die
        // on frame 400 because a setting drifted.
        var projection = Pose().Projection(1.6f, near: 0f, far: 0f);

        Assert.False(float.IsNaN(projection.M11));
        Assert.False(float.IsInfinity(projection.M33));
    }

    [Fact]
    public void FieldOfViewFromTheKeyframeIsWhatReachesTheFrustum()
    {
        // A dolly-zoom is FOV changing along the shot; if the frustum ignored the pose's FOV the effect
        // would simply not exist in the export.
        var wide = Pose(fov: 1.4f).Projection(1.6f, 1f, 200000f);
        var tight = Pose(fov: 0.4f).Projection(1.6f, 1f, 200000f);

        Assert.True(tight.M22 > wide.M22, "a narrower field of view must magnify");
    }
}
