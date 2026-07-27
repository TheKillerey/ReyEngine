using System.Numerics;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M203: a map placement's <c>colorModulate</c> tints the particles it places.
///
/// <para>Measured over the 8 shipping map WADs: 172 of 29,811 placements carry one, <b>none is identity</b>
/// (52 tint-only, 72 alpha-only, 48 both), and every component lies in [0, 1] with none above 1.0 - which is
/// what makes a MULTIPLY the safe reading. 16 of the 29 (bin, system) pairs that use it carried several
/// visibly distinct tints that all rendered identically before this.</para>
///
/// <para>The tint is applied where the instance buffer is filled, which is the one place the billboard,
/// ribbon and mesh paths all read their colour from - so these tests exercise the shared point.</para>
/// </summary>
public class PlacementTintTests
{
    /// <summary>A one-emitter system that emits immediately and lives long enough to sample.</summary>
    private static VfxSystemDefinition System(Vector4 birthColour) => new(
        PathHash: 1, Name: "tint_test", ParticlePath: "",
        Emitters: new[]
        {
            new VfxEmitterDefinition(
                Name: "e",
                Rate: VfxCurveF.Const(50f),
                ParticleLifetime: VfxCurveF.Const(5f),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: false,
                Disabled: false,
                BlendMode: 1,
                BirthScale: VfxCurve3.Const(new Vector3(10f, 10f, 10f)),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(birthColour),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                // a path is required for IsVisual, which is what SetSystem filters on; the file need not exist
                TexturePath: "ASSETS/Test/p.dds",
                TexDiv: new Vector2(1f, 1f),
                NumFrames: 1,
                RandomStartFrame: false,
                IsMeshPrimitive: false),
        });

    private static Vector4 FirstParticleColour(Vector4 birthColour, Vector4? tint)
    {
        var sim = new VfxParticleSimulator(seed: 1234);
        sim.SetSystem(System(birthColour), Matrix4x4.Identity);
        if (tint is { } t) sim.PlacementTint = t;
        sim.Update(0.5f);   // long enough to have emitted

        var e = sim.Emitters.First();
        Assert.True(e.InstanceCount > 0, "no particles were emitted, so the check would be vacuous");
        // instance layout: pos(3), sizeX, sizeY, colour(4) - the colour every primitive path reads back
        return new Vector4(e.Instances[5], e.Instances[6], e.Instances[7], e.Instances[8]);
    }

    [Fact]
    public void WithNoTintTheColourIsUnchanged()
    {
        var c = FirstParticleColour(new Vector4(0.8f, 0.6f, 0.4f, 1f), tint: null);
        Assert.Equal(0.8f, c.X, 3);
        Assert.Equal(0.6f, c.Y, 3);
        Assert.Equal(0.4f, c.Z, 3);
        Assert.Equal(1.0f, c.W, 3);
    }

    [Fact]
    public void ATintMultipliesEveryChannel()
    {
        // a real shipped value: Srs_Ray_Light_Maps_Bloom1 is (1, 0.97, 0.84, 0.6)
        var c = FirstParticleColour(new Vector4(1f, 1f, 1f, 1f), new Vector4(1f, 0.97f, 0.84f, 0.6f));
        Assert.Equal(1.00f, c.X, 3);
        Assert.Equal(0.97f, c.Y, 3);
        Assert.Equal(0.84f, c.Z, 3);
        Assert.Equal(0.60f, c.W, 3);
    }

    [Fact]
    public void AnAlphaOnlyTintLeavesRgbAlone()
    {
        // 72 of the 172 shipped tints are alpha-only, so this is the most common shape
        var c = FirstParticleColour(new Vector4(0.5f, 0.25f, 0.125f, 1f), new Vector4(1f, 1f, 1f, 0.5f));
        Assert.Equal(0.5f, c.X, 3);
        Assert.Equal(0.25f, c.Y, 3);
        Assert.Equal(0.125f, c.Z, 3);
        Assert.Equal(0.5f, c.W, 3);
    }

    [Fact]
    public void TwoPlacementsOfTheSameSystemRenderDifferently()
    {
        // The actual symptom: TFT_Set6_ZaunCity_GlowIdle ships 14 distinct tints that all drew identically.
        var a = FirstParticleColour(Vector4.One, new Vector4(0.651f, 1f, 0.549f, 1f));
        var b = FirstParticleColour(Vector4.One, new Vector4(0.65f, 1f, 0.55f, 0.8f));
        Assert.NotEqual(a, b);
        Assert.Equal(1.0f, a.W, 3);
        Assert.Equal(0.8f, b.W, 3);
    }

    [Fact]
    public void AnIdentityTintIsIndistinguishableFromNone()
    {
        var none = FirstParticleColour(new Vector4(0.3f, 0.4f, 0.5f, 0.6f), tint: null);
        var one = FirstParticleColour(new Vector4(0.3f, 0.4f, 0.5f, 0.6f), Vector4.One);
        Assert.Equal(none, one);
    }
}
