using System.Numerics;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M463: the 112 bytes Riot's environment and character pixel shaders read as
/// <c>LightRegionInfo_SharedDataBuffer[0]</c>.
///
/// <para>Asserted directly rather than inferred from a frame, for the same reason
/// <see cref="ClusterLightBuilderTests"/> is: this is byte packing feeding a shader we cannot modify, whose
/// failure mode is a silently black sun. Offsets come from blob 27's own resource-bind declaration —
/// reproduced in <see cref="LightRegionInfoBuilder"/>'s summary — and the float bit-casts are exactly what
/// <c>ld_structured</c> reinterprets, so a wrong offset here is a wrong colour with no error anywhere.</para>
///
/// <para>The GPU half is <c>disasm sunregion</c>, which uploads these bytes to a real device and reads them
/// back through Riot's struct. These tests are what make that probe a confirmation rather than the only
/// evidence.</para>
/// </summary>
public class LightRegionInfoBuilderTests
{
    private static Vector3 Vec3At(byte[] b, int offset) => new(
        BitConverter.ToSingle(b, offset),
        BitConverter.ToSingle(b, offset + 4),
        BitConverter.ToSingle(b, offset + 8));

    /// <summary>jade's authored sun, which is also what M451's split exists for.</summary>
    private static readonly MapSunProperties Jade = new()
    {
        SunColor = new Vector4(0.87f, 0.75f, 0.60f, 1f),
        SunIntensityScale = 0.5f,
        SunDirection = new Vector3(0f, 1f, 0f),
    };

    [Fact]
    public void The_record_is_the_stride_the_shader_declares()
    {
        Assert.Equal(112, LightRegionInfoBuilder.Stride);
        Assert.Equal(112, LightRegionInfoBuilder.Pack(new MapSunProperties()).Length);
    }

    // ------------------------------------------------------------------ +0, the field that was zero

    /// <summary>The whole bug in one assertion. Mantis reads its sun radiance from +0 and never touches
    /// SUN_LIGHT_COLOR, so a zeroed record is a black sun.</summary>
    [Fact]
    public void Sun_radiance_lands_at_offset_zero_as_colour_times_intensity()
    {
        byte[] b = LightRegionInfoBuilder.Pack(Jade);

        Assert.Equal(0.87f * 0.5f, BitConverter.ToSingle(b, 0), 6);
        Assert.Equal(0.75f * 0.5f, BitConverter.ToSingle(b, 4), 6);
        Assert.Equal(0.60f * 0.5f, BitConverter.ToSingle(b, 8), 6);
    }

    /// <summary>M451 keeps a SAVE form (hue + SunIntensityScale) and a RENDER form (folded, scale pinned to
    /// 1). The packer multiplies, so it is correct for both — which is what lets the view-model hand it the
    /// render form while the tests use the authored one.</summary>
    [Fact]
    public void The_folded_render_form_and_the_split_save_form_pack_identically()
    {
        var split = Jade;
        var folded = Jade with
        {
            SunColor = new Vector4(0.87f * 0.5f, 0.75f * 0.5f, 0.60f * 0.5f, 1f),
            SunIntensityScale = 1f,
        };

        Assert.Equal(LightRegionInfoBuilder.Pack(split), LightRegionInfoBuilder.Pack(folded));
    }

    // ------------------------------------------------------------------ the character block

    [Fact]
    public void Character_sun_colour_repeats_the_env_sun_at_offset_sixteen()
    {
        byte[] b = LightRegionInfoBuilder.Pack(Jade);

        Assert.Equal(Vec3At(b, 0), Vec3At(b, 16));
    }

    /// <summary>Riot ships sunDirection NON-UNIT — Map22 base_dragon_cloud is (2, 8, -3), length 8.775. The
    /// character field is normalised on the way in, matching what UnitSun does for SUN_LIGHT_DIRECTION, so
    /// the two families cannot end up lit by differently-scaled versions of the same sun.</summary>
    [Fact]
    public void Character_sun_direction_at_offset_thirty_two_is_normalised()
    {
        byte[] b = LightRegionInfoBuilder.Pack(Jade with { SunDirection = new Vector3(2f, 8f, -3f) });

        var dir = Vec3At(b, 32);
        Assert.Equal(1f, dir.Length(), 5);
        Assert.Equal(Vector3.Normalize(new Vector3(2f, 8f, -3f)), dir);
    }

    // ------------------------------------------------------------------ the two uint fields

    /// <summary>Both probe indices are 0 and that is forced, not chosen: the renderer binds a cube array of
    /// exactly one cube, and the shader spends this field as both an array slice and an index into
    /// IBL_CUBEMAP_SCALES. Any other value reads outside the array, which D3D11 resolves as undefined.
    /// Read as raw BITS — a float 0 and a uint 0 are the same bytes, so a test that read them as floats
    /// would pass even if the packer wrote the wrong type.</summary>
    [Fact]
    public void Both_probe_indices_are_uint_zero()
    {
        byte[] b = LightRegionInfoBuilder.Pack(Jade);

        Assert.Equal(0u, BitConverter.ToUInt32(b, 12));
        Assert.Equal(0u, BitConverter.ToUInt32(b, 28));
        Assert.Equal(0u, LightRegionInfoBuilder.ProbeIndex);
    }

    // ------------------------------------------------------------------ the tail

    /// <summary>+44 onward is Priority and a complete second fog model that nothing ReyEngine renders reads.
    /// Left zero deliberately (see the packer): the fog that IS read comes from PerFramePixelCB, and the
    /// sign convention of DepthFogStart/End is unmeasured. This pins that decision so a later "helpful"
    /// fill has to change a test that explains why.</summary>
    [Fact]
    public void Everything_past_the_character_block_stays_zero()
    {
        byte[] b = LightRegionInfoBuilder.Pack(new MapSunProperties
        {
            FogColor = new Vector4(1f, 0f, 1f, 1f),
            FogStartAndEnd = new Vector2(-10000f, -50000f),
            HorizonColor = new Vector4(0.2f, 0.4f, 0.9f, 1f),
            GroundColor = new Vector4(0.1f, 0.1f, 0.1f, 1f),
            SkyLightColor = new Vector4(0.35f, 0.45f, 0.8f, 1f),
            SkyLightScale = 1.5f,
        });

        for (int i = 44; i < LightRegionInfoBuilder.Stride; i++)
            Assert.Equal(0, b[i]);
    }

    /// <summary>The sky is NOT in this record. It reaches the shader through the IBL cubemap and
    /// IBL_CUBEMAP_SCALES instead, because that is where Mantis's ambient actually comes from (blob 27
    /// lines 494-499) — +0 is sun radiance only, whatever the §1.4 table's "sun-and-ambient" label
    /// suggests.</summary>
    [Fact]
    public void Changing_only_the_sky_does_not_change_the_record()
    {
        var a = new MapSunProperties { SkyLightColor = new Vector4(0.35f, 0.45f, 0.8f, 1f), SkyLightScale = 1.5f };
        var b = a with { SkyLightColor = new Vector4(0.9f, 0.3f, 0.1f, 1f), SkyLightScale = 4f };

        Assert.Equal(LightRegionInfoBuilder.Pack(a), LightRegionInfoBuilder.Pack(b));
    }

    // ------------------------------------------------------------------ the hostile inputs

    /// <summary>A NaN does not fail loudly on a GPU — it propagates through the BRDF and takes the pixel
    /// with it. An empty numeric box is enough to produce one from the panel.</summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-1f)]
    public void Non_finite_or_negative_radiance_packs_as_black(float bad)
    {
        byte[] b = LightRegionInfoBuilder.Pack(new MapSunProperties
        {
            SunColor = new Vector4(bad, bad, bad, 1f),
        });

        Assert.Equal(Vector3.Zero, Vec3At(b, 0));
        Assert.Equal(Vector3.Zero, Vec3At(b, 16));
    }

    /// <summary>A zero direction would make dot(N, dir) zero everywhere, which is a black sun that looks
    /// exactly like the bug this milestone fixes. Up is the fallback.</summary>
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(float.NaN, 1f, 0f)]
    public void A_degenerate_direction_falls_back_to_up(float x, float y, float z)
    {
        byte[] b = LightRegionInfoBuilder.Pack(new MapSunProperties { SunDirection = new Vector3(x, y, z) });

        Assert.Equal(new Vector3(0f, 1f, 0f), Vec3At(b, 32));
    }

    /// <summary>No map, no sun component, or an unparseable bin — Extract returns null and the packer must
    /// still produce a defined record rather than throw into the render loop.</summary>
    [Fact]
    public void A_null_sun_packs_the_records_own_defaults()
    {
        byte[] fromNull = LightRegionInfoBuilder.Pack(null);
        byte[] fromDefault = LightRegionInfoBuilder.Pack(new MapSunProperties());

        Assert.Equal(fromDefault, fromNull);
        Assert.Equal(Vector3.One, Vec3At(fromNull, 0));            // SunColor 1 x scale 1
        Assert.Equal(new Vector3(0f, 1f, 0f), Vec3At(fromNull, 32));
    }
}
