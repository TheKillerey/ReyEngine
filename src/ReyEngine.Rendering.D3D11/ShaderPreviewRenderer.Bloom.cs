using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace ReyEngine.Rendering.D3D11;

// M460: the glow buffer and the bloom chain.
//
// THE FINDING THIS ACTS ON (docs/research/frame-pipeline.md §2.2, §3.3). Every environment pixel shader
// Riot ships declares TWO render targets, and SV_Target1 is the glow source. 18 static-mesh shader families
// - the FEATURE_BLOOM set: ENV_GlowSign, DefaultEnv_Glow, Emissive_Basic, Hologram, Mantis_Env_Baked_PBR
// and the rest - write a COMPUTED o1 of the form `mul o1.xyz, <mask>, <colour>`. Everything else writes the
// constant (0,0,0,1). ReyEngine bound one RTV, so Riot's own shaders were computing that glow every frame
// and it was going on the floor. Nothing here reimplements an effect; it stops discarding an output of code
// that was already executing.
//
// MEASURED BEFORE BUILT (M460 step 0, `disasm glowperm sweep`). Which SHADERS can glow is not the same
// question as which PERMUTATIONS a real map selects, and if the answer had been "none" this would render a
// black RT1 and look broken for a reason unrelated to the code. Over all 206 shipped map material bins,
// 8,714 drawn materials resolve their pixel permutation exactly as Dx11SceneBuilder does, and 116 of them
// (1.33%, across 35 bins) land on a blob that writes a computed o1. Small, and precisely the surfaces a
// viewer looks at: lanterns, glowsigns, emissive foliage, holograms.
//
// EVERY SHADER HERE IS RIOT'S OWN COMPILED BLOB, run verbatim, exactly as the material shaders already are.
// No HLSL is authored in this file. What is synthesised CPU-side is one float2 per pass and the number of
// levels - see BloomChain, which holds all of it and is unit-tested away from the GPU.
public sealed unsafe partial class ShaderPreviewRenderer
{
    // ---------------------------------------------------------------- the blobs

    private byte[]? _bloomVsCode, _bloomDownCode, _bloomUpCode, _bloomBlurCode, _bloomCompositeCode;
    private bool _bloomTried, _bloomPipelineOk;

    /// <summary>Set once by the host, from the shader cache. Null blobs leave the whole feature off, which
    /// is the same contract the sky has: a host that never supplies a source pays nothing.</summary>
    public void SetBloomShaders(byte[]? vs, byte[]? downsample, byte[]? upsample, byte[]? blur, byte[]? composite)
    {
        _bloomVsCode = vs; _bloomDownCode = downsample; _bloomUpCode = upsample;
        _bloomBlurCode = blur; _bloomCompositeCode = composite;
        // A second scene build may hand over a different cache (a patch, a mod mount). Drop the built
        // pipeline so the next frame recreates it from the new bytes rather than silently keeping the old,
        // and the targets with it so a host that has just withdrawn its shaders stops paying for them.
        DisposeBloomPipeline();
        ResetBloomTargets();
        _bloomTried = false; _bloomPipelineOk = false;
    }

    /// <summary>True once all five blobs are present. Reported so a host can say "no bloom shaders" rather
    /// than leaving a switched-on toggle that does nothing.</summary>
    public bool HasBloomShaders =>
        _bloomVsCode is not null && _bloomDownCode is not null && _bloomUpCode is not null
        && _bloomBlurCode is not null && _bloomCompositeCode is not null;

    /// <summary>Full-screen passes the last frame ran. Zero with the toggle on means the chain bailed —
    /// no blobs, a target too small to halve, or a pipeline that would not build.</summary>
    public int BloomPasses { get; private set; }

    // ---------------------------------------------------------------- pipeline

    private ComPtr<ID3D11VertexShader> _bloomVs;
    private ComPtr<ID3D11PixelShader> _bloomDownPs, _bloomUpPs, _bloomBlurPs, _bloomCompositePs;
    private ComPtr<ID3D11InputLayout> _bloomLayout;
    private ComPtr<ID3D11Buffer> _bloomQuadVb, _bloomCb;
    private ComPtr<ID3D11RasterizerState> _bloomRaster;
    private ComPtr<ID3D11BlendState> _bloomBlendOpaque, _bloomBlendAdd;
    private ComPtr<ID3D11DepthStencilState> _bloomDepthOff;

    // ---------------------------------------------------------------- targets

    /// <summary>RT1 for the scene pass: what Riot's shaders write their glow into. Level 0 of the chain.</summary>
    private ComPtr<ID3D11Texture2D> _glowRt;
    private ComPtr<ID3D11RenderTargetView> _glowRtv;
    private ComPtr<ID3D11ShaderResourceView> _glowSrv;

    /// <summary>Levels 1..N-1. Index 0 is left empty so the array index IS the chain level - the one place
    /// an off-by-one here would be invisible is exactly the mapping between the two.</summary>
    private ComPtr<ID3D11Texture2D>[] _bloomMip = Array.Empty<ComPtr<ID3D11Texture2D>>();
    private ComPtr<ID3D11RenderTargetView>[] _bloomMipRtv = Array.Empty<ComPtr<ID3D11RenderTargetView>>();
    private ComPtr<ID3D11ShaderResourceView>[] _bloomMipSrv = Array.Empty<ComPtr<ID3D11ShaderResourceView>>();

    /// <summary>Scratch at level-1 size for the separable Gaussian: horizontal writes here, vertical reads
    /// it and writes back into level 1. One texture rather than two, because the vertical pass's source and
    /// destination are already different resources.</summary>
    private ComPtr<ID3D11Texture2D> _bloomBlurRt;
    private ComPtr<ID3D11RenderTargetView> _bloomBlurRtv;
    private ComPtr<ID3D11ShaderResourceView> _bloomBlurSrv;

    private IReadOnlyList<BloomLevel> _bloomLevels = Array.Empty<BloomLevel>();

    /// <summary>Set for the duration of a frame that is collecting glow. Read by
    /// <see cref="BindSceneTargets"/>, which is the ONE place the scene's render targets are bound - the
    /// capture helpers unbind and rebind mid-loop, and a second literal binding site there is exactly how
    /// RT1 would be silently dropped halfway through the frame.</summary>
    private bool _glowBound;

    private bool _bloomTargetsTried;

    /// <summary>Drop the glow buffer and the chain, and let the next frame that wants them rebuild at the
    /// new size. Called from EnsureTargets on a resize, and whenever the host swaps the shader set.</summary>
    private void ResetBloomTargets()
    {
        DisposeBloomTargets();
        _bloomTargetsTried = false;
    }

    /// <summary>
    /// Allocate the glow buffer and the mip chain, at the colour target's current size.
    ///
    /// <para>LAZY, and gated on the host having actually supplied shaders, rather than allocated alongside
    /// the colour target. At 1080p this set is about 13 MB, and the standalone shader-preview window drives
    /// this same renderer without ever calling SetBloomShaders - it would have paid for a glow buffer that
    /// nothing could ever read. Attempted once per size: a creation failure that repeated every frame would
    /// hammer the driver and fill the log with the same line.</para>
    /// </summary>
    private bool EnsureBloomTargets()
    {
        if (_bloomTargetsTried) return _glowRtv.Handle is not null;
        _bloomTargetsTried = true;

        int w = _width, h = _height;
        _bloomLevels = BloomChain.Levels(w, h);
        if (_bloomLevels.Count < 2) return false;   // nothing to downsample into; DrawBloom bails too

        // B8G8R8A8_UNORM, matching the colour target exactly. NOT a float format: §3.2's central negative
        // result is that Riot's frame is display-referred by construction, and the composite is a SCREEN
        // blend which provably cannot exceed 1, so there is nothing for extra range to carry.
        var desc = new Texture2DDesc
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.FormatB8G8R8A8Unorm, SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource),
        };
        if (!CreateBloomTarget(desc, out _glowRt, out _glowRtv, out _glowSrv))
        { Log("bloom: the glow render target could not be created; RT1 stays unbound"); DisposeBloomTargets(); return false; }

        int n = _bloomLevels.Count;
        _bloomMip = new ComPtr<ID3D11Texture2D>[n];
        _bloomMipRtv = new ComPtr<ID3D11RenderTargetView>[n];
        _bloomMipSrv = new ComPtr<ID3D11ShaderResourceView>[n];
        for (int i = 1; i < n; i++)
        {
            var d = desc;
            d.Width = (uint)_bloomLevels[i].Width; d.Height = (uint)_bloomLevels[i].Height;
            if (CreateBloomTarget(d, out _bloomMip[i], out _bloomMipRtv[i], out _bloomMipSrv[i])) continue;
            Log($"bloom: mip level {i} could not be created; the chain is off");
            DisposeBloomTargets();
            return false;
        }

        var blurDesc = desc;
        blurDesc.Width = (uint)_bloomLevels[1].Width; blurDesc.Height = (uint)_bloomLevels[1].Height;
        if (CreateBloomTarget(blurDesc, out _bloomBlurRt, out _bloomBlurRtv, out _bloomBlurSrv)) return true;
        Log("bloom: the separable-blur scratch target could not be created; the chain is off");
        DisposeBloomTargets();
        return false;
    }

    private bool CreateBloomTarget(Texture2DDesc desc, out ComPtr<ID3D11Texture2D> tex,
        out ComPtr<ID3D11RenderTargetView> rtv, out ComPtr<ID3D11ShaderResourceView> srv)
    {
        tex = default; rtv = default; srv = default;
        ComPtr<ID3D11Texture2D> t = default;
        if (_device.CreateTexture2D(in desc, null, ref t) < 0) return false;
        tex = t;
        ComPtr<ID3D11RenderTargetView> r = default;
        if (_device.CreateRenderTargetView(tex, null, ref r) < 0) return false;
        rtv = r;
        ComPtr<ID3D11ShaderResourceView> s = default;
        if (_device.CreateShaderResourceView(tex, null, ref s) < 0) return false;
        srv = s;
        return true;
    }

    /// <summary>
    /// Bring up the five blobs as a pipeline. Lazy and remembered on failure, the same shape every other
    /// custom pass here uses - a driver that refuses one of these must not be asked again every frame.
    /// </summary>
    private bool EnsureBloomPipeline()
    {
        if (_bloomTried) return _bloomPipelineOk;
        _bloomTried = true;
        if (!HasBloomShaders) { Log("bloom: the host supplied no shader blobs; the chain is off"); return false; }

        ComPtr<ID3D11VertexShader> vs = default;
        fixed (byte* p = _bloomVsCode)
            if (_device.CreateVertexShader(p, (nuint)_bloomVsCode!.Length, ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vs) < 0)
            { Log("bloom: CreateVertexShader (gamma/post_effect.vs) failed"); return false; }
        _bloomVs = vs;

        if (!CreateBloomPs(_bloomDownCode!, "filters/mipchainbloomdownsample.ps", out _bloomDownPs)) return false;
        if (!CreateBloomPs(_bloomUpCode!, "filters/mipchainbloomupsample.ps", out _bloomUpPs)) return false;
        if (!CreateBloomPs(_bloomBlurCode!, "filters/bloom.ps", out _bloomBlurPs)) return false;
        if (!CreateBloomPs(_bloomCompositeCode!, "gamma/ps_copy_post.ps", out _bloomCompositePs)) return false;

        // gamma/post_effect.vs declares exactly one input: POSITION0, float2 (blob 0 line 21, dcl_input
        // v0.xy). It turns it into clip space itself - `o0.xy = v0.xy * 2 - 1` at line 24 - and derives the
        // UV as `(x, 1 - y)` at line 26. So the quad below is in [0,1] and nothing else is needed.
        var semantic = System.Text.Encoding.ASCII.GetBytes("POSITION\0");
        fixed (byte* sem = semantic)
        fixed (byte* code = _bloomVsCode)
        {
            var el = new InputElementDesc
            {
                SemanticName = sem, SemanticIndex = 0,
                Format = Format.FormatR32G32Float, InputSlot = 0, AlignedByteOffset = 0,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            ComPtr<ID3D11InputLayout> layout = default;
            if (_device.CreateInputLayout(&el, 1, code, (nuint)_bloomVsCode!.Length, ref layout) < 0)
            { Log("bloom: CreateInputLayout failed"); return false; }
            _bloomLayout = layout;
        }

        // Two triangles over the unit square. Winding is not chosen carefully because the raster state
        // below culls nothing - a full-screen pass that vanishes because of a winding convention is a
        // failure mode with no upside to preventing it any other way.
        float[] quad = { 0f, 0f,  1f, 0f,  0f, 1f,   0f, 1f,  1f, 0f,  1f, 1f };
        var vbDesc = new BufferDesc
        {
            ByteWidth = (uint)(quad.Length * sizeof(float)),
            Usage = Usage.Immutable, BindFlags = (uint)BindFlag.VertexBuffer,
        };
        ComPtr<ID3D11Buffer> vb = default;
        fixed (float* p = quad)
        {
            var sub = new SubresourceData { PSysMem = p };
            if (_device.CreateBuffer(in vbDesc, in sub, ref vb) < 0) { Log("bloom: quad vertex buffer failed"); return false; }
        }
        _bloomQuadVb = vb;

        // All three filters declare the identical $Globals: one float2 UVStep at +0, and nothing else.
        // 16 bytes is the constant-buffer minimum granularity, so the float2 is padded rather than packed.
        var cbDesc = new BufferDesc
        {
            ByteWidth = 16, Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> cb = default;
        if (_device.CreateBuffer(in cbDesc, null, ref cb) < 0) { Log("bloom: cbuffer failed"); return false; }
        _bloomCb = cb;

        var rs = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 };
        ComPtr<ID3D11RasterizerState> raster = default;
        if (_device.CreateRasterizerState(in rs, ref raster) < 0) { Log("bloom: rasterizer state failed"); return false; }
        _bloomRaster = raster;

        var opaque = new BlendDesc();
        opaque.RenderTarget[0] = new RenderTargetBlendDesc
        { BlendEnable = 0, RenderTargetWriteMask = (byte)ColorWriteEnable.All };
        ComPtr<ID3D11BlendState> bo = default;
        if (_device.CreateBlendState(in opaque, ref bo) < 0) { Log("bloom: opaque blend state failed"); return false; }
        _bloomBlendOpaque = bo;

        // ADDITIVE, for the upsample only. frame-pipeline.md §7 item 2 records this as NOT measured: DXBC
        // carries no pass list, so the shader shows a 3x3 tent (/16) and says nothing about how the result
        // meets the level below. One-over-one is the standard mip-chain bloom, and it is the reading that
        // makes the tent a filter rather than a replacement - a replacing upsample would discard every
        // level's own detail on the way back up and leave only the coarsest. Flagged rather than asserted.
        var add = new BlendDesc();
        add.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = Blend.One, DestBlend = Blend.One, BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.One, BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ComPtr<ID3D11BlendState> ba = default;
        if (_device.CreateBlendState(in add, ref ba) < 0) { Log("bloom: additive blend state failed"); return false; }
        _bloomBlendAdd = ba;

        var dsd = new DepthStencilDesc { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero };
        ComPtr<ID3D11DepthStencilState> ds = default;
        if (_device.CreateDepthStencilState(in dsd, ref ds) < 0) { Log("bloom: depth state failed"); return false; }
        _bloomDepthOff = ds;

        _bloomPipelineOk = true;
        return true;
    }

    private bool CreateBloomPs(byte[] code, string name, out ComPtr<ID3D11PixelShader> ps)
    {
        ComPtr<ID3D11PixelShader> p = default;
        fixed (byte* b = code)
            if (_device.CreatePixelShader(b, (nuint)code.Length, ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref p) < 0)
            { Log($"bloom: CreatePixelShader ({name}) failed"); ps = default; return false; }
        ps = p;
        return true;
    }

    // ---------------------------------------------------------------- the chain

    /// <summary>Can this frame collect glow at all? Decided once, before the clear, because the answer
    /// changes how many render targets get bound and every later step has to agree with that one.
    ///
    /// <para><c>_sceneCopy</c> is in the list because the composite reads the finished frame while writing
    /// to it, so without that copy there is no way to composite - and collecting glow all frame and then
    /// having nowhere to put it would be strictly worse than not collecting it.</para></summary>
    private bool BloomAvailable(PreviewSettings s) =>
        s.Bloom && HasBloomShaders
        && _sceneCopy.Handle is not null && _sceneCopySrv.Handle is not null
        && EnsureBloomTargets() && _bloomLevels.Count >= 2
        && EnsureBloomPipeline();

    /// <summary>
    /// Run the chain and composite it onto the scene. Called after all scene geometry and after RT1 has
    /// been unbound, and before the editor furniture - the game composites bloom under its UI layer, and a
    /// bloomed gizmo would be an editor artefact rather than a rendering of the map.
    /// </summary>
    private void DrawBloom()
    {
        BloomPasses = 0;
        int n = _bloomLevels.Count;
        if (n < 2) return;

        // The rasterizer state is the one piece of pipeline state the editor overlays below do NOT set for
        // themselves - they bind their own shaders, layout, topology, blend and depth, and inherit whatever
        // fill and cull mode the scene left behind. Wireframe is a fill mode, so a bloom pass that quietly
        // swapped in its own solid state would make the gizmo and the bucket grid stop drawing as wireframe
        // when bloom is on and start again when it is off. Saved and restored instead, so this whole
        // function is invisible to everything after it. RSGetState AddRefs, hence the Dispose.
        ComPtr<ID3D11RasterizerState> priorRaster = default;
        _ctx.RSGetState(ref priorRaster);

        // No render target of any kind while the state below is set up - the glow buffer is about to be
        // read as an SRV and it was an RTV a moment ago.
        _ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        _ctx.IASetInputLayout(_bloomLayout);
        uint stride = 8, offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _bloomQuadVb, in stride, in offset);
        _ctx.VSSetShader(_bloomVs, null, 0);
        _ctx.RSSetState(_bloomRaster);
        _ctx.OMSetDepthStencilState(_bloomDepthOff, 0);

        // The three filters sample at s0; gamma/ps_copy_post binds Clamp_No_Mip_SharedSampler at s15.
        // Bound at both, once, because it is the same state and the alternative is a per-pass branch that
        // can only ever be wrong in one direction.
        var samp = _linearClamp;
        _ctx.PSSetSamplers(0, 1, ref samp);
        _ctx.PSSetSamplers(15, 1, ref samp);

        // 1. DOWN. Level i is filtered from level i-1 with the 13-tap, UVStep = one texel of the SOURCE.
        for (int i = 1; i < n; i++)
        {
            var src = i == 1 ? _glowSrv : _bloomMipSrv[i - 1];
            var (sx, sy) = BloomChain.UvStep(_bloomLevels[i - 1]);
            BloomPass(_bloomMipRtv[i], _bloomLevels[i], src, _bloomDownPs, sx, sy, additive: false);
        }

        // 2. UP, additively, stopping at level 1. Nothing coarser than level 1 is ever composited on its
        // own; level 1 accumulates the whole chain and is what the Gaussian then works on.
        for (int i = n - 1; i >= 2; i--)
        {
            var (sx, sy) = BloomChain.UvStep(_bloomLevels[i]);
            BloomPass(_bloomMipRtv[i - 1], _bloomLevels[i - 1], _bloomMipSrv[i], _bloomUpPs, sx, sy, additive: true);
        }

        // 3. The 7-tap separable Gaussian, horizontal then vertical, both at level 1. The vertical pass
        // writes back into level 1 while reading the scratch, so the two never alias.
        var lvl1 = _bloomLevels[1];
        var (hx, hy) = BloomChain.BlurStep(lvl1, horizontal: true);
        BloomPass(_bloomBlurRtv, lvl1, _bloomMipSrv[1], _bloomBlurPs, hx, hy, additive: false);
        var (vx, vy) = BloomChain.BlurStep(lvl1, horizontal: false);
        BloomPass(_bloomMipRtv[1], lvl1, _bloomBlurSrv, _bloomBlurPs, vx, vy, additive: false);

        // 4. COMPOSITE. gamma/ps_copy_post.ps blob 1 (the BLOOM=1 permutation) reads
        // SAMPLER_BACK_BUFFER_COPY at t0 and BLOOM_TEXTURE at t1 and does
        //     o0.rgb = 1 - (1 - scene) * (1 - bloom);  o0.a = scene.a
        // - the SCREEN blend, not an additive one, which is why the whole chain is correct in 8 bits.
        //
        // ps_copy_post rather than ps_gamma: ps_gamma's extra step is a per-channel 1-D lookup table
        // (§2.3) driven by the [Accessibility] ColorGamma/ColorBrightness sliders, which sit at their 0.5
        // midpoint by default and make the LUT an identity. It is an accessibility control, not a tonemap
        // or an sRGB encode, and it is generated at runtime rather than shipped in the WADs - so there is
        // nothing to bind and nothing to apply. Skipping it is the correct default, and ps_copy_post is
        // Riot's own no-LUT variant of the same composite rather than our approximation of one.
        //
        // The scene has to be copied first: the composite reads the whole frame and writes the whole frame,
        // and a resource cannot be an SRV and an RTV in the same draw. _sceneCopy is the same-format,
        // same-size copy M282 already allocates for heat haze; every distortion draw finished long ago.
        _ctx.CopyResource(_sceneCopy, _rt);

        // No depth-stencil view: this pass tests nothing and writes nothing to depth, and leaving the DSV
        // bound while the editor furniture is about to want it changes nothing except what a debug layer
        // has to reason about.
        //
        // M763: the target is switched BEFORE the inputs are bound. The last blur pass leaves level 1 bound
        // as the render target, and D3D11 refuses an SRV of a resource that is still bound for output - it
        // silently binds NULL instead. From M460 until M763, BLOOM_TEXTURE therefore read black, the screen
        // blend returned the scene unchanged, and the frame was byte-identical with bloom on or off while
        // BloomPasses still counted 12. Measured: Ahri moves 0 px before this order and 64,095 px after.
        var target = stackalloc ID3D11RenderTargetView*[1];
        target[0] = _rtv;
        _ctx.OMSetRenderTargets(1, target, (ID3D11DepthStencilView*)null);

        var srvs = stackalloc ID3D11ShaderResourceView*[2];
        srvs[0] = _sceneCopySrv; srvs[1] = _bloomMipSrv[1];
        _ctx.PSSetShaderResources(0, 2, srvs);
        var full = new Viewport(0, 0, _width, _height, 0, 1);
        _ctx.RSSetViewports(1, in full);
        _ctx.PSSetShader(_bloomCompositePs, null, 0);
        var factor = stackalloc float[4] { 0f, 0f, 0f, 0f };
        _ctx.OMSetBlendState(_bloomBlendOpaque, factor, 0xFFFFFFFF);
        _ctx.Draw(6, 0);
        BloomPasses++;

        // Unbind both, then restore the scene's target and viewport. _rt is about to be an RTV again for
        // the editor overlays, and _sceneCopySrv is a view of the resource the next frame's CopyResource
        // writes into.
        var none = stackalloc ID3D11ShaderResourceView*[2];
        none[0] = null; none[1] = null;
        _ctx.PSSetShaderResources(0, 2, none);
        _ctx.OMSetRenderTargets(1, ref _rtv, _dsv);
        _ctx.RSSetViewports(1, in full);
        if (priorRaster.Handle is not null) { _ctx.RSSetState(priorRaster); priorRaster.Dispose(); }
    }

    /// <summary>One full-screen filter pass. <paramref name="stepX"/>/<paramref name="stepY"/> are the
    /// <c>$Globals.UVStep</c> the shader offsets its taps by; see <see cref="BloomChain"/> for why they are
    /// the SOURCE level's texel size.</summary>
    private void BloomPass(ComPtr<ID3D11RenderTargetView> dst, BloomLevel level,
        ComPtr<ID3D11ShaderResourceView> src, ComPtr<ID3D11PixelShader> ps,
        float stepX, float stepY, bool additive)
    {
        // Drop the previous pass's source before binding this pass's target: the level being written now
        // was very often the level read a moment ago, and D3D11 resolves that hazard by silently unbinding
        // the SRV - which would leave the next pass sampling nothing with no error anywhere.
        var none = stackalloc ID3D11ShaderResourceView*[2];
        none[0] = null; none[1] = null;
        _ctx.PSSetShaderResources(0, 2, none);

        var target = stackalloc ID3D11RenderTargetView*[1];
        target[0] = dst;
        _ctx.OMSetRenderTargets(1, target, (ID3D11DepthStencilView*)null);
        var vp = new Viewport(0, 0, level.Width, level.Height, 0, 1);
        _ctx.RSSetViewports(1, in vp);

        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), stepX);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), stepY);
        Upload(_bloomCb, bytes, bytes.Length);
        _ctx.PSSetConstantBuffers(0, 1, ref _bloomCb);

        var s = src;
        _ctx.PSSetShaderResources(0, 1, ref s);
        _ctx.PSSetShader(ps, null, 0);

        var factor = stackalloc float[4] { 0f, 0f, 0f, 0f };
        _ctx.OMSetBlendState(additive ? _bloomBlendAdd : _bloomBlendOpaque, factor, 0xFFFFFFFF);
        _ctx.Draw(6, 0);
        BloomPasses++;
    }

    // ---------------------------------------------------------------- teardown

    private void DisposeBloomTargets()
    {
        _glowSrv.Dispose(); _glowRtv.Dispose(); _glowRt.Dispose();
        _glowSrv = default; _glowRtv = default; _glowRt = default;
        for (int i = 0; i < _bloomMip.Length; i++)
        {
            _bloomMipSrv[i].Dispose(); _bloomMipRtv[i].Dispose(); _bloomMip[i].Dispose();
        }
        _bloomMip = Array.Empty<ComPtr<ID3D11Texture2D>>();
        _bloomMipRtv = Array.Empty<ComPtr<ID3D11RenderTargetView>>();
        _bloomMipSrv = Array.Empty<ComPtr<ID3D11ShaderResourceView>>();
        _bloomBlurSrv.Dispose(); _bloomBlurRtv.Dispose(); _bloomBlurRt.Dispose();
        _bloomBlurSrv = default; _bloomBlurRtv = default; _bloomBlurRt = default;
        _bloomLevels = Array.Empty<BloomLevel>();
    }

    private void DisposeBloomPipeline()
    {
        _bloomVs.Dispose(); _bloomVs = default;
        _bloomDownPs.Dispose(); _bloomDownPs = default;
        _bloomUpPs.Dispose(); _bloomUpPs = default;
        _bloomBlurPs.Dispose(); _bloomBlurPs = default;
        _bloomCompositePs.Dispose(); _bloomCompositePs = default;
        _bloomLayout.Dispose(); _bloomLayout = default;
        _bloomQuadVb.Dispose(); _bloomQuadVb = default;
        _bloomCb.Dispose(); _bloomCb = default;
        _bloomRaster.Dispose(); _bloomRaster = default;
        _bloomBlendOpaque.Dispose(); _bloomBlendOpaque = default;
        _bloomBlendAdd.Dispose(); _bloomBlendAdd = default;
        _bloomDepthOff.Dispose(); _bloomDepthOff = default;
    }

    private void DisposeBloom()
    {
        DisposeBloomTargets();
        DisposeBloomPipeline();
    }
}
