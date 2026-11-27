using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using ReyEngine.Formats.Shaders;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace ReyEngine.Rendering.D3D11;

// M760: the SCREEN fog - Riot's own gamma/postfog.vs + gamma/postfog.ps (blob 0, the $Globals variant)
// run verbatim over the finished scene, exactly as bloom runs its blobs. No HLSL is authored here: what is
// synthesised CPU-side is the 144-byte $Globals (docs/research/frame-pipeline.md section 2.4):
//
//     float3   CameraPos             +0
//     float4x4 WorldViewProjInverse  +16     world = mul(float4(clipXY, depth, 1), M) / w
//     float4   DepthFogParams        +80     (maxIntensity, start, 1/(end - start), -)
//     float3   DepthFogColor         +96
//     float4   HeightFogParams       +112
//     float3   HeightFogColor        +128
//
// t0 sDepthTexture (our R32 depth copy), t1 SAMPLER_BACK_BUFFER_COPY (our scene copy), s0 and s15. It runs
// after the scene and the dynamic-light overlay and BEFORE bloom, the game's order (ibid., passes 4 and 5).
public sealed unsafe partial class ShaderPreviewRenderer
{
    private byte[]? _postFogVsCode, _postFogPsCode;
    private bool _postFogTried, _postFogOk;
    private PostFogLayout _postFogConstants = PostFogLayout.Legacy;
    private ComPtr<ID3D11VertexShader> _postFogVs;
    private ComPtr<ID3D11PixelShader> _postFogPs;
    private ComPtr<ID3D11InputLayout> _postFogLayout;
    private ComPtr<ID3D11Buffer> _postFogVb, _postFogCb;
    private ComPtr<ID3D11SamplerState> _postFogSampler;
    private ComPtr<ID3D11BlendState> _postFogBlend;
    private ComPtr<ID3D11DepthStencilState> _postFogDepthOff;
    private ComPtr<ID3D11RasterizerState> _postFogRaster;

    /// <summary>Full-screen fog passes the last frame ran (0 or 1).</summary>
    public int PostFogPasses { get; private set; }

    /// <summary>Set by the host from the shader cache; null leaves the screen fog off.</summary>
    public void SetPostFogShaders(byte[]? vs, byte[]? ps)
    {
        _postFogVsCode = vs; _postFogPsCode = ps;
        DisposePostFog();
        _postFogTried = false; _postFogOk = false;
    }

    public bool HasPostFogShaders => _postFogVsCode is not null && _postFogPsCode is not null;

    /// <summary>Where blob 0 keeps each value in its <c>$Globals</c>. Riot's 16.23 rebuild put two debug flags
    /// (DEBUG_RENDERTARGET_IS_SRGB_HANDLED, DEBUG_BACK_BUFFER_COPY_IS_SRGB_HANDLED) in front of CameraPos and
    /// moved every value after them 16 bytes down, so the offsets are READ from the blob (<see cref="From"/>)
    /// and only fall back to the original 144-byte layout for a blob that names none of them.</summary>
    public readonly record struct PostFogLayout(int Camera, int Inverse, int DepthParams, int DepthColor,
        int HeightParams, int HeightColor, int Size)
    {
        /// <summary>The pre-16.23 layout: CameraPos at 0, then the matrix, the two fogs.</summary>
        public static readonly PostFogLayout Legacy = new(0, 16, 80, 96, 112, 128, 144);

        public static PostFogLayout From(DxbcShader ps)
        {
            var globals = ps.ConstantBuffers.FirstOrDefault(c => c.Name == "$Globals");
            if (globals is null) return Legacy;
            int At(string name) => globals.Variables.FirstOrDefault(v => v.Name == name)?.Offset ?? -1;
            int camera = At("CameraPos"), inverse = At("WorldViewProjInverse"), dp = At("DepthFogParams"),
                dc = At("DepthFogColor"), hp = At("HeightFogParams"), hc = At("HeightFogColor");
            // all six or none: a half-found layout would write some values to the old place and some to the new
            if (camera < 0 || inverse < 0 || dp < 0 || dc < 0 || hp < 0 || hc < 0) return Legacy;
            return new PostFogLayout(camera, inverse, dp, dc, hp, hc, Math.Max(globals.AllocationSize, hc + 12));
        }
    }

    /// <summary>The constant buffer exactly as blob 0 reads it. Public and pure so its layout - the part that
    /// fails silently on the GPU - is unit-tested without a device.</summary>
    public static byte[] PostFogConstants(Vector3 camera, Matrix4x4 viewProj,
        (Vector4 DepthParams, Vector3 DepthColor, Vector4 HeightParams, Vector3 HeightColor) fog)
        => PostFogConstants(camera, viewProj, fog, PostFogLayout.Legacy);

    public static byte[] PostFogConstants(Vector3 camera, Matrix4x4 viewProj,
        (Vector4 DepthParams, Vector3 DepthColor, Vector4 HeightParams, Vector3 HeightColor) fog, PostFogLayout layout)
    {
        var b = new byte[layout.Size];
        void F(int at, float v) => BitConverter.TryWriteBytes(b.AsSpan(at, 4), v);
        F(layout.Camera, camera.X); F(layout.Camera + 4, camera.Y); F(layout.Camera + 8, camera.Z);
        // world.x = dot(clip, cb0[1]) ... : the registers must hold the COLUMNS of inverse(viewProj) under
        // System.Numerics' row-vector convention (clip = world * VP  =>  world = clip * inv(VP)).
        Matrix4x4.Invert(viewProj, out var inv);
        var t = Matrix4x4.Transpose(inv);
        float[] m =
        {
            t.M11, t.M12, t.M13, t.M14, t.M21, t.M22, t.M23, t.M24,
            t.M31, t.M32, t.M33, t.M34, t.M41, t.M42, t.M43, t.M44,
        };
        for (int i = 0; i < 16; i++) F(layout.Inverse + i * 4, m[i]);
        int d = layout.DepthParams, dc = layout.DepthColor, h = layout.HeightParams, hc = layout.HeightColor;
        F(d, fog.DepthParams.X); F(d + 4, fog.DepthParams.Y); F(d + 8, fog.DepthParams.Z); F(d + 12, fog.DepthParams.W);
        F(dc, fog.DepthColor.X); F(dc + 4, fog.DepthColor.Y); F(dc + 8, fog.DepthColor.Z);
        F(h, fog.HeightParams.X); F(h + 4, fog.HeightParams.Y); F(h + 8, fog.HeightParams.Z); F(h + 12, fog.HeightParams.W);
        F(hc, fog.HeightColor.X); F(hc + 4, fog.HeightColor.Y); F(hc + 8, fog.HeightColor.Z);
        return b;
    }

    private bool EnsurePostFogPipeline()
    {
        if (_postFogTried) return _postFogOk;
        _postFogTried = true;
        if (!HasPostFogShaders) { Log("screen fog: the host supplied no gamma/postfog blobs; it is off"); return false; }

        ComPtr<ID3D11VertexShader> vs = default;
        fixed (byte* p = _postFogVsCode)
            if (_device.CreateVertexShader(p, (nuint)_postFogVsCode!.Length, ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vs) < 0)
            { Log("screen fog: CreateVertexShader (gamma/postfog.vs) failed"); return false; }
        _postFogVs = vs;
        ComPtr<ID3D11PixelShader> ps = default;
        fixed (byte* p = _postFogPsCode)
            if (_device.CreatePixelShader(p, (nuint)_postFogPsCode!.Length, ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref ps) < 0)
            { Log("screen fog: CreatePixelShader (gamma/postfog.ps) failed"); return false; }
        _postFogPs = ps;
        // the constant layout is whatever THIS blob declares (16.23 moved it); an unparseable blob keeps the original
        try { _postFogConstants = PostFogLayout.From(DxbcReflection.Parse(_postFogPsCode!)); }
        catch { _postFogConstants = PostFogLayout.Legacy; }

        // postfog.vs declares one input, POSITION0 float2, and makes the clip position (v0 * 2 - 1) and the
        // UV (x, 1 - y) itself - the same contract as post_effect.vs, so the same unit-square quad.
        var semantic = System.Text.Encoding.ASCII.GetBytes("POSITION\0");
        fixed (byte* sem = semantic)
        fixed (byte* code = _postFogVsCode)
        {
            var el = new InputElementDesc
            {
                SemanticName = sem, SemanticIndex = 0, Format = Format.FormatR32G32Float, InputSlot = 0,
                AlignedByteOffset = 0, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            ComPtr<ID3D11InputLayout> layout = default;
            if (_device.CreateInputLayout(&el, 1, code, (nuint)_postFogVsCode!.Length, ref layout) < 0)
            { Log("screen fog: CreateInputLayout failed"); return false; }
            _postFogLayout = layout;
        }

        float[] quad = { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f, 1f };
        var vbDesc = new BufferDesc { ByteWidth = (uint)(quad.Length * sizeof(float)), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.VertexBuffer };
        ComPtr<ID3D11Buffer> vb = default;
        fixed (float* p = quad)
        {
            var sub = new SubresourceData { PSysMem = p };
            if (_device.CreateBuffer(in vbDesc, in sub, ref vb) < 0) { Log("screen fog: quad failed"); return false; }
        }
        _postFogVb = vb;

        var cbDesc = new BufferDesc { ByteWidth = (uint)_postFogConstants.Size, Usage = Usage.Dynamic, BindFlags = (uint)BindFlag.ConstantBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write };
        ComPtr<ID3D11Buffer> cb = default;
        if (_device.CreateBuffer(in cbDesc, null, ref cb) < 0) { Log("screen fog: cbuffer failed"); return false; }
        _postFogCb = cb;

        // POINT, clamp: the depth copy and the scene copy are both exactly frame-sized, so every pixel reads
        // its own texel. A linear depth fetch would blend across silhouettes and fog an edge by the average
        // of the near and the far surface.
        var sd = new SamplerDesc
        {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxLOD = float.MaxValue, ComparisonFunc = ComparisonFunc.Never,
        };
        ComPtr<ID3D11SamplerState> samp = default;
        if (_device.CreateSamplerState(in sd, ref samp) < 0) { Log("screen fog: sampler failed"); return false; }
        _postFogSampler = samp;

        var opaque = new BlendDesc();
        opaque.RenderTarget[0] = new RenderTargetBlendDesc { BlendEnable = 0, RenderTargetWriteMask = (byte)ColorWriteEnable.All };
        ComPtr<ID3D11BlendState> bo = default;
        if (_device.CreateBlendState(in opaque, ref bo) < 0) { Log("screen fog: blend state failed"); return false; }
        _postFogBlend = bo;
        var dsd = new DepthStencilDesc { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero };
        ComPtr<ID3D11DepthStencilState> ds = default;
        if (_device.CreateDepthStencilState(in dsd, ref ds) < 0) { Log("screen fog: depth state failed"); return false; }
        _postFogDepthOff = ds;
        var rs = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 };
        ComPtr<ID3D11RasterizerState> raster = default;
        if (_device.CreateRasterizerState(in rs, ref raster) < 0) { Log("screen fog: rasterizer failed"); return false; }
        _postFogRaster = raster;

        _postFogOk = true;
        return true;
    }

    /// <summary>Fog the finished scene in place. Called after the scene and before bloom; leaves the colour
    /// target and depth bound behind it, as it found them.</summary>
    private void DrawPostFog(PreviewSettings s, Matrix4x4 view, Matrix4x4 proj)
    {
        PostFogPasses = 0;
        if (s.ScreenFog is not { } fog) return;
        if (_sceneCopy.Handle is null || _sceneCopySrv.Handle is null || _depthCopy.Handle is null
            || _depthCopySrv.Handle is null || _depth.Handle is null) return;
        if (!EnsurePostFogPipeline()) return;
        // a singular view-projection has no inverse, and NaNs in WorldViewProjInverse would fog the frame
        // with garbage - skip the pass for that frame instead (M761)
        if (!Matrix4x4.Invert(Matrix4x4.Multiply(view, proj), out _)) return;

        ComPtr<ID3D11RasterizerState> priorRaster = default;
        _ctx.RSGetState(ref priorRaster);

        // both inputs are copies: the pass writes the colour target it reads, and reads the depth it tests
        _ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        _ctx.CopyResource(_depthCopy, _depth);
        _ctx.CopyResource(_sceneCopy, _rt);

        var bytes = PostFogConstants(ShaderCamera(s), Matrix4x4.Multiply(view, proj), fog, _postFogConstants);
        MappedSubresource map = default;
        if (_ctx.Map(_postFogCb, 0, Map.WriteDiscard, 0, ref map) >= 0)
        {
            fixed (byte* src = bytes) Buffer.MemoryCopy(src, map.PData, bytes.Length, bytes.Length);
            _ctx.Unmap(_postFogCb, 0);
        }

        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        _ctx.IASetInputLayout(_postFogLayout);
        uint stride = 2 * sizeof(float), offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _postFogVb, in stride, in offset);
        _ctx.VSSetShader(_postFogVs, null, 0);
        _ctx.PSSetShader(_postFogPs, null, 0);
        _ctx.PSSetConstantBuffers(0, 1, ref _postFogCb);
        var srvs = stackalloc ID3D11ShaderResourceView*[2];
        srvs[0] = _depthCopySrv; srvs[1] = _sceneCopySrv;
        _ctx.PSSetShaderResources(0, 2, srvs);
        _ctx.PSSetSamplers(0, 1, ref _postFogSampler);
        _ctx.PSSetSamplers(15, 1, ref _postFogSampler);
        _ctx.RSSetState(_postFogRaster);
        _ctx.OMSetDepthStencilState(_postFogDepthOff, 0);
        var factor = stackalloc float[4] { 0f, 0f, 0f, 0f };
        _ctx.OMSetBlendState(_postFogBlend, factor, 0xFFFFFFFF);
        var target = stackalloc ID3D11RenderTargetView*[1];
        target[0] = _rtv;
        _ctx.OMSetRenderTargets(1, target, (ID3D11DepthStencilView*)null);
        var full = new Viewport(0, 0, _width, _height, 0, 1);
        _ctx.RSSetViewports(1, in full);
        _ctx.Draw(6, 0);
        PostFogPasses = 1;
        DrawCalls++;

        var none = stackalloc ID3D11ShaderResourceView*[2] { null, null };
        _ctx.PSSetShaderResources(0, 2, none);
        _ctx.OMSetRenderTargets(1, ref _rtv, _dsv);
        if (priorRaster.Handle is not null) { _ctx.RSSetState(priorRaster); priorRaster.Dispose(); }
    }

    private void DisposePostFog()
    {
        _postFogVs.Dispose(); _postFogVs = default;
        _postFogPs.Dispose(); _postFogPs = default;
        _postFogLayout.Dispose(); _postFogLayout = default;
        _postFogVb.Dispose(); _postFogVb = default;
        _postFogCb.Dispose(); _postFogCb = default;
        _postFogSampler.Dispose(); _postFogSampler = default;
        _postFogBlend.Dispose(); _postFogBlend = default;
        _postFogDepthOff.Dispose(); _postFogDepthOff = default;
        _postFogRaster.Dispose(); _postFogRaster = default;
    }
}
