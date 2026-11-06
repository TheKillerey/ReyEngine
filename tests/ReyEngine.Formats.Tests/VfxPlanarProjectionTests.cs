using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>M862: a VfxPrimitivePlanarProjection (1,154 emitters) is a decal on the ground, not a quad facing the camera.</summary>
public sealed class VfxPlanarProjectionTests
{
    private static (PreviewVertex[] Verts, int Count) Quad(ParticleQuadBuilder.QuadOrientation orientation, Vector3 position, float spin = 0f)
    {
        var inst = new float[ParticleQuadBuilder.Stride];
        inst[ParticleQuadBuilder.OffPos] = position.X; inst[ParticleQuadBuilder.OffPos + 1] = position.Y; inst[ParticleQuadBuilder.OffPos + 2] = position.Z;
        inst[ParticleQuadBuilder.OffSizeX] = 100f; inst[ParticleQuadBuilder.OffSizeY] = 100f;
        inst[ParticleQuadBuilder.OffRot] = spin;
        for (int i = 0; i < 4; i++) inst[ParticleQuadBuilder.OffColor + i] = 1f;
        var verts = new PreviewVertex[4]; var indices = new uint[6];
        int v = 0, idx = 0;
        // a camera looking along -Z from the front: the billboard basis is the X/Y plane
        ParticleQuadBuilder.Append(inst, 1, verts, ref v, indices, ref idx, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, orientation);
        return (verts, v);
    }

    [Fact]
    public void TheClassIsRecognisedFromThePrimitive()
    {
        var def = new VfxEmitterDefinition("p", VfxCurveF.Const(1), VfxCurveF.Const(1), null, 0, 0, true, false, 1,
            VfxCurve3.Const(Vector3.One), null, VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
            "ASSETS/x.dds", Vector2.One, 1, false, false, PrimitiveClass: HashAlgorithms.Fnv1a("VfxPrimitivePlanarProjection"));
        Assert.True(def.IsPlanarProjection);
        Assert.False((def with { PrimitiveClass = 0 }).IsPlanarProjection);
    }

    [Fact]
    public void AProjectedQuadLiesFlatOnTheGroundWhereABillboardStandsUp()
    {
        var billboard = Quad(default, new Vector3(0, 80, 0));
        Assert.Contains(billboard.Verts.Take(4), v => MathF.Abs(v.Position.Y - 80f) > 10f);   // faces the camera: spread in Y

        var projected = Quad(new ParticleQuadBuilder.QuadOrientation(false, false, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
            Projected: true, ProjectionGroundY: 0f), new Vector3(0, 80, 0));
        Assert.All(projected.Verts.Take(4), v => Assert.Equal(0f, v.Position.Y, 3));   // on the ground plane, whatever the emitter's height
        // and it spans X and Z
        Assert.True(projected.Verts.Take(4).Max(v => v.Position.Z) - projected.Verts.Take(4).Min(v => v.Position.Z) > 90f);
        Assert.True(projected.Verts.Take(4).Max(v => v.Position.X) - projected.Verts.Take(4).Min(v => v.Position.X) > 90f);
    }

    [Fact]
    public void WithoutAGroundThePlacedDecalKeepsItsOwnHeight()
    {
        var q = Quad(new ParticleQuadBuilder.QuadOrientation(false, false, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Projected: true), new Vector3(0, 7, 0));
        Assert.All(q.Verts.Take(4), v => Assert.Equal(7f, v.Position.Y, 3));
    }

    [Fact]
    public void ATiltedPlacementStillLaysTheDecalFlat()
    {
        // a placement turned onto its side: right = +X, forward = +Y - flattened, the forward falls back to +Z
        var q = Quad(new ParticleQuadBuilder.QuadOrientation(false, false, Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY, Projected: true, ProjectionGroundY: 0f), Vector3.Zero);
        Assert.All(q.Verts.Take(4), v => Assert.Equal(0f, v.Position.Y, 3));
    }
}
