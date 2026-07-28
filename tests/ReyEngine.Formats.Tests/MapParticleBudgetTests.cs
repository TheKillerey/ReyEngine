using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M266 (A4): the quad budget of the multi-placement map particle driver.
///
/// <para>The failure mode being guarded is a budget that is HIT SILENTLY. A driver that simply fills until
/// the buffer runs out produces exactly the same total quad count as the proportional rule, draws a
/// perfectly plausible frame, and makes whole emitters vanish - the LAST ones in pass order, which is where
/// the additive glows are. An emitter that vanished looks identical to an emitter that finished, so nothing
/// about the image says the budget was involved.</para>
///
/// <para>These drive <see cref="D3D11MapParticles.Pack"/> directly rather than a whole driver. Pack is the
/// packing and budget pass, extracted static and device-free precisely so it can be tested at all: building
/// even one particle material needs a D3D11 device AND the game's shader cache, neither of which a unit test
/// has. The reporting sentence is asserted through <see cref="D3D11MapParticles.OverBudgetLine"/>, which is
/// the same function <c>FrameReport</c> composes it with.</para>
/// </summary>
public class MapParticleBudgetTests
{
    private static VfxEmitterDefinition Emitter(string name) => new(
        Name: name,
        Rate: VfxCurveF.Const(10f),
        ParticleLifetime: VfxCurveF.Const(2f),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: false,
        Disabled: false,
        BlendMode: 1,
        BirthScale: VfxCurve3.Const(new Vector3(5f, 5f, 1f)),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(Vector4.One),
        ColorOverLife: null,
        BirthVelocity: null,
        Acceleration: null,
        BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(Vector3.Zero),
        TexturePath: "ASSETS/Test/dot.dds",
        TexDiv: new Vector2(1f, 1f),
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: false);

    /// <summary>A live emitter carrying <paramref name="count"/> particles of plausible size. The floats are
    /// only exercised as geometry here - what is under test is how many of them survive the budget.</summary>
    private static VfxParticleSimulator.EmitterState Source(string name, int count)
    {
        var es = new VfxParticleSimulator.EmitterState { Def = Emitter(name) };
        es.Instances = new float[count * ParticleQuadBuilder.Stride];
        es.InstanceCount = count;
        for (int p = 0; p < count; p++)
        {
            int o = p * ParticleQuadBuilder.Stride;
            es.Instances[o + ParticleQuadBuilder.OffPos] = p * 10f;
            es.Instances[o + ParticleQuadBuilder.OffSizeX] = 20f;
            es.Instances[o + ParticleQuadBuilder.OffSizeY] = 20f;
            for (int c = 0; c < 4; c++) es.Instances[o + ParticleQuadBuilder.OffColor + c] = 1f;
        }
        return es;
    }

    /// <summary>Five slices with very different demands, including a one-particle emitter - the one a
    /// "fill until full" implementation would still serve and a "scale everything down" implementation
    /// without the Max(1) floor would round away to nothing.</summary>
    private static IReadOnlyList<IReadOnlyList<VfxParticleSimulator.EmitterState>> Scene() => new[]
    {
        new[] { Source("big_a", 40), Source("big_b", 30) },   // two placements of the same emitter
        new[] { Source("medium", 25) },
        new[] { Source("single", 1) },
        new[] { Source("small", 12) },
        new[] { Source("tiny", 7) },
    };

    private const int Requested = 40 + 30 + 25 + 1 + 12 + 7;   // 115

    private static (int Written, int Requested, int Truncated, D3D11MapParticles.PackedRange[] Ranges, int Verts, int Indices)
        Run(IReadOnlyList<IReadOnlyList<VfxParticleSimulator.EmitterState>> scene, int budget)
    {
        var verts = new PreviewVertex[budget * 4];
        var indices = new uint[budget * 6];
        var ranges = new D3D11MapParticles.PackedRange[scene.Count];
        int written = D3D11MapParticles.Pack(scene, budget, verts, indices,
            Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, ranges,
            out int vertexCount, out int indexCount, out int requested, out int truncated);
        return (written, requested, truncated, ranges, vertexCount, indexCount);
    }

    [Fact]
    public void OverBudgetThinsEveryEmitterAndReportsIt()
    {
        const int budget = 20;
        var (written, requested, truncated, ranges, verts, indices) = Run(Scene(), budget);

        Assert.Equal(Requested, requested);
        Assert.True(written <= budget, $"{written} quads written against a {budget}-quad ceiling");
        Assert.True(written > 0, "nothing was written, so the rest of the assertions would be vacuous");

        int dropped = requested - written;
        Assert.True(dropped > 0, "the budget was not actually exceeded, so this proves nothing");
        Assert.True(truncated > 0, "the overflow was not counted");

        // The proportional rule, stated as the thing that distinguishes it from filling until full: EVERY
        // slice that asked for something still drew something.
        for (int i = 0; i < ranges.Length; i++)
            Assert.True(ranges[i].Quads >= 1,
                $"slice {i} asked for particles and drew none - the thinning is not proportional");

        // Contiguous and in order: the indices Append writes are absolute and the draw uses
        // BaseVertexLocation 0, so overlapping or restarting ranges would point a slice at another slice's
        // vertices and still render something plausible.
        int cursor = 0;
        foreach (var range in ranges)
        {
            Assert.Equal(cursor, range.Start);
            Assert.Equal(range.Quads * 6, range.Count);
            cursor += range.Count;
        }
        Assert.Equal(cursor, indices);
        Assert.Equal(written * 4, verts);
        Assert.Equal(written * 6, indices);

        // A budget that is applied without being reported is the actual bug. The count is part of the
        // sentence, not a log line somewhere else.
        string line = D3D11MapParticles.OverBudgetLine(dropped, budget, truncated);
        Assert.Contains(dropped.ToString(), line);
        Assert.Contains(budget.ToString(), line);
    }

    [Fact]
    public void UnderBudgetDrawsEverythingAndReportsNothing()
    {
        // The control. Without it, a Pack that dropped everything on the floor would pass the test above by
        // satisfying "written <= budget" trivially.
        var (written, requested, truncated, ranges, _, _) = Run(Scene(), Requested * 2);

        Assert.Equal(Requested, requested);
        Assert.Equal(Requested, written);
        Assert.Equal(0, truncated);
        Assert.Equal(new[] { 70, 25, 1, 12, 7 }, ranges.Select(r => r.Quads).ToArray());
    }

    [Fact]
    public void AnEmptySliceContributesNothingButKeepsItsPlaceInTheRangeTable()
    {
        // A placement culled by the camera leaves its emitter with no live sources. The slice must still get
        // a range - the driver indexes ranges by slice position - and that range must be empty rather than
        // inheriting the previous slice's, which would draw one emitter's particles through another
        // emitter's shader and textures.
        var scene = new[]
        {
            new[] { Source("a", 3) },
            Array.Empty<VfxParticleSimulator.EmitterState>(),
            new[] { Source("c", 2) },
        };
        var (written, _, truncated, ranges, _, _) = Run(scene, 100);

        Assert.Equal(5, written);
        Assert.Equal(0, truncated);
        Assert.Equal(0, ranges[1].Count);
        Assert.Equal(0, ranges[1].Quads);
        Assert.Equal(ranges[0].Count, ranges[1].Start);
        Assert.Equal(ranges[1].Start, ranges[2].Start);
    }
}
