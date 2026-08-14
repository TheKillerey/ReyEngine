using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace ReyEngine.Rendering.D3D11;

// M465: the sun shadow map.
//
// THE FINDING THIS ACTS ON (docs/research/frame-pipeline.md §4.2/§5.3, light-system.md §1.7). Every
// environment pixel shader Riot ships samples SHADOW_MAP_DEPTH_PCF_SharedTexture with five
// SampleCmpLevelZero taps, and ReyEngine's shaders already run that PCF correctly - against a 1x1 white
// stand-in, so nothing has ever been in shadow. This renders the map from the sun and binds the result.
//
// DISASSEMBLED BEFORE BUILT (M465 step 0). environment/shadowmap.vs takes POSITION and nothing else,
// multiplies by WORLD_MATRIX then VIEW_PROJECTION_MATRIX, and emits SV_Position alone;
// environment/shadowmap.ps has no inputs, no resources, no constant buffers and is `mov o0, 1; ret`.
// So the pass is DEPTH-ONLY and the shadow map is a depth-stencil resource read through a comparison
// sampler - not a colour target carrying packed depth. The full reasoning, including why
// filters/blur_shadow_3's packed `dot(rgb, (1/65536, 1/256, 1))` belongs to a different resource, is in
// SunShadowFit's header.
//
// BOTH SHADERS ARE RIOT'S OWN COMPILED BLOBS, run verbatim. No HLSL is authored here. What is synthesised
// CPU-side is the orthographic sun frustum, mShadowProj, the two depth biases and the 5-tap kernel - all of
// it in SunShadowFit, which is free of Direct3D and unit-tested away from the GPU.
public sealed unsafe partial class ShaderPreviewRenderer
{
    // ---------------------------------------------------------------- the blobs

    private byte[]? _shadowVsCode, _shadowPsCode;
    private bool _shadowTried, _shadowPipelineOk;

    /// <summary>Set once by the host, from the shader cache. Null blobs leave the whole feature off, the
    /// same contract the sky and the bloom chain have: a host that never supplies a source pays nothing.
    /// </summary>
    public void SetShadowShaders(byte[]? vs, byte[]? ps)
    {
        _shadowVsCode = vs; _shadowPsCode = ps;
        // A second scene build may hand over a different cache (a patch, a mod mount). Drop the built
        // pipeline so the next frame recreates it from the new bytes rather than silently keeping the old.
        DisposeShadowPipeline();
        _shadowTried = false; _shadowPipelineOk = false;
        _shadowFrame = null;
    }

    /// <summary>True once both blobs are present. Reported so a host can say "no shadow shaders" rather than
    /// leaving a switched-on toggle that does nothing.</summary>
    public bool HasShadowShaders => _shadowVsCode is not null && _shadowPsCode is not null;

    /// <summary>Caster slices drawn into the shadow map last frame. Zero with the toggle on means the pass
    /// bailed - no blobs, no sun, nothing visible, or a target that would not build.</summary>
    public int ShadowDraws { get; private set; }

    /// <summary>Half-extent of the fitted orthographic sun frustum, in world units, or 0 when the pass did
    /// not run. Next to <see cref="ShadowDraws"/> because a fit that has quietly grown to cover the whole
    /// map is the difference between crisp shadows and mush, and nothing else reports it.</summary>
    public float ShadowRadius { get; private set; }

    /// <summary>The fit the LAST successful pass used, or null when this frame has no shadow map. Read by
    /// FillConstantBuffer (for mShadowProj, the two biases and the kernel) and by StandIn (to decide whether
    /// the real depth texture or the white stand-in is bound), which is why it is one field rather than
    /// several: those two must never disagree about whether a shadow map exists.</summary>
    private SunShadowFrame? _shadowFrame;

    // ---------------------------------------------------------------- pipeline

    private ComPtr<ID3D11VertexShader> _shadowVs;
    private ComPtr<ID3D11PixelShader> _shadowPs;
    private ComPtr<ID3D11InputLayout> _shadowLayout;
    private ComPtr<ID3D11Buffer> _shadowWorldCb, _shadowFrameCb;
    private ComPtr<ID3D11RasterizerState> _shadowRaster;
    private ComPtr<ID3D11DepthStencilState> _shadowDepthState;
    private ComPtr<ID3D11BlendState> _shadowBlendNoColor;

    private ComPtr<ID3D11Texture2D> _shadowMap;
    private ComPtr<ID3D11DepthStencilView> _shadowDsv;
    private ComPtr<ID3D11ShaderResourceView> _shadowSrv;
    private bool _shadowTargetTried;

    /// <summary>
    /// Allocate the depth target. R32_TYPELESS with a D32_FLOAT depth-stencil view and an R32_FLOAT shader
    /// resource view - the same split <c>EnsureTargets</c> uses for the scene depth buffer, and for the same
    /// reason: a fully typed depth format can never carry an SRV, and this one has to be SAMPLED.
    ///
    /// <para>Attempted once: a creation failure that repeated every frame would hammer the driver and fill
    /// the log with one line. Fixed size rather than tracking the viewport - the shadow map's resolution is
    /// a quality setting, not a function of the window.</para>
    /// </summary>
    private bool EnsureShadowTarget()
    {
        if (_shadowTargetTried) return _shadowDsv.Handle is not null;
        _shadowTargetTried = true;

        var desc = new Texture2DDesc
        {
            Width = SunShadowFit.ShadowMapSize, Height = SunShadowFit.ShadowMapSize,
            MipLevels = 1, ArraySize = 1,
            Format = Format.FormatR32Typeless, SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)(BindFlag.DepthStencil | BindFlag.ShaderResource),
        };
        ComPtr<ID3D11Texture2D> tex = default;
        if (_device.CreateTexture2D(in desc, null, ref tex) < 0)
        { Log("shadow: the depth target could not be created; the pass is off"); return false; }
        _shadowMap = tex;

        var dsvDesc = new DepthStencilViewDesc
        { Format = Format.FormatD32Float, ViewDimension = DsvDimension.Texture2D };
        ComPtr<ID3D11DepthStencilView> dsv = default;
        if (_device.CreateDepthStencilView(_shadowMap, in dsvDesc, ref dsv) < 0)
        { Log("shadow: CreateDepthStencilView failed; the pass is off"); DisposeShadowTarget(); return false; }
        _shadowDsv = dsv;

        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Format.FormatR32Float,
            ViewDimension = Silk.NET.Core.Native.D3DSrvDimension.D3D11SrvDimensionTexture2D,
        };
        srvDesc.Texture2D.MostDetailedMip = 0;
        srvDesc.Texture2D.MipLevels = 1;
        ComPtr<ID3D11ShaderResourceView> srv = default;
        if (_device.CreateShaderResourceView(_shadowMap, in srvDesc, ref srv) < 0)
        { Log("shadow: CreateShaderResourceView failed; the pass is off"); DisposeShadowTarget(); return false; }
        _shadowSrv = srv;
        return true;
    }

    /// <summary>
    /// Bring up the two blobs as a pipeline. Lazy and remembered on failure, the same shape every other
    /// custom pass here uses.
    /// </summary>
    private bool EnsureShadowPipeline()
    {
        if (_shadowTried) return _shadowPipelineOk;
        _shadowTried = true;
        if (!HasShadowShaders) { Log("shadow: the host supplied no shader blobs; the pass is off"); return false; }

        ComPtr<ID3D11VertexShader> vs = default;
        fixed (byte* p = _shadowVsCode)
            if (_device.CreateVertexShader(p, (nuint)_shadowVsCode!.Length, ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vs) < 0)
            { Log("shadow: CreateVertexShader (environment/shadowmap.vs) failed"); return false; }
        _shadowVs = vs;

        ComPtr<ID3D11PixelShader> ps = default;
        fixed (byte* p = _shadowPsCode)
            if (_device.CreatePixelShader(p, (nuint)_shadowPsCode!.Length, ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref ps) < 0)
            { Log("shadow: CreatePixelShader (environment/shadowmap.ps) failed"); return false; }
        _shadowPs = ps;

        // environment/shadowmap.vs declares exactly one input: POSITION0, float3 (`dcl_input v0.xyz`). The
        // stride is the full PreviewVertex because this reads the SAME vertex buffer the scene pass reads -
        // position lives at offset 0 of it, so no repacking and no second upload.
        var semantic = System.Text.Encoding.ASCII.GetBytes("POSITION\0");
        fixed (byte* sem = semantic)
        fixed (byte* code = _shadowVsCode)
        {
            var el = new InputElementDesc
            {
                SemanticName = sem, SemanticIndex = 0,
                Format = Format.FormatR32G32B32Float, InputSlot = 0, AlignedByteOffset = 0,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            ComPtr<ID3D11InputLayout> layout = default;
            if (_device.CreateInputLayout(&el, 1, code, (nuint)_shadowVsCode!.Length, ref layout) < 0)
            { Log("shadow: CreateInputLayout failed"); return false; }
            _shadowLayout = layout;
        }

        // cb1 = $Globals, a single float4x4 WORLD_MATRIX. cb2 = PerFrameVertexCB; the shader declares
        // CB2[11], i.e. it reads up to register 10, so 176 bytes is the whole of what it can touch and
        // VIEW_PROJECTION_MATRIX at +112 is the last of it.
        if (!CreateShadowCb(64, out _shadowWorldCb)) return false;
        if (!CreateShadowCb(176, out _shadowFrameCb)) return false;

        // CullMode.None deliberately. M356 measured that this project has never established the winding of
        // League map geometry on the D3D11 path - enabling cull on the map pass in M354 deleted the terrain -
        // so the scene pass leaves culling to a per-material flag. Front-face culling is the usual trick for
        // shadow acne, and it is not available until that winding is known; DepthClipEnable stays on because
        // the near plane is fitted over the whole caster set rather than pancaked.
        var rs = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 };
        ComPtr<ID3D11RasterizerState> raster = default;
        if (_device.CreateRasterizerState(in rs, ref raster) < 0) { Log("shadow: rasterizer state failed"); return false; }
        _shadowRaster = raster;

        var dsd = new DepthStencilDesc
        {
            DepthEnable = 1, DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunc.Less, StencilEnable = 0,
        };
        ComPtr<ID3D11DepthStencilState> ds = default;
        if (_device.CreateDepthStencilState(in dsd, ref ds) < 0) { Log("shadow: depth state failed"); return false; }
        _shadowDepthState = ds;

        // No colour target is bound during the pass, so this is belt and braces - but shadowmap.ps writes
        // opaque white to SV_Target0 and a write mask of zero states outright that the value is waste.
        var bd = new BlendDesc();
        bd.RenderTarget[0] = new RenderTargetBlendDesc { BlendEnable = 0, RenderTargetWriteMask = 0 };
        ComPtr<ID3D11BlendState> blend = default;
        if (_device.CreateBlendState(in bd, ref blend) < 0) { Log("shadow: blend state failed"); return false; }
        _shadowBlendNoColor = blend;

        _shadowPipelineOk = true;
        return true;
    }

    private bool CreateShadowCb(uint bytes, out ComPtr<ID3D11Buffer> buffer)
    {
        var desc = new BufferDesc
        {
            ByteWidth = bytes, Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> cb = default;
        if (_device.CreateBuffer(in desc, null, ref cb) < 0)
        { Log($"shadow: {bytes}-byte constant buffer failed"); buffer = default; return false; }
        buffer = cb;
        return true;
    }

    // ---------------------------------------------------------------- the pass

    /// <summary>
    /// Render every opaque caster from the sun's point of view, and remember the fit so the scene pass can
    /// consume it. Called before the scene's targets are bound; leaves no state the scene pass does not
    /// set for itself, except the viewport, which the caller resets immediately after.
    ///
    /// <para>Sets <see cref="_shadowFrame"/> to null and returns on any bail-out, which is what makes the
    /// stand-in path and the Always comparison function reappear for the rest of the frame. A half-run
    /// shadow pass would otherwise leave the map sampling a stale depth buffer against a live matrix, which
    /// looks like shadows sliding across the world.</para>
    /// </summary>
    private void RenderSunShadowMap(PreviewSettings s, Matrix4x4 view, Matrix4x4 proj)
    {
        ShadowDraws = 0;
        ShadowRadius = 0f;
        _shadowFrame = null;

        if (!s.Shadows || !HasShadowShaders) return;
        // M228/M275: this points TOWARD the sun and is normalised on upload. A map that authored nothing
        // gets no shadow pass rather than a frustum aimed along a zero vector.
        var sun = s.MapSunDirection ?? -s.SunDirection;
        if (sun.LengthSquared() < 1e-12f) return;
        if (!EnsureShadowTarget() || !EnsureShadowPipeline()) return;

        // Receivers: what is on screen. Casters: everything, because the thing casting onto the screen is
        // very often off it. Both are gathered from the same per-slice bounds the frustum cull uses.
        var viewProj = Matrix4x4.Multiply(view, proj);
        if (!CollectShadowBounds(viewProj, out var casters, out var visible)) return;
        if (!SunShadowFit.TryReceiverBounds(viewProj, SunShadowFit.DefaultShadowDistance, visible, out var receivers))
            return;
        if (SunShadowFit.Fit(sun, casters, receivers) is not { } fit) return;

        // The map's own shadow SRV is about to become a depth-stencil view again. D3D11 would resolve that
        // by silently unbinding it, but only for the slot it actually notices; clearing the whole PS range
        // the material loop uses keeps the debug layer quiet and the hazard explicit.
        var none = stackalloc ID3D11ShaderResourceView*[16];
        for (int i = 0; i < 16; i++) none[i] = null;
        _ctx.PSSetShaderResources(0, 16, none);

        _ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, _shadowDsv);
        _ctx.ClearDepthStencilView(_shadowDsv, (uint)ClearFlag.Depth, 1f, 0);
        var vp = new Viewport(0, 0, SunShadowFit.ShadowMapSize, SunShadowFit.ShadowMapSize, 0, 1);
        _ctx.RSSetViewports(1, in vp);
        _ctx.RSSetState(_shadowRaster);
        _ctx.OMSetDepthStencilState(_shadowDepthState, 0);
        var factor = stackalloc float[4] { 0f, 0f, 0f, 0f };
        _ctx.OMSetBlendState(_shadowBlendNoColor, factor, 0xFFFFFFFF);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        _ctx.IASetInputLayout(_shadowLayout);
        _ctx.VSSetShader(_shadowVs, null, 0);
        _ctx.PSSetShader(_shadowPs, null, 0);

        // WORLD_MATRIX is the identity: map geometry reaches the vertex buffer already in world space, which
        // is exactly what the scene pass relies on too (RenderFrame passes Matrix4x4.Identity as `world`).
        var worldBytes = new byte[64];
        WriteMatrix(worldBytes, 0, Matrix4x4.Identity, s.TransposeMatrices);
        Upload(_shadowWorldCb, worldBytes, worldBytes.Length);

        // VIEW_PROJECTION_MATRIX at +112 - the constant the depth pass actually reads. NOT mShadowProj:
        // shadowmap.vs marks +176 [unused] and multiplies by cb2[7..10], which is +112.
        var frameBytes = new byte[176];
        WriteMatrix(frameBytes, 112, fit.ViewProjection, s.TransposeMatrices);
        Upload(_shadowFrameCb, frameBytes, frameBytes.Length);

        _ctx.VSSetConstantBuffers(1, 1, ref _shadowWorldCb);
        _ctx.VSSetConstantBuffers(2, 1, ref _shadowFrameCb);

        // Cull against the SUN's frustum, not the camera's - the whole point of the caster set is that it
        // includes geometry the camera cannot see.
        var sunPlanes = ExtractFrustum(fit.ViewProjection);
        int boundSource = -1;
        int draws = 0;
        foreach (var mat in _materials)
        {
            if (!IsShadowCaster(mat)) continue;
            if (mat.Bounds is { } bb && !FrustumContains(sunPlanes, bb.Min, bb.Max)) continue;

            uint count = mat.IndexCount < 0 ? (uint)_indexCount : (uint)mat.IndexCount;
            if (count == 0 || !BindMeshSource(dynamic: false, ref boundSource)) continue;
            _ctx.DrawIndexed(count, (uint)Math.Max(0, mat.StartIndex), 0);
            draws++;
        }

        // Unbind the depth-stencil view before anything can want the texture as an SRV again.
        _ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);

        ShadowDraws = draws;
        ShadowRadius = fit.Radius;
        // A pass that drew nothing produced an all-far depth buffer, in which every comparison passes and
        // every pixel is lit. Binding it would be indistinguishable from the white stand-in, so say so with
        // the same signal the rest of this file uses rather than pretending a shadow map exists.
        _shadowFrame = draws > 0 ? fit : null;
    }

    /// <summary>
    /// Which materials lay down depth for the sun.
    ///
    /// <para>Opaque static map geometry only. Everything excluded here is excluded because Riot's own
    /// environment shadow pass could not draw it: the particle families have their own cutout shaders
    /// (frame-pipeline.md §4.9, out of scope), and the ribbon, mesh-emitter and heat-haze branches carry
    /// geometry that is not in the scene vertex buffer at all. Materials that do not write depth are
    /// transparent by the same authored flag the scene pass uses, and a transparent surface that stamped an
    /// opaque shadow would be worse than no shadow.</para>
    ///
    /// <para><b>Alpha-tested geometry casts a SOLID shadow here</b>, because environment/shadowmap.ps has no
    /// discard - see SunShadowFit's header for the measured alternative Riot uses and what it would take.
    /// </para>
    /// </summary>
    private static bool IsShadowCaster(PreviewMaterial mat) =>
        mat.Visible && mat.WritesDepth && !mat.Additive && !mat.UsesDynamicMesh
        && mat.DistortionStrength is null && mat.RibbonId is null && mat.MeshGeometryId is null;

    /// <summary>
    /// One walk of the material list producing both sets: the union of every caster's bounds, and the
    /// per-slice boxes of the casters the camera can see. Slices with no bounds (a single-mesh preview, a
    /// particle system) are drawn but cannot contribute to a fit, so they are counted only as casters.
    /// </summary>
    private bool CollectShadowBounds(Matrix4x4 viewProj,
        out (Vector3 Min, Vector3 Max) casters, out List<(Vector3 Min, Vector3 Max)> visible)
    {
        casters = default;
        visible = _shadowVisibleScratch;
        visible.Clear();

        var planes = ExtractFrustum(viewProj);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var mat in _materials)
        {
            if (!IsShadowCaster(mat) || mat.Bounds is not { } bb) continue;
            min = Vector3.Min(min, bb.Min);
            max = Vector3.Max(max, bb.Max);
            any = true;
            if (FrustumContains(planes, bb.Min, bb.Max)) visible.Add(bb);
        }
        if (!any || visible.Count == 0) return false;
        casters = (min, max);
        return true;
    }

    private readonly List<(Vector3 Min, Vector3 Max)> _shadowVisibleScratch = new();

    /// <summary>Write a matrix at a byte offset, in the same layout <c>Mat()</c> uses - transposed when the
    /// host says so, which for every League shader it does.</summary>
    private static void WriteMatrix(byte[] bytes, int offset, Matrix4x4 m, bool transpose)
    {
        if (transpose) m = Matrix4x4.Transpose(m);
        var v = new[]
        {
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44,
        };
        for (int i = 0; i < v.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(offset + i * 4, 4), v[i]);
    }

    // ---------------------------------------------------------------- teardown

    private void DisposeShadowTarget()
    {
        _shadowSrv.Dispose(); _shadowDsv.Dispose(); _shadowMap.Dispose();
        _shadowSrv = default; _shadowDsv = default; _shadowMap = default;
    }

    private void DisposeShadowPipeline()
    {
        _shadowVs.Dispose(); _shadowVs = default;
        _shadowPs.Dispose(); _shadowPs = default;
        _shadowLayout.Dispose(); _shadowLayout = default;
        _shadowWorldCb.Dispose(); _shadowWorldCb = default;
        _shadowFrameCb.Dispose(); _shadowFrameCb = default;
        _shadowRaster.Dispose(); _shadowRaster = default;
        _shadowDepthState.Dispose(); _shadowDepthState = default;
        _shadowBlendNoColor.Dispose(); _shadowBlendNoColor = default;
    }

    private void DisposeShadow()
    {
        DisposeShadowTarget();
        DisposeShadowPipeline();
        _shadowTargetTried = false;
        _shadowFrame = null;
    }
}
