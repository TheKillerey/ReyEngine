using System;
using System.Collections.Generic;
using System.Numerics;

namespace ReyEngine.App.Services;

/// <summary>
/// M694: when a placed prop's pose is worth computing this frame. Two rules, shared by both viewports:
///
/// <para><b>Near.</b> A mesh whose every placement is beyond the particle camera gate's distance is
/// not posed at all - nobody can see it breathe. The distance is
/// <see cref="VfxPlaybackSim.MaxDistanceSquared"/>'s, so props and particles fall silent at the same
/// range, and the camera position is the X-mirrored one both viewports already keep for that gate
/// (world space is unmirrored; the flip lives in the view matrix).</para>
///
/// <para><b>Rate.</b> An idle is re-posed at most <see cref="MinInterval"/> apart. A jungle camp
/// breathing at 30 Hz is indistinguishable from 144 Hz, and at 144 Hz the pose was most of the frame.
/// A DRIVEN mesh - the playground actor, which supplies its own clip and time - is posed every frame:
/// its clip changes under it and its walk must not stutter.</para>
/// </summary>
public static class PropAnimationGate
{
    /// <summary>Idle props re-pose no more often than this: 30 Hz.</summary>
    public const float MinInterval = 1f / 30f;

    /// <summary>Is any placement of the mesh within the gate distance of the (mirrored) camera?</summary>
    public static bool AnyNear(IReadOnlyList<Matrix4x4> instances, Vector3 mirroredCamera, float maxDistanceSq)
    {
        if (instances.Count == 0) return false;
        for (int i = 0; i < instances.Count; i++)
        {
            var m = instances[i];
            float dx = m.M41 - mirroredCamera.X, dy = m.M42 - mirroredCamera.Y, dz = m.M43 - mirroredCamera.Z;
            if (dx * dx + dy * dy + dz * dz <= maxDistanceSq) return true;
        }
        return false;
    }

    /// <summary>Pose now? <paramref name="lastPoseTime"/> is the clock reading of the last pose
    /// (negative infinity for never); <paramref name="driven"/> bypasses the rate cap.</summary>
    public static bool ShouldPose(float lastPoseTime, float now, bool driven, bool near)
    {
        if (!near) return false;
        if (driven) return true;
        return float.IsNegativeInfinity(lastPoseTime) || now - lastPoseTime >= MinInterval || now < lastPoseTime;
    }
}
