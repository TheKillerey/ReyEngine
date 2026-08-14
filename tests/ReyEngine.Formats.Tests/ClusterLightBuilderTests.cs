using System.Numerics;
using ReyEngine.Formats.Lighting;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M456: the CPU half of Riot's clustered forward light path.
///
/// <para>Every assertion here is against docs/research/light-system.md §2.1-§2.4, which was measured out of
/// the compiled DXBC in ShaderCache.dx11.wad.client - Mantis_Env_Baked_PBR blob 27 lines 636-741 and
/// DefaultEnv_Flat blob 226 lines 206-278. This is pure bit packing feeding a shader we cannot modify and
/// whose failure mode is a black screen with no error, so it is asserted directly rather than inferred from
/// a rendered frame.</para>
/// </summary>
public class ClusterLightBuilderTests
{
    private static ClusterLight L(float x, float y, float z, float r,
        float cr = 1f, float cg = 1f, float cb = 1f, float intensity = 1f)
        => new(new Vector3(x, y, z), r, new Vector3(cr, cg, cb), intensity);

    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    // ------------------------------------------------------------------ §2.2 the header

    [Fact]
    public void Header_packs_the_point_count_in_the_low_half_of_x()
    {
        var (x, y, z, w) = ClusterLightBuilder.Header(5);
        Assert.Equal(5u, x & 0xFFFFu);      // loop 1: plain point lights
        Assert.Equal(0u, x >> 16);          // loop 2: point + stationary mask - out of scope
        Assert.Equal(0u, y);                // loops 4 and 5: spot + cookie
        Assert.Equal(0u, z);                // loops 3 and 6: cube shadow / spot PCF
        Assert.Equal(ClusterLightBuilder.VisibilityMaskAll, w & 0xFFFFu);
        Assert.Equal(1u, w >> 16);          // 5 lights -> one uint4 of masks
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(8, 1)]      // exactly one uint4 of 8 x uint16
    [InlineData(9, 2)]      // one over, so a second word
    [InlineData(16, 2)]
    [InlineData(64, 8)]
    public void Mask_list_holds_eight_masks_per_uint4(int lights, int expectedWords)
    {
        Assert.Equal(expectedWords, ClusterLightBuilder.MaskWordCount(lights));
        var (_, _, _, w) = ClusterLightBuilder.Header(lights);
        // The shader reads the payload cursor as clusterIdx + 1 + (header.w >> 16), so this field IS the
        // offset from the header to the first light record. Getting it wrong reads masks as light data.
        Assert.Equal((uint)expectedWords, w >> 16);
    }

    [Fact]
    public void Block_size_is_header_plus_masks_plus_two_words_per_light()
    {
        Assert.Equal(1 + 0 + 0, ClusterLightBuilder.BlockUint4Count(0));
        Assert.Equal(1 + 1 + 2, ClusterLightBuilder.BlockUint4Count(1));
        Assert.Equal(1 + 1 + 16, ClusterLightBuilder.BlockUint4Count(8));
        Assert.Equal(1 + 2 + 18, ClusterLightBuilder.BlockUint4Count(9));
    }

    // ------------------------------------------------------------------ §2.4 the light record

    [Fact]
    public void Light_record_is_two_uint4s_of_bit_cast_floats()
    {
        Span<uint> rec = stackalloc uint[8];
        ClusterLightBuilder.WriteLightRecord(rec, L(10f, 20f, 30f, 4f, 0.25f, 0.5f, 0.75f, 3f));

        // word0 = float4(position.xyz, 1/radius). The shader does L = w0.xyz - P, then
        // atten = 1 - saturate(dist * w0.w) - LINEAR falloff, so .w is the RECIPROCAL of the radius and
        // not the radius itself. Writing the radius makes every light a pinpoint at any sane range.
        Assert.Equal(10f, F(rec[0]));
        Assert.Equal(20f, F(rec[1]));
        Assert.Equal(30f, F(rec[2]));
        Assert.Equal(0.25f, F(rec[3]));

        // word1 = float4(colour.rgb, intensity). The Lambert family (DefaultEnv_Flat) loads only .xyz;
        // the PBR family (Mantis) also multiplies by .w.
        Assert.Equal(0.25f, F(rec[4]));
        Assert.Equal(0.5f, F(rec[5]));
        Assert.Equal(0.75f, F(rec[6]));
        Assert.Equal(3f, F(rec[7]));
    }

    [Fact]
    public void Record_floats_are_bit_casts_not_numeric_conversions()
    {
        Span<uint> rec = stackalloc uint[8];
        ClusterLightBuilder.WriteLightRecord(rec, L(1f, 0f, 0f, 1f));
        // 1.0f is 0x3F800000. A uint conversion would have written 1.
        Assert.Equal(0x3F800000u, rec[0]);
        Assert.NotEqual(1u, rec[0]);
    }

    [Fact]
    public void Strength_is_folded_into_the_colour_because_half_the_shaders_never_read_word1_w()
    {
        // DefaultEnv_Flat blob 226 line 263 loads word1 as `r13.xyw ... t6.xyxz` - components x, y and z
        // of the buffer only. A strength written into word1.w would be invisible on nearly every shipped
        // map material and visible on Mantis, which is worse than being wrong everywhere.
        var l = ClusterLightBuilder.MakeLight(new Vector3(1f, 2f, 3f), 10f, new Vector3(0.5f, 0.25f, 1f), 4f);
        Assert.Equal(new Vector3(2f, 1f, 4f), l.Color);
        Assert.Equal(1f, l.Intensity);          // NOT 4 as well - PBR would then square it
        Assert.Equal(10f, l.Radius);
        Assert.Equal(new Vector3(1f, 2f, 3f), l.Position);

        Span<uint> rec = stackalloc uint[8];
        ClusterLightBuilder.WriteLightRecord(rec, l);
        Assert.Equal(2f, F(rec[4]));
        Assert.Equal(1f, F(rec[7]));
    }

    // ------------------------------------------------------------------ §2.1 the grid transform

    [Fact]
    public void World_to_cluster_is_the_affine_map_in_constant_register_order()
    {
        // A 100-unit cube, 10 cells on X. The shader does dp4 against float4(worldPos, 1), so register 0
        // must be (scaleX, 0, 0, biasX) - NOT a transposed matrix row.
        var grid = ClusterLightBuilder.Build(
            Array.Empty<ClusterLight>(), new Vector3(0f), new Vector3(100f), 10, 10, 10);
        var m = grid.WorldToCluster;

        Assert.Equal(0.1f, m[0], 5);
        Assert.Equal(0f, m[1]); Assert.Equal(0f, m[2]);
        Assert.Equal(0f, m[3], 5);                   // bias: -min * scale, and min is 0

        Assert.Equal(0f, m[4]); Assert.Equal(0.1f, m[5], 5); Assert.Equal(0f, m[6]); Assert.Equal(0f, m[7], 5);
        Assert.Equal(0f, m[8]); Assert.Equal(0f, m[9]); Assert.Equal(0.1f, m[10], 5); Assert.Equal(0f, m[11], 5);
        Assert.Equal(1f, m[15]);

        // CLUSTER_MAX_CLAMP is the last valid INDEX, because the shader clamps then truncates. Writing the
        // dimension instead lets ftoi produce index 10 of a 10-cell axis.
        Assert.Equal(new Vector3(9f, 9f, 9f), grid.MaxClamp);
    }

    [Fact]
    public void Transform_maps_a_world_point_onto_its_own_cell()
    {
        var grid = ClusterLightBuilder.Build(
            Array.Empty<ClusterLight>(), new Vector3(-500f, 0f, -500f), new Vector3(500f, 200f, 500f),
            10, 4, 10);
        var m = grid.WorldToCluster;

        // Reproduce the shader: c = dot(float4(P,1), reg_i), clamp, truncate.
        static int Cell(float[] m, int row, Vector3 p, float clamp)
        {
            float c = p.X * m[row * 4] + p.Y * m[row * 4 + 1] + p.Z * m[row * 4 + 2] + m[row * 4 + 3];
            return (int)MathF.Min(MathF.Max(c, 0f), clamp);
        }

        Assert.Equal(0, Cell(m, 0, new Vector3(-500f, 0f, -500f), grid.MaxClamp.X));
        Assert.Equal(9, Cell(m, 0, new Vector3(500f, 0f, 0f), grid.MaxClamp.X));      // clamped, not 10
        Assert.Equal(5, Cell(m, 0, new Vector3(1f, 0f, 0f), grid.MaxClamp.X));
        Assert.Equal(2, Cell(m, 1, new Vector3(0f, 120f, 0f), grid.MaxClamp.Y));
        Assert.Equal(9, Cell(m, 2, new Vector3(0f, 0f, 499f), grid.MaxClamp.Z));
    }

    // ------------------------------------------------------------------ binning

    [Fact]
    public void A_light_lands_in_exactly_the_cells_its_sphere_touches()
    {
        // 10x1x10 grid over a 100-unit square: cells are 10 units wide. A radius-12 light at the centre of
        // cell (5,0,5) - world (55,0,55) - spans world 43..67 on both axes, i.e. cells 4..6.
        //
        // Radius 12 rather than 15 on purpose: 15 puts the sphere's far edge at exactly 70, the shared
        // boundary between cells 6 and 7, and the conservative AABB then includes cell 7 as well. That is
        // correct - over-inclusion costs an atten <= 0 rejection in the shader and nothing else - but it
        // makes the test about float rounding at a boundary instead of about the binning rule.
        var grid = ClusterLightBuilder.Build(
            new[] { L(55f, 0f, 55f, 12f) },
            new Vector3(0f, 0f, 0f), new Vector3(100f, 0f, 100f), 10, 1, 10);

        var lit = new HashSet<(int X, int Z)>();
        for (int z = 0; z < 10; z++)
        for (int x = 0; x < 10; x++)
            if (grid.Map[x + z * 10] != 0) lit.Add((x, z));

        Assert.Equal(9, lit.Count);                 // 3 x 3
        for (int z = 4; z <= 6; z++)
        for (int x = 4; x <= 6; x++)
            Assert.Contains((x, z), lit);
        Assert.DoesNotContain((3, 5), lit);
        Assert.DoesNotContain((7, 5), lit);
        Assert.DoesNotContain((5, 3), lit);
    }

    [Fact]
    public void The_grid_grows_to_contain_a_light_placed_outside_the_geometry()
    {
        // Geometry is a unit box at the origin; the light is 1,000 units away with radius 50. If the grid
        // only covered the geometry, every world X would collapse onto the same cell and this light would
        // light the box it is nowhere near.
        var grid = ClusterLightBuilder.Build(
            new[] { L(1000f, 0f, 0f, 50f) }, new Vector3(-1f), new Vector3(1f), 8, 1, 8);

        // Z was expanded symmetrically (-50..50 against a 2-unit box), so the geometry sits at z cell 4.
        int geometryCell = 0 + 0 * 8 + 4 * 8;
        Assert.Equal(0u, grid.Map[geometryCell]);          // the box is NOT lit

        // ...and the far +X column is, which is where the light actually is.
        Assert.NotEqual(0u, grid.Map[7 + 0 * 8 + 4 * 8]);
        Assert.True(grid.NonEmptyCells > 0);
        Assert.True(grid.NonEmptyCells < grid.CellCount);  // it did not smear over the whole grid
    }

    // ------------------------------------------------------------------ empty cells

    [Fact]
    public void Every_empty_cell_points_at_a_zeroed_header_the_shader_skips()
    {
        var grid = ClusterLightBuilder.Build(
            new[] { L(0f, 0f, 0f, 1f) }, new Vector3(-100f), new Vector3(100f), 8, 2, 8);

        Assert.Equal(8 * 2 * 8, grid.Map.Length);
        Assert.True(grid.NonEmptyCells < grid.CellCount);   // the light cannot reach every cell

        // Element 0 is the shared empty header. header.w & 0xFFFF is the cluster's aggregate visibility
        // mask, and a zero there makes the shader's two-byte AND fail before it reads a single count -
        // so an empty cell costs one texture load and nothing else.
        Assert.Equal(0u, grid.Data[0]);
        Assert.Equal(0u, grid.Data[1]);
        Assert.Equal(0u, grid.Data[2]);
        Assert.Equal(0u, grid.Data[3]);

        int empties = 0;
        foreach (uint at in grid.Map) if (at == 0) empties++;
        Assert.Equal(grid.CellCount - grid.NonEmptyCells, empties);
    }

    [Fact]
    public void An_empty_scene_still_produces_a_valid_buffer()
    {
        var grid = ClusterLightBuilder.Empty(4, 2, 4);
        Assert.Equal(0, grid.LightCount);
        Assert.Equal(0, grid.NonEmptyCells);
        Assert.Equal(32, grid.Map.Length);
        Assert.All(grid.Map, at => Assert.Equal(0u, at));
        Assert.Equal(1, grid.Uint4Count);                   // just the shared zero header
        Assert.Equal(new[] { 0u, 0u, 0u, 0u }, grid.Data);
    }

    // ------------------------------------------------------------------ end-to-end block layout

    [Fact]
    public void A_cell_block_is_header_then_masks_then_records_in_that_order()
    {
        // One light, one cell: the grid is a single cell so the whole map points at one block.
        var light = L(3f, 4f, 5f, 2f, 0.1f, 0.2f, 0.3f, 7f);
        var grid = ClusterLightBuilder.Build(
            new[] { light }, new Vector3(0f), new Vector3(1f), 1, 1, 1);

        uint at = Assert.Single(grid.Map);
        Assert.Equal(1u, at);                               // element 0 is the shared empty header

        int h = (int)at * 4;
        Assert.Equal(1u, grid.Data[h] & 0xFFFFu);           // one point light
        Assert.Equal(0u, grid.Data[h + 1]);
        Assert.Equal(0u, grid.Data[h + 2]);
        Assert.Equal(0xFFFFu, grid.Data[h + 3] & 0xFFFFu);  // cluster visibility mask
        Assert.Equal(1u, grid.Data[h + 3] >> 16);           // one uint4 of per-light masks

        // The mask list, all bits set: §2.3 is a two-byte AND and BOTH halves must overlap the object's
        // ENV_LIGHTING_MASK. Anything that clears either byte culls the light in silence.
        for (int i = 0; i < 4; i++) Assert.Equal(0xFFFFFFFFu, grid.Data[h + 4 + i]);

        int rec = h + 4 + 4;                                 // header (4) + one mask uint4 (4)
        Assert.Equal(3f, F(grid.Data[rec + 0]));
        Assert.Equal(4f, F(grid.Data[rec + 1]));
        Assert.Equal(5f, F(grid.Data[rec + 2]));
        Assert.Equal(0.5f, F(grid.Data[rec + 3]));           // 1 / 2
        Assert.Equal(0.1f, F(grid.Data[rec + 4]));
        Assert.Equal(0.2f, F(grid.Data[rec + 5]));
        Assert.Equal(0.3f, F(grid.Data[rec + 6]));
        Assert.Equal(7f, F(grid.Data[rec + 7]));
        Assert.Equal(rec + 8, grid.Data.Length);             // nothing trailing
    }

    [Fact]
    public void Cells_covered_by_the_same_lights_share_one_block()
    {
        // A light large enough to cover the whole grid: every cell holds the same one-light set, so the
        // buffer must hold the empty header plus exactly ONE block - not one per cell.
        var grid = ClusterLightBuilder.Build(
            new[] { L(0f, 0f, 0f, 10_000f) }, new Vector3(-100f), new Vector3(100f), 8, 4, 8);

        Assert.Equal(8 * 4 * 8, grid.NonEmptyCells);
        Assert.Equal(1, grid.DistinctBlocks);
        Assert.Equal(1 + ClusterLightBuilder.BlockUint4Count(1), grid.Uint4Count);
        Assert.All(grid.Map, at => Assert.Equal(1u, at));
    }

    [Fact]
    public void Two_lights_in_one_cell_produce_two_records_and_one_mask_word()
    {
        var grid = ClusterLightBuilder.Build(
            new[] { L(0f, 0f, 0f, 1000f), L(1f, 1f, 1f, 1000f) },
            new Vector3(0f), new Vector3(1f), 1, 1, 1);

        int h = (int)grid.Map[0] * 4;
        Assert.Equal(2u, grid.Data[h] & 0xFFFFu);
        Assert.Equal(0u, grid.Data[h] >> 16);   // the point count must NOT leak into loop 2's field
        Assert.Equal(1u, grid.Data[h + 3] >> 16);
        Assert.Equal(1 + ClusterLightBuilder.BlockUint4Count(2), grid.Uint4Count);
    }

    // ------------------------------------------------------------------ refusals and caps

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_light_with_no_usable_radius_is_dropped_rather_than_packed(float radius)
    {
        // 1/0 is infinity and 1/NaN is NaN; either poisons the shader's saturate for the whole cell.
        var grid = ClusterLightBuilder.Build(
            new[] { L(0f, 0f, 0f, radius) }, new Vector3(-10f), new Vector3(10f), 4, 1, 4);
        Assert.Equal(0, grid.LightCount);
        Assert.Equal(0, grid.NonEmptyCells);
        Assert.Equal(1, grid.Uint4Count);
    }

    [Fact]
    public void A_degenerate_bounding_box_still_produces_a_finite_transform()
    {
        var grid = ClusterLightBuilder.Build(
            Array.Empty<ClusterLight>(), new Vector3(7f), new Vector3(7f), 4, 4, 4);
        foreach (float f in grid.WorldToCluster) Assert.True(float.IsFinite(f));
    }

    [Fact]
    public void The_per_cell_cap_is_reported_rather_than_silently_truncating()
    {
        var lights = new List<ClusterLight>();
        for (int i = 0; i < 10; i++) lights.Add(L(0f, 0f, 0f, 1000f));
        var grid = ClusterLightBuilder.Build(
            lights, new Vector3(0f), new Vector3(1f), 1, 1, 1, maxLightsPerCell: 4);

        int h = (int)grid.Map[0] * 4;
        Assert.Equal(4u, grid.Data[h] & 0xFFFFu);
        Assert.Equal(6, grid.DroppedBindings);
    }

    [Fact]
    public void Object_and_light_masks_agree_so_the_two_byte_AND_passes()
    {
        // §2.3: visible = (mask & ENV & 0x00FF) != 0 && (mask & ENV & 0xFF00) != 0. Asserted as the shader
        // evaluates it, because the failure mode is that every light is culled and nothing says so.
        uint env = ClusterLightBuilder.EnvLightingMaskAll;
        uint mask = ClusterLightBuilder.VisibilityMaskAll;
        Assert.NotEqual(0u, mask & (env & 0x00FFu));
        Assert.NotEqual(0u, mask & (env & 0xFF00u));
    }

    [Fact]
    public void Grid_dimensions_are_clamped_to_something_a_texture_can_hold()
    {
        var grid = ClusterLightBuilder.Build(
            Array.Empty<ClusterLight>(), new Vector3(0f), new Vector3(1f), 0, -4, 100_000);
        Assert.Equal(1, grid.DimX);
        Assert.Equal(1, grid.DimY);
        Assert.Equal(256, grid.DimZ);
        Assert.Equal(grid.CellCount, grid.Map.Length);
    }

    [Fact]
    public void Map_is_laid_out_x_fastest_then_y_then_z()
    {
        // The upload hands this array straight to a Texture3D with rowPitch = DimX*4 and
        // slicePitch = DimX*DimY*4, so the CPU index order has to be exactly that. A light confined to one
        // cell is the only way to see the ordering.
        var grid = ClusterLightBuilder.Build(
            new[] { L(0.5f, 0.5f, 2.5f, 0.1f) }, new Vector3(0f), new Vector3(4f, 4f, 4f), 4, 4, 4);

        int lit = -1;
        for (int i = 0; i < grid.Map.Length; i++) if (grid.Map[i] != 0) { lit = i; break; }
        Assert.Equal(0 + 0 * 4 + 2 * 16, lit);
    }
}
