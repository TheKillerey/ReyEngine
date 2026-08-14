using System.Numerics;
using System.Reflection;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Formats.Materials;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M458: Riot's PBR point-light BRDF in the GL viewport, and the per-material switch that selects it.
///
/// The formula is read off compiled DXBC - <c>Mantis_Env_Baked_PBR</c> pixel blob 27, precompute at lines
/// 570 and 585-608, loop body at lines 689-741 (docs/research/light-system.md 2.4). It is UE4's
/// GGX / Smith-Schlick / Schlick formulation:
///
///     D    = a2 / (PI * (NdotH*NdotH*(a2-1) + 1)^2)
///     G    = G_V * (NdotL / (NdotL*(1-k) + k))
///     F    = F0 + (1-F0) * (1 - max(dot(H,V),0))^5
///     spec = F * D * G / (NdotV*4 * NdotL + 1e-4)
///     kD   = albedo * (1-F) * oneMinusMetal
///     out += lightColour * atten * intensity * (kD * (1/PI) + spec) * NdotL
///
/// <para><b>Two layers of test, and what each one can actually prove.</b> The BRDF lives only in a GLSL
/// string - there is no C# implementation of it to call - so:</para>
/// <list type="number">
///   <item>The numeric tests below evaluate a C# MIRROR of the shader, and pin it against expectations
///   written as CLOSED-FORM ALGEBRA rather than as recorded decimals. Every case picks a geometry where
///   the BRDF collapses analytically (N = V = L makes NdotH = 1 and dot(H,V) = 1, so D = 1/(PI*a2),
///   G = 1 and F = F0), so the expected value is derived, not observed. A transcription slip in the
///   mirror fails against the algebra.</item>
///   <item>The source tests read the shipped GLSL back out of the assembly and require the mirror's own
///   terms to be present in it verbatim, which is the only link available between the two - Avalonia
///   swallows GL init errors, so a wrong shader renders a blank viewport with a healthy process and no
///   test could otherwise see it.</item>
/// </list>
///
/// <para><b>What no test here can prove:</b> that the two viewports agree on a rendered Mantis frame.
/// Nothing in this suite executes the shader.</para>
/// </summary>
public class GgxPointLightTests
{
    // The shader's own literals, not MathF.PI - the GLSL says 3.14159265 and 0.31830989, and the mirror
    // has to be the same arithmetic to be worth comparing against.
    private const float Pi = 3.14159265f;
    private const float InvPi = 0.31830989f;

    /// <summary>A line-for-line C# mirror of the GLSL in <c>ViewportMeshRenderer.MeshFrag</c>, returning
    /// the BRDF contribution for one light at unit colour, unit strength and unit attenuation.</summary>
    private static float Brdf(float roughness, float metal, float albedo, Vector3 n, Vector3 v, Vector3 l)
    {
        // ---- precompute, hoisted out of the loop exactly as blob 27 lines 570 / 585-608 hoist it ----
        float rough = roughness;
        float f0 = 0.04f + (albedo - 0.04f) * metal;             // lines 550-551
        if (rough < 0f) { rough = 0.075f; f0 = 0f; }             // lines 585-587, the RMA sentinel
        float oneMinusMetal = 1f - metal;                        // line 565
        float a = Math.Clamp(rough, 0.04f, 1f);                  // lines 588-589
        a *= a;                                                  // line 590
        float a2 = a * a;                                        // line 591
        float a2m1 = a2 - 1f;                                    // line 595
        float k = (rough + 1f) * (rough + 1f) * 0.125f;          // lines 600-602, PRE-clamp roughness
        float ndv = MathF.Max(Vector3.Dot(n, v), 0f);            // lines 553-554
        float gv = ndv / (ndv * (1f - k) + k);                   // lines 604-605
        float nv4 = ndv * 4f;                                    // line 570

        // ---- per light, blob 27 lines 702-741 ----
        float ndl = MathF.Max(Vector3.Dot(n, l), 0f);
        var h = Vector3.Normalize(v + l);                        // lines 703-706
        float ndh = MathF.Max(Vector3.Dot(n, h), 0f);            // lines 707-708
        float dDen = ndh * ndh * a2m1 + 1f;                      // lines 709-710
        float dTerm = a2 / (Pi * dDen * dDen);                   // lines 711-713
        float gTerm = gv * (ndl / (ndl * (1f - k) + k));         // lines 714-716
        float fBase = MathF.Max(1f - MathF.Max(Vector3.Dot(h, v), 0f), 0f);   // lines 717-720
        float fSq = fBase * fBase;
        float fTerm = f0 + (1f - f0) * (fSq * fSq * fBase);      // lines 721-724
        float spec = fTerm * (dTerm * gTerm) / (nv4 * ndl + 0.0001f);   // lines 727-730
        float kD = albedo * (1f - fTerm) * oneMinusMetal;        // lines 725-726, 731
        return (kD * InvPi + spec) * ndl;                        // lines 739-741
    }

    private static readonly Vector3 Up = new(0f, 1f, 0f);

    /// <summary>Head-on dielectric. N = V = L collapses the BRDF: NdotH = 1 so D = a2/(PI*a2*a2) =
    /// 1/(PI*a2); NdotL = NdotV = 1 so both Smith halves are 1; dot(H,V) = 1 so the Schlick power is 0 and
    /// F is exactly F0 = 0.04. With roughness 0.5, a = 0.25 and a2 = 0.0625.</summary>
    [Fact]
    public void Head_on_dielectric_matches_the_closed_form()
    {
        const float a2 = 0.0625f;                                       // (0.5*0.5)^2
        float expectedSpec = 0.04f / (Pi * a2 * (4f + 0.0001f));
        float expected = 0.96f * InvPi + expectedSpec;                  // kD = 1 * (1 - 0.04) * 1

        Assert.Equal(expected, Brdf(0.5f, 0f, 1f, Up, Up, Up), 6);
    }

    /// <summary>A full metal has NO diffuse lobe at all (oneMinusMetal = 0) and its F0 is the albedo
    /// itself, so the same geometry leaves only the specular term. This is what pins line 551's
    /// <c>F0 = lerp(0.04, albedo, metal)</c> and line 565's <c>1 - metal</c>.</summary>
    [Fact]
    public void A_full_metal_keeps_only_the_specular_lobe()
    {
        const float a2 = 0.0625f;
        float expected = 1f / (Pi * a2 * (4f + 0.0001f));                // F0 = albedo = 1, kD = 0

        Assert.Equal(expected, Brdf(0.5f, 1f, 1f, Up, Up, Up), 6);
    }

    /// <summary>The RMA sentinel (blob 27 lines 585-587): a NEGATIVE roughness forces 0.075 AND zeroes F0.
    /// Zeroed F0 plus dot(H,V) = 1 kills the specular entirely, leaving a pure albedo/PI diffuse lobe.
    /// This case discriminates hard - drop the <c>f0 = 0</c> half and roughness 0.075 makes a2 tiny, D
    /// enormous, and the result lands near 1243 instead of 0.318.</summary>
    [Fact]
    public void A_negative_roughness_takes_Riots_sentinel_and_zeroes_F0()
    {
        Assert.Equal(InvPi, Brdf(-1f, 0f, 1f, Up, Up, Up), 6);
    }

    /// <summary>Off-axis, where the Smith and Schlick terms actually do work. V and L sit at 45 degrees
    /// either side of the normal, so H is the normal (NdotH = 1 again), NdotV = NdotL = 1/sqrt(2), the
    /// specular denominator 4*NdotV*NdotL is exactly 2, and dot(H,V) = 1/sqrt(2) drives a real Fresnel.
    /// Both Smith halves are equal here, so G is one factor squared.</summary>
    [Fact]
    public void Off_axis_engages_the_Smith_and_Schlick_terms()
    {
        var v = Vector3.Normalize(new Vector3(1f, 1f, 0f));
        var l = Vector3.Normalize(new Vector3(-1f, 1f, 0f));

        const float k = 0.28125f;                                       // (0.5+1)^2 / 8
        const float a2 = 0.0625f;
        float nd = 1f / MathF.Sqrt(2f);                                 // NdotV == NdotL
        float g1 = nd / (nd * (1f - k) + k);
        float fBase = 1f - nd;                                          // dot(H,V) = NdotV here
        float fTerm = 0.04f + 0.96f * (fBase * fBase * fBase * fBase * fBase);
        float dTerm = 1f / (Pi * a2);                                   // NdotH = 1
        float spec = fTerm * dTerm * (g1 * g1) / (2f + 0.0001f);
        float expected = ((1f - fTerm) * InvPi + spec) * nd;

        Assert.Equal(expected, Brdf(0.5f, 0f, 1f, Up, v, l), 6);
    }

    /// <summary>Roughening a surface spreads its highlight, so the head-on specular peak must fall
    /// monotonically. A sign slip in <c>a2 - 1</c> or a swapped numerator/denominator in D inverts this.</summary>
    [Fact]
    public void The_specular_peak_falls_as_roughness_rises()
    {
        float glossy = Brdf(0.1f, 0f, 1f, Up, Up, Up);
        float mid = Brdf(0.5f, 0f, 1f, Up, Up, Up);
        float rough = Brdf(1f, 0f, 1f, Up, Up, Up);

        Assert.True(glossy > mid, $"roughness 0.1 ({glossy}) should out-specular 0.5 ({mid})");
        Assert.True(mid > rough, $"roughness 0.5 ({mid}) should out-specular 1.0 ({rough})");
    }

    /// <summary>A light behind the surface contributes nothing, in the PBR family exactly as in the
    /// Lambert one - the trailing NdotL is the gate and there is no ambient wrap anywhere.
    ///
    /// <para>The light direction here is deliberately NOT the exact negation of the view direction.
    /// <c>normalize(V + L)</c> is undefined when V and L cancel exactly, and the resulting NaN survives the
    /// multiply by NdotL = 0. That hazard is Riot's too - blob 27 lines 704-706 are a <c>dp3</c> of the same
    /// sum followed by <c>rsq</c>, which is equally undefined at zero - so the shader reproduces it rather
    /// than inventing a guard Riot does not have. It needs bit-exact opposing unit vectors to trigger.</para></summary>
    [Fact]
    public void A_light_behind_the_surface_contributes_nothing()
    {
        var l = Vector3.Normalize(new Vector3(0.3f, -1f, 0f));
        Assert.Equal(0f, Brdf(0.5f, 0f, 1f, Up, Up, l), 6);
    }

    // ---------------------------------------------------------------------------------------------
    // The shipped GLSL. These are the anti-drift half: they require the mirror above to describe the
    // shader that actually ships, since nothing can execute the shader in a test.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Every term of the BRDF, present verbatim in the emitted fragment shader.</summary>
    [Fact]
    public void The_GL_fragment_shader_carries_the_GGX_terms()
    {
        string code = StripComments(MeshFrag());

        // Precompute, hoisted out of the loop (blob 27 lines 570, 585-608).
        Assert.Contains("vec3 pbrF0 = mix(vec3(0.04), base, uPbrMetal);", code);
        Assert.Contains("if (pbrRough < 0.0) { pbrRough = 0.075; pbrF0 = vec3(0.0); }", code);
        Assert.Contains("float pbrOneMinusMetal = 1.0 - uPbrMetal;", code);
        Assert.Contains("float pbrA = clamp(pbrRough, 0.04, 1.0);", code);
        Assert.Contains("float pbrA2 = pbrA * pbrA;", code);
        Assert.Contains("float pbrA2m1 = pbrA2 - 1.0;", code);
        Assert.Contains("float pbrK = (pbrRough + 1.0) * (pbrRough + 1.0) * 0.125;", code);
        Assert.Contains("float pbrGv = pbrNdotV / (pbrNdotV * (1.0 - pbrK) + pbrK);", code);
        Assert.Contains("float pbrNv4 = pbrNdotV * 4.0;", code);

        // The loop body (blob 27 lines 702-741).
        Assert.Contains("float dDen = ndh * ndh * pbrA2m1 + 1.0;", code);
        Assert.Contains("float dTerm = pbrA2 / (3.14159265 * dDen * dDen);", code);
        Assert.Contains("float gTerm = pbrGv * (ndl / (ndl * (1.0 - pbrK) + pbrK));", code);
        Assert.Contains("vec3 fTerm = pbrF0 + (1.0 - pbrF0) * (fSq * fSq * fBase);", code);
        Assert.Contains("vec3 spec = fTerm * (dTerm * gTerm) / (pbrNv4 * ndl + 0.0001);", code);
        Assert.Contains("vec3 kD = base * (1.0 - fTerm) * pbrOneMinusMetal;", code);
        Assert.Contains("* (kD * 0.31830989 + spec) * ndl;", code);
    }

    /// <summary>The precompute must stay OUTSIDE the loop. It is bounded at 1024 iterations, and Riot
    /// hoists all of this itself; sinking any of it into the body would multiply the cost by the light
    /// count for no change in result, which no numeric test could ever notice.</summary>
    [Fact]
    public void The_view_dependent_precompute_is_hoisted_out_of_the_light_loop()
    {
        string code = StripComments(MeshFrag());

        int loopAt = code.IndexOf("for (int i = 0; i < 1024; i++)", StringComparison.Ordinal);
        Assert.True(loopAt > 0, "the bounded point-light loop is gone");

        foreach (var hoisted in new[]
                 {
                     "vec3 pbrF0 =", "float pbrOneMinusMetal =", "float pbrA =", "float pbrA2 =",
                     "float pbrA2m1 =", "float pbrK =", "float pbrNdotV =", "float pbrGv =", "float pbrNv4 =",
                 })
        {
            int at = code.IndexOf(hoisted, StringComparison.Ordinal);
            Assert.True(at > 0, $"{hoisted} is missing from the shader");
            Assert.True(at < loopAt, $"{hoisted} must be computed BEFORE the light loop, not inside it");
        }
    }

    /// <summary>The whole point of the per-submesh flag: a non-PBR material must come out of this
    /// milestone running M457's Lambert term, unchanged, character for character.</summary>
    [Fact]
    public void The_Lambert_path_for_non_PBR_materials_is_untouched()
    {
        string code = StripComments(MeshFrag());

        // M457's accumulation, byte-identical, still selected when the flag is off.
        Assert.Contains("dynamicLight += lightColour.rgb * lightColour.a * atten * ndl;", code);
        Assert.Contains("float riotF = 1.0 - t;", code);
        Assert.Contains("float atten = mix(riotF, softF, uLightFalloffSoftness);", code);
        Assert.Contains("col += (uPbrLighting == 1 ? pbrLight : base * dynamicLight) * uLightIntensity;", code);

        // Neither the retired M70 ambient wrap nor a stray PBR constant may leak into that branch.
        Assert.DoesNotContain("0.35 + 0.65", code);

        int lambertAt = code.IndexOf("dynamicLight += lightColour.rgb", StringComparison.Ordinal);
        int elseAt = code.LastIndexOf("} else {", lambertAt, StringComparison.Ordinal);
        Assert.True(elseAt > 0 && lambertAt - elseAt < 120,
            "the Lambert accumulation must sit directly in the uPbrLighting else-branch");
    }

    /// <summary>A single byte above 0x7E in a shader string builds fine in C# and then blanks the viewport
    /// with no error at all - the same guard M457's tests carry.</summary>
    [Fact]
    public void The_fragment_shader_stays_ASCII_only()
    {
        string frag = MeshFrag();
        for (int i = 0; i < frag.Length; i++)
            Assert.True(frag[i] <= 0x7E,
                $"MeshFrag carries a non-ASCII character U+{(int)frag[i]:X4} at index {i}");
    }

    // ---------------------------------------------------------------------------------------------
    // Material selection: which submeshes get the PBR branch at all.
    // ---------------------------------------------------------------------------------------------

    private static MaterialBinding Binding(string shader) =>
        new("Surface", shader, Array.Empty<string>(), false,
            new List<TextureSlot> { new("Diffuse_Texture", new BinTreeString(0, "assets/d.tex")) },
            Array.Empty<MaterialParameter>());

    /// <summary>The Mantis family gets the PBR BRDF. The test goes through
    /// <c>ExtendedChannelRule.IsExtendedShader</c> rather than repeating a name check, so there stays
    /// exactly ONE definition of what Mantis means in the app.</summary>
    [Theory]
    [InlineData("Shaders/StaticMesh/Mantis_Env_Baked_PBR", true)]
    [InlineData("mantis_env_baked_pbr", true)]          // the rule is case-insensitive
    [InlineData("Shaders/StaticMesh/DefaultEnv_Flat", false)]
    [InlineData("Shaders/StaticMesh/ENV_GlowSign", false)]
    [InlineData("", false)]
    public void Only_the_Mantis_family_selects_the_PBR_BRDF(string shader, bool expected)
    {
        var profile = MaterialProfiles.Classify(Binding(shader), MaterialSourceKind.MapMaterials);

        Assert.Equal(expected, profile.IsPbrShader);
        Assert.Equal(ReyEngine.Formats.MapGeo.ExtendedChannelRule.IsExtendedShader(shader),
                     profile.IsPbrShader);
    }

    /// <summary>Nothing opts in by accident: the default profile, which every unresolved submesh falls back
    /// to, keeps the env family's Lambert term.</summary>
    [Fact]
    public void The_default_profile_is_not_PBR()
    {
        Assert.False(MaterialProfile.Default.IsPbrShader);
    }

    // ---------------------------------------------------------------------------------------------

    private static string MeshFrag()
    {
        var f = typeof(ReyEngine.Rendering.ViewportMeshRenderer)
            .GetField("MeshFrag", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f);
        return Assert.IsType<string>(f!.GetRawConstantValue());
    }

    /// <summary>Drop // comments before asserting on what the shader COMPUTES - the shader explains its own
    /// history in prose, and a substring check that cannot tell code from commentary would either fail on
    /// the explanation or push someone to delete it.</summary>
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
}
