using System.Numerics;
using Silk.NET.OpenGL;

namespace ReyEngine.Rendering;

/// <summary>
/// Renders a ground grid as the empty-scene backdrop. This is the foundation the
/// mesh renderer (SKN/MAPGEO) will build on: shared shader-compile + camera plumbing.
/// </summary>
public sealed class GridRenderer : IDisposable
{
    private GL _gl = null!;
    private uint _vao, _vbo, _program;
    private int _uMvp, _uColor;
    private int _gridVertexCount;
    private int _axisOffset;
    private bool _ready;

    private const string VertexSrc = @"#version 330 core
layout(location = 0) in vec3 aPos;
uniform mat4 uMvp;
void main() { gl_Position = uMvp * vec4(aPos, 1.0); }";

    private const string FragmentSrc = @"#version 330 core
out vec4 FragColor;
uniform vec4 uColor;
void main() { FragColor = uColor; }";

    public void Initialize(GL gl)
    {
        _gl = gl;
        _program = CreateProgram(VertexSrc, FragmentSrc);
        _uMvp = _gl.GetUniformLocation(_program, "uMvp");
        _uColor = _gl.GetUniformLocation(_program, "uColor");

        float[] verts = BuildGeometry(halfCells: 20, cell: 100f, out _gridVertexCount, out _axisOffset);
        UploadVerts(verts);
        _ready = true;
    }

    private unsafe void UploadVerts(float[] verts)
    {
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);
        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = verts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)(3 * sizeof(float)), (void*)0);
        _gl.BindVertexArray(0);
    }

    public void Render(Matrix4x4 viewProjection)
    {
        if (!_ready) return;

        var m = Matrix4x4.Transpose(viewProjection); // numerics row-major -> GL column-major
        _gl.UseProgram(_program);
        _gl.UniformMatrix4(_uMvp, 1, false, in m.M11);
        _gl.BindVertexArray(_vao);

        // Grid lines
        _gl.Uniform4(_uColor, 0.16f, 0.19f, 0.27f, 1f);
        _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_gridVertexCount);

        // X axis (cyan) and Z axis (magenta) for orientation
        _gl.Uniform4(_uColor, 0.21f, 0.89f, 0.76f, 1f);
        _gl.DrawArrays(PrimitiveType.Lines, _axisOffset, 2);
        _gl.Uniform4(_uColor, 0.42f, 0.36f, 0.90f, 1f);
        _gl.DrawArrays(PrimitiveType.Lines, _axisOffset + 2, 2);

        _gl.BindVertexArray(0);
        _gl.UseProgram(0);
    }

    private static float[] BuildGeometry(int halfCells, float cell, out int gridVerts, out int axisOffset)
    {
        var list = new List<float>();
        float ext = halfCells * cell;
        for (int i = -halfCells; i <= halfCells; i++)
        {
            float p = i * cell;
            list.AddRange(new[] { p, 0f, -ext, p, 0f, ext });   // parallel to Z
            list.AddRange(new[] { -ext, 0f, p, ext, 0f, p });   // parallel to X
        }
        gridVerts = list.Count / 3;
        axisOffset = gridVerts;

        // Axes (drawn slightly above the grid)
        list.AddRange(new[] { -ext, 0.1f, 0f, ext, 0.1f, 0f }); // X
        list.AddRange(new[] { 0f, 0.1f, -ext, 0f, 0.1f, ext }); // Z
        return list.ToArray();
    }

    private uint CreateProgram(string vs, string fs)
    {
        uint v = Compile(ShaderType.VertexShader, vs);
        uint f = Compile(ShaderType.FragmentShader, fs);
        uint p = _gl.CreateProgram();
        _gl.AttachShader(p, v);
        _gl.AttachShader(p, f);
        _gl.LinkProgram(p);
        _gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("Program link failed: " + _gl.GetProgramInfoLog(p));
        _gl.DetachShader(p, v);
        _gl.DetachShader(p, f);
        _gl.DeleteShader(v);
        _gl.DeleteShader(f);
        return p;
    }

    private uint Compile(ShaderType type, string src)
    {
        uint s = _gl.CreateShader(type);
        _gl.ShaderSource(s, src);
        _gl.CompileShader(s);
        _gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"{type} compile failed: " + _gl.GetShaderInfoLog(s));
        return s;
    }

    public void Dispose()
    {
        if (!_ready) return;
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
        _ready = false;
    }
}
