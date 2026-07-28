using System.Collections.Generic;
using ReyEngine.Formats.Shaders;
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
    // the guess both renderers share, kept identical on purpose so they cannot disagree
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
