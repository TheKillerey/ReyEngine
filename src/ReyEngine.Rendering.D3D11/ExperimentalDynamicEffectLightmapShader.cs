using System.Runtime.InteropServices;
using System.Text;
using ReyEngine.Formats.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace ReyEngine.Rendering.D3D11;

/// <summary>
/// M312 experimental in-game shader-cache patch: an SRX_DynamicEffect-compatible static-mesh shader
/// that keeps the shipped diffuse/tint and flow-ripple operations, then applies Riot's baked-light term.
/// This compiles to ordinary DXBC; <c>ShaderCachePatchWriter</c> gives it the missing permutation keys.
/// </summary>
public static unsafe class ExperimentalDynamicEffectLightmapShader
{
    public sealed record Compiled(byte[] Vertex, byte[] Pixel, byte[] FlowRipplePixel);

    /// <summary>Compile the three blobs used by every affected Winter Rift material.</summary>
    public static bool TryCompile(out Compiled? compiled, out string? error)
    {
        compiled = null;
        if (!TryCompile("#define COMPILE_VERTEX 1\n" + Source, "vsmain", "vs_5_0", out var vs, out error)) return false;
        if (!TryCompile("#define COMPILE_PIXEL 1\n" + Source, "psmain", "ps_5_0", out var ps, out error)) return false;
        if (!TryCompile("#define COMPILE_PIXEL 1\n#define FLOW_RIPPLE_ON 1\n" + Source,
                "psmain", "ps_5_0", out var flow, out error)) return false;
        if (!TryRenameGlobalBuffer(vs!, "MaterialVertexCB", out vs, out error)) return false;
        if (!TryRenameGlobalBuffer(ps!, "MaterialPixelCB", out ps, out error)) return false;
        if (!TryRenameGlobalBuffer(flow!, "MaterialPixelCB", out flow, out error)) return false;
        compiled = new Compiled(vs!, ps!, flow!);
        return true;
    }

    /// <summary>
    /// Riot's material binder identifies the material cbuffer by the reflected name <c>$Globals</c>.
    /// HLSL reserves that compiler-generated name and rejects it in source, so compile with a longer legal
    /// identifier and shorten its null-terminated RDEF string in place. Offsets and bytecode stay unchanged;
    /// only reflection metadata changes. D3D11 shader creation is covered by the Windows regression test.
    /// </summary>
    private static bool TryRenameGlobalBuffer(byte[] bytecode, string compiledName,
        out byte[]? rewritten, out string? error)
    {
        rewritten = null;
        error = null;
        const string riotName = "$Globals";
        byte[] from = Encoding.ASCII.GetBytes(compiledName);
        byte[] to = Encoding.ASCII.GetBytes(riotName);
        if (to.Length > from.Length)
        {
            error = $"RDEF name {riotName} is longer than {compiledName}";
            return false;
        }

        var rdef = DxbcReflection.Chunks(bytecode).SingleOrDefault(c => c.Tag == "RDEF");
        if (rdef.Tag is null)
        {
            error = "compiled shader has no RDEF chunk";
            return false;
        }

        rewritten = (byte[])bytecode.Clone();
        int replacements = 0;
        int end = rdef.Offset + rdef.Size;
        for (int at = rdef.Offset; at + from.Length < end; at++)
        {
            if (rewritten[at + from.Length] != 0
                || !rewritten.AsSpan(at, from.Length).SequenceEqual(from)) continue;

            to.CopyTo(rewritten, at);
            rewritten.AsSpan(at + to.Length, from.Length - to.Length).Clear();
            replacements++;
            at += from.Length;
        }

        if (replacements == 0)
        {
            rewritten = null;
            error = $"compiled shader RDEF does not contain {compiledName}";
            return false;
        }

        var reflection = DxbcReflection.Parse(rewritten);
        if (!reflection.ConstantBuffers.Any(cb => cb.Name == riotName)
            || reflection.ConstantBuffers.Any(cb => cb.Name == compiledName))
        {
            rewritten = null;
            error = $"compiled shader RDEF did not validate after renaming {compiledName} to {riotName}";
            return false;
        }
        if (!DxbcChecksum.TryUpdate(rewritten) || !DxbcChecksum.IsValid(rewritten))
        {
            rewritten = null;
            error = "failed to rebuild the compiled shader's DXBC checksum";
            return false;
        }
        return true;
    }

    private static bool TryCompile(string source, string entryPoint, string target,
        out byte[]? bytecode, out string? error)
    {
        bytecode = null;
        error = null;
        var src = Encoding.ASCII.GetBytes(source);
        var entry = Encoding.ASCII.GetBytes(entryPoint + "\0");
        var profile = Encoding.ASCII.GetBytes(target + "\0");
        ID3D10Blob* code = null;
        ID3D10Blob* errors = null;
        int hr;
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* sourcePtr = src)
            fixed (byte* entryPtr = entry)
            fixed (byte* profilePtr = profile)
                hr = compiler.Compile(sourcePtr, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                    entryPtr, profilePtr, 0u, 0u, &code, &errors);
        }
        catch (Exception ex)
        {
            error = "the Windows HLSL compiler is unavailable: " + ex.Message;
            return false;
        }

        try
        {
            if (hr < 0 || code is null)
            {
                error = errors is null
                    ? $"{target} compilation failed: 0x{hr:X8}"
                    : Marshal.PtrToStringAnsi((nint)errors->GetBufferPointer());
                return false;
            }

            int length = checked((int)code->GetBufferSize());
            bytecode = new byte[length];
            Marshal.Copy((nint)code->GetBufferPointer(), bytecode, 0, length);
            return true;
        }
        finally
        {
            if (errors is not null) errors->Release();
            if (code is not null) code->Release();
        }
    }

    // Keep this ASCII-only. Unlike the GL driver it is not known to reject UTF-8 source, but making the
    // compiler input deterministic is more valuable than relying on that distinction.
    private const string Source = """
struct PixelInput
{
    float4 Position : SV_Position;
    float4 NormalWorldY : TEXCOORD0;
    float4 Uvs : TEXCOORD1;
    float2 WorldXZ : TEXCOORD2;
    float3 ShadowCoord : TEXCOORD3;
    float3 Fow : TEXCOORD4;
};

#ifdef COMPILE_VERTEX
cbuffer MaterialVertexCB : register(b1)
{
    row_major float4x4 WORLD_MATRIX;
};

cbuffer PerFrameVertexCB : register(b2)
{
    row_major float4x4 mProj;
    float3 vCamera;
    float _vertexPad0;
    float4 TIME;
    float4 TERRAIN_XFORM;
    row_major float4x4 VIEW_PROJECTION_MATRIX;
    row_major float4x4 mShadowProj;
    row_major float4x4 SCREEN_MATRIX;
    float4 FOG_OF_WAR_PARAMS;
    float4 FOG_OF_WAR_ALWAYS_BELOW_Y;
    float4 FOW_HEIGHT_FADE;
    float4 NAV_GRID_XFORM;
    float4 MANTIS_FORCE_DATA;
    row_major float4x4 mView;
    row_major float4x4 mViewInv;
    float4 GLOBAL_ENVIRONMENT_VALUES;
    float3 SUN_LIGHT_DIRECTION;
    float NORMAL_OFFSET_BIAS;
    float DRAGON_TERRAIN;
    uint ENV_QUALITY;
    float2 _vertexPad1;
};

struct VertexInput
{
    float3 Position : POSITION0;
    float3 Normal : NORMAL0;
    float2 Uv : TEXCOORD0;
    float2 LightUv : TEXCOORD7;
};

PixelInput vsmain(VertexInput input)
{
    PixelInput output;
    float4 world = mul(WORLD_MATRIX, float4(input.Position, 1.0));
    float3 normal = normalize(mul((float3x3)WORLD_MATRIX, input.Normal));
    float facing = (1.0 - abs(dot(normal, SUN_LIGHT_DIRECTION))) * NORMAL_OFFSET_BIAS;
    float4 shadowWorld = float4(world.xyz + normal * facing, 1.0);

    output.Position = mul(VIEW_PROJECTION_MATRIX, world);
    output.NormalWorldY = float4(normal, world.y);
    output.Uvs = float4(input.Uv, input.LightUv);
    output.WorldXZ = world.xz;
    output.ShadowCoord = mul(mShadowProj, shadowWorld).xyz;
    output.Fow.xy = world.xz * FOG_OF_WAR_PARAMS.xy + FOG_OF_WAR_PARAMS.zw;
    output.Fow.z = saturate(world.y * FOW_HEIGHT_FADE.x + FOW_HEIGHT_FADE.y);
    return output;
}
#endif

#ifdef COMPILE_PIXEL
cbuffer MaterialPixelCB : register(b0)
{
    float4 BAKED_LIGHT_SCALE_AND_BIAS;
    float4 BaseTex_TintColor;
    float4 FLOW_Color;
    float3 EMISSION_EmissionColor;
    float FLOW_RIPPLE_Frequence;
    float2 FLOW_Center;
    float2 EMISSION_ROTATE_TexUVScale;
    float2 EMISSION_ROTATE_RotationCenter;
    float FLOW_RIPPLE_Width;
    float FLOW_RIPPLE_ShapeSmoothness;
    float FLOW_RIPPLE_Dark;
    float FLOW_RIPPLE_Bright;
    float FLOW_FAN_Count;
    float EMISSION_AnimationSpeed;
};

cbuffer PerFramePixelCB : register(b1)
{
    float3 vCamera;
    float _pixelPad0;
    float4 TIME;
    float4 TERRAIN_XFORM;
    float3 SHADOW_COLOR;
    float _pixelPad1;
    float3 SHADOW_COLOR_COMPLEMENT;
    float _pixelPad2;
    float4 cDepthConversionParams;
    float4 SUN_LIGHT_COLOR;
    float SUN_PENUMBRA_SATURATION;
    float3 SUN_LIGHT_DIRECTION;
    float LIGHT_MAP_COLOR_SCALE_AND_INTENSITY;
    float3 ENV_FOG_COLOR;
    float3 ENV_FOG_ALT_COLOR;
    float _pixelPad3;
    float4 ENV_FOG_START_END_SCALE_EMISSIVE_REMAP;
    float4 FOG_OVERLAY_UV_ANIMATE;
    float4 FOW_EDGE_CONTROL;
    float3 SUN_LIGHT_DIRECTION_FOR_SPEC;
    float _pixelPad4;
    float4 SHADOW_SAMPLE_OFFSETS;
    float4 LIGHT_GRID_WORLD_TO_GRID;
    float LIGHT_GRID_TEXTURE_SCALE;
    float GRASS_INTERP;
    float ENV_BRIGHTNESS;
    float _pixelPad5;
    row_major float4x4 mView;
    float4 SPOT_SHADOW_SAMPLE_OFFSETS;
    float HDR_ENV_DIFFUSE_SCALE;
    float3 _pixelPad6;
    float4 NAV_GRID_XFORM;
    float IBL_CUBEMAP_INDEX;
    float3 _pixelPad7;
    float4 UI_FRAMEBUFFER_COPY_SIZE;
    row_major float4x4 mViewInv;
    uint ENV_QUALITY;
    float CONSTANT_DEPTH_BIAS;
    float SLOPE_SCALED_DEPTH_BIAS;
    float _pixelPad8;
    float4 WATER_DISTURBANCE_XFORM;
};

SamplerComparisonState SHADOW_MAP_DEPTH_PCF_SharedSampler : register(s0);
SamplerState BAKED_LIGHT__SMP : register(s1);
SamplerState DiffuseTexture__SMP : register(s2);
SamplerState Mask_Tex__SMP : register(s3);
SamplerState Clamp_No_Mip_SharedSampler : register(s15);

Texture2D FOW_MAP_SharedTexture : register(t0);
Texture2D SHADOW_MAP_DEPTH_PCF_SharedTexture : register(t1);
Texture2D BAKED_LIGHT__TX : register(t2);
Texture2D DiffuseTexture__TX : register(t3);
Texture2D Mask_Tex__TX : register(t4);

struct PixelOutput
{
    float4 Color : SV_Target0;
    float4 Auxiliary : SV_Target1;
};

float3 Overlay(float3 baseColor, float3 tint)
{
    float3 low = 2.0 * baseColor * tint;
    float3 high = 1.0 - 2.0 * (1.0 - baseColor) * (1.0 - tint);
    return lerp(high, low, tint < 0.5);
}

float ShadowVisibility(float3 shadowCoord, float3 normal)
{
    float3 sun = normalize(SUN_LIGHT_DIRECTION);
    float slope = 1.0 - saturate(dot(normal, -sun));
    float depth = saturate(shadowCoord.z - (SLOPE_SCALED_DEPTH_BIAS * slope + CONSTANT_DEPTH_BIAS));
    float result = SHADOW_MAP_DEPTH_PCF_SharedTexture.SampleCmpLevelZero(
        SHADOW_MAP_DEPTH_PCF_SharedSampler, shadowCoord.xy, depth);
    result += SHADOW_MAP_DEPTH_PCF_SharedTexture.SampleCmpLevelZero(
        SHADOW_MAP_DEPTH_PCF_SharedSampler, shadowCoord.xy + SHADOW_SAMPLE_OFFSETS.xy, depth);
    result += SHADOW_MAP_DEPTH_PCF_SharedTexture.SampleCmpLevelZero(
        SHADOW_MAP_DEPTH_PCF_SharedSampler, shadowCoord.xy - SHADOW_SAMPLE_OFFSETS.xy, depth);
    result += SHADOW_MAP_DEPTH_PCF_SharedTexture.SampleCmpLevelZero(
        SHADOW_MAP_DEPTH_PCF_SharedSampler, shadowCoord.xy + SHADOW_SAMPLE_OFFSETS.zw, depth);
    result += SHADOW_MAP_DEPTH_PCF_SharedTexture.SampleCmpLevelZero(
        SHADOW_MAP_DEPTH_PCF_SharedSampler, shadowCoord.xy - SHADOW_SAMPLE_OFFSETS.zw, depth);
    return result * 0.2;
}

PixelOutput psmain(PixelInput input)
{
    PixelOutput output;
    float4 diffuse = DiffuseTexture__TX.Sample(DiffuseTexture__SMP, input.Uvs.xy);
    if (diffuse.a < 0.0) discard;
    float3 baseColor = Overlay(diffuse.rgb, BaseTex_TintColor.rgb);

#ifdef FLOW_RIPPLE_ON
    float mask = Mask_Tex__TX.Sample(Mask_Tex__SMP, input.Uvs.xy).g;
    float2 delta = float2(0.5, 0.5) - input.Uvs.xy - FLOW_Center;
    float wave = sin((length(delta) - TIME.x * 0.1) * FLOW_RIPPLE_Frequence);
    float shape = saturate((wave - FLOW_RIPPLE_Width) / FLOW_RIPPLE_ShapeSmoothness);
    shape = shape * shape * (3.0 - 2.0 * shape);
    float brightness = lerp(FLOW_RIPPLE_Dark, FLOW_RIPPLE_Bright, shape);
    float3 flow = mask * FLOW_Color.rgb;
    baseColor += flow * flow * brightness;
#endif

    float3 normal = normalize(input.NormalWorldY.xyz);
    float shadow = ShadowVisibility(input.ShadowCoord, normal);
    float2 lightUv = input.Uvs.zw * BAKED_LIGHT_SCALE_AND_BIAS.xy + BAKED_LIGHT_SCALE_AND_BIAS.zw;
    float4 baked = BAKED_LIGHT__TX.Sample(BAKED_LIGHT__SMP, lightUv);
    shadow = min(shadow, baked.a);
    float ndl = max(dot(normal, SUN_LIGHT_DIRECTION), 0.0);
    float3 lighting = baked.rgb * LIGHT_MAP_COLOR_SCALE_AND_INTENSITY
                    + ndl * shadow * SUN_LIGHT_COLOR.rgb;

    float4 fow = FOW_MAP_SharedTexture.Sample(Clamp_No_Mip_SharedSampler, input.Fow.xy);
    float reveal = input.Fow.z * (1.0 - fow.a) + fow.a;
    output.Color = float4(lerp(fow.rgb, baseColor * lighting, reveal), diffuse.a);
    output.Auxiliary = float4(0.0, 0.0, 0.0, 1.0);
    return output;
}
#endif
""";
}
