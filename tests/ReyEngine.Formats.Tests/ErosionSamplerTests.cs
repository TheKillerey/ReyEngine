using System.Numerics;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M717, the fifth ltk-manager reading ported. Section 2.50 checks their quad shaders against Riot's
/// shipped ones and reports three things. Two of them we already had.
///
/// <para><b>The erosion map samples where the base texture does</b>, which carries the flipbook cell.
/// That was their bug, not ours: our flipbook division happens once in the vertex shader and the fragment
/// shader only ever sees the finished atlas coordinate, so there is no second in-cell uv for the erosion
/// to be read at. Confirmed against Riot's own bytecode while checking - 128 of 128 quad_ps permutations
/// and 1,024 of 1,024 mesh_ps permutations sample the erosion at a coordinate TEXTURE also samples at.
/// Pinned here so it stays true.</para>
///
/// <para><b>PIXEL_COLOR_REMAP_RAMP is not built</b>, and we go one better: the D3D11 path binds the very
/// 1x1 transparent black that makes the stage a no-op, for a reason M221 disassembled.</para>
///
/// <para>What the reading did surface is two gaps of ours: the erosion map's own address mode, which
/// neither renderer honoured, and the LOCK_ALPHA bundle, which we compiled two stages into that the
/// client's shader has no axis for.</para>
/// </summary>
public sealed class ErosionSamplerTests
{
    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static VfxEmitterDefinition Emitter(int? uvMode, string primitive, bool erosion = true, bool soft = false) => new(
        Name: "e",
        Rate: VfxCurveF.Const(10f),
        ParticleLifetime: VfxCurveF.Const(1f),
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
        TexDiv: new Vector2(2f, 2f),
        NumFrames: 4,
        RandomStartFrame: false,
        IsMeshPrimitive: primitive == "VfxPrimitiveMesh",
        AlphaErosion: erosion
            ? new VfxAlphaErosion("ASSETS/Test/e.dds", new Vector4(1f, 0f, 0f, 0f), VfxCurveF.Const(0.5f), 1.5f, 0.1f, 0.1f)
            : null,
        SoftParticle: soft ? new VfxSoftParticle(0f, 1f, 0f, 1f) : null,
        PrimitiveClass: primitive.Length == 0 ? 0u : Core.Hashing.HashAlgorithms.Fnv1a(primitive),
        Extras: uvMode is null ? null : new VfxEmitterExtras { UvMode = uvMode });

    // ================================================================ the address mode

    [Fact]
    public void TheErosionMapCarriesItsOwnAddressMode()
    {
        var e = Emitter(null, "");
        // absent is -1 on the model and the renderers read that as the declared default, 2 - a MIRROR.
        // 79.6% of the emitters that author an erosion leave the field out, so this is the common case
        // and it was being sampled as a wrap on both renderers.
        Assert.Equal(-1, e.AlphaErosion!.AddressMode);

        var wrapped = e.AlphaErosion with { AddressMode = 0 };
        Assert.Equal(0, wrapped.AddressMode);
    }

    [Fact]
    public void BothRenderersBindTheErosionSamplerPerSlot()
    {
        string? gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        Assert.NotNull(gl);
        // a sampler OBJECT, not texture state: one GL texture is shared across all five particle slots,
        // so per-texture state cannot express two slots wanting different modes - the M635 argument
        Assert.Contains("_gl.BindSampler(5, ErosionSampler(", gl);
        Assert.Contains("private uint ErosionSampler(int addressMode) => PaletteSampler(addressMode < 0 ? 2 : addressMode);", gl);
        // released with the palette's, or the next draw inherits it
        Assert.Contains("_gl.BindSampler(5, 0);", gl);

        string? pipeline = Source("src", "ReyEngine.App", "Services", "VfxD3D11EmitterPipeline.cs");
        Assert.NotNull(pipeline);
        Assert.Contains("mat.SlotAddress ??= new Dictionary<string, int>", pipeline);

        string? dx11 = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        Assert.NotNull(dx11);
        // the third of Riot's three modes had no state at all before this
        Assert.Contains("TextureAddressMode.Mirror", dx11);
        Assert.Contains("private ComPtr<ID3D11SamplerState> AuthoredSampler(int mode)", dx11);
    }

    // ================================================================ the fixed-alpha-uv bundle

    [Fact]
    public void ALockAlphaQuadCompilesNeitherErosionNorFade()
    {
        // The client routes it to quad_ps_fixedalphauv, which ships 64 permutations over six axes and has
        // no ALPHA_EROSION and no SOFT_PARTICLES among them - read off Riot's own shader cache.
        foreach (var primitive in new[] { "", "VfxPrimitiveArbitraryQuad", "VfxPrimitiveRay",
                                          "VfxPrimitiveCameraTrail", "VfxPrimitiveBeam" })
        {
            var defines = VfxShaderFlags.For(Emitter(2, primitive, erosion: true, soft: true), out _);
            Assert.DoesNotContain("ALPHA_EROSION", defines.Keys);
            Assert.DoesNotContain("SOFT_PARTICLES", defines.Keys);
        }
    }

    [Fact]
    public void AMeshKeepsItsErosionUnderLockAlpha()
    {
        // Riot's mesh_ps carries SEPARATE_ALPHA_UV and ALPHA_EROSION together across 1,024 shipped
        // permutations, so the asymmetry is in the shader set rather than a convenience of ours.
        foreach (var primitive in new[] { "VfxPrimitiveMesh", "VfxPrimitiveAttachedMesh" })
        {
            var defines = VfxShaderFlags.For(Emitter(2, primitive, erosion: true, soft: true), out _);
            Assert.Contains("ALPHA_EROSION", defines.Keys);
            Assert.Contains("SOFT_PARTICLES", defines.Keys);
        }
    }

    [Fact]
    public void EveryOtherUvModeIsUntouched()
    {
        // 0 is the default and is written zero times; 1 is screen space; 3 to 5 are the local-space modes.
        // Only 2 selects the bundle, and an emitter that authors no uvMode at all is the ordinary case.
        foreach (int? mode in new int?[] { null, 0, 1, 3, 4, 5 })
        {
            var defines = VfxShaderFlags.For(Emitter(mode, "", erosion: true, soft: true), out _);
            Assert.Contains("ALPHA_EROSION", defines.Keys);
            Assert.Contains("SOFT_PARTICLES", defines.Keys);
        }

        Assert.Equal(2, VfxPrimitiveSupport.LockAlphaUvMode);
        // and the field that decides it is no longer filed as having no effect on the preview
        Assert.DoesNotContain("uvMode", VfxParkedEmitterFields.Names);
        Assert.Null(VfxPreviewCoverage.IgnoredNote(Core.Hashing.HashAlgorithms.Fnv1a("uvMode")));
        Assert.True(VfxPreviewCoverage.IsParsed(Core.Hashing.HashAlgorithms.Fnv1a("uvMode")));
        Assert.True(VfxPrimitiveSupport.DrawsFixedAlphaUv(2, 0u));
        Assert.False(VfxPrimitiveSupport.DrawsFixedAlphaUv(2, Core.Hashing.HashAlgorithms.Fnv1a("VfxPrimitiveMesh")));
        Assert.False(VfxPrimitiveSupport.DrawsFixedAlphaUv(null, 0u));
    }

    [Fact]
    public void TheRenderersAskTheSameQuestionTheDefinesDo()
    {
        string? gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        Assert.NotNull(gl);
        // the quad erosion gate and the mesh erosion gate, both through the one helper rather than a copied
        // test - and since M720 the quad's soft-fade gate, which quad_ps_fixedalphauv has no axis for either
        Assert.Equal(3, gl!.Split("VfxPrimitiveSupport.DrawsFixedAlphaUv(").Length - 1);

        string? flags = Source("src", "ReyEngine.Formats", "Vfx", "VfxShaderFlags.cs");
        Assert.NotNull(flags);
        Assert.Contains("bool fixedAlphaUv = VfxPrimitiveSupport.DrawsFixedAlphaUv(", flags);
    }

    // ================================================================ what we already had

    [Fact]
    public void TheErosionIsReadAtTheBaseTexturesOwnCoordinate()
    {
        string? gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        Assert.NotNull(gl);
        // the flipbook division happens once, in the vertex shader, so the fragment shader has only the
        // finished atlas coordinate to sample anything with - the reading's bug is structurally impossible
        Assert.Contains("vUv = (vec2(fx, fy) + uvc) / vec2(cols, rows)", gl);
        Assert.Equal(2, gl!.Split("texture(uErosionTex, vUv)").Length - 1);   // the quad and the mesh
        Assert.Contains("texture(uTex, vUv)", gl);

        // and the D3D11 side runs Riot's own shader, so it samples wherever the bytecode says
        string? pipeline = Source("src", "ReyEngine.App", "Services", "VfxD3D11EmitterPipeline.cs");
        Assert.NotNull(pipeline);
        Assert.DoesNotContain("ErosionUv", pipeline);
    }
}
