using System.Numerics;

namespace ReyEngine.Rendering;

/// <summary>How an object stores its extra rotation as three Euler angles (degrees), in row-vector terms.</summary>
public enum GizmoEulerOrder
{
    /// <summary>Map meshes (<c>MapGeoMesh.ScaleRotationMatrix</c>): Rx * Ry * Rz - X first, then Y, then Z.</summary>
    Xyz,
    /// <summary>Placements and added meshes: <c>Matrix4x4.CreateFromYawPitchRoll(Y, X, Z)</c> = Rz * Rx * Ry -
    /// roll about Z first, then pitch about X, then yaw about Y.</summary>
    YawPitchRoll,
}

/// <summary>
/// M766: a rotate ring turns the object about exactly the axis it draws.
///
/// <para>Before M766 a ring drag added its degrees to one Euler component. That is a rotation about the drawn
/// axis only when the other two components are zero. A mesh composes X, then Y, then Z, so only its Z ring
/// turned about world Z; the X ring turned about the object's own X and the Y ring about an axis in between.
/// Placements apply their angles INSIDE their authored frame (v * Delta * Authored), so on an authored-rotated
/// particle or prop no ring matched the world axis it showed. And Local mode drew a mesh's axes with the
/// placement convention.</para>
///
/// <para>The way Unreal and Unity do it: with world orientation W = E(e) * B (E the Euler composition, B the
/// authored rotation or identity), turning by theta about a world axis d is W' = W * Rot(d, theta), so
/// E(e') = E(e) * B * Rot(d, theta) * B^-1, decomposed back into the object's own convention. Of the two Euler
/// solutions, the one nearest the drag-start angles is kept, and each angle is unwrapped towards its start
/// value, so the inspector's numbers move smoothly instead of jumping by 180 or 360. Stored values keep their
/// meaning: only which angles a drag produces changes.</para>
/// </summary>
public static class GizmoRotation
{
    private const float D2R = MathF.PI / 180f, R2D = 180f / MathF.PI;

    /// <summary>The rotation matrix for <paramref name="degrees"/> in <paramref name="order"/>.</summary>
    public static Matrix4x4 Compose(Vector3 degrees, GizmoEulerOrder order)
    {
        var r = degrees * D2R;
        return order == GizmoEulerOrder.Xyz
            ? Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y) * Matrix4x4.CreateRotationZ(r.Z)
            : Matrix4x4.CreateFromYawPitchRoll(r.Y, r.X, r.Z);
    }

    /// <summary>Euler angles (degrees) that compose to the rotation part of <paramref name="m"/>, choosing
    /// among the equivalent solutions the one nearest <paramref name="near"/>.</summary>
    public static Vector3 Decompose(Matrix4x4 m, GizmoEulerOrder order, Vector3 near)
    {
        var n = near * D2R;
        Vector3 a, b;
        if (order == GizmoEulerOrder.Xyz)
        {
            // Rx*Ry*Rz: M13 = -sy, M23 = sx*cy, M33 = cx*cy, M12 = cy*sz, M11 = cy*cz
            float sy = Math.Clamp(-m.M13, -1f, 1f);
            float y = MathF.Asin(sy);
            if (MathF.Abs(sy) > 0.99999f)
            {
                // gimbal lock: X and Z turn about the same axis; keep X where it was and solve Z
                float x = n.X;
                float z = sy > 0 ? x - MathF.Atan2(m.M21, m.M22) : MathF.Atan2(-m.M21, m.M22) - x;
                a = b = new Vector3(x, y, z);
            }
            else
            {
                a = new Vector3(MathF.Atan2(m.M23, m.M33), y, MathF.Atan2(m.M12, m.M11));
                b = new Vector3(a.X + MathF.PI, MathF.PI - y, a.Z + MathF.PI);
            }
        }
        else
        {
            // Rz*Rx*Ry: M32 = -sx, M31 = cx*sy, M33 = cx*cy, M12 = sz*cx, M22 = cz*cx
            float sx = Math.Clamp(-m.M32, -1f, 1f);
            float x = MathF.Asin(sx);
            if (MathF.Abs(sx) > 0.99999f)
            {
                // gimbal lock: yaw and roll turn about the same axis; keep yaw where it was and solve roll
                float y = n.Y;
                float z = sx > 0 ? y + MathF.Atan2(m.M13, m.M11) : MathF.Atan2(-m.M13, m.M11) - y;
                a = b = new Vector3(x, y, z);
            }
            else
            {
                a = new Vector3(x, MathF.Atan2(m.M31, m.M33), MathF.Atan2(m.M12, m.M22));
                b = new Vector3(MathF.PI - x, a.Y + MathF.PI, a.Z + MathF.PI);
            }
        }
        a = Unwrap(a, n); b = Unwrap(b, n);
        return (Vector3.DistanceSquared(a, n) <= Vector3.DistanceSquared(b, n) ? a : b) * R2D;
    }

    /// <summary>The Euler angles after turning <paramref name="startDegrees"/> by <paramref name="angleDegrees"/>
    /// about the WORLD direction <paramref name="worldAxis"/>. <paramref name="baseRotation"/> is the rotation the
    /// angles sit inside (a placement's authored rotation) - identity for meshes and added meshes; any scale or
    /// translation in it is ignored. <paramref name="near"/> picks among equivalent angle sets: a drag passes the
    /// angles it wrote last frame, so a long drag never jumps; it defaults to the start.
    ///
    /// <para>Exact for any authored rotation, mirrored or not, with a UNIFORM authored scale. A non-uniform
    /// authored scale sits between these angles and the authored rotation (v * E * S * R), so no choice of
    /// angles turns the rendered object rigidly about an arbitrary world axis there; the drag then turns it
    /// in its own unscaled frame, which is the nearest the stored form can express.</para></summary>
    public static Vector3 RotateAbout(Vector3 startDegrees, GizmoEulerOrder order, Matrix4x4 baseRotation,
        Vector3 worldAxis, float angleDegrees, Vector3? near = null)
    {
        if (worldAxis.LengthSquared() < 1e-12f || angleDegrees == 0f) return startDegrees;
        var b = RotationOnly(baseRotation);
        Matrix4x4.Invert(b, out var bInv);   // orthonormal: always invertible
        var turn = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(worldAxis), angleDegrees * D2R);
        var e = Compose(startDegrees, order) * b * turn * bInv;
        return Decompose(e, order, near ?? startDegrees);
    }

    /// <summary>The object's own X, Y and Z directions in world space - what Local mode draws.</summary>
    public static (Vector3 X, Vector3 Y, Vector3 Z) LocalAxes(Vector3 degrees, GizmoEulerOrder order, Matrix4x4 baseRotation)
    {
        var w = Compose(degrees, order) * RotationOnly(baseRotation);
        return (Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, w)),
                Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, w)),
                Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, w)));
    }

    /// <summary>The orthonormal frame of an affine transform: translation and scale dropped, rows
    /// orthogonalised against authoring drift - and handedness KEPT. A placement authored with one axis
    /// mirrored renders mirrored, so its frame is a reflection (determinant -1); rebuilding it right-handed
    /// would draw its Local rings, and turn it, about axes it does not have (found in review, M766). The
    /// maths stays exact: conjugating a rotation by a reflection is still a rotation.</summary>
    public static Matrix4x4 RotationOnly(Matrix4x4 m)
    {
        var x = new Vector3(m.M11, m.M12, m.M13);
        var y = new Vector3(m.M21, m.M22, m.M23);
        var zRow = new Vector3(m.M31, m.M32, m.M33);
        if (x.LengthSquared() < 1e-12f || y.LengthSquared() < 1e-12f) return Matrix4x4.Identity;
        x = Vector3.Normalize(x);
        y = Vector3.Normalize(y - x * Vector3.Dot(y, x));   // Gram-Schmidt against authoring drift
        var z = Vector3.Cross(x, y);
        if (Vector3.Dot(z, zRow) < 0f) z = -z;              // the authored handedness, mirror included
        return new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f);
    }

    private static Vector3 Unwrap(Vector3 v, Vector3 near) => new(Nearest(v.X, near.X), Nearest(v.Y, near.Y), Nearest(v.Z, near.Z));

    private static float Nearest(float angle, float near) =>
        angle + MathF.Round((near - angle) / MathF.Tau) * MathF.Tau;
}
