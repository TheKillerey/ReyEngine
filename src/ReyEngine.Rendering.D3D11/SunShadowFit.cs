using System.Numerics;

namespace ReyEngine.Rendering.D3D11;

/// <summary>
/// Everything one frame's sun shadow map needs, in the conventions Riot's own shaders consume.
/// All matrices are System.Numerics ROW-VECTOR (<c>v * M</c>) and are transposed on upload by
/// <c>Mat()</c>, exactly like <c>VIEW_PROJECTION_MATRIX</c> - see <see cref="SunShadowFit"/>.
/// </summary>
/// <param name="ViewProjection">World -> light clip. Fed to the depth pass as
/// <c>PerFrameVertexCB.VIEW_PROJECTION_MATRIX</c>, because that is the constant
/// <c>environment/shadowmap.vs</c> actually reads.</param>
/// <param name="ShadowProj">World -> (u, v, depth). Fed to <c>PerFrameVertexCB.mShadowProj</c> (+176);
/// the consuming vertex shader takes only rows 0-2 and never divides by w.</param>
/// <param name="ConstantDepthBias"><c>PerFramePixelCB</c>+484, in normalised depth units.</param>
/// <param name="SlopeScaledDepthBias"><c>PerFramePixelCB</c>+488, in normalised depth units.</param>
/// <param name="SampleOffsets"><c>PerFramePixelCB</c>+224 - the 5-tap kernel, as two UV offsets.</param>
/// <param name="Radius">Half-extent of the orthographic square, in world units.</param>
/// <param name="WorldUnitsPerTexel">How much world one shadow texel covers. The bias scales with it.</param>
/// <param name="DepthRange">Far minus near along the light axis, in world units.</param>
public readonly record struct SunShadowFrame(
    Matrix4x4 ViewProjection,
    Matrix4x4 ShadowProj,
    float ConstantDepthBias,
    float SlopeScaledDepthBias,
    Vector4 SampleOffsets,
    float Radius,
    float WorldUnitsPerTexel,
    float DepthRange);

/// <summary>
/// M465: the CPU half of Riot's sun shadow map - the orthographic sun frustum, the two matrices, the two
/// depth biases and the PCF kernel. Deliberately free of Direct3D so every one of them can be asserted
/// without a device; a shadow frustum that is subtly wrong shows up on screen only as "the shadows look
/// off", which is not a signal anybody can act on.
///
/// <para><b>WHAT THE DISASSEMBLY SAID</b> (M465 step 0; <c>environment/shadowmap.vs-dx11</c> and
/// <c>.ps-dx11</c>, one permutation and one blob each, so there is no stub to be fooled by):</para>
/// <code>
/// shadowmap.vs   in:  POSITION0 xyz          - and nothing else. No normal, no UV, no skinning.
///                cb1 $Globals          { float4x4 WORLD_MATRIX; }
///                cb2 PerFrameVertexCB  - only VIEW_PROJECTION_MATRIX (+112) is USED
///                out: SV_Position only
///                r1 = float4(pos,1) * WORLD_MATRIX;  o0 = r1 * VIEW_PROJECTION_MATRIX;   (lines 68-77)
/// shadowmap.ps   in:  NOTHING.  0 resources, 0 constant buffers.
///                mov o0.xyzw, l(1,1,1,1); ret                                            (line 20)
/// </code>
///
/// <para><b>So the shadow map is a DEPTH-STENCIL resource, not a colour target with packed depth.</b> The
/// pixel shader computes nothing and binds nothing; the pass exists to lay down depth. The consuming side
/// agrees: <c>DefaultEnv_Flat</c> blob 226 declares
/// <c>SHADOW_MAP_DEPTH_PCF_SharedSampler</c> as <c>sampler_c</c> (a comparison sampler) and reads
/// <c>SHADOW_MAP_DEPTH_PCF_SharedTexture</c> with <c>sample_c_lz</c> five times (lines 179-195).
/// The packed <c>dot(rgb, (1/65536, 1/256, 1))</c> in <c>filters/blur_shadow_3.ps</c> is a DIFFERENT
/// resource - that shader binds <c>ShadowMapBlurInput_SharedTexture</c> with an ordinary sampler, so it
/// post-processes some other, colour-encoded shadow product and has no bearing on this one.</para>
///
/// <para><b>The write pass uses VIEW_PROJECTION_MATRIX, not mShadowProj.</b> frame-pipeline.md §4.2 lists
/// <c>mShadowProj</c> under "what must be synthesised CPU-side", which is true - but it is consumed by the
/// SHADING vertex shader, not by the depth pass. During the depth pass the engine simply points
/// <c>VIEW_PROJECTION_MATRIX</c> at the light. Both matrices are produced here so neither has to be
/// guessed at the call site.</para>
///
/// <para><b>Alpha-tested geometry is NOT handled by this pass.</b> <c>environment/shadowmap.ps</c> has no
/// texture and no discard, so it stamps solid depth. Riot's alpha-tested casters go through the MATERIAL's
/// own <c>GENERATE_SHADOW_MAP</c> permutation instead: <c>defaultenv_flat.ps</c> blob 16
/// (<c>FEATURE_MASKED + DISCARD_ALPHA_TEXELS + GENERATE_SHADOW_MAP</c>) is
/// <c>sample t0 at TEXCOORD1.xy; lt r0.x, r0.x, 1.0; discard_nz; mov o0, 1</c> - 5 instruction slots.
/// Blobs 0-47 of that TOC are used by <c>GENERATE_SHADOW_MAP</c> permutations and by nothing else. Those
/// need the material's own vertex shader (they read an interpolated UV, which
/// <c>environment/shadowmap.vs</c> does not emit), so wiring them is a separate piece of work.</para>
///
/// <para><b>WHAT IS CHOSEN HERE RATHER THAN MEASURED.</b> DXBC carries no frustum, no bias and no kernel -
/// those are engine-side values this project has never seen. <see cref="ConstantBiasTexels"/>,
/// <see cref="SlopeBiasTexels"/>, <see cref="ShadowMapSize"/> and <see cref="DefaultShadowDistance"/> are
/// therefore chosen, and are gathered here with those names so that a future RenderDoc capture changes a
/// constant instead of starting a search. The bias FORM is measured -
/// <c>bias = SLOPE_SCALED * (1 - dot(Ngeo, normalize(SUN_LIGHT_DIRECTION))) + CONSTANT</c>, light-system.md
/// §1.7, re-verified in M465 at <c>DefaultEnv_Flat</c> blob 226 lines 172-177 - only its two magnitudes are
/// ours.</para>
/// </summary>
public static class SunShadowFit
{
    /// <summary>Edge of the (square) shadow map. Chosen, not measured. 2048 is the usual desktop choice and
    /// gives about 10 world units per texel over a whole Summoner's Rift, roughly 1 unit per texel when the
    /// camera is framing a single building.</summary>
    public const int ShadowMapSize = 2048;

    /// <summary>
    /// How far in front of the camera shadows are fitted, in world units. Chosen. Summoner's Rift is about
    /// 14,800 units across and 21,000 on the diagonal, so this covers a whole map; the camera's own far
    /// plane is 200,000 (<c>OrbitCamera.Far</c>) and fitting to that would spend the entire shadow map on
    /// empty space beyond the terrain.
    ///
    /// <para>Geometry further from the eye than this receives no dynamic shadow. On a baked map it still
    /// carries the baked shadow mask, because the shader takes <c>min(pcf, baked.w)</c> - the two are
    /// independent and ours only ever darkens further.</para>
    /// </summary>
    public const float DefaultShadowDistance = 25000f;

    /// <summary>Constant depth bias, expressed in SHADOW TEXELS of world size rather than in depth units,
    /// so it tracks the frustum fit instead of needing a different value per map and per zoom level.
    /// Chosen: about one and a half texels is the usual starting point for a 5-tap PCF kernel spanning one
    /// texel diagonally. Too small produces acne (surfaces self-shadowing in stripes); too large produces
    /// peter-panning (shadows detaching from the feet of what casts them). Only an eye can settle it.</summary>
    public const float ConstantBiasTexels = 1.5f;

    /// <summary>Slope-scaled depth bias, in the same texel units, applied as
    /// <c>SLOPE * (1 - N.L)</c> by the shader - so it reaches its full value on surfaces edge-on to the sun,
    /// where a single texel spans the most depth. Chosen; four times the constant term is conventional.</summary>
    public const float SlopeBiasTexels = 6f;

    /// <summary>
    /// NDC -> texture space, as a row-vector matrix: <c>u = x*0.5 + 0.5</c>, <c>v = y*-0.5 + 0.5</c>,
    /// <c>depth = z</c> unchanged.
    ///
    /// <para>V is flipped because D3D texture space runs top-down while clip space runs bottom-up. Z is left
    /// alone because System.Numerics already emits the D3D convention, z in [0,1] - the same reason
    /// <c>ExtractFrustum</c> uses the unsummed row for its near plane. Getting the flip wrong does not
    /// produce a blank screen, it produces shadows mirrored about the horizontal, which is exactly the kind
    /// of wrong that survives a casual look.</para>
    /// </summary>
    public static Matrix4x4 UvRemap => new(
        0.5f, 0f, 0f, 0f,
        0f, -0.5f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0.5f, 0.5f, 0f, 1f);

    // ------------------------------------------------------------------ the fit

    /// <summary>
    /// Fit an orthographic sun frustum to <paramref name="receivers"/> while keeping every caster in
    /// <paramref name="casters"/> inside its depth range, and derive the two matrices, the two biases and
    /// the PCF kernel from it. Returns null when there is nothing to fit - a degenerate box, or a sun
    /// direction of zero, both of which a map can genuinely author.
    ///
    /// <para><b>The fit, and why each part of it is the way it is.</b></para>
    /// <list type="number">
    ///   <item><b>X/Y from the receiver box's bounding SPHERE, not its box.</b> A square that circumscribes
    ///   the sphere contains the receiver region for EVERY sun direction, so rotating the sun (or the map's
    ///   authored sun differing from the next map's) cannot make the frustum clip at an edge. It wastes at
    ///   most 1.73x of the map's area against a per-direction tight fit, and never loses a shadow - which is
    ///   the trade the brief asks for in that order.</item>
    ///   <item><b>The centre is SNAPPED to whole texels of the light basis.</b> Without this the whole
    ///   shadow map slides by a fraction of a texel whenever the camera moves and every shadow edge crawls.
    ///   Snapping in the LIGHT basis rather than in world axes is what makes it a no-op for the sampler.</item>
    ///   <item><b>Near comes from the CASTERS, far from the receivers.</b> A frustum fitted only to what is
    ///   on screen has no depth for the thing standing off-screen between the sun and the screen, and its
    ///   shadow simply vanishes. Pulling the near plane back over the whole caster set is the cheap,
    ///   complete fix; it costs depth range, which is why the biases below are expressed relative to it.</item>
    /// </list>
    /// </summary>
    /// <param name="sunDirection">Points TOWARD the sun, matching <c>MapSunProperties.SunDirection</c> and
    /// the <c>SUN_LIGHT_DIRECTION</c> the shaders dot against the normal. Need not be unit length.</param>
    public static SunShadowFrame? Fit(
        Vector3 sunDirection,
        (Vector3 Min, Vector3 Max) casters,
        (Vector3 Min, Vector3 Max) receivers,
        int size = ShadowMapSize)
    {
        if (size < 1) return null;
        float sunLength = sunDirection.Length();
        if (!float.IsFinite(sunLength) || sunLength < 1e-6f) return null;
        if (!IsValid(receivers) || !IsValid(casters)) return null;

        var toSun = sunDirection / sunLength;
        var travel = -toSun;                       // the direction the light actually goes

        // CreateLookAt's own basis, reproduced so the texel snap can use it. zaxis = normalize(eye - target)
        // = -travel = toSun. The up vector only has to be non-parallel to it.
        var up = MathF.Abs(Vector3.Dot(toSun, Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var zAxis = toSun;
        var xAxis = Vector3.Cross(up, zAxis);
        float xLen = xAxis.Length();
        if (xLen < 1e-6f) return null;
        xAxis /= xLen;
        var yAxis = Vector3.Cross(zAxis, xAxis);

        var center = (receivers.Min + receivers.Max) * 0.5f;
        float radius = (receivers.Max - receivers.Min).Length() * 0.5f;
        // A single flat quad, or a scene the size of one vertex, still has to produce a usable frustum.
        radius = MathF.Max(radius, 1e-3f);

        float worldUnitsPerTexel = 2f * radius / size;
        if (!float.IsFinite(worldUnitsPerTexel) || worldUnitsPerTexel <= 0f) return null;

        // Snap in the light basis. Reconstructing the centre from the three (snapped) coordinates is exact
        // because the basis is orthonormal.
        float cx = MathF.Floor(Vector3.Dot(center, xAxis) / worldUnitsPerTexel) * worldUnitsPerTexel;
        float cy = MathF.Floor(Vector3.Dot(center, yAxis) / worldUnitsPerTexel) * worldUnitsPerTexel;
        float cz = Vector3.Dot(center, zAxis);
        center = xAxis * cx + yAxis * cy + zAxis * cz;

        // Depth along the light's travel direction, measured from the snapped centre.
        float near = float.MaxValue, far = float.MinValue;
        foreach (var corner in Corners(casters)) near = MathF.Min(near, Vector3.Dot(corner - center, travel));
        foreach (var corner in Corners(receivers)) far = MathF.Max(far, Vector3.Dot(corner - center, travel));

        // The receiver SPHERE is what the x/y extent covers, so the depth range has to cover it too, or a
        // receiver in the corner of the square falls outside the near/far pair that was fitted to the box.
        near = MathF.Min(near, -radius);
        far = MathF.Max(far, radius);

        float pad = MathF.Max(1f, radius * 0.01f);
        near -= pad;
        far += pad;
        float depthRange = far - near;
        if (!float.IsFinite(depthRange) || depthRange < 1e-4f) return null;

        var lightView = Matrix4x4.CreateLookAt(center, center + travel, up);
        var ortho = Matrix4x4.CreateOrthographic(2f * radius, 2f * radius, near, far);
        var viewProjection = lightView * ortho;

        // Depth units, from world units: the shader subtracts the bias from a [0,1] depth, so a fixed
        // constant would mean a different distance on every map and at every zoom level.
        float constantBias = ConstantBiasTexels * worldUnitsPerTexel / depthRange;
        float slopeBias = SlopeBiasTexels * worldUnitsPerTexel / depthRange;

        return new SunShadowFrame(
            viewProjection,
            viewProjection * UvRemap,
            constantBias,
            slopeBias,
            SampleOffsets(size),
            radius,
            worldUnitsPerTexel,
            depthRange);
    }

    /// <summary>
    /// The 5-tap PCF kernel, as the single float4 the shader reads: two UV offset pairs, applied as
    /// <c>centre, uv+O1, uv-O1, uv-O2, uv+O2</c>. One texel on the diagonal in each direction, so the five
    /// taps form a quincunx.
    ///
    /// <para><b>Only .xy and .zw are offsets; there is no z component.</b> Measured, and it corrects
    /// light-system.md §4.3, which lists this constant as "2 offset vectors each, (du, dv, dz)". In
    /// <c>DefaultEnv_Flat</c> blob 226 the depth reference is built once at line 178 and the offset vector's
    /// third component is set to a literal zero before each add - <c>mov r3.zw, l(0,0,0,1.0)</c> at line 182
    /// and <c>mov r3.z, l(0)</c> at line 190 - so all five taps compare against the SAME biased depth. (The
    /// spot-light kernel in §2.5 does offset its reference; that is a different constant,
    /// <c>SPOT_SHADOW_SAMPLE_OFFSETS</c>, and is out of scope here.)</para>
    /// </summary>
    public static Vector4 SampleOffsets(int size)
    {
        float texel = 1f / MathF.Max(1, size);
        return new Vector4(texel, texel, -texel, texel);
    }

    // ------------------------------------------------------------------ what to fit to

    /// <summary>
    /// The world-space region worth covering: what is on screen, clipped to
    /// <paramref name="shadowDistance"/> in front of the eye.
    ///
    /// <para>Both halves matter. Without the frustum box a single merged terrain run - and map geometry is
    /// merged into very long runs - drags the fit out to the whole map however close the camera is.
    /// Without the visible boxes the fit covers the empty air above and beside the map, and on a camera
    /// looking down at a floor most of the shadow map would be sky.</para>
    ///
    /// <para>Returns false when nothing visible survives, which the caller must read as "no shadow pass
    /// this frame" rather than as an error - an empty scene, or a camera pointed away from the map, is an
    /// ordinary thing for an editor to be doing.</para>
    /// </summary>
    public static bool TryReceiverBounds(
        Matrix4x4 viewProjection,
        float shadowDistance,
        IEnumerable<(Vector3 Min, Vector3 Max)> visible,
        out (Vector3 Min, Vector3 Max) bounds)
    {
        bounds = default;
        var corners = FrustumCorners(viewProjection, shadowDistance);
        if (corners is null) return false;

        var fMin = new Vector3(float.MaxValue);
        var fMax = new Vector3(float.MinValue);
        foreach (var c in corners) { fMin = Vector3.Min(fMin, c); fMax = Vector3.Max(fMax, c); }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var box in visible)
        {
            var lo = Vector3.Max(box.Min, fMin);
            var hi = Vector3.Min(box.Max, fMax);
            // Strictly-empty on any axis means the box does not meet the frustum's own box at all.
            if (lo.X > hi.X || lo.Y > hi.Y || lo.Z > hi.Z) continue;
            min = Vector3.Min(min, lo);
            max = Vector3.Max(max, hi);
            any = true;
        }
        if (!any) return false;
        bounds = (min, max);
        return true;
    }

    /// <summary>
    /// The eight world-space corners of a view-projection's clip volume, with the four far corners pulled
    /// in to at most <paramref name="maxDistance"/> from their near counterparts. Index 0-3 are the near
    /// corners, 4-7 the far ones, paired by index.
    ///
    /// <para>Unprojects the D3D NDC box - x,y in [-1,1] and <b>z in [0,1]</b>, not [-1,1]. Using the GL
    /// range here would place the near corners behind the eye and quietly double the fitted region.</para>
    ///
    /// <para>Returns null when the matrix will not invert or a corner lands on the plane at infinity.</para>
    /// </summary>
    public static Vector3[]? FrustumCorners(Matrix4x4 viewProjection, float maxDistance = float.PositiveInfinity)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) return null;

        var ndc = new[]
        {
            new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f),
            new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f),
            new Vector3(-1f, -1f, 1f), new Vector3(1f, -1f, 1f),
            new Vector3(-1f, 1f, 1f), new Vector3(1f, 1f, 1f),
        };

        var world = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var p = Vector4.Transform(new Vector4(ndc[i], 1f), inverse);
            if (MathF.Abs(p.W) < 1e-9f || !float.IsFinite(p.W)) return null;
            world[i] = new Vector3(p.X, p.Y, p.Z) / p.W;
            if (!float.IsFinite(world[i].X) || !float.IsFinite(world[i].Y) || !float.IsFinite(world[i].Z))
                return null;
        }

        if (float.IsFinite(maxDistance) && maxDistance > 0f)
        {
            for (int i = 0; i < 4; i++)
            {
                var ray = world[i + 4] - world[i];
                float len = ray.Length();
                if (len > maxDistance) world[i + 4] = world[i] + ray / len * maxDistance;
            }
        }
        return world;
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsValid((Vector3 Min, Vector3 Max) box)
    {
        var (min, max) = box;
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(min.Z)) return false;
        if (!float.IsFinite(max.X) || !float.IsFinite(max.Y) || !float.IsFinite(max.Z)) return false;
        return max.X >= min.X && max.Y >= min.Y && max.Z >= min.Z;
    }

    private static IEnumerable<Vector3> Corners((Vector3 Min, Vector3 Max) box)
    {
        var (min, max) = box;
        for (int i = 0; i < 8; i++)
            yield return new Vector3(
                (i & 1) == 0 ? min.X : max.X,
                (i & 2) == 0 ? min.Y : max.Y,
                (i & 4) == 0 ? min.Z : max.Z);
    }
}
