using System.Linq;
using System.Numerics;
using Silk.NET.OpenGL;

namespace ReyEngine.Rendering.Vfx;

/// <summary>
/// Draws simulated VFX particles (M36) as camera-facing, textured billboards using hardware instancing.
/// One draw call per emitter (its texture + blend + flipbook). Additive or alpha blended, depth-tested
/// against the scene but not depth-writing, so glows read through geometry without sorting artefacts.
/// GLSL is ASCII-only (non-ASCII bytes break the GL driver's lexer -> blank output).
/// </summary>
public sealed class VfxParticleRenderer
{
    private GL _gl = null!;
    private uint _program, _vao, _quadVbo, _instVbo;
    private int _uViewProj, _uCamRight, _uCamUp, _uTexDiv, _uTex, _uUvScrollRate;
    private int _uTexMult, _uHasTexMult, _uTexDivMult, _uUvScrollRateMult;
    private int _uIsDistortion, _uDistortionTex, _uSceneTex, _uViewportSize, _uDistortionStrength;
    private int _uAlphaRef;   // M174 (1.4)
    // M174 (2.3): the UV transform stack.
    private int _uErosionTex, _uErosionParams, _uErosionMixer, _uHasErosion;   // M174 (2.1)
    private int _uUvOffset, _uUvScale, _uUvScrollInt, _uUvRotation, _uUvClamp;
    private int _uEmitterUvScroll, _uUvFlip, _uUvRotInt, _uUvRotRate, _uUvCenter, _uEmitterAge;
    private int _uDirectionOriented, _uArbitraryQuad;
    private int _uPlacementRight, _uPlacementUp, _uPlacementForward;
    private int _instCapFloats;
    private bool _ready;
    private readonly List<uint> _ownedTextures = new();
    /// <summary>M117c: per uploaded texture, whether its alpha channel varies (any pixel below ~1.0).
    /// Drives the mode-3 blend split — see <see cref="IsAdditiveFor"/>.</summary>
    private readonly Dictionary<uint, bool> _texHasAlpha = new();
    private uint _sceneTexture;
    private int _sceneWidth, _sceneHeight;

    // M174 (2.1): the 19th float is the per-particle alpha-erosion drive. Riot's own quad path passes
    // it the same way - quad_vs reads vertex attribute TEXCOORD0.w into TEXCOORD3.z.
    //
    // INTERNAL, and the ONLY definition of the instance stride. VfxParticleSimulator sizes and fills the
    // buffer this describes, and until M174 it carried its own hardcoded copy of the number - so bumping
    // the stride here left the simulator allocating one float per particle too few and overrunning the
    // array on the first spawn. One constant, referenced from both sides, is what prevents that.
    internal const int Stride = 19;

    private bool _gles;

    public unsafe void Initialize(GL gl)
    {
        _gl = gl;
        bool gles = ShaderUtil.DetectGles(gl);
        _gles = gles;
        _program = ShaderUtil.CreateProgram(gl, gles, Vert, Frag);
        _uViewProj = gl.GetUniformLocation(_program, "uViewProj");
        _uCamRight = gl.GetUniformLocation(_program, "uCamRight");
        _uCamUp = gl.GetUniformLocation(_program, "uCamUp");
        _uTexDiv = gl.GetUniformLocation(_program, "uTexDiv");
        _uTex = gl.GetUniformLocation(_program, "uTex");
        _uTexMult = gl.GetUniformLocation(_program, "uTexMult");
        _uHasTexMult = gl.GetUniformLocation(_program, "uHasTexMult");
        _uTexDivMult = gl.GetUniformLocation(_program, "uTexDivMult");
        _uUvScrollRateMult = gl.GetUniformLocation(_program, "uUvScrollRateMult");
        _uUvScrollRate = gl.GetUniformLocation(_program, "uUvScrollRate");
        _uIsDistortion = gl.GetUniformLocation(_program, "uIsDistortion");
        _uDistortionTex = gl.GetUniformLocation(_program, "uDistortionTex");
        _uSceneTex = gl.GetUniformLocation(_program, "uSceneTex");
        _uViewportSize = gl.GetUniformLocation(_program, "uViewportSize");
        _uDistortionStrength = gl.GetUniformLocation(_program, "uDistortionStrength");
        _uAlphaRef = gl.GetUniformLocation(_program, "uAlphaRef");
        _uErosionTex = gl.GetUniformLocation(_program, "uErosionTex");
        _uErosionParams = gl.GetUniformLocation(_program, "uErosionParams");
        _uErosionMixer = gl.GetUniformLocation(_program, "uErosionMixer");
        _uHasErosion = gl.GetUniformLocation(_program, "uHasErosion");
        _uUvOffset = gl.GetUniformLocation(_program, "uUvOffset");
        _uUvScale = gl.GetUniformLocation(_program, "uUvScale");
        _uUvScrollInt = gl.GetUniformLocation(_program, "uUvScrollInt");
        _uUvRotation = gl.GetUniformLocation(_program, "uUvRotation");
        _uUvClamp = gl.GetUniformLocation(_program, "uUvClamp");
        _uEmitterUvScroll = gl.GetUniformLocation(_program, "uEmitterUvScroll");
        _uUvFlip = gl.GetUniformLocation(_program, "uUvFlip");
        _uUvRotInt = gl.GetUniformLocation(_program, "uUvRotInt");
        _uUvRotRate = gl.GetUniformLocation(_program, "uUvRotRate");
        _uUvCenter = gl.GetUniformLocation(_program, "uUvCenter");
        _uEmitterAge = gl.GetUniformLocation(_program, "uEmitterAge");
        _uDirectionOriented = gl.GetUniformLocation(_program, "uDirectionOriented");
        _uArbitraryQuad = gl.GetUniformLocation(_program, "uArbitraryQuad");
        _uPlacementRight = gl.GetUniformLocation(_program, "uPlacementRight");
        _uPlacementUp = gl.GetUniformLocation(_program, "uPlacementUp");
        _uPlacementForward = gl.GetUniformLocation(_program, "uPlacementForward");

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        // static base quad (4 corners, drawn as a triangle fan)
        float[] quad = { -0.5f, -0.5f, 0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f };
        _quadVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        fixed (float* q = quad)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), q, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        // per-instance buffer (filled per emitter each frame)
        _instVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instVbo);
        uint bstride = Stride * sizeof(float);
        gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, bstride, (void*)0);
        gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, bstride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(3); gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, bstride, (void*)(5 * sizeof(float)));
        gl.EnableVertexAttribArray(4); gl.VertexAttribPointer(4, 2, VertexAttribPointerType.Float, false, bstride, (void*)(9 * sizeof(float)));
        gl.EnableVertexAttribArray(5); gl.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, bstride, (void*)(11 * sizeof(float)));
        gl.EnableVertexAttribArray(6); gl.VertexAttribPointer(6, 3, VertexAttribPointerType.Float, false, bstride, (void*)(15 * sizeof(float)));
        gl.EnableVertexAttribArray(7); gl.VertexAttribPointer(7, 1, VertexAttribPointerType.Float, false, bstride, (void*)((Stride - 1) * sizeof(float)));
        gl.VertexAttribDivisor(1, 1);
        gl.VertexAttribDivisor(2, 1);
        gl.VertexAttribDivisor(3, 1);
        gl.VertexAttribDivisor(4, 1);
        gl.VertexAttribDivisor(5, 1);
        gl.VertexAttribDivisor(6, 1);
        gl.VertexAttribDivisor(7, 1);

        gl.BindVertexArray(0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _ready = true;
    }

    public unsafe uint UploadTexture(byte[] rgba, int width, int height)
    {
        // M117c: does this texture use its alpha channel at all? (>1% of pixels below ~opaque)
        int _n = width * height, _varied = 0;
        for (int _i = 3; _i < rgba.Length; _i += 4) if (rgba[_i] < 250) _varied++;
        bool _hasAlpha = _varied > _n / 100;

        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* p = rgba)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.GenerateMipmap(TextureTarget.Texture2D);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        // Repeat so mesh particles can scroll their UVs (waterfall flow); billboard/flipbook UVs stay in [0,1].
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _ownedTextures.Add(tex);
        _texHasAlpha[tex] = _hasAlpha;
        return tex;
    }

    /// <summary>Copy the current framebuffer color before particles draw. Distortion emitters sample this
    /// immutable scene copy, avoiding the framebuffer feedback loop forbidden by GLES.</summary>
    public unsafe void CaptureScene(uint width, uint height)
    {
        if (!_ready || width == 0 || height == 0) return;
        if (_sceneTexture == 0)
        {
            _sceneTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _sceneTexture);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }
        else _gl.BindTexture(TextureTarget.Texture2D, _sceneTexture);

        if (_sceneWidth != (int)width || _sceneHeight != (int)height)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, null);
            _sceneWidth = (int)width;
            _sceneHeight = (int)height;
        }
        _gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, width, height);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>Draw all emitters of the simulator. <paramref name="viewProj"/> and <paramref name="view"/>
    /// are the app's mirror-inclusive matrices (same ones passed to the mesh renderer).</summary>
    public unsafe void Render(VfxParticleSimulator sim, Matrix4x4 viewProj, Matrix4x4 view)
    {
        if (!_ready || sim.LiveParticleCount == 0) return;

        // camera basis in world space, derived from the (mirror-inclusive) view matrix's inverse, so
        // billboards face the camera and are oriented correctly on screen even under the -X mirror.
        Matrix4x4.Invert(view, out var inv);
        var camRight = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, inv));
        var camUp = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, inv));

        _gl.UseProgram(_program);
        _gl.UniformMatrix4(_uViewProj, 1, false, in viewProj.M11);
        _gl.Uniform3(_uCamRight, camRight.X, camRight.Y, camRight.Z);
        _gl.Uniform3(_uCamUp, camUp.X, camUp.Y, camUp.Z);
        _gl.Uniform1(_uTex, 0);
        _gl.Uniform1(_uTexMult, 1);
        _gl.Uniform1(_uSceneTex, 2);
        _gl.Uniform1(_uDistortionTex, 3);
        _gl.Uniform2(_uViewportSize, (float)_sceneWidth, (float)_sceneHeight);

        _gl.BindVertexArray(_vao);
        _gl.ActiveTexture(TextureUnit.Texture0);

        bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(false);                 // additive/alpha particles never write depth
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendEquation(GLEnum.FuncAdd);

        // M174 (1.3): draw in authored `pass` order. 1,114,110 emitters (79.7%) carry `pass`, with 2,913
        // distinct values across the full I16 range, and until now it was discarded entirely - so layered
        // effects composited in container order and additive glows landed under the sprites they belong
        // over. OrderBy is a STABLE sort, which gives container order as the tiebreak for free.
        //
        // Whether Riot sorts globally or per-system is UNKNOWN; this sorts within the whole frame's emitter
        // list, which matches per-system ordering whenever one system is previewed at a time.
        foreach (var es in sim.Emitters.OrderBy(static e => e.Def.Pass))
        {
            if (es.InstanceCount == 0) continue;
            // M47: mesh-primitive emitters draw their .scb/.sco geometry instead of billboards
            if (es.MeshVao != 0) { if (_meshProgram != 0) RenderMeshEmitter(es, viewProj); continue; }
            if (es.Texture == 0) continue;
            bool isDistortion = es.Def.Distortion is not null;
            if (isDistortion && (es.DistortionTexture == 0 || _sceneTexture == 0)) continue;

            int floats = es.InstanceCount * Stride;
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instVbo);
            fixed (float* d = es.Instances)
            {
                if (floats > _instCapFloats)
                {
                    _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(floats * sizeof(float)), d, BufferUsageARB.DynamicDraw);
                    _instCapFloats = floats;
                }
                else
                {
                    _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(floats * sizeof(float)), d);
                }
            }

            // Distortion replaces the covered scene sample through its normal-map mask; Riot's authored
            // blendMode=1 must not make that refracted sample additive (which would turn heat haze white).
            if (isDistortion) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            else if (IsAdditiveFor(es.Def.BlendMode, es.Texture)) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            // M174 (1.6): only a ZERO divisor is nonsense (95 emitters). Negative components (2,293 quad
            // emitters) and sub-1 components (390) are authored deliberately and were being clamped to 1,
            // which silently discarded them. What negative/fractional means in League is UNKNOWN - the
            // shader now divides by the value as authored, which for a negative component mirrors the axis.
            _gl.Uniform2(_uTexDiv, es.Def.TexDiv.X == 0 ? 1f : es.Def.TexDiv.X, es.Def.TexDiv.Y == 0 ? 1f : es.Def.TexDiv.Y);
            _gl.Uniform2(_uUvScrollRate, es.Def.UvScrollRate.X, es.Def.UvScrollRate.Y);
            _gl.Uniform1(_uHasTexMult, es.TextureMult != 0 ? 1 : 0);
            var multDiv = es.Def.TextureMultTexDiv;
            _gl.Uniform2(_uTexDivMult, multDiv.X == 0 ? 1f : multDiv.X, multDiv.Y == 0 ? 1f : multDiv.Y);
            _gl.Uniform2(_uUvScrollRateMult, es.Def.TextureMultUvScrollRate.X, es.Def.TextureMultUvScrollRate.Y);
            // M174 (1.4): alphaRef is an 0..255 cutoff; the engine confirms it (quad_ps declares ALPHA_TEST
            // and AlphaTestReferenceValue). 34,788 emitters author a non-zero one.
            _gl.Uniform1(_uAlphaRef, es.Def.AlphaRef / 255f);
            var d2 = es.Def;
            // M174 (2.1): alpha erosion, bound to texture unit 5.
            // The IsDegenerate check is the guard described on VfxAlphaErosion: under the INFERRED
            // parameter packing, 16% of erosion emitters evaluate to a mask of zero everywhere, which
            // would erase them. Skipping those keeps them looking exactly as they did before M174.
            bool hasErosion = es.ErosionTexture != 0 && d2.AlphaErosion is { IsDegenerate: false };
            _gl.Uniform1(_uHasErosion, hasErosion ? 1 : 0);
            if (hasErosion)
            {
                var ero = d2.AlphaErosion!;
                var yzw = ero.PackYzw();
                _gl.ActiveTexture(TextureUnit.Texture5);
                _gl.BindTexture(TextureTarget.Texture2D, es.ErosionTexture);
                _gl.Uniform1(_uErosionTex, 5);
                // .x is unused: the drive arrives per particle through the instance attribute, exactly
                // as Riot's quad path does it.
                _gl.Uniform4(_uErosionParams, 0f, yzw.X, yzw.Y, yzw.Z);
                _gl.Uniform4(_uErosionMixer, ero.ChannelMixer.X, ero.ChannelMixer.Y, ero.ChannelMixer.Z, ero.ChannelMixer.W);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            _gl.Uniform2(_uUvOffset, d2.UvOffset.X, d2.UvOffset.Y);
            _gl.Uniform2(_uUvScale, d2.UvScale.X == 0 ? 1f : d2.UvScale.X, d2.UvScale.Y == 0 ? 1f : d2.UvScale.Y);
            _gl.Uniform2(_uUvScrollInt, d2.UvScrollIntegrated.X, d2.UvScrollIntegrated.Y);
            _gl.Uniform1(_uUvRotation, d2.UvRotation * (MathF.PI / 180f));
            _gl.Uniform1(_uUvClamp, d2.UvScrollClamp ? 1 : 0);
            _gl.Uniform2(_uEmitterUvScroll, d2.EmitterUvScrollRate.X, d2.EmitterUvScrollRate.Y);
            _gl.Uniform2(_uUvFlip, d2.UvFlipU ? 1f : 0f, d2.UvFlipV ? 1f : 0f);
            _gl.Uniform1(_uUvRotInt, d2.UvRotateIntegrated * (MathF.PI / 180f));
            _gl.Uniform1(_uUvRotRate, d2.UvRotateRate * (MathF.PI / 180f));
            _gl.Uniform2(_uUvCenter, d2.UvTransformCenter.X, d2.UvTransformCenter.Y);
            _gl.Uniform1(_uEmitterAge, es.Age);
            _gl.Uniform1(_uDirectionOriented, es.Def.IsDirectionOriented ? 1 : 0);
            _gl.Uniform1(_uArbitraryQuad, es.Def.IsArbitraryQuad ? 1 : 0);
            _gl.Uniform1(_uIsDistortion, isDistortion ? 1 : 0);
            _gl.Uniform1(_uDistortionStrength, es.Def.Distortion?.Strength ?? 0f);
            _gl.Uniform3(_uPlacementRight, es.PlacementRight.X, es.PlacementRight.Y, es.PlacementRight.Z);
            _gl.Uniform3(_uPlacementUp, es.PlacementUp.X, es.PlacementUp.Y, es.PlacementUp.Z);
            _gl.Uniform3(_uPlacementForward, es.PlacementForward.X, es.PlacementForward.Y, es.PlacementForward.Z);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, es.Texture);
            if (es.TextureMult != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, es.TextureMult);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            if (isDistortion)
            {
                _gl.ActiveTexture(TextureUnit.Texture2);
                _gl.BindTexture(TextureTarget.Texture2D, _sceneTexture);
                _gl.ActiveTexture(TextureUnit.Texture3);
                _gl.BindTexture(TextureTarget.Texture2D, es.DistortionTexture);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            _gl.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, (uint)es.InstanceCount);
        }

        // restore reasonable defaults for the next pass
        _gl.DepthMask(true);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        if (!depthTest) _gl.Disable(EnableCap.DepthTest);
        _gl.BindVertexArray(0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture3);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    /// <summary>
    /// M117: which emitter blendModes render additively (SrcAlpha, One). Grounded in a survey of
    /// every emitter across six champions (Kayn/Ahri/Jinx/Lux/Yasuo/Thresh), correlating each mode
    /// with its textures' alpha usage:
    ///   1 → additive (canonical; 653 real-alpha + 360 flat-alpha dark-bg glows)
    ///   3 → additive (Kayn R scythe flipbooks + skinned scythe .skn, Jinx R missile — dark-bg
    ///       NO-alpha textures that rendered as black boxes under alpha blending)
    ///   4 → additive (flash/glow/fresnel family)
    ///   5 → additive (weapon streaks; "HeartMesh_ADD" literally says so)
    ///   0 → alpha (legacy .troy convention; zero occurrences in the survey)
    ///   2 → alpha (BlackMotes / Darkunderglow — DARK on-screen effects, which additive cannot
    ///       produce: additive only ever brightens)
    /// </summary>
    private static bool IsAdditive(int blendMode) => blendMode is 1 or 3 or 4 or 5;

    /// <summary>M117c: the effective blend for an emitter. Mode 3 is texture-dependent in practice —
    /// Kayn's base R scythe flipbooks are dark-background with NO alpha (must be additive or they show
    /// as black boxes), while skin02's R ghost .skn uses the skin's TX_CM WITH alpha and washes into an
    /// oversaturated blob when added. Both are mode 3, so the alpha channel decides: no alpha =>
    /// additive, real alpha => alpha blend. Modes 1/4/5 stay additive, 0/2 stay alpha.</summary>
    private bool IsAdditiveFor(int blendMode, uint texture) =>
        blendMode == 3
            ? !(_texHasAlpha.TryGetValue(texture, out var hasAlpha) && hasAlpha)
            : IsAdditive(blendMode);

    /// <summary>Delete all sprite textures + emitter meshes uploaded so far (before a new system uploads).</summary>
    public void ClearTextures()
    {
        if (!_ready) return;
        foreach (var t in _ownedTextures) _gl.DeleteTexture(t);
        _ownedTextures.Clear();
        _texHasAlpha.Clear();
        foreach (var (vao, vbo, ebo) in _ownedMeshes) { _gl.DeleteVertexArray(vao); _gl.DeleteBuffer(vbo); if (ebo != 0) _gl.DeleteBuffer(ebo); }
        _ownedMeshes.Clear();
        _whiteTex = 0; // owned-texture list held it; EnsureMeshProgram re-creates on demand
    }

    public void Dispose()
    {
        if (!_ready) return;
        foreach (var t in _ownedTextures) _gl.DeleteTexture(t);
        _ownedTextures.Clear();
        _texHasAlpha.Clear();
        _gl.DeleteBuffer(_quadVbo);
        _gl.DeleteBuffer(_instVbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
        if (_sceneTexture != 0) _gl.DeleteTexture(_sceneTexture);
        _sceneTexture = 0;
        _sceneWidth = _sceneHeight = 0;
        _ready = false;
    }

    // ---- M47 mesh-primitive particles (.scb/.sco): per-particle uniforms, simple textured draw ----
    private uint _meshProgram;
    private int _muViewProj, _muWorldPos, _muScale, _muRot, _muColor, _muTex, _muUvOffset;
    private int _muTexMult, _muHasTexMult, _muUvOffsetMult;
    private int _muMeshTexDiv, _muMeshTexDivMult;   // M117
    private int _muPlacementRight, _muPlacementUp, _muPlacementForward;
    private uint _whiteTex;

    /// <summary>M117b: a mesh-shader compile failure must NOT throw on the render thread — that
    /// killed the whole app the moment any mesh emitter uploaded. Mesh particles just stay invisible.</summary>
    private bool _meshProgramFailed;

    private unsafe void EnsureMeshProgram()
    {
        if (_meshProgramFailed) return;
        if (_meshProgram == 0)
        {
            try { _meshProgram = ShaderUtil.CreateProgram(_gl, _gles, MeshVert, MeshFrag); }
            catch (Exception ex)
            {
                _meshProgramFailed = true;
                System.Diagnostics.Debug.WriteLine($"VFX mesh shader failed to compile - mesh particles disabled: {ex.Message}");
                return;
            }
            _muViewProj = _gl.GetUniformLocation(_meshProgram, "uViewProj");
            _muWorldPos = _gl.GetUniformLocation(_meshProgram, "uWorldPos");
            _muScale = _gl.GetUniformLocation(_meshProgram, "uScale");
            _muRot = _gl.GetUniformLocation(_meshProgram, "uRot");
            _muColor = _gl.GetUniformLocation(_meshProgram, "uColor");
            _muTex = _gl.GetUniformLocation(_meshProgram, "uTex");
            _muUvOffset = _gl.GetUniformLocation(_meshProgram, "uUvOffset");
            _muTexMult = _gl.GetUniformLocation(_meshProgram, "uTexMult");
            _muHasTexMult = _gl.GetUniformLocation(_meshProgram, "uHasTexMult");
            _muUvOffsetMult = _gl.GetUniformLocation(_meshProgram, "uUvOffsetMult");
            _muMeshTexDiv = _gl.GetUniformLocation(_meshProgram, "uMeshTexDiv");
            _muMeshTexDivMult = _gl.GetUniformLocation(_meshProgram, "uMeshTexDivMult");
            _muPlacementRight = _gl.GetUniformLocation(_meshProgram, "uPlacementRight");
            _muPlacementUp = _gl.GetUniformLocation(_meshProgram, "uPlacementUp");
            _muPlacementForward = _gl.GetUniformLocation(_meshProgram, "uPlacementForward");
        }
        if (_whiteTex == 0) _whiteTex = UploadTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
    }

    /// <summary>Upload an emitter's mesh (pos3 + uv2 per vertex). Pass <paramref name="indices"/> for
    /// indexed (.skn) meshes — drawn with DrawElements; triangle-soup .scb meshes draw sequentially.</summary>
    public unsafe void UploadEmitterMesh(VfxParticleSimulator.EmitterState es, float[] positions, float[] uvs, uint[]? indices = null)
    {
        if (!_ready) return;
        EnsureMeshProgram();
        if (_meshProgramFailed) return;   // M117b: no program - the emitter falls back to billboards upstream
        int verts = positions.Length / 3;
        var inter = new float[verts * 5];
        for (int i = 0; i < verts; i++)
        {
            inter[i * 5 + 0] = positions[i * 3 + 0];
            inter[i * 5 + 1] = positions[i * 3 + 1];
            inter[i * 5 + 2] = positions[i * 3 + 2];
            inter[i * 5 + 3] = i * 2 + 0 < uvs.Length ? uvs[i * 2 + 0] : 0f;
            inter[i * 5 + 4] = i * 2 + 1 < uvs.Length ? uvs[i * 2 + 1] : 0f;
        }
        var vao = _gl.GenVertexArray();
        var vbo = _gl.GenBuffer();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = inter)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(inter.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)(3 * sizeof(float)));
        uint ebo = 0;
        if (indices is { Length: > 0 })
        {
            ebo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
            fixed (uint* ip = indices)
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), ip, BufferUsageARB.StaticDraw);
        }
        _gl.BindVertexArray(0);
        es.MeshVao = vao; es.MeshVbo = vbo; es.MeshEbo = ebo;
        es.MeshVertexCount = verts;
        es.MeshIndexCount = indices?.Length ?? 0;
        es.MeshInterleaved = inter;
        _ownedMeshes.Add((vao, vbo, ebo));
    }
    private readonly List<(uint Vao, uint Vbo, uint Ebo)> _ownedMeshes = new();

    /// <summary>M48: replace the mesh's positions (CPU-skinned wing-flap frame); UVs are kept.</summary>
    public unsafe void UpdateEmitterMeshPositions(VfxParticleSimulator.EmitterState es, float[] positions)
    {
        if (!_ready || es.MeshVbo == 0 || es.MeshInterleaved is not { } inter) return;
        int verts = Math.Min(es.MeshVertexCount, positions.Length / 3);
        for (int i = 0; i < verts; i++)
        {
            inter[i * 5 + 0] = positions[i * 3 + 0];
            inter[i * 5 + 1] = positions[i * 3 + 1];
            inter[i * 5 + 2] = positions[i * 3 + 2];
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, es.MeshVbo);
        fixed (float* p = inter)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(verts * 5 * sizeof(float)), p);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <summary>Draw a mesh-primitive emitter: one textured draw per live particle (counts are small).</summary>
    private void RenderMeshEmitter(VfxParticleSimulator.EmitterState es, Matrix4x4 viewProj)
    {
        EnsureMeshProgram();
        _gl.UseProgram(_meshProgram);
        _gl.BindVertexArray(es.MeshVao);
        _gl.UniformMatrix4(_muViewProj, 1, false, in viewProj.M11);
        _gl.Uniform1(_muTex, 0);
        _gl.Uniform1(_muTexMult, 1);
        _gl.Uniform1(_muHasTexMult, es.TextureMult != 0 ? 1 : 0);
        _gl.Uniform3(_muPlacementRight, es.PlacementRight.X, es.PlacementRight.Y, es.PlacementRight.Z);
        _gl.Uniform3(_muPlacementUp, es.PlacementUp.X, es.PlacementUp.Y, es.PlacementUp.Z);
        _gl.Uniform3(_muPlacementForward, es.PlacementForward.X, es.PlacementForward.Y, es.PlacementForward.Z);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, es.Texture != 0 ? es.Texture : _whiteTex);
        if (es.TextureMult != 0)
        {
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, es.TextureMult);
            _gl.ActiveTexture(TextureUnit.Texture0);
        }
        if (IsAdditiveFor(es.Def.BlendMode, es.Texture)) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        // M47c: mesh particles animate by SCROLLING their texture along the mesh UVs (waterfall flow) -
        // matches Riot's particle-system shader (Scrolling_Rate cbuffer + birthUvScrollRate data).
        var scroll = es.Def.UvScrollRate * es.Age;
        _gl.Uniform2(_muUvOffset, scroll.X, scroll.Y);
        var scrollMult = es.Def.TextureMultUvScrollRate * es.Age;
        _gl.Uniform2(_muUvOffsetMult, scrollMult.X, scrollMult.Y);
        // M117: texDiv = UV divisor (fractional tiling / atlas cell) — was ignored, which smeared
        // whole atlases across .scb meshes and broke tiled ring/cloud textures.
        var mdiv = es.Def.TexDiv;
        _gl.Uniform2(_muMeshTexDiv, mdiv.X > 0 ? mdiv.X : 1f, mdiv.Y > 0 ? mdiv.Y : 1f);
        var mdivMult = es.Def.TextureMultTexDiv;
        _gl.Uniform2(_muMeshTexDivMult, mdivMult.X > 0 ? mdivMult.X : 1f, mdivMult.Y > 0 ? mdivMult.Y : 1f);
        for (int i = 0; i < es.InstanceCount; i++)
        {
            int o = i * Stride;   // [cx,cy,cz, sx,sy, r,g,b,a, rot, frame]
            _gl.Uniform3(_muWorldPos, es.Instances[o], es.Instances[o + 1], es.Instances[o + 2]);
            // mesh particles use birthScale.x as a uniform scale; a scale of ~1 means unscaled geometry
            float rawScale = es.Instances[o + 3];
            float sc = MathF.Abs(rawScale) < 0.01f ? MathF.CopySign(0.01f, rawScale == 0f ? 1f : rawScale) : rawScale;
            _gl.Uniform1(_muScale, sc);
            _gl.Uniform1(_muRot, es.Instances[o + 9]);
            _gl.Uniform4(_muColor, es.Instances[o + 5], es.Instances[o + 6], es.Instances[o + 7], es.Instances[o + 8]);
            unsafe
            {
                if (es.MeshIndexCount > 0) _gl.DrawElements(PrimitiveType.Triangles, (uint)es.MeshIndexCount, DrawElementsType.UnsignedInt, (void*)0);
                else _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)es.MeshVertexCount);
            }
        }
        _gl.UseProgram(_program);   // back to the billboard program for the next emitter
        _gl.BindVertexArray(_vao);
    }

    private const string MeshVert = @"
layout(location=0) in vec3 aPos;
layout(location=1) in vec2 aUv;
uniform mat4 uViewProj;
uniform vec3 uWorldPos;
uniform float uScale;
uniform float uRot;
uniform vec2 uUvOffset;
uniform vec2 uUvOffsetMult;
uniform vec2 uMeshTexDiv;      // M117c: UV tiling factor (uv * texDiv)
uniform vec2 uMeshTexDivMult;
uniform vec3 uPlacementRight;
uniform vec3 uPlacementUp;
uniform vec3 uPlacementForward;
out vec2 vUv;
out vec2 vUvMult;
void main(){
    float s = sin(uRot); float c = cos(uRot);
    vec3 local = vec3(aPos.x * c - aPos.z * s, aPos.y, aPos.x * s + aPos.z * c) * uScale;
    vec3 p = uPlacementRight * local.x + uPlacementUp * local.y + uPlacementForward * local.z + uWorldPos;
    gl_Position = uViewProj * vec4(p, 1.0);
    // M117c: on mesh emitters texDiv is a TILING factor (uv * texDiv) - Kayn skin02's R pillar
    // cylinders (1x2 / 1x3 erode textures repeating up the pillar) pinned the direction; 0.25 on
    // the base ring swirl stretches the texture 4x along the ring, which also fits.
    vUv = aUv * max(uMeshTexDiv, vec2(0.0001)) + uUvOffset;
    vUvMult = aUv * max(uMeshTexDivMult, vec2(0.0001)) + uUvOffsetMult;
}";

    private const string MeshFrag = @"
in vec2 vUv;
in vec2 vUvMult;
uniform sampler2D uTex;
uniform sampler2D uTexMult;
uniform int uHasTexMult;
uniform vec4 uColor;
out vec4 fragColor;
void main(){
    vec4 texel = texture(uTex, vUv);
    if (uHasTexMult != 0) texel *= texture(uTexMult, vUvMult);
    fragColor = texel * uColor;
}";

    private const string Vert = @"
layout(location=0) in vec2 aCorner;    // base quad corner in [-0.5, 0.5]
layout(location=1) in vec3 aCenter;    // per-instance world center
layout(location=2) in vec2 aSize;      // per-instance width, height
layout(location=3) in vec4 aColor;     // per-instance rgba
layout(location=4) in vec2 aRotFrame;  // per-instance rotation (rad), flipbook frame
layout(location=5) in vec4 aAgeVelX;   // age, velocity xyz
layout(location=6) in vec3 aRotation;  // Euler xyz in radians
layout(location=7) in float aErosionDrive;  // M174 (2.1): per-particle erosion drive
uniform mat4 uViewProj;
uniform vec3 uCamRight;
uniform vec3 uCamUp;
uniform vec2 uTexDiv;                   // flipbook grid columns, rows
uniform vec2 uUvScrollRate;
uniform vec2 uUvOffset;
uniform vec2 uUvScale;
uniform vec2 uUvScrollInt;
uniform float uUvRotation;
uniform int uUvClamp;
uniform vec2 uEmitterUvScroll;
uniform vec2 uUvFlip;
uniform float uUvRotInt;
uniform float uUvRotRate;
uniform vec2 uUvCenter;
uniform float uEmitterAge;
uniform vec2 uTexDivMult;
uniform vec2 uUvScrollRateMult;
uniform int uDirectionOriented;
uniform int uArbitraryQuad;
uniform vec3 uPlacementRight;
uniform vec3 uPlacementUp;
uniform vec3 uPlacementForward;
out float vErosionDrive;   // M174 (2.1): per-particle erosion drive -> fragment stage
out vec2 vUv;
out vec2 vUvMult;
out vec4 vColor;
vec3 rotateEuler(vec3 p, vec3 r){
    float sx = sin(r.x); float cx = cos(r.x);
    float sy = sin(r.y); float cy = cos(r.y);
    float sz = sin(r.z); float cz = cos(r.z);
    p = vec3(p.x, p.y * cx - p.z * sx, p.y * sx + p.z * cx);
    p = vec3(p.x * cy + p.z * sy, p.y, -p.x * sy + p.z * cy);
    return vec3(p.x * cz - p.y * sz, p.x * sz + p.y * cz, p.z);
}
void main(){
    float rotation = uArbitraryQuad != 0 ? 0.0 : aRotFrame.x;
    if (uDirectionOriented != 0) {
        float vx = dot(aAgeVelX.yzw, uCamRight);
        float vy = dot(aAgeVelX.yzw, uCamUp);
        if (abs(vx) + abs(vy) > 0.0001) rotation = atan(-vx, vy);
    }
    float s = sin(rotation);
    float c = cos(rotation);
    vec2 rc = vec2(aCorner.x * c - aCorner.y * s, aCorner.x * s + aCorner.y * c);
    vec3 localRight = rotateEuler(vec3(1.0, 0.0, 0.0), aRotation);
    vec3 localUp = rotateEuler(vec3(0.0, 1.0, 0.0), aRotation);
    vec3 placedRight = uPlacementRight * localRight.x + uPlacementUp * localRight.y + uPlacementForward * localRight.z;
    vec3 placedUp = uPlacementRight * localUp.x + uPlacementUp * localUp.y + uPlacementForward * localUp.z;
    vec3 right = uArbitraryQuad != 0 ? placedRight : uCamRight;
    vec3 up = uArbitraryQuad != 0 ? placedUp : uCamUp;
    vec3 world = aCenter + right * (rc.x * aSize.x) + up * (rc.y * aSize.y);
    gl_Position = uViewProj * vec4(world, 1.0);
    vec2 cell = aCorner + vec2(0.5, 0.5);      // [0,1] within the frame cell
    // M174: sign is preserved so a negative divisor mirrors the axis, but the flipbook GRID has to be
    // counted with the magnitude - a -2 divisor is still a 2-cell axis.
    float cols = uTexDiv.x == 0.0 ? 1.0 : uTexDiv.x;
    float rows = uTexDiv.y == 0.0 ? 1.0 : uTexDiv.y;
    float gridCols = max(abs(cols), 1.0);
    float gridRows = max(abs(rows), 1.0);
    // Flipbooks select complete atlas cells. Fractional frame coordinates slide the UV window
    // across adjacent cells and visibly slice sprites whose pixels reach the cell boundary.
    float frame = floor(aRotFrame.y + 0.0001);
    float fx = mod(frame, gridCols);
    float fy = floor(frame / gridCols);

    // ---- M174 (2.3): the UV transform stack ----
    // Applied to the CELL coordinate, before the atlas frame is added, so rotating or zooming a flipbook
    // sprite stays inside its own cell instead of sliding into the neighbouring frame.
    //
    // ORDER IS INFERRED. Riot's own order is not established, so this uses the conventional one:
    // flip, then scale and rotate about the pivot, then translate. Sub-terms whose semantics the census
    // could confirm individually are each marked at their use below.
    float age = aAgeVelX.x;
    // NB: named uvc, not c - this function already has a `float c = cos(rotation)` for the billboard
    // spin, and shadowing it here made every line below a type error.
    vec2 uvc = vec2(cell.x, 1.0 - cell.y);

    if (uUvFlip.x > 0.5) uvc.x = 1.0 - uvc.x;
    if (uUvFlip.y > 0.5) uvc.y = 1.0 - uvc.y;

    // uvRotation is a fixed angle; birthUvRotateRate advances with particle age; particleUVRotateRate is
    // an INTEGRATED value, which for a constant rate is also rate*age - the distinction only matters once
    // its curve is animated, which this does not yet support.
    float uvAngle = uUvRotation + uUvRotRate * age + uUvRotInt * age;
    uvc -= uUvCenter;
    uvc *= uUvScale;
    if (abs(uvAngle) > 0.0001) {
        float cs = cos(uvAngle), sn = sin(uvAngle);
        uvc = vec2(uvc.x * cs - uvc.y * sn, uvc.x * sn + uvc.y * cs);
    }
    uvc += uUvCenter;

    // birthUVOffset is a fixed shift; the scroll terms advance with particle age, except
    // emitterUvScrollRate which advances with EMITTER age.
    uvc += uUvOffset + uUvScrollInt * age + uEmitterUvScroll * uEmitterAge;
    if (uUvClamp != 0) uvc = clamp(uvc, vec2(0.0), vec2(1.0));

    vUv = (vec2(fx, fy) + uvc) / vec2(cols, rows)
        + uUvScrollRate * age;
    // M174: the multiply stage gets the same treatment. It still does NOT advance with the flipbook frame
    // - a multi-cell multiplier atlas stays on cell 0 - which is a separate known defect (tier 2.3).
    vec2 multDiv = vec2(uTexDivMult.x == 0.0 ? 1.0 : uTexDivMult.x,
                        uTexDivMult.y == 0.0 ? 1.0 : uTexDivMult.y);
    vUvMult = vec2(cell.x, 1.0 - cell.y) / multDiv
        + uUvScrollRateMult * aAgeVelX.x;
    vColor = aColor;
    vErosionDrive = aErosionDrive;
}";

    private const string Frag = @"
in vec2 vUv;
in vec2 vUvMult;
in vec4 vColor;
in float vErosionDrive;
uniform sampler2D uErosionTex;
uniform vec4 uErosionParams;   // (unused, sliceWidth, 1/featherIn, 1/featherOut)
uniform vec4 uErosionMixer;
uniform int uHasErosion;
uniform sampler2D uTex;
uniform sampler2D uTexMult;
uniform int uHasTexMult;
uniform int uIsDistortion;
uniform sampler2D uDistortionTex;
uniform sampler2D uSceneTex;
uniform vec2 uViewportSize;
uniform float uDistortionStrength;
uniform float uAlphaRef;
out vec4 fragColor;
void main(){
    vec4 t = texture(uTex, vUv);
    if (uHasTexMult != 0) t *= texture(uTexMult, vUvMult);
    // M174 (2.1): alpha erosion (dissolve). DECODED from the SHEX instruction streams of
    // particlesystem/quad_ps, particlesystem/mesh_ps and skinnedmesh/particle_ps, which agree exactly:
    // a difference of two SATURATED LINEAR ramps, giving a trapezoidal band. Deliberately NOT a
    // smoothstep - Riot uses a real smoothstep for soft particles in the very same shader and a linear
    // ramp here. Riot does not re-clamp the difference, so neither do we.
    if (uHasErosion != 0) {
        float E  = clamp(dot(texture(uErosionTex, vUv), uErosionMixer), 0.0, 1.0);
        float te = vErosionDrive - E;
        float ea = clamp((te + uErosionParams.y) * uErosionParams.z, 0.0, 1.0);
        float eb = clamp( te                    * uErosionParams.w, 0.0, 1.0);
        t.a *= (ea - eb);
    }

    // M174 (1.4): alpha test. Riot tests the ERODED alpha, so this must follow the stage above.
    if (uAlphaRef > 0.0 && t.a * vColor.a < uAlphaRef) discard;
    if (uIsDistortion != 0) {
        vec4 normalSample = texture(uDistortionTex, vUv);
        float mask = normalSample.a * t.a * vColor.a;
        vec2 normalOffset = normalSample.rg * 2.0 - vec2(1.0);
        vec2 sceneUv = gl_FragCoord.xy / max(uViewportSize, vec2(1.0));
        sceneUv = clamp(sceneUv + normalOffset * uDistortionStrength * mask, vec2(0.0), vec2(1.0));
        vec4 refracted = texture(uSceneTex, sceneUv);
        fragColor = vec4(refracted.rgb, mask);
        return;
    }
    fragColor = t * vColor;
}";
}
