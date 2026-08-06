using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.DXGI;

namespace ReyEngine.Rendering.D3D11;

// M364 (v0.4.0 item 7b): beam and trail ribbons. D3D11MapParticles skipped these at one line with an
// accurate reason - "ribbon geometry, not billboards" - because the map-particle path builds billboards
// into one shared dynamic buffer and a ribbon is a strip extruded from particle history.
//
// WHY A DEDICATED PIPELINE RATHER THAN THE M283 MESH CHANNEL. The mesh channel looked like the obvious
// vehicle (it already has a Dynamic vertex buffer and a per-frame position rewrite), but it is INSTANCED
// geometry drawn once per particle through Riot's quad shader, and it carries pos+uv only. A ribbon is a
// single world-space strip with per-vertex colour. Forcing it through that channel would have needed a
// synthetic identity instance, a UV update path the channel deliberately does not have, and a fixed vertex
// count that a ribbon does not have.
//
// GL settles the question: it does NOT use Riot's shader for ribbons either. It runs its own tiny
// _trailProgram and documents the gaps that come with it (no flipbook/texDiv, erosion, soft particles,
// palette, distortion or UV transform stack). This is that same program in HLSL, so the two renderers draw
// ribbons the same way by construction, including the same stated limitations.
//
// The buffer GROWS on demand exactly as GL's does, which is what makes a per-frame-varying vertex count a
// non-issue rather than a problem needing a degenerate-tail convention.
public sealed unsafe partial class ShaderPreviewRenderer
{
    /// <summary>pos3 + uv2 + rgba4, the same 9-float vertex <c>VfxParticleRenderer.BuildRibbon</c> writes.</summary>
    private const int RibbonStride = 9;

    private sealed class RibbonGeom
    {
        public ComPtr<ID3D11Buffer> Vb;
        public int CapacityFloats;
        public int VertexCount;
    }

    private readonly List<RibbonGeom?> _ribbons = new();
    private ComPtr<ID3D11VertexShader> _ribbonVs;
    private ComPtr<ID3D11PixelShader> _ribbonPs;
    private ComPtr<ID3D11InputLayout> _ribbonLayout;
    private ComPtr<ID3D11Buffer> _ribbonCb;
    private bool _ribbonTried;

    /// <summary>How many ribbons actually drew last frame.</summary>
    public int RibbonDraws { get; private set; }

    // ASCII only - handed to the compiler as raw ASCII bytes. A direct transliteration of TrailVert /
    // TrailFrag, including the alpha test: GL discards when t.a * colour.a < alphaRef, and a ribbon whose
    // emitter authored no alphaRef gets 0, which discards nothing.
    private const string RibbonHlsl = @"
cbuffer RibbonCB : register(b0)
{
    row_major float4x4 gViewProj;
    float4 gParams;      // .x = alpha reference
};

Texture2D    gTex  : register(t0);
SamplerState gSamp : register(s0);

struct VIn  { float3 pos : POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };
struct VOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 col : COLOR0; };

VOut vsmain(VIn i)
{
    VOut o;
    o.pos = mul(float4(i.pos, 1.0), gViewProj);
    o.uv = i.uv;
    o.col = i.col;
    return o;
}

float4 psmain(VOut i) : SV_Target
{
    float4 t = gTex.Sample(gSamp, i.uv);
    if (gParams.x > 0.0 && t.a * i.col.a < gParams.x) discard;
    return t * i.col;
}
";

    private bool EnsureRibbon()
    {
        if (_ribbonTried) return _ribbonVs.Handle is not null;
        _ribbonTried = true;

        var src = System.Text.Encoding.ASCII.GetBytes(RibbonHlsl);
        ID3D10Blob* vsCode = null, psCode = null, errs = null;
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* sp = src)
            {
                var vsEntry = System.Text.Encoding.ASCII.GetBytes("vsmain\0");
                var vsTarget = System.Text.Encoding.ASCII.GetBytes("vs_5_0\0");
                fixed (byte* ep = vsEntry) fixed (byte* tp = vsTarget)
                    if (compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                            ep, tp, 0u, 0u, &vsCode, &errs) < 0 || vsCode is null)
                    { Log("ribbon vs failed to compile - beams and trails will not draw"); return false; }

                var psEntry = System.Text.Encoding.ASCII.GetBytes("psmain\0");
                var psTarget = System.Text.Encoding.ASCII.GetBytes("ps_5_0\0");
                fixed (byte* ep = psEntry) fixed (byte* tp = psTarget)
                    if (compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                            ep, tp, 0u, 0u, &psCode, &errs) < 0 || psCode is null)
                    { Log("ribbon ps failed to compile - beams and trails will not draw"); return false; }
            }
        }
        catch (Exception ex) { Log("ribbon: the HLSL compiler is unavailable: " + ex.Message); return false; }

        ComPtr<ID3D11VertexShader> vs = default;
        if (_device.CreateVertexShader(vsCode->GetBufferPointer(), vsCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vs) < 0) { Log("ribbon CreateVertexShader failed"); return false; }
        _ribbonVs = vs;

        ComPtr<ID3D11PixelShader> ps = default;
        if (_device.CreatePixelShader(psCode->GetBufferPointer(), psCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref ps) < 0) { Log("ribbon CreatePixelShader failed"); return false; }
        _ribbonPs = ps;

        var pos = System.Text.Encoding.ASCII.GetBytes("POSITION\0");
        var uv = System.Text.Encoding.ASCII.GetBytes("TEXCOORD\0");
        var col = System.Text.Encoding.ASCII.GetBytes("COLOR\0");
        fixed (byte* pp = pos)
        fixed (byte* up = uv)
        fixed (byte* cp = col)
        {
            var els = stackalloc InputElementDesc[3];
            els[0] = new InputElementDesc
            {
                SemanticName = pp, SemanticIndex = 0, Format = Format.FormatR32G32B32Float,
                InputSlot = 0, AlignedByteOffset = 0,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            els[1] = new InputElementDesc
            {
                SemanticName = up, SemanticIndex = 0, Format = Format.FormatR32G32Float,
                InputSlot = 0, AlignedByteOffset = 12,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            els[2] = new InputElementDesc
            {
                SemanticName = cp, SemanticIndex = 0, Format = Format.FormatR32G32B32A32Float,
                InputSlot = 0, AlignedByteOffset = 20,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            ComPtr<ID3D11InputLayout> layout = default;
            if (_device.CreateInputLayout(els, 3, vsCode->GetBufferPointer(), vsCode->GetBufferSize(), ref layout) < 0)
            { Log("ribbon CreateInputLayout failed"); return false; }
            _ribbonLayout = layout;
        }

        var cbDesc = new BufferDesc
        {
            ByteWidth = 80,                      // float4x4 + float4
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> cb = default;
        if (_device.CreateBuffer(in cbDesc, null, ref cb) < 0) { Log("ribbon cbuffer failed"); return false; }
        _ribbonCb = cb;
        return true;
    }

    /// <summary>Reserve a ribbon slot. The vertex buffer is allocated lazily by the first
    /// <see cref="UpdateRibbon"/>, because a ribbon's size is not known until its first frame - trails grow
    /// with particle history and beams depend on their resolved endpoints.</summary>
    public int CreateRibbon()
    {
        if (!EnsureRibbon()) return -1;
        for (int i = 0; i < _ribbons.Count; i++)
            if (_ribbons[i] is null) { _ribbons[i] = new RibbonGeom(); return i; }
        _ribbons.Add(new RibbonGeom());
        return _ribbons.Count - 1;
    }

    /// <summary>Upload one frame of ribbon vertices. <paramref name="floatCount"/> is the write cursor
    /// <c>BuildRibbon</c> returned, NOT the buffer length.
    ///
    /// <para>The buffer grows on demand and never shrinks, which is exactly what the GL path does with its
    /// _trailVboCapacity. That is what makes a per-frame-varying vertex count a non-issue: the draw uses
    /// this frame's count, so stale vertices past it are never referenced and need no degenerate-tail
    /// convention.</para></summary>
    public bool UpdateRibbon(int id, float[] verts, int floatCount)
    {
        if (id < 0 || id >= _ribbons.Count || _ribbons[id] is not { } geom) return false;
        if (floatCount <= 0 || floatCount > verts.Length) { geom.VertexCount = 0; return false; }

        if (floatCount > geom.CapacityFloats || geom.Vb.Handle is null)
        {
            geom.Vb.Dispose();
            geom.Vb = default;
            int cap = Math.Max(floatCount, 4096);
            var desc = new BufferDesc
            {
                ByteWidth = (uint)(cap * sizeof(float)),
                Usage = Usage.Dynamic, BindFlags = (uint)BindFlag.VertexBuffer,
                CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> vb = default;
            if (_device.CreateBuffer(in desc, null, ref vb) < 0)
            { Log("ribbon vertex buffer allocation failed"); geom.VertexCount = 0; return false; }
            geom.Vb = vb;
            geom.CapacityFloats = cap;
        }

        var map = new MappedSubresource();
        if (_ctx.Map(geom.Vb, 0, Map.WriteDiscard, 0, ref map) < 0) { geom.VertexCount = 0; return false; }
        fixed (float* src = verts)
            System.Buffer.MemoryCopy(src, map.PData, (long)geom.CapacityFloats * sizeof(float),
                (long)floatCount * sizeof(float));
        _ctx.Unmap(geom.Vb, 0);
        geom.VertexCount = floatCount / RibbonStride;
        return true;
    }

    public void ReleaseRibbon(int id)
    {
        if (id < 0 || id >= _ribbons.Count || _ribbons[id] is not { } geom) return;
        geom.Vb.Dispose();
        _ribbons[id] = null;
    }

    public int RibbonCount => _ribbons.Count(r => r is not null);

    /// <summary>Draw one ribbon material. Returns false if it had nothing to draw this frame, which is the
    /// normal state for a trail whose particles have not moved far enough to have history yet.</summary>
    private bool DrawRibbon(PreviewMaterial mat, Matrix4x4 viewProj)
    {
        if (mat.RibbonId is not { } id || id < 0 || id >= _ribbons.Count) return false;
        if (_ribbons[id] is not { } geom || geom.VertexCount == 0 || geom.Vb.Handle is null) return false;
        if (!EnsureRibbon()) return false;

        // The sprite, under whichever name the emitter's permutation of quad_ps declared it. The material
        // was still built through the ordinary emitter pipeline - only the DRAW is ours - so its texture
        // lives under a reflected name rather than a fixed slot.
        var first = mat.PsRefl.Textures.FirstOrDefault(t => mat.Textures.ContainsKey(t.Name));
        var srv = first is not null ? mat.Textures[first.Name] : _white;

        float alphaRef = mat.Params.TryGetValue("AlphaTestReferenceValue", out var ar) && ar.Length > 0 ? ar[0] : 0f;
        SetRibbonCb(viewProj, alphaRef);

        _ctx.IASetInputLayout(_ribbonLayout);
        _ctx.VSSetShader(_ribbonVs, null, 0);
        _ctx.PSSetShader(_ribbonPs, null, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        _ctx.VSSetConstantBuffers(0, 1, ref _ribbonCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _ribbonCb);
        _ctx.PSSetShaderResources(0, 1, ref srv);
        var samp = _linearWrap;
        _ctx.PSSetSamplers(0, 1, ref samp);

        uint stride = RibbonStride * sizeof(float), offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref geom.Vb, in stride, in offset);

        // Two-sided, exactly as GL states it: billboards and ribbons are two-sided quads, so culling either
        // winding would drop half of them depending on which way the camera faces. Set explicitly because
        // the draw loop may have just bound the back-face-culling state for the previous material.
        _ctx.RSSetState(_raster);
        // Depth test on, writes off - the state every particle in this renderer already draws under, and
        // the state GL's ribbons inherit from its particle path.
        _ctx.OMSetDepthStencilState(_depthStateNoWrite, 0);
        var factor = stackalloc float[4] { 0f, 0f, 0f, 0f };
        _ctx.OMSetBlendState(mat.Additive ? _blendAdditive : _blend, factor, 0xFFFFFFFF);

        _ctx.Draw((uint)geom.VertexCount, 0);
        RibbonDraws++;
        return true;
    }

    private void SetRibbonCb(Matrix4x4 vp, float alphaRef)
    {
        var bytes = new byte[80];
        var m = new[]
        {
            vp.M11, vp.M12, vp.M13, vp.M14, vp.M21, vp.M22, vp.M23, vp.M24,
            vp.M31, vp.M32, vp.M33, vp.M34, vp.M41, vp.M42, vp.M43, vp.M44,
            alphaRef, 0f, 0f, 0f,
        };
        System.Buffer.BlockCopy(m, 0, bytes, 0, 80);
        Upload(_ribbonCb, bytes, 80);
    }

    private void ReleaseRibbons()
    {
        foreach (var r in _ribbons) r?.Vb.Dispose();
        _ribbons.Clear();
    }

    private void DisposeRibbon()
    {
        ReleaseRibbons();
        _ribbonVs.Dispose(); _ribbonPs.Dispose(); _ribbonLayout.Dispose(); _ribbonCb.Dispose();
    }
}
