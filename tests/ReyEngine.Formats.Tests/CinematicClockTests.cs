using System;
using System.IO;
using System.Linq;
using ReyEngine.App.Services;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M736: a capture walks the SHOT's clock while the live viewport it shares a surface with walks the wall
/// clock, and the two were writing to the same per-subsystem state.
///
/// <para>RenderFrameAsync asks for a live frame on purpose, so the viewport shows the shot going by. Each
/// of those live frames handed the particle step a delta of (wall clock - shot time): the simulator clamps
/// that to its 0.1 s ceiling and then applies it, so a captured frame advanced ~0.1 s instead of 1/60 -
/// particles about six times too fast at 60 fps. The same crossing stamped the props' LastPoseTime with
/// wall-clock values, so two captured frames in a row failed the 30 Hz gate (1/60 &lt; 1/30) and the second
/// repeated the first one's pose, which is the props coming out too slow.</para>
/// </summary>
public sealed class CinematicClockTests
{
    /// <summary>An exported frame poses whatever the rate cap would have said. The cap is there to spend a
    /// live frame's budget; an export has no such budget and wants one pose per frame it writes.</summary>
    [Fact]
    public void ACapturedFrameAlwaysPosesAndTheLiveCapStillHolds()
    {
        // inside the 30 Hz window: the live viewport skips, the capture does not
        Assert.False(PropAnimationGate.ShouldPose(1f, 1.01f, driven: false, near: true));
        Assert.True(PropAnimationGate.ShouldPose(1f, 1.01f, driven: false, near: true, poseEveryFrame: true));

        // 60 fps steps, which is exactly the case that came out slow
        Assert.False(PropAnimationGate.ShouldPose(1f, 1f + 1f / 60f, driven: false, near: true));
        Assert.True(PropAnimationGate.ShouldPose(1f, 1f + 1f / 60f, driven: false, near: true, poseEveryFrame: true));

        // distance still wins - a prop nowhere near the shot is not worth posing even for an export
        Assert.False(PropAnimationGate.ShouldPose(1f, 2f, driven: false, near: false, poseEveryFrame: true));

        // and nothing about the live path changed
        Assert.True(PropAnimationGate.ShouldPose(1f, 1.04f, driven: false, near: true));
        Assert.True(PropAnimationGate.ShouldPose(float.NegativeInfinity, 1f, driven: false, near: true));
    }

    [Fact]
    public void TheCaptureStepsOnTheShotsClockAndTheLiveFrameStepsOnNothing()
    {
        var surface = Source("src", "ReyEngine.App", "Views", "Dx11ViewportSurface.cs");
        var props = Source("src", "ReyEngine.App", "Services", "D3D11MapProps.cs");
        if (surface is null || props is null) return;

        // the capture keeps its own clock, reset when it begins and never touched by a live frame
        Assert.Contains("private float _captureLastTime = -1f;", surface);
        Assert.Contains("float delta = _captureLastTime < 0f ? 0f : MathF.Max(0f, timeSeconds - _captureLastTime);", surface);
        // a captured frame steps by that delta; a live frame drawn during a capture steps by nothing
        Assert.Contains("float particleDt = _capture is { } cap ? cap.Delta : (liveFrameDuringCapture ? 0f : ParticleDelta(t));", surface);
        Assert.Contains("bool liveFrameDuringCapture = _capturing && _capture is null;", surface);
        // and only the captured frames move the props, every one of them
        Assert.Contains("PlayPropAnimations && !liveFrameDuringCapture", surface);
        Assert.Contains("poseEveryFrame: _capture is not null", surface);
        Assert.Contains("bool poseEveryFrame = false)", props);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }
}
