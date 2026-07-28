using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M266: the OpenGL viewport and the D3D11 viewport must animate the same playback identically.
///
/// <para>Every one of the five things that decide a placement's animation - the seed, the placement matrix,
/// the colorModulate tint, the start delay and the sprite aspect / colour gradient - lands in the packed
/// instance floats the renderers read. So one bit-exact comparison of those floats covers all five at once,
/// which is why these tests assert on <c>EmitterState.Instances</c> rather than on any of the five
/// separately.</para>
///
/// <para>The seed can only be checked IN PROCESS. It comes from <see cref="HashCode.Combine"/>, which is
/// salted per process: two runs of correct code disagree, so no screenshot, log or capture taken across
/// runs can validate it. Both sides must be built and ticked inside one process, which is what happens
/// here.</para>
///
/// <para><b>But this test does not check the seed, and an earlier version of this comment claimed it did.</b>
/// Verified by mutation: replacing the seed expression with a constant leaves this test GREEN, because both
/// sides then agree on the same wrong value - it compares the two paths against each other, so any error
/// they share is invisible to it. The seed is pinned by
/// <see cref="RepeatedPlacementsDoNotAnimateInLockstep"/> alone, which is the only test that fails on a
/// constant seed. A shared-implementation parity test can prove the two agree; it can never prove they are
/// right.</para>
/// </summary>
public class VfxPlaybackSimParityTests
{
    // ------------------------------------------------------------------ fixtures

    private static VfxEmitterDefinition Emitter(string name, string? texture,
        bool useTextureAspect = false, int numFrames = 1, bool randomStartFrame = false, float? frameRate = null)
        => new(
            Name: name,
            Rate: VfxCurveF.Const(40f),
            ParticleLifetime: VfxCurveF.Const(3f),
            EmitterLifetime: null,
            ParticleLinger: 0f,
            TimeBeforeFirstEmission: 0f,
            IsSingleParticle: false,
            Disabled: false,
            BlendMode: 1,
            BirthScale: VfxCurve3.Const(new Vector3(12f, 7f, 1f)),
            ScaleOverLife: null,
            BirthColor: VfxCurve4.Const(new Vector4(0.9f, 0.7f, 0.5f, 1f)),
            ColorOverLife: null,
            BirthVelocity: null,
            Acceleration: null,
            BirthRotationalVelocity: null,
            EmitterPosition: VfxCurve3.Const(Vector3.Zero),
            TexturePath: texture,
            TexDiv: new Vector2(1f, 1f),
            NumFrames: numFrames,
            RandomStartFrame: randomStartFrame,
            IsMeshPrimitive: false,
            UseTextureAspect: useTextureAspect,
            FrameRate: frameRate);

    /// <summary>Three emitters, the FIRST of them non-visual on purpose.
    ///
    /// <para>SetSystem drops non-visual emitters, so authored index 1 becomes live index 0 - the exact skew
    /// that produced the M236 out-of-range crash. Every per-emitter list on a VfxPlaybackItem is aligned to
    /// the AUTHORED order, so a path that indexes them by live position binds the wrong sprite to the wrong
    /// emitter and still runs.</para></summary>
    private static VfxSystemDefinition ThreeEmitterSystem() => new(
        PathHash: 0xB0FFA17u, Name: "parity_test", ParticlePath: "assets/test/parity.bin",
        Emitters: new[]
        {
            Emitter("non_visual", texture: null),
            Emitter("aspect", "ASSETS/Test/aspect.dds", useTextureAspect: true),
            Emitter("gradient", "ASSETS/Test/plain.dds"),
        });

    /// <summary>A wide sprite: with UseTextureAspect the simulator multiplies sizeX by width/height, so a
    /// path that skips it renders the emitter square and nothing about the image says so.</summary>
    private static TextureImage WideSprite() => new(64, 16, new byte[64 * 16 * 4]);

    /// <summary>A 4x4 gradient with every texel different, so the CPU bilinear sample lands on distinct
    /// colours per particle age and a missing gradient shows up as a flat colour rather than as noise.</summary>
    private static TextureImage Gradient()
    {
        var px = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++)
        {
            px[i * 4 + 0] = (byte)(16 * i);
            px[i * 4 + 1] = (byte)(255 - 16 * i);
            px[i * 4 + 2] = (byte)(8 * i + 40);
            px[i * 4 + 3] = (byte)(200 - 4 * i);
        }
        return new TextureImage(4, 4, px);
    }

    private static VfxPlaybackItem Item(VfxSystemDefinition system, Vector3 at, Vector4? tint, float startDelay)
        => new(system, Matrix4x4.CreateTranslation(at),
            EmitterTextures: new TextureImage?[] { null, WideSprite(), new TextureImage(8, 8, new byte[8 * 8 * 4]) },
            EmitterColorTextures: new TextureImage?[] { null, null, Gradient() },
            ColorModulate: tint)
        { StartDelay = startDelay };

    // ------------------------------------------------------------------ the two sides

    /// <summary>The GL side, transcribed from <c>ViewportControl.RebuildParticleSim</c> as it stood before
    /// the extraction, INCLUDING the seed expression.
    ///
    /// <para>Transcribed rather than delegated on purpose: if the shared contract's seed or setup order ever
    /// changes without the GL viewport changing with it, this test is what notices. Delegating would make
    /// the comparison vacuous.</para></summary>
    private static VfxParticleSimulator CreateTheWayGlDoes(VfxPlaybackItem item)
    {
        int seed = HashCode.Combine(item.System.PathHash,
            BitConverter.SingleToInt32Bits(item.Transform.M41),
            BitConverter.SingleToInt32Bits(item.Transform.M42),
            BitConverter.SingleToInt32Bits(item.Transform.M43));
        var sim = new VfxParticleSimulator(seed);
        sim.SetSystem(item.System, item.Transform);
        if (item.ColorModulate is { } tint) sim.PlacementTint = tint;
        if (item.StartDelay > 0f) sim.SetStartDelay(item.StartDelay);
        BindTheSimulationHalfOfEmitterAssets(sim, item);
        return sim;
    }

    /// <summary>The half of <c>ViewportControl.BindEmitterAssets</c> that changes the SIMULATOR's output, as
    /// opposed to which GL handle it binds. The rest of that method is interleaved with the GL upload cache
    /// and cannot run headlessly, which is why only this half is mirrored on the D3D11 side.</summary>
    private static void BindTheSimulationHalfOfEmitterAssets(VfxParticleSimulator sim, VfxPlaybackItem item)
    {
        foreach (var es in sim.Emitters)
        {
            int idx = -1;
            for (int i = 0; i < item.System.Emitters.Count; i++)
                if (ReferenceEquals(item.System.Emitters[i], es.Def)) { idx = i; break; }
            var img = idx >= 0 && idx < item.EmitterTextures.Count ? item.EmitterTextures[idx] : null;
            if (img is not null && es.Def.UseTextureAspect)
            {
                float cellWidth = img.Width / Math.Max(1f, es.Def.TexDiv.X);
                float cellHeight = img.Height / Math.Max(1f, es.Def.TexDiv.Y);
                if (cellHeight > 0f) es.SpriteAspect = Math.Clamp(cellWidth / cellHeight, 0.05f, 20f);
            }
            var colorImg = item.EmitterColorTextures is { } cts && idx >= 0 && idx < cts.Count ? cts[idx] : null;
            if (colorImg is not null)
            {
                es.ColorGradient = colorImg.Rgba;
                es.ColorGradientW = colorImg.Width;
                es.ColorGradientH = colorImg.Height;
            }
        }
    }

    /// <summary>The D3D11 side: whatever the shared contract does, and nothing else.</summary>
    private static VfxParticleSimulator CreateTheWayD3D11Does(VfxPlaybackItem item)
    {
        var sim = VfxPlaybackSim.Create(item);
        Assert.NotNull(sim);
        VfxPlaybackSim.ApplySimulationAssets(sim!, item);
        return sim!;
    }

    private static void Tick(VfxParticleSimulator sim, int frames)
    {
        for (int i = 0; i < frames; i++) sim.Update(1f / 60f);
    }

    /// <summary>Compared as RAW BITS, not with a tolerance. Two simulations of the same data on the same
    /// machine are either the same computation or they are not; an epsilon here would hide a drifted seed
    /// behind "close enough".</summary>
    private static void AssertBitIdentical(VfxParticleSimulator a, VfxParticleSimulator b)
    {
        Assert.Equal(a.Emitters.Count, b.Emitters.Count);
        int compared = 0;
        for (int e = 0; e < a.Emitters.Count; e++)
        {
            var ea = a.Emitters[e];
            var eb = b.Emitters[e];
            Assert.Same(ea.Def, eb.Def);
            Assert.Equal(ea.InstanceCount, eb.InstanceCount);
            Assert.Equal(ea.SpriteAspect, eb.SpriteAspect);
            int floats = ea.InstanceCount * ParticleQuadBuilder.Stride;
            for (int i = 0; i < floats; i++)
            {
                if (BitConverter.SingleToInt32Bits(ea.Instances[i]) == BitConverter.SingleToInt32Bits(eb.Instances[i]))
                    continue;
                Assert.Fail($"emitter {e} ({ea.Def.Name}) float {i} "
                            + $"(particle {i / ParticleQuadBuilder.Stride}, field {i % ParticleQuadBuilder.Stride}): "
                            + $"GL {ea.Instances[i]} vs D3D11 {eb.Instances[i]}");
            }
            compared += floats;
        }
        Assert.True(compared > 0, "no instances were produced, so the comparison would be vacuous");
    }

    // ------------------------------------------------------------------ A1

    [Fact]
    public void SameItemsProduceIdenticalInstances()
    {
        var system = ThreeEmitterSystem();
        var items = new[]
        {
            Item(system, new Vector3(1234.5f, 60f, -987.25f), new Vector4(1f, 0.97f, 0.84f, 0.6f), 0f),
            Item(system, new Vector3(-4000f, 12.5f, 3000f), null, 0.25f),
            Item(system, Vector3.Zero, new Vector4(0.5f, 0.5f, 1f, 1f), 0f),
        };

        foreach (var item in items)
        {
            var gl = CreateTheWayGlDoes(item);
            var dx = CreateTheWayD3D11Does(item);

            // The non-visual emitter must have been dropped by BOTH, or the per-emitter texture lists are
            // being indexed by live position somewhere and the comparison below would be against the wrong
            // pairing rather than against nothing.
            Assert.Equal(2, gl.Emitters.Count);
            Assert.Equal(2, dx.Emitters.Count);

            Tick(gl, 120);
            Tick(dx, 120);
            AssertBitIdentical(gl, dx);
        }
    }

    [Fact]
    public void TheAuthoredAssetsReachTheSimulatorOnBothSides()
    {
        // A guard on the guard: if neither side applied SpriteAspect or the gradient, A1 would still pass
        // (both would be equally wrong), and a wrong particle SIZE is the kind of thing that looks fine.
        var item = Item(ThreeEmitterSystem(), new Vector3(500f, 0f, 500f), null, 0f);
        var dx = CreateTheWayD3D11Does(item);

        var aspect = dx.Emitters.Single(e => e.Def.Name == "aspect");
        Assert.Equal(4f, aspect.SpriteAspect, 4);          // 64x16 sprite, texDiv (1,1)
        Assert.Null(aspect.ColorGradient);

        var gradient = dx.Emitters.Single(e => e.Def.Name == "gradient");
        Assert.Equal(1f, gradient.SpriteAspect, 4);        // UseTextureAspect is off for this one
        Assert.NotNull(gradient.ColorGradient);
        Assert.Equal(4, gradient.ColorGradientW);
        Assert.Equal(4, gradient.ColorGradientH);
    }

    // ------------------------------------------------------------------ A2

    /// <summary>One emitter whose per-particle randomness shows up in a float the renderer reads.
    ///
    /// <para>randomStartFrame rolls the flipbook frame per particle, which lands in instance field 10. It is
    /// used here instead of position because two placements at different translations have different world
    /// positions no matter what the seed is - so a position comparison would pass for a reason that has
    /// nothing to do with the seed.</para></summary>
    private static VfxSystemDefinition FlipbookSystem() => new(
        PathHash: 0x5EEDu, Name: "lockstep_test", ParticlePath: "assets/test/lockstep.bin",
        Emitters: new[]
        {
            Emitter("torch", "ASSETS/Test/flame.dds", numFrames: 8, randomStartFrame: true, frameRate: 4f),
        });

    /// <summary>Every instance float, with the placement's own translation removed from the position.
    ///
    /// <para>Removing it is the whole point: what "animating in lockstep" means is that two braziers standing
    /// in different places do the SAME thing relative to where they stand. Comparing raw world positions
    /// cannot see that.</para></summary>
    private static float[] PlacementRelative(VfxParticleSimulator sim, Vector3 translation)
    {
        var outp = new List<float>();
        foreach (var es in sim.Emitters)
        {
            int floats = es.InstanceCount * ParticleQuadBuilder.Stride;
            for (int i = 0; i < floats; i++)
            {
                float v = es.Instances[i];
                int field = i % ParticleQuadBuilder.Stride;
                if (field == 0) v -= translation.X;
                else if (field == 1) v -= translation.Y;
                else if (field == 2) v -= translation.Z;
                outp.Add(v);
            }
        }
        return outp.ToArray();
    }

    [Fact]
    public void RepeatedPlacementsDoNotAnimateInLockstep()
    {
        var system = FlipbookSystem();
        var a = new Vector3(2500f, 100f, -1750f);
        var b = new Vector3(-8000f, 100f, 6250f);

        var simA = CreateTheWayD3D11Does(Item(system, a, null, 0f));
        var simB = CreateTheWayD3D11Does(Item(system, b, null, 0f));
        Tick(simA, 90);
        Tick(simB, 90);

        var relA = PlacementRelative(simA, a);
        var relB = PlacementRelative(simB, b);
        Assert.True(relA.Length > 0, "nothing was emitted, so the check would be vacuous");
        Assert.Equal(relA.Length, relB.Length);
        Assert.False(relA.SequenceEqual(relB),
            "two placements of the same system produced byte-identical animation relative to their own "
            + "origins - the seed is not placement-derived, which is what a hardcoded seed looks like");
    }

    [Fact]
    public void TheSamePlacementIsReproducible()
    {
        // The control for the test above: the difference there has to come from the PLACEMENT, not from the
        // simulator being nondeterministic. Without this, a stray DateTime or Guid in the seed would pass A2
        // while being wrong in the worst possible way - GL and D3D11 would disagree with each other too.
        var system = FlipbookSystem();
        var at = new Vector3(2500f, 100f, -1750f);

        var first = CreateTheWayD3D11Does(Item(system, at, null, 0f));
        var second = CreateTheWayD3D11Does(Item(system, at, null, 0f));
        Tick(first, 90);
        Tick(second, 90);

        AssertBitIdentical(first, second);
    }

    [Fact]
    public void AnEmptySystemProducesNoSimulator()
    {
        // Both viewports skip these rather than registering an emitterless placement, so the contract has to
        // report it rather than hand back a live-but-inert simulator.
        var empty = new VfxSystemDefinition(1, "empty", "", Array.Empty<VfxEmitterDefinition>());
        Assert.Null(VfxPlaybackSim.Create(
            new VfxPlaybackItem(empty, Matrix4x4.Identity, Array.Empty<TextureImage?>())));
    }
}
