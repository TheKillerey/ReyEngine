using System.Numerics;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M266: the billboard basis both viewports build their particle quads in.
///
/// <para>The point of these tests is not that the shared helper produces "a basis" - it is to pin down
/// exactly WHERE the old D3D11 approximation was right and where it was wrong, so nobody simplifies it back.
/// <c>ParticleQuadBuilder.Basis(normalize(cameraPosition), UnitY)</c> is correct for an effect at the world
/// origin viewed by an unrolled, unmirrored camera - which is precisely the shader preview window and
/// precisely not the map viewport, where every placement is off-origin and the -X mirror is applied inside
/// the renderer.</para>
/// </summary>
public class VfxBillboardBasisTests
{
    private static void AssertClose(Vector3 expected, Vector3 actual, string what)
        => Assert.True((expected - actual).Length() < 1e-5f, $"{what}: expected {expected}, got {actual}");

    [Fact]
    public void MatchesQuadBuilderBasisForAnUnrolledCamera()
    {
        var eye = new Vector3(1200f, 800f, -400f);
        var target = Vector3.Zero;
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);

        var (right, up, normal) = VfxBillboardBasis.FromView(view);
        var (bRight, bUp, bNormal) = ParticleQuadBuilder.Basis(Vector3.Normalize(eye - target), Vector3.UnitY);

        AssertClose(bRight, right, "right");
        AssertClose(bUp, up, "up");
        AssertClose(bNormal, normal, "normal");

        // and it really is a basis, not two coincidentally-equal vectors
        Assert.True(MathF.Abs(Vector3.Dot(right, up)) < 1e-5f, "right and up are not orthogonal");
        AssertClose(normal, Vector3.Cross(right, up), "right x up");
    }

    [Fact]
    public void DivergesForAMirroredView()
    {
        // The -X mirror is applied INSIDE ShaderPreviewRenderer.RenderFrame, so a caller that derives its
        // basis from the raw camera view misses it and every quad comes out flipped about the screen's
        // vertical axis. The mirror-inclusive view is the only thing that knows.
        var eye = new Vector3(1200f, 800f, -400f);
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        var mirrored = Matrix4x4.CreateScale(-1f, 1f, 1f) * view;

        var (plainRight, _, _) = VfxBillboardBasis.FromView(view);
        var (mirroredRight, _, _) = VfxBillboardBasis.FromView(mirrored);

        Assert.True((plainRight - mirroredRight).Length() > 0.1f,
            "the mirrored view produced the same screen-right axis as the unmirrored one, so the mirror is "
            + "not reaching the basis");
        // The flip is exactly the X negation the mirror applies, nothing else.
        AssertClose(new Vector3(-plainRight.X, plainRight.Y, plainRight.Z), mirroredRight, "mirrored right");
    }

    [Fact]
    public void DivergesFromTheOriginRelativeApproximationOffOrigin()
    {
        // ShaderPreviewRenderer.CameraForward is literally Normalize(cameraPosition) - "the unit vector from
        // the ORIGIN toward the camera". A map placement is thousands of units from the origin, at which
        // point that vector has no relation to the direction the camera is actually looking.
        var target = new Vector3(-9000f, 100f, 4200f);
        var eye = target + new Vector3(1200f, 800f, -400f);
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);

        var (_, _, normal) = VfxBillboardBasis.FromView(view);
        var (_, _, approxNormal) = ParticleQuadBuilder.Basis(Vector3.Normalize(eye), Vector3.UnitY);

        Assert.True((normal - approxNormal).Length() > 0.5f,
            "the origin-relative approximation agreed with the real view direction for an off-origin "
            + "camera, which would mean this test is not exercising the case it claims to");
    }

    [Fact]
    public void ADegenerateViewFallsBackToWorldAxesRatherThanNaN()
    {
        // M230 cost a milestone to a NaN reaching a vertex position. A view matrix that will not invert is
        // junk input, but it must degrade to a visible quad, not to geometry that vanishes.
        var (right, up, normal) = VfxBillboardBasis.FromView(new Matrix4x4());

        AssertClose(Vector3.UnitX, right, "right");
        AssertClose(Vector3.UnitY, up, "up");
        AssertClose(Vector3.UnitZ, normal, "normal");
    }
}
