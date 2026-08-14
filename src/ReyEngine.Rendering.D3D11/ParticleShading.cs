using System.Numerics;

namespace ReyEngine.Rendering.D3D11;

/// <summary>
/// M461: the arithmetic of Riot's particle pixel stages, in C#, so it can be asserted instead of only
/// executed on a GPU nobody is watching.
///
/// <para>Everything here is transcribed from compiled DXBC under
/// <c>assets/shaders/hlsl/particlesystem/</c>, disassembled with the <c>hdump</c> probe. The two sources
/// are <c>quad_ps.ps</c> blob 129 (the <c>SOFT_PARTICLES=1</c> permutation) and <c>distortion_ps.ps</c>
/// blob 0, and both are written out line by line in docs/research/frame-pipeline.md §4.9. Line numbers
/// below are that disassembly's, reproducible with
/// <c>disasm hdump dis &lt;toc&gt; &lt;blob&gt; &lt;out&gt;</c>.</para>
///
/// <para><b>Why a second copy at all.</b> The shipped soft-particle stage IS Riot's blob - ReyEngine runs
/// the real permutation - so this is not a reimplementation of it; it is the reference the constant feed
/// is held to. The distortion pass is different: ReyEngine draws heat haze with its own small HLSL,
/// because Riot's <c>distortion_vs</c> wants a vertex stream ReyEngine's shared quad buffer does not
/// carry. For that pass this file IS the specification, and the HLSL in
/// <see cref="ShaderPreviewRenderer"/> is a transcription of it that a GPU probe checks against Riot's
/// own blob.</para>
/// </summary>
public static class ParticleShading
{
    // ------------------------------------------------------------------ soft particles

    /// <summary>The neutral <c>cDepthConversionParams</c>. Both terms feed reciprocals in the shader, so
    /// neither component may be zero: a zero makes the linearised depth non-finite and the quad
    /// disappears rather than misbehaving visibly.</summary>
    public static readonly Vector4 NeutralDepthConversion = new(1f, 1f, 0f, 0f);

    /// <summary>
    /// Window depth to view distance, for <c>cDepthConversionParams</c>. The shader spends it as
    /// <c>1 / (z * DC.y + DC.x)</c>, so <c>DC.x = 1/near</c> and <c>DC.y = 1/far - 1/near</c>.
    ///
    /// <para><b>This is deliberately NOT the pair the GL path uses,</b> and the difference is not a bug in
    /// either. System.Numerics emits a Direct3D-convention projection (near maps to 0, not -1) and D3D's
    /// viewport transform passes that through, so window depth fills [0,1] and the textbook pair is
    /// correct here. GL applies its own <c>d = (z+1)/2</c> on top of an already-D3D-convention matrix, so
    /// its window depth only occupies [0.5,1] and it must double the slope to compensate. Copying GL's
    /// pair into D3D would reintroduce that factor of two mirrored, and it would be invisible to
    /// inspection because the result still looks like a plausible soft particle.</para>
    /// </summary>
    public static Vector4 DepthConversion(Matrix4x4 proj)
    {
        // CreatePerspectiveFieldOfView writes M33 = f/(n-f) and M43 = n*f/(n-f), so both planes come back
        // out: n = M43/M33 and f = M43/(M33+1). Recovered from the matrix rather than passed in, so a
        // caller-supplied projection is handled as correctly as a derived one.
        float m33 = proj.M33, m43 = proj.M43;
        if (MathF.Abs(m33) < 1e-9f || MathF.Abs(m33 + 1f) < 1e-9f) return NeutralDepthConversion;
        float near = m43 / m33, far = m43 / (m33 + 1f);
        if (!(near > 1e-6f) || !(far > near) || float.IsNaN(near) || float.IsNaN(far)) return NeutralDepthConversion;
        float invN = 1f / near, invF = 1f / far;
        return new Vector4(invN, invF - invN, 0f, 0f);
    }

    /// <summary>One window-depth sample as a view distance. <c>quad_ps</c> blob 129 lines 110-113.</summary>
    public static float Linearise(float windowZ, Vector4 depthConversion)
        => 1f / (windowZ * depthConversion.Y + depthConversion.X);

    /// <summary>
    /// The two-sided depth-fade band. <c>quad_ps</c> blob 129 lines 107-120:
    /// <code>
    /// float d  = linearise(sceneZ) - linearise(particleZ);
    /// float t0 = saturate((d - P.x) * P.z);
    /// float t1 = saturate((d - P.y) * P.w);
    /// float fade = smoothstep01(t0) - smoothstep01(t1);
    /// </code>
    ///
    /// <para>Two details are worth stating because both were open questions M175 closed by running Riot's
    /// permutation on a device: the smoothstep is a REAL <c>t*t*(3-2t)</c> - alpha erosion, in the very
    /// same shader family, uses linear ramps, so the two stages genuinely differ - and the
    /// <c>.x</c>-pairs-with-<c>.z</c> swizzle is Riot's, not a guess.</para>
    ///
    /// <para>It is a BAND, not a near-fade: <c>.xy</c> are two independent depth offsets and <c>.zw</c> two
    /// inverse widths, so a particle can fade in as it approaches a surface <i>and</i> out again past a far
    /// distance. Emitters really do author the second edge.</para>
    /// </summary>
    public static float SoftFade(float sceneWindowZ, float particleWindowZ, Vector4 depthConversion, Vector4 p)
    {
        float d = Linearise(sceneWindowZ, depthConversion) - Linearise(particleWindowZ, depthConversion);
        return FadeFromDistance(d, p);
    }

    /// <summary>The band evaluated at an already-computed depth difference, in world units. Split out
    /// because the difference is the quantity emitters author against, and testing it directly does not
    /// require inventing a projection.</summary>
    public static float FadeFromDistance(float depthDifference, Vector4 p)
    {
        float t0 = Math.Clamp((depthDifference - p.X) * p.Z, 0f, 1f);
        float t1 = Math.Clamp((depthDifference - p.Y) * p.W, 0f, 1f);
        return Smoothstep01(t0) - Smoothstep01(t1);
    }

    private static float Smoothstep01(float t) => t * t * (3f - 2f * t);

    /// <summary>
    /// <c>cSoftParticleParams</c> that leaves the fade at 1 for every possible depth - what a material with
    /// no authored widths must get.
    ///
    /// <para>Derived rather than guessed. <c>P.x = -1e6</c> with <c>P.z = 1</c> makes the first term
    /// <c>saturate(d + 1e6) = 1</c> for any finite d, and <c>P.w = 0</c> makes the second
    /// <c>saturate(0) = 0</c>, so <c>fade = 1 - 0 = 1</c> regardless of what the depth texture holds. The
    /// earlier guess <c>(0, 1e6, 0, 1e6)</c> evaluated to <c>0 - 0 = 0</c> - fully transparent - which is
    /// why <c>SOFT_PARTICLES</c> once never drew a pixel.</para>
    /// </summary>
    public static readonly Vector4 NeutralSoftParams = new(-1e6f, 0f, 1f, 0f);

    /// <summary>
    /// <c>cSoftParticleControl</c> is NOT a parameter, it is a per-channel SELECTOR, and treating it as
    /// unused is what made every <c>SOFT_PARTICLES</c> permutation render nothing. The tail of blob 129 is
    /// <code>
    /// mad o0.xyz, cb0[1].xxxx, colour, fade * colour * cb0[1].y
    /// mad o0.w,   cb0[1].z,    alpha,  fade * alpha  * cb0[1].w
    /// </code>
    /// so the four weights are <c>(rgbBase, rgbFade, aBase, aFade)</c> and each channel picks between the
    /// un-faded value and the faded one. All zeros multiplies the whole output by zero, which is exactly
    /// the black frame that was once observed.
    ///
    /// <para>Which channel should carry the fade depends on how the emitter blends, which is presumably
    /// why Riot made it selectable at all rather than baking it in: an additive sprite is invisible when
    /// its RGB goes to zero and ignores its alpha entirely, while an alpha-blended one is the other way
    /// round. The CPU packs this before upload, so the bytecode cannot name what feeds it - the blend mode
    /// is the only input ReyEngine has, and it is the one the two documented values correspond to.</para>
    /// </summary>
    public static Vector4 SoftControl(bool additive)
        => additive
            ? new Vector4(0f, 1f, 1f, 0f)   // additive: fade the RGB, leave alpha alone
            : new Vector4(1f, 0f, 0f, 1f);  // alpha:    leave RGB alone, fade the alpha

    /// <summary>Apply the selector to a straight-alpha colour, exactly as blob 129's last two
    /// instructions do.</summary>
    public static Vector4 SoftApply(Vector4 colour, float fade, Vector4 control)
        => new(
            control.X * colour.X + control.Y * fade * colour.X,
            control.X * colour.Y + control.Y * fade * colour.Y,
            control.X * colour.Z + control.Y * fade * colour.Z,
            control.Z * colour.W + control.W * fade * colour.W);

    // ------------------------------------------------------------------ PARTICLE_DEPTH_PUSH_PULL

    /// <summary>
    /// Riot's particle/geometry z-fighting mitigation, and the one thing every non-shadow particle vertex
    /// shader does before projection. <c>quad_vs</c> blob 1 lines 81-85 (<c>mesh_vs</c> 106-110 is the
    /// same five instructions):
    /// <code>
    /// add r0.xyz, v0.xyzx, -cb2[4].xyzx     // pos - vCamera
    /// dp3 r0.w, r0.xyzx, r0.xyzx
    /// rsq r0.w, r0.w
    /// mul r0.xyz, r0.wwww, r0.xyzx          // normalize
    /// mad r0.xyz, r0.xyzx, cb1[1].xxxx, v0.xyzx   // pos + dir * PARTICLE_DEPTH_PUSH_PULL
    /// </code>
    ///
    /// <para>The sign matters and is not guessable from the name: the direction is <b>away from</b> the
    /// camera, so a POSITIVE value pushes the quad further away and a NEGATIVE one pulls it towards the
    /// viewer. Measured on real content, the authored values are overwhelmingly negative - Summoner's Rift
    /// places -200, -125, -80, -70, -50 and -10 against a single +80 - which is what a ground-hugging decal
    /// wanting to win the depth test against the terrain it sits on looks like.</para>
    ///
    /// <para>The shadow vertex shaders deliberately omit it, so a particle's shadow is cast from where the
    /// particle really is rather than from where it is drawn.</para>
    ///
    /// <para>Degenerate input is passed through unchanged: a vertex exactly at the camera has no view ray
    /// to slide along, and normalising a zero vector would send it to NaN and take the whole quad with
    /// it.</para>
    /// </summary>
    public static Vector3 DepthPushPull(Vector3 position, Vector3 camera, float pushPull)
    {
        var away = position - camera;
        float lenSq = away.LengthSquared();
        if (lenSq < 1e-12f || float.IsNaN(lenSq)) return position;
        return position + away * (pushPull / MathF.Sqrt(lenSq));
    }

    // ------------------------------------------------------------------ distortion

    /// <summary>
    /// <para><b>The colour-over-life ramp ReyEngine substitutes for Riot's
    /// <c>PARTICLE_COLOR_TEXTURE</c>,</b> and the one place in this milestone where measurement runs out.
    /// Named rather than inlined so the substitution is visible at every call site.</para>
    ///
    /// <para>Riot's <c>distortion_ps</c> samples that texture at <b>uv1</b> and uses its alpha to scale the
    /// refraction offset and its RGB to tint the refracted sample. Measured on real content: all three
    /// heat-haze emitters placed on Summoner's Rift author one - <c>3152_Items_Distort_RGBA_2</c>,
    /// <c>Fade_in_fade_out</c> and <c>color-bellcurve32</c> - so this is not a dormant input.</para>
    ///
    /// <para><b>Why the texture is not bound.</b> uv1 is a per-vertex stream the CPU fills:
    /// <c>quad_vs</c> blob 1 line 98 is <c>mov o2.zw, v3.xxxy</c> and nothing more, so the bytecode cannot
    /// say what the lookup axis means. ReyEngine's own quad builder currently writes the sprite's corner UV
    /// there (<c>ParticleQuadBuilder.Append</c>: <c>vert.Uv1 = new Vector2(u, v)</c>), which is a
    /// placeholder. Binding the ramp against that would sample a TIME curve across SPACE - the haze would
    /// fade across the sprite instead of over the particle's life - which is a worse error than not
    /// sampling it, and would look plausible enough to survive review.</para>
    ///
    /// <para><b>What is used instead.</b> ReyEngine's simulator already evaluates the emitter's authored
    /// <c>colorOverLife</c> on the CPU and writes it into the vertex colour, so the vertex alpha carries
    /// the same curve the ramp's alpha encodes. Two of the three placed emitters author
    /// <c>colorOverLife</c> (<c>Distort_Heat</c> is 0 -> 1 -> 0, <c>Ground_Distort_Start1</c> is 1 -> 0);
    /// the third authors none, and gets alpha 1, which is exactly what an unbound white ramp would give.
    /// RGB is left at 1 rather than reusing the vertex colour, because the vertex colour is ALREADY a
    /// separate factor in Riot's tint and using it twice would square it.</para>
    /// </summary>
    public static Vector4 SubstituteRamp(Vector4 vertexColour) => new(1f, 1f, 1f, vertexColour.W);

    /// <summary>
    /// The refraction offset, in UV units. <c>distortion_ps</c> blob 0 lines 62-67:
    /// <code>
    /// r0.xy = normalMap.rg - 0.5
    /// r0.xy = r0.xy * DistortionPower
    /// r0.xy = r0.xy * ramp.a           // line 66 - the scale ReyEngine used to omit
    /// r0.xy = r0.xy * 2.0 + screenUV
    /// </code>
    ///
    /// <para>The <c>(n - 0.5) * 2</c> pair is the usual unsigned-to-signed decode, so this is
    /// <c>(2n - 1) * power * rampAlpha</c>. ReyEngine's previous version scaled instead by
    /// <c>normal.a * diffuse.a * vertexColour.a</c> - two factors Riot does not have there at all; the
    /// diffuse of a heat-haze emitter is routinely a deliberate blank, so folding its alpha in was
    /// attenuating the effect with a texture that carries no information.</para>
    /// </summary>
    public static Vector2 DistortionOffset(Vector4 normalTexel, float strength, float rampAlpha)
        => new(
            (normalTexel.X - 0.5f) * strength * rampAlpha * 2f,
            (normalTexel.Y - 0.5f) * strength * rampAlpha * 2f);

    /// <summary>Where the refracted sample is taken. The clamp is ReyEngine's, not Riot's - Riot relies on
    /// the shared clamp sampler, and doing it in the maths keeps this reference and the HLSL agreeing on
    /// what happens at the screen edge.</summary>
    public static Vector2 DistortionSceneUv(Vector2 screenUv, Vector2 offset)
        => new(
            Math.Clamp(screenUv.X + offset.X, 0f, 1f),
            Math.Clamp(screenUv.Y + offset.Y, 0f, 1f));

    /// <summary>
    /// The heat-haze output. <c>distortion_ps</c> blob 0 lines 68-72:
    /// <code>
    /// r0.xyw = BACK_BUFFER_COPY.Sample(screenUV + offset)      // r0.z still holds normalMap.a
    /// r2.xyz = TEXTURE.Sample(uv0).rgb * vertexColour.rgb
    /// r1.xyz = ramp.rgb * r2.xyz
    /// o0     = r0.xywz * r1.xyzw     -> rgb = scene * tint,  a = normalMap.a * ramp.a
    /// </code>
    ///
    /// <para>Two corrections over ReyEngine's previous version, both named in frame-pipeline.md §5.2. The
    /// refracted sample is <b>tinted</b> by <c>diffuse.rgb * vertexColour.rgb * ramp.rgb</c> rather than
    /// returned raw, so a heat-haze particle is a tinted refraction and not a pure displacement; and the
    /// output alpha is <c>normalMap.a * ramp.a</c> rather than the combined mask, so it is the NORMAL
    /// MAP - the texture that actually carries the effect's shape - that decides where the quad is
    /// visible, not the blank diffuse.</para>
    /// </summary>
    public static Vector4 Distort(Vector3 scene, Vector4 diffuse, Vector4 vertexColour, Vector4 normalTexel, Vector4 ramp)
    {
        var tint = new Vector3(
            diffuse.X * vertexColour.X * ramp.X,
            diffuse.Y * vertexColour.Y * ramp.Y,
            diffuse.Z * vertexColour.Z * ramp.Z);
        return new Vector4(scene.X * tint.X, scene.Y * tint.Y, scene.Z * tint.Z, normalTexel.W * ramp.W);
    }
}
