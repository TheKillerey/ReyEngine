using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Rendering.D3D11;

/// <summary>Which of a material's bound textures a debug view wants. See the census note on
/// <see cref="ShaderPreviewRenderer.ClassifyDebugSlot"/> for how these were decided.</summary>
public enum DebugSlot { None = 0, Diffuse, Mask, Emissive, MatCap, Lightmap }

/// <summary>
/// M661: the viewport's debug views, for the D3D11 renderer.
///
/// <para><b>Why they could not simply be "wired up".</b> In OpenGL every debug view is an
/// <c>if (uMode == N)</c> branch inside OUR fragment shader. D3D11 draws with Riot's own compiled pixel
/// shaders, which have no such branch and cannot be given one — so the mode was not a setting anybody
/// had forgotten to pass, it was a feature with nowhere to live. It lives here: a generated pixel shader
/// that REPLACES Riot's for the duration of the debug pass, built on the same reflection trick
/// <see cref="ShaderPreviewRenderer.BuildComparisonShader"/> already used.</para>
///
/// <para><b>One shader per vertex signature, not per material.</b> The generated shader has to declare
/// the exact interpolants the material's vertex shader writes, and a map uses 91 distinct shaders — but
/// far fewer distinct signatures. The mode itself is a constant the shader branches on, so switching
/// debug view costs no compile at all. What varies per material is which texture is the diffuse, the
/// mask and so on; the debug pass binds those to FIXED registers t0..t4, which is what lets one shader
/// serve every material.</para>
/// </summary>
public sealed unsafe partial class ShaderPreviewRenderer
{
    /// <summary>Anything below this draws normally: 0 Basic and 1 RiotApprox are Riot's own shaders,
    /// which is what the D3D11 viewport already showed.</summary>
    public const int FirstDebugMode = 2;

    private readonly Dictionary<string, ComPtr<ID3D11PixelShader>> _debugPs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _debugPsFailed = new(StringComparer.Ordinal);
    private ComPtr<ID3D11Buffer> _debugCb;

    /// <summary>M661: how many materials this frame drew through a generated debug shader, and how many
    /// could not get one. A debug view that silently falls back to the normal render looks like the mode
    /// doing nothing, so the counts are reported rather than swallowed.</summary>
    public int DebugDraws { get; private set; }
    public int DebugFallbacks { get; private set; }

    /// <summary>
    /// Which debug slot a shader's texture belongs to, by its reflected DXBC name.
    ///
    /// <para><b>Measured, not guessed.</b> A census over the 91 shaders that Map11/12/22 materials
    /// actually link (probe: <c>CharacterProbe debugslots</c>) gave 68 distinct texture names and
    /// corrected two rules that looked obviously right: the lightmap is <c>BAKED_LIGHT__TX</c>, which
    /// "BakedLight" does not match, and the emissive is sometimes <c>EmissionTex__TX</c>, which
    /// "Emissive" does not match. It also showed the single most common name of all,
    /// <c>FOW_MAP_SharedTexture</c> at 9,049 uses, is not a material texture at all — every
    /// <c>_SharedTexture</c> is engine-owned (fog of war, terrain blend, the env cube, the depth copy),
    /// and binding one as "the diffuse" would be a debug view of the wrong thing.</para>
    ///
    /// <para>Coverage over 9,062 map materials: diffuse 98.0%, lightmap 6.4%, mask 2.0%, emissive 0.6%,
    /// matcap 0.0%. The small numbers are the DATA, not a gap in the rules — a map material usually has
    /// only a diffuse, so Mask reads white and Emissive black nearly everywhere, exactly as the OpenGL
    /// viewport shows them.</para>
    /// </summary>
    public static DebugSlot ClassifyDebugSlot(string name)
    {
        if (string.IsNullOrEmpty(name)) return DebugSlot.None;
        if (name.EndsWith("_SharedTexture", StringComparison.OrdinalIgnoreCase)) return DebugSlot.None;
        bool Has(string s) => name.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("MatCap")) return Has("Mask") ? DebugSlot.None : DebugSlot.MatCap;
        if (Has("Mask")) return DebugSlot.Mask;
        if (Has("Emissive") || Has("Emission") || Has("Glow")) return DebugSlot.Emissive;
        if (Has("Lightmap") || Has("Light_Map") || Has("BAKED_LIGHT") || Has("BakedLight")) return DebugSlot.Lightmap;
        if (Has("Diffuse") || Has("BaseColor") || Has("Albedo") || Has("MainTex")) return DebugSlot.Diffuse;
        return DebugSlot.None;
    }

    /// <summary>Remember a bound texture under its debug slot, so the debug pass can find it without
    /// knowing the material's register layout. Called wherever a material takes a texture.</summary>
    internal static void RecordDebugSlot(PreviewMaterial m, string reflectedName,
        ComPtr<ID3D11ShaderResourceView> srv)
    {
        var slot = ClassifyDebugSlot(reflectedName);
        if (slot != DebugSlot.None) m.DebugTextures[slot] = srv;
    }

    /// <summary>
    /// A key that two materials share exactly when one generated shader can serve both: the vertex
    /// shader's output signature, which is what the pixel shader's input struct has to match.
    /// </summary>
    private static string DebugSignatureKey(DxbcShader vsRefl) =>
        string.Join('|', vsRefl.Outputs.Select(o => $"{o.FullSemantic}:{Math.Max(1, o.ComponentCount)}:{o.SystemValueType}"));

    /// <summary>The generated debug pixel shader for this material's vertex signature, compiled once.
    /// Null when it will not compile - the caller then draws the material normally rather than not at
    /// all, and counts it.</summary>
    private ComPtr<ID3D11PixelShader> DebugPixelShaderFor(DxbcShader vsRefl)
    {
        string key = DebugSignatureKey(vsRefl);
        if (_debugPs.TryGetValue(key, out var cached)) return cached;
        if (_debugPsFailed.Contains(key)) return default;

        string hlsl = BuildDebugShaderSource(vsRefl);
        var ps = CompileDebugPixelShader(hlsl, out string? error);
        if (ps.Handle is null)
        {
            _debugPsFailed.Add(key);
            Log($"debug shader for signature [{key}] did not compile: {error}");
            return default;
        }
        _debugPs[key] = ps;
        return ps;
    }

    /// <summary>M671: which vertex-shader outputs the generated shaders read as the uv, the world normal,
    /// the vertex colour and the lightmap uv. Field names follow the PSIn struct the generators emit:
    /// <c>f0</c>, <c>f1</c>, ... in output order, SV_Position included.</summary>
    public readonly record struct DebugInterpolators(string? Uv, string? Normal, string? Colour, string? LightmapUv);

    private static bool IsPositionOutput(DxbcSignatureElement o) =>
        o.SystemValueType == 1 || o.Semantic.StartsWith("SV_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// M661 read the signature in order: the first TEXCOORD with two or more components was the uv, a
    /// later three-component one the normal, a second two-component one the lightmap's. That is the
    /// shape of every map vertex shader and of skinnedmesh/diffuse_alpha. It is not the shape of
    /// skinnedmesh/onsen, whose TEXCOORD0 is the four-wide VERTEX COLOUR and whose uv is TEXCOORD1.xy:
    /// the rule took the colour as the uv, every pixel sampled texel (0.02, 0.02), and the Diffuse view
    /// of Locke was one flat brown silhouette (M671, probe: AatroxTrace psdump). The uv is now the first
    /// TEXCOORD with EXACTLY two components when the signature has one; the wider-first fallback stays
    /// for a signature that packs its uv into a wider register. The normal and lightmap rules are the
    /// M661 ones, read after the uv as before.
    /// </summary>
    public static DebugInterpolators PickDebugInterpolators(DxbcShader vsRefl)
    {
        var outs = vsRefl.Outputs;
        static bool IsTex(DxbcSignatureElement o) =>
            !IsPositionOutput(o) && o.Semantic.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase);
        static int Comps(DxbcSignatureElement o) => Math.Max(1, o.ComponentCount);

        int uv = -1;
        for (int i = 0; i < outs.Count && uv < 0; i++) if (IsTex(outs[i]) && Comps(outs[i]) == 2) uv = i;
        for (int i = 0; i < outs.Count && uv < 0; i++) if (IsTex(outs[i]) && Comps(outs[i]) >= 2) uv = i;

        int normal = -1, lm = -1, colour = -1;
        for (int i = 0; i < outs.Count; i++)
        {
            var o = outs[i];
            if (colour < 0 && !IsPositionOutput(o) && Comps(o) >= 3
                && o.Semantic.Equals("COLOR", StringComparison.OrdinalIgnoreCase)) colour = i;
            if (!IsTex(o) || i <= uv) continue;
            if (Comps(o) >= 3 && normal < 0) normal = i;
            else if (Comps(o) >= 2 && lm < 0 && normal < 0) lm = i;
        }
        static string? Field(int i) => i < 0 ? null : "f" + i;
        return new DebugInterpolators(Field(uv), Field(normal), Field(colour), Field(lm));
    }

    /// <summary>
    /// The generated shader. Every branch mirrors the OpenGL fragment shader's <c>uMode</c> arm exactly,
    /// down to the stand-in colours - white for a missing mask, black for a missing emissive, magenta for
    /// missing vertex colour - so the two viewports answer the same question the same way.
    /// </summary>
    public static string BuildDebugShaderSource(DxbcShader vsRefl)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Texture2D gDiffuse : register(t0);");
        sb.AppendLine("Texture2D gMask : register(t1);");
        sb.AppendLine("Texture2D gEmissive : register(t2);");
        sb.AppendLine("Texture2D gMatCap : register(t3);");
        sb.AppendLine("Texture2D gLightmap : register(t4);");
        sb.AppendLine("SamplerState gSamp : register(s0);");
        // gFlags: x = mode, y = which slots are bound (bitmask), z = two-sided, w = mirrored
        sb.AppendLine("cbuffer DebugCB : register(b0) { float4 gFlags; float4 gSunDir; };");
        sb.AppendLine("struct PSIn {");

        // M671: which interpolator is which is decided by PickDebugInterpolators, shared with the
        // comparison shader so the two generated shaders cannot read one signature two ways.
        var pick = PickDebugInterpolators(vsRefl);
        string? uvField = pick.Uv, normalField = pick.Normal, colourField = pick.Colour, lmUvField = pick.LightmapUv;
        int n = 0;
        foreach (var o in vsRefl.Outputs)
        {
            int comps = Math.Max(1, o.ComponentCount);
            string type = comps == 1 ? "float" : "float" + comps;
            string field = "f" + n++;
            sb.AppendLine("    " + type + " " + field + " : " + (IsPositionOutput(o) ? "SV_Position" : o.FullSemantic) + ";");
        }
        sb.AppendLine("    bool front : SV_IsFrontFace;");
        sb.AppendLine("};");

        sb.AppendLine("float4 main(PSIn i) : SV_Target {");
        sb.AppendLine("    int mode = (int)gFlags.x;");
        sb.AppendLine("    int have = (int)gFlags.y;");
        sb.AppendLine(uvField is null ? "    float2 uv = float2(0.5, 0.5);" : $"    float2 uv = i.{uvField}.xy;");
        sb.AppendLine(lmUvField is null ? "    float2 lmUv = uv;" : $"    float2 lmUv = i.{lmUvField}.xy;");
        sb.AppendLine(normalField is null
            ? "    float3 nrm = float3(0.0, 1.0, 0.0);"
            : $"    float3 nrm = normalize(i.{normalField}.xyz);");
        sb.AppendLine("    float4 d = gDiffuse.Sample(gSamp, uv);");

        sb.AppendLine("    if (mode == 2) return float4(d.rgb, 1.0);");
        sb.AppendLine("    if (mode == 3) return float4(d.aaa, 1.0);");
        sb.AppendLine("    if (mode == 4) return float4(nrm * 0.5 + 0.5, 1.0);");
        sb.AppendLine("    if (mode == 5) return float4((have & 2) ? gMask.Sample(gSamp, uv).rgb : float3(1,1,1), 1.0);");
        sb.AppendLine("    if (mode == 6) return float4((have & 4) ? gEmissive.Sample(gSamp, uv).rgb : float3(0,0,0), 1.0);");
        sb.AppendLine("    if (mode == 7) return float4((have & 8) ? gMatCap.Sample(gSamp, nrm.xy * 0.5 + 0.5).rgb : float3(0.2,0.2,0.2), 1.0);");
        sb.AppendLine("    if (mode == 8) {");
        sb.AppendLine("        float2 t = floor(uv * 8.0);");
        sb.AppendLine("        float c = fmod(t.x + t.y, 2.0);");
        sb.AppendLine("        return float4(lerp(float3(0.14,0.15,0.19), float3(0.85,0.86,0.92), c), 1.0);");
        sb.AppendLine("    }");
        sb.AppendLine("    if (mode == 9) {");
        sb.AppendLine("        float3 h = normalize(normalize(-gSunDir.xyz) + float3(0,0,1));");
        sb.AppendLine("        float sp = pow(saturate(dot(nrm, h)), 32.0);");
        sb.AppendLine("        return float4(sp, sp, sp, 1.0);");
        sb.AppendLine("    }");
        sb.AppendLine(colourField is null
            ? "    if (mode == 10) return float4(0.8, 0.0, 0.8, 1.0);"
            : $"    if (mode == 10) return float4(i.{colourField}.rgb, 1.0);");
        sb.AppendLine("    if (mode == 11) return float4((have & 16) ? gLightmap.Sample(gSamp, lmUv).rgb : float3(0.03,0.03,0.08), 1.0);");
        sb.AppendLine("    if (mode == 12) return i.front ? float4(0.15,0.80,0.30,1.0) : float4(0.90,0.20,0.20,1.0);");
        sb.AppendLine("    if (mode == 13) return gFlags.z > 0.5 ? float4(0.25,0.65,1.0,1.0) : float4(0.13,0.14,0.17,1.0);");
        sb.AppendLine("    if (mode == 14) return gFlags.w > 0.5 ? float4(1.0,0.55,0.12,1.0) : float4(0.13,0.14,0.17,1.0);");
        sb.AppendLine("    return float4(d.rgb, 1.0);");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private unsafe ComPtr<ID3D11PixelShader> CompileDebugPixelShader(string hlsl, out string? error)
    {
        error = null;
        var src = Encoding.ASCII.GetBytes(hlsl);
        var entry = Encoding.ASCII.GetBytes("main\0");
        var target = Encoding.ASCII.GetBytes("ps_5_0\0");
        ID3D10Blob* code = null, errs = null;
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* sp = src)
            fixed (byte* ep = entry)
            fixed (byte* tp = target)
            {
                int hr = compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                    ep, tp, 0u, 0u, &code, &errs);
                if (hr < 0 || code is null)
                {
                    error = errs is not null
                        ? Marshal.PtrToStringAnsi((IntPtr)errs->GetBufferPointer())
                        : $"hr=0x{hr:x8}";
                    return default;
                }
            }
            ComPtr<ID3D11PixelShader> ps = default;
            _device.CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref ps);
            return ps;
        }
        catch (Exception ex) { error = ex.Message; return default; }
        finally
        {
            if (code is not null) code->Release();
            if (errs is not null) errs->Release();
        }
    }

    /// <summary>
    /// Bind the debug pass for one material: the generated shader, its five slots at fixed registers, and
    /// the constants. Returns false when there is no shader for this signature, and the caller then draws
    /// the material the normal way rather than skipping it.
    /// </summary>
    private unsafe bool BindDebugPass(PreviewMaterial mat, PreviewSettings s, Matrix4x4 world)
    {
        var ps = DebugPixelShaderFor(mat.VsRefl);
        if (ps.Handle is null) { DebugFallbacks++; return false; }

        _ctx.PSSetShader(ps, null, 0);

        int have = 0;
        var srvs = stackalloc ID3D11ShaderResourceView*[5];
        for (int i = 0; i < 5; i++)
        {
            var slot = (DebugSlot)(i + 1);
            if (mat.DebugTextures.TryGetValue(slot, out var bound) && bound.Handle is not null)
            { srvs[i] = bound.Handle; have |= 1 << i; }
            else srvs[i] = _white.Handle;
        }
        _ctx.PSSetShaderResources(0, 5, srvs);
        var samp = MaterialSampler(mat.SamplerAddress);
        _ctx.PSSetSamplers(0, 1, ref samp);

        // M661: per MATERIAL, from the group array the host publishes - the same per-mesh fact the OpenGL
        // viewport reads, at the same granularity, because a D3D11 material is built from one mapgeo group
        // and a group belongs to one mesh. Deriving it from `world` here would have answered a different
        // question: the merged vertex buffer has every mesh transform baked in, so the world is one
        // matrix for the whole map and the view would have been uniform - true, and useless.
        float mirrored = mat.SourceMirrored ? 1f
            : (!Matrix4x4.Identity.Equals(world) && world.GetDeterminant() < 0f ? 1f : 0f);
        float twoSided = mat.CullBackFaces ? 0f : 1f;

        var sd = Vector3.Normalize(s.SunDirection);
        var values = new[]
        {
            (float)s.DebugMode, have, twoSided, mirrored,
            sd.X, sd.Y, sd.Z, 0f,
        };
        var bytes = new byte[32];
        for (int i = 0; i < values.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), values[i]);
        EnsureDebugCb();
        Upload(_debugCb, bytes, bytes.Length);
        _ctx.PSSetConstantBuffers(0, 1, ref _debugCb);

        DebugDraws++;
        return true;
    }

    private void EnsureDebugCb()
    {
        if (_debugCb.Handle is not null) return;
        var desc = new BufferDesc
        {
            ByteWidth = 32, Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> cb = default;
        if (_device.CreateBuffer(in desc, null, ref cb) >= 0) _debugCb = cb;
    }

    private void DisposeDebugViews()
    {
        foreach (var ps in _debugPs.Values) ps.Dispose();
        _debugPs.Clear();
        _debugPsFailed.Clear();
        _debugCb.Dispose();
        _debugCb = default;
    }
}
