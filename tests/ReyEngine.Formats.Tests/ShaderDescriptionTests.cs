using System.Collections.Generic;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M241 (phase 1): the backend-neutral description layer. These pin the two design decisions that are easy
/// to "simplify" later into something wrong — that state is keyed alongside the shader, and that GPU vendor
/// is not in the key at all.
/// </summary>
public class ShaderDescriptionTests
{
    private static DxbcShader Refl(byte[] bytes) => new() { Bytecode = bytes, Stage = DxbcStage.Vertex };

    private static ShaderDescription Desc(string name, ulong key, byte[] bytes,
        Dictionary<string, string>? defines = null) =>
        new(name, DxbcStage.Vertex, key, 0, defines ?? new Dictionary<string, string>(), Refl(bytes));

    [Fact]
    public void Define_signature_is_order_independent()
    {
        // Two dictionaries with the same content in different insertion order describe the same variant,
        // and must not produce two cache entries.
        var a = Desc("s", 1, new byte[] { 1 }, new() { ["B"] = "1", ["A"] = "1" });
        var b = Desc("s", 1, new byte[] { 1 }, new() { ["A"] = "1", ["B"] = "1" });
        Assert.Equal(a.DefineSignature, b.DefineSignature);
    }

    [Fact]
    public void No_defines_reads_as_the_base_permutation_not_an_empty_string()
    {
        Assert.Equal("(base)", Desc("s", 0, new byte[] { 1 }).DefineSignature);
    }

    [Fact]
    public void Blend_is_part_of_the_pipeline_key()
    {
        // The same shader permutation drawn additively and drawn with straight alpha is two pipelines.
        // Particle emitters inside one system routinely mix both, so this is not a corner case.
        var vs = Desc("vs", 10, new byte[] { 1, 2 });
        var ps = Desc("ps", 20, new byte[] { 3, 4 });

        var additive = PipelineKey.For(vs, ps, StateDescription.Particle(BlendKind.Additive), "15.1", RenderBackend.D3D11);
        var alpha = PipelineKey.For(vs, ps, StateDescription.Particle(BlendKind.Alpha), "15.1", RenderBackend.D3D11);

        Assert.NotEqual(additive, alpha);
    }

    [Fact]
    public void Identical_inputs_produce_an_identical_key()
    {
        var vs = Desc("vs", 10, new byte[] { 1, 2 });
        var ps = Desc("ps", 20, new byte[] { 3, 4 });
        Assert.Equal(
            PipelineKey.For(vs, ps, StateDescription.Geometry, "15.1", RenderBackend.D3D11),
            PipelineKey.For(vs, ps, StateDescription.Geometry, "15.1", RenderBackend.D3D11));
    }

    [Fact]
    public void Changed_bytecode_changes_the_key_even_under_the_same_name_and_version()
    {
        // This is what makes the cache survive a Riot patch that rewrites a blob in place.
        var ps = Desc("ps", 20, new byte[] { 3, 4 });
        var before = PipelineKey.For(Desc("vs", 10, new byte[] { 1, 2 }), ps, StateDescription.Geometry, "15.1", RenderBackend.D3D11);
        var after = PipelineKey.For(Desc("vs", 10, new byte[] { 1, 9 }), ps, StateDescription.Geometry, "15.1", RenderBackend.D3D11);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Bytecode_hash_separates_the_two_stages()
    {
        // Without a separator, (vs="ab", ps="") and (vs="a", ps="b") would collide.
        Assert.NotEqual(
            PipelineKey.HashBytecode(new byte[] { 1, 2 }, System.Array.Empty<byte>()),
            PipelineKey.HashBytecode(new byte[] { 1 }, new byte[] { 2 }));
    }

    [Fact]
    public void Backend_is_part_of_the_key()
    {
        var vs = Desc("vs", 10, new byte[] { 1 });
        var ps = Desc("ps", 20, new byte[] { 2 });
        Assert.NotEqual(
            PipelineKey.For(vs, ps, StateDescription.Geometry, "15.1", RenderBackend.D3D11),
            PipelineKey.For(vs, ps, StateDescription.Geometry, "15.1", RenderBackend.OpenGL));
    }

    [Theory]
    // The texture-BLIND fallback, which is all this table can be. Modes 2 and 3 are mixed populations
    // whose real answer comes from the sprite (VfxShaderFlags.IsAdditive(int, bool?)); they appear here as
    // Alpha because that is what an unknown sprite falls back to, NOT because the integer decides them.
    //
    // This assertion restates the table, so on its own it can only fail when someone edits the table - it
    // is a change-detector, not evidence. The evidence lives in Mode_2_is_decided_by_the_sprite below and
    // in VfxShaderFlags' comment; what this one is FOR is pinning the fallback, because a silent change to
    // the unknown-sprite case would otherwise be invisible.
    [InlineData(0, BlendKind.Alpha)]
    [InlineData(1, BlendKind.Additive)]
    [InlineData(2, BlendKind.Alpha)]
    [InlineData(3, BlendKind.Additive)]
    [InlineData(4, BlendKind.Additive)]
    [InlineData(5, BlendKind.Additive)]
    public void Riot_blend_modes_map_as_both_renderers_already_assume(int mode, BlendKind expected)
    {
        Assert.Equal(expected, StateDescription.BlendFromRiotMode(mode));
        Assert.True(StateDescription.IsBlendModeUnderstood(mode));
    }

    /// <summary>
    /// M273. Mode 2 was rendered as Alpha unconditionally, and on Map22's Rising_Mist_Supernova that painted
    /// solid black rectangles: isolated over a mid-grey clear, impactStones_smoke darkened 100.0% of the
    /// 3,271 px it covered with a worst channel drop of 127 (grey 127 straight to 0), while the identical
    /// quads forced additive darkened 0 px. Its sprite TFT_PDM_Cosmic_Spark_2x2 is 0.0% alpha-varied and
    /// 100% opaque - additive art with no silhouette in alpha at all, so alpha blending had nothing to mask
    /// the black field with.
    ///
    /// The mode cannot simply be flipped, because M117 assigned it Alpha for a real reason: Kayn's
    /// BlackMotes and Darkunderglow_* are DARK effects and additive can only brighten. Those sprites measure
    /// 99.6%, 99.7% and 100.0% alpha-varied, so the per-sprite rule keeps them on Alpha. Both observations
    /// are satisfied at once, which is why the split is the fix and a flip is not.
    ///
    /// Corpus check behind the numbers: 8,189 mode-2 emitters, 2,114 distinct sprites -> 67% of sprites
    /// (62% of emitters) have no real alpha and become additive. M260's independent border-darkness census
    /// put mode 2 at 65% additive, so two unrelated measurements agree.
    /// </summary>
    [Theory]
    // mode, sprite uses its alpha channel, expected additive
    [InlineData(2, false, true)]    // TFT_PDM_Cosmic_Spark_2x2 - the black rectangles
    [InlineData(2, true, false)]    // Kayn_base_w_slayer_glow - Darkunderglow must still be able to darken
    [InlineData(3, false, true)]    // M117c, unchanged: Kayn R scythe flipbooks
    [InlineData(3, true, false)]    // M117c, unchanged: skin02 R ghost reusing the skin's TX_CM
    [InlineData(1, true, true)]     // every other mode ignores the sprite entirely
    [InlineData(1, false, true)]
    [InlineData(4, true, true)]
    [InlineData(5, true, true)]
    [InlineData(0, false, false)]
    public void Mode_2_is_decided_by_the_sprite(int mode, bool spriteUsesAlpha, bool expectedAdditive)
        => Assert.Equal(expectedAdditive, VfxShaderFlags.IsAdditive(mode, spriteUsesAlpha));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void An_unknown_sprite_falls_back_to_the_blind_table_rather_than_guessing(int mode)
    {
        // Null is "nobody resolved the sprite", which happens when a stage binds the soft-dot fallback.
        // Falling back preserves the behaviour that shipped rather than picking the majority cluster.
        Assert.Equal(VfxShaderFlags.IsAdditive(mode), VfxShaderFlags.IsAdditive(mode, null));
    }

    [Fact]
    public void Alpha_use_is_measured_with_a_tolerance_because_encoders_scatter_254s()
    {
        // 4 px, all alpha 255: flat opaque, no silhouette -> additive art.
        Assert.False(VfxShaderFlags.TextureUsesAlpha(new byte[] { 0,0,0,255, 0,0,0,255, 0,0,0,255, 0,0,0,255 }));

        // A real silhouette: half the sprite transparent.
        Assert.True(VfxShaderFlags.TextureUsesAlpha(new byte[] { 0,0,0,0, 0,0,0,0, 0,0,0,255, 0,0,0,255 }));

        // 254 is NOT alpha use - that is BC encoder noise on flat-opaque art, and an exact ==255 test would
        // read it as a silhouette and send genuinely additive sprites down the alpha path.
        Assert.False(VfxShaderFlags.TextureUsesAlpha(new byte[] { 0,0,0,254, 0,0,0,254, 0,0,0,255, 0,0,0,255 }));

        // The threshold is a >1% SHARE, so one transparent pixel in a large sprite is still "no alpha".
        var mostlyOpaque = new byte[400 * 4];
        for (int i = 3; i < mostlyOpaque.Length; i += 4) mostlyOpaque[i] = 255;
        mostlyOpaque[3] = 0;                                  // 1 of 400 = 0.25%
        Assert.False(VfxShaderFlags.TextureUsesAlpha(mostlyOpaque));
        for (int i = 3; i < 10 * 4; i += 4) mostlyOpaque[i] = 0;   // 10 of 400 = 2.5%
        Assert.True(VfxShaderFlags.TextureUsesAlpha(mostlyOpaque));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Modes_past_the_table_are_reported_as_not_understood(int mode)
    {
        // 258 emitters use these. They still get a blend so something draws, but a caller can mark the
        // result approximate rather than presenting a guess as fact.
        Assert.False(StateDescription.IsBlendModeUnderstood(mode));
    }

    [Fact]
    public void Particles_disable_the_depth_test_and_geometry_does_not()
    {
        Assert.False(StateDescription.Particle(BlendKind.Additive).DepthTest);
        Assert.False(StateDescription.Particle(BlendKind.Additive).DepthWrite);
        Assert.True(StateDescription.Geometry.DepthTest);
    }

    [Fact]
    public void Back_face_culling_is_off_for_every_preset()
    {
        // League's art is authored single-sided; capes and foliage cards are meant to be seen from behind.
        Assert.False(StateDescription.Geometry.CullBackFaces);
        Assert.False(StateDescription.Particle(BlendKind.Alpha).CullBackFaces);
    }
}
