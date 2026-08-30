using System.Numerics;
using ReyEngine.Core.Cinematics;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M603: the camera path a cinematic shot follows.
///
/// <para>Every failure mode here is one you only see in the finished video, after a capture has run. A
/// rotation that swings the long way round, a corner where two keyframes meet, a camera that keeps flying
/// past the last keyframe — none of it throws, none of it looks wrong in a static preview, and each costs
/// a re-render to discover. So the properties are asserted rather than eyeballed: the path passes through
/// its keyframes, orientation never takes the long way, and time outside the shot clamps.</para>
/// </summary>
public sealed class CinematicPathTests
{
    private const float Eps = 1e-3f;

    private static CinematicKeyframe Key(float t, Vector3 p, float fov = 1.0f,
        CinematicEase ease = CinematicEase.Linear, float roll = 0f) =>
        new(t, p, Quaternion.Identity, fov, roll, ease);

    private static CinematicShot Shot(params CinematicKeyframe[] keys)
    {
        var shot = new CinematicShot();
        foreach (var k in keys) shot.Add(k);
        return shot;
    }

    // ===================================================== the path goes through the keyframes

    [Fact]
    public void ThePathPassesThroughEveryKeyframe()
    {
        // Catmull-Rom was chosen over Bézier precisely for this: a keyframe is a place the camera
        // visits, not a hint about where it might go.
        var shot = Shot(
            Key(0f, new Vector3(0, 0, 0)),
            Key(1f, new Vector3(100, 50, 0)),
            Key(2f, new Vector3(200, 0, 100)),
            Key(3f, new Vector3(300, 80, 100)));

        foreach (var k in shot.Keyframes)
        {
            var pose = shot.Sample(k.Time);
            Assert.True(Vector3.Distance(pose.Position, k.Position) < Eps,
                $"at t={k.Time} the path is {Vector3.Distance(pose.Position, k.Position):f3} away from its keyframe");
        }
    }

    [Fact]
    public void TwoKeyframesDegradeToAStraightLine()
    {
        // The endpoint-duplication rule has to leave the simplest shot alone. A two-key move that bows
        // is a spline reaching for neighbours that do not exist.
        var shot = Shot(Key(0f, Vector3.Zero), Key(1f, new Vector3(100, 0, 0)));

        for (float u = 0f; u <= 1f; u += 0.125f)
        {
            var p = shot.Sample(u).Position;
            Assert.Equal(0f, p.Y, 3);
            Assert.Equal(0f, p.Z, 3);
            Assert.Equal(100f * u, p.X, 2);
        }
    }

    [Fact]
    public void ThePathIsContinuousWithNoCornerAtAKeyframe()
    {
        // A polyline is C0 but not C1: the direction jumps at each keyframe and reads as a flick. Compare
        // the direction just before and just after an interior keyframe.
        var shot = Shot(
            Key(0f, new Vector3(0, 0, 0)),
            Key(1f, new Vector3(100, 0, 0)),
            Key(2f, new Vector3(100, 0, 100)),
            Key(3f, new Vector3(0, 0, 100)));

        const float h = 0.01f;
        var before = Vector3.Normalize(shot.Sample(1f).Position - shot.Sample(1f - h).Position);
        var after = Vector3.Normalize(shot.Sample(1f + h).Position - shot.Sample(1f).Position);

        // A right-angle polyline would give dot == 0 here.
        Assert.True(Vector3.Dot(before, after) > 0.99f,
            $"direction turns by {MathF.Acos(Vector3.Dot(before, after)) * 180f / MathF.PI:f1}° at the keyframe");
    }

    // ===================================================== orientation

    [Fact]
    public void A180DegreeTurnTakesTheShortWayRound()
    {
        // The reason the spec forbids Euler interpolation. Slerp always takes the shorter arc; component
        // interpolation can take the 359° one, which is a camera that whips the wrong way mid-shot.
        var a = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -2.9f);
        var b = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 2.9f);   // ~0.48 rad apart the short way
        var shot = new CinematicShot();
        shot.Add(new CinematicKeyframe(0f, Vector3.Zero, a, 1f, 0f, CinematicEase.Linear));
        shot.Add(new CinematicKeyframe(1f, Vector3.Zero, b, 1f, 0f, CinematicEase.Linear));

        var mid = shot.Sample(0.5f).Orientation;
        float toA = MathF.Abs(Quaternion.Dot(mid, a));
        float toB = MathF.Abs(Quaternion.Dot(mid, b));

        Assert.True(toA > 0.9f && toB > 0.9f,
            $"halfway is not between the two orientations (dot {toA:f3} / {toB:f3}) - it went the long way");
    }

    [Fact]
    public void OrientationStaysUnitLengthAcrossTheWholeShot()
    {
        // A drifting magnitude turns into a skewed view matrix rather than an error.
        var shot = new CinematicShot();
        shot.Add(new CinematicKeyframe(0f, Vector3.Zero,
            Quaternion.CreateFromYawPitchRoll(0.3f, -0.2f, 0f), 1f));
        shot.Add(new CinematicKeyframe(2f, new Vector3(50, 20, 10),
            Quaternion.CreateFromYawPitchRoll(2.4f, 0.6f, 0f), 1f));

        for (float t = 0f; t <= 2f; t += 0.1f)
            Assert.Equal(1f, shot.Sample(t).Orientation.Length(), 3);
    }

    [Fact]
    public void ALookAtShotAimsAtTheTargetAtEveryInstantNotJustAtKeyframes()
    {
        // Deriving orientation per sample is the whole point of LookAtTarget. Slerping orientations
        // authored at the keyframes would only be correct AT them, and drift in between.
        var target = new Vector3(0, 0, 0);
        var shot = Shot(
            Key(0f, new Vector3(-200, 100, -200)),
            Key(1f, new Vector3(0, 100, -300)),
            Key(2f, new Vector3(200, 100, -200)));
        shot.LookAtTarget = target;

        for (float t = 0f; t <= 2f; t += 0.05f)
        {
            var pose = shot.Sample(t);
            var wanted = Vector3.Normalize(target - pose.Position);
            Assert.True(Vector3.Dot(pose.Forward, wanted) > 0.9999f,
                $"at t={t:f2} the camera is {MathF.Acos(Math.Clamp(Vector3.Dot(pose.Forward, wanted), -1f, 1f)) * 180f / MathF.PI:f2}° off target");
        }
    }

    [Fact]
    public void AimingStraightDownDoesNotProduceNaN()
    {
        // Straight up or down has no unique yaw. The naive cross product with world up is zero there,
        // and the whole view matrix becomes NaN — a black frame with nothing logged.
        var q = CinematicShot.Aim(new Vector3(0, 500, 0), Vector3.Zero);

        Assert.False(float.IsNaN(q.X) || float.IsNaN(q.Y) || float.IsNaN(q.Z) || float.IsNaN(q.W));
        Assert.Equal(1f, q.Length(), 3);
        var forward = Vector3.Transform(-Vector3.UnitZ, q);
        Assert.True(Vector3.Dot(Vector3.Normalize(forward), -Vector3.UnitY) > 0.999f);
    }

    // ===================================================== time

    [Fact]
    public void TimeBeforeAndAfterTheShotClampsInsteadOfExtrapolating()
    {
        // A capture that runs a frame long must repeat the last pose. Extrapolating a Catmull-Rom past
        // its end accelerates away from the path.
        var shot = Shot(Key(0f, Vector3.Zero), Key(1f, new Vector3(100, 0, 0)), Key(2f, new Vector3(200, 0, 0)));

        Assert.Equal(shot.Sample(0f).Position, shot.Sample(-5f).Position);
        Assert.Equal(shot.Sample(2f).Position, shot.Sample(99f).Position);
    }

    [Theory]
    [InlineData(CinematicEase.Linear)]
    [InlineData(CinematicEase.EaseIn)]
    [InlineData(CinematicEase.EaseOut)]
    [InlineData(CinematicEase.EaseInOut)]
    public void EasingChangesSpeedButStillHitsTheKeyframesOnTime(CinematicEase ease)
    {
        // Easing reparameterises time; it must fix both ends or a keyframe stops being a point in time.
        Assert.Equal(0f, CinematicShot.Shape(0f, ease), 5);
        Assert.Equal(1f, CinematicShot.Shape(1f, ease), 5);

        var shot = Shot(Key(0f, Vector3.Zero, ease: ease), Key(1f, new Vector3(100, 0, 0), ease: ease));
        Assert.Equal(0f, shot.Sample(0f).Position.X, 2);
        Assert.Equal(100f, shot.Sample(1f).Position.X, 2);
    }

    [Fact]
    public void EaseInStartsSlowerThanLinearAndEaseOutStartsFaster()
    {
        var linear = Shot(Key(0f, Vector3.Zero), Key(1f, new Vector3(100, 0, 0)));
        var easeIn = Shot(Key(0f, Vector3.Zero, ease: CinematicEase.EaseIn),
                          Key(1f, new Vector3(100, 0, 0), ease: CinematicEase.EaseIn));
        var easeOut = Shot(Key(0f, Vector3.Zero, ease: CinematicEase.EaseOut),
                           Key(1f, new Vector3(100, 0, 0), ease: CinematicEase.EaseOut));

        Assert.True(easeIn.Sample(0.25f).Position.X < linear.Sample(0.25f).Position.X);
        Assert.True(easeOut.Sample(0.25f).Position.X > linear.Sample(0.25f).Position.X);
    }

    [Fact]
    public void SpeedScaleShortensTheShotWithoutMovingAnyKeyframe()
    {
        var shot = Shot(Key(0f, Vector3.Zero), Key(4f, new Vector3(400, 0, 0)));
        Assert.Equal(4f, shot.Duration, 3);

        shot.SpeedScale = 2f;
        Assert.Equal(2f, shot.Duration, 3);
        // Same path, half the time: the end is reached at 2 s and the keyframes are untouched.
        Assert.Equal(400f, shot.Sample(2f).Position.X, 2);
        Assert.Equal(4f, shot.Keyframes[^1].Time, 3);
    }

    [Fact]
    public void KeyframesAreHeldInTimeOrderHoweverTheyAreAdded()
    {
        var shot = Shot(Key(2f, new Vector3(200, 0, 0)), Key(0f, Vector3.Zero), Key(1f, new Vector3(100, 0, 0)));

        Assert.Equal(new[] { 0f, 1f, 2f }, shot.Keyframes.Select(k => k.Time).ToArray());
    }

    // ===================================================== FOV, roll and the view matrix

    [Fact]
    public void FieldOfViewAndRollFollowTheSameEasedParameterAsTheMove()
    {
        // A dolly-zoom is the move and the FOV change in lockstep. On separate clocks it reads as two
        // effects fighting.
        var shot = Shot(
            Key(0f, Vector3.Zero, fov: 1.0f, ease: CinematicEase.Linear, roll: 0f),
            Key(1f, new Vector3(100, 0, 0), fov: 0.5f, ease: CinematicEase.Linear, roll: 0.4f));

        var mid = shot.Sample(0.5f);
        Assert.Equal(50f, mid.Position.X, 1);
        Assert.Equal(0.75f, mid.FieldOfView, 3);
        Assert.Equal(0.2f, mid.Roll, 3);
    }

    [Fact]
    public void RollTiltsTheUpVectorAndNothingElse()
    {
        // Roll cannot come from an orbit camera - it builds its view against a fixed world up. This is
        // why the pose hands the renderer a view matrix rather than camera angles.
        var upright = new CinematicPose(Vector3.Zero, Quaternion.Identity, 1f, 0f);
        var rolled = new CinematicPose(Vector3.Zero, Quaternion.Identity, 1f, MathF.PI / 2f);

        Assert.True(Vector3.Dot(upright.Forward, rolled.Forward) > 0.9999f);   // aim unchanged
        Assert.True(MathF.Abs(Vector3.Dot(upright.Up, rolled.Up)) < 0.01f);    // up turned 90°
        Assert.Equal(1f, rolled.Up.Length(), 3);
    }

    [Fact]
    public void TheViewMatrixLooksWhereThePoseSaysItDoes()
    {
        var pose = new CinematicPose(new Vector3(0, 100, 300), CinematicShot.Aim(new Vector3(0, 100, 300), Vector3.Zero), 1f, 0f);

        // A point at the origin must land on the view axis: x and y ≈ 0, and in front of the camera.
        var seen = Vector3.Transform(Vector3.Zero, pose.ViewMatrix);
        Assert.Equal(0f, seen.X, 2);
        Assert.Equal(0f, seen.Y, 2);
        Assert.True(seen.Z < 0f, "the target should be in front of the camera (-Z in view space)");
    }

    [Fact]
    public void AnEmptyShotSamplesToSomethingUsableRatherThanThrowing()
    {
        // The UI samples while the user is still placing the first keyframe.
        var shot = new CinematicShot();
        var pose = shot.Sample(1.5f);

        Assert.Equal(0f, shot.Duration);
        Assert.Equal(1f, pose.Orientation.Length(), 3);
        Assert.True(pose.FieldOfView > 0f);
        Assert.False(float.IsNaN(pose.ViewMatrix.M11));
    }
}
