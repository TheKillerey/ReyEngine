using System.Numerics;

namespace ReyEngine.Core.Rendering;

/// <summary>
/// M657/M658: how big a world-space thing is on screen, and how big it should be to occupy a wanted
/// number of pixels.
///
/// <para>Every editor overlay that is FURNITURE rather than content wants this: a placement marker, a
/// transform gizmo, a handle. They are drawn in world space so they sit where the thing they describe
/// sits, but their SIZE should be a screen quantity - a world-sized handle is unusable at one end of the
/// camera's range and invisible at the other.</para>
///
/// <para>Measured as two clip-space points rather than from a projection term, so it needs nothing but
/// the view-projection and the viewport height, works for any projection, and gives both backends the
/// same answer: D3D11 calls this on the CPU, the OpenGL marker shader does the same arithmetic per
/// vertex. Two backends that size differently is a bug that only ever shows up as "it looks wrong in
/// the other renderer".</para>
/// </summary>
public static class ScreenSize
{
    /// <summary>
    /// How many pixels tall a <paramref name="worldSize"/>-long offset along <paramref name="cameraUp"/>
    /// appears at <paramref name="position"/>. Zero when it cannot be measured - at or behind the eye, or
    /// with no viewport - which every caller treats as "leave the size alone".
    /// </summary>
    public static float MeasurePixels(Vector3 position, Vector3 cameraUp, float worldSize,
        Matrix4x4 viewProjection, float viewportHeightPx)
    {
        if (viewportHeightPx <= 0f || worldSize <= 0f) return 0f;
        var c0 = Vector4.Transform(new Vector4(position, 1f), viewProjection);
        var c1 = Vector4.Transform(new Vector4(position + cameraUp * worldSize, 1f), viewProjection);
        if (c0.W <= 1e-4f || c1.W <= 1e-4f) return 0f;
        return MathF.Abs(c1.Y / c1.W - c0.Y / c0.W) * 0.5f * viewportHeightPx;
    }

    /// <summary>
    /// The world size that draws <paramref name="targetPixels"/> tall at <paramref name="position"/> -
    /// a thing that keeps exactly its screen size however far away it is. Zero when it cannot be
    /// measured, so the caller can fall back rather than draw something of size NaN.
    /// </summary>
    public static float WorldSizeForPixels(Vector3 position, Vector3 cameraUp, float targetPixels,
        Matrix4x4 viewProjection, float viewportHeightPx)
    {
        if (targetPixels <= 0f) return 0f;
        // One world unit as a pixel count, then scale it up to the size wanted. Probing with 1 rather
        // than with the answer avoids the circularity of measuring a size to decide that size.
        float perUnit = MeasurePixels(position, cameraUp, 1f, viewProjection, viewportHeightPx);
        return perUnit <= 1e-6f ? 0f : targetPixels / perUnit;
    }

    /// <summary>
    /// <paramref name="worldSize"/>, shrunk if it would draw taller than <paramref name="maxPixels"/>.
    /// Never enlarges: this is for something that is world-sized on purpose and only needs a ceiling.
    /// </summary>
    public static float CapAtPixels(Vector3 position, Vector3 cameraUp, float worldSize,
        Matrix4x4 viewProjection, float viewportHeightPx, float maxPixels)
    {
        if (maxPixels <= 0f) return worldSize;
        float px = MeasurePixels(position, cameraUp, worldSize, viewProjection, viewportHeightPx);
        return px > maxPixels ? worldSize * (maxPixels / px) : worldSize;
    }
}
