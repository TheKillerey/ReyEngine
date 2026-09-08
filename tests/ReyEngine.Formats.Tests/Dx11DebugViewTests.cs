using System;
using System.Linq;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M661: the debug views in the D3D11 viewport.
///
/// <para>They could not be "wired up": in OpenGL each one is an <c>if (uMode == N)</c> branch inside our
/// own fragment shader, and D3D11 draws with Riot's compiled pixel shaders, which have no such branch.
/// So a pixel shader is GENERATED to replace Riot's for the debug pass, and these tests cover the two
/// halves of that which are pure enough to check without a GPU: which texture belongs to which debug
/// slot, and what the generated shader says.</para>
/// </summary>
public class Dx11DebugViewTests
{
    // ---- the slot rules, which are a MEASUREMENT ---------------------------------------------------

    /// <summary>The names below are the real ones, from a census over the 91 shaders Map11/12/22
    /// materials link (probe: <c>CharacterProbe debugslots</c>). Two of them corrected rules that looked
    /// obviously right when written from memory.</summary>
    [Theory]
    [InlineData("DiffuseTexture__TX", DebugSlot.Diffuse)]
    [InlineData("Diffuse_Texture__TX", DebugSlot.Diffuse)]
    [InlineData("Mask_Texture__TX", DebugSlot.Mask)]
    [InlineData("MaskTexture__TX", DebugSlot.Mask)]
    [InlineData("_MaskTex__TX", DebugSlot.Mask)]
    [InlineData("Emissive_Texture__TX", DebugSlot.Emissive)]
    public void TheOrdinaryNamesClassifyAsExpected(string name, DebugSlot expected) =>
        Assert.Equal(expected, ShaderPreviewRenderer.ClassifyDebugSlot(name));

    /// <summary>The lightmap is BAKED_LIGHT__TX. A rule matching "BakedLight" does not match it, and the
    /// first version of these rules did exactly that — 579 material uses would have shown the Lightmap
    /// view as its "no lightmap" dark blue on maps that have one.</summary>
    [Fact]
    public void TheLightmapIsBAKED_LIGHT_WhichCamelCaseDoesNotMatch()
    {
        Assert.Equal(DebugSlot.Lightmap, ShaderPreviewRenderer.ClassifyDebugSlot("BAKED_LIGHT__TX"));
        Assert.Equal(DebugSlot.Lightmap, ShaderPreviewRenderer.ClassifyDebugSlot("BakedLightTexture__TX"));
    }

    /// <summary>The emissive is sometimes EmissionTex__TX. A rule matching only "Emissive"/"Glow" misses
    /// it, which the census caught.</summary>
    [Fact]
    public void EmissionCountsAsEmissiveAndNotOnlyEmissive()
    {
        Assert.Equal(DebugSlot.Emissive, ShaderPreviewRenderer.ClassifyDebugSlot("EmissionTex__TX"));
        Assert.Equal(DebugSlot.Emissive, ShaderPreviewRenderer.ClassifyDebugSlot("Emissive_Texture__TX"));
        Assert.Equal(DebugSlot.Emissive, ShaderPreviewRenderer.ClassifyDebugSlot("GlowTexture__TX"));
    }

    /// <summary>
    /// The single most common texture name across map shaders is FOW_MAP_SharedTexture, at 9,049 uses -
    /// more than the diffuse. It is the fog of war, an ENGINE resource, and so is every other
    /// _SharedTexture. Taking one as a material slot would make the debug views show the wrong thing at
    /// the very top of the frequency list.
    /// </summary>
    [Theory]
    [InlineData("FOW_MAP_SharedTexture")]
    [InlineData("TERRAIN_BLEND_SharedTexture")]
    [InlineData("ENV_CUBE_SharedTexture")]
    [InlineData("sDepthTexture_SharedTexture")]
    [InlineData("PIXEL_COLOR_REMAP_RAMP_SharedTexture")]
    public void EngineSharedTexturesAreNotMaterialSlots(string name) =>
        Assert.Equal(DebugSlot.None, ShaderPreviewRenderer.ClassifyDebugSlot(name));

    /// <summary>A matcap MASK is neither - it gates the matcap rather than being it, and calling it the
    /// mask would put it in the Mask view where it does not belong.</summary>
    [Fact]
    public void AMatCapMaskIsNeitherMatCapNorMask()
    {
        Assert.Equal(DebugSlot.MatCap, ShaderPreviewRenderer.ClassifyDebugSlot("MatCapTexture__TX"));
        Assert.Equal(DebugSlot.None, ShaderPreviewRenderer.ClassifyDebugSlot("MatCap_Mask__TX"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Noise_Texture__TX")]
    [InlineData("Decal_Texture__TX")]
    [InlineData("Flow_Map__TX")]
    public void AnythingUnrecognisedIsNoSlotRatherThanAGuess(string name) =>
        Assert.Equal(DebugSlot.None, ShaderPreviewRenderer.ClassifyDebugSlot(name));

    // ---- the generated shader ----------------------------------------------------------------------

    private static DxbcSignatureElement Out(string semantic, uint index, byte mask, uint sv = 0) =>
        new(semantic, index, 0, mask, 0, 3, sv);

    /// <summary>A signature shaped like a real map vertex shader's: position, uv, world normal, colour.</summary>
    private static DxbcShader Signature(bool withColour = true, bool withNormal = true) =>
        new()
        {
            Bytecode = Array.Empty<byte>(),
            Stage = DxbcStage.Vertex,
            Outputs = new[]
            {
                Out("SV_POSITION", 0, 0xF, 1),
                Out("TEXCOORD", 0, 0x3),
                withNormal ? Out("TEXCOORD", 1, 0x7) : Out("TEXCOORD", 1, 0x3),
                withColour ? Out("COLOR", 0, 0xF) : Out("TEXCOORD", 2, 0x3),
            },
        };

    [Fact]
    public void TheGeneratedShaderDeclaresAllFiveDebugSlotsAtFixedRegisters()
    {
        string hlsl = ShaderPreviewRenderer.BuildDebugShaderSource(Signature());
        Assert.Contains("register(t0)", hlsl);
        Assert.Contains("register(t1)", hlsl);
        Assert.Contains("register(t2)", hlsl);
        Assert.Contains("register(t3)", hlsl);
        Assert.Contains("register(t4)", hlsl);
        Assert.Contains("register(b0)", hlsl);
        Assert.Contains("register(s0)", hlsl);
    }

    /// <summary>Every mode the dropdown offers has to have somewhere to go, or picking it silently shows
    /// the mode before it.</summary>
    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)]
    [InlineData(14)]
    public void EveryDebugModeHasABranch(int mode)
    {
        string hlsl = ShaderPreviewRenderer.BuildDebugShaderSource(Signature());
        Assert.Contains($"mode == {mode}", hlsl);
    }

    /// <summary>The input struct must mirror the vertex shader's OUTPUT signature exactly - a pixel
    /// shader whose interpolants do not match the stage before it does not link.</summary>
    [Fact]
    public void TheInputStructMirrorsTheVertexOutputs()
    {
        string hlsl = ShaderPreviewRenderer.BuildDebugShaderSource(Signature());
        Assert.Contains(": SV_Position;", hlsl);
        Assert.Contains(": TEXCOORD0;", hlsl);
        Assert.Contains(": TEXCOORD1;", hlsl);
        Assert.Contains(": COLOR0;", hlsl);
        Assert.Contains("SV_IsFrontFace", hlsl);   // the face-orientation view has no other source
    }

    /// <summary>Missing inputs fall back the way the OpenGL shader does rather than failing to compile:
    /// magenta for absent vertex colour, a flat up-normal when nothing carries one.</summary>
    [Fact]
    public void AMissingSemanticFallsBackTheWayTheOpenGlShaderDoes()
    {
        string noColour = ShaderPreviewRenderer.BuildDebugShaderSource(Signature(withColour: false));
        Assert.Contains("mode == 10) return float4(0.8, 0.0, 0.8, 1.0)", noColour);

        var positionOnly = new DxbcShader
        {
            Bytecode = Array.Empty<byte>(),
            Stage = DxbcStage.Vertex,
            Outputs = new[] { Out("SV_POSITION", 0, 0xF, 1) },
        };
        string bare = ShaderPreviewRenderer.BuildDebugShaderSource(positionOnly);
        Assert.Contains("float3 nrm = float3(0.0, 1.0, 0.0);", bare);
        Assert.Contains("float2 uv = float2(0.5, 0.5);", bare);
    }

    /// <summary>The stand-in colours are the OpenGL shader's, so the same view answers the same question
    /// the same way in both renderers - white for no mask, black for no emissive, dark blue for no
    /// lightmap, the same green/red for facing.</summary>
    [Fact]
    public void TheStandInColoursMatchTheOpenGlShader()
    {
        string hlsl = ShaderPreviewRenderer.BuildDebugShaderSource(Signature());
        Assert.Contains("float3(1,1,1)", hlsl);           // mask, when there is none
        Assert.Contains("float3(0,0,0)", hlsl);           // emissive, when there is none
        Assert.Contains("float3(0.2,0.2,0.2)", hlsl);     // matcap, when there is none
        Assert.Contains("float3(0.03,0.03,0.08)", hlsl);  // lightmap, when there is none
        Assert.Contains("float4(0.15,0.80,0.30,1.0)", hlsl);   // front faces
        Assert.Contains("float4(0.90,0.20,0.20,1.0)", hlsl);   // back faces
    }

    /// <summary>Two materials whose vertex shaders write the same signature share one generated shader;
    /// that is what keeps this to a handful of compiles on a map with 91 shaders.</summary>
    [Fact]
    public void TheSameSignatureProducesTheSameSource()
    {
        Assert.Equal(ShaderPreviewRenderer.BuildDebugShaderSource(Signature()),
                     ShaderPreviewRenderer.BuildDebugShaderSource(Signature()));
        Assert.NotEqual(ShaderPreviewRenderer.BuildDebugShaderSource(Signature()),
                        ShaderPreviewRenderer.BuildDebugShaderSource(Signature(withColour: false)));
    }

    /// <summary>Basic and RiotApprox are Riot's OWN shaders in this renderer - there is no approximation
    /// to differ from, because the real thing is what draws. Only 2 and up replace anything.</summary>
    [Fact]
    public void ModesBelowTwoAreNotDebugModes() =>
        Assert.Equal(2, ShaderPreviewRenderer.FirstDebugMode);

    // ---- which interpolator is which (M671) --------------------------------------------------------

    static DxbcShader Signature(params (string Semantic, uint Index, byte Mask, uint Register, uint SysValue)[] outs) => new()
    {
        Bytecode = Array.Empty<byte>(),
        Outputs = outs.Select(o => new DxbcSignatureElement(o.Semantic, o.Index, o.Register, o.Mask, o.Mask, 3, o.SysValue)).ToList(),
    };

    /// <summary>
    /// M671. skinnedmesh/onsen's vertex shader writes its VERTEX COLOUR to TEXCOORD0 (four wide) and the
    /// uv to TEXCOORD1.xy. M661's rule - the first two-or-more-component TEXCOORD is the uv - took the
    /// colour, every pixel sampled texel (0.02, 0.02), and the Diffuse debug view of Locke was one flat
    /// brown silhouette. The signature below is the real one (probe: AatroxTrace psdump Locke).
    /// </summary>
    [Fact]
    public void OnsensFourWideTexcoord0IsNotTheUv()
    {
        var vs = Signature(
            ("SV_Position", 0, 0xF, 0, 1),
            ("TEXCOORD", 0, 0xF, 1, 0),   // vertex colour
            ("TEXCOORD", 1, 0x3, 2, 0),   // uv
            ("TEXCOORD", 2, 0xC, 2, 0),   // fog-of-war uv
            ("TEXCOORD", 3, 0x7, 3, 0),   // world normal
            ("TEXCOORD", 4, 0x7, 4, 0),   // world position
            ("COLOR", 0, 0xF, 5, 0));     // the ambient cube, evaluated per vertex
        var pick = ShaderPreviewRenderer.PickDebugInterpolators(vs);
        Assert.Equal("f2", pick.Uv);
        Assert.Equal("f4", pick.Normal);
        Assert.Equal("f3", pick.LightmapUv);
        Assert.Equal("f6", pick.Colour);
        Assert.Contains("float2 uv = i.f2.xy;", ShaderPreviewRenderer.BuildDebugShaderSource(vs));
    }

    /// <summary>The shapes M661 was written against keep their answers: a map signature (uv, lightmap
    /// uv, normal) and skinnedmesh/diffuse_alpha (uv, fog uv, normal, world position, colour).</summary>
    [Fact]
    public void TheMapAndDiffuseAlphaShapesAreUnchanged()
    {
        var map = ShaderPreviewRenderer.PickDebugInterpolators(Signature(
            ("SV_Position", 0, 0xF, 0, 1), ("TEXCOORD", 0, 0x3, 1, 0), ("TEXCOORD", 1, 0x3, 2, 0), ("TEXCOORD", 2, 0x7, 3, 0)));
        Assert.Equal("f1", map.Uv);
        Assert.Equal("f2", map.LightmapUv);
        Assert.Equal("f3", map.Normal);
        Assert.Null(map.Colour);

        var da = ShaderPreviewRenderer.PickDebugInterpolators(Signature(
            ("SV_Position", 0, 0xF, 0, 1), ("TEXCOORD", 0, 0x3, 1, 0), ("TEXCOORD", 1, 0xC, 1, 0),
            ("TEXCOORD", 2, 0x7, 2, 0), ("TEXCOORD", 3, 0x7, 3, 0), ("COLOR", 0, 0xF, 4, 0)));
        Assert.Equal("f1", da.Uv);
        Assert.Equal("f2", da.LightmapUv);
        Assert.Equal("f3", da.Normal);
        Assert.Equal("f5", da.Colour);
    }

    /// <summary>A signature that packs its uv wider than two keeps the M661 fallback rather than losing
    /// the uv altogether.</summary>
    [Fact]
    public void AWiderUvStillCountsWhenNoTwoWideTexcoordExists()
    {
        var p = ShaderPreviewRenderer.PickDebugInterpolators(Signature(
            ("SV_Position", 0, 0xF, 0, 1), ("TEXCOORD", 0, 0x7, 1, 0), ("TEXCOORD", 1, 0x7, 2, 0)));
        Assert.Equal("f1", p.Uv);
        Assert.Equal("f2", p.Normal);
    }

}
