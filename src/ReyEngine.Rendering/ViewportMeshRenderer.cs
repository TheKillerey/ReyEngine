using System.Numerics;
using Silk.NET.OpenGL;

namespace ReyEngine.Rendering;

/// <summary>
/// Renders a single preview mesh: solid (simple diffuse), wireframe (line-indexed,
/// so it works on GL ES too), plus optional bounding box and skeleton bone overlays.
/// </summary>
public sealed class ViewportMeshRenderer : IDisposable
{
    private GL _gl = null!;
    private bool _ready;

    private uint _meshProgram, _lineProgram;
    private int _mMvp, _mModel, _mColor, _mLight;
    private int _lMvp, _lColor;

    // Mesh: interleaved [pos3, nrm3, uv2]
    private uint _vao, _vbo, _ebo, _wireEbo;
    private int _indexCount, _wireIndexCount;
    private bool _hasMesh;

    // Overlays (pos-only line lists)
    private uint _boundsVao, _boundsVbo;
    private uint _boneVao, _boneVbo;
    private int _boundsVerts, _boneVerts;

    private const string MeshVert = @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUv;
uniform mat4 uMvp;
uniform mat4 uModel;
out vec3 vN;
void main() {
    vN = mat3(uModel) * aNormal;
    gl_Position = uMvp * vec4(aPos, 1.0);
}";

    private const string MeshFrag = @"
in vec3 vN;
out vec4 FragColor;
uniform vec3 uColor;
uniform vec3 uLight;
void main() {
    vec3 n = length(vN) > 0.001 ? normalize(vN) : vec3(0.0, 1.0, 0.0);
    float d = max(dot(n, normalize(-uLight)), 0.0);
    vec3 c = uColor * (0.28 + 0.8 * d);
    FragColor = vec4(c, 1.0);
}";

    private const string LineVert = @"
layout(location = 0) in vec3 aPos;
uniform mat4 uMvp;
void main() { gl_Position = uMvp * vec4(aPos, 1.0); }";

    private const string LineFrag = @"
out vec4 FragColor;
uniform vec4 uColor;
void main() { FragColor = uColor; }";

    public bool HasMesh => _hasMesh;

    public void Initialize(GL gl, bool gles)
    {
        _gl = gl;
        _meshProgram = ShaderUtil.CreateProgram(gl, gles, MeshVert, MeshFrag);
        _mMvp = gl.GetUniformLocation(_meshProgram, "uMvp");
        _mModel = gl.GetUniformLocation(_meshProgram, "uModel");
        _mColor = gl.GetUniformLocation(_meshProgram, "uColor");
        _mLight = gl.GetUniformLocation(_meshProgram, "uLight");

        _lineProgram = ShaderUtil.CreateProgram(gl, gles, LineVert, LineFrag);
        _lMvp = gl.GetUniformLocation(_lineProgram, "uMvp");
        _lColor = gl.GetUniformLocation(_lineProgram, "uColor");

        _boundsVao = gl.GenVertexArray();
        _boundsVbo = gl.GenBuffer();
        _boneVao = gl.GenVertexArray();
        _boneVbo = gl.GenBuffer();
        _ready = true;
    }

    public unsafe void SetMesh(float[] positions, float[] normals, float[] uvs, uint[] indices,
        int vertexCount, Vector3 min, Vector3 max)
    {
        if (!_ready) return;
        DeleteMeshBuffers();

        var interleaved = new float[vertexCount * 8];
        for (int i = 0; i < vertexCount; i++)
        {
            interleaved[i * 8 + 0] = positions[i * 3 + 0];
            interleaved[i * 8 + 1] = positions[i * 3 + 1];
            interleaved[i * 8 + 2] = positions[i * 3 + 2];
            interleaved[i * 8 + 3] = normals[i * 3 + 0];
            interleaved[i * 8 + 4] = normals[i * 3 + 1];
            interleaved[i * 8 + 5] = normals[i * 3 + 2];
            interleaved[i * 8 + 6] = uvs[i * 2 + 0];
            interleaved[i * 8 + 7] = uvs[i * 2 + 1];
        }

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = interleaved)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(interleaved.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        uint stride = 8 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
        _indexCount = indices.Length;

        // Wireframe edge indices (triangle -> 3 edges).
        var wire = new uint[indices.Length * 2];
        int w = 0;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            uint a = indices[t], b = indices[t + 1], c = indices[t + 2];
            wire[w++] = a; wire[w++] = b;
            wire[w++] = b; wire[w++] = c;
            wire[w++] = c; wire[w++] = a;
        }
        _wireEbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _wireEbo);
        fixed (uint* p = wire)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(wire.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
        _wireIndexCount = wire.Length;

        _gl.BindVertexArray(0);

        UploadLines(_boundsVao, _boundsVbo, BuildBoxLines(min, max), out _boundsVerts);
        _hasMesh = true;
    }

    public void SetBoneSegments(float[]? lineVerts)
    {
        if (!_ready) return;
        if (lineVerts is null || lineVerts.Length == 0) { _boneVerts = 0; return; }
        UploadLines(_boneVao, _boneVbo, lineVerts, out _boneVerts);
    }

    public void ClearMesh()
    {
        _hasMesh = false;
        _boneVerts = 0;
        _boundsVerts = 0;
    }

    public unsafe void Render(Matrix4x4 viewProjection, bool wireframe, bool showBounds, bool showBones)
    {
        if (!_ready) return;
        var m = Matrix4x4.Transpose(viewProjection);

        if (_hasMesh)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Less);
            _gl.Disable(EnableCap.CullFace);

            _gl.UseProgram(_meshProgram);
            _gl.UniformMatrix4(_mMvp, 1, false, in m.M11);
            var model = Matrix4x4.Identity;
            _gl.UniformMatrix4(_mModel, 1, false, in model.M11);
            _gl.Uniform3(_mColor, 0.62f, 0.66f, 0.74f);
            _gl.Uniform3(_mLight, -0.4f, -0.85f, -0.45f);

            _gl.BindVertexArray(_vao);
            if (wireframe)
            {
                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _wireEbo);
                _gl.DrawElements(PrimitiveType.Lines, (uint)_wireIndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }
            else
            {
                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
                _gl.DrawElements(PrimitiveType.Triangles, (uint)_indexCount, DrawElementsType.UnsignedInt, (void*)0);
            }
            _gl.BindVertexArray(0);
        }

        if ((showBounds && _boundsVerts > 0) || (showBones && _boneVerts > 0))
        {
            _gl.UseProgram(_lineProgram);
            _gl.UniformMatrix4(_lMvp, 1, false, in m.M11);

            if (showBounds && _boundsVerts > 0)
            {
                _gl.Uniform4(_lColor, 0.36f, 0.89f, 0.76f, 1f);
                _gl.BindVertexArray(_boundsVao);
                _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_boundsVerts);
            }
            if (showBones && _boneVerts > 0)
            {
                _gl.Disable(EnableCap.DepthTest); // draw bones over the mesh
                _gl.Uniform4(_lColor, 1.0f, 0.65f, 0.2f, 1f);
                _gl.BindVertexArray(_boneVao);
                _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_boneVerts);
            }
            _gl.BindVertexArray(0);
        }
    }

    private unsafe void UploadLines(uint vao, uint vbo, float[] verts, out int vertexCount)
    {
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = verts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)(3 * sizeof(float)), (void*)0);
        _gl.BindVertexArray(0);
        vertexCount = verts.Length / 3;
    }

    private static float[] BuildBoxLines(Vector3 a, Vector3 b)
    {
        Vector3[] c =
        {
            new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, a.Y, b.Z), new(a.X, a.Y, b.Z),
            new(a.X, b.Y, a.Z), new(b.X, b.Y, a.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z),
        };
        int[,] edges = { {0,1},{1,2},{2,3},{3,0}, {4,5},{5,6},{6,7},{7,4}, {0,4},{1,5},{2,6},{3,7} };
        var list = new float[edges.GetLength(0) * 2 * 3];
        int k = 0;
        for (int e = 0; e < edges.GetLength(0); e++)
        {
            foreach (int idx in new[] { edges[e, 0], edges[e, 1] })
            {
                list[k++] = c[idx].X; list[k++] = c[idx].Y; list[k++] = c[idx].Z;
            }
        }
        return list;
    }

    private void DeleteMeshBuffers()
    {
        if (!_hasMesh) return;
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteBuffer(_wireEbo);
        _gl.DeleteVertexArray(_vao);
        _hasMesh = false;
    }

    public void Dispose()
    {
        if (!_ready) return;
        DeleteMeshBuffers();
        _gl.DeleteBuffer(_boundsVbo);
        _gl.DeleteBuffer(_boneVbo);
        _gl.DeleteVertexArray(_boundsVao);
        _gl.DeleteVertexArray(_boneVao);
        _gl.DeleteProgram(_meshProgram);
        _gl.DeleteProgram(_lineProgram);
        _ready = false;
    }
}
