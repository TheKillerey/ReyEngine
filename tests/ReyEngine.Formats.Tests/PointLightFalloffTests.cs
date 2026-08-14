using System.Reflection;
using ReyEngine.Formats.Baking;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M457: the point-light falloff curve, pinned at known distances.
///
/// The curve moved this milestone. Riot's own point light - measured off compiled DXBC, identically in the
/// Lambert family (DefaultEnv_Flat blob 226, line 261ff) and the PBR family (Mantis blob 27, line 693), see
/// docs/research/light-system.md 2.4 - attenuates as
///
///     atten = 1 - saturate(dist * invRadius)
///
/// which is LINEAR: no square, no smoothstep, no inverse square. ReyEngine's index-0 curve used to be
/// (1-t)^2, so a light drew a far tighter pool than the game's. These tests hold the new definition at
/// specific distances rather than asserting a shape, so a future "tidy-up" of BakeLighting.Attenuation
/// cannot quietly restore the old curve.
///
/// The last two tests are the anti-drift ones. BakeLighting.Attenuation calls itself THE single definition
/// shared by the atlas bake, the lightgrid probes and (re-typed in GLSL) the viewport; the two shader
/// mirrors cannot call into it, so they are checked by reading the shipped shader source and requiring the
/// same terms - including the ABSENCE of the old 0.35 + 0.65*N.L ambient wrap, which Riot's loop does not
/// have and which none of the three now applies.
/// </summary>
public class PointLightFalloffTests
{
    private static BakeLighting At(float softness) => new() { FalloffSoftness = softness };

    /// <summary>Softness 0 IS Riot's curve: a straight line from 1 at the light to 0 at the radius.</summary>
    [Theory]
    [InlineData(0f, 1.00f)]
    [InlineData(100f, 0.90f)]
    [InlineData(250f, 0.75f)]
    [InlineData(500f, 0.50f)]     // the old (1-t)^2 gave 0.25 here - the halfway point is the tell
    [InlineData(750f, 0.25f)]
    [InlineData(999f, 0.001f)]
    public void At_softness_zero_the_falloff_is_Riots_linear_curve(float dist, float expected)
    {
        Assert.Equal(expected, At(0f).Attenuation(dist, 1000f), 3);
    }

    /// <summary>Softness 1 keeps the legacy wide curve, (1-t^2)^2, unchanged - it is the artistic escape
    /// hatch, and maps already tuned with it must still resolve to what they were tuned to.</summary>
    [Theory]
    [InlineData(0f, 1.0f)]
    [InlineData(500f, 0.5625f)]   // (1 - 0.25)^2
    [InlineData(800f, 0.1296f)]   // (1 - 0.64)^2
    public void At_softness_one_the_legacy_wide_curve_is_unchanged(float dist, float expected)
    {
        Assert.Equal(expected, At(1f).Attenuation(dist, 1000f), 4);
    }

    /// <summary>Everything between is the plain lerp of the two, so the slider stays a continuous blend
    /// rather than a switch.</summary>
    [Fact]
    public void Between_the_ends_it_is_the_straight_blend_of_the_two_curves()
    {
        const float dist = 300f, radius = 1000f, k = 0.4f;
        float t = dist / radius;
        float riot = 1f - t;
        float soft = (1f - t * t) * (1f - t * t);
        Assert.Equal(riot + (soft - riot) * k, At(k).Attenuation(dist, radius), 5);
    }

    /// <summary>Outside the radius, and on a degenerate light, the term is exactly zero at every softness -
    /// Riot's saturate() does the same, and the shaders skip the light entirely.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Outside_the_radius_contributes_nothing(float softness)
    {
        Assert.Equal(0f, At(softness).Attenuation(1000f, 1000f));
        Assert.Equal(0f, At(softness).Attenuation(1200f, 1000f));
        Assert.Equal(0f, At(softness).Attenuation(5f, 0f));       // radius 0 = no light, not a divide by zero
    }

    /// <summary>The GLSL mirror in the GL viewport. Read out of the shipped source, because a shader string
    /// compiles happily with the wrong math in it and Avalonia swallows the only signal that would say so.</summary>
    [Fact]
    public void The_GL_fragment_shader_runs_the_same_term()
    {
        string frag = ShaderSource(typeof(ReyEngine.Rendering.ViewportMeshRenderer), "MeshFrag");
        string code = StripComments(frag);

        Assert.Contains("float riotF = 1.0 - t;", code);
        Assert.Contains("mix(riotF, softF, uLightFalloffSoftness)", code);
        // Plain N.L, exactly as Riot's Lambert loop has it.
        Assert.Contains("dynamicLight += lightColour.rgb * lightColour.a * atten * ndl;", code);
        Assert.DoesNotContain("0.35 + 0.65", code);   // the retired ambient wrap
        AssertAscii(frag, "MeshFrag");
    }

    /// <summary>The HLSL mirror in the D3D11 fallback overlay. That file's header promises it matches the GL
    /// loop term for term; this is what keeps the promise true.</summary>
    [Fact]
    public void The_D3D11_fallback_overlay_runs_the_same_term()
    {
        string hlsl = ShaderSource(typeof(ReyEngine.Rendering.D3D11.ShaderPreviewRenderer), "LightHlsl");
        string code = StripComments(hlsl);

        Assert.Contains("float riotF = 1.0 - t;", code);
        Assert.Contains("lerp(riotF, softF, gCounts.z)", code);
        Assert.Contains("acc += gColorStrength[k].rgb * gColorStrength[k].a * atten * ndl;", code);
        Assert.DoesNotContain("0.35 + 0.65", code);
        AssertAscii(hlsl, "LightHlsl");
    }

    /// <summary>Drop // comments before asserting on what the shader COMPUTES. Both shaders explain in
    /// prose what they no longer do (the retired 0.35 + 0.65 wrap is named there on purpose), and a
    /// substring check that cannot tell code from commentary would either fail on the explanation or push
    /// someone to delete it.</summary>
    private static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        foreach (var line in src.Split('\n'))
        {
            int at = line.IndexOf("//", StringComparison.Ordinal);
            sb.Append(at < 0 ? line : line[..at]).Append('\n');
        }
        return sb.ToString();
    }

    private static string ShaderSource(Type owner, string field)
    {
        var f = owner.GetField(field, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f);
        return Assert.IsType<string>(f!.GetRawConstantValue());
    }

    /// <summary>A single byte above 0x7E in a shader string builds fine in C# and then blanks the viewport
    /// with no error at all (AGENTS.md constraint 1). Cheap to check, invisible when it breaks.</summary>
    private static void AssertAscii(string src, string label)
    {
        for (int i = 0; i < src.Length; i++)
            Assert.True(src[i] <= 0x7E,
                $"{label} carries a non-ASCII character U+{(int)src[i]:X4} at index {i}");
    }
}
