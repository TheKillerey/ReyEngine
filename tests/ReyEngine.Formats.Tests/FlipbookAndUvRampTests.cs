using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M719, the sixth ltk-manager reading ported: 2.11's two corrections - the flipbook frame and the scroll
/// clamp - with 2.13's flip order and 3.2's address enum, which the clamp's removal could not ship without.
///
/// <para>The helper cases are the reference renderer's own (uvTransform.test.ts), carried over number for
/// number, so a disagreement reads as one. The simulator cases are named after real emitters from the
/// census over 1,581,956 of them.</para>
/// </summary>
public sealed class FlipbookAndUvRampTests
{
    private const int Stride = 19;
    private const int OffFrame = 10;

    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static VfxSystemDefinition Book(int numFrames, float startFrame, bool randomStart, float? frameRate,
        Vector2 texDiv, float rate = 1f, bool single = true) => new(
        PathHash: 1, Name: "book", ParticlePath: "",
        Emitters: new[]
        {
            new VfxEmitterDefinition(
                Name: "book",
                Rate: VfxCurveF.Const(rate),
                ParticleLifetime: VfxCurveF.Const(5f),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: single,
                Disabled: false,
                BlendMode: 1,
                BirthScale: VfxCurve3.Const(new Vector3(20f, 20f, 20f)),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: "ASSETS/Test/p.dds",
                TexDiv: texDiv,
                NumFrames: numFrames,
                RandomStartFrame: randomStart,
                IsMeshPrimitive: false,
                FrameRate: frameRate,
                StartFrame: startFrame),
        });

    private static List<float> FramesOverTime(VfxSystemDefinition system, float seconds)
    {
        var sim = new VfxParticleSimulator(seed: 1234);
        sim.SetSystem(system, Matrix4x4.Identity);
        var seen = new List<float>();
        for (float t = 0f; t < seconds; t += 1f / 30f)
        {
            sim.Update(1f / 30f);
            var e = sim.Emitters.First();
            if (e.InstanceCount > 0) seen.Add(e.Instances[OffFrame]);
        }
        return seen;
    }

    // ================================================================ the frame, as the reference has it

    [Theory]
    // start, phase, age, rate, numFrames -> frame
    [InlineData(0f, 0f, 0f, 0f, 8, 0f)]
    [InlineData(0f, 1f, 0f, 0f, 8, 1f)]
    [InlineData(0f, 3.9f, 0f, 0f, 8, 3f)]
    [InlineData(0f, 4f, 0f, 0f, 8, 4f)]
    [InlineData(0f, 7f, 0f, 0f, 8, 7f)]
    [InlineData(0f, 9f, 0f, 0f, 8, 1f)]        // past the end of the book, back to its start
    [InlineData(3f, 0f, 0f, 0f, 4, 3f)]
    [InlineData(3f, 5f, 0f, 0f, 4, 4f)]        // the run wraps, THEN the start lands: 3 + fmod(5, 4)
    [InlineData(3f, 2f, 0f, 0f, 8, 5f)]        // their cell 1 of a 2x2 grid; ours is unwrapped, the sampler wraps it
    [InlineData(0f, 0f, 0.25f, 10f, 4, 2f)]
    [InlineData(0f, 0f, 0.35f, 10f, 4, 3f)]
    public void TheFrameIsTheReferenceRenderersFrame(float start, float phase, float age, float rate, int frames, float expected) =>
        Assert.Equal(expected, VfxFlipbook.Frame(start, phase, age, rate, frames));

    [Fact]
    public void TheEdgesAreChoicesAndBehaveAsRecorded()
    {
        Assert.Equal(5f, VfxFlipbook.Frame(5f, 0f, 3f, 0f, 1));      // a still cell over a grid (M535)
        Assert.Equal(5f, VfxFlipbook.Frame(5f, 0f, 3f, 30f, 0));     // a written numFrames of 0 counts as 1
        Assert.Equal(3f, VfxFlipbook.Frame(2f, 1.5f, 10f, -3f, 4));  // a negative rate holds
        Assert.Equal(6f, VfxFlipbook.Frame(6f, 0f, 0f, 0f, 4));      // never clamped to numFrames - 1

        // System.Random's largest sample times four rounds to exactly 4.0f in float. Kept below it, the
        // phase lands on the run's last cell instead of wrapping to its start.
        float phase = VfxFlipbook.RandomPhase(1.0 - 1.0 / int.MaxValue, 4);
        Assert.True(phase < 4f);
        Assert.Equal(4f, VfxFlipbook.Frame(1f, phase, 0f, 0f, 4));
    }

    // ================================================================ the frame, in the simulator

    [Fact]
    public void ARandomStartKeepsItsAuthoredStart()
    {
        // Poppy_Skin40_R_hit_instant / Sparkles2 and 10,267 other reachable books: numFrames 4, startFrame 1,
        // a random start and no rate. The engine's cells are 1 to 4; we drew 0 to 3 and dropped the start.
        var seen = new HashSet<float>();
        for (int seed = 0; seed < 32; seed++)
        {
            var sim = new VfxParticleSimulator(seed);
            sim.SetSystem(Book(4, 1f, randomStart: true, frameRate: null, new Vector2(2f, 2f), rate: 200f, single: false),
                Matrix4x4.Identity);
            sim.Update(0.1f);
            var e = sim.Emitters.First();
            for (int i = 0; i < e.InstanceCount; i++) seen.Add(e.Instances[i * Stride + OffFrame]);
        }
        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, seen.OrderBy(f => f).ToArray());
    }

    [Fact]
    public void ABookWithAStartCyclesBackToItsStart()
    {
        // Fizz_Skin09_Q_debuff / Flashing1: numFrames 9, startFrame 1, frameRate 24. One particle for two
        // seconds runs through cell 9 and back to 1 - and never to the grid's first cell, where we sent it.
        var frames = FramesOverTime(Book(9, 1f, randomStart: false, frameRate: 24f, new Vector2(4f, 4f)), 2f);
        Assert.Contains(9f, frames);
        Assert.DoesNotContain(0f, frames);
        Assert.Equal(1f, frames.Min());
    }

    [Fact]
    public void ASingleFrameEmitterDrawsTheStillCellItsStartPicks()
    {
        // M535 made the converter write FireTorch_Med/Flat's startFrame 5 over a 2x3 sheet. The simulator's
        // numFrames > 1 gate then drew cell 0 for it and for 2,960 other reachable emitters.
        var frames = FramesOverTime(Book(1, 5f, randomStart: false, frameRate: null, new Vector2(2f, 3f)), 1f);
        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.Equal(5f, f));
    }

    [Fact]
    public void AStartPastTheBookIsNotClamped()
    {
        var frames = FramesOverTime(Book(4, 6f, randomStart: false, frameRate: null, new Vector2(4f, 4f)), 1f);
        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.Equal(6f, f));
    }

    [Fact]
    public void TheRandomPhaseTakesTheSampleTheWholeCellDrawTook()
    {
        // The phase is drawn where _rng.Next(numFrames) was, under the same condition. With nothing rolled
        // before it, the first particle's frame is the seeded stream's first sample times four - which is
        // exactly what Next(4) returned, so the stream every later roll reads from has not moved.
        const int seed = 1234;
        var sim = new VfxParticleSimulator(seed);
        sim.SetSystem(Book(4, 0f, randomStart: true, frameRate: null, new Vector2(2f, 2f)), Matrix4x4.Identity);
        sim.Update(1f / 30f);
        var e = sim.Emitters.First();
        Assert.True(e.InstanceCount > 0);

        Assert.Equal((float)new Random(seed).Next(4), e.Instances[OffFrame]);
        Assert.Equal(MathF.Floor(VfxFlipbook.RandomPhase(new Random(seed).NextDouble(), 4)), e.Instances[OffFrame]);
    }

    // ================================================================ the ramp

    [Fact]
    public void TheRampWrapsOnItsOwnAndTheScrollsLandOnTop()
    {
        // uvTransform.test.ts: offset 0.5, birth scroll 2, age 1, integrated 3 - the ramp of 2.5 wraps to 0.5
        // on its own, and the integrated 3 lands on top.
        var layer = new VfxUvLayer(new Vector2(0.5f, 0f), new Vector2(2f, 0f), new Vector2(3f, 0f), Vector2.Zero,
            false, Vector2.One, 0f, 0f, new Vector2(0.5f, 0.5f), false, false);
        Assert.Equal(3.5f, VfxUvTransform.Translation(layer, 1f, 0f).X, 5);

        // Under uvScrollClamp the ramp holds one cell out, either way, and the integrated term is untouched.
        var held = layer with { BirthScrollRate = new Vector2(2f, -4f), ScrollClamp = true };
        var t = VfxUvTransform.Translation(held, 1f, 0f);
        Assert.Equal(4f, t.X, 5);
        Assert.Equal(-1f, t.Y, 5);

        // The emitter's scroll runs on the system's clock, not on a particle's age.
        var emitter = new VfxUvLayer(Vector2.Zero, Vector2.Zero, Vector2.Zero, new Vector2(0.25f, -0.5f), false,
            Vector2.One, 0f, 0f, new Vector2(0.5f, 0.5f), false, false);
        var e = VfxUvTransform.Translation(emitter, 0f, 4f);
        Assert.Equal(1f, e.X, 5);
        Assert.Equal(-2f, e.Y, 5);
    }

    [Theory]
    [InlineData(-0.25f, false, 0.75f)]   // C#'s % truncates; the wrap is x - floor(x), as GLSL's is
    [InlineData(2.25f, false, 0.25f)]
    [InlineData(-1.5f, true, -1f)]
    [InlineData(1f, true, 1f)]
    [InlineData(0.4f, true, 0.4f)]       // inside its reach a clamped ramp scrolls as it always did
    public void TheRampAtItsEdges(float ramp, bool clamp, float expected) =>
        Assert.Equal(expected, VfxUvTransform.Ramp(ramp, 0f, 0f, clamp), 5);

    [Fact]
    public void AFlipLandsAfterTheTranslation()
    {
        // 2.13: a flip is a post-multiply, so a flipped layer's scroll reverses. Corner u = 0 shifted a
        // quarter cell reads 0.25, and flipped after that 0.75. Flipped first, as the GL viewport did until
        // M719, it read 1.25.
        var layer = new VfxUvLayer(new Vector2(0.25f, 0f), Vector2.Zero, Vector2.Zero, Vector2.Zero, false,
            Vector2.One, 0f, 0f, new Vector2(0.5f, 0.5f), FlipU: true, FlipV: false);
        Assert.Equal(0.75f, VfxUvTransform.Cell(layer, 0f, 0f, 0f, 0f).X, 5);
    }

    [Fact]
    public void ScaleAndRotationTurnAboutTheCentre()
    {
        var zoom = new VfxUvLayer(Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, false,
            new Vector2(2f, 2f), 0f, 0f, new Vector2(0.5f, 0.5f), false, false);
        Assert.Equal(new Vector2(-0.5f, -0.5f), VfxUvTransform.Cell(zoom, 0f, 0f, 0f, 0f));

        // (0.5, 0) about the centre turns a quarter to (0, 0.5)
        var c = VfxUvTransform.Cell(zoom with { Scale = Vector2.One, RotationDegrees = 90f }, 1f, 0.5f, 0f, 0f);
        Assert.Equal(0.5f, c.X, 4);
        Assert.Equal(1f, c.Y, 4);

        // and the default layer is the identity
        Assert.Equal(new Vector2(0.3f, 0.7f), VfxUvTransform.Cell(default, 0.3f, 0.7f, 5f, 9f));
    }

    [Fact]
    public void AnEmitterWithNoMultiplierHasNoMultiplierTransform()
    {
        var def = Book(1, 0f, false, null, Vector2.One).Emitters[0] with
        {
            TextureMultUvOffset = new Vector2(0.5f, 0.5f),
            TextureMultUvScrollClamp = true,
        };
        Assert.Equal(default(VfxUvLayer), VfxUvLayer.MultOf(def));

        var withMult = def with { TextureMultPath = "ASSETS/Test/m.dds" };
        Assert.Equal(new Vector2(0.5f, 0.5f), VfxUvLayer.MultOf(withMult).Offset);
        Assert.True(VfxUvLayer.MultOf(withMult).ScrollClamp);
    }

    // ================================================================ the address mode

    [Fact]
    public void TheBaseAddressIsTheEnginesEnumNotTheSamplers()
    {
        // ParticleSystem::TEXTUREADDRESS is 0 WRAP, 1 MIRROR, 2 CLAMP, 3 BORDER. The sampler's enum, which
        // the erosion and palette fields reach unremapped, is 0 wrap, 1 clamp, 2 mirror.
        Assert.Equal(2, VfxTextureAddress.Clamp);
        Assert.Equal(0, VfxTextureAddress.SamplerModeOf(null));      // absent: the declared default, WRAP
        Assert.Equal(0, VfxTextureAddress.SamplerModeOf(VfxTextureAddress.Wrap));
        Assert.Equal(2, VfxTextureAddress.SamplerModeOf(VfxTextureAddress.Mirror));
        Assert.Equal(1, VfxTextureAddress.SamplerModeOf(VfxTextureAddress.Clamp));
        Assert.Equal(1, VfxTextureAddress.SamplerModeOf(VfxTextureAddress.Border));   // folded, and recorded

        Assert.DoesNotContain("texAddressModeBase", VfxParkedEmitterFields.Names);
        Assert.True(VfxPreviewCoverage.IsParsed(HashAlgorithms.Fnv1a("texAddressModeBase")));
    }

    // ================================================================ one definition, both renderers

    [Fact]
    public void TheD3D11BuilderBakesTheHelpersCellPerCorner()
    {
        var layer = new VfxUvLayer(new Vector2(0.5f, 0f), new Vector2(2f, 0f), Vector2.Zero, Vector2.Zero, true,
            Vector2.One, 0f, 0f, new Vector2(0.5f, 0.5f), false, true);
        var instance = new float[ParticleQuadBuilder.Stride];
        instance[ParticleQuadBuilder.OffSizeX] = 10f;
        instance[ParticleQuadBuilder.OffSizeY] = 10f;
        instance[ParticleQuadBuilder.OffFrame + 1] = 1f;   // age
        var verts = new PreviewVertex[4];
        var indices = new uint[6];

        int v = 0, i = 0;
        ParticleQuadBuilder.Append(instance, 1, verts, ref v, indices, ref i, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
            default, new ParticleQuadBuilder.UvLayers(layer, default, 0f));
        // corner 0 is (0, 0): a ramp of 2.5 held to 1 under uvScrollClamp, with no factor of the grid
        Assert.Equal(1f, verts[0].Uv0.X, 5);
        var corners = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
        for (int k = 0; k < 4; k++)
        {
            var want = VfxUvTransform.Cell(layer, corners[k].X, corners[k].Y, 1f, 0f);
            Assert.Equal(want.X, verts[k].Uv0.X, 5);
            Assert.Equal(want.Y, verts[k].Uv0.Y, 5);
        }

        // wrapped instead of held, the same ramp lands at 0.5
        v = 0; i = 0;
        ParticleQuadBuilder.Append(instance, 1, verts, ref v, indices, ref i, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
            default, new ParticleQuadBuilder.UvLayers(layer with { ScrollClamp = false }, default, 0f));
        Assert.Equal(0.5f, verts[0].Uv0.X, 5);
    }

    [Fact]
    public void TheGlShaderRunsTheSharedFormulaAndNothingElse()
    {
        string? gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        Assert.NotNull(gl);
        Assert.Contains("ReyEngine.Formats.Vfx.VfxUvTransform.Glsl", gl);
        Assert.Contains("vec2 uvc = reyUvCell(cellCorner, age, uEmitterAge, uUvOffset, uUvScrollRate", gl);
        Assert.Contains("vUv = (vec2(fx, fy) + uvc) / vec2(cols, rows);", gl);
        Assert.Contains("vUvMult = (vec2(mfx, mfy) + uvcMult) / vec2(multCols, multRows);", gl);
        Assert.Contains("VfxUvLayer.MultOf(es.Def)", gl);

        // gone: the whole-coordinate clamp, the scroll added after the divide, the flip before the transform
        Assert.DoesNotContain("uvc = clamp(uvc, vec2(0.0), vec2(1.0))", gl);
        Assert.DoesNotContain("+ uUvScrollRate * age", gl);
        Assert.DoesNotContain("if (uUvFlip.x > 0.5)", gl);

        // the base sampler, for the quad draw and the mesh draw, converted from the engine's enum
        Assert.Equal(2, gl!.Split("_gl.BindSampler(0, BaseSampler(es.Def));").Length - 1);
        Assert.Contains("VfxTextureAddress.SamplerModeOf(def.Extras?.TexAddressModeBase)", gl);

        Assert.All(VfxUvTransform.Glsl, c => Assert.True(c < 128, "non-ASCII inside the shared GLSL"));
    }

    [Fact]
    public void BothHostsThePipelineAndTheResolverReadTheSameDefinitions()
    {
        foreach (string host in new[] { "D3D11MapParticles.cs", "D3D11ParticlePlayback.cs" })
        {
            string? text = Source("src", "ReyEngine.App", "Services", host);
            Assert.NotNull(text);
            Assert.Contains("ParticleQuadBuilder.UvLayers.For(es.Def, es.EmitterAge)", text);
        }

        string? pipeline = Source("src", "ReyEngine.App", "Services", "VfxD3D11EmitterPipeline.cs");
        Assert.NotNull(pipeline);
        Assert.Contains("VfxTextureAddress.SamplerModeOf(baseAddress)", pipeline);

        string? resolver = Source("src", "ReyEngine.Formats", "Vfx", "VfxSystemResolver.cs");
        Assert.NotNull(resolver);
        Assert.Contains("GetBool(textureMult.Properties, F_uvScrollClampMult)", resolver);
        Assert.Contains("ReadValueVec2(Get(textureMult.Properties, F_birthUVOffsetMult))", resolver);
    }
}
