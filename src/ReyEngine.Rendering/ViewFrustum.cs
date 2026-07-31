using System.Numerics;

namespace ReyEngine.Rendering;

/// <summary>Allocation-free conservative AABB/frustum test for the OpenGL clip convention.</summary>
public readonly struct ViewFrustum
{
    private readonly Vector4 _left, _right, _bottom, _top, _near, _far;

    private ViewFrustum(Vector4 left, Vector4 right, Vector4 bottom, Vector4 top, Vector4 near, Vector4 far)
    {
        _left = Normalize(left); _right = Normalize(right);
        _bottom = Normalize(bottom); _top = Normalize(top);
        _near = Normalize(near); _far = Normalize(far);
    }

    /// <summary>Extract six inward-facing planes from a row-vector world-to-OpenGL-clip matrix.</summary>
    public static ViewFrustum FromOpenGl(Matrix4x4 m) => new(
        new(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41),
        new(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41),
        new(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42),
        new(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42),
        new(m.M14 + m.M13, m.M24 + m.M23, m.M34 + m.M33, m.M44 + m.M43),
        new(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43));

    /// <summary>True unless the box is wholly outside at least one plane. Conservative by design.</summary>
    public bool Intersects(Vector3 min, Vector3 max) =>
        Inside(_left, min, max) && Inside(_right, min, max)
        && Inside(_bottom, min, max) && Inside(_top, min, max)
        && Inside(_near, min, max) && Inside(_far, min, max);

    private static bool Inside(Vector4 p, Vector3 min, Vector3 max)
    {
        float x = p.X >= 0f ? max.X : min.X;
        float y = p.Y >= 0f ? max.Y : min.Y;
        float z = p.Z >= 0f ? max.Z : min.Z;
        return p.X * x + p.Y * y + p.Z * z + p.W >= 0f;
    }

    private static Vector4 Normalize(Vector4 p)
    {
        float len = MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
        return len > 1e-6f ? p / len : p;
    }
}
