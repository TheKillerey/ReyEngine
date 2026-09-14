using System.Numerics;
using System.Runtime.CompilerServices;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.Meshes;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.DXGI;

namespace ReyEngine.Rendering.D3D11;

/// <summary>
/// M727: one submesh of a legacy (NVR) preview backdrop, with the texture layers the GL backdrop binds for
/// it - in GL's own slot meaning, not the NVR file's channel numbering.
/// </summary>
/// <param name="Base">GL slot 0: the diffuse, or the composite atlas itself on a height-blend ground with no
/// height-scale map.</param>
/// <param name="Mask">GL slot 1: the height-scale map on composite ground, else the four-blend BLEND_MAP -
/// exactly GL's <c>Merge(BackgroundMaskTextures, BackgroundBlendTextures)</c>.</param>
/// <param name="Color1">GL slot 2/3/4: COLOR_MAP_1..3, the layers blended over <paramref name="Base"/>.</param>
/// <param name="Lightmap">GL slot 6: the baked composite ground atlas.</param>
public sealed record BackdropSubmesh(
    int StartIndex, int IndexCount,
    TextureImage? Base, TextureImage? Mask,
    TextureImage? Color1, TextureImage? Color2, TextureImage? Color3,
    TextureImage? Lightmap,
    bool CompositeGround, int AlphaMode, float AlphaCutoff, bool ClampUv);

/// <summary>M727: the backdrop geometry and its per-submesh layers. Uploaded once per map.</summary>
public sealed record BackdropScene(MeshAsset Mesh, IReadOnlyList<BackdropSubmesh> Submeshes);

/// <summary>
/// M727: the per-frame half - placement, and the backdrop's lighting already resolved by the host. M729: by the
/// same function the GL viewport calls (BackdropLighting.Resolve, in the app), so there is no recipe to mirror.
/// </summary>
/// <param name="DirectionToSun">M729: unit vector from the surface TOWARD the sun - the argument GL's
/// SetSunLighting takes. Never zero: GL treats a zero direction as "use my default sun", not as "no sun", and
/// M727 reading it as "no directional term" is exactly how D3D11 came to draw Dominion flat.</param>
/// <param name="CompositeModel">The M142 model: vertex colour used AS a lightmap (x2), for maps that ship a
/// composite ground atlas. Mutually exclusive with <paramref name="VertexBakedScale"/>, as in GL.</param>
/// <param name="VertexBakedScale">The M89 model: <c>col += base * vColor * scale</c>. 0 disables it.</param>
/// <param name="Lights">Light.dat point lights in BACKDROP-LOCAL space; the pass moves them by
/// <paramref name="World"/>, the same transform GL applies before uploading them.</param>
public sealed record BackdropFrame(
    bool Visible, Matrix4x4 World,
    Vector3 DirectionToSun, Vector3 SunColor, Vector3 SkyColor,
    bool CompositeModel, float VertexBakedScale,
    IReadOnlyList<PointLight>? Lights, bool LightsEnabled, float LightIntensity);

// M727: the legacy NVR preview backdrop (Dominion / Twisted Treeline), drawn by D3D11 the way the GL viewport
// draws it.
//
// WHY THIS EXISTS. The character window's D3D11 host had no backdrop channel, so M725 drew the map as a
// diffuse-only PROP - no four-blend ground, no height blend, no composite atlas, no vertex light, no Light.dat -
// and a property-ordering bug then made even that draw white. This is a real pass instead.
//
// WHAT IT IS MODELLED ON. Two references, and they are not the same thing:
//
//   - The MATERIAL math is the legacy client's own, read out of its shipped HLSL source (DATA/Shaders/HLSL/
//     Environment/LIT_PS, CREATE_GROUND_MOSAIC_FOUR_BLEND_PS and HeightBlending/HeightBlending.hls - the
//     .ps_2_0 files there are plain source text despite the extension). The four-blend lerp chain on the
//     second UV and the height blend (weights = saturate(h - maxH + band) * saturate(h * 32), normalised) are
//     term-for-term those files, and they are also exactly what ViewportMeshRenderer already does.
//
//   - The LIGHTING is ViewportControl's backdrop recipe, deliberately, because the brief was for D3D11 to
//     match the GL viewport. That recipe is NOT the legacy engine's: LIT_PS lights through a Mod4X colour map
//     and dual vertex colours, measured at roughly twice GL's brightness on Map10's vertex-lit statics. That
//     divergence is real and is recorded against both renderers rather than fixed silently in one of them.
//
// Built on the sky/overlay pattern: lazy compile remembered on failure, and its own shaders, layout, cbuffers,
// samplers and states - it shares nothing with the scene pass, which runs Riot's compiled shaders.
public sealed unsafe partial class ShaderPreviewRenderer
{
    private ComPtr<ID3D11VertexShader> _bdVs;
    private ComPtr<ID3D11PixelShader> _bdPs;
    private ComPtr<ID3D11InputLayout> _bdLayout;
    private ComPtr<ID3D11Buffer> _bdFrameCb, _bdDrawCb, _bdVb, _bdIb;
    private ComPtr<ID3D11SamplerState> _bdWrap, _bdClamp;
    private ComPtr<ID3D11RasterizerState> _bdRaster;
    private ComPtr<ID3D11BlendState> _bdOpaqueBlend, _bdAlphaBlend;
    private ComPtr<ID3D11DepthStencilState> _bdDepthWrite, _bdDepthRead;
    private ComPtr<ID3D11ShaderResourceView> _bdWhite;
    private bool _bdTried;

    private readonly List<ComPtr<ID3D11ShaderResourceView>> _bdSrvs = new();
    private readonly List<BdDraw> _bdDraws = new();
    private bool _bdHasColors;
    private BackdropFrame? _bdFrame;
    private float[] _bdFrameData = Array.Empty<float>();
    private readonly float[] _bdDrawData = new float[8];

    /// <summary>One draw: an index range and its layers as indices into <see cref="_bdSrvs"/>, -1 for none.</summary>
    private readonly record struct BdDraw(int Start, int Count, int Base, int Mask, int C1, int C2, int C3, int Light,
        bool CompositeGround, int AlphaMode, float Cutoff, bool Clamp);

    /// <summary>Point lights per frame - GL's own bound, so the two renderers agree on which lights exist. M729: this
    /// was 256, on the belief that Light.dat tables run to ~100. Map10 ships 95, but Dominion ships 462, and the 206
    /// past the cap never lit anything under D3D11. GL uploads up to 1024 (SetPointLights) and loops to 1024 in its
    /// shader. One cbuffer still holds the lot: 48 + 1024 * 8 floats is 2060 float4 slots, under D3D11's 4096. A
    /// longer list is truncated and reported.</summary>
    public const int MaxBackdropLights = 1024;

    private const int BdStride = 14 * sizeof(float);   // pos3 nrm3 uv2 lm2 col4

    public bool HasBackdrop => _bdDraws.Count > 0 && _bdVb.Handle is not null;

    /// <summary>Draws the backdrop made last frame, for the host's status line.</summary>
    public int BackdropDraws { get; private set; }

    /// <summary>Lights beyond <see cref="MaxBackdropLights"/> that were not uploaded last frame.</summary>
    public int BackdropLightsDropped { get; private set; }

    // ASCII only: the compiler is handed these bytes raw (the M117b trap). No double quotes either - this is a
    // C# verbatim string and one would end it.
    //
    // Every texture is sampled unconditionally at the top of the pixel shader and the branches are pure math,
    // so no gradient operation sits inside flow control.
    private const string BackdropHlsl = @"
cbuffer BackdropFrame : register(b0)
{
    row_major float4x4 gWorld;
    row_major float4x4 gViewProj;
    float4 gSunDir;                     // xyz: unit vector TOWARD the sun (M729), never zero
    float4 gSunColor;
    float4 gSkyColor;
    float4 gModel;                      // x composite model, y vertex baked scale, z light count, w light intensity
    float4 gLightPosRadius[MAX_BACKDROP_LIGHTS];        // xyz world position, w radius
    float4 gLightColorStrength[MAX_BACKDROP_LIGHTS];    // rgb colour, a per-light strength
};

cbuffer BackdropDraw : register(b1)
{
    float4 gFlags;                      // x has mask/blend, y composite ground, z has lightmap, w has vertex colour
    float4 gAlpha;                      // x alpha mode, y cutoff, z clamp uv
};

Texture2D tBase  : register(t0);
Texture2D tMask  : register(t1);
Texture2D tC1    : register(t2);
Texture2D tC2    : register(t3);
Texture2D tC3    : register(t4);
Texture2D tLight : register(t5);
SamplerState sWrap  : register(s0);
SamplerState sClamp : register(s1);

struct VIn
{
    float3 pos : POSITION;
    float3 nrm : NORMAL;
    float2 uv  : TEXCOORD0;
    float2 lm  : TEXCOORD1;
    float4 col : COLOR0;
};

struct VOut
{
    float4 pos   : SV_Position;
    float3 world : TEXCOORD0;
    float3 nrm   : TEXCOORD1;
    float2 uv    : TEXCOORD2;
    float2 lm    : TEXCOORD3;
    float4 col   : COLOR0;
};

VOut vsmain(VIn i)
{
    VOut o;
    float4 w = mul(float4(i.pos, 1.0), gWorld);
    o.pos = mul(w, gViewProj);
    o.world = w.xyz;
    o.nrm = mul(float4(i.nrm, 0.0), gWorld).xyz;
    o.uv = i.uv;
    o.lm = i.lm;
    o.col = i.col;
    return o;
}

float4 psmain(VOut i) : SV_Target
{
    float4 texW = tBase.Sample(sWrap, i.uv);
    float4 texC = tBase.Sample(sClamp, i.uv);
    float4 tex  = gAlpha.z > 0.5 ? texC : texW;
    float4 mk   = tMask.Sample(sWrap, i.lm);
    float4 c1   = tC1.Sample(sWrap, i.uv);
    float4 c2   = tC2.Sample(sWrap, i.uv);
    float4 c3   = tC3.Sample(sWrap, i.uv);
    float4 lmT  = tLight.Sample(sWrap, i.lm);
    float4 bLm  = tBase.Sample(sWrap, i.lm);

    float3 base = tex.rgb;
    float alpha = tex.a;

    // CREATE_GROUND_MOSAIC_FOUR_BLEND: each colour layer lerps over the running colour by one mask channel.
    if (gFlags.x > 0.5)
    {
        float3 g = base;
        g = lerp(g, c1.rgb, mk.r);
        g = lerp(g, c2.rgb, mk.g);
        g = lerp(g, c3.rgb, mk.b);
        base = g;
        alpha = 1.0;
    }

    // HeightBlending.hls: layer height = tile alpha * painted scale; only layers near the tallest survive.
    if (gFlags.y > 0.5)
    {
        if (gFlags.x > 0.5)
        {
            float4 h = float4(tex.a, c1.a, c2.a, c3.a) * mk;
            float hmax = max(max(h.x, h.y), max(h.z, h.w));
            float4 wgt = saturate(h - hmax + 0.25) * saturate(h * 32.0);
            float wsum = max(wgt.x + wgt.y + wgt.z + wgt.w, 0.0001);
            base = (tex.rgb * wgt.x + c1.rgb * wgt.y + c2.rgb * wgt.z + c3.rgb * wgt.w) / wsum;
        }
        else
        {
            base = bLm.rgb;
        }
        alpha = 1.0;
    }

    float3 n = dot(i.nrm, i.nrm) > 1e-6 ? normalize(i.nrm) : float3(0.0, 1.0, 0.0);
    float sunLen = dot(gSunDir.xyz, gSunDir.xyz);
    // M729: gSunDir already points TOWARD the sun - GL's SetSunLighting argument, from the one shared resolver.
    float3 toSun = sunLen > 1e-8 ? gSunDir.xyz / sqrt(max(sunLen, 1e-8)) : float3(0.0, 1.0, 0.0);
    float d = max(dot(n, toSun), 0.0);

    float3 col = gFlags.y > 0.5 ? base : base * (gSkyColor.rgb + gSunColor.rgb * d);
    if (gFlags.y > 0.5 && gFlags.z > 0.5) col = base * lmT.rgb * 2.0;
    if (gModel.y > 0.0 && gFlags.w > 0.5) col += base * i.col.rgb * gModel.y;
    if (gModel.x > 0.5 && gFlags.y < 0.5 && gFlags.w > 0.5)
    {
        float vlum = dot(i.col.rgb, float3(0.3333, 0.3333, 0.3333));
        col = lerp(col, base * i.col.rgb * 2.0, smoothstep(0.0, 0.05, vlum));
    }

    int count = (int)gModel.z;
    if (count > 0)
    {
        float3 acc = float3(0.0, 0.0, 0.0);
        [loop] for (int k = 0; k < count; k++)
        {
            float3 toLight = gLightPosRadius[k].xyz - i.world;
            float radius = gLightPosRadius[k].w;
            float dist = length(toLight);
            if (dist < radius)
            {
                float atten = 1.0 - dist / radius;
                float ndl = max(dot(n, toLight / max(dist, 0.0001)), 0.0);
                acc += gLightColorStrength[k].rgb * gLightColorStrength[k].a * atten * ndl;
            }
        }
        col += base * acc * gModel.w;
    }

    float mode = gAlpha.x;
    bool cutout = (mode > 0.5 && mode < 1.5) || mode > 2.5;
    if (cutout && alpha < gAlpha.y) discard;
    float outA = mode > 1.5 ? saturate(alpha) : 1.0;
    // LIT_PS: finalColor = saturate(finalColor) - what an 8-bit target does to GL's output as well.
    return float4(saturate(col), outA);
}";

    private bool EnsureBackdrop()
    {
        if (_bdTried) return _bdVs.Handle is not null;
        _bdTried = true;

        // M729: the light arrays are sized from MaxBackdropLights, never from a second literal that could drift from it.
        var src = System.Text.Encoding.ASCII.GetBytes(
            BackdropHlsl.Replace("MAX_BACKDROP_LIGHTS", MaxBackdropLights.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        ID3D10Blob* vsCode = null, psCode = null, errs = null;
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* sp = src)
            {
                if (!CompileBackdrop(compiler, sp, src.Length, "vsmain", "vs_5_0", &vsCode, &errs)) return false;
                if (!CompileBackdrop(compiler, sp, src.Length, "psmain", "ps_5_0", &psCode, &errs)) return false;
            }
        }
        catch (Exception ex) { Log("backdrop: the HLSL compiler is unavailable: " + ex.Message); return false; }

        ComPtr<ID3D11VertexShader> vs = default;
        if (_device.CreateVertexShader(vsCode->GetBufferPointer(), vsCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vs) < 0) { Log("backdrop CreateVertexShader failed"); return false; }
        ComPtr<ID3D11PixelShader> ps = default;
        if (_device.CreatePixelShader(psCode->GetBufferPointer(), psCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref ps) < 0) { Log("backdrop CreatePixelShader failed"); return false; }

        var semPos = System.Text.Encoding.ASCII.GetBytes("POSITION\0");
        var semNrm = System.Text.Encoding.ASCII.GetBytes("NORMAL\0");
        var semUv = System.Text.Encoding.ASCII.GetBytes("TEXCOORD\0");
        var semCol = System.Text.Encoding.ASCII.GetBytes("COLOR\0");
        ComPtr<ID3D11InputLayout> layout = default;
        fixed (byte* p0 = semPos)
        fixed (byte* p1 = semNrm)
        fixed (byte* p2 = semUv)
        fixed (byte* p3 = semCol)
        {
            var els = stackalloc InputElementDesc[5];
            els[0] = Element(p0, 0, Format.FormatR32G32B32Float, 0);
            els[1] = Element(p1, 0, Format.FormatR32G32B32Float, 12);
            els[2] = Element(p2, 0, Format.FormatR32G32Float, 24);
            els[3] = Element(p2, 1, Format.FormatR32G32Float, 32);
            els[4] = Element(p3, 0, Format.FormatR32G32B32A32Float, 40);
            if (_device.CreateInputLayout(els, 5, vsCode->GetBufferPointer(), vsCode->GetBufferSize(), ref layout) < 0)
            { Log("backdrop CreateInputLayout failed"); return false; }
        }

        var frameDesc = new BufferDesc
        {
            ByteWidth = (uint)((48 + MaxBackdropLights * 8) * sizeof(float)),   // 32,960 at 1024 lights: a multiple of 16
            Usage = Usage.Dynamic, BindFlags = (uint)BindFlag.ConstantBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> frameCb = default;
        if (_device.CreateBuffer(in frameDesc, null, ref frameCb) < 0) { Log("backdrop frame cbuffer failed"); return false; }
        var drawDesc = new BufferDesc
        {
            ByteWidth = 32,
            Usage = Usage.Dynamic, BindFlags = (uint)BindFlag.ConstantBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> drawCb = default;
        if (_device.CreateBuffer(in drawDesc, null, ref drawCb) < 0) { Log("backdrop draw cbuffer failed"); return false; }

        // Two samplers because GL decides clamp PER SUBMESH: legacy decals project once onto an oversized quad,
        // and wrapping their out-of-range UVs tiles them into hard seams (M142.6).
        var wrapDesc = new SamplerDesc
        {
            Filter = Filter.MinMagMipLinear, MaxLOD = float.MaxValue,
            AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
        };
        ComPtr<ID3D11SamplerState> wrap = default;
        _device.CreateSamplerState(in wrapDesc, ref wrap);
        var clampDesc = wrapDesc with
        {
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
        };
        ComPtr<ID3D11SamplerState> clamp = default;
        _device.CreateSamplerState(in clampDesc, ref clamp);

        // Never culled, matching GL's bg.Render(..., cullBackfaces: false): NVR surfaces are routinely one-sided
        // quads seen from both sides, and M356 is the standing reason not to guess a winding on a mirrored view.
        var rs = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 };
        ComPtr<ID3D11RasterizerState> raster = default;
        _device.CreateRasterizerState(in rs, ref raster);

        var opaque = new BlendDesc();
        opaque.RenderTarget[0] = new RenderTargetBlendDesc { BlendEnable = 0, RenderTargetWriteMask = (byte)ColorWriteEnable.All };
        ComPtr<ID3D11BlendState> opaqueBlend = default;
        _device.CreateBlendState(in opaque, ref opaqueBlend);
        // GL's AlphaMode 2 decals: ordinary straight alpha (MapPreviewLoader writes factors 6/7). Destination
        // alpha is kept, so the readback's alpha stays what the opaque geometry wrote.
        var alphaDesc = new BlendDesc();
        alphaDesc.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = Blend.SrcAlpha, DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.Zero, DestBlendAlpha = Blend.One, BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ComPtr<ID3D11BlendState> alphaBlend = default;
        _device.CreateBlendState(in alphaDesc, ref alphaBlend);

        var writeDesc = new DepthStencilDesc { DepthEnable = 1, DepthWriteMask = DepthWriteMask.All, DepthFunc = ComparisonFunc.Less };
        ComPtr<ID3D11DepthStencilState> depthWrite = default;
        _device.CreateDepthStencilState(in writeDesc, ref depthWrite);
        var readDesc = new DepthStencilDesc { DepthEnable = 1, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.LessEqual };
        ComPtr<ID3D11DepthStencilState> depthRead = default;
        _device.CreateDepthStencilState(in readDesc, ref depthRead);

        // White for any layer a submesh does not have - the same stand-in the GL path draws, so a missing texture
        // reads the same in both viewports rather than white in one and black in the other.
        var white = MakeTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
        if (white is null) { Log("backdrop white stand-in failed"); return false; }

        _bdVs = vs; _bdPs = ps; _bdLayout = layout;
        _bdFrameCb = frameCb; _bdDrawCb = drawCb;
        _bdWrap = wrap; _bdClamp = clamp; _bdRaster = raster;
        _bdOpaqueBlend = opaqueBlend; _bdAlphaBlend = alphaBlend;
        _bdDepthWrite = depthWrite; _bdDepthRead = depthRead;
        _bdWhite = white.Value;
        Log("backdrop pipeline built");
        return true;

        static InputElementDesc Element(byte* semantic, uint index, Format format, uint offset) => new()
        {
            SemanticName = semantic, SemanticIndex = index, Format = format,
            InputSlot = 0, AlignedByteOffset = offset,
            InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
        };
    }

    private bool CompileBackdrop(D3DCompiler compiler, byte* src, int len, string entry, string target,
        ID3D10Blob** code, ID3D10Blob** errs)
    {
        var e = System.Text.Encoding.ASCII.GetBytes(entry + "\0");
        var t = System.Text.Encoding.ASCII.GetBytes(target + "\0");
        fixed (byte* ep = e)
        fixed (byte* tp = t)
        {
            if (compiler.Compile(src, (nuint)len, (byte*)null, null, (ID3DInclude*)null,
                    ep, tp, 0u, 0u, code, errs) >= 0 && *code is not null)
                return true;
        }
        // Say WHY. The sky's version logs only that it failed, which leaves a shader edit undiagnosable.
        string detail = errs is not null && *errs is not null
            ? System.Text.Encoding.ASCII.GetString((byte*)(*errs)->GetBufferPointer(), (int)(*errs)->GetBufferSize()).Trim('\0', ' ', '\r', '\n')
            : "no compiler message";
        Log($"backdrop {entry} failed to compile: {detail}");
        return false;
    }

    /// <summary>
    /// Upload a backdrop, replacing any previous one. Null clears it. Returns false only when the pipeline
    /// cannot be built - the host's cue to fall back to its diffuse-only prop rather than show nothing.
    /// </summary>
    public bool SetBackdrop(BackdropScene? scene)
    {
        ReleaseBackdropScene();
        if (scene is null) return true;
        if (!EnsureBackdrop()) return false;

        var m = scene.Mesh;
        int vc = m.VertexCount;
        if (vc <= 0 || m.Indices.Length == 0) { Log("backdrop rejected: no geometry"); return true; }

        var pos = m.Positions; var nrm = m.Normals; var uv = m.Uvs;
        var lm = m.LightmapUvs; var col = m.Colors;
        _bdHasColors = col is not null;
        var inter = new float[vc * 14];
        for (int i = 0; i < vc; i++)
        {
            int o = i * 14;
            inter[o + 0] = pos[i * 3]; inter[o + 1] = pos[i * 3 + 1]; inter[o + 2] = pos[i * 3 + 2];
            if (i * 3 + 2 < nrm.Length) { inter[o + 3] = nrm[i * 3]; inter[o + 4] = nrm[i * 3 + 1]; inter[o + 5] = nrm[i * 3 + 2]; }
            else inter[o + 4] = 1f;
            if (i * 2 + 1 < uv.Length) { inter[o + 6] = uv[i * 2]; inter[o + 7] = uv[i * 2 + 1]; }
            if (lm is not null && i * 2 + 1 < lm.Length) { inter[o + 8] = lm[i * 2]; inter[o + 9] = lm[i * 2 + 1]; }
            if (col is not null && i * 4 + 3 < col.Length)
            {
                inter[o + 10] = col[i * 4]; inter[o + 11] = col[i * 4 + 1]; inter[o + 12] = col[i * 4 + 2]; inter[o + 13] = col[i * 4 + 3];
            }
            else inter[o + 13] = 1f;
        }

        var vbDesc = new BufferDesc { ByteWidth = (uint)(inter.Length * sizeof(float)), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.VertexBuffer };
        ComPtr<ID3D11Buffer> vb = default;
        fixed (float* p = inter)
        {
            var sub = new SubresourceData { PSysMem = p };
            if (_device.CreateBuffer(in vbDesc, in sub, ref vb) < 0) { Log("backdrop vertex buffer failed"); return false; }
        }
        var ibDesc = new BufferDesc { ByteWidth = (uint)(m.Indices.Length * sizeof(uint)), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.IndexBuffer };
        ComPtr<ID3D11Buffer> ib = default;
        fixed (uint* p = m.Indices)
        {
            var sub = new SubresourceData { PSysMem = p };
            if (_device.CreateBuffer(in ibDesc, in sub, ref ib) < 0) { vb.Dispose(); Log("backdrop index buffer failed"); return false; }
        }
        _bdVb = vb; _bdIb = ib;

        // One SRV per distinct image. The loader already shares TextureImage instances between submeshes that
        // name the same file, so keying by reference is exactly the dedupe that data affords.
        var index = new Dictionary<TextureImage, int>(ReferenceEqualityComparer.Instance);
        int Srv(TextureImage? t)
        {
            if (t is null) return -1;
            if (index.TryGetValue(t, out int at)) return at;
            var srv = MakeTexture(t.Rgba, t.Width, t.Height);
            if (srv is null) return index[t] = -1;
            _bdSrvs.Add(srv.Value);
            return index[t] = _bdSrvs.Count - 1;
        }

        foreach (var s in scene.Submeshes)
        {
            if (s.IndexCount <= 0) continue;
            _bdDraws.Add(new BdDraw(s.StartIndex, s.IndexCount,
                Srv(s.Base), Srv(s.Mask), Srv(s.Color1), Srv(s.Color2), Srv(s.Color3), Srv(s.Lightmap),
                s.CompositeGround, s.AlphaMode, s.AlphaCutoff, s.ClampUv));
        }
        // Opaque, then cutout, then blended - GL's own pass order, so decals settle over the ground they sit on.
        // Stable within each group: a List sort is not, so order by a key that carries the original position.
        var ordered = _bdDraws.Select((d, i) => (d, i))
            .OrderBy(x => x.d.AlphaMode is 2 or 3 ? 2 : x.d.AlphaMode == 1 ? 1 : 0).ThenBy(x => x.i)
            .Select(x => x.d).ToList();
        _bdDraws.Clear();
        _bdDraws.AddRange(ordered);

        Log($"backdrop uploaded: {vc:n0} verts, {_bdDraws.Count} draw(s), {_bdSrvs.Count} texture(s)"
            + (_bdHasColors ? "" : ", no vertex colour"));
        return true;
    }

    /// <summary>The per-frame placement and lighting. Null, or a frame that is not visible, draws nothing.</summary>
    public void SetBackdropFrame(BackdropFrame? frame) => _bdFrame = frame;

    /// <summary>Draw the backdrop. Called right after the sky and before the scene: it tests AND writes depth, so
    /// the character composites against it correctly. Sets its own states, which the scene pass then resets.</summary>
    private int DrawBackdrop(Matrix4x4 view, Matrix4x4 proj)
    {
        BackdropDraws = 0;
        if (_bdFrame is not { Visible: true } f || !HasBackdrop || !EnsureBackdrop()) return 0;

        FillBackdropFrame(f, Matrix4x4.Multiply(view, proj));

        uint stride = BdStride, offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _bdVb, in stride, in offset);
        _ctx.IASetIndexBuffer(_bdIb, Format.FormatR32Uint, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        _ctx.IASetInputLayout(_bdLayout);
        _ctx.VSSetShader(_bdVs, null, 0);
        _ctx.PSSetShader(_bdPs, null, 0);
        _ctx.VSSetConstantBuffers(0, 1, ref _bdFrameCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _bdFrameCb);
        _ctx.PSSetConstantBuffers(1, 1, ref _bdDrawCb);
        var samps = stackalloc ID3D11SamplerState*[2];
        samps[0] = _bdWrap.Handle; samps[1] = _bdClamp.Handle;
        _ctx.PSSetSamplers(0, 2, samps);
        _ctx.RSSetState(_bdRaster);

        var factor = stackalloc float[4] { 0f, 0f, 0f, 0f };
        var srvs = stackalloc ID3D11ShaderResourceView*[6];
        int boundBlend = -1;
        foreach (var d in _bdDraws)
        {
            int blend = d.AlphaMode is 2 or 3 ? 1 : 0;
            if (blend != boundBlend)
            {
                _ctx.OMSetBlendState(blend == 1 ? _bdAlphaBlend : _bdOpaqueBlend, factor, 0xFFFFFFFF);
                _ctx.OMSetDepthStencilState(blend == 1 ? _bdDepthRead : _bdDepthWrite, 0);
                boundBlend = blend;
            }

            var dd = _bdDrawData;
            dd[0] = d.Mask >= 0 ? 1f : 0f;
            dd[1] = d.CompositeGround ? 1f : 0f;
            dd[2] = d.Light >= 0 ? 1f : 0f;
            dd[3] = _bdHasColors ? 1f : 0f;
            dd[4] = d.AlphaMode;
            dd[5] = d.Cutoff;
            dd[6] = d.Clamp ? 1f : 0f;
            dd[7] = 0f;
            WriteFloats(_bdDrawCb, dd);

            srvs[0] = Layer(d.Base); srvs[1] = Layer(d.Mask); srvs[2] = Layer(d.C1);
            srvs[3] = Layer(d.C2); srvs[4] = Layer(d.C3); srvs[5] = Layer(d.Light);
            _ctx.PSSetShaderResources(0, 6, srvs);
            _ctx.DrawIndexed((uint)d.Count, (uint)d.Start, 0);
            BackdropDraws++;
        }

        // Unbind: the scene pass binds per material without clearing slots it does not use.
        for (int i = 0; i < 6; i++) srvs[i] = null;
        _ctx.PSSetShaderResources(0, 6, srvs);
        return BackdropDraws;
    }

    private ID3D11ShaderResourceView* Layer(int i) => i >= 0 && i < _bdSrvs.Count ? _bdSrvs[i].Handle : _bdWhite.Handle;

    private void FillBackdropFrame(BackdropFrame f, Matrix4x4 viewProj)
    {
        int floats = 48 + MaxBackdropLights * 8;
        if (_bdFrameData.Length != floats) _bdFrameData = new float[floats];
        var d = _bdFrameData;
        Array.Clear(d);
        PutMatrix(d, 0, f.World);
        PutMatrix(d, 16, viewProj);
        d[32] = f.DirectionToSun.X; d[33] = f.DirectionToSun.Y; d[34] = f.DirectionToSun.Z;
        d[36] = f.SunColor.X; d[37] = f.SunColor.Y; d[38] = f.SunColor.Z;
        d[40] = f.SkyColor.X; d[41] = f.SkyColor.Y; d[42] = f.SkyColor.Z;

        // GL's clamps (SetLightIntensity 0..8, per-light strength 0..64), so a slider extreme cannot make the two
        // viewports disagree about the same lights.
        float intensity = Math.Clamp(f.LightIntensity, 0f, 8f);
        int n = 0;
        BackdropLightsDropped = 0;
        if (f.LightsEnabled && intensity > 0f && f.Lights is { Count: > 0 } lights)
        {
            n = Math.Min(lights.Count, MaxBackdropLights);
            BackdropLightsDropped = lights.Count - n;
            for (int i = 0; i < n; i++)
            {
                var l = lights[i];
                // Backdrop-local to world by the backdrop's own transform - exactly what ViewportControl does to
                // BackgroundLights before uploading them, so a moved map keeps its light pools on its torches.
                var p = Vector3.Transform(l.Position, f.World);
                int at = 48 + i * 4;
                d[at] = p.X; d[at + 1] = p.Y; d[at + 2] = p.Z; d[at + 3] = l.Radius;
                at = 48 + MaxBackdropLights * 4 + i * 4;
                d[at] = l.Color.X; d[at + 1] = l.Color.Y; d[at + 2] = l.Color.Z;
                d[at + 3] = Math.Clamp(l.Intensity, 0f, 64f);
            }
        }
        d[44] = f.CompositeModel ? 1f : 0f;
        d[45] = f.CompositeModel ? 0f : f.VertexBakedScale;   // the two models never run together, as in GL
        d[46] = n;
        d[47] = intensity;
        WriteFloats(_bdFrameCb, d);
    }

    private static void PutMatrix(float[] d, int at, Matrix4x4 m)
    {
        d[at + 0] = m.M11; d[at + 1] = m.M12; d[at + 2] = m.M13; d[at + 3] = m.M14;
        d[at + 4] = m.M21; d[at + 5] = m.M22; d[at + 6] = m.M23; d[at + 7] = m.M24;
        d[at + 8] = m.M31; d[at + 9] = m.M32; d[at + 10] = m.M33; d[at + 11] = m.M34;
        d[at + 12] = m.M41; d[at + 13] = m.M42; d[at + 14] = m.M43; d[at + 15] = m.M44;
    }

    private void WriteFloats(ComPtr<ID3D11Buffer> buffer, float[] data)
    {
        MappedSubresource mapped = default;
        if (_ctx.Map(buffer, 0, Map.WriteDiscard, 0, ref mapped) < 0) return;
        fixed (float* src = data)
            System.Buffer.MemoryCopy(src, mapped.PData, data.Length * sizeof(float), data.Length * sizeof(float));
        _ctx.Unmap(buffer, 0);
    }

    private void ReleaseBackdropScene()
    {
        for (int i = 0; i < _bdSrvs.Count; i++) { var s = _bdSrvs[i]; s.Dispose(); }
        _bdSrvs.Clear();
        _bdDraws.Clear();
        _bdVb.Dispose(); _bdVb = default;
        _bdIb.Dispose(); _bdIb = default;
    }

    private void DisposeBackdrop()
    {
        ReleaseBackdropScene();
        _bdVs.Dispose(); _bdPs.Dispose(); _bdLayout.Dispose();
        _bdFrameCb.Dispose(); _bdDrawCb.Dispose();
        _bdWrap.Dispose(); _bdClamp.Dispose(); _bdRaster.Dispose();
        _bdOpaqueBlend.Dispose(); _bdAlphaBlend.Dispose();
        _bdDepthWrite.Dispose(); _bdDepthRead.Dispose();
        _bdWhite.Dispose();
    }
}
