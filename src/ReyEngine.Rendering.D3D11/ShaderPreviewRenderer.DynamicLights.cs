using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.DXGI;

namespace ReyEngine.Rendering.D3D11;

// M452: the D3D11 half of the editor's dynamic point lights (Light.dat / the Lighting window list), so
// switching renderers no longer switches the user's placed lights off. The GL viewport has drawn these
// since M70; this surface drew none.
//
// The map pass runs Riot's fixed compiled pixel shaders, so light math cannot be injected into it. This is
// therefore an ADDITIVE OVERLAY: after the scene has drawn, the map slices are drawn AGAIN with a small
// custom shader that accumulates only the point-light contribution - blend ONE/ONE, depth LESS_EQUAL,
// depth write OFF - so the pools of light land on the already-rendered surfaces.
//
// The light model is the GL loop (ViewportMeshRenderer, uNumLights block) term for term, because GL is the
// reference AND the lightmap baker uses the same curve (BakeLighting.Attenuation) - if this pass drew a
// different pool than the bake produces, DX11 would be previewing a lie. The XZ fit is applied on the CPU
// via BakeLighting.FitPosition - the ONE shared implementation - rather than re-typed in HLSL, which would
// be exactly the third-copy drift that method's own comment warns about.
//
// Built on the sky/mesh-pipeline pattern: lazy compile remembered on failure, own shaders/layout/cbuffer/
// states, drawing the SAME static vertex/index buffers the scene pass uses (no geometry is duplicated).
public sealed unsafe partial class ShaderPreviewRenderer
{
    private ComPtr<ID3D11VertexShader> _lightVs;
    private ComPtr<ID3D11PixelShader> _lightPs;
    private ComPtr<ID3D11InputLayout> _lightLayout;
    private ComPtr<ID3D11Buffer> _lightCb;
    private ComPtr<ID3D11DepthStencilState> _lightDepth;
    private ComPtr<ID3D11BlendState> _lightBlend;
    private ComPtr<ID3D11ShaderResourceView> _lightGrey;
    private bool _lightTried;
    private float[] _lightCbData = System.Array.Empty<float>();

    /// <summary>Lights per draw. The cbuffer carries exactly this many; a longer list loops the pass in
    /// batches (the user has ported Light.dat tables with ~365 lights, which is 6 batches - see
    /// <see cref="LightBatchCount"/> and its test).</summary>
    public const int MaxLightsPerDraw = 64;

    /// <summary>How many overlay batches a light list needs. Pure so the batching rule is testable
    /// without a device.</summary>
    public static int LightBatchCount(int lightCount) =>
        lightCount <= 0 ? 0 : (lightCount + MaxLightsPerDraw - 1) / MaxLightsPerDraw;

    /// <summary>Set by <see cref="DrawDynamicLights"/>: slice draws x batches last frame, for diagnostics.</summary>
    public int DynamicLightDraws { get; private set; }

    // ASCII only - the compiler is handed these bytes raw (same trap as the GL sources, M117b).
    //
    // The pixel math mirrors ViewportMeshRenderer's uNumLights loop EXACTLY - falloff blend, the
    // 0.35 + 0.65*ndl wrap, per-light strength in .a, global intensity applied once at the end. No
    // specular, no shadows: GL has neither, and this pass must not invent light the reference lacks.
    private const string LightHlsl = @"
cbuffer LightCB : register(b0)
{
    row_major float4x4 gViewProj;
    float4 gCounts;                // x = lights this batch, y = global intensity, z = falloff softness
    float4 gPosRadius[64];         // xyz = FITTED world position, w = radius * radius scale
    float4 gColorStrength[64];     // rgb = colour 0..1, a = per-light strength (M153)
};
Texture2D    gDiffuse : register(t0);
SamplerState gSamp    : register(s0);

struct VIn  { float3 pos : POSITION; float3 nrm : NORMAL; float2 uv : TEXCOORD0; };
struct VOut { float4 pos : SV_Position; float3 world : TEXCOORD0; float3 nrm : TEXCOORD1; float2 uv : TEXCOORD2; };

VOut vsmain(VIn i)
{
    VOut o;
    o.pos = mul(float4(i.pos, 1.0), gViewProj);
    // This pass recomputes clip position with its own shader, so its depth does not land bit-identically
    // on what Riot's vertex shader wrote for the same triangle - at equal depth LESS_EQUAL is a coin toss
    // (see EnsureOverlay's no-test escape hatch). The bucket grid solved the same problem with this exact
    // clip-space bias (GridHlsl), so the constant is proven on map-scale depth rather than guessed.
    o.pos.z -= 0.0006 * o.pos.w;
    o.world = i.pos;               // map geometry is world-space (RenderFrame draws it with world = I)
    o.nrm = i.nrm;
    o.uv = i.uv;
    return o;
}

float4 psmain(VOut i) : SV_Target
{
    // Same degenerate-normal guard as the GL fragment stage.
    float3 n = dot(i.nrm, i.nrm) > 1e-6 ? normalize(i.nrm) : float3(0.0, 1.0, 0.0);
    float3 acc = float3(0.0, 0.0, 0.0);
    int count = (int)gCounts.x;
    [loop] for (int k = 0; k < count; k++)
    {
        float3 toLight = gPosRadius[k].xyz - i.world;
        float radius = gPosRadius[k].w;
        float dist = length(toLight);
        if (dist < radius)
        {
            // The falloff blend the baker uses (BakeLighting.Attenuation): 0 = (1-t)^2, 1 = (1-t^2)^2.
            float t = dist / radius;
            float sharpF = 1.0 - t;      sharpF *= sharpF;
            float softF  = 1.0 - t * t;  softF  *= softF;
            float atten = lerp(sharpF, softF, gCounts.z);
            float ndl = max(dot(n, toLight / max(dist, 0.0001)), 0.0);
            acc += gColorStrength[k].rgb * gColorStrength[k].a * atten * (0.35 + 0.65 * ndl);
        }
    }
    // base * light * global intensity, added onto the frame by the ONE/ONE blend - the same
    // `col += base * dynamicLight * uLightIntensity` GL computes in-shader. gDiffuse is the slice's own
    // diffuse where one is identifiable, else a 0.5 grey stand-in (see LightBaseTexture); the UV is the
    // raw TEXCOORD0 - authored per-material UV transforms are not re-applied here, which off-tints the
    // rare scrolling material but never moves or reshapes the pool. Alpha 0: the blend keeps dest alpha.
    float4 d = gDiffuse.Sample(gSamp, i.uv);
    // COVERAGE. The first cut sampled .rgb and ignored .a, so an additive pool landed on every texel of a
    // decal or cutout quad including the fully transparent ones - a rectangular glow patch where the decal
    // is invisible, which is the decal regression this fixes. A texel that covers nothing can reflect no
    // light, so the term is scaled by coverage and dropped entirely below the alpha-test floor. The grey
    // stand-in is a=1, so an opaque slice is unaffected. (Note for the reader: no double quotes in here -
    // this whole listing lives in a C# verbatim string and a stray quote ends it.)
    if (d.a < 0.02) discard;
    return float4(d.rgb * acc * gCounts.y * d.a, 0.0);
}";

    private bool EnsureDynamicLights()
    {
        if (_lightTried) return _lightVs.Handle is not null;
        _lightTried = true;

        ID3D10Blob* vsCode = null, psCode = null, errs = null;
        var src = System.Text.Encoding.ASCII.GetBytes(LightHlsl);
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* sp = src)
            {
                if (!CompileLight(compiler, sp, src.Length, "vsmain", "vs_5_0", &vsCode, &errs)) return false;
                if (!CompileLight(compiler, sp, src.Length, "psmain", "ps_5_0", &psCode, &errs)) return false;
            }
        }
        catch (Exception ex) { Log("lights: the HLSL compiler is unavailable: " + ex.Message); return false; }

        ComPtr<ID3D11VertexShader> vs = default;
        if (_device.CreateVertexShader(vsCode->GetBufferPointer(), vsCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vs) < 0)
        { Log("lights CreateVertexShader failed"); return false; }
        _lightVs = vs;

        ComPtr<ID3D11PixelShader> ps = default;
        if (_device.CreatePixelShader(psCode->GetBufferPointer(), psCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref ps) < 0)
        { Log("lights CreatePixelShader failed"); return false; }
        _lightPs = ps;

        // POSITION + NORMAL + TEXCOORD0 read out of the SAME fat vertex the scene pass draws
        // (PreviewVertex: +0 / +12 / +40), so the overlay covers the identical geometry by construction.
        var semPos = System.Text.Encoding.ASCII.GetBytes("POSITION\0");
        var semNrm = System.Text.Encoding.ASCII.GetBytes("NORMAL\0");
        var semUv = System.Text.Encoding.ASCII.GetBytes("TEXCOORD\0");
        fixed (byte* p0 = semPos)
        fixed (byte* p1 = semNrm)
        fixed (byte* p2 = semUv)
        {
            var els = stackalloc InputElementDesc[3];
            els[0] = new InputElementDesc
            {
                SemanticName = p0, SemanticIndex = 0, Format = Format.FormatR32G32B32Float,
                InputSlot = 0, AlignedByteOffset = 0,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            els[1] = new InputElementDesc
            {
                SemanticName = p1, SemanticIndex = 0, Format = Format.FormatR32G32B32Float,
                InputSlot = 0, AlignedByteOffset = 12,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            els[2] = new InputElementDesc
            {
                SemanticName = p2, SemanticIndex = 0, Format = Format.FormatR32G32Float,
                InputSlot = 0, AlignedByteOffset = 40,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            ComPtr<ID3D11InputLayout> layout = default;
            if (_device.CreateInputLayout(els, 3, vsCode->GetBufferPointer(), vsCode->GetBufferSize(), ref layout) < 0)
            { Log("lights CreateInputLayout failed"); return false; }
            _lightLayout = layout;
        }

        var cbDesc = new BufferDesc
        {
            ByteWidth = 16 * 4 + 16 + MaxLightsPerDraw * 16 * 2,   // float4x4 + float4 + 2 arrays = 2128
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> cb = default;
        if (_device.CreateBuffer(in cbDesc, null, ref cb) < 0) { Log("lights cbuffer failed"); return false; }
        _lightCb = cb;

        // Depth test LESS_EQUAL against what the scene wrote, write OFF: light lands ON surfaces and is
        // occluded by nearer ones, and the pass never disturbs depth for the editor furniture after it.
        var dsd = new DepthStencilDesc
        {
            DepthEnable = 1, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.LessEqual,
        };
        ComPtr<ID3D11DepthStencilState> ds = default;
        _device.CreateDepthStencilState(in dsd, ref ds);
        _lightDepth = ds;

        // Strict additive (ONE/ONE) - the shader's output IS the light term to add. Alpha keeps the
        // destination (source contributes zero), so the readback's alpha stays what the scene wrote.
        var bd = new BlendDesc();
        bd.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = Blend.One, DestBlend = Blend.One, BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.Zero, DestBlendAlpha = Blend.One, BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ComPtr<ID3D11BlendState> bs = default;
        _device.CreateBlendState(in bd, ref bs);
        _lightBlend = bs;

        // The 0.5 grey base for slices with no identifiable diffuse (128/255): a stated approximation,
        // not a failure - the pool's shape and brightness stay right, only its texturing is flat.
        var grey = MakeTexture(new byte[] { 128, 128, 128, 255 }, 1, 1);
        if (grey is { } g) _lightGrey = g;

        Log("dynamic light pipeline built");
        return true;
    }

    private bool CompileLight(D3DCompiler compiler, byte* src, int len, string entry, string target,
        ID3D10Blob** code, ID3D10Blob** errs)
    {
        var e = System.Text.Encoding.ASCII.GetBytes(entry + "\0");
        var t = System.Text.Encoding.ASCII.GetBytes(target + "\0");
        fixed (byte* ep = e)
        fixed (byte* tp = t)
            if (compiler.Compile(src, (nuint)len, (byte*)null, null, (ID3DInclude*)null,
                    ep, tp, 0u, 0u, code, errs) < 0 || *code is null)
            { Log($"lights {entry} failed to compile"); return false; }
        return true;
    }

    /// <summary>The slice's diffuse for the overlay's base term. Exact static-mesh names first, then any
    /// texture whose reflected name says DIFFUSE (they are all plain Texture2D - the only array SRV a map
    /// material binds is TERRAIN_BLEND_SharedTexture, which never matches). Terrain-blend and other
    /// multi-layer grounds have no single diffuse and get the grey stand-in.</summary>
    private ComPtr<ID3D11ShaderResourceView> LightBaseTexture(PreviewMaterial mat)
    {
        if (mat.Textures.TryGetValue("DiffuseTexture__TX", out var d) && d.Handle is not null) return d;
        if (mat.Textures.TryGetValue("BAKED_DIFFUSE_TEXTURE__TX", out var b) && b.Handle is not null) return b;
        foreach (var kv in mat.Textures)
            if (kv.Value.Handle is not null && kv.Key.Contains("DIFFUSE", StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return _lightGrey;
    }

    /// <summary>Draw the point-light overlay over the finished scene. Returns the number of draws.
    /// Skips itself entirely when the toggle is off or there is nothing to light with or onto.</summary>
    private int DrawDynamicLights(PreviewSettings s, Matrix4x4 view, Matrix4x4 proj, Vector4[] planes)
    {
        DynamicLightDraws = 0;
        var lights = s.DynamicLights;
        // The GL gate exactly: activeLights = enabled && uploaded ? count : 0 (ViewportMeshRenderer).
        if (!s.DynamicLightsEnabled || lights is null || lights.Count == 0) return 0;
        if (_vb.Handle is null || _indexCount == 0) return 0;
        if (!EnsureDynamicLights()) return 0;

        // The same clamps the GL setters apply (SetLightIntensity/RadiusScale/FalloffSoftness/
        // PositionScale/PositionScaleXZ), so a degenerate slider value cannot make the two viewports
        // disagree about the same lights.
        float intensity = Math.Clamp(s.DynamicLightIntensity, 0f, 8f);
        if (intensity <= 0f) return 0;
        float radiusScale = Math.Clamp(s.DynamicLightRadiusScale, 0.01f, 40f);
        float softness = Math.Clamp(s.DynamicLightFalloffSoftness, 0f, 1f);
        float spread = Math.Clamp(s.DynamicLightPositionScale, 0.05f, 20f);
        var scaleXZ = Vector2.Clamp(s.DynamicLightPositionScaleXZ, new Vector2(0.05f), new Vector2(20f));
        var offset = s.DynamicLightPositionOffset;

        // view already carries the X mirror (applied at the top of RenderFrame), and light positions stay
        // in the same unmirrored world space as the vertices - the same relationship the GL path has.
        var mvp = Matrix4x4.Multiply(view, proj);

        uint stride = PreviewVertex.SizeInBytes, off = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _vb, in stride, in off);
        _ctx.IASetIndexBuffer(_ib, Format.FormatR32Uint, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        _ctx.IASetInputLayout(_lightLayout);
        _ctx.VSSetShader(_lightVs, null, 0);
        _ctx.PSSetShader(_lightPs, null, 0);
        _ctx.VSSetConstantBuffers(0, 1, ref _lightCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _lightCb);
        _ctx.PSSetSamplers(0, 1, ref _linearWrap);
        _ctx.RSSetState(_raster);   // cull-off scene state: depth already resolves what is visible
        _ctx.OMSetDepthStencilState(_lightDepth, 0);
        var factor = stackalloc float[4] { 0f, 0f, 0f, 0f };
        _ctx.OMSetBlendState(_lightBlend, factor, 0xFFFFFFFF);

        int draws = 0;
        int floats = 16 + 4 + MaxLightsPerDraw * 8;
        if (_lightCbData.Length != floats) _lightCbData = new float[floats];
        int batches = LightBatchCount(lights.Count);
        for (int batch = 0; batch < batches; batch++)
        {
            int first = batch * MaxLightsPerDraw;
            int n = Math.Min(MaxLightsPerDraw, lights.Count - first);
            FillLightCb(mvp, lights, first, n, intensity, softness, radiusScale, spread, scaleXZ, offset);

            foreach (var mat in _materials)
            {
                // Map slices only: everything else (particles, mesh emitters, props, ribbons, overlays)
                // reports MapGroupIndex -1 and owns its own lighting story.
                if (mat.MapGroupIndex < 0 || !mat.Visible || mat.UsesDynamicMesh) continue;
                if (mat.MeshGeometryId is not null || mat.RibbonId is not null
                    || mat.DistortionStrength is not null) continue;
                if (mat.Bounds is { } bb && !FrustumContains(planes, bb.Min, bb.Max)) continue;
                uint count = mat.IndexCount < 0 ? (uint)_indexCount : (uint)mat.IndexCount;
                if (count == 0) continue;

                var srv = LightBaseTexture(mat);
                _ctx.PSSetShaderResources(0, 1, ref srv);
                _ctx.DrawIndexed(count, (uint)Math.Max(0, mat.StartIndex), 0);
                draws++;
            }
        }

        // Unbind t0 - the next frame's material pass binds per material without clearing unused slots.
        var none = stackalloc ID3D11ShaderResourceView*[1];
        none[0] = null;
        _ctx.PSSetShaderResources(0, 1, none);

        DynamicLightDraws = draws;
        return draws;
    }

    private void FillLightCb(Matrix4x4 mvp, IReadOnlyList<ReyEngine.Formats.Lighting.PointLight> lights,
        int first, int n, float intensity, float softness, float radiusScale,
        float spread, Vector2 scaleXZ, Vector2 offset)
    {
        var d = _lightCbData;
        Array.Clear(d);
        d[0] = mvp.M11; d[1] = mvp.M12; d[2] = mvp.M13; d[3] = mvp.M14;
        d[4] = mvp.M21; d[5] = mvp.M22; d[6] = mvp.M23; d[7] = mvp.M24;
        d[8] = mvp.M31; d[9] = mvp.M32; d[10] = mvp.M33; d[11] = mvp.M34;
        d[12] = mvp.M41; d[13] = mvp.M42; d[14] = mvp.M43; d[15] = mvp.M44;
        d[16] = n; d[17] = intensity; d[18] = softness; d[19] = 0f;
        for (int i = 0; i < n; i++)
        {
            var l = lights[first + i];
            // THE shared fit formula - scale XZ about the world origin, offset after, height untouched.
            var p = Formats.Baking.BakeLighting.FitPosition(l.Position, spread, scaleXZ, offset);
            int at = 20 + i * 4;
            d[at + 0] = p.X; d[at + 1] = p.Y; d[at + 2] = p.Z; d[at + 3] = l.Radius * radiusScale;
            at = 20 + MaxLightsPerDraw * 4 + i * 4;
            d[at + 0] = l.Color.X; d[at + 1] = l.Color.Y; d[at + 2] = l.Color.Z;
            // The same per-light strength clamp the GL upload applies (SetPointLights).
            d[at + 3] = Math.Clamp(l.Intensity, 0f, 64f);
        }

        MappedSubresource m = default;
        if (_ctx.Map(_lightCb, 0, Map.WriteDiscard, 0, ref m) < 0) return;
        fixed (float* src = d)
            System.Buffer.MemoryCopy(src, m.PData, d.Length * sizeof(float), d.Length * sizeof(float));
        _ctx.Unmap(_lightCb, 0);
    }

    private void DisposeDynamicLights()
    {
        _lightVs.Dispose(); _lightPs.Dispose(); _lightLayout.Dispose();
        _lightCb.Dispose(); _lightDepth.Dispose(); _lightBlend.Dispose();
        _lightGrey.Dispose();
    }
}
