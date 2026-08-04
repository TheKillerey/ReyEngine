using System.Runtime.InteropServices;
using System.Text;
using ReyEngine.Formats.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace ReyEngine.Rendering.D3D11;

/// <summary>
/// Experimental baked-light programs for the legacy SRX blend shaders. Riot's cache contains only
/// NO_BAKED_LIGHTING permutations for these shaders, so generated UV7/atlas data cannot be enabled by
/// changing the material alone. The programs keep the resource and constant names League binds while
/// adding the standard BAKED_LIGHT contract used by the map renderer.
/// </summary>
public static unsafe class ExperimentalSrxBlendLightmapShader
{
    public sealed record Compiled(byte[] Vertex, byte[] MasterPixel, byte[] ChemtechVertex, byte[] ChemtechPixel);

    public static bool TryCompile(out Compiled? compiled, out string? error)
    {
        compiled = null;
        if (!Compile("#define COMPILE_VERTEX 1\n" + Source, "vsmain", "vs_5_0", out var vs, out error)) return false;
        if (!Compile("#define COMPILE_PIXEL 1\n" + Source, "psmain", "ps_5_0", out var master, out error)) return false;
        if (!Compile("#define COMPILE_VERTEX 1\n#define CHEMTECH 1\n" + Source,
                "vsmain", "vs_5_0", out var chemVs, out error)) return false;
        if (!Compile("#define COMPILE_PIXEL 1\n#define CHEMTECH 1\n" + Source,
                "psmain", "ps_5_0", out var chemPs, out error)) return false;

        if (!RenameGlobalBuffer(vs!, "MaterialVertexCB", out vs, out error)) return false;
        if (!RenameGlobalBuffer(master!, "MaterialPixelCB", out master, out error)) return false;
        if (!RenameGlobalBuffer(chemVs!, "MaterialVertexCB", out chemVs, out error)) return false;
        if (!RenameGlobalBuffer(chemPs!, "MaterialPixelCB", out chemPs, out error)) return false;
        compiled = new Compiled(vs!, master!, chemVs!, chemPs!);
        return true;
    }

    private static bool RenameGlobalBuffer(byte[] bytecode, string compiledName,
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

    private static bool Compile(string source, string entryPoint, string target,
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

    // ASCII only. These offsets and resource names match the reflected contracts in the current Riot
    // cache; League binds them by name, including the renamed $Globals material cbuffer.
    private const string Source = """
#ifdef CHEMTECH
struct PixelInput
{
    float4 Position : SV_Position;
    float4 NormalWorldY : TEXCOORD0;
    float4 Uvs : TEXCOORD1;
    float2 WorldXZ : TEXCOORD2;
    float3 ShadowCoord : TEXCOORD3;
    float3 Fow : TEXCOORD4;
    float2 TerrainUv : TEXCOORD5;
};
#else
struct PixelInput
{
    float4 Position : SV_Position;
    float4 NormalWorldY : TEXCOORD0;
    float4 Uvs : TEXCOORD1;
    float2 WorldXZ : TEXCOORD2;
    float3 ShadowCoord : TEXCOORD3;
    float3 Fow : TEXCOORD4;
};
#endif

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
#ifdef CHEMTECH
    output.TerrainUv = world.xz * TERRAIN_XFORM.xy + TERRAIN_XFORM.zw;
#endif
    return output;
}
#endif

#ifdef COMPILE_PIXEL
#ifdef CHEMTECH
cbuffer MaterialPixelCB : register(b0)
{
    float4 BAKED_LIGHT_SCALE_AND_BIAS;
    float4 Tint_Color;
    float3 EmissionColor;
    float AlphaPower;
    float2 EmissionTexUV;
    float EmiRotationSpeed;
    float DesaturationValue;
};
#else
cbuffer MaterialPixelCB : register(b0)
{
    float4 BAKED_LIGHT_SCALE_AND_BIAS;
};
#endif

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

#ifdef CHEMTECH
SamplerState TERRAIN_BLEND_SharedSampler : register(s0);
SamplerComparisonState SHADOW_MAP_DEPTH_PCF_SharedSampler : register(s1);
SamplerState BAKED_LIGHT__SMP : register(s2);
SamplerState Diffuse_Texture__SMP : register(s3);
SamplerState EmissionMaskTex__SMP : register(s4);
SamplerState EmissionTex__SMP : register(s5);
SamplerState Clamp_No_Mip_SharedSampler : register(s15);

Texture2DArray TERRAIN_BLEND_SharedTexture : register(t0);
Texture2D FOW_MAP_SharedTexture : register(t1);
Texture2D SHADOW_MAP_DEPTH_PCF_SharedTexture : register(t2);
Texture2D BAKED_LIGHT__TX : register(t3);
Texture2D Diffuse_Texture__TX : register(t4);
Texture2D EmissionMaskTex__TX : register(t5);
Texture2D EmissionTex__TX : register(t6);
#else
SamplerComparisonState SHADOW_MAP_DEPTH_PCF_SharedSampler : register(s0);
SamplerState BAKED_LIGHT__SMP : register(s1);
SamplerState DiffuseTexture__SMP : register(s2);
SamplerState Clamp_No_Mip_SharedSampler : register(s15);

Texture2D FOW_MAP_SharedTexture : register(t0);
Texture2D SHADOW_MAP_DEPTH_PCF_SharedTexture : register(t1);
Texture2D BAKED_LIGHT__TX : register(t2);
Texture2D DiffuseTexture__TX : register(t3);
#endif

struct PixelOutput
{
    float4 Color : SV_Target0;
    float4 Auxiliary : SV_Target1;
};

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

float3 BakedLighting(PixelInput input, float3 normal)
{
    float shadow = ShadowVisibility(input.ShadowCoord, normal);
    float2 lightUv = input.Uvs.zw * BAKED_LIGHT_SCALE_AND_BIAS.xy + BAKED_LIGHT_SCALE_AND_BIAS.zw;
    float4 baked = BAKED_LIGHT__TX.Sample(BAKED_LIGHT__SMP, lightUv);
    shadow = min(shadow, baked.a);
    float ndl = max(dot(normal, SUN_LIGHT_DIRECTION), 0.0);
    return baked.rgb * LIGHT_MAP_COLOR_SCALE_AND_INTENSITY
         + ndl * shadow * SUN_LIGHT_COLOR.rgb;
}

float Reveal(float3 fow)
{
    float4 sampleFow = FOW_MAP_SharedTexture.Sample(Clamp_No_Mip_SharedSampler, fow.xy);
    return fow.z * (1.0 - sampleFow.a) + sampleFow.a;
}

float3 Overlay(float3 baseColor, float3 tint)
{
    float3 low = 2.0 * baseColor * tint;
    float3 high = 1.0 - 2.0 * (1.0 - baseColor) * (1.0 - tint);
    return lerp(high, low, tint < 0.5);
}

PixelOutput psmain(PixelInput input)
{
    PixelOutput output;
    float3 normal = normalize(input.NormalWorldY.xyz);
    float3 lighting = BakedLighting(input, normal);

#ifdef CHEMTECH
    float4 diffuse = Diffuse_Texture__TX.Sample(Diffuse_Texture__SMP, input.Uvs.xy);
    if (diffuse.a < AlphaPower) discard;
    float alpha = diffuse.a;
    float2 terrainUv = input.WorldXZ * TERRAIN_XFORM.xy + TERRAIN_XFORM.zw;
    float terrainTint = TERRAIN_BLEND_SharedTexture.Sample(
        TERRAIN_BLEND_SharedSampler, float3(terrainUv, 0.0)).r * Tint_Color.a;
    float3 baseColor = lerp(diffuse.rgb, diffuse.rgb * Tint_Color.rgb, saturate(terrainTint));

    float2 emissionScale = max(abs(EmissionTexUV), float2(0.0001, 0.0001));
    float angle = TIME.x * EmiRotationSpeed;
    float sine = sin(angle);
    float cosine = cos(angle);
    float2 centered = (input.Uvs.xy - 0.5) * emissionScale;
    float2 emissionUv = float2(centered.x * cosine - centered.y * sine,
                               centered.x * sine + centered.y * cosine) + 0.5;
    float emissionMask = EmissionMaskTex__TX.Sample(EmissionMaskTex__SMP, input.Uvs.xy).r;
    float3 emissionTexture = EmissionTex__TX.Sample(EmissionTex__SMP, emissionUv).rgb;
    float3 emission = Overlay(emissionTexture, emissionMask.xxx) * EmissionColor;
    float emissionGrey = dot(emission, float3(0.2125, 0.7154, 0.0721));
    emission = lerp(emission, emissionGrey.xxx, saturate(DesaturationValue));
    float3 color = baseColor * lighting + emission;
    float4 fow = FOW_MAP_SharedTexture.Sample(Clamp_No_Mip_SharedSampler, input.Fow.xy);
    output.Color = float4(lerp(fow.rgb * alpha, color, Reveal(input.Fow)), alpha);
#else
    float4 diffuse = DiffuseTexture__TX.Sample(DiffuseTexture__SMP, input.Uvs.xy);
    float3 color = diffuse.rgb * lighting;
    float4 fow = FOW_MAP_SharedTexture.Sample(Clamp_No_Mip_SharedSampler, input.Fow.xy);
    output.Color = float4(lerp(fow.rgb, color, Reveal(input.Fow)), diffuse.a);
#endif
    output.Auxiliary = float4(0.0, 0.0, 0.0, 1.0);
    return output;
}
#endif
""";
}
