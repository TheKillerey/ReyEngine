using System.Numerics;

namespace ReyEngine.Rendering.Vfx;

/// <summary>
/// M877: the orientation basis an emitter's quads and meshes are drawn in. Shared by both backends (they read
/// <see cref="VfxParticleSimulator.EmitterState.PlacementRight"/> and friends), so GL and D3D11 agree.
/// <c>isLocalOrientation</c> (declared default true, written false on 162,164 emitters): false means the particle's
/// orientation ignores the host's rotation while its position still follows the host.
/// <c>rotationOverride</c>, the system <c>transform</c>, <c>hasPostRotateOrientation</c> and <c>directionVelocityScale</c>
/// are NOT applied on this path: their meaning could not be established from evidence. M881: character playback does not come
/// through here for its frame - see <see cref="VfxParticleSimulator.CharacterSemantics"/> (LTK Manager's rule: a non-local emitter
/// removes only the host rotation, so its rotationOverride and translation survive).
/// </summary>
public static class VfxEmitterOrientation
{
    public static (Vector3 Right, Vector3 Up, Vector3 Forward) Basis(in Matrix4x4 host, bool isLocalOrientation, bool honorNonLocal)
    {
        if (honorNonLocal && !isLocalOrientation)
            return (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        return (Axis(Vector3.UnitX, host), Axis(Vector3.UnitY, host), Axis(Vector3.UnitZ, host));
    }

    private static Vector3 Axis(Vector3 axis, in Matrix4x4 m)
    {
        var v = Vector3.TransformNormal(axis, m);
        return v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : axis;
    }
}
