using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Lighting;

/// <summary>
/// M463: the single <c>LightRegionInfo_SharedDataBuffer</c> element Riot's environment and character pixel
/// shaders read, packed from the map's authored <see cref="MapSunProperties"/>.
///
/// <para><b>Why this exists.</b> M456 bound this buffer ZERO-FILLED, on the reasoning that light regions are
/// unauthored on Live (docs/research/light-system.md §1.6 — 0 of 12,747 shipped bins author one) and zero is
/// therefore the shipped answer. That is right for <c>DYNAMIC_ENV_LIGHT_FACTOR</c> / <c>_IDS</c> and wrong
/// here, and the difference is the whole bug. With the FACTOR texture at zero the shader computes
/// <c>wDefault = 1 - saturate(0) = 1</c> and routes the ENTIRE contribution to element 0 of this buffer —
/// so element 0 is not an unused fallback, it is the only entry that is ever read.</para>
///
/// <para><b>And offset +0 is the sun.</b> <c>Mantis_Env_Baked_PBR</c> never reads
/// <c>PerFramePixelCB.SUN_LIGHT_COLOR</c> at all (§1.7 — reflection marks it unused, and <c>cb1[6]</c>
/// appears zero times in the listing). Its sun radiance is this field instead. A zero-filled buffer
/// therefore multiplies the sun by zero on every Mantis pixel: the surface keeps its ambient IBL and loses
/// its directional light completely.</para>
///
/// <para><b>What +0 actually is — re-measured for this milestone, because the §1.4 table calls it
/// "sun-and-ambient" and only half of that is true.</b> In blob 27 the weighted region colour accumulates
/// into r10 (lines 532, 535, 538, 541) and r10 has exactly ONE consumer, the <c>movc</c> at line 545 that
/// picks between it and white on the RMA sentinel. Line 550 then overwrites r10. The value flows
/// 545 -> 548 (<c>* (1 - CLOUD_CARDS)</c>) -> 549 (<c>* sunShadow</c>) -> 634 (<c>* BRDF * NdotL</c>) and
/// nowhere else. <b>It is sun radiance only.</b> Ambient on Mantis is a separate quantity entirely: the two
/// <c>texturecubearray</c> samples at lines 494/498, each scaled by <c>IBL_CUBEMAP_SCALES[probeIndex].x</c>
/// (lines 495/499) — which is why the sky is wired to the IBL path in the renderer and not to this
/// record.</para>
///
/// <para><b>The record, verbatim from the bytecode.</b> Blob 27's own resource-bind comment block declares
/// the struct with NAMES, which settles what §1.4 could only map by offset and what §6 item 5 listed as an
/// open unknown ("whether +48..+111 is the fog block" — it is):</para>
/// <code>
/// struct LightRegionRenderData          // stride 112
/// {
///     float3 SunLightColor;             //   0   &lt;- read by Mantis as SUN RADIANCE
///     uint   ProbeIndex;                //  12
///     float3 CharacterSunLightColor;    //  16   &lt;- read by lit_uber_ps
///     uint   CharacterProbeIndex;       //  28
///     float3 CharacterSunLightDirection;//  32
///     uint   Priority;                  //  44
///     float3 DepthFogColor;             //  48
///     float  DepthFogMaxIntensity;      //  60
///     float3 HeightFogColor;            //  64
///     float  HeightFogMaxIntensity;     //  76
///     float  DepthFogStart;             //  80
///     float  DepthFogEnd;               //  84
///     float  HeightFogStart;            //  88
///     float  HeightFogEnd;              //  92
///     float3 ReflectionSkyTint;         //  96
///     float  mUnusedPadding0;           // 108
/// }
/// </code>
///
/// <para>Device-free and pure so the byte offsets and float bit-casts — the part that fails silently on the
/// GPU — are unit-testable without one. <c>SunRegionGpu</c> then proves the same bytes read back through a
/// real <c>ld_structured</c>.</para>
/// </summary>
public static class LightRegionInfoBuilder
{
    /// <summary>Element stride, from the <c>ld_structured_indexable(structured_buffer, stride=112)</c> that
    /// every consumer issues (Mantis blob 27 line 492; <c>lit_uber_ps</c> blob 400 lines 298/305/306).</summary>
    public const int Stride = 112;

    /// <summary>The IBL cubemap slot this record points every lookup at.
    ///
    /// <para>Zero is FORCED rather than chosen: the shader spends the field as
    /// <c>IBL_CUBEMAP_SCALES[probeIndex].x</c> and as the array slice of a <c>TextureCubeArray</c>, and the
    /// renderer binds a stand-in with <c>NumCubes = 1</c>. Any other index reads outside that array, which
    /// D3D11 resolves as undefined rather than as an error. <see cref="MapSunProperties"/> carries no probe
    /// index to map from either — the bin field lives on <c>LightRegionRenderData</c>, a class no shipped
    /// map authors.</para></summary>
    public const uint ProbeIndex = 0;

    /// <summary>Pack the one element the shader reads. Never throws; a null sun packs the record's own
    /// defaults, which is what an unauthored or unparseable map should look like.</summary>
    public static byte[] Pack(MapSunProperties? sun)
    {
        var s = sun ?? new MapSunProperties();
        var buf = new byte[Stride];

        // THE RENDER FORM, folded. M451 split the authored sun into a hue (sunColor) and a strength
        // (SunIntensityScale) because the SAVE path has to keep them apart — the bin carries two fields and
        // collapsing them would write a value the game reads differently. The SHADER has one input, so the
        // fold happens here, at the boundary, exactly as SUN_LIGHT_COLOR is folded for DefaultEnv_Flat.
        Vector3 radiance = Safe(new Vector3(s.SunColor.X, s.SunColor.Y, s.SunColor.Z) * s.SunIntensityScale);

        // +0  float3  env sun radiance (Mantis blob 27, lines 492/532 -> 545 -> 549 -> 634)
        WriteVec3(buf, 0, radiance);

        // +12 uint    env IBL cubemap index
        WriteU32(buf, 12, ProbeIndex);

        // +16 float3  CHARACTER sun colour (lit_uber_ps blob 400 line 298, ld_structured ... l(16))
        //
        // Fed from the same authored sun. MapSunProperties models no separate character sun: the bin's
        // CharacterSunLightColor lives on LightRegionRenderData, which §1.6 measured as authored by zero
        // shipped maps, so there is no second value to read and no map-authored ratio between the two to
        // preserve. One sun lighting both the world and the things standing on it is the assumption, and it
        // is an assumption rather than a measurement.
        WriteVec3(buf, 16, radiance);

        // +28 uint    character IBL cubemap index (lit_uber_ps lines 299-303)
        WriteU32(buf, 28, ProbeIndex);

        // +32 float3  character sun DIRECTION (lit_uber_ps line 306; normalised by the shader at 369-371)
        //
        // Normalised here anyway, for the same reason UnitSun does it for SUN_LIGHT_DIRECTION: Riot ships
        // non-unit sun vectors up to length 8.775 and the env family consumes them raw, so a viewport that
        // hands one family a normalised vector and the other a raw one lights the same scene two ways.
        WriteVec3(buf, 32, Normalized(s.SunDirection));

        // +44..+111  LEFT ZERO, deliberately, and this is the one place a mapping COULD have been invented.
        //
        // The struct above names these: Priority, then a complete second fog model (DepthFogColor/Start/End,
        // HeightFogColor/Start/End, two max-intensity scalars) and ReflectionSkyTint. MapSunProperties
        // carries FogColor and FogStartAndEnd, so the name match is tempting and it is still refused, for
        // two reasons that are about evidence rather than taste:
        //
        //   1. NOTHING REYENGINE RENDERS READS THEM. Neither Mantis blob 27 nor lit_uber_ps blob 400
        //      touches a byte past +44 - they issue ld_structured at l(0), l(16) and l(32) and nowhere
        //      else. The only shader that might is environment/reflectionsky.ps, which binds this buffer
        //      (§9) and which this renderer does not run. Writing here would be unobservable, and an
        //      unobservable value is exactly the kind that goes wrong quietly and stays wrong.
        //   2. THE CONVENTION IS UNMEASURED. Riot stores fogStartAndEnd in a NEGATIVE, reversed view-space
        //      convention (Twisted Treeline ships -10000, -50000) which PerFramePixelCB consumes raw. Two
        //      float fields called DepthFogStart / DepthFogEnd in a different buffer are NOT known to share
        //      it, and guessing the sign inverts the fog rather than failing loudly.
        //
        // The map's fog does reach the shader - through ENV_FOG_COLOR and
        // ENV_FOG_START_END_SCALE_EMISSIVE_REMAP, which are measured and which the renderer already fills.
        return buf;
    }

    /// <summary>Non-finite in, black out. A NaN reaching a structured buffer does not fail loudly — it
    /// propagates through the BRDF and takes the whole pixel with it, and a colour is the one input a user
    /// can drive to NaN from the UI (an empty numeric box parses to NaN in more than one Avalonia binding
    /// mode). Negative radiance is clamped for the same reason it is nonsense: it subtracts light.</summary>
    private static Vector3 Safe(Vector3 v) => new(Safe(v.X), Safe(v.Y), Safe(v.Z));

    private static float Safe(float f) => float.IsFinite(f) && f > 0f ? f : 0f;

    /// <summary>A degenerate or non-finite direction falls back to straight down-to-up rather than to a
    /// zero vector: <c>dot(N, 0)</c> is 0 everywhere, which is a black sun that looks like this code never
    /// ran.</summary>
    private static Vector3 Normalized(Vector3 v)
    {
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
            return new Vector3(0f, 1f, 0f);
        float len = v.Length();
        return len > 1e-6f && float.IsFinite(len) ? v / len : new Vector3(0f, 1f, 0f);
    }

    private static void WriteVec3(byte[] buf, int offset, Vector3 v)
    {
        BitConverter.TryWriteBytes(buf.AsSpan(offset + 0, 4), v.X);
        BitConverter.TryWriteBytes(buf.AsSpan(offset + 4, 4), v.Y);
        BitConverter.TryWriteBytes(buf.AsSpan(offset + 8, 4), v.Z);
    }

    private static void WriteU32(byte[] buf, int offset, uint value) =>
        BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), value);
}
