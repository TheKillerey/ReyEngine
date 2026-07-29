using System.Linq;
using System.Numerics;
using ReyEngine.Formats.Vfx;
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
    // M175: soft particles (2.2), palette (2.6), depth push/pull (2.8)
    private int _uDepthTex, _uDepthConv, _uSoftParams, _uSoftControl, _uHasSoft;
    private int _uPaletteTex, _uPaletteMixer, _uPaletteV, _uHasPalette;
    private int _uDepthPushPull, _uCamPos;
    private int _instCapFloats;
    private bool _ready;
    private readonly List<uint> _ownedTextures = new();
    /// <summary>M117c: per uploaded texture, whether its alpha channel varies (any pixel below ~1.0).
    /// Drives the mode-3 blend split — see <see cref="IsAdditiveFor"/>.</summary>
    private readonly Dictionary<uint, bool> _texHasAlpha = new();
    private uint _sceneTexture;
    private int _sceneWidth, _sceneHeight;
    // M175 (2.2): the scene depth, blitted out of the viewport's depth renderbuffer so it can be sampled.
    private uint _depthTexture, _depthFbo;
    private int _depthWidth, _depthHeight;
    private bool _depthOk;

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
        _uDepthTex = gl.GetUniformLocation(_program, "uDepthTex");
        _uDepthConv = gl.GetUniformLocation(_program, "uDepthConv");
        _uSoftParams = gl.GetUniformLocation(_program, "uSoftParams");
        _uSoftControl = gl.GetUniformLocation(_program, "uSoftControl");
        _uHasSoft = gl.GetUniformLocation(_program, "uHasSoft");
        _uPaletteTex = gl.GetUniformLocation(_program, "uPaletteTex");
        _uPaletteMixer = gl.GetUniformLocation(_program, "uPaletteMixer");
        _uPaletteV = gl.GetUniformLocation(_program, "uPaletteV");
        _uHasPalette = gl.GetUniformLocation(_program, "uHasPalette");
        _uDepthPushPull = gl.GetUniformLocation(_program, "uDepthPushPull");
        _uCamPos = gl.GetUniformLocation(_program, "uCamPos");

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

    /// <summary>M181 (2.12): upload a reflection cubemap. Six RGBA8 faces in the DDS order that
    /// <c>CubemapDecoder</c> already produces for the M122 skybox, so the two paths agree about face
    /// ordering rather than each having their own convention.</summary>
    public unsafe uint UploadCubemap(byte[][] faces, int faceSize)
    {
        if (!_ready || faces.Length < 6 || faceSize <= 0) return 0;
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.TextureCubeMap, tex);
        for (int f = 0; f < 6; f++)
            fixed (byte* p = faces[f])
                _gl.TexImage2D(TextureTarget.TextureCubeMapPositiveX + f, 0, InternalFormat.Rgba8,
                    (uint)faceSize, (uint)faceSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
        _ownedTextures.Add(tex);
        return tex;
    }

    public unsafe uint UploadTexture(byte[] rgba, int width, int height)
    {
        // M117c: does this texture use its alpha channel at all? M273 moved the predicate itself into
        // VfxShaderFlags - it is now consulted by the D3D11 path too, and two copies of a threshold that
        // decides a blend state is exactly the drift the shared table exists to prevent.
        bool _hasAlpha = VfxShaderFlags.TextureUsesAlpha(rgba);

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

    /// <summary>M175 (2.2): blit the scene's depth into a texture the particle pass can sample.
    ///
    /// The viewport draws into an FBO whose depth attachment is a RENDERBUFFER, which cannot be sampled
    /// and which <c>CopyTexSubImage2D</c> cannot read (that path only ever reads colour). A depth-only
    /// <c>BlitFramebuffer</c> into a second FBO backed by a depth TEXTURE is the one route GLES 3.0
    /// offers, so that is what this does.
    ///
    /// Call with the scene FBO bound as the read target, BEFORE any particle draws.</summary>
    public unsafe void CaptureDepth(uint width, uint height)
    {
        if (!_ready || width == 0 || height == 0) return;
        _gl.GetInteger(GetPName.ReadFramebufferBinding, out int prevRead);
        _gl.GetInteger(GetPName.DrawFramebufferBinding, out int prevDraw);

        if (_depthTexture == 0)
        {
            _depthTexture = _gl.GenTexture();
            _depthFbo = _gl.GenFramebuffer();
            _gl.BindTexture(TextureTarget.Texture2D, _depthTexture);
            // NEAREST, deliberately. Interpolating two depth samples produces a distance at which no
            // geometry exists - halfway between a near wall and the far plane is a surface that is not
            // there - and the fade would halo around every silhouette edge.
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }

        if (_depthWidth != (int)width || _depthHeight != (int)height)
        {
            _gl.BindTexture(TextureTarget.Texture2D, _depthTexture);
            // Must match the scene buffer's format: M182 made that DEPTH24_STENCIL8, and glBlitFramebuffer
            // rejects a depth blit between differing depth formats.
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Depth24Stencil8, width, height, 0,
                PixelFormat.DepthStencil, PixelType.UnsignedInt248, null);
            _depthWidth = (int)width;
            _depthHeight = (int)height;
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _depthFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                TextureTarget.Texture2D, _depthTexture, 0);
            // If this FBO is not complete the blit below silently does nothing, and the texture keeps
            // whatever it was allocated with - which would read as depth 0, i.e. "solid geometry directly
            // on the lens", and would fade every soft particle to nothing. Checking once and refusing to
            // bind is the difference between the feature being unavailable and the effects disappearing.
            _depthOk = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == GLEnum.FramebufferComplete;
            if (_depthOk)
            {
                // No ClearDepth call here: `glClearDepth` is desktop-GL only and does not exist in GLES 3.0
                // (ANGLE throws SymbolLoadingException for it), and the GL default clear-depth is already
                // 1.0 - the far plane - which is exactly the value this wants. Nothing else in the codebase
                // changes it.
                _gl.DepthMask(true);
                _gl.Clear((uint)ClearBufferMask.DepthBufferBit);
            }
        }

        if (_depthOk)
        {
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)prevRead);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _depthFbo);
            // Depth blits must use NEAREST; GL rejects LINEAR outright for depth.
            _gl.BlitFramebuffer(0, 0, (int)width, (int)height, 0, 0, (int)width, (int)height,
                (uint)ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        }

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)prevRead);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)prevDraw);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>Draw all emitters of the simulator. <paramref name="viewProj"/> and <paramref name="view"/>
    /// are the app's mirror-inclusive matrices (same ones passed to the mesh renderer).
    /// <paramref name="near"/>/<paramref name="far"/> are the camera's clip planes, needed to turn the
    /// sampled window depth back into a view distance for M175 (2.2).</summary>
    public unsafe void Render(VfxParticleSimulator sim, Matrix4x4 viewProj, Matrix4x4 view,
        float near = 1f, float far = 200000f)
    {
        if (!_ready || sim.LiveParticleCount == 0) return;

        // camera basis in world space, derived from the (mirror-inclusive) view matrix's inverse, so
        // billboards face the camera and are oriented correctly on screen even under the -X mirror.
        // M266: the derivation moved to VfxBillboardBasis so the D3D11 particle path uses THIS one rather
        // than its own origin-relative approximation. `inv` is still needed below for camPos.
        Matrix4x4.Invert(view, out var inv);
        var (camRight, camUp, _) = VfxBillboardBasis.FromView(view);

        _gl.UseProgram(_program);
        _gl.UniformMatrix4(_uViewProj, 1, false, in viewProj.M11);
        _gl.Uniform3(_uCamRight, camRight.X, camRight.Y, camRight.Z);
        _gl.Uniform3(_uCamUp, camUp.X, camUp.Y, camUp.Z);
        _gl.Uniform1(_uTex, 0);
        _gl.Uniform1(_uTexMult, 1);
        _gl.Uniform1(_uSceneTex, 2);
        _gl.Uniform1(_uDistortionTex, 3);
        // Both captures are the framebuffer size, but CaptureScene only runs when a distortion emitter is
        // present - so the depth dimensions are the ones that exist in the ordinary case, and the soft
        // stage needs a correct size to address gl_FragCoord against.
        _gl.Uniform2(_uViewportSize,
            _depthWidth > 0 ? _depthWidth : _sceneWidth,
            _depthHeight > 0 ? _depthHeight : _sceneHeight);
        var camPos = new Vector3(inv.M41, inv.M42, inv.M43);
        _gl.Uniform3(_uCamPos, camPos.X, camPos.Y, camPos.Z);

        // M175 (2.2): window depth -> view distance, in Riot's `1 / (z * dc.y + dc.x)` form.
        //
        // The constants are NOT the textbook GL ones. ReyEngine builds its projection with
        // Matrix4x4.CreatePerspectiveFieldOfView, and System.Numerics follows the DIRECT3D convention:
        // clip-space z maps near->0, far->+1, not near->-1. GL then applies its own viewport transform
        // d = (z_ndc + 1) / 2, so window depth actually occupies [0.5, 1.0] - only half the range.
        //
        // Substituting that back gives 1/dist = d * (2/f - 2/n) + (2/n - 1/f), i.e.:
        //     dc.x = 2/near - 1/far,   dc.y = 2*(1/far - 1/near)
        // The textbook GL pair (1/near, 1/far - 1/near) is exactly half that slope, which made every
        // measured distance ~1.9x too large and silently halved the width of every authored fade band.
        // The offscreen probe caught it by comparing measured alpha against smoothstep at five distances;
        // it is invisible to inspection because the effect still looks like a plausible soft particle.
        float invN = 1f / MathF.Max(near, 1e-4f), invF = 1f / MathF.Max(far, 1e-4f);
        _gl.Uniform2(_uDepthConv, 2f * invN - invF, 2f * (invF - invN));
        // The depth pass uses the SAME viewport dimensions as the colour capture, so gl_FragCoord/size
        // addresses it correctly; if the depth blit never ran, no emitter enables the stage below.
        bool depthReady = _depthOk && _depthTexture != 0 && _depthWidth > 0;
        if (depthReady)
        {
            _gl.ActiveTexture(TextureUnit.Texture4);
            _gl.BindTexture(TextureTarget.Texture2D, _depthTexture);
            _gl.Uniform1(_uDepthTex, 4);
            _gl.ActiveTexture(TextureUnit.Texture0);
        }

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
            if (es.MeshVao != 0) { if (_meshProgram != 0) RenderMeshEmitter(es, viewProj, camPos); continue; }
            // M183 (2.5): beams draw a ribbon between two fixed endpoints. Placed BEFORE the trail branch,
            // which is safe because no primitive class sets both - ReadTrail gates on the two trail
            // classes and ReadBeam on VfxPrimitiveBeam. It stays AFTER the mesh branch on purpose: 5,116
            // beam primitives name a mesh, and every one of the 609 beam emitters carrying a
            // reflectionDefinition is among them, so mesh-first preserves the M174 (1.5) behaviour those
            // rely on. That ordering is a DECISION (no regression), not a measurement.
            if (es.Def.Beam is not null) { RenderBeamEmitter(es, viewProj, camPos); continue; }
            // M177 (2.5): trail emitters draw a ribbon through the particle's own motion history.
            if (es.Def.Trail is not null) { RenderTrailEmitter(es, viewProj, camPos); continue; }
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
            // M175 (2.2): soft particles. The resolver has already dropped configurations that would fade
            // to nothing at every distance, so reaching here means the stage does something visible.
            bool hasSoft = depthReady && d2.SoftParticle is not null;
            _gl.Uniform1(_uHasSoft, hasSoft ? 1 : 0);
            if (hasSoft)
            {
                var sp = d2.SoftParticle!.PackParams();
                _gl.Uniform4(_uSoftParams, sp.X, sp.Y, sp.Z, sp.W);
                // cSoftParticleControl: leave RGB alone, scale alpha by the fade. What Riot actually feeds
                // this is not recoverable from the bytecode - see VfxSoftParticle's remarks.
                _gl.Uniform4(_uSoftControl, 1f, 0f, 0f, 1f);
            }

            // M175 (2.6): palette recolour, bound to texture unit 6.
            bool hasPalette = es.PaletteTexture != 0 && d2.Palette is not null;
            _gl.Uniform1(_uHasPalette, hasPalette ? 1 : 0);
            if (hasPalette)
            {
                var pal = d2.Palette!;
                _gl.ActiveTexture(TextureUnit.Texture6);
                _gl.BindTexture(TextureTarget.Texture2D, es.PaletteTexture);
                _gl.Uniform1(_uPaletteTex, 6);
                // M184 (2.10): a palette is a gradient LUT, and UploadTexture sets GL_REPEAT on every
                // texture object it creates - so a lookup that lands even slightly outside [0,1] wraps
                // round to the far end of the gradient instead of holding the last colour.
                //
                // A SAMPLER OBJECT rather than texture state, because ViewportControl caches one GL
                // texture per decoded image and shares it across all five particle slots; 268 textures in
                // the corpus are authored under more than one address mode, so per-texture state cannot
                // express this. 7,266 of 9,969 palette structs author no mode at all and take the clamp.
                _gl.BindSampler(6, PaletteSampler(d2.PaletteAddressMode));
                _gl.Uniform4(_uPaletteMixer, pal.SrcMixer.X, pal.SrcMixer.Y, pal.SrcMixer.Z, pal.SrcMixer.W);
                _gl.Uniform1(_uPaletteV, pal.RowV);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }

            // M175 (2.8): depth push/pull. DECODED from quad_vs instructions 12-16, cross-checked against
            // defaultparticlequadunlit.vs (which applies the same form to EMITTER_DEPTH_PUSH_PULL).
            _gl.Uniform1(_uDepthPushPull, d2.DepthPushPull);
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
            ApplyStencil(es.Def);
            _gl.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, (uint)es.InstanceCount);
        }

        // restore reasonable defaults for the next pass
        ClearStencil();
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
        _gl.ActiveTexture(TextureUnit.Texture4);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture5);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture6);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.BindSampler(6, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    /// <summary>M182 (2.9): per-emitter stencil state. Modes measured over 3,891 authored values:
    ///
    ///   1 (15.0%)  WRITE      - draw as usual, replacing the stencil value with stencilRef
    ///   2 (58.9%)  TEST EQUAL - draw only where the stencil already equals stencilRef
    ///   3 (25.9%)  TEST NOT-EQUAL - draw only where it does not
    ///   4 (0.2%)   UNRESOLVED - nothing in the data distinguishes it; left alone, 6 instances
    ///
    /// The mode meanings are a project decision, recorded as such rather than as a measurement. The
    /// corpus is consistent with them: of 1,016 objects using stencil at all, 254 contain both a mode-1
    /// emitter and a non-1 one, which is the shape of "one writes, another tests".
    ///
    /// The test modes do NOT write (StencilMask 0). A mask that also rewrote the buffer would change what
    /// later emitters in the same frame see, and nothing suggests the tests are meant to be destructive.
    ///
    /// ORDERING: this only works because M174 (1.3) draws emitters in authored `pass` order, so a writer
    /// with a lower pass has already run by the time a tester reads. Emitters that share a pass fall back
    /// to container order, which is the authored order in the bin.</summary>
    private void ApplyStencil(ReyEngine.Formats.Vfx.VfxEmitterDefinition def)
    {
        switch (def.StencilMode)
        {
            case 1:
                // A writer with no authored ref writes 0, which is what the buffer already holds - i.e. a
                // no-op rather than a hazard, so it is allowed through.
                _gl.Enable(EnableCap.StencilTest);
                _gl.StencilMask(0xFF);
                _gl.StencilFunc(StencilFunction.Always, Math.Max(0, def.StencilRef), 0xFF);
                _gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Replace);
                _stencilActive = true;
                break;
            case 2:
            case 3:
                // No numeric ref means the emitter names a symbolic StencilReferenceId this code does not
                // resolve. Testing against a defaulted 0 would be actively harmful for mode 3 - "draw
                // where the stencil is not 0" fails everywhere on a cleared buffer and the emitter
                // disappears - so an unresolved reference draws unmasked instead. 726 of 3,891 emitters
                // with a stencilMode are in this position.
                if (def.StencilRef < 0) { if (_stencilActive) ClearStencil(); break; }
                _gl.Enable(EnableCap.StencilTest);
                _gl.StencilMask(0x00);   // test only - see the remarks above
                _gl.StencilFunc(def.StencilMode == 2 ? StencilFunction.Equal : StencilFunction.Notequal,
                    def.StencilRef, 0xFF);
                _gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
                _stencilActive = true;
                break;
            default:
                // Mode 4 and anything unrecognised: draw with the stencil untouched, and make sure the
                // previous emitter's state does not leak into this draw.
                if (_stencilActive) ClearStencil();
                break;
        }
    }

    private bool _stencilActive;

    /// <summary>Return to the GL defaults. The write mask goes back to 0xFF, not 0: leaving it at 0 would
    /// silently prevent the NEXT FRAME'S glClear from clearing the stencil plane, and stale stencil under
    /// an active mask is garbage that only shows up a frame later.</summary>
    private void ClearStencil()
    {
        if (!_stencilActive) return;
        _gl.Disable(EnableCap.StencilTest);
        _gl.StencilMask(0xFF);
        _gl.StencilFunc(StencilFunction.Always, 0, 0xFF);
        _gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
        _stencilActive = false;
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
    ///
    /// M273: the table itself now lives in VfxShaderFlags so the D3D11 path cannot drift from it, and
    /// mode 2 joined mode 3 as texture-decided. The M117 observation above still holds and is what
    /// keeps it honest - the Kayn sprites that motivated "2 → alpha" measure 99.6-100% alpha-varied
    /// and still render alpha. See VfxShaderFlags.IsAdditive(int, bool?).
    /// </summary>
    private bool IsAdditiveFor(int blendMode, uint texture) =>
        VfxShaderFlags.IsAdditive(blendMode,
            _texHasAlpha.TryGetValue(texture, out var hasAlpha) ? hasAlpha : null);

    /// <summary>M184 (2.10): Riot's texture-address enum, read off their OWN named shared samplers in
    /// assets/shaders/shareddata.bin - Wrap_No_Mip / CharacterWrap / EnvironmentWrap write 0, and the
    /// sampler named `Mirror` writes 2. It is Unity's TextureWrapMode ordering, not D3D11's.
    ///
    /// -1 (absent) maps to CLAMP for the palette specifically: a gradient LUT that wraps shows the far end
    /// of the ramp at both extremes, which is the visible bug this fixes. Mode 3 has no core GLES 3.0
    /// equivalent under either candidate reading (Border or MirrorOnce), so it falls back to mirrored
    /// repeat - the identity is unresolved but has no implementation consequence.</summary>
    private uint PaletteSampler(int addressMode)
    {
        int idx = addressMode switch { 0 => 0, 1 => 1, 2 => 2, 3 => 2, _ => 1 };   // absent -> clamp
        if (_addressSamplers[idx] != 0) return _addressSamplers[idx];
        uint s = _gl.GenSampler();
        int wrap = idx switch
        {
            0 => (int)TextureWrapMode.Repeat,
            2 => (int)GLEnum.MirroredRepeat,
            _ => (int)TextureWrapMode.ClampToEdge,
        };
        _gl.SamplerParameter(s, SamplerParameterI.WrapS, wrap);
        _gl.SamplerParameter(s, SamplerParameterI.WrapT, wrap);
        _gl.SamplerParameter(s, SamplerParameterI.MinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        _gl.SamplerParameter(s, SamplerParameterI.MagFilter, (int)TextureMagFilter.Linear);
        _addressSamplers[idx] = s;
        return s;
    }

    private readonly uint[] _addressSamplers = new uint[3];

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
        _whiteCube = 0; // same: EnsureWhiteCube re-creates on demand
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
        if (_depthTexture != 0) _gl.DeleteTexture(_depthTexture);
        if (_depthFbo != 0) _gl.DeleteFramebuffer(_depthFbo);
        _depthTexture = _depthFbo = 0;
        _depthWidth = _depthHeight = 0;
        _depthOk = false;
        if (_trailProgram != 0) _gl.DeleteProgram(_trailProgram);
        if (_trailVbo != 0) _gl.DeleteBuffer(_trailVbo);
        if (_trailVao != 0) _gl.DeleteVertexArray(_trailVao);
        _trailProgram = _trailVbo = _trailVao = 0;
        _trailVboCapacity = 0;
        _ready = false;
    }

    // ---- M47 mesh-primitive particles (.scb/.sco): per-particle uniforms, simple textured draw ----
    private uint _meshProgram;
    private int _muViewProj, _muWorldPos, _muScale, _muRot, _muColor, _muTex, _muUvOffset;
    private int _muTexMult, _muHasTexMult, _muUvOffsetMult;
    private int _muMeshTexDiv, _muMeshTexDivMult;   // M117
    private int _muPlacementRight, _muPlacementUp, _muPlacementForward;
    private int _muCamPosMesh, _muFresnelColor, _muFresnelPower, _muHasFresnel;   // M178 (2.12)
    private int _muReflCube, _muReflFresnel, _muReflDirect, _muReflGlancing, _muReflTint, _muHasRefl;   // M181
    private uint _whiteTex;

    /// <summary>M117b: a mesh-shader compile failure must NOT throw on the render thread — that
    /// killed the whole app the moment any mesh emitter uploaded. Mesh particles just stay invisible.</summary>
    private bool _meshProgramFailed;

    /// <summary>M178: pos3 + uv2 + normal3 per mesh-particle vertex.</summary>
    private const int MeshStride = 8;

    /// <summary>M178 (2.12): per-vertex normals for a VFX mesh, accumulated from face normals and
    /// normalised. .scb/.sco carry none, so without this the fresnel stage has no surface to work from.
    ///
    /// Faces are weighted by their own cross-product magnitude rather than normalised first, which is the
    /// usual area weighting: it keeps a big face from being outvoted by a sliver sharing the same vertex.
    /// A vertex whose faces cancel out falls back to +Y rather than a zero vector, so a degenerate mesh
    /// produces a flat rim instead of NaNs.</summary>
    private static float[] ComputeNormals(float[] positions, uint[]? indices)
    {
        int verts = positions.Length / 3;
        var n = new float[verts * 3];
        Vector3 P(int i) => new(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
        void Add(int i, Vector3 v)
        {
            n[i * 3 + 0] += v.X; n[i * 3 + 1] += v.Y; n[i * 3 + 2] += v.Z;
        }

        if (indices is { Length: > 0 })
        {
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int i0 = (int)indices[t], i1 = (int)indices[t + 1], i2 = (int)indices[t + 2];
                if (i0 >= verts || i1 >= verts || i2 >= verts) continue;
                var face = Vector3.Cross(P(i1) - P(i0), P(i2) - P(i0));
                Add(i0, face); Add(i1, face); Add(i2, face);
            }
        }
        else
        {
            // triangle soup: three consecutive vertices per face
            for (int i = 0; i + 2 < verts; i += 3)
            {
                var face = Vector3.Cross(P(i + 1) - P(i), P(i + 2) - P(i));
                Add(i, face); Add(i + 1, face); Add(i + 2, face);
            }
        }

        for (int i = 0; i < verts; i++)
        {
            var v = new Vector3(n[i * 3], n[i * 3 + 1], n[i * 3 + 2]);
            v = v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitY;
            n[i * 3 + 0] = v.X; n[i * 3 + 1] = v.Y; n[i * 3 + 2] = v.Z;
        }
        return n;
    }

    /// <summary>A 1x1 white cubemap bound to the reflection unit when an emitter has no map, so the cube
    /// sampler never shares a texture unit with a 2-D sampler. See RenderMeshEmitter for why that
    /// matters.</summary>
    private uint _whiteCube;

    private uint EnsureWhiteCube()
    {
        if (_whiteCube != 0) return _whiteCube;
        var faces = new byte[6][];
        for (int f = 0; f < 6; f++) faces[f] = new byte[] { 255, 255, 255, 255 };
        _whiteCube = UploadCubemap(faces, 1);
        return _whiteCube;
    }

    private unsafe void EnsureMeshProgram()
    {
        if (_meshProgramFailed) return;
        if (_meshProgram == 0)
        {
            try { _meshProgram = ShaderUtil.CreateProgram(_gl, _gles, MeshVert, MeshFrag); }
            catch (Exception ex)
            {
                _meshProgramFailed = true;
                // Console, not Debug: a mesh-shader failure silently disables every mesh particle, and
                // Debug.WriteLine is invisible in a release run and in the offscreen probes - which is
                // exactly how M174 shipped a blank viewport twice.
                Console.Error.WriteLine("[VFX] mesh shader failed to compile - mesh particles disabled: " + ex.Message);
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
            _muCamPosMesh = _gl.GetUniformLocation(_meshProgram, "uCamPosMesh");
            _muFresnelColor = _gl.GetUniformLocation(_meshProgram, "uFresnelColor");
            _muFresnelPower = _gl.GetUniformLocation(_meshProgram, "uFresnelPower");
            _muHasFresnel = _gl.GetUniformLocation(_meshProgram, "uHasFresnel");
            _muReflCube = _gl.GetUniformLocation(_meshProgram, "uReflCube");
            _muReflFresnel = _gl.GetUniformLocation(_meshProgram, "uReflFresnel");
            _muReflDirect = _gl.GetUniformLocation(_meshProgram, "uReflDirect");
            _muReflGlancing = _gl.GetUniformLocation(_meshProgram, "uReflGlancing");
            _muReflTint = _gl.GetUniformLocation(_meshProgram, "uReflTint");
            _muHasRefl = _gl.GetUniformLocation(_meshProgram, "uHasRefl");
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
        // M178 (2.12): pos3 + uv2 + normal3. StaticMeshData (.scb/.sco) carries no normals at all, so
        // they are derived from the faces here - the fresnel stage needs a surface direction and there is
        // nowhere else to get one.
        var normals = ComputeNormals(positions, indices);
        var inter = new float[verts * MeshStride];
        for (int i = 0; i < verts; i++)
        {
            int o = i * MeshStride;
            inter[o + 0] = positions[i * 3 + 0];
            inter[o + 1] = positions[i * 3 + 1];
            inter[o + 2] = positions[i * 3 + 2];
            inter[o + 3] = i * 2 + 0 < uvs.Length ? uvs[i * 2 + 0] : 0f;
            inter[o + 4] = i * 2 + 1 < uvs.Length ? uvs[i * 2 + 1] : 0f;
            inter[o + 5] = normals[i * 3 + 0];
            inter[o + 6] = normals[i * 3 + 1];
            inter[o + 7] = normals[i * 3 + 2];
        }
        var vao = _gl.GenVertexArray();
        var vbo = _gl.GenBuffer();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = inter)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(inter.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, MeshStride * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, MeshStride * sizeof(float), (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, MeshStride * sizeof(float), (void*)(5 * sizeof(float)));
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
            inter[i * MeshStride + 0] = positions[i * 3 + 0];
            inter[i * MeshStride + 1] = positions[i * 3 + 1];
            inter[i * MeshStride + 2] = positions[i * 3 + 2];
        }
        // NB: normals are deliberately NOT recomputed for the re-skinned frame. M48 re-skins a ~100-vertex
        // butterfly every frame, and per-frame normal regeneration would cost more than the fresnel rim is
        // worth on a mesh that small; the bind-pose normals stay close enough through a wing flap.
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, es.MeshVbo);
        fixed (float* p = inter)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(verts * MeshStride * sizeof(float)), p);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <summary>Draw a mesh-primitive emitter: one textured draw per live particle (counts are small).</summary>
    private void RenderMeshEmitter(VfxParticleSimulator.EmitterState es, Matrix4x4 viewProj, Vector3 camPos)
    {
        EnsureMeshProgram();
        _gl.UseProgram(_meshProgram);
        _gl.BindVertexArray(es.MeshVao);
        _gl.UniformMatrix4(_muViewProj, 1, false, in viewProj.M11);
        // M178 (2.12): the fresnel rim. Riot only compiles REFLECTIVE into the mesh path - the define does
        // not exist in quad_ps at all - and 95.9% of emitters carrying a reflection struct use a
        // mesh-capable primitive, so this is the only place it belongs.
        var refl = es.Def.Reflection;
        bool hasFresnel = refl is { HasFresnel: true };
        _gl.Uniform1(_muHasFresnel, hasFresnel ? 1 : 0);
        _gl.Uniform3(_muCamPosMesh, camPos.X, camPos.Y, camPos.Z);
        if (hasFresnel)
        {
            _gl.Uniform4(_muFresnelColor, refl!.FresnelColor.X, refl.FresnelColor.Y, refl.FresnelColor.Z, refl.FresnelColor.W);
            _gl.Uniform1(_muFresnelPower, refl.Fresnel);
        }
        // M181 (2.12): the cubemap half. Only 13% of reflection emitters name a map, so the rim above and
        // this stage are independent - an emitter can have either, both or neither.
        bool hasRefl = es.ReflectionCubemap != 0 && refl is not null;
        _gl.Uniform1(_muHasRefl, hasRefl ? 1 : 0);
        // The cube sampler is bound to its own unit and given a real texture EVEN WHEN UNUSED.
        // Leaving it at the default 0 points it at the same unit as uTex's sampler2D, and a samplerCube
        // and a sampler2D on one texture unit is a type conflict that makes the whole draw invalid - so
        // every mesh particle WITHOUT a reflection map silently stopped rendering. The branch in the
        // shader is not enough; the binding has to be valid whether the branch runs or not.
        _gl.ActiveTexture(TextureUnit.Texture7);
        _gl.BindTexture(TextureTarget.TextureCubeMap, hasRefl ? es.ReflectionCubemap : EnsureWhiteCube());
        _gl.Uniform1(_muReflCube, 7);
        _gl.ActiveTexture(TextureUnit.Texture0);
        if (hasRefl)
        {
            // A negative exponent would make pow(f, n) = 1/f^|n| diverge as the surface turns edge-on;
            // 0 is well defined (pow(f,0)=1, so the term is 0 and the opacity is the direct value).
            _gl.Uniform1(_muReflFresnel, MathF.Max(0f, refl!.ReflectionFresnel));
            _gl.Uniform1(_muReflDirect, refl.OpacityDirect);
            _gl.Uniform1(_muReflGlancing, refl.OpacityGlancing);
            _gl.Uniform4(_muReflTint, refl.ReflectionFresnelColor.X, refl.ReflectionFresnelColor.Y,
                refl.ReflectionFresnelColor.Z, refl.ReflectionFresnelColor.W);
            _gl.ActiveTexture(TextureUnit.Texture0);
        }
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
        // M209 (2.11): backface culling is APPLIED. The winding is CLOCKWISE.
        //
        // Data side - the DEFAULT is an INFERENCE, not a measurement, and the A/B below did not test it.
        // The A/B subject is a CLOSED mesh, where culling and not culling look identical by construction:
        // that is exactly what lets it decide the winding, and exactly what makes it silent on the default.
        //
        // The inference: the field is Bool, only ever written `true`, and omitted by ~1.1M emitters. Were
        // the default also true, the field would have no discriminating power anywhere in shipped data -
        // every emitter would be unculled and writing it 297,513 times would be pure noise. Under a false
        // default it splits the corpus meaningfully, and "disableX" names the non-default state by
        // convention. Strong, but NOT the settled fact an earlier draft of this comment claimed: the
        // report refutes the general form of the argument (unknown #14 and the self-correction list),
        // because `alphaRef` is written at its own apparent default 391,078 times - so Riot's writer does
        // not reliably omit defaults, and absence means "the author never touched this".
        //
        // Exposure if it is backwards: the 171,153 culled here would want both faces and the 247,882
        // spared would want culling. Open/thin meshes are where that would show; closed ones never would.
        //
        // Winding, settled at M208-M209: M182's probe said CCW, a second probe disagreed, and the tie was
        // broken in the app against real Riot geometry rather than a probe-generated sphere - front=CW
        // renders Aatrox_Skin33_Back_Turbine_mesh.scb identically to no culling, front=CCW renders it
        // hollow. That also agrees with ViewportMeshRenderer, which independently verified CW for the
        // map pipeline: the viewport mirrors world X, which flips the handedness of Riot's source data.
        // Evidence is .scb; .skn mesh emitters take the same convention on the assumption that the
        // engine does not switch winding per container format - unverified, but no .skn counter-example
        // has been seen.
        //
        // Per EMITTER, not global. Full-corpus census (240 wads, mesh-primitive emitters naming a mesh
        // file, 419,035 of them): 247,882 set disableBackfaceCull and keep both faces; the remaining
        // 171,153 are culled by this change. A further 1,417 name no mesh and draw nothing either way.
        bool cull = !es.Def.DisableBackfaceCull;
        if (cull)
        {
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(TriangleFace.Back);
        }

        for (int i = 0; i < es.InstanceCount; i++)
        {
            int o = i * Stride;   // [cx,cy,cz, sx,sy, r,g,b,a, rot, frame]
            _gl.Uniform3(_muWorldPos, es.Instances[o], es.Instances[o + 1], es.Instances[o + 2]);
            // mesh particles use birthScale.x as a uniform scale; a scale of ~1 means unscaled geometry
            float rawScale = es.Instances[o + 3];
            float sc = MathF.Abs(rawScale) < 0.01f ? MathF.CopySign(0.01f, rawScale == 0f ? 1f : rawScale) : rawScale;
            _gl.Uniform1(_muScale, sc);
            // M209: a negative scale mirrors the mesh (uScale is scalar, so det = sc^3 < 0) and reverses
            // its winding. Without this those particles cull exactly the faces they should keep.
            if (cull) _gl.FrontFace(sc < 0f ? FrontFaceDirection.Ccw : FrontFaceDirection.CW);
            _gl.Uniform1(_muRot, es.Instances[o + 9]);
            _gl.Uniform4(_muColor, es.Instances[o + 5], es.Instances[o + 6], es.Instances[o + 7], es.Instances[o + 8]);
            unsafe
            {
                if (es.MeshIndexCount > 0) _gl.DrawElements(PrimitiveType.Triangles, (uint)es.MeshIndexCount, DrawElementsType.UnsignedInt, (void*)0);
                else _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)es.MeshVertexCount);
            }
        }
        // M209: culling is scoped to the mesh draw. Billboards and ribbons are two-sided quads, so leaving
        // it on would cull half of them depending on which way the camera faces. FrontFace is left as-is:
        // every other pipeline that culls (ViewportMeshRenderer) sets it per draw.
        if (cull) _gl.Disable(EnableCap.CullFace);
        _gl.UseProgram(_program);   // back to the billboard program for the next emitter
        _gl.BindVertexArray(_vao);
    }

    // ---- M177 (2.5) trail ribbons ----
    private uint _trailProgram, _trailVao, _trailVbo;
    private int _tuViewProj, _tuTex, _tuAlphaRef;
    private float[] _trailVerts = Array.Empty<float>();
    private int _trailVboCapacity;
    private bool _trailProgramFailed;

    /// <summary>pos3 + uv2 + rgba4 per ribbon vertex.</summary>
    private const int TrailStride = 9;

    private unsafe void EnsureTrailProgram()
    {
        if (_trailProgramFailed || _trailProgram != 0) return;
        try { _trailProgram = ShaderUtil.CreateProgram(_gl, _gles, TrailVert, TrailFrag); }
        catch (Exception ex)
        {
            // Same rule as the mesh path (M117b): a shader failure on the render thread must not take the
            // app down. Trails simply do not draw.
            _trailProgramFailed = true;
            // Console, not Debug, matching the mesh path: Debug.WriteLine is invisible in a release run
            // and in the offscreen probes, which is how M174 shipped a blank viewport twice.
            Console.Error.WriteLine("[VFX] ribbon shader failed to compile - trails and beams disabled: " + ex.Message);
            return;
        }
        _tuViewProj = _gl.GetUniformLocation(_trailProgram, "uViewProj");
        _tuTex = _gl.GetUniformLocation(_trailProgram, "uTex");
        _tuAlphaRef = _gl.GetUniformLocation(_trailProgram, "uAlphaRef");
        _trailVao = _gl.GenVertexArray();
        _trailVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_trailVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trailVbo);
        uint stride = TrailStride * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
        // Deliberately NOT BindVertexArray(0) here. Setup runs inside Render()'s emitter loop, and every
        // early return in a ribbon path below would then leave VAO 0 bound - so the NEXT emitter in the
        // frame would draw with no attribute arrays at all and silently vanish. Restore the billboard VAO
        // instead, which is the state the loop expects on entry.
        _gl.BindVertexArray(_vao);
    }

    /// <summary>M177 (2.5): build and draw one emitter's trail ribbons.
    ///
    /// The geometry is generated here, from where each particle has been - it is not in the bin. Riot's
    /// trail payload only says how long the ribbon may get, how often its texture repeats along that
    /// length, and how many points may be appended per frame.
    ///
    /// Two segment-orientation cases, matching the two primitive classes: a CameraTrail twists so the
    /// ribbon always faces the viewer, an ArbitraryTrail holds the placement's own up axis. That split is
    /// INFERRED from the class names - it is the only distinction the names draw and the payloads are
    /// identical - but it is the whole reason Riot ships two classes.</summary>
    private unsafe void RenderTrailEmitter(VfxParticleSimulator.EmitterState es, Matrix4x4 viewProj, Vector3 camPos)
    {
        EnsureTrailProgram();
        if (_trailProgramFailed || es.Texture == 0) return;
        // M183: the trail path never applied stencil state, so a mode-2/3 emitter earlier in pass order
        // silently masked it. Same fix as the new beam path.
        ApplyStencil(es.Def);
        var trail = es.Def.Trail!;
        float tiling = trail.EffectiveTiling;

        int quadCount = 0;
        foreach (var p in es.Particles) if (p.HistoryCount >= 2) quadCount += p.HistoryCount - 1;
        if (quadCount == 0) return;

        int needed = quadCount * 6 * TrailStride;
        if (_trailVerts.Length < needed) _trailVerts = new float[Math.Max(needed, 4096)];
        var buf = _trailVerts;
        int k = 0;

        for (int pi = 0; pi < es.Particles.Count; pi++)
        {
            var p = es.Particles[pi];
            if (p.HistoryCount < 2 || p.History is not { } hist) continue;

            // Colour and width come from the same per-particle values the billboard path uses, so a trail
            // emitter's colour-over-life curve drives the ribbon exactly as it would drive a sprite.
            int o = pi * Stride;
            float r = 1f, g = 1f, b = 1f, a = 1f, halfWidth = 8f;
            if (o + 8 < es.Instances.Length && pi < es.InstanceCount)
            {
                halfWidth = MathF.Abs(es.Instances[o + 3]) * 0.5f;
                r = es.Instances[o + 5]; g = es.Instances[o + 6]; b = es.Instances[o + 7]; a = es.Instances[o + 8];
            }
            if (halfWidth < 1e-3f) halfWidth = 1e-3f;

            // M183: shared with the beam path so the two ribbons cannot drift. `null` uTotal means
            // "derive U from accumulated arc length", which is the trail's behaviour.
            var colour = new Vector4(r, g, b, a);
            k = BuildRibbon(buf, k, hist.AsSpan(0, p.HistoryCount), halfWidth, colour, colour,
                tiling, null, es.Def.IsArbitraryTrail, es.PlacementUp, es.PlacementForward, camPos);
        }
        if (k == 0) return;

        _gl.UseProgram(_trailProgram);
        _gl.BindVertexArray(_trailVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trailVbo);
        fixed (float* d = buf)
        {
            if (k > _trailVboCapacity)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(k * sizeof(float)), d, BufferUsageARB.DynamicDraw);
                _trailVboCapacity = k;
            }
            else _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(k * sizeof(float)), d);
        }
        _gl.UniformMatrix4(_tuViewProj, 1, false, in viewProj.M11);
        _gl.Uniform1(_tuTex, 0);
        _gl.Uniform1(_tuAlphaRef, es.Def.AlphaRef / 255f);
        if (IsAdditiveFor(es.Def.BlendMode, es.Texture)) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, es.Texture);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(k / TrailStride));

        _gl.UseProgram(_program);       // back to the billboard program for the next emitter
        _gl.BindVertexArray(_vao);
    }

    /// <summary>M183 (2.5): extrude a polyline into a textured ribbon; returns the new write cursor.
    ///
    /// Shared by trails and beams. <paramref name="uTotal"/> selects the two U conventions: null means
    /// accumulate arc length and divide by <paramref name="tiling"/> (the trail), a value means spread
    /// exactly that many repeats across the whole run (the beam, whose repeat count comes from
    /// VfxBeamDefinition.UvRepeats). <paramref name="c0"/>/<paramref name="c1"/> lerp along the run; pass
    /// the same colour twice for a uniform ribbon.</summary>
    private static int BuildRibbon(float[] buf, int k, ReadOnlySpan<Vector3> points, float halfWidth,
        Vector4 c0, Vector4 c1, float tiling, float? uTotal,
        bool arbitrary, Vector3 up, Vector3 forward, Vector3 camPos)
    {
        if (points.Length < 2) return k;
        float total = 0f;
        if (uTotal is not null)
            for (int i = 0; i < points.Length - 1; i++) total += Vector3.Distance(points[i], points[i + 1]);

        float travelled = 0f;
        for (int i = 0; i < points.Length - 1; i++)
        {
            var p0 = points[i];
            var p1 = points[i + 1];
            var seg = p1 - p0;
            float segLen = seg.Length();
            if (segLen < 1e-5f) continue;
            var dir = seg / segLen;

            Vector3 side;
            if (arbitrary)
            {
                side = Vector3.Cross(dir, up);
                // A segment running straight along the placement's up axis has no width under that cross
                // product; fall back to another axis rather than collapsing the ribbon.
                if (side.LengthSquared() < 1e-8f) side = Vector3.Cross(dir, forward);
            }
            else
            {
                side = Vector3.Cross(dir, camPos - p0);
                if (side.LengthSquared() < 1e-8f) side = Vector3.Cross(dir, Vector3.UnitY);
            }
            if (side.LengthSquared() < 1e-8f) continue;
            side = Vector3.Normalize(side) * halfWidth;

            float t0 = travelled;
            travelled += segLen;
            float u0, u1;
            if (uTotal is { } repeats)
            {
                u0 = total > 1e-5f ? t0 / total * repeats : 0f;
                u1 = total > 1e-5f ? travelled / total * repeats : repeats;
            }
            else
            {
                u0 = t0 / tiling;
                u1 = travelled / tiling;
            }

            // Colour lerps by ring index. Both current callers pass c0 == c1, so this is a no-op today;
            // it exists so per-vertex distance colour can be switched on without touching the extruder.
            float denom = points.Length - 1;
            var col0 = Vector4.Lerp(c0, c1, i / denom);
            var col1 = Vector4.Lerp(c0, c1, (i + 1) / denom);

            var a0 = p0 - side; var b0 = p0 + side;
            var a1 = p1 - side; var b1 = p1 + side;

            void Vert(Vector3 pos, float u, float v, Vector4 c)
            {
                buf[k++] = pos.X; buf[k++] = pos.Y; buf[k++] = pos.Z;
                buf[k++] = u; buf[k++] = v;
                buf[k++] = c.X; buf[k++] = c.Y; buf[k++] = c.Z; buf[k++] = c.W;
            }
            Vert(a0, u0, 0f, col0); Vert(b0, u0, 1f, col0); Vert(b1, u1, 1f, col1);
            Vert(a0, u0, 0f, col0); Vert(b1, u1, 1f, col1); Vert(a1, u1, 0f, col1);
        }
        return k;
    }

    /// <summary>M183 (2.5): draw an emitter's beam - a ribbon from its source to the bound target.
    ///
    /// ONE RIBBON PER EMITTER, not per particle. INFERRED, and the reason matters: the source and target
    /// offsets are per-EMITTER constants, so N live particles would produce N exactly coincident ribbons.
    /// Under this path's DepthMask(false) and the additive blend most beams author, that is an N-times
    /// brightness multiplier - a 20-particle beam would blow out to white. Width and colour therefore come
    /// from instance 0, which carries the emitter's authored birthScale/colour curves like any other
    /// particle.
    ///
    /// Beams inherit the ribbon program's documented gaps - no flipbook/texDiv, erosion, soft particles,
    /// palette, distortion or UV transform stack. Trails already lack all of it; this is a shared, stated
    /// limitation rather than a beam-specific omission.</summary>
    private unsafe void RenderBeamEmitter(VfxParticleSimulator.EmitterState es, Matrix4x4 viewProj, Vector3 camPos)
    {
        EnsureTrailProgram();
        // Its own texture guard: the loop's shared `es.Texture == 0` check sits downstream of this branch.
        if (_trailProgramFailed || es.Texture == 0 || !es.HasBeamEndpoints || es.InstanceCount == 0) return;
        var beam = es.Def.Beam!;

        float halfWidth = MathF.Abs(es.Instances[3]) * 0.5f;
        if (halfWidth < 1e-3f) halfWidth = 1e-3f;
        var colour = new Vector4(es.Instances[5], es.Instances[6], es.Instances[7], es.Instances[8]);

        float length = Vector3.Distance(es.BeamSource, es.BeamTarget);
        Span<Vector3> ends = stackalloc Vector3[2];
        ends[0] = es.BeamSource;
        ends[1] = es.BeamTarget;

        int needed = 6 * TrailStride;
        if (_trailVerts.Length < needed) _trailVerts = new float[Math.Max(needed, 4096)];
        int k = BuildRibbon(_trailVerts, 0, ends, halfWidth, colour, colour,
            1f, beam.UvRepeats(length), arbitrary: false, es.PlacementUp, es.PlacementForward, camPos);
        if (k == 0) return;

        ApplyStencil(es.Def);
        _gl.UseProgram(_trailProgram);
        _gl.BindVertexArray(_trailVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trailVbo);
        fixed (float* d = _trailVerts)
        {
            if (k > _trailVboCapacity)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(k * sizeof(float)), d, BufferUsageARB.DynamicDraw);
                _trailVboCapacity = k;
            }
            else _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(k * sizeof(float)), d);
        }
        _gl.UniformMatrix4(_tuViewProj, 1, false, in viewProj.M11);
        _gl.Uniform1(_tuTex, 0);
        _gl.Uniform1(_tuAlphaRef, es.Def.AlphaRef / 255f);
        if (IsAdditiveFor(es.Def.BlendMode, es.Texture)) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, es.Texture);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(k / TrailStride));

        _gl.UseProgram(_program);
        _gl.BindVertexArray(_vao);
    }

    private const string TrailVert = @"
layout(location=0) in vec3 aPos;
layout(location=1) in vec2 aUv;
layout(location=2) in vec4 aColor;
uniform mat4 uViewProj;
out vec2 vUv;
out vec4 vColor;
void main(){
    gl_Position = uViewProj * vec4(aPos, 1.0);
    vUv = aUv;
    vColor = aColor;
}";

    private const string TrailFrag = @"
in vec2 vUv;
in vec4 vColor;
uniform sampler2D uTex;
uniform float uAlphaRef;
out vec4 fragColor;
void main(){
    vec4 t = texture(uTex, vUv);
    if (uAlphaRef > 0.0 && t.a * vColor.a < uAlphaRef) discard;
    fragColor = t * vColor;
}";

    private const string MeshVert = @"
layout(location=0) in vec3 aPos;
layout(location=1) in vec2 aUv;
layout(location=2) in vec3 aNormal;
uniform mat4 uViewProj;
uniform vec3 uCamPosMesh;
uniform vec4 uFresnelColor;
uniform float uFresnelPower;
uniform int uHasFresnel;
uniform float uReflFresnel;
uniform float uReflDirect;
uniform float uReflGlancing;
uniform highp int uHasRefl;   // explicit: ES defaults int to highp in VS but mediump in FS, and a
                              // uniform shared by both stages must agree or the program will not link
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
out vec3 vFresnel;
out vec4 vReflect;   // M181: xyz = reflection vector, w = reflection opacity
void main(){
    float s = sin(uRot); float c = cos(uRot);
    vec3 local = vec3(aPos.x * c - aPos.z * s, aPos.y, aPos.x * s + aPos.z * c) * uScale;
    vec3 p = uPlacementRight * local.x + uPlacementUp * local.y + uPlacementForward * local.z + uWorldPos;
    gl_Position = uViewProj * vec4(p, 1.0);
    // M178 (2.12): the fresnel rim. DECODED from particlesystem/mesh_vs permutation REFLECTIVE:
    //     f    = saturate(dot(-V, N));  term = 1 - pow(f, vFresnel.w);  out = term * vFresnel.rgb
    // The normal is rotated through the same spin and placement basis as the position, so the rim stays
    // put on the surface instead of sliding as the particle turns.
    vFresnel = vec3(0.0);
    vReflect = vec4(0.0, 1.0, 0.0, 0.0);
    if (uHasFresnel != 0 || uHasRefl != 0) {
        vec3 nLocal = vec3(aNormal.x * c - aNormal.z * s, aNormal.y, aNormal.x * s + aNormal.z * c);
        vec3 nWorld = uPlacementRight * nLocal.x + uPlacementUp * nLocal.y + uPlacementForward * nLocal.z;
        float len = length(nWorld);
        if (len > 1e-5) {
            nWorld /= len;
            vec3 view = normalize(p - uCamPosMesh);
            float f = clamp(dot(-view, nWorld), 0.0, 1.0);
            if (uHasFresnel != 0)
                vFresnel = (1.0 - pow(f, uFresnelPower)) * uFresnelColor.rgb;
            // M181 (2.12): DECODED from mesh_vs perm #7 -
            //     o4.xyz = V - 2*dot(V,N)*N              (instructions 44-46)
            //     o4.w   = lerp(vReflection.y, vReflection.z, 1 - pow(f, vReflection.x))   (54-57)
            // The lerp endpoints are what pin .y to reflectionOpacityDirect: at a head-on view f = 1, so
            // the term is 0 and the opacity is exactly .y.
            if (uHasRefl != 0) {
                vec3 r = view - 2.0 * dot(view, nWorld) * nWorld;
                float t = 1.0 - pow(f, uReflFresnel);
                vReflect = vec4(r, mix(uReflDirect, uReflGlancing, t));
            }
        }
    }
    // M117c: on mesh emitters texDiv is a TILING factor (uv * texDiv) - Kayn skin02's R pillar
    // cylinders (1x2 / 1x3 erode textures repeating up the pillar) pinned the direction; 0.25 on
    // the base ring swirl stretches the texture 4x along the ring, which also fits.
    vUv = aUv * max(uMeshTexDiv, vec2(0.0001)) + uUvOffset;
    vUvMult = aUv * max(uMeshTexDivMult, vec2(0.0001)) + uUvOffsetMult;
}";

    private const string MeshFrag = @"
in vec2 vUv;
in vec2 vUvMult;
in vec3 vFresnel;
in vec4 vReflect;
uniform samplerCube uReflCube;
uniform vec4 uReflTint;
uniform highp int uHasRefl;   // see MeshVert - the precision must match the vertex declaration
uniform sampler2D uTex;
uniform sampler2D uTexMult;
uniform int uHasTexMult;
uniform vec4 uColor;
out vec4 fragColor;
void main(){
    vec4 texel = texture(uTex, vUv);
    if (uHasTexMult != 0) texel *= texture(uTexMult, vUvMult);
    vec4 outColor = texel * uColor;
    // M181 (2.12): DECODED from mesh_ps REFLECTIVE, instructions 28-32 -
    //     r1.xyz = cubemap.Sample(R).rgb;   r1.xyz *= reflOpacity
    //     r2.xyz = lerp(1, vReflectionFColor.rgb, reflOpacity)
    //     rgb   += r1.xyz * r2.xyz
    // Note the tint LERPS FROM WHITE by the same opacity, so a weak reflection is barely tinted and a
    // strong one takes the authored colour fully. Multiplying by the tint directly would darken every
    // faint reflection instead.
    if (uHasRefl != 0) {
        vec3 refl = texture(uReflCube, normalize(vReflect.xyz)).rgb * vReflect.w;
        vec3 tint = mix(vec3(1.0), uReflTint.rgb, vReflect.w);
        outColor.rgb += refl * tint;
    }
    // M178 (2.12): mesh_ps adds the rim scaled by the particle's ALPHA, not by its colour:
    //     mad r0.xyz, v5.xyzx, r1.wwww, r0.xyzx
    // where r1.w is the alpha computed before the cubemap sample overwrote r1.xyz. So a fading particle
    // loses its rim at the same rate it fades, which is what stops the rim outliving the sprite.
    outColor.rgb += vFresnel * outColor.a;
    fragColor = outColor;
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
uniform float uDepthPushPull;   // M175 (2.8)
uniform vec3 uCamPos;
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
    // M175 (2.8): depthPushPull. DECODED from particlesystem/quad_vs, which does exactly this:
    //     12: add r0.xyz, v0.xyz, -cb2[4].xyz     // vCamera
    //     13-15: normalize
    //     16: mad r0.xyz, r0.xyz, cb1[1].xxxx, v0.xyz
    // i.e. world += normalize(world - camera) * depthPushPull, per VERTEX and before projection, so
    // POSITIVE pushes away from the camera. Because every corner slides along its own camera ray, the
    // sprite's screen position and size are untouched and only its depth changes - which is why this can
    // be applied unconditionally without any risk of moving artwork.
    if (uDepthPushPull != 0.0) {
        vec3 away = world - uCamPos;
        float len = length(away);
        if (len > 1e-4) world += (away / len) * uDepthPushPull;
    }
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
uniform sampler2D uDepthTex;     // M175 (2.2)
uniform vec2 uDepthConv;
uniform vec4 uSoftParams;
uniform vec4 uSoftControl;
uniform int uHasSoft;
uniform sampler2D uPaletteTex;   // M175 (2.6)
uniform vec4 uPaletteMixer;
uniform float uPaletteV;
uniform int uHasPalette;
out vec4 fragColor;
void main(){
    vec4 t = texture(uTex, vUv);
    if (uHasTexMult != 0) t *= texture(uTexMult, vUvMult);

    // M175 (2.6): palette recolour. DECODED from particlesystem/quad_ps and VALIDATED against Riot's own
    // permutation on a D3D11 device (worst 0.0096 - docs/research/d3d11-spike.md):
    //     m   = saturate(dot(sourceTexel, mixer));  U = m + select.z;  V = select.x + select.w
    //     rgb = palette.Sample(U, V).rgb            // REPLACES rgb; alpha stays the source's
    // The U/V animation curves that would feed those two offsets are deliberately NOT applied - see the
    // remarks on VfxPalette. Their authored median is 1.0, and a constant +1 on U would drive every
    // lookup off the right-hand end of the gradient, so the naive curve-value-is-the-offset reading is
    // refuted by the data itself. Better to sample the authored row than to sample confidently wrong.
    if (uHasPalette != 0) {
        float m = clamp(dot(t, uPaletteMixer), 0.0, 1.0);
        t.rgb = texture(uPaletteTex, vec2(m, uPaletteV)).rgb;
    }
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

    // M175 (2.2): soft particles. DECODED from quad_ps and VALIDATED against Riot's own permutation
    // (worst 0.0021 - docs/research/d3d11-spike.md). Note the genuine smoothstep: alpha erosion above
    // uses LINEAR ramps in the very same shader family, and running both confirmed they really differ.
    //
    // Placed AFTER the alpha test on purpose. Riot's ordering here is not recoverable, but testing the
    // faded alpha would re-introduce exactly the hard cutoff against geometry that this stage exists to
    // remove - the fade would be quantised back into a hard edge at the cutoff.
    if (uHasSoft != 0) {
        vec2 duv = gl_FragCoord.xy / max(uViewportSize, vec2(1.0));
        float sceneZ = texture(uDepthTex, duv).r;
        // 1/(z*dc.y + dc.x): window depth back to view distance. A fragment with nothing behind it reads
        // the far plane, so diff is enormous and the fade saturates open - particles in open air are
        // untouched by this stage, which is what makes it safe to apply broadly.
        float lscene = 1.0 / (sceneZ * uDepthConv.y + uDepthConv.x);
        float lself  = 1.0 / (gl_FragCoord.z * uDepthConv.y + uDepthConv.x);
        float diff = lscene - lself;
        vec2 st = clamp((vec2(diff) - uSoftParams.xy) * uSoftParams.zw, 0.0, 1.0);
        vec2 sm = st * st * (3.0 - 2.0 * st);
        float fade = sm.x - sm.y;
        t.rgb *= uSoftControl.x + uSoftControl.y * fade;
        t.a   *= uSoftControl.z + uSoftControl.w * fade;
    }
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
