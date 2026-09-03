using System.Numerics;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M635: the frame a cast effect is placed in. A spell is aimed - its caster-side system, its missile
/// and its target-side system all have a "toward the target" direction - and until now every one of them
/// was spawned with the identity rotation, so Aatrox's W slammed the ground in the same world direction
/// whatever he was aiming at.
///
/// <para><b>Which local axis is forward is read off the data, and the answer is -Z.</b> The champion MESH
/// faces +Z in its own space (19 of 22 skeletons put the toe bones and the jaw / nose / eye bones at
/// positive Z of the foot and head bones; the controller already turns the model with
/// <c>atan2(dx, dz)</c> on the same convention). The VFX are authored the other way round:
/// <list type="bullet">
///   <item><c>Aatrox_Base_W_Cas</c> puts its explosion, smoke, dust ring and ember burst at
///     Z = -100 of the system origin - the slam that lands in FRONT of him.</item>
///   <item><c>Aatrox_Base_E_Dash2</c> gives its dust a velocity of +2000 along Z - trailing BEHIND the
///     dash - and its distortion streak -200 along Z, leading it.</item>
///   <item>Over twelve champions, missile-system particle velocities point +Z twice as often as -Z (21
///     against 10 among the horizontally authored ones): sparks trail behind a missile.</item>
/// </list>
/// The system-level <c>transform</c> does not supply this - only 1 of 250 spell-family systems authors one
/// at all, and it is a translation - so the convention is the engine's, not the data's. It is one constant
/// here so that if a champion contradicts it on screen, it is one line to revisit.</para>
/// </summary>
public static class VfxCastFrame
{
    /// <summary>The direction a cast system points at its target, in the system's own space.</summary>
    public static readonly Vector3 LocalForward = -Vector3.UnitZ;

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
    /// (Y ignored). The model's own facing is <c>atan2(x, z)</c> because it faces +Z; the VFX face -Z, so
    /// they turn half a revolution further.</summary>
    public static float YawToward(Vector3 direction) =>
        MathF.Atan2(direction.X, direction.Z) + MathF.PI;

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
