using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M707, the first of the ltk-manager readings ported back: an emitter that names NO texture is answered by
/// the engine with a 1x1 TRANSPARENT BLACK on the base sampler, so it draws nothing of its own. A texture
/// that IS named and cannot be read is a different case entirely.
///
/// <para>We had one case where the engine has two, and each of our three hosts got it wrong in its own way.
/// Both GL viewports substituted the soft placeholder dot, so every emitter that never had a sprite sprayed
/// dots across the map. The D3D11 map viewport did the same and counted it as an unresolved sprite. The
/// D3D11 preview window bound nothing at all, which leaves the renderer's opaque 1x1 WHITE on the slot -
/// that is the hard white card reported on Ahri_Skin89_E_mis.</para>
///
/// <para>The soft dot is kept, deliberately, for the case it was written for: a path the editor was asked
/// for and could not find. That is an editor failure and the user should see it. Absence is not failure.</para>
/// </summary>
public sealed class UnnamedTextureTests
{
    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static VfxEmitterDefinition Emitter(string? texture, string? mesh = null) => new(
        Name: "e",
        Rate: VfxCurveF.Const(10f),
        ParticleLifetime: VfxCurveF.Const(1f),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: false,
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
        TexturePath: texture,
        TexDiv: new Vector2(1f, 1f),
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: mesh is not null,
        MeshPath: mesh);

    // ================================================================ the model draws the distinction

    [Fact]
    public void NamingNoTextureIsNotTheSameAsNamingOneThatWillNotLoad()
    {
        Assert.True(Emitter(null).NamesNoTexture);
        Assert.True(Emitter("").NamesNoTexture);
        // this one HAS been asked for. Whether the editor can find it is a separate question, and the
        // answer must not change what the emitter authored.
        Assert.False(Emitter("ASSETS/Nowhere/missing.dds").NamesNoTexture);
        // a path of blanks counts as NAMED, because IsVisual says so and the engine says so. Two
        // predicates over one field that disagree put an emitter on the draw path under one and off it
        // under the other.
        Assert.False(Emitter("   ").NamesNoTexture);
        Assert.True(Emitter("   ").IsVisual);

        // it is independent of IsVisual: a mesh emitter with no sprite still has geometry to draw
        var meshOnly = Emitter(null, "ASSETS/Test/m.scb");
        Assert.True(meshOnly.NamesNoTexture);
        Assert.True(meshOnly.IsVisual);

        // and IsVisual is what decides whether any of this is reached at all. An emitter with no texture
        // and nothing else is dropped by the simulator before a renderer ever asks for its sprite, which
        // is why the population this milestone changes is a few hundred and not the 23,006 emitters in
        // the installed game that name no base texture.
        Assert.False(Emitter(null).IsVisual);
    }

    // ================================================================ the pixels

    [Fact]
    public void TheUnnamedStandInIsOneTransparentTexelAndTheSoftDotIsUntouched()
    {
        byte[] unnamed = VfxPlaybackSim.Unnamed();
        Assert.Equal(4, unnamed.Length);
        // transparent BLACK, not transparent white: the base texel is multiplied through, and under an
        // additive blend a white rgb with zero alpha still lights the pixel in some paths.
        Assert.All(unnamed, b => Assert.Equal(0, b));

        // the soft dot is still a soft dot - opaque white in the middle, transparent at the corner
        byte[] dot = VfxPlaybackSim.SoftDot(64);
        Assert.Equal(64 * 64 * 4, dot.Length);
        Assert.True(dot[((32 * 64) + 32) * 4 + 3] > 200, "the soft dot lost its core");
        Assert.Equal(0, dot[3]);

        // two keys, never one. Both are reserved by a leading space, which no asset path has, so neither
        // can collide with a real texture in the pool.
        Assert.NotEqual(VfxPlaybackSim.SoftDotKey, VfxPlaybackSim.UnnamedKey);
        Assert.StartsWith(" ", VfxPlaybackSim.UnnamedKey);
        Assert.StartsWith(" ", VfxPlaybackSim.SoftDotKey);
    }

    [Fact]
    public void TheD3D11SpriteHasBothStandIns()
    {
        var unnamed = VfxD3D11EmitterPipeline.Sprite.Unnamed;
        Assert.Equal(VfxPlaybackSim.UnnamedKey, unnamed.Key);
        Assert.Null(unnamed.Open);      // no asset to open: the pipeline uploads the texel itself

        var fallback = VfxD3D11EmitterPipeline.Sprite.Fallback;
        Assert.Equal(VfxPlaybackSim.SoftDotKey, fallback.Key);
        Assert.Null(fallback.Open);
    }

    // ================================================================ every host is wired to it

    [Fact]
    public void TheGlHostsBindTheTransparentTexelRatherThanTheSoftDot()
    {
        string? src = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        Assert.NotNull(src);
        // one binding site serves the map viewport, the particle editor, the mesh preview and the workshop
        // preview - they all host this control - so the branch has to be here and only here
        Assert.Contains("es.Texture = es.Def.NamesNoTexture ? _unnamedTex : _softDotTex;", src);
        // uploaded twice for the same reason the soft dot is: at GL init, and again per playback, because
        // ClearTextures owns and deletes both between systems
        Assert.Equal(2, src!.Split("UploadTexture(Services.VfxPlaybackSim.Unnamed(), 1, 1)").Length - 1);
    }

    [Fact]
    public void TheD3D11MapViewportBranchesBeforeItCountsAnUnresolvedSprite()
    {
        string? src = Source("src", "ReyEngine.App", "Services", "D3D11MapParticles.cs");
        Assert.NotNull(src);
        int branch = src!.IndexOf("if (def.NamesNoTexture) { UnnamedSprites++;", StringComparison.Ordinal);
        int count = src.IndexOf("UnresolvedSprites++", StringComparison.Ordinal);
        Assert.True(branch > 0, "the unnamed branch is missing");
        Assert.True(count > branch, "an emitter that names no texture must not be counted as unresolved");
        // it gets its own count and its own report line instead, because an effect that was never drawn
        // and an effect that went missing look identical on screen
        Assert.Contains("UnnamedSprites = 0;", src);
        Assert.Contains("{N(UnnamedSprites)} emitter(s) name no texture at all", src);

        // the mesh and the ribbon both refuse the unnamed sprite as well: they are far too large a surface
        // to hand a stand-in of any kind. Asked of the SET, so the next stand-in cannot walk past a pair of
        // hand-copied literals the way this one did.
        Assert.Equal(2, src.Split("VfxPlaybackSim.IsStandIn(sprite.Key)").Length - 1);
        Assert.True(VfxPlaybackSim.IsStandIn(VfxPlaybackSim.SoftDotKey));
        Assert.True(VfxPlaybackSim.IsStandIn(VfxPlaybackSim.UnnamedKey));
        Assert.False(VfxPlaybackSim.IsStandIn("assets/test/p.dds"));
        Assert.False(VfxPlaybackSim.IsStandIn(null));
    }

    [Fact]
    public void TheD3D11PreviewWindowNoLongerLeavesTheSlotUnbound()
    {
        string? src = Source("src", "ReyEngine.App", "Services", "D3D11ParticlePlayback.cs");
        Assert.NotNull(src);
        // the white card: a blank path returned null, nothing was bound, and the renderer's own opaque
        // 1x1 white stood in. Only the BASE stage changes - the others are genuinely optional.
        Assert.Contains("sampler == \"TEXTURE\" ? VfxD3D11EmitterPipeline.Sprite.Unnamed : null", src);
        // and the same window drew mesh emitters the other two hosts refuse
        Assert.Contains("VfxPlaybackSim.IsStandIn(meshSprite.Key)", src);

        string? pipeline = Source("src", "ReyEngine.App", "Services", "VfxD3D11EmitterPipeline.cs");
        Assert.NotNull(pipeline);
        Assert.Contains("VfxPlaybackSim.Unnamed(), 1, 1", pipeline);
        // the white card had a SECOND arm, one level down: a texture that IS named and cannot be read
        // bound nothing at all. The base stage now falls back the way the other two hosts do.
        Assert.Contains("[unreadable: soft-dot fallback]", pipeline);
        Assert.Contains("if (sampler != \"TEXTURE\") return null;", pipeline);
    }

    [Fact]
    public void TheHeatHazePassDoesNotTintItselfBlack()
    {
        // The distortion pass tints the refracted scene by the emitter's diffuse RGB and takes its alpha
        // from the normal map. Its "this emitter ships no diffuse" identity was a NULL HANDLE test, so
        // binding a transparent texel there would have kept the alpha and multiplied the colour to zero -
        // a black smear precisely where the engine and the OpenGL viewport draw nothing at all.
        string? renderer = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        Assert.NotNull(renderer);
        int refuse = renderer!.IndexOf("if (mat.BaseTextureIsUnnamed) return false;", StringComparison.Ordinal);
        int identity = renderer.IndexOf("if (diffuse.Handle is null) diffuse = _white;", StringComparison.Ordinal);
        Assert.True(refuse > 0, "the heat-haze pass does not refuse an emitter that names no texture");
        Assert.True(refuse < identity, "it must refuse BEFORE the white identity, which no longer fires");

        // and the flag is only ever set for the base slot, by the one place that binds the texel
        string? pipeline = Source("src", "ReyEngine.App", "Services", "VfxD3D11EmitterPipeline.cs");
        Assert.NotNull(pipeline);
        Assert.Contains("if (sampler == \"TEXTURE\") mat.BaseTextureIsUnnamed = true;", pipeline);
    }

    [Fact]
    public void TheTwoRenderersAgreeAboutARibbonWithNoTexture()
    {
        // The D3D11 host refuses a beam or trail with no sprite and says so in its build report. GL guards
        // on the texture HANDLE, and the transparent texel is a handle - so without this the OpenGL
        // viewport would assemble and upload a ribbon every frame in order to draw nothing.
        string? src = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        Assert.NotNull(src);
        Assert.Contains("es.Def.NamesNoTexture && (es.Def.Beam is not null || es.Def.Trail is not null)", src);
    }
}
