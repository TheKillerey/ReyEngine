using System.Numerics;

namespace ReyEngine.Rendering;

/// <summary>
/// Pure screen-space picking math for the viewport's translate gizmo: project a world point to
/// screen pixels, cast a ray from a screen pixel back into the world, and find where that ray
/// passes closest to a world-space axis line. No Avalonia/GL dependency so it can be unit-tested
/// in isolation from the render loop.
/// </summary>
public static class ViewportPicking
{
    /// <summary>World point → device-independent screen pixel, using the same viewProj as rendering.
    /// False if the point is behind the camera (degenerate for picking).</summary>
    public static bool ProjectToScreen(Vector3 world, Matrix4x4 viewProj, double width, double height, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), viewProj);
        if (clip.W <= 1e-5f) { screen = default; return false; }
        float ndcX = clip.X / clip.W, ndcY = clip.Y / clip.W;
        screen = new Vector2((float)((ndcX * 0.5 + 0.5) * width), (float)((1 - (ndcY * 0.5 + 0.5)) * height));
        return true;
    }

    /// <summary>Screen pixel → world-space ray (origin = camera eye, for a perspective camera all
    /// picking rays emanate from there; direction reconstructed via the inverse view-projection).</summary>
    public static bool TryGetRay(Vector2 screen, Matrix4x4 viewProj, double width, double height, Vector3 camPos,
        out Vector3 rayOrigin, out Vector3 rayDir)
    {
        rayOrigin = camPos; rayDir = Vector3.UnitZ;
        if (width <= 0 || height <= 0 || !Matrix4x4.Invert(viewProj, out var inv)) return false;

        float ndcX = (float)(screen.X / width) * 2f - 1f;
        float ndcY = 1f - (float)(screen.Y / height) * 2f;
        var midH = Vector4.Transform(new Vector4(ndcX, ndcY, 0.5f, 1f), inv);
        if (MathF.Abs(midH.W) < 1e-8f) return false;
        var midPoint = new Vector3(midH.X, midH.Y, midH.Z) / midH.W;

        var dir = midPoint - camPos;
        if (dir.LengthSquared() < 1e-12f) return false;
        rayOrigin = camPos;
        rayDir = Vector3.Normalize(dir);
        return true;
    }

    /// <summary>
    /// The parameter t (world units from <paramref name="linePoint"/> along <paramref name="lineDir"/>)
    /// of the point on the infinite line closest to the given ray — i.e. where a mouse ray "touches"
    /// a gizmo axis. <paramref name="lineDir"/> must be a unit vector. Standard closest-point-between-
    /// two-lines solution (Ericson, Real-Time Collision Detection §5.1.8), specialized for line vs ray
    /// with a fallback for the (rare) near-parallel case.
    /// </summary>
    public static float ClosestParameterOnLine(Vector3 rayOrigin, Vector3 rayDir, Vector3 linePoint, Vector3 lineDir)
    {
        // L1 = ray: rayOrigin + s*rayDir (D1=rayDir) — L2 = axis: linePoint + t*lineDir (D2=lineDir)
        Vector3 r = rayOrigin - linePoint;
        float a = Vector3.Dot(rayDir, rayDir);   // ~1
        float e = Vector3.Dot(lineDir, lineDir); // ~1
        float b = Vector3.Dot(rayDir, lineDir);
        float c = Vector3.Dot(rayDir, r);
        float f = Vector3.Dot(lineDir, r);
        float denom = a * e - b * b;

        if (MathF.Abs(denom) < 1e-8f)
        {
            // Ray parallel to axis on screen (looking straight down it) — project r onto lineDir instead.
            return -f / e;
        }
        return (a * f - b * c) / denom;
    }
}
