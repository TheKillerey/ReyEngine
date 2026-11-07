using System.Numerics;
using ReyEngine.Rendering.Vfx;
using Xunit;

namespace ReyEngine.Formats.Tests;

public class EmitterOrientationTests
{
    private static readonly Matrix4x4 Tilted =
        Matrix4x4.CreateFromYawPitchRoll(0.7f, 0.5f, 0.2f) * Matrix4x4.CreateTranslation(10, 20, 30);

    [Fact]
    public void NonLocalEmitterKeepsWorldAxesWhenHonored()
    {
        var (r, u, f) = VfxEmitterOrientation.Basis(Tilted, isLocalOrientation: false, honorNonLocal: true);
        Assert.Equal(Vector3.UnitX, r);
        Assert.Equal(Vector3.UnitY, u);
        Assert.Equal(Vector3.UnitZ, f);
    }

    [Fact]
    public void LocalEmitterFollowsTheHost()
    {
        var (r, u, f) = VfxEmitterOrientation.Basis(Tilted, true, true);
        Assert.NotEqual(Vector3.UnitX, r);
        Assert.True(Vector3.Distance(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, Tilted)), f) < 1e-5f);
        Assert.True(MathF.Abs(Vector3.Dot(r, u)) < 1e-5f);
    }

    [Fact]
    public void HostThatDoesNotHonorNonLocalIsUnchanged()
    {
        var a = VfxEmitterOrientation.Basis(Tilted, false, false);
        var b = VfxEmitterOrientation.Basis(Tilted, true, false);
        Assert.Equal(a, b);
    }
}
