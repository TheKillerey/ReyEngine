using ReyEngine.Formats.Shaders;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// <para>M231: most of the shader cache pairs a vertex and a pixel TOC under one name — 371 of 462 — but the
/// <c>assets/shaders/hlsl/</c> families put the stage in the NAME instead. <c>particlesystem/quad_vs</c> ships
/// only a <c>.vs.dx11</c>; its partner <c>particlesystem/quad_ps</c> only a <c>.ps.dx11</c>. Asking either for
/// its missing stage returns null, which the preview read as "cannot be previewed" — so every League particle
/// shader was unreachable.</para>
///
/// <para>The rule is the LAST <c>_vs</c>/<c>_ps</c> token rather than a suffix, because it also has to carry
/// <c>quad_vs_fixedalphauv</c> across to <c>quad_ps_fixedalphauv</c>.</para>
/// </summary>
public class ShaderStagePairingTests
{
    [Theory]
    // the particle family, which is what motivated this
    [InlineData("assets/shaders/hlsl/particlesystem/quad_vs", "assets/shaders/hlsl/particlesystem/quad_ps")]
    [InlineData("assets/shaders/hlsl/particlesystem/mesh_vs", "assets/shaders/hlsl/particlesystem/mesh_ps")]
    [InlineData("assets/shaders/hlsl/particlesystem/distortion_mesh_vs", "assets/shaders/hlsl/particlesystem/distortion_mesh_ps")]
    [InlineData("assets/shaders/hlsl/skinnedmesh/particle_vs", "assets/shaders/hlsl/skinnedmesh/particle_ps")]
    // infix, not suffix - the case a naive EndsWith rule gets wrong
    [InlineData("assets/shaders/hlsl/particlesystem/quad_vs_fixedalphauv", "assets/shaders/hlsl/particlesystem/quad_ps_fixedalphauv")]
    public void Vertex_name_maps_to_its_pixel_partner(string vsName, string psName)
    {
        Assert.Equal(psName, ShaderCacheReader.PartnerStageName(vsName, DxbcStage.Pixel));
        Assert.Equal(vsName, ShaderCacheReader.PartnerStageName(psName, DxbcStage.Vertex));
    }

    [Theory]
    // no _vs / _ps token anywhere: there is nothing to rename, and inventing one would silently
    // preview a different shader
    [InlineData("assets/shaders/generated/shaders/staticmesh/defaultenv_flat")]
    [InlineData("assets/shaders/hlsl/ui/animation")]
    [InlineData("assets/shaders/hlsl/gamma/post_effect")]
    public void Names_with_no_stage_token_have_no_partner(string name)
    {
        Assert.Null(ShaderCacheReader.PartnerStageName(name, DxbcStage.Pixel));
        Assert.Null(ShaderCacheReader.PartnerStageName(name, DxbcStage.Vertex));
    }

    [Fact]
    public void Renaming_is_reversible()
    {
        const string vs = "assets/shaders/hlsl/particlesystem/shadow_quad_vs";
        var ps = ShaderCacheReader.PartnerStageName(vs, DxbcStage.Pixel);
        Assert.NotNull(ps);
        Assert.Equal(vs, ShaderCacheReader.PartnerStageName(ps!, DxbcStage.Vertex));
    }

    [Fact]
    public void Only_the_last_token_is_rewritten()
    {
        // A path with an EARLIER "_vs" in it must have only the last one rewritten.
        Assert.Equal("assets/shaders/hlsl/a_vs_b/quad_ps",
            ShaderCacheReader.PartnerStageName("assets/shaders/hlsl/a_vs_b/quad_vs", DxbcStage.Pixel));
    }
}
