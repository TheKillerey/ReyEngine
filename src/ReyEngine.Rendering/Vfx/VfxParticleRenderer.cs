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
    private int _uViewProj, _uCamRight, _uCamUp, _uTexDiv, _uTex;
    private int _instCapFloats;
    private bool _ready;
    private readonly List<uint> _ownedTextures = new();

    private const int Stride = 11; // floats per instance: cx,cy,cz, sx,sy, r,g,b,a, rot, frame

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
        gl.VertexAttribDivisor(1, 1);
        gl.VertexAttribDivisor(2, 1);
        gl.VertexAttribDivisor(3, 1);
        gl.VertexAttribDivisor(4, 1);

        gl.BindVertexArray(0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _ready = true;
    }

    public unsafe uint UploadTexture(byte[] rgba, int width, int height)
    {
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
        return tex;
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

        _gl.BindVertexArray(_vao);
        _gl.ActiveTexture(TextureUnit.Texture0);

        bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(false);                 // additive/alpha particles never write depth
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendEquation(GLEnum.FuncAdd);

        foreach (var es in sim.Emitters)
        {
            if (es.InstanceCount == 0) continue;
            // M47: mesh-primitive emitters draw their .scb/.sco geometry instead of billboards
            if (es.MeshVao != 0) { RenderMeshEmitter(es, viewProj); continue; }
            if (es.Texture == 0) continue;

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

            // blend mode: 1 = additive (glow) is the common case; treat 0/1/4/5 as additive, else alpha.
            if (IsAdditive(es.Def.BlendMode)) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            _gl.Uniform2(_uTexDiv, es.Def.TexDiv.X <= 0 ? 1f : es.Def.TexDiv.X, es.Def.TexDiv.Y <= 0 ? 1f : es.Def.TexDiv.Y);
            _gl.BindTexture(TextureTarget.Texture2D, es.Texture);
            _gl.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, (uint)es.InstanceCount);
        }

        // restore reasonable defaults for the next pass
        _gl.DepthMask(true);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        if (!depthTest) _gl.Disable(EnableCap.DepthTest);
        _gl.BindVertexArray(0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    private static bool IsAdditive(int blendMode) => blendMode is 0 or 1 or 4 or 5;

    /// <summary>Delete all sprite textures + emitter meshes uploaded so far (before a new system uploads).</summary>
    public void ClearTextures()
    {
        if (!_ready) return;
        foreach (var t in _ownedTextures) _gl.DeleteTexture(t);
        _ownedTextures.Clear();
        foreach (var (vao, vbo, ebo) in _ownedMeshes) { _gl.DeleteVertexArray(vao); _gl.DeleteBuffer(vbo); if (ebo != 0) _gl.DeleteBuffer(ebo); }
        _ownedMeshes.Clear();
        _whiteTex = 0; // owned-texture list held it; EnsureMeshProgram re-creates on demand
    }

    public void Dispose()
    {
        if (!_ready) return;
        foreach (var t in _ownedTextures) _gl.DeleteTexture(t);
        _ownedTextures.Clear();
        _gl.DeleteBuffer(_quadVbo);
        _gl.DeleteBuffer(_instVbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
        _ready = false;
    }

    // ---- M47 mesh-primitive particles (.scb/.sco): per-particle uniforms, simple textured draw ----
    private uint _meshProgram;
    private int _muViewProj, _muWorldPos, _muScale, _muRot, _muColor, _muTex, _muUvOffset;
    private uint _whiteTex;

    private unsafe void EnsureMeshProgram()
    {
        if (_meshProgram == 0)
        {
            _meshProgram = ShaderUtil.CreateProgram(_gl, _gles, MeshVert, MeshFrag);
            _muViewProj = _gl.GetUniformLocation(_meshProgram, "uViewProj");
            _muWorldPos = _gl.GetUniformLocation(_meshProgram, "uWorldPos");
            _muScale = _gl.GetUniformLocation(_meshProgram, "uScale");
            _muRot = _gl.GetUniformLocation(_meshProgram, "uRot");
            _muColor = _gl.GetUniformLocation(_meshProgram, "uColor");
            _muTex = _gl.GetUniformLocation(_meshProgram, "uTex");
            _muUvOffset = _gl.GetUniformLocation(_meshProgram, "uUvOffset");
        }
        if (_whiteTex == 0) _whiteTex = UploadTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
    }

    /// <summary>Upload an emitter's mesh (pos3 + uv2 per vertex). Pass <paramref name="indices"/> for
    /// indexed (.skn) meshes — drawn with DrawElements; triangle-soup .scb meshes draw sequentially.</summary>
    public unsafe void UploadEmitterMesh(VfxParticleSimulator.EmitterState es, float[] positions, float[] uvs, uint[]? indices = null)
    {
        if (!_ready) return;
        EnsureMeshProgram();
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
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, es.Texture != 0 ? es.Texture : _whiteTex);
        if (IsAdditive(es.Def.BlendMode)) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        // M47c: mesh particles animate by SCROLLING their texture along the mesh UVs (waterfall flow) —
        // matches Riot's particle-system shader (Scrolling_Rate cbuffer + birthUvScrollRate data).
        var scroll = es.Def.UvScrollRate * es.Age;
        _gl.Uniform2(_muUvOffset, scroll.X, scroll.Y);
        for (int i = 0; i < es.InstanceCount; i++)
        {
            int o = i * Stride;   // [cx,cy,cz, sx,sy, r,g,b,a, rot, frame]
            _gl.Uniform3(_muWorldPos, es.Instances[o], es.Instances[o + 1], es.Instances[o + 2]);
            // mesh particles use birthScale.x as a uniform scale; a scale of ~1 means unscaled geometry
            float sc = MathF.Max(0.01f, es.Instances[o + 3]);
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
out vec2 vUv;
void main(){
    float s = sin(uRot); float c = cos(uRot);
    vec3 p = vec3(aPos.x * c - aPos.z * s, aPos.y, aPos.x * s + aPos.z * c) * uScale + uWorldPos;
    gl_Position = uViewProj * vec4(p, 1.0);
    vUv = aUv + uUvOffset;
}";

    private const string MeshFrag = @"
in vec2 vUv;
uniform sampler2D uTex;
uniform vec4 uColor;
out vec4 fragColor;
void main(){
    fragColor = texture(uTex, vUv) * uColor;
}";

    private const string Vert = @"
layout(location=0) in vec2 aCorner;    // base quad corner in [-0.5, 0.5]
layout(location=1) in vec3 aCenter;    // per-instance world center
layout(location=2) in vec2 aSize;      // per-instance width, height
layout(location=3) in vec4 aColor;     // per-instance rgba
layout(location=4) in vec2 aRotFrame;  // per-instance rotation (rad), flipbook frame
uniform mat4 uViewProj;
uniform vec3 uCamRight;
uniform vec3 uCamUp;
uniform vec2 uTexDiv;                   // flipbook grid columns, rows
out vec2 vUv;
out vec4 vColor;
void main(){
    float s = sin(aRotFrame.x);
    float c = cos(aRotFrame.x);
    vec2 rc = vec2(aCorner.x * c - aCorner.y * s, aCorner.x * s + aCorner.y * c);
    vec3 world = aCenter + uCamRight * (rc.x * aSize.x) + uCamUp * (rc.y * aSize.y);
    gl_Position = uViewProj * vec4(world, 1.0);
    vec2 cell = aCorner + vec2(0.5, 0.5);      // [0,1] within the frame cell
    float cols = max(uTexDiv.x, 1.0);
    float rows = max(uTexDiv.y, 1.0);
    float frame = aRotFrame.y;
    float fx = mod(frame, cols);
    float fy = floor(frame / cols);
    vUv = (vec2(fx, fy) + vec2(cell.x, 1.0 - cell.y)) / vec2(cols, rows);
    vColor = aColor;
}";

    private const string Frag = @"
in vec2 vUv;
in vec4 vColor;
uniform sampler2D uTex;
out vec4 fragColor;
void main(){
    vec4 t = texture(uTex, vUv);
    fragColor = t * vColor;
}";
}
