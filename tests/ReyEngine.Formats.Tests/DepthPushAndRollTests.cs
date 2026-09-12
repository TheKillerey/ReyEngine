using System.Numerics;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M714, the fourth ltk-manager reading ported - and the first one where we already had the answer.
///
/// <para>The reading says the quad vertex shader computes the depth push as
/// <c>P + normalize(P - vCamera) * k</c>, per corner along its own ray, with a positive k pushing away
/// from the eye. That is exactly what M175 decoded out of <c>quad_vs</c> instructions 12-16 and shipped,
/// before the reference renderer got there - so half of this milestone is a no-op and says so.</para>
///
/// <para>What it did find is two disagreements of our own: the OpenGL mesh path never took the push while
/// the Direct3D 11 one always has, because that one runs Riot's <c>mesh_vs</c>, whose lines 106-110 are
/// the same five instructions; and the billboard's roll was seeded from lane X and then accumulated from
/// lane Z.</para>
/// </summary>
public sealed class DepthPushAndRollTests
{
    private const int Stride = 19;

    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static VfxSystemDefinition One(VfxCurve3? overLife) => new(
        PathHash: 1, Name: "s", ParticlePath: "",
        Emitters: new[]
        {
            new VfxEmitterDefinition(
                Name: "e",
                Rate: VfxCurveF.Const(30f),
                ParticleLifetime: VfxCurveF.Const(4f),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: false,
                Disabled: false,
                BlendMode: 1,
                BirthScale: VfxCurve3.Const(new Vector3(10f, 10f, 10f)),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: "ASSETS/Test/p.dds",
                TexDiv: new Vector2(1f, 1f),
                NumFrames: 1,
                RandomStartFrame: false,
                IsMeshPrimitive: false,
                RotationOverLife: overLife),
        });

    /// <summary>The spin of the first live particle after a second, in radians. Slot 9 is the roll both
    /// renderers read.</summary>
    private static float SpinAfterASecond(VfxCurve3? overLife)
    {
        var sim = new VfxParticleSimulator(seed: 1);
        sim.SetSystem(One(overLife), Matrix4x4.Identity);
        for (int i = 0; i < 30; i++) sim.Update(1f / 30f);
        var e = sim.Emitters.Single();
        Assert.True(e.InstanceCount > 0, "no particle was born in a second");
        return e.Instances[9];
    }

    // ================================================================ the roll lane

    [Fact]
    public void TheOverLifeSpinDrivesTheLaneItIsSeededFrom()
    {
        // One accumulator cannot take its birth angle from lane X and its rate from lane Z. It did.
        float onX = SpinAfterASecond(VfxCurve3.Const(new Vector3(90f, 0f, 0f)));
        float onZ = SpinAfterASecond(VfxCurve3.Const(new Vector3(0f, 0f, 90f)));
        float none = SpinAfterASecond(null);

        Assert.True(MathF.Abs(onX) > 0.1f, $"a rotation authored on X must turn the billboard, got {onX}");
        Assert.Equal(none, onZ, 4);   // and Z no longer does, which is the half that changes
    }

    [Fact]
    public void TheSeedAndTheRateNowComeFromOnePlace()
    {
        string? sim = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleSimulator.cs");
        Assert.NotNull(sim);
        // seeded from lane X since M640, which is what settled the argument
        Assert.Contains("Rot = d.IsMeshPrimitive ? 0f : birthRotation.X", sim);
        Assert.Contains("RotVel = rotVel.X", sim);
        // and accumulated from the same lane now, in both the linger and the ordinary branch
        Assert.Contains("lingerRot.Sample(lingerT).X", sim);
        Assert.Contains("rotCurve.Sample(particleT).X", sim);
        Assert.DoesNotContain("rotCurve.Sample(particleT).Z", sim);
    }

    // ================================================================ the push

    [Fact]
    public void TheQuadPathAlreadyHadTheReadingAndStillDoes()
    {
        string? gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        Assert.NotNull(gl);
        // normalised, per corner, positive away from the eye - decoded from quad_vs instructions 12-16 in
        // M175, which is the same span the reading re-reads. Nothing here changed; this pins that.
        Assert.Contains("world += (away / len) * uDepthPushPull;", gl);
        Assert.Contains("POSITIVE pushes away from the camera", gl);
    }

    [Fact]
    public void TheMeshPathTakesThePushToo()
    {
        string? gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        Assert.NotNull(gl);
        // Riot's mesh_vs computes the identical five instructions, which is why the D3D11 path - running
        // that very shader - has always pushed while this one did not.
        Assert.Contains("uniform float uMeshDepthPushPull;", gl);
        Assert.Contains("if (len > 1e-4) p += (away / len) * uMeshDepthPushPull;", gl);
        Assert.Contains("_gl.Uniform1(_muDepthPushPull, es.Def.DepthPushPull);", gl);

        // and the form matches the decoded transcription the D3D11 side already carries
        string? decoded = Source("src", "ReyEngine.Rendering.D3D11", "ParticleShading.cs");
        Assert.NotNull(decoded);
        Assert.Contains("mesh_vs", decoded);
        Assert.Contains("return position + away * (pushPull / MathF.Sqrt(lenSq));", decoded);
    }

    [Fact]
    public void RibbonsStillTakeNoPush()
    {
        // 2.37 says which of the mesh and ribbon shaders read it is not attested; 2.47 attests the mesh
        // and says nothing new about the ribbon. Neither renderer pushes a ribbon, and that stays true
        // until a ribbon vertex shader is disassembled.
        string? gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        Assert.NotNull(gl);
        int trail = gl!.IndexOf("private const string TrailVert", StringComparison.Ordinal);
        int afterTrail = gl.IndexOf("private const string", trail + 10, StringComparison.Ordinal);
        Assert.True(trail > 0 && afterTrail > trail);
        Assert.DoesNotContain("PushPull", gl[trail..afterTrail]);
    }
}
