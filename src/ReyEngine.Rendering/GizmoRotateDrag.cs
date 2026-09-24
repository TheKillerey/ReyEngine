using System.Numerics;

namespace ReyEngine.Rendering;

/// <summary>
/// M764: how a mouse drag on a rotate ring becomes degrees.
///
/// <para>Until M764 the drag was <c>(mouse.X - press.X) * 0.5</c> for every ring, from every side - a fixed
/// sign that ignored both the camera and the viewport's X mirror (League's world is left-handed, so the map
/// views put Scale(-1,1,1) in front of the view). The mirror reverses the on-screen sense of rotations about
/// Y and Z, which is why dragging the Y ring right turned the object left. X and Z were not consistently
/// flipped but depended on the camera, because a horizontal drag says nothing about which side of the ring
/// is under the cursor.</para>
///
/// <para>The fix is the one Move already gets for free by ray-casting through the mirrored matrix: measure
/// the drag ALONG the ring as it runs on screen at the grabbed point. The grabbed point g (relative to the
/// pivot) moves along <c>cross(axis, g)</c> for a positive angle - true for both the mesh rotation
/// (MapGeoMesh.ScaleRotationMatrix) and the placement rotation (CreateFromYawPitchRoll), measured by the
/// M764 rotate probe on four cameras (36 of 36 grab points follow the mouse, against 0-26 before). Projecting
/// that through the same matrix the picking uses makes the mirror part of the answer rather than a special
/// case. Stored rotations keep their meaning; only the mouse-to-degrees mapping changes.</para>
/// </summary>
public static class GizmoRotateDrag
{
    /// <summary>Degrees per pixel dragged along the ring - the rate the old horizontal mapping used.</summary>
    public const float DegreesPerPixel = 0.5f;

    /// <summary>
    /// The unit screen direction the ring runs at the grabbed point, oriented so a drag along it is a
    /// POSITIVE rotation about <paramref name="axis"/>. Null when the ring is seen edge-on there (or does not
    /// project), in which case the caller falls back to the horizontal drag.
    ///
    /// <para>The grabbed point is the ring point under the cursor, and where the front and back of the ring
    /// overlap on screen (a ring seen nearly edge-on) it is the one NEAREST THE CAMERA - that is the one the
    /// user sees and means, and the back point's screen direction is the reverse of it.</para>
    /// </summary>
    /// <param name="viewProj">The viewport's own (mirrored) view-projection, as the picking uses it.</param>
    public static Vector2? ScreenTangent(Vector3 pivot, Vector3 axis, float radius, Vector2 cursor,
        Matrix4x4 viewProj, float width, float height)
    {
        if (axis.LengthSquared() < 1e-12f || radius <= 0f) return null;
        axis = Vector3.Normalize(axis);
        // the ring's own basis, exactly as ViewportControl builds and hit-tests it
        var u = Vector3.Normalize(MathF.Abs(axis.Y) < 0.99f ? Vector3.Cross(axis, Vector3.UnitY) : Vector3.Cross(axis, Vector3.UnitX));
        var w = Vector3.Cross(axis, u);

        const int N = 256;
        var pts = new (Vector3 g, Vector2 s, float d, float depth)[N];
        int count = 0; float best = float.MaxValue;
        for (int i = 0; i < N; i++)
        {
            float t = i / (float)N * MathF.Tau;
            var g = (u * MathF.Cos(t) + w * MathF.Sin(t)) * radius;
            var clip = Vector4.Transform(new Vector4(pivot + g, 1f), viewProj);
            if (!ViewportPicking.ProjectToScreen(pivot + g, viewProj, width, height, out var s)) continue;
            float d = Vector2.Distance(s, cursor);
            pts[count++] = (g, s, d, clip.W);
            best = MathF.Min(best, d);
        }
        if (count == 0) return null;

        const float TieSlopPx = 2f;   // front and back overlap within a pixel or two; wider swaps in other points of a thin ellipse
        int pick = -1;
        for (int i = 0; i < count; i++)
            if (pts[i].d <= best + TieSlopPx && (pick < 0 || pts[i].depth < pts[pick].depth)) pick = i;
        var (grab, grabScreen, _, _) = pts[pick];

        // a small step in the direction the grabbed point travels for +angle
        if (!ViewportPicking.ProjectToScreen(pivot + grab + Vector3.Cross(axis, grab) * 0.01f, viewProj, width, height, out var ahead))
            return null;
        var tangent = ahead - grabScreen;
        if (tangent.Length() < 1e-4f) return null;   // edge-on at the grab point
        return Vector2.Normalize(tangent);
    }

    /// <summary>How nearly edge-on a ring may be seen (|cos| between the pick ray and the ring's axis) before
    /// its plane stops being a usable target and the drag falls back to <see cref="ScreenTangent"/>.</summary>
    public const float EdgeOnCos = 0.15f;

    /// <summary>
    /// M767: where the pick ray crosses the ring's plane, as an angle (radians) about <paramref name="axis"/>
    /// in the ring's own basis - increasing in the direction a POSITIVE rotation turns. False when the ray
    /// runs along the plane or meets it behind the camera.
    ///
    /// <para>This is the rotate mapping from M767 on. M764 counted only the drag component along the ring's
    /// screen tangent at the press point, frozen for the drag: a drag across a ring's end rotated 0.2-1.7
    /// degrees for 60 px, and following the ring round went up to +55 degrees and back to 0 at the half turn
    /// (measured by the M767 debugger pass on a real Map453 mesh). Accumulating this angle between frames
    /// keeps the grabbed point under the cursor for any number of turns; the pick ray already carries the
    /// viewport's X mirror, so no special case is needed for it.</para>
    /// </summary>
    public static bool RingAngle(Vector3 rayOrigin, Vector3 rayDir, Vector3 pivot, Vector3 axis, out float angle)
    {
        angle = 0f;
        if (axis.LengthSquared() < 1e-12f || rayDir.LengthSquared() < 1e-12f) return false;
        var n = Vector3.Normalize(axis);
        var d = Vector3.Normalize(rayDir);
        float denom = Vector3.Dot(d, n);
        if (MathF.Abs(denom) < 1e-4f) return false;
        float t = Vector3.Dot(pivot - rayOrigin, n) / denom;
        if (t <= 0f) return false;
        var v = rayOrigin + d * t - pivot;
        if (v.LengthSquared() < 1e-8f) return false;
        var u = Vector3.Normalize(MathF.Abs(n.Y) < 0.99f ? Vector3.Cross(n, Vector3.UnitY) : Vector3.Cross(n, Vector3.UnitX));
        var w = Vector3.Cross(n, u);   // u -> w is the direction +angle turns (cross(axis, g) at g = u)
        angle = MathF.Atan2(Vector3.Dot(v, w), Vector3.Dot(v, u));
        return true;
    }

    /// <summary>True when the ring is seen too nearly edge-on for <see cref="RingAngle"/> to be usable.</summary>
    public static bool IsEdgeOn(Vector3 rayDir, Vector3 axis) =>
        axis.LengthSquared() < 1e-12f || rayDir.LengthSquared() < 1e-12f
        || MathF.Abs(Vector3.Dot(Vector3.Normalize(rayDir), Vector3.Normalize(axis))) < EdgeOnCos;

    /// <summary>The change from <paramref name="previous"/> to <paramref name="current"/> (radians), taken the
    /// short way round - what one frame of a ring drag adds.
    ///
    /// <para>The short way is always the right way between two mouse events, however fast: the cursor's straight
    /// screen segment maps to a straight line in the ring's plane (projection keeps lines straight), and a
    /// straight line that misses the pivot sweeps less than 180 degrees around it. Checked in review (M767)
    /// against the same paths drawn a quarter pixel at a time; no sub-sampling is needed.</para></summary>
    public static float AngleStep(float previous, float current)
    {
        float d = current - previous;
        while (d > MathF.PI) d -= MathF.Tau;
        while (d < -MathF.PI) d += MathF.Tau;
        return d;
    }

    /// <summary>The rotation for a drag of <paramref name="drag"/> pixels since the press: along the ring's
    /// screen tangent when there is one, horizontally otherwise (the pre-M764 mapping).</summary>
    public static float Degrees(Vector2 drag, Vector2? tangent) =>
        (tangent is { } t ? Vector2.Dot(drag, t) : drag.X) * DegreesPerPixel;
}
