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

    /// <summary>
    /// Screen pixel → world-space ray, derived purely from the inverse of the SAME matrix used to
    /// render (two unprojected points at different depths). No separate camera-position input, so it
    /// stays correct regardless of extra transforms baked into the viewProj (e.g. the League -X mirror).
    /// </summary>
    public static bool TryGetRay(Vector2 screen, Matrix4x4 viewProj, double width, double height,
        out Vector3 rayOrigin, out Vector3 rayDir)
    {
        rayOrigin = default; rayDir = Vector3.UnitZ;
        if (width <= 0 || height <= 0 || !Matrix4x4.Invert(viewProj, out var inv)) return false;

        float ndcX = (float)(screen.X / width) * 2f - 1f;
        float ndcY = 1f - (float)(screen.Y / height) * 2f;

        if (!Unproject(new Vector4(ndcX, ndcY, 0.05f, 1f), inv, out var nearPoint)) return false;
        if (!Unproject(new Vector4(ndcX, ndcY, 0.95f, 1f), inv, out var farPoint)) return false;

        var dir = farPoint - nearPoint;
        if (dir.LengthSquared() < 1e-12f) return false;
        rayOrigin = nearPoint;
        rayDir = Vector3.Normalize(dir);
        return true;
    }

    private static bool Unproject(Vector4 ndc, Matrix4x4 invViewProj, out Vector3 world)
    {
        var h = Vector4.Transform(ndc, invViewProj);
        if (MathF.Abs(h.W) < 1e-8f) { world = default; return false; }
        world = new Vector3(h.X, h.Y, h.Z) / h.W;
        return true;
    }

    /// <summary>
    /// Möller–Trumbore ray/triangle intersection. Returns the ray parameter t (world units along
    /// <paramref name="rayDir"/> if it is unit length), or null on miss. Backfaces count as hits so
    /// picking works regardless of winding (map meshes render with culling disabled anyway).
    /// </summary>
    public static float? RayTriangle(Vector3 rayOrigin, Vector3 rayDir, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        const float eps = 1e-7f;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var p = Vector3.Cross(rayDir, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < eps) return null;
        float invDet = 1f / det;
        var s = rayOrigin - v0;
        float u = Vector3.Dot(s, p) * invDet;
        if (u < 0f || u > 1f) return null;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(rayDir, q) * invDet;
        if (v < 0f || u + v > 1f) return null;
        float t = Vector3.Dot(e2, q) * invDet;
        return t > eps ? t : null;
    }

    /// <summary>Ray/AABB slab test. Returns entry distance t (0 if the origin is inside), or null on miss.</summary>
    public static float? RayAabb(Vector3 rayOrigin, Vector3 rayDir, Vector3 min, Vector3 max)
    {
        float tMin = 0f, tMax = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float o = i == 0 ? rayOrigin.X : i == 1 ? rayOrigin.Y : rayOrigin.Z;
            float d = i == 0 ? rayDir.X : i == 1 ? rayDir.Y : rayDir.Z;
            float lo = i == 0 ? min.X : i == 1 ? min.Y : min.Z;
            float hi = i == 0 ? max.X : i == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < lo || o > hi) return null;
                continue;
            }
            float inv = 1f / d;
            float t1 = (lo - o) * inv, t2 = (hi - o) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax) return null;
        }
        return tMin;
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
