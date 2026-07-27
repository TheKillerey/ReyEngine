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

    /// <summary>M215: when set, the renderer uses THESE instead of its own orbit maths.
    ///
    /// <para>The window drives an <c>OrbitCamera</c> - the same class and the same WASD/look/pan bindings as
    /// the map viewport - and hands the result down. The matrices are passed rather than the camera object
    /// so this assembly keeps no reference to the OpenGL renderer that owns it; the isolation is the point
    /// of the separate project. The built-in orbit stays as the fallback the headless harnesses use.</para></summary>
    public Matrix4x4? SuppliedView;
    public Matrix4x4? SuppliedProjection;
    public Vector3? SuppliedCameraPosition;
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

    /// <summary>M223: mirror world X, which is what the rest of the editor's viewport does. League's data is
    /// authored in the opposite handedness to the renderer, so without this a map is laid out mirrored
    /// against every other view in the app.</summary>
    public bool MirrorX = true;

    /// <summary>M216: what the bone palette should contain.
    ///
    /// <para>A champion vertex shader marks <c>mProj</c> as USED while <c>VIEW_PROJECTION_MATRIX</c> and
    /// <c>mView</c> are not, which says the bones are expected to carry object-to-VIEW and the shader only
    /// applies projection afterwards. With no animation, bone-to-object is identity, so the palette should
    /// hold the view matrix. Identity was the first guess and it put the mesh on the near plane.</para></summary>
    public BonePose BonePose = BonePose.ViewTransposed;

    /// <summary>Rows per bone matrix in <c>BonesCB</c>. League caps skeletons at 256 bones and the buffer is
    /// 12,288 bytes, which is 48 bytes each at 3 rows (a 4x3) or 64 at 4 (a full 4x4 over 192 bones). Both
    /// divide evenly, so this is measured in the app rather than assumed - a wrong stride makes the skeleton
    /// shear instead of vanishing, which is easy to mistake for a broken mesh.</summary>
    public int BoneMatrixRows = 3;
}

/// <summary>Result of trying to bring a shader pair up — every failure carries its own message.</summary>
/// <summary>M216: candidate contents for the bone palette, kept switchable because the packing is not
/// recorded anywhere in the bytecode we read.</summary>
public enum BonePose
{
    /// <summary>Bind pose. Correct only if the shader applies a view matrix itself.</summary>
    Identity,
    /// <summary>The view matrix, rows as-is.</summary>
    View,
    /// <summary>The view matrix transposed - the layout a float4x3 takes under HLSL's default packing.</summary>
    ViewTransposed,
}

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

/// <summary>M214: one material's whole pipeline — its two shaders, the input layout generated from them,
/// its constant buffers, its textures, and the slice of the index buffer it draws.
///
/// <para>A scene is a list of these. A champion skin is one mesh whose submeshes each name a different
/// material, so drawing it correctly means a separate shader, permutation and texture set per submesh —
/// exactly what the single-material bench could not express.</para></summary>
public sealed unsafe class PreviewMaterial : IDisposable
{
    public required string Name { get; init; }
    public required DxbcShader VsRefl { get; init; }
    public required DxbcShader PsRefl { get; init; }

    internal ComPtr<ID3D11VertexShader> Vs;
    internal ComPtr<ID3D11PixelShader> Ps;
    internal ComPtr<ID3D11InputLayout> Layout;
    internal readonly Dictionary<int, ComPtr<ID3D11Buffer>> VsCbs = new();
    internal readonly Dictionary<int, ComPtr<ID3D11Buffer>> PsCbs = new();
    internal readonly Dictionary<string, ComPtr<ID3D11ShaderResourceView>> Textures =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Values this material authors, e.g. its own TintColor. Beat the renderer's engine values.</summary>
    public Dictionary<string, float[]> Params { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which slice of the shared index buffer this material covers. IndexCount &lt; 0 means "all".</summary>
    public int StartIndex { get; set; }
    public int IndexCount { get; set; } = -1;

    public bool Visible { get; set; } = true;

    public IEnumerable<string> UnboundTextures =>
        PsRefl.Textures.Concat(VsRefl.Textures).Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !Textures.ContainsKey(n));

    public void Dispose()
    {
        Vs.Dispose(); Ps.Dispose(); Layout.Dispose();
        foreach (var b in VsCbs.Values) b.Dispose();
        foreach (var b in PsCbs.Values) b.Dispose();
        foreach (var t in Textures.Values) t.Dispose();
        VsCbs.Clear(); PsCbs.Clear(); Textures.Clear();
    }
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

    private ComPtr<ID3D11PixelShader> _comparePs;
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

    /// <summary>The scene. One entry for the single-shader bench, one per submesh for a loaded model.</summary>
    private readonly List<PreviewMaterial> _materials = new();
    public IReadOnlyList<PreviewMaterial> Materials => _materials;

    private ComPtr<ID3D11ShaderResourceView> _white;
    private ComPtr<ID3D11ShaderResourceView> _identityRamp;

    public string DeviceDescription { get; private set; } = "(no device)";
    public bool IsReady => _device.Handle is not null && _materials.Count > 0;
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

        // M221: the colour-remap ramp stand-in must be TRANSPARENT, and the shader says so itself.
        //
        // Disassembling skinnedmesh/diffuse_alpha ps blob 5 settles what two rounds of guessing could not:
        //
        //     dp3  r1.x, r0.yzwy, l(0.2126, 0.7152, 0.0722)   // luma, Rec.709
        //     mov  r1.y, l(0.5)
        //     sample r1.xyzw, r1.xyxx, t1.xyzw, s15           // ramp.Sample(luma, 0.5)
        //     lt   r1.w, l(0.000000), r1.w                    // is the sampled ALPHA > 0 ?
        //     movc r0.yzw, r1.wwww, r1.xxyz, r0.yyzw          // yes -> replace rgb; no -> keep it
        //
        // The remap is GATED ON THE RAMP'S ALPHA. It is not unconditional, which is what the D3D11 spike
        // assumed and what M218/M219 inherited. Any opaque ramp forces the replacement - white gave a white
        // model, a greyscale identity gave a black-and-white one - because alpha was 255 in both.
        //
        // Alpha zero and the shader skips the stage on its own, keeping the lit diffuse. That is a real
        // no-op rather than an approximation of one, and it is almost certainly what the engine binds when
        // no colour grading is active. The rgb is irrelevant; only the alpha is read.
        _identityRamp = MakeTexture(new byte[] { 0, 0, 0, 0 }, 1, 1);
    }

    // ---------------------------------------------------------------- shaders

    /// <summary>Bench mode: replace the scene with a single material covering the whole mesh.</summary>
    public ShaderLoadReport LoadShaders(DxbcShader vsRefl, DxbcShader psRefl)
    {
        ClearMaterials();
        var m = BuildMaterial("(single shader)", vsRefl, psRefl, 0, -1, out var r);
        if (m is not null) _materials.Add(m);
        return r;
    }

    public void ClearMaterials()
    {
        foreach (var m in _materials) m.Dispose();
        _materials.Clear();
        foreach (var b in _sharedCbs.Values) b.Dispose();
        _sharedCbs.Clear();
        _comparePs.Dispose();
        _comparePs = default;
    }

    /// <summary>M214: bring up one material's pipeline. <paramref name="indexCount"/> below zero means the
    /// whole index buffer, which is what the bench uses.</summary>
    public PreviewMaterial? BuildMaterial(string name, DxbcShader vsRefl, DxbcShader psRefl,
        int startIndex, int indexCount, out ShaderLoadReport r)
    {
        r = new ShaderLoadReport();
        if (_device.Handle is null) { r.Error = "no D3D11 device"; return null; }

        var mat = new PreviewMaterial
        {
            Name = name, VsRefl = vsRefl, PsRefl = psRefl,
            StartIndex = startIndex, IndexCount = indexCount,
        };

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
            mat.Dispose();
            return null;
        }
        mat.Vs = vs;
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
            mat.Dispose();
            return null;
        }
        mat.Ps = ps;
        r.Step($"CreatePixelShader OK ({psRefl.ByteSize:n0} bytes, {psRefl.ShaderModel})");

        // ---- 2. input layout, generated from the vertex shader's own signature
        if (!CreateInputLayout(mat, r)) { mat.Dispose(); return null; }

        // ---- 3. one constant buffer per reflected cbuffer, sized as the shader declares
        CreateConstantBuffers(vsRefl, mat.VsCbs, r, "vs");
        CreateConstantBuffers(psRefl, mat.PsCbs, r, "ps");

        r.Success = true;
        return mat;
    }

    public void AddMaterial(PreviewMaterial m) => _materials.Add(m);

    private bool CreateInputLayout(PreviewMaterial mat, ShaderLoadReport r)
    {
        var vsRefl = mat.VsRefl;
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
            mat.Layout = layout;
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
        // one comparison shader, generated from the FIRST material's vertex signature
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

    /// <summary>Bind RGBA8 pixels to a reflected texture name (e.g. <c>DiffuseTexture__TX</c>) on EVERY
    /// material that declares it. That is what the bench wants; a scene binds per material instead.</summary>
    public void SetTexture(string reflectedName, byte[] rgba, int width, int height)
    {
        foreach (var m in _materials) SetTexture(m, reflectedName, rgba, width, height);
        if (_materials.Count > 0) Log($"bound texture '{reflectedName}' ({width}x{height})");
    }

    public void SetTexture(PreviewMaterial m, string reflectedName, byte[] rgba, int width, int height)
    {
        if (m.Textures.TryGetValue(reflectedName, out var old)) old.Dispose();
        m.Textures[reflectedName] = MakeTexture(rgba, width, height);
    }

    public void ClearTextures()
    {
        foreach (var m in _materials)
        {
            foreach (var t in m.Textures.Values) t.Dispose();
            m.Textures.Clear();
        }
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

    private (bool wire, bool cull, bool depth, bool blend, bool mirror)? _stateKey;

    private void UpdateStates(PreviewSettings s)
    {
        // M216: these were disposed and recreated every single frame. They only depend on four toggles.
        var key = (s.Wireframe, s.CullBackFaces, s.DepthTest, s.AlphaBlend, s.MirrorX);
        if (_stateKey == key && _raster.Handle is not null) return;
        _stateKey = key;

        _raster.Dispose(); _blend.Dispose(); _depthState.Dispose();
        _raster = default; _blend = default; _depthState = default;

        var rd = new RasterizerDesc
        {
            FillMode = s.Wireframe ? FillMode.Wireframe : FillMode.Solid,
            CullMode = s.CullBackFaces ? CullMode.Back : CullMode.None,
            // M223: a mirrored view reverses triangle winding, so the front face has to swap with it or
            // backface culling removes exactly the faces it should keep. ViewportMeshRenderer does the same
            // thing off the model determinant.
            FrontCounterClockwise = s.MirrorX,
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
    private void FillConstantBuffer(PreviewMaterial? mat, DxbcConstantBuffer cb, PreviewSettings s,
        Matrix4x4 world, Matrix4x4 view, Matrix4x4 proj, List<string>? unbound)
    {
        // M216: reused, not reallocated. At 1,600 draw calls x 2-4 cbuffers this was thousands of
        // short-lived arrays per frame and the GC pressure showed up directly in the frame time.
        int need = Math.Max(16, cb.AllocationSize);
        if (_cbScratch.Length < need) _cbScratch = new byte[need];
        var bytes = _cbScratch;
        Array.Clear(bytes, 0, need);
        var vp = Matrix4x4.Multiply(view, proj);
        var cam = s.SuppliedCameraPosition ?? CameraPosition(s);

        // M214: the bone palette. A skinned character's vertex shader transforms every vertex by the bones
        // its BLENDINDICES name, so a zero-filled BonesCB collapses the whole mesh to the origin and draws
        // nothing at all - which is exactly what happened first. There is no animation here, so the correct
        // content is the identity bind pose: the mesh renders as authored.
        if (cb.Name.Contains("Bone", StringComparison.OrdinalIgnoreCase))
        {
            int rows = Math.Clamp(s.BoneMatrixRows, 3, 4);
            int stride = rows * 16;

            var m = s.BonePose switch
            {
                BonePose.View => view,
                BonePose.ViewTransposed => Matrix4x4.Transpose(view),
                _ => Matrix4x4.Identity,
            };
            var pose = new[]
            {
                m.M11, m.M12, m.M13, m.M14,
                m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34,
                m.M41, m.M42, m.M43, m.M44,
            };

            for (int at = 0; at + stride <= bytes.Length; at += stride)
                for (int i = 0; i < rows * 4; i++)
                    BitConverter.TryWriteBytes(bytes.AsSpan(at + i * 4, 4), pose[i]);

            _pendingCb = bytes; _pendingLength = need;
            return;
        }

        foreach (var v in cb.Variables)
        {
            float[]? data = null;

            // a material's own authored value wins over the window's global override, which wins over
            // the engine stand-ins below
            if (mat is not null && mat.Params.TryGetValue(v.Name, out var pv)) data = pv;
            else if (Overrides.TryGetValue(v.Name, out var ov)) data = ov;
            else
            {
                data = v.Name.ToUpperInvariant() switch
                {
                    "WORLD_MATRIX" or "MWORLD" => Mat(world, s),
                    "VIEW_PROJECTION_MATRIX" or "MVIEWPROJ" => Mat(vp, s),
                    "MVIEW" => Mat(view, s),
                    "MVIEWINV" => Mat(Invert(view), s),
                    "MWORLDINV" => Mat(Invert(world), s),
                    // the ambient cube a character is lit by when it is not standing on a baked lightgrid.
                    // Neutral rather than zero: zero renders the model black and looks like a load failure.
                    "LIGHTGRID_COLORS" => new[]
                    {
                        0.5f, 0.5f, 0.5f, 1f, 0.5f, 0.5f, 0.5f, 1f, 0.5f, 0.5f, 0.5f, 1f,
                        0.5f, 0.5f, 0.5f, 1f, 0.5f, 0.5f, 0.5f, 1f, 0.5f, 0.5f, 0.5f, 1f,
                    },
                    "MPROJ" => Mat(proj, s),
                    "VCAMERA" or "CAMERA_POSITION" => new[] { cam.X, cam.Y, cam.Z, 1f },
                    "TIME" => new[] { s.TimeSeconds, s.TimeSeconds * 0.5f, MathF.Sin(s.TimeSeconds), 1f },
                    "SUN_LIGHT_DIRECTION" => new[] { s.SunDirection.X, s.SunDirection.Y, s.SunDirection.Z, 0f },
                    "SUN_LIGHT_COLOR" => new[] { s.SunColor.X, s.SunColor.Y, s.SunColor.Z, s.SunColor.W },
                    // M223: the fog-of-war neutral is NOT all zeros, which is what these were.
                    //
                    // From the vertex shader (staticmesh/defaultenv_flat):
                    //     mad o3.xy, r0.xzxx, cb2[19].xyxx, cb2[19].zwzz   // uv   = worldXZ * FOW_PARAMS.xy + .zw
                    //     mad_sat o3.z, r0.y, cb2[21].x, cb2[21].y         // fade = saturate(worldY * HEIGHT_FADE.x + .y)
                    // and the pixel shader:
                    //     blend = fade * (1 - fowMap.a) + fowMap.a
                    //     rgb   = lerp(fowRgb, lit, blend)
                    //
                    // With HEIGHT_FADE all zero the fade term is saturate(0) = 0, so anything the FOW map
                    // does not mark fully visible collapses toward the fog colour, and it does so as a
                    // function of WORLD Y - which is exactly the reported "meshes below a certain height go
                    // black". Setting .y = 1 makes the fade saturate to 1 at every height, so the geometry
                    // stays lit no matter what the FOW map says. That is the honest neutral for a preview
                    // with no fog of war, and it is read off the shader rather than guessed.
                    "FOW_HEIGHT_FADE" => new[] { 0f, 1f, 0f, 0f },

                    // uv = worldXZ * 0 + 0 samples one texel of the (white) stand-in, which is what a
                    // fully-revealed map looks like. Left at zero deliberately.
                    "FOG_OF_WAR_PARAMS" => new[] { 0f, 0f, 0f, 0f },

                    // Below this world height the engine treats everything as permanently visible. Nothing
                    // in the preview should ever be force-fogged, so push it above any real geometry.
                    "FOG_OF_WAR_ALWAYS_BELOW_Y" => new[] { 1e9f, 1e9f, 1e9f, 1e9f },

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

                    // M218: constants that MULTIPLY must not default to zero.
                    //
                    // Everything unrecognised falls through to a zero-filled buffer, which is the right
                    // conservative choice for an additive or offset term and catastrophically wrong for a
                    // multiplicative one. `Alpha` is a champion material's opacity: with no value it came
                    // through as 0 and Kayn rendered perfectly and completely invisibly - the geometry, the
                    // bones and the textures were all correct and every pixel was discarded at the end.
                    //
                    // Measured, not assumed: sweeping every USED constant of skinnedmesh/diffuse_alpha over
                    // the loaded model, `Alpha` was the ONLY one that changed whether anything appeared
                    // (0 lit pixels at 0, 1,684 at 1). The others below are not proven the same way - they
                    // are scale terms where 1 is the identity and 0 would silently erase their contribution,
                    // so 1 is the honest neutral rather than a measurement.
                    "ALPHA" => new[] { 1f, 1f, 1f, 1f },
                    "LIGHTGRID_SCALE" or "KGRASSFADE" => new[] { 1f, 1f, 1f, 1f },
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
        _pendingLength = need;
    }

    private byte[] _pendingCb = Array.Empty<byte>();
    private byte[] _cbScratch = new byte[1024];
    private byte[] _readback = Array.Empty<byte>();
    private int _pendingLength;
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

    private void Upload(ComPtr<ID3D11Buffer> buf, byte[] data, int length)
    {
        MappedSubresource m = default;
        if (_ctx.Map(buf, 0, Map.WriteDiscard, 0, ref m) < 0) return;
        fixed (byte* src = data)
            System.Buffer.MemoryCopy(src, m.PData, length, length);
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
            _sharedUploadedThisFrame.Clear();
            EnsureTargets(width, height);
            UpdateStates(s);

            float radius = MathF.Max(0.05f, Mesh?.Radius ?? 1f);
            var view = s.SuppliedView ?? Matrix4x4.CreateLookAt(
                CameraPosition(s) * radius, Vector3.Zero, Vector3.UnitY);
            if (s.MirrorX) view = Matrix4x4.CreateScale(-1f, 1f, 1f) * view;
            var proj = s.SuppliedProjection ?? Matrix4x4.CreatePerspectiveFieldOfView(
                s.Fov, (float)width / height, radius * 0.02f, radius * 40f);
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

            _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
            uint stride = PreviewVertex.SizeInBytes, offset = 0;
            _ctx.IASetVertexBuffers(0, 1, ref _vb, in stride, in offset);
            _ctx.IASetIndexBuffer(_ib, Format.FormatR32Uint, 0);

            bool compare = s.UseComparisonShader && _comparePs.Handle is not null;

            // M214: one pass per material. A champion skin is one vertex/index buffer whose submeshes each
            // want their own shader, permutation and textures, so the pipeline is rebound per slice.
            foreach (var mat in _materials)
            {
            if (!mat.Visible) continue;
            _ctx.IASetInputLayout(mat.Layout);
            _ctx.VSSetShader(mat.Vs, null, 0);
            _ctx.PSSetShader(compare ? _comparePs : mat.Ps, null, 0);

            // Constant buffers, filled from reflection.
            //
            // M216: a buffer whose contents do not depend on the material - PerFrameVertexCB and
            // PerFramePixelCB, which are per FRAME by name and by content - is filled and uploaded once and
            // then shared by every material. On Howling Abyss that is 1,600 draw calls x 2 buffers of
            // Map/fill/Unmap collapsing to two, which is where most of the frame time was going.
            foreach (var cb in mat.VsRefl.ConstantBuffers)
            {
                if (cb.BindPoint < 0) continue;
                var buf = ResolveCb(mat, cb, mat.VsCbs, s, world, view, proj, unboundConstants);
                if (buf.Handle is null) continue;
                _ctx.VSSetConstantBuffers((uint)cb.BindPoint, 1, ref buf);
            }
            foreach (var cb in mat.PsRefl.ConstantBuffers)
            {
                if (cb.BindPoint < 0) continue;
                var buf = ResolveCb(mat, cb, mat.PsCbs, s, world, view, proj, unboundConstants);
                if (buf.Handle is null) continue;
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
                Upload(_compareCb, cbBytes, cbBytes.Length);
                _ctx.PSSetConstantBuffers(0, 1, ref _compareCb);

                var firstBound = mat.PsRefl.Textures.FirstOrDefault(t => mat.Textures.ContainsKey(t.Name));
                var srv = firstBound is not null ? mat.Textures[firstBound.Name] : _white;
                _ctx.PSSetShaderResources(0, 1, ref srv);
                var samp = _linearWrap;
                _ctx.PSSetSamplers(0, 1, ref samp);
            }
            else
            {
                BindResources(mat, mat.PsRefl, pixel: true);
            }
            BindResources(mat, mat.VsRefl, pixel: false);

            uint count = mat.IndexCount < 0 ? (uint)_indexCount : (uint)mat.IndexCount;
            if (count > 0)
            {
                _ctx.DrawIndexed(count, (uint)Math.Max(0, mat.StartIndex), 0);
                DrawCalls++;
            }
            }

            _ctx.CopyResource(_stage, _rt);
            MappedSubresource map = default;
            int hr = _ctx.Map(_stage, 0, Map.Read, 0, ref map);
            if (hr < 0) { error = $"Map(staging) failed 0x{hr:X8}"; return null; }

            int rowBytes = width * 4;
            if (_readback.Length != rowBytes * height) _readback = new byte[rowBytes * height];
            var outBytes = _readback;
            fixed (byte* dst = outBytes)
            {
                // M216: the staging pitch usually equals the row, in which case this is one copy
                if (map.RowPitch == (uint)rowBytes)
                    System.Buffer.MemoryCopy(map.PData, dst, outBytes.Length, outBytes.Length);
                else
                    for (int y = 0; y < height; y++)
                        System.Buffer.MemoryCopy((byte*)map.PData + (nuint)y * map.RowPitch,
                            dst + y * rowBytes, rowBytes, rowBytes);
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

    private readonly Dictionary<string, ComPtr<ID3D11Buffer>> _sharedCbs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sharedUploadedThisFrame = new(StringComparer.Ordinal);

    /// <summary>Pick the buffer for this cbuffer and make sure it holds the right bytes. Material-independent
    /// buffers are shared and uploaded at most once per frame.</summary>
    private ComPtr<ID3D11Buffer> ResolveCb(PreviewMaterial mat, DxbcConstantBuffer cb,
        Dictionary<int, ComPtr<ID3D11Buffer>> own, PreviewSettings s,
        Matrix4x4 world, Matrix4x4 view, Matrix4x4 proj, List<string>? unbound)
    {
        bool materialSpecific = false;
        if (mat.Params.Count > 0)
            foreach (var v in cb.Variables)
                if (mat.Params.ContainsKey(v.Name)) { materialSpecific = true; break; }

        if (materialSpecific)
        {
            if (!own.TryGetValue(cb.BindPoint, out var mine)) return default;
            FillConstantBuffer(mat, cb, s, world, view, proj, unbound);
            Upload(mine, _pendingCb, _pendingLength);
            return mine;
        }

        string key = cb.Name + "#" + cb.AllocationSize;
        if (!_sharedCbs.TryGetValue(key, out var shared))
        {
            var desc = new BufferDesc
            {
                ByteWidth = (uint)Math.Max(16, cb.AllocationSize),
                Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.ConstantBuffer,
                CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> nb = default;
            if (_device.CreateBuffer(in desc, null, ref nb) < 0) return default;
            shared = nb;
            _sharedCbs[key] = shared;
        }
        if (_sharedUploadedThisFrame.Add(key))
        {
            FillConstantBuffer(mat, cb, s, world, view, proj, unbound);
            Upload(shared, _pendingCb, _pendingLength);
        }
        return shared;
    }

    private void BindResources(PreviewMaterial mat, DxbcShader? refl, bool pixel)
    {
        if (refl is null) return;
        foreach (var t in refl.Textures)
        {
            var srv = mat.Textures.TryGetValue(t.Name, out var bound) ? bound : StandIn(t.Name);
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

    /// <summary>The stand-in for a texture nothing supplied. White for almost everything; an identity
    /// ramp for the colour remap, where white would replace the whole image.</summary>
    private ComPtr<ID3D11ShaderResourceView> StandIn(string name) =>
        name.Contains("REMAP_RAMP", StringComparison.OrdinalIgnoreCase) ? _identityRamp : _white;


    /// <summary>Which reflected textures currently have nothing bound (they sample a stand-in).</summary>
    public IEnumerable<string> UnboundTextureNames() =>
        _materials.SelectMany(m => m.UnboundTextures).Distinct(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- teardown

    public void Dispose()
    {
        ClearMaterials();
        _white.Dispose();
        _identityRamp.Dispose();
        _vb.Dispose(); _ib.Dispose(); _compareCb.Dispose();
        _rtv.Dispose(); _rt.Dispose(); _stage.Dispose(); _dsv.Dispose(); _depth.Dispose();
        _linearWrap.Dispose(); _linearClamp.Dispose();
        _raster.Dispose(); _blend.Dispose(); _depthState.Dispose();
        _ctx.Dispose(); _device.Dispose();
        _d3d?.Dispose();
    }
}
