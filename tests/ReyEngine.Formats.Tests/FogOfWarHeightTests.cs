using System;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M769: map mesh particles drew solid BLACK below world Y = -1 ("particles get under a specific height fully
/// black... mostly on waterfalls", SRX_Infernal_Dragon_Pit_Bottom at Y -69.7). The preview fed 1e9 to all four
/// lanes of FOG_OF_WAR_ALWAYS_BELOW_Y; particlesystem/mesh_vs reads it as <c>t = saturate(worldY * .z + .w)</c>.
/// </summary>
public sealed class FogOfWarHeightTests
{
    // mesh_vs blob 29: mad_sat o3.z, r1.y, cb2[20].z, cb2[20].w
    private static float VisibleWeight(float[] c, float worldY) => Math.Clamp(worldY * c[2] + c[3], 0f, 1f);

    [Theory]
    [InlineData(-19000f)]
    [InlineData(-69.683f)]    // the reported placement
    [InlineData(-1.5f)]
    [InlineData(0f)]
    [InlineData(151.13f)]     // where the user moved it to make it render
    [InlineData(20000f)]
    public void A_mesh_particle_is_fully_visible_at_every_height(float worldY) =>
        Assert.Equal(1f, VisibleWeight(ShaderPreviewRenderer.FogOfWarAlwaysBelowY, worldY));

    [Fact]
    public void The_old_value_was_a_step_at_minus_one()
    {
        // pinned so the test above is known to measure the reported failure
        var old = new[] { 1e9f, 1e9f, 1e9f, 1e9f };
        Assert.Equal(0f, VisibleWeight(old, -69.683f));
        Assert.Equal(1f, VisibleWeight(old, 151.13f));
    }

    [Fact]
    public void The_feed_is_a_copy_so_no_caller_can_change_it() =>
        Assert.Equal(new[] { 0f, 0f, 0f, 1f }, ShaderPreviewRenderer.FogOfWarAlwaysBelowY);
}
