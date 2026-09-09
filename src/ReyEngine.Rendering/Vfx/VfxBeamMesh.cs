using System.Numerics;

namespace ReyEngine.Rendering.Vfx;

/// <summary>Fit a beam mesh's longitudinal Z interval between its two runtime endpoints.
/// Blitzcrank's cable spans -10.0318..10.0319 in Z and authors scale (50,50,0):
/// the missing longitudinal scale comes from the tether length, not a flat ribbon.</summary>
public static class VfxBeamMesh
{
    public static Matrix4x4 Transform(Vector3 source, Vector3 target, Vector2 zRange, Vector2 thickness)
    {
        var delta = target - source;
        float length = delta.Length(), extent = zRange.Y - zRange.X;
        if (length < 1e-4f || extent < 1e-4f) return Matrix4x4.CreateScale(0f);
        var forward = delta / length;
        var reference = MathF.Abs(forward.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        var rightAxis = Vector3.Normalize(Vector3.Cross(reference, forward));
        var right = rightAxis * thickness.X;
        var up = Vector3.Cross(forward, rightAxis) * thickness.Y;
        var longitudinal = forward * (length / extent);
        var translation = source - longitudinal * zRange.X;
        return new Matrix4x4(right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0, longitudinal.X, longitudinal.Y, longitudinal.Z, 0,
            translation.X, translation.Y, translation.Z, 1);
    }
}
