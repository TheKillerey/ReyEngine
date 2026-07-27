using System.Runtime.CompilerServices;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// <para>The DX11 preview builds its input layout from each shader's own ISGN and points every semantic at a
/// byte offset inside <see cref="PreviewVertex"/>. Those offsets are asserted by hand in
/// <c>ShaderPreviewRenderer.MapSemantic</c>, so a field inserted, reordered or resized in the struct silently
/// desynchronises the two - D3D happily reads whatever bytes are at the stated offset and the shader gets
/// plausible garbage.</para>
///
/// <para>This has already cost two milestones. M224: TEXCOORD7 had no slot, aliased the zero pad, and every
/// lightmap lookup landed on texel (0,0) - black ground across Map12. M230: TEXCOORD5 had no slot, so the
/// grass clump pivot read as the origin, a zero-length vector reached an <c>rsq</c>, and NaN vertex positions
/// made all 104,876 triangles of Map12 grass vanish. Both were offset bugs that compiled, ran, and drew.</para>
/// </summary>
public class PreviewVertexLayoutTests
{
    /// <summary>Semantic → offset, mirroring ShaderPreviewRenderer.MapSemantic exactly.</summary>
    public static TheoryData<string, int> Offsets => new()
    {
        { "POSITION0", 0 },
        { "NORMAL0", 12 },
        { "TANGENT0", 24 },
        { "TEXCOORD0", 40 },
        { "TEXCOORD1", 48 },
        { "TEXCOORD2", 56 },
        { "TEXCOORD3", 64 },
        { "TEXCOORD7", 72 },
        { "COLOR0", 80 },
        { "BLENDWEIGHT0", 96 },
        { "BLENDINDICES0", 112 },
        { "TEXCOORD5", 128 },
        { "(zero pad)", 144 },
    };

    [Theory]
    [MemberData(nameof(Offsets))]
    public unsafe void Semantic_offset_matches_the_field_the_renderer_points_it_at(string semantic, int expected)
    {
        var v = default(PreviewVertex);
        byte* b = (byte*)Unsafe.AsPointer(ref v);
        long Off(void* f) => (byte*)f - b;

        long actual = semantic switch
        {
            "POSITION0" => Off(&v.Position),
            "NORMAL0" => Off(&v.Normal),
            "TANGENT0" => Off(&v.Tangent),
            "TEXCOORD0" => Off(&v.Uv0),
            "TEXCOORD1" => Off(&v.Uv1),
            "TEXCOORD2" => Off(&v.Uv2),
            "TEXCOORD3" => Off(&v.Uv3),
            "TEXCOORD7" => Off(&v.Uv7),
            "COLOR0" => Off(&v.Color),
            "BLENDWEIGHT0" => Off(&v.BlendWeight),
            "BLENDINDICES0" => Off(&v.B0),
            "TEXCOORD5" => Off(&v.GrassPivot),
            _ => Off(&v.Zero),
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public unsafe void Stride_constant_matches_the_real_struct_size()
    {
        // The vertex buffer is allocated and bound with SizeInBytes. If the runtime pads the struct past it,
        // every vertex after the first reads from the wrong place.
        Assert.Equal(PreviewVertex.SizeInBytes, sizeof(PreviewVertex));
    }

    [Fact]
    public unsafe void Unmatched_semantics_land_on_a_pad_that_is_actually_zero()
    {
        // MapSemantic sends anything it does not recognise to offset 144 and lets it read up to 4 floats.
        // That is only a safe fallback while those 16 bytes are (a) in bounds and (b) zero.
        var v = default(PreviewVertex);
        byte* b = (byte*)Unsafe.AsPointer(ref v);
        long pad = (byte*)&v.Zero - b;

        Assert.True(pad + 16 <= PreviewVertex.SizeInBytes,
            $"the zero pad at +{pad} runs past the {PreviewVertex.SizeInBytes}-byte stride");
        Assert.Equal(System.Numerics.Vector4.Zero, v.Zero);
    }
}
