using System.Numerics;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// Shared League world frame for aimed spells. Champion models and spell systems use +Z forward.
/// Verified from the complete authored spawn offset, not an emitter position in isolation:
/// Aatrox Q1/Q2/Q3 indicators spawn at shape Z=375/250/200, in front of the caster.
/// Negative W cast offsets describe that emitter's placement, not the system's forward axis.
/// The renderer's handedness mirror belongs in the view, never in this world-space aim.
/// </summary>
public static class VfxCastFrame
{
    /// <summary>The direction a cast system points at its target, in the system's own space.</summary>
    public static readonly Vector3 LocalForward = Vector3.UnitZ;

    /// <summary>A placement at <paramref name="at"/> whose <see cref="LocalForward"/> points along the
    /// ground from <paramref name="from"/> to <paramref name="to"/>. Height is ignored - spells are aimed
    /// across the ground - and two coincident points give a translation with no rotation, because there is
    /// no direction to face and spinning the effect to some default would be inventing one.</summary>
    public static Matrix4x4 Toward(Vector3 from, Vector3 to, Vector3 at)
    {
        var d = new Vector3(to.X - from.X, 0f, to.Z - from.Z);
        if (d.LengthSquared() < 1e-6f) return Matrix4x4.CreateTranslation(at);
        return Matrix4x4.CreateRotationY(YawToward(d)) * Matrix4x4.CreateTranslation(at);
    }

    /// <summary>The yaw about Y that turns <see cref="LocalForward"/> onto <paramref name="direction"/>
    /// (Y ignored), the same <c>atan2(x, z)</c> used by the character controller.</summary>
    public static float YawToward(Vector3 direction) =>
        MathF.Atan2(direction.X, direction.Z);

    /// <summary>The rotation half of a placement, with its translation removed - what a travelling missile
    /// has to keep while its position is re-issued every frame. Both viewports rebuilt the matrix from the
    /// lerped position alone, which threw the aim away on the first tick of flight.</summary>
    public static Matrix4x4 RotationOf(Matrix4x4 placement)
    {
        placement.M41 = 0f; placement.M42 = 0f; placement.M43 = 0f;
        return placement;
    }

    /// <summary>Where <see cref="LocalForward"/> ends up in the world under this placement - the check the
    /// tests make, and what a host can log.</summary>
    public static Vector3 WorldForward(Matrix4x4 placement) =>
        Vector3.Normalize(Vector3.TransformNormal(LocalForward, placement));
}
