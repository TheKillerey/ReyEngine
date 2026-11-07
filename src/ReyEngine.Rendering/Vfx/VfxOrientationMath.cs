using System.Numerics;

namespace ReyEngine.Rendering.Vfx;

/// <summary>
/// M881: the orientation maths of LTK Manager's VFX engine (engine/utils/basis.ts), which the user reports renders champion cast
/// particles correctly. Used only by <see cref="VfxParticleSimulator.CharacterSemantics"/>. Every basis is a row-major 3x3
/// <c>float[9]</c> whose COLUMNS are the local axes, so <c>world = basis * local</c> (LTK's convention), and Euler degrees
/// compose as <c>Ry * Rx * Rz</c> (the same composition the renderers' rotateEuler builds from slots 15-17).
/// </summary>
public static class VfxOrientationMath
{
    public const float RotationRate = 60f;   // LTK ROTATION_RATE: rotation0 is authored in degrees per 1/60 s (systemModel.ts:330)
    private const float Degree = MathF.PI / 180f;

    /// <summary>basis.ts standingInto: the basis euler degrees (x, y, z) stand at.</summary>
    public static float[] Standing(float xDeg, float yDeg, float zDeg)
    {
        float cx = MathF.Cos(xDeg * Degree), sx = MathF.Sin(xDeg * Degree);
        float cy = MathF.Cos(yDeg * Degree), sy = MathF.Sin(yDeg * Degree);
        float cz = MathF.Cos(zDeg * Degree), sz = MathF.Sin(zDeg * Degree);
        return new[]
        {
            cy * cz + sy * sx * sz, -cy * sz + sy * sx * cz, sy * cx,
            cx * sz, cx * cz, -sx,
            -sy * cz + cy * sx * sz, sy * sz + cy * sx * cz, cy * cx,
        };
    }

    /// <summary>basis.ts multiplyInto: <c>a * b</c>.</summary>
    public static float[] Multiply(float[] a, float[] b)
    {
        var o = new float[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                o[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        return o;
    }

    public static float[] Identity() => new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

    /// <summary>basis.ts alongInto: local +Z along <paramref name="d"/>, local X = world up crossed into it. Identity for no length.</summary>
    public static float[] Along(Vector3 d)
    {
        float length = d.Length();
        if (length == 0f || !float.IsFinite(length)) return Identity();
        float zx = d.X / length, zy = d.Y / length, zz = d.Z / length;
        float xx = zz, xy = 0f, xz = -zx;
        float across = MathF.Sqrt(xx * xx + xz * xz);
        if (across < 1e-4f) { xx = 1f; xz = 0f; }
        else { xx /= across; xz /= across; }
        return new[]
        {
            xx, zy * xz - zz * xy, zx,
            xy, zz * xx - zx * xz, zy,
            xz, zx * xy - zy * xx, zz,
        };
    }

    /// <summary>basis.ts flightInto: local Y along the travel, local X level and to its left, Z square to both.</summary>
    public static float[] Flight(Vector3 d)
    {
        float across = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        float length = MathF.Sqrt(across * across + d.Y * d.Y);
        if (length == 0f) return new float[] { -1, 0, 0, 0, 0, 1, 0, 1, 0 };
        if (across == 0f && d.Y > 0f) return Identity();
        float dx = d.X / length, dy = d.Y / length, dz = d.Z / length;
        float lx, lz;
        if (across == 0f) { lx = -1f; lz = 0f; } else { lx = -d.Z / across; lz = d.X / across; }
        float ux = -dy * lz, uy = lz * dx - lx * dz, uz = lx * dy;
        return new[] { lx, dx, ux, 0, dy, uy, lz, dz, uz };
    }

    /// <summary>basis.ts yawInto: local +Z is the direction laid flat, up stays the world's.</summary>
    public static float[] Yaw(Vector3 d)
    {
        float length = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        if (length == 0f) return Identity();
        float x = d.X / length, z = d.Z / length;
        return new[] { z, 0, x, 0, 1, 0, -x, 0, z };
    }

    /// <summary>The euler radians (x, y, z) whose <see cref="Standing"/> basis is <paramref name="b"/> - the inverse the renderers need,
    /// because they read a particle's orientation as Euler angles in the emitter's placement frame.</summary>
    public static Vector3 EulerOf(float[] b)
    {
        float sx = Math.Clamp(-b[5], -1f, 1f);
        float cx = MathF.Sqrt(MathF.Max(0f, 1f - sx * sx));
        if (cx < 1e-4f)
            return new Vector3(MathF.Asin(sx), MathF.Atan2(-b[6], b[0]), 0f);   // gimbal: roll folded into yaw
        return new Vector3(MathF.Asin(sx), MathF.Atan2(b[2], b[8]), MathF.Atan2(b[3], b[4]));
    }

    /// <summary>Row-vector System.Numerics matrix of a column-convention basis (row i = the image of local axis i).</summary>
    public static Matrix4x4 ToMatrix(float[] b) => new(
        b[0], b[3], b[6], 0f,
        b[1], b[4], b[7], 0f,
        b[2], b[5], b[8], 0f,
        0f, 0f, 0f, 1f);

    public static Vector3 Apply(float[] b, Vector3 v) => new(
        b[0] * v.X + b[1] * v.Y + b[2] * v.Z,
        b[3] * v.X + b[4] * v.Y + b[5] * v.Z,
        b[6] * v.X + b[7] * v.Y + b[8] * v.Z);

    /// <summary>The rotation half of a host placement as a column basis (normalised axes; identity when degenerate).</summary>
    public static float[] RotationOfPlacement(in Matrix4x4 m)
    {
        static Vector3 N(Vector3 v) => v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : v;
        var x = N(new Vector3(m.M11, m.M12, m.M13));
        var y = N(new Vector3(m.M21, m.M22, m.M23));
        var z = N(new Vector3(m.M31, m.M32, m.M33));
        if (x == Vector3.Zero || y == Vector3.Zero || z == Vector3.Zero) return Identity();
        return new[] { x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z };
    }
}
