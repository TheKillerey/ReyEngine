using System.Numerics;
using Silk.NET.OpenGL;

namespace ReyEngine.Rendering;

/// <summary>
/// Renders a preview mesh: per-submesh textured diffuse (solid), wireframe (line-indexed),
/// plus optional bounding box and skeleton bone overlays.
/// </summary>
public sealed class ViewportMeshRenderer : IDisposable
{
    private GL _gl = null!;
    private bool _ready;

    private uint _meshProgram, _lineProgram;
    private int _mMvp, _mModel, _mLight, _mTex, _mHasTex, _mBaseColor, _mMode, _mCamPos;
    private int _mMask, _mGradient, _mEmissive, _mHasMask, _mHasGradient, _mHasEmissive;
    private int _mMatCap, _mMatCapMask, _mHasMatCap, _mHasMatCapMask, _mView;
    private int _mUvScaleOffset, _mUvRot, _mUsesRim, _mUsesSpec;   // M32: per-material UV + feature flags
    private int _lMvp, _lColor;

    private uint _vao, _vbo, _ebo, _wireEbo;
    private int _indexCount, _wireIndexCount;
    private bool _hasMesh;
    private float[]? _interleaved; // kept so per-frame skinning can update pos+normal in place
    private int _meshVertexCount;

    private uint _whiteTex;
    private SubmeshDraw[] _submeshes = Array.Empty<SubmeshDraw>();
    private readonly List<uint> _ownedTextures = new(); // unique textures, shared across submeshes

    private uint _boundsVao, _boundsVbo;
    private uint _boneVao, _boneVbo;
    private uint _highlightVao, _highlightVbo;
    private uint _groupBoundsVao, _groupBoundsVbo;
    private uint _gizmoVao, _gizmoVbo;
    private int _boundsVerts, _boneVerts, _highlightVerts, _groupBoundsVerts;
    private bool _hasGizmo;
    private Vector3 _gizmoPivot;
    private float _gizmoArmLength;

    private struct SubmeshDraw
    {
        public int Start;
        public int Count;
        public uint Texture;     // slot 0 diffuse
        public uint Mask;        // slot 1
        public uint Gradient;    // slot 2
        public uint Emissive;    // slot 3
        public uint MatCap;      // slot 4
        public uint MatCapMask;  // slot 5
        public bool Visible;     // layer/visibility filter (map dragon/baron)

        // M32 per-material preview data. Defaults are identity/off so untouched submeshes render as before.
        public Vector4 UvScaleOffset;   // xy scale, zw offset
        public float UvRotationRadians;
        public bool UsesRim;
        public bool UsesSpecular;

        public static SubmeshDraw Create(int start, int count) =>
            new() { Start = start, Count = count, Visible = true, UvScaleOffset = new Vector4(1, 1, 0, 0) };
    }

    /// <summary>Per-submesh preview material data pushed from the App's resolved <c>MaterialProfile</c> (M32).</summary>
    public readonly record struct SubmeshMaterial(
        bool UsesRim, bool UsesSpecular, Vector2 UvScale, Vector2 UvOffset, float UvRotationDegrees)
    {
        public static readonly SubmeshMaterial Default = new(false, false, Vector2.One, Vector2.Zero, 0f);
    }

    private const string MeshVert = @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUv;
uniform mat4 uMvp;
uniform mat4 uModel;
out vec3 vN;
out vec2 vUv;
out vec3 vWorld;
void main() {
    vN = mat3(uModel) * aNormal;
    vUv = aUv;
    vWorld = (uModel * vec4(aPos, 1.0)).xyz;
    gl_Position = uMvp * vec4(aPos, 1.0);
}";

    // uMode: 0 Basic · 1 RiotApprox · 2 Debug base · 3 Debug alpha · 4 Debug normal · 5 Debug mask
    //        6 Debug emissive · 7 Debug matcap · 8 Debug UV checker · 9 Debug specular
    // M32: rim + specular are gated per-material (uUsesRim/uUsesSpec) — no fake specular by default.
    // The base UV is transformed per-material by uUvScaleOffset (xy scale, zw offset) + uUvRot (radians).
    private const string MeshFrag = @"
in vec3 vN;
in vec2 vUv;
in vec3 vWorld;
out vec4 FragColor;
uniform vec3 uLight;
uniform sampler2D uTex;
uniform sampler2D uMask;
uniform sampler2D uGradient;
uniform sampler2D uEmissive;
uniform sampler2D uMatCap;
uniform sampler2D uMatCapMask;
uniform int uHasTex;
uniform int uHasMask;
uniform int uHasGradient;
uniform int uHasEmissive;
uniform int uHasMatCap;
uniform int uHasMatCapMask;
uniform vec3 uBaseColor;
uniform int uMode;
uniform vec3 uCamPos;
uniform mat4 uView;
uniform vec4 uUvScaleOffset;   // xy = scale, zw = offset (identity = 1,1,0,0)
uniform float uUvRot;          // radians, rotate about (0.5, 0.5); 0 = none
uniform int uUsesRim;          // fresnel rim highlight only when the material profile asks for it
uniform int uUsesSpec;         // specular highlight only when the material profile asks for it

// League MatCap_Tex: a spheremap of fake studio lighting sampled by the view-space normal.
vec3 matcapColour(vec3 worldN) {
    vec3 vn = normalize(mat3(uView) * worldN);
    vec2 uv = vn.xy * 0.5 + 0.5;
    return texture(uMatCap, uv).rgb;
}
// Per-material UV transform: optional rotation about the centre, then scale + offset.
vec2 xformUv(vec2 uv0) {
    vec2 uv = uv0;
    if (uUvRot != 0.0) {
        float c = cos(uUvRot), s = sin(uUvRot);
        vec2 p = uv - 0.5;
        uv = vec2(p.x * c - p.y * s, p.x * s + p.y * c) + 0.5;
    }
    return uv * uUvScaleOffset.xy + uUvScaleOffset.zw;
}
void main() {
    vec3 n = length(vN) > 0.001 ? normalize(vN) : vec3(0.0, 1.0, 0.0);
    vec2 uv = xformUv(vUv);
    vec4 tex = (uHasTex == 1) ? texture(uTex, uv) : vec4(uBaseColor, 1.0);
    vec3 base = tex.rgb;
    float alpha = (uHasTex == 1) ? tex.a : 1.0;

    if (uMode == 2) { FragColor = vec4(base, 1.0); return; }                 // debug: base/diffuse
    if (uMode == 3) { FragColor = vec4(vec3(alpha), 1.0); return; }          // debug: alpha
    if (uMode == 4) { FragColor = vec4(n * 0.5 + 0.5, 1.0); return; }        // debug: normals
    if (uMode == 5) {                                                        // debug: mask (white if none)
        vec3 mk = (uHasMask == 1) ? texture(uMask, uv).rgb : vec3(1.0);
        FragColor = vec4(mk, 1.0); return;
    }
    if (uMode == 6) {                                                        // debug: emissive (black if none)
        vec3 em = (uHasEmissive == 1) ? texture(uEmissive, uv).rgb : vec3(0.0);
        FragColor = vec4(em, 1.0); return;
    }
    if (uMode == 7) {                                                        // debug: matcap (grey if none)
        vec3 mc = (uHasMatCap == 1) ? matcapColour(n) : vec3(0.2);
        FragColor = vec4(mc, 1.0); return;
    }
    if (uMode == 8) {                                                        // debug: UV checker (post-transform)
        vec2 t = floor(uv * 8.0);
        float c = mod(t.x + t.y, 2.0);
        FragColor = vec4(mix(vec3(0.14, 0.15, 0.19), vec3(0.85, 0.86, 0.92), c), 1.0); return;
    }

    vec3 viewDir = normalize(uCamPos - vWorld);
    float d = max(dot(n, normalize(-uLight)), 0.0);
    float light = 0.35 + 0.75 * d;
    vec3 col = base * light;

    // Specular highlight — computed only when the material's profile enables it (League materials are
    // diffuse/lambert by default). Blinn-Phong half-vector term.
    float specTerm = 0.0;
    if (uUsesSpec == 1) {
        vec3 h = normalize(normalize(-uLight) + viewDir);
        specTerm = pow(max(dot(n, h), 0.0), 32.0);
    }
    if (uMode == 9) { FragColor = vec4(vec3(specTerm), 1.0); return; }       // debug: specular only

    if (uMode == 1) {
        // RiotApprox. Alpha cutout, then the fresnel rim (only if the material uses rim) coloured by its
        // Gradient sampler and gated by its Mask, + matcap, + emissive glow, + optional specular.
        if (alpha < 0.35) discard;
        if (uUsesRim == 1) {
            float fres = pow(1.0 - max(dot(n, viewDir), 0.0), 3.0);
            vec3 rimCol = (uHasGradient == 1)
                ? texture(uGradient, vec2(clamp(fres, 0.02, 0.98), 0.5)).rgb
                : (0.5 + 0.5 * base);
            float gate = (uHasMask == 1) ? mix(0.5, 1.0, texture(uMask, uv).r) : 1.0;
            col += fres * 0.6 * rimCol * gate;
        }
        if (uHasMatCap == 1) {
            float mcGate = (uHasMatCapMask == 1) ? texture(uMatCapMask, uv).r : 1.0;
            col += matcapColour(n) * 0.6 * mcGate;
        }
        if (uHasEmissive == 1) {
            float em = texture(uEmissive, uv).r;
            col += base * em * 1.5;
        }
        col += specTerm * 0.5;   // white-ish highlight, only when uUsesSpec
    }
    FragColor = vec4(col, 1.0);
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
    public int SubmeshCount => _submeshes.Length;

    public void Initialize(GL gl, bool gles)
    {
        _gl = gl;
        _meshProgram = ShaderUtil.CreateProgram(gl, gles, MeshVert, MeshFrag);
        _mMvp = gl.GetUniformLocation(_meshProgram, "uMvp");
        _mModel = gl.GetUniformLocation(_meshProgram, "uModel");
        _mLight = gl.GetUniformLocation(_meshProgram, "uLight");
        _mTex = gl.GetUniformLocation(_meshProgram, "uTex");
        _mHasTex = gl.GetUniformLocation(_meshProgram, "uHasTex");
        _mBaseColor = gl.GetUniformLocation(_meshProgram, "uBaseColor");
        _mMode = gl.GetUniformLocation(_meshProgram, "uMode");
        _mCamPos = gl.GetUniformLocation(_meshProgram, "uCamPos");
        _mMask = gl.GetUniformLocation(_meshProgram, "uMask");
        _mGradient = gl.GetUniformLocation(_meshProgram, "uGradient");
        _mEmissive = gl.GetUniformLocation(_meshProgram, "uEmissive");
        _mHasMask = gl.GetUniformLocation(_meshProgram, "uHasMask");
        _mHasGradient = gl.GetUniformLocation(_meshProgram, "uHasGradient");
        _mHasEmissive = gl.GetUniformLocation(_meshProgram, "uHasEmissive");
        _mMatCap = gl.GetUniformLocation(_meshProgram, "uMatCap");
        _mMatCapMask = gl.GetUniformLocation(_meshProgram, "uMatCapMask");
        _mHasMatCap = gl.GetUniformLocation(_meshProgram, "uHasMatCap");
        _mHasMatCapMask = gl.GetUniformLocation(_meshProgram, "uHasMatCapMask");
        _mView = gl.GetUniformLocation(_meshProgram, "uView");
        _mUvScaleOffset = gl.GetUniformLocation(_meshProgram, "uUvScaleOffset");
        _mUvRot = gl.GetUniformLocation(_meshProgram, "uUvRot");
        _mUsesRim = gl.GetUniformLocation(_meshProgram, "uUsesRim");
        _mUsesSpec = gl.GetUniformLocation(_meshProgram, "uUsesSpec");

        _lineProgram = ShaderUtil.CreateProgram(gl, gles, LineVert, LineFrag);
        _lMvp = gl.GetUniformLocation(_lineProgram, "uMvp");
        _lColor = gl.GetUniformLocation(_lineProgram, "uColor");

        _whiteTex = MakeSolidTexture(255, 255, 255);

        _boundsVao = gl.GenVertexArray();
        _boundsVbo = gl.GenBuffer();
        _boneVao = gl.GenVertexArray();
        _boneVbo = gl.GenBuffer();
        _highlightVao = gl.GenVertexArray();
        _highlightVbo = gl.GenBuffer();
        _groupBoundsVao = gl.GenVertexArray();
        _groupBoundsVbo = gl.GenBuffer();
        _gizmoVao = gl.GenVertexArray();
        _gizmoVbo = gl.GenBuffer();
        _ready = true;
    }

    public unsafe void SetMesh(float[] positions, float[] normals, float[] uvs, uint[] indices,
        int vertexCount, Vector3 min, Vector3 max, IReadOnlyList<(int start, int count)> submeshes)
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
        _interleaved = interleaved;
        _meshVertexCount = vertexCount;

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

        if (submeshes.Count == 0)
            _submeshes = new[] { SubmeshDraw.Create(0, indices.Length) };
        else
            _submeshes = submeshes.Select(s => SubmeshDraw.Create(s.start, s.count)).ToArray();

        UploadLines(_boundsVao, _boundsVbo, BuildBoxLines(min, max), out _boundsVerts);
        _hasMesh = true;
    }

    /// <summary>Uploads a texture once and returns its GL id (caller shares it across submeshes).</summary>
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
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _ownedTextures.Add(tex);
        return tex;
    }

    public void SetSubmeshTextureId(int index, uint textureId) => SetSubmeshLayer(index, 0, textureId);

    /// <summary>Show/hide a submesh (map dragon/baron layer filter). No-op outside range.</summary>
    public void SetSubmeshVisible(int index, bool visible)
    {
        if (!_ready || !_hasMesh || index < 0 || index >= _submeshes.Length) return;
        _submeshes[index].Visible = visible;
    }

    /// <summary>Set a submesh texture layer: 0 diffuse · 1 mask · 2 gradient · 3 emissive (0 = none).</summary>
    public void SetSubmeshLayer(int index, int slot, uint textureId)
    {
        if (!_ready || !_hasMesh || index < 0 || index >= _submeshes.Length) return;
        switch (slot)
        {
            case 0: _submeshes[index].Texture = textureId; break;
            case 1: _submeshes[index].Mask = textureId; break;
            case 2: _submeshes[index].Gradient = textureId; break;
            case 3: _submeshes[index].Emissive = textureId; break;
            case 4: _submeshes[index].MatCap = textureId; break;
            case 5: _submeshes[index].MatCapMask = textureId; break;
        }
    }

    /// <summary>Push a submesh's preview material (M32): UV transform + rim/specular feature flags.
    /// No-op outside range; unset submeshes keep the identity/off defaults (render exactly as before).</summary>
    public void SetSubmeshMaterial(int index, SubmeshMaterial mat)
    {
        if (!_ready || !_hasMesh || index < 0 || index >= _submeshes.Length) return;
        _submeshes[index].UvScaleOffset = new Vector4(mat.UvScale.X, mat.UvScale.Y, mat.UvOffset.X, mat.UvOffset.Y);
        _submeshes[index].UvRotationRadians = mat.UvRotationDegrees * (MathF.PI / 180f);
        _submeshes[index].UsesRim = mat.UsesRim;
        _submeshes[index].UsesSpecular = mat.UsesSpecular;
    }

    /// <summary>Reset every submesh's preview material to identity UV + no rim/specular (M32).</summary>
    public void ClearSubmeshMaterials()
    {
        for (int i = 0; i < _submeshes.Length; i++)
        {
            _submeshes[i].UvScaleOffset = new Vector4(1, 1, 0, 0);
            _submeshes[i].UvRotationRadians = 0f;
            _submeshes[i].UsesRim = false;
            _submeshes[i].UsesSpecular = false;
        }
    }

    /// <summary>Replace pos+normal of the existing mesh (keeps UVs/indices/textures) — for per-frame skinning.</summary>
    public unsafe void UpdateVertices(float[] positions, float[] normals)
    {
        if (!_ready || !_hasMesh || _interleaved is null) return;
        int vc = Math.Min(_meshVertexCount, Math.Min(positions.Length / 3, normals.Length / 3));
        for (int i = 0; i < vc; i++)
        {
            _interleaved[i * 8 + 0] = positions[i * 3 + 0];
            _interleaved[i * 8 + 1] = positions[i * 3 + 1];
            _interleaved[i * 8 + 2] = positions[i * 3 + 2];
            _interleaved[i * 8 + 3] = normals[i * 3 + 0];
            _interleaved[i * 8 + 4] = normals[i * 3 + 1];
            _interleaved[i * 8 + 5] = normals[i * 3 + 2];
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = _interleaved)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(_interleaved.Length * sizeof(float)), p);
    }

    public void SetBoneSegments(float[]? lineVerts)
    {
        if (!_ready) return;
        if (lineVerts is null || lineVerts.Length == 0) { _boneVerts = 0; return; }
        UploadLines(_boneVao, _boneVbo, lineVerts, out _boneVerts);
    }

    /// <summary>Draw a highlight wireframe box (always on top) around the given world-space bounds,
    /// or clear it when either bound is null (e.g. no map mesh selected).</summary>
    public void SetHighlightBounds(Vector3? min, Vector3? max)
    {
        if (min is { } a && max is { } b) SetHighlightBoxes(new[] { (a, b) });
        else SetHighlightBoxes(Array.Empty<(Vector3, Vector3)>());
    }

    /// <summary>Draw an amber highlight box (always on top) around EACH selected mesh's world bounds.</summary>
    public void SetHighlightBoxes(IReadOnlyList<(Vector3 min, Vector3 max)> boxes)
    {
        if (!_ready) return;
        if (boxes.Count == 0) { _highlightVerts = 0; return; }
        var verts = new float[boxes.Count * 12 * 2 * 3];
        int o = 0;
        foreach (var (min, max) in boxes)
        {
            var box = BuildBoxLines(min, max);
            Array.Copy(box, 0, verts, o, box.Length);
            o += box.Length;
        }
        UploadLines(_highlightVao, _highlightVbo, verts, out _highlightVerts);
    }

    /// <summary>Draw a dimmer box around the whole selection (the group bounds), or clear it.</summary>
    public void SetGroupBounds(Vector3? min, Vector3? max)
    {
        if (!_ready) return;
        if (min is not { } a || max is not { } b) { _groupBoundsVerts = 0; return; }
        UploadLines(_groupBoundsVao, _groupBoundsVbo, BuildBoxLines(a, b), out _groupBoundsVerts);
    }

    /// <summary>Set (or clear, with pivot=null) the translate gizmo: 3 axis lines from the pivot,
    /// each <paramref name="armLength"/> world units long.</summary>
    public void SetGizmo(Vector3? pivot, float armLength)
    {
        _hasGizmo = pivot.HasValue && armLength > 0f;
        if (pivot.HasValue) { _gizmoPivot = pivot.Value; _gizmoArmLength = armLength; }
    }

    public void ClearMesh()
    {
        DeleteMeshBuffers();
        _boneVerts = 0;
        _boundsVerts = 0;
        _highlightVerts = 0;
        _groupBoundsVerts = 0;
        _hasGizmo = false;
    }

    public unsafe void Render(Matrix4x4 viewProjection, bool wireframe, bool showBounds, bool showBones)
        => Render(viewProjection, Matrix4x4.Identity, Vector3.Zero, 0, wireframe, showBounds, showBones);

    public unsafe void Render(Matrix4x4 viewProjection, Vector3 camPos, int previewMode, bool wireframe, bool showBounds, bool showBones)
        => Render(viewProjection, Matrix4x4.Identity, camPos, previewMode, wireframe, showBounds, showBones);

    public unsafe void Render(Matrix4x4 viewProjection, Matrix4x4 view, Vector3 camPos, int previewMode, bool wireframe, bool showBounds, bool showBones)
    {
        if (!_ready) return;
        var m = viewProjection;

        if (_hasMesh)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.Disable(EnableCap.CullFace);
            _gl.BindVertexArray(_vao);

            if (wireframe)
            {
                _gl.UseProgram(_lineProgram);
                _gl.UniformMatrix4(_lMvp, 1, false, in m.M11);
                _gl.Uniform4(_lColor, 0.55f, 0.62f, 0.72f, 1f);
                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _wireEbo);
                _gl.DrawElements(PrimitiveType.Lines, (uint)_wireIndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }
            else
            {
                _gl.UseProgram(_meshProgram);
                _gl.UniformMatrix4(_mMvp, 1, false, in m.M11);
                var model = Matrix4x4.Identity;
                _gl.UniformMatrix4(_mModel, 1, false, in model.M11);
                _gl.Uniform3(_mLight, -0.4f, -0.85f, -0.45f);
                _gl.Uniform3(_mBaseColor, 0.62f, 0.66f, 0.74f);
                _gl.Uniform1(_mMode, previewMode);
                _gl.Uniform3(_mCamPos, camPos.X, camPos.Y, camPos.Z);
                _gl.UniformMatrix4(_mView, 1, false, in view.M11);
                _gl.Uniform1(_mTex, 0);
                _gl.Uniform1(_mMask, 1);
                _gl.Uniform1(_mGradient, 2);
                _gl.Uniform1(_mEmissive, 3);
                _gl.Uniform1(_mMatCap, 4);
                _gl.Uniform1(_mMatCapMask, 5);
                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);

                foreach (var s in _submeshes)
                {
                    if (!s.Visible) continue;
                    // M32 per-material: UV transform + rim/specular gates (identity/off by default).
                    _gl.Uniform4(_mUvScaleOffset, s.UvScaleOffset.X, s.UvScaleOffset.Y, s.UvScaleOffset.Z, s.UvScaleOffset.W);
                    _gl.Uniform1(_mUvRot, s.UvRotationRadians);
                    _gl.Uniform1(_mUsesRim, s.UsesRim ? 1 : 0);
                    _gl.Uniform1(_mUsesSpec, s.UsesSpecular ? 1 : 0);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                    _gl.BindTexture(TextureTarget.Texture2D, s.Texture != 0 ? s.Texture : _whiteTex);
                    _gl.Uniform1(_mHasTex, s.Texture != 0 ? 1 : 0);
                    _gl.ActiveTexture(TextureUnit.Texture1);
                    _gl.BindTexture(TextureTarget.Texture2D, s.Mask != 0 ? s.Mask : _whiteTex);
                    _gl.Uniform1(_mHasMask, s.Mask != 0 ? 1 : 0);
                    _gl.ActiveTexture(TextureUnit.Texture2);
                    _gl.BindTexture(TextureTarget.Texture2D, s.Gradient != 0 ? s.Gradient : _whiteTex);
                    _gl.Uniform1(_mHasGradient, s.Gradient != 0 ? 1 : 0);
                    _gl.ActiveTexture(TextureUnit.Texture3);
                    _gl.BindTexture(TextureTarget.Texture2D, s.Emissive != 0 ? s.Emissive : _whiteTex);
                    _gl.Uniform1(_mHasEmissive, s.Emissive != 0 ? 1 : 0);
                    _gl.ActiveTexture(TextureUnit.Texture4);
                    _gl.BindTexture(TextureTarget.Texture2D, s.MatCap != 0 ? s.MatCap : _whiteTex);
                    _gl.Uniform1(_mHasMatCap, s.MatCap != 0 ? 1 : 0);
                    _gl.ActiveTexture(TextureUnit.Texture5);
                    _gl.BindTexture(TextureTarget.Texture2D, s.MatCapMask != 0 ? s.MatCapMask : _whiteTex);
                    _gl.Uniform1(_mHasMatCapMask, s.MatCapMask != 0 ? 1 : 0);
                    _gl.DrawElements(PrimitiveType.Triangles, (uint)s.Count, DrawElementsType.UnsignedInt, (void*)(s.Start * sizeof(uint)));
                }
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
                _gl.Disable(EnableCap.DepthTest);
                _gl.Uniform4(_lColor, 1.0f, 0.65f, 0.2f, 1f);
                _gl.BindVertexArray(_boneVao);
                _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_boneVerts);
            }
            _gl.BindVertexArray(0);
        }

        // Selection highlight: a bright box around each selected mesh + a dimmer box around the whole
        // group, always on top so they read clearly even inside dense geometry.
        if (_highlightVerts > 0 || _groupBoundsVerts > 0)
        {
            _gl.UseProgram(_lineProgram);
            _gl.UniformMatrix4(_lMvp, 1, false, in m.M11);
            _gl.Disable(EnableCap.DepthTest);
            if (_groupBoundsVerts > 0)
            {
                _gl.Uniform4(_lColor, 0.5f, 0.65f, 0.95f, 1f); // group bounds — cool blue
                _gl.BindVertexArray(_groupBoundsVao);
                _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_groupBoundsVerts);
            }
            if (_highlightVerts > 0)
            {
                _gl.Uniform4(_lColor, 1.0f, 0.78f, 0.2f, 1f); // selection amber
                _gl.BindVertexArray(_highlightVao);
                _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_highlightVerts);
            }
            _gl.BindVertexArray(0);
        }

        // Translate gizmo: 3 axis lines from the selected mesh's pivot (X=red, Y=green, Z=blue), always
        // on top so it stays clickable regardless of what's behind it.
        if (_hasGizmo)
        {
            Span<float> verts = stackalloc float[]
            {
                _gizmoPivot.X, _gizmoPivot.Y, _gizmoPivot.Z, _gizmoPivot.X + _gizmoArmLength, _gizmoPivot.Y, _gizmoPivot.Z,
                _gizmoPivot.X, _gizmoPivot.Y, _gizmoPivot.Z, _gizmoPivot.X, _gizmoPivot.Y + _gizmoArmLength, _gizmoPivot.Z,
                _gizmoPivot.X, _gizmoPivot.Y, _gizmoPivot.Z, _gizmoPivot.X, _gizmoPivot.Y, _gizmoPivot.Z + _gizmoArmLength,
            };
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _gizmoVbo);
            unsafe
            {
                fixed (float* p = verts)
                    _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
            }
            _gl.BindVertexArray(_gizmoVao);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

            _gl.UseProgram(_lineProgram);
            _gl.UniformMatrix4(_lMvp, 1, false, in m.M11);
            _gl.Disable(EnableCap.DepthTest);
            _gl.LineWidth(2.5f);
            _gl.Uniform4(_lColor, 0.95f, 0.25f, 0.25f, 1f); // X red
            _gl.DrawArrays(PrimitiveType.Lines, 0, 2);
            _gl.Uniform4(_lColor, 0.3f, 0.9f, 0.35f, 1f);   // Y green
            _gl.DrawArrays(PrimitiveType.Lines, 2, 2);
            _gl.Uniform4(_lColor, 0.3f, 0.55f, 0.98f, 1f);  // Z blue
            _gl.DrawArrays(PrimitiveType.Lines, 4, 2);
            _gl.LineWidth(1f);
            _gl.BindVertexArray(0);
        }
    }

    private unsafe uint MakeSolidTexture(byte r, byte g, byte b)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        byte[] px = { r, g, b, 255 };
        fixed (byte* p = px)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
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
            foreach (int idx in new[] { edges[e, 0], edges[e, 1] })
            {
                list[k++] = c[idx].X; list[k++] = c[idx].Y; list[k++] = c[idx].Z;
            }
        return list;
    }

    private void DeleteMeshBuffers()
    {
        if (!_hasMesh) return;
        foreach (var t in _ownedTextures) _gl.DeleteTexture(t);
        _ownedTextures.Clear();
        _submeshes = Array.Empty<SubmeshDraw>();
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
        _gl.DeleteTexture(_whiteTex);
        _gl.DeleteBuffer(_boundsVbo);
        _gl.DeleteBuffer(_boneVbo);
        _gl.DeleteVertexArray(_boundsVao);
        _gl.DeleteVertexArray(_boneVao);
        _gl.DeleteProgram(_meshProgram);
        _gl.DeleteProgram(_lineProgram);
        _ready = false;
    }
}
