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

    /// <summary>The rotation for a drag of <paramref name="drag"/> pixels since the press: along the ring's
    /// screen tangent when there is one, horizontally otherwise (the pre-M764 mapping).</summary>
    public static float Degrees(Vector2 drag, Vector2? tangent) =>
        (tangent is { } t ? Vector2.Dot(drag, t) : drag.X) * DegreesPerPixel;
}
