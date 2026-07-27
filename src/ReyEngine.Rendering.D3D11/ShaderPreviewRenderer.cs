using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ReyEngine.Formats.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.DXGI;
// our namespace is itself called D3D11, which shadows Silk's API type of the same name
using SilkD3D11 = Silk.NET.Direct3D11.D3D11;

namespace ReyEngine.Rendering.D3D11;

/// <summary>Camera / lighting / render knobs the preview drives the shader with.</summary>
public sealed class PreviewSettings
{
    public float Yaw = 0.6f, Pitch = 0.4f, Distance = 3.2f;
    public float Fov = 0.9f;
    public Vector3 SunDirection = Vector3.Normalize(new Vector3(-0.4f, -0.8f, -0.45f));
    public Vector4 SunColor = new(1f, 0.97f, 0.9f, 1f);
    public Vector4 ClearColor = new(0.08f, 0.09f, 0.11f, 1f);
    public float TimeSeconds;

    public bool Wireframe;
    public bool CullBackFaces = true;
    public bool DepthTest = true;
    public bool AlphaBlend;

    /// <summary>HLSL packs cbuffer matrices column-major by default, so a row-major
    /// <see cref="Matrix4x4"/> is conventionally transposed before upload. Riot's shaders were compiled
    /// with the default packing, but whether they do <c>mul(v, M)</c> or <c>mul(M, v)</c> is not recorded
    /// anywhere in the bytecode we read — so this is exposed as an A/B rather than guessed silently.
    /// If the mesh renders inside-out, squashed, or vanishes, flip this first.</summary>
    public bool TransposeMatrices = true;

    /// <summary>Draw with ReyEngine's own shading model instead of Riot's pixel shader, for the A/B.</summary>
    public bool UseComparisonShader;
}

/// <summary>Result of trying to bring a shader pair up — every failure carries its own message.</summary>
public sealed class ShaderLoadReport
{
    public bool Success;
    public string? Error;
    public readonly List<string> Steps = new();
    public readonly List<string> Warnings = new();
    /// <summary>Vertex-shader input elements the fat vertex has no real data for.</summary>
    public readonly List<string> UnmatchedInputs = new();
    /// <summary>Reflected textures with nothing bound — these sample as opaque white.</summary>
    public readonly List<string> UnboundTextures = new();
    /// <summary>Reflected, USED constants nothing filled in — they upload as zero.</summary>
    public readonly List<string> UnboundConstants = new();

    public void Step(string s) => Steps.Add(s);
    public void Warn(string s) => Warnings.Add(s);
}

/// <summary>M210: an isolated Direct3D 11 renderer that runs Riot's own compiled shaders.
///
/// <para><b>Why offscreen + readback rather than a swapchain.</b> Avalonia offers <c>OpenGlControlBase</c>
/// and no D3D11 equivalent. Presenting a real swapchain means either a <c>NativeControlHost</c> child HWND,
/// which sits above the Avalonia compositor and would draw over every toolbar and overlay in the window, or
/// shared-texture interop back into the GL context. Both were identified as the deciding cost in the D3D11
/// spike. This renderer sidesteps both: it draws into an offscreen render target, copies to a staging
/// texture, and hands back BGRA bytes the UI blits into a normal bitmap. Slower than a swapchain and
/// completely irrelevant at preview sizes, with no compositor conflict at all.</para>
///
/// <para>Everything about the pipeline is driven by what the shader's own DXBC declares — constant-buffer
/// slots and byte offsets, texture and sampler registers, and the vertex input signature. Nothing is
/// hardcoded per shader, because nothing is known per shader.</para>
/// </summary>
public sealed unsafe class ShaderPreviewRenderer : IDisposable
{
    private SilkD3D11? _d3d;
    private ComPtr<ID3D11Device> _device;
    private ComPtr<ID3D11DeviceContext> _ctx;

    private ComPtr<ID3D11VertexShader> _vs;
    private ComPtr<ID3D11PixelShader> _ps;
    private ComPtr<ID3D11PixelShader> _comparePs;
    private ComPtr<ID3D11InputLayout> _layout;
    private ComPtr<ID3D11Buffer> _vb, _ib;
    private int _indexCount;

    private ComPtr<ID3D11Texture2D> _rt, _stage, _depth;
    private ComPtr<ID3D11RenderTargetView> _rtv;
    private ComPtr<ID3D11DepthStencilView> _dsv;
    private int _width, _height;

    private ComPtr<ID3D11SamplerState> _linearWrap, _linearClamp;
    private ComPtr<ID3D11RasterizerState> _raster;
    private ComPtr<ID3D11BlendState> _blend;
    private ComPtr<ID3D11DepthStencilState> _depthState;

    // one D3D buffer per reflected cbuffer, per stage, keyed by bind slot
    private readonly Dictionary<int, ComPtr<ID3D11Buffer>> _vsCbs = new();
    private readonly Dictionary<int, ComPtr<ID3D11Buffer>> _psCbs = new();
    private DxbcShader? _vsRefl, _psRefl;

    private readonly Dictionary<string, ComPtr<ID3D11ShaderResourceView>> _textures =
        new(StringComparer.OrdinalIgnoreCase);
    private ComPtr<ID3D11ShaderResourceView> _white;

    public string DeviceDescription { get; private set; } = "(no device)";
    public bool IsReady => _device.Handle is not null && _vs.Handle is not null && _ps.Handle is not null;
    public int DrawCalls { get; private set; }
    public double LastFrameMs { get; private set; }
    public PreviewMesh? Mesh { get; private set; }

    /// <summary>Every D3D call that mattered, newest last — the window shows this verbatim.</summary>
    public List<string> Diagnostics { get; } = new();

    private void Log(string s)
    {
        Diagnostics.Add(s);
        if (Diagnostics.Count > 400) Diagnostics.RemoveRange(0, 100);
    }

    // ---------------------------------------------------------------- device

    public bool Initialize(out string? error)
    {
        error = null;
        try
        {
            _d3d = SilkD3D11.GetApi(null);
            var levels = stackalloc D3DFeatureLevel[2] { D3DFeatureLevel.Level111, D3DFeatureLevel.Level110 };
            D3DFeatureLevel got = default;
            ComPtr<ID3D11Device> dev = default;
            ComPtr<ID3D11DeviceContext> ctx = default;

            int hr = _d3d.CreateDevice(default(ComPtr<IDXGIAdapter>), D3DDriverType.Hardware, 0,
                (uint)CreateDeviceFlag.None, levels, 2u, SilkD3D11.SdkVersion, ref dev, ref got, ref ctx);
            if (hr < 0)
            {
                error = $"D3D11CreateDevice failed: 0x{hr:X8}";
                Log(error);
                return false;
            }
            _device = dev;
            _ctx = ctx;
            DeviceDescription = $"D3D11 hardware device, feature level 0x{(uint)got:X}";
            Log(DeviceDescription);

            CreateStaticStates();
            return true;
        }
        catch (Exception ex)
        {
            error = $"D3D11 unavailable: {ex.Message}";
            Log(error);
            return false;
        }
    }

    private void CreateStaticStates()
    {
        var sd = new SamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
            MaxLOD = float.MaxValue, ComparisonFunc = ComparisonFunc.Never,
        };
        ComPtr<ID3D11SamplerState> s1 = default;
        _device.CreateSamplerState(in sd, ref s1);
        _linearWrap = s1;

        sd.AddressU = sd.AddressV = sd.AddressW = TextureAddressMode.Clamp;
        ComPtr<ID3D11SamplerState> s2 = default;
        _device.CreateSamplerState(in sd, ref s2);
        _linearClamp = s2;

        // an opaque white 1x1 stands in for every texture the material does not supply, so a missing
        // binding shows as "unlit but present" rather than as a black or undefined surface
        var px = new byte[] { 255, 255, 255, 255 };
        _white = MakeTexture(px, 1, 1);
    }

    // ---------------------------------------------------------------- shaders

    public ShaderLoadReport LoadShaders(DxbcShader vsRefl, DxbcShader psRefl)
    {
        var r = new ShaderLoadReport();
        if (_device.Handle is null) { r.Error = "no D3D11 device"; return r; }

        ReleaseShaders();
        _vsRefl = vsRefl;
        _psRefl = psRefl;

        // ---- 1. the shader objects themselves
        ComPtr<ID3D11VertexShader> vs = default;
        int hr;
        fixed (byte* p = vsRefl.Bytecode)
            hr = _device.CreateVertexShader(p, (nuint)vsRefl.Bytecode.Length,
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vs);
        if (hr < 0)
        {
            r.Error = $"CreateVertexShader failed: 0x{hr:X8}"
                      + (vsRefl.WasTrimmed ? "" : " (bytecode was NOT trimmed to its declared size - see ShaderCacheReader)");
            Log(r.Error);
            return r;
        }
        _vs = vs;
        r.Step($"CreateVertexShader OK ({vsRefl.ByteSize:n0} bytes, {vsRefl.ShaderModel})");

        ComPtr<ID3D11PixelShader> ps = default;
        fixed (byte* p = psRefl.Bytecode)
            hr = _device.CreatePixelShader(p, (nuint)psRefl.Bytecode.Length,
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref ps);
        if (hr < 0)
        {
            r.Error = $"CreatePixelShader failed: 0x{hr:X8}"
                      + (psRefl.WasTrimmed ? "" : " (bytecode was NOT trimmed to its declared size)");
            Log(r.Error);
            return r;
        }
        _ps = ps;
        r.Step($"CreatePixelShader OK ({psRefl.ByteSize:n0} bytes, {psRefl.ShaderModel})");

        // ---- 2. input layout, generated from the vertex shader's own signature
        if (!CreateInputLayout(vsRefl, r)) return r;

        // ---- 3. one constant buffer per reflected cbuffer, sized as the shader declares
        CreateConstantBuffers(vsRefl, _vsCbs, r, "vs");
        CreateConstantBuffers(psRefl, _psCbs, r, "ps");

        r.Success = true;
        return r;
    }

    private bool CreateInputLayout(DxbcShader vsRefl, ShaderLoadReport r)
    {
        // D3D validates the layout against the WHOLE input signature, so every non-system element needs an
        // entry even when the mesh has no data for it. Unknown semantics alias the zero pad.
        var elems = new List<InputElementDesc>();
        var names = new List<GCHandle>();
        try
        {
            foreach (var e in vsRefl.Inputs)
            {
                if (e.SystemValueType != 0) continue;              // SV_VertexID and friends come from the system

                var nameBytes = System.Text.Encoding.ASCII.GetBytes(e.Semantic + "\0");
                var h = GCHandle.Alloc(nameBytes, GCHandleType.Pinned);
                names.Add(h);

                (uint offset, int comps, bool known) = MapSemantic(e.Semantic, (int)e.Index, e.ComponentCount);
                if (!known) r.UnmatchedInputs.Add(e.FullSemantic);

                elems.Add(new InputElementDesc
                {
                    SemanticName = (byte*)h.AddrOfPinnedObject(),
                    SemanticIndex = e.Index,
                    Format = FormatFor(e.ComponentType, comps),
                    InputSlot = 0,
                    AlignedByteOffset = offset,
                    InputSlotClass = InputClassification.PerVertexData,
                    InstanceDataStepRate = 0,
                });
            }

            if (elems.Count == 0)
            {
                r.Error = "the vertex shader declares no input elements - nothing to build a layout from";
                return false;
            }

            ComPtr<ID3D11InputLayout> layout = default;
            int hr;
            fixed (InputElementDesc* pe = elems.ToArray())
            fixed (byte* pb = vsRefl.Bytecode)
                hr = _device.CreateInputLayout(pe, (uint)elems.Count, pb, (nuint)vsRefl.Bytecode.Length, ref layout);

            if (hr < 0)
            {
                r.Error = $"CreateInputLayout failed: 0x{hr:X8} over {elems.Count} elements "
                          + $"({string.Join(", ", vsRefl.Inputs.Select(i => i.FullSemantic))})";
                Log(r.Error);
                return false;
            }
            _layout = layout;
            r.Step($"CreateInputLayout OK ({elems.Count} elements)");
            if (r.UnmatchedInputs.Count > 0)
                r.Warn($"the test mesh has no data for {string.Join(", ", r.UnmatchedInputs)} - those read as zero");
            return true;
        }
        finally
        {
            foreach (var h in names) h.Free();
        }
    }

    /// <summary>Semantic → byte offset inside <see cref="PreviewVertex"/>.</summary>
    private static (uint Offset, int Components, bool Known) MapSemantic(string semantic, int index, int declared)
        => (semantic.ToUpperInvariant(), index) switch
        {
            ("POSITION", 0) => (0u, 3, true),
            ("NORMAL", 0) => (12u, 3, true),
            ("TANGENT", 0) => (24u, 4, true),
            ("TEXCOORD", 0) => (40u, 2, true),
            ("TEXCOORD", 1) => (48u, 2, true),
            ("TEXCOORD", 2) => (56u, 2, true),
            ("TEXCOORD", 3) => (64u, 2, true),
            ("COLOR", 0) => (72u, 4, true),
            ("BLENDWEIGHT", 0) => (88u, 4, true),
            ("BLENDINDICES", 0) => (104u, 4, true),
            _ => (120u, Math.Max(1, declared), false),           // the zero pad
        };

    private static Format FormatFor(uint componentType, int comps) => componentType switch
    {
        1 => comps switch { 1 => Format.FormatR32Uint, 2 => Format.FormatR32G32Uint, 3 => Format.FormatR32G32B32Uint, _ => Format.FormatR32G32B32A32Uint },
        2 => comps switch { 1 => Format.FormatR32Sint, 2 => Format.FormatR32G32Sint, 3 => Format.FormatR32G32B32Sint, _ => Format.FormatR32G32B32A32Sint },
        _ => comps switch { 1 => Format.FormatR32Float, 2 => Format.FormatR32G32Float, 3 => Format.FormatR32G32B32Float, _ => Format.FormatR32G32B32A32Float },
    };

    private void CreateConstantBuffers(DxbcShader refl, Dictionary<int, ComPtr<ID3D11Buffer>> into,
        ShaderLoadReport r, string stage)
    {
        foreach (var cb in refl.ConstantBuffers)
        {
            if (cb.BindPoint < 0) { r.Warn($"{stage}: cbuffer '{cb.Name}' has no bind point and is skipped"); continue; }
            var desc = new BufferDesc
            {
                ByteWidth = (uint)Math.Max(16, cb.AllocationSize),
                Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.ConstantBuffer,
                CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> buf = default;
            int hr = _device.CreateBuffer(in desc, null, ref buf);
            if (hr < 0) { r.Warn($"{stage}: CreateBuffer for '{cb.Name}' failed 0x{hr:X8}"); continue; }
            into[cb.BindPoint] = buf;
            r.Step($"{stage} cbuffer b{cb.BindPoint} '{cb.Name}' ({cb.Size} bytes, {cb.Variables.Count} vars)");
        }
    }

    /// <summary>M210: ReyEngine's material model as a pixel shader, so the two can be compared directly.
    ///
    /// <para>The HLSL is GENERATED from the Riot vertex shader's own output signature rather than written by
    /// hand. D3D requires a pixel shader's input signature to be a matching subset of the bound vertex
    /// shader's outputs, and every League shader outputs a different interpolant set - so one fixed hand
    /// written comparison shader would link against a few shaders and fail on the rest. Mirroring the
    /// signature makes it link against whatever happens to be loaded.</para>
    ///
    /// <para>The shading is deliberately only what ReyEngine's material path does: diffuse x (lambert sun +
    /// ambient). It is precisely the approximation whose fidelity is in question, so it must not quietly
    /// acquire any of Riot's extra stages.</para></summary>
    public bool BuildComparisonShader(DxbcShader vsRefl, out string? error)
    {
        error = null;
        _comparePs.Dispose();
        _comparePs = default;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Texture2D gDiffuse : register(t0);");
        sb.AppendLine("SamplerState gSamp : register(s0);");
        sb.AppendLine("cbuffer CompareCB : register(b0) { float4 gSunDir; float4 gSunColor; };");
        sb.AppendLine("struct PSIn {");

        string uvField = null, normalField = null;
        int n = 0;
        foreach (var o in vsRefl.Outputs)
        {
            int comps = Math.Max(1, o.ComponentCount);
            string type = comps == 1 ? "float" : "float" + comps;
            string field = "f" + n++;
            bool isPos = o.SystemValueType == 1
                         || o.Semantic.StartsWith("SV_", StringComparison.OrdinalIgnoreCase);
            string sem = isPos ? "SV_Position" : o.FullSemantic;
            sb.AppendLine("    " + type + " " + field + " : " + sem + ";");
            if (!isPos && o.Semantic.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase))
            {
                if (comps >= 3 && normalField is null && uvField is not null) normalField = field;
                else if (comps >= 2 && uvField is null) uvField = field;
            }
        }
        sb.AppendLine("};");
        sb.AppendLine("float4 main(PSIn i) : SV_Target {");
        sb.AppendLine(uvField is null ? "    float2 uv = float2(0.5, 0.5);" : "    float2 uv = i." + uvField + ".xy;");
        sb.AppendLine("    float4 d = gDiffuse.Sample(gSamp, uv);");
        sb.AppendLine(normalField is null
            ? "    float ndl = 0.75;"
            : "    float ndl = saturate(dot(normalize(i." + normalField + ".xyz), -gSunDir.xyz));");
        sb.AppendLine("    float3 lit = d.rgb * (gSunColor.rgb * ndl + 0.25);");
        sb.AppendLine("    return float4(lit, d.a);");
        sb.AppendLine("}");

        ComparisonShaderSource = sb.ToString();
        var src = System.Text.Encoding.ASCII.GetBytes(ComparisonShaderSource);
        var entry = System.Text.Encoding.ASCII.GetBytes("main\0");
        var target = System.Text.Encoding.ASCII.GetBytes("ps_5_0\0");
        ID3D10Blob* code = null, errs = null;
        int hr;
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* sp = src)
            fixed (byte* ep = entry)
            fixed (byte* tp = target)
                hr = compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                    ep, tp, 0u, 0u, &code, &errs);
        }
        catch (Exception ex) { error = "the HLSL compiler is unavailable: " + ex.Message; return false; }

        if (hr < 0 || code is null)
        {
            error = errs is not null
                ? "comparison shader failed to compile: " + SilkMarshal.PtrToString((nint)errs->GetBufferPointer())
                : string.Format("comparison shader failed to compile: 0x{0:X8}", hr);
            Log(error);
            return false;
        }

        ComPtr<ID3D11PixelShader> cps = default;
        hr = _device.CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(),
            ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref cps);
        if (hr < 0)
        {
            error = string.Format("CreatePixelShader (comparison) failed 0x{0:X8}", hr);
            Log(error);
            return false;
        }
        _comparePs = cps;
        Log("comparison shader built from the vertex shader's output signature");
        return true;
    }

    public bool HasComparisonShader => _comparePs.Handle is not null;

    /// <summary>The generated HLSL, so the window can show exactly what it is being compared against.</summary>
    public string? ComparisonShaderSource { get; private set; }

    // ---------------------------------------------------------------- resources

    public void SetMesh(PreviewMesh mesh)
    {
        Mesh = mesh;
        _vb.Dispose(); _ib.Dispose();
        _vb = default; _ib = default;

        var vdesc = new BufferDesc
        {
            ByteWidth = (uint)(mesh.Vertices.Length * PreviewVertex.SizeInBytes),
            Usage = Usage.Immutable, BindFlags = (uint)BindFlag.VertexBuffer,
        };
        fixed (PreviewVertex* p = mesh.Vertices)
        {
            var sub = new SubresourceData { PSysMem = p };
            ComPtr<ID3D11Buffer> b = default;
            _device.CreateBuffer(in vdesc, in sub, ref b);
            _vb = b;
        }

        var idesc = new BufferDesc
        {
            ByteWidth = (uint)(mesh.Indices.Length * 4),
            Usage = Usage.Immutable, BindFlags = (uint)BindFlag.IndexBuffer,
        };
        fixed (uint* p = mesh.Indices)
        {
            var sub = new SubresourceData { PSysMem = p };
            ComPtr<ID3D11Buffer> b = default;
            _device.CreateBuffer(in idesc, in sub, ref b);
            _ib = b;
        }
        _indexCount = mesh.Indices.Length;
        Log($"mesh '{mesh.Name}': {mesh.Vertices.Length:n0} verts, {mesh.TriangleCount:n0} tris");
    }

    /// <summary>Bind RGBA8 pixels to a reflected texture name (e.g. <c>DiffuseTexture__TX</c>).</summary>
    public void SetTexture(string reflectedName, byte[] rgba, int width, int height)
    {
        if (_textures.TryGetValue(reflectedName, out var old)) old.Dispose();
        _textures[reflectedName] = MakeTexture(rgba, width, height);
        Log($"bound texture '{reflectedName}' ({width}x{height})");
    }

    public void ClearTextures()
    {
        foreach (var t in _textures.Values) t.Dispose();
        _textures.Clear();
    }

    private ComPtr<ID3D11ShaderResourceView> MakeTexture(byte[] rgba, int w, int h)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.FormatR8G8B8A8Unorm, SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource,
        };
        ComPtr<ID3D11Texture2D> tex = default;
        fixed (byte* p = rgba)
        {
            var sub = new SubresourceData { PSysMem = p, SysMemPitch = (uint)(w * 4) };
            _device.CreateTexture2D(in desc, in sub, ref tex);
        }
        ComPtr<ID3D11ShaderResourceView> srv = default;
        _device.CreateShaderResourceView(tex, null, ref srv);
        tex.Dispose();
        return srv;
    }

    private void EnsureTargets(int w, int h)
    {
        if (_width == w && _height == h && _rt.Handle is not null) return;
        _rtv.Dispose(); _rt.Dispose(); _stage.Dispose(); _dsv.Dispose(); _depth.Dispose();
        _rtv = default; _rt = default; _stage = default; _dsv = default; _depth = default;
        _width = w; _height = h;

        // BGRA so the readback drops straight into an Avalonia Bgra8888 bitmap with no swizzle
        var rtDesc = new Texture2DDesc
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.FormatB8G8R8A8Unorm, SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default, BindFlags = (uint)BindFlag.RenderTarget,
        };
        ComPtr<ID3D11Texture2D> rt = default;
        _device.CreateTexture2D(in rtDesc, null, ref rt);
        _rt = rt;
        ComPtr<ID3D11RenderTargetView> rtv = default;
        _device.CreateRenderTargetView(_rt, null, ref rtv);
        _rtv = rtv;

        var st = rtDesc;
        st.Usage = Usage.Staging; st.BindFlags = 0; st.CPUAccessFlags = (uint)CpuAccessFlag.Read;
        ComPtr<ID3D11Texture2D> stage = default;
        _device.CreateTexture2D(in st, null, ref stage);
        _stage = stage;

        var dd = new Texture2DDesc
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.FormatD32Float, SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default, BindFlags = (uint)BindFlag.DepthStencil,
        };
        ComPtr<ID3D11Texture2D> depth = default;
        _device.CreateTexture2D(in dd, null, ref depth);
        _depth = depth;
        ComPtr<ID3D11DepthStencilView> dsv = default;
        _device.CreateDepthStencilView(_depth, null, ref dsv);
        _dsv = dsv;
    }

    private void UpdateStates(PreviewSettings s)
    {
        _raster.Dispose(); _blend.Dispose(); _depthState.Dispose();
        _raster = default; _blend = default; _depthState = default;

        var rd = new RasterizerDesc
        {
            FillMode = s.Wireframe ? FillMode.Wireframe : FillMode.Solid,
            CullMode = s.CullBackFaces ? CullMode.Back : CullMode.None,
            FrontCounterClockwise = 0,
            DepthClipEnable = 1,
        };
        ComPtr<ID3D11RasterizerState> rs = default;
        _device.CreateRasterizerState(in rd, ref rs);
        _raster = rs;

        var bd = new BlendDesc();
        bd.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = s.AlphaBlend,
            SrcBlend = Blend.SrcAlpha, DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.InvSrcAlpha, BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ComPtr<ID3D11BlendState> bs = default;
        _device.CreateBlendState(in bd, ref bs);
        _blend = bs;

        var dsd = new DepthStencilDesc
        {
            DepthEnable = s.DepthTest,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunc.Less,
            StencilEnable = 0,
        };
        ComPtr<ID3D11DepthStencilState> ds = default;
        _device.CreateDepthStencilState(in dsd, ref ds);
        _depthState = ds;
    }

    // ---------------------------------------------------------------- constants

    /// <summary>User/material overrides by constant name, e.g. <c>TintColor</c> → 4 floats.</summary>
    public Dictionary<string, float[]> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fill one reflected cbuffer. Values come from, in order: an explicit override, the engine
    /// value for a name we recognise, the constant's own RDEF default, then zero.</summary>
    private void FillConstantBuffer(DxbcConstantBuffer cb, PreviewSettings s, Matrix4x4 world,
        Matrix4x4 view, Matrix4x4 proj, List<string>? unbound)
    {
        var bytes = new byte[Math.Max(16, cb.AllocationSize)];
        var vp = Matrix4x4.Multiply(view, proj);
        var cam = CameraPosition(s);

        foreach (var v in cb.Variables)
        {
            float[]? data = null;

            if (Overrides.TryGetValue(v.Name, out var ov)) data = ov;
            else
            {
                data = v.Name.ToUpperInvariant() switch
                {
                    "WORLD_MATRIX" or "MWORLD" => Mat(world, s),
                    "VIEW_PROJECTION_MATRIX" or "MVIEWPROJ" => Mat(vp, s),
                    "MVIEW" => Mat(view, s),
                    "MVIEWINV" => Mat(Invert(view), s),
                    "MPROJ" => Mat(proj, s),
                    "VCAMERA" or "CAMERA_POSITION" => new[] { cam.X, cam.Y, cam.Z, 1f },
                    "TIME" => new[] { s.TimeSeconds, s.TimeSeconds * 0.5f, MathF.Sin(s.TimeSeconds), 1f },
                    "SUN_LIGHT_DIRECTION" => new[] { s.SunDirection.X, s.SunDirection.Y, s.SunDirection.Z, 0f },
                    "SUN_LIGHT_COLOR" => new[] { s.SunColor.X, s.SunColor.Y, s.SunColor.Z, s.SunColor.W },
                    "FOG_OF_WAR_PARAMS" or "FOW_HEIGHT_FADE" => new[] { 0f, 0f, 0f, 0f },

                    // M212: these two ADD, and the result multiplies a diffuse the shader has ALREADY
                    // doubled and clamped. Measured on staticmesh/defaultenv_flat blob#53 by binding a flat
                    // texture and sweeping - with headroom below clipping, because the first attempt
                    // measured a saturated image where every setting looked identical and concluded
                    // nothing:
                    //
                    //     output = saturate(2 x texture) x (SHADOW_COLOR + SHADOW_COLOR_COMPLEMENT) x TintColor
                    //
                    // Evidence. Four different splits summing to 1.0 all gave exactly 2.00x on a 0.125
                    // texture, a sum of 2.0 gave 4.00x, a sum of 0 gave 0 - so it is the SUM, linearly, and
                    // the split is not observable here (DISABLE_SHADOWS forces the shadow term). Sweeping
                    // the texture instead held 1.00x from input 64 to 126 and then clamped hard at 127 for
                    // 130, 140, 160 and 200 - a saturate() that reaches 1.0 at texture 0.5, i.e. the
                    // diffuse is doubled and clamped BEFORE the light term is applied. Only three
                    // constants are USED in that permutation, so the x2 is a literal in the shader.
                    //
                    // That x2 is intentional - it is the overbright-albedo convention, of a piece with the
                    // lightMapColorScale=2 map data records - so League's environment diffuse is authored
                    // at roughly half scale. The preview must NOT cancel it: picking a sum of 0.5 to undo
                    // the doubling would make a synthetic mid-grey test texture look tidy while
                    // misrepresenting what the game actually draws.
                    //
                    // The real defect in M210 was a sum of 2.0, which double-brightened on top of the
                    // shader's intended doubling and clipped everything bright to white. A sum of 1.0 is
                    // the neutral value for a term that multiplies - and is what a colour plus its
                    // complement ought to add up to. The SPLIT is a stand-in and is labelled as one: only
                    // the sum is observable in this permutation, so nothing measured constrains it. Both
                    // are editable in the Constants tab.
                    "SHADOW_COLOR" => new[] { 0.35f, 0.35f, 0.35f, 1f },
                    "SHADOW_COLOR_COMPLEMENT" => new[] { 0.65f, 0.65f, 0.65f, 1f },

                    // Not USED by the permutation the above was measured on (it carries NO_BAKED_LIGHTING),
                    // so this is unverified and stays neutral rather than being set to the 2 that map data
                    // records for lightMapColorScale. Doubling on an untested guess is the exact mistake
                    // above.
                    "LIGHT_MAP_COLOR_SCALE_AND_INTENSITY" => new[] { 1f, 1f, 1f, 1f },
                    "TINTCOLOR" => new[] { 1f, 1f, 1f, 1f },
                    _ => null,
                };
            }

            data ??= v.DefaultValue;
            if (data is null)
            {
                if (v.IsUsed) unbound?.Add($"{cb.Name}.{v.Name} ({v.TypeName})");
                continue;                                        // leaves zeros
            }

            int n = Math.Min(v.Size / 4, data.Length);
            for (int i = 0; i < n; i++)
            {
                int at = v.Offset + i * 4;
                if (at + 4 > bytes.Length) break;
                BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), data[i]);
            }
        }

        _pendingCb = bytes;
    }

    private byte[] _pendingCb = Array.Empty<byte>();
    private ComPtr<ID3D11Buffer> _compareCb;

    private void EnsureCompareCb()
    {
        if (_compareCb.Handle is not null) return;
        var d = new BufferDesc
        {
            ByteWidth = 32, Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> b = default;
        _device.CreateBuffer(in d, null, ref b);
        _compareCb = b;
    }

    private static float[] Mat(Matrix4x4 m, PreviewSettings s)
    {
        if (s.TransposeMatrices) m = Matrix4x4.Transpose(m);
        return new[]
        {
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44,
        };
    }

    private static Matrix4x4 Invert(Matrix4x4 m) => Matrix4x4.Invert(m, out var r) ? r : Matrix4x4.Identity;

    private static Vector3 CameraPosition(PreviewSettings s) => new(
        s.Distance * MathF.Cos(s.Pitch) * MathF.Sin(s.Yaw),
        s.Distance * MathF.Sin(s.Pitch),
        s.Distance * MathF.Cos(s.Pitch) * MathF.Cos(s.Yaw));

    private void Upload(ComPtr<ID3D11Buffer> buf, byte[] data)
    {
        MappedSubresource m = default;
        if (_ctx.Map(buf, 0, Map.WriteDiscard, 0, ref m) < 0) return;
        fixed (byte* src = data)
            System.Buffer.MemoryCopy(src, m.PData, data.Length, data.Length);
        _ctx.Unmap(buf, 0);
    }

    // ---------------------------------------------------------------- render

    /// <summary>Draw one frame and return it as BGRA8 bytes, row-packed at <paramref name="width"/>*4.
    /// Returns null when there is nothing to draw; <paramref name="error"/> then says why.</summary>
    public byte[]? RenderFrame(int width, int height, PreviewSettings s, out string? error,
        List<string>? unboundConstants = null)
    {
        error = null;
        DrawCalls = 0;
        if (!IsReady) { error = "no shader loaded"; return null; }
        if (_vb.Handle is null || _indexCount == 0) { error = "no mesh set"; return null; }
        if (width <= 0 || height <= 0) { error = "zero-sized target"; return null; }

        var sw = Stopwatch.StartNew();
        try
        {
            EnsureTargets(width, height);
            UpdateStates(s);

            float radius = MathF.Max(0.05f, Mesh?.Radius ?? 1f);
            var eye = CameraPosition(s) * radius;
            var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(s.Fov, (float)width / height, radius * 0.02f, radius * 40f);
            var world = Matrix4x4.Identity;

            var vpRect = new Viewport(0, 0, width, height, 0, 1);
            _ctx.RSSetViewports(1, in vpRect);
            _ctx.OMSetRenderTargets(1, ref _rtv, _dsv);

            var clear = stackalloc float[4] { s.ClearColor.X, s.ClearColor.Y, s.ClearColor.Z, s.ClearColor.W };
            _ctx.ClearRenderTargetView(_rtv, clear);
            _ctx.ClearDepthStencilView(_dsv, (uint)ClearFlag.Depth, 1f, 0);

            _ctx.RSSetState(_raster);
            var factor = stackalloc float[4] { 0, 0, 0, 0 };
            _ctx.OMSetBlendState(_blend, factor, 0xFFFFFFFF);
            _ctx.OMSetDepthStencilState(_depthState, 0);

            _ctx.IASetInputLayout(_layout);
            _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
            uint stride = PreviewVertex.SizeInBytes, offset = 0;
            _ctx.IASetVertexBuffers(0, 1, ref _vb, in stride, in offset);
            _ctx.IASetIndexBuffer(_ib, Format.FormatR32Uint, 0);

            _ctx.VSSetShader(_vs, null, 0);
            bool compare = s.UseComparisonShader && _comparePs.Handle is not null;
            _ctx.PSSetShader(compare ? _comparePs : _ps, null, 0);

            // constant buffers, filled from reflection
            if (_vsRefl is not null)
                foreach (var cb in _vsRefl.ConstantBuffers)
                {
                    if (cb.BindPoint < 0 || !_vsCbs.TryGetValue(cb.BindPoint, out var buf)) continue;
                    FillConstantBuffer(cb, s, world, view, proj, unboundConstants);
                    Upload(buf, _pendingCb);
                    _ctx.VSSetConstantBuffers((uint)cb.BindPoint, 1, ref buf);
                }
            if (_psRefl is not null)
                foreach (var cb in _psRefl.ConstantBuffers)
                {
                    if (cb.BindPoint < 0 || !_psCbs.TryGetValue(cb.BindPoint, out var buf)) continue;
                    FillConstantBuffer(cb, s, world, view, proj, unboundConstants);
                    Upload(buf, _pendingCb);
                    _ctx.PSSetConstantBuffers((uint)cb.BindPoint, 1, ref buf);
                }

            // textures and samplers, at the registers the shader declares
            if (compare)
            {
                var cbBytes = new byte[32];
                var sd = s.SunDirection;
                var vals = new[] { sd.X, sd.Y, sd.Z, 0f, s.SunColor.X, s.SunColor.Y, s.SunColor.Z, 1f };
                for (int i = 0; i < vals.Length; i++) BitConverter.TryWriteBytes(cbBytes.AsSpan(i * 4, 4), vals[i]);
                EnsureCompareCb();
                Upload(_compareCb, cbBytes);
                _ctx.PSSetConstantBuffers(0, 1, ref _compareCb);

                var firstBound = _psRefl?.Textures.FirstOrDefault(t => _textures.ContainsKey(t.Name));
                var srv = firstBound is not null ? _textures[firstBound.Name] : _white;
                _ctx.PSSetShaderResources(0, 1, ref srv);
                var samp = _linearWrap;
                _ctx.PSSetSamplers(0, 1, ref samp);
            }
            else
            {
                BindResources(_psRefl, pixel: true);
            }
            BindResources(_vsRefl, pixel: false);

            _ctx.DrawIndexed((uint)_indexCount, 0, 0);
            DrawCalls++;

            _ctx.CopyResource(_stage, _rt);
            MappedSubresource map = default;
            int hr = _ctx.Map(_stage, 0, Map.Read, 0, ref map);
            if (hr < 0) { error = $"Map(staging) failed 0x{hr:X8}"; return null; }

            var outBytes = new byte[width * height * 4];
            fixed (byte* dst = outBytes)
            {
                for (int y = 0; y < height; y++)
                {
                    var srcRow = (byte*)map.PData + (nuint)y * map.RowPitch;
                    System.Buffer.MemoryCopy(srcRow, dst + y * width * 4, width * 4, width * 4);
                }
            }
            _ctx.Unmap(_stage, 0);

            LastFrameMs = sw.Elapsed.TotalMilliseconds;
            return outBytes;
        }
        catch (Exception ex)
        {
            error = $"render failed: {ex.Message}";
            Log(error);
            return null;
        }
    }

    private void BindResources(DxbcShader? refl, bool pixel)
    {
        if (refl is null) return;
        foreach (var t in refl.Textures)
        {
            var srv = _textures.TryGetValue(t.Name, out var bound) ? bound : _white;
            if (pixel) _ctx.PSSetShaderResources(t.BindPoint, 1, ref srv);
            else _ctx.VSSetShaderResources(t.BindPoint, 1, ref srv);
        }
        foreach (var smp in refl.Samplers)
        {
            // "Clamp_" prefixed shared samplers are the engine's clamped ones; everything else wraps.
            var st = smp.Name.StartsWith("Clamp", StringComparison.OrdinalIgnoreCase) ? _linearClamp : _linearWrap;
            if (pixel) _ctx.PSSetSamplers(smp.BindPoint, 1, ref st);
            else _ctx.VSSetSamplers(smp.BindPoint, 1, ref st);
        }
    }

    /// <summary>Which reflected textures currently have nothing bound (they sample white).</summary>
    public IEnumerable<string> UnboundTextureNames()
    {
        foreach (var refl in new[] { _vsRefl, _psRefl })
        {
            if (refl is null) continue;
            foreach (var t in refl.Textures)
                if (!_textures.ContainsKey(t.Name)) yield return t.Name;
        }
    }

    // ---------------------------------------------------------------- teardown

    private void ReleaseShaders()
    {
        _vs.Dispose(); _ps.Dispose(); _layout.Dispose(); _comparePs.Dispose();
        _vs = default; _ps = default; _layout = default; _comparePs = default;
        foreach (var b in _vsCbs.Values) b.Dispose();
        foreach (var b in _psCbs.Values) b.Dispose();
        _vsCbs.Clear(); _psCbs.Clear();
    }

    public void Dispose()
    {
        ReleaseShaders();
        ClearTextures();
        _white.Dispose();
        _vb.Dispose(); _ib.Dispose(); _compareCb.Dispose();
        _rtv.Dispose(); _rt.Dispose(); _stage.Dispose(); _dsv.Dispose(); _depth.Dispose();
        _linearWrap.Dispose(); _linearClamp.Dispose();
        _raster.Dispose(); _blend.Dispose(); _depthState.Dispose();
        _ctx.Dispose(); _device.Dispose();
        _d3d?.Dispose();
    }
}
