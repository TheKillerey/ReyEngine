using System.Numerics;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M461: the particle arithmetic of docs/research/frame-pipeline.md §4.9, held to the disassembly.
///
/// <para>Every failure mode in this file is invisible. A wrong soft-particle band does not draw a bad
/// particle, it draws NO particle; a wrong <c>cSoftParticleControl</c> multiplies the whole output by zero;
/// a distortion offset scaled by the wrong factor still produces a plausible-looking heat haze. None of
/// that reaches a bug report, so it is asserted here instead.</para>
/// </summary>
public class ParticleShadingTests
{
    private const float Eps = 1e-5f;

    // ================================================================ depth conversion

    /// <summary>The whole point of <c>cDepthConversionParams</c>: window depth back to view distance. If
    /// this round trip does not hold, every soft particle fades at the wrong distance and nothing says so.
    /// </summary>
    [Theory]
    [InlineData(1f, 10000f, 50f)]
    [InlineData(1f, 10000f, 500f)]
    [InlineData(1f, 10000f, 9000f)]
    [InlineData(10f, 3000f, 42f)]
    [InlineData(0.1f, 500f, 0.5f)]
    public void Linearise_recovers_the_view_distance_that_produced_the_window_depth(
        float near, float far, float distance)
    {
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 1.6f, near, far);
        var dc = ParticleShading.DepthConversion(proj);

        // Project a point at -distance along view Z (System.Numerics is right-handed, row-vector) and take
        // the perspective divide, which is exactly what the rasteriser writes into the depth buffer.
        var clip = Vector4.Transform(new Vector4(0f, 0f, -distance, 1f), proj);
        float windowZ = clip.Z / clip.W;

        // RELATIVE tolerance, because the quantity being inverted is 1/z in float32: at 90% of the way to
        // the far plane a single-precision window depth is only good to a few parts in 100,000, and that
        // is a property of reciprocal depth rather than of this arithmetic. 0.1% catches a wrong constant
        // (which is out by a factor, not by a rounding) while tolerating the format.
        float got = ParticleShading.Linearise(windowZ, dc);
        Assert.InRange(got, distance * 0.999f, distance * 1.001f);
    }

    [Fact]
    public void DepthConversion_is_the_textbook_D3D_pair_and_not_the_GL_one()
    {
        // GL doubles the slope because its window depth only occupies [0.5,1]. Copying that constant into
        // D3D is a ~2x error that still looks like a plausible soft particle, so it is pinned by name.
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 1.6f, 1f, 1001f);
        var dc = ParticleShading.DepthConversion(proj);
        Assert.Equal(1f / 1f, dc.X, Eps);                  // 1/near
        Assert.Equal(1f / 1001f - 1f / 1f, dc.Y, Eps);     // 1/far - 1/near
    }

    [Fact]
    public void A_degenerate_projection_falls_back_to_the_neutral_pair_rather_than_to_a_zero()
    {
        // Both components feed reciprocals in the shader. A zero makes the linearised depth non-finite and
        // the quad vanishes - a failure that looks like "the emitter is broken", not "the matrix is".
        Assert.Equal(ParticleShading.NeutralDepthConversion, ParticleShading.DepthConversion(default));
        Assert.Equal(ParticleShading.NeutralDepthConversion, ParticleShading.DepthConversion(Matrix4x4.Identity));
        Assert.NotEqual(0f, ParticleShading.NeutralDepthConversion.X);
        Assert.NotEqual(0f, ParticleShading.NeutralDepthConversion.Y);
    }

    // ================================================================ the fade band

    [Fact]
    public void The_neutral_params_leave_the_fade_at_one_for_every_depth()
    {
        // A material whose emitter authored no widths must render EXACTLY as it did before soft particles
        // existed. The pre-M234 guess (0, 1e6, 0, 1e6) evaluated to 0 at every depth and erased the sprite.
        //
        // The sweep stops short of +-1e6 on purpose, and that bound is the neutral's one real limitation:
        // it works by putting the first edge at -1e6, so a depth difference of exactly -1e6 sits ON that
        // edge and fades to 0. The shader's difference is
        // linearise(scene) - linearise(particle), which cannot exceed the far plane, so no real projection
        // reaches it - but it is a saturation trick rather than a true identity and is worth pinning as one.
        foreach (float d in new[] { -1e5f, -100f, 0f, 0.001f, 1f, 50f, 1000f, 1e5f })
            Assert.Equal(1f, ParticleShading.FadeFromDistance(d, ParticleShading.NeutralSoftParams), Eps);

        Assert.Equal(0f, ParticleShading.FadeFromDistance(-1e6f, ParticleShading.NeutralSoftParams), Eps);
    }

    [Fact]
    public void The_pre_M234_guess_erases_the_sprite_which_is_why_it_is_not_the_neutral()
    {
        var broken = new Vector4(0f, 1e6f, 0f, 1e6f);
        foreach (float d in new[] { 0f, 1f, 100f, 1000f })
            Assert.Equal(0f, ParticleShading.FadeFromDistance(d, broken), Eps);
    }

    [Fact]
    public void The_band_is_two_sided_it_fades_in_near_geometry_and_out_again_far_from_it()
    {
        // .xy are two independent depth offsets, .zw two inverse widths: in over [0,100], out over
        // [500,600]. A one-sided near-fade would never come back down, so the last sample is the assertion
        // that actually distinguishes the two readings.
        var p = new Vector4(0f, 500f, 1f / 100f, 1f / 100f);

        Assert.Equal(0f, ParticleShading.FadeFromDistance(0f, p), Eps);       // at the surface: invisible
        Assert.True(ParticleShading.FadeFromDistance(50f, p) > 0.4f);         // mid fade-in
        Assert.Equal(1f, ParticleShading.FadeFromDistance(200f, p), Eps);     // fully visible plateau
        Assert.True(ParticleShading.FadeFromDistance(550f, p) < 0.6f);        // coming back down
        Assert.Equal(0f, ParticleShading.FadeFromDistance(1000f, p), Eps);    // gone again past the far edge
    }

    [Fact]
    public void The_ramp_is_a_real_smoothstep_and_not_a_linear_one()
    {
        // Alpha erosion, in the very same shader family, uses LINEAR ramps. The two stages genuinely
        // differ, so a shared helper would be wrong for one of them.
        var p = new Vector4(0f, 0f, 1f, 0f);       // fade in over [0,1], no second edge
        Assert.Equal(0.5f, ParticleShading.FadeFromDistance(0.5f, p), Eps);     // symmetric point agrees
        Assert.Equal(0.15625f, ParticleShading.FadeFromDistance(0.25f, p), Eps);   // t*t*(3-2t), not t
        Assert.NotEqual(0.25f, ParticleShading.FadeFromDistance(0.25f, p), 3);
    }

    [Fact]
    public void The_x_offset_pairs_with_the_z_width_not_with_the_y_one()
    {
        // Riot's swizzle, validated on a device in M175. Getting it wrong swaps which edge is which, which
        // for an asymmetric band silently inverts the effect.
        var p = new Vector4(10f, 1000f, 1f, 0f);   // edge at 10 with width 1; second edge effectively off
        Assert.Equal(0f, ParticleShading.FadeFromDistance(10f, p), Eps);
        Assert.Equal(1f, ParticleShading.FadeFromDistance(11f, p), Eps);
    }

    [Fact]
    public void SoftFade_composes_the_conversion_and_the_band()
    {
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 1.6f, 1f, 10000f);
        var dc = ParticleShading.DepthConversion(proj);
        float Window(float dist)
        {
            var clip = Vector4.Transform(new Vector4(0f, 0f, -dist, 1f), proj);
            return clip.Z / clip.W;
        }

        // Scene 700 units out, particle at 500: a 200-unit gap, inside a band that opens over [0,400].
        var p = new Vector4(0f, 100000f, 1f / 400f, 0f);
        float fade = ParticleShading.SoftFade(Window(700f), Window(500f), dc, p);
        Assert.Equal(ParticleShading.FadeFromDistance(200f, p), fade, 3);

        // A particle flush against the scene is fully faded out; one in open air is fully visible.
        Assert.Equal(0f, ParticleShading.SoftFade(Window(500f), Window(500f), dc, p), 3);
        Assert.Equal(1f, ParticleShading.SoftFade(Window(9000f), Window(500f), dc, p), 3);
    }

    /// <summary>The emitter data path, end to end: what the resolver reads out of a bin has to reach the
    /// band as a sane set of widths.</summary>
    [Fact]
    public void An_authored_emitter_packs_into_params_the_band_can_use()
    {
        var soft = new VfxSoftParticle(BeginIn: 0f, DeltaIn: 100f, BeginOut: 0f, DeltaOut: 0f);
        var packed = soft.PackParams();
        Assert.False(soft.IsDegenerate);

        Assert.Equal(0f, ParticleShading.FadeFromDistance(0f, packed), Eps);
        Assert.Equal(1f, ParticleShading.FadeFromDistance(100f, packed), Eps);
        Assert.True(ParticleShading.FadeFromDistance(50f, packed) > 0.4f);
    }

    [Fact]
    public void A_negative_deltaIn_inverts_the_ramp_rather_than_being_clamped_away()
    {
        // ~1,500 shipped emitters author a negative deltaIn. Under this formula that makes the sprite
        // visible NEAR geometry instead of away from it - a coherent choice for ground-hugging mist, and
        // clamping it to positive would silently discard the authored intent.
        var soft = new VfxSoftParticle(BeginIn: 0f, DeltaIn: -100f, BeginOut: 0f, DeltaOut: 0f);
        var packed = soft.PackParams();
        Assert.True(packed.Z < 0f);
        Assert.Equal(1f, ParticleShading.FadeFromDistance(-100f, packed), Eps);   // visible on the near side
        Assert.Equal(0f, ParticleShading.FadeFromDistance(100f, packed), Eps);
    }

    // ================================================================ the channel selector

    [Fact]
    public void An_alpha_blended_emitter_fades_its_alpha_and_leaves_rgb_alone()
    {
        var control = ParticleShading.SoftControl(additive: false);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), control);

        var colour = new Vector4(0.8f, 0.6f, 0.4f, 1f);
        var faded = ParticleShading.SoftApply(colour, 0.25f, control);
        Assert.Equal(0.8f, faded.X, Eps);
        Assert.Equal(0.6f, faded.Y, Eps);
        Assert.Equal(0.4f, faded.Z, Eps);
        Assert.Equal(0.25f, faded.W, Eps);
    }

    [Fact]
    public void An_additive_emitter_fades_its_rgb_and_leaves_alpha_alone()
    {
        // An additive sprite ignores its alpha entirely, so fading alpha there does nothing at all - which
        // is presumably why Riot made the channel selectable rather than baking one choice in.
        var control = ParticleShading.SoftControl(additive: true);
        Assert.Equal(new Vector4(0f, 1f, 1f, 0f), control);

        var colour = new Vector4(0.8f, 0.6f, 0.4f, 1f);
        var faded = ParticleShading.SoftApply(colour, 0.25f, control);
        Assert.Equal(0.2f, faded.X, Eps);
        Assert.Equal(0.15f, faded.Y, Eps);
        Assert.Equal(0.1f, faded.Z, Eps);
        Assert.Equal(1f, faded.W, Eps);
    }

    [Fact]
    public void Both_selectors_are_a_no_op_at_full_fade()
    {
        // The invariant that makes the neutral band safe: fade = 1 must reproduce the input exactly under
        // EITHER selector, or a particle with no authored widths would change appearance.
        var colour = new Vector4(0.3f, 0.5f, 0.7f, 0.9f);
        foreach (bool additive in new[] { false, true })
            Assert.Equal(colour, ParticleShading.SoftApply(colour, 1f, ParticleShading.SoftControl(additive)));
    }

    [Fact]
    public void An_all_zero_selector_multiplies_the_whole_output_by_zero()
    {
        // The black frame M234b diagnosed. Pinned so that "cSoftParticleControl is unused, leave it at the
        // zero-filled default" cannot come back as a plausible-sounding simplification.
        var black = ParticleShading.SoftApply(new Vector4(1f, 1f, 1f, 1f), 1f, Vector4.Zero);
        Assert.Equal(Vector4.Zero, black);
    }

    // ================================================================ depth push pull

    [Fact]
    public void A_positive_push_moves_the_vertex_away_from_the_camera()
    {
        var camera = new Vector3(0f, 0f, 0f);
        var pos = new Vector3(0f, 0f, 100f);
        var pushed = ParticleShading.DepthPushPull(pos, camera, 10f);
        Assert.Equal(110f, pushed.Z, Eps);
        Assert.Equal(Vector3.Distance(camera, pos) + 10f, Vector3.Distance(camera, pushed), 3);
    }

    [Fact]
    public void A_negative_push_pulls_the_vertex_towards_the_camera()
    {
        // The sign is the whole point and is not guessable from the name. Summoner's Rift authors -200,
        // -125, -80, -70, -50 and -10 against a single +80, so getting this backwards would push every
        // ground decal INTO the terrain it is trying to win the depth test against.
        var camera = new Vector3(0f, 0f, 0f);
        var pulled = ParticleShading.DepthPushPull(new Vector3(0f, 0f, 100f), camera, -80f);
        Assert.Equal(20f, pulled.Z, Eps);
    }

    [Fact]
    public void The_push_is_along_the_view_ray_not_along_an_axis()
    {
        var camera = new Vector3(10f, 20f, 30f);
        var pos = new Vector3(40f, 20f, 70f);            // 50 units away, on a 3-4-5 diagonal
        var pushed = ParticleShading.DepthPushPull(pos, camera, 50f);
        Assert.Equal(100f, Vector3.Distance(camera, pushed), 3);
        // Still exactly on the ray: the direction from the camera is unchanged.
        var before = Vector3.Normalize(pos - camera);
        var after = Vector3.Normalize(pushed - camera);
        Assert.Equal(1f, Vector3.Dot(before, after), 4);
    }

    [Fact]
    public void Zero_is_the_identity_and_a_vertex_at_the_camera_is_passed_through()
    {
        var pos = new Vector3(3f, 4f, 5f);
        Assert.Equal(pos, ParticleShading.DepthPushPull(pos, Vector3.Zero, 0f));
        // No view ray to slide along. Normalising the zero vector would send the vertex to NaN and take
        // the whole quad with it, which reads as "the emitter vanished".
        var degenerate = ParticleShading.DepthPushPull(pos, pos, 25f);
        Assert.Equal(pos, degenerate);
        Assert.False(float.IsNaN(degenerate.X));
    }

    // ================================================================ distortion

    private static readonly Vector4 White = new(1f, 1f, 1f, 1f);

    [Fact]
    public void The_offset_decodes_the_normal_map_around_a_half_grey_centre()
    {
        // (n - 0.5) * 2 is the unsigned-to-signed decode. A flat 0.5 texel must not displace anything, or
        // an unauthored normal map would smear the whole screen.
        Assert.Equal(Vector2.Zero,
            ParticleShading.DistortionOffset(new Vector4(0.5f, 0.5f, 0f, 1f), strength: 0.02f, rampAlpha: 1f));

        var full = ParticleShading.DistortionOffset(new Vector4(1f, 0f, 0f, 1f), strength: 0.02f, rampAlpha: 1f);
        Assert.Equal(0.02f, full.X, Eps);      // +1 * strength
        Assert.Equal(-0.02f, full.Y, Eps);     // -1 * strength
    }

    [Fact]
    public void The_offset_is_scaled_by_the_ramp_alpha()
    {
        // frame-pipeline.md §5.2 item 3, correction one. distortion_ps blob 0 line 66 -
        // mul r0.xy, r0.xyxx, r1.wwww - which ReyEngine omitted before M461, so the haze never animated
        // with the emitter's fade curve.
        var n = new Vector4(1f, 1f, 0f, 1f);
        var full = ParticleShading.DistortionOffset(n, 0.02f, rampAlpha: 1f);
        var half = ParticleShading.DistortionOffset(n, 0.02f, rampAlpha: 0.5f);
        var none = ParticleShading.DistortionOffset(n, 0.02f, rampAlpha: 0f);

        Assert.Equal(full.X * 0.5f, half.X, Eps);
        Assert.Equal(Vector2.Zero, none);      // a fully faded particle refracts nothing at all
    }

    [Fact]
    public void The_refracted_sample_is_tinted_by_diffuse_times_vertex_colour_times_ramp()
    {
        // Correction two. distortion_ps blob 0 lines 69-72: a heat-haze particle is a TINTED refraction,
        // not a pure displacement. ReyEngine returned the scene sample raw before M461.
        var scene = new Vector3(0.8f, 0.8f, 0.8f);
        var diffuse = new Vector4(1f, 0.5f, 0.25f, 0.123f);      // alpha deliberately odd - it must not matter
        var vcol = new Vector4(0.5f, 1f, 1f, 1f);
        var ramp = new Vector4(1f, 1f, 0.5f, 1f);

        var outp = ParticleShading.Distort(scene, diffuse, vcol, normalTexel: White, ramp: ramp);
        Assert.Equal(0.8f * 1f * 0.5f * 1f, outp.X, Eps);
        Assert.Equal(0.8f * 0.5f * 1f * 1f, outp.Y, Eps);
        Assert.Equal(0.8f * 0.25f * 1f * 0.5f, outp.Z, Eps);
    }

    [Fact]
    public void A_white_diffuse_and_a_white_ramp_leave_the_refraction_untinted()
    {
        // The identity that keeps a blank "color-hold" sprite working. Riot ships exactly that for
        // SRX_Infernal_North_Red_Top/Distort_Heat, so the tint must degrade to a pass-through.
        var scene = new Vector3(0.2f, 0.4f, 0.6f);
        var outp = ParticleShading.Distort(scene, White, White, White, White);
        Assert.Equal(scene.X, outp.X, Eps);
        Assert.Equal(scene.Y, outp.Y, Eps);
        Assert.Equal(scene.Z, outp.Z, Eps);
    }

    [Fact]
    public void The_output_alpha_is_the_normal_map_alpha_times_the_ramp_alpha_and_ignores_the_diffuse()
    {
        // Correction three: o0.w = r0.z * r1.w, where r0.z survived from the NORMAL MAP sample at line 62.
        // The diffuse of a heat-haze emitter is routinely a deliberate blank, so folding its alpha into the
        // mask - as ReyEngine did - was attenuating the effect with a texture carrying no information.
        var diffuse = new Vector4(1f, 1f, 1f, 0.1f);
        var normal = new Vector4(0.5f, 0.5f, 0f, 0.8f);
        var ramp = new Vector4(1f, 1f, 1f, 0.5f);

        var outp = ParticleShading.Distort(Vector3.One, diffuse, White, normal, ramp);
        Assert.Equal(0.8f * 0.5f, outp.W, Eps);
    }

    [Fact]
    public void The_substituted_ramp_carries_the_vertex_alpha_and_a_white_rgb()
    {
        // The one place this milestone leaves measurement behind, so it is pinned rather than left
        // implicit. RGB must stay 1: the vertex colour is ALREADY a separate factor in Riot's tint, and
        // reusing it here would square it.
        var vcol = new Vector4(0.25f, 0.5f, 0.75f, 0.4f);
        var ramp = ParticleShading.SubstituteRamp(vcol);
        Assert.Equal(new Vector4(1f, 1f, 1f, 0.4f), ramp);

        var outp = ParticleShading.Distort(Vector3.One, White, vcol, White, ramp);
        Assert.Equal(0.25f, outp.X, Eps);       // vertex colour applied ONCE
        Assert.Equal(0.5f, outp.Y, Eps);
        Assert.Equal(0.75f, outp.Z, Eps);
    }

    [Fact]
    public void An_emitter_with_no_colour_over_life_behaves_as_if_the_ramp_were_white()
    {
        // SRU_DragonHextech_Transition_Dragonpit_01/warpwave authors no colorOverLife, so its vertex alpha
        // is 1 and the substitution degenerates to exactly the unbound-white case.
        var ramp = ParticleShading.SubstituteRamp(White);
        Assert.Equal(White, ramp);
        Assert.Equal(ParticleShading.DistortionOffset(new Vector4(1f, 1f, 0f, 1f), 0.02f, 1f),
                     ParticleShading.DistortionOffset(new Vector4(1f, 1f, 0f, 1f), 0.02f, ramp.W));
    }

    [Fact]
    public void The_scene_lookup_is_clamped_to_the_screen()
    {
        Assert.Equal(new Vector2(0f, 1f),
            ParticleShading.DistortionSceneUv(new Vector2(0.02f, 0.99f), new Vector2(-0.5f, 0.5f)));
        Assert.Equal(new Vector2(0.52f, 0.49f),
            ParticleShading.DistortionSceneUv(new Vector2(0.5f, 0.5f), new Vector2(0.02f, -0.01f)));
    }
}
