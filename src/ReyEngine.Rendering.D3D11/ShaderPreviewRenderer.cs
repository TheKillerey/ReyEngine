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

    /// <summary>M228: the map's own sun and lightmap values, when a map scene supplied them. Null falls
    /// back to the UI sliders and a neutral scale.</summary>
    public Vector4? MapSunColor;
    public Vector3? MapSunDirection;
    public float? MapLightMapScale;

    /// <summary>M229: the map's depth-fog colour and its RAW fogStartAndEnd, in Riot's own convention -
    /// the shader consumes them unmodified, so they must NOT be normalised to (near, far) here.</summary>
    public Vector4? MapFogColor;
    public Vector2? MapFogStartEnd;

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

    /// <summary>M232: draw this material with additive blending rather than straight alpha. Set from the
    /// emitter's blendMode; see VfxShaderFlags for how that integer is read and what is still a guess.</summary>
    public bool Additive { get; set; }

    public IEnumerable<string> UnboundTextures =>
        PsRefl.Textures.Concat(VsRefl.Textures).Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !Textures.ContainsKey(n));

    /// <summary>M226: the texture views are NOT owned here. They live in the renderer's pool, shared by
    /// every material that binds the same asset - a Map12 load binds 1,841 slots from 138 distinct files,
    /// and one lightmap atlas is shared by 282 of them. Disposing per material would release a view other
    /// materials are still using, which blanks textures or crashes.</summary>
    /// <summary>M242: false when Vs/Ps/Layout came from the pipeline cache and are shared with other
    /// materials. Disposing a shared shader object is a use-after-free that shows up as a device removal
    /// on some later frame, a long way from the cause - so ownership is explicit rather than assumed.</summary>
    public bool OwnsPipeline { get; set; } = true;

    public void Dispose()
    {
        if (OwnsPipeline) { Vs.Dispose(); Ps.Dispose(); Layout.Dispose(); }
        // Constant buffers are ALWAYS per-material: they hold this material's uploaded values, so two
        // materials sharing a pipeline still need their own. Only the immutable objects are shared.
        foreach (var b in VsCbs.Values) b.Dispose();
        foreach (var b in PsCbs.Values) b.Dispose();
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
        _white = MakeTexture(px, 1, 1) ?? default;

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
        _identityRamp = MakeTexture(new byte[] { 0, 0, 0, 0 }, 1, 1) ?? default;
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

    /// <summary>M232: the additive blend state, selected per material by <see cref="PreviewMaterial.Additive"/>.</summary>
    private ComPtr<ID3D11BlendState> _blendAdditive;

    public void ClearMaterials()
    {
        foreach (var m in _materials) m.Dispose();
        _materials.Clear();
        foreach (var b in _sharedCbs.Values) b.Dispose();
        _sharedCbs.Clear();
        // M226: the pool outlives individual materials but not the scene
        foreach (var t in _texPool.Values) t.Dispose();
        foreach (var t in _retired) t.Dispose();
        _texPool.Clear();
        _retired.Clear();
        _comparePs.Dispose();
        _comparePs = default;
    }

    /// <summary>M242: the immutable half of a pipeline - the two shader objects and the input layout.
    /// Everything else about a material (constant buffer contents, textures, draw range) differs per
    /// material and is not shared.</summary>
    private sealed class CachedPipeline
    {
        public ComPtr<ID3D11VertexShader> Vs;
        public ComPtr<ID3D11PixelShader> Ps;
        public ComPtr<ID3D11InputLayout> Layout;

        public void Dispose() { Vs.Dispose(); Ps.Dispose(); Layout.Dispose(); }
    }

    private readonly Dictionary<PipelineKey, CachedPipeline> _pipelines = new();

    /// <summary>Pipelines currently held, and how many builds the cache satisfied without touching the
    /// driver. Surfaced so a scene report can show the ratio rather than claim an improvement.</summary>
    public int CachedPipelineCount => _pipelines.Count;
    public int PipelineCacheHits { get; private set; }
    public int PipelineCacheMisses { get; private set; }

    /// <summary>The game build the cache is keyed against. Set by the host; changing it does not by itself
    /// invalidate anything, because the bytecode hash already covers correctness - this is what makes the
    /// cache PRUNABLE on patch day and what a user-facing message keys on.</summary>
    public string GameVersion { get; set; } = "unknown";

    /// <summary>Drop every cached pipeline. The scene's materials must be gone first - they hold
    /// non-owning references to exactly these objects.</summary>
    public void ClearPipelineCache()
    {
        foreach (var pl in _pipelines.Values) pl.Dispose();
        _pipelines.Clear();
        PipelineCacheHits = PipelineCacheMisses = 0;
    }

    /// <summary>M214: bring up one material's pipeline. <paramref name="indexCount"/> below zero means the
    /// whole index buffer, which is what the bench uses.
    ///
    /// <para>M242: the shader objects and input layout come from the pipeline cache when an identical
    /// (shader, permutation, state, backend) combination has already been built. Map12 was building 921 of
    /// these for 120 material names, and CreateVertexShader / CreatePixelShader / CreateInputLayout are the
    /// expensive calls in here - they are where the driver compiles.</para></summary>
    public PreviewMaterial? BuildMaterial(string name, DxbcShader vsRefl, DxbcShader psRefl,
        int startIndex, int indexCount, out ShaderLoadReport r,
        ShaderDescription? vsDesc = null, ShaderDescription? psDesc = null,
        StateDescription? state = null)
    {
        r = new ShaderLoadReport();
        if (_device.Handle is null) { r.Error = "no D3D11 device"; return null; }

        // Only cacheable when the caller supplied the descriptions that identify the variant. Without them
        // there is no honest key - two materials could share a shader NAME and differ in permutation - so
        // the uncached path stays, rather than inventing a key that might collide.
        PipelineKey? key = vsDesc is not null && psDesc is not null
            ? PipelineKey.For(vsDesc, psDesc, state ?? StateDescription.Geometry, GameVersion, RenderBackend.D3D11)
            : null;

        if (key is { } k && _pipelines.TryGetValue(k, out var hit))
        {
            PipelineCacheHits++;
            var shared = new PreviewMaterial
            {
                Name = name, VsRefl = vsRefl, PsRefl = psRefl,
                StartIndex = startIndex, IndexCount = indexCount,
                OwnsPipeline = false,
                Vs = hit.Vs, Ps = hit.Ps, Layout = hit.Layout,
            };
            // Constant buffers are per material even on a hit - same layout, different contents.
            CreateConstantBuffers(vsRefl, shared.VsCbs, r, "vertex");
            CreateConstantBuffers(psRefl, shared.PsCbs, r, "pixel");
            r.Step($"pipeline cache HIT ({_pipelines.Count} resident)");
            r.Success = true;
            return shared;
        }
        if (key is not null) PipelineCacheMisses++;

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

        // M242: publish the immutable half so the next material with the same key skips the driver calls
        // above. The material keeps using its own handles - the cache holds the SAME COM objects, and the
        // ownership flag is what stops the material from releasing them out from under the cache.
        if (key is { } store)
        {
            _pipelines[store] = new CachedPipeline { Vs = mat.Vs, Ps = mat.Ps, Layout = mat.Layout };
            mat.OwnsPipeline = false;
            r.Step($"pipeline cached ({_pipelines.Count} resident)");
        }

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

    /// <summary>M231: does this material's vertex shader want mProj to be the whole world-to-clip transform?
    /// True when it uses mProj without a bone palette, which across the cache selects exactly the particle
    /// shaders. Null material (the built-in comparison path) keeps the champion reading.</summary>
    private static bool ParticleStyleProjection(PreviewMaterial? mat)
    {
        if (mat is null) return false;

        // A bone palette means the champion reading: bones already applied object-to-view.
        if (mat.VsRefl.ConstantBuffers.Any(cb => cb.Name.Contains("Bone", StringComparison.OrdinalIgnoreCase)))
            return false;

        // env_scrollingdiffuse and the four tft_* shaders use mProj AND a view-projection matrix. Whatever
        // their mProj is for, it is not the world-to-clip transform - that job is already taken - so they are
        // explicitly excluded rather than swept in by "has no bones".
        if (mat.VsRefl.ConstantBuffers.Any(cb => cb.Variables.Any(v => v.IsUsed
                && v.Name.Contains("VIEW_PROJECTION", StringComparison.OrdinalIgnoreCase))))
            return false;

        return true;
    }

    /// <summary>Tiles a float4 <paramref name="count"/> times - for cbuffer array constants.</summary>
    private static float[] Repeat(float[] v, int count)
    {
        var r = new float[v.Length * count];
        for (int i = 0; i < count; i++) Array.Copy(v, 0, r, i * v.Length, v.Length);
        return r;
    }

    /// <summary>Semantic → byte offset inside <see cref="PreviewVertex"/>.</summary>
    private static (uint Offset, int Components, bool Known) MapSemantic(string semantic, int index, int declared)
        => (semantic.ToUpperInvariant(), index) switch
        {
            ("POSITION", 0) => (0u, 3, true),
            ("NORMAL", 0) => (12u, 3, true),
            ("TANGENT", 0) => (24u, 4, true),
            // M232: four components. quad_vs packs frame index and erosion drive into .zw - see PreviewVertex.
            ("TEXCOORD", 0) => (40u, 4, true),
            ("TEXCOORD", 1) => (56u, 2, true),
            ("TEXCOORD", 2) => (64u, 2, true),
            ("TEXCOORD", 3) => (72u, 2, true),
            ("TEXCOORD", 5) => (136u, 3, true),                  // M230: the grass clump pivot
            ("TEXCOORD", 7) => (80u, 2, true),                   // M224: the lightmap UV
            ("COLOR", 0) => (88u, 4, true),
            ("BLENDWEIGHT", 0) => (104u, 4, true),
            ("BLENDINDICES", 0) => (120u, 4, true),
            _ => (152u, Math.Max(1, declared), false),           // the zero pad
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

    /// <summary>M232: true when the current buffers were made writable by <see cref="SetDynamicMesh"/>.</summary>
    private bool _dynamicMesh;
    private int _vbCapacity, _ibCapacity;

    /// <summary>
    /// <para>M232: allocate DYNAMIC vertex and index buffers big enough for <paramref name="maxVertices"/>,
    /// so animated particle geometry can be rewritten every frame with Map/WriteDiscard instead of
    /// recreating a buffer per frame.</para>
    ///
    /// <para>Re-allocates only when the request outgrows what is already there, so a system whose particle
    /// count fluctuates settles on one allocation rather than thrashing.</para>
    /// </summary>
    public void SetDynamicMesh(int maxVertices, int maxIndices)
    {
        if (_dynamicMesh && maxVertices <= _vbCapacity && maxIndices <= _ibCapacity) return;

        _vb.Dispose(); _ib.Dispose();
        _vb = default; _ib = default;
        _vbCapacity = Math.Max(maxVertices, 4);
        _ibCapacity = Math.Max(maxIndices, 6);

        var vdesc = new BufferDesc
        {
            ByteWidth = (uint)(_vbCapacity * PreviewVertex.SizeInBytes),
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.VertexBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> vb = default;
        _device.CreateBuffer(in vdesc, null, ref vb);
        _vb = vb;

        var idesc = new BufferDesc
        {
            ByteWidth = (uint)(_ibCapacity * sizeof(uint)),
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.IndexBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> ib = default;
        _device.CreateBuffer(in idesc, null, ref ib);
        _ib = ib;

        _dynamicMesh = true;
        _indexCount = 0;
        Mesh = null;
    }

    /// <summary>M232: overwrite the dynamic buffers for this frame. Silently no-ops when the buffers are
    /// immutable, so a caller that forgot SetDynamicMesh gets a still frame rather than a device removal.</summary>
    public void UpdateDynamicMesh(PreviewVertex[] vertices, int vertexCount, uint[] indices, int indexCount)
    {
        if (!_dynamicMesh || _vb.Handle is null || _ib.Handle is null) return;
        vertexCount = Math.Min(vertexCount, _vbCapacity);
        indexCount = Math.Min(indexCount, _ibCapacity);

        MappedSubresource mv = default;
        if (_ctx.Map(_vb, 0, Map.WriteDiscard, 0, ref mv) >= 0)
        {
            fixed (PreviewVertex* src = vertices)
                System.Buffer.MemoryCopy(src, mv.PData,
                    (long)_vbCapacity * PreviewVertex.SizeInBytes,
                    (long)vertexCount * PreviewVertex.SizeInBytes);
            _ctx.Unmap(_vb, 0);
        }

        MappedSubresource mi = default;
        if (_ctx.Map(_ib, 0, Map.WriteDiscard, 0, ref mi) >= 0)
        {
            fixed (uint* src = indices)
                System.Buffer.MemoryCopy(src, mi.PData, (long)_ibCapacity * sizeof(uint),
                    (long)indexCount * sizeof(uint));
            _ctx.Unmap(_ib, 0);
        }

        _indexCount = indexCount;
    }

    public void SetMesh(PreviewMesh mesh)
    {
        _dynamicMesh = false;
        _vbCapacity = _ibCapacity = 0;
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

    /// <summary>M226: decoded textures, keyed by the asset path they came from and shared by every
    /// material that wants them. The renderer owns these; <see cref="PreviewMaterial"/> only references
    /// them.</summary>
    private readonly Dictionary<string, ComPtr<ID3D11ShaderResourceView>> _texPool = new(StringComparer.Ordinal);

    /// <summary>Bind an already-decoded asset from the pool. Returns false when it has not been decoded yet,
    /// which is the caller's cue to read and decode it - and the point of the whole thing, because on a hit
    /// neither the WAD read nor the decode happens. A Map12 load decoded one 2048 lightmap 282 times at
    /// 35.8 ms each before this existed.</summary>
    public bool TryBindCached(PreviewMaterial m, string reflectedName, string key)
    {
        if (!_texPool.TryGetValue(key, out var srv) || srv.Handle is null) return false;
        m.Textures[reflectedName] = srv;
        return true;
    }

    public bool IsCached(string key) => _texPool.TryGetValue(key, out var v) && v.Handle is not null;

    /// <summary>Bind RGBA8 pixels to a reflected texture name on EVERY material that declares it. That is
    /// what the bench wants; a scene binds per material instead.</summary>
    public void SetTexture(string reflectedName, byte[] rgba, int width, int height)
    {
        foreach (var m in _materials) SetTexture(m, reflectedName, rgba, width, height);
        if (_materials.Count > 0) Log($"bound texture '{reflectedName}' ({width}x{height})");
    }

    /// <summary>Bench path: no asset identity, so it gets a private pool entry per slot.</summary>
    public void SetTexture(PreviewMaterial m, string reflectedName, byte[] rgba, int width, int height)
        => SetTexture(m, reflectedName, "\u0000bench:" + reflectedName, rgba, width, height);

    /// <summary>Scene path: <paramref name="key"/> is the asset path, so the view is created once and
    /// shared.</summary>
    public void SetTexture(PreviewMaterial m, string reflectedName, string key, byte[] rgba, int width, int height)
    {
        // ALWAYS create. Skipping the work on a cache hit is the CALLER's job, via TryBindCached - if this
        // reused the pooled view whenever the key existed, re-binding a different image to the same slot
        // from the Textures tab would silently keep showing the old one.
        var made = MakeTexture(rgba, width, height);
        if (made is null) return;                           // creation failed and was reported

        // A view already under this key may still be referenced by materials bound earlier, so it is
        // retired rather than disposed here; the whole set goes at ClearTextures.
        if (_texPool.TryGetValue(key, out var previous) && previous.Handle is not null) _retired.Add(previous);

        _texPool[key] = made.Value;
        m.Textures[reflectedName] = made.Value;
    }

    /// <summary>Views replaced while something might still hold them. Freed with the rest of the pool.</summary>
    private readonly List<ComPtr<ID3D11ShaderResourceView>> _retired = new();

    public void ClearTextures()
    {
        foreach (var m in _materials) m.Textures.Clear();
        foreach (var t in _texPool.Values) t.Dispose();
        foreach (var t in _retired) t.Dispose();
        _texPool.Clear();
        _retired.Clear();
    }

    /// <summary>How many distinct assets are resident, for the scene report.</summary>
    public int CachedTextureCount => _texPool.Count;

    /// <summary>M226: returns null on failure instead of a null-handle view. Neither HRESULT was checked
    /// before, so a failed creation still got stored under its key and then bound as a null resource -
    /// which samples BLACK rather than falling through to the white stand-in. BuildMaterial already checks
    /// its shader HRESULTs; this was the one place that did not.</summary>
    private ComPtr<ID3D11ShaderResourceView>? MakeTexture(byte[] rgba, int w, int h)
    {
        if (w <= 0 || h <= 0 || rgba.Length < w * h * 4)
        {
            Log($"texture rejected: {w}x{h} needs {(long)w * h * 4} bytes, got {rgba.Length}");
            return null;
        }

        var desc = new Texture2DDesc
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.FormatR8G8B8A8Unorm, SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource,
        };
        ComPtr<ID3D11Texture2D> tex = default;
        int hr;
        fixed (byte* p = rgba)
        {
            var sub = new SubresourceData { PSysMem = p, SysMemPitch = (uint)(w * 4) };
            hr = _device.CreateTexture2D(in desc, in sub, ref tex);
        }
        if (hr < 0) { Log($"CreateTexture2D failed 0x{hr:X8} for {w}x{h}"); return null; }

        ComPtr<ID3D11ShaderResourceView> srv = default;
        hr = _device.CreateShaderResourceView(tex, null, ref srv);
        tex.Dispose();
        if (hr < 0) { Log($"CreateShaderResourceView failed 0x{hr:X8} for {w}x{h}"); return null; }
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

        // M232: additive, for particle emitters whose blendMode says so. Same source factor, but the
        // destination ADDS instead of being scaled down, and alpha is left alone.
        var abd = new BlendDesc();
        abd.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = Blend.SrcAlpha, DestBlend = Blend.One, BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.Zero, DestBlendAlpha = Blend.One, BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        _blendAdditive.Dispose();
        ComPtr<ID3D11BlendState> abs = default;
        _device.CreateBlendState(in abd, ref abs);
        _blendAdditive = abs;

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
                    // M231: mProj is TWO different matrices depending on who is asking, and the shader
                    // itself does not say which - only the company it keeps does.
                    //
                    // Censused every vertex stage in the cache and the split is total:
                    //   235 use mProj AND declare a bone buffer  -> champions. The bone palette carries
                    //       object-to-VIEW (see BonePose), so mProj is projection ALONE.
                    //    17 use mProj and declare NO bone buffer -> and all 17 are particle shaders
                    //       (particles/* and particlesystem/*). Nothing else is in that set.
                    //
                    // For the particle case mProj must be the FULL world-to-clip transform, which
                    // particlesystem/quad_vs proves directly: it computes POSITION - vCamera, and vCamera is
                    // a world-space camera position, so POSITION is world-space and mProj has to finish the
                    // job. Binding projection alone there puts the quad on the near plane.
                    //
                    // Five staticmesh shaders (env_scrollingdiffuse, tft_*) use mProj AND a VP matrix. They
                    // are outside this rule and keep the old behaviour; nothing has measured what their
                    // mProj is for.
                    "MPROJ" => Mat(ParticleStyleProjection(mat) ? vp : proj, s),
                    "VCAMERA" or "CAMERA_POSITION" => new[] { cam.X, cam.Y, cam.Z, 1f },

                    // M231: the particle flipbook atlas descriptor. Derived from quad_vs, which spends it as
                    //     col = frame - floor(frame * TEXTURE_INFO.y) * TEXTURE_INFO.x
                    //     out.uv = (col + u) * TEXTURE_INFO.y, (row + v) * TEXTURE_INFO.z
                    // so x = columns, y = 1/columns, z = 1/rows. A single-frame particle is a 1x1 atlas,
                    // which passes the UV through unchanged - the right default for a still preview, and the
                    // only one that does not silently crop the texture to a sub-rectangle.
                    "TEXTURE_INFO" => new[] { 1f, 1f, 1f, 0f },

                    // M231. Shifts the quad along the view ray to bias it in the depth test; zero is
                    // "where the emitter put it". NOTE: this case was accidentally deleted by the M234
                    // patch and restored here - the unbound-constant report is what caught it.
                    "PARTICLE_DEPTH_PUSH_PULL" or "EMITTER_DEPTH_PUSH_PULL" or "CAMERAOFFSET"
                        => new[] { 0f, 0f, 0f, 0f },

                    // M234: DERIVED, by disassembling the permutation each define selects. The earlier
                    // attempt reasoned from field names, made two permutations worse and none better, and
                    // was reverted; these are read off the arithmetic instead, and each is chosen so the
                    // stage is a provable no-op for ALL inputs rather than merely plausible.

                    // ALPHA_EROSION, from ps blob 15:
                    //     dp4_sat r0.y, erosionTexel, cb0[1]     // e = sat(dot(texel, MIXER))
                    //     add     r0.x, r0.y, -cb0[0].y
                    //     add     r0.xy, -r0.xyxx, v3.zzzz       // x = drive-e+P.y , y = drive-e
                    //     mul_sat r0.xy, r0.xyxx, cb0[0].zwzz    // x *= P.z , y *= P.w
                    //     add     r0.x, -r0.y, r0.x              // mask = sat(x) - sat(y)
                    // With drive and e both in [0,1], drive-e is in [-1,1]. P.y = 2 puts x in [1,3] so
                    // sat(x) is 1 everywhere, and P.w = 0 forces sat(y) = 0, giving mask = 1 for every
                    // input. P.y = 1 was the earlier guess and it fails exactly when the erosion map is
                    // the WHITE stand-in: e = 1, drive = 0, sat((0-1+1)*1) = 0, sprite fully erased. That
                    // is why those permutations rendered blank. P.x is not referenced at all here.
                    "CALPHAEROSIONPARAMS" => new[] { 0f, 2f, 1f, 0f },
                    // Dotted against the erosion texel; the census measured (1,0,0,0) - red - on ~74%.
                    "CALPHAEROSIONTEXTUREMIXER" => new[] { 1f, 0f, 0f, 0f },

                    // ALPHA_TEST, from ps blob 8:
                    //     mad r1.x, v1.w, r0.w, -cb0[0].x   // vColor.a * tex.a - REF
                    //     lt  r1.x, r1.x, l(0)
                    //     discard_nz r1.x
                    // so a reference of 0 discards only where alpha is negative, i.e. never.
                    "ALPHATESTREFERENCEVALUE" => new[] { 0f, 0f, 0f, 0f },

                    // PALETTIZE_TEXTURES, from ps blob 21:
                    //     dp4_sat r1.x, spriteTexel, cb0[1]   // u = sat(dot(texel, SRC_MIXER))
                    //     add     r1.x, r1.x, cb0[0].z        // u += Select.z
                    //     add     r1.y, cb0[0].w, cb0[0].x    // v  = Select.w + Select.x
                    //     sample  paletteStrip(u, v)
                    // There is no bypass - the palette colour REPLACES the sprite's - so this stage cannot
                    // be neutralised, only fed sanely: row 0, and the red channel driving the lookup, which
                    // is the same convention the erosion mixer uses. A real emitter overrides both.
                    "CPALETTESELECTMAIN" => new[] { 0f, 0f, 0f, 0f },
                    "CPALETTESRCMIXERMAIN" => new[] { 1f, 0f, 0f, 0f },

                    // SOFT_PARTICLES, from ps blob 129:
                    //     d    = 1/(sceneDepth*DC.y + DC.x) - 1/(SV_Position.z*DC.y + DC.x)
                    //     fade = smoothstep(sat((d - P.x)*P.z)) - smoothstep(sat((d - P.y)*P.w))
                    // P.x = -1e6 with P.z = 1 makes the first term sat(d + 1e6) = 1 for any finite d, and
                    // P.w = 0 makes the second sat(0) = 0, so fade = 1 - 0 = 1 regardless of what depth
                    // the stand-in texture holds. The earlier guess (0, 1e6, 0, 1e6) evaluated to 0 - 0 = 0
                    // - fully transparent - which is why SOFT_PARTICLES never drew a pixel.
                    "CSOFTPARTICLEPARAMS" => new[] { -1e6f, 0f, 1f, 0f },
                    // M234b: NOT a parameter - a SELECTOR, and calling it unused was what made every
                    // SOFT_PARTICLES permutation render nothing. The tail of ps blob 129 is
                    //     mad o0.xyz, cb0[1].xxxx, colour, fade*colour*cb0[1].y
                    //     mad o0.w,   cb0[1].z,    alpha,  fade*alpha *cb0[1].w
                    // so it picks, per channel, between the un-faded value and the faded one. All zeros
                    // multiplies the whole output by zero, which is exactly the black frame observed.
                    //
                    // Which channel should carry the fade depends on how the emitter blends, which is
                    // presumably why Riot made it selectable at all: an additive sprite is invisible when
                    // its RGB goes to zero and ignores alpha entirely, while an alpha-blended one is the
                    // other way round.
                    "CSOFTPARTICLECONTROL" => mat is { Additive: true }
                        ? new[] { 0f, 1f, 1f, 0f }      // additive: fade the RGB, leave alpha
                        : new[] { 1f, 0f, 0f, 1f },     // alpha:    leave RGB, fade the alpha
                    // Feeds two reciprocals. Both terms must stay finite or d is NaN and the quad vanishes,
                    // so neither component may be zero.
                    "CDEPTHCONVERSIONPARAMS" => new[] { 1f, 1f, 0f, 0f },

                    // M232: MULT_PASS's second flipbook atlas descriptor, same shape as TEXTURE_INFO
                    // and derived the same way (M231).
                    "TEXTURE_INFO_2" => new[] { 1f, 1f, 1f, 0f },
                    "KCOLORFACTOR" => new[] { 1f, 1f, 1f, 1f },
                    "TIME" => new[] { s.TimeSeconds, s.TimeSeconds * 0.5f, MathF.Sin(s.TimeSeconds), 1f },
                    // M228: this points TOWARD the sun, and getting it backwards makes flat ground black.
                    //
                    // From staticmesh/defaultenv_flat ps blob 59:
                    //     dp3 r0.x, normal, cb1[7].yzw      // N . SUN_LIGHT_DIRECTION
                    //     max r0.x, r0.x, l(0.000000)
                    //     mad r0.xyz, r0.xxxx, occl*SUN_COLOR, baked*SCALE
                    //
                    // So a direction pointing DOWN - which is what the UI slider naturally produces, and what
                    // the comparison shader wants - gives max(negative, 0) = 0 on every up-facing surface.
                    // The whole sun term vanishes and the only light left is the baked one, whose atlases
                    // measure a mean of 6.5-50 out of 255 on Map12. That reads as black ground under lit
                    // walls, which is exactly what was reported. MapSunProperties.SunDirection defaults to
                    // (0,1,0) - up - which independently says the stored convention is toward-the-sun.
                    "SUN_LIGHT_DIRECTION" => s.MapSunDirection is { } msd
                        ? new[] { msd.X, msd.Y, msd.Z, 0f }
                        : new[] { -s.SunDirection.X, -s.SunDirection.Y, -s.SunDirection.Z, 0f },

                    "SUN_LIGHT_COLOR" => s.MapSunColor is { } msc
                        ? new[] { msc.X, msc.Y, msc.Z, msc.W }
                        : new[] { s.SunColor.X, s.SunColor.Y, s.SunColor.Z, s.SunColor.W },
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

                    // M229: DEPTH FOG, and all-zeros here is what turned every mesh below world Y = 0
                    // completely black. From staticmesh/defaultenv_flat ps blob 152 - the base permutation,
                    // which is what an ordinary map material resolves to:
                    //
                    //     add     r0.w, -cb1[10].y, cb1[10].x     // start - end
                    //     div     r0.w, l(1.0), r0.w              // 1 / (start - end)
                    //     add     r1.x, v1.w, -cb1[10].y          // v1.w is WORLD Y, from the VS
                    //     mul_sat r0.w, r0.w, r1.x                // t = saturate((worldY - end)/(start - end))
                    //     ... smoothstep(t), * 2.88539, exp2, reciprocal -> fogFactor
                    //     mad o0.xyz, fogFactor, (fogColour - lit), lit
                    //
                    // With start = end = 0 the divide is 1/0 = INF, so t becomes a STEP at worldY = 0:
                    // 1 above it and 0 below. That makes fogFactor 0.135 above and exactly 1.0 below, and
                    // a fogFactor of 1 replaces the pixel with the fog colour outright - which was black,
                    // because nothing supplied ENV_FOG_COLOR either. Hence "meshes under a specific y value
                    // go fully black", and only partly on a mesh that straddles zero.
                    //
                    // Riot stores fogStartAndEnd negative and "reversed" (Twisted Treeline ships
                    // -10000, -50000). The shader consumes them raw, so they are passed through raw rather
                    // than through TryGetFogRange's (near, far) normalisation.
                    "ENV_FOG_START_END_SCALE_EMISSIVE_REMAP" => s.MapFogStartEnd is { } fse
                        ? new[] { fse.X, fse.Y, 1f, 1f }
                        // No map fog: pick a range wide enough that t saturates to 1 everywhere, so the
                        // factor is the uniform 0.135 minimum and there is no cliff. The stage cannot be
                        // switched off from the constants - 0.135 is the floor of 1/exp2(smoothstep*2.885).
                        : new[] { 1f, -1e9f, 1f, 1f },

                    "ENV_FOG_COLOR" or "ENV_FOG_ALT_COLOR" => s.MapFogColor is { } fc
                        ? new[] { fc.X, fc.Y, fc.Z, fc.W }
                        : new[] { 0f, 0f, 0f, 1f },

                    // Below this world height the engine treats everything as permanently visible. Nothing
                    // in the preview should ever be force-fogged, so push it above any real geometry.
                    "FOG_OF_WAR_ALWAYS_BELOW_Y" => new[] { 1e9f, 1e9f, 1e9f, 1e9f },

                    // M230: the ten grass-flattening spheres - one per nearby unit, the reason grass parts as
                    // a champion walks through it. Leaving them zero did not merely disable the effect, it
                    // made grass VANISH. staticmesh/vertexdeform vs blob 25:
                    //
                    //     add  r7.xyz, -r3.xyzx, r7.xyzx    // sphereXZ - pivotXZ, both (0,0,0)
                    //     dp3  r1.w, r7.yzxy, r7.yzxy       // 0
                    //     rsq  r2.w, r1.w                   // rsq(0) = +INF
                    //     mul  r7.xyz, r2.wwww, r7.xyzx     // INF * 0 = NaN
                    //     ...
                    //     add  r6.xyz, r6.xyzx, r7.xyzx     // NaN accumulates over all ten iterations
                    //     add  r1.xyz, r1.xyzx, r6.xyzx     // and lands in the output POSITION
                    //
                    // A NaN vertex position makes the rasteriser discard the triangle, so all 104,876
                    // triangles of Map12 grass drew nothing at all.
                    //
                    // The inert state is "no unit is standing in the grass": spheres far away, radius zero.
                    // Both loops then resolve to exactly no effect rather than to NaN -
                    //   distortion: len is huge and (spread*radius*velocity) is 0, so div_sat gives 1, the
                    //     angle works out to (1*-0.1 + 0.1)*2pi = 0, and sincos(0) is the identity rotation;
                    //   see-through alpha: t = saturate((dist - R)/R) reaches 1 well before 2R, and the final
                    //     lerp(SeeThroughAlphaMin, SeeThroughAlphaMax, 1) is fully opaque.
                    // 1e6 is ~60x outside any real map yet squares to 3e12, far inside fp32 range.
                    "GrassDistortSpheres" => Repeat(new[] { 1e6f, 1e6f, 1e6f, 0f }, 10),
                    "GrassVelocities" => Repeat(new[] { 0f, 0f, 0f, 0f }, 10),

                    // Scales the velocity term above. Neutral at 1; with zero velocities it is moot either way.
                    "VelocityStrength" => new[] { 1f, 1f, 1f, 1f },

                    // The mesh's own centre, and NOT only a distance reference: it is also the wave's phase
                    // offset, sin(sin(cx+cy+cz) + WaveFrequency*TIME), which is how Riot stops every clump on
                    // the map from swaying in lockstep. The scene loader overrides this per material slice
                    // via Params; this fallback is for a single-mesh preview, already centred on the origin.
                    "MESH_CENTER" => new[] { 0f, 0f, 0f, 0f },

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
                    // M228: a SCALAR at PerFramePixelCB+128 (ENV_FOG_COLOR sits at +132, so the float4
                    // stand-in was only ever safe because FillConstantBuffer clamps to v.Size/4 = 1 float).
                    // The shader does baked.rgb * this. Map data records 2 for Map12 and 0.6 for Map11, so
                    // it is per map and must come FROM the map rather than be hardcoded either way.
                    "LIGHT_MAP_COLOR_SCALE_AND_INTENSITY" => s.MapLightMapScale is { } lms
                        ? new[] { lms, lms, lms, lms }
                        : new[] { 1f, 1f, 1f, 1f },

                    // M224: the lightmap UV transform, and the single biggest cause of black map ground -
                    // 899 materials on Map12 read it and nothing supplied it, so it uploaded as zero and
                    // collapsed every lightmap lookup onto texel (0,0) of the atlas.
                    //
                    // Identity rather than the map's real values, because MapGeoDecoder has ALREADY applied
                    // the per-mesh scale and bias when it produced the lightmap UVs (uv7 * scale + bias).
                    // Applying it a second time here would be the error this replaces, in the other
                    // direction.
                    "BAKED_LIGHT_SCALE_AND_BIAS" => new[] { 1f, 1f, 0f, 0f },
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

    /// <summary>M231: unit vector from the origin toward the camera - which is what a billboard at the origin
    /// needs to face. Uses the supplied camera position when a scene set one, otherwise the orbit.</summary>
    public static Vector3 CameraForward(PreviewSettings s)
    {
        var c = s.SuppliedCameraPosition ?? CameraPosition(s);
        return c.LengthSquared() > 1e-8f ? Vector3.Normalize(c) : Vector3.UnitZ;
    }

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

            // M232: blend is per MATERIAL, not per frame. Particle emitters in one system routinely mix
            // additive and straight-alpha passes, so binding one state before the loop cannot represent
            // them. Non-particle materials leave Additive false and get exactly the previous behaviour.
            if (mat.Additive && _blendAdditive.Handle is not null)
                _ctx.OMSetBlendState(_blendAdditive, factor, 0xFFFFFFFF);
            else
                _ctx.OMSetBlendState(_blend, factor, 0xFFFFFFFF);

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
        ClearTextures();
        _white.Dispose();
        _identityRamp.Dispose();
        // M242: the cache owns shader objects that no material releases, so it must be drained here or
        // every pipeline ever built leaks for the lifetime of the process.
        ClearPipelineCache();
        _vb.Dispose(); _ib.Dispose(); _compareCb.Dispose();
        _rtv.Dispose(); _rt.Dispose(); _stage.Dispose(); _dsv.Dispose(); _depth.Dispose();
        _linearWrap.Dispose(); _linearClamp.Dispose();
        _raster.Dispose(); _blend.Dispose(); _depthState.Dispose();
        _ctx.Dispose(); _device.Dispose();
        _d3d?.Dispose();
    }
}
