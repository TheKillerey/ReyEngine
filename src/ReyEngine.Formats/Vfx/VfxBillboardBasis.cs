using System.Numerics;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// <para>M266: the camera basis a billboarded particle quad is built in, derived from the view matrix the
/// frame will actually be drawn with.</para>
///
/// <para>It lives in Formats because that is the only assembly BOTH <c>ReyEngine.Rendering</c> (OpenGL) and
/// <c>ReyEngine.Rendering.D3D11</c> reference, and the whole point is that there is one implementation. The
/// GL renderer had the only copy; the D3D11 particle path used
/// <c>ParticleQuadBuilder.Basis(normalize(cameraPosition), UnitY)</c> instead, which is an ORIGIN-RELATIVE
/// approximation - correct only for an effect sitting at (0,0,0), which is exactly what the shader preview
/// window previews and exactly what a map placement is not.</para>
/// </summary>
public static class VfxBillboardBasis
{
    /// <summary>
    /// <para>Camera right/up/normal in world space from the MIRROR-INCLUSIVE view matrix's inverse.</para>
    ///
    /// <para>Mirror-inclusive matters: the -X mirror is applied inside the renderer, not by the caller that
    /// owns the camera, so a basis built from the raw camera view is right-handed where the frame is
    /// left-handed and every quad comes out flipped about the vertical axis.</para>
    /// </summary>
    public static (Vector3 Right, Vector3 Up, Vector3 Normal) FromView(Matrix4x4 mirrorInclusiveView)
    {
        // A view matrix that will not invert is degenerate (a zero scale somewhere); identity at least keeps
        // the quads facing world +Z instead of filling the vertex buffer with NaN, which is what M230 cost.
        if (!Matrix4x4.Invert(mirrorInclusiveView, out var inv)) inv = Matrix4x4.Identity;
        return (
            Safe(Vector3.TransformNormal(Vector3.UnitX, inv), Vector3.UnitX),
            Safe(Vector3.TransformNormal(Vector3.UnitY, inv), Vector3.UnitY),
            Safe(Vector3.TransformNormal(Vector3.UnitZ, inv), Vector3.UnitZ));
    }

    private static Vector3 Safe(Vector3 v, Vector3 fallback)
        => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : fallback;
}
