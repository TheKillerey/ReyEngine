using System.Numerics;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M465: the CPU half of the sun shadow map.
///
/// <para>Both shaders are Riot's compiled blobs run verbatim, so there is nothing of ours to test in them.
/// What CAN be wrong is everything around them: which way depth increases, whether the NDC-to-texture
/// remap flips the right axis, whether an off-screen caster survives the near plane, and whether the two
/// biases mean anything at the scale the map is drawn at. Every one of those fails silently - the pass
/// runs, the map binds, and the image is either unchanged or subtly wrong - which is exactly why the maths
/// lives in a file with no Direct3D in it.</para>
///
/// <para>Asserted against docs/research/light-system.md §1.7 (the consuming 5-tap PCF and the bias formula)
/// and the M465 disassembly of <c>environment/shadowmap.vs</c>/<c>.ps</c> recorded in SunShadowFit's
/// header.</para>
/// </summary>
public class SunShadowFitTests
{
    // A cube 200 units on a side about the origin, and a sun directly overhead. Chosen so the arithmetic
    // below can be done by hand: the centre lands on the texel grid without being moved by the snap, and
    // the light axis is a world axis.
    private static readonly (Vector3 Min, Vector3 Max) Cube =
        (new Vector3(-100f, -100f, -100f), new Vector3(100f, 100f, 100f));

    private static readonly Vector3 Overhead = new(0f, 1f, 0f);   // points TOWARD the sun

    private static Vector3 Project(Matrix4x4 m, Vector3 world)
    {
        var p = Vector4.Transform(new Vector4(world, 1f), m);
        return new Vector3(p.X, p.Y, p.Z);
    }

    // ------------------------------------------------------------------ the frustum fit

    [Fact]
    public void A_known_box_and_sun_produce_the_expected_orthographic_extents()
    {
        var fit = SunShadowFit.Fit(Overhead, Cube, Cube)!.Value;

        // The square circumscribes the receiver box's bounding SPHERE, so the half-extent is the box's
        // half-diagonal: |(200,200,200)| / 2.
        float expected = new Vector3(200f, 200f, 200f).Length() * 0.5f;
        Assert.Equal(expected, fit.Radius, 3);
        Assert.Equal(2f * expected / SunShadowFit.ShadowMapSize, fit.WorldUnitsPerTexel, 6);

        // Depth: near is pulled back over the whole caster set and out to the sphere, far likewise, then
        // both are padded by max(1, radius/100).
        float pad = MathF.Max(1f, expected * 0.01f);
        Assert.Equal(2f * (expected + pad), fit.DepthRange, 3);
    }

    [Fact]
    public void The_receiver_centre_lands_in_the_middle_of_the_shadow_map()
    {
        var fit = SunShadowFit.Fit(Overhead, Cube, Cube)!.Value;
        var c = Project(fit.ShadowProj, Vector3.Zero);

        Assert.Equal(0.5f, c.X, 4);
        Assert.Equal(0.5f, c.Y, 4);
        Assert.Equal(0.5f, c.Z, 4);
    }

    [Fact]
    public void Every_receiver_corner_lands_inside_the_shadow_map()
    {
        var fit = SunShadowFit.Fit(Overhead, Cube, Cube)!.Value;

        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? Cube.Min.X : Cube.Max.X,
                (i & 2) == 0 ? Cube.Min.Y : Cube.Max.Y,
                (i & 4) == 0 ? Cube.Min.Z : Cube.Max.Z);
            var uv = Project(fit.ShadowProj, corner);

            Assert.InRange(uv.X, 0f, 1f);
            Assert.InRange(uv.Y, 0f, 1f);
            Assert.InRange(uv.Z, 0f, 1f);
        }
    }

    [Fact]
    public void The_fit_survives_a_sun_parallel_to_the_default_up_vector()
    {
        // Straight down is the degenerate case for a look-at built on (0,1,0), and MapSunProperties
        // defaults its direction to exactly (0,1,0) - so this is the DEFAULT, not an edge case.
        Assert.NotNull(SunShadowFit.Fit(new Vector3(0f, 1f, 0f), Cube, Cube));
        Assert.NotNull(SunShadowFit.Fit(new Vector3(0f, -1f, 0f), Cube, Cube));
    }

    [Fact]
    public void A_non_unit_sun_direction_fits_the_same_frustum_as_its_unit_form()
    {
        // Riot ships sun directions up to 8.775 long (M275). Length carries no meaning here.
        var unit = SunShadowFit.Fit(Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.45f)), Cube, Cube)!.Value;
        var scaled = SunShadowFit.Fit(new Vector3(0.4f, 0.8f, 0.45f) * 8.775f, Cube, Cube)!.Value;

        Assert.Equal(unit.Radius, scaled.Radius, 3);
        Assert.Equal(unit.DepthRange, scaled.DepthRange, 2);
        Assert.Equal(unit.ConstantDepthBias, scaled.ConstantDepthBias, 6);
    }

    [Fact]
    public void A_zero_sun_or_an_inverted_box_produces_no_fit()
    {
        Assert.Null(SunShadowFit.Fit(Vector3.Zero, Cube, Cube));
        Assert.Null(SunShadowFit.Fit(Overhead, Cube, (new Vector3(10f), new Vector3(-10f))));
        Assert.Null(SunShadowFit.Fit(Overhead, (new Vector3(float.NaN), new Vector3(1f)), Cube));
        Assert.Null(SunShadowFit.Fit(Overhead, Cube, Cube, size: 0));
    }

    // ------------------------------------------------------------------ depth polarity

    [Fact]
    public void Depth_increases_away_from_the_sun()
    {
        // THE test. Get this backwards and the pass runs, the map binds, every comparison inverts, and the
        // image is lit exactly where it should be shadowed - which reads as "the shadows are wrong" rather
        // than as a sign convention.
        var fit = SunShadowFit.Fit(Overhead, Cube, Cube)!.Value;

        float above = Project(fit.ShadowProj, new Vector3(0f, 100f, 0f)).Z;
        float middle = Project(fit.ShadowProj, Vector3.Zero).Z;
        float below = Project(fit.ShadowProj, new Vector3(0f, -100f, 0f)).Z;

        Assert.True(above < middle, $"a caster nearer the sun must store a SMALLER depth ({above} vs {middle})");
        Assert.True(middle < below);

        // And by the exact amount, so a change of near/far convention cannot pass by staying monotonic.
        float radius = new Vector3(200f, 200f, 200f).Length() * 0.5f;
        float half = radius + MathF.Max(1f, radius * 0.01f);
        Assert.Equal(0.5f - 100f / (2f * half), above, 4);
        Assert.Equal(0.5f + 100f / (2f * half), below, 4);
    }

    [Fact]
    public void A_caster_far_outside_the_receiver_box_still_lands_inside_the_depth_range()
    {
        // The off-screen caster. A frustum fitted only to what is on screen has no depth for the tower
        // standing beside the camera, and its shadow simply vanishes; the near plane is pulled back over
        // the whole caster set precisely to stop that.
        var casters = (new Vector3(-100f, -100f, -100f), new Vector3(100f, 5000f, 100f));
        var fit = SunShadowFit.Fit(Overhead, casters, Cube)!.Value;

        var high = Project(fit.ShadowProj, new Vector3(0f, 5000f, 0f));
        Assert.InRange(high.Z, 0f, 1f);
        // Still the closest thing to the sun in the scene, so still the smallest depth.
        Assert.True(high.Z < Project(fit.ShadowProj, Vector3.Zero).Z);
    }

    // ------------------------------------------------------------------ matrix construction

    [Fact]
    public void ShadowProj_is_the_view_projection_with_the_ndc_to_texture_remap_folded_in()
    {
        // The shading vertex shader takes rows 0-2 of mShadowProj and uses them directly - three dp4s, no
        // divide by w (defaultenv_flat.vs blob 13, lines 107-110) - so the remap has to be IN the matrix.
        var fit = SunShadowFit.Fit(new Vector3(0.4f, 0.8f, 0.45f), Cube, Cube)!.Value;
        var probe = new Vector3(37f, -12f, 61f);

        var ndc = Project(fit.ViewProjection, probe);
        var uv = Project(fit.ShadowProj, probe);

        Assert.Equal(ndc.X * 0.5f + 0.5f, uv.X, 5);
        Assert.Equal(ndc.Y * -0.5f + 0.5f, uv.Y, 5);   // V is flipped; D3D texture space is top-down
        Assert.Equal(ndc.Z, uv.Z, 5);                   // Z is already 0..1 in the D3D convention
    }

    [Fact]
    public void The_orthographic_projection_leaves_w_at_one_so_dropping_the_divide_is_exact()
    {
        // Riot's shading VS never divides by w. That is only safe because the sun frustum is orthographic.
        var fit = SunShadowFit.Fit(new Vector3(0.4f, 0.8f, 0.45f), Cube, Cube)!.Value;
        var p = Vector4.Transform(new Vector4(37f, -12f, 61f, 1f), fit.ShadowProj);
        Assert.Equal(1f, p.W, 6);
    }

    [Fact]
    public void The_centre_is_snapped_to_whole_texels_so_sub_texel_camera_motion_does_not_move_the_map()
    {
        // Without this every shadow edge crawls as the camera moves. Two receiver boxes of the SAME size,
        // whose centres differ by a fraction of a texel WITHIN one texel cell, must produce the identical
        // matrix. (Motion that crosses a cell boundary is meant to move the map, by exactly one texel -
        // that is what quantising is.)
        float texel = SunShadowFit.Fit(Overhead, Cube, Cube)!.Value.WorldUnitsPerTexel;

        // Start the centre mid-cell so neither nudge below can straddle a boundary.
        var midCell = new Vector3(texel * 10.5f, 0f, texel * 10.5f);
        var a = Shifted(midCell);
        var b = Shifted(midCell + new Vector3(texel * 0.1f, 0f, texel * 0.1f));
        var c = Shifted(midCell + new Vector3(texel * 0.4f, 0f, texel * 0.4f));

        Assert.Equal(a.ShadowProj, b.ShadowProj);
        Assert.Equal(a.ShadowProj, c.ShadowProj);

        // A whole texel of motion, on the other hand, must move it - or the snap has become a freeze.
        var moved = Shifted(midCell + new Vector3(texel * 4f, 0f, texel * 4f));
        Assert.NotEqual(a.ShadowProj, moved.ShadowProj);

        SunShadowFrame Shifted(Vector3 by) =>
            SunShadowFit.Fit(Overhead, Cube, (Cube.Min + by, Cube.Max + by))!.Value;
    }

    // ------------------------------------------------------------------ the biases

    [Fact]
    public void The_biases_are_the_chosen_texel_counts_expressed_in_depth_units()
    {
        // light-system.md §1.7: bias = SLOPE_SCALED * (1 - dot(Ngeo, L)) + CONSTANT, subtracted from a
        // depth in [0,1]. So a constant here would mean a different world distance on every map and at
        // every zoom level; both are derived from the fit instead.
        var fit = SunShadowFit.Fit(Overhead, Cube, Cube)!.Value;

        Assert.Equal(
            SunShadowFit.ConstantBiasTexels * fit.WorldUnitsPerTexel / fit.DepthRange,
            fit.ConstantDepthBias, 9);
        Assert.Equal(
            SunShadowFit.SlopeBiasTexels * fit.WorldUnitsPerTexel / fit.DepthRange,
            fit.SlopeScaledDepthBias, 9);
        Assert.True(fit.ConstantDepthBias > 0f);
        Assert.True(fit.SlopeScaledDepthBias > fit.ConstantDepthBias);
    }

    [Fact]
    public void A_bias_in_depth_units_is_the_same_distance_in_world_units_at_any_scale()
    {
        // The property that makes the texel formulation worth having: a map a hundred times bigger gets a
        // hundred times the world bias, which is what keeps acne and peter-panning from swapping places
        // between a prop preview and a whole Summoner's Rift.
        var small = SunShadowFit.Fit(Overhead, Cube, Cube)!.Value;
        var big = SunShadowFit.Fit(Overhead,
            (Cube.Min * 100f, Cube.Max * 100f), (Cube.Min * 100f, Cube.Max * 100f))!.Value;

        float smallWorld = small.ConstantDepthBias * small.DepthRange;
        float bigWorld = big.ConstantDepthBias * big.DepthRange;

        Assert.Equal(SunShadowFit.ConstantBiasTexels * small.WorldUnitsPerTexel, smallWorld, 4);
        Assert.Equal(100f, bigWorld / smallWorld, 2);
    }

    // ------------------------------------------------------------------ the PCF kernel

    [Fact]
    public void The_sample_offsets_are_two_uv_pairs_one_texel_on_each_diagonal()
    {
        var o = SunShadowFit.SampleOffsets(2048);
        float t = 1f / 2048f;

        Assert.Equal(t, o.X, 9);
        Assert.Equal(t, o.Y, 9);
        Assert.Equal(-t, o.Z, 9);
        Assert.Equal(t, o.W, 9);
    }

    [Fact]
    public void The_five_taps_are_five_distinct_texels_forming_a_quincunx()
    {
        // The shader applies them as centre, uv+O1, uv-O1, uv-O2, uv+O2 with ONE shared depth reference
        // (blob 226 lines 179-195; the offset vectors' z is a literal zero at lines 182 and 190).
        var o = SunShadowFit.SampleOffsets(SunShadowFit.ShadowMapSize);
        var centre = new Vector2(0.5f, 0.5f);
        var o1 = new Vector2(o.X, o.Y);
        var o2 = new Vector2(o.Z, o.W);

        var taps = new[] { centre, centre + o1, centre - o1, centre - o2, centre + o2 };
        Assert.Equal(5, taps.Distinct().Count());

        // Each of the four outer taps is one texel away on both axes - a diagonal cross, not a plus.
        float texel = 1f / SunShadowFit.ShadowMapSize;
        foreach (var tap in taps.Skip(1))
        {
            Assert.Equal(texel, MathF.Abs(tap.X - centre.X), 9);
            Assert.Equal(texel, MathF.Abs(tap.Y - centre.Y), 9);
        }
    }

    [Fact]
    public void The_kernel_scales_with_the_map_size_rather_than_assuming_2048()
    {
        Assert.Equal(1f / 1024f, SunShadowFit.SampleOffsets(1024).X, 9);
        Assert.Equal(1f / 4096f, SunShadowFit.SampleOffsets(4096).X, 9);
    }

    // ------------------------------------------------------------------ what gets fitted to

    private static Matrix4x4 TestViewProjection(Vector3 eye, Vector3 target, float far) =>
        Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY)
        * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 1f, 1f, far);

    [Fact]
    public void The_frustum_corners_come_back_in_the_d3d_depth_convention()
    {
        // z in [0,1], not [-1,1]. The GL range would put the four "near" corners behind the eye.
        var eye = new Vector3(0f, 0f, 500f);
        var forward = new Vector3(0f, 0f, -1f);
        var vp = TestViewProjection(eye, Vector3.Zero, 2000f);
        var corners = SunShadowFit.FrustumCorners(vp)!;

        Assert.Equal(8, corners.Length);
        // Measured ALONG the view axis, not as a straight-line distance - a corner of the near plane is
        // further from the eye than the plane itself, by 1/cos of the half-diagonal angle.
        for (int i = 0; i < 4; i++)
            Assert.Equal(1f, Vector3.Dot(corners[i] - eye, forward), 3);
        // The far plane to within 0.05%: unprojecting through a single-precision inverse of a matrix with a
        // 1:2000 near/far ratio loses about four digits, which is a property of the arithmetic rather than
        // of the frustum. The real camera's ratio is far worse (1 to 200,000), and it does not matter -
        // this feeds a bounding box, not a depth test.
        for (int i = 4; i < 8; i++)
            Assert.InRange(Vector3.Dot(corners[i] - eye, forward), 1999f, 2001f);
    }

    [Fact]
    public void The_frustum_is_truncated_at_the_shadow_distance()
    {
        // The camera's own far plane is 200,000 (OrbitCamera.Far). Fitting to that would spend the whole
        // shadow map on empty space beyond the map.
        var vp = TestViewProjection(new Vector3(0f, 0f, 500f), Vector3.Zero, 200000f);
        var corners = SunShadowFit.FrustumCorners(vp, 1000f)!;

        for (int i = 0; i < 4; i++)
            Assert.Equal(1000f, (corners[i + 4] - corners[i]).Length(), 1);
    }

    [Fact]
    public void Receiver_bounds_clip_a_map_sized_slice_down_to_what_is_in_front_of_the_camera()
    {
        // Map geometry is merged into very long runs, so one terrain slice can span the whole map. Without
        // the frustum box that single slice drags the fit out to map scale however close the camera is.
        var vp = TestViewProjection(new Vector3(0f, 200f, 500f), Vector3.Zero, 200000f);
        var wholeMap = (new Vector3(-8000f, -100f, -8000f), new Vector3(8000f, 100f, 8000f));

        Assert.True(SunShadowFit.TryReceiverBounds(vp, 1500f, new[] { wholeMap }, out var bounds));

        var size = bounds.Max - bounds.Min;
        Assert.True(size.X < 4000f, $"X was not clipped: {size.X}");
        Assert.True(size.Z < 4000f, $"Z was not clipped: {size.Z}");
        // And never wider than the slice it came from.
        Assert.True(bounds.Min.X >= wholeMap.Item1.X && bounds.Max.X <= wholeMap.Item2.X);
        Assert.True(bounds.Min.Y >= wholeMap.Item1.Y && bounds.Max.Y <= wholeMap.Item2.Y);
    }

    [Fact]
    public void Receiver_bounds_report_nothing_when_the_camera_looks_away_from_the_map()
    {
        // An ordinary thing for an editor to be doing, and the caller must read it as "no shadow pass this
        // frame" rather than as an error.
        var vp = TestViewProjection(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f), 5000f);
        var behind = (new Vector3(-100f, -100f, -9000f), new Vector3(100f, 100f, -8000f));

        Assert.False(SunShadowFit.TryReceiverBounds(vp, 5000f, new[] { behind }, out _));
        Assert.False(SunShadowFit.TryReceiverBounds(vp, 5000f,
            Array.Empty<(Vector3, Vector3)>(), out _));
    }

    [Fact]
    public void Receiver_bounds_union_every_visible_slice()
    {
        // Not straight down: an up vector of (0,1,0) is degenerate for a camera looking along it, and a
        // NaN view matrix would make this test pass or fail for the wrong reason.
        var vp = TestViewProjection(new Vector3(0f, 400f, 400f), Vector3.Zero, 5000f);
        var a = (new Vector3(-50f, -10f, -50f), new Vector3(-10f, 10f, -10f));
        var b = (new Vector3(10f, -10f, 10f), new Vector3(50f, 10f, 50f));

        Assert.True(SunShadowFit.TryReceiverBounds(vp, 5000f, new[] { a, b }, out var bounds));
        Assert.Equal(-50f, bounds.Min.X, 3);
        Assert.Equal(50f, bounds.Max.Z, 3);
    }

    [Fact]
    public void A_singular_view_projection_produces_no_corners_rather_than_a_nan_frustum()
    {
        Assert.Null(SunShadowFit.FrustumCorners(new Matrix4x4()));
    }
}
