using System.Numerics;
using ReyEngine.Core.Decoding;
using Silk.NET.OpenGL;

namespace ReyEngine.Rendering;

/// <summary>
/// M122: draws the scene's skybox. Three sources:
///   Cubemap  - League's DDS cubemaps (riots_sru_skybox_cubemap.dds), samplerCube on a unit cube.
///   Equirect - a single 2D sky texture (ha_skybox_01.tex, custom user images) sampled by view
///              direction on the same cube (no seams - the direction math ignores the geometry).
///   Mesh     - authored skybox domes (.scb/.sco/.skn) with their own UVs and a 2D texture.
/// Drawn FIRST each frame with depth test and depth writes off, centered on the camera (the view
/// matrix is rotation-only), so the scene overdraws it and it never parallaxes.
/// All GLSL below is ASCII-only - a single em-dash in a comment kills the GL compile (see M117b).
/// </summary>
public sealed class SkyboxRenderer : IDisposable
{
    private GL _gl = null!;
    private bool _gles, _ready;

    private uint _dirProgram, _meshProgram;
    private int _dViewRot, _dProj, _dCube, _dTex, _dMode;
    private int _mViewRot, _mProj, _mTex;

    private uint _cubeVao, _cubeVbo;                 // unit cube for cubemap/equirect
    private uint _meshVao, _meshVbo, _meshEbo;       // authored dome
    private int _meshIndexCount, _meshVertexCount;

    private uint _cubeTex;      // GL cube map
    private uint _flatTex;      // 2D texture (equirect or mesh)
    private int _mode = -1;     // -1 none, 0 cubemap, 1 equirect, 2 mesh

    public bool HasSkybox => _mode >= 0;

    public void Initialize(GL gl)
    {
        _gl = gl;
        _gles = ShaderUtil.DetectGles(gl);

        _dirProgram = ShaderUtil.CreateProgram(gl, _gles, DirVert, DirFrag);
        _dViewRot = gl.GetUniformLocation(_dirProgram, "uViewRot");
        _dProj = gl.GetUniformLocation(_dirProgram, "uProj");
        _dCube = gl.GetUniformLocation(_dirProgram, "uCube");
        _dTex = gl.GetUniformLocation(_dirProgram, "uTex");
        _dMode = gl.GetUniformLocation(_dirProgram, "uMode");

        _meshProgram = ShaderUtil.CreateProgram(gl, _gles, MeshVert, MeshFrag);
        _mViewRot = gl.GetUniformLocation(_meshProgram, "uViewRot");
        _mProj = gl.GetUniformLocation(_meshProgram, "uProj");
        _mTex = gl.GetUniformLocation(_meshProgram, "uTex");

        _cubeVao = gl.GenVertexArray();
        _cubeVbo = gl.GenBuffer();
        UploadCubeGeometry();

        _meshVao = gl.GenVertexArray();
        _meshVbo = gl.GenBuffer();
        _meshEbo = gl.GenBuffer();
        _ready = true;
    }

    private unsafe void UploadCubeGeometry()
    {
        // 36 verts, positions only - the fragment shader works from the interpolated direction.
        float[] v =
        {
            -1,-1,-1,  1,-1,-1,  1, 1,-1,  -1,-1,-1,  1, 1,-1, -1, 1,-1,   // -Z
            -1,-1, 1,  1, 1, 1,  1,-1, 1,  -1,-1, 1, -1, 1, 1,  1, 1, 1,   // +Z
            -1,-1,-1, -1, 1,-1, -1, 1, 1,  -1,-1,-1, -1, 1, 1, -1,-1, 1,   // -X
             1,-1,-1,  1, 1, 1,  1, 1,-1,   1,-1,-1,  1,-1, 1,  1, 1, 1,   // +X
            -1, 1,-1,  1, 1,-1,  1, 1, 1,  -1, 1,-1,  1, 1, 1, -1, 1, 1,   // +Y
            -1,-1,-1, -1,-1, 1,  1,-1, 1,  -1,-1,-1,  1,-1, 1,  1,-1,-1,   // -Y
        };
        _gl.BindVertexArray(_cubeVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _cubeVbo);
        fixed (float* p = v)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(v.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        _gl.BindVertexArray(0);
    }

    public void Clear()
    {
        if (!_ready) return;
        _mode = -1;
        if (_cubeTex != 0) { _gl.DeleteTexture(_cubeTex); _cubeTex = 0; }
        if (_flatTex != 0) { _gl.DeleteTexture(_flatTex); _flatTex = 0; }
        _meshIndexCount = 0; _meshVertexCount = 0;
    }

    public unsafe void SetCubemap(CubemapImage cm)
    {
        if (!_ready) return;
        Clear();
        _cubeTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.TextureCubeMap, _cubeTex);
        for (int f = 0; f < 6; f++)
            fixed (byte* p = cm.Faces[f])
                _gl.TexImage2D(TextureTarget.TextureCubeMapPositiveX + f, 0, InternalFormat.Rgba8,
                    (uint)cm.FaceSize, (uint)cm.FaceSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
        _mode = 0;
    }

    public void SetEquirect(TextureImage img)
    {
        if (!_ready) return;
        Clear();
        _flatTex = Upload2D(img, wrapU: true);
        _mode = 1;
    }

    public unsafe void SetMesh(float[] positions, float[] uvs, uint[] indices, TextureImage? texture)
    {
        if (!_ready) return;
        Clear();
        int verts = positions.Length / 3;
        var inter = new float[verts * 5];
        for (int i = 0; i < verts; i++)
        {
            inter[i * 5 + 0] = positions[i * 3 + 0];
            inter[i * 5 + 1] = positions[i * 3 + 1];
            inter[i * 5 + 2] = positions[i * 3 + 2];
            inter[i * 5 + 3] = i * 2 + 1 < uvs.Length ? uvs[i * 2] : 0f;
            inter[i * 5 + 4] = i * 2 + 1 < uvs.Length ? uvs[i * 2 + 1] : 0f;
        }
        _gl.BindVertexArray(_meshVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _meshVbo);
        fixed (float* p = inter)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(inter.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)(3 * sizeof(float)));
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _meshEbo);
        fixed (uint* ip = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), ip, BufferUsageARB.StaticDraw);
        _gl.BindVertexArray(0);
        _meshIndexCount = indices.Length;
        _meshVertexCount = verts;

        _flatTex = texture is null ? UploadWhite() : Upload2D(texture, wrapU: true);
        _mode = 2;
    }

    private unsafe uint Upload2D(TextureImage img, bool wrapU)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* p = img.Rgba)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)(wrapU ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge));
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private unsafe uint UploadWhite()
    {
        uint tex = _gl.GenTexture();
        var px = new byte[] { 255, 255, 255, 255 };
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* p = px)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    /// <summary>Draw the skybox. <paramref name="viewRot"/> must be the camera view matrix with its
    /// translation stripped (and the world's X-mirror applied), so the sky rotates but never moves.</summary>
    public unsafe void Render(Matrix4x4 viewRot, Matrix4x4 proj)
    {
        if (!_ready || _mode < 0) return;

        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);

        if (_mode == 2 && _meshIndexCount + _meshVertexCount > 0)
        {
            _gl.UseProgram(_meshProgram);
            _gl.UniformMatrix4(_mViewRot, 1, false, in viewRot.M11);
            _gl.UniformMatrix4(_mProj, 1, false, in proj.M11);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _flatTex);
            _gl.Uniform1(_mTex, 0);
            _gl.BindVertexArray(_meshVao);
            if (_meshIndexCount > 0) _gl.DrawElements(PrimitiveType.Triangles, (uint)_meshIndexCount, DrawElementsType.UnsignedInt, (void*)0);
            else _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_meshVertexCount);
        }
        else
        {
            _gl.UseProgram(_dirProgram);
            _gl.UniformMatrix4(_dViewRot, 1, false, in viewRot.M11);
            _gl.UniformMatrix4(_dProj, 1, false, in proj.M11);
            _gl.Uniform1(_dMode, _mode);
            if (_mode == 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.TextureCubeMap, _cubeTex);
                _gl.Uniform1(_dCube, 0);
            }
            else
            {
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, _flatTex);
                _gl.Uniform1(_dTex, 1);
            }
            _gl.BindVertexArray(_cubeVao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 36);
        }

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    public void Dispose()
    {
        if (!_ready) return;
        Clear();
        _gl.DeleteProgram(_dirProgram);
        _gl.DeleteProgram(_meshProgram);
        _gl.DeleteVertexArray(_cubeVao);
        _gl.DeleteBuffer(_cubeVbo);
        _gl.DeleteVertexArray(_meshVao);
        _gl.DeleteBuffer(_meshVbo);
        _gl.DeleteBuffer(_meshEbo);
        _ready = false;
    }

    // direction-based path: cubemap or equirect sampled from the interpolated view direction
    private const string DirVert = @"
layout(location=0) in vec3 aPos;
uniform mat4 uViewRot;
uniform mat4 uProj;
out vec3 vDir;
void main(){
    vDir = aPos;
    vec4 p = uProj * uViewRot * vec4(aPos, 1.0);
    gl_Position = p.xyww;   // depth = 1.0, always the far plane
}";

    private const string DirFrag = @"
in vec3 vDir;
uniform samplerCube uCube;
uniform sampler2D uTex;
uniform int uMode;   // 0 cubemap, 1 equirect
out vec4 fragColor;
void main(){
    vec3 d = normalize(vDir);
    if (uMode == 0) { fragColor = vec4(texture(uCube, d).rgb, 1.0); return; }
    float u = atan(d.x, d.z) / 6.2831853 + 0.5;
    float v = acos(clamp(d.y, -1.0, 1.0)) / 3.1415927;
    fragColor = vec4(texture(uTex, vec2(u, v)).rgb, 1.0);
}";

    // authored dome path: the mesh brings its own UVs
    private const string MeshVert = @"
layout(location=0) in vec3 aPos;
layout(location=1) in vec2 aUv;
uniform mat4 uViewRot;
uniform mat4 uProj;
out vec2 vUv;
void main(){
    vUv = aUv;
    vec4 p = uProj * uViewRot * vec4(aPos, 1.0);
    gl_Position = p.xyww;
}";

    private const string MeshFrag = @"
in vec2 vUv;
uniform sampler2D uTex;
out vec4 fragColor;
void main(){
    fragColor = vec4(texture(uTex, vUv).rgb, 1.0);
}";
}
