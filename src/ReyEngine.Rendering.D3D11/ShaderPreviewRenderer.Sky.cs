using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.DXGI;

namespace ReyEngine.Rendering.D3D11;

// M362 (v0.4.0 item 3b): the D3D11 half of ReyEngine.Rendering.SkyboxRenderer, so switching renderers no
// longer switches the sky off. Same three sources the GL path has carried since M122:
//
//   Cubemap  - League's DDS cubemaps, a TextureCube on a unit cube.
//   Equirect - one 2D sky texture sampled by view DIRECTION on that same cube, so the cube's own seams
//              never show.
//   Mesh     - authored domes (.scb/.sco/.skn) that bring their own UVs.
//
// THE DEPTH STATE IS MEASURED, NOT ASSUMED - the roadmap flagged it as the one thing to settle before
// writing this. SkyboxRenderer.Render disables the depth test, disables depth WRITES, disables blending and
// disables culling, and draws FIRST, with gl_Position = p.xyww pinning depth to the far plane. This mirrors
// all five of those decisions rather than the plausible alternative (LEQUAL with writes off, drawn last).
//
// Built as its own pipeline on the overlay's pattern - lazy compile remembered on failure, own
// shaders/layouts/cbuffer/states - because the sky shares nothing with the scene pass: different vertex
// format, different matrices, no lighting. Two HLSL entry pairs, not one, because the dome carries UVs and
// the cube does not, so their input layouts genuinely differ.
public sealed unsafe partial class ShaderPreviewRenderer
{
    private ComPtr<ID3D11VertexShader> _skyDirVs, _skyMeshVs;
    private ComPtr<ID3D11PixelShader> _skyDirPs, _skyMeshPs;
    private ComPtr<ID3D11InputLayout> _skyDirLayout, _skyMeshLayout;
    private ComPtr<ID3D11Buffer> _skyCb, _skyCubeVb, _skyMeshVb, _skyMeshIb;
    private ComPtr<ID3D11SamplerState> _skySamp;
    private ComPtr<ID3D11RasterizerState> _skyRaster;
    private ComPtr<ID3D11BlendState> _skyBlend;
    private ComPtr<ID3D11DepthStencilState> _skyDepth;
    private ComPtr<ID3D11ShaderResourceView> _skyCubeSrv, _skyFlatSrv;
    private bool _skyTried;

    /// <summary>-1 none, 0 cubemap, 1 equirect, 2 mesh. The same numbering the GL renderer uses, and the
    /// same numbering the shader branches on.</summary>
    private int _skyMode = -1;
    private int _skyIndexCount, _skyVertexCount;

    /// <summary>True once a sky source has been set. Hosts that never call a Set* leave the sky off and pay
    /// nothing - <see cref="DrawSky"/> returns before it even compiles.</summary>
    public bool HasSkybox => _skyMode >= 0;

    /// <summary>Set by <see cref="DrawSky"/> so a host can report what actually drew rather than what was
    /// requested.</summary>
    public int SkyDraws { get; private set; }

    // ASCII only. This string is handed to the compiler as raw ASCII bytes, so a stray non-ASCII character
    // in even a comment corrupts the source it sees (the same trap that cost a blank GL viewport in M117b).
    private const string SkyHlsl = @"
cbuffer SkyCB : register(b0)
{
    row_major float4x4 gMvp;   // rotation-only view * projection - no translation, so the sky never moves
    float4 gMode;              // .x: 0 cubemap, 1 equirect
};

TextureCube  gCube : register(t0);
Texture2D    gFlat : register(t1);
SamplerState gSamp : register(s0);

struct DirOut { float4 pos : SV_Position; float3 dir : TEXCOORD0; };

DirOut vsdir(float3 p : POSITION)
{
    DirOut o;
    float4 c = mul(float4(p, 1.0), gMvp);
    o.pos = c.xyww;   // z/w = 1 after the divide: the far plane, matching the GL path
    o.dir = p;        // the cube's own corner IS the view direction once the view is rotation-only
    return o;
}

float4 psdir(DirOut i) : SV_Target
{
    float3 d = normalize(i.dir);
    if (gMode.x < 0.5) return float4(gCube.Sample(gSamp, d).rgb, 1.0);
    float u = atan2(d.x, d.z) / 6.2831853 + 0.5;
    float v = acos(clamp(d.y, -1.0, 1.0)) / 3.1415927;
    // SampleLevel, not Sample. u is built from atan2, so it jumps a full turn across the wrap seam; the
    // hardware derivative of that jump is enormous and selects the smallest mip, which paints the seam as
    // a blurred vertical stripe. The GL path never had this because it uploads no mip chain at all, so
    // pinning level 0 both removes the artifact and IS the GL behaviour.
    return float4(gFlat.SampleLevel(gSamp, float2(u, v), 0).rgb, 1.0);
}

struct MeshIn  { float3 pos : POSITION; float2 uv : TEXCOORD0; };
struct MeshOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

MeshOut vsmesh(MeshIn i)
{
    MeshOut o;
    float4 c = mul(float4(i.pos, 1.0), gMvp);
    o.pos = c.xyww;
    o.uv = i.uv;
    return o;
}

float4 psmesh(MeshOut i) : SV_Target { return float4(gFlat.Sample(gSamp, i.uv).rgb, 1.0); }
";

    private bool EnsureSky()
    {
        if (_skyTried) return _skyDirVs.Handle is not null;
        _skyTried = true;

        var src = System.Text.Encoding.ASCII.GetBytes(SkyHlsl);
        ID3D10Blob* dirVsCode = null, dirPsCode = null, meshVsCode = null, meshPsCode = null, errs = null;
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* sp = src)
            {
                if (!CompileSky(compiler, sp, src.Length, "vsdir", "vs_5_0", &dirVsCode, &errs)) return false;
                if (!CompileSky(compiler, sp, src.Length, "psdir", "ps_5_0", &dirPsCode, &errs)) return false;
                if (!CompileSky(compiler, sp, src.Length, "vsmesh", "vs_5_0", &meshVsCode, &errs)) return false;
                if (!CompileSky(compiler, sp, src.Length, "psmesh", "ps_5_0", &meshPsCode, &errs)) return false;
            }
        }
        catch (Exception ex) { Log("sky: the HLSL compiler is unavailable: " + ex.Message); return false; }

        ComPtr<ID3D11VertexShader> dvs = default, mvs = default;
        ComPtr<ID3D11PixelShader> dps = default, mps = default;
        if (_device.CreateVertexShader(dirVsCode->GetBufferPointer(), dirVsCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref dvs) < 0) { Log("sky CreateVertexShader (dir) failed"); return false; }
        if (_device.CreatePixelShader(dirPsCode->GetBufferPointer(), dirPsCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref dps) < 0) { Log("sky CreatePixelShader (dir) failed"); return false; }
        if (_device.CreateVertexShader(meshVsCode->GetBufferPointer(), meshVsCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref mvs) < 0) { Log("sky CreateVertexShader (mesh) failed"); return false; }
        if (_device.CreatePixelShader(meshPsCode->GetBufferPointer(), meshPsCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref mps) < 0) { Log("sky CreatePixelShader (mesh) failed"); return false; }
        _skyDirVs = dvs; _skyDirPs = dps; _skyMeshVs = mvs; _skyMeshPs = mps;

        var pos = System.Text.Encoding.ASCII.GetBytes("POSITION\0");
        var uv = System.Text.Encoding.ASCII.GetBytes("TEXCOORD\0");
        fixed (byte* pp = pos)
        fixed (byte* up = uv)
        {
            var posEl = new InputElementDesc
            {
                SemanticName = pp, SemanticIndex = 0, Format = Format.FormatR32G32B32Float,
                InputSlot = 0, AlignedByteOffset = 0,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            ComPtr<ID3D11InputLayout> dl = default;
            if (_device.CreateInputLayout(&posEl, 1, dirVsCode->GetBufferPointer(), dirVsCode->GetBufferSize(), ref dl) < 0)
            { Log("sky CreateInputLayout (dir) failed"); return false; }
            _skyDirLayout = dl;

            var els = stackalloc InputElementDesc[2];
            els[0] = posEl;
            els[1] = new InputElementDesc
            {
                SemanticName = up, SemanticIndex = 0, Format = Format.FormatR32G32Float,
                InputSlot = 0, AlignedByteOffset = 12,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            ComPtr<ID3D11InputLayout> ml = default;
            if (_device.CreateInputLayout(els, 2, meshVsCode->GetBufferPointer(), meshVsCode->GetBufferSize(), ref ml) < 0)
            { Log("sky CreateInputLayout (mesh) failed"); return false; }
            _skyMeshLayout = ml;
        }

        var cbDesc = new BufferDesc
        {
            ByteWidth = 80,                      // float4x4 + float4, the same shape the overlay uses
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> cb = default;
        if (_device.CreateBuffer(in cbDesc, null, ref cb) < 0) { Log("sky cbuffer failed"); return false; }
        _skyCb = cb;

        // Wrap on U because the equirect's u wraps a full turn and the seam must join; clamp on V so the
        // poles do not sample across. Cube sampling resolves faces from the direction and does not consult
        // these modes, so one sampler serves all three sources.
        var sd = new SamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue,
        };
        ComPtr<ID3D11SamplerState> samp = default;
        _device.CreateSamplerState(in sd, ref samp);
        _skySamp = samp;

        // Solid and unculled, matching GL's Disable(CullFace) - the camera sits INSIDE the cube, so half its
        // triangles face away and culling either one winding would punch holes in the sky.
        //
        // DepthClipEnable off is deliberate: xyww puts z exactly ON the far-plane clip boundary, where
        // rounding decides whether a pixel survives. Nothing here tests depth, so clipping against it can
        // only ever remove sky that should be there.
        var rs = new RasterizerDesc
        {
            FillMode = FillMode.Solid, CullMode = CullMode.None,
            FrontCounterClockwise = 0, DepthClipEnable = 0,
        };
        ComPtr<ID3D11RasterizerState> raster = default;
        _device.CreateRasterizerState(in rs, ref raster);
        _skyRaster = raster;

        var bd = new BlendDesc();
        bd.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 0, RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ComPtr<ID3D11BlendState> blend = default;
        _device.CreateBlendState(in bd, ref blend);
        _skyBlend = blend;

        // DepthEnable = 0 disables the test AND the write in D3D11, which is both halves of what GL spells
        // out as Disable(DepthTest) + DepthMask(false).
        var dsd = new DepthStencilDesc { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero };
        ComPtr<ID3D11DepthStencilState> ds = default;
        _device.CreateDepthStencilState(in dsd, ref ds);
        _skyDepth = ds;

        // 36 positions, the same unit cube the GL path uploads. Positions only - the pixel shader works
        // from the interpolated direction, so normals and UVs would be dead weight.
        float[] v =
        {
            -1,-1,-1,  1,-1,-1,  1, 1,-1,  -1,-1,-1,  1, 1,-1, -1, 1,-1,   // -Z
            -1,-1, 1,  1, 1, 1,  1,-1, 1,  -1,-1, 1, -1, 1, 1,  1, 1, 1,   // +Z
            -1,-1,-1, -1, 1,-1, -1, 1, 1,  -1,-1,-1, -1, 1, 1, -1,-1, 1,   // -X
             1,-1,-1,  1, 1, 1,  1, 1,-1,   1,-1,-1,  1,-1, 1,  1, 1, 1,   // +X
            -1, 1,-1,  1, 1,-1,  1, 1, 1,  -1, 1,-1,  1, 1, 1, -1, 1, 1,   // +Y
            -1,-1,-1, -1,-1, 1,  1,-1, 1,  -1,-1,-1,  1,-1, 1,  1,-1,-1,   // -Y
        };
        var vbDesc = new BufferDesc
        {
            ByteWidth = (uint)(v.Length * sizeof(float)),
            Usage = Usage.Immutable, BindFlags = (uint)BindFlag.VertexBuffer,
        };
        ComPtr<ID3D11Buffer> vb = default;
        fixed (float* p = v)
        {
            var sub = new SubresourceData { PSysMem = p };
            if (_device.CreateBuffer(in vbDesc, in sub, ref vb) < 0) { Log("sky cube vertex buffer failed"); return false; }
        }
        _skyCubeVb = vb;
        return true;
    }

    private bool CompileSky(D3DCompiler compiler, byte* src, int len, string entry, string target,
        ID3D10Blob** code, ID3D10Blob** errs)
    {
        var e = System.Text.Encoding.ASCII.GetBytes(entry + "\0");
        var t = System.Text.Encoding.ASCII.GetBytes(target + "\0");
        fixed (byte* ep = e)
        fixed (byte* tp = t)
            if (compiler.Compile(src, (nuint)len, (byte*)null, null, (ID3DInclude*)null,
                    ep, tp, 0u, 0u, code, errs) < 0 || *code is null)
            { Log($"sky {entry} failed to compile"); return false; }
        return true;
    }

    /// <summary>Drop whatever sky is loaded. Safe before <see cref="EnsureSky"/> has ever run.</summary>
    public void ClearSky()
    {
        _skyMode = -1;
        _skyIndexCount = 0; _skyVertexCount = 0;
        _skyCubeSrv.Dispose(); _skyCubeSrv = default;
        _skyFlatSrv.Dispose(); _skyFlatSrv = default;
        _skyMeshVb.Dispose(); _skyMeshVb = default;
        _skyMeshIb.Dispose(); _skyMeshIb = default;
    }

    /// <summary>Six RGBA faces in +X -X +Y -Y +Z -Z order - D3D11's cube slice order, which is also the
    /// order <c>CubemapImage.Faces</c> holds and the order the GL path binds them in.</summary>
    public bool SetSkyCubemap(IReadOnlyList<byte[]> faces, int faceSize)
    {
        if (!EnsureSky()) return false;
        if (faces.Count < 6 || faceSize <= 0) { Log($"sky cubemap rejected: {faces.Count} face(s) at {faceSize}px"); return false; }
        int need = faceSize * faceSize * 4;
        for (int f = 0; f < 6; f++)
            if (faces[f] is null || faces[f].Length < need)
            { Log($"sky cubemap rejected: face {f} needs {need} bytes, got {faces[f]?.Length ?? 0}"); return false; }

        ClearSky();
        var desc = new Texture2DDesc
        {
            Width = (uint)faceSize, Height = (uint)faceSize, MipLevels = 1, ArraySize = 6,
            Format = Format.FormatR8G8B8A8Unorm, SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default, BindFlags = (uint)BindFlag.ShaderResource,
            MiscFlags = (uint)ResourceMiscFlag.Texturecube,
        };
        ComPtr<ID3D11Texture2D> tex = default;
        int hr = _device.CreateTexture2D(in desc, null, ref tex);
        if (hr < 0) { Log($"sky CreateTexture2D (cube) failed 0x{hr:X8}"); return false; }

        // Uploaded per slice rather than as initial data: six managed arrays would each need pinning, and
        // UpdateSubresource is already how this renderer fills its other array textures.
        for (uint f = 0; f < 6; f++)
            fixed (byte* p = faces[(int)f])
                _ctx.UpdateSubresource(tex, f, (Box*)null, p, (uint)(faceSize * 4), 0u);

        var srvDesc = new ShaderResourceViewDesc
        {
            Format = desc.Format,
            ViewDimension = (D3DSrvDimension)9,
            Anonymous = new ShaderResourceViewDescUnion
            {
                TextureCube = new TexcubeSrv { MostDetailedMip = 0, MipLevels = 1 },
            },
        };
        ComPtr<ID3D11ShaderResourceView> srv = default;
        hr = _device.CreateShaderResourceView(tex, in srvDesc, ref srv);
        tex.Dispose();
        if (hr < 0) { Log($"sky CreateShaderResourceView (cube) failed 0x{hr:X8}"); return false; }
        _skyCubeSrv = srv;
        _skyMode = 0;
        return true;
    }

    /// <summary>A single sky texture sampled by view direction. Not projected onto the cube's faces, so the
    /// cube's corners never show as seams.</summary>
    public bool SetSkyEquirect(byte[] rgba, int width, int height)
    {
        if (!EnsureSky()) return false;
        ClearSky();
        var srv = MakeTexture(rgba, width, height);
        if (srv is null) { Log("sky equirect texture could not be created"); return false; }
        _skyFlatSrv = srv.Value;
        _skyMode = 1;
        return true;
    }

    /// <summary>An authored dome with its own UVs. A null texture draws white, matching the GL path's
    /// stand-in rather than dropping the dome entirely.</summary>
    public bool SetSkyMesh(float[] positions, float[] uvs, uint[] indices, byte[]? rgba, int width, int height)
    {
        if (!EnsureSky()) return false;
        int verts = positions.Length / 3;
        if (verts <= 0) { Log("sky mesh rejected: no vertices"); return false; }

        ClearSky();
        var inter = new float[verts * 5];
        for (int i = 0; i < verts; i++)
        {
            inter[i * 5 + 0] = positions[i * 3 + 0];
            inter[i * 5 + 1] = positions[i * 3 + 1];
            inter[i * 5 + 2] = positions[i * 3 + 2];
            inter[i * 5 + 3] = i * 2 + 1 < uvs.Length ? uvs[i * 2] : 0f;
            inter[i * 5 + 4] = i * 2 + 1 < uvs.Length ? uvs[i * 2 + 1] : 0f;
        }

        var vbDesc = new BufferDesc
        {
            ByteWidth = (uint)(inter.Length * sizeof(float)),
            Usage = Usage.Immutable, BindFlags = (uint)BindFlag.VertexBuffer,
        };
        ComPtr<ID3D11Buffer> vb = default;
        fixed (float* p = inter)
        {
            var sub = new SubresourceData { PSysMem = p };
            if (_device.CreateBuffer(in vbDesc, in sub, ref vb) < 0) { Log("sky mesh vertex buffer failed"); return false; }
        }
        _skyMeshVb = vb;
        _skyVertexCount = verts;

        if (indices.Length > 0)
        {
            var ibDesc = new BufferDesc
            {
                ByteWidth = (uint)(indices.Length * sizeof(uint)),
                Usage = Usage.Immutable, BindFlags = (uint)BindFlag.IndexBuffer,
            };
            ComPtr<ID3D11Buffer> ib = default;
            fixed (uint* p = indices)
            {
                var sub = new SubresourceData { PSysMem = p };
                if (_device.CreateBuffer(in ibDesc, in sub, ref ib) < 0) { Log("sky mesh index buffer failed"); return false; }
            }
            _skyMeshIb = ib;
            _skyIndexCount = indices.Length;
        }

        var srv = rgba is not null && width > 0 && height > 0
            ? MakeTexture(rgba, width, height)
            : MakeTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
        if (srv is not null) _skyFlatSrv = srv.Value;
        _skyMode = 2;
        return true;
    }

    private void SetSkyCb(Matrix4x4 mvp, float mode)
    {
        var bytes = new byte[80];
        var m = new[]
        {
            mvp.M11, mvp.M12, mvp.M13, mvp.M14, mvp.M21, mvp.M22, mvp.M23, mvp.M24,
            mvp.M31, mvp.M32, mvp.M33, mvp.M34, mvp.M41, mvp.M42, mvp.M43, mvp.M44,
            mode, 0f, 0f, 0f,
        };
        System.Buffer.BlockCopy(m, 0, bytes, 0, 80);
        Upload(_skyCb, bytes, 80);
    }

    /// <summary>Draw the sky. <paramref name="view"/> is the full camera view matrix - the translation is
    /// stripped here rather than by the caller, so no host can forget and get a sky that parallaxes.
    /// Call FIRST, before any scene geometry: nothing here writes depth, so the scene simply paints over
    /// it.</summary>
    public int DrawSky(Matrix4x4 view, Matrix4x4 proj)
    {
        SkyDraws = 0;
        if (_skyMode < 0 || !EnsureSky()) return 0;

        var viewRot = view;
        viewRot.M41 = 0f; viewRot.M42 = 0f; viewRot.M43 = 0f;
        SetSkyCb(Matrix4x4.Multiply(viewRot, proj), _skyMode == 1 ? 1f : 0f);

        _ctx.RSSetState(_skyRaster);
        _ctx.OMSetBlendState(_skyBlend, stackalloc float[] { 0f, 0f, 0f, 0f }, 0xFFFFFFFF);
        _ctx.OMSetDepthStencilState(_skyDepth, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        _ctx.VSSetConstantBuffers(0, 1, ref _skyCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _skyCb);
        _ctx.PSSetSamplers(0, 1, ref _skySamp);

        uint offset = 0;
        if (_skyMode == 2)
        {
            if (_skyMeshVb.Handle is null || _skyVertexCount == 0) return 0;
            uint stride = 5 * sizeof(float);
            _ctx.IASetInputLayout(_skyMeshLayout);
            _ctx.VSSetShader(_skyMeshVs, null, 0);
            _ctx.PSSetShader(_skyMeshPs, null, 0);
            _ctx.IASetVertexBuffers(0, 1, ref _skyMeshVb, in stride, in offset);
            BindSkyTextures(cube: false);
            if (_skyIndexCount > 0 && _skyMeshIb.Handle is not null)
            {
                _ctx.IASetIndexBuffer(_skyMeshIb, Format.FormatR32Uint, 0);
                _ctx.DrawIndexed((uint)_skyIndexCount, 0, 0);
            }
            else _ctx.Draw((uint)_skyVertexCount, 0);
        }
        else
        {
            if (_skyCubeVb.Handle is null) return 0;
            uint stride = 3 * sizeof(float);
            _ctx.IASetInputLayout(_skyDirLayout);
            _ctx.VSSetShader(_skyDirVs, null, 0);
            _ctx.PSSetShader(_skyDirPs, null, 0);
            _ctx.IASetVertexBuffers(0, 1, ref _skyCubeVb, in stride, in offset);
            BindSkyTextures(cube: _skyMode == 0);
            _ctx.Draw(36, 0);
        }

        // Unbind: t0/t1 stay live otherwise, and the scene pass binds its own textures per material without
        // clearing slots it does not use.
        var none = stackalloc ID3D11ShaderResourceView*[2];
        none[0] = null; none[1] = null;
        _ctx.PSSetShaderResources(0, 2, none);

        SkyDraws = 1;
        return 1;
    }

    private void BindSkyTextures(bool cube)
    {
        var srvs = stackalloc ID3D11ShaderResourceView*[2];
        srvs[0] = cube ? _skyCubeSrv.Handle : null;
        srvs[1] = cube ? null : _skyFlatSrv.Handle;
        _ctx.PSSetShaderResources(0, 2, srvs);
    }

    private void DisposeSky()
    {
        ClearSky();
        _skyDirVs.Dispose(); _skyDirPs.Dispose(); _skyMeshVs.Dispose(); _skyMeshPs.Dispose();
        _skyDirLayout.Dispose(); _skyMeshLayout.Dispose();
        _skyCb.Dispose(); _skyCubeVb.Dispose();
        _skySamp.Dispose(); _skyRaster.Dispose(); _skyBlend.Dispose(); _skyDepth.Dispose();
    }
}
